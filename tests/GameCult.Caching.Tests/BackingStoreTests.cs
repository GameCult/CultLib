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

                cache.FlushAllBackingStores();

                Assert.That(cache.IsDirty, Is.False);
                Assert.That(store.IsDirty, Is.False);
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
                Assert.That(reopened.TryGet<NamedTestEntry>(handle.Key, out var loaded), Is.True);
                Assert.That(loaded?.Value, Is.EqualTo("magic"));
                Assert.That(reopened.TryGet<NamedTestEntry>(new CultRecordKey("absent"), out var missing), Is.False);
                Assert.That(missing, Is.Null);
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
        public async Task DirectoryMessagePackBackingStore_Open_CreatesNothing_UntilAWrite()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "store.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);

            try
            {
                foreach (var readOnly in new[] { true, false })
                {
                    using (await CultCacheMessagePack.OpenAsync(filePath, new CultCacheOpenOptions { UseDirectoryStore = true, ReadOnly = readOnly }))
                    {
                    }

                    Assert.That(Directory.GetFileSystemEntries(root), Is.Empty,
                        $"opening a missing directory store (read-only {readOnly}) created files");
                }

                Directory.CreateDirectory(recordsPath);
                foreach (var readOnly in new[] { true, false })
                {
                    using (var cache = await CultCacheMessagePack.OpenAsync(filePath, new CultCacheOpenOptions { UseDirectoryStore = true, ReadOnly = readOnly }))
                    {
                        Assert.That(cache.AllStoredDocuments, Is.Empty);
                    }

                    Assert.That(Directory.GetFileSystemEntries(recordsPath), Is.Empty,
                        $"opening a manifest-less directory store (read-only {readOnly}) created a lock");
                    Assert.That(File.Exists(filePath), Is.False);
                }

                using (var writer = await CultCacheMessagePack.OpenAsync(filePath, new CultCacheOpenOptions { UseDirectoryStore = true }))
                {
                    await writer.UpsertAsync(new NamedTestEntry { Name = "first-write", Value = "creates the store" });
                    await writer.FlushAsync();
                }

                Assert.That(File.Exists(filePath), Is.True);
                Assert.That(File.Exists(Path.Combine(recordsPath, ".commit.lock")), Is.True);
                Assert.That(Directory.GetFiles(recordsPath, "*.msgpack"), Has.Length.EqualTo(1));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task CultCacheMessagePack_OpenAsync_HydratesPersistedGlobal()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(GlobalTestEntry) });

            try
            {
                using (var seed = await CultCacheMessagePack.OpenAsync(
                           filePath,
                           new CultCacheOpenOptions { Registry = registry, UseDirectoryStore = true }))
                {
                    Assert.That(seed.GetGlobal<GlobalTestEntry>(), Is.Null, "opening a store invented a global");
                    await seed.UpsertAsync(new GlobalTestEntry { Value = "durable authority" });
                    await seed.FlushAsync();
                }

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { Registry = registry, UseDirectoryStore = true });

                Assert.That(reopened.GetGlobal<GlobalTestEntry>()?.Value, Is.EqualTo("durable authority"));
                Assert.That(reopened.IsDirty, Is.False, "hydrating a global staged a write");
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
        public async Task DirectoryMessagePackBackingStore_Manifest_Indexes_Pages_By_Content_Hash_Only()
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

                using var reopened = await CultCacheMessagePack.OpenAsync(
                    filePath,
                    new CultCacheOpenOptions { UseDirectoryStore = true });
                Assert.That(reopened.Get<NamedTestEntry>(hot.Key)?.Value, Is.EqualTo("hydrate-me"));
                Assert.That(reopened.Get<NamedTestEntry>(cold.Key)?.Value.Length, Is.EqualTo(1024 * 1024));
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
        public async Task DirectoryMessagePackBackingStore_ReleasesGenerationLeaseBeforePublishingObservers()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            var recordsPath = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath);
            try
            {
                var readerStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var readerCache = new CultCache();
                readerCache.AddBackingStore(readerStore);

                var writerStore = new DirectoryMessagePackBackingStore(filePath, recordsPath);
                var writerCache = new CultCache();
                writerCache.AddBackingStore(writerStore);
                await writerCache.UpsertAsync(new NamedTestEntry { Name = "source", Value = "one" });
                writerStore.PushAll();

                var observerRan = false;
                using var subscription = readerCache.Watch<NamedTestEntry>().Subscribe(_ =>
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
                var reader = new CultCache();
                reader.AddBackingStore(new DirectoryMessagePackBackingStore(filePath, recordsPath));
                await reader.PullAllBackingStoresAsync();
                Assert.That(reader.GetAll<NamedTestEntry>(), Is.Empty,
                    "a reader must not resurrect an orphaned page left by another process");
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
        public async Task CultCache_GetAll_Tracks_Upsert_Replacement_And_Removal()
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

            var initial = cache.GetAll<NamedTestEntry>().Single();
            Assert.That(cache.TryGetHandle(initial)?.Key, Is.EqualTo(key));
            Assert.That(initial.Value, Is.EqualTo("first"));

            await cache.UpsertAsync(typeof(NamedTestEntry), new NamedTestEntry
            {
                Name = "indexed",
                Value = "second"
            }, key);
            var replaced = cache.GetAll<NamedTestEntry>().Single();
            Assert.That(replaced.Value, Is.EqualTo("second"));

            Assert.That(cache.Remove(key), Is.True);
            Assert.That(cache.GetAll<NamedTestEntry>(), Is.Empty);
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

            var payload = MessagePackSerializer.Serialize(reference, CultDocumentMessagePackSerialization.Options);
            var roundTrip = MessagePackSerializer.Deserialize<CultRecordRef<NamedTestEntry>>(payload, CultDocumentMessagePackSerialization.Options);

            Assert.That(roundTrip.Key.Value, Is.EqualTo("record-1"));
        }

        [Test]
        public void MessagePackSerialization_Rejects_InvalidPayload()
        {
            Assert.That(
                () => MessagePackSerializer.Deserialize<CultRecordRef<NamedTestEntry>>(new byte[] { 0xC1 }, CultDocumentMessagePackSerialization.Options),
                Throws.TypeOf<MessagePackSerializationException>());
        }

        [Test]
        public void Registry_Describes_AttributedDocuments_And_References()
        {
            var named = CultDocumentRegistry.Shared.GetRequired<NamedTestEntry>();
            Assert.That(named.SchemaName, Is.EqualTo("tests.named_entry"));
            Assert.That(named.NameMember, Is.EqualTo(nameof(NamedTestEntry.Name)));

            var parentMember = CultDocumentRegistry.Shared.GetRequired<ReferenceHolderEntry>().ToCatalogEntry().Members
                .Single(member => member.MemberName == nameof(ReferenceHolderEntry.Parent));
            Assert.That(parentMember.IsReference, Is.True);
            Assert.That(parentMember.TargetSchemaName, Is.EqualTo("tests.named_entry"));
            Assert.That(parentMember.TypeName, Does.Contain("CultRecordRef"));
        }

        [Test]
        public void Plain_CultDocument_Payload_RoundTrips()
        {
            var original = new NamedTestEntry
            {
                Name = "Teeth",
                Value = "slot-array"
            };

            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(original, typeof(NamedTestEntry));
            var roundTrip = (NamedTestEntry)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(NamedTestEntry), payload);

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
            var roundTripSnapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(
                CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            Assert.That(roundTripRecord.Key, Is.EqualTo("record-1"));
            Assert.That(roundTripRecord.SchemaId, Is.EqualTo("schema-1"));
            Assert.That(roundTripRecord.StoredAt, Is.EqualTo("2026-05-08T12:00:00Z"));
            Assert.That(roundTripRecord.Payload, Is.EqualTo(record.Payload));
            Assert.That(roundTripSnapshot.SchemaCatalog.Single().CompatibleSchemaIds, Is.EqualTo(catalog.Single().CompatibleSchemaIds));
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

        [Test]
        public async Task CultCache_Commit_Hides_Batch_Until_The_Directory_Store_Has_Committed_It()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultcache-commit-{Guid.NewGuid():N}");
            var manifest = Path.Combine(root, "state.cc");
            Directory.CreateDirectory(root);
            try
            {
                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                var firstKey = new CultRecordKey("commit:first");
                var secondKey = new CultRecordKey("commit:second");
                var observed = 0;
                using var subscription = cache.Watch<NamedTestEntry>().Subscribe(_ => observed++);

                var committed = cache.Commit(batch =>
                {
                    batch.Upsert(new NamedTestEntry { Name = "first", Value = "one" }, new CultRecordHandle<NamedTestEntry>(firstKey));
                    Assert.That(cache.Get<NamedTestEntry>(firstKey), Is.Null);
                    Assert.That(File.Exists(manifest), Is.False);
                    batch.Upsert(new NamedTestEntry { Name = "second", Value = "two" }, new CultRecordHandle<NamedTestEntry>(secondKey));
                    Assert.That(observed, Is.Zero);
                });

                Assert.That(committed, Is.True);
                Assert.That(cache.Get<NamedTestEntry>(firstKey)?.Value, Is.EqualTo("one"));
                Assert.That(cache.Get<NamedTestEntry>(secondKey)?.Value, Is.EqualTo("two"));
                Assert.That(observed, Is.EqualTo(2));
                Assert.That(cache.IsDirty, Is.False);

                using var reopened = new CultCache();
                reopened.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                Assert.That(reopened.Get<NamedTestEntry>(firstKey)?.Value, Is.EqualTo("one"));
                Assert.That(reopened.Get<NamedTestEntry>(secondKey)?.Value, Is.EqualTo("two"));
                await Task.CompletedTask;
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void CultCache_Commit_Stage_Failure_Cannot_Leak_Into_Later_Commit()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultcache-abort-{Guid.NewGuid():N}");
            var manifest = Path.Combine(root, "state.cc");
            Directory.CreateDirectory(root);
            try
            {
                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                var abortedKey = new CultRecordKey("commit:aborted");
                var committedKey = new CultRecordKey("commit:committed");
                var observed = 0;
                using var subscription = cache.Watch<NamedTestEntry>().Subscribe(_ => observed++);

                Assert.Throws<InvalidOperationException>(() => cache.Commit(batch =>
                {
                    batch.Upsert(new NamedTestEntry { Name = "aborted", Value = "must-not-leak" }, new CultRecordHandle<NamedTestEntry>(abortedKey));
                    throw new InvalidOperationException("abort probe");
                }));

                Assert.That(cache.Get<NamedTestEntry>(abortedKey), Is.Null);
                Assert.That(observed, Is.Zero);
                Assert.That(cache.Commit(batch =>
                    batch.Upsert(new NamedTestEntry { Name = "committed", Value = "survives" }, new CultRecordHandle<NamedTestEntry>(committedKey))), Is.True);
                Assert.That(observed, Is.EqualTo(1));

                using var reopened = new CultCache();
                reopened.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                Assert.That(reopened.Get<NamedTestEntry>(abortedKey), Is.Null);
                Assert.That(reopened.Get<NamedTestEntry>(committedKey)?.Value, Is.EqualTo("survives"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void CultCache_Commit_Batch_Used_After_Commit_Throws()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultcache-escaped-batch-{Guid.NewGuid():N}");
            var manifest = Path.Combine(root, "state.cc");
            Directory.CreateDirectory(root);
            try
            {
                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                var committedKey = new CultRecordKey("commit:committed-parent");
                var escapedKey = new CultRecordKey("commit:escaped-child");
                CultCacheBatch? escaped = null;

                cache.Commit(batch =>
                {
                    batch.Upsert(new NamedTestEntry { Name = "parent", Value = "committed" }, new CultRecordHandle<NamedTestEntry>(committedKey));
                    escaped = batch;
                });

                Assert.That(
                    () => escaped!.Upsert(new NamedTestEntry { Name = "child", Value = "must-fail" }, new CultRecordHandle<NamedTestEntry>(escapedKey)),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains("already been committed"));
                Assert.That(cache.Get<NamedTestEntry>(committedKey)?.Value, Is.EqualTo("committed"));
                Assert.That(cache.Get<NamedTestEntry>(escapedKey), Is.Null);

                using var reopened = new CultCache();
                reopened.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                Assert.That(reopened.Get<NamedTestEntry>(committedKey)?.Value, Is.EqualTo("committed"));
                Assert.That(reopened.Get<NamedTestEntry>(escapedKey), Is.Null);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void CultCache_Commit_Observer_Writes_In_A_New_Commit()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultcache-observer-commit-{Guid.NewGuid():N}");
            var manifest = Path.Combine(root, "state.cc");
            Directory.CreateDirectory(root);
            try
            {
                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                var firstKey = new CultRecordKey("commit:observer-source");
                var secondKey = new CultRecordKey("commit:observer-write");
                using var subscription = cache.Watch<NamedTestEntry>().Subscribe(change =>
                {
                    if (!change.Key.Equals(firstKey)) return;
                    cache.Commit(batch => batch.Upsert(
                        new NamedTestEntry { Name = "observer", Value = "separate-commit" },
                        new CultRecordHandle<NamedTestEntry>(secondKey)));
                });

                cache.Commit(batch => batch.Upsert(
                    new NamedTestEntry { Name = "source", Value = "committed-first" },
                    new CultRecordHandle<NamedTestEntry>(firstKey)));

                Assert.That(cache.Get<NamedTestEntry>(secondKey)?.Value, Is.EqualTo("separate-commit"));
                using var reopened = new CultCache();
                reopened.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                Assert.That(reopened.Get<NamedTestEntry>(firstKey)?.Value, Is.EqualTo("committed-first"));
                Assert.That(reopened.Get<NamedTestEntry>(secondKey)?.Value, Is.EqualTo("separate-commit"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private const string NamedFixtureCanonicalSchemaJson =
            "{\"schemaName\":\"tests.named_entry\",\"schemaVersion\":\"tests.named_entry.v1\",\"members\":[{\"slot\":0,\"name\":\"Name\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":true},{\"slot\":1,\"name\":\"Value\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false}]}";
        private const string NamedFixtureSchemaId = "sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8";
        private const string NamedFixtureContentHash = "sha256:23150930afcc1d84f0cb3012ccc2debcb9b4685f62083033bbaab0083f1e832e";
        private const string ReferenceFixtureSchemaId = "sha256:4ec0c52c7581c04777525af72f805b10705212ffd0138f3bff1eb4bcd1d0f23c";

        [CultDocument("tests.named_entry", "tests.named_entry.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NamedTestEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Value = string.Empty;
        }

        [CultDocument("tests.global_entry", "tests.global_entry.v1")]
        [MessagePackObject(AllowPrivate = true)]
        [CultGlobal]
        internal sealed class GlobalTestEntry
        {
            [Key(0)]
            public string Value = "schema default";
        }

        [CultDocument("tests.reference_holder", "tests.reference_holder.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class ReferenceHolderEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public CultRecordRef<NamedTestEntry> Parent = new(new CultRecordKey("parent"));
        }

        [CultDocument("tests.named_entry", "tests.named_entry.v2")]
        [MessagePackObject(AllowPrivate = true)]
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
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NamedTestEntryRemoved
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.named_entry", "tests.named_entry.v4")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NamedTestEntryTypeMismatch
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Value;
        }

        [CultDocument("tests.alt_named_entry", "tests.alt_named_entry.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class AlternateNamedTestEntry
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.reference_holder", "tests.reference_holder.v2")]
        [MessagePackObject(AllowPrivate = true)]
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
