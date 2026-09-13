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
  a dirty store.

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
  non-blocking lock attempt and reports `Contended` instead of waiting.
- A conditional commit on a store with staged single-record writes throws:
  flush first.
- Single-file stores lock a sidecar `<path>.lock` opened exclusively. Directory
  stores use their existing commit lease.

**A plain flush is last-writer-wins.** It writes the whole snapshot under the
same lock, so two writers never interleave bytes, but it compares nothing and
overwrites whatever another process committed. Conditional commit is the only
safe multi-process write: processes sharing a store must all use it.

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
| One home store, no mirrors | contract; implementation replaces replication | routes by type, still pushes to later matching stores | routes by type, still mirrors | routes by type, still writes every matching store |
| Attach hydrates; loading never writes | contract; attach still pushes existing records | attach does not read; pull loads without writing | attach does not read; pull loads without writing | attach does not read; pull loads without writing |
| Runtime type decides schema | contract; generic parameter still decides | n/a (explicit type ids) | n/a | n/a |
| Assignable lookups and watches | `Get<T>`/`GetAll<T>` only | exact type ids | exact type ids | exact type ids |
| Globals never invented, singleton | contract; constructor still invents defaults | no globals | single `__global__`; a write with a new key replaces it; refused only on pull; invents nothing | single `__global__`, invents nothing |
| Explicit batch commit | contract; ambient transaction still present | `put_prepared_batch`, one store, all-or-nothing | not implemented | `put_envelopes`, one type per call, not all-or-nothing |
| Conditional commit | contract | store-level `compare_exchange` family | not implemented | not implemented |
| Per-assembly options, value-type arrays | contract | n/a | n/a | n/a |

A "contract" cell means this document is ahead of the C# code; the C# change
lands with `StoreRoutingTests` turning green.
