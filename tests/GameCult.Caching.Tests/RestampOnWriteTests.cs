#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // variants:ruling:drift-record-write-ownership: a writer owns a record stored under id X when it declares X, or when the type
    // that reads X reads it whole; it then writes, relabels and removes it, and a write stores it under the type's own id. A record
    // a type reads only by dropping persisted slots stays read-only until its id is declared compatible.
    public class RestampOnWriteTests
    {
        // One schema name at three versions of its type and nothing declared: Grown reads Narrow's records whole (it adds a slot),
        // and reads Wide's records only by dropping slot 3.
        [CultDocument("tests.restamp_note", "tests.restamp_note.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteNarrow
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        [CultDocument("tests.restamp_note", "tests.restamp_note.v2")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteGrown
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
            [Key(2)] public string Colour = string.Empty;
        }

        [CultDocument("tests.restamp_note", "tests.restamp_note.v3")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteWide
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
            [Key(2)] public string Colour = string.Empty;
            [Key(3)] public string Extra = string.Empty;
        }

        // Grown with the lossy id declared: declaration is the consent to the loss.
        [CultDocument("tests.restamp_note", "tests.restamp_note.v4", CompatibleSchemaIds = new[] { WideId })]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteGrownDeclaring
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
            [Key(2)] public string Colour = string.Empty;
        }

        private const string WideId = "sha256:82c2b721aceb56e00288baab590fabcdd82c3bda44400f7530fa9e29c4e96252";

        private static readonly CultDocumentRegistry NarrowRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteNarrow) });
        private static readonly CultDocumentRegistry GrownRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteGrown) });
        private static readonly CultDocumentRegistry WideRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteWide) });
        private static readonly CultDocumentRegistry DeclaringRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteGrownDeclaring) });
        private static readonly CultRecordKey Note = new("note");
        private static readonly CultRecordKey Other = new("other");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-restamp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        public static readonly object[] StoreKinds = { false, true };

        private CacheBackingStore Store(string path, bool directory) =>
            directory ? new DirectoryMessagePackBackingStore(path) : new SingleFileMessagePackBackingStore(path);

        private CultCache Open(string path, bool directory, CultDocumentRegistry registry)
        {
            var cache = new CultCache(registry);
            cache.AddBackingStore(Store(path, directory));
            return cache;
        }

        // The schema id each key is stored under, read back through a fresh cache that declares everything it sees.
        private string StoredIdOf(string path, bool directory, CultDocumentRegistry registry, CultRecordKey key)
        {
            using var cache = Open(path, directory, registry);
            return cache.GetStored(key)!.StoredSchemaId;
        }

        private static string Fingerprint(string path)
        {
            using var sha = SHA256.Create();
            if (File.Exists(path))
                return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
            var parts = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file))));
            return string.Join("|", parts);
        }

        // Two records written by a writer that knew Narrow: the one under test and a bystander.
        private string SeedNarrow(bool directory)
        {
            var path = Path.Combine(_directory, directory ? "store" : "store.cc");
            using var cache = Open(path, directory, NarrowRegistry);
            Assert.That(cache.Commit(batch =>
            {
                batch.Upsert(new NoteNarrow { Name = "note", Value = 1 }, new CultRecordHandle<NoteNarrow>(Note));
                batch.Upsert(new NoteNarrow { Name = "other", Value = 7 }, new CultRecordHandle<NoteNarrow>(Other));
            }), Is.True);
            return path;
        }

        private string SeedWide(bool directory)
        {
            var path = Path.Combine(_directory, directory ? "store" : "store.cc");
            using var cache = Open(path, directory, WideRegistry);
            Assert.That(cache.Commit(batch => batch.Upsert(new NoteWide { Name = "note", Value = 1, Extra = "kept?" }, new CultRecordHandle<NoteWide>(Note))), Is.True);
            return path;
        }

        [TestCaseSource(nameof(StoreKinds))]
        public void ADriftRecordReadWholeIsWrittenUnderItsTypesId(bool directory)
        {
            var path = SeedNarrow(directory);
            var narrowId = NarrowRegistry.GetRequired<NoteNarrow>().SchemaId;
            var grownId = GrownRegistry.GetRequired<NoteGrown>().SchemaId;
            Assert.That(StoredIdOf(path, directory, NarrowRegistry, Note), Is.EqualTo(narrowId));

            using (var cache = Open(path, directory, GrownRegistry))
            {
                Assert.That(cache.Get<NoteGrown>(Note)!.Value, Is.EqualTo(1));
                Assert.That(cache.Commit(batch => batch.Upsert(new NoteGrown { Name = "note", Value = 2, Colour = "red" }, new CultRecordHandle<NoteGrown>(Note))), Is.True);
            }

            Assert.That(StoredIdOf(path, directory, GrownRegistry, Note), Is.EqualTo(grownId), "the written record moved to its type's id");
            Assert.That(StoredIdOf(path, directory, NarrowRegistry, Other), Is.EqualTo(narrowId), "the bystander kept its persisted id (write set)");
            using var reopened = Open(path, directory, GrownRegistry);
            Assert.That(reopened.Get<NoteGrown>(Note)!.Colour, Is.EqualTo("red"));
            Assert.That(reopened.Get<NoteGrown>(Other)!.Value, Is.EqualTo(7));
        }

        [TestCaseSource(nameof(StoreKinds))]
        public void RemovingADriftRecordReadWholeSucceeds(bool directory)
        {
            var path = SeedNarrow(directory);
            using (var cache = Open(path, directory, GrownRegistry))
                Assert.That(cache.Commit(batch => batch.Remove(Note)), Is.True);

            using var reopened = Open(path, directory, GrownRegistry);
            Assert.That(reopened.Get<NoteGrown>(Note), Is.Null);
            Assert.That(reopened.Get<NoteGrown>(Other), Is.Not.Null);
        }

        [TestCaseSource(nameof(StoreKinds))]
        public void ADriftRecordReadOnlyByDroppingASlotStaysReadOnly(bool directory)
        {
            var path = SeedWide(directory);
            var before = Fingerprint(path);

            using var cache = Open(path, directory, GrownRegistry);
            Assert.That(cache.Get<NoteGrown>(Note), Is.Not.Null, "a read-only record is still served");
            var upsert = Assert.Throws<CultSchemaConflictException>(() =>
                cache.Commit(batch => batch.Upsert(new NoteGrown { Name = "note", Value = 2 }, new CultRecordHandle<NoteGrown>(Note))))!;
            Assert.That(upsert.RecordKey, Is.EqualTo("note"));
            Assert.That(upsert.SchemaId, Is.EqualTo(WideRegistry.GetRequired<NoteWide>().SchemaId));
            Assert.That(upsert.Message, Does.Contain("would drop persisted slots").And.Contain("to accept the loss."), "the refusal names the narrowed rule");
            var remove = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Remove(Note)))!;
            Assert.That(remove.RecordKey, Is.EqualTo("note"));
            Assert.That(Fingerprint(path), Is.EqualTo(before), "a refused write leaves the store as it was");
        }

        [TestCaseSource(nameof(StoreKinds))]
        public void ADeclaredLossyDriftRecordIsWritable(bool directory)
        {
            var path = SeedWide(directory);
            var wideId = WideRegistry.GetRequired<NoteWide>().SchemaId;
            Assert.That(wideId, Is.EqualTo(WideId), "the declaration names the lossy record's id: " + wideId);

            using (var cache = Open(path, directory, DeclaringRegistry))
                Assert.That(cache.Commit(batch => batch.Upsert(new NoteGrownDeclaring { Name = "note", Value = 2 }, new CultRecordHandle<NoteGrownDeclaring>(Note))), Is.True);

            Assert.That(StoredIdOf(path, directory, DeclaringRegistry, Note), Is.EqualTo(DeclaringRegistry.GetRequired<NoteGrownDeclaring>().SchemaId));
        }

        // Another writer rewrites the record after this cache loaded it: ownership is judged again under the store's lock.
        [TestCaseSource(nameof(StoreKinds))]
        public void AnotherWritersDriftRecordIsJudgedUnderTheLock(bool directory)
        {
            var path = SeedNarrow(directory);
            using var cache = Open(path, directory, GrownRegistry);
            Assert.That(cache.Get<NoteGrown>(Note), Is.Not.Null);

            // A writer that knows Wide replaces the record: this cache's loaded copy was read whole, the durable one is not.
            using (var other = Open(path, directory, WideRegistry))
            {
                Assert.That(other.Commit(batch => batch.Upsert(new NoteWide { Name = "note", Value = 3, Extra = "x" }, new CultRecordHandle<NoteWide>(Note))), Is.True);
            }

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                cache.Commit(batch => batch.Upsert(new NoteGrown { Name = "note", Value = 4 }, new CultRecordHandle<NoteGrown>(Note))))!;
            Assert.That(refusal.RecordKey, Is.EqualTo("note"));
            Assert.That(StoredIdOf(path, directory, WideRegistry, Note), Is.EqualTo(WideRegistry.GetRequired<NoteWide>().SchemaId));
        }

        [Test]
        public void ACachelessWriteRestampsADriftRecordOfItsSchema()
        {
            var path = SeedNarrow(directory: false);
            new SingleFileMessagePackBackingStore(path).WriteDocument(Note, new NoteGrown { Name = "note", Value = 2 }, registry: GrownRegistry);

            Assert.That(StoredIdOf(path, false, GrownRegistry, Note), Is.EqualTo(GrownRegistry.GetRequired<NoteGrown>().SchemaId));
            Assert.That(StoredIdOf(path, false, NarrowRegistry, Other), Is.EqualTo(NarrowRegistry.GetRequired<NoteNarrow>().SchemaId));
        }

        [Test]
        public void ACachelessWriteOverALossyDriftRecordIsRefusedAndTheFileIsUntouched()
        {
            var path = SeedWide(directory: false);
            var before = Fingerprint(path);
            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                new SingleFileMessagePackBackingStore(path).WriteDocument(Note, new NoteGrown { Name = "note", Value = 2 }, registry: GrownRegistry))!;
            Assert.That(refusal.Message, Does.Contain("reads it whole").And.Contain("to accept the loss of its unread slots."), "the refusal names the narrowed rule");
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // A cacheless writer's entry that lists the record's id compatible consents to the loss, as a cache's declaration does.
        [Test]
        public void ACachelessWriteByAnEntryListingTheIdCompatibleOverwritesALossyDriftRecord()
        {
            var path = SeedWide(directory: false);
            new SingleFileMessagePackBackingStore(path).WriteDocument(Note, new NoteGrownDeclaring { Name = "note", Value = 2 }, registry: DeclaringRegistry);

            Assert.That(StoredIdOf(path, false, DeclaringRegistry, Note), Is.EqualTo(DeclaringRegistry.GetRequired<NoteGrownDeclaring>().SchemaId));
        }

        // The type that would read the id cannot be named (two registered versions share the schema name and the id declares neither),
        // so the record is not owned and a write over it is refused typed, not as the resolver's own error.
        [TestCaseSource(nameof(StoreKinds))]
        public void ARecordNoRegisteredTypeCanBeNamedForIsNotOwned(bool directory)
        {
            var path = SeedNarrow(directory);
            var both = CultDocumentRegistry.ForTypes(new[] { typeof(NoteNarrow), typeof(NoteGrown) });
            using var cache = Open(path, directory, both);
            using (var other = Open(path, directory, WideRegistry))
                Assert.That(other.Commit(batch => batch.Upsert(new NoteWide { Name = "note", Value = 3 }, new CultRecordHandle<NoteWide>(Note))), Is.True);

            Assert.Throws<CultSchemaConflictException>(() =>
                cache.Commit(batch => batch.Upsert(new NoteGrown { Name = "note", Value = 4 }, new CultRecordHandle<NoteGrown>(Note))));
        }

        [TestCaseSource(nameof(StoreKinds))]
        public void ExpectAgreesWithRestamp(bool directory)
        {
            var path = SeedNarrow(directory);
            var third = new CultRecordKey("third");
            using var cache = Open(path, directory, GrownRegistry);

            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(Note, cache.Get(Note));
                batch.ExpectUnchanged();
                batch.Upsert(new NoteGrown { Name = "third" }, new CultRecordHandle<NoteGrown>(third));
            }), Is.True, "an unchanged drift record satisfies Expect and ExpectUnchanged");

            using var second = Open(path, directory, GrownRegistry);
            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(Note, cache.Get(Note));
                batch.Upsert(new NoteGrown { Name = "note", Value = 2 }, new CultRecordHandle<NoteGrown>(Note));
            }), Is.True, "a batch that Expects the drift record and upserts it commits");
            Assert.That(StoredIdOf(path, directory, GrownRegistry, Note), Is.EqualTo(GrownRegistry.GetRequired<NoteGrown>().SchemaId), "and restamps it");

            Assert.That(cache.Commit(batch =>
            {
                batch.ExpectUnchanged();
                batch.Upsert(new NoteGrown { Name = "fourth" }, new CultRecordHandle<NoteGrown>(new CultRecordKey("fourth")));
            }), Is.True, "the writer's later ExpectUnchanged holds");
            Assert.That(second.Commit(batch =>
            {
                batch.Expect(Note, second.Get(Note));
                batch.Upsert(new NoteGrown { Name = "note", Value = 3 }, new CultRecordHandle<NoteGrown>(Note));
            }), Is.False, "a cache loaded before the restamp fails its Expect: the bytes changed");
        }

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = Path.Combine(dir.FullName, "tests", "vectors", "ref-list-drift");
                if (Directory.Exists(vectors)) return Path.Combine(vectors, name);
            }

            throw new DirectoryNotFoundException($"tests/vectors/ref-list-drift not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        // The store the 1.0.60 writer wrote: its 'list' holder compares compatible with RefListHolder, gaining reference semantics
        // on a slot, and ignores no persisted slot, so RefListHolder reads it whole and may write it.
        [Test]
        public async Task TheCommitted1060VectorsListRecordIsWrittenUnderTheHoldersOwnId()
        {
            var path = Path.Combine(_directory, "vector.cc");
            File.Copy(VectorPath("v1-0-60-written.cc"), path);
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RefListDriftTests.RefListTarget), typeof(RefListDriftTests.RefListHolder) });
            var holderId = registry.GetRequired<RefListDriftTests.RefListHolder>().SchemaId;
            var before = Snapshot(path);
            Assert.That(before.Records.Single(record => record.Key == "list").SchemaId, Is.Not.EqualTo(holderId), "the vector's list is under an older id");

            using (var cache = new CultCache(registry))
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await cache.PullAllBackingStoresAsync();
                var holder = cache.GetAll<RefListDriftTests.RefListHolder>().Single();
                Assert.That(cache.Commit(batch => batch.Upsert(
                    new RefListDriftTests.RefListHolder { Name = holder.Name, Items = holder.Items.Reverse().ToArray() },
                    new CultRecordHandle<RefListDriftTests.RefListHolder>(new CultRecordKey("list")))), Is.True);
            }

            var after = Snapshot(path);
            Assert.That(after.Records.Single(record => record.Key == "list").SchemaId, Is.EqualTo(holderId));
            Assert.That(after.SchemaCatalog.Single(entry => entry.SchemaId == holderId).Members.Single(member => member.Slot == 1).IsReference, Is.True);
            foreach (var kept in new[] { "alpha", "beta" })
            {
                Assert.That(after.Records.Single(record => record.Key == kept).SchemaId, Is.EqualTo(before.Records.Single(record => record.Key == kept).SchemaId), kept + " keeps its persisted id");
                Assert.That(after.Records.Single(record => record.Key == kept).Payload, Is.EqualTo(before.Records.Single(record => record.Key == kept).Payload), kept + " keeps its bytes");
            }

            using var reopened = new CultCache(registry);
            reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            await reopened.PullAllBackingStoresAsync();
            Assert.That(reopened.GetAll<RefListDriftTests.RefListHolder>().Single().Items.Select(item => item.Key.Value).ToArray(), Is.EqualTo(new[] { "beta", "alpha" }));
        }

        private static CultPersistedStoreSnapshot Snapshot(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
    }
}
