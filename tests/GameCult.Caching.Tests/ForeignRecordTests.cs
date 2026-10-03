#nullable enable
using System;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.EmittedDocumentTypes;

namespace GameCult.Caching.Tests
{
    // A record under a schema id no registered type owns or lists as compatible is foreign. CultCache never destroys or relabels
    // it: both store kinds carry it byte for byte, list it as foreign, and refuse any write that would replace or remove it.
    public class ForeignRecordTests
    {
        private static readonly Type Deck = Emit("ForeignSuiteDeck", "tests.foreign_deck", "tests.foreign_deck.v1", new[] { new Field("Name", typeof(string), 0, IsName: true) });
        // The deck's schema declaring another runtime's id compatible (OtherRuntimeId below): one under the deck's own version, and a
        // later version that a registry holding the deck can register beside it.
        private static readonly Type ClaimingDeck = Emit("ForeignSuiteClaimingDeck", "tests.foreign_deck", "tests.foreign_deck.v1", new[] { new Field("Name", typeof(string), 0, IsName: true) }, new[] { "other.runtime.deck" });
        private static readonly Type LateClaimingDeck = Emit("ForeignSuiteLateClaimingDeck", "tests.foreign_deck", "tests.foreign_deck.v2", new[] { new Field("Name", typeof(string), 0, IsName: true) }, new[] { "other.runtime.deck" });
        private static readonly Type Widget = Emit("ForeignSuiteWidget", "tests.foreign_widget", "tests.foreign_widget.v1", new[] { new Field("Label", typeof(string), 0, IsName: true) });

        // A build that has both types, and one that has only the deck: to it the widget is foreign.
        private static readonly CultDocumentRegistry Full = CultDocumentRegistry.ForTypes(new[] { Deck, Widget });
        private static readonly CultDocumentRegistry DeckOnly = CultDocumentRegistry.ForTypes(new[] { Deck });
        private static readonly string WidgetId = Full.GetRequired(Widget).SchemaId;

        // Another writer's deck (an older or newer build, another runtime) under an id the deck does not declare. It declares the
        // deck's own id compatible, so it may rewrite the deck's records; the deck still reads them through that listing, read-only.
        private static readonly Type MovingDeck = Emit("ForeignSuiteMovingDeck", "tests.foreign_deck", "tests.foreign_deck.v3", new[] { new Field("Name", typeof(string), 0, IsName: true) }, new[] { DeckOnly.GetRequired(Deck).SchemaId });
        private static readonly CultDocumentRegistry Moving = CultDocumentRegistry.ForTypes(new[] { MovingDeck });
        private static readonly string MovingId = Moving.GetRequired(MovingDeck).SchemaId;

        private static readonly CultRecordKey D = new("d");
        private static readonly CultRecordKey E = new("e");
        private static readonly CultRecordKey W = new("w");
        private static readonly CultRecordKey V = new("v");
        private const string Canary = "CANARY-7f3c-payload-value";

