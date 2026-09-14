# CultCache Store Composition

How one cache composes its backing stores: where a record lives, when a store
is read or written, what a global is, how a multi-record commit lands, and who
owns serialization options. `GameCult.Caching` in C# is the reference; the
runtime table at the end says which runtime implements what.

None of this changes bytes on disk. Every store file is a complete
`cultcache.store.v1` snapshot (single file) or a v4 directory manifest, exactly
as `cultcache-persistence-format.md` describes. No file records which cache or
which route wrote it.

## One home store per document type

- A document type has exactly one home store. A cache attaches stores with an
  optional list of home types: `AddBackingStore(store, params Type[] homes)`.
- The home of a document is chosen from its **runtime type**
  (`document.GetType()`), never from a generic type parameter at the call site.
  Among routed stores whose home types are assignable from the runtime type, the
  most derived home type wins; otherwise the untyped store; otherwise none.
- C# routes by assignable CLR type. TypeScript, Rust and Python route by the
  exact type string they already use.
- A second untyped store is a registration error. A home type already claimed by
  another store, by exact type equality, is a registration error. Routes to a
  base type and a derived type may coexist; the most derived wins.
- A write whose document has no home, once any store is attached, is an error.
  A cache with zero stores is an in-memory cache and needs no home.
- Nothing is mirrored or replicated. A write touches exactly one store.
- This is the contract in every runtime. A runtime that still mirrors is
  non-conforming until it deletes its mirrors.

## Attachment is hydration

- Attaching a store reads it. There is no interval in which a store is attached
  but unread, and no push of existing records into a newly attached store.
- A store must be clean (`IsDirty` false) when attached.
- A record's home cannot change after it is admitted. Attaching a store that
  would move the home of a record already in the cache is an error. The
  consequence is the attach order: routed stores first, the untyped store last.
- A record loaded from a store that is not its home is refused at load, naming
  the key and both stores.
- Pulling again after attach is a re-pull. It does not erase mutations staged in
  a dirty store. A dirty single-file store skips the re-pull entirely; a missing
  single file keeps staged writes dirty and does not drop loaded records.
- A load publishes changes to `Watch` subscribers and fires `OnUpdate`. A local
  write or commit publishes to `Watch` only; `OnUpdate` fires for loads alone.
- Changes published through `Watch`/`WatchRecord` carry a `Sequence`: a
  per-cache, in-memory number assigned when the cache admits the change, under
  its gate. It increases in admission order and is not persisted.
  `OnUpdate(previous, current)` carries none. Streams CultNet derives from
  `OnUpdate` (`CultNetDatabase.WatchAllChanges`, the subscription server,
  database-backed Mesh handles) have no ordering guarantee and no stale
  protection.
- `GetWithSequence(key)` returns the document and the cache's current
  `Sequence`, read together under the gate: every change with a `Sequence` at
  or below it is reflected in the document.
- Adding the optional `sequence` constructor argument to
  `CultCacheDocumentChange<T>` changed its binary signature; no external
  constructor is known.
- Observers and `OnUpdate` run after the cache releases its gate, never under
  it: an observer may read or write the cache from any thread. Each call
  publishes exactly its own changes, on its own thread, after releasing the gate
  and before it returns; no call publishes another call's changes or waits on
  another call's delivery. A write made by an observer is its own call and
  publishes its own changes before that write returns. A direct `PullAll` on an
  attached store is such a call.
- Cross-thread delivery order is not guaranteed. A consumer that keeps a latest
  value per record ignores a change whose `Sequence` is not above the one it
  already applied for that record. It subscribes first, then takes
  `GetWithSequence` and adopts that `Sequence` as applied; a change delivered
  after subscribing with a `Sequence` at or below the read is already in the
  document and is dropped. CultMesh cache-record mirrors (`ObserveAsync`,
  `ReactiveAsync`, and `RefreshAsync` on both) do this.
- Not stale-protected: schema-alias handles (`AsSchemaAlias`) get no
  `Sequence`; removals do not advance a mirror's applied `Sequence`, and the
  mirrors do not surface removals.
