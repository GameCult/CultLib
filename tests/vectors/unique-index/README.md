# unique-index vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/UniqueIndexVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1`. Every other run reads these files and compares them byte for byte; a changed vector is a
reviewed commit. Other runtimes are held to the same canonical text and the same verdicts (`variants:ruling:unique-indexes`).

## Files

- `values.msgpack`: an array of `[label, kind, value, text-or-nil]`, kind one of `string`, `int64`, `uint64`, `double`,
  `bool` (decode integers 64-bit wide). `text` is the canonical index text of a member holding `value`: a record holding
  it is found by `GetByIndex(alias, text)`. A nil text is a blank value: no value, never indexed, never judged, found by
  no text, and two records holding it commit. Strings are as is (never trimmed), blank being empty or only C#
  `White_Space` characters (U+0009-000D, U+0020, U+0085, U+00A0, U+1680, U+2000-200A, U+2028, U+2029, U+202F, U+205F,
  U+3000); U+FEFF and U+001F are values. Booleans are `True` and `False`. Integers are their decimal digits. A
  non-integral finite double is its exact decimal expansion (written by C# `RenderExactDouble`, never hand-typed),
  `-0.0` is `0`, NaN and infinities are nil.
- `duplicate-store.cc`: a single-file store, schema `tests.unique_item` v1, slots `[Sku: string, Category: string]`,
  stored-at pinned to `2026-01-01T00:00:00Z`. Records `item:a` and `item:b` both hold Sku `S-1`; `item:c` holds Sku
  `" "` and `item:d` holds Sku `U+0085`. Written through a role Sku.
- `blank-store.cc`: the same, only `item:c` and `item:d`.
- `outcomes.msgpack`: rows `[file, unique alias or nil, open|refused, k1 or nil, k2 or nil]`. Opening `file` with that
  alias a unique index (nil: a role, no unique alias) yields the verdict. A refusal reads, byte for byte,
  `Records <k1> and <k2> (tests.unique_item) both hold unique index '<alias>'; an index value names one record.`
  with `k1 < k2` ordinal, never contains the value, and leaves the file byte-identical.
