# Changelog

All notable changes to `cultcache-rs` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases are recorded
only in git history.

## Unreleased

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a record that holds an element id (an owned list element with an `[CultElementId]` member, carried through every write) marks the store v3, and a v3 store is carried through a rewrite with its ids. A runtime from before this change refuses a v3 store by name.
- A write over a file the reader refuses (a zero-byte file, a truncated store, a stamp this runtime does not read) refuses with `CultCacheStoreUnreadable` and leaves the file as it is. A whole-store write used to replace such a file. This reverses 0.3.1, where `push_all` replaced a file with no readable stamp (healing it); the stamp rule now has one owner, the reader every write asks under the lock, and it accepts v1 and v3 (0.3.1 refused v3).

### Added

- `SchemaConflictError`, the typed refusal of a write the catalog cannot describe: records of different types under one schema id, or an arrived schema and a registered one that share an id and disagree on the schema name. A whole-store write (`push_all`) refuses with it, names the schema id, the names involved and a record key, writes nothing and leaves the file as it is. A caller tells it apart with `error.downcast_ref::<SchemaConflictError>()`.

## [0.3.1]

### Fixed
- `push_all` reads the store's header under the lock and refuses a format this runtime does
  not read, with `CultCacheStoreUnreadable` `UnsupportedFormat`, leaving the file as it was.
  A file with no readable stamp is still replaced, so a whole write heals it.
