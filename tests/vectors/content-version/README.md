# content-version vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/ContentVersionVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1` (never set in CI). Every other run opens the committed `store.cc`, compares what the reference reads
to `versions.msgpack` and compares the store bytes the reference writes to the file; a changed vector is a reviewed commit.
Other runtimes compute the version of each record of `store.cc` and must equal these rows
(`variants:ruling:write-set-content-version-declared-ownership`).

RULE: the content version of a stored record is the lowercase hex SHA-256 of its persisted encoding, the MessagePack array
`[key, schemaId, storedAt, payload bytes]` exactly as the store writes the record.

## Files

- `store.cc`: a single-file store of one type, `tests.cv_item` (one string slot, `[0: Text]`), holding `alpha` = "first" and
  `beta` = "second", stored-at pinned to `2026-01-01T00:00:00Z`.
- `versions.msgpack`: an array of `[key, version]`, sorted by key, the version the reference reads for the record
  (`GetStored(key).StoredVersion`).
