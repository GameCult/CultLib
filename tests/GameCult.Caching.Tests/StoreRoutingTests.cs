#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    // The contract is src/GameCult.Caching/Contracts/cultcache-store-composition.md.
    public class StoreRoutingTests
    {
        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-routing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task LoadingNeverWritesASecondStore()
        {
            var registry = Registry();
            var first = PathOf("a.cc");
            var second = PathOf("b.cc");
            await Seed(first, registry, new RoutingNote { Name = "note", Text = "seeded" });

            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(first));
            await cache.PullAllBackingStoresAsync();
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(second), typeof(RoutingOther));
            await cache.FlushAsync();

            Assert.That(File.Exists(second), Is.False, "attaching a store and flushing without a mutation wrote it");
        }

        [Test]
        public async Task UpsertWeaponThroughGearHandleReloadsAsWeapon()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RoutingGear), typeof(RoutingWeapon) });
            var path = PathOf("gear.cc");
            var handle = new CultRecordHandle<RoutingGear>(new CultRecordKey("lance"));

            using (var cache = new CultCache(registry))
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await cache.UpsertAsync<RoutingGear>(new RoutingWeapon { Name = "lance", Damage = 12 }, handle);
                cache.FlushAllBackingStores();
            }

            using var reopened = new CultCache(registry);
            reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            await reopened.PullAllBackingStoresAsync();
            var loaded = reopened.Get(handle.Key);

            Assert.That(loaded, Is.InstanceOf<RoutingWeapon>(), "the handle's type parameter chose the schema");
            Assert.That(((RoutingWeapon)loaded!).Damage, Is.EqualTo(12));
            Assert.That(((RoutingWeapon)loaded!).Name, Is.EqualTo("lance"), "the inherited member did not round-trip");
        }

        [Test]
        public async Task GetByNameMatchesAssignableTypes()
        {
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(RoutingLeaf) }));
            await cache.UpsertAsync(new RoutingLeaf { Name = "alpha" });

            Assert.That(cache.GetByName<RoutingBase>("alpha"), Is.InstanceOf<RoutingLeaf>());
        }

        [Test]
        public async Task WatchMatchesAssignableTypes()
        {
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(RoutingLeaf) }));
            var changes = 0;
            using var subscription = cache.Watch<RoutingBase>().Subscribe(_ => changes++);

            await cache.UpsertAsync(new RoutingLeaf { Name = "alpha" });

            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public async Task GetGlobalMatchesAssignableTypes()
        {
            using var cache = new CultCache(GlobalRegistry());
            await cache.UpsertAsync(new RoutingGlobal { Value = "explicit" });

            Assert.That((cache.GetGlobal<RoutingGlobalBase>() as RoutingGlobal)?.Value, Is.EqualTo("explicit"));
        }

        [Test]
        public void ConstructingACacheInventsNothing()
        {
            using var cache = new CultCache(GlobalRegistry());

            Assert.That(cache.AllEntries, Is.Empty);
        }

        [Test]
        public async Task AttachThenFlushLeavesASeededFileByteIdentical()
        {
            var path = PathOf("seeded.cc");
            var key = await Seed(
                path,
                CultDocumentRegistry.ForTypes(new[] { typeof(RoutingNote) }),
                new RoutingNote { Name = "note", Text = "seeded" });
            var before = File.ReadAllBytes(path);

            using var cache = new CultCache(GlobalRegistry(typeof(RoutingNote)));
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            await cache.PullAllBackingStoresAsync();
            await cache.FlushAsync();

            Assert.Multiple(() =>
            {
                Assert.That(cache.Get<RoutingNote>(key)?.Text, Is.EqualTo("seeded"), "the seeded record did not load");
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "loading and flushing rewrote the file");
            });
        }

        [Test]
        public void RefusedPushLeavesNothingInMemory()
        {
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(RoutingNote) }));
            cache.AddBackingStore(new RefusingStore(PathOf("refusing.cc")));
            var key = new CultRecordKey("refused");
            var changes = 0;
            using var subscription = cache.Watch<RoutingNote>().Subscribe(_ => changes++);

            Assert.That(
                async () => await cache.UpsertAsync(typeof(RoutingNote), new RoutingNote { Name = "refused" }, key),
                Throws.InvalidOperationException);
            Assert.That(cache.Get(key), Is.Null, "the store refused the write but the cache kept the document");
            Assert.That(changes, Is.Zero, "the store refused the write but the cache published a change");
        }

        [Test]
        public async Task RoutedStoresWriteTheSameBytesAsSingleStores()
        {
            var registry = Registry();
            var note = new CultRecordKey("note");
            var other = new CultRecordKey("other");

            using (var routed = new CultCache(registry))
            {
                routed.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("catalog.cc")), typeof(RoutingNote));
                routed.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("run.cc")), typeof(RoutingOther));
                await routed.UpsertAsync(typeof(RoutingNote), new RoutingNote { Name = "n", Text = "catalog" }, note);
                await routed.UpsertAsync(typeof(RoutingOther), new RoutingOther { Name = "o", Text = "run" }, other);
                await routed.FlushAsync();
            }

            await Seed(PathOf("catalog-alone.cc"), registry, new RoutingNote { Name = "n", Text = "catalog" }, note);
            await Seed(PathOf("run-alone.cc"), registry, new RoutingOther { Name = "o", Text = "run" }, other);

            Assert.That(Normalized(PathOf("catalog.cc")), Is.EqualTo(Normalized(PathOf("catalog-alone.cc"))));
            Assert.That(Normalized(PathOf("run.cc")), Is.EqualTo(Normalized(PathOf("run-alone.cc"))));
        }

        [Test]
        public async Task CatalogRecordNeverLandsInRunStore()
        {
            var registry = Registry();
            using var cache = new CultCache(registry);
            var catalog = new SingleFileMessagePackBackingStore(PathOf("catalog.cc"));
            var run = new SingleFileMessagePackBackingStore(PathOf("run.cc"));
            cache.AddBackingStore(catalog, typeof(RoutingNote));
            cache.AddBackingStore(run, typeof(RoutingOther));

            await cache.UpsertAsync(new RoutingNote { Name = "n", Text = "catalog" });

            Assert.That(catalog.IsDirty, Is.True);
            Assert.That(run.IsDirty, Is.False);
            await cache.FlushAsync();
            Assert.That(File.Exists(PathOf("run.cc")), Is.False);
            Assert.That(Snapshot(PathOf("catalog.cc")).Records, Has.Length.EqualTo(1));
        }

        [Test]
        public void AttachAfterDirtyThrows()
        {
            var registry = Registry();
            using var cache = new CultCache(registry);
            var store = new SingleFileMessagePackBackingStore(PathOf("dirty.cc"));
            store.Push(new CultStoredDocument(new CultRecordKey("staged"), FixedStoredAt, registry.GetRequired<RoutingNote>(), new RoutingNote()));

            Assert.That(() => cache.AddBackingStore(store), Throws.InvalidOperationException.With.Message.Contains("staged writes"));
            Assert.That(cache.BackingStores, Is.Empty);
        }

        [Test]
        public void SecondUntypedStoreThrows()
        {
            using var cache = new CultCache(Registry());
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("a.cc")));

            Assert.That(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("b.cc"))),
                Throws.InvalidOperationException.With.Message.Contains("second untyped store"));
            Assert.That(cache.BackingStores, Has.Count.EqualTo(1));
        }

        [Test]
        public void DuplicateHomeTypeThrows()
        {
            using var cache = new CultCache(Registry());
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("a.cc")), typeof(RoutingNote));

            Assert.That(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("b.cc")), typeof(RoutingNote)),
                Throws.InvalidOperationException.With.Message.Contains("already routed"));
            Assert.That(cache.BackingStores, Has.Count.EqualTo(1));
        }

        [Test]
        public void WriteWithoutHomeThrows()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RoutingNote), typeof(RoutingOther), typeof(RoutingGear) });
            using var cache = new CultCache(registry);
            var notes = new SingleFileMessagePackBackingStore(PathOf("notes.cc"));
            var gear = new SingleFileMessagePackBackingStore(PathOf("gear.cc"));
            cache.AddBackingStore(notes, typeof(RoutingNote));
            cache.AddBackingStore(gear, typeof(RoutingGear));
            var key = new CultRecordKey("homeless");

            Assert.That(async () => await cache.UpsertAsync(typeof(RoutingOther), new RoutingOther(), key),
                Throws.InvalidOperationException.With.Message.Contains("No backing store is home"));
            Assert.That(notes.IsDirty, Is.False);
            Assert.That(gear.IsDirty, Is.False);
            Assert.That(cache.Get(key), Is.Null);
        }

        [Test]
        public async Task ForeignRecordIsRefusedOnLoad()
        {
            var registry = Registry();
            var runPath = PathOf("run.cc");
            var key = await Seed(runPath, registry, new RoutingNote { Name = "n", Text = "misplaced" });
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("catalog.cc")), typeof(RoutingNote));

            var refusal = Assert.Throws<InvalidOperationException>(() =>
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(runPath), typeof(RoutingOther)));

            Assert.That(refusal!.Message, Does.Contain(key.Value).And.Contain(runPath).And.Contain(PathOf("catalog.cc")));
            Assert.That(cache.AllEntries, Is.Empty);
            Assert.That(cache.BackingStores, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task KeyCannotMoveToAnotherStoreOnWrite()
        {
            var registry = Registry();
            var catalogPath = PathOf("catalog.cc");
            var runPath = PathOf("run.cc");
            var key = new CultRecordKey("shared-key");
            using var cache = new CultCache(registry);
            var run = new SingleFileMessagePackBackingStore(runPath);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(catalogPath), typeof(RoutingNote));
            cache.AddBackingStore(run, typeof(RoutingOther));
            await cache.UpsertAsync(typeof(RoutingNote), new RoutingNote { Name = "n", Text = "catalog" }, key);
            await cache.FlushAsync();
            var before = File.ReadAllBytes(catalogPath);

            var single = Assert.Throws<InvalidOperationException>(() =>
                cache.UpsertAsync(typeof(RoutingOther), new RoutingOther { Name = "o", Text = "run" }, key));
            var batched = Assert.Throws<InvalidOperationException>(() =>
                cache.Commit(batch => batch.Upsert(typeof(RoutingOther), new RoutingOther { Name = "o", Text = "run" }, key)));
            await cache.FlushAsync();

            Assert.That(single!.Message, Does.Contain(key.Value).And.Contain(catalogPath).And.Contain(runPath));
            Assert.That(batched!.Message, Is.EqualTo(single.Message));
            Assert.That(run.IsDirty, Is.False);
            Assert.That(File.Exists(runPath), Is.False);
            Assert.That(File.ReadAllBytes(catalogPath), Is.EqualTo(before));
            Assert.That(cache.Get<RoutingNote>(key)?.Text, Is.EqualTo("catalog"));
        }

        [Test]
        public void KeyLoadedFromTwoStoresIsRefused()
        {
            var registry = Registry();
            var catalogPath = PathOf("catalog.cc");
            var runPath = PathOf("run.cc");
            var key = new CultRecordKey("shared-key");
            WriteRecords(catalogPath, registry, (key.Value, FixedStoredAt, new RoutingNote { Name = "n", Text = "catalog" }));
            WriteRecords(runPath, registry, (key.Value, FixedStoredAt, new RoutingOther { Name = "o", Text = "run" }));
            var runBefore = File.ReadAllBytes(runPath);
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(catalogPath), typeof(RoutingNote));
            var changes = 0;
            using var subscription = cache.Watch<object>().Subscribe(_ => changes++);

            var refusal = Assert.Throws<InvalidOperationException>(() =>
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(runPath), typeof(RoutingOther)));

            Assert.That(refusal!.Message, Does.Contain(key.Value).And.Contain(catalogPath).And.Contain(runPath));
            Assert.That(cache.BackingStores, Has.Count.EqualTo(1));
            Assert.That(cache.AllEntries.Count(), Is.EqualTo(1));
            Assert.That(cache.Get<RoutingNote>(key)?.Text, Is.EqualTo("catalog"));
            Assert.That(changes, Is.Zero);
            Assert.That(File.ReadAllBytes(runPath), Is.EqualTo(runBefore));
        }

        [Test]
        public async Task LateRouteOverAdmittedTypeThrows()
        {
            var registry = Registry();
            var untypedPath = PathOf("untyped.cc");
            var routedPath = PathOf("notes.cc");
            var key = await Seed(untypedPath, registry, new RoutingNote { Name = "n", Text = "admitted" });

            using (var cache = new CultCache(registry))
            {
                var untyped = new SingleFileMessagePackBackingStore(untypedPath);
                cache.AddBackingStore(untyped);

                var refusal = Assert.Throws<InvalidOperationException>(() =>
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(routedPath), typeof(RoutingNote)));

                Assert.That(refusal!.Message, Does.Contain(untypedPath).And.Contain(routedPath));
                Assert.That(cache.Get<RoutingNote>(key)?.Text, Is.EqualTo("admitted"));
                Assert.That(cache.AllEntries.Count(), Is.EqualTo(1));
                Assert.That(untyped.IsDirty, Is.False);
            }

            // Routed stores first, the untyped store last. The untyped store holds a type no route claims; a Note in it
            // would be a foreign record (ForeignRecordIsRefusedOnLoad).
            File.Move(untypedPath, routedPath);
            var otherKey = await Seed(untypedPath, registry, new RoutingOther { Name = "o", Text = "untyped" });
            using var ordered = new CultCache(registry);
            ordered.AddBackingStore(new SingleFileMessagePackBackingStore(routedPath), typeof(RoutingNote));
            Assert.That(() => ordered.AddBackingStore(new SingleFileMessagePackBackingStore(untypedPath)), Throws.Nothing);
            Assert.That(ordered.Get<RoutingNote>(key)?.Text, Is.EqualTo("admitted"));
            Assert.That(ordered.Get<RoutingOther>(otherKey)?.Text, Is.EqualTo("untyped"));
        }

        [Test]
        public async Task ReadOnlyStoreRefusesWrites()
        {
            var registry = Registry();
            var path = PathOf("catalog.cc");
            await Seed(path, registry, new RoutingNote { Name = "n", Text = "authored" });
            var before = File.ReadAllBytes(path);
            using var cache = new CultCache(registry);
            var store = new SingleFileMessagePackBackingStore(path, readOnly: true);
            cache.AddBackingStore(store);
            var key = new CultRecordKey("new");

            Assert.That(async () => await cache.UpsertAsync(typeof(RoutingNote), new RoutingNote(), key),
                Throws.InvalidOperationException.With.Message.Contains("read-only"));
            Assert.That(() => store.PushAll(), Throws.InvalidOperationException.With.Message.Contains(path));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(cache.Get(key), Is.Null);
            Assert.That(cache.IsDirty, Is.False);
            Assert.That(File.Exists(path + ".lock"), Is.False);
        }

        [Test]
        public async Task ReadOnlyStoreIsNotFlushed()
        {
            var registry = Registry();
            var catalogPath = PathOf("catalog.cc");
            await Seed(catalogPath, registry, new RoutingNote { Name = "n", Text = "authored" });
            var before = File.ReadAllBytes(catalogPath);
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(catalogPath, readOnly: true), typeof(RoutingNote));
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("run.cc")), typeof(RoutingOther));

            await cache.UpsertAsync(new RoutingOther { Name = "o", Text = "run" });
            await cache.FlushAsync();

            Assert.That(File.ReadAllBytes(catalogPath), Is.EqualTo(before));
            Assert.That(File.Exists(catalogPath + ".lock"), Is.False);
            Assert.That(Snapshot(PathOf("run.cc")).Records, Has.Length.EqualTo(1));
            Assert.That(cache.IsDirty, Is.False);
        }

        [Test]
        public async Task GlobalSingletonIsEnforced()
        {
            var registry = GlobalRegistry();
            using (var cache = new CultCache(registry))
            {
                await cache.UpsertAsync(new RoutingGlobal { Value = "first" });

                Assert.That(async () => await cache.UpsertAsync(typeof(RoutingGlobal), new RoutingGlobal { Value = "second" }, new CultRecordKey("second")),
                    Throws.InvalidOperationException.With.Message.Contains("is a global"));
                Assert.That(cache.AllEntries.Count(), Is.EqualTo(1));
            }

            var path = PathOf("two-globals.cc");
            WriteRecords(path, registry,
                ("global:one", FixedStoredAt, new RoutingGlobal { Value = "one" }),
                ("global:two", FixedStoredAt, new RoutingGlobal { Value = "two" }));
            using var reader = new CultCache(registry);
            Assert.That(() => reader.AddBackingStore(new SingleFileMessagePackBackingStore(path)),
                Throws.InvalidOperationException.With.Message.Contains("is a global"));
            Assert.That(reader.AllEntries, Is.Empty);
        }

        [Test]
        public async Task AmbiguousAssignableLookupThrows()
        {
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(RoutingLeaf), typeof(RoutingLeafTwin) }));
            var leaf = await cache.UpsertAsync(typeof(RoutingLeaf), new RoutingLeaf { Name = "alpha" });
            var twin = await cache.UpsertAsync(typeof(RoutingLeafTwin), new RoutingLeafTwin { Name = "alpha" });

            Assert.That(() => cache.GetByName<RoutingBase>("alpha"),
                Throws.InvalidOperationException.With.Message.Contains(leaf.Value).And.Message.Contains(twin.Value));
            Assert.That(cache.GetByName<RoutingLeaf>("alpha"), Is.Not.Null);
        }

        [Test]
        public void BatchIsAllOrNothingOnStoreFailure()
        {
            var registry = Registry();
            var path = PathOf("batch.cc");
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            cache.Commit(batch => batch.Upsert(new RoutingNote { Name = "a" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("a"))));
            var before = File.ReadAllBytes(path);
            var changes = 0;
            using var subscription = cache.Watch<RoutingNote>().Subscribe(_ => changes++);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(() => cache.Commit(batch =>
                {
                    batch.Upsert(new RoutingNote { Name = "b1" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("b1")));
                    batch.Upsert(new RoutingNote { Name = "b2" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("b2")));
                }), Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
            }

            Assert.That(cache.Get(new CultRecordKey("b1")), Is.Null);
            Assert.That(cache.Get(new CultRecordKey("b2")), Is.Null);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(cache.IsDirty, Is.False);
            Assert.That(changes, Is.Zero);
        }

        [Test]
        public void BatchAcrossTwoHomesThrows()
        {
            var registry = Registry();
            using var cache = new CultCache(registry);
            var catalog = new SingleFileMessagePackBackingStore(PathOf("catalog.cc"));
            var run = new SingleFileMessagePackBackingStore(PathOf("run.cc"));
            cache.AddBackingStore(catalog, typeof(RoutingNote));
            cache.AddBackingStore(run, typeof(RoutingOther));

            Assert.That(() => cache.Commit(batch =>
            {
                batch.Upsert(new RoutingNote { Name = "n" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("n")));
                batch.Upsert(new RoutingOther { Name = "o" }, new CultRecordHandle<RoutingOther>(new CultRecordKey("o")));
            }), Throws.InvalidOperationException.With.Message.Contains("one home store"));

            Assert.That(catalog.IsDirty, Is.False);
            Assert.That(run.IsDirty, Is.False);
            Assert.That(cache.Get(new CultRecordKey("n")), Is.Null);
            Assert.That(cache.Get(new CultRecordKey("o")), Is.Null);
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public void BatchObserversSeeOnlyCommittedRecords()
        {
            var registry = Registry();
            using var cache = new CultCache(registry);
            var changes = 0;
            var changesWhenStoreCommitted = -1;
            cache.AddBackingStore(new ObservingStore(PathOf("observed.cc"), () => changesWhenStoreCommitted = changes));
            using var subscription = cache.Watch<RoutingNote>().Subscribe(_ => changes++);

            cache.Commit(batch =>
            {
                batch.Upsert(new RoutingNote { Name = "one" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("one")));
                batch.Upsert(new RoutingNote { Name = "two" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("two")));
                Assert.That(changes, Is.Zero);
            });

            Assert.That(changesWhenStoreCommitted, Is.Zero, "a change was published before the store committed");
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(() => cache.Commit(batch =>
            {
                batch.Upsert(new RoutingNote { Name = "three" }, new CultRecordHandle<RoutingNote>(new CultRecordKey("three")));
                throw new InvalidOperationException("abandoned");
            }), Throws.InvalidOperationException);
            Assert.That(changes, Is.EqualTo(2));
        }

        [Test]
        public async Task BatchReadsSeeCommittedStateOnly()
        {
            using var cache = new CultCache(Registry());
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("reads.cc")));
            var key = await cache.UpsertAsync(typeof(RoutingNote), new RoutingNote { Name = "n", Text = "committed" });
            await cache.FlushAsync();

            cache.Commit(batch =>
            {
                batch.Upsert(typeof(RoutingNote), new RoutingNote { Name = "n", Text = "staged" }, key);
                Assert.That(cache.Get<RoutingNote>(key)?.Text, Is.EqualTo("committed"));
            });

            Assert.That(cache.Get<RoutingNote>(key)?.Text, Is.EqualTo("staged"));
        }

        [Test]
        public async Task SingleUpsertAndBatchShareOneAdmissionPath()
        {
            using var cache = new CultCache(GlobalRegistry());
            await cache.UpsertAsync(new RoutingGlobal { Value = "first" });
            var second = new CultRecordKey("second");

            var single = Assert.Throws<InvalidOperationException>(() =>
                cache.UpsertAsync(typeof(RoutingGlobal), new RoutingGlobal { Value = "second" }, second));
            var batched = Assert.Throws<InvalidOperationException>(() =>
                cache.Commit(batch => batch.Upsert(typeof(RoutingGlobal), new RoutingGlobal { Value = "second" }, second)));

            Assert.That(batched!.Message, Is.EqualTo(single!.Message));
            Assert.That(cache.Get(second), Is.Null);
        }

        [Test]
        public void PullAllAdmissionFailureLeavesStoreAndCacheConsistent()
        {
            var registry = GlobalRegistry(typeof(RoutingNote));
            var path = PathOf("pull.cc");
            WriteRecords(path, registry,
                ("note:1", "2026-01-01T00:00:00.0000000+00:00", new RoutingNote { Name = "1", Text = "old" }),
                ("global:one", FixedStoredAt, new RoutingGlobal { Value = "one" }));
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));

            WriteRecords(path, registry,
                ("note:1", "2026-01-02T00:00:00.0000000+00:00", new RoutingNote { Name = "1", Text = "new" }),
                ("note:2", "2026-01-02T00:00:00.0000000+00:00", new RoutingNote { Name = "2", Text = "arrived" }),
                ("global:one", FixedStoredAt, new RoutingGlobal { Value = "one" }),
                ("global:two", FixedStoredAt, new RoutingGlobal { Value = "two" }));
            Assert.That(async () => await cache.PullAllBackingStoresAsync(), Throws.InvalidOperationException);
            Assert.That(cache.Get<RoutingNote>(new CultRecordKey("note:1"))?.Text, Is.EqualTo("old"));
            Assert.That(cache.Get(new CultRecordKey("note:2")), Is.Null);

            WriteRecords(path, registry,
                ("note:1", "2026-01-02T00:00:00.0000000+00:00", new RoutingNote { Name = "1", Text = "new" }),
                ("note:2", "2026-01-02T00:00:00.0000000+00:00", new RoutingNote { Name = "2", Text = "arrived" }),
                ("global:one", FixedStoredAt, new RoutingGlobal { Value = "one" }));
            cache.PullAllBackingStoresAsync();
            Assert.That(cache.Get<RoutingNote>(new CultRecordKey("note:1"))?.Text, Is.EqualTo("new"),
                "the refused pull left the store believing it already held the new record");
            Assert.That(cache.Get<RoutingNote>(new CultRecordKey("note:2"))?.Text, Is.EqualTo("arrived"));
        }

        [Test]
        public void StoreAttachedToSecondCacheThrows()
        {
            var registry = Registry();
            var store = new SingleFileMessagePackBackingStore(PathOf("shared.cc"));
            using var first = new CultCache(registry);
            using var second = new CultCache(registry);
            first.AddBackingStore(store);

            Assert.That(() => second.AddBackingStore(store), Throws.InvalidOperationException.With.Message.Contains("already attached"));
            Assert.That(second.BackingStores, Is.Empty);
        }

        [Test]
        public void PullAllPullsEveryStoreWhenAHandlerThrows()
        {
            var registry = Registry();
            var notes = PathOf("notes.cc");
            var others = PathOf("others.cc");
            const string before = "2026-01-01T00:00:00.0000000+00:00";
            const string after = "2026-01-02T00:00:00.0000000+00:00";
            WriteRecords(notes, registry, ("note", before, new RoutingNote { Name = "note", Text = "old" }));
            WriteRecords(others, registry, ("other", before, new RoutingOther { Name = "other", Text = "old" }));
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(notes), typeof(RoutingNote));
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(others), typeof(RoutingOther));

            WriteRecords(notes, registry, ("note", after, new RoutingNote { Name = "note", Text = "new" }));
            WriteRecords(others, registry, ("other", after, new RoutingOther { Name = "other", Text = "new" }));
            cache.OnUpdate += (_, document) =>
            {
                if (document is RoutingNote)
                    throw new InvalidOperationException("handler refuses notes");
            };

            Assert.That(() => cache.PullAllBackingStoresAsync(), Throws.InvalidOperationException.With.Message.Contains("handler refuses notes"));
            Assert.That(cache.Get<RoutingOther>(new CultRecordKey("other"))?.Text, Is.EqualTo("new"), "the second store was not pulled");
        }

        [Test]
        public async Task NestedHoldOnAnotherCacheThrows()
        {
            var registry = Registry();
            using var second = new CultCache(registry);
            using var first = new CultCache(registry);
            first.AddBackingStore(new NestingStore(PathOf("nesting.cc"), () => second.UpsertAsync(new RoutingOther { Name = "other" })));
            await first.UpsertAsync(new RoutingNote { Name = "note" });

            Assert.That(() => first.FlushAllBackingStores(), Throws.InvalidOperationException.With.Message.Contains("another cache's hold"));
            Assert.That(second.AllEntries, Is.Empty);
        }

        private const string FixedStoredAt = "2026-09-13T00:00:00.0000000+00:00";

        private string PathOf(string fileName) => Path.Combine(_directory, fileName);

        private static CultDocumentRegistry Registry() =>
            CultDocumentRegistry.ForTypes(new[] { typeof(RoutingNote), typeof(RoutingOther) });

        private static CultDocumentRegistry GlobalRegistry(params Type[] others) =>
            CultDocumentRegistry.ForTypes(new[] { typeof(RoutingGlobal) }.Concat(others));

        private static async Task<CultRecordKey> Seed(string path, CultDocumentRegistry registry, object document, CultRecordKey? key = null)
        {
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            var written = await cache.UpsertAsync(document.GetType(), document, key);
            cache.FlushAllBackingStores();
            File.Delete(path + ".lock");
            return written;
        }

        private static void WriteRecords(string path, CultDocumentRegistry registry, params (string Key, string StoredAt, object Document)[] records)
        {
            var descriptors = records.Select(record => registry.GetRequired(record.Document.GetType())).ToArray();
            var snapshot = new CultPersistedStoreSnapshot
            {
                SchemaCatalog = descriptors.Distinct().Select(descriptor => descriptor.ToCatalogEntry()).ToArray(),
                Records = records.Select((record, index) => new CultPersistedRecord
                {
                    Key = record.Key,
                    SchemaId = descriptors[index].SchemaId,
                    StoredAt = record.StoredAt,
                    Payload = CultDocumentMessagePackSerialization.SerializeUntyped(record.Document, record.Document.GetType(), registry)
                }).ToArray()
            };
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        private static CultPersistedStoreSnapshot Snapshot(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        private static byte[] Normalized(string path)
        {
            var snapshot = Snapshot(path);
            foreach (var record in snapshot.Records)
                record.StoredAt = FixedStoredAt;
            return CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot);
        }

        private sealed class RefusingStore : SingleFileMessagePackBackingStore
        {
            public RefusingStore(string filePath) : base(filePath)
            {
            }

            public override void Push(CultStoredDocument entry) =>
                throw new InvalidOperationException("RefusingStore refuses every write.");
        }

        private sealed class NestingStore : SingleFileMessagePackBackingStore
        {
            private readonly Action _nested;

            public NestingStore(string filePath, Action nested) : base(filePath)
            {
                _nested = nested;
            }

            public override void PushAll()
            {
                _nested();
                base.PushAll();
            }
        }

        private sealed class ObservingStore : SingleFileMessagePackBackingStore
        {
            private readonly Action _committed;

            public ObservingStore(string filePath, Action committed) : base(filePath)
            {
                _committed = committed;
            }

            public override CultCommitOutcome CommitBatch(CultCommitRequest request, bool wait)
            {
                var outcome = base.CommitBatch(request, wait);
                _committed();
                return outcome;
            }
        }

        [CultDocument("tests.routing_note", "tests.routing_note.v1")]
        internal sealed class RoutingNote
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Text = string.Empty;
        }

        [CultDocument("tests.routing_other", "tests.routing_other.v1")]
        internal sealed class RoutingOther
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Text = string.Empty;
        }

        [CultDocument("tests.routing_gear", "tests.routing_gear.v1")]
        public class RoutingGear
        {
            [Key(0)]
            public string Name = string.Empty;
        }

        [CultDocument("tests.routing_weapon", "tests.routing_weapon.v1")]
        public sealed class RoutingWeapon : RoutingGear
        {
            [Key(1)]
            public int Damage;
        }

        internal abstract class RoutingBase
        {
        }

        [CultDocument("tests.routing_leaf", "tests.routing_leaf.v1")]
        internal sealed class RoutingLeaf : RoutingBase
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.routing_leaf_twin", "tests.routing_leaf_twin.v1")]
        internal sealed class RoutingLeafTwin : RoutingBase
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        internal abstract class RoutingGlobalBase
        {
        }

        [CultDocument("tests.routing_global", "tests.routing_global.v1")]
        [CultGlobal]
        internal sealed class RoutingGlobal : RoutingGlobalBase
        {
            [Key(0)]
            public string Value = "schema default";
        }
    }
}
