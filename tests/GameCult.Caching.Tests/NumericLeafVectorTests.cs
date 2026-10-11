#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The numeric-leaf golden in tests/vectors/numeric-leaves: what the reference reads and refuses for an int, long, float
    // and double slot, and the bytes it writes for them. The C# reference is the only writer (CULTCACHE_WRITE_VECTORS=1);
    // every other run reads the committed bytes and compares them byte for byte. Other runtimes are held to these files.
    public class NumericLeafVectorTests
    {
        private const string WriteVariable = "CULTCACHE_WRITE_VECTORS";
        private static readonly string[] Leaves = { "int32", "int64", "float32", "float64" };
        private static readonly string[] WideLeaves = { "string", "boolean", "bytes", "any", "list-string", "array-string", "nullable-int32", "list-int32", "record", "union" };

        [MessagePackObject] public sealed class Int32Slot { [Key(0)] public int V; }
        [MessagePackObject] public sealed class Int64Slot { [Key(0)] public long V; }
        [MessagePackObject] public sealed class Float32Slot { [Key(0)] public float V; }
        [MessagePackObject] public sealed class Float64Slot { [Key(0)] public double V; }

        // The wider leaves: one-slot records [value] for each kind of slot a document can declare.
        [MessagePackObject] public sealed class StringSlot { [Key(0)] public string? V; }
        [MessagePackObject] public sealed class BoolSlot { [Key(0)] public bool V; }
        [MessagePackObject] public sealed class BytesSlot { [Key(0)] public byte[]? V; }
        [MessagePackObject] public sealed class AnySlot { [Key(0)] public object? V; }
        [MessagePackObject] public sealed class ListStringSlot { [Key(0)] public List<string>? V; }
        [MessagePackObject] public sealed class ArrayStringSlot { [Key(0)] public string[]? V; }
        [MessagePackObject] public sealed class NullableInt32Slot { [Key(0)] public int? V; }
        [MessagePackObject] public sealed class ListInt32Slot { [Key(0)] public List<int>? V; }
        [MessagePackObject] public sealed class LeafInner { [Key(0)] public string N = string.Empty; [Key(1)] public float W; }
        [MessagePackObject] public sealed class RecordSlot { [Key(0)] public LeafInner? V; }
        [Union(0, typeof(LeafCircle))] public interface ILeafShape { }
        [MessagePackObject] public sealed class LeafCircle : ILeafShape { [Key(0)] public float R; }
        [MessagePackObject] public sealed class UnionSlot { [Key(0)] public ILeafShape? V; }

        // One member of every leaf kind: the record whose catalog rows and payload leaf-record.msgpack pins.
        [CultDocument("cultcache.leaf-record", "cultcache.leaf_record.v1")]
        [MessagePackObject]
        public sealed class LeafRecord
        {
            [Key(0)] [CultName] public string Id = string.Empty;
            [Key(1)] public bool Flag;
            [Key(2)] public byte[] Blob = Array.Empty<byte>();
            [Key(3)] public int I;
            [Key(4)] public long L;
            [Key(5)] public float F;
            [Key(6)] public double D;
            [Key(7)] public int? MaybeI;
            [Key(8)] public List<string> Tags = new();
            [Key(9)] public string[] Names = Array.Empty<string>();
            [Key(10)] public object? Any;
            [Key(11)] public LeafInner? Nested;
            [Key(12)] public List<LeafInner> Items = new();
            [Key(13)] public ILeafShape? Shape;
            [Key(14)] public float[] Floats = Array.Empty<float>();
            [Key(15)] public float? MaybeF;
        }

        // A record of every leaf kind, the shape a replicated document has.
        [MessagePackObject]
        public sealed class Mixed
        {
            [Key(0)] public string Id = string.Empty;
            [Key(1)] public int I;
            [Key(2)] public long L;
            [Key(3)] public float F;
            [Key(4)] public double D;
        }

        private static bool Writing => Environment.GetEnvironmentVariable(WriteVariable) == "1";

        private static readonly MessagePackSerializerOptions Options = CultDocumentMessagePackSerialization.OptionsFor(typeof(NumericLeafVectorTests).Assembly);

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = Path.Combine(dir.FullName, "tests", "vectors");
                if (Directory.Exists(vectors)) return Path.Combine(vectors, "numeric-leaves", name);
            }

            throw new DirectoryNotFoundException($"tests/vectors not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        // Under the write variable the reference's bytes become the file; otherwise they must equal the committed file.
        private static void Check(string name, byte[] reference)
        {
            var path = VectorPath(name);
            if (Writing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, reference);
                return;
            }

            Assert.That(File.Exists(path), Is.True, $"vector {name} is committed");
            Assert.That(Convert.ToHexString(reference), Is.EqualTo(Convert.ToHexString(File.ReadAllBytes(path))), $"vector {name}");
        }

        // ---- reads: a one-slot record [value], every value fed to every leaf ----

        // The value token of each read case, as MessagePack hex: integers of every width and edge, floats, and kinds no leaf reads.
        private static readonly string[] ReadValues =
        {
            "05", "ff", "ccc8", "cd0100", "ceb2d05e00", "d0ce", "d18000", "d280000000", "d38000000000000000",
            "cf0020000000000001", "cfffffffffffffffff", "cf8000000000000000", "d30020000000000001", "d3fffffffffffffff0",
            // integers beyond 2^53 that a float32 slot must round once: past a tie, on a tie to even, on a tie to odd, an odd value just past exact, negative
            "d31000001000000001", "cf8000008000000001", "cf8000008000000000", "cf8000018000000000", "cf8000010000000001", "cf8000010000000000", "d3efffffe800000001", "d3effffff000000001",
            // an integer a float32 cannot hold that a reader holds as a plain number: 2^24 + 1 rounds to 2^24
            "ce01000001",
            "ca3fc00000", "cb3ff8000000000000", "cb401c000000000000", "cb7ff8000000000000", "ca7f800000", "ca7f800001", "cb7e37e43c8800759c",
            "cb47efffffe0000000", "cb3fb999999999999a", "ca3dcccccd", "cb8000000000000000",
            "c0", "c3", "a161", "c40100", "90", "80",
        };

        private static (bool Accepted, byte[] Bits) Read(string leaf, byte[] payload)
        {
            try
            {
                var bits = new byte[8];
                switch (leaf)
                {
                    case "int32": BinaryPrimitives.WriteInt64BigEndian(bits, MessagePackSerializer.Deserialize<Int32Slot>(payload, Options).V); break;
                    case "int64": BinaryPrimitives.WriteInt64BigEndian(bits, MessagePackSerializer.Deserialize<Int64Slot>(payload, Options).V); break;
                    case "float32": BinaryPrimitives.WriteInt64BigEndian(bits, BitConverter.DoubleToInt64Bits(MessagePackSerializer.Deserialize<Float32Slot>(payload, Options).V)); break;
                    default: BinaryPrimitives.WriteInt64BigEndian(bits, BitConverter.DoubleToInt64Bits(MessagePackSerializer.Deserialize<Float64Slot>(payload, Options).V)); break;
                }

                return (true, bits);
            }
            catch (Exception)
            {
                return (false, Array.Empty<byte>());
            }
        }

        private static byte[] Payload(string valueHex) => Convert.FromHexString("91" + valueHex);

        // reads.msgpack: an array of [leaf, payload (bin), accepted, bits (bin of 8: the int64 or the double's IEEE bits, big-endian; empty when refused)].
        private static byte[] ReadsVector()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(Leaves.Length * ReadValues.Length);
            foreach (var leaf in Leaves)
                foreach (var value in ReadValues)
                {
                    var payload = Payload(value);
                    var (accepted, bits) = Read(leaf, payload);
                    writer.WriteArrayHeader(4);
                    writer.Write(leaf);
                    writer.Write(payload);
                    writer.Write(accepted);
                    writer.Write(bits);
                }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        // ---- writes: the reference's own bytes for a value ----

        // text is a decimal for an integer leaf and the double's IEEE bits (hex) for a float leaf.
        private static readonly (string Leaf, string Text)[] WriteValues =
        {
            ("int32", "0"), ("int32", "1"), ("int32", "127"), ("int32", "128"), ("int32", "255"), ("int32", "256"), ("int32", "65535"), ("int32", "65536"),
            ("int32", "-1"), ("int32", "-32"), ("int32", "-33"), ("int32", "-128"), ("int32", "-129"), ("int32", "-32768"), ("int32", "-32769"),
            ("int32", "2147483647"), ("int32", "-2147483648"),
            ("int64", "0"), ("int64", "127"), ("int64", "128"), ("int64", "4294967295"), ("int64", "4294967296"), ("int64", "9007199254740991"),
            ("int64", "-9007199254740991"), ("int64", "-2147483649"), ("int64", "9007199254740994"), ("int64", "9223372036854775807"),
            ("int64", "-9223372036854775808"), ("int64", "9007199254740993"), ("int64", "-9007199254740993"),
            ("float32", "3ff8000000000000"), ("float32", "3fb999999999999a"), ("float32", "0000000000000000"), ("float32", "8000000000000000"),
            ("float32", "4008000000000000"), ("float32", "46293e5939a08cea"), ("float32", "7ff0000000000000"), ("float32", "7ff8000000000000"),
            ("float64", "3ff8000000000000"), ("float64", "3fb999999999999a"), ("float64", "0000000000000000"), ("float64", "8000000000000000"),
            ("float64", "4008000000000000"), ("float64", "7e37e43c8800759c"), ("float64", "7ff0000000000000"), ("float64", "7ff8000000000000"),
        };

        private static byte[] Write(string leaf, string text)
        {
            double Double() => BitConverter.Int64BitsToDouble((long)ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return leaf switch
            {
                "int32" => MessagePackSerializer.Serialize(new Int32Slot { V = int.Parse(text, CultureInfo.InvariantCulture) }, Options),
                "int64" => MessagePackSerializer.Serialize(new Int64Slot { V = long.Parse(text, CultureInfo.InvariantCulture) }, Options),
                "float32" => MessagePackSerializer.Serialize(new Float32Slot { V = (float)Double() }, Options),
                _ => MessagePackSerializer.Serialize(new Float64Slot { V = Double() }, Options),
            };
        }

        // writes.msgpack: an array of [leaf, text, bytes (bin): the one-slot record the reference writes for the value].
        private static byte[] WritesVector()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(WriteValues.Length);
            foreach (var (leaf, text) in WriteValues)
            {
                writer.WriteArrayHeader(3);
                writer.Write(leaf);
                writer.Write(text);
                writer.Write(Write(leaf, text));
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        // mixed-record.msgpack: the reference's bytes for Mixed { "n1", 7, 8, 1.5f, 2.5 }, which other runtimes must write identically.
        private static byte[] MixedRecord() => MessagePackSerializer.Serialize(new Mixed { Id = "n1", I = 7, L = 8, F = 1.5f, D = 2.5 }, Options);

        // float32-nan.msgpack: an array of [payload (bin), bytes (bin)]: a one-slot float32 record holding an infinity or a NaN,
        // and the bytes the reference writes after reading it. A signalling NaN (quiet bit clear) comes back unchanged.
        private static readonly string[] Float32NanValues = { "ca7f800000", "ca7f800001", "ca7fa00000", "ca7fc00001", "ca7fe00000", "caff800001" };

        private static byte[] Float32NanVector()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(Float32NanValues.Length);
            foreach (var value in Float32NanValues)
            {
                var payload = Payload(value);
                writer.WriteArrayHeader(2);
                writer.Write(payload);
                writer.Write(MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Float32Slot>(payload, Options), Options));
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        // ---- the wider leaves: reads and one record of every leaf ----

        // The value token of each wide read case, as MessagePack hex. c1 is a code no reader assigns, so even the any leaf refuses one row.
        private static readonly string[] WideValues =
        {
            "05", "ff", "cb3ff8000000000000", "ca3fc00000", "c0", "c2", "c3", "a161", "d90161", "c40100", "90", "9101", "91a161", "80", "81a161a162",
            "920091ca3f800000", "9291a161ca3f800000", "9192a161ca3f800000", "c1",
        };

        // What the reference reads from a one-slot record of the leaf, and the bytes it writes back for what it read.
        private static (bool Accepted, byte[] Rewrite) ReadWide(string leaf, byte[] payload) => leaf switch
        {
            "string" => Reread<StringSlot>(payload),
            "boolean" => Reread<BoolSlot>(payload),
            "bytes" => Reread<BytesSlot>(payload),
            "any" => Reread<AnySlot>(payload),
            "list-string" => Reread<ListStringSlot>(payload),
            "array-string" => Reread<ArrayStringSlot>(payload),
            "nullable-int32" => Reread<NullableInt32Slot>(payload),
            "list-int32" => Reread<ListInt32Slot>(payload),
            "record" => Reread<RecordSlot>(payload),
            _ => Reread<UnionSlot>(payload),
        };

        private static (bool Accepted, byte[] Rewrite) Reread<T>(byte[] payload)
        {
            T value;
            try
            {
                value = MessagePackSerializer.Deserialize<T>(payload, Options);
            }
            catch (Exception)
            {
                return (false, Array.Empty<byte>());
            }

            return (true, MessagePackSerializer.Serialize(value, Options));
        }

        // leaves-reads.msgpack: an array of [leaf, payload (bin), accepted, rewrite (bin: the reference's bytes for what it read, empty when refused)].
        private static byte[] LeavesReadsVector()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(WideLeaves.Length * WideValues.Length);
            foreach (var leaf in WideLeaves)
                foreach (var value in WideValues)
                {
                    var payload = Payload(value);
                    var (accepted, rewrite) = ReadWide(leaf, payload);
                    writer.WriteArrayHeader(4);
                    writer.Write(leaf);
                    writer.Write(payload);
                    writer.Write(accepted);
                    writer.Write(rewrite);
                }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        // Every float32 slot holds 0.1f, which float32 holds only as its own rounding, and the double slot 0.1, which float32 cannot hold; 1.5f is exact.
        private static LeafRecord Everything() => new()
        {
            Id = "leaves", Flag = true, Blob = new byte[] { 1, 2, 3 }, I = -7, L = 5_000_000_000, F = 0.1f, D = 0.1, MaybeI = 9,
            Tags = new List<string> { "a", "b" }, Names = new[] { "x", "y" }, Any = "text",
            Nested = new LeafInner { N = "inner", W = 0.1f }, Items = new List<LeafInner> { new() { N = "one", W = 0.1f }, new() { N = "two", W = 1.5f } },
            Shape = new LeafCircle { R = 0.1f }, Floats = new[] { 0.1f, 1.5f }, MaybeF = 0.1f,
        };

        // leaf-record.msgpack: [members, payload]. members are the catalog rows the store writes for LeafRecord, each
        // [slot, memberName, typeName, isReference, isMany, targetSchemaName, isName, indexAlias]; payload (bin) is the record's bytes in that store.
        private static async Task<byte[]> LeafRecordVector()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultlib-leaf-record-{Guid.NewGuid():N}.cc");
            try
            {
                var store = new SingleFileMessagePackBackingStore(path);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                await cache.AddAsync(Everything());
                store.PushAll();
                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
                var members = snapshot.SchemaCatalog.Single(entry => entry.SchemaName == "cultcache.leaf-record").Members;
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new MessagePackWriter(buffer);
                writer.WriteArrayHeader(2);
                writer.WriteArrayHeader(members.Length);
                foreach (var member in members)
                {
                    writer.WriteArrayHeader(8);
                    writer.Write(member.Slot);
                    writer.Write(member.MemberName);
                    writer.Write(member.TypeName);
                    writer.Write(member.IsReference);
                    writer.Write(member.IsMany);
                    writer.Write(member.TargetSchemaName);
                    writer.Write(member.IsName);
                    writer.Write(member.IndexAlias);
                }

                writer.Write(snapshot.Records.Single().Payload);
                writer.Flush();
                return buffer.WrittenSpan.ToArray();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- tests ----

        [Test]
        public void ReadsVectorMatches() => Check("reads.msgpack", ReadsVector());

        [Test]
        public void Float32NanVectorMatches() => Check("float32-nan.msgpack", Float32NanVector());

        [Test]
        public void Float32SignallingNanIsReadAndWrittenBitForBit()
        {
            foreach (var value in new[] { "ca7f800001", "ca7fa00000", "caff800001" })
            {
                var payload = Payload(value);
                var again = MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Float32Slot>(payload, Options), Options);
                Assert.That(Convert.ToHexString(again), Is.EqualTo(Convert.ToHexString(payload)), value);
            }
        }

        [Test]
        public void WritesVectorMatches() => Check("writes.msgpack", WritesVector());

        [Test]
        public void MixedRecordVectorMatches()
        {
            var written = MixedRecord();
            Assert.That(Convert.ToHexString(written), Is.EqualTo("95A26E310708CA3FC00000CB4004000000000000"));
            Check("mixed-record.msgpack", written);
            var read = MessagePackSerializer.Deserialize<Mixed>(written, Options);
            Assert.That((read.Id, read.I, read.L, read.F, read.D), Is.EqualTo(("n1", 7, 8L, 1.5f, 2.5)));
        }

        [Test]
        public void LeavesReadsVectorMatches() => Check("leaves-reads.msgpack", LeavesReadsVector());

        [Test]
        public async Task LeafRecordVectorMatches()
        {
            var written = await LeafRecordVector();
            Check("leaf-record.msgpack", written);
            var reader = new MessagePackReader(written);
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(16), "one member per leaf");
        }

        [Test]
        public void LeavesReadCoverEveryLeafAndBothOutcomes()
        {
            var rows = WideLeaves.SelectMany(leaf => WideValues.Select(value => (leaf, result: ReadWide(leaf, Payload(value))))).ToList();
            foreach (var leaf in WideLeaves)
            {
                Assert.That(rows.Count(row => row.leaf == leaf && row.result.Accepted), Is.GreaterThan(0), $"{leaf} accepts");
                Assert.That(rows.Count(row => row.leaf == leaf && !row.result.Accepted), Is.GreaterThan(0), $"{leaf} refuses");
            }
        }

        [Test]
        public void ReadsCoverEveryLeafAndBothOutcomes()
        {
            var rows = Leaves.SelectMany(leaf => ReadValues.Select(value => (leaf, value, result: Read(leaf, Payload(value))))).ToList();
            Assert.That(rows, Has.Count.EqualTo(Leaves.Length * ReadValues.Length));
            foreach (var leaf in Leaves)
            {
                Assert.That(rows.Count(row => row.leaf == leaf && row.result.Accepted), Is.GreaterThan(5), $"{leaf} accepts");
                Assert.That(rows.Count(row => row.leaf == leaf && !row.result.Accepted), Is.GreaterThan(5), $"{leaf} refuses");
            }
        }

        // One rounding from int64 or uint64 to float, as the reference does: 2^60 + 2^36 + 1 is past the tie, so it rounds up.
        [TestCase("float32", "d31000001000000001", "43b0000020000000")]
        [TestCase("float32", "cf8000008000000001", "43e0000020000000")]
        [TestCase("float32", "cf8000008000000000", "43e0000000000000")]
        [TestCase("float32", "cf8000018000000000", "43e0000040000000")]
        [TestCase("float32", "cf8000010000000001", "43e0000020000000")]
        [TestCase("float32", "cf8000010000000000", "43e0000020000000")]
        [TestCase("float64", "cf0020000000000001", "4340000000000000")]
        [TestCase("int64", "cf0020000000000001", "0020000000000001")]
        public void ReadsRoundExactlyAsTheReferenceDoes(string leaf, string value, string bitsHex)
        {
            var (accepted, bits) = Read(leaf, Payload(value));
            Assert.That(accepted, Is.True);
            Assert.That(Convert.ToHexString(bits).ToLowerInvariant(), Is.EqualTo(bitsHex));
        }

        [Test]
        public void ValuesOutsideTheLeafAreRefused()
        {
            Assert.That(Read("int32", Payload("ceb2d05e00")).Accepted, Is.False);
            Assert.That(Read("int64", Payload("cfffffffffffffffff")).Accepted, Is.False);
            Assert.That(Read("int32", Payload("ca3fc00000")).Accepted, Is.False);
        }

        [Test]
        public void EveryWrittenValueIsReadBackAndWrittenAgainUnchanged()
        {
            foreach (var (leaf, text) in WriteValues)
            {
                var written = Write(leaf, text);
                Assert.That(Read(leaf, written).Accepted, Is.True, $"{leaf} {text}");
                var again = leaf switch
                {
                    "int32" => MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Int32Slot>(written, Options), Options),
                    "int64" => MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Int64Slot>(written, Options), Options),
                    "float32" => MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Float32Slot>(written, Options), Options),
                    _ => MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Float64Slot>(written, Options), Options),
                };
                Assert.That(Convert.ToHexString(again), Is.EqualTo(Convert.ToHexString(written)), $"{leaf} {text}");
            }
        }
    }
}