- An `OnUpdate` handler exception is rethrown to that caller after all of that
  call's changes are delivered (an `AggregateException` if several threw). If
  the call itself failed, its own exception is rethrown and handler exceptions
  are dropped. A `Watch` subscriber exception is not rethrown; it follows R3's
  unhandled-exception handling. A throwing handler cannot undo the store's
  adoption of a load; the store and cache already agree when publication starts.
- Pulling all stores pulls every attached store even if a handler throws during
  one store's load, then rethrows.
- Hydration failure on open is loud: a corrupt store file, or a record whose
  schema the registry cannot resolve, makes the open throw and leaves the file
  byte-identical. Consumers never delete and rewrite a store they failed to
  open.

## Locking

- One lock order: the cache's gate, then the store's lock. A store attached to a
  cache takes that cache's gate as its own lock, so a store's load callback into
  the cache cannot take the two out of order. A store must not call `Loaded`
  while holding its own lock outside the cache's hold.
- A thread holds one cache's gate at a time: entering a cache's hold inside
  another cache's hold throws.
- A store doing I/O on behalf of its cache (pull, flush, commit) holds the gate,
  so that cache's readers wait for the I/O. The file lock (`<path>.lock` or the
  directory commit lease) is always taken inside the gate and released before a
  load is handed to the cache.
- Direct calls on an attached store (`Push`, `Delete`, `PushAll`, `CommitBatch`,
  `PullAll`) take the same gate, so they wait for that cache's pull, flush or
  commit, and the cache's flush on dispose runs under it.
- In every runtime, a store call must not wait for a write to its own cache to
  finish: that write queues behind the operation that is waiting for it, a
  cycle. A handler or subscriber that writes back without the store waiting for
  it is fine; its write queues and lands after the operation. Python refuses a
  mutation started on a thread already inside one of that cache's mutations
  with an error; no runtime detects a wait that escapes to another thread or
  async context.

## Dirtiness: loading never writes

- A store's `IsDirty` derives only from its own writes and deletes. Pulling sets
  it false.
- A cache's `IsDirty` is whether any attached store is dirty, or, with no stores,
  whether it has been mutated in memory.
- Opening a store and flushing without a mutation leaves every file
  byte-identical.
- A mutation reaches the home store before the cache's in-memory view changes.
  If the store refuses the write, the cache holds nothing new and publishes no
  change.

## Read-only stores

- A store can be opened read-only. Writes, deletes and full pushes on it throw
  and name the store.
- A write whose home is a read-only store throws before anything changes.
- Flushing skips read-only stores.

## Lookups and watches honor inheritance

`Get<T>`, `GetAll<T>`, `GetByName<T>`, `GetByIndex<T>`, `GetGlobal<T>` and
`Watch<T>` match every document whose runtime type is assignable to `T`. A
lookup that returns one document and finds several candidates throws, naming
them. Indexes stay keyed by concrete type; lookups enumerate assignable keys.

## Globals

- `[CultGlobal]` means: at most one record of the type per cache, `GetGlobal<T>()`
  returns it or `null`, and a write without a handle keys it
  `global:{schemaId}`.
- The cache never creates a global. Constructing a cache, attaching a store and
  pulling produce no record the store did not contain. The code path that begins
  a global's lifecycle creates it with an ordinary upsert.
- A second record of a global type, through a write or a load, is refused
  before any store is touched.
- TypeScript and Python key the single record `__global__`; C# keys it
  `global:{schemaId}`. The divergence is recorded, not changed, because changing
  it changes bytes. Rust has no global concept.

## Atomic commit: an explicit batch

- A multi-record commit is an explicit batch value staged by the caller and
  committed with `Commit`. Every record in the batch must resolve to one home
  store; a batch spanning two homes throws before any store is touched.
- The store commits the batch as one durable step: one atomic replace for a
  single file, pages then manifest for a directory store. A failed commit
  restores the store's staging and changes nothing in memory.
- Nothing in a batch is visible, to the staging code or to observers, until the
  store has accepted it. Reads during staging see committed state. There is no
  ambient transaction and no in-flight overlay.
- After the store accepts, each record is admitted through the same path as a
  single write (home, read-only and global checks included) and one change is
  published per record.
- A cache with no stores admits a batch in memory.

