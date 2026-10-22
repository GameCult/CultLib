#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;

namespace GameCult.Caching.Tests
{
    // A single-file store file is read and written without a cache through ReadStoreSnapshot, WriteRecord and WriteDocument: the same reader, lock,
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

        private static CultCodec Codec(CultDocumentRegistry? registry = null) =>
            CultCacheMessagePack.CreateCodec(registry ?? CultDocumentRegistry.Shared);

        [CultDocument("tests.cacheless.base", "tests.cacheless.base.v1")]
        [MessagePackObject]
        public class DeclaredBase
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [MessagePackObject]
        public sealed class UndeclaredDerived : DeclaredBase
        {
            [Key(1)] public string Extra { get; set; } = "";
        }

        // Reads the store file as a SingleFileMessagePackBackingStore does, and holds the first reader inside ReadStore so a test can
        // see whether a second one gets in beside it.
        private sealed class GatedStore : SingleFileBackingStore
        {
            private int _held;
            private int _inside;
            private int _mostInside;
            public readonly ManualResetEventSlim Entered = new();
            public readonly ManualResetEventSlim Release = new();
            public CultDocumentRegistry? RegistryBeforeWait;
            public CultDocumentRegistry? RegistryAfterWait;

            public GatedStore(string path) : base(path) { }

            public int MostInside => Volatile.Read(ref _mostInside);

            protected override byte[] SerializeStore(string formatVersion, System.Collections.Generic.IReadOnlyList<byte[]> catalogEntries, System.Collections.Generic.IReadOnlyList<byte[]> records) =>
                CultDocumentMessagePackSerialization.SerializeStore(formatVersion, catalogEntries, records);

            protected override byte[] SerializeRecord(CultPersistedRecord record) =>
                CultDocumentMessagePackSerialization.SerializePersistedRecord(record);

            protected override byte[] SerializeCatalogEntry(CultSchemaCatalogEntry entry) =>
                CultDocumentMessagePackSerialization.SerializeCatalogEntry(entry);

            protected override (CultPersistedStoreSnapshot Snapshot, byte[][] CatalogBytes, byte[][] RecordBytes) ReadStore(byte[] data)
            {
                var inside = Interlocked.Increment(ref _inside);
                int seen;
                while ((seen = MostInside) < inside && Interlocked.CompareExchange(ref _mostInside, inside, seen) != seen) { }
                Entered.Set();
                try
                {
                    if (Interlocked.Exchange(ref _held, 1) == 0)
                    {
                        RegistryBeforeWait = Registry;
                        Release.Wait(TimeSpan.FromSeconds(30));
                        RegistryAfterWait = Registry;
                    }
                    var store = CultDocumentMessagePackSerialization.ReadStore(data);
                    CultDocumentMessagePackSerialization.RequireSingleFileFormat(store.Snapshot);
                    return store;
                }
                finally
                {
                    Interlocked.Decrement(ref _inside);
                }
            }

            protected override byte[] SerializePayload(object document, Type documentType) =>
                CultDocumentMessagePackSerialization.SerializeUntyped(document, documentType, Registry);

