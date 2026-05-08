using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Caching.NewtonsoftJson;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class BackingStoreTests
    {
        [Test]
        public async Task MultiFileBackingStore_Rename_RemovesStaleFile()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);

            try
            {
                var store = new MultiFileNewtonsoftJsonBackingStore(rootPath);
                var cache = new CultCache();
                cache.AddBackingStore(store);

                var entry = new NamedTestEntry
                {
                    Name = "OriginalName",
                    Value = "value"
                };

                await cache.AddAsync(entry);

                entry.Name = "RenamedUser";
                await cache.AddAsync(entry);

                var files = Directory.GetFiles(rootPath, "*.json", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .ToArray();

                Assert.That(files, Does.Contain("RenamedUser.json"));
                Assert.That(files, Does.Not.Contain("OriginalName.json"));
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        [Test]
        public async Task MultiFileNewtonsoftJsonBackingStore_RoundTrips_DiscoveredEntryType()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);

            try
            {
                var writeStore = new MultiFileNewtonsoftJsonBackingStore(rootPath);
                var writeCache = new CultCache();
                writeCache.AddBackingStore(writeStore);

                var entry = new NamedTestEntry
                {
                    Name = "RoundTrip",
                    Value = "payload"
                };

                var handle = await writeCache.AddAsync(entry);

                var readStore = new MultiFileNewtonsoftJsonBackingStore(rootPath);
                var readCache = new CultCache();
                readCache.AddBackingStore(readStore);
                await readCache.PullAllBackingStoresAsync();

                var loaded = readCache.Get<NamedTestEntry>(handle.Key);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Value, Is.EqualTo("payload"));
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        [Test]
        public async Task SingleFileNewtonsoftJsonBackingStore_RoundTrips_DiscoveredEntryType()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.json");

            try
            {
                var writeStore = new SingleFileNewtonsoftJsonBackingStore(filePath);
                var writeCache = new CultCache();
                writeCache.AddBackingStore(writeStore);

                var entry = new NamedTestEntry
                {
                    Name = "SingleFile",
                    Value = "payload"
                };

                var handle = await writeCache.AddAsync(entry);
                writeStore.PushAll();

                var readStore = new SingleFileNewtonsoftJsonBackingStore(filePath);
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

        [CultDocument("tests.named_entry", "tests.named_entry.v1")]
        [MessagePackObject]
        internal sealed class NamedTestEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Value = string.Empty;
        }
    }
}
