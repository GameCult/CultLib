# key-identity vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/KeyIdentityVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1` (never set in CI). Every other run reads these files, runs every row against the reference and
compares the files byte for byte; a changed vector is a reviewed commit. Other runtimes are held to the same verdicts
(`variants:ruling:cache-refuses-retype`, `variants:ruling:write-ownership-by-written-type`,
`variants:ruling:unheld-removal-refused-at-commit`).

RULE: a cache holds one record per key whatever its type; a write of a type that does not own the held record's schema id at
that key is refused as a schema conflict naming the key, with the cache, its store file and every foreign record
byte-identical; retyping is a delete then a put. A store that holds a record no registered type claims loads: the cache does
not serve it, lists its key as foreign, keeps its bytes through every write, and refuses a write or a removal at its key.
Registering a type that declares its schema id claims it at the next load.

## Files

- Schemas: `tests.ki_alpha`, `tests.ki_beta`, `tests.ki_gamma` (`alpha`, `beta`, `gamma` below), each one string slot
  `[0: Text]`, version the name plus `.v1`. The schema ids C# writes in the store catalogs and on every record:
  - `tests.ki_alpha`: `sha256:b20703a98f49c253898404ab50ba2c630720f0b8a5e1a41614d5650be22799fe`
  - `tests.ki_beta`: `sha256:dee4b727cf2ee04e61ab635ff0c5f3dca7345a411dc5f1018e583ab16009cf94`
  - `tests.ki_gamma`: `sha256:fbc26e351e00c9e7185e8de171c7486aba707c229c949f0aae6b889288af5f6d`
- `foreign-store.cc`: a single-file store, alpha `a` at key `j`, gamma `g` at key `k`, stored-at pinned to
  `2026-01-01T00:00:00Z`. `catalog-store.cc`: alpha `a` at `qx`. `run-store.cc`: beta `b` at `qx` (row 11). The row-11 key is `qx` because it must
  occur in the refusal's message only as the key, never inside a schema name (`k` is inside `ki_alpha`).
- `rows.msgpack`: an array of `[label, registered, ops, expect]`.
  - `registered`: the types the cache registers, a subset of `[alpha, beta, gamma]`.
  - `ops`, in order: `[open, file-or-nil-or-memory]` (a cache on a fresh copy of the vector file; nil is a fresh path with no
    file; the string `memory` is a cache with no store), `[attach, file, [home types]]` (route one more store, a fresh copy
    of the vector file, to those home types, and pull every store), `[reopen]` (dispose, then a new cache on the same path),
    `[put, type, key, text]`, `[delete, type, key]` (each one commit).
  - `expect` is `[refusal, held, file, foreign, kept]`. Every op but the last succeeds; the last succeeds or refuses, and in the generator only a schema conflict (or, for an attach, an invalid-operation refusal) counts as a refusal: any other exception fails the run.
    `refusal` is nil or `[key]`: a typed schema-conflict refusal naming the key for a put or a delete (rows 1, 3, 5, 8, 9),
    and for row 11 any refusal (an invalid-operation error, not a schema conflict) whose message contains the key as a delimited token (not inside a schema name or other word; the C# walker also pins its own `Record <key> is held by` opening, which a leaf need not copy); the key in `expect` is the one the stores share, the store file it was refused from left byte-identical.
    `held` is the cache's records, sorted by type then key, as `[type, key, text]`. `file` is the store file's records after
    the ops, sorted by key, as `[key, schema name]`, nil for a cache with no store. `foreign` is the keys the cache lists as
    foreign, sorted. `kept` is the foreign keys whose stored record (schema id, stored-at, payload) after the ops equals the
    file's before the first op.

## Rows

1 retype-refused-held-in-file, 2 retype-by-remove-then-add, 3 retype-refused-memory-cache, 4 same-type-replaces, 5
retype-refused-after-reload, 6 foreign-loads, 7 foreign-kept-by-a-write, 8 foreign-key-write-refused, 9
foreign-key-delete-refused, 10 foreign-claimed-by-a-later-registration (registered `[alpha, gamma]`: row 6's store loads
with nothing foreign), 11 key-in-two-stores-refused, 12 same-type-replaces-memory-cache (a cache with no store replaces a same-type record: admitted).

Every runtime walks 12 rows and asserts the count; none is exempt (every row is single-record ops). A leaf never registers
gamma in rows 6 and 8; only row 10 does.
