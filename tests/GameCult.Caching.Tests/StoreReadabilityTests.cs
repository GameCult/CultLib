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

        // An id one entry owns and a later entry lists as compatible names the entry that owns it.
        [TestCase("own-id-over-compatible-v3.bin")]
        [TestCase("compatible-before-owner-v3.bin")]
        public void AnIdAnEntryOwnsNamesThatEntryNotOneThatListsItAsCompatible(string vector)
        {
            var path = Path.Combine(_directory, "own-id.cc");
            File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(VectorRoot(), vector)));

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

        // The catalog a write leaves is derived from its records: one entry per carried id, the entry that owns the id (a registered
        // descriptor over an arrived entry), else one that lists it as compatible, written as chosen.
        private static CultSchemaCatalogEntry Entry(string id, string name, string hash, params string[] compatible) => new()
        {
            SchemaId = id, SchemaName = name, SchemaVersion = name + ".v1", ContentHash = hash, CompatibleSchemaIds = compatible
        };

        private static CultPersistedRecord Rec(string key, string schemaId) => new() { Key = key, SchemaId = schemaId, StoredAt = "t" };

        [Test]
        public void ARegisteredDescriptorWinsOverAnArrivedEntryWithTheSameOwnId()
        {
            var registered = new[] { Entry("x", "new.name", "fresh", "x") };
            var arrived = new[] { Entry("x", "old.name", "stale", "x", "old") };

            var derived = CultSchemaCatalogEntry.Derive(new[] { Rec("a", "x") }, registered, arrived).Single();
            Assert.That(derived.SchemaName, Is.EqualTo("new.name"));
            Assert.That(derived.ContentHash, Is.EqualTo("fresh"));
            Assert.That(derived.CompatibleSchemaIds, Is.EqualTo(new[] { "x" }), "written as chosen: no union with the arrived entry's ids");
        }

        [Test]
        public void AnEntryThatOwnsAnIdIsChosenOverOneThatListsItAsCompatibleInEveryOrder()
        {
            var owner = Entry("y", "owner", "h1", "y");
            var lister = Entry("x", "lister", "h2", "x", "y");
            foreach (var arrived in new[] { new[] { owner, lister }, new[] { lister, owner } })
            {
                var derived = CultSchemaCatalogEntry.Derive(new[] { Rec("a", "y") }, Array.Empty<CultSchemaCatalogEntry>(), arrived);
                Assert.That(derived.Select(entry => entry.SchemaId), Is.EqualTo(new[] { "y" }), "an entry no record needs is not written");
            }

            var registeredLister = Entry("r", "registered.lister", "h3", "r", "z");
            var arrivedLister = Entry("a", "arrived.lister", "h4", "a", "z");
            foreach (var arrived in new[] { new[] { arrivedLister }, Array.Empty<CultSchemaCatalogEntry>() })
            {
                var derived = CultSchemaCatalogEntry.Derive(new[] { Rec("a", "z") }, new[] { registeredLister }, arrived).Single();
                Assert.That(derived.SchemaId, Is.EqualTo("r"), "with no owner, a registered entry that lists the id is chosen over an arrived one");
            }
        }

        [Test]
        public void TwoArrivedEntriesThatShareAnOwnIdAndDisagreeOnTheSchemaNameRefuseTheWrite()
        {
            var arrived = new[] { Entry("x", "first", "h1", "x"), Entry("x", "second", "h2", "x") };

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                CultSchemaCatalogEntry.Derive(new[] { Rec("b", "x"), Rec("a", "x") }, Array.Empty<CultSchemaCatalogEntry>(), arrived))!;
            Assert.That(refusal.SchemaId, Is.EqualTo("x"));
            Assert.That(refusal.SchemaNames, Is.EquivalentTo(new[] { "first", "second" }));
            Assert.That(refusal.RecordKey, Is.EqualTo("a"));

            // A registered descriptor that owns the id settles the disagreement.
            Assert.DoesNotThrow(() => CultSchemaCatalogEntry.Derive(new[] { Rec("a", "x") }, new[] { Entry("x", "first", "h3", "x") }, arrived));
        }

        [Test]
        public void ARecordNoChosenEntryPublishesRefusesTheWrite()
        {
            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                CultSchemaCatalogEntry.Derive(new[] { Rec("a", "orphan") }, new[] { Entry("x", "n", "h", "x") }, Array.Empty<CultSchemaCatalogEntry>()))!;
            Assert.That(refusal.SchemaId, Is.EqualTo("orphan"));
            Assert.That(refusal.RecordKey, Is.EqualTo("a"));
        }

        // A file entry that names an older schema under the registered schema's id, with a record kept under an older id it lists: a
        // commit onto the file, or a whole-view flush, leaves a store that reopens with the record readable and the registered entry.
        private string LegacyFile(string name)
        {
            var path = Seed(name);
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            entry.ContentHash = "stale";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "old.id" };
            manifest.Records.Single().SchemaId = "old.id";
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));
            return path;
        }

        [Test]
        public void ACommitOntoAFileWhoseEntryListsAnOlderIdLeavesAStoreThatReopens()
        {
            var path = Seed("legacy-conditional.cc");
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            manifest.Records.Single().SchemaId = "old.id";
            entry.ContentHash = "stale";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "old.id" };
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));

            using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }))
            {
                Assert.That(cache.Commit(batch =>
                {
                    batch.Expect(new CultRecordKey("e"), null);
                    batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"));
                }), Is.True);
            }

            var written = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            Assert.That(written.SchemaCatalog.Single().ContentHash, Is.Not.EqualTo("stale"));
            using var reopened = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            Assert.That(reopened.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null, "the record kept under the older id stays readable");
        }

        [Test]
        public void AWholeViewFlushAfterARenameWithAStableIdWritesTheRegisteredEntry()
        {
            var path = LegacyFile("legacy-flush.cc");
            using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));

            var written = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            Assert.That(written.SchemaCatalog.Single().ContentHash, Is.Not.EqualTo("stale"));
            using var reopened = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            Assert.That(reopened.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null);
        }

        // A record the cache does not hold, under an id only an arrived entry publishes while a registered descriptor owns that
        // entry's own id, cannot be written readable: the write is refused and the file is left as it was.
        [Test]
        public void ARecordOnlyAnOverriddenEntryPublishesRefusesTheWriteAndLeavesTheFile()
        {
            var path = Seed("legacy-orphan.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            entry.SchemaName = "vectors.legacy";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "old.id" };
            var existing = manifest.Records.Single();
            manifest.Records = new[] { existing, new CultPersistedRecord { Key = "z", SchemaId = "old.id", StoredAt = "t", Payload = existing.Payload } };
            var bytes = CultDocumentMessagePackSerialization.SerializeSnapshot(manifest);
            File.WriteAllBytes(path, bytes);

            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("e"), null);
                batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"));
            }))!;
            Assert.That(refusal.SchemaId, Is.EqualTo("old.id"));
            Assert.That(refusal.RecordKey, Is.EqualTo("z"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        // The directory store derives its manifest the same way: a record kept under an older id, the entry naming an older schema,
        // and a commit leaves a manifest that reads, with the registered entry.
        [Test]
        public void ADirectoryRewriteWhoseEntryListsAnOlderIdLeavesAManifestThatReads()
        {
            var path = DirectoryStore("legacy-dir.cc");
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            entry.ContentHash = "stale";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "old.id" };
            manifest.Records.Single().SchemaId = "old.id";
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));

            using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));

            var rewritten = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            Assert.That(rewritten.SchemaCatalog.Single().ContentHash, Is.Not.EqualTo("stale"));
            using var reopened = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true });
            Assert.That(reopened.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null);
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
