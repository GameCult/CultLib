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
            public readonly ManualResetEventSlim ReaderInside = new();
            public readonly ManualResetEventSlim ReleaseReader = new();
            // How many threads are inside ReadStore at once, and the most there ever were.
            private int _inside, _mostInside;
            public int MostInside => Volatile.Read(ref _mostInside);
            // Run by the thread whose role is "hook", inside ReadStore: user code that runs under the store's gate.
            [ThreadStatic] public static Action? RoleHook;
            public CultDocumentRegistry? Attached => AttachedRegistry;
            public bool IsAttachedToACache => Cache != null;

            public RoleStore(string path) : base(path) { }

            protected override byte[] SerializeStore(string formatVersion, System.Collections.Generic.IReadOnlyList<byte[]> catalogEntries, System.Collections.Generic.IReadOnlyList<byte[]> records) =>
                CultDocumentMessagePackSerialization.SerializeStore(formatVersion, catalogEntries, records);

            protected override byte[] SerializeRecord(CultPersistedRecord record) =>
                CultDocumentMessagePackSerialization.SerializePersistedRecord(record);

            protected override byte[] SerializeCatalogEntry(CultSchemaCatalogEntry entry) =>
                CultDocumentMessagePackSerialization.SerializeCatalogEntry(entry);

            protected override (CultPersistedStoreSnapshot Snapshot, byte[][] CatalogBytes, byte[][] RecordBytes) ReadStore(byte[] data)
            {
                var now = Interlocked.Increment(ref _inside);
                int seen;
                while ((seen = Volatile.Read(ref _mostInside)) < now && Interlocked.CompareExchange(ref _mostInside, now, seen) != seen) { }
                try
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
                    else if (Role == "reader")
                    {
                        ReaderInside.Set();
                        ReleaseReader.Wait(TimeSpan.FromSeconds(30));
                    }
                    else if (Role == "hook")
                    {
                        RoleHook?.Invoke();
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
            }) { IsBackground = true };
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
            var keys = new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records.Select(record => record.Key).ToArray();
            Assert.That(keys, Has.Length.EqualTo(3), "the seed, the call's record and the cache's record");
            Assert.That(keys, Does.Contain("a").And.Contain("first"));
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

        // A document whose getter runs user code while MessagePack encodes it, under the store's gate.
        [CultDocument("tests.cacheless.tap", "tests.cacheless.tap.v1")]
        [MessagePackObject]
        public class TapDoc
        {
            [ThreadStatic] public static Action? Hook;
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public string Tap { get { var hook = Hook; Hook = null; hook?.Invoke(); return "t"; } set { } }
            [Key(2)] public System.Collections.Generic.List<IdReel> Reels { get; set; } = new();
        }

        private static void Finishes(Thread thread, string what) =>
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, what + " finished (a deadlock fails here, it does not hang)");

        private static bool Refused(Exception? error)
        {
            for (var inner = error; inner != null; inner = inner.InnerException)
                if (inner is InvalidOperationException)
                    return true;
            return false;
        }

        // While a thread holds a store's gate (a cacheless call, an attach's pull) it enters no cache it does not already hold, reads
        // included, and no second store. The call is refused where the getter ran, the file is not written, and minted ids are given back.
        [Test]
        public void AGetterThatEntersACacheDuringACachelessWriteIsRefused()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            using var unrelated = new CultCache(registry);
            var doc = new TapDoc { Name = "d", Reels = { new IdReel() } };
            TapDoc.Hook = () => unrelated.Get(new CultRecordKey("x"));

            var error = Assert.Catch(() => new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("d"), doc, registry: registry));

            Assert.That(Refused(error), Is.True, "the getter's read of a cache was refused");
            Assert.That(File.Exists(_path), Is.False);
            Assert.That(doc.Reels[0].Id, Is.Null.Or.Empty, "the minted id was given back");
        }

        [Test]
        public void AGetterThatWritesACacheDuringACachelessWriteIsRefused()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            using var unrelated = new CultCache(registry);
            TapDoc.Hook = () => _ = unrelated.AddAsync(new TapDoc { Name = "other" });

            var error = Assert.Catch(() => new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("d"), new TapDoc { Name = "d" }, registry: registry));

            Assert.That(Refused(error), Is.True, "the getter's write to a cache was refused");
            Assert.That(File.Exists(_path), Is.False);
            Assert.That(unrelated.AllEntries, Is.Empty, "the refused write reached nothing");
        }

        // Soul P4: the writer holds the store's gate and its getter reads the cache that another thread is attaching the same store to.
        // The attach holds the cache's gate and waits for the store's. The getter is refused instead of waiting on the cache's gate.
        [Test]
        public async Task AGetterThatEntersTheAttachingCacheIsRefusedNotDeadlocked()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            var store = new RoleStore(_path);
            var cache = new CultCache(registry);
            var inside = new ManualResetEventSlim();
            var attachWaits = new ManualResetEventSlim();
            var write = OnThread("writer", () =>
            {
                TapDoc.Hook = () =>
                {
                    inside.Set();
                    attachWaits.Wait(TimeSpan.FromSeconds(30));
                    _ = cache.Get(new CultRecordKey("anything"));
                };
                store.WriteDocument(new CultRecordKey("d"), new TapDoc { Name = "d" }, registry: registry);
            });
            Assert.That(inside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the getter ran under the store's gate");
            var attach = OnThread("attach-after", () => cache.AddBackingStore(store));
            WaitUntilBlocked(attach.Thread);
            attachWaits.Set();

            Finishes(write.Thread, "the writer");
            Finishes(attach.Thread, "the attach");
            Assert.That(Refused(write.Error()), Is.True, "the write was refused");
            Assert.That(attach.Error(), Is.Null);
            await cache.AddAsync(new TapDoc { Name = "after" });
            Assert.DoesNotThrow(() => cache.FlushAllBackingStores());
            // Disposed only after every join passed: a deadlocked gate would make Dispose wait for it.
            cache.Dispose();
        }

        // The attach holds the store's gate while it pulls; user code in the pull may not enter a second store.
        [Test]
        public void AnAttachPullThatEntersAnotherStoreIsRefused()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path);
            var second = new SingleFileMessagePackBackingStore(Path.Combine(_directory, "second.cc"));
            var cache = new CultCache(registry);
            var attach = OnThread("hook", () =>
            {
                RoleStore.RoleHook = () => second.WriteDocument(new CultRecordKey("b"), new IdDeck { Name = "b" }, registry: registry);
                try { cache.AddBackingStore(store); }
                finally { RoleStore.RoleHook = null; }
            });
            Finishes(attach.Thread, "the attach");

            Assert.That(Refused(attach.Error()), Is.True, "the pull's write to a second store was refused");
            var refusal = attach.Error()!;
            while (refusal is not InvalidOperationException && refusal.InnerException != null)
                refusal = refusal.InnerException;
            while (refusal.InnerException is InvalidOperationException deeper)
                refusal = deeper;
            Assert.That(refusal.Message, Does.StartWith("A store's gate was entered inside another store's hold"));
            Assert.That(File.Exists(Path.Combine(_directory, "second.cc")), Is.False);
            Assert.That(cache.BackingStores, Is.Empty, "the failed attach left nothing behind");
            Assert.DoesNotThrow(() => store.WriteDocument(new CultRecordKey("c"), new IdDeck { Name = "c" }, registry: registry), "the store is cacheless after");
            // Disposed only after every join passed: a deadlocked gate would make Dispose wait for it.
            cache.Dispose();
        }

        // Soul P4's window: a reader queued on a failed attach's cache gate must retry under the store's gate. Whichever of the reader and
        // the cacheless writer queued behind the attach gets the store first is alone inside it, and a second cache's attach waits for it.
        [TestCase("snapshot")]
        [TestCase("pullall")]
        public async Task AReaderQueuedOnAFailedAttachRunsUnderTheStoresGate(string how)
        {
            var callRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var registry1 = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var registry2 = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: callRegistry);
            var store = new RoleStore(_path) { FailAttachPull = true };
            var cache1 = new CultCache(registry1);
            var cache2 = new CultCache(registry2);
            var attach1 = OnThread("attach", () => cache1.AddBackingStore(store));
            Assert.That(store.AttachInside.Wait(TimeSpan.FromSeconds(10)), Is.True);
            CultPersistedStoreSnapshot? snapshot = null;
            var reader = OnThread("reader", () => { if (how == "snapshot") snapshot = store.ReadStoreSnapshot(); else store.PullAll(); });
            WaitUntilBlocked(reader.Thread);
            var writer = OnThread("call-in-read", () => store.WriteDocument(new CultRecordKey("first"), new IdDeck { Name = "first" }, registry: callRegistry));
            WaitUntilBlocked(writer.Thread);

            store.ReleaseAttach.Set();
            Finishes(attach1.Thread, "the failed attach");
            Assert.That(SpinWait.SpinUntil(() => store.ReaderInside.IsSet || store.CallInside.IsSet, TimeSpan.FromSeconds(10)), Is.True, "one of them is inside the store");
            var attach2 = OnThread("attach2", () => cache2.AddBackingStore(store));
            WaitUntilBlocked(attach2.Thread);
            Assert.That(attach2.Thread.IsAlive, Is.True, "the second cache's attach waits while a reader or a call is inside the store");

            store.ReleaseReader.Set();
            store.ReleaseCall.Set();
            Finishes(reader.Thread, "the reader");
            Finishes(writer.Thread, "the writer");
            Finishes(attach2.Thread, "the second attach");
            Assert.That(store.MostInside, Is.EqualTo(1), "never two threads inside the store's read at once");
            // A PullAll on a cacheless store with no registry (the cacheless writer that ran before it detached its own) is a usage error;
            // being refused for entering a cache's hold is not.
            Assert.That(reader.Error()?.Message, Is.Null.Or.Contain("not attached to a CultDocumentRegistry"), "the reader was not refused");
            if (how == "snapshot")
                Assert.That(snapshot?.Records.Select(record => record.Key), Does.Contain("a"), "the retried read ran and returned the file's snapshot");
            Assert.That(attach2.Error(), Is.Null);
            await cache2.AddAsync(new IdDeck { Name = "b" });
            Assert.DoesNotThrow(() => cache2.FlushAllBackingStores());
            // Disposed only after every join passed: a deadlocked gate would make Dispose wait for it.
            cache1.Dispose();
            cache2.Dispose();
        }

        // After a failed attach left the store cacheless, the next cache attaches it, and a PullAll that queues on that attach (it guesses
        // the second cache from Cache) runs under the second cache's gate once the attach ends: no refusal, and the cache holds the records.
        [Test]
        public void AFailedAttachThenPullAllLoadsIntoTheSecondCache()
        {
            var callRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: callRegistry);
            var store = new RoleStore(_path) { FailAttachPull = true };
            var cache1 = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));
            var cache2 = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));
            var attach1 = OnThread("attach", () => cache1.AddBackingStore(store));
            Assert.That(store.AttachInside.Wait(TimeSpan.FromSeconds(10)), Is.True);
            store.ReleaseAttach.Set();
            Finishes(attach1.Thread, "the failed attach");
            Assert.That(Refused(attach1.Error()), Is.True);

            store.AttachInside.Reset();
            store.ReleaseAttach.Reset();
            store.FailAttachPull = false;
            var attach2 = OnThread("attach", () => cache2.AddBackingStore(store));
            Assert.That(store.AttachInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the second attach is inside its pull");
            var pull = OnThread("pull", () => store.PullAll());
            WaitUntilBlocked(pull.Thread);

            store.ReleaseAttach.Set();
            Finishes(attach2.Thread, "the second attach");
            Finishes(pull.Thread, "the queued PullAll");
            Assert.That(attach2.Error(), Is.Null);
            Assert.That(pull.Error(), Is.Null, "the queued PullAll was not refused");
            Assert.That(cache2.Get<IdDeck>(new CultRecordKey("a")), Is.Not.Null, "the second cache holds the file's records");
            // Disposed only after every join passed: a deadlocked gate would make Dispose wait for it.
            cache1.Dispose();
            cache2.Dispose();
        }

        // A reader that guessed "no cache" and queued on the store's gate behind an attach finds the cache when it gets the gate, and runs
        // under the cache's gate: while it is inside the store, the cache's own readers wait.
        [Test]
        public void AReaderThatQueuedBeforeAnAttachRunsUnderTheCacheGateAfterIt()
        {
            var callRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: callRegistry);
            var store = new RoleStore(_path);
            var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));
            var call = OnThread("call-in-read", () => store.WriteDocument(new CultRecordKey("first"), new IdDeck { Name = "first" }, registry: callRegistry));
            Assert.That(store.CallInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the call holds the store's gate");
            var attach = OnThread("attach", () => cache.AddBackingStore(store));
            WaitUntilBlocked(attach.Thread);
            var reader = OnThread("reader", () => store.ReadStoreSnapshot());
            WaitUntilBlocked(reader.Thread);

            store.ReleaseCall.Set();
            Assert.That(store.AttachInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the attach queued first and got the store's gate first");
            store.ReleaseAttach.Set();
            Assert.That(store.ReaderInside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the reader retried and is inside the store");
            var probe = OnThread("probe", () => cache.Get(new CultRecordKey("a")));
            Assert.That(SpinWait.SpinUntil(() => !probe.Thread.IsAlive || probe.Thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(probe.Thread.IsAlive, Is.True, "the cache's own reader waits while the retried reader is inside the store: it holds the cache's gate");

            store.ReleaseReader.Set();
            Finishes(call.Thread, "the call");
            Finishes(attach.Thread, "the attach");
            Finishes(reader.Thread, "the reader");
            Finishes(probe.Thread, "the cache's reader");
            Assert.That(attach.Error(), Is.Null);
            Assert.That(reader.Error(), Is.Null);
            cache.Dispose();
        }

        // The gate refuses a cache the thread does not hold; it does not refuse the cache whose gate the thread already holds.
        [Test]
        public void AnAttachPullMayReadTheCacheItIsAttachingTo()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path);
            var cache = new CultCache(registry);
            var attach = OnThread("hook", () =>
            {
                RoleStore.RoleHook = () => cache.Get(new CultRecordKey("a"));
                try { cache.AddBackingStore(store); }
                finally { RoleStore.RoleHook = null; }
            });
            Finishes(attach.Thread, "the attach");

            Assert.That(attach.Error(), Is.Null);
            Assert.That(cache.BackingStores, Has.Count.EqualTo(1));
            cache.Dispose();
        }

        // A store call made from user code the same store's call is running is the same thread's own hold, not a second store.
        [Test]
        public void UserCodeInACachelessCallMayCallItsOwnStore()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            var store = new SingleFileMessagePackBackingStore(_path);
            var descriptor = registry.GetRequired<TapDoc>();
            TapDoc.Hook = () => store.Delete(new CultStoredDocument(new CultRecordKey("x"), DateTimeOffset.UtcNow.ToString("O"), descriptor, new TapDoc { Name = "x" }));

            Assert.DoesNotThrow(() => store.WriteDocument(new CultRecordKey("d"), new TapDoc { Name = "d" }, registry: registry));
        }

        // A failed attach leaves the store as it found it: no cache and no registry. A cacheless PullAll after it is then a usage error
        // that loads nothing, so the next cache to attach admits the file's records, and creating what it cannot find does not duplicate.
        [Test]
        public void AFailedAttachLeavesNoRegistryAndTheNextCacheSeesTheFilesRecords()
        {
            var callRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: callRegistry);
            var store = new RoleStore(_path) { FailAttachPull = true };
            store.ReleaseAttach.Set();
            var cache1 = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));
            var attach1 = OnThread("attach", () => cache1.AddBackingStore(store));
            Finishes(attach1.Thread, "the failed attach");
            Assert.That(Refused(attach1.Error()), Is.True);
            Assert.That(store.IsAttachedToACache, Is.False);
            Assert.That(store.Attached, Is.Null, "the failed attach took its registry back");

            Assert.That(Assert.Throws<InvalidOperationException>(() => store.PullAll())!.Message, Does.Contain("not attached to a CultDocumentRegistry"));
            store.FailAttachPull = false;
            var cache2 = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));
            var attach2 = OnThread("attach", () => cache2.AddBackingStore(store));
            Finishes(attach2.Thread, "the second attach");
            Assert.That(attach2.Error(), Is.Null);

            Assert.That(cache2.Get<IdDeck>(new CultRecordKey("a")), Is.Not.Null, "the second cache holds the file's record");
            Assert.That(cache2.GetAll<IdDeck>().Count(), Is.EqualTo(1));
            cache2.FlushAllBackingStores();
            Assert.That(new SingleFileMessagePackBackingStore(_path).ReadStoreSnapshot()!.Records, Has.Length.EqualTo(1));
            cache1.Dispose();
            cache2.Dispose();
        }

        // Soul P4's load: cacheless writers and snapshot readers race an attach that fails (odd rounds) or succeeds. Whoever ends up
        // owning the store owns its registry, and its cache writes and flushes. A call split into a refusal hold and a body hold fails here:
        // a body that runs after the attach took the store leaves the call's registry (or none) on the cache's store.
        [Test]
        public async Task CachelessCallsRacingAnAttachNeverLeaveTheCachesStoreWithoutItsRegistry()
        {
            var bad = new System.Collections.Generic.List<string>();
            for (var round = 0; round < 60; round++)
            {
                var path = Path.Combine(_directory, $"load{round}.cc");
                var mine = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
                var reg1 = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
                var reg2 = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
                var attachFails = round % 2 == 1;
                var store = new RoleStore(path) { FailAttachPull = attachFails };
                store.ReleaseAttach.Set();
                store.WriteDocument(new CultRecordKey("seed"), new IdDeck { Name = "seed" }, registry: mine);
                using var cache1 = new CultCache(reg1);
                using var cache2 = new CultCache(reg2);
                var go = new ManualResetEventSlim();
                var tasks = new System.Collections.Generic.List<Task>();
                for (var writer = 0; writer < 4; writer++)
                {
                    var n = writer;
                    tasks.Add(Task.Run(() =>
                    {
                        go.Wait();
                        for (var k = 0; k < 8; k++)
                        {
                            try { store.WriteDocument(new CultRecordKey($"w{n}-{k}"), new IdDeck { Name = $"w{n}-{k}" }, registry: mine); }
                            catch (InvalidOperationException) { }
                        }
                    }));
                }
                for (var reader = 0; reader < 2; reader++)
                    tasks.Add(Task.Run(() =>
                    {
                        go.Wait();
                        for (var k = 0; k < 12; k++)
                            store.ReadStoreSnapshot();
                    }));
                tasks.Add(Task.Run(() =>
                {
                    go.Wait();
                    Thread.SpinWait(1500 * round);
                    RoleStore.Role = "attach";
                    try { cache1.AddBackingStore(store); }
                    catch (InvalidOperationException) { }
                    finally { RoleStore.Role = null; }
                    if (!store.IsAttachedToACache)
                        cache2.AddBackingStore(store);
                }));
                go.Set();
                Assert.That(Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(60)), Is.True, "the racing calls and the attach finished (a deadlock fails here, it does not hang)");
                var owner = attachFails ? cache2 : cache1;
                var ownerRegistry = attachFails ? reg2 : reg1;
                if (!ReferenceEquals(store.Attached, ownerRegistry))
                    bad.Add($"round {round}: the owning cache's registry is not on its store");
                else
                {
                    await owner.AddAsync(new IdDeck { Name = "after" });
                    owner.FlushAllBackingStores();
                }
            }

            Assert.That(bad, Is.Empty);
        }

        // Every way into a cache's gate refuses a thread that holds a store's gate and not that cache's.
        [TestCase("Get")]
        [TestCase("GetWithSequence")]
        [TestCase("GetStored")]
        [TestCase("GetAll")]
        [TestCase("IsDirty")]
        [TestCase("BackingStores")]
        [TestCase("AllEntries")]
        [TestCase("AllStoredDocuments")]
        [TestCase("TryGetHandle")]
        public void EveryCacheGateEntryRefusesAGetterRunningUnderAStoresGate(string entry)
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            using var unrelated = new CultCache(registry);
            var key = new CultRecordKey("x");
            TapDoc.Hook = () =>
            {
                switch (entry)
                {
                    case "Get": unrelated.Get(key); break;
                    case "GetWithSequence": unrelated.GetWithSequence(key); break;
                    case "GetStored": unrelated.GetStored(key); break;
                    case "GetAll": unrelated.GetAll<TapDoc>(); break;
                    case "IsDirty": _ = unrelated.IsDirty; break;
                    case "BackingStores": _ = unrelated.BackingStores; break;
                    case "AllEntries": _ = unrelated.AllEntries; break;
                    case "AllStoredDocuments": _ = unrelated.AllStoredDocuments; break;
                    default: unrelated.TryGetHandle(new TapDoc()); break;
                }
            };

            var error = Assert.Catch(() => new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("d"), new TapDoc { Name = "d" }, registry: registry));

            Assert.That(Refused(error), Is.True, entry + " was refused under the store's gate");
            Assert.That(File.Exists(_path), Is.False);
        }

        // Calling its own store from user code does not end the store's hold for the rest of the call: a cache entered afterwards is
        // still refused.
        [Test]
        public void UserCodeThatCalledItsOwnStoreStillCannotEnterACacheAfterwards()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            var store = new SingleFileMessagePackBackingStore(_path);
            var descriptor = registry.GetRequired<TapDoc>();
            using var unrelated = new CultCache(registry);
            TapDoc.Hook = () =>
            {
                store.Delete(new CultStoredDocument(new CultRecordKey("x"), DateTimeOffset.UtcNow.ToString("O"), descriptor, new TapDoc { Name = "x" }));
                unrelated.Get(new CultRecordKey("x"));
            };

            var error = Assert.Catch(() => store.WriteDocument(new CultRecordKey("d"), new TapDoc { Name = "d" }, registry: registry));

            Assert.That(Refused(error), Is.True, "the read after the own-store call was refused");
            Assert.That(File.Exists(_path), Is.False);
        }

        // The attach lends the pull its registry and publishes the cache before the pull: while the pull runs the store holds the loan and
        // is attached, so every entry point agrees on what the store is for the whole attach.
        [Test]
        public void AnAttachLendsTheRegistryAndPublishesTheCacheForThePull()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var codec = Codec(registry);
            var store = new RoleStore(_path);
            using var cache = new CultCache(registry, codec);
            bool cacheDuringPull = true;
            CultDocumentRegistry? registryDuringPull = null;
            var attach = OnThread("hook", () =>
            {
                RoleStore.RoleHook = () =>
                {
                    cacheDuringPull = store.IsAttachedToACache;
                    registryDuringPull = store.Attached;
                };
                try { cache.AddBackingStore(store); }
                finally { RoleStore.RoleHook = null; }
            });
            Finishes(attach.Thread, "the attach");

            Assert.That(attach.Error(), Is.Null);
            Assert.That(cacheDuringPull, Is.True, "the store is attached for the pull");
            Assert.That(registryDuringPull, Is.SameAs(registry));
            Assert.That(store.IsAttachedToACache, Is.True, "still attached once the pull succeeded");
        }

        // A failed attach takes back both the registry and the cache, in its own hold of the store.
        [Test]
        public void AFailedAttachTakesBackTheRegistryAndTheCache()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path) { FailAttachPull = true };
            store.ReleaseAttach.Set();
            using var cache = new CultCache(registry, Codec(registry));
            var attach = OnThread("attach", () => cache.AddBackingStore(store));
            Finishes(attach.Thread, "the failed attach");

            Assert.That(attach.Error(), Is.Not.Null);
            Assert.That(store.IsAttachedToACache, Is.False);
            Assert.That(store.Attached, Is.Null, "the registry was taken back");
        }

        [Test]
        public void AttachingNoStoreIsRefusedBeforeAnythingIsHeld()
        {
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) }));

            Assert.Throws<ArgumentNullException>(() => cache.AddBackingStore(null!));
        }

        private const string ReentryRefusal = "A cacheless write was made inside a call that already holds this store";

        private const string AttachedRefusal = "The store is attached to a cache, write through the cache.";

        private const string JournalRefusal = "A journal entered the cache it journals";

        // Every store entry point, called by a journal the attach's pull runs on the attaching thread, through a real attach. The store is
        // attached for its whole attach, so each call is refused as it is after the attach, and nothing reaches the file or the store's
        // staging that the cache never admitted: after a flush the attaching cache and a fresh cache over the file hold the same keys.
        [TestCase("single", "pullall")]
        [TestCase("single", "push")]
        [TestCase("single", "delete")]
        [TestCase("single", "pushall")]
        [TestCase("single", "push+pushall")]
        [TestCase("single", "delete+pushall")]
        [TestCase("single", "commit")]
        [TestCase("single", "judge")]
        [TestCase("single", "stage")]
        [TestCase("single", "snapshot")]
        [TestCase("single", "writedoc")]
        [TestCase("single", "writerecord")]
        [TestCase("directory", "pullall")]
        [TestCase("directory", "push")]
        [TestCase("directory", "delete")]
        [TestCase("directory", "pushall")]
        [TestCase("directory", "push+pushall")]
        [TestCase("directory", "delete+pushall")]
        [TestCase("directory", "commit")]
        [TestCase("directory", "judge")]
        [TestCase("directory", "stage")]
        public async Task AJournalOnTheAttachingThreadCannotReachItsStore(string kind, string op)
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            var descriptor = registry.GetRequired(typeof(IdDeck));
            var path = kind == "single" ? _path : Path.Combine(_directory, "dir");
            CacheBackingStore Open() => kind == "single"
                ? new SingleFileMessagePackBackingStore(path)
                : new DirectoryMessagePackBackingStore(path);

            using (var seed = new CultCache(registry, Codec(registry)))
            {
                seed.AddBackingStore(Open());
                await seed.UpsertAsync(typeof(IdDeck), new IdDeck { Name = "a" }, new CultRecordKey("a"));
                seed.FlushAllBackingStores();
            }
            CultPersistedRecord? recordA = null;
            CultSchemaCatalogEntry? entryA = null;
            if (kind == "single")
            {
                var snapshot = new SingleFileMessagePackBackingStore(path).ReadStoreSnapshot()!;
                recordA = snapshot.Records.First();
                entryA = snapshot.SchemaCatalog.First();
            }

            var store = Open();
            using var cache = new CultCache(registry, Codec(registry));
            var docB = new CultStoredDocument(new CultRecordKey("b"), DateTimeOffset.UtcNow.ToString("O"), descriptor, new IdDeck { Name = "b" });
            var journalRuns = 0;
            Exception? journalError = null;
            using var journal = cache.AddJournal(_ =>
            {
                if (journalRuns++ > 0)
                    return;
                try
                {
                    switch (op)
                    {
                        case "pullall": store.PullAll(); break;
                        case "push": store.Push(docB); break;
                        case "delete": store.Delete(cache.GetStored(new CultRecordKey("a"))!); break;
                        case "pushall": store.PushAll(); break;
                        case "push+pushall": store.Push(docB); store.PushAll(); break;
                        case "delete+pushall": store.Delete(cache.GetStored(new CultRecordKey("a"))!); store.PushAll(); break;
                        case "commit":
                            store.CommitBatch(new CultCommitRequest(new[] { docB }, Array.Empty<CultStoredDocument>(), Array.Empty<string>(),
                                Array.Empty<(CultRecordKey, string?)>(), false), wait: true);
                            break;
                        case "judge": store.JudgeChecks(new[] { "a" }, wait: true); break;
                        case "stage": store.RefuseRemovalOfUndeclared(new[] { "zz" }); break;
                        case "snapshot": ((SingleFileBackingStore)store).ReadStoreSnapshot(); break;
                        case "writedoc":
                            ((SingleFileBackingStore)store).WriteDocument(new CultRecordKey("b"), new IdDeck { Name = "b" }, registry: registry);
                            break;
                        case "writerecord": ((SingleFileBackingStore)store).WriteRecord(recordA!, entryA!, registry: registry); break;
                        default: throw new ArgumentException(op);
                    }
                }
                catch (Exception error) { journalError = error; }
            });

            cache.AddBackingStore(store);

            Assert.That(journalRuns, Is.GreaterThan(0), "the pull's admission ran the journal");
            Assert.That(journalError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(journalError!.Message, Does.StartWith(op is "writedoc" or "writerecord" ? AttachedRefusal : JournalRefusal));
            Assert.That(store.IsDirty, Is.False, "nothing the cache never admitted is staged");

            await cache.UpsertAsync(typeof(IdDeck), new IdDeck { Name = "later" }, new CultRecordKey("later"));
            Assert.DoesNotThrow(() => cache.FlushAllBackingStores());
            Assert.DoesNotThrow(() => store.PullAll(), "the refused call did not take back the attach's registry");
            using var fresh = new CultCache(registry, Codec(registry));
            fresh.AddBackingStore(Open());
            string[] Keys(CultCache from) => from.AllStoredDocuments.Select(document => document.Key.Value).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert.That(Keys(cache), Does.Contain("a").And.Contain("later").And.Not.Contain("b"));
            Assert.That(Keys(cache), Is.EqualTo(Keys(fresh)), "the attaching cache and a fresh cache over the same file agree");
        }

        // A cacheless write made inside another store's hold is refused as entering a second store, not as re-entering its own: the
        // second-store rule is checked before the same-store one, so the message sends the caller to the right place.
        [Test]
        public void ACachelessWriteInsideAnotherStoresHoldGetsTheSecondStoreRefusal()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            var pathB = Path.Combine(_directory, "second.cc");
            var storeA = new RoleStore(_path);
            var storeB = new RoleStore(pathB);
            TapDoc.Hook = () => storeB.WriteDocument(new CultRecordKey("inner"), new TapDoc { Name = "inner" }, registry: registry);

            var outer = Assert.Catch<Exception>(() => storeA.WriteDocument(new CultRecordKey("outer"), new TapDoc { Name = "outer" }, registry: registry));

            var refusal = outer!;
            while (refusal is not InvalidOperationException && refusal.InnerException != null)
                refusal = refusal.InnerException;
            while (refusal.InnerException is InvalidOperationException deeper)
                refusal = deeper;
            Assert.That(refusal, Is.InstanceOf<InvalidOperationException>());
            Assert.That(refusal.Message, Does.StartWith("A store's gate was entered inside another store's hold"));
            Assert.That(File.Exists(_path), Is.False);
            Assert.That(File.Exists(pathB), Is.False);
        }

        // A cacheless write owns its store outright: user code another cacheless write of the same store runs may not make a second.
        [Test]
        public void ACachelessWriteInsideACachelessWriteOfTheSameStoreIsRefused()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(TapDoc) });
            var store = new RoleStore(_path);
            TapDoc.Hook = () => store.WriteDocument(new CultRecordKey("inner"), new TapDoc { Name = "inner" }, registry: registry);

            var outer = Assert.Catch<Exception>(() => store.WriteDocument(new CultRecordKey("outer"), new TapDoc { Name = "outer" }, registry: registry));

            Assert.That(Refused(outer), Is.True, "the outer write failed with the inner call's refusal");
            var refusal = outer!;
            while (refusal is not InvalidOperationException && refusal.InnerException != null)
                refusal = refusal.InnerException;
            Assert.That(refusal.Message, Does.StartWith(ReentryRefusal));
            Assert.That(File.Exists(_path), Is.False);
            Assert.That(store.Attached, Is.Null);
        }

        // The bound on a call's gates is structural: an attach publishes Cache and takes it back inside one hold of the store's gate, so a
        // cache read under that gate is settled. This test publishes and retracts in two holds, the only way to miss; the reader, which
        // settled on the cache and then finds it gone under that cache's gate, throws.
        [Test]
        public void ACallWhoseSettledCacheChangesThrows()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path);
            using var cache = new CultCache(registry);
            var holding = new ManualResetEventSlim();
            var publish = new ManualResetEventSlim();
            var retract = new ManualResetEventSlim();
            var tamper = OnThread("tamper", () => cache.Held(() =>
            {
                store.UnderStoreGate(() =>
                {
                    holding.Set();
                    publish.Wait(TimeSpan.FromSeconds(30));
                    store.Cache = cache;
                    return true;
                });
                retract.Wait(TimeSpan.FromSeconds(30));
                store.UnderStoreGate(() => { store.Cache = null; return true; });
                return true;
            }));
            Assert.That(holding.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Exception? readerError = null;
            var reader = OnThread("reader", () =>
            {
                try { store.ReadStoreSnapshot(); }
                catch (Exception e) { readerError = e; throw; }
            });
            WaitUntilBlocked(reader.Thread);
            publish.Set();
            Thread.Sleep(500);
            retract.Set();
            Finishes(tamper.Thread, "the tampering thread");
            Finishes(reader.Thread, "the reader");

            Assert.That(tamper.Error(), Is.Null);
            Assert.That(readerError, Is.InstanceOf<InvalidOperationException>().With.Message.StartWith("A store's settled cache changed"));
        }

        // Failing attaches over and over, from one, two and four caches, against readers: a reader's gate changes under it at most once
        // (the bound above throws on a second), so no reader fails and every thread finishes.
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void ReadersAcrossFailingAttachChurnNeverExceedTheBound(int attachers)
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path) { FailAttachPull = true };
            store.ReleaseAttach.Set();
            var caches = Enumerable.Range(0, attachers).Select(_ => new CultCache(registry)).ToArray();
            var stop = 0;
            var readerErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var attachErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var threads = caches.Select(c => OnThread("attach", () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    try { c.AddBackingStore(store); }
                    catch (InvalidOperationException e) when (e.Message.Contains("pull fails")) { }
                    catch (Exception e) { attachErrors.Add(e.Message); }
                }
            })).Concat(Enumerable.Range(0, 4).Select(_ => OnThread("churn-reader", () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    try { store.ReadStoreSnapshot(); }
                    catch (Exception e) { readerErrors.Add(e.Message); }
                }
            }))).ToArray();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            Volatile.Write(ref stop, 1);
            foreach (var t in threads)
                Finishes(t.Thread, "a churn thread");

            Assert.That(readerErrors, Is.Empty);
            Assert.That(attachErrors, Is.Empty);
            foreach (var c in caches)
                c.Dispose();
        }

        // A call on a detached store takes exactly one gate (the store's), and one on a store whose attach has settled takes exactly
        // one (the settled cache's, reached by the guess): the counter counts each take.
        [Test]
        public void OneStoreCallTakesExactlyOneGateOnADetachedStoreAndOnASettledOne()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path);
            store.ReleaseAttach.Set();

            CacheBackingStore.GateTakes = 0;
            store.ReadStoreSnapshot();
            Assert.That(CacheBackingStore.GateTakes, Is.EqualTo(1), "a detached store's call takes the store's gate");

            using var cache = new CultCache(registry);
            cache.AddBackingStore(store);
            CacheBackingStore.GateTakes = 0;
            store.ReadStoreSnapshot();
            Assert.That(CacheBackingStore.GateTakes, Is.EqualTo(1), "a settled store's call takes only its cache's gate");
        }

        // Under attaches that succeed, a call takes at most three gates, and three is reachable: the guess was null, an attach then
        // settled before the call took the store's gate, and the call took the settled cache's gate as its third.
        [Test]
        public void OneStoreCallTakesAtMostThreeGatesUnderSuccessfulAttachChurnAndThreeIsReached()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var mostTakes = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            for (var round = 0; mostTakes < 3 && DateTime.UtcNow < deadline; round++)
            {
                var store = new RoleStore(_path);
                store.ReleaseAttach.Set();
                var cache = new CultCache(registry);
                using var barrier = new Barrier(2);
                var takes = 0;
                var spin = (round % 16) * 40;
                var caller = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    CacheBackingStore.GateTakes = 0;
                    store.ReadStoreSnapshot();
                    takes = CacheBackingStore.GateTakes;
                });
                var attacher = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    Thread.SpinWait(spin);
                    cache.AddBackingStore(store);
                });
                caller.Start();
                attacher.Start();
                Finishes(caller, "a calling thread");
                Finishes(attacher, "an attaching thread");
                cache.Dispose();
                Assert.That(takes, Is.InRange(1, 3), "no call took more than three gates");
                mostTakes = Math.Max(mostTakes, takes);
            }

            Assert.That(mostTakes, Is.EqualTo(3), "a call that raced a successful attach took the third gate");
        }

        // Under attaches that fail, Cache read under the store's gate is always null (an attach publishes and retracts it inside one
        // hold of that gate), so one call takes at most two gates: the guessed cache's, then the store's. A retry around the unlocked
        // guess would take one per stale guess and exceed this.
        [Test]
        public void OneStoreCallTakesAtMostTwoGatesUnderFailingAttachChurn()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
            new SingleFileMessagePackBackingStore(_path).WriteDocument(new CultRecordKey("a"), new IdDeck { Name = "a" }, registry: registry);
            var store = new RoleStore(_path) { FailAttachPull = true };
            store.ReleaseAttach.Set();
            var caches = Enumerable.Range(0, 4).Select(_ => new CultCache(registry)).ToArray();
            var stop = 0;
            var mostTakes = 0;
            var calls = 0;
            var threads = caches.Select(c => OnThread("attach", () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    try { c.AddBackingStore(store); }
                    catch (InvalidOperationException e) when (e.Message.Contains("pull fails")) { }
                }
            })).Concat(Enumerable.Range(0, 4).Select(_ => OnThread("counting-reader", () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    CacheBackingStore.GateTakes = 0;
                    try { store.ReadStoreSnapshot(); }
                    catch (InvalidOperationException) { }
                    var takes = CacheBackingStore.GateTakes;
                    Interlocked.Increment(ref calls);
                    int seen;
                    while (takes > (seen = Volatile.Read(ref mostTakes)) && Interlocked.CompareExchange(ref mostTakes, takes, seen) != seen) { }
                }
            }))).ToArray();
            Thread.Sleep(TimeSpan.FromSeconds(3));
            Volatile.Write(ref stop, 1);
            foreach (var t in threads)
                Finishes(t.Thread, "a churn thread");

            Assert.That(calls, Is.GreaterThan(100), "the readers made calls under churn");
            Assert.That(mostTakes, Is.LessThanOrEqualTo(2), "no call took more than two gates");
            foreach (var c in caches)
                c.Dispose();
        }
    }
}
