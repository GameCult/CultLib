# Changelog

All notable changes to `@gamecult/cultcache-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Breaking

- A single-file store marked `cultcache.store.v3` (it holds an element id, written by the C# runtime) is read, and a rewrite keeps the mark and the ids; this runtime does not decide when a document carries ids. A runtime from before this change refuses a v3 store by name, so rolling back to an older release after a store has been written as v3 is one-way: there is no carry-back, so restore from a copy taken before upgrading.
- `indexes` are unique, as C# `[CultIndex]`: a write or a load that leaves two records of a type holding one non-blank
  value under one alias is refused naming both keys and the alias, and writes nothing. Index text is the C# canonical
  text (booleans `True`/`False`, integers as digits, doubles as the exact expansion, blank values never indexed), so
  an empty string is no longer a value. `registerIndex` over held duplicates installs nothing. A new `roles` map names
  the many-row selection aliases (never indexed for lookup, same catalog `indexAlias`). The per-key migration rule is
  in the README; classify every key before the CultLib your tools load is updated. The refusal verdict is the contract, its text is informational.

### Changed

- Opening a legacy (pre-v1) store is a pure read: payloads that were not bytes are normalized in memory and the
  file is left as it was, so a codec that refuses the content cannot lose the old bytes. The next push, delete or
  flush rewrites the file as v1, merging what the open read.
- `SingleFileStoreRead` no longer carries `repairedLegacyPayload`; no consumer used it.

## [0.15.0]

0.14.1 and 0.14.2 were never tagged or published; this entry carries them with the Unreleased work, and `cultcache-ts-v0.14.0` is the previous release.

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a store the C# runtime marked v3 (it holds a record with an element id: an owned list element with a `[CultElementId]` member) keeps its header here, and is carried through every rewrite with its ids; this runtime never drops the mark. A runtime from before this change refuses a v3 store by name.
- A single-file store refuses a zero-byte file as `StoreUnreadableError`; only a path with nothing at it is an empty store. A header this runtime does not read is refused, and the refusal echoes the header only as `cultcache.store.v<digits>`.
- A write over a file the reader refuses (`StoreUnreadableError`, from `push`, `pushAll` and `delete`) refuses and leaves the file as it is. A flush used to replace such a file. A write set cannot merge with a disk it cannot read, so the file is repaired or removed by its owner before the store writes again. A full `pushAll` over a file with no readable stamp does not replace it: the reader refuses it. The stamp rule has one owner, `refuseUnreadFormat`, asked only by the one reader that every write consults under the lock. The reader judges the stamp from the header before it decodes the body, so a newer store is refused by name whether or not its body is whole.
- A dangling symbolic link at the store path is an I/O error with code `ENOENT`, not an empty store.

### Added

- `@gamecult/cultcache-ts/browser`, also what a bundler resolves for the package root under the `browser` export condition: the root surface without `SingleFileMessagePackBackingStore` and `readSingleFileStore`, so it imports no `node:*` built-in and no `koffi` and bundles with esbuild `platform: "browser"`. The Node entry exports exactly what it did.
- `readSingleFileStore(path)`, the one synchronous reader of a single-file store, for consumers that must read a store file rather than decode it themselves. `pullAll`, `push`, `delete` and `pushAll` ask the same reader.
- `StoreUnreadableError` for a store file this runtime cannot read, `SchemaConflictError` for a write the catalog cannot describe, and `schemaIdentityOf` with its result type `CultCacheSchemaIdentity` (`schemaId`, `schemaName`, `schemaVersion`, `compatibleSchemaIds`), the one derivation of a definition's identity.
- The document payload codec, exported from `@gamecult/cultcache-ts/document`: `decodeDocumentPayload`, `encodeDocumentPayload`, and the two named byte escapes `documentPayloadFromBytes` and `documentPayloadBytes`.
- `SingleFileStoreRead`, exported: the result type of `readSingleFileStore` (`absent`, `format`, `envelopes`, `repairedLegacyPayload`).
- `CultCacheInspection.format` is `StoreFormat | "legacy.envelope-array"`: it reports `cultcache.store.v3` for a v3 store.
- A soft `pushAll` writes only where nothing is at the path, leaves a store it can read byte for byte, and refuses what the reader refuses.
- `SingleFileMessagePackBackingStore` takes a cross-process write lock on `<file>.lock`,
  interoperable with the C# and Rust runtimes (`flock` on Unix, `LockFileEx` on Windows).
  `push` and `delete` read the current file, merge and replace it under the lock, so concurrent
  TypeScript and Rust writers no longer lose each other's records across those calls. A full
  `pushAll` still replaces the whole store, so records a concurrent writer added are lost to it.
  C# writes are not all alike: a conditional commit on a clean store lands onto the file as it
  is, but C# writers can still overwrite records a concurrent TypeScript writer added.
- The staged file is fsynced before the replace, and on Unix the directory after it. A
  directory sync failure after the replace is reported as replaced but not durable.
- `checkPayloadDepth` is re-exported from `@gamecult/cultcache-ts/document`, so dependents
  (`cultnet-ts/content`) bound payload nesting with the same check the cache
  applies on decode.

### Changed

- The Windows replace is a POSIX-semantics rename first (it lands while C# and Rust readers
  hold the store open), falling back to a write-through `MoveFileExW`, with 25 attempts 20 ms
  apart on errors 5, 32 and 33. Writers wait up to 30 s for the lock.

### Dependencies

- `koffi` 3.3.0, loaded only on the first write.
