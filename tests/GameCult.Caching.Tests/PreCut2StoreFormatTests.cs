#nullable enable
using System;
using System.IO;
using System.Linq;
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

        // Shared refusal vectors: tests/vectors/document-variants-c0, read by every runtime's tests.
        private const string ItemSchemaId = "sha256:88d3fdf0a927acf3b163940d8f8c7fe62b3316542ce771a67ec8bc038f594788";

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c0", name);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException($"Shared vector {name} not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        private static string Refusal(string vector)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-vector-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                File.Copy(VectorPath(vector), file);
                using var cache = new CultCache();
                var error = Assert.Catch(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(file)));
                Assert.That(error, Is.Not.Null);
                Assert.That(error!.ToString(), Does.Contain(nameof(NotSupportedException)));
                return error.ToString();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void SingleFileRefusesUnknownHeaderByName()
        {
            Assert.That(Refusal("unknown-header.msgpack"), Does.Contain("cultcache.store.v9"));
        }

        [Test]
        public void SingleFileRefusesExtraRecordSlotNamingTheRecord()
        {
            var message = Refusal("extra-slot-full-payload.msgpack");
            Assert.That(message, Does.Contain("item:anvil"));
            Assert.That(message, Does.Contain(ItemSchemaId));
        }

        // C1 reads v2. This vector was written by hand before C1 existed and its variant keeps its base's name, so a
        // cache with a codec refuses it under R6, naming both records; a cache without one cannot resolve it at all.
        [Test]
        public void SingleFileRefusesTheHandWrittenVariantVectorNamingTheRecords()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-vector-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                File.Copy(VectorPath("variant-v2.msgpack"), file);

                using var withCodec = new CultCache(CultDocumentRegistry.Shared, CultCacheMessagePack.CreateCodec(CultDocumentRegistry.Shared));
                var named = Assert.Throws<InvalidOperationException>(() => withCodec.AddBackingStore(new SingleFileMessagePackBackingStore(file)))!;
                Assert.That(named.Message, Does.Contain("item:anvil-big").And.Contain("item:anvil"));

                using var without = new CultCache();
                var codecless = Assert.Throws<InvalidOperationException>(() => without.AddBackingStore(new SingleFileMessagePackBackingStore(file)))!;
                Assert.That(codecless.Message, Does.Contain("item:anvil-big").And.Contain("codec"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void SnapshotWithoutAFormatVersionIsRefusedNotDefaultedToV1()
        {
            // [nil, [], []]
            var bytes = new byte[] { 0x93, 0xc0, 0x90, 0x90 };

            Assert.That(() => CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("format version"));
        }

        // Every runtime reads a store file the same way: a missing file is an empty store, an existing empty file is not
        // a store (no CultCache writer leaves one), and an empty array carries no header, so it claims no format and is an
        // empty store too.
        [Test]
        public async Task SingleFileRefusesAnEmptyFileAndOpensAMissingFileOrAnEmptyArrayAsEmpty()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-empty-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var empty = Path.Combine(root, "empty.msgpack");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                using (var cache = new CultCache())
                {
                    Assert.Catch(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(empty)));
                    Assert.That(cache.BackingStores, Is.Empty);
                }
                Assert.That(File.ReadAllBytes(empty), Is.Empty);

                var emptyArray = Path.Combine(root, "empty-array.msgpack");
                File.WriteAllBytes(emptyArray, new byte[] { 0x90 });
                foreach (var path in new[] { Path.Combine(root, "missing.msgpack"), emptyArray })
                {
                    using var cache = new CultCache();
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await cache.PullAllBackingStoresAsync();
                    Assert.That(cache.GetAll<PreCut2FixtureItem>(), Is.Empty, path);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A refusal echoes a header only in the shape cultcache.store.v<digits>; any other is described by its length.
        [TestCase("cultcache.store.v12", true)]
        [TestCase("cultcache.store.SECRET-HEADER", false)]
        [TestCase("cultcache.store.v12SECRET", false)]
        [TestCase("cultcache.store.v", false)]
        public void SingleFileRefusalEchoesAHeaderOnlyInTheKnownShape(string header, bool echoed)
        {
            var snapshot = new CultPersistedStoreSnapshot { FormatVersion = header };
            var error = Assert.Throws<NotSupportedException>(() => CultDocumentMessagePackSerialization.RequireSingleFileFormat(snapshot))!;
            if (echoed)
            {
                Assert.That(error.Message, Does.Contain("format " + header + " is not"));
            }
            else
            {
                Assert.That(error.Message, Does.Not.Contain("SECRET").And.Not.Contain("format " + header + " is not"));
                Assert.That(error.Message, Does.Contain($"of {header.Length} bytes"));
            }
        }

        [Test]
        public void V1StoreWrittenAtTheBaseCommitStillDecodesByteForByte()
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(VectorPath("v1-base.msgpack")));
            CultDocumentMessagePackSerialization.RequireSingleFileFormat(snapshot);

            Assert.That(snapshot.FormatVersion, Is.EqualTo("cultcache.store.v1"));
            Assert.That(snapshot.Records.Select(record => record.Key), Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(snapshot.Records[0].Payload, Is.EqualTo(new byte[] { 0x92, 0xa5, (byte)'a', (byte)'l', (byte)'p', (byte)'h', (byte)'a', 0x01 }));
            Assert.That(snapshot.Records[1].Payload, Is.EqualTo(new byte[] { 0x92, 0xa4, (byte)'b', (byte)'e', (byte)'t', (byte)'a', 0x02 }));
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
