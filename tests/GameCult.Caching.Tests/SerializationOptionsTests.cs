#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using MessagePack.Formatters;
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

        // An unset reference is "" (A0) on the wire. Legacy is the same record as 1.0.58 wrote it, with nil (C0).
        private const string UnsetRefPayloadHex = "93A6686F6C646572A0A56F74686572";
        private const string UnsetRefLegacyPayloadHex = "93A6686F6C646572C0A56F74686572";
        // A ref-keyed dictionary whose unset key is "" (A0): msgpack readers under strict_map_key refuse a nil key.
        private const string RefKeyedPayloadHex = "92A6686F6C64657282A5616C706861CA3E800000A0CA40000000";

        [Test]
        public async Task UnsetRefWritesEmptyStringAndResavesByteIdentical()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultlib-unsetref-{Guid.NewGuid():N}.cc");
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(UnsetRefHolder) });
            var key = new CultRecordKey("holder");
            try
            {
                using (var seed = new CultCache(registry))
                {
                    seed.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await seed.UpsertAsync(typeof(UnsetRefHolder), new UnsetRefHolder { Name = "holder", Set = new CultRecordRef<UnsetRefHolder>(new CultRecordKey("other")) }, key);
                    await seed.FlushAsync();
                }
                var written = Convert.ToHexString(StoredPayload(path));

                using (var reopened = new CultCache(registry))
                {
                    reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await reopened.PullAllBackingStoresAsync();
                    await reopened.UpsertAsync(typeof(UnsetRefHolder), reopened.Get<UnsetRefHolder>(key)!, key);
                    await reopened.FlushAsync();
                }
                var resaved = Convert.ToHexString(StoredPayload(path));

                var legacy = (UnsetRefHolder)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(UnsetRefHolder), Convert.FromHexString(UnsetRefLegacyPayloadHex), registry);
                var legacyResaved = Convert.ToHexString(CultDocumentMessagePackSerialization.SerializeUntyped(legacy, typeof(UnsetRefHolder), registry));

                Assert.Multiple(() =>
                {
                    Assert.That(written, Is.EqualTo(UnsetRefPayloadHex), "the writer writes an unset reference as \"\"");
                    Assert.That(resaved, Is.EqualTo(UnsetRefPayloadHex), "reloaded through a fresh cache, it resaves byte-identical");
                    Assert.That(legacy.Unset, Is.EqualTo(default(CultRecordRef<UnsetRefHolder>)), "a nil reference reads as unset");
                    Assert.That(legacyResaved, Is.EqualTo(UnsetRefPayloadHex), "a nil reference resaves as \"\"");
                });
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public async Task RefKeyedDictionaryWithUnsetKeyRoundTripsByteIdentical()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultlib-refkeyed-{Guid.NewGuid():N}.cc");
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RefKeyedHolder) });
            var alpha = new CultRecordRef<RefKeyedHolder>(new CultRecordKey("alpha"));
            var handle = new CultRecordHandle<RefKeyedHolder>(new CultRecordKey("holder"));
            try
            {
                using (var cache = new CultCache(registry))
                {
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await cache.UpsertAsync(new RefKeyedHolder { Name = "holder", Weights = new() { [alpha] = 0.25f, [default] = 2f } }, handle);
                    cache.FlushAllBackingStores();
                }
                var written = Convert.ToHexString(StoredPayload(path));

                Dictionary<CultRecordRef<RefKeyedHolder>, float> weights;
                using (var reopened = new CultCache(registry))
                {
                    reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await reopened.PullAllBackingStoresAsync();
                    var loaded = reopened.Get<RefKeyedHolder>(handle.Key)!;
                    weights = loaded.Weights;
                    await reopened.UpsertAsync(loaded, handle);
                    reopened.FlushAllBackingStores();
                }
                var resaved = Convert.ToHexString(StoredPayload(path));

                Assert.Multiple(() =>
                {
                    Assert.That(written, Is.EqualTo(RefKeyedPayloadHex), "the unset key is written as \"\"");
                    Assert.That(weights, Is.EqualTo(new Dictionary<CultRecordRef<RefKeyedHolder>, float> { [alpha] = 0.25f, [default] = 2f }), "the unset key reads back unset");
                    Assert.That(resaved, Is.EqualTo(RefKeyedPayloadHex), "reloaded, it resaves byte-identical");
                });
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static byte[] StoredPayload(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Single().Payload;

        [Test]
        public void DeclaredResolverEncodesValueTypeAsPositionalArray()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(PairHolder) });
            var holder = new PairHolder { Name = "pair", Value = new Pair { A = 1.5f, B = -2f } };

            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(holder, typeof(PairHolder), registry);

            var reader = new MessagePackReader(payload);
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
            reader.Skip();
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
            Assert.That(reader.ReadSingle(), Is.EqualTo(1.5f));
            Assert.That(reader.ReadSingle(), Is.EqualTo(-2f));
            var decoded = (PairHolder)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(PairHolder), payload, registry);
            Assert.That(decoded.Value, Is.EqualTo(holder.Value));
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

        // GenericHolder<Pair> lives in an assembly that declares a resolver writing Pair as [B, A]; Pair's own assembly writes [A, B].
        [Test]
        public void GenericDocumentUsesItsOwnAssemblyResolvers()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GenericHolders"), AssemblyBuilderAccess.Run, new[]
            {
                new CustomAttributeBuilder(typeof(CultCacheFormatterResolverAttribute).GetConstructor(new[] { typeof(Type) })!, new object[] { typeof(ReversedPairResolver) })
            });
            var builder = assembly.DefineDynamicModule("GenericHolders").DefineType("GenericHolder`1", TypeAttributes.Public | TypeAttributes.Sealed);
            var parameter = builder.DefineGenericParameters("T")[0];
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, new object[] { "tests.generic_holder", "tests.generic_holder.v1" }));
            builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false }));
            builder.DefineField("Value", parameter, FieldAttributes.Public)
                .SetCustomAttribute(new CustomAttributeBuilder(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { 0 }));
            var type = builder.CreateType()!.MakeGenericType(typeof(Pair));
            var registry = CultDocumentRegistry.ForTypes(new[] { type });
            var holder = Activator.CreateInstance(type)!;
            type.GetField("Value")!.SetValue(holder, new Pair { A = 1.5f, B = -2f });

            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(holder, type, registry);

            Assert.That(type.Assembly, Is.Not.SameAs(typeof(Pair).Assembly));
            var reader = new MessagePackReader(payload);
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(1));
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
            Assert.That(reader.ReadSingle(), Is.EqualTo(-2f), "Pair's own assembly resolver was used instead of the document's");
            Assert.That(reader.ReadSingle(), Is.EqualTo(1.5f));
            var decoded = CultDocumentMessagePackSerialization.DeserializeUntyped(type, payload, registry);
            Assert.That(type.GetField("Value")!.GetValue(decoded), Is.EqualTo(new Pair { A = 1.5f, B = -2f }));
        }

        public sealed class ReversedPairResolver : IFormatterResolver
        {
            public static readonly IFormatterResolver Instance = new ReversedPairResolver();
            public IMessagePackFormatter<T>? GetFormatter<T>() => typeof(T) == typeof(Pair) ? (IMessagePackFormatter<T>)(object)new ReversedPairFormatter() : null;
        }

        public sealed class ReversedPairFormatter : IMessagePackFormatter<Pair>
        {
            public void Serialize(ref MessagePackWriter writer, Pair value, MessagePackSerializerOptions options) { writer.WriteArrayHeader(2); writer.Write(value.B); writer.Write(value.A); }
            public Pair Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) { reader.ReadArrayHeader(); var b = reader.ReadSingle(); return new Pair { A = reader.ReadSingle(), B = b }; }
        }

        [CultDocument("tests.pair_holder", "tests.pair_holder.v1")]
        [MessagePackObject]
        public sealed class PairHolder
        {
            [Key(0)] public string Name = string.Empty;
            [Key(1)] public Pair Value;
        }

        [CultDocument("cultcache.interop-note", "cultcache.interop_note.v1")]
        [MessagePackObject]
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
        [MessagePackObject]
        public sealed class RefKeyedHolder
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public Dictionary<CultRecordRef<RefKeyedHolder>, float> Weights = new();
        }

        [CultDocument("tests.unset_ref_holder", "tests.unset_ref_holder.v1")]
        [MessagePackObject]
        public sealed class UnsetRefHolder
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public CultRecordRef<UnsetRefHolder> Unset;
            [Key(2)] public CultRecordRef<UnsetRefHolder> Set;
        }
    }
}
