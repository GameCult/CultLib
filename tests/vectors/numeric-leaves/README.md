# numeric-leaves vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/NumericLeafVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1`. Every other run reads these files and compares them byte for byte; a changed vector is a
reviewed commit. Other runtimes decode them and are held to the same answers.

A document with one slot, `[value]`, whose slot is a C# `int`, `long`, `float` or `double` (leaf `int32`, `int64`,
`float32`, `float64`).

- `reads.msgpack`: an array of `[leaf, payload, accepted, bits]`. `payload` (bin) is the one-slot record fed to the
  reference; `accepted` is whether it read; `bits` (bin, 8 bytes, big-endian) is the int64 it read, or the IEEE bits of
  the double it read (a float32 widened), and empty when refused. Every value is fed to every leaf.
- `writes.msgpack`: an array of `[leaf, text, bytes]`. `text` is a decimal for an integer leaf and the double's IEEE
  bits in hex for a float leaf; `bytes` (bin) is the one-slot record the reference writes for that value.
- `float32-nan.msgpack`: an array of `[payload, bytes]`. `payload` (bin) is a one-slot `float` record holding an infinity
  or a NaN; `bytes` (bin) is what the reference writes after reading it. It keeps every bit, a signalling NaN's included.
