# Changelog

All notable changes to `@gamecult/cultcache-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.14.2]

### Added
- `SingleFileMessagePackBackingStore` takes a cross-process write lock on `<file>.lock`,
  interoperable with the C# and Rust runtimes (`flock` on Unix, `LockFileEx` on Windows).
  Each write reads the current file, merges and replaces it under the lock, so concurrent
  writers no longer lose each other's records.
- The staged file is fsynced before the replace, and on Unix the directory after it. A
  directory sync failure after the replace is reported as replaced but not durable.

### Changed
- The Windows replace is a POSIX-semantics rename first (it lands while C# and Rust readers
  hold the store open), falling back to a write-through `MoveFileExW`, with 25 attempts 20 ms
  apart on errors 5, 32 and 33. Writers wait up to 30 s for the lock.
- Opening a legacy store that needs healing rewrites it under the lock, from a read taken
  under the lock.

### Dependencies
- `koffi` 3.3.0, loaded only on the first write.

## [0.14.1]

### Added
- `checkPayloadDepth` is re-exported from `@gamecult/cultcache-ts/document`, so dependents
  (`cultnet-ts/content`) bound payload nesting with the same check the cache
  applies on decode.
