# slot-nil vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/SlotNilVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1`. Every other run reads this file and compares it byte for byte; a changed vector is a
reviewed commit. Other runtimes decode it and are held to the same answers.

A record of five slots: `S` string, `I` int, `B` byte[], `R` a record of one string, `L` List of string.

- `reads.msgpack`: an array of `[name, payload, accepted, rewritten]`. `payload` (bin) is the record fed to the
  reference; `accepted` is whether it read; `rewritten` (bin) is what the reference writes after reading it, and empty
  when refused. It covers a nil in each kind of slot, a MessagePack str in the byte[] slot, a four-byte UTF-8 character
  (U+1F600) as the string slot and as a str in the byte[] slot, and records short of their last slots.
