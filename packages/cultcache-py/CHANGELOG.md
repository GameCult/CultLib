# Changelog

## Unreleased

### Breaking

- A single-file store written as `cultcache.store.v3` is read and written: a record that holds an element id (an owned list element with an `[CultElementId]` member, carried through every write) marks the store v3, and a v3 store is carried through a rewrite with its ids. A runtime from before this change refuses a v3 store by name.
- A write over a file the reader refuses (`StoreUnreadableError`, from `push`, `push_all` and `delete`) refuses and leaves the file as it is. A flush used to replace such a file. A write set cannot merge with a disk it cannot read, so the file is repaired or removed by its owner before the store writes again.
- Added `StoreUnreadableError` (a `ValueError`) for a store file this runtime cannot read, and `SchemaConflictError` (a `ValueError`) for a
  write the catalog cannot describe. A store is exactly three slots; a schema is never recovered from a record's payload.
- The single-file writer derives the catalog from the records it writes: one entry per carried schema id, the registered entry over an
  arrived one, written as chosen and never merged. A cache stamps a record with its registered schema's id.
- Every registration refusal is now a `SchemaConflictError`, a `ValueError`, where it was a `CultCacheError`, a `RuntimeError`: registering a document type or a schema name twice no longer raises what `except CultCacheError` or `except RuntimeError` catches.
- Two documents that own one schema id are refused at registration (`SchemaConflictError`). 2.0.1 accepted them. A document may still list another's id as compatible.
- A loaded record resolves by its schema id first: the document that owns the id, else the one document that lists it as compatible; several listers and no owner refuse the record with `SchemaConflictError`. The record's type name resolves it only when its schema id names no registered document. 2.0.1 resolved by the record's type name alone.
