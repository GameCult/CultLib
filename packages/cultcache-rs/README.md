# CultCache Rust

`cultcache-rs` is the Rust port of the useful part of GameCult's CultCache:
a polymorphism-aware in-memory cache that remembers through pluggable backing
stores.

Consumer code should think in domain types:

```rust
let player = cache.get_required::<PlayerData>("player:meta")?;
cache.put("player:meta", &player)?;
```

The persistence layer should deal with envelopes, routing, MessagePack payload
bytes, and schema identity. Application code should not paw at loose JSON files
like a sad little bureaucrat with a clipboard.

## What It Is

CultCache is a domain cache with persistence adapters.

- `CultCache` is the query and mutation surface.
- Domain structs implement `DatabaseEntry`.
- A cache holds one record per key whatever its type. Records are held per
  `(type, key)`, but a write of a second type under a key the cache holds, or a
  key a store carries foreign, is refused with `SchemaConflictError` naming the
  key (no store needed); retyping a key is a delete, then an add. A store
  record no registered type claims loads: the cache does not serve it, lists it
  in `foreign_records()`, lays it back byte for byte through every write
  including `put_prepared_batch`, and refuses a write or delete at its key.
  Registering a type that claims its schema id picks it up at the next pull.
- Each payload is encoded directly from the known `DatabaseEntry` type into
  MessagePack bytes.
- Backing stores are adapters, not the public data model.
- Writes persist to the resolved backing store before the in-memory cache is
  updated.
- Type-specific backing stores beat generic backing stores.
- `SingleFileMessagePackBackingStore` is the first concrete store and guards
  file access with a sidecar lock file.

This is not an ORM, not a database, and not distributed consensus in a novelty
hat. Several processes may write the same single-file store, one at a time under
its sidecar lock; write ordering across keys or stores needs a coordinator.

## Current API

```rust
use cultcache_rs::{CultCache, DatabaseEntry, SingleFileMessagePackBackingStore};

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "settings")]
struct Settings {
    #[cultcache(key = 0)]
    theme: String,

    #[cultcache(key = 1, default)]
    retries: u32,
}

let mut cache = CultCache::new();
cache.register_entry_type::<Settings>()?;
cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new("cache.cc"));
cache.pull_all_backing_stores()?;

cache.put("app", &Settings {
    theme: "ash".to_string(),
    retries: 3,
})?;

let settings = cache.get_required::<Settings>("app")?;
# Ok::<(), anyhow::Error>(())
```

For a closed set of entries, generate a registry instead of repeating yourself:

```rust
use cultcache_rs::cultcache_registry;

cultcache_registry!(GameCultEntries {
    Settings,
    PlayerData,
});

let mut cache = CultCache::new();
cache.register_registry(GameCultEntries)?;
```

The important part is that callers retrieve domain values directly:

```rust
let settings: Settings = cache.get_required("app")?;
let all_settings: Vec<Settings> = cache.get_all()?;
```

The cache is the ergonomic surface. The backing store is the memory prosthetic.

## How This Maps From Original CultCache

The original C# CultCache relies on runtime reflection:

- cacheable models inherit from `DatabaseEntry`
- the cache scans child classes at startup
- `Get<T>(id)` returns a `T`
- optional `INamedEntry` enables name lookup
- optional field/property indexes are registered by member name
- backing stores pull/push/delete `DatabaseEntry` values

Rust does not have C#-style assembly scanning or runtime subclass discovery.
The closest honest Rust translation is:

- domain structs implement a marker trait, `DatabaseEntry`
- the derive macro generates a positional array formatter from explicit integer
  field slots
- the envelope carries the polymorphic type discriminator
- unknown persisted type ids fail closed instead of constructing arbitrary
  runtime types
- a generated registry should eventually replace repetitive manual
  registration

Manual `register_entry_type::<T>()` is still accepted, but entry identity no
longer needs a hand-written impl: `#[derive(DatabaseEntry)]` declares it beside
the domain type. `cultcache_registry!` provides the current generated resolver
surface for a closed entry set.

