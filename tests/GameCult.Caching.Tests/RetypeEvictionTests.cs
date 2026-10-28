#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    // variants:ruling:one-batch-retype-is-eviction-then-add: a batch that removes a key and adds a record there stages the held
    // record's eviction and then the add, so watchers, the batch validators, the store judges and the removal bookkeeping see what they
    // see when the two are staged apart (cache.Remove, then UpsertAsync). Where a test runs both shapes it asserts the same answer.
    public class RetypeEvictionTests
    {
        [CultDocument("tests.ev_a", "tests.ev_a.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class EvA
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        [CultDocument("tests.ev_b", "tests.ev_b.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class EvB
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        // A type only the second writer registers.
        [CultDocument("tests.ev_c", "tests.ev_c.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class EvC
        {
            [Key(0)] [CultName] public string Name = string.Empty;
        }

        [CultDocument("tests.ev_g", "tests.ev_g.v1")]
        [MessagePackObject(AllowPrivate = true)]
        [CultGlobal]
        internal sealed class EvG
        {
            [Key(0)] public string Value = string.Empty;
        }

        // RbX's schema id, which RbY lists. The test below asserts it, so a changed derivation fails there.
        private const string RbXId = "sha256:25087e0d76fd5b9ac950723ccd47f3f3ac1428b53b49ca9bda9aae8b38a0220a";

        // The rebase pair: RbY lists RbX's id compatible, so a plain write of RbY owns an RbX record; RbZ lists nothing.
        [CultDocument("tests.rb_x", "tests.rb_x.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RbX
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        [CultDocument("tests.rb_y", "tests.rb_y.v1", CompatibleSchemaIds = new[] { RbXId })]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RbY
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        [CultDocument("tests.rb_z", "tests.rb_z.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RbZ
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        private static readonly CultDocumentRegistry AB = CultDocumentRegistry.ForTypes(new[] { typeof(EvA), typeof(EvB), typeof(EvG) });
        private static readonly CultDocumentRegistry ABC = CultDocumentRegistry.ForTypes(new[] { typeof(EvA), typeof(EvB), typeof(EvC), typeof(EvG) });
        private static readonly CultDocumentRegistry Rb = CultDocumentRegistry.ForTypes(new[] { typeof(RbX), typeof(RbY), typeof(RbZ) });
        private static readonly CultRecordKey K1 = new("k1");
        private static readonly CultRecordKey K2 = new("k2");
        private static readonly CultRecordKey K3 = new("k3");

        // The stores a retype is judged in, and no store at all. The directory store holds no variant, so variant tests skip it.
        public static readonly object[] Kinds = { "single-file", "directory", "storeless" };
        public static readonly object[] VariantKinds = { "single-file", "storeless" };

        private string _directory = string.Empty;
        private int _paths;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-retypeeviction-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        // A fresh store path per call, so one test can run a scenario twice.
        private string NewPath(string kind) => Path.Combine(_directory, $"{kind}-{_paths++}", kind == "directory" ? "store" : "store.cc");

        private static CultCache Open(string path, string kind, CultDocumentRegistry registry)
        {
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            if (kind != "storeless")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                cache.AddBackingStore(kind == "directory" ? new DirectoryMessagePackBackingStore(path) : new SingleFileMessagePackBackingStore(path));
                cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            }

            return cache;
        }

        private static Type TypeAt(string path, string kind, CultDocumentRegistry registry, CultRecordKey key)
        {
            using var cache = Open(path, kind, registry);
            return cache.GetStored(key)!.Descriptor.DocumentType;
        }

        // Retypes a held key as one batch (Remove, then the add the caller stages) or staged apart (cache.Remove, then the add's own call).
        private static void Retype(CultCache cache, bool oneBatch, CultRecordKey key, Action<CultCacheBatch> add, Action apart)
        {
            if (oneBatch)
                Assert.That(cache.Commit(batch =>
                {
                    batch.Remove(key);
                    add(batch);
                }), Is.True);
            else
            {
                Assert.That(cache.Remove(key), Is.True);
                apart();
            }
        }

        private static void PutB(CultCache cache, CultRecordKey key, int value = 2) =>
            Assert.That(cache.Commit(batch => batch.Upsert(typeof(EvB), new EvB { Name = key.Value, Value = value }, key)), Is.True);

        // finding retype-not-an-eviction: Watch<old> sees Removed and Watch<new> sees Added, in a batch and staged apart alike.
        [TestCaseSource(nameof(Kinds))]
        public void ARetypeIsRemovedForTheOldTypeAndAddedForTheNewWhetherOneBatchOrStagedApart(string kind)
        {
            foreach (var oneBatch in new[] { true, false })
            {
                using var cache = Open(NewPath(kind), kind, AB);
                PutB(cache, K2);
                var removedLog = new List<CultCacheDocumentChangeKind>();
                var addedLog = new List<CultCacheDocumentChangeKind>();
                using var oldWatch = cache.Watch<EvB>().Subscribe(change => removedLog.Add(change.Kind));
                using var newWatch = cache.Watch<EvA>().Subscribe(change => addedLog.Add(change.Kind));

                Retype(cache, oneBatch, K2,
                    batch => batch.Upsert(typeof(EvA), new EvA { Name = "k2", Value = 9 }, K2),
                    () => cache.UpsertAsync(typeof(EvA), new EvA { Name = "k2", Value = 9 }, K2).GetAwaiter().GetResult());

                Assert.That(removedLog, Is.EqualTo(new[] { CultCacheDocumentChangeKind.Removed }), $"Watch<old>, one batch {oneBatch}");
                Assert.That(addedLog, Is.EqualTo(new[] { CultCacheDocumentChangeKind.Added }), $"Watch<new>, one batch {oneBatch}");
                Assert.That(cache.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvA)));
            }
        }

        // finding retype-not-an-eviction, global key, and finding global-eviction-uncovered: evicting a global frees its slot, and the
        // batch that retypes a global's key may land the next global of its type elsewhere, as staged apart.
        [TestCaseSource(nameof(Kinds))]
        public void EvictingAGlobalFreesItsSlotAndAGlobalKeyRetypesInOneBatch(string kind)
        {
            using var cache = Open(NewPath(kind), kind, AB);
            var g1 = new CultRecordKey("g1");
            var g2 = new CultRecordKey("g2");
            Assert.That(cache.Commit(batch => batch.Upsert(typeof(EvG), new EvG { Value = "one" }, g1)), Is.True);

            Assert.Throws<InvalidOperationException>(
                () => cache.Commit(batch => batch.Upsert(typeof(EvG), new EvG { Value = "two" }, g2)), "a second global while the first stands is refused");

            Assert.That(cache.Commit(batch =>
            {
                batch.Remove(g1);
                batch.Upsert(typeof(EvG), new EvG { Value = "two" }, g2);
            }), Is.True, "the eviction freed the slot");
            Assert.That(cache.GetGlobal<EvG>()!.Value, Is.EqualTo("two"));
            Assert.That(cache.GetStored(g1), Is.Null);

            Assert.That(cache.Commit(batch =>
            {
                batch.Remove(g2);
                batch.Upsert(typeof(EvA), new EvA { Name = "g2", Value = 1 }, g2);
                batch.Upsert(typeof(EvG), new EvG { Value = "three" }, g1);
            }), Is.True, "staged apart the retype of the global's key and the next global commit");
            Assert.That(cache.GetStored(g2)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvA)));
            Assert.That(cache.GetGlobal<EvG>()!.Value, Is.EqualTo("three"));
        }

        // finding retype-not-an-eviction (variant key) and variant-ops-drop-the-removal: a variant key retypes to a plain record of
        // another type, a plain key becomes a variant of another type's base, and a key removed, added and then added as a variant
        // lands as the variant, each answering as it does staged apart.
        [TestCaseSource(nameof(VariantKinds))]
        public void VariantKeysRetypeInOneBatchAsStagedApart(string kind)
        {
            foreach (var oneBatch in new[] { true, false })
            {
                using var cache = Open(NewPath(kind), kind, AB);
                var baseA = new CultRecordKey("base-a");
                Assert.That(cache.Commit(batch =>
                {
                    batch.Upsert(typeof(EvA), new EvA { Name = "base-a", Value = 1 }, baseA);
                    batch.UpsertVariant(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3") });
                    batch.Upsert(typeof(EvB), new EvB { Name = "k2", Value = 2 }, K2);
                    batch.Upsert(typeof(EvB), new EvB { Name = "k1", Value = 2 }, K1);
                }), Is.True);

                Retype(cache, oneBatch, K3,
                    batch => batch.Upsert(typeof(EvB), new EvB { Name = "k3", Value = 3 }, K3),
                    () => cache.UpsertAsync(typeof(EvB), new EvB { Name = "k3", Value = 3 }, K3).GetAwaiter().GetResult());
                Assert.That(cache.GetStored(K3)!.Variant, Is.Null, $"one batch {oneBatch}");
                Assert.That(cache.GetStored(K3)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvB)));

                Retype(cache, oneBatch, K2,
                    batch => batch.UpsertVariant(K2, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v2") }),
                    () => cache.UpsertVariantAsync(K2, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v2") }).GetAwaiter().GetResult());
                Assert.That(cache.GetStored(K2)!.Variant?.BaseKey, Is.EqualTo(baseA.Value), $"one batch {oneBatch}");
                Assert.That(cache.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvA)));

                // Removed and then added as a variant (in one batch, with a plain add of the same type between): the variant lands.
                Retype(cache, oneBatch, K1,
                    batch =>
                    {
                        batch.Upsert(typeof(EvA), new EvA { Name = "k1", Value = 2 }, K1);
                        batch.UpsertVariant(K1, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v1") });
                    },
                    () => cache.UpsertVariantAsync(K1, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v1") }).GetAwaiter().GetResult());
                Assert.That(cache.GetStored(K1)!.Variant?.BaseKey, Is.EqualTo(baseA.Value), $"one batch {oneBatch}");
            }
        }

        [TestCaseSource(nameof(VariantKinds))]
        public void AFlattenAfterARemovalFlattensNothingInOneBatchAndStagedApart(string kind)
        {
            using var cache = Open(NewPath(kind), kind, AB);
            var baseA = new CultRecordKey("base-a");
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(EvA), new EvA { Name = "base-a", Value = 1 }, baseA);
                batch.UpsertVariant(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3") });
            });

            var flatten = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch =>
            {
                batch.Remove(K3);
                batch.Flatten(K3);
            }))!;
            Assert.That(flatten.Message, Does.Contain("Flatten(k3)").And.Contain("not a variant"));
            Assert.That(cache.GetStored(K3)!.Variant, Is.Not.Null, "the refused batch changed nothing");
            Assert.That(cache.Remove(K3), Is.True);
            Assert.Throws<InvalidOperationException>(() => cache.FlattenAsync(K3).GetAwaiter().GetResult());
        }

        // finding flatten-guard-reads-removals: a Flatten acts on what the key will hold, as staged apart. A variant staged at the key
        // (after a removal or not) is flattened as it lands; a plain record staged there leaves nothing to flatten.
        [TestCaseSource(nameof(VariantKinds))]
        public void AFlattenOfAVariantStagedInTheSameBatchFlattensItAsStagedApart(string kind)
        {
            foreach (var oneBatch in new[] { true, false })
            {
                using var cache = Open(NewPath(kind), kind, AB);
                var baseA = new CultRecordKey("base-a");
                Assert.That(cache.Commit(batch =>
                {
                    batch.Upsert(typeof(EvA), new EvA { Name = "base-a", Value = 1 }, baseA);
                    batch.UpsertVariant(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3") });
                    batch.Upsert(typeof(EvB), new EvB { Name = "k1", Value = 2 }, K1);
                }), Is.True);

                // Remove, UpsertVariant, Flatten: the plain record of the variant's resolution, retyped.
                if (oneBatch)
                    Assert.That(cache.Commit(batch =>
                    {
                        batch.Remove(K1);
                        batch.UpsertVariant(K1, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v1") });
                        batch.Flatten(K1);
                    }), Is.True);
                else
                {
                    Assert.That(cache.Remove(K1), Is.True);
                    cache.UpsertVariantAsync(K1, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v1") }).GetAwaiter().GetResult();
                    cache.FlattenAsync(K1).GetAwaiter().GetResult();
                }

                var flattened = cache.GetStored(K1)!;
                Assert.That(flattened.Variant, Is.Null, $"one batch {oneBatch}");
                Assert.That(flattened.Descriptor.DocumentType, Is.EqualTo(typeof(EvA)));
                Assert.That(((EvA)flattened.Document).Name, Is.EqualTo("v1"));

                // A held variant rebased and flattened in one batch flattens the rebase, not the held record.
                if (oneBatch)
                    Assert.That(cache.Commit(batch =>
                    {
                        batch.UpsertVariant(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3b") });
                        batch.Flatten(K3);
                    }), Is.True);
                else
                {
                    cache.UpsertVariantAsync(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3b") }).GetAwaiter().GetResult();
                    cache.FlattenAsync(K3).GetAwaiter().GetResult();
                }

                Assert.That(cache.GetStored(K3)!.Variant, Is.Null, $"one batch {oneBatch}");
                Assert.That(((EvA)cache.GetStored(K3)!.Document).Name, Is.EqualTo("v3b"));
            }
        }

        [TestCaseSource(nameof(VariantKinds))]
        public void AFlattenOfAPlainRecordStagedInTheSameBatchIsRefusedAsStagedApart(string kind)
        {
            using var cache = Open(NewPath(kind), kind, AB);
            PutB(cache, K2);

            var refused = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch =>
            {
                batch.Remove(K2);
                batch.Upsert(typeof(EvA), new EvA { Name = "k2" }, K2);
                batch.Flatten(K2);
            }))!;
            Assert.That(refused.Message, Does.Contain("Flatten(k2)").And.Contain("not a variant"));
            Assert.That(cache.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvB)), "the refused batch changed nothing");

            Assert.That(cache.Remove(K2), Is.True);
            cache.UpsertAsync(typeof(EvA), new EvA { Name = "k2" }, K2).GetAwaiter().GetResult();
            Assert.Throws<InvalidOperationException>(() => cache.FlattenAsync(K2).GetAwaiter().GetResult());
        }

        // finding flatten-after-staged-plain-unpinned: where the key held a variant, Remove, a plain Upsert, then Flatten is refused
        // as staged apart, and the plain write is never dropped for a flatten of the variant the cache held.
        [TestCaseSource(nameof(VariantKinds))]
        public void AFlattenAfterARemovalAndAPlainWriteOnAHeldVariantIsRefusedAndKeepsTheHeldVariant(string kind)
        {
            using var cache = Open(NewPath(kind), kind, AB);
            var baseA = new CultRecordKey("base-a");
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(EvA), new EvA { Name = "base-a", Value = 1 }, baseA);
                batch.UpsertVariant(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3") });
            });

            var refused = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch =>
            {
                batch.Remove(K3);
                batch.Upsert(typeof(EvA), new EvA { Name = "plain" }, K3);
                batch.Flatten(K3);
            }))!;
            Assert.That(refused.Message, Does.Contain("Flatten(k3)").And.Contain("not a variant"));
            Assert.That(cache.GetStored(K3)!.Variant, Is.Not.Null, "the refused batch changed nothing");

            Assert.That(cache.Remove(K3), Is.True);
            cache.UpsertAsync(typeof(EvA), new EvA { Name = "plain" }, K3).GetAwaiter().GetResult();
            var apart = Assert.Throws<InvalidOperationException>(() => cache.FlattenAsync(K3).GetAwaiter().GetResult())!;
            Assert.That(apart.Message, Is.EqualTo(refused.Message));
            Assert.That(((EvA)cache.GetStored(K3)!.Document).Name, Is.EqualTo("plain"));
        }

        // finding op-after-staged-flatten-diverges: an operation after a Flatten at the same key acts on the plain record that flatten
        // leaves, so one intent has one answer in a batch and staged apart, in both stores kinds and after a reload from disk.
        [TestCaseSource(nameof(VariantKinds))]
        public void AnOperationAfterAStagedFlattenAnswersAsStagedApart(string kind)
        {
            var scenarios = new[] { "F,F", "F,P", "F,V", "V,F,V", "F,P,F", "V,F,P" };
            foreach (var scenario in scenarios)
            {
                var steps = scenario.Split(',');
                var results = new List<string>();
                foreach (var oneBatch in new[] { true, false })
                {
                    var path = NewPath(kind);
                    string outcome;
                    using (var cache = Open(path, kind, AB))
                    {
                        var baseA = new CultRecordKey("base-a");
                        cache.Commit(batch =>
                        {
                            batch.Upsert(typeof(EvA), new EvA { Name = "base-a", Value = 1 }, baseA);
                            batch.UpsertVariant(K3, baseA, new[] { cache.Override<EvA>(nameof(EvA.Name), "v3") });
                        });
                        var variant = new[] { cache.Override<EvA>(nameof(EvA.Name), "vv") };

                        try
                        {
                            if (oneBatch)
                                cache.Commit(batch =>
                                {
                                    foreach (var step in steps)
                                    {
                                        if (step == "F") batch.Flatten(K3);
                                        else if (step == "P") batch.Upsert(typeof(EvA), new EvA { Name = "plain" }, K3);
                                        else batch.UpsertVariant(K3, baseA, variant);
                                    }
                                });
                            else
                                foreach (var step in steps)
                                {
                                    if (step == "F") cache.FlattenAsync(K3).GetAwaiter().GetResult();
                                    else if (step == "P") cache.UpsertAsync(typeof(EvA), new EvA { Name = "plain" }, K3).GetAwaiter().GetResult();
                                    else cache.UpsertVariantAsync(K3, baseA, variant).GetAwaiter().GetResult();
                                }
                            outcome = "committed";
                        }
                        catch (InvalidOperationException refused)
                        {
                            outcome = "refused: " + refused.Message;
                        }

                        outcome += " | " + Describe(cache);
                        cache.FlushAsync().GetAwaiter().GetResult();
                    }

                    if (kind != "storeless" && outcome.StartsWith("committed", StringComparison.Ordinal))
                    {
                        using var reloaded = Open(path, kind, AB);
                        outcome += " | reloaded " + Describe(reloaded);
                    }

                    results.Add(outcome);
                }

                // A refused batch changes nothing, while staged apart the steps before the refused one have landed: the answer compared
                // is the verdict and its text, and the end state only where both committed.
                var batchAnswer = results[0].Split(" | ")[0];
                var apartAnswer = results[1].Split(" | ")[0];
                Assert.That(batchAnswer, Is.EqualTo(apartAnswer), $"{kind} {scenario}");
                if (batchAnswer == "committed")
                    Assert.That(results[0], Is.EqualTo(results[1]), $"{kind} {scenario}");
            }

            static string Describe(CultCache cache)
            {
                var held = cache.GetStored(K3)!;
                return $"{held.Descriptor.DocumentType.Name} variant={held.Variant?.BaseKey} name={((EvA)held.Document).Name}";
            }
        }

        // finding cross-store-retype-decided-by-key-home: a one-batch retype between two home stores is refused by the rule that a commit
        // lands in one home store, with that rule's text (an evicted key is absent to the one-key-one-store check, as when staged
        // apart); staged apart, as two commits, it leaves the key in exactly one store.
        [Test]
        public void ARetypeAcrossTwoHomeStoresInOneBatchIsRefusedByTheBatchSpanningRuleAndStagedApartLeavesOneStore()
        {
            string[] KeysIn(string path) =>
                new SingleFileMessagePackBackingStore(path).ReadStoreSnapshot()?.Records.Select(record => record.Key).ToArray() ?? Array.Empty<string>();

            CultCache OpenTwo(string a, string b)
            {
                var cache = new CultCache(AB, CultCacheMessagePack.CreateCodec(AB));
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(a), typeof(EvA));
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(b), typeof(EvB));
                cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
                return cache;
            }

            var pathA = NewPath("single-file");
            var pathB = NewPath("single-file");
            Directory.CreateDirectory(Path.GetDirectoryName(pathA)!);
            Directory.CreateDirectory(Path.GetDirectoryName(pathB)!);
            using (var cache = OpenTwo(pathA, pathB))
            {
                PutB(cache, K1);
                Assert.That(KeysIn(pathB), Is.EqualTo(new[] { "k1" }));

                var refused = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch =>
                {
                    batch.Remove(K1);
                    batch.Upsert(typeof(EvA), new EvA { Name = "k1" }, K1);
                }))!;
                Assert.That(refused.Message, Does.Contain("The batch spans").And.Contain("a commit lands in one home store")
                    .And.Contain("stage its removal and its add apart").And.Not.Contain("second store"));
                Assert.That(KeysIn(pathA), Is.Empty, "the refused batch wrote nothing");
                Assert.That(KeysIn(pathB), Is.EqualTo(new[] { "k1" }));
                Assert.That(cache.GetStored(K1)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvB)));

                Assert.That(cache.Remove(K1), Is.True);
                cache.FlushAsync().GetAwaiter().GetResult();
                cache.UpsertAsync(typeof(EvA), new EvA { Name = "k1" }, K1).GetAwaiter().GetResult();
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            Assert.That(KeysIn(pathA), Is.EqualTo(new[] { "k1" }), "staged apart the key is in the add's store");
            Assert.That(KeysIn(pathB), Is.Empty, "and no longer in the removal's");
        }

        // finding removal-bookkeeping-unpinned, mutant X2(a retype elsewhere in the batch skips every key's staging claim): a batch
        // holding two retypes commits, and a cross-type add at a key it did not remove is still refused beside a retype.
        [TestCaseSource(nameof(Kinds))]
        public void EachKeyOfABatchIsJudgedByItsOwnRemoval(string kind)
        {
            using var cache = Open(NewPath(kind), kind, AB);
            PutB(cache, K1);
            PutB(cache, K2);
            PutB(cache, K3);

            Assert.That(cache.Commit(batch =>
            {
                batch.Remove(K1);
                batch.Upsert(typeof(EvA), new EvA { Name = "k1" }, K1);
                batch.Remove(K2);
                batch.Upsert(typeof(EvA), new EvA { Name = "k2" }, K2);
            }), Is.True, "two retypes");

            var bad = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch =>
            {
                batch.Remove(K1);
                batch.Upsert(typeof(EvB), new EvB { Name = "k1" }, K1);
                batch.Upsert(typeof(EvA), new EvA { Name = "k3" }, K3);
            }))!;
            Assert.That(bad.RecordKey, Is.EqualTo(K3.Value), "one key's removal licenses no other key's retype");
            Assert.That(cache.GetStored(K3)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvB)));
            Assert.That(cache.GetStored(K1)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvA)), "the refused batch changed nothing");

            Assert.That(cache.Commit(batch =>
            {
                batch.Upsert(typeof(EvB), new EvB { Name = "k3", Value = 5 }, K3);
                batch.Remove(K3);
            }), Is.True, "an add and then a removal removes");
            Assert.That(cache.GetStored(K3), Is.Null);
        }

        // finding removal-bookkeeping-unpinned, mutant X13 (any second add counts as a replacement): two adds of one batch with no
        // removal are an upsert, judged against what the key holds.
        [TestCaseSource(nameof(Kinds))]
        public void TwoAddsWithNoRemovalAreAnUpsert(string kind)
        {
            using var cache = Open(NewPath(kind), kind, AB);
            PutB(cache, K2);

            var refused = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch =>
            {
                batch.Upsert(typeof(EvA), new EvA { Name = "k2", Value = 1 }, K2);
                batch.Upsert(typeof(EvA), new EvA { Name = "k2", Value = 2 }, K2);
            }))!;
            if (kind == "storeless")
                Assert.That(refused.Message, Does.Contain("would retype the key").And.Contain("Remove the record first, in the same batch or an earlier commit, then add the other type's."));
            Assert.That(cache.GetStored(K2)!.Descriptor.DocumentType, Is.EqualTo(typeof(EvB)));
        }

        // finding removal-bookkeeping-unpinned, mutant X18 (no removal noted for a record this cache wrote itself): a cache that wrote
        // the key itself, then removes it and stages another type there, lands that type at the flush.
        [TestCaseSource(nameof(Kinds))]
        public void ARecordThisCacheWroteItselfRetypesStagedApart(string kind)
        {
            var path = NewPath(kind);
            using (var cache = Open(path, kind, AB))
            {
                PutB(cache, K2);
                cache.FlushAllBackingStores();
                Assert.That(cache.Remove(K2), Is.True);
                cache.UpsertAsync(typeof(EvA), new EvA { Name = "k2", Value = 9 }, K2).GetAwaiter().GetResult();
                cache.FlushAllBackingStores();
            }

            if (kind != "storeless")
                Assert.That(TypeAt(path, kind, AB, K2), Is.EqualTo(typeof(EvA)));
        }

        // finding removal-bookkeeping-unpinned, mutant M (a refusal keeps the staged removal): a removal the store refused is gone, so
        // a later add at a key another writer has since rewritten is judged as an upsert, not as the stale removal.
        [TestCaseSource(nameof(Kinds))]
        public void ARefusedRemovalIsNotReusedByALaterAdd(string kind)
        {
            if (kind == "storeless")
                Assert.Ignore("a refusal is a store's.");
            var path = NewPath(kind);
            using (var seed = Open(path, kind, ABC))
                PutB(seed, K1);

            using var cache = Open(path, kind, AB);
            using var other = Open(path, kind, ABC);
            Assert.That(cache.Remove(K1), Is.True);

            // Another writer retypes the key to a type this cache does not know: the cache's staged removal meets a record it may not remove.
            Assert.That(other.Commit(batch =>
            {
                batch.Remove(K1);
                batch.Upsert(typeof(EvC), new EvC { Name = "k1" }, K1);
            }), Is.True);
            Assert.Throws<CultSchemaConflictException>(() => cache.FlushAllBackingStores());

            // The writer removes it again and puts a B there; this cache pulls and then stages an A with no removal in front of it.
            Assert.That(other.Commit(batch => batch.Remove(K1)), Is.True);
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            Assert.That(other.Commit(batch => batch.Upsert(typeof(EvB), new EvB { Name = "k1", Value = 7 }, K1)), Is.True);
            cache.UpsertAsync(typeof(EvA), new EvA { Name = "k1", Value = 8 }, K1).GetAwaiter().GetResult();

            Assert.Throws<CultSchemaConflictException>(() => cache.FlushAllBackingStores());
            Assert.That(TypeAt(path, kind, AB, K1), Is.EqualTo(typeof(EvB)));
        }

        // finding removal-bookkeeping-unpinned, mutant X9 (a refusal clears every staged removal): a refusal of one key leaves another
        // key's staged retype, and the next flush lands it.
        [TestCaseSource(nameof(Kinds))]
        public void ARefusedFlushKeepsAnotherKeysStagedRetype(string kind)
        {
            if (kind == "storeless")
                Assert.Ignore("a refusal is a store's.");
            var path = NewPath(kind);
            using (var seed = Open(path, kind, ABC))
                PutB(seed, K2);

            using var cache = Open(path, kind, AB);
            using var other = Open(path, kind, ABC);
            Assert.That(cache.Remove(K2), Is.True);
            cache.UpsertAsync(typeof(EvA), new EvA { Name = "k2", Value = 9 }, K2).GetAwaiter().GetResult();
            cache.UpsertAsync(typeof(EvA), new EvA { Name = "k1", Value = 1 }, K1).GetAwaiter().GetResult();

            // Another writer puts a type this cache does not know at k1 before the flush: k1's staged add is refused under the lock.
            Assert.That(other.Commit(batch => batch.Upsert(typeof(EvC), new EvC { Name = "k1" }, K1)), Is.True);
            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.FlushAllBackingStores())!;
            Assert.That(refusal.RecordKey, Is.EqualTo(K1.Value));
            cache.FlushAllBackingStores();

            Assert.That(TypeAt(path, kind, ABC, K2), Is.EqualTo(typeof(EvA)), "the retype of k2 survived the refusal of k1");
        }

        // finding rebase-check-was-reachable, ruling variant-rebase-follows-plain-write: a variant rebased onto a base of another type
        // follows the plain-write rule. Where RbY lists RbX's id (a plain write of RbY would own an RbX record) the rebase commits and
        // the variant becomes an RbY; where it would be refused (RbZ) the rebase is refused and the variant is untouched.
        [TestCaseSource(nameof(VariantKinds))]
        public void ARebaseChangesTypeExactlyWhereAPlainWriteMay(string kind)
        {
            Assert.That(Rb.GetRequired(typeof(RbX)).SchemaId, Is.EqualTo(RbXId));
            using var cache = Open(NewPath(kind), kind, Rb);
            var baseX = new CultRecordKey("base-x");
            var baseY = new CultRecordKey("base-y");
            var baseZ = new CultRecordKey("base-z");
            var variant = new CultRecordKey("v");
            Assert.That(cache.Commit(batch =>
            {
                batch.Upsert(typeof(RbX), new RbX { Name = "base-x" }, baseX);
                batch.Upsert(typeof(RbY), new RbY { Name = "base-y" }, baseY);
                batch.Upsert(typeof(RbZ), new RbZ { Name = "base-z" }, baseZ);
                batch.UpsertVariant(variant, baseX, new[] { cache.Override<RbX>(nameof(RbX.Name), "v") });
            }), Is.True);

            // The plain write of the same types gives the same two answers.
            Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Upsert(typeof(RbZ), new RbZ { Name = "base-x" }, baseX)));
            Assert.Throws<CultSchemaConflictException>(
                () => cache.Commit(batch => batch.UpsertVariant(variant, baseZ, new[] { cache.Override<RbZ>(nameof(RbZ.Name), "v") })));
            Assert.That(cache.GetStored(variant)!.Descriptor.DocumentType, Is.EqualTo(typeof(RbX)));
            Assert.That(cache.GetStored(variant)!.Variant!.BaseKey, Is.EqualTo(baseX.Value));

            Assert.That(cache.Commit(batch => batch.UpsertVariant(variant, baseY, new[] { cache.Override<RbY>(nameof(RbY.Name), "v") })), Is.True);
            Assert.That(cache.GetStored(variant)!.Descriptor.DocumentType, Is.EqualTo(typeof(RbY)));
            Assert.That(cache.GetStored(variant)!.Variant!.BaseKey, Is.EqualTo(baseY.Value));
        }
    }
}
