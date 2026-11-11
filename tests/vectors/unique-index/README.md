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
- `pair-store.cc`, `surrogate-store.cc`, `types-store.cc`, `repull-before.cc`, `repull-after.cc`: single-file stores
  written like `duplicate-store.cc`, through roles, for the rows of `refusals.msgpack` that open or pull them. Schemas
  `tests.unique_item`, `tests.uq_pair` (Sku and Alt both unique), `tests.uq_alpha` and `tests.uq_beta`, each slots
  `[0: Sku string, 1: second string]`.
- `outcomes.msgpack`: rows `[file, unique alias or nil, open|refused, k1 or nil, k2 or nil]`. Opening `file` with that
  alias a unique index (nil: a role, no unique alias) yields the verdict. A refusal reads, byte for byte,
  `Records <k1> and <k2> (tests.unique_item) both hold unique index '<alias>'; an index value names one record.`
  with `k1 < k2` ordinal, never contains the value, and leaves the file byte-identical.

## refusals.msgpack

RULE: a unique-index refusal is a function of the state the admission would leave, never of staging, file or pull
order. Over every (type, unique alias, non-blank value) that state holds at two or more distinct keys, name the one
least by (alias, its least key, its second-least key, schema name), each compared by UTF-16 code unit (C#
`StringComparer.Ordinal`): `Records <k1> and <k2> (<schema>) both hold unique index '<alias>'; an index value names one
record.` with `k1 < k2` its two least keys. One rule for a single write, a batch, a load, a re-pull and an index
registration.

Rows `[label, ops, refusal]`. Each op is `["open", file-or-nil]` (copy that vector file to a fresh path, or nil for a
fresh path with no file, and open a cache on it), `["pull", file]` (overwrite the open cache's file with that vector
file, then pull every backing store again) or `["commit", [[key, schema, sku, second], ...]]` (one batch, staged in row
order; schema is the schema name). `refusal` is nil (every op succeeds) or `[k1, k2, schema, alias]`: every op but the
last succeeds and the last refuses with exactly the RULE text built from those fields, leaving the file
byte-identical. Schemas are those of the store files above; `second` is `Category` (plain) except in `tests.uq_pair`,
where it is `Alt` (unique).

A runtime performs every row its API can perform and asserts the count it walked: C# and Rust 13; Python 12 (not
`batch-interleaved-types`: `put_envelopes` takes one type); TypeScript 7 (`held-then-landing` and the five load and
re-pull rows 8-13: no batch commit, `variants:idea:unique-index-runtimes.ts-batch-commit`).
