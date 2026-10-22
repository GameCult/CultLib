# Changelog

All notable changes to `@gamecult/cultcache-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a record that holds an element id (an owned list element with an `[CultElementId]` member, carried through every write) marks the store v3, and a v3 store is carried through a rewrite with its ids. A runtime from before this change refuses a v3 store by name.
- A single-file store refuses a zero-byte file as `StoreUnreadableError`; only a path with nothing at it is an empty store. A header this runtime does not read is refused, and the refusal echoes the header only as `cultcache.store.v<digits>`.
- A write over a file the reader refuses (`StoreUnreadableError`, from `push`, `pushAll` and `delete`) refuses and leaves the file as it is. A flush used to replace such a file. A write set cannot merge with a disk it cannot read, so the file is repaired or removed by its owner before the store writes again.
- This reverses the 0.14.2 `pushAll` fix in two ways. A full `pushAll` over a file with no readable stamp no longer replaces it (healing it): the reader refuses it, and the file is left as it is. And a `cultcache.store.v3` stamp is read, not refused. The stamp rule has one owner, `refuseUnreadFormat`, asked only by the one reader that every write consults under the lock. The reader judges the stamp from the header before it decodes the body, so a newer store is refused by name whether or not its body is whole.
- A dangling symbolic link at the store path is an I/O error with code `ENOENT`, not an empty store.

### Added

- `readSingleFileStore(path)`, the one synchronous reader of a single-file store, for consumers that must read a store file rather than decode it themselves. `pullAll`, `push`, `delete` and `pushAll` ask the same reader.
- `StoreUnreadableError` for a store file this runtime cannot read, `SchemaConflictError` for a write the catalog cannot describe, and `schemaIdentityOf`.
- A soft `pushAll` writes only where nothing is at the path, leaves a store it can read byte for byte, and refuses what the reader refuses.

## [0.14.2]

### Added
- `SingleFileMessagePackBackingStore` takes a cross-process write lock on `<file>.lock`,
  interoperable with the C# and Rust runtimes (`flock` on Unix, `LockFileEx` on Windows).
  `push` and `delete` read the current file, merge and replace it under the lock, so concurrent
  TypeScript and Rust writers no longer lose each other's records across those calls. A full
  `pushAll` still replaces the whole store, so records a concurrent writer added are lost to it.
  C# writes are not all alike: a conditional commit on a clean store lands onto the file as it
  is, but C# writers can still overwrite records a concurrent TypeScript writer added.
- The staged file is fsynced before the replace, and on Unix the directory after it. A
  directory sync failure after the replace is reported as replaced but not durable.

### Changed
- The Windows replace is a POSIX-semantics rename first (it lands while C# and Rust readers
  hold the store open), falling back to a write-through `MoveFileExW`, with 25 attempts 20 ms
  apart on errors 5, 32 and 33. Writers wait up to 30 s for the lock.
- Opening a legacy store that needs healing rewrites it under the lock, from a read taken
  under the lock.

### Fixed
- A full `pushAll` reads the store's header under the lock first and refuses a store format
  this runtime does not read (the same error a read gives), leaving the file as it was. A
  file with no readable stamp is still replaced, so a whole write heals it. Soft `pushAll`
  is unchanged. The stamp is decoded by the decoder a read uses, so a stamp followed by bytes
  that are not UTF-8 is refused by a whole write as a read refuses it.

### Dependencies
- `koffi` 3.3.0, loaded only on the first write.

## [0.14.1]

### Added
- `checkPayloadDepth` is re-exported from `@gamecult/cultcache-ts/document`, so dependents
  (`cultnet-ts/content`) bound payload nesting with the same check the cache
  applies on decode.
