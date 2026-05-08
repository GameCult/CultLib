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
    }
}
