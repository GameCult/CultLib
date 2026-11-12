# Changelog

All notable changes to `@gamecult/cultcache-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Breaking

- `SingleFileMessagePackBackingStore.pushAll` through a dangling symbolic link at the store path is refused with `ENOENT`, as `push` and `pullAll` are, and writes nothing; it used to replace the link with a new store. The reader owns the verdict on what is at the path.
- `SingleFileMessagePackBackingStore.pushAll` writes the given entries onto the store as it is under the lock, as `push` does: a record under the same key is replaced and no other is removed. It used to replace the whole file with the entries, so `CultMesh.flush()` from a cache that held less than the store (a node created with `pullOnStart: false`, or one whose image was stale) wiped the records it never read. A caller that relied on `pushAll` to reset a store removes the file first. A soft flush is unchanged. `CultCache.storedSnapshot()` is the image as the stores hold it (a global loaded from a legacy key under that key) and is what `CultMesh.flush` persists, so a flush no longer leaves such a global under two keys; `snapshot()` is unchanged.
- A single-file store marked `cultcache.store.v3` (it holds an element id, written by the C# runtime) is read, and a rewrite keeps the mark and the ids; this runtime does not decide when a document carries ids. A runtime from before this change refuses a v3 store by name, so rolling back to an older release after a store has been written as v3 is one-way: there is no carry-back, so restore from a copy taken before upgrading.
- `indexes` are unique, as C# `[CultIndex]`: a write or a load that leaves two records of a type holding one non-blank
  value under one alias is refused naming both keys and the alias, and writes nothing. Index text is the C# canonical
  text (booleans `True`/`False`, integers as digits, doubles as the exact expansion, blank values never indexed), so
  an empty string is no longer a value. `registerIndex` over held duplicates installs nothing. A new `roles` map names
  the many-row selection aliases (never indexed for lookup, same catalog `indexAlias`). The per-key migration rule is
  in the README; classify every key before the CultLib your tools load is updated. The refusal verdict is the contract, its text is informational.
- The single-file reader (v1 and the legacy envelope array) refuses a store that holds two records under one key as `StoreUnreadableError`; a store that opened before may now be refused. Repair it by removing the extra record with the runtime that wrote it.
- A `put`, `push` or `pushAll` that would land a second type under a held key (against the file, or within one batch) is refused with `SchemaConflictError`, and nothing is written. The written type may replace the record when its catalog entry carries the stored schema id as its own or lists it compatible (a type renamed under a stable id restamps the record, as C# does); otherwise retyping a key is a delete, then a put. The C# allowance for an undeclared older id of the same schema name that reads whole is not ported: a same-named type keeps passing, including one that drops persisted slots, which C# refuses.
- Same-type writes replace by key; in one `pushAll` batch the last entry for a key wins.
- A store whose canonical-schema catalog slot is not a string is refused as `StoreUnreadableError`.
- The cache holds one record per key (variants:ruling:cache-refuses-retype). A `put` or `putEnvelope` that lands a second type under a key the cache holds, or under a key a store carries foreign, is refused with `SchemaConflictError` naming the key, even with no store (a memory-only cache used to hold both types at the key). A type that owns the held record's schema id (as for the store) replaces it, unless the held record is in a store other than the written type's home: that rename is refused with `SchemaConflictError` (remove the record first). Two stores holding one key, registered or foreign, refuse the load and admit nothing. A store's `delete` removes the record whose stored schema id the removal's type owns, whatever the arrived record's `type` string.
- A store holding a record no registered type claims now loads (it used to throw "No schema is registered..."): the cache does not serve the record, lists it in `foreignRecords()`, keeps its bytes through every write, and refuses a `put` or a `delete` at its key with `SchemaConflictError` (variants:ruling:unheld-removal-refused-at-commit). Registering a type that claims its schema id serves it at the next `pullAllBackingStores`.
- A record whose payload carries a slot past its reader's declared slots, at any depth (the record, a nested record, a list element, a union arm, a unit arm), is now held read-only: it loads and reads, and every `put`, `putEnvelope`, `update` or `delete` of its key is refused with `SchemaConflictError` before a store is touched (cultlib-gaps:ruling:extra-trailing-slots, as the C# reference). A load used to skip the extra slots silently and a later write shed them. The refusal names the schema, the key, the type that was read with extra slots and both slot counts, never a slot value, and carries them as `SchemaConflictError.unreadSlots`. An untouched record keeps its bytes through writes of other keys. Missing trailing slots keep their declared defaults and mark nothing. A `put` never marks (the value is encoded by this runtime); a `putEnvelope` of a payload carrying extra slots lands its bytes and holds the record read-only. A mark at depth 0 is consented to only when the record arrived under a schema id other than the reader's own that the reader lists compatible, and that id's catalog entry lists those slots (a writer that adds a slot and keeps the reader's id is never consented to) (`unreadSlots.explained`); every other mark refuses whatever is declared. New: `CultCache.isWritable(definition, key)` (live, advisory), `CultCache.unreadSlots(definition, key)` and the exported `CultCacheUnreadSlots`. Documents with no slot codec (a plain map, a user formatter) never mark. A `delete` is by key: a type that owns the schema id of the record held at the key as another type removes that one record (one store call, the cache dropped once the store has removed it), and a type that does not own it is refused with `SchemaConflictError`, as a `put` is; `isWritable` answers both. Implied semver: a minor bump (0.16.0) while the package is 0.x; a major one at 1.0 and after.

### Added

- `checkPayloadDepth` returns the offset where the first value ends, or `undefined` when the payload ends early (a header, a container or a str/bin/scalar body cut short; it was `void`); it still refuses only depth. The bytes-after refusal is internal (`checkPayloadIsOneValue`, not exported); no new public name.
- `CultCache.foreignRecords()`: the `{key, schemaId, schemaName, storedAt}` of every record the last pull carried and no registered type claims, sorted by key ordinal.

### Changed

- A document payload is exactly one MessagePack value (cultlib-gaps:ruling:trailing-bytes-refuse). `decodeDocumentPayload`, the one reader (store loads, `put`/`putEnvelope`, CultNet replication, the provider command), and the inspector refuse bytes after the first value with `document type "T" payload has N bytes after its first MessagePack value.`, counted from the walk's end and never walked or echoed; a payload that ends early stays the decoder's to refuse. The refused set is unchanged; only the text changed (it was `@msgpack`'s `RangeError` "Extra N of M byte(s)" for a plain document and the codec reader's "bytes after its value" for a codec document, and the inspector's preview no longer shows a tail), so code matching either text must match this one. `readWire` reads one value and no longer checks what follows. Stated limit: a definition with its own `formatter` is not walked, so whether its payload is one value is the formatter's. Implied bump: 0.16.0 (pre-1.0 minor, with the Breaking entries above).
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