## Conditional commit (compare-exchange)

A batch may carry conditions, evaluated under the home store's exclusive lock
against what is durably on disk at that moment:

- `Expect(key, current)`: the record at `key` must still be the one this cache
  observed, or, with `current` null, must be absent. Only named keys are
  constrained; unrelated concurrent writes do not fail the commit. Passing any
  instance other than the one the cache holds throws.
- `ExpectUnchanged()`: the store's persisted record set must equal what this
  cache last loaded from it, compared as the ordered list of
  `(key, schemaId, storedAt)`. Any insert, delete or replace fails it.

Record identity for conditions is `(schemaId, storedAt)`. It is sound because
every write to a key mints a `storedAt` strictly later than the record it
replaces; writers bump a minted timestamp by one tick when it is not later.

- A failed condition is a lost race, not an error: `Commit` returns false and
  nothing is written, changed in memory, or published. `TryCommit` makes one
  non-blocking lock attempt and reports `Contended` instead of waiting. On a
  directory store the lock is the commit lease that pulls also take, so
  `Contended` can mean another cache or process is reading or committing. The
  attempt itself runs inside the cache gate, so `TryCommit` may first block
  behind a pull or flush of the same cache, for up to that operation's lease
  wait bound (30 seconds).
- A conditional commit on a store with staged single-record writes throws:
  flush first.
- Single-file stores lock a sidecar `<path>.lock` opened exclusively. Directory
  stores use their existing commit lease.

**A plain flush and an unconditional commit are last-writer-wins: per file for
single-file stores, per key for directory stores.** Both run
under the same lock, so two writers never interleave bytes, but they compare
nothing. Only a conditional commit (`Expect` or `ExpectUnchanged`) protects
against another writer; processes sharing a store must all use it.

- A single-file store writes this cache's whole view of the file: a record
  another writer added since this cache last pulled is gone.
- A directory store writes this cache's staged keys onto the current manifest:
  another writer's unrelated keys survive, a key both wrote holds the last
  write.
- An unconditional commit writes exactly what a flush of the same staged state
  plus the batch would write. It also persists any single writes staged earlier
  in that store, and leaves the store clean.
- A conditional commit lands the batch onto the file as it is under the lock.

A store is written by one runtime at a time. Whether a Rust `fs2` lock and a C#
exclusive open of the same lock file exclude each other is not established, and
another runtime need not obey the `storedAt` rule.

## Serialization options

- Options belong to the assembly that declares a document. That assembly names
  its formatter resolvers with `[assembly: CultCacheFormatterResolver(typeof(R))]`;
  the payload serializer resolves options from the document's assembly. There is no mutable static, no registration call, and no load-order
  rule.
- A generic document's options come from the generic definition's assembly:
  `Doc<X>` uses the resolvers `Doc`'s assembly declares, never `X`'s. A formatter
  for `X` that `Doc<X>` needs is declared in `Doc`'s assembly.
- An assembly that declares nothing, and the store envelope, use the base
  options.
- Deserialization runs under untrusted-data security. `CultRecordRef<T>` is a
  permitted dictionary key, compared by its key string.

## Value-type encoding

Vector-like value types (Unity.Mathematics, consumer structs) encode as
**fixed-length positional arrays of primitive components**: a two-component
float vector is `[f32, f32]`, a three-component one `[f32, f32, f32]`, an
integer pair `[int, int]`. No ext types, no maps, no names. The persisted member
type name is the CLR full name, the same rule as `System.Int32`. A reader in
another runtime needs only the component count and primitive type. CultMath has
no serialization; a CultMath encoding, if added, uses this shape.

## Runtime status

