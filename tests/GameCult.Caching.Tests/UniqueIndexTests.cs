#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // variants:ruling:unique-indexes. Within one document type and one [CultIndex] alias, no two held records hold one
    // non-blank value; CultCache.Resolve judges every commit and every load, and a refusal never echoes the value.
    public class UniqueIndexTests
    {
        private const string Canary = "CANARY-7f3a";

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[]
        {
            typeof(UqItem), typeof(UqTwin), typeof(UqTagged)
        });

        // Emitted, so CultDocumentRegistry.Shared never scans them (one schema declared two ways, and a member Shared would refuse).
        // The same schema name, version and shape, once as a role and once as a unique index: a role store holds the pair, the index type refuses it.
        private static readonly Type UqLoadRole = EmittedDocumentTypes.Emit(
            "UqLoadRole", "tests.uq_load", "tests.uq_load.v1", new[] { new EmittedDocumentTypes.Field("Code", typeof(string), 0, Role: "code") });
        private static readonly Type UqLoadIndex = EmittedDocumentTypes.Emit(
            "UqLoadIndex", "tests.uq_load", "tests.uq_load.v1", new[] { new EmittedDocumentTypes.Field("Code", typeof(string), 0, Index: "code") });
        private static readonly Type UqBoth = EmittedDocumentTypes.Emit(
            "UqBoth", "tests.uq_both", "tests.uq_both.v1", new[] { new EmittedDocumentTypes.Field("Both", typeof(string), 0, Index: "x", Role: "x") });

        private static object Load(Type type, string code) => EmittedDocumentTypes.New(type, ("Code", code));

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-uidx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path, CultDocumentRegistry? registry = null) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry ?? Registry });

        private static CultRecordKey Key(string name) => new(name);

        private static UqItem Item(string? sku) => new() { Sku = sku };

        private static InvalidOperationException Refused(TestDelegate write) => Assert.Throws<InvalidOperationException>(write)!;

        [Test]
        public void ABatchLeavingTwoRecordsWithOneValueIsRefusedNamingBothKeysAndTheIndex()
        {
            var path = PathOf("items.cc");
            using var cache = Open(path);
            cache.Commit(batch => batch.Upsert(typeof(UqItem), Item("keep"), Key("seed")));
            var before = File.ReadAllBytes(path);

            var refused = Refused(() => cache.Commit(batch =>
            {
                batch.Upsert(typeof(UqItem), Item(Canary), Key("zeta"));
                batch.Upsert(typeof(UqItem), Item(Canary), Key("alpha"));
            }));

            Assert.That(refused.Message, Does.Contain("alpha").And.Contain("zeta").And.Contain("sku").And.Contain("tests.uq_item"));
            Assert.That(refused.Message.IndexOf("alpha", StringComparison.Ordinal), Is.LessThan(refused.Message.IndexOf("zeta", StringComparison.Ordinal)), "keys in ordinal order");
            Assert.That(refused.Message, Does.Not.Contain(Canary), "the value is never echoed");
            Assert.That(cache.Get(Key("alpha")), Is.Null);
            Assert.That(cache.Get(Key("zeta")), Is.Null);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "the store is byte-identical");
            Assert.That(cache.GetByIndex<UqItem>("sku", Canary), Is.Null);
        }

        [Test]
        public async Task AWriteOfAHeldValueAtAnotherKeyIsRefused()
        {
            using var cache = Open(PathOf("items.cc"));
            await cache.UpsertAsync(typeof(UqItem), Item(Canary), Key("first"));

            var single = Assert.ThrowsAsync<InvalidOperationException>(() => cache.UpsertAsync(typeof(UqItem), Item(Canary), Key("second")))!;
            Assert.That(single.Message, Does.Contain("first").And.Contain("second").And.Contain("sku").And.Not.Contain(Canary));
            var batched = Refused(() => cache.Commit(batch => batch.Upsert(typeof(UqItem), Item(Canary), Key("third"))));
            Assert.That(batched.Message, Does.Contain("first").And.Contain("third"));

            Assert.That(cache.Get(Key("second")), Is.Null);
            Assert.That(cache.Get(Key("third")), Is.Null);
            Assert.That(cache.GetByIndex<UqItem>("sku", Canary), Is.Not.Null);

            await cache.UpsertAsync(typeof(UqItem), Item(Canary), Key("first"));
        }

        [Test]
        public void SwappingTwoValuesInOneCommitLandsAndBothAreFound()
        {
            var path = PathOf("items.cc");
            using (var cache = Open(path))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(UqItem), Item("one"), Key("a"));
                    batch.Upsert(typeof(UqItem), Item("two"), Key("b"));
                });
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(UqItem), Item("two"), Key("a"));
                    batch.Upsert(typeof(UqItem), Item("one"), Key("b"));
                });

                Assert.That(cache.GetStored(Key("a"))!.Document, Is.SameAs(cache.GetByIndex<UqItem>("sku", "two")));
                Assert.That(cache.GetStored(Key("b"))!.Document, Is.SameAs(cache.GetByIndex<UqItem>("sku", "one")));
            }

            using var reopened = Open(path);
            Assert.That(reopened.GetStored(Key("a"))!.Document, Is.SameAs(reopened.GetByIndex<UqItem>("sku", "two")));
            Assert.That(reopened.GetStored(Key("b"))!.Document, Is.SameAs(reopened.GetByIndex<UqItem>("sku", "one")));
        }

        [Test]
        public void AValueMovesWhenItsHolderIsRemovedOrChangedInTheSameCommit()
        {
            using var cache = Open(PathOf("items.cc"));
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(UqItem), Item("one"), Key("a"));
                batch.Upsert(typeof(UqItem), Item("two"), Key("b"));
            });

            cache.Commit(batch =>
            {
                batch.Remove(Key("a"));
                batch.Upsert(typeof(UqItem), Item("one"), Key("c"));
            });
            Assert.That(cache.GetStored(Key("c"))!.Document, Is.SameAs(cache.GetByIndex<UqItem>("sku", "one")), "removed holder released its value");

            cache.Commit(batch =>
            {
                batch.Upsert(typeof(UqItem), Item("three"), Key("b"));
                batch.Upsert(typeof(UqItem), Item("two"), Key("d"));
            });
            Assert.That(cache.GetStored(Key("d"))!.Document, Is.SameAs(cache.GetByIndex<UqItem>("sku", "two")), "changed holder released its value");
            Assert.That(cache.GetStored(Key("b"))!.Document, Is.SameAs(cache.GetByIndex<UqItem>("sku", "three")));
            Assert.That(cache.GetByIndex<UqItem>("sku", "one")!.Sku, Is.EqualTo("one"));
        }

        [Test]
        public void BlankValuesNeverConflict()
        {
            var path = PathOf("items.cc");
            using (var cache = Open(path))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(UqItem), Item(null), Key("a"));
                    batch.Upsert(typeof(UqItem), Item(""), Key("b"));
                    batch.Upsert(typeof(UqItem), Item("   "), Key("c"));
                    batch.Upsert(typeof(UqItem), Item(null), Key("d"));
                });
                Assert.That(cache.GetByIndex<UqItem>("sku", ""), Is.Null);
                Assert.That(cache.GetByIndex<UqItem>("sku", "   "), Is.Null);
            }

            using var reopened = Open(path);
            Assert.That(reopened.Get(Key("d")), Is.Not.Null, "a store of blank values reopens");
        }

        [Test]
        public void TwoTypesMayShareAnAliasAndAValue()
        {
            using var cache = Open(PathOf("items.cc"));
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(UqItem), Item("same"), Key("item"));
                batch.Upsert(typeof(UqTwin), new UqTwin { Sku = "same" }, Key("twin"));
            });

            Assert.That(cache.GetByIndex<UqItem>("sku", "same"), Is.Not.Null);
            Assert.That(cache.GetByIndex<UqTwin>("sku", "same"), Is.Not.Null);
            Assert.That(Registry.GetRequired<UqItem>().DeclaredMembers.Single().IndexAlias, Is.EqualTo("sku"), "a unique index addresses selection as a role does");
        }

        [Test]
        public void RoleMembersMayShareValuesAndStaySelectable()
        {
            var path = PathOf("tagged.cc");
            using (var cache = Open(path))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(UqTagged), new UqTagged { Kind = "weapon" }, Key("a"));
                    batch.Upsert(typeof(UqTagged), new UqTagged { Kind = "weapon" }, Key("b"));
                });
                Assert.That(cache.GetByIndex<UqTagged>("kind", "weapon"), Is.Null, "a role is not a lookup");
            }

            using var reopened = Open(path);
            Assert.That(reopened.Get(Key("b")), Is.Not.Null, "a role pair reopens");
            var member = Registry.GetRequired<UqTagged>().DeclaredMembers.Single(candidate => candidate.MemberName == nameof(UqTagged.Kind));
            Assert.That(member.IndexAlias, Is.EqualTo("kind"), "the role still supplies the selection address");
        }

        [Test]
        public void AStoreHoldingDuplicatesRefusesToOpenNamingBothKeysAndTheIndex()
        {
            var path = PathOf("load.cc");
            var roleRegistry = CultDocumentRegistry.ForTypes(new[] { UqLoadRole });
            using (var writer = Open(path, roleRegistry))
            {
                writer.Commit(batch =>
                {
                    batch.Upsert(UqLoadRole, Load(UqLoadRole, Canary), Key("m"));
                    batch.Upsert(UqLoadRole, Load(UqLoadRole, Canary), Key("k"));
                });
            }

            var before = File.ReadAllBytes(path);
            var indexRegistry = CultDocumentRegistry.ForTypes(new[] { UqLoadIndex });
            var refused = Refused(() => Open(path, indexRegistry).Dispose());

            Assert.That(refused.Message, Does.Contain("k").And.Contain("m").And.Contain("code").And.Not.Contain(Canary));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "the file is untouched");

            using var cache = new CultCache(indexRegistry);
            var store = new SingleFileMessagePackBackingStore(path);
            Refused(() => cache.AddBackingStore(store));
            Assert.That(cache.BackingStores, Is.Empty, "the refused store is not attached");
            Assert.That(cache.Get(Key("k")), Is.Null);
        }

        [Test]
        public void AWriteOntoAMovedFileIsAConflictAndTheRetryIsRefused()
        {
            var path = PathOf("items.cc");
            using var seed = Open(path);
            seed.Commit(batch => batch.Upsert(typeof(UqItem), Item("base"), Key("a")));
            using var second = Open(path);
            using (var first = Open(path))
                first.Commit(batch => batch.Upsert(typeof(UqItem), Item(Canary), Key("b")));

            var conflict = Assert.Throws<CultWriteConflictException>(
                () => second.Commit(batch => batch.Upsert(typeof(UqItem), Item(Canary), Key("c"))))!;
            Assert.That(conflict.Message, Does.Contain("unique index"));
            Assert.That(second.Get(Key("b")), Is.Not.Null, "the moved record is loaded");

            var retry = Refused(() => second.Commit(batch => batch.Upsert(typeof(UqItem), Item(Canary), Key("c"))));
            Assert.That(retry.Message, Does.Contain("b").And.Contain("c").And.Not.Contain(Canary));

            using var reopened = Open(path);
            Assert.That(reopened.Get(Key("c")), Is.Null, "the file never holds the pair");
            Assert.That(reopened.Get(Key("b")), Is.Not.Null);
        }

        [Test]
        public void CachelessWritesOfAUniqueIndexedTypeAreRefused()
        {
            var path = PathOf("items.cc");
            using (var cache = Open(path))
                cache.Commit(batch => batch.Upsert(typeof(UqItem), Item("a"), Key("a")));
            var before = File.ReadAllBytes(path);
            var store = new SingleFileMessagePackBackingStore(path);

            Refused(() => store.WriteDocument(Key("x"), Item(Canary), registry: Registry));
            var entry = Registry.GetRequired<UqItem>().ToCatalogEntry();
            Refused(() => store.WriteRecord(
                new CultPersistedRecord { Key = "y", SchemaId = entry.SchemaId, StoredAt = DateTimeOffset.UtcNow.ToString("O"), Payload = new byte[] { 0x90 } },
                entry,
                registry: Registry));

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "the file is byte-identical");
            store.WriteDocument(Key("t"), new UqTagged { Kind = "k" }, registry: Registry);
            using var reopened = Open(path);
            Assert.That(reopened.Get(Key("t")), Is.Not.Null, "a role-only type still writes cacheless");
        }

        [Test]
        public void AMemberWithBothAttributesIsRefusedAtRegistration()
        {
            var refused = Refused(() => CultDocumentRegistry.ForTypes(new[] { UqBoth }));
            Assert.That(refused.Message, Does.Contain("Both").And.Contain("[CultIndex]").And.Contain("[CultRole]"));
        }

        [CultDocument("tests.uq_item", "tests.uq_item.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class UqItem
        {
            [Key(0)] [CultIndex("sku")] public string? Sku { get; set; }
        }

        [CultDocument("tests.uq_twin", "tests.uq_twin.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class UqTwin
        {
            [Key(0)] [CultIndex("sku")] public string? Sku { get; set; }
        }

        [CultDocument("tests.uq_tagged", "tests.uq_tagged.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class UqTagged
        {
            [Key(0)] [CultRole("kind")] public string? Kind { get; set; }
        }
    }
}