## Intended Rust Ergonomics

The target user-facing shape should be:

```rust
#[derive(Clone, DatabaseEntry)]
#[cultcache(type = "player")]
pub struct PlayerData {
    #[cultcache(key = 0)]
    pub id: String,

    #[cultcache(key = 1)]
    pub name: String,

    #[cultcache(key = 2)]
    pub faction: String,
}

let mut cache = CultCache::builder()
    .with_generated_entries(game_entries())
    .with_generic_store(SingleFileMessagePackBackingStore::new("cache.cc"))
    .build()?;

cache.pull_all_backing_stores()?;
let player = cache.get_required::<PlayerData>("player:ari")?;
```

The current `cultcache_registry!` macro is deliberately simple. The remaining
ergonomic endpoint is automatic inventory or build-script generation so users
do not have to maintain the registry list by hand.

The registry is the Rust equivalent of C# reflection and MessagePack resolver
setup. It should know:

- entry type id
- generated MessagePack/serde formatter path
- Rust type name / schema name
- optional store domain route
- optional name key extractor
- optional secondary indexes
- optional global singleton marker

In other words: code generation should restore the original CultCache feeling
without pretending Rust has runtime reflection hiding under the floorboards.

## Current Surface

- `CultCache::new`
- `register_entry_type::<T>`
- `add_generic_backing_store`
- `add_backing_store`
- `pull_all_backing_stores`
- `get::<T>`
- `get_required::<T>`
- `get_envelope::<T>`
- `get_required_envelope::<T>`
- `get_all::<T>`
- `get_all_with_keys::<T>`
- `get_by_name::<T>`
- `get_by_index::<T>`
- `get_global::<T>`
- `soa::<T>`
- `put::<T>`
- `put_envelope::<T>`
- `update::<T>`
- `delete::<T>`
- `snapshot`
- `snapshot_soa`
- `load_soa`
- `SingleFileMessagePackBackingStore`

## Unions

A union that crosses runtimes is written `[armKey, [armSlots...]]`, the shape the C# reference writes for
`[Union(key, type)]`. Derive `CultUnion` on an enum: every variant carries `#[cultcache(key = N)]` and is a unit
(`[N, []]`) or a newtype over a `#[derive(CultSlots)]` struct, which is a `DatabaseEntry`'s slot array without the
entry consts (a `String` or an `Option` is not an arm, and is a compile error). An arm key is the reference's Int32, negatives included, and is given once. A key the enum does not declare is a decode error naming the enum and the declared keys, and never
quoting the payload. A null union is `Option<Enum>`. Plain serde enums are untouched and keep their bytes.

```rust
use cultcache_rs::{CultSlots, CultUnion};

#[derive(Clone, Debug, PartialEq, CultUnion)]
enum Node {
    #[cultcache(key = 0)]
    Box(BoxArm),
    #[cultcache(key = 1)]
    Text(TextArm),
    #[cultcache(key = 5)]
    Spacer, // a unit arm: [5, []]
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct BoxArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    children: Vec<Node>, // recursive
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct TextArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    content: String,
}

let tree = Node::Box(BoxArm { id: "root".into(), children: vec![Node::Spacer] });
let bytes = rmp_serde::to_vec(&tree)?;
assert_eq!(rmp_serde::from_slice::<Node>(&bytes)?, tree);
# Ok::<(), Box<dyn std::error::Error>>(())
```

## Records written by a newer build

A payload that carries a slot past the slots its type declares, in a record, a nested record, a list element or a union
arm (a unit arm declares none), still decodes: the extra slots are skipped. The cache then holds that record read-only,
because writing it would drop what the newer writer stored. `put`, `put_envelope`, `put_raw_envelope`,
`put_prepared_batch` and `delete` of its key are refused with `SchemaConflictError`, whose `unread_slots` names the type
and the slot counts and never a value; `CultCache::is_writable(type, key)` answers the same check. An untouched
read-only record keeps its bytes through every other write. A short payload (missing trailing slots) is not marked, and
a payload nested past `MAX_PAYLOAD_DEPTH` is refused before anything is read. `decode_payload_marked` is the decode that
returns the `UnreadSlots` mark. This runtime has no compatible schema ids, so no mark is consented.

