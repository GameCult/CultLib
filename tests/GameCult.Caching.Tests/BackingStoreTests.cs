using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Caching.NewtonsoftJson;
using Newtonsoft.Json;
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
                store.RegisterType<NamedTestEntry>();
                var cache = new CultCache();
                cache.AddBackingStore(store);

                var entry = new NamedTestEntry
                {
                    ID = Guid.NewGuid(),
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
                writeStore.RegisterType<NamedTestEntry>();
                var writeCache = new CultCache();
                writeCache.AddBackingStore(writeStore);

                var entry = new NamedTestEntry
                {
                    ID = Guid.NewGuid(),
                    Name = "RoundTrip",
                    Value = "payload"
                };

                await writeCache.AddAsync(entry);

                var readStore = new MultiFileNewtonsoftJsonBackingStore(rootPath);
                readStore.RegisterType<NamedTestEntry>();
                var readCache = new CultCache();
                readCache.AddBackingStore(readStore);
                await readCache.PullAllBackingStoresAsync();

                var loaded = readCache.Get(entry.ID);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(GetEntryValue(loaded!), Is.EqualTo("payload"));
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
                writeStore.RegisterType<NamedTestEntry>();
                var writeCache = new CultCache();
                writeCache.AddBackingStore(writeStore);

                var entry = new NamedTestEntry
                {
                    ID = Guid.NewGuid(),
                    Name = "SingleFile",
                    Value = "payload"
                };

                await writeCache.AddAsync(entry);
                writeStore.PushAll();

                var readStore = new SingleFileNewtonsoftJsonBackingStore(filePath);
                readStore.RegisterType<NamedTestEntry>();
                var readCache = new CultCache();
                readCache.AddBackingStore(readStore);
                await readCache.PullAllBackingStoresAsync();

                var loaded = readCache.Get(entry.ID);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(GetEntryValue(loaded!), Is.EqualTo("payload"));
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
        public void MultiFileNewtonsoftJsonBackingStore_Rejects_UnknownDiscriminator()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);

            try
            {
                var store = new MultiFileNewtonsoftJsonBackingStore(rootPath);
                store.RegisterType<NamedTestEntry>();
                var payload = """
                              {
                                "Type": "Unknown.Assembly:Unknown.Type",
                                "Data": {
                                  "ID": "00000000-0000-0000-0000-000000000001",
                                  "Name": "Bad",
                                  "Value": "Bad"
                                }
                              }
                              """;

                Assert.That(
                    () => store.Deserialize(Encoding.UTF8.GetBytes(payload)),
                    Throws.TypeOf<JsonSerializationException>().With.Message.Contains("Unknown DatabaseEntry discriminator"));
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
        public async Task MultiFileNewtonsoftJsonBackingStore_Delete_Removes_File()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);

            try
            {
                var store = new MultiFileNewtonsoftJsonBackingStore(rootPath);
                store.RegisterType<NamedTestEntry>();
                var cache = new CultCache();
                cache.AddBackingStore(store);

                var entry = new NamedTestEntry
                {
                    ID = Guid.NewGuid(),
                    Name = "DeleteMe",
                    Value = "payload"
                };

                await cache.AddAsync(entry);
                cache.Remove(entry);

                var files = Directory.GetFiles(rootPath, "*.json", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .ToArray();

                Assert.That(files, Does.Not.Contain("DeleteMe.json"));
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
        public void MessagePackDatabaseEntrySerialization_RoundTrips_DatabaseLink()
        {
            var serializationType = typeof(MultiFileMessagePackBackingStore).Assembly
                .GetType("GameCult.Caching.MessagePack.DatabaseEntrySerialization", throwOnError: true)!;
            var serialize = serializationType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(DatabaseLink<DatabaseEntry>));
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(DatabaseLink<DatabaseEntry>));
            var link = new DatabaseLink<DatabaseEntry> { LinkID = Guid.NewGuid() };

            var payload = (byte[])serialize.Invoke(null, [link])!;
            var roundTrip = (DatabaseLink<DatabaseEntry>)deserialize.Invoke(null, [payload])!;

            Assert.That(roundTrip.LinkID, Is.EqualTo(link.LinkID));
        }

        [Test]
        public void MessagePackDatabaseEntrySerialization_Rejects_InvalidPayload()
        {
            var serializationType = typeof(MultiFileMessagePackBackingStore).Assembly
                .GetType("GameCult.Caching.MessagePack.DatabaseEntrySerialization", throwOnError: true)!;
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(DatabaseEntry));

            Assert.That(
                () => deserialize.Invoke(null, [new byte[] { 0xC1 }]),
                Throws.TypeOf<TargetInvocationException>()
                    .With.InnerException.InstanceOf<global::MessagePack.MessagePackSerializationException>());
        }

        private sealed class NamedTestEntry : DatabaseEntry, INamedEntry
        {
            public string Name = string.Empty;
            public string Value = string.Empty;

            public string EntryName
            {
                get => Name;
                set => Name = value;
            }
        }

        private static string GetEntryValue(DatabaseEntry entry)
        {
            return entry.GetType().GetField("Value")?.GetValue(entry) as string ?? string.Empty;
        }
    }
}