            protected override object DeserializePayload(Type documentType, byte[] payload) =>
                CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload, Registry);
        }

        // ReadStoreSnapshot takes the store's gate: while one read is inside the reader, a second does not enter beside it.
        [Test]
        public async Task ReadStoreSnapshotTakesTheStoresGate()
        {
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            var store = new GatedStore(_path);

            var first = Task.Run(() => store.ReadStoreSnapshot());
            Assert.That(store.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first read reached the reader");
            var second = Task.Run(() => store.ReadStoreSnapshot());
            await Task.Delay(500);
            var mostInside = store.MostInside;
            store.Release.Set();
            await Task.WhenAll(first, second);

            Assert.That(mostInside, Is.EqualTo(1), "a second read waits at the gate while the first is inside the reader");
            Assert.That(first.Result!.Records, Has.Length.EqualTo(1));
            Assert.That(second.Result!.Records, Has.Length.EqualTo(1));
        }

        // A call's registry is attached under the store's gate: a second call with another registry waits for the first, so the
        // first sees its own registry from its start to its end.
        [Test]
        public async Task ACallKeepsItsRegistryWhileAnotherCallWaits()
        {
            var mine = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var other = CultDocumentRegistry.ForTypes(new[] { typeof(DeclaredBase) });
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            var store = new GatedStore(_path);

            var first = Task.Run(() => store.WriteDocument(new CultRecordKey("first"), new IdDeck { Name = "first" }, registry: mine));
            Assert.That(store.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first call reached the reader");
            var second = Task.Run(() => store.WriteDocument(new CultRecordKey("second"), new DeclaredBase { Name = "second" }, registry: other));
            await Task.Delay(500);
            store.Release.Set();
            await Task.WhenAll(first, second);

            Assert.That(store.RegistryBeforeWait, Is.SameAs(mine));
            Assert.That(store.RegistryAfterWait, Is.SameAs(mine), "the second call's registry did not replace the first's mid-call");
            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records.Select(record => record.Key), Is.EquivalentTo(new[] { "a", "first", "second" }));
        }

        // WriteDocument<T> stores the document as T: the schema id, the id plan, the header and the bytes all come from the one
        // declared type, as they did when CultMesh encoded typeof(T).
        [Test]
        public void WriteDocumentEncodesTheDeclaredTypeNotTheRuntimeType()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(DeclaredBase) });

            new SingleFileMessagePackBackingStore(_path).WriteDocument<DeclaredBase>(new CultRecordKey("d"), new UndeclaredDerived { Name = "n", Extra = "x" }, registry: registry);

            var record = new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records.Single();
            var asBase = CultDocumentMessagePackSerialization.SerializeUntyped(new DeclaredBase { Name = "n" }, typeof(DeclaredBase), registry);
            Assert.That(record.SchemaId, Is.EqualTo(registry.GetRequired<DeclaredBase>().SchemaId));
            Assert.That(record.Payload, Is.EqualTo(asBase), "base bytes under the base schema: one slot, not the derived type's two");
        }

        // The registry is the call's argument. A typed call on a store instance leaves nothing behind, so a later WriteRecord that
        // gives none decides the header as a fresh instance does.
        [Test]
        public async Task ATypedCallLeavesNoRegistryOnTheStore()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            using (var cache = new CultCache(registry))
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(_path));
                await cache.AddAsync(new IdDeck { Name = "deck", Reels = { new IdReel { Label = "reel" } } });
                cache.FlushAllBackingStores();
            }
            void Unmark()
            {
                var written = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_path));
                written.FormatVersion = CultPersistedStoreSnapshot.FormatV1;
                File.WriteAllBytes(_path, CultDocumentMessagePackSerialization.SerializeSnapshot(written));
            }
            Unmark();
            var store = new SingleFileMessagePackBackingStore(_path);

            store.WriteDocument(new CultRecordKey("again"), new IdDeck { Name = "again" }, registry: registry);
            Assert.That(store.ReadStoreSnapshot()!.FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3), "the typed call, given a registry, read the copied ids");
            Unmark();
            store.WriteRecord(Record("other", One, 1), One, holdsIds: false, registry: null);

            Assert.That(store.ReadStoreSnapshot()!.FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV1), "a write given no registry decides nothing for copied records");
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
            var elsewhere = CultDocumentRegistry.ForTypes(new[] { typeof(StoreRoutingTests.RoutingNote) });

            var error = Assert.Throws<InvalidOperationException>(() => store.WriteDocument(new CultRecordKey("deck"), new IdDeck { Name = "d" }, registry: elsewhere))!;

            Assert.That(error.Message, Does.Contain("write through the cache"));
        }

        [Test]
        public void WriteDocumentRefusesANullDocumentAndDecodeDocumentANullPayload()
        {
            var store = new SingleFileMessagePackBackingStore(_path);
            Assert.Throws<ArgumentNullException>(() => store.WriteDocument<IdDeck>(new CultRecordKey("deck"), null!));
            Assert.Throws<ArgumentNullException>(() => Codec().DecodeDocument<IdDeck>(new CultRecordKey("deck"), null!));
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
            Assert.That(records["now"].StoredAt, Does.Match(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}\+00:00$"), "round-trip format");
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
            var first = Codec(registry).DecodeDocument<IdDeck>(new CultRecordKey("deck"), unset).Reels[0].Id;
            var second = Codec(registry).DecodeDocument<IdDeck>(new CultRecordKey("deck"), unset).Reels[0].Id;
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
            var read = Codec(registry).DecodeDocument<IdDeck>(new CultRecordKey("deck"), snapshot.Records.Single(record => record.Key == "deck").Payload);
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
        public void WriteDocumentRefusesOnAStoreAttachedToACache()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var store = new SingleFileMessagePackBackingStore(_path);
            using var cache = new CultCache(registry);
            cache.AddBackingStore(store);

            Assert.That(Assert.Throws<InvalidOperationException>(() => store.WriteDocument(new CultRecordKey("deck"), new IdDeck { Name = "d" }))!.Message, Does.Contain("write through the cache"));
            Assert.That(File.Exists(_path), Is.False);
        }
    }
}