## Structure Of Arrays

The C# CultCache hot path exposes CPU-local SoA scans as:

```csharp
Span<float> x = cache.Soa<PlayerTransform>()
    .Column<float>(nameof(PlayerTransform.PositionX))
    .Span;
```

Rust cannot discover those columns through runtime reflection, so each domain
type declares its column extractors explicitly with `SoaDocument` or the
`cultcache_soa!` helper:

```rust
cultcache_soa!(PlayerTransform {
    "position_x" => |row: &PlayerTransform| row.position_x,
    "health" => |row: &PlayerTransform| row.health,
});

let table = cache.soa::<PlayerTransform>()?;
let health = table.column::<i32>("health")?;
```

This is the same public shape as the C# rite: callers ask the cache for a typed
table, then pull contiguous typed columns by name. The Rust version is explicit
about the extractor table because the closed-world schema boundary is a virtue,
not a missing reflection party trick.

## Backing Store Routing

Generic store:

```rust
cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new("cache.cc"))?;
```

Type-specific store:

```rust
cache.add_backing_store(
    SingleFileMessagePackBackingStore::new("players.cc"),
    ["player"],
)?;
```

The contract, shared with every runtime, is one home store per document type
(`src/GameCult.Caching/Contracts/cultcache-store-composition.md` in CultLib):

- a type-specific store owns its types; otherwise the generic store is the home
- a second generic store is an error, and so is a type claimed by a second
  store; both registrations return `Err` and attach nothing
- a write touches exactly one store; there are no mirrors
- a write whose type has no home store is an error

## Persistence Semantics

`put` persists before mutating the in-memory cache. If persistence fails, the
cache does not pretend the write succeeded.

The envelope is MessagePack, and the payload inside each envelope is also raw
MessagePack bytes encoded from the registered concrete `DatabaseEntry` type.
That avoids the old bootstrap path where payloads were normalized through
`serde_json::Value`.

For bit-compatible neighbors, that also enables a real fast lane:

- `get_envelope::<T>()` exports the canonical persisted bytes for a typed entry
- `put_envelope::<T>()` ingests the same envelope into another cache instance
  without re-encoding the payload first

It still decodes once for validation and typed reads. We are not pretending one
`Vec<u8>` became metaphysically zero-copy because we believed in it harder. But
the stupid decode/re-encode loop is gone.

The `DatabaseEntry` derive does not trust Rust source field order as the durable
schema. Every persisted member must declare a stable integer slot:

```rust
#[derive(Clone, DatabaseEntry)]
#[cultcache(type = "player")]
pub struct PlayerData {
    #[cultcache(key = 0)]
    pub id: String,

    #[cultcache(key = 1)]
    pub name: String,

    #[cultcache(key = 2, default)]
    pub level: u32,
}
```

The derive emits a tuple/array formatter. Gaps serialize as nil. Missing fields
marked `default` use `Default::default()` when older payloads are read. Deleted
field slots should stay reserved until an explicit store migration rewrites the
data.

The single-file MessagePack store rewrites an atomic snapshot. That is a sane
starting point for small typed state surfaces, settings, Epiphany agent memory,
heartbeat state, and other compact control-plane data. Large corpora should use
a sharded store or a real database instead of asking one file to become a
warehouse and then acting wounded when physics invoices us.

The single-file store uses a sidecar lock file for shared reads and exclusive
writes. That protects ordinary multi-process access to the same file. It is
still not a multi-master replication protocol, and it is not a substitute for a
coordinator when higher-level write ordering matters.

A write that fails part way, for example on a full disk, removes its staging
file and leaves the store as it was. A read that fails tells you why through
the error chain:

