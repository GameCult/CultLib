#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;

namespace GameCult.Caching.Tests
{
    // A single-file store file is read and written without a cache through ReadStoreSnapshot and WriteRecord: the same reader, lock,
    // atomic replace and header rule a cache's store uses. CultMesh's single-file document helpers are built on these two members.
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
    }
}
