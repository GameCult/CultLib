# Changelog

All notable changes to `cultcache-rs` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases are recorded
only in git history.

## Unreleased

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a store the C# runtime marked v3 (it holds a record with an element id: an owned list element with a `[CultElementId]` member) keeps its header here, and is carried through every rewrite with its ids; this runtime never drops the mark. A runtime from before this change refuses a v3 store by name. Rolling back to an older release after a store has been written as v3 is one-way: there is no carry-back, so restore from a copy taken before upgrading.
- A write over a file the reader refuses (a zero-byte file, a truncated store, a stamp this runtime does not read) refuses with `CultCacheStoreUnreadable` and leaves the file as it is. A whole-store write used to replace such a file. This reverses 0.3.1, where `push_all` replaced a file with no readable stamp (healing it); the stamp rule now has one owner, the reader every write asks under the lock, and it accepts v1 and v3 (0.3.1 refused v3).
- `#[cultcache(index)]` means unique, as in C# `RefuseIndexSharing`: within one type and one alias no two records hold one non-blank value. A put, batch, push or load that would make two records hold a value is refused, naming both keys and the alias, and a store that already holds a duplicate does not open. A grouping index is no longer expressible; a consumer that relied on shared values must drop the `index` and look up by another way.
- `bool` index and lookup text renders `True` / `False`, as C# does, where it rendered `true` / `false`.
- `DatabaseEntry::UNIQUE_INDEXES` replaces `cult_indexes` as the one index declaration and changes type (its members are the unique aliases); a hand implementation overriding `cult_indexes` no longer compiles.
- `register_entry_type` over a type that already holds records re-judges them under the new declaration (unique indexes and the global rule) and refuses, changing nothing, when they violate it. A declaration with an index also refuses when a held record cannot be decoded as the new type; a declaration without one does not read the records, so it does not.
- A single-file store holds one record per key. The v1 and legacy readers refuse a store with two records under one key (`CultCacheStoreUnreadable`, the record `Undecodable`): the store does not open, and a write over it refuses and leaves the file. A consumer that holds such a file must repair it with the runtime that wrote it before opening it here.
- Every single-file writer (`push`, `push_all`, `insert_entry_if_absent`, the compare-exchange and snapshot-append family, and the cache's puts and batches) refuses to put a second type under a key the store holds, with `SchemaConflictError` naming the key and both types, writing nothing. Retyping a key is a delete, then an add (one batch may hold both). A write owns the held record when its schema id equals the id the held record is stored under, as C# `WrittenOwns` asks; a type renamed under a stable id therefore replaces its own record, and a write of another type name under another id is refused (a write of the same type name under another id still replaces the record, as before). Registered types here declare no compatible ids, so equality is the whole test. Writers used to leave two records under the key.
- `compare_and_swap_entry` over a single file refuses an in-place retype (expected `A/k`, replacement `B/k` under another schema id) with the same `SchemaConflictError`, as `compare_exchange` does, where it used to retype the record and return `true`. A lost race still returns `false` first.
- A write of the same type under a held key replaces the record by key, and in one `push_all` the last of several same-type entries under a key is the record. The redb stores are unchanged and still key by type and key.

### Added

- Lookups, matching the C# `GetByName`, `GetByIndex` and `GetGlobal`. `#[cultcache(name)]` and `#[cultcache(index)]` / `#[cultcache(index = "alias")]` on `DatabaseEntry` fields and `#[cultcache(global)]` on the struct give `CultCache::get_by_name`, `get_by_index` and `get_global`. An empty name and a blank index value are not found; records of one type sharing a name find the one with the smallest key (an index value is unique, below). A second record of a global type under another key is refused on every write path and on load with `CultCacheSecondGlobal`. Two `name` members, or two members with one index alias, are refused at compile time. A member's text comes from `CultLookupValue` (`String`, `Option` of one, integers, `bool`, `char`).
- `SchemaConflictError`, the typed refusal of a write the catalog cannot describe: records of different types under one schema id, or an arrived schema and a registered one that share an id and disagree on the schema name. A whole-store write (`push_all`) refuses with it, names the schema id, the names involved and a record key, writes nothing and leaves the file as it is. A caller tells it apart with `error.downcast_ref::<SchemaConflictError>()`.

## [0.3.1]

### Fixed
- `push_all` reads the store's header under the lock and refuses a format this runtime does
  not read, with `CultCacheStoreUnreadable` `UnsupportedFormat`, leaving the file as it was.
  A file with no readable stamp is still replaced, so a whole write heals it.