```rust
use cultcache_rs::{CacheBackingStore, CultCacheStoreUnreadable, CultCacheStoreUnreadableKind, SingleFileMessagePackBackingStore};

let store = SingleFileMessagePackBackingStore::new("cache.cc");
if let Err(error) = store.pull_all() {
    match error.downcast_ref::<CultCacheStoreUnreadable>().map(|unreadable| unreadable.kind) {
        // The bytes were read and are not a store: truncated, garbled, or invalid.
        Some(CultCacheStoreUnreadableKind::Undecodable) => {}
        // The header names a format this runtime does not read, such as a newer one.
        Some(CultCacheStoreUnreadableKind::UnsupportedFormat) => {}
        // The bytes could not be read at all: a std::io::Error is in the chain.
        _ => {}
    }
}
```

An I/O failure never carries `CultCacheStoreUnreadable`, and the chain under
`CultCacheStoreUnreadable` never holds a `std::io::Error`.

Only a store file that does not exist (`std::io::ErrorKind::NotFound`) reads as
an empty store. Any other failure to reach it, such as a symlink loop, a file
where a parent directory should be, or a missing permission, is an I/O error.
A store file that exists and holds zero bytes is `Undecodable`: no CultCache
writer leaves one. A symbolic link whose target is gone is not a missing store:
reading it is an I/O error. A write at a store path that is a link, live or dangling, is
refused with `StoreLinkedError` (`error.downcast_ref::<StoreLinkedError>()`) before the
lock and the replace, so no write swaps a link for a regular file; configure the link's
target as the store path. Reads through a live link work. A refusal names no
value read from the file; it names a record only by its key and schema id, and
a header only when it has the shape `cultcache.store.v<digits>`.

A write that fails says what it left on disk:

```rust
use cultcache_rs::{CultCacheStoreWriteFailed, CultCacheStoreWriteFailedKind};

fn outcome(error: &anyhow::Error) {
    match error.downcast_ref::<CultCacheStoreWriteFailed>().map(|failed| failed.kind) {
        // The entries cannot be stored; retrying them fails the same way.
        Some(CultCacheStoreWriteFailedKind::Rejected) => {}
        // Staging, fsync or rename failed: the store file holds what it held before,
        // and a retry may succeed once the fault (a full disk, say) clears.
        Some(CultCacheStoreWriteFailedKind::NotReplaced) => {}
        // The rename landed and the directory sync failed: the new snapshot is in
        // place but may not survive a crash.
        Some(CultCacheStoreWriteFailedKind::ReplacedNotDurable) => {}
        _ => {}
    }
}
```

## Store format and rollback

A store whose elements hold an id is marked `cultcache.store.v3` by the C# runtime. This runtime reads and writes a v3 single-file store, keeps the header it read and carries the ids through a rewrite; it never drops the mark, so it has no carry-back. A cultcache-rs from before element ids refuses a v3 store by name, so rolling back to it after a store has been written as v3 is one-way: restore from a copy taken before upgrading, and upgrade every reader before any writer. (The C# line is different: CultLib 1.x does not refuse; see the GameCult.Caching README.)

## Near-Term Ergonomic Improvements

1. **Derive macro**
   - `#[derive(DatabaseEntry)]`
   - `#[cultcache(type = "...")]`
   - `#[cultcache(key = N)]` on every persisted field
   - tuple/array formatter generation landed
   - optional `#[cultcache(name)]`, `#[cultcache(index)]`, and
     `#[cultcache(global)]` are still future work

2. **Generated registry**
   - a `CultCacheRegistry` trait
   - `cache.register_registry(GameCultEntries)`
   - basic macro registry landed
   - automatic inventory/build-script generation and store routes are future work

3. **Name and index lookups**
   - `cache.get_by_name::<T>("Potion")`
   - `cache.get_by_index::<T>("callsign", "Lucent-7")`: `#[cultcache(index)]` is unique. Within one type no two
     records hold one non-blank value of the alias; a put, batch or load that would make two is refused, naming both
     keys and the alias, and writes nothing. A grouping (faction, kind, status) is not an index: filter `get_all`.
   - generated extractors instead of reflection strings

