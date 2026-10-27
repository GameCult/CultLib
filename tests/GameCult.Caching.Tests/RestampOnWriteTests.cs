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

        // Another schema with Narrow's exact shape: never the same type, whatever the members say.
        [CultDocument("tests.restamp_other", "tests.restamp_other.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteOther
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        // Reads a Narrow-shaped entry only by dropping Value.
        [CultDocument("tests.restamp_short", "tests.restamp_short.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteShort
        {
            [Key(0)] [CultName] public string Name = string.Empty;
        }

        // Another schema with Narrow's shape that lists an old id: it claims records stored under that id.
        [CultDocument("tests.restamp_lister", "tests.restamp_lister.v1", CompatibleSchemaIds = new[] { ListerOldId })]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class NoteLister
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        private const string ListerOldId = "tests.restamp.lister_old";

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

        // A held record read only by dropping a slot is refused before anything is staged: with the store's lock held by another
        // writer, the write is refused typed instead of waiting on the lock and reporting contention.
        [TestCaseSource(nameof(StoreKinds))]
        public void ALossyDriftRecordIsRefusedBeforeTheStoresLockIsAsked(bool directory)
        {
            var path = SeedWide(directory);
            using var cache = Open(path, directory, GrownRegistry);
            using var held = directory
                ? new FileStream(Path.Combine(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(path), ".commit.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            Assert.Throws<CultSchemaConflictException>(() =>
                cache.TryCommit(batch => batch.Upsert(new NoteGrown { Name = "note", Value = 2 }, new CultRecordHandle<NoteGrown>(Note))));
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

        private static readonly CultRecordKey Base = new("base");
        private static readonly CultRecordKey Delta = new("delta");

        private CultCache OpenWithCodec(string path, bool directory, CultDocumentRegistry registry)
        {
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            cache.AddBackingStore(Store(path, directory));
            return cache;
        }

        private string StoredIdOfVariantStore(string path, CultDocumentRegistry registry, CultRecordKey key)
        {
            using var cache = OpenWithCodec(path, false, registry);
            return cache.GetStored(key)!.StoredSchemaId;
        }

        // A base and a variant of it, written by a writer that knew Narrow; Grown then reads the variant whole as compatible drift.
        private string SeedNarrowVariant()
        {
            var path = Path.Combine(_directory, "store.cc");
            using var cache = OpenWithCodec(path, false, NarrowRegistry);
            Assert.That(cache.Commit(batch => batch.Upsert(new NoteNarrow { Name = "base", Value = 1 }, new CultRecordHandle<NoteNarrow>(Base))), Is.True);
            Assert.That(cache.Commit(batch => batch.UpsertVariant(Delta, Base, new[] { cache.Override<NoteNarrow>("Name", "delta"), cache.Override<NoteNarrow>("Value", 5) })), Is.True);
            return path;
        }

        // Variants live in a single file (the directory store refuses them). The staging half asks the flag ToStoredDocument set on a loaded variant; Resolved and Flatten copies are never asked (only the store's loaded view carries the read-whole flag): an owned
        // drift variant is rewritten, flattened and removed like a plain drift record, restamping only what is written.
        [Test]
        public async Task AnOwnedDriftVariantIsRewritten()
        {
            var path = SeedNarrowVariant();
            using (var cache = OpenWithCodec(path, false, GrownRegistry))
            {
                await cache.PullAllBackingStoresAsync();
                Assert.That(cache.Commit(batch => batch.UpsertVariant(Delta, Base, new[] { cache.Override<NoteGrown>("Name", "delta"), cache.Override<NoteGrown>("Value", 6) })), Is.True);
            }

            Assert.That(StoredIdOfVariantStore(path, GrownRegistry, Delta), Is.EqualTo(GrownRegistry.GetRequired<NoteGrown>().SchemaId));
            Assert.That(StoredIdOfVariantStore(path, NarrowRegistry, Base), Is.EqualTo(NarrowRegistry.GetRequired<NoteNarrow>().SchemaId), "the base was not written");
        }

        [Test]
        public async Task AnOwnedDriftVariantIsFlattened()
        {
            var path = SeedNarrowVariant();
            using (var cache = OpenWithCodec(path, false, GrownRegistry))
            {
                await cache.PullAllBackingStoresAsync();
                Assert.That(cache.Commit(batch => batch.Flatten(Delta)), Is.True);
            }

            Assert.That(StoredIdOfVariantStore(path, GrownRegistry, Delta), Is.EqualTo(GrownRegistry.GetRequired<NoteGrown>().SchemaId));
        }

        [Test]
        public async Task AnOwnedDriftVariantIsRemoved()
        {
            var path = SeedNarrowVariant();
            using (var cache = OpenWithCodec(path, false, GrownRegistry))
            {
                await cache.PullAllBackingStoresAsync();
                Assert.That(cache.Commit(batch => batch.Remove(Delta)), Is.True);
            }

            using var reopened = OpenWithCodec(path, false, GrownRegistry);
            await reopened.PullAllBackingStoresAsync();
            Assert.That(reopened.GetStored(Delta), Is.Null);
            Assert.That(reopened.GetStored(Base), Is.Not.Null);
        }

        // A store whose entry is a newer writer's rename of Narrow's schema, with identical members and the given compatible ids.
        private string SeedRenamed(bool listsNarrow, string? alsoLists = null, string name = "tests.restamp_note_renamed")
        {
            var path = SeedNarrow(directory: false);
            var snapshot = Snapshot(path);
            var narrowId = NarrowRegistry.GetRequired<NoteNarrow>().SchemaId;
            var entry = snapshot.SchemaCatalog.Single();
            entry.SchemaId = entry.ContentHash = "tests.restamp.renamed";
            entry.SchemaName = name;
            entry.SchemaVersion = "tests.restamp_note_renamed.v2";
            entry.CompatibleSchemaIds = new[] { "tests.restamp.renamed" }
                .Concat(listsNarrow ? new[] { narrowId } : Array.Empty<string>())
                .Concat(alsoLists == null ? Array.Empty<string>() : new[] { alsoLists })
                .ToArray();
            foreach (var record in snapshot.Records)
                record.SchemaId = entry.SchemaId;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        // One writer of NoteNarrow, with a cache or without one: the same Owns answer for the same record.
        private void WriteNarrow(string path, bool cacheless, CultDocumentRegistry? registry = null)
        {
            registry ??= NarrowRegistry;
            if (cacheless)
            {
                new SingleFileMessagePackBackingStore(path).WriteDocument(Note, new NoteNarrow { Name = "note", Value = 2 }, registry: registry);
                return;
            }

            using var cache = Open(path, false, registry);
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            Assert.That(cache.Commit(batch => batch.Upsert(new NoteNarrow { Name = "note", Value = 2 }, new CultRecordHandle<NoteNarrow>(Note))), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ARenamedEntryListingTheWritersIdIsOwnedByCacheAndCachelessWriterAlike(bool cacheless)
        {
            var path = SeedRenamed(listsNarrow: true);
            WriteNarrow(path, cacheless);

            Assert.That(Snapshot(path).Records.Single(record => record.Key == "note").SchemaId, Is.EqualTo(NarrowRegistry.GetRequired<NoteNarrow>().SchemaId));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ARenamedEntryNotListingTheWritersIdIsRefusedByCacheAndCachelessWriterAlike(bool cacheless)
        {
            var path = SeedRenamed(listsNarrow: false);
            var before = Fingerprint(path);
            Assert.Throws<CultSchemaConflictException>(() => WriteNarrow(path, cacheless));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // A writer of one schema never overwrites and retypes a record of another schema, however alike their members.
        [TestCase(false)]
        [TestCase(true)]
        public void AWriterOfOneSchemaNeverRetypesASameShapeRecordOfAnother(bool cacheless)
        {
            var path = Path.Combine(_directory, "store.cc");
            var otherRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteOther) });
            using (var seed = Open(path, false, otherRegistry))
                Assert.That(seed.Commit(batch => batch.Upsert(new NoteOther { Name = "note", Value = 1 }, new CultRecordHandle<NoteOther>(Note))), Is.True);
            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() => WriteNarrow(path, cacheless));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        private static readonly CultDocumentRegistry NarrowShortRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteNarrow), typeof(NoteShort) });

        // Which entry reads a persisted entry is one precedence: a cacheless writer lent a registry picks the claimant that registry
        // picks, never one by schema name over its own entry. Here the entry is named and shaped like Narrow but lists Short's id.
        [TestCase(false)]
        [TestCase(true)]
        public void ACachelessWriterPicksTheClaimantItsRegistryPicks(bool cacheless)
        {
            var path = SeedRenamed(listsNarrow: false, alsoLists: NarrowShortRegistry.GetRequired<NoteShort>().SchemaId, name: "tests.restamp_note");
            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() => WriteNarrow(path, cacheless, NarrowShortRegistry));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ACachelessWriterOwnsWhatItsRegistryOwns(bool cacheless)
        {
            var path = SeedRenamed(listsNarrow: false, alsoLists: NarrowShortRegistry.GetRequired<NoteShort>().SchemaId, name: "tests.restamp_note");

            WriteNarrow(path, cacheless, NarrowRegistry);

            Assert.That(Snapshot(path).Records.Single(record => record.Key == "note").SchemaId, Is.EqualTo(NarrowRegistry.GetRequired<NoteNarrow>().SchemaId));
        }

        private static readonly CultDocumentRegistry NarrowListerRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(NoteNarrow), typeof(NoteLister) });

        // A store whose Narrow-shaped record sits under an id another registered type (Lister) claims: its old id, which its entry
        // lists, or its current id.
        private string SeedListerRecord(bool underCurrentId)
        {
            var path = SeedNarrow(directory: false);
            var snapshot = Snapshot(path);
            var listerId = NarrowListerRegistry.GetRequired<NoteLister>().SchemaId;
            var entry = snapshot.SchemaCatalog.Single();
            entry.SchemaId = entry.ContentHash = underCurrentId ? listerId : "tests.restamp.lister_published";
            entry.SchemaName = "tests.restamp_lister";
            entry.SchemaVersion = "tests.restamp_lister.v1";
            entry.CompatibleSchemaIds = underCurrentId ? new[] { listerId } : new[] { "tests.restamp.lister_published", ListerOldId };
            foreach (var record in snapshot.Records)
                record.SchemaId = underCurrentId ? listerId : ListerOldId;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        // A cacheless write owns a record only through a claimant it writes: another registered type that lists the record's id, or
        // owns it, and reads it whole keeps the record, whatever the lent registry (or Shared, with none) registers.
        // Registry null is Shared, which holds every [CultDocument] type of the test assembly.
        [TestCase(false, true)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(true, false)]
        public void ACachelessWriterNeverOwnsARecordAnotherRegisteredTypeClaims(bool underCurrentId, bool lendRegistry)
        {
            var path = SeedListerRecord(underCurrentId);
            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() => WriteNarrow(path, cacheless: true, lendRegistry ? NarrowListerRegistry : CultDocumentRegistry.Shared));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // Claiming is by entry, not by schema name: a record under an id a same-named other version of the written schema lists is
        // that version's, and a write of Narrow does not take it.
        [Test]
        public void ACachelessWriterNeverOwnsARecordASameNamedVersionClaims()
        {
            var path = SeedRenamed(listsNarrow: false, alsoLists: WideId);
            var snapshot = Snapshot(path);
            foreach (var record in snapshot.Records)
                record.SchemaId = WideId;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() => WriteNarrow(path, cacheless: true, CultDocumentRegistry.ForTypes(new[] { typeof(NoteNarrow), typeof(NoteGrownDeclaring) })));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        private const string ListerDriftId = "tests.restamp.lister_drift";

        // The Lister type's entry as a caller passes it: its id's own listing, or the id list the caller chooses.
        private static CultSchemaCatalogEntry ListerEntry(string[] compatible, CultSchemaMemberCatalogEntry[]? members = null, string hashPrefix = "")
        {
            var full = NarrowListerRegistry.GetRequired<NoteLister>().ToCatalogEntry();
            return new CultSchemaCatalogEntry
            {
                SchemaId = full.SchemaId,
                ContentHash = hashPrefix + full.ContentHash,
                SchemaName = full.SchemaName,
                SchemaVersion = full.SchemaVersion,
                CompatibleSchemaIds = compatible,
                Members = members ?? full.Members,
                CanonicalSchemaJson = full.CanonicalSchemaJson,
            };
        }

        private static CultPersistedRecord ListerRecord(byte[]? payload = null) => new()
        {
            Key = "note",
            SchemaId = NarrowListerRegistry.GetRequired<NoteLister>().SchemaId,
            StoredAt = "2026-10-22T00:00:00.0000000+00:00",
            Payload = payload ?? MessagePackSerializer.Serialize(new NoteLister { Name = "note", Value = 2 }),
        };

        // The one stored entry and its records renamed to the given id, name and compatible ids (ContentHash kept), Narrow-shaped.
        private string SeedListerStore(string id, string name, params string[] compatible)
        {
            var path = SeedRenamed(listsNarrow: false, name: name);
            var snapshot = Snapshot(path);
            var entry = snapshot.SchemaCatalog.Single();
            entry.SchemaId = id;
            entry.CompatibleSchemaIds = compatible;
            foreach (var seeded in snapshot.Records)
                seeded.SchemaId = id;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        // A direct WriteRecord is bound by the entry it passes, and by it alone: the entry the registry holds for the same schema id
        // changes nothing, lent or not. An entry omitting the old id refuses the record; one listing it owns the record, as a cache would.
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ThePassedEntryAloneDecidesTheWrittenTypesIds(bool entryListsOldId, bool lend)
        {
            var path = SeedListerStore(ListerOldId, "tests.restamp_old", ListerOldId);
            var passed = ListerEntry(entryListsOldId ? new[] { NarrowListerRegistry.GetRequired<NoteLister>().SchemaId, ListerOldId } : new[] { NarrowListerRegistry.GetRequired<NoteLister>().SchemaId });
            var registry = lend ? CultDocumentRegistry.ForTypes(new[] { typeof(NoteLister) }) : null;
            var before = Fingerprint(path);

            if (entryListsOldId)
            {
                new SingleFileMessagePackBackingStore(path).WriteRecord(ListerRecord(), passed, null, registry);
                Assert.That(Snapshot(path).Records.Single(stored => stored.Key == "note").SchemaId, Is.EqualTo(passed.SchemaId));
                return;
            }
            Assert.Throws<CultSchemaConflictException>(() => new SingleFileMessagePackBackingStore(path).WriteRecord(ListerRecord(), passed, null, registry));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // The written type's own drift, read whole, is owned however the entry lists ids and whatever registry is lent: a registry
        // that holds the written type never refuses what its own entry would own.
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ALentRegistryNeverRefusesTheWrittenTypesOwnDrift(bool passedListsOwnId, bool lend)
        {
            var path = SeedListerStore(ListerDriftId, "tests.restamp_lister", ListerDriftId);
            var own = NarrowListerRegistry.GetRequired<NoteLister>().SchemaId;
            var passed = ListerEntry(passedListsOwnId ? new[] { own } : Array.Empty<string>());

            new SingleFileMessagePackBackingStore(path).WriteRecord(ListerRecord(), passed, null, lend ? NarrowListerRegistry : null);

            Assert.That(Snapshot(path).Records.Single(stored => stored.Key == "note").SchemaId, Is.EqualTo(own));
        }

        // A lent registry never widens the shape of the passed entry: an entry narrower than the stored one reads it only by dropping
        // a slot, and the registry's wider entry for the same id is not asked.
        [TestCase(false)]
        [TestCase(true)]
        public void ALentRegistryNeverWidensTheShapeOfThePassedEntry(bool lend)
        {
            var path = SeedListerStore(ListerDriftId, "tests.restamp_lister", ListerDriftId);
            var full = NarrowListerRegistry.GetRequired<NoteLister>().ToCatalogEntry();
            var passed = ListerEntry(full.CompatibleSchemaIds, full.Members.Take(1).ToArray(), "narrow-");
            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() =>
                new SingleFileMessagePackBackingStore(path).WriteRecord(ListerRecord(MessagePackSerializer.Serialize(new[] { "note" })), passed, null, lend ? NarrowListerRegistry : null));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // The shadow, where it is decided: a written entry answers for its schema id whatever the registry holds, and the registry
        // still adds its other types.
        [Test]
        public void AWrittenEntryShadowsTheLentRegistrysTypeForItsId()
        {
            var written = NarrowRegistry.GetRequired<NoteNarrow>().ToCatalogEntry();
            var lookup = new CatalogEntryLookup(new[] { written }, NarrowListerRegistry);
            var lister = NarrowListerRegistry.GetRequired<NoteLister>();

            Assert.That(lookup.Owner(written.SchemaId), Is.SameAs(written));
            Assert.That(lookup.Named(written.SchemaName), Is.EqualTo(new[] { written }).Using<CultSchemaCatalogEntry>((x, y) => ReferenceEquals(x, y)));
            Assert.That(lookup.Listing(written.SchemaId), Is.EqualTo(new[] { written }).Using<CultSchemaCatalogEntry>((x, y) => ReferenceEquals(x, y)));
            Assert.That(lookup.Owner(lister.SchemaId)!.SchemaId, Is.EqualTo(lister.SchemaId));
            Assert.That(lookup.Listing(ListerOldId).Select(entry => entry.SchemaId), Is.EqualTo(new[] { lister.SchemaId }));
            Assert.That(lookup.Named(lister.SchemaName).Select(entry => entry.SchemaId), Is.EqualTo(new[] { lister.SchemaId }));

            // A write of several entries shadows by the entry that has the id, not by all of them having it.
            var other = CultDocumentRegistry.ForTypes(new[] { typeof(NoteOther) }).GetRequired<NoteOther>().ToCatalogEntry();
            var several = new CatalogEntryLookup(new[] { other, written }, NarrowListerRegistry);
            Assert.That(several.Named(written.SchemaName), Is.EqualTo(new[] { written }).Using<CultSchemaCatalogEntry>((x, y) => ReferenceEquals(x, y)));
        }

        // The registry's other types change nothing about a record the written type claims itself.
        [Test]
        public void AnotherRegisteredTypeDoesNotTakeAWritersOwnDriftRecord()
        {
            var path = SeedRenamed(listsNarrow: true);

            WriteNarrow(path, cacheless: true, NarrowListerRegistry);

            Assert.That(Snapshot(path).Records.Single(record => record.Key == "note").SchemaId, Is.EqualTo(NarrowRegistry.GetRequired<NoteNarrow>().SchemaId));
        }

        // What the lent registry registers beyond the written type can only make the claim more ambiguous, never own more: a second
        // version of the written schema's name refuses a record the write owned by name alone.
        [Test]
        public void ASecondVersionOfTheWrittenNameInTheLentRegistryOnlyRefuses()
        {
            var path = SeedRenamed(listsNarrow: false, name: "tests.restamp_note");
            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() => WriteNarrow(path, cacheless: true, CultDocumentRegistry.ForTypes(new[] { typeof(NoteNarrow), typeof(NoteGrown) })));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // A record stored under an id its catalog entry only lists (no entry owns it) is published by that entry, and the type that
        // reads the entry whole owns the record: a cache and a cacheless writer alike restamp it.
        [TestCase(false)]
        [TestCase(true)]
        public void ARecordUnderAnIdOnlyListedIsPublishedByTheEntryListingIt(bool cacheless)
        {
            const string listedOnly = "tests.restamp.listedonly";
            var path = SeedRenamed(listsNarrow: true, alsoLists: listedOnly);
            var snapshot = Snapshot(path);
            foreach (var record in snapshot.Records)
                record.SchemaId = listedOnly;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            WriteNarrow(path, cacheless);

            Assert.That(Snapshot(path).Records.Single(record => record.Key == "note").SchemaId, Is.EqualTo(NarrowRegistry.GetRequired<NoteNarrow>().SchemaId));
        }

        // A raw record write names no registry, so its one entry is the claimant: it reads a renamed entry that lists its id, or has
        // its schema name, and nothing else.
        [TestCase(true, "tests.restamp_note_renamed", true)]
        [TestCase(false, "tests.restamp_note", true)]
        [TestCase(false, "tests.restamp_note_renamed", false)]
        public void ARecordWriteWithNoRegistryTakesItsOneEntryAsClaimant(bool listsNarrow, string persistedName, bool owned)
        {
            var record = Snapshot(SeedNarrow(directory: false)).Records.Single(candidate => candidate.Key == "note");
            var path = SeedRenamed(listsNarrow, name: persistedName);
            var before = Fingerprint(path);
            var write = new SingleFileMessagePackBackingStore(path);
            var entry = NarrowRegistry.GetRequired<NoteNarrow>().ToCatalogEntry();

            if (owned)
            {
                write.WriteRecord(record, entry);
                Assert.That(Snapshot(path).Records.Single(candidate => candidate.Key == "note").SchemaId, Is.EqualTo(entry.SchemaId));
            }
            else
            {
                Assert.Throws<CultSchemaConflictException>(() => write.WriteRecord(record, entry));
                Assert.That(Fingerprint(path), Is.EqualTo(before));
            }
        }

        // An id no catalog entry publishes belongs to nobody a writer can name, so no writer owns it. A store file cannot hold such a
        // record (ReadStore refuses it), so the rule is pinned where it is decided, for a cacheless writer's lookup.
        [Test]
        public void AnUnpublishedIdIsOwnedByNoOne()
        {
            var narrow = NarrowRegistry.GetRequired<NoteNarrow>().ToCatalogEntry();
            var writes = new[] { narrow };
            Func<string, bool> declares = id => id == narrow.SchemaId;

            Assert.That(CultDocumentRegistry.Owns("tests.restamp.unpublished", writes, declares, new CatalogEntryLookup(writes, null), entry => entry, _ => true), Is.False);
            Assert.That(CultDocumentRegistry.Owns(narrow.SchemaId, writes, declares, new CatalogEntryLookup(writes, null), entry => entry, _ => true), Is.True);
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
