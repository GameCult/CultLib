using System;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // A record's payload is one MessagePack value: each CultNet path that decodes a payload refuses a tail, by the one rule in
    // CultPayloadDepth.Check. One test per path family, so that pointing a path at a tolerant reader turns it red.
    public class BytesAfterPathTests
    {
        private const string Clause = "1 bytes after its first MessagePack value";

        [CultDocument("tests.bytes_after_net", "tests.bytes_after_net.v1")]
        [MessagePackObject]
        public sealed class TailDoc
        {
            [Key(0)] [CultName] public string Id = "a";
            [Key(1)] public int N = 3;
        }

        private static readonly CultDocumentRegistry Documents = CultDocumentRegistry.ForTypes(new[] { typeof(TailDoc) });

        private static byte[] WithTail() =>
            CultDocumentMessagePackSerialization.SerializeUntyped(new TailDoc(), typeof(TailDoc), Documents).Concat(new byte[] { 0xc0 }).ToArray();

        private static CultNetRawDocumentRecord Raw(CultNetDocumentRegistry registry, byte[] payload) => new()
        {
            SchemaId = Documents.GetRequired(typeof(TailDoc)).SchemaId,
            RecordKey = "a",
            StoredAt = "1970-01-01T00:00:00.000Z",
            Payload = payload,
        };

        private static void Refuses(TestDelegate read) =>
            Assert.That(Assert.Catch(read)!.ToString(), Does.Contain(Clause));

        [Test]
        public void TheGenericBindingRefusesATail() =>
            Refuses(() => CultNetDocumentBinding.ForDocument<TailDoc>(Documents).PayloadDeserializer(WithTail()));

        [Test]
        public void TheRuntimeTypeBindingRefusesATail() =>
            Refuses(() => CultNetDocumentBinding.ForDocument(typeof(TailDoc), Documents).PayloadDeserializer(WithTail()));

        [Test]
        public void ARawEnvelopeThroughARegisteredBindingRefusesATail()
        {
            var registry = new CultNetDocumentRegistry(Documents).Register(CultNetDocumentBinding.ForDocument<TailDoc>(Documents));
            Refuses(() => registry.DeserializeRawDocument(Raw(registry, WithTail())));
        }

        [Test]
        public void ARawEnvelopeWithNoBindingRefusesATail()
        {
            var registry = new CultNetDocumentRegistry(Documents);
            Refuses(() => registry.DeserializeRawDocument(Raw(registry, WithTail())));
        }
    }
}
