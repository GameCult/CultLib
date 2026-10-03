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
        private static readonly Type Widget = Emit("ForeignSuiteWidget", "tests.foreign_widget", "tests.foreign_widget.v1", new[] { new Field("Label", typeof(string), 0, IsName: true) });

        // A build that has both types, and one that has only the deck: to it the widget is foreign.
        private static readonly CultDocumentRegistry Full = CultDocumentRegistry.ForTypes(new[] { Deck, Widget });
        private static readonly CultDocumentRegistry DeckOnly = CultDocumentRegistry.ForTypes(new[] { Deck });
        private static readonly string WidgetId = Full.GetRequired(Widget).SchemaId;

        private static readonly CultRecordKey D = new("d");
        private static readonly CultRecordKey E = new("e");
        private static readonly CultRecordKey W = new("w");
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
        // cache's record over it is refused; the file keeps the other writer's record.
        [TestCase(false)]
        [TestCase(true)]
        public void AWriteOverARecordAnotherWriterMadeForeignIsRefused(bool directory)
        {
            var path = Seeded("replaced.cc", directory);
            using var cache = Open(path, DeckOnly, directory);
            using (var other = Open(path, Full, directory))
                other.Commit(batch => batch.Upsert(Widget, WidgetOf("now a widget"), D));
            var before = Fingerprint(path);

            var refusal = Assert.Throws<CultSchemaConflictException>(() => Land(cache, Write.Flush, D, DeckOf(Canary)))!;

            Assert.That(refusal.RecordKey, Is.EqualTo(D.Value));
            Assert.That(refusal.Message, Does.Not.Contain(Canary));
            Assert.That(Fingerprint(path), Is.EqualTo(before));
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
        // carries one. Without one, a whole-view write decides by what it writes, and nothing here holds an id.
        [Test]
        public void AMarkedHeaderStaysMarkedWhileTheStoreCarriesAForeignRecord(
            [Values(Write.Flush, Write.Commit)] Write write,
            [Values] bool carriesForeign)
        {
            var path = Seeded("marked.cc", directory: false);
            var snapshot = Read(path);
            snapshot.FormatVersion = CultPersistedStoreSnapshot.FormatV3;
            if (!carriesForeign)
            {
                snapshot.Records = snapshot.Records.Where(record => record.Key != W.Value).ToArray();
                snapshot.SchemaCatalog = snapshot.SchemaCatalog.Where(entry => entry.SchemaId != WidgetId).ToArray();
            }

            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            using (var cache = Open(path, DeckOnly, directory: false))
                Land(cache, write, E, DeckOf("e"));

            Assert.That(Read(path).FormatVersion, Is.EqualTo(carriesForeign ? CultPersistedStoreSnapshot.FormatV3 : CultPersistedStoreSnapshot.FormatV1));
        }

        private const string OtherRuntimeId = "other.runtime.deck";

        // A deck another runtime wrote under its own id for the deck's schema. This build reads it through the catalog's schema name,
        // but does not own the id.
        private string OtherRuntimeStore(string name)
        {
            var path = PathOf(name);
            using (var cache = Open(path, DeckOnly, directory: false))
                cache.Commit(batch => batch.Upsert(Deck, DeckOf("d"), D));
            var snapshot = Read(path);
            var entry = snapshot.SchemaCatalog.Single();
            entry.SchemaId = OtherRuntimeId;
            entry.ContentHash = OtherRuntimeId;
            entry.CompatibleSchemaIds = new[] { OtherRuntimeId };
            snapshot.Records.Single().SchemaId = OtherRuntimeId;
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

        // A write of the record itself stores what the cache holds, under the type's own id.
        [Test]
        public void AWriteOfTheRecordItselfStoresItUnderTheTypesOwnId([Values(Write.Flush, Write.Commit)] Write write)
        {
            var path = OtherRuntimeStore("rewritten.cc");

            using (var cache = Open(path, DeckOnly, directory: false))
                Land(cache, write, D, DeckOf("changed"));

            Assert.That(Read(path).Records.Single().SchemaId, Is.EqualTo(DeckOnly.GetRequired(Deck).SchemaId));
            using var reopened = Open(path, DeckOnly, directory: false);
            Assert.That(EmittedDocumentTypes.Read(reopened.Get(D)!, "Name"), Is.EqualTo("changed"));
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
    }
}
