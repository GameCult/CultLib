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

        [CultDocument("tests.cacheless.id_base", "tests.cacheless.id_base.v1")]
        [MessagePackObject]
        public class IdBase
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [MessagePackObject]
        public sealed class IdDerived : IdBase
        {
            [Key(1)] public System.Collections.Generic.List<IdReel> Reels { get; set; } = new();
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
            public CultDocumentRegistry? Attached => AttachedRegistry;
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
                        RegistryBeforeWait = AttachedRegistry;
                        Release.Wait(TimeSpan.FromSeconds(30));
                        RegistryAfterWait = AttachedRegistry;
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

        // An attach during a cacheless call waits for it: the call finishes on the registry it was given, the store then gains the
        // cache's registry, and the cache's next flush works.
        [Test]
        public async Task AnAttachDuringACachelessCallWaitsForIt()
        {
            var mine = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var cacheRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: mine);
            var store = new GatedStore(_path);

            var write = Task.Run(() => store.WriteDocument(new CultRecordKey("first"), new IdDeck { Name = "first" }, registry: mine));
            Assert.That(store.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the call reached the reader");
            using var cache = new CultCache(cacheRegistry);
            var attach = Task.Run(() => cache.AddBackingStore(store));
            Assert.That(attach.Wait(TimeSpan.FromMilliseconds(500)), Is.False, "the attach waits while a cacheless call is inside the store");
            store.Release.Set();
            await write;
            await attach;

            Assert.That(store.RegistryAfterWait, Is.SameAs(mine), "the call kept its own registry to its end");
            Assert.That(store.Attached, Is.SameAs(cacheRegistry), "the call's end did not detach the cache's registry");
            await cache.AddAsync(new IdDeck { Name = "b" });
            Assert.DoesNotThrow(() => cache.FlushAllBackingStores());
        }

        // A cacheless call that finds the store attached by the time it holds the gate is refused, and leaves the cache's registry.
        [Test]
        public void ACachelessCallOnAnAttachedStoreIsRefusedAndLeavesTheRegistry()
        {
            var cacheRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var store = new GatedStore(_path);
            store.Release.Set();
            using var cache = new CultCache(cacheRegistry);
            cache.AddBackingStore(store);

            Assert.Throws<InvalidOperationException>(() => store.WriteDocument(new CultRecordKey("x"), new IdDeck { Name = "x" }));
            Assert.Throws<InvalidOperationException>(() => store.WriteRecord(Record("a", One, 1), One));
            Assert.That(store.Attached, Is.SameAs(cacheRegistry));
        }

        // A store whose hooks fire by the role of the thread inside it, so a test can hold an attach and a cacheless call at chosen
        // points: the attach inside its pull (and make that pull fail), a call inside its read or inside its payload encoding.
        private sealed class RoleStore : SingleFileBackingStore
        {
            [ThreadStatic] public static string? Role;
            public bool FailAttachPull;
            public readonly ManualResetEventSlim AttachInside = new();
            public readonly ManualResetEventSlim ReleaseAttach = new();
            public readonly ManualResetEventSlim CallInside = new();
            public readonly ManualResetEventSlim ReleaseCall = new();
            public CultDocumentRegistry? Attached => AttachedRegistry;

            public RoleStore(string path) : base(path) { }

            protected override byte[] SerializeStore(string formatVersion, System.Collections.Generic.IReadOnlyList<byte[]> catalogEntries, System.Collections.Generic.IReadOnlyList<byte[]> records) =>
                CultDocumentMessagePackSerialization.SerializeStore(formatVersion, catalogEntries, records);

            protected override byte[] SerializeRecord(CultPersistedRecord record) =>
                CultDocumentMessagePackSerialization.SerializePersistedRecord(record);

            protected override byte[] SerializeCatalogEntry(CultSchemaCatalogEntry entry) =>
                CultDocumentMessagePackSerialization.SerializeCatalogEntry(entry);

            protected override (CultPersistedStoreSnapshot Snapshot, byte[][] CatalogBytes, byte[][] RecordBytes) ReadStore(byte[] data)
            {
                if (Role == "attach")
                {
                    AttachInside.Set();
                    ReleaseAttach.Wait(TimeSpan.FromSeconds(30));
                    if (FailAttachPull)
                        throw new InvalidOperationException("the attach's pull fails");
                }
                else if (Role == "call-in-read")
                {
                    CallInside.Set();
                    ReleaseCall.Wait(TimeSpan.FromSeconds(30));
                }
                var store = CultDocumentMessagePackSerialization.ReadStore(data);
                CultDocumentMessagePackSerialization.RequireSingleFileFormat(store.Snapshot);
                return store;
            }

            protected override byte[] SerializePayload(object document, Type documentType)
            {
                if (Role == "call-in-payload")
                {
                    CallInside.Set();
                    ReleaseCall.Wait(TimeSpan.FromSeconds(30));
                }
                return CultDocumentMessagePackSerialization.SerializeUntyped(document, documentType, Registry);
            }

            protected override object DeserializePayload(Type documentType, byte[] payload) =>
                CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload, Registry);
        }

        // Runs body on its own thread under a role, and returns the thread with whatever it threw.
        private static (Thread Thread, Func<Exception?> Error) OnThread(string role, Action body)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                RoleStore.Role = role;
                try { body(); }
                catch (Exception e) { error = e; }
                finally { RoleStore.Role = null; }
            });
            thread.Start();
            return (thread, () => error);
        }

        private static void WaitUntilBlocked(Thread thread) =>
            Assert.That(SpinWait.SpinUntil(() => thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10)), Is.True, "the thread is blocked at a gate");

        // A cacheless call that arrives during an attach waits for the attach to end, whatever the attach does. The call's thread blocks
        // on the store's gate (it does not read Cache first and queue on the cache's gate), so when the pull fails the call runs, and the
        // next attach waits for it; when the pull succeeds the call finds the cache and is refused, leaving the cache's registry.
        [TestCase(true)]
        [TestCase(false)]
        public async Task ACallArrivingDuringAnAttachWaitsForTheAttachToEnd(bool theAttachFails)
        {
            var callRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var registry1 = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var registry2 = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: callRegistry);
            var store = new RoleStore(_path) { FailAttachPull = theAttachFails };
            using var cache1 = new CultCache(registry1);
            var attach = OnThread("attach", () => cache1.AddBackingStore(store));
            Assert.That(store.AttachInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the attach reached its pull");
            var call = OnThread("call-in-read", () => store.WriteDocument(new CultRecordKey("first"), new IdDeck { Name = "first" }, registry: callRegistry));
            WaitUntilBlocked(call.Thread);
            Assert.That(store.CallInside.IsSet, Is.False, "the call waits while the attach is inside the store");

            store.ReleaseAttach.Set();
            attach.Thread.Join();
            Assert.That(attach.Error(), theAttachFails ? Is.InstanceOf<InvalidOperationException>() : Is.Null);

            if (!theAttachFails)
            {
                call.Thread.Join();
                Assert.That(call.Error(), Is.InstanceOf<InvalidOperationException>(), "the attach succeeded, so the call is refused");
                Assert.That(store.CallInside.IsSet, Is.False);
                Assert.That(store.Attached, Is.SameAs(registry1), "the refused call left the cache's registry");
                await cache1.AddAsync(new IdDeck { Name = "b" });
                Assert.DoesNotThrow(() => cache1.FlushAllBackingStores());
                return;
            }

            Assert.That(store.CallInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the attach failed, so the call runs");
            using var cache2 = new CultCache(registry2);
            var secondAttach = Task.Run(() => cache2.AddBackingStore(store));
            Assert.That(secondAttach.Wait(TimeSpan.FromMilliseconds(500)), Is.False, "the next attach waits while the call is inside the store");
            store.ReleaseCall.Set();
            call.Thread.Join();
            await secondAttach;

            Assert.That(call.Error(), Is.Null);
            Assert.That(store.Attached, Is.SameAs(registry2), "the call's end did not detach the second cache's registry");
            await cache2.AddAsync(new IdDeck { Name = "c" });
            Assert.DoesNotThrow(() => cache2.FlushAllBackingStores());
            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records.Select(record => record.Key), Is.SupersetOf(new[] { "a", "first", "c" }));
        }

        // The attach waits for a call that is already inside the store, wherever in the call it is: here inside the payload encoding,
        // before the file is read. The call ends on its own registry, then the cache owns the store and its registry.
        [Test]
        public async Task AnAttachDuringACallEncodingItsPayloadWaitsForIt()
        {
            var callRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var cacheRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var store = new RoleStore(_path);
            var call = OnThread("call-in-payload", () => store.WriteDocument(new CultRecordKey("first"), new IdDeck { Name = "first" }, registry: callRegistry));
            Assert.That(store.CallInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the call reached its payload encoding");
            using var cache = new CultCache(cacheRegistry);
            var attach = Task.Run(() => cache.AddBackingStore(store));
            Assert.That(attach.Wait(TimeSpan.FromMilliseconds(500)), Is.False, "the attach waits while a call is inside the store");

            store.ReleaseCall.Set();
            call.Thread.Join();
            await attach;

            Assert.That(call.Error(), Is.Null);
            Assert.That(store.Attached, Is.SameAs(cacheRegistry), "the call's end did not detach the cache's registry");
            await cache.AddAsync(new IdDeck { Name = "b" });
            Assert.DoesNotThrow(() => cache.FlushAllBackingStores());
        }

        // An attach the cache refuses before it pulls leaves the store as it found it: no cache, no registry, and a cacheless call
        // still runs.
        [Test]
        public void ARefusedAttachLeavesTheStoreCacheless()
        {
            var cacheRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            using var cache = new CultCache(cacheRegistry);
            cache.AddBackingStore(new RoleStore(Path.Combine(_directory, "first.cc")));
            var second = new RoleStore(_path);

            Assert.Throws<InvalidOperationException>(() => cache.AddBackingStore(second), "a second untyped store is refused");
            Assert.DoesNotThrow(() => second.WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: cacheRegistry));

            Assert.That(second.Attached, Is.Null);
            Assert.That(second.ReadStoreSnapshot()!.Records, Has.Length.EqualTo(1));
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

        // The header, like the bytes and the id plan, comes from the declared type: ids that only the derived type holds are not the
        // stored document's.
        [Test]
        public void WriteDocumentMarksTheStoreByTheDeclaredTypeNotTheRuntimeType()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdBase) });
            var derived = new IdDerived { Name = "n", Reels = { new IdReel { Id = "0123456789ab", Label = "r" } } };

            new SingleFileMessagePackBackingStore(_path).WriteDocument<IdBase>(new CultRecordKey("d"), derived, registry: registry);

            var snapshot = new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!;
            Assert.That(snapshot.FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV1), "the stored IdBase holds no element id");
            Assert.That(snapshot.Records.Single().Payload, Is.EqualTo(CultDocumentMessagePackSerialization.SerializeUntyped(new IdBase { Name = "n" }, typeof(IdBase), registry)));
        }

        // The id plan comes from the declared type too: an unset id that only the derived type holds is not the stored document's, so
        // it is not minted into the caller's object.
        [Test]
        public void WriteDocumentMintsIdsOfTheDeclaredTypeNotTheRuntimeType()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdBase) });
            var derived = new IdDerived { Name = "n", Reels = { new IdReel { Label = "r" } } };

            new SingleFileMessagePackBackingStore(_path).WriteDocument<IdBase>(new CultRecordKey("d"), derived, registry: registry);

            Assert.That(derived.Reels.Single().Id, Is.Null.Or.Empty, "an id outside the declared type is left unset");
        }

        // No call leaves its registry on the store: after a write, a refused write and WriteRecord, the store holds none.
        [Test]
        public void NoCacheCallLeavesARegistryOnTheStore()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteRecord(Record("a", One, 1), One);
            var store = new GatedStore(_path);
            store.Release.Set();

            store.WriteDocument(new CultRecordKey("d"), new IdDeck { Name = "d" }, registry: registry);
            Assert.That(store.Attached, Is.Null, "after WriteDocument");
            store.WriteRecord(Record("b", One, 2), One, holdsIds: false, registry: registry);
            Assert.That(store.Attached, Is.Null, "after WriteRecord");

            File.WriteAllBytes(_path, new byte[] { 0xC1 });
            Assert.Throws<CultStoreUnreadableException>(() => store.WriteDocument(new CultRecordKey("e"), new IdDeck { Name = "e" }, registry: registry));
            Assert.That(store.Attached, Is.Null, "after a refused write");
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
