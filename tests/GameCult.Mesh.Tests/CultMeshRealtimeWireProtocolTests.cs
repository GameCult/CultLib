using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Mesh.Tests
{
    /// <summary>
    /// The realtime frame codec, rule by rule, and the C# half of the
    /// cross-runtime byte parity. <c>contracts/cultmesh/realtime-frame-vectors.json</c>
    /// is written here under CULTMESH_WRITE_VECTORS=1 and decoded by
    /// <c>packages/cultmesh-ts/test/realtime-wire.test.ts</c>;
    /// <c>realtime-frame-vectors.ts-written.json</c> is written by
    /// <c>scripts/write-cultmesh-realtime-frame-vectors.mjs</c> with the
    /// TypeScript codec and judged here by the reference only. Both files carry
    /// the same field shape: an identity is a string or a repeat rule, a payload
    /// is base64 or a ramp rule, so one reader on each side serves both.
    /// </summary>
    [TestFixture]
    public class CultMeshRealtimeWireProtocolTests
    {
        // The three identities that sit exactly on the u16 length prefix. Each
        // repeat unit is five UTF-8 bytes over two characters, so 13,107 repeats
        // are 65,535 bytes and 26,214 characters: byte length and character
        // length differ, which is the point of the case. The vector records the
        // rule and the digest of the whole encoding instead of 200 KB of literal
        // identity text.
        private const string WideChannelUnit = "\u00e4\u20ac";
        private const string WideSchemaUnit = "\u20ac\u00e4";
        private const string WideBodyUnit = "\u00f1\u20ac";
        private const int WideIdentityBytes = 65535;

        [Test]
        public void RealtimeFrameVectorsAreSharedWithTypeScript()
        {
            var path = VectorPath("realtime-frame-vectors.json");
            if (Environment.GetEnvironmentVariable("CULTMESH_WRITE_VECTORS") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    frames = Frames().Select(Recorded).ToArray(),
                    malformed = Malformed().Select(entry => new
                    {
                        label = entry.Label,
                        encoding = Convert.ToBase64String(entry.Bytes),
                        message = Refusal(entry.Bytes)
                    }).ToArray()
                }, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }) + "\n");
            }

            using var vectors = JsonDocument.Parse(File.ReadAllText(path));
            var root = vectors.RootElement;
            var expected = Frames().ToDictionary(entry => entry.Label);
            root.GetProperty("frames").GetArrayLength().Should().Be(expected.Count);
            foreach (var element in root.GetProperty("frames").EnumerateArray())
            {
                var label = element.GetProperty("label").GetString()!;
                expected.Should().ContainKey(label);
                var frame = expected[label].Frame;
                var encoded = CultMeshRealtimeWireProtocol.EncodeFrame(frame);
                element.GetProperty("encodedLength").GetInt32().Should().Be(encoded.Length, label);
                element.GetProperty("sha256OfEncoding").GetString().Should().Be(Sha256(encoded), label);
                if (element.TryGetProperty("encoding", out var encoding) && encoding.ValueKind == JsonValueKind.String)
                    Convert.FromBase64String(encoding.GetString()!).Should().Equal(encoded, label);

                // The recorded fields rebuild the frame, and it round-trips.
                Same(Read(element.GetProperty("fields")), frame, label);
                var decoded = CultMeshRealtimeWireProtocol.DecodeFrame(encoded);
                Same(decoded, frame, label);
                CultMeshRealtimeWireProtocol.EncodeFrame(decoded).Should().Equal(encoded, label);
            }

            var malformed = Malformed().ToDictionary(entry => entry.Label);
            root.GetProperty("malformed").GetArrayLength().Should().Be(malformed.Count);
            foreach (var element in root.GetProperty("malformed").EnumerateArray())
            {
                var label = element.GetProperty("label").GetString()!;
                malformed.Should().ContainKey(label);
                var bytes = Convert.FromBase64String(element.GetProperty("encoding").GetString()!);
                bytes.Should().Equal(malformed[label].Bytes, label);
                Refusal(bytes).Should().Be(element.GetProperty("message").GetString(), label);
            }
        }

        // The other direction, cross-process: the TypeScript codec wrote these
        // bytes and only the reference implementation judges them here.
        [Test]
        public void RealtimeFrameVectorsWrittenByTypeScriptDecodeHere()
        {
            var path = VectorPath("realtime-frame-vectors.ts-written.json");
            using var vectors = JsonDocument.Parse(File.ReadAllText(path));
            var frames = vectors.RootElement.GetProperty("frames");
            frames.GetArrayLength().Should().BeGreaterThan(0);
            foreach (var element in frames.EnumerateArray())
            {
                var label = element.GetProperty("label").GetString()!;
                var frame = Read(element.GetProperty("fields"));
                var mine = CultMeshRealtimeWireProtocol.EncodeFrame(frame);

                // What TypeScript wrote is what this side writes, byte for byte.
                element.GetProperty("encodedLength").GetInt32().Should().Be(mine.Length, label);
                element.GetProperty("sha256OfEncoding").GetString().Should().Be(Sha256(mine), label);
                var theirs = element.TryGetProperty("encoding", out var encoding) && encoding.ValueKind == JsonValueKind.String
                    ? Convert.FromBase64String(encoding.GetString()!)
                    : mine;
                theirs.Should().Equal(mine, label);

                // And it decodes here to the fields TypeScript claims it encoded.
                Same(CultMeshRealtimeWireProtocol.DecodeFrame(theirs), frame, label);
            }

            foreach (var element in vectors.RootElement.GetProperty("malformed").EnumerateArray())
            {
                var label = element.GetProperty("label").GetString()!;
                var bytes = Convert.FromBase64String(element.GetProperty("encoding").GetString()!);
                Refusal(bytes).Should().Be(element.GetProperty("message").GetString(), label);
            }
        }

        [Test]
        public void EncodeRefusesWhatTheTransportContractForbids()
        {
            Spoiled(frame => frame.ChannelId = " ").Should().Be("Realtime channel identity is required.");
            Spoiled(frame => frame.SchemaId = "\t").Should().Be("Realtime schema identity is required.");
            Spoiled(frame => frame.BodyId = "").Should().Be("Realtime body identity is required.");
            Spoiled(frame => frame.ProducerEpoch = -1).Should().Be("Realtime epoch and sequence must be non-negative.");
            Spoiled(frame => frame.Sequence = -1).Should().Be("Realtime epoch and sequence must be non-negative.");
            Spoiled(frame => frame.ChannelId = new string('a', ushort.MaxValue + 1))
                .Should().Be("Realtime frame identity exceeds the QUIC wire limit.");
            Spoiled(frame => frame.Payload = new byte[CultMeshRealtimeWireProtocol.MaximumFrameBytes + 1])
                .Should().Be("Realtime frame payload exceeds the QUIC wire limit.");
        }

        [Test]
        public void TheWireConstantsAreTheOnesTypeScriptMirrors()
        {
            CultMeshRealtimeWireProtocol.ApplicationProtocolName.Should().Be("cultmesh-state-v1");
            CultMeshRealtimeWireProtocol.ConnectionCloseCode.Should().Be(0x43554c54);
            CultMeshRealtimeWireProtocol.StreamAbortCode.Should().Be(0x53544154);
            CultMeshRealtimeWireProtocol.ReliableStream.Should().Be(1);
            CultMeshRealtimeWireProtocol.LatestOnlyStream.Should().Be(2);
            CultMeshRealtimeWireProtocol.MaximumFrameBytes.Should().Be(64 * 1024 * 1024);
            CultMeshRealtimeWireProtocol.MaximumEncodedFrameBytes.Should().Be(
                CultMeshRealtimeWireProtocol.MaximumFrameBytes + 37 + 3 * ushort.MaxValue);
            ((int)CultMeshRealtimeDelivery.ReliableOrdered).Should().Be(0);
            ((int)CultMeshRealtimeDelivery.LatestOnly).Should().Be(1);
            ((int)CultMeshRealtimeDelivery.Unreliable).Should().Be(2);
        }

        private static IEnumerable<(string Label, CultMeshRealtimeFrame Frame, bool Wide)> Frames()
        {
            yield return ("reliable-ordered, small payload", Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", 1, 2,
                CultMeshRealtimeDelivery.ReliableOrdered, new byte[] { 0x00, 0x7f, 0x80, 0xff, 0x2a }), false);
            yield return ("latest-only, small payload", Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", 3, 4,
                CultMeshRealtimeDelivery.LatestOnly, new byte[] { 0x01, 0x02, 0x03 }), false);
            yield return ("unreliable, small payload", Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", 5, 6,
                CultMeshRealtimeDelivery.Unreliable, new byte[] { 0xfe }), false);
            yield return ("empty payload", Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", 0, 0,
                CultMeshRealtimeDelivery.ReliableOrdered, Array.Empty<byte>()), false);
            yield return ("multi-byte identities", Frame(
                "aetheria.z\u00f6ne-\u221e", "cultmesh.realtime.\u20ac1", "b\u00f8dy-\ud83d\udd25", 11, 12,
                CultMeshRealtimeDelivery.LatestOnly, Encoding.UTF8.GetBytes("payload-\u221e")), false);
            yield return ("epoch and sequence at long.MaxValue", Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", long.MaxValue, long.MaxValue,
                CultMeshRealtimeDelivery.Unreliable, new byte[] { 0x5a }), false);
            yield return ("identities at exactly 65535 UTF-8 bytes", Frame(
                Repeat(WideChannelUnit, WideIdentityBytes), Repeat(WideSchemaUnit, WideIdentityBytes),
                Repeat(WideBodyUnit, WideIdentityBytes), 13, 14,
                CultMeshRealtimeDelivery.ReliableOrdered, new byte[] { 0x11, 0x22 }), true);
            yield return ("64 MiB payload", Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", 15, 16,
                CultMeshRealtimeDelivery.ReliableOrdered, Ramp(CultMeshRealtimeWireProtocol.MaximumFrameBytes)), false);
        }

        private static IEnumerable<(string Label, byte[] Bytes)> Malformed()
        {
            var good = CultMeshRealtimeWireProtocol.EncodeFrame(Frame(
                "aetheria.zone-1", "cultmesh.realtime.v1", "body-7", 1, 2,
                CultMeshRealtimeDelivery.ReliableOrdered, new byte[] { 9, 9, 9 }));
            yield return ("truncated header", good.Take(36).ToArray());
            yield return ("wrong magic", Spoil(good, bytes => bytes[0] ^= 0xff));
            yield return ("wire version 2", Spoil(good, bytes => bytes[35] = 2));
            yield return ("header size 36", Spoil(good, bytes => bytes[31] = 36));
            yield return ("delivery 3", Spoil(good, bytes => bytes[4] = 3));
            yield return ("delivery 255", Spoil(good, bytes => bytes[4] = 255));
            yield return ("payload length -1", Spoil(good, bytes => bytes[30] = 0x80));
            yield return ("length sum off by one", good.Concat(new byte[] { 0 }).ToArray());
        }

        private static object Recorded((string Label, CultMeshRealtimeFrame Frame, bool Wide) entry)
        {
            var encoded = CultMeshRealtimeWireProtocol.EncodeFrame(entry.Frame);
            var frame = entry.Frame;
            return new
            {
                label = entry.Label,
                fields = new
                {
                    channelId = entry.Wide ? Rule(WideChannelUnit, WideIdentityBytes) : frame.ChannelId,
                    schemaId = entry.Wide ? Rule(WideSchemaUnit, WideIdentityBytes) : frame.SchemaId,
                    bodyId = entry.Wide ? Rule(WideBodyUnit, WideIdentityBytes) : frame.BodyId,
                    producerEpoch = frame.ProducerEpoch,
                    sequence = frame.Sequence,
                    delivery = Name(frame.Delivery),
                    payload = frame.Payload.Length > 4096
                        ? (object)new { ramp = frame.Payload.Length }
                        : Convert.ToBase64String(frame.Payload.ToArray())
                },
                // The two oversized cases would put megabytes into a committed
                // contract file; both sides build them from the rules above and
                // compare the digest of the whole encoding.
                encoding = encoded.Length > 4096 ? null : Convert.ToBase64String(encoded),
                encodedLength = encoded.Length,
                sha256OfEncoding = Sha256(encoded)
            };
        }

        private static object Rule(string unit, int byteLength) => new { repeat = unit, byteLength };

        private static void Same(CultMeshRealtimeFrame actual, CultMeshRealtimeFrame expected, string label)
        {
            actual.ChannelId.Should().Be(expected.ChannelId, label);
            actual.SchemaId.Should().Be(expected.SchemaId, label);
            actual.BodyId.Should().Be(expected.BodyId, label);
            actual.ProducerEpoch.Should().Be(expected.ProducerEpoch, label);
            actual.Sequence.Should().Be(expected.Sequence, label);
            actual.Delivery.Should().Be(expected.Delivery, label);
            actual.Payload.Length.Should().Be(expected.Payload.Length, label);
            actual.Payload.Span.SequenceEqual(expected.Payload.Span).Should().BeTrue(label);
        }

        private static CultMeshRealtimeFrame Read(JsonElement fields) => new()
        {
            ChannelId = Identity(fields.GetProperty("channelId")),
            SchemaId = Identity(fields.GetProperty("schemaId")),
            BodyId = Identity(fields.GetProperty("bodyId")),
            ProducerEpoch = fields.GetProperty("producerEpoch").GetInt64(),
            Sequence = fields.GetProperty("sequence").GetInt64(),
            Delivery = Delivery(fields.GetProperty("delivery").GetString()!),
            Payload = Payload(fields.GetProperty("payload"))
        };

        private static string Identity(JsonElement element) =>
            element.ValueKind == JsonValueKind.String
                ? element.GetString()!
                : Repeat(element.GetProperty("repeat").GetString()!, element.GetProperty("byteLength").GetInt32());

        private static byte[] Payload(JsonElement element) =>
            element.ValueKind == JsonValueKind.String
                ? Convert.FromBase64String(element.GetString()!)
                : Ramp(element.GetProperty("ramp").GetInt32());

        /// <summary>The repeat unit written whole until the UTF-8 byte length is exactly <paramref name="byteLength"/>.</summary>
        private static string Repeat(string unit, int byteLength)
        {
            var unitBytes = Encoding.UTF8.GetByteCount(unit);
            if (byteLength % unitBytes != 0)
                throw new ArgumentException($"'{unit}' is {unitBytes} UTF-8 bytes and does not divide {byteLength}.");
            return string.Concat(Enumerable.Repeat(unit, byteLength / unitBytes));
        }

        /// <summary>A deterministic payload both runtimes build the same way.</summary>
        private static byte[] Ramp(int length)
        {
            var bytes = new byte[length];
            for (var index = 0; index < length; index++) bytes[index] = (byte)(index % 251);
            return bytes;
        }

        private static string Spoiled(Action<CultMeshRealtimeFrame> spoil)
        {
            var frame = Frame("c", "s", "b", 1, 2, CultMeshRealtimeDelivery.ReliableOrdered, new byte[] { 1 });
            spoil(frame);
            try
            {
                CultMeshRealtimeWireProtocol.EncodeFrame(frame);
                return "<no refusal>";
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        private static string Refusal(byte[] bytes)
        {
            try
            {
                CultMeshRealtimeWireProtocol.DecodeFrame(bytes);
                return "<no refusal>";
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        private static byte[] Spoil(byte[] bytes, Action<byte[]> spoil)
        {
            var copy = bytes.ToArray();
            spoil(copy);
            return copy;
        }

        private static CultMeshRealtimeFrame Frame(
            string channelId, string schemaId, string bodyId, long producerEpoch, long sequence,
            CultMeshRealtimeDelivery delivery, byte[] payload) =>
            new()
            {
                ChannelId = channelId,
                SchemaId = schemaId,
                BodyId = bodyId,
                ProducerEpoch = producerEpoch,
                Sequence = sequence,
                Delivery = delivery,
                Payload = payload
            };

        private static string Name(CultMeshRealtimeDelivery delivery) => delivery switch
        {
            CultMeshRealtimeDelivery.ReliableOrdered => "reliable-ordered",
            CultMeshRealtimeDelivery.LatestOnly => "latest-only",
            CultMeshRealtimeDelivery.Unreliable => "unreliable",
            _ => throw new ArgumentOutOfRangeException(nameof(delivery))
        };

        private static CultMeshRealtimeDelivery Delivery(string name) => name switch
        {
            "reliable-ordered" => CultMeshRealtimeDelivery.ReliableOrdered,
            "latest-only" => CultMeshRealtimeDelivery.LatestOnly,
            "unreliable" => CultMeshRealtimeDelivery.Unreliable,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown delivery")
        };

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static string VectorPath(string name) => Path.Combine(RepoRoot(), "contracts", "cultmesh", name);

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
        }
    }
}
