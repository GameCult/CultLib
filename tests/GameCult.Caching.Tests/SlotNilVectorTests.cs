#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The slot golden in tests/vectors/slot-nil: what the reference reads and rewrites for a nil or a short record in each
    // kind of slot. The C# reference is the only writer (CULTCACHE_WRITE_VECTORS=1); every other run reads the committed
    // bytes and compares them byte for byte. Other runtimes are held to these files.
    public class SlotNilVectorTests
    {
        private const string WriteVariable = "CULTCACHE_WRITE_VECTORS";

        [MessagePackObject] public sealed class Inner { [Key(0)] public string X = string.Empty; }

        // One slot of each kind: a string, a value type, a byte[], a record and a list.
        [MessagePackObject]
        public sealed class Probe
        {
            [Key(0)] public string S = string.Empty;
            [Key(1)] public int I;
            [Key(2)] public byte[] B = Array.Empty<byte>();
            [Key(3)] public Inner? R;
            [Key(4)] public List<string> L = new();
        }

        private static bool Writing => Environment.GetEnvironmentVariable(WriteVariable) == "1";

        private static readonly MessagePackSerializerOptions Options = CultDocumentMessagePackSerialization.OptionsFor(typeof(SlotNilVectorTests).Assembly);

        // [name, payload]: the whole case table, in file order.
        private static readonly (string Name, string Hex)[] Cases =
        {
            ("full", "95a16107c402616291a17890"),
            ("nil-string", "95c007c4026162c090"),
            ("nil-bytes", "95a16107c0c090"),
            ("nil-record", "95a16107c4026162c090"),
            ("nil-list", "95a16107c4026162c0c0"),
            ("nil-int", "95a161c0c4026162c090"),
            ("bytes-str", "95a16107a3616263c090"),
            ("bytes-bin", "95a16107c403616263c090"),
            ("short-0", "90"),
            ("short-1", "91a161"),
            ("short-2", "92a16107"),
            ("short-3", "93a16107c4026162"),
            ("short-3-nil-string", "93c007c4026162"),
            ("short-3-nil-int", "93a161c0c4026162"),
        };

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = Path.Combine(dir.FullName, "tests", "vectors");
                if (Directory.Exists(vectors)) return Path.Combine(vectors, "slot-nil", name);
            }

            throw new DirectoryNotFoundException($"tests/vectors not found above {TestContext.CurrentContext.TestDirectory}.");
        }

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

        private static (bool Accepted, byte[] Rewritten) Read(byte[] payload)
        {
            try
            {
                return (true, MessagePackSerializer.Serialize(MessagePackSerializer.Deserialize<Probe>(payload, Options), Options));
            }
            catch (MessagePackSerializationException)
            {
                return (false, Array.Empty<byte>());
            }
        }

        // reads.msgpack: an array of [name, payload (bin), accepted, rewritten (bin: what the reference writes after reading it; empty when refused)].
        private static byte[] ReadsVector()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(Cases.Length);
            foreach (var (name, hex) in Cases)
            {
                var payload = Convert.FromHexString(hex);
                var (accepted, rewritten) = Read(payload);
                writer.WriteArrayHeader(4);
                writer.Write(name);
                writer.Write(payload);
                writer.Write(accepted);
                writer.Write(rewritten);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        [Test]
        public void ReadsVectorMatches() => Check("reads.msgpack", ReadsVector());

        [Test]
        public void NilIsAcceptedInAReferenceSlotAndRefusedInAValueSlot()
        {
            Assert.That(Read(Convert.FromHexString("95c007c4026162c090")).Accepted, Is.True);
            Assert.That(Read(Convert.FromHexString("95a161c0c4026162c090")).Accepted, Is.False);
        }
    }
}
