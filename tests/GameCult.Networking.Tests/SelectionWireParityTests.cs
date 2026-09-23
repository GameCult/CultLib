#nullable enable
using System;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // CultNet typed selection, Cut 1 (docs/cultnet-selection-cut.md, "Self's rulings for fix batch
    // 7", R-AX). The C# half of the byte-parity vectors committed into
    // packages/cultnet-rs/tests/selection_wire_parity.rs: each constant below is the exact hex that
    // file's own Rust encoder is asserted against. Landing both sides against the same literal
    // string means either runtime's serializer drifting from the other fails here, not only in the
    // Rust suite.
    //
    // The all-absent header/edge vectors were already landed on the Rust side by R-AP; the ones
    // below are the R-AX extension - a header with `tags` populated and an edge with a `payload`
    // populated, the exact shapes where `string[]`/`Vec<String>` and `byte[]`/`serde_bytes` could
    // diverge and an all-absent value proves nothing about - plus the whole
    // `cultnet.snapshot_response_raw.v1` envelope, byte-identical between the two encoders. None
    // diverged on capture.
    public sealed class SelectionWireParityTests
    {
        private const string LeafA = "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff";
        private const string CiterId = "sha256:9d0bc9aebc3619b07aabf564f98667afde41444c1b866661d777687d443608dc";

        private const string RustHeaderWithTagsHex = "8AA8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666AA736368656D614E616D65C0AD736368656D6156657273696F6EC0B1736368656D61436F6E74656E7448617368C0A97265636F72644B6579A4612D6571A873746F7265644174B8313937302D30312D30315430303A30303A30302E3030305AAF736F7572636552756E74696D654964C0AD736F757263654167656E744964C0AA736F75726365526F6C65C0A47461677392A5616C706861A462657461";
        private const string RustEdgeAllAbsentHex = "85A466726F6D82A8736368656D614964D9477368613235363A39643062633961656263333631396230376161626635363466393836363761666465343134343463316238363636363164373737363837643434333630386463A97265636F72644B6579A763697465722D31A4726F6C65A644657369676EA2746F82A8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666A97265636F72644B6579A4612D6571AF7061796C6F6164456E636F64696E67C0A77061796C6F6164C0";
        private const string RustEdgeWithPayloadHex = "85A466726F6D82A8736368656D614964D9477368613235363A39643062633961656263333631396230376161626635363466393836363761666465343134343463316238363636363164373737363837643434333630386463A97265636F72644B6579A763697465722D31A4726F6C65A644657369676EA2746F82A8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666A97265636F72644B6579A4612D6571AF7061796C6F6164456E636F64696E67AB6D6573736167657061636BA77061796C6F6164C403010203";
        private const string RustEnvelopeAllAbsentHex = "8BAD736368656D6156657273696F6ED92063756C746E65742E736E617073686F745F726573706F6E73655F7261772E7631A96D6573736167654964A16DA76D61746368656401A461734F6601A46E657874C0A768656164657273918AA8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666AA736368656D614E616D65C0AD736368656D6156657273696F6EC0B1736368656D61436F6E74656E7448617368C0A97265636F72644B6579A4612D6571A873746F7265644174B8313937302D30312D30315430303A30303A30302E3030305AAF736F7572636552756E74696D654964C0AD736F757263654167656E744964C0AA736F75726365526F6C65C0A474616773C0A9646F63756D656E7473C0A56564676573C0A773686172644964C0AA736861726445706F6368C0B073686172644C6F6753657175656E6365C0";

        [Test]
        public void HeaderWithTagsMatchesTheConstantCommittedIntoTheRustTest()
        {
            var header = new CultNetRawDocumentHeader
            {
                SchemaId = LeafA,
                RecordKey = "a-eq",
                StoredAt = "1970-01-01T00:00:00.000Z",
                Tags = new[] { "alpha", "beta" }
            };
            var hex = Convert.ToHexString(MessagePackSerializer.Serialize(header, CultNetSchemaMessageSerialization.Options));
            Assert.That(hex, Is.EqualTo(RustHeaderWithTagsHex),
                "string[] Tags must encode to the same bytes cultnet-rs's Vec<String> tags does");
        }

        [Test]
        public void EdgeAllAbsentMatchesTheConstantCommittedIntoTheRustTest()
        {
            // R-AZ (docs/cultnet-selection-cut.md, "Soul, the merge gate, fourth pass"): the
            // 246-byte all-absent Edge vector existed only on the Rust side (SM-10). The all-absent
            // header is covered incidentally because it is nested inside the 376-byte envelope test
            // above; the edge has no such cover, so a C# edge encoder that resumed omitting a null
            // payload/payloadEncoding would pass every other test here and still diverge from Rust.
            var edge = new CultNetEdge
            {
                From = new CultNetRecordRef { SchemaId = CiterId, RecordKey = "citer-1" },
                Role = "Design",
                To = new CultNetRecordRef { SchemaId = LeafA, RecordKey = "a-eq" }
            };
            var hex = Convert.ToHexString(MessagePackSerializer.Serialize(edge, CultNetSchemaMessageSerialization.Options));
            Assert.That(hex, Is.EqualTo(RustEdgeAllAbsentHex),
                "an all-absent Edge (PayloadEncoding/Payload both null) must encode to the same bytes cultnet-rs's all-absent Edge does");
        }

        [Test]
        public void EdgeWithPayloadMatchesTheConstantCommittedIntoTheRustTest()
        {
            var edge = new CultNetEdge
            {
                From = new CultNetRecordRef { SchemaId = CiterId, RecordKey = "citer-1" },
                Role = "Design",
                To = new CultNetRecordRef { SchemaId = LeafA, RecordKey = "a-eq" },
                PayloadEncoding = "messagepack",
                Payload = new byte[] { 1, 2, 3 }
            };
            var hex = Convert.ToHexString(MessagePackSerializer.Serialize(edge, CultNetSchemaMessageSerialization.Options));
            Assert.That(hex, Is.EqualTo(RustEdgeWithPayloadHex),
                "byte[] Payload must encode to the same bytes cultnet-rs's serde_bytes payload does");
        }

        [Test]
        public void SnapshotResponseRawV1EnvelopeMatchesTheConstantCommittedIntoTheRustTest()
        {
            var message = new CultNetSnapshotResponseRawV1Message
            {
                MessageId = "m",
                Matched = 1,
                AsOf = 1,
                Headers = new[]
                {
                    new CultNetRawDocumentHeader { SchemaId = LeafA, RecordKey = "a-eq", StoredAt = "1970-01-01T00:00:00.000Z" }
                }
            };
            var hex = Convert.ToHexString(MessagePackSerializer.Serialize(message, CultNetSchemaMessageSerialization.Options));
            Assert.That(hex.Length / 2, Is.EqualTo(376));
            Assert.That(hex, Is.EqualTo(RustEnvelopeAllAbsentHex),
                "the whole cultnet.snapshot_response_raw.v1 envelope must encode identically on both runtimes");
        }
    }
}
