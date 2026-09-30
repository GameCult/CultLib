# Changelog

## Unreleased

- Added `StoreUnreadableError` (a `ValueError`) for a store file this runtime cannot read, and `SchemaConflictError` (a `ValueError`) for a
  write the catalog cannot describe. A store is exactly three slots; a schema is never recovered from a record's payload.
- The single-file writer derives the catalog from the records it writes: one entry per carried schema id, the registered entry over an
  arrived one, written as chosen and never merged. A cache stamps a record with its registered schema's id.
