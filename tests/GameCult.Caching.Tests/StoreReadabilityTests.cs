#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            if (!Directory.Exists(_directory))
                return;
            // A recursive delete cannot remove a link whose target is gone; take the links out by themselves first.
            foreach (var entry in Directory.EnumerateFileSystemEntries(_directory).Where(entry => IsALink(entry)))
            {
                if ((new FileInfo(entry).Attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(entry, false);
                else
                    File.Delete(entry);
            }

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

        private static bool HoldsAVariant(byte[] bytes) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes).Records.Any(record => record.Variant != null);

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

            if (reads && HoldsAVariant(bytes))
            {
                // The cache has read nothing of this file, and a variant resolves against records it has not read.
                Assert.That(() => store.PushAll(), Throws.TypeOf<CultWriteConflictException>());
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "a write into a store holding a variant this cache never read changed it");
                return;
            }

            if (reads)
            {
                store.PushAll();
                // A flush applies its staged writes (none) to the file: it cannot show that a record it did not read holds no
                // element id, so the file keeps the header it carries.
                Assert.That(Header(path), Is.EqualTo(Header(Path.Combine(VectorRoot(), vector))));
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

            if (reads && HoldsAVariant(bytes))
            {
                Assert.That(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))), Throws.TypeOf<CultWriteConflictException>());
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "a write into a store holding a variant this cache never read changed it");
                return;
            }

            if (reads)
            {
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));
                Assert.That(Header(path), Is.EqualTo(Header(Path.Combine(VectorRoot(), vector))),
                    "the commit wrote a record without ids and copied the file's records, which it cannot show hold none");
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

        private static byte[] StoreWithHeader(string header) =>
            CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
            {
                FormatVersion = header,
                SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
                Records = Array.Empty<CultPersistedRecord>()
            });

        // A refusal shows a stored header only as cultcache.store.v followed by ASCII digits; any other header is described by its
        // length, so what the file says never reaches a log. A single-file store and a directory manifest are refused alike.
        [TestCase("cultcache.store.v9", true, false)]
        [TestCase("cultcache.store.SECRETHDR", false, false)]
        [TestCase("cultcache.store.v", false, false)]
        [TestCase("cultcache.store.v٣", false, false)]
        [TestCase("cultcache.store.v9", true, true)]
        [TestCase("cultcache.store.SECRETHDR", false, true)]
        [TestCase("cultcache.store.v", false, true)]
        [TestCase("cultcache.store.v٣", false, true)]
        public void ARefusalEchoesAHeaderOnlyAsStoreVersionDigits(string header, bool echoed, bool directoryStore)
        {
            var path = Path.Combine(_directory, "header.cc");
            File.WriteAllBytes(path, StoreWithHeader(header));

            var refusal = Assert.Throws<CultStoreUnreadableException>(() =>
                CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directoryStore }))!;

            // The readable formats are named in the same message, so "echoed" is read from how the header is introduced.
            Assert.That(refusal.Message.Contains(" is " + header + ";", StringComparison.Ordinal) || refusal.Message.Contains("format " + header + " is not readable", StringComparison.Ordinal), Is.EqualTo(echoed), refusal.Message);
            Assert.That(refusal.Message.Contains("an unrecognised cultcache.store.* header of ", StringComparison.Ordinal), Is.EqualTo(!echoed), refusal.Message);
            Assert.That(refusal.Message.Contains("SECRETHDR", StringComparison.Ordinal), Is.False, refusal.Message);
            if (!echoed)
                Assert.That(refusal.Message.Contains("٣", StringComparison.Ordinal), Is.False, refusal.Message);
        }

        // Only nothing at the path is an empty store. A link whose target is gone is an I/O error on open and on commit, never an
        // empty store and never an untyped missing-file or access failure, and the link is left where it was.
        [Test]
        public void ADanglingDirectoryLinkIsAnIoErrorOnOpenNotAnEmptyStore()
        {
            var path = Path.Combine(_directory, "dangling.cc");
            CreateDanglingDirectoryLink(path, Path.Combine(_directory, "unmounted"));

            Assert.That(() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }), Throws.TypeOf<IOException>());
            Assert.That(IsALink(path), "the link was replaced");
            Assert.That(Directory.Exists(Path.Combine(_directory, "unmounted")), Is.False);
        }

        [Test]
        public void ACommitOntoADanglingDirectoryLinkIsAnIoErrorAndLeavesTheLink()
        {
            var path = Path.Combine(_directory, "link.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            CreateDanglingDirectoryLink(path, Path.Combine(_directory, "unmounted"));

            Assert.That(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))), Throws.TypeOf<IOException>());
            Assert.That(IsALink(path), "the link was replaced");
            Assert.That(Directory.Exists(Path.Combine(_directory, "unmounted")), Is.False, "a write went through the link");
        }

        [Test]
        public void ADanglingFileLinkIsAnIoErrorOnOpenNotAnEmptyStore()
        {
            var path = Path.Combine(_directory, "dangling-file.cc");
            CreateFileLinkOrIgnore(path, Path.Combine(_directory, "unmounted", "store.cc"));

            Assert.That(() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }), Throws.TypeOf<IOException>());
            Assert.That(new FileInfo(path).LinkTarget, Is.Not.Null, "the link was replaced");
            Assert.That(Directory.Exists(Path.Combine(_directory, "unmounted")), Is.False);
        }

        [Test]
        public void ACommitOntoAFileLinkThatDanglesAfterOpenIsAnIoErrorAndLeavesTheLink()
        {
            var target = Path.Combine(_directory, "target.cc");
            var path = Path.Combine(_directory, "file-link.cc");
            File.WriteAllBytes(target, File.ReadAllBytes(Path.Combine(VectorRoot(), "empty-array.bin")));
            CreateFileLinkOrIgnore(path, target);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            File.Delete(target);

            Assert.That(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))), Throws.TypeOf<IOException>());
            Assert.That(new FileInfo(path).LinkTarget, Is.Not.Null, "the link was replaced");
            Assert.That(File.Exists(target), Is.False, "a write went through the link");
        }

        // A directory at the path is something at the path: it is an I/O error on open and on commit, never an empty store, and
        // it is left as it was.
        [Test]
        public void ADirectoryAtTheStorePathIsAnIoErrorOnOpenAndOnCommitNotAnEmptyStore()
        {
            var path = Path.Combine(_directory, "directory.cc");
            Directory.CreateDirectory(path);

            Assert.That(() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }),
                Throws.TypeOf<IOException>().With.Message.Contains("directory"));

            var later = Path.Combine(_directory, "later.cc");
            using var cache = CultCacheMessagePack.Create(later, new CultCacheOpenOptions { Registry = Registry });
            Directory.CreateDirectory(later);
            Assert.That(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))),
                Throws.TypeOf<IOException>().With.Message.Contains("directory"));

            Assert.That(Directory.GetFileSystemEntries(path), Is.Empty);
            Assert.That(Directory.GetFileSystemEntries(later), Is.Empty);
        }

        // A link whose target is gone. A directory junction needs no privilege on Windows, where a file symlink needs one this
        // workstation lacks; elsewhere a symlink to a missing directory is the same thing.
        private static void CreateDanglingDirectoryLink(string path, string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(path, target);
                return;
            }

            Directory.CreateDirectory(target);
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit();
            Assert.That(mklink.ExitCode, Is.Zero, "mklink /J failed");
            Directory.Delete(target);
        }

        private static bool IsALink(string path)
        {
            var attributes = new FileInfo(path).Attributes;
            return (int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static void CreateFileLinkOrIgnore(string path, string target)
        {
            try
            {
                File.CreateSymbolicLink(path, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Ignore("A file symlink needs a privilege this account lacks: " + ex.Message);
            }
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

            // A registered entry publishes only the ids it owns: the caller passes it for the ids its write stages, and an id nothing
            // staged sits under is published by the arrived entry that lists it, or by none.
            var registeredLister = Entry("r", "registered.lister", "h3", "r", "z");
            var arrivedLister = Entry("a", "arrived.lister", "h4", "a", "z");
            var derivedByArrived = CultSchemaCatalogEntry.Derive(new[] { Rec("a", "z") }, new[] { registeredLister }, new[] { arrivedLister }).Single();
            Assert.That(derivedByArrived.SchemaId, Is.EqualTo("a"), "a registered entry that lists the id does not publish it");
            Assert.Throws<CultSchemaConflictException>(() =>
                CultSchemaCatalogEntry.Derive(new[] { Rec("a", "z") }, new[] { registeredLister }, Array.Empty<CultSchemaCatalogEntry>()));
        }

        // Entries of one tier that tie are taken in one fixed order: the catalog does not depend on the order they arrive in.
        [Test]
        public void EntriesThatTieAreTakenInOneFixedOrder()
        {
            // The content hash orders them against the order their compatible ids would give: h1 sorts first, its list sorts last.
            var one = Entry("x", "n", "h2", "x");
            var two = Entry("x", "n", "h1", "x", "y");
            foreach (var order in new[] { new[] { one, two }, new[] { two, one } })
            {
                Assert.That(CultSchemaCatalogEntry.Derive(new[] { Rec("a", "x") }, order, Array.Empty<CultSchemaCatalogEntry>()).Single().ContentHash, Is.EqualTo("h1"));
                Assert.That(CultSchemaCatalogEntry.Derive(new[] { Rec("a", "x") }, Array.Empty<CultSchemaCatalogEntry>(), order).Single().ContentHash, Is.EqualTo("h1"));
            }
        }

        // A registered descriptor that owns an id keeps it against an arrived entry that owns it too and lists the id another
        // record sits under, whichever of the two ids sorts first: the arrived entry's list is not merged in, so the record under
        // the listed id is published by no chosen entry.
        [TestCase("a", "b")]
        [TestCase("b", "a")]
        public void AnArrivedEntryThatOwnsARegisteredIdDoesNotReplaceTheDescriptorNorPublishWhatItLists(string ownId, string listedId)
        {
            var registered = new[] { Entry(ownId, "registered", "fresh", ownId) };
            var arrived = new[] { Entry(ownId, "arrived", "stale", ownId, listedId) };
            var records = new[] { Rec("k1", ownId), Rec("k2", listedId) };

            var refusal = Assert.Throws<CultSchemaConflictException>(() => CultSchemaCatalogEntry.Derive(records, registered, arrived))!;
            Assert.That(refusal.SchemaId, Is.EqualTo(listedId));
            Assert.That(refusal.RecordKey, Is.EqualTo("k2"));
        }

        // Ids are taken in sorted order, so which entry survives, and so which id the refusal names, does not depend on the order
        // the records arrive in.
        [Test]
        public void TheRefusalNamesTheSameIdWhateverOrderTheRecordsArriveIn()
        {
            var arrived = new[] { Entry("x", "n", "h1", "x", "y"), Entry("x", "n", "h2", "x", "z") };
            foreach (var records in new[] { new[] { Rec("r1", "y"), Rec("r2", "z") }, new[] { Rec("r2", "z"), Rec("r1", "y") } })
            {
                var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                    CultSchemaCatalogEntry.Derive(records, Array.Empty<CultSchemaCatalogEntry>(), arrived))!;
                Assert.That(refusal.SchemaId, Is.EqualTo("y"));
                Assert.That(refusal.RecordKey, Is.EqualTo("r1"));
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

        // A file entry that names an older schema under the registered schema's id, with a record kept under an older id it lists.
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

        // The cache holds the record d, but a commit onto the file writes only its batch: d stays as the file holds it, under an id
        // only the file's entry lists, and the registered descriptor does not. No store writer restamps a record; the write is
        // refused and the file left as it was.
        [Test]
        public void ACommitOntoAFileWhoseCachedRecordSitsUnderAnIdOnlyAnArrivedEntryListsIsRefusedAndTheFileLeftAsItWas()
        {
            var path = Seed("legacy-conditional.cc");
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            manifest.Records.Single().SchemaId = "old.id";
            entry.ContentHash = "stale";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "old.id" };
            var bytes = CultDocumentMessagePackSerialization.SerializeSnapshot(manifest);
            File.WriteAllBytes(path, bytes);

            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null, "the cache reads the record under the older id");
            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("e"), null);
                batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"));
            }))!;
            Assert.That(refusal.SchemaId, Is.EqualTo("old.id"));
            Assert.That(refusal.RecordKey, Is.EqualTo("d"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        // Another writer rewrites the record d under a different id, keeping its storedAt. A cache that read d earlier commits a
        // record of its own onto the file: the other writer's d is not the cache's to rewrite, so it is left exactly as written.
        [Test]
        public void ACommitNeverRewritesARecordAnotherWriterChangedThatKeptItsStoredAt()
        {
            var path = Seed("lost-update.cc");
            var other = Seed("lost-update-other.cc");
            using (var cache = CultCacheMessagePack.Create(other, new CultCacheOpenOptions { Registry = Registry }))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "changed-by-other" }, new CultRecordKey("d")));
            var otherPayload = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(other)).Records.Single().Payload;

            using var reader = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            Assert.That(reader.Get<IdDeck>(new CultRecordKey("d"))!.Name, Is.EqualTo("d"));

            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            var record = manifest.Records.Single();
            record.SchemaId = "other.writer.id";
            record.Payload = otherPayload;
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "other.writer.id" };
            var bytes = CultDocumentMessagePackSerialization.SerializeSnapshot(manifest);
            File.WriteAllBytes(path, bytes);

            Assert.Throws<CultSchemaConflictException>(() => reader.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("e"), null);
                batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"));
            }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "the other writer's record is on disk as it wrote it");
        }

        // One rule for both commits: an unconditional commit onto the file copies the record under the older id as it is, and is
        // refused for the same reason a conditional one is.
        [Test]
        public void AnUnconditionalCommitOntoAFileWhoseRecordSitsUnderAnIdOnlyAnArrivedEntryListsIsRefusedAndTheFileLeftAsItWas()
        {
            var path = LegacyFile("legacy-unconditional.cc");
            var bytes = File.ReadAllBytes(path);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))))!;
            Assert.That(refusal.SchemaId, Is.EqualTo("old.id"));
            Assert.That(refusal.RecordKey, Is.EqualTo("d"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
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

        // The directory store derives its manifest the same way and restamps nothing: a record kept under an older id only the
        // manifest's entry lists is refused, and the manifest is left as it was.
        [Test]
        public void ADirectoryRewriteWhoseKeptRecordSitsUnderAnIdOnlyTheManifestEntryListsIsRefusedAndTheManifestLeftAsItWas()
        {
            var path = DirectoryStore("legacy-dir.cc");
            var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = manifest.SchemaCatalog.Single();
            entry.ContentHash = "stale";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, "old.id" };
            manifest.Records.Single().SchemaId = "old.id";
            var bytes = CultDocumentMessagePackSerialization.SerializeSnapshot(manifest);
            File.WriteAllBytes(path, bytes);

            using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }))
            {
                var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                    cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))))!;
                Assert.That(refusal.SchemaId, Is.EqualTo("old.id"));
                Assert.That(refusal.RecordKey, Is.EqualTo("d"));
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
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

        // A record's version is the SHA-256 of the bytes it is stored as: in a single file the record's own slice of the file, read
        // here with the MessagePack reader and not the serializer; in a directory the hash the manifest indexes its page under.
        [Test]
        public void AVersionIsTheSha256OfTheRecordsStoredBytes([Values] bool directory)
        {
            var source = File.ReadAllBytes(Path.Combine(VectorRoot(), "..", "v3-base.msgpack"));
            var slices = new Dictionary<string, string>();
            var reader = new MessagePackReader(source);
            reader.ReadArrayHeader();
            reader.Skip();
            reader.Skip();
            var count = reader.ReadArrayHeader();
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(source);
            for (var i = 0; i < count; i++)
            {
                var start = (int)reader.Consumed;
                reader.Skip();
                slices[snapshot.Records[i].Key] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source.AsSpan(start, (int)reader.Consumed - start))).ToLowerInvariant();
            }

            var path = Path.Combine(_directory, "version.cc");
            File.WriteAllBytes(path, source);
            if (directory)
            {
                // The same records, written as a directory store: its versions are the manifest's page hashes.
                File.Delete(path);
                using (var seed = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true }))
                    seed.Commit(batch =>
                    {
                        foreach (var key in slices.Keys)
                            batch.Upsert(typeof(VectorItem), new VectorItem { Name = key }, new CultRecordKey(key));
                    });
                slices = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records
                    .ToDictionary(record => record.Key, record => Convert.ToHexString(record.Payload).ToLowerInvariant());
            }

            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directory });

            Assert.That(cache.AllStoredDocuments.ToDictionary(stored => stored.Key.Value, stored => stored.StoredVersion), Is.EqualTo(slices));
            Assert.That(slices, Is.Not.Empty);
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
