# Changelog

## 0.15.0

### Breaking

- A single-file store refuses a zero-byte file as `StoreUnreadableError`; only a path with nothing at it is an empty store. A header this runtime does not read is refused, and the refusal echoes the header only as `cultcache.store.v<digits>`.
- A dangling symbolic link at the store path is an I/O error with code `ENOENT`, not an empty store.

### Added

- `readSingleFileStore(path)`, the one synchronous reader of a single-file store, for consumers that must read a store file rather than decode it themselves. `pullAll`, `push`, `delete` and `pushAll` ask the same reader.
- `StoreUnreadableError` for a store file this runtime cannot read, `SchemaConflictError` for a write the catalog cannot describe, and `schemaIdentityOf`.
- A soft `pushAll` writes only where nothing is at the path, leaves a store it can read byte for byte, and refuses what the reader refuses.
