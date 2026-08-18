#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

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
        public async Task SingleFileMessagePackBackingStore_PullAll_SeesFileCreatedAfterReaderOpened()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.msgpack");

            try
            {
                var readCache = new CultCache();
                readCache.AddBackingStore(new SingleFileMessagePackBackingStore(filePath));
                await readCache.PullAllBackingStoresAsync();

                var observedChanges = 0;
                using var subscription = readCache.Watch<NamedTestEntry>()
                    .Subscribe(_ => observedChanges++);

                var writeStore = new SingleFileMessagePackBackingStore(filePath);
                var writeCache = new CultCache();
                writeCache.AddBackingStore(writeStore);
                var handle = await writeCache.AddAsync(new NamedTestEntry
                {
                    Name = "external-writer",
                    Value = "visible-after-refresh"
                });
                writeStore.PushAll();

                await readCache.PullAllBackingStoresAsync();

                Assert.That(readCache.Get<NamedTestEntry>(handle.Key)?.Value, Is.EqualTo("visible-after-refresh"));
                Assert.That(observedChanges, Is.EqualTo(1));

                await readCache.PullAllBackingStoresAsync();
                Assert.That(observedChanges, Is.EqualTo(1), "an unchanged snapshot must not replay its documents");

                await writeCache.UpsertAsync(new NamedTestEntry
                {
                    Name = "external-writer",
                    Value = "updated-once"
                }, handle);
                writeStore.PushAll();
                await readCache.PullAllBackingStoresAsync();

                Assert.That(readCache.Get<NamedTestEntry>(handle.Key)?.Value, Is.EqualTo("updated-once"));
                Assert.That(observedChanges, Is.EqualTo(2));
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
        public async Task SingleFileMessagePackBackingStore_PullAll_DoesNotEraseUnflushedLocalMutations()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.msgpack");

            try
            {
                using (var seed = await CultCacheMessagePack.OpenAsync(filePath))
                {
                    await seed.UpsertAsync(new NamedTestEntry
                    {
                        Name = "persisted",
                        Value = "before-local-write"
                    });
                    await seed.FlushAsync();
                }

                CultRecordHandle<NamedTestEntry> localHandle;
                using (var cache = await CultCacheMessagePack.OpenAsync(filePath))
                {
                    localHandle = await cache.UpsertAsync(new NamedTestEntry
                    {
                        Name = "local",
                        Value = "must-survive-pull"
                    });

                    Assert.That(cache.IsDirty, Is.True);
                    await cache.PullAllBackingStoresAsync();

                    Assert.That(cache.Get<NamedTestEntry>(localHandle.Key)?.Value,
                        Is.EqualTo("must-survive-pull"));
                    Assert.That(cache.IsDirty, Is.True,
                        "pulling a clean disk snapshot must not pardon staged local mutations");
                    await cache.FlushAsync();
                }

                using var reopened = await CultCacheMessagePack.OpenAsync(filePath);
                Assert.That(reopened.Get<NamedTestEntry>(localHandle.Key)?.Value,
                    Is.EqualTo("must-survive-pull"));
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
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
        public async Task CultCacheManagedDocument_Keeps_Poco_Surface_And_Updates_Soa_Storage()
        {
            var cache = new CultCache();
            var aliceKey = new CultRecordKey("entity:alice");
            var managed = cache.Document<TransformEntry>(aliceKey);
            TransformEntry? observed = null;
            using var subscription = managed.Watch().Subscribe(value => observed = value);

            await managed.ReplaceAsync(new TransformEntry
            {
                Name = "alice",
                PositionX = 1.5f,
                PositionY = 2.5f,
                Health = 90
            });
            await cache.UpsertAsync(new TransformEntry
            {
                Name = "bob",
                PositionX = 3.5f,
                PositionY = 4.5f,
                Health = 80
            }, new CultRecordHandle<TransformEntry>(new CultRecordKey("entity:bob")));

            managed.Value!.Health = 70;
            await managed.CommitAsync();

            var table = cache.Soa<TransformEntry>();

            Assert.That(table.Count, Is.EqualTo(2));
            Assert.That(table.Keys.Select(key => key.Value).ToArray(), Is.EqualTo(new[] { "entity:alice", "entity:bob" }));
            Assert.That(table.Column<float>(nameof(TransformEntry.PositionX)).Span.ToArray(), Is.EqualTo(new[] { 1.5f, 3.5f }));
            Assert.That(table.Column<float>(nameof(TransformEntry.PositionY)).Span.ToArray(), Is.EqualTo(new[] { 2.5f, 4.5f }));
            Assert.That(table.Column<int>(nameof(TransformEntry.Health)).Span.ToArray(), Is.EqualTo(new[] { 70, 80 }));
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed!.Health, Is.EqualTo(70));
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_Writes_Record_Pages_Without_Rewriting_Cold_Records()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                var cache = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                var first = await cache.UpsertAsync(new NamedTestEntry
                {
                    Name = "cold",
                    Value = new string('a', 4096)
                });
                await cache.FlushAsync();
                cache.Dispose();

                var firstRecord = Directory.GetFiles(recordsPath, "*.msgpack").Single();
                var firstWrite = File.GetLastWriteTimeUtc(firstRecord);

                await Task.Delay(1100);

                var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                var second = await reopened.UpsertAsync(new NamedTestEntry
                {
                    Name = "hot",
                    Value = "new"
                });
                await reopened.FlushAsync();
                reopened.Dispose();

                var recordFiles = Directory.GetFiles(recordsPath, "*.msgpack");
                Assert.That(recordFiles, Has.Length.EqualTo(2));
                Assert.That(File.GetLastWriteTimeUtc(firstRecord), Is.EqualTo(firstWrite));
                Assert.That(new FileInfo(filePath).Length, Is.LessThan(new FileInfo(firstRecord).Length));

                var read = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(read.Get<NamedTestEntry>(first.Key)!.Value, Is.EqualTo(new string('a', 4096)));
                Assert.That(read.Get<NamedTestEntry>(second.Key)!.Value, Is.EqualTo("new"));
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                if (Directory.Exists(recordsPath))
                {
                    Directory.Delete(recordsPath, recursive: true);
                }
            }
        }

        [Test]
        public async Task CultCacheMessagePack_OpenAsync_HydratesPersistedGlobalBeforeDefault()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(GlobalTestEntry) });
            typeof(CultDocumentDescriptor)
                .GetField("<IsGlobal>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(registry.GetRequired<GlobalTestEntry>(), true);

            try
            {
                using (var seed = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { Registry = registry, UseDirectoryStore = true }))
                {
                    var global = seed.GetGlobal<GlobalTestEntry>();
                    Assert.That(global, Is.Not.Null);
                    global!.Value = "durable authority";
                    await seed.UpsertAsync(global);
                    await seed.FlushAsync();
                }

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions
                    {
                        Registry = registry,
                        UseDirectoryStore = true,
                        DirectoryStoreHydrationFilter = _ => false
                    });

                Assert.That(reopened.GetGlobal<GlobalTestEntry>(), Is.Null,
                    "the hydration filter owns whether the durable global becomes hot");
                Assert.That(reopened.IsDirty, Is.False,
                    "a cold durable global must not stage its schema default as a write");

                var globalKey = "global:" + registry.GetRequired<GlobalTestEntry>().SchemaId;
                await reopened.PullBackingStoreRecordsAsync(metadata => metadata.Key == globalKey);
                Assert.That(reopened.GetGlobal<GlobalTestEntry>()?.Value, Is.EqualTo("durable authority"));
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_CleanFlush_DoesNotRewriteManifest()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                using (var seed = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    await seed.UpsertAsync(new NamedTestEntry
                    {
                        Name = "clean-flush",
                        Value = "must-remain-durable"
                    });
                    await seed.FlushAsync();
                }

                using var cache = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(cache.IsDirty, Is.False);

                using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.That(async () => await cache.FlushAsync(), Throws.Nothing,
                        "a clean indexed store must not touch its durable manifest");
                }

                Assert.That(cache.IsDirty, Is.False);
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_IndexedFilter_DoesNotOpenOrDeleteColdPayloads()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                CultRecordHandle<NamedTestEntry> hot;
                CultRecordHandle<NamedTestEntry> cold;
                using (var seed = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    hot = await seed.UpsertAsync(new NamedTestEntry { Name = "hot", Value = "hydrate-me" });
                    cold = await seed.UpsertAsync(new NamedTestEntry
                    {
                        Name = "cold",
                        Value = new string('c', 1024 * 1024)
                    });
                    await seed.FlushAsync();
                }

                var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(filePath));
                Assert.That(manifest.FormatVersion, Is.EqualTo("cultcache.store.v4.directory-content-addressed-pages"));
                Assert.That(manifest.Records, Has.Length.EqualTo(2));
                Assert.That(manifest.Records.All(record => record.Payload.Length == 32), Is.True,
                    "the hot index carries only the SHA-256 page identity, never the document body");

                var coldPath = Directory.GetFiles(recordsPath, "*.msgpack")
                    .Single(path => string.Equals(
                        CultDocumentMessagePackSerialization.DeserializePersistedRecord(File.ReadAllBytes(path)).Key,
                        cold.Key.Value,
                        StringComparison.Ordinal));

                using (new FileStream(coldPath, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var selected = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions
                           {
                               UseDirectoryStore = true,
                               DirectoryStoreHydrationFilter = metadata =>
                                   string.Equals(metadata.Key, hot.Key.Value, StringComparison.Ordinal)
                           }))
                {
                    Assert.That(selected.Get<NamedTestEntry>(hot.Key)?.Value, Is.EqualTo("hydrate-me"));
                    Assert.That(selected.Get<NamedTestEntry>(cold.Key), Is.Null);
                    await selected.UpsertAsync(new NamedTestEntry { Name = "new", Value = "persist-with-cold-page-locked" });
                    await selected.FlushAsync();
                }

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(cold.Key)?.Value.Length, Is.EqualTo(1024 * 1024));
                Assert.That(reopened.GetAll<NamedTestEntry>().Count(), Is.EqualTo(3));

                var finalManifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(filePath));
                Assert.That(finalManifest.Records, Has.Length.EqualTo(3));
                Assert.That(finalManifest.Records.Any(record => record.Key == cold.Key.Value), Is.True);
                Assert.That(finalManifest.Records.All(record => record.Payload.Length == 32), Is.True);
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                if (Directory.Exists(recordsPath))
                    Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_PullSelected_HydratesMatchingColdPagesOnly()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                CultRecordHandle<NamedTestEntry> hot;
                CultRecordHandle<NamedTestEntry> requested;
                CultRecordHandle<NamedTestEntry> unrelated;
                using (var seed = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    hot = await seed.UpsertAsync(new NamedTestEntry { Name = "hot", Value = "already-loaded" });
                    requested = await seed.UpsertAsync(new NamedTestEntry { Name = "requested", Value = "load-later" });
                    unrelated = await seed.UpsertAsync(new NamedTestEntry
                    {
                        Name = "unrelated",
                        Value = new string('u', 1024 * 1024)
                    });
                    await seed.FlushAsync();
                }

                string PageFor(CultRecordHandle<NamedTestEntry> handle) =>
                    Directory.GetFiles(recordsPath, "*.msgpack").Single(path => string.Equals(
                        CultDocumentMessagePackSerialization.DeserializePersistedRecord(File.ReadAllBytes(path)).Key,
                        handle.Key.Value,
                        StringComparison.Ordinal));

                using var selected = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions
                    {
                        UseDirectoryStore = true,
                        DirectoryStoreHydrationFilter = metadata => metadata.Key == hot.Key.Value
                    });
                Assert.That(selected.Get<NamedTestEntry>(requested.Key), Is.Null);

                using (new FileStream(PageFor(unrelated), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await selected.PullBackingStoreRecordsAsync(metadata => metadata.Key == requested.Key.Value);
                    Assert.That(selected.Get<NamedTestEntry>(requested.Key)?.Value, Is.EqualTo("load-later"));
                    Assert.That(selected.Get<NamedTestEntry>(hot.Key)?.Value, Is.EqualTo("already-loaded"));
                    Assert.That(selected.Get<NamedTestEntry>(unrelated.Key), Is.Null);
                }
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                if (Directory.Exists(recordsPath))
                    Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_Loads_Record_When_Manifest_Misses_Catalog_Entry()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            var recordPath = Path.Combine(recordsPath, "stale-schema-record.msgpack");

            try
            {
                Directory.CreateDirectory(recordsPath);

                var entry = new SchemaStampedTestEntry
                {
                    SchemaVersion = "tests.schema_stamped_entry.v1",
                    Name = "schema-stamped",
                    Value = "still readable"
                };
                var record = new CultPersistedRecord
                {
                    Key = "record:stale",
                    SchemaId = "sha256:stale-schema-id-from-cold-record",
                    StoredAt = "2026-06-25T12:00:00Z",
                    Payload = CultDocumentMessagePackSerialization.SerializeUntyped(entry, typeof(SchemaStampedTestEntry))
                };
                var manifest = new CultPersistedStoreSnapshot
                {
                    FormatVersion = "cultcache.store.v1.directory",
                    SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
                    Records = Array.Empty<CultPersistedRecord>()
                };

                File.WriteAllBytes(filePath, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));
                File.WriteAllBytes(recordPath, CultDocumentMessagePackSerialization.SerializePersistedRecord(record));

                var readStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var readCache = new CultCache();
                readCache.AddBackingStore(readStore);
                await readCache.PullAllBackingStoresAsync();

                var loaded = readCache.Get<SchemaStampedTestEntry>(new CultRecordKey("record:stale"));

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Value, Is.EqualTo("still readable"));
                Assert.That(readStore.LastSchemaMigrationReports, Has.Count.EqualTo(1));
                Assert.That(readStore.LastSchemaMigrationReports[0].PersistedSchemaName, Is.EqualTo("tests.schema_stamped_entry"));
                Assert.That(readStore.LastSchemaMigrationReports[0].Kind, Is.EqualTo(CultSchemaMigrationKind.CompatibleDrift));
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                if (Directory.Exists(recordsPath))
                {
                    Directory.Delete(recordsPath, recursive: true);
                }
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_Does_Not_Expose_New_Pages_Before_Manifest_Commit()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                var store = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                var key = new CultRecordKey("record:catalog-precommit");
                await cache.UpsertAsync(new NamedTestEntry { Name = "old", Value = "durable" }, new CultRecordHandle<NamedTestEntry>(key));
                store.PushAll();

                var recordPath = Directory.GetFiles(recordsPath, "*.msgpack").Single();
                var durableSchema = CultDocumentMessagePackSerialization
                    .DeserializePersistedRecord(File.ReadAllBytes(recordPath))
                    .SchemaId;
                var alternateDescriptor = CultDocumentRegistry.Shared.GetRequired<AlternateNamedTestEntry>();
                store.Push(new CultStoredDocument(
                    key,
                    DateTimeOffset.UtcNow.ToString("O"),
                    alternateDescriptor,
                    new AlternateNamedTestEntry { Name = "new" }));

                store.FlushStageProbe = (stage, _) =>
                {
                    if (string.Equals(stage, DirectoryMessagePackBackingStore.BeforeManifestCommitStage, StringComparison.Ordinal))
                        throw new IOException("injected crash before manifest commit");
                };
                Assert.That(() => store.PushAll(), Throws.TypeOf<IOException>());

                var precommit = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(filePath));
                var alternateSchema = alternateDescriptor.SchemaId;
                Assert.That(precommit.SchemaCatalog.Select(entry => entry.SchemaId), Does.Contain(durableSchema));
                Assert.That(precommit.SchemaCatalog.Select(entry => entry.SchemaId), Does.Not.Contain(alternateSchema));
                Assert.That(store.IsDirty, Is.True);
                Assert.That(CultDocumentMessagePackSerialization.DeserializePersistedRecord(File.ReadAllBytes(recordPath)).SchemaId, Is.EqualTo(durableSchema));

                store.FlushStageProbe = null;
                store.PushAll();

                var compacted = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(filePath));
                Assert.That(compacted.SchemaCatalog.Select(entry => entry.SchemaId), Is.EqualTo(new[] { alternateSchema }));
                Assert.That(store.IsDirty, Is.False);
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task DirectoryMessagePackBackingStore_Fault_Before_Manifest_Leaves_Entire_Previous_Generation(int writtenPageCount)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                var store = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                var handles = new[]
                {
                    new CultRecordHandle<NamedTestEntry>(new CultRecordKey("record:atomic:a")),
                    new CultRecordHandle<NamedTestEntry>(new CultRecordKey("record:atomic:b")),
                    new CultRecordHandle<NamedTestEntry>(new CultRecordKey("record:atomic:c"))
                };
                foreach (var handle in handles)
                    await cache.UpsertAsync(new NamedTestEntry { Name = handle.Key.Value, Value = "old" }, handle);
                store.PushAll();

                foreach (var handle in handles)
                    await cache.UpsertAsync(new NamedTestEntry { Name = handle.Key.Value, Value = "new" }, handle);
                store.FlushStageProbe = (stage, index) =>
                {
                    if ((writtenPageCount < handles.Length &&
                         string.Equals(stage, DirectoryMessagePackBackingStore.BeforeRecordPageWriteStage, StringComparison.Ordinal) &&
                         index == writtenPageCount) ||
                        (writtenPageCount == handles.Length &&
                         string.Equals(stage, DirectoryMessagePackBackingStore.BeforeManifestCommitStage, StringComparison.Ordinal)))
                        throw new IOException($"injected crash after {writtenPageCount} pages");
                };
                Assert.That(() => store.PushAll(), Throws.TypeOf<IOException>());

                using (var reopened = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    foreach (var handle in handles)
                        Assert.That(reopened.Get<NamedTestEntry>(handle.Key)?.Value, Is.EqualTo("old"));
                }

                store.FlushStageProbe = null;
                store.PushAll();
                using (var reopened = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    foreach (var handle in handles)
                        Assert.That(reopened.Get<NamedTestEntry>(handle.Key)?.Value, Is.EqualTo("new"));
                }
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_SameTimestampPayload_CannotOverwriteCommittedPageBeforeManifest()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                const string storedAt = "2026-08-19T00:00:00.0000000+00:00";
                var store = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                var key = new CultRecordKey("record:atomic:same-timestamp");
                var descriptor = CultDocumentRegistry.Shared.GetRequired<NamedTestEntry>();
                store.Push(new CultStoredDocument(
                    key,
                    storedAt,
                    descriptor,
                    new NamedTestEntry { Name = "same", Value = "old" }));
                store.PushAll();

                store.Push(new CultStoredDocument(
                    key,
                    storedAt,
                    descriptor,
                    new NamedTestEntry { Name = "same", Value = "new" }));
                store.FlushStageProbe = (stage, _) =>
                {
                    if (string.Equals(stage, DirectoryMessagePackBackingStore.BeforeManifestCommitStage, StringComparison.Ordinal))
                        throw new IOException("injected crash before same-timestamp manifest commit");
                };
                Assert.That(() => store.PushAll(), Throws.TypeOf<IOException>());

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(key)?.Value, Is.EqualTo("old"));
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_MutationDuringFlush_RemainsDirtyAndCommitsNextGeneration()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                var store = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                var first = await cache.UpsertAsync(new NamedTestEntry { Name = "first", Value = "one" });
                using var flushEntered = new ManualResetEventSlim();
                using var writerAttempting = new ManualResetEventSlim();
                store.FlushStageProbe = (stage, _) =>
                {
                    if (!string.Equals(stage, DirectoryMessagePackBackingStore.BeforeManifestCommitStage, StringComparison.Ordinal))
                        return;
                    flushEntered.Set();
                    Assert.That(writerAttempting.Wait(TimeSpan.FromSeconds(5)), Is.True);
                };

                var flush = Task.Run(() => store.PushAll());
                var writer = Task.Run(async () =>
                {
                    Assert.That(flushEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    writerAttempting.Set();
                    return await cache.UpsertAsync(new NamedTestEntry { Name = "second", Value = "two" });
                });
                await flush;
                var second = await writer;

                Assert.That(store.IsDirty, Is.True, "the mutation blocked behind flush must remain pending");
                store.FlushStageProbe = null;
                store.PushAll();
                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(first.Key)?.Value, Is.EqualTo("one"));
                Assert.That(reopened.Get<NamedTestEntry>(second.Key)?.Value, Is.EqualTo("two"));
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_ConcurrentInstances_MergeUnderOneCommitLease()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                var firstStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var firstCache = new CultCache();
                firstCache.AddBackingStore(firstStore);
                var secondStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var secondCache = new CultCache();
                secondCache.AddBackingStore(secondStore);
                var first = await firstCache.UpsertAsync(new NamedTestEntry { Name = "first-process", Value = "one" });
                var second = await secondCache.UpsertAsync(new NamedTestEntry { Name = "second-process", Value = "two" });

                await Task.WhenAll(
                    Task.Run(() => firstStore.PushAll()),
                    Task.Run(() => secondStore.PushAll()));

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(first.Key)?.Value, Is.EqualTo("one"));
                Assert.That(reopened.Get<NamedTestEntry>(second.Key)?.Value, Is.EqualTo("two"));
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_ReaderLeasesManifestUntilEveryReferencedPageIsHydrated()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                var key = new CultRecordKey("record:reader-generation");
                var writerStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var writerCache = new CultCache();
                writerCache.AddBackingStore(writerStore);
                await writerCache.UpsertAsync(
                    new NamedTestEntry { Name = "generation", Value = "old" },
                    new CultRecordHandle<NamedTestEntry>(key));
                writerStore.PushAll();

                var readerStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var readerCache = new CultCache();
                readerCache.AddBackingStore(readerStore);
                using var manifestRead = new ManualResetEventSlim();
                using var releaseReader = new ManualResetEventSlim();
                readerStore.ReadStageProbe = stage =>
                {
                    if (!string.Equals(stage, DirectoryMessagePackBackingStore.AfterManifestReadStage, StringComparison.Ordinal))
                        return;
                    manifestRead.Set();
                    Assert.That(releaseReader.Wait(TimeSpan.FromSeconds(5)), Is.True);
                };

                var read = Task.Run(() => readerStore.PullAll());
                Assert.That(manifestRead.Wait(TimeSpan.FromSeconds(5)), Is.True);
                await writerCache.UpsertAsync(
                    new NamedTestEntry { Name = "generation", Value = "new" },
                    new CultRecordHandle<NamedTestEntry>(key));
                var write = Task.Run(() => writerStore.PushAll());
                Assert.That(write.Wait(TimeSpan.FromMilliseconds(100)), Is.False,
                    "writer must wait while a reader hydrates its selected manifest generation");

                releaseReader.Set();
                await Task.WhenAll(read, write);
                Assert.That(readerCache.Get<NamedTestEntry>(key)?.Value, Is.EqualTo("old"));

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(key)?.Value, Is.EqualTo("new"));
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_ReleasesGenerationLeaseBeforePublishingObservers()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                var writerStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var writerCache = new CultCache();
                writerCache.AddBackingStore(writerStore);
                await writerCache.UpsertAsync(new NamedTestEntry { Name = "source", Value = "one" });
                writerStore.PushAll();

                var readerStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var readerCache = new CultCache();
                readerCache.AddBackingStore(readerStore);
                var observerRan = false;
                using var subscription = readerStore.EntryAdded.Subscribe(_ =>
                {
                    if (observerRan)
                        return;
                    observerRan = true;
                    readerCache.UpsertAsync(new NamedTestEntry { Name = "observer", Value = "two" })
                        .GetAwaiter()
                        .GetResult();
                    readerStore.PushAll();
                });

                Assert.That(() => readerStore.PullAll(), Throws.Nothing);
                Assert.That(observerRan, Is.True);
                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.GetAll<NamedTestEntry>().Select(value => value.Name),
                    Is.EquivalentTo(new[] { "source", "observer" }));
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_PullsOnlyExternalRecordDeltas()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                using var reader = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                var observedChanges = 0;
                using var subscription = reader.Watch<NamedTestEntry>().Subscribe(_ => observedChanges++);

                using var writer = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                var handle = await writer.UpsertAsync(new NamedTestEntry
                {
                    Name = "external-command",
                    Value = "once"
                });
                await writer.FlushAsync();

                await reader.PullAllBackingStoresAsync();
                await reader.PullAllBackingStoresAsync();

                Assert.That(reader.Get<NamedTestEntry>(handle.Key)?.Value, Is.EqualTo("once"));
                Assert.That(observedChanges, Is.EqualTo(1), "unchanged paged records must not replay");
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                if (Directory.Exists(recordsPath))
                    Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_PullAll_PreservesUnflushedLocalKeys()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                using (var seed = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    await seed.UpsertAsync(new NamedTestEntry
                    {
                        Name = "persisted",
                        Value = "before-local-write"
                    });
                    await seed.FlushAsync();
                }

                CultRecordHandle<NamedTestEntry> localHandle;
                using (var cache = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    localHandle = await cache.UpsertAsync(new NamedTestEntry
                    {
                        Name = "local",
                        Value = "must-survive-pull"
                    });

                    await cache.PullAllBackingStoresAsync();

                    Assert.That(cache.Get<NamedTestEntry>(localHandle.Key)?.Value,
                        Is.EqualTo("must-survive-pull"));
                    Assert.That(cache.IsDirty, Is.True);
                    await cache.FlushAsync();
                }

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(localHandle.Key)?.Value,
                    Is.EqualTo("must-survive-pull"));
            }
            finally
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                if (Directory.Exists(recordsPath))
                    Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryMessagePackBackingStore_Deletion_Commits_While_Locked_Old_Page_Remains_An_Orphan()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                var store = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                var handle = await cache.AddAsync(new NamedTestEntry { Name = "delete", Value = "durable" });
                store.PushAll();
                var recordPath = Directory.GetFiles(recordsPath, "*.msgpack").Single();
                Assert.That(cache.Remove(handle.Key), Is.True);

                using (new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.That(() => store.PushAll(), Throws.Nothing);
                    Assert.That(File.Exists(recordPath), Is.True);
                }

                Assert.That(store.IsDirty, Is.False);
                var compacted = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(filePath));
                Assert.That(compacted.SchemaCatalog, Is.Empty);
                Assert.That(compacted.Records, Is.Empty);
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(recordsPath)) Directory.Delete(recordsPath, recursive: true);
            }
        }

        [Test]
        public async Task CultCache_RuntimeType_Upsert_Snapshot_And_Remove_Work_For_Editor_Tooling()
        {
            var cache = new CultCache();
            var entry = new NamedTestEntry
            {
                Name = "editor",
                Value = "first"
            };

            var key = await cache.UpsertAsync(typeof(NamedTestEntry), entry);
            var stored = cache.AllStoredDocuments.Single(document => document.Key.Equals(key));

            Assert.That(stored.Document, Is.SameAs(entry));
            Assert.That(stored.Descriptor.DocumentType, Is.EqualTo(typeof(NamedTestEntry)));

            entry.Value = "second";
            var sameKey = await cache.UpsertAsync(typeof(NamedTestEntry), entry, key);

            Assert.That(sameKey, Is.EqualTo(key));
            Assert.That(cache.Get<NamedTestEntry>(key)!.Value, Is.EqualTo("second"));
            Assert.That(cache.Remove(key), Is.True);
            Assert.That(cache.Get<NamedTestEntry>(key), Is.Null);
            Assert.That(cache.Remove(key), Is.False);
        }

        [Test]
        public async Task CultCache_TypedStoredDocumentIndex_Tracks_Upsert_Replacement_And_Removal()
        {
            var cache = new CultCache();
            var key = new CultRecordKey("typed-index:named");
            await cache.UpsertAsync(typeof(NamedTestEntry), new NamedTestEntry
            {
                Name = "indexed",
                Value = "first"
            }, key);
            await cache.UpsertAsync(typeof(AlternateNamedTestEntry), new AlternateNamedTestEntry
            {
                Name = "unrelated"
            }, new CultRecordKey("typed-index:unrelated"));

            var initial = cache.GetStoredDocuments<NamedTestEntry>().Single();
            Assert.That(initial.Key, Is.EqualTo(key));
            Assert.That(((NamedTestEntry)initial.Document).Value, Is.EqualTo("first"));

            await cache.UpsertAsync(typeof(NamedTestEntry), new NamedTestEntry
            {
                Name = "indexed",
                Value = "second"
            }, key);
            var replaced = cache.GetStoredDocuments<NamedTestEntry>().Single();
            Assert.That(((NamedTestEntry)replaced.Document).Value, Is.EqualTo("second"));

            Assert.That(cache.Remove(key), Is.True);
            Assert.That(cache.GetStoredDocuments<NamedTestEntry>(), Is.Empty);
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

        [CultDocument("tests.global_entry", "tests.global_entry.v1")]
        internal sealed class GlobalTestEntry
        {
            [Key(0)]
            public string Value = "schema default";
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

        [CultDocument("tests.transform_entry", "tests.transform_entry.v1")]
        internal sealed class TransformEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public float PositionX;

            [Key(2)]
            public float PositionY;

            [Key(3)]
            public int Health;
        }

        [CultDocument("tests.schema_stamped_entry", "tests.schema_stamped_entry.v1")]
        internal sealed class SchemaStampedTestEntry
        {
            [Key(0)]
            public string SchemaVersion = string.Empty;

            [Key(1)]
            [CultName]
            public string Name = string.Empty;

            [Key(2)]
            public string Value = string.Empty;
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
