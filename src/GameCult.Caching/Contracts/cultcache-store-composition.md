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
- Observers and `OnUpdate` run after the cache releases its gate, never under
  it: an observer may read or write the cache from any thread. A change is
  published after the cache releases its gate and before the call that made it
  returns, on that caller's thread; no call publishes another call's changes. A
  direct `PullAll` on an attached store is such a call.
- An observer exception is rethrown to that caller after all of that call's
  changes are delivered (an `AggregateException` if several threw). If the call
  itself failed, its own exception is rethrown and observer exceptions are
  dropped. A throwing observer cannot undo the store's adoption of a load; the
  store and cache already agree when publication starts.
- Hydration failure on open is loud: a corrupt store file, or a record whose
  schema the registry cannot resolve, makes the open throw and leaves the file
  byte-identical. Consumers never delete and rewrite a store they failed to
  open.

## Locking

- One lock order: the cache's gate, then the store's lock. A store attached to a
  cache takes that cache's gate as its own lock, so a store's load callback into
  the cache cannot take the two out of order.
- A store doing I/O on behalf of its cache (pull, flush, commit) holds the gate,
  so that cache's readers wait for the I/O. The file lock (`<path>.lock` or the
  directory commit lease) is always taken inside the gate and released before a
  load is handed to the cache.
- Direct calls on an attached store (`Push`, `Delete`, `PushAll`, `CommitBatch`,
  `PullAll`) take the same gate, so they wait for that cache's pull, flush or
  commit, and the cache's flush on dispose runs under it.

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
  generated and reflective serializers resolve options from the document's
  assembly. There is no mutable static, no registration call, and no load-order
  rule.
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
| One home store, no mirrors | implemented | routes by type, still pushes to later matching stores | routes by type, still mirrors | routes by type, still writes every matching store |
| Attach hydrates; loading never writes | implemented | attach does not read; pull loads without writing | attach does not read; pull loads without writing | attach does not read; pull loads without writing |
| Runtime type decides schema | implemented | n/a (explicit type ids) | n/a | n/a |
| Assignable lookups and watches | implemented | exact type ids | exact type ids | exact type ids |
| Globals never invented, singleton | implemented | no globals | single `__global__`; a write with a new key replaces it; refused only on pull; invents nothing | single `__global__`, invents nothing |
| Explicit batch commit | implemented | `put_prepared_batch`, one store, all-or-nothing | not implemented | `put_envelopes`, one type per call, not all-or-nothing |
| Conditional commit | implemented | store-level `compare_exchange` family | not implemented | not implemented |
| Per-assembly options, value-type arrays | contract | n/a | n/a | n/a |

A "contract" cell means this document is ahead of the C# code.

Opening a store always hydrates it. A consumer that wants a store to hold only
what it writes removes the records it does not keep, upserts, and flushes. On a
single-file store the flush replaces the file in one atomic step. On a directory
store the flush writes pages, then the manifest, under the commit lease, merging
its staged keys and removals onto the current manifest; a key another writer
added since the pull survives. There is no open-without-reading option.
