# numeric-leaves vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/NumericLeafVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1`. Every other run reads these files and compares them byte for byte; a changed vector is a
reviewed commit. Other runtimes decode them and are held to the same answers.

These vectors hold every leaf kind, not only numbers. A document with one slot, `[value]`, whose slot is a C# `int`,
`long`, `float` or `double` (leaf `int32`, `int64`, `float32`, `float64`), or a `string`, `bool`, `byte[]`, `object`,
`List<string>`, `string[]`, `int?`, `List<int>`, a two-slot record class, a `List` of it or a `[Union]` interface (leaf
`string`, `boolean`, `bytes`, `any`, `list-string`, `array-string`, `nullable-int32`, `list-int32`, `record`, `list-record`,
`union`).

- `reads.msgpack`: an array of `[leaf, payload, accepted, bits]`. `payload` (bin) is the one-slot record fed to the
  reference; `accepted` is whether it read; `bits` (bin, 8 bytes, big-endian) is the int64 it read, or the IEEE bits of
  the double it read (a float32 widened), and empty when refused. Every value is fed to every leaf.
- `writes.msgpack`: an array of `[leaf, text, bytes]`. `text` is a decimal for an integer leaf and the double's IEEE
  bits in hex for a float leaf; `bytes` (bin) is the one-slot record the reference writes for that value.
- `float32-nan.msgpack`: an array of `[payload, bytes]`. `payload` (bin) is a one-slot `float` record holding an infinity
  or a NaN; `bytes` (bin) is what the reference writes after reading it. It keeps every bit, a signalling NaN's included.
- `leaves-reads.msgpack`: an array of `[leaf, payload, accepted, rewrite]` for the wider leaves. `payload` (bin) is the
  one-slot record `[token]` fed to the reference; `accepted` is whether it read; `rewrite` (bin) is what the reference
  writes after reading it, and empty when refused. Every token is fed to every leaf: 11 leaves x 28 tokens, 308 rows.
  The tokens include an application ext (`d40100`, which the `any` leaf refuses), the timestamp exts (`d6ff00000000`,
  `d7ff0000000000000000`, `c70cff000000000000000000000000`, which the `any` leaf reads and rewrites as `91d6ff00000000`),
  same-shaped non-timestamp exts (`d6fe00000000`, `c70100ff`, which it refuses), and complete, int-in-float-slot and
  str-in-float-slot two-slot records.
  On the `any` leaf's rewrite rows a sibling is held to the rewrite's decoded value, not its MessagePack integer width
  (the reference widens a boxed fixint, so the record `9105` rewrites as `91cc05`; ruling `cultlib-gaps:ruling:any-rewrite-binds-value`).
  Every other leaf's rewrite binds bytes.
- `leaf-record.msgpack`: `[members, payload]` for one `cultcache.leaf-record` document holding every leaf. `members` are
  the catalog rows the single-file store writes, each `[slot, memberName, typeName, isReference, isMany,
  targetSchemaName, isName, indexAlias]`; `payload` (bin) is the record's bytes in that store.
