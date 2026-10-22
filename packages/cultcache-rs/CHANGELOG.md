# Changelog

All notable changes to `cultcache-rs` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases are recorded
only in git history.

## Unreleased

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a record that holds an element id (an owned list element with an `[CultElementId]` member, carried through every write) marks the store v3, and a v3 store is carried through a rewrite with its ids. A runtime from before this change refuses a v3 store by name.
- A write over a file the reader refuses (a zero-byte file, a truncated store, a stamp this runtime does not read) refuses with `CultCacheStoreUnreadable` and leaves the file as it is. A whole-store write used to replace such a file.