4. **Schema/version metadata**
   - schema version in the envelope or payload metadata
   - explicit migration hooks
   - refusal on unknown persisted entry types unless a migration/resolver is
     installed

5. **Projection helpers**
   - JSON projection for review and git diffs
   - vector stripping / large-field elision for agent memory surfaces

## Why Not Skip Registration Immediately?

Rust can only deserialize polymorphic data into concrete types if something
maps the persisted `type` discriminator to the Rust type. In C#, reflection and
MessagePack resolvers can discover a closed `DatabaseEntry` inheritance tree at
runtime and route it through known formatters. In Rust, that map has to come from
somewhere:

- explicit registration
- a generated registry
- a macro inventory crate
- a hand-written resolver

Explicit registration is the simplest honest implementation. Generated registry
is the ergonomic destination. Hand-written resolver tables are the punishment
we give ourselves if we get lazy.

## Security Model

CultCache should never parse arbitrary structured data into arbitrary runtime
types. It only accepts persisted envelopes whose `type` discriminator resolves
to a known `DatabaseEntry` type registered in the cache or generated registry.

That is the Rust version of the original trick:

- C# refuses to store objects outside the `DatabaseEntry` root type.
- Runtime-visible `DatabaseEntry` subclasses get dedicated formatters.
- A write of an unregistered type is rejected; a stored record of an unregistered type is carried as foreign, not decoded.
- MessagePack payload bytes deserialize through known concrete formatters, not
  an open-world type loader or generic structured-value bridge.

The derive/registry work should preserve that shape. Its job is to remove
manual registration ceremony, not to loosen the closed-world boundary.

Payload nesting is bounded at 256 MessagePack containers, checked before any
recursion; past the bound the refusal is a typed `PayloadTooDeep`. Typed decode
of stored bytes then grows its stack: the decoder checks the remaining stack
at entry and `serde_stacker` checks it at every container, moving
onto a fresh segment whenever less than 512 KiB remains, so a payload inside the
bound decodes on a small thread instead of aborting the process. Envelope and
store reads (single-file, redb, v1 snapshots) use the same grown decoder and
keep accepting and refusing what they did, without the payload walk.

The guarantee holds for records the derives produce. `MAX_RECORD_INLINE_BYTES`
(16 KiB) is the largest `size_of` a `DatabaseEntry`, `CultSlots` or `CultUnion`
record may have, and the derives refuse a larger one at compile time: put its
large data behind a `Vec` or a `Box`. A field of a type the derives did not
produce that is larger than that, behind a `Box` or `Vec`, is outside the
guarantee. The decoder's own frames have a fixed floor that no payload changes: at the bound a thread decodes a record of
this size from about 56 KiB in a release build and 72 KiB in a debug build for a caller that only tests the result
(`is_ok`), plus the caller's own frames. The figure depends on how the caller holds the result: one that moves the
record out holds a second copy and needs more (about 104 KiB in debug). Stack growth was verified on
x86_64-pc-windows-msvc by hand (`decode_stack`, revision 10a39dac, 13 tests, debug and release); a `stacker` bump or a change
to the stack-size tests repeats it.
The crate does not
build for wasm32 (`fs2`), and no wasm behaviour is claimed.

### Build dependencies

Stack growth adds these crates to every consumer's build, and a C toolchain
(`psm`, `stacker`). On all targets: `serde_stacker` 0.1.14, `stacker` 0.1.25,
`psm` 0.1.32, `cc` 1.8.0, `find-msvc-tools` 0.1.14, `shlex` 2.0.1 (MIT OR
Apache-2.0), `object` 0.39.1 (Apache-2.0 OR MIT) and `ar_archive_writer` 0.5.3
(Apache-2.0 WITH LLVM-exception, build-only). On `x86_64-pc-windows-msvc` also
`libc` 0.2.190 and `windows-sys` 0.61.2 (MIT OR Apache-2.0), beside the 0.59 the
crate uses itself.

## License

Private GameCult infrastructure for now. Public packaging can wait until the
API grows enough teeth to deserve strangers.
