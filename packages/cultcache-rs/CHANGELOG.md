# Changelog

All notable changes to `cultcache-rs` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases are recorded
only in git history.

## Unreleased

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a record that holds an element id (an owned list element with an `[CultElementId]` member, carried through every write) marks the store v3, and a v3 store is carried through a rewrite with its ids. A runtime from before this change refuses a v3 store by name.
- A write over a file the reader refuses (a zero-byte file, a truncated store, a stamp this runtime does not read) refuses with `CultCacheStoreUnreadable` and leaves the file as it is. A whole-store write used to replace such a file. This reverses 0.3.1, where `push_all` replaced a file with no readable stamp (healing it); the stamp rule now has one owner, the reader every write asks under the lock, and it accepts v1 and v3 (0.3.1 refused v3).
- `#[cultcache(index)]` means unique, as in C# `RefuseIndexSharing`: within one type and one alias no two records hold one non-blank value. A put, batch, push or load that would make two records hold a value is refused, naming both keys and the alias, and a store that already holds a duplicate does not open. A grouping index is no longer expressible; a consumer that relied on shared values must drop the `index` and look up by another way.
- `bool` index and lookup text renders `True` / `False`, as C# does, where it rendered `true` / `false`.
- `DatabaseEntry::UNIQUE_INDEXES` replaces `cult_indexes` as the one index declaration and changes type (its members are the unique aliases); a hand implementation overriding `cult_indexes` no longer compiles.
- `register_entry_type` over a type that already holds records re-judges them under the new declaration (unique indexes and the global rule) and refuses, changing nothing, when they violate it. A declaration with an index also refuses when a held record cannot be decoded as the new type; a declaration without one does not read the records, so it does not.

### Added

- Lookups, matching the C# `GetByName`, `GetByIndex` and `GetGlobal`. `#[cultcache(name)]` and `#[cultcache(index)]` / `#[cultcache(index = "alias")]` on `DatabaseEntry` fields and `#[cultcache(global)]` on the struct give `CultCache::get_by_name`, `get_by_index` and `get_global`. An empty name and a blank index value are not found; records of one type sharing a name find the one with the smallest key (an index value is unique, below). A second record of a global type under another key is refused on every write path and on load with `CultCacheSecondGlobal`. Two `name` members, or two members with one index alias, are refused at compile time. A member's text comes from `CultLookupValue` (`String`, `Option` of one, integers, `bool`, `char`).
- `SchemaConflictError`, the typed refusal of a write the catalog cannot describe: records of different types under one schema id, or an arrived schema and a registered one that share an id and disagree on the schema name. A whole-store write (`push_all`) refuses with it, names the schema id, the names involved and a record key, writes nothing and leaves the file as it is. A caller tells it apart with `error.downcast_ref::<SchemaConflictError>()`.

## [0.3.1]

### Fixed
- `push_all` reads the store's header under the lock and refuses a format this runtime does
  not read, with `CultCacheStoreUnreadable` `UnsupportedFormat`, leaving the file as it was.
  A file with no readable stamp is still replaced, so a whole write heals it.
