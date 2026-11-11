# Changelog

## Unreleased

### Changed

- `msgpack` is a hard dependency, as `pyproject.toml` declared it. The optional-import fallbacks and the `cultcache-py[msgpack]` extra they named (which never existed) are gone: a missing msgpack is an `ImportError` at import.
- The single-file store's `push`, `push_all` and `delete` take the cross-process lock on `<path>.lock` (flock; LockFileEx over the whole file on Windows) across the read and the replace, waiting up to `lock_timeout` seconds (30 by default, `TimeoutError` after), and exclude the C#, Rust and TypeScript runtimes' writers. The staged file is `<path>.tmp-<pid>-<uuid>`, fsynced before the replace, with a directory fsync after it off Windows. A fixed `<path>.tmp` is no longer used.

### Breaking

- `indexes` are unique, as C# `[CultIndex]`: a write, load or `register_index` that leaves two records of a type holding one non-blank value under one alias is refused (`CultCacheError`) naming both keys and the alias, and writes nothing. Index text is the C# canonical text (`True`/`False`, integers as digits, floats as the exact expansion, blank values never indexed), so an empty or whitespace string is no longer a value. A new `roles` map names the many-row selection aliases (never indexed for lookup, same catalog `index_alias`). Classify every `indexes` key per the README before updating. The verdict is the contract, the text is informational.

- A single-file store written as `cultcache.store.v3` is read and written: a record that holds an element id (an owned list element with an `[CultElementId]` member, carried through every write) marks the store v3, and a v3 store is carried through a rewrite with its ids. A runtime from before this change refuses a v3 store by name. Rolling back to an older release after a store has been written as v3 is one-way: there is no carry-back, so restore from a copy taken before upgrading.
- A write over a file the reader refuses (`StoreUnreadableError`, from `push`, `push_all` and `delete`) refuses and leaves the file as it is. A flush used to replace such a file. A write set cannot merge with a disk it cannot read, so the file is repaired or removed by its owner before the store writes again.
- Added `StoreUnreadableError` (a `ValueError`) for a store file this runtime cannot read, and `SchemaConflictError` (a `ValueError`) for a
  write the catalog cannot describe. A store is exactly three slots; a schema is never recovered from a record's payload.
- The single-file writer derives the catalog from the records it writes: one entry per carried schema id, the registered entry over an
  arrived one, written as chosen and never merged. A cache stamps a record with its registered schema's id.
- Every registration refusal is now a `SchemaConflictError`, a `ValueError`, where it was a `CultCacheError`, a `RuntimeError`: registering a document type or a schema name twice no longer raises what `except CultCacheError` or `except RuntimeError` catches.
- Two documents that own one schema id are refused at registration (`SchemaConflictError`). 2.0.1 accepted them. A document may still list another's id as compatible.
- A loaded record resolves by its schema id first: the document that owns the id, else the one document that lists it as compatible; several listers and no owner refuse the record with `SchemaConflictError`. The record's type name resolves it only when its schema id names no registered document. 2.0.1 resolved by the record's type name alone.
