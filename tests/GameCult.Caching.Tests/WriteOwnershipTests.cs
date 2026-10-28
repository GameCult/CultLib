#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // variants:ruling:write-ownership-by-written-type and variants:ruling:cache-refuses-retype: an upsert replaces the record stored at
    // its key only when the type it writes there owns that record. Which type owns the id, and which other types a registry holds,
    // change nothing; retyping a key is a removal and an add. The rule is one per-key predicate (CultDocumentRegistry.WrittenOwns)
    // for a cache and a cacheless writer, in a single-file store and a directory store.
    public class WriteOwnershipTests
    {
        // Two schemas of one shape that are never the same type.
        [CultDocument("tests.wo_a", "tests.wo_a.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WoA
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        [CultDocument("tests.wo_b", "tests.wo_b.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WoB
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        private static readonly CultDocumentRegistry ARegistry = CultDocumentRegistry.ForTypes(new[] { typeof(WoA) });
        private static readonly CultDocumentRegistry ABRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(WoA), typeof(WoB) });
        private static readonly CultRecordKey K1 = new("k1");
        private static readonly CultRecordKey K2 = new("k2");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-writeownership-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        public static readonly object[] StoreKinds = { false, true };

        private string PathFor(bool directory) => Path.Combine(_directory, directory ? "store" : "store.cc");

        private static CultCache Open(string path, bool directory, CultDocumentRegistry registry)
        {
            var cache = new CultCache(registry);
            cache.AddBackingStore(directory ? new DirectoryMessagePackBackingStore(path) : new SingleFileMessagePackBackingStore(path));
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            return cache;
        }

        private static string Fingerprint(string path)
        {
            using var sha = SHA256.Create();
            if (File.Exists(path))
                return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
            return string.Join("|", Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)))));
        }

        private static Type TypeAt(string path, bool directory, CultDocumentRegistry registry, CultRecordKey key)
        {
            using var cache = Open(path, directory, registry);
            return cache.GetStored(key)!.Descriptor.DocumentType;
        }

        // A store that a cache holding both types wrote: A at k1 and B at k2.
        private string SeedAAtK1BAtK2(bool directory)
        {
            var path = PathFor(directory);
            using var cache = Open(path, directory, ABRegistry);
            Assert.That(cache.Commit(batch =>
            {
                batch.Upsert(new WoA { Name = "k1", Value = 1 }, new CultRecordHandle<WoA>(K1));
                batch.Upsert(new WoB { Name = "k2", Value = 2 }, new CultRecordHandle<WoB>(K2));
            }), Is.True);
            return path;
        }

        // P1: a cache holding A and B may not replace B's record with A's, whoever else the registry declares.
        [TestCaseSource(nameof(StoreKinds))]
        public void ACacheRefusesToWriteOneRegisteredTypeOverAnothersRecord(bool directory)
        {
            var path = SeedAAtK1BAtK2(directory);
            var before = Fingerprint(path);

            using (var cache = Open(path, directory, ABRegistry))
            {
                Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Upsert(new WoA { Name = "k2", Value = 9 }, new CultRecordHandle<WoA>(K2))));
                Assert.That(cache.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(WoB)), "the cache still holds B's record");
                Assert.Throws<CultSchemaConflictException>(() => cache.UpsertAsync(typeof(WoA), new WoA { Name = "k2", Value = 9 }, K2));
                Assert.That(cache.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(WoB)), "a staged write is refused at the call, not at the flush");
            }

            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(TypeAt(path, directory, ABRegistry, K2), Is.EqualTo(typeof(WoB)));

            // The pair that differs only in the type written: B over B's own record.
            using (var cache = Open(path, directory, ABRegistry))
                Assert.That(cache.Commit(batch => batch.Upsert(new WoB { Name = "k2", Value = 3 }, new CultRecordHandle<WoB>(K2))), Is.True);
        }

        // Retype is a removal and an add, in separate commits.
        [TestCaseSource(nameof(StoreKinds))]
        public void ARetypeIsARemovalAndAnAddInSeparateCommits(bool directory)
        {
            var path = SeedAAtK1BAtK2(directory);

            using (var cache = Open(path, directory, ABRegistry))
            {
                Assert.That(cache.Commit(batch => batch.Remove(K2)), Is.True);
                Assert.That(cache.Commit(batch => batch.Upsert(new WoA { Name = "k2", Value = 9 }, new CultRecordHandle<WoA>(K2))), Is.True);
            }

            Assert.That(TypeAt(path, directory, ABRegistry, K2), Is.EqualTo(typeof(WoA)));
            Assert.That(TypeAt(path, directory, ABRegistry, K1), Is.EqualTo(typeof(WoA)), "the bystander is untouched");
        }

        // The store is judged again where the write lands, under its lock: another writer retyped the key after this cache loaded it,
        // so this cache's own-type write of the old record is now a write over a record that is not its type's.
        [TestCaseSource(nameof(StoreKinds))]
        public void AnotherWritersRetypeRefusesThisCachesStaleWriteUnderTheLock(bool directory)
        {
            var path = SeedAAtK1BAtK2(directory);
            using var stale = Open(path, directory, ABRegistry);
            Assert.That(stale.GetStored(K1)!.Descriptor.DocumentType, Is.EqualTo(typeof(WoA)));
            using (var other = Open(path, directory, ABRegistry))
            {
                Assert.That(other.Commit(batch => batch.Remove(K1)), Is.True);
                Assert.That(other.Commit(batch => batch.Upsert(new WoB { Name = "k1", Value = 5 }, new CultRecordHandle<WoB>(K1))), Is.True);
            }

            var before = Fingerprint(path);

            Assert.Throws<CultSchemaConflictException>(() => stale.Commit(batch => batch.Upsert(new WoA { Name = "k1", Value = 9 }, new CultRecordHandle<WoA>(K1))));

            Assert.That(Fingerprint(path), Is.EqualTo(before));
            Assert.That(TypeAt(path, directory, ABRegistry, K1), Is.EqualTo(typeof(WoB)));
        }

        public static readonly object[] Registries = { "none", "A", "AB", "Shared" };

        private static CultDocumentRegistry? Lent(object kind) => kind switch
        {
            "A" => ARegistry,
            "AB" => ABRegistry,
            "Shared" => CultDocumentRegistry.Shared,
            _ => null,
        };

        // P3: a cacheless write of A over B's record is refused whatever registry is lent, and the same lent or not.
        [TestCaseSource(nameof(Registries))]
        public void ACachelessWriteOfOneTypeOverAnothersRecordIsRefusedWhateverRegistryIsLent(object lent)
        {
            var path = SeedAAtK1BAtK2(directory: false);
            var before = Fingerprint(path);
            var store = new SingleFileMessagePackBackingStore(path);

            Assert.Throws<CultSchemaConflictException>(() => store.WriteDocument(K2, new WoA { Name = "k2", Value = 9 }, registry: Lent(lent)));
            Assert.That(Fingerprint(path), Is.EqualTo(before));

            // WriteRecord names its entry and nothing else.
            var entry = ARegistry.GetRequired<WoA>().ToCatalogEntry();
            var record = new CultPersistedRecord
            {
                Key = K2.Value,
                SchemaId = entry.SchemaId,
                StoredAt = "2026-10-28T00:00:00.0000000+00:00",
                Payload = MessagePackSerializer.Serialize(new WoA { Name = "k2", Value = 9 }),
            };
            Assert.Throws<CultSchemaConflictException>(() => store.WriteRecord(record, entry, null, Lent(lent)));
            Assert.That(Fingerprint(path), Is.EqualTo(before));

            // The pair that differs only in whose record it is: A over A's own, which commits for every registry.
            store.WriteDocument(K1, new WoA { Name = "k1", Value = 9 }, registry: Lent(lent) ?? ARegistry);
            Assert.That(TypeAt(path, false, ABRegistry, K1), Is.EqualTo(typeof(WoA)));
        }

        // The judge is per key, and an answer for one written type is never reused for another. Another writer left A's record at two
        // keys; this stale cache holds A at one and B at the other and writes both. The key A owns lands, the key where B meets A's
        // record is the one refused, whichever is judged first.
        [TestCase("a-own", "b-bad", true, false)]
        [TestCase("a-own", "b-bad", false, false)]
        [TestCase("b-own", "a-bad", true, true)]
        [TestCase("b-own", "a-bad", false, true)]
        public void AStaleBatchIsJudgedPerKeyByEachKeysWrittenType(string ownKey, string badKey, bool ownFirst, bool directory)
        {
            var path = PathFor(directory);
            var own = new CultRecordKey(ownKey);
            var bad = new CultRecordKey(badKey);
            using (var seed = Open(path, directory, ABRegistry))
                Assert.That(seed.Commit(batch =>
                {
                    batch.Upsert(new WoA { Name = ownKey, Value = 1 }, new CultRecordHandle<WoA>(own));
                    batch.Upsert(new WoB { Name = badKey, Value = 1 }, new CultRecordHandle<WoB>(bad));
                }), Is.True);
            using var stale = Open(path, directory, ABRegistry);
            using (var other = Open(path, directory, ABRegistry))
            {
                Assert.That(other.Commit(batch => batch.Remove(bad)), Is.True);
                Assert.That(other.Commit(batch => batch.Upsert(new WoA { Name = badKey, Value = 2 }, new CultRecordHandle<WoA>(bad))), Is.True);
            }

            var before = Fingerprint(path);
            void StageOwn(CultCacheBatch batch) => batch.Upsert(new WoA { Name = ownKey, Value = 3 }, new CultRecordHandle<WoA>(own));
            void StageBad(CultCacheBatch batch) => batch.Upsert(new WoB { Name = badKey, Value = 3 }, new CultRecordHandle<WoB>(bad));

            var refusal = Assert.Throws<CultSchemaConflictException>(() => stale.Commit(batch =>
            {
                if (ownFirst) { StageOwn(batch); StageBad(batch); } else { StageBad(batch); StageOwn(batch); }
            }))!;

            Assert.That(refusal.RecordKey, Is.EqualTo(badKey));
            Assert.That(refusal.Message, Does.Not.Contain($"'{ownKey}'"), "the key A owns is not among the refused");
            Assert.That(Fingerprint(path), Is.EqualTo(before));
        }

        // A batch that removes a key is a removal of it, whatever this cache has staged there: the removal rule judges it, so a key
        // another writer retyped can still be removed by the cache that staged a write of the old type.
        [TestCaseSource(nameof(StoreKinds))]
        public void ABatchRemovalOfAStagedKeyIsJudgedAsARemoval(bool directory)
        {
            var path = SeedAAtK1BAtK2(directory);
            using var stale = Open(path, directory, ABRegistry);
            stale.UpsertAsync(typeof(WoA), new WoA { Name = "k1", Value = 9 }, K1).GetAwaiter().GetResult();
            using (var other = Open(path, directory, ABRegistry))
            {
                Assert.That(other.Commit(batch => batch.Remove(K1)), Is.True);
                Assert.That(other.Commit(batch => batch.Upsert(new WoB { Name = "k1", Value = 5 }, new CultRecordHandle<WoB>(K1))), Is.True);
            }

            Assert.That(stale.Commit(batch => batch.Remove(K1)), Is.True);

            using var reopened = Open(path, directory, ABRegistry);
            Assert.That(reopened.GetStored(K1), Is.Null);
            Assert.That(reopened.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(WoB)), "the bystander is untouched");
        }
    }
}
