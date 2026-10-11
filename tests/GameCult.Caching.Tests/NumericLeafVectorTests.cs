#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
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

        [MessagePackObject] public sealed class Int32Slot { [Key(0)] public int V; }
        [MessagePackObject] public sealed class Int64Slot { [Key(0)] public long V; }
        [MessagePackObject] public sealed class Float32Slot { [Key(0)] public float V; }
        [MessagePackObject] public sealed class Float64Slot { [Key(0)] public double V; }

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
            // integers beyond 2^53 that a float32 slot must round once: past a tie, on a tie to even, on a tie to odd, negative
            "d31000001000000001", "cf8000008000000001", "cf8000008000000000", "cf8000018000000000", "d3efffffe800000001", "d3effffff000000001",
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

        // ---- tests ----

        [Test]
        public void ReadsVectorMatches() => Check("reads.msgpack", ReadsVector());

        [Test]
        public void WritesVectorMatches() => Check("writes.msgpack", WritesVector());

        [Test]
        public void ReadsCoverEveryLeafAndBothOutcomes()
        {
            var rows = Leaves.SelectMany(leaf => ReadValues.Select(value => (leaf, value, result: Read(leaf, Payload(value))))).ToList();
            Assert.That(rows, Has.Count.EqualTo(Leaves.Length * ReadValues.Length));
            foreach (var leaf in Leaves)
            {
                Assert.That(rows.Count(row => row.leaf == leaf && row.result.Accepted), Is.GreaterThan(10), $"{leaf} accepts");
                Assert.That(rows.Count(row => row.leaf == leaf && !row.result.Accepted), Is.GreaterThan(5), $"{leaf} refuses");
            }
        }

        // One rounding from int64 or uint64 to float, as the reference does: 2^60 + 2^36 + 1 is past the tie, so it rounds up.
        [TestCase("float32", "d31000001000000001", "43b0000020000000")]
        [TestCase("float32", "cf8000008000000001", "43e0000020000000")]
        [TestCase("float32", "cf8000008000000000", "43e0000000000000")]
        [TestCase("float32", "cf8000018000000000", "43e0000040000000")]
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
