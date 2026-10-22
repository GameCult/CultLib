# Changelog

## Unreleased

- A write over a file the reader refuses (`StoreUnreadableError`, from `push`, `push_all` and `delete`) refuses and leaves the file as it is. A flush used to replace such a file. A write set cannot merge with a disk it cannot read, so the file is repaired or removed by its owner before the store writes again.
- Added `StoreUnreadableError` (a `ValueError`) for a store file this runtime cannot read, and `SchemaConflictError` (a `ValueError`) for a
  write the catalog cannot describe. A store is exactly three slots; a schema is never recovered from a record's payload.
- The single-file writer derives the catalog from the records it writes: one entry per carried schema id, the registered entry over an
  arrived one, written as chosen and never merged. A cache stamps a record with its registered schema's id.