        public enum Write
        {
            Flush,
            Commit,
            ConditionalCommit
        }

        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-foreign-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path, CultDocumentRegistry registry, bool directory) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry, UseDirectoryStore = directory });

        private static object DeckOf(string name) => New(Deck, ("Name", name));

        private static object WidgetOf(string label) => New(Widget, ("Label", label));

        private static CultPersistedStoreSnapshot Read(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        // Every file of the store, by name, with its bytes: a refused write leaves all of them as they were.
        private static string[] Fingerprint(string path)
        {
            var files = new[] { path }.Concat(Directory.Exists(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(path))
                    ? Directory.GetFiles(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(path), "*.msgpack")
                    : Array.Empty<string>())
                .OrderBy(file => file, StringComparer.Ordinal);
            return files.Select(file => Path.GetFileName(file) + ":" + Convert.ToBase64String(File.ReadAllBytes(file))).ToArray();
        }

        // What the store holds for one record as the file (single-file) or manifest (directory: the page hash) names it.
        private static string Stored(string path, CultRecordKey key)
        {
            var record = Read(path).Records.Single(candidate => candidate.Key == key.Value);
            return $"{record.SchemaId}|{record.StoredAt}|{Convert.ToBase64String(record.Payload)}|{record.Variant == null}";
        }

        // A store holding a deck d and a widget w, written by the build that has both types.
        private string Seeded(string name, bool directory)
        {
            var path = PathOf(name);
            using var cache = Open(path, Full, directory);
            cache.Commit(batch =>
            {
                batch.Upsert(Deck, DeckOf("d"), D);
                batch.Upsert(Widget, WidgetOf("w"), W);
            });
            return path;
        }

        private static CacheBackingStore StoreOf(CultCache cache) => cache.BackingStores.Single();

        private static void Land(CultCache cache, Write write, CultRecordKey key, object document)
        {
            switch (write)
            {
                case Write.Flush:
                    cache.UpsertAsync(document.GetType(), document, key).GetAwaiter().GetResult();
                    cache.FlushAsync().GetAwaiter().GetResult();
                    break;
                case Write.Commit:
                    Assert.That(cache.Commit(batch => batch.Upsert(document.GetType(), document, key)), Is.True);
                    break;
                case Write.ConditionalCommit:
                    Assert.That(cache.Commit(batch =>
                    {
                        batch.Expect(D, cache.Get(D));
                        batch.Upsert(document.GetType(), document, key);
                    }), Is.True);
                    break;
            }
        }

        private static void Refused(CultCache cache, Write write, CultRecordKey key, object document, CultRecordKey foreignKey)
        {
            var refusal = Assert.Throws<CultSchemaConflictException>(() => Land(cache, write, key, document))!;
            Assert.Multiple(() =>
            {
                Assert.That(refusal.RecordKey, Is.EqualTo(foreignKey.Value));
                Assert.That(refusal.SchemaId, Is.EqualTo(WidgetId));
                Assert.That(refusal.SchemaNames, Is.EqualTo(new[] { "tests.foreign_widget" }));
                Assert.That(refusal.Message, Does.Contain(foreignKey.Value).And.Contain(WidgetId));
                Assert.That(refusal.Message, Does.Not.Contain(Canary), "a refusal never echoes the document it refused");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AForeignRecordLoadsAsForeignAndTheCacheDoesNotHoldIt(bool directory)
        {
            var path = Seeded("seeded.cc", directory);
            var storedAt = Read(path).Records.Single(record => record.Key == W.Value).StoredAt;

            using var cache = Open(path, DeckOnly, directory);

            var foreign = StoreOf(cache).ForeignRecords.Single();
            Assert.Multiple(() =>
            {
                Assert.That(foreign.Key, Is.EqualTo(W.Value));
                Assert.That(foreign.SchemaId, Is.EqualTo(WidgetId));
                Assert.That(foreign.SchemaName, Is.EqualTo("tests.foreign_widget"));
                Assert.That(foreign.StoredAt, Is.EqualTo(storedAt));
                Assert.That(cache.Get(W), Is.Null, "the cache never holds a foreign record");
                Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("d"), "the records it claims load as usual");
            });
        }

        // Every write path lays the foreign record back as the file holds it: the single-file store's whole view (a flush and an
        // unconditional commit), its commit onto the file, and the directory store's generation.
        [Test]
        public void AWriteOfAnotherRecordKeepsTheForeignRecordByteForByte(
            [Values(false, true)] bool directory,
            [Values] Write write)
        {
            var path = Seeded("kept.cc", directory);
            var before = Stored(path, W);

            using (var cache = Open(path, DeckOnly, directory))
                Land(cache, write, E, DeckOf("e"));

            Assert.That(Stored(path, W), Is.EqualTo(before));
            Assert.That(Read(path).Records.Select(record => record.Key), Is.EquivalentTo(new[] { "d", "e", "w" }));
            using var full = Open(path, Full, directory);
            Assert.That(EmittedDocumentTypes.Read(full.Get(W)!, "Label"), Is.EqualTo("w"), "the build that has the type still reads it");
            Assert.That(StoreOf(full).ForeignRecords, Is.Empty);
        }

        [Test]
        public void AWriteThatWouldReplaceAForeignRecordIsRefusedAndNothingIsWritten(
            [Values(false, true)] bool directory,
            [Values] Write write)
        {
            var path = Seeded("replace.cc", directory);
            var before = Fingerprint(path);

            using var cache = Open(path, DeckOnly, directory);
            Refused(cache, write, W, DeckOf(Canary), W);

            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(cache.Get(W), Is.Null);
        }

        // Another writer replaced a record this cache holds with a record of a type it does not have. A write that would lay this
        // cache's record over it is refused; the file keeps the other writer's record. The refusal reloads it as a pull would: the
        // store lists it foreign, so the next write of it is refused before staging.
        [Test]
        public void AWriteOverARecordAnotherWriterMadeForeignIsRefused([Values] bool directory, [Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = Seeded("replaced.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var before = Fingerprint(path);

            var refusal = Assert.Throws<CultSchemaConflictException>(() => Land(cache, write, D, DeckOf(Canary)))!;

            Assert.That(refusal.RecordKey, Is.EqualTo(D.Value));
            Assert.That(refusal.Message, Does.Not.Contain(Canary));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(StoreOf(cache).ForeignRecords.Select(record => record.Key), Is.EqualTo(new[] { "d", "w" }));
            Assert.Throws<CultSchemaConflictException>(() => cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult());
            Assert.That(cache.IsDirty, Is.False);
        }

        // A refusal does not poison the store: what the cache held for the refused record goes with the refusal, so a later write
        // to another record lands and the file keeps the other writer's record.
        [Test]
        public void AfterARefusedWriteAWriteToAnotherRecordLands(
            [Values(false, true)] bool directory,
            [Values(Write.Flush, Write.Commit)] Write refused)
        {
            var path = Seeded("unstuck.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            Assert.Throws<CultSchemaConflictException>(() => Land(cache, refused, D, DeckOf(Canary)));
            Land(cache, Write.Commit, E, DeckOf("e"));

            Assert.That(Stored(path, D), Is.EqualTo(widget), "the other writer's record is as it was");
            Assert.That(EmittedDocumentTypes.Read(cache.Get(E)!, "Name"), Is.EqualTo("e"));
            Assert.That(Read(path).Records.Select(record => record.Key), Does.Contain("e"));
        }

        // The cache and the store agree on what a refusal left: the refused record is not served, not after a later write, not after
        // a pull, and not by a fresh open. What the cache served before the refusal was never what the file holds.
        [Test]
        public void ARefusedRecordIsNotServedAfterTheRefusalAfterALaterWriteOrAfterAPull(
            [Values(false, true)] bool directory,
            [Values(Write.Flush, Write.Commit)] Write refused)
        {
            var path = Seeded("not-served.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));

            var refusal = Assert.Throws<CultSchemaConflictException>(() => Land(cache, refused, D, DeckOf(Canary)))!;
            Assert.That(refusal.Message, Does.StartWith("Record 'd' (schema id '" + WidgetId + "'"), "one refused key reads as one record");
            var afterRefusal = cache.Get(D);
            Land(cache, Write.Commit, E, DeckOf("e"));
            var afterLaterWrite = cache.Get(D);
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            var afterPull = cache.Get(D);
            using var fresh = Open(path, DeckOnly, directory);

            Assert.Multiple(() =>
            {
                Assert.That(afterRefusal, Is.Null, "after the refusal");
                Assert.That(afterLaterWrite, Is.Null, "after a later write");
                Assert.That(afterPull, Is.Null, "after a pull");
                Assert.That(fresh.Get(D), Is.Null, "a fresh open");
                Assert.That(StoreOf(cache).ForeignRecords.Select(record => record.Key), Is.EqualTo(StoreOf(fresh).ForeignRecords.Select(record => record.Key)));
            });
        }

        // A refusal names every key it refused, and every one of them leaves the cache; a record the write did not touch stays.
        [Test]
        public void ARefusalOfSeveralRecordsNamesEachAndTheCacheDropsEach(
            [Values(false, true)] bool directory,
            [Values(Write.Flush, Write.Commit)] Write refused)
        {
            var path = PathOf("several.cc");
            var d2 = new CultRecordKey("d2");
            using (var seed = Open(path, Full, directory))
                seed.Commit(batch =>
                {
                    batch.Upsert(Deck, DeckOf("d"), D);
                    batch.Upsert(Deck, DeckOf("d2"), d2);
                    batch.Upsert(Deck, DeckOf("e"), E);
                });
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch =>
                {
                    batch.Upsert(Widget, WidgetOf("w1"), D);
                    batch.Upsert(Widget, WidgetOf("w2"), d2);
                });

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
            {
                if (refused == Write.Flush)
                {
                    cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult();
                    cache.UpsertAsync(Deck, DeckOf(Canary), d2).GetAwaiter().GetResult();
                    cache.FlushAsync().GetAwaiter().GetResult();
                }
                else
                {
                    cache.Commit(batch =>
                    {
                        batch.Upsert(Deck, DeckOf(Canary), D);
                        batch.Upsert(Deck, DeckOf(Canary), d2);
                    });
                }
            })!;
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(refusal.RecordKey, Is.EqualTo("d"), "the first refused key in key order");
                Assert.That(refusal.Message, Does.Contain("Records 'd' (schema id '" + WidgetId + "'").And.Contain("'d2' (schema id '" + WidgetId + "'").And.Not.Contain(Canary));
                Assert.That(cache.Get(D), Is.Null);
                Assert.That(cache.Get(d2), Is.Null);
                Assert.That(EmittedDocumentTypes.Read(cache.Get(E)!, "Name"), Is.EqualTo("e"), "a record the write did not touch stays");
            });
        }

        // A write that never touches a record another writer made foreign is not refused for holding a stale copy of it: it lands
        // the first time, in both store kinds, and the next pull drops the copy.
        [Test]
        public void AWriteThatNeverTouchedARecordAnotherWriterMadeForeignLandsAtOnce(
            [Values(false, true)] bool directory,
            [Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = Seeded("untouched.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            Land(cache, write, E, DeckOf("e"));
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(Stored(path, D), Is.EqualTo(widget), "the other writer's record is as it was");
                Assert.That(Read(path).Records.Select(record => record.Key), Does.Contain("e"));
                Assert.That(cache.Get(D), Is.Null, "the pull drops the stale copy");
            });
        }

        // The write carried the file's record for d; it did not store this cache's copy, so the copy's stamp is still what the
        // cache read, and a condition on it still fails against the other writer's record.
        [Test]
        public void ACarriedForeignRecordDoesNotRestampTheStaleCopyTheCacheHolds(
            [Values(false, true)] bool directory,
            [Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = Seeded("restamp.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));

            Land(cache, write, E, DeckOf("e"));

            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(D, cache.Get(D));
                batch.Upsert(Deck, DeckOf("e2"), E);
            }), Is.False, "the cache never read the other writer's record");
        }

        // The same when the stale copy was only held, never written: a flush after the other writer's record landed refuses once.
        [Test]
        public void AFlushRefusedForAStaleCopyOfAForeignRecordRefusesOnce([Values(false, true)] bool directory)
        {
            var path = Seeded("stale.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            Assert.Throws<CultSchemaConflictException>(() => Land(cache, Write.Commit, D, DeckOf(Canary)));
            Land(cache, Write.Flush, E, DeckOf("e"));
            Land(cache, Write.Flush, E, DeckOf("e again"));

            Assert.That(Stored(path, D), Is.EqualTo(widget));
        }

        // A key staged and not yet written is refused by any write that carries it, whatever key that write names: an unconditional
        // commit writes what a flush would.
        [Test]
        public void AStagedWriteIsRefusedByACommitOfAnotherRecord([Values(false, true)] bool directory)
        {
            var path = Seeded("staged-commit.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult();
            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Upsert(Deck, DeckOf("e"), E)))!;

            Assert.That(refusal.RecordKey, Is.EqualTo(D.Value));
            Assert.That(Stored(path, D), Is.EqualTo(widget));
        }

        // What a write landed is no longer staged: a record that becomes foreign afterwards is the other writer's, and writing
        // another record is not refused for it.
        [Test]
        public void AWrittenRecordIsNoLongerStagedSoALaterForeignRecordDoesNotRefuseOtherWrites(
            [Values(false, true)] bool directory,
            [Values(Write.Flush, Write.Commit)] Write landed)
        {
            var path = Seeded("landed.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            Land(cache, landed, D, DeckOf("d again"));
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            Land(cache, Write.Commit, E, DeckOf("e"));

            Assert.That(Stored(path, D), Is.EqualTo(widget));
            Assert.That(Read(path).Records.Select(record => record.Key), Does.Contain("e"));
        }

        // A commit writes the whole view, so what it landed that was staged is no longer staged either.
        [Test]
        public void AStagedWriteLandedByACommitIsNoLongerStaged([Values(false, true)] bool directory)
        {
            var path = Seeded("staged-landed.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            cache.UpsertAsync(Deck, DeckOf("d staged"), D).GetAwaiter().GetResult();
            Land(cache, Write.Commit, E, DeckOf("e"));
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            Land(cache, Write.Commit, new CultRecordKey("e2"), DeckOf("e2"));

            Assert.That(Stored(path, D), Is.EqualTo(widget));
            Assert.That(Read(path).Records.Select(record => record.Key), Does.Contain("e2"));
        }

        // A staged removal is a write of its own: a refusal takes it too.
        [TestCase(false)]
        [TestCase(true)]
        public void AStagedRemovalOfARecordAnotherWriterMadeForeignIsRefusedOnceAndThenGone(bool directory)
        {
            var path = Seeded("staged-removal.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var widget = Stored(path, D);

            cache.Remove(D);
            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.FlushAsync().GetAwaiter().GetResult())!;
            Land(cache, Write.Commit, E, DeckOf("e"));

            Assert.That(refusal.RecordKey, Is.EqualTo(D.Value));
            Assert.That(Stored(path, D), Is.EqualTo(widget));
        }

        // What a refusal leaves staged is exactly what it did not refuse: the other record's write still lands on the next flush,
        // and a store with nothing left staged is clean.
        [TestCase(false)]
        [TestCase(true)]
        public void ARefusalLeavesStagedWhatItDidNotRefuse(bool directory)
        {
            var path = Seeded("pending.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));

            cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult();
            Assert.Throws<CultSchemaConflictException>(() => cache.FlushAsync().GetAwaiter().GetResult());
            Assert.That(cache.IsDirty, Is.False, "nothing is left staged");

            cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult();
            cache.UpsertAsync(Deck, DeckOf("e"), E).GetAwaiter().GetResult();
            Assert.Throws<CultSchemaConflictException>(() => cache.FlushAsync().GetAwaiter().GetResult());
            Assert.That(cache.IsDirty, Is.True, "e is still staged");
            cache.FlushAsync().GetAwaiter().GetResult();

            Assert.That(Read(path).Records.Select(record => record.Key), Does.Contain("e"));
            Assert.That(cache.IsDirty, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovingARecordAnotherWriterMadeForeignIsRefused(bool directory)
        {
            var path = Seeded("removed.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            cache.Commit(batch => batch.Upsert(Deck, DeckOf("e"), E));
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var before = Fingerprint(path);

            // A condition keeps the single-file commit on the file as it is, so only the removal reaches d.
            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch =>
            {
                batch.Expect(E, cache.Get(E));
                batch.Remove(D);
            }))!;

            Assert.That(refusal.RecordKey, Is.EqualTo(D.Value));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ARecordAnotherWriterMadeForeignLeavesTheCacheAtThePull(bool directory)
        {
            var path = Seeded("pulled.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));

            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.That(cache.Get(D), Is.Null);
            Assert.That(StoreOf(cache).ForeignRecords.Select(record => record.Key), Is.EqualTo(new[] { "d", "w" }));
        }

        // A directory load without a lease that a writer's commit forces to retry lists each foreign record once, from the manifest
        // the retry read.
        [Test]
        public void ARetriedDirectoryLoadListsEachForeignRecordOnce()
        {
            var path = Seeded("retried.cc", directory: true);
            var store = new DirectoryMessagePackBackingStore(path);
            using var cache = new CultCache(DeckOnly);
            cache.AddBackingStore(store);
            File.Delete(Path.Combine(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(path), ".commit.lock"));
            var commits = 0;
            // The writer is another cache, so it commits from another thread; it commits once, under the first unleased read.
            store.UnleasedManifestRead = () =>
            {
                if (commits++ > 0)
                    return;
                System.Threading.Tasks.Task.Run(() =>
                {
                    using var writer = Open(path, Full, directory: true);
                    writer.Commit(batch => batch.Upsert(Deck, DeckOf("e"), E));
                }).GetAwaiter().GetResult();
            };

            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.That(cache.Get(E), Is.Not.Null, "the load retried and read the writer's generation");
            Assert.That(store.ForeignRecords.Select(record => record.Key), Is.EqualTo(new[] { "w" }));
        }

        // The file decides what is carried, not what this cache loaded: a foreign record another writer added since is kept too.
        [TestCase(false)]
        [TestCase(true)]
        public void AForeignRecordAddedAfterTheLoadIsKept(bool directory)
        {
            var path = Seeded("added.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            var z = new CultRecordKey("z");
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("z"), z));
            var before = Stored(path, z);

            Land(cache, Write.Flush, E, DeckOf("e"));

            Assert.That(Stored(path, z), Is.EqualTo(before));
        }

        // A writer cannot see whether a foreign record holds element ids, so a store marked as holding them stays marked while it
        // carries one: a write that copies any record forward keeps the mark.
        [Test]
        public void AMarkedHeaderStaysMarkedWhileTheStoreCarriesAForeignRecord([Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = Seeded("marked.cc", directory: false);
            var snapshot = Read(path);
            snapshot.FormatVersion = CultPersistedStoreSnapshot.FormatV3;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            using (var cache = Open(path, DeckOnly, directory: false))
                Land(cache, write, E, DeckOf("e"));

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
        }

        private const string OtherRuntimeId = "other.runtime.deck";

        // A deck another runtime wrote under its own id for the deck's schema. This build reads it through the catalog's schema name,
        // but does not own the id.
        private string OtherRuntimeStore(string name, bool withVariant = false)
        {
            var path = PathOf(name);
            using (var cache = Open(path, DeckOnly, directory: false))
            {
                cache.Commit(batch => batch.Upsert(Deck, DeckOf("d"), D));
                if (withVariant)
                    cache.Commit(batch => batch.UpsertVariant(V, D, new[] { cache.Override(Deck, "Name", "v") }));
            }

            var snapshot = Read(path);
            var entry = snapshot.SchemaCatalog.Single();
            entry.SchemaId = OtherRuntimeId;
            entry.ContentHash = OtherRuntimeId;
            entry.CompatibleSchemaIds = new[] { OtherRuntimeId };
            foreach (var record in snapshot.Records)
                record.SchemaId = OtherRuntimeId;
            // An older entry that also lists the id, first in the catalog: the entry that owns the id is the one that publishes it.
            snapshot.SchemaCatalog = new[]
            {
                new CultSchemaCatalogEntry
                {
                    SchemaId = "decoy.older", SchemaName = "tests.decoy", SchemaVersion = "tests.decoy.v1", ContentHash = "decoy.older",
                    CompatibleSchemaIds = new[] { "decoy.older", OtherRuntimeId }
                },
                entry
            };
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        // A store whose file is gone holds nothing, foreign records included.
        [Test]
        public void AStoreWhoseFileIsGoneListsNoForeignRecords()
        {
            var path = Seeded("gone.cc", directory: false);
            using var cache = Open(path, DeckOnly, directory: false);
            File.Delete(path);

            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.That(StoreOf(cache).ForeignRecords, Is.Empty);
        }

        // The registry's public resolution still refuses an id no registered type claims, naming the schema and the id: only a store
        // carries such a record.
        [Test]
        public void ResolvingAnUnclaimedIdThroughTheRegistryRefusesByName()
        {
            var entry = Full.GetRequired(Widget).ToCatalogEntry();

            var refusal = Assert.Throws<InvalidOperationException>(() => DeckOnly.ResolvePersistedSchema(WidgetId, new[] { entry }))!;

            Assert.That(refusal.Message, Does.Contain("tests.foreign_widget").And.Contain(WidgetId));
        }

        private static byte[] CatalogEntryBytes(string path, string schemaId) =>
            CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
            {
                SchemaCatalog = Read(path).SchemaCatalog.Where(entry => entry.SchemaId == schemaId).ToArray()
            });

        // A write of another record copies a record under an id this build does not own as the store holds it: its id, storedAt,
        // bytes and the catalog entry that publishes its id.
        [Test]
        public void AWriteOfAnotherRecordCopiesARecordUnderAnIdThisBuildDoesNotOwn([Values] Write write)
        {
            var path = OtherRuntimeStore("other-runtime.cc");
            var before = Stored(path, D);
            var entry = CatalogEntryBytes(path, OtherRuntimeId);

            using (var cache = Open(path, DeckOnly, directory: false))
            {
                Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("d"), "this build reads it");
                Land(cache, write, E, DeckOf("e"));
            }

            Assert.That(Stored(path, D), Is.EqualTo(before));
            Assert.That(CatalogEntryBytes(path, OtherRuntimeId), Is.EqualTo(entry));
            using var reopened = Open(path, DeckOnly, directory: false);
            Assert.That(EmittedDocumentTypes.Read(reopened.Get(E)!, "Name"), Is.EqualTo("e"), "the write beside the read-only record landed");
        }

        // The entry that publishes a copied record is the one the store holds when the write lands, not the one the record loaded with.
        [Test]
        public void ACopiedRecordIsPublishedByTheCatalogEntryTheStoreHoldsNow([Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = OtherRuntimeStore("redescribed.cc");
            using var cache = Open(path, DeckOnly, directory: false);
            var snapshot = Read(path);
            snapshot.SchemaCatalog.Single(entry => entry.SchemaId == OtherRuntimeId).SchemaVersion = "tests.foreign_deck.redescribed";
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            var current = CatalogEntryBytes(path, OtherRuntimeId);

            Land(cache, write, E, DeckOf("e"));

            Assert.That(CatalogEntryBytes(path, OtherRuntimeId), Is.EqualTo(current));
        }

        // A write over a foreign record is refused at the write itself, in both store kinds, not first at the flush: nothing is staged
        // and the cache never holds the record.
        [Test]
        public void AWriteOverAForeignRecordIsRefusedBeforeStaging([Values] bool directory)
        {
            var path = Seeded(directory ? "staged-dir.cc" : "staged-file.cc", directory);
            var before = Fingerprint(path);

            using (var cache = Open(path, DeckOnly, directory))
            {
                var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.UpsertAsync(Deck, DeckOf(Canary), W).GetAwaiter().GetResult())!;
                Assert.That((refusal.RecordKey, refusal.SchemaId), Is.EqualTo((W.Value, WidgetId)));
                Assert.That(refusal.Message, Does.Not.Contain(Canary));
                Assert.That(cache.IsDirty, Is.False, "nothing was staged");
                Assert.That(cache.Get(W), Is.Null);
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // A record read through the catalog's schema name under an id this build neither owns nor declares is read-only: a write of
        // it is refused typed before anything is staged, the store is left byte for byte, and the record is still served.
        [Test]
        public void ARecordReadByNameIsReadOnly([Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = OtherRuntimeStore("read-only.cc");
            var before = Fingerprint(path);

            using (var cache = Open(path, DeckOnly, directory: false))
            {
                var refusal = Assert.Throws<CultSchemaConflictException>(() => Land(cache, write, D, DeckOf("changed")))!;
                Assert.That((refusal.RecordKey, refusal.SchemaId), Is.EqualTo((D.Value, OtherRuntimeId)));
                Assert.That(refusal.SchemaNames, Is.EqualTo(new[] { "tests.foreign_deck" }), "it names the type that reads it");
                Assert.That(cache.IsDirty, Is.False);
                Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("d"));
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        public enum ReadOnlyWrite
        {
            Upsert,
            UpsertVariantAtItsKey,
            Flatten,
            Remove,
            CommitUpsert,
            CommitRemove
        }

        // Every cache write path refuses a staged write or removal of a read-only record before staging: the typed conflict, nothing
        // dirty, the store byte for byte after a later flush, and the record still served.
        [Test]
        public void EveryWritePathRefusesAReadOnlyRecord([Values] ReadOnlyWrite path)
        {
            var file = OtherRuntimeStore("every-path-" + path + ".cc", withVariant: true);
            var before = Fingerprint(file);

            using (var cache = Open(file, DeckOnly, directory: false))
            {
                var key = path is ReadOnlyWrite.UpsertVariantAtItsKey or ReadOnlyWrite.Flatten ? V : D;
                var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                {
                    switch (path)
                    {
                        case ReadOnlyWrite.Upsert:
                            cache.UpsertAsync(Deck, DeckOf("changed"), D).GetAwaiter().GetResult();
                            break;
                        case ReadOnlyWrite.UpsertVariantAtItsKey:
                            cache.UpsertVariantAsync(V, D, new[] { cache.Override(Deck, "Name", "changed") }).GetAwaiter().GetResult();
                            break;
                        case ReadOnlyWrite.Flatten:
                            cache.FlattenAsync(V).GetAwaiter().GetResult();
                            break;
                        case ReadOnlyWrite.Remove:
                            cache.Remove(D);
                            break;
                        case ReadOnlyWrite.CommitUpsert:
                            cache.Commit(batch => batch.Upsert(Deck, DeckOf("changed"), D));
                            break;
                        case ReadOnlyWrite.CommitRemove:
                            cache.Commit(batch => batch.Remove(D));
                            break;
                    }
                })!;

                Assert.That((refusal.RecordKey, refusal.SchemaId), Is.EqualTo((key.Value, OtherRuntimeId)));
                Assert.That(cache.IsDirty, Is.False, "nothing was staged");
                Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("d"));
                Assert.That(EmittedDocumentTypes.Read(cache.Get(V)!, "Name"), Is.EqualTo("v"));
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            Assert.That(Fingerprint(file), Is.EqualTo(before));
        }

        // A type that declares the id compatible owns the record: its write lands under the type's own id and reopens.
        [Test]
        public void DeclaringTheIdClaimsTheRecord()
        {
            var path = OtherRuntimeStore("declared.cc");
            var claiming = CultDocumentRegistry.ForTypes(new[] { ClaimingDeck });

            using (var cache = Open(path, claiming, directory: false))
                cache.Commit(batch => batch.Upsert(ClaimingDeck, New(ClaimingDeck, ("Name", "claimed")), D));

            Assert.That(Read(path).Records.Single().SchemaId, Is.EqualTo(claiming.GetRequired(ClaimingDeck).SchemaId));
            using var reopened = Open(path, claiming, directory: false);
            Assert.That(EmittedDocumentTypes.Read(reopened.Get(D)!, "Name"), Is.EqualTo("claimed"));
        }

        // Whether a record is read-only is read live from the registry at the write, not fixed at load: a type registered after the
        // load that declares the id makes the record writable, and the write stores it under the writing type's id, not the declarer's.
        [Test]
        public void ATypeRegisteredAfterLoadThatDeclaresTheIdMakesItWritable()
        {
            var path = OtherRuntimeStore("late.cc");
            var registry = CultDocumentRegistry.ForTypes(new[] { Deck });

            using (var cache = Open(path, registry, directory: false))
            {
                Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Upsert(Deck, DeckOf("refused"), D)));
                registry.GetRequired(LateClaimingDeck);
                cache.Commit(batch => batch.Upsert(Deck, DeckOf("claimed"), D));
            }

            Assert.That(Read(path).Records.Single().SchemaId, Is.EqualTo(registry.GetRequired(Deck).SchemaId));
            using var reopened = Open(path, DeckOnly, directory: false);
            Assert.That(EmittedDocumentTypes.Read(reopened.Get(D)!, "Name"), Is.EqualTo("claimed"));
        }

        // A commit onto the file that involves a variant is judged on the set the file will hold. A foreign record is not decoded
        // for that judgement, so it neither fails the commit nor joins the set.
        [Test]
        public void ACommitInvolvingAVariantIsNotRefusedByAForeignRecord()
        {
            var path = Seeded("variant.cc", directory: false);
            var before = Stored(path, W);
            using var cache = Open(path, DeckOnly, directory: false);
            var v = new CultRecordKey("v");
            cache.UpsertVariantAsync(v, D, new[] { cache.Override(Deck, "Name", "v") }).GetAwaiter().GetResult();
            cache.FlushAsync().GetAwaiter().GetResult();

            Land(cache, Write.ConditionalCommit, E, DeckOf("e"));

            Assert.That(Stored(path, W), Is.EqualTo(before));
            Assert.That(Read(path).Records.Select(record => record.Key), Is.EquivalentTo(new[] { "d", "e", "v", "w" }));
        }

        // A variant's base that another writer replaced with a foreign record is gone from the set the file will hold, so a commit
        // onto that file is refused as it would be if the base had been removed.
        [Test]
        public void ACommitOntoAFileWhereAForeignRecordReplacedAVariantsBaseIsRefused()
        {
            var path = Seeded("base.cc", directory: false);
            using var cache = Open(path, DeckOnly, directory: false);
            var v = new CultRecordKey("v");
            cache.UpsertVariantAsync(v, D, new[] { cache.Override(Deck, "Name", "v") }).GetAwaiter().GetResult();
            cache.FlushAsync().GetAwaiter().GetResult();
            var snapshot = Read(path);
            var widget = snapshot.Records.Single(record => record.Key == W.Value);
            snapshot.Records = snapshot.Records
                .Select(record => record.Key == D.Value
                    ? new CultPersistedRecord { Key = D.Value, SchemaId = widget.SchemaId, StoredAt = widget.StoredAt, Payload = widget.Payload }
                    : record)
                .ToArray();
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            var before = Fingerprint(path);

            var refusal = Assert.Throws(Is.InstanceOf<InvalidOperationException>(), () => cache.Commit(batch =>
            {
                batch.Expect(E, null);
                batch.Upsert(Deck, DeckOf("e"), E);
            }))!;

            Assert.That(refusal.Message, Does.Contain(v.Value).And.Contain(D.Value));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // The other writer rewrites a record under its own id, which this build reads but does not declare.
        private static void Move(string path, CultRecordKey key, string name, bool directory)
        {
            using var other = Open(path, Moving, directory);
            Assert.That(other.Commit(batch => batch.Upsert(MovingDeck, New(MovingDeck, ("Name", name)), key)), Is.True);
        }

        private static void RefusedOnDisk(CultSchemaConflictException refusal, CultRecordKey key)
        {
            Assert.Multiple(() =>
            {
                Assert.That((refusal.RecordKey, refusal.SchemaId), Is.EqualTo((key.Value, MovingId)));
                Assert.That(refusal.SchemaNames, Is.EqualTo(new[] { "tests.foreign_deck" }));
                Assert.That(refusal.Message, Does.Not.Contain(Canary), "a refusal never echoes the document it refused");
            });
        }

        // The staging half cannot see a record another writer moved to an undeclared id after this cache loaded it: the write is
        // refused where it lands, under the store's lock, in both store kinds. Nothing is written, and the cache reloads the record as
        // the store holds it, read-only, so the next write of it is refused before staging.
        [Test]
        public void AWriteOverARecordAnotherWriterMovedToAnUndeclaredIdIsRefusedAndReloadedReadOnly(
            [Values] bool directory,
            [Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = PathOf("moved.cc");
            using var cache = Open(path, DeckOnly, directory);
            cache.Commit(batch => batch.Upsert(Deck, DeckOf("d"), D));
            if (write == Write.Flush)
                cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult();
            Move(path, D, "moved", directory);
            var before = Fingerprint(path);

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
            {
                if (write == Write.Flush)
                    cache.FlushAsync().GetAwaiter().GetResult();
                else
                    cache.Commit(batch => batch.Upsert(Deck, DeckOf(Canary), D));
            })!;

            RefusedOnDisk(refusal, D);
            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("moved"), "the cache serves what the store holds");
            Assert.That(cache.IsDirty, Is.False);
            RefusedOnDisk(Assert.Throws<CultSchemaConflictException>(() => cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult())!, D);
            Assert.That(cache.IsDirty, Is.False, "the second write was refused at staging");
            cache.FlushAsync().GetAwaiter().GetResult();
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        [Test]
        public void AStagedRemovalOfARecordMovedToAnUndeclaredIdIsRefused([Values] bool directory)
        {
            var path = PathOf("moved-removal.cc");
            using var cache = Open(path, DeckOnly, directory);
            cache.Commit(batch => batch.Upsert(Deck, DeckOf("d"), D));
            Assert.That(cache.Remove(D), Is.True);
            Move(path, D, "moved", directory);
            var moved = Stored(path, D);

            RefusedOnDisk(Assert.Throws<CultSchemaConflictException>(() => cache.FlushAsync().GetAwaiter().GetResult())!, D);

            Assert.That(Stored(path, D), Is.EqualTo(moved));
            Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("moved"));
            Assert.That(cache.IsDirty, Is.False);
        }

        // A key this cache never held, which another writer created under an undeclared id after the load: the write is refused, and
        // the cache then holds the record read-only.
        [Test]
        public void ARecordThatAppearedUnderAnUndeclaredIdAtAWrittenKeyIsRefusedAndHeld([Values] bool directory)
        {
            var path = PathOf("appeared.cc");
            using var cache = Open(path, DeckOnly, directory);
            cache.Commit(batch => batch.Upsert(Deck, DeckOf("e"), E));
            Move(path, D, "appeared", directory);
            var before = Fingerprint(path);

            cache.UpsertAsync(Deck, DeckOf(Canary), D).GetAwaiter().GetResult();
            RefusedOnDisk(Assert.Throws<CultSchemaConflictException>(() => cache.FlushAsync().GetAwaiter().GetResult())!, D);

            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("appeared"));
            RefusedOnDisk(Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Upsert(Deck, DeckOf(Canary), D)))!, D);
            Assert.That(cache.IsDirty, Is.False);
        }

        // A refusal reads the record it refused: in a store holding a variant, where a write lands only onto the store as this cache
        // last read it, the next write of another record lands at once instead of being refused for the record already refused.
        [Test]
        public void AfterAnUndeclaredRefusalTheNextWriteIntoAVariantStoreLands([Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = PathOf("variant-store.cc");
            using var cache = Open(path, DeckOnly, directory: false);
            cache.Commit(batch =>
            {
                batch.Upsert(Deck, DeckOf("d"), D);
                batch.Upsert(Deck, DeckOf("e"), E);
                batch.UpsertVariant(V, E, new[] { cache.Override(Deck, "Name", "v") });
            });
            Move(path, D, "moved", directory: false);

            RefusedOnDisk(Assert.Throws<CultSchemaConflictException>(() => Land(cache, write, D, DeckOf(Canary)))!, D);
            Land(cache, write, new CultRecordKey("f"), DeckOf("f"));

            Assert.That(Read(path).Records.Select(record => record.Key), Is.EquivalentTo(new[] { "d", "e", "f", "v" }));
            Assert.That(Read(path).Records.Single(record => record.Key == D.Value).SchemaId, Is.EqualTo(MovingId));
        }

        public enum DirectWrite
        {
            Push,
            Delete,
            CommitUpsert,
            CommitDelete
        }

        // The store's own write API reaches no staging refusal: a read-only record written through it directly is refused where the
        // write lands, in both store kinds, and the store keeps the other writer's record under its id.
        [Test]
        public void ADirectStoreWriteOfAReadOnlyRecordIsRefusedAndNeverRelabelsIt([Values] bool directory, [Values] DirectWrite write)
        {
            var path = PathOf("direct.cc");
            Move(path, D, "theirs", directory);
            var before = Fingerprint(path);
            using var cache = Open(path, DeckOnly, directory);
            var store = StoreOf(cache);
            var mine = new CultStoredDocument(D, DateTimeOffset.UtcNow.ToString("O"), DeckOnly.GetRequired(Deck), DeckOf(Canary));
            var held = cache.GetStored(D)!;

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
            {
                switch (write)
                {
                    case DirectWrite.Push:
                        store.Push(mine);
                        store.PushAll();
                        break;
                    case DirectWrite.Delete:
                        store.Delete(held);
                        store.PushAll();
                        break;
                    case DirectWrite.CommitUpsert:
                        store.CommitBatch(new CultCommitRequest(new[] { mine }, Array.Empty<CultStoredDocument>(), Array.Empty<(CultRecordKey, string?)>(), false), wait: true);
                        break;
                    case DirectWrite.CommitDelete:
                        store.CommitBatch(new CultCommitRequest(Array.Empty<CultStoredDocument>(), new[] { held }, Array.Empty<(CultRecordKey, string?)>(), false), wait: true);
                        break;
                }
            })!;

            RefusedOnDisk(refusal, D);
            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(Read(path).Records.Single().SchemaId, Is.EqualTo(MovingId), "never relabelled");
            Assert.That(store.IsDirty, Is.False);
            Assert.That(EmittedDocumentTypes.Read(cache.Get(D)!, "Name"), Is.EqualTo("theirs"));
        }

        // A removal of a foreign record, which the cache does not hold, is refused typed: the caller is never told it removed what the
        // store still carries. The store is read as it is at the removal, so a foreign record another writer added after the load
        // is refused too, and listed foreign from then on.
        [Test]
        public void RemovingAForeignRecordIsRefusedNotReportedAsDone([Values] bool directory, [Values] bool batch, [Values] bool appearedAfterLoad)
        {
            var path = PathOf(directory ? "remove-foreign-dir.cc" : "remove-foreign-file.cc");
            using (var seed = Open(path, Full, directory))
            {
                seed.Commit(stage => stage.Upsert(Deck, DeckOf("d"), D));
                if (!appearedAfterLoad)
                    seed.Commit(stage => stage.Upsert(Widget, WidgetOf("w"), W));
            }

            using (var cache = Open(path, DeckOnly, directory))
            {
                if (appearedAfterLoad)
                {
                    using var other = Open(path, Full, directory);
                    other.Commit(stage => stage.Upsert(Widget, WidgetOf("w"), W));
                }

                var before = Fingerprint(path);
                var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                {
                    if (batch)
                        cache.Commit(stage => stage.Remove(W));
                    else
                        cache.Remove(W);
                })!;
                Assert.That((refusal.RecordKey, refusal.SchemaId), Is.EqualTo((W.Value, WidgetId)));
                Assert.That(cache.IsDirty, Is.False);
                Assert.That(StoreOf(cache).ForeignRecords.Select(record => record.Key), Is.EqualTo(new[] { "w" }));
                Assert.That(cache.Remove(new CultRecordKey("absent")), Is.False, "a key nothing holds is still no removal");
                Assert.That(Fingerprint(path), Is.EqualTo(before));
            }
        }
    }
}
