#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RoutingNote) });
            var first = PathOf("a.cc");
            var second = PathOf("b.cc");
            await Seed(first, registry, new RoutingNote { Name = "note", Text = "seeded" });

            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(first));
            await cache.PullAllBackingStoresAsync();
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(second));
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

        private string PathOf(string fileName) => Path.Combine(_directory, fileName);

        private static async Task<CultRecordKey> Seed(string path, CultDocumentRegistry registry, object document)
        {
            using var cache = new CultCache(registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            var key = await cache.UpsertAsync(document.GetType(), document);
            cache.FlushAllBackingStores();
            return key;
        }

        // A [CultGlobal] type in this assembly would be discovered by CultDocumentRegistry.Shared, and today's
        // constructor would invent it inside every other test's cache. The flag lives on a private registry instead.
        private static CultDocumentRegistry GlobalRegistry(params Type[] others)
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(RoutingGlobal) }.Concat(others));
            typeof(CultDocumentDescriptor)
                .GetField("<IsGlobal>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(registry.GetRequired<RoutingGlobal>(), true);
            Assert.That(registry.GetRequired<RoutingGlobal>().IsGlobal, Is.True, "precondition: the global flag did not take");
            return registry;
        }

        private sealed class RefusingStore : SingleFileMessagePackBackingStore
        {
            public RefusingStore(string filePath) : base(filePath)
            {
            }

            public override void Push(CultStoredDocument entry) =>
                throw new InvalidOperationException("RefusingStore refuses every write.");
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

        // RoutingWeapon's own slots are not dense, so it gets no generated codec and serializes through
        // MessagePack's dynamic resolver, which needs a public [MessagePackObject].
        [CultDocument("tests.routing_gear", "tests.routing_gear.v1")]
        [MessagePackObject]
        public class RoutingGear
        {
            [Key(0)]
            public string Name = string.Empty;
        }

        [CultDocument("tests.routing_weapon", "tests.routing_weapon.v1")]
        [MessagePackObject]
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

        internal abstract class RoutingGlobalBase
        {
        }

        [CultDocument("tests.routing_global", "tests.routing_global.v1")]
        internal sealed class RoutingGlobal : RoutingGlobalBase
        {
            [Key(0)]
            public string Value = "schema default";
        }
    }
}