| Rule | C# | Rust | TypeScript | Python |
|---|---|---|---|---|
| One home store, no mirrors | implemented | implemented (exact type ids; `add_backing_store` returns `Err` on a second generic store or a claimed type; an empty type list is the generic store) | implemented (exact type ids; `addBackingStore` throws on a second generic store or a claimed type; no types is the generic store) | implemented (exact type ids; `add_backing_store`/`add_generic_store` raise on a claimed type or a second generic store; an empty type list is the generic store) |
| Zero stores is in memory | implemented | implemented | implemented | implemented |
| Home cannot move after admission | implemented | implemented (`add_backing_store` returns `Err` and attaches nothing) | implemented (`addBackingStore` rejects and attaches nothing; it runs in the cache's serial queue, so it cannot move a write that is in flight) | implemented (`add_backing_store`/`add_generic_store` raise and attach nothing; they hold the cache's lock, so they cannot move a write that is in flight) |
| Refused at load when not home | implemented | implemented (`pull_all_backing_stores` returns `Err` naming key and both stores; `load_soa` returns `Err` for a type with no home once a store is attached; both admit nothing and leave the prior view intact) | implemented (`pullAllBackingStores` throws naming key and both stores; admits nothing) | implemented (`pull_all_backing_stores` raises naming key and both stores; admits nothing) |
| Refused write changes neither store nor cache | implemented | implemented (`put`, `put_envelope`, `put_raw_envelope`, `put_prepared_batch` and `delete` validate before the store call and apply infallibly after it; there are no user accessors) | implemented (home, `__global__` key and name and index accessors run before the store call; overwrite and delete use lookup values stored at admission; attach, pull, registration, writes and deletes run through one per-cache promise queue; one of those started from inside an operation queues behind it, so a store that waits for it never finishes (see Locking)) | implemented (same validation order and stored lookup keys as TypeScript; attach, pull, registration, writes, deletes, name and index resolution and snapshots hold one lock per cache; a mutation started on a thread already inside one of that cache's mutations raises, reads stay reentrant; a wait on another thread is not detected (see Locking)) |
| Attach hydrates; loading never writes | implemented | attach does not read (call `pull_all_backing_stores`); pull loads without writing | attach does not read (call `pullAllBackingStores`); pull loads without writing | attach does not read (call `pull_all_backing_stores`); pull loads without writing |
| Runtime type decides schema | implemented | n/a (explicit type ids) | n/a | n/a |
| Assignable lookups and watches | implemented | exact type ids | exact type ids | exact type ids |
| Globals never invented, singleton | implemented | no globals | single `__global__`; a write under any other key is refused before any store is touched; replacing writes `__global__` in place; a second persisted global (any keys) is refused on pull; invents nothing; load compatibility: a pull adopts a global stored under another key as `__global__` without writing, and the first write of it pushes `__global__`, then deletes the legacy record, deleting `__global__` again if that delete fails (a delete of it removes only the legacy record). A crash between the two steps, or a failed compensation, leaves both records on disk, and the next pull refuses them loudly as a second global. Removed once no store holds a non-`__global__` global | single `__global__`; `put`, `put_envelope` and `put_envelopes` refuse any other key before any store is touched; a second persisted global (any keys) is refused on pull; invents nothing; load compatibility: a pull adopts a global stored under another key as `__global__` without writing, and the first write of it pushes `__global__`, then deletes the legacy record, deleting `__global__` again if that delete fails (a delete of it removes only the legacy record). A crash between the two steps, or a failed compensation, leaves both records on disk, and the next pull refuses them loudly as a second global. Removed once no store holds a non-`__global__` global |
| Explicit batch commit | implemented | `put_prepared_batch`: staged records must share one home; that store receives one `push_all` of its own held records plus the batch; empty key or `stored_at` refused first; all-or-nothing on stores whose `push_all` is atomic, which the trait requires of every store: `push_all` has no default, and the bundled single-file and redb stores replace in one step | not implemented | `put_envelopes`, one type per call; every record validated before one `push_all`, applied only after the store accepts; atomicity on disk is the store's `push_all` |
| Conditional commit | implemented | store-level `compare_exchange` family | not implemented | not implemented |
| Per-assembly options, value-type arrays | contract | n/a | n/a | n/a |

A "contract" cell means this document is ahead of the C# code.

Opening a store always hydrates it. A consumer that wants a store to hold only
what it writes removes the records it does not keep, upserts, and flushes. On a
single-file store the flush replaces the file in one atomic step. On a directory
store the flush writes pages, then the manifest, under the commit lease, merging
its staged keys and removals onto the current manifest; a key another writer
added since the pull survives. There is no open-without-reading option.
