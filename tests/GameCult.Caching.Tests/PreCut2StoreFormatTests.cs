#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class PreCut2StoreFormatTests
    {
        private static string FixtureRoot =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "pre-cut2");

        private static string CopyFixture(string name)
        {
            var target = Path.Combine(Path.GetTempPath(), $"cultlib-precut2-{Guid.NewGuid():N}");
            var source = Path.Combine(FixtureRoot, name);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            return target;
        }

        private static void AssertFixtureRecords(CultCache cache)
        {
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:anvil")), Is.Not.Null);
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:anvil"))!.Name, Is.EqualTo("anvil"));
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:anvil"))!.Count, Is.EqualTo(3));
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:bellows"))?.Name, Is.EqualTo("bellows"));
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:bellows"))?.Count, Is.EqualTo(42));
            Assert.That(cache.Get<PreCut2FixtureNote>(new CultRecordKey("note:origin"))?.Text,
                Is.EqualTo("written before cut 2"));
            Assert.That(cache.GetAll<PreCut2FixtureItem>(), Has.Length.EqualTo(2));
            Assert.That(cache.GetAll<PreCut2FixtureNote>(), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task DirectoryStoreOpensV4ManifestWrittenBeforeCut2()
        {
            var root = CopyFixture("directory-v4");
            try
            {
                var manifest = Path.Combine(root, "store.cc");
                Assert.That(CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(manifest)).FormatVersion,
                    Is.EqualTo("cultcache.store.v4.directory-content-addressed-pages"));

                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                await cache.PullAllBackingStoresAsync();

                AssertFixtureRecords(cache);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SingleFileOpensV1SnapshotWrittenBeforeCut2()
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                Assert.That(CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file)).FormatVersion,
                    Is.EqualTo("cultcache.store.v1"));

                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();

                AssertFixtureRecords(cache);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // Format strings taken from DirectoryMessagePackBackingStore.cs at 0db1fe5.
        [TestCase("cultcache.store.v1")]
        [TestCase("cultcache.store.v1.directory")]
        [TestCase("cultcache.store.v2.directory-indexed")]
        [TestCase("cultcache.store.v3.directory-immutable-pages")]
        public void DirectoryStoreRefusesNonV4Manifest(string formatVersion)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-refuse-{Guid.NewGuid():N}");
            var manifest = Path.Combine(root, "store.cc");
            Directory.CreateDirectory(root);
            try
            {
                var bytes = CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
                {
                    FormatVersion = formatVersion,
                    SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
                    Records = Array.Empty<CultPersistedRecord>()
                });
                File.WriteAllBytes(manifest, bytes);

                using var cache = new CultCache();

                Assert.That(() => cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest)),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains(formatVersion));
                Assert.That(cache.BackingStores, Is.Empty);
                Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(bytes));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
