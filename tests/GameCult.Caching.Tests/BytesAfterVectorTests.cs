#nullable enable
using System;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using UnionSurface = GameCult.Caching.Tests.UnionParityVectorTests.UnionSurface;

namespace GameCult.Caching.Tests
{
    // A record's payload is exactly one MessagePack value. The vectors are union-surface.msgpack followed by a tail; the C#
    // reference is their only writer (VectorFiles, CULTCACHE_WRITE_VECTORS=1) and every other runtime is held to bytes-after-outcomes.msgpack.
    public class BytesAfterVectorTests
    {
        private const string Vector = "union-parity";
        private const string Canary = "CANARY-bytes-after-7d1e";
        private const string Clause = "bytes after its first MessagePack value";
        private const int Limit = CultMessagePackSecurity.MaximumPayloadDepth;

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(UnionSurface) });

        private static byte[] Base => VectorFiles.Committed(Vector, "union-surface.msgpack");

        private static byte[] DeepTail()
        {
            var text = System.Text.Encoding.ASCII.GetBytes(Canary);
            return Enumerable.Repeat((byte)0x91, 300).Concat(new[] { (byte)(0xa0 | text.Length) }).Concat(text).ToArray();
        }

        // file, tail
        private static (string File, byte[] Tail)[] Cases() => new (string, byte[])[]
        {
            ("bytes-after-nil.msgpack", new byte[] { 0xc0 }),
            ("bytes-after-never-used.msgpack", new byte[] { 0xc1, 0xc1 }),
            ("bytes-after-deep-tail.msgpack", DeepTail()),
            ("bytes-after-second-document.msgpack", Base),
        };

        private static byte[] Raw(Action<MessagePackWriter> write)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            write(writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        // The bounds row shape: [file, outcome, union, key|nil, detail].
        private static byte[] OutcomesFile() => Raw(w =>
        {
            var cases = Cases();
            w.WriteArrayHeader(cases.Length);
            foreach (var (file, _) in cases)
            {
                w.WriteArrayHeader(5);
                w.Write(file);
                w.Write("refused");
                w.Write("UnionSurface");
                w.WriteNil();
                w.Write("bytes-after");
            }
        });

        private static string Message(Action action)
        {
            try { action(); }
            catch (Exception exception)
            {
                var message = "";
                for (var e = exception; e != null; e = e.InnerException) message += e.Message + "\n";
                return message;
            }

            throw new AssertionException("the payload was accepted");
        }

        [Test]
        public void VectorsMatchAndRefuse()
        {
            var baseBytes = Base;
            foreach (var (file, tail) in Cases()) VectorFiles.Check(Vector, file, baseBytes.Concat(tail).ToArray());
            VectorFiles.Check(Vector, "bytes-after-outcomes.msgpack", OutcomesFile());

            // From here only the committed files speak.
            var reader = new MessagePackReader(VectorFiles.Committed(Vector, "bytes-after-outcomes.msgpack"));
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(4));
            for (var i = 0; i < 4; i++)
            {
                Assert.That(reader.ReadArrayHeader(), Is.EqualTo(5));
                var file = reader.ReadString()!;
                Assert.That(reader.ReadString(), Is.EqualTo("refused"));
                Assert.That(reader.ReadString(), Is.EqualTo("UnionSurface"));
                Assert.That(reader.TryReadNil(), Is.True);
                Assert.That(reader.ReadString(), Is.EqualTo("bytes-after"));

                var payload = VectorFiles.Committed(Vector, file);
                Assert.That(payload.Take(baseBytes.Length), Is.EqualTo(baseBytes), $"{file} begins with union-surface.msgpack");
                var count = payload.Length - baseBytes.Length;
                var message = Message(() => CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(UnionSurface), payload, Registry));
                Assert.That(message, Does.Contain(typeof(UnionSurface).FullName).And.Contain($"{count} {Clause}").And.Contain("refused on read"), file);
                Assert.That(message, Does.Not.Contain(Canary).And.Not.Contain("the limit is"), file);
            }
        }

        [Test]
        public void StoreLoadRefuses()
        {
            var work = Path.Combine(Path.GetTempPath(), $"cultlib-bytesafter-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            try
            {
                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(VectorFiles.Committed(Vector, "union-surface.cc"));
                snapshot.Records[0].Payload = VectorFiles.Committed(Vector, "bytes-after-nil.msgpack");
                var scratch = Path.Combine(work, "tail.cc");
                File.WriteAllBytes(scratch, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
                var load = Message(() => CultCacheMessagePack.Create(scratch, new CultCacheOpenOptions { Registry = Registry }).Dispose());
                Assert.That(load, Does.Contain("surface").And.Contain($"1 {Clause}"));
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }

        public sealed class TwoValueFormatter : global::MessagePack.Formatters.IMessagePackFormatter<TwoValues>
        {
            public void Serialize(ref MessagePackWriter writer, TwoValues value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(0);
                writer.Write(7);
            }

            public TwoValues Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) { reader.Skip(); return new TwoValues(); }
        }

        [CultDocument("tests.two_values", "tests.two_values.v1")]
        [MessagePackFormatter(typeof(TwoValueFormatter))]
        public sealed class TwoValues
        {
            public string Id = string.Empty;
        }

        [Test]
        public void OwnFormatterWritingTwoValuesRefusedOnWrite()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TwoValues) });
            var message = Message(() => CultDocumentMessagePackSerialization.SerializeUntyped(new TwoValues(), typeof(TwoValues), registry));
            Assert.That(message, Does.Contain(typeof(TwoValues).FullName).And.Contain($"1 {Clause}").And.Contain("refused on write"));
        }
    }
}
