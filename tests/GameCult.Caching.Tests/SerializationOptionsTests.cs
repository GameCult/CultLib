#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class SerializationOptionsTests
    {
        // Captured on CultLib 825b4f7, before per-assembly options.
        private const string InteropNotePayloadHex =
            "96B963756C7463616368652E696E7465726F705F6E6F74652E7631AB6E6F74653A637368617270A6637368617270BD6373686172702077726F746520612043756C744361636865206E6F7465D9245468652076312073746F726520666F726D61742069732074686520636F6E74726163742E92A6637368617270A7696E7465726F70";

        [Test]
        public void InteropNoteBytesUnchanged()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(OptionsInteropNote) });
            var note = new OptionsInteropNote
            {
                DocumentId = "note:csharp",
                AuthorRuntimeId = "csharp",
                Title = "csharp wrote a CultCache note",
                Body = "The v1 store format is the contract.",
                Tags = new[] { "csharp", "interop" }
            };

            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(note, typeof(OptionsInteropNote), registry);

            Assert.That(Convert.ToHexString(payload), Is.EqualTo(InteropNotePayloadHex));
        }

        [Test]
        public async Task RefKeyedDictionaryRoundTrips()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultlib-refkeyed-{Guid.NewGuid():N}.cc");
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RefKeyedHolder) });
            var alpha = new CultRecordRef<RefKeyedHolder>(new CultRecordKey("alpha"));
            var beta = new CultRecordRef<RefKeyedHolder>(new CultRecordKey("beta"));
            try
            {
                using (var cache = new CultCache(registry))
                {
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await cache.UpsertAsync(
                        new RefKeyedHolder { Name = "holder", Weights = new() { [alpha] = 0.25f, [beta] = 2f } },
                        new CultRecordHandle<RefKeyedHolder>(new CultRecordKey("holder")));
                    cache.FlushAllBackingStores();
                }

                using var reopened = new CultCache(registry);
                reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await reopened.PullAllBackingStoresAsync();
                var loaded = reopened.Get<RefKeyedHolder>(new CultRecordKey("holder"));

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Weights[new CultRecordRef<RefKeyedHolder>(new CultRecordKey("alpha"))], Is.EqualTo(0.25f));
                Assert.That(loaded.Weights[new CultRecordRef<RefKeyedHolder>(new CultRecordKey("beta"))], Is.EqualTo(2f));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void DeclaredResolverEncodesValueTypeAsPositionalArray()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(PairHolder) });
            var holder = new PairHolder { Name = "pair", Value = new Pair { A = 1.5f, B = -2f } };
            Assert.That(registry.GetRequired<PairHolder>().GeneratedPayloadSerializer, Is.Not.Null, "no generated codec");

            var generated = CultDocumentMessagePackSerialization.SerializeUntyped(holder, typeof(PairHolder), registry);
            // SerializeUntyped's reflective fallback, which a document without a generated codec takes.
            var reflective = MessagePackSerializer.Serialize(
                typeof(PairHolder), holder, CultDocumentMessagePackSerialization.OptionsFor(typeof(PairHolder).Assembly));

            Assert.That(reflective, Is.EqualTo(generated));
            var reader = new MessagePackReader(generated);
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
            reader.Skip();
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
            Assert.That(reader.ReadSingle(), Is.EqualTo(1.5f));
            Assert.That(reader.ReadSingle(), Is.EqualTo(-2f));

            var fromGenerated = (PairHolder)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(PairHolder), generated, registry);
            var fromReflective = (PairHolder)MessagePackSerializer.Deserialize(
                typeof(PairHolder), reflective, CultDocumentMessagePackSerialization.OptionsFor(typeof(PairHolder).Assembly))!;
            Assert.That(fromGenerated.Value, Is.EqualTo(holder.Value));
            Assert.That(fromReflective.Value, Is.EqualTo(holder.Value));
        }

        [Test]
        public void UnknownAssemblyGetsBaseOptions()
        {
            Assert.That(CultDocumentMessagePackSerialization.OptionsFor(typeof(object).Assembly),
                Is.SameAs(CultDocumentMessagePackSerialization.Options));
            Assert.That(CultDocumentMessagePackSerialization.Options.Resolver.GetFormatter<Pair>(), Is.Null);
            Assert.That(CultDocumentMessagePackSerialization.OptionsFor(typeof(Pair).Assembly),
                Is.SameAs(CultDocumentMessagePackSerialization.OptionsFor(typeof(Pair).Assembly)));
            Assert.That(CultDocumentMessagePackSerialization.OptionsFor(typeof(Pair).Assembly).Resolver.GetFormatter<Pair>(), Is.Not.Null);
        }

        [CultDocument("tests.pair_holder", "tests.pair_holder.v1")]
        [MessagePackObject]
        public sealed class PairHolder
        {
            [Key(0)] public string Name = string.Empty;
            [Key(1)] public Pair Value;
        }

        [CultDocument("cultcache.interop-note", "cultcache.interop_note.v1")]
        public sealed class OptionsInteropNote
        {
            [Key(0)] public string SchemaVersion { get; set; } = "cultcache.interop_note.v1";
            [Key(1)] [CultName] public string DocumentId { get; set; } = string.Empty;
            [Key(2)] public string AuthorRuntimeId { get; set; } = string.Empty;
            [Key(3)] public string Title { get; set; } = string.Empty;
            [Key(4)] public string Body { get; set; } = string.Empty;
            [Key(5)] public string[] Tags { get; set; } = Array.Empty<string>();
        }

        [CultDocument("tests.ref_keyed_holder", "tests.ref_keyed_holder.v1")]
        public sealed class RefKeyedHolder
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public Dictionary<CultRecordRef<RefKeyedHolder>, float> Weights = new();
        }
    }
}
