#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;

namespace GameCult.Caching.Tests
{
    // A single-file store file is read and written without a cache through ReadStoreSnapshot, WriteRecord, WriteDocument and DecodeDocument: the same reader, lock,
    // atomic replace and header rule a cache's store uses. CultMesh's single-file document helpers are built on these members.
    public class CachelessSingleFileTests
    {
        private static readonly CultSchemaCatalogEntry One = new() { SchemaId = "tests.cacheless.one", SchemaName = "tests.cacheless.one" };
        private static readonly CultSchemaCatalogEntry Two = new() { SchemaId = "tests.cacheless.two", SchemaName = "tests.cacheless.two" };
        private string _directory = "";
        private string _path = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-cacheless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "store.cc");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private static CultPersistedRecord Record(string key, CultSchemaCatalogEntry entry, params byte[] payload) =>
            new() { Key = key, SchemaId = entry.SchemaId, StoredAt = "2026-10-22T00:00:00.0000000+00:00", Payload = payload };

        [Test]
        public void WriteRecordKeepsAnotherWritersRecord()
        {
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("b", Two, 2), Two);
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 3), One);

            var snapshot = new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!;

            Assert.That(snapshot.Records, Has.Length.EqualTo(2));
            Assert.That(Array.Find(snapshot.Records, record => record.Key == "a")!.Payload, Is.EqualTo(new byte[] { 3 }));
            Assert.That(Array.Find(snapshot.Records, record => record.Key == "b")!.Payload, Is.EqualTo(new byte[] { 2 }));
            Assert.That(snapshot.SchemaCatalog, Has.Length.EqualTo(2));
        }

        [Test]
        public void WriteRecordRefusesOnAStoreAttachedToACache()
        {
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            var before = File.ReadAllBytes(_path);
            var store = new SingleFileMessagePackBackingStore(_path);
            using var cache = new CultCache();
            cache.AddBackingStore(store);

            var error = Assert.Throws<InvalidOperationException>(() => store.WriteRecord(Record("b", Two, 2), Two))!;

            Assert.That(error.Message, Does.Contain("write through the cache"));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void ReadStoreSnapshotOfNothingIsNull()
        {
            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot(), Is.Null);
        }

        [Test]
        public void ReadStoreSnapshotRefusesAV4Stamp()
        {
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_path));
            snapshot.FormatVersion = "cultcache.store.v4";
            File.WriteAllBytes(_path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            var error = Assert.Throws<CultStoreUnreadableException>(() => new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot())!;

            Assert.That(error.Message, Does.Contain("cultcache.store.v4"));
        }

        [Test]
        public void WriteRecordWithIdsMarksV3()
        {
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.FormatVersion, Is.Not.EqualTo(CultPersistedStoreSnapshot.FormatV3));

            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("b", Two, 2), Two, holdsIds: true);

            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
        }

        [Test]
        public void WriteRecordRefusesNullArguments()
        {
            var store = new SingleFileMessagePackBackingStore(_path);

            Assert.Throws<ArgumentNullException>(() => store.WriteRecord(null!, One));
            Assert.Throws<ArgumentNullException>(() => store.WriteRecord(Record("a", One, 1), null!));
            Assert.That(File.Exists(_path), Is.False);
        }

        [Test]
        public void ARefusedWriteDocumentOnAnAttachedStoreNeverReachesTheRegistryItWasGiven()
        {
            var store = new SingleFileMessagePackBackingStore(_path);
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));
            cache.AddBackingStore(store);
            var elsewhere = CultDocumentRegistry.ForTypes(new[] { typeof(IdReel) });

            var error = Assert.Throws<InvalidOperationException>(() => store.WriteDocument(new CultRecordKey("deck"), new IdDeck { Name = "d" }, registry: elsewhere))!;

            Assert.That(error.Message, Does.Contain("write through the cache"));
        }

        [Test]
        public void WriteDocumentRefusesANullDocumentAndDecodeDocumentANullPayload()
        {
            var store = new SingleFileMessagePackBackingStore(_path);
            Assert.Throws<ArgumentNullException>(() => store.WriteDocument<IdDeck>(new CultRecordKey("deck"), null!));
            Assert.Throws<ArgumentNullException>(() => store.DecodeDocument<IdDeck>(new CultRecordKey("deck"), null!));
        }

        [Test]
        public void WriteDocumentStoresTheGivenStoredAtAndOtherwiseNow()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var store = new SingleFileMessagePackBackingStore(_path);
            store.WriteDocument(new CultRecordKey("fixed"), new IdDeck { Name = "a" }, "2026-01-02T03:04:05.0000000+00:00", registry);
            store.WriteDocument(new CultRecordKey("now"), new IdDeck { Name = "b" }, null, registry);
            store.WriteDocument(new CultRecordKey("blank"), new IdDeck { Name = "c" }, "  ", registry);

            var records = new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records.ToDictionary(record => record.Key);

            Assert.That(records["fixed"].StoredAt, Is.EqualTo("2026-01-02T03:04:05.0000000+00:00"));
            Assert.That(DateTimeOffset.Parse(records["now"].StoredAt), Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(5)));
            Assert.That(DateTimeOffset.Parse(records["blank"].StoredAt), Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void WriteDocumentMintsRandomIdsAndDecodeDocumentMintsTheSameIdsEveryTime()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            string Written(string name)
            {
                var path = Path.Combine(_directory, name);
                var deck = new IdDeck { Name = "deck", Reels = { new IdReel { Label = "reel" } } };
                new SingleFileMessagePackBackingStore(path).WriteDocument(new CultRecordKey("deck"), deck, registry: registry);
                return deck.Reels[0].Id;
            }
            Assert.That(Written("one.cc"), Is.Not.EqualTo(Written("two.cc")), "a write mints random ids");

            var unset = CultDocumentMessagePackSerialization.SerializeUntyped(new IdDeck { Name = "deck", Reels = { new IdReel { Label = "reel" } } }, typeof(IdDeck), registry);
            var store = new SingleFileMessagePackBackingStore(_path);
            var first = store.DecodeDocument<IdDeck>(new CultRecordKey("deck"), unset, registry).Reels[0].Id;
            var second = new SingleFileMessagePackBackingStore(_path).DecodeDocument<IdDeck>(new CultRecordKey("deck"), unset, registry).Reels[0].Id;
            Assert.That(first, Does.Match("^[0-9a-f]{12}$"));
            Assert.That(second, Is.EqualTo(first), "a load mints from the key and path, so every reader mints the same id");
        }

        // The write is under the store's lock and waits for it: a write that finds the lock held lands after it is released, and
        // never without it.
        [Test]
        public async Task WriteRecordWaitsForTheStoreLock()
        {
            var write = default(Task);
            using (new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                write = Task.Run(() => new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One));
                await Task.Delay(500);
                Assert.That(write.IsCompleted, Is.False, "the write waits while another holder has the lock");
                Assert.That(File.Exists(_path), Is.False);
            }

            await write;

            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records, Has.Length.EqualTo(1));
        }

        // The registry the payload was encoded with lets the write decide the header for the records it copies: another runtime may
        // have written ids under an unmarked header, and a write that can read them marks the store. Without a registry it decides
        // nothing for them.
        [TestCase(true, CultPersistedStoreSnapshot.FormatV3)]
        [TestCase(false, CultPersistedStoreSnapshot.FormatV1)]
        public async Task WriteRecordGivenARegistryMarksAStoreWhoseCopiedRecordHoldsIds(bool withRegistry, string expected)
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            using (var cache = new CultCache(registry))
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(_path));
                await cache.AddAsync(new IdDeck { Name = "deck", Reels = { new IdReel { Label = "reel" } } });
                cache.FlushAllBackingStores();
            }

            var written = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_path));
            Assert.That(written.FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3), "the cache marked the store it wrote ids into");
            written.FormatVersion = CultPersistedStoreSnapshot.FormatV1;
            File.WriteAllBytes(_path, CultDocumentMessagePackSerialization.SerializeSnapshot(written));

            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("other", One, 1), One, holdsIds: false, registry: withRegistry ? registry : null);

            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.FormatVersion, Is.EqualTo(expected));
        }

        [Test]
        public void WriteDocumentMintsIdsMarksV3AndKeepsTheOtherRecord()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("other", One, 1), One);
            var deck = new IdDeck { Name = "deck", Reels = { new IdReel { Label = "reel" } } };

            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("deck"), deck, registry: registry);

            var snapshot = new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!;
            Assert.That(snapshot.FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
            Assert.That(snapshot.Records.Select(record => record.Key), Is.EquivalentTo(new[] { "other", "deck" }));
            Assert.That(deck.Reels[0].Id, Does.Match("^[0-9a-f]{12}$"), "the id the file holds was minted on the document");
            var read = new SingleFileMessagePackBackingStore(_path).DecodeDocument<IdDeck>(new CultRecordKey("deck"), snapshot.Records.Single(record => record.Key == "deck").Payload, registry);
            Assert.That(read.Reels[0].Id, Is.EqualTo(deck.Reels[0].Id));
        }

        [Test]
        public void WriteDocumentGivesMintedIdsBackWhenTheWriteIsRefused()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            File.WriteAllBytes(_path, new byte[] { 0xC1 });
            var deck = new IdDeck { Name = "deck", Reels = { new IdReel { Label = "reel" } } };

            Assert.Throws<CultStoreUnreadableException>(() =>
                new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("deck"), deck, registry: registry));

            Assert.That(deck.Reels[0].Id, Is.Null.Or.Empty, "a refused write leaves the document as it was");
        }

        [Test]
        public void WriteDocumentAndDecodeDocumentRefuseOnAStoreAttachedToACache()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var store = new SingleFileMessagePackBackingStore(_path);
            using var cache = new CultCache(registry);
            cache.AddBackingStore(store);

            Assert.That(Assert.Throws<InvalidOperationException>(() => store.WriteDocument(new CultRecordKey("deck"), new IdDeck { Name = "d" }))!.Message, Does.Contain("write through the cache"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => store.DecodeDocument<IdDeck>(new CultRecordKey("deck"), new byte[] { 0x90 }))!.Message, Does.Contain("read through the cache"));
            Assert.That(File.Exists(_path), Is.False);
        }
    }
}
