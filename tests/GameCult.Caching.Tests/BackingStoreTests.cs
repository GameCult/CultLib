#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class BackingStoreTests
    {
        [Test]
        public async Task SingleFileMessagePackBackingStore_RoundTrips_DiscoveredEntryType()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.msgpack");

            try
            {
                var writeStore = new SingleFileMessagePackBackingStore(filePath);
                var writeCache = new CultCache();
                writeCache.AddBackingStore(writeStore);

                var entry = new NamedTestEntry
                {
                    Name = "SingleFileMsgpack",
                    Value = "payload"
                };

                var handle = await writeCache.AddAsync(entry);
                writeStore.PushAll();

                var readStore = new SingleFileMessagePackBackingStore(filePath);
                var readCache = new CultCache();
                readCache.AddBackingStore(readStore);
                await readCache.PullAllBackingStoresAsync();

                var loaded = readCache.Get<NamedTestEntry>(handle.Key);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Value, Is.EqualTo("payload"));
                Assert.That(readStore.LastSchemaMigrationReports, Has.Count.EqualTo(1));
                Assert.That(readStore.LastSchemaMigrationReports[0].Kind, Is.EqualTo(CultSchemaMigrationKind.Exact));
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Test]
        public async Task CultCache_DirtyState_Tracks_Mutations_And_ExplicitFlush()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.msgpack");

            try
            {
                var store = new SingleFileMessagePackBackingStore(filePath);
                var cache = new CultCache();
                cache.AddBackingStore(store);

                Assert.That(cache.IsDirty, Is.False);
                Assert.That(store.IsDirty, Is.False);

                await cache.AddAsync(new NamedTestEntry
                {
                    Name = "dirty",
                    Value = "pending"
                });

                Assert.That(cache.IsDirty, Is.True);
                Assert.That(store.IsDirty, Is.True);
                Assert.That(cache.LastSuccessfulFlushAtUtc, Is.Null);

                cache.FlushAllBackingStores();

                Assert.That(cache.IsDirty, Is.False);
                Assert.That(store.IsDirty, Is.False);
                Assert.That(cache.LastSuccessfulFlushAtUtc, Is.Not.Null);
                Assert.That(store.LastSuccessfulFlushAtUtc, Is.Not.Null);
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Test]
        public async Task CultCacheMessagePack_OpenAsync_Creates_Usable_Durable_Cache()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.msgpack");

            try
            {
                var cache = await CultCacheMessagePack.OpenAsync(filePath);
                var handle = await cache.UpsertAsync(new NamedTestEntry
                {
                    Name = "open",
                    Value = "magic"
                });
                await cache.FlushAsync();
                cache.Dispose();

                var reopened = await CultCacheMessagePack.OpenAsync(filePath);
                Assert.That(reopened.TryGet(handle.Key, out NamedTestEntry? loaded), Is.True);
                Assert.That(loaded!.Value, Is.EqualTo("magic"));
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Test]
        public async Task CultCache_FlushOnDispose_Persists_When_Enabled()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.msgpack");

            try
            {
                var store = new SingleFileMessagePackBackingStore(filePath);
                var cache = new CultCache
                {
                    FlushAttachedStoresOnDispose = true
                };
                cache.AddBackingStore(store);

                var handle = await cache.AddAsync(new NamedTestEntry
                {
                    Name = "dispose",
                    Value = "flush"
                });

                cache.Dispose();

                var readStore = new SingleFileMessagePackBackingStore(filePath);
                var readCache = new CultCache();
                readCache.AddBackingStore(readStore);
                await readCache.PullAllBackingStoresAsync();

                var loaded = readCache.Get<NamedTestEntry>(handle.Key);
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Value, Is.EqualTo("flush"));
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Test]
        public void MessagePackSerialization_RoundTrips_CultRecordRef()
        {
            var reference = new CultRecordRef<NamedTestEntry>(new CultRecordKey("record-1"));

            var payload = CultDocumentMessagePackSerialization.Serialize(reference);
            var roundTrip = CultDocumentMessagePackSerialization.Deserialize<CultRecordRef<NamedTestEntry>>(payload);

            Assert.That(roundTrip.Key.Value, Is.EqualTo("record-1"));
        }

        [Test]
        public void MessagePackSerialization_Rejects_InvalidPayload()
        {
            Assert.That(
                () => CultDocumentMessagePackSerialization.Deserialize<CultRecordRef<NamedTestEntry>>(new byte[] { 0xC1 }),
                Throws.TypeOf<MessagePackSerializationException>());
        }

        [Test]
        public void GeneratedMetadataProvider_Emits_AttributedDocuments_And_References()
        {
            var providers = typeof(NamedTestEntry).Assembly
                .GetCustomAttributes(typeof(CultGeneratedDocumentMetadataProviderAttribute), false)
                .Cast<CultGeneratedDocumentMetadataProviderAttribute>()
                .ToArray();

            Assert.That(providers, Is.Not.Empty);

            var definitions = providers
                .SelectMany(provider =>
                    ((ICultGeneratedDocumentMetadataProvider)Activator.CreateInstance(provider.ProviderType)!)
                    .GetDocumentDefinitions())
                .ToArray();

            var named = definitions.Single(definition => definition.DocumentType == typeof(NamedTestEntry));
            Assert.That(named.SchemaName, Is.EqualTo("tests.named_entry"));
            Assert.That(named.NameMember, Is.EqualTo(nameof(NamedTestEntry.Name)));

            var referenceHolder = definitions.Single(definition => definition.DocumentType == typeof(ReferenceHolderEntry));
            var parentMember = referenceHolder.Members.Single(member => member.MemberName == nameof(ReferenceHolderEntry.Parent));
            Assert.That(parentMember.IsReference, Is.True);
            Assert.That(parentMember.TargetSchemaName, Is.EqualTo("tests.named_entry"));
            Assert.That(parentMember.TypeName, Does.Contain("CultRecordRef"));
        }

        [Test]
        public void GeneratedMetadataProvider_Emits_Payload_Codecs_For_Plain_CultDocuments()
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired<NamedTestEntry>();
            var original = new NamedTestEntry
            {
                Name = "Teeth",
                Value = "slot-array"
            };

            Assert.That(descriptor.GeneratedPayloadSerializer, Is.Not.Null);
            Assert.That(descriptor.GeneratedPayloadDeserializer, Is.Not.Null);

            var payload = descriptor.GeneratedPayloadSerializer!(original);
            var roundTrip = (NamedTestEntry)descriptor.GeneratedPayloadDeserializer!(payload);

            Assert.That(roundTrip.Name, Is.EqualTo("Teeth"));
            Assert.That(roundTrip.Value, Is.EqualTo("slot-array"));
        }

        [Test]
        public void MessagePackStoreSerialization_RoundTrips_Snapshot_Record_And_Catalog()
        {
            var record = new CultPersistedRecord
            {
                Key = "record-1",
                SchemaId = "schema-1",
                StoredAt = "2026-05-08T12:00:00Z",
                Payload = new byte[] { 0x91, 0xA3, 0x66, 0x6F, 0x6F }
            };
            var catalog = new[]
            {
                new CultSchemaCatalogEntry
                {
                    SchemaId = "schema-1",
                    SchemaName = "tests.named_entry",
                    SchemaVersion = "tests.named_entry.v1",
                    ContentHash = "hash-1",
                    CanonicalSchemaJson = "{\"fields\":2}",
                    CompatibleSchemaIds = new[] { "schema-1", "schema-0" }
                }
            };
            var snapshot = new CultPersistedStoreSnapshot
            {
                FormatVersion = "cultcache.store.v1",
                SchemaCatalog = catalog,
                Records = new[] { record }
            };

            var roundTripRecord = CultDocumentMessagePackSerialization.DeserializePersistedRecord(
                CultDocumentMessagePackSerialization.SerializePersistedRecord(record));
            var roundTripCatalog = CultDocumentMessagePackSerialization.DeserializeSchemaCatalog(
                CultDocumentMessagePackSerialization.SerializeSchemaCatalog(catalog));
            var roundTripSnapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(
                CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            Assert.That(roundTripRecord.Key, Is.EqualTo("record-1"));
            Assert.That(roundTripRecord.SchemaId, Is.EqualTo("schema-1"));
            Assert.That(roundTripRecord.StoredAt, Is.EqualTo("2026-05-08T12:00:00Z"));
            Assert.That(roundTripRecord.Payload, Is.EqualTo(record.Payload));
            Assert.That(roundTripCatalog.Single().CompatibleSchemaIds, Is.EqualTo(catalog.Single().CompatibleSchemaIds));
            Assert.That(roundTripSnapshot.FormatVersion, Is.EqualTo("cultcache.store.v1"));
            Assert.That(roundTripSnapshot.SchemaCatalog.Single().SchemaName, Is.EqualTo("tests.named_entry"));
            Assert.That(roundTripSnapshot.Records.Single().Key, Is.EqualTo("record-1"));
        }

        [Test]
        public void Registry_CanonicalSchemaJson_Tracks_Reference_Metadata()
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired<ReferenceHolderEntry>();

            Assert.That(descriptor.CanonicalSchemaJson, Does.Contain("\"targetSchemaName\":\"tests.named_entry\""));
            Assert.That(descriptor.CanonicalSchemaJson, Does.Contain("\"isReference\":true"));
        }

        [Test]
        public void Registry_CanonicalSchema_Fixtures_Are_Stable()
        {
            var named = CultDocumentRegistry.Shared.GetRequired<NamedTestEntry>();
            var referenceHolder = CultDocumentRegistry.Shared.GetRequired<ReferenceHolderEntry>();

            Assert.That(named.CanonicalSchemaJson, Is.EqualTo(NamedFixtureCanonicalSchemaJson));
            Assert.That(named.SchemaId, Is.EqualTo(NamedFixtureSchemaId));
            Assert.That(named.ContentHash, Is.EqualTo(NamedFixtureContentHash));
            Assert.That(referenceHolder.SchemaId, Is.EqualTo(ReferenceFixtureSchemaId));
        }

        [Test]
        public void ResolvePersistedSchemaReport_Classifies_Compatible_And_Incompatible_Drift()
        {
            var registry = CultDocumentRegistry.Shared;
            var namedV1 = registry.GetRequired<NamedTestEntry>();
            var namedV2 = registry.GetRequired<NamedTestEntryAdditive>();
            var namedV3 = registry.GetRequired<NamedTestEntryRemoved>();
            var namedMismatch = registry.GetRequired<NamedTestEntryTypeMismatch>();
            var referenceV1 = registry.GetRequired<ReferenceHolderEntry>();
            var referenceRetargeted = registry.GetRequired<ReferenceHolderRetargetedEntry>();

            var additiveCatalog = namedV1.ToCatalogEntry();
            additiveCatalog.SchemaId = "persisted.tests.named_entry.v1";
            additiveCatalog.CompatibleSchemaIds = new[] { namedV2.SchemaId };
            var additiveReport = registry.ResolvePersistedSchemaReport(additiveCatalog.SchemaId, new[] { additiveCatalog });
            Assert.That(additiveReport.Kind, Is.EqualTo(CultSchemaMigrationKind.CompatibleDrift));
            Assert.That(additiveReport.DefaultedMissingSlots, Is.EqualTo(new[] { 2 }));
            Assert.That(additiveReport.IgnoredExtraSlots, Is.Empty);

            var removedCatalog = namedV2.ToCatalogEntry();
            removedCatalog.SchemaId = "persisted.tests.named_entry.v2";
            removedCatalog.CompatibleSchemaIds = new[] { namedV3.SchemaId };
            var removedReport = registry.ResolvePersistedSchemaReport(removedCatalog.SchemaId, new[] { removedCatalog });
            Assert.That(removedReport.Kind, Is.EqualTo(CultSchemaMigrationKind.CompatibleDrift));
            Assert.That(removedReport.DefaultedMissingSlots, Is.Empty);
            Assert.That(removedReport.IgnoredExtraSlots, Is.EqualTo(new[] { 1, 2 }));

            var mismatchCatalog = namedV1.ToCatalogEntry();
            mismatchCatalog.SchemaId = "persisted.tests.named_entry.type_mismatch";
            mismatchCatalog.CompatibleSchemaIds = new[] { namedMismatch.SchemaId };
            Assert.That(
                () => registry.ResolvePersistedSchemaReport(mismatchCatalog.SchemaId, new[] { mismatchCatalog }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed type"));

            var retargetedCatalog = referenceV1.ToCatalogEntry();
            retargetedCatalog.SchemaId = "persisted.tests.reference_holder.v1";
            retargetedCatalog.CompatibleSchemaIds = new[] { referenceRetargeted.SchemaId };
            Assert.That(
                () => registry.ResolvePersistedSchemaReport(retargetedCatalog.SchemaId, new[] { retargetedCatalog }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed target schema"));
        }

        private const string NamedFixtureCanonicalSchemaJson =
            "{\"schemaName\":\"tests.named_entry\",\"schemaVersion\":\"tests.named_entry.v1\",\"members\":[{\"slot\":0,\"name\":\"Name\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":true},{\"slot\":1,\"name\":\"Value\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false}]}";
        private const string NamedFixtureSchemaId = "sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8";
        private const string NamedFixtureContentHash = "sha256:23150930afcc1d84f0cb3012ccc2debcb9b4685f62083033bbaab0083f1e832e";
        private const string ReferenceFixtureSchemaId = "sha256:bd85064961cc74565fb73e3ccbc4217cfba4dc4869e365a08bea4f704739bd8f";

        [CultDocument("tests.named_entry", "tests.named_entry.v1")]
        internal sealed class NamedTestEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Value = string.Empty;
        }

        [CultDocument("tests.reference_holder", "tests.reference_holder.v1")]
        internal sealed class ReferenceHolderEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public CultRecordRef<NamedTestEntry> Parent = new(new CultRecordKey("parent"));
        }

        [CultDocument("tests.named_entry", "tests.named_entry.v2")]
        internal sealed class NamedTestEntryAdditive
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Value = string.Empty;

            [Key(2)]
            public string Notes = string.Empty;
        }

        [CultDocument("tests.named_entry", "tests.named_entry.v3")]
        internal sealed class NamedTestEntryRemoved
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.named_entry", "tests.named_entry.v4")]
        internal sealed class NamedTestEntryTypeMismatch
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Value;
        }

        [CultDocument("tests.alt_named_entry", "tests.alt_named_entry.v1")]
        internal sealed class AlternateNamedTestEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.reference_holder", "tests.reference_holder.v2")]
        internal sealed class ReferenceHolderRetargetedEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public CultRecordRef<AlternateNamedTestEntry> Parent = new(new CultRecordKey("parent"));
        }
    }
}
