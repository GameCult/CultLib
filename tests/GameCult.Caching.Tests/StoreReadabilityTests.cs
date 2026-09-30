#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;

namespace GameCult.Caching.Tests
{
    // A file is replaced by a flush or a commit exactly when this runtime's own reader opens it: one verdict per file, asked by
    // open, flush and commit alike. The bytes and the verdict of every runtime are shared: tests/vectors/document-variants-c2a/readability.
    public class StoreReadabilityTests
    {
        private const int CSharp = 1;
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck), typeof(VectorItem) });
        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-readability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private static string VectorRoot()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c2a", "readability");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            throw new DirectoryNotFoundException($"Shared readability vectors not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        public static IEnumerable<TestCaseData> Vectors() =>
            File.ReadAllLines(Path.Combine(VectorRoot(), "manifest.txt"))
                .Where(line => line.Length > 0 && line[0] != '#')
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Select(cells => new TestCaseData(cells[0], cells[CSharp] == "reads").SetName($"{cells[0]} {cells[CSharp]}"));

        private static string Header(string path) => CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

        private string Seed(string name)
        {
            var path = Path.Combine(_directory, name);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "d" }, new CultRecordKey("d")));
            return path;
        }

        [TestCaseSource(nameof(Vectors))]
        public void OpenReadsExactlyTheFilesTheVectorsSayItReads(string vector, bool reads)
        {
            var path = Path.Combine(_directory, "open.cc");
            File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(VectorRoot(), vector)));

            void Open() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }).Dispose();

            if (reads)
                Assert.DoesNotThrow(Open);
            else
                Assert.That(Open, Throws.TypeOf<CultStoreUnreadableException>().With.Property(nameof(CultStoreUnreadableException.Path)).EqualTo(path));
        }

        [TestCaseSource(nameof(Vectors))]
        public void AFlushReplacesAFileExactlyWhenItReads(string vector, bool reads)
        {
            var path = Seed("flush.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            var bytes = File.ReadAllBytes(Path.Combine(VectorRoot(), vector));
            File.WriteAllBytes(path, bytes);
            var store = cache.BackingStores[0];

            if (reads)
            {
                store.PushAll();
                // A whole-store flush writes what the store holds: one deck with no element ids is v1, whatever the file was.
                Assert.That(Header(path), Is.EqualTo("cultcache.store.v1"));
                return;
            }

            Assert.That(() => store.PushAll(), Throws.TypeOf<CultStoreUnreadableException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "a file this runtime cannot read was rewritten");
        }

        [TestCaseSource(nameof(Vectors))]
        public void AnUnconditionalCommitReplacesAFileExactlyWhenItReads(string vector, bool reads)
        {
            var path = Seed("commit.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            var bytes = File.ReadAllBytes(Path.Combine(VectorRoot(), vector));
            File.WriteAllBytes(path, bytes);

            if (reads)
            {
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));
                Assert.That(Header(path), Is.EqualTo("cultcache.store.v1"), "an unconditional commit writes the store's whole view, which holds no element id");
                return;
            }

            Assert.That(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))), Throws.TypeOf<CultStoreUnreadableException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "a file this runtime cannot read was rewritten");
        }

        private string DirectoryStore(string name)
        {
            var path = Path.Combine(_directory, name);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true });
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "d" }, new CultRecordKey("d")));
            return path;
        }

        // The directory store's refusal names the manifest it refused, as the single-file store names its file.
        [Test]
        public void AnUnreadableDirectoryManifestIsRefusedNamingIt()
        {
            var path = DirectoryStore("manifest.cc");
            File.WriteAllBytes(path, new byte[] { 0x01 });

            Assert.That(
                () => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }),
                Throws.TypeOf<CultStoreUnreadableException>().With.Property(nameof(CultStoreUnreadableException.Path)).EqualTo(path));
        }

        // A record kept under an id its catalog entry lists only as a compatible id keeps that entry through a rewrite: the manifest
        // the rewrite leaves is one this runtime reads.
        [Test]
        public void ADirectoryRewriteKeepsTheCatalogEntryThatPublishesAKeptRecordAsACompatibleId()
        {
            var path = DirectoryStore("compatible.cc");
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            manifest.Records.Single().SchemaId = "vectors.old.id";
            entry.SchemaId = "vectors.old.next";
            entry.CompatibleSchemaIds = new[] { "vectors.old.id" };
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));

            using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));

            var rewritten = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            Assert.That(rewritten.SchemaCatalog.Select(catalogEntry => catalogEntry.SchemaId), Does.Contain("vectors.old.next"));
            Assert.That(rewritten.Records.Select(record => record.SchemaId), Does.Contain("vectors.old.id"));
        }

        // An id one entry owns and a later entry lists as compatible names the entry that owns it.
        [Test]
        public void AnIdAnEntryOwnsNamesThatEntryNotOneThatListsItAsCompatible()
        {
            var path = Path.Combine(_directory, "own-id.cc");
            File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(VectorRoot(), "own-id-over-compatible-v3.bin")));

            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            Assert.That(cache.Get<VectorItem>(new CultRecordKey("alpha")), Is.Not.Null, "the record read as the schema that owns its id");
        }

        // The registry resolves a persisted id to the entry that owns it, not to one listed earlier that lists it as compatible.
        [Test]
        public void ThePersistedSchemaAnIdNamesIsTheEntryThatOwnsIt()
        {
            CultSchemaCatalogEntry Entry(string id, string name, params string[] compatible) => new()
            {
                SchemaId = id, SchemaName = name, SchemaVersion = name + ".v1", ContentHash = id, CompatibleSchemaIds = compatible
            };
            var catalog = new[]
            {
                Entry("x.lists", "tests.element_id_deck", "x.own"),
                Entry("x.own", "vectors.item", "x.own")
            };

            Assert.That(Registry.ResolvePersistedSchemaReport("x.own", catalog).LocalSchemaName, Is.EqualTo("vectors.item"));
        }

        // A directory rewrite where the entry that lists a kept record's id shares its own id with the schema being written keeps that
        // id in the entry it writes, so the manifest still reads.
        [Test]
        public void ADirectoryRewriteMergesEntriesThatShareAnIdAndKeepsEveryIdTheyList()
        {
            var path = DirectoryStore("merged.cc");
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            manifest.Records.Single().SchemaId = "vectors.old.id";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "vectors.old.id" };
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));

            using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));

            var rewritten = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            Assert.That(rewritten.SchemaCatalog.Single().CompatibleSchemaIds, Does.Contain("vectors.old.id"));
        }

        // Entries that share an id are merged whatever order they arrive in: every id any of them lists survives.
        [Test]
        public void EntriesThatShareAnIdMergeTheirCompatibleIdsInEitherOrder()
        {
            CultSchemaCatalogEntry Entry(params string[] compatible) => new()
            {
                SchemaId = "x", SchemaName = "n", SchemaVersion = "n.v1", ContentHash = "h", CompatibleSchemaIds = compatible
            };
            var plain = Entry("x");
            var wide = Entry("x", "y");

            foreach (var order in new[] { new[] { plain, wide }, new[] { wide, plain } })
            foreach (var preferLast in new[] { false, true })
            {
                var merged = CultSchemaCatalogEntry.MergeById(order, preferLast).Single();
                Assert.That(merged.CompatibleSchemaIds, Is.EquivalentTo(new[] { "x", "y" }));
            }
        }

        // A directory manifest in a format the directory store does not read is refused with the typed exception, naming it.
        [Test]
        public void ADirectoryManifestInAnotherFormatIsRefusedNamingIt()
        {
            var path = Path.Combine(_directory, "wrong-format.cc");
            File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(VectorRoot(), "..", "v3-base.msgpack")));

            Assert.That(
                () => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }),
                Throws.TypeOf<CultStoreUnreadableException>().With.Property(nameof(CultStoreUnreadableException.Path)).EqualTo(path));
        }

        // A commit that lands onto the file as it is keeps the header the file carries: a store already v3 stays v3.
        [TestCase("../v3-base.msgpack", "cultcache.store.v3")]
        [TestCase("compatible-id-only-v3.bin", "cultcache.store.v3")]
        [TestCase("zero-byte.bin", "cultcache.store.v1")]
        [TestCase("empty-array.bin", "cultcache.store.v1")]
        public void AConditionalCommitOntoAReadableFileKeepsItsHeader(string vector, string header)
        {
            var path = Seed("conditional.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(VectorRoot(), vector)));

            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("e"), null);
                batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"));
            }), Is.True);
            Assert.That(Header(path), Is.EqualTo(header));
        }

        // Every runtime's tests walk the manifest, so a vector without a row would go untested.
        [Test]
        public void EveryVectorInTheFolderHasAManifestRow()
        {
            var rows = Vectors().Select(row => (string)row.Arguments[0]).Where(name => !name.StartsWith("..", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal);
            var files = Directory.GetFiles(VectorRoot(), "*.bin").Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal);
            Assert.That(rows, Is.EqualTo(files));
        }

        // The refusal carries what the reader choked on.
        [Test]
        public void ARefusalKeepsItsCauseAsTheInnerException()
        {
            var error = Assert.Throws<CultStoreUnreadableException>(() =>
                CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(Path.Combine(VectorRoot(), "truncated.bin"))))!;
            Assert.That(error.InnerException, Is.Not.Null);
        }

        // The record type of the shared valid store (v3-base.msgpack: alpha and beta), so a cache can open it.
        [CultDocument("vectors.item", "vectors.item.v1")]
        [MessagePackObject]
        public sealed class VectorItem
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public int Count { get; set; }
        }
    }
}
