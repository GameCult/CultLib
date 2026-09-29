#nullable enable
using System;
using System.Collections.Generic;
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
    // The contract is src/GameCult.Caching/Contracts/cultcache-persistence-format.md (variant records) and
    // docs/document-variants-cut.md C1. Every input goes through the cache's own write API and the real single-file store.
    public class VariantTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[]
        {
            typeof(VariantGear), typeof(VariantOther), typeof(VariantGlobal)
        });

        private static readonly CultRecordKey BaseKey = new("laser");
        private static readonly CultRecordKey BigKey = new("laser-big");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-variants-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path, bool readOnly = false, CultDocumentRegistry? registry = null) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry ?? Registry, ReadOnly = readOnly });

        private static VariantGear Laser() => new() { Name = "laser", Power = 10, Code = "l1", Tags = { "beam" } };

        private static void SeedBase(CultCache cache) =>
            cache.Commit(batch => batch.Upsert(typeof(VariantGear), Laser(), BaseKey));

        // laser-big: a variant of laser that overrides its name, power and code (an indexed value must be its own) and inherits the rest.
        private static void SeedBig(CultCache cache, CultRecordKey? key = null, CultRecordKey? baseKey = null, string name = "laser big", int power = 25) =>
            cache.Commit(batch => batch.UpsertVariant(key ?? BigKey, baseKey ?? BaseKey, new[]
            {
                cache.Override<VariantGear>(nameof(VariantGear.Name), name),
                cache.Override<VariantGear>(nameof(VariantGear.Power), power),
                cache.Override<VariantGear>(nameof(VariantGear.Code), name)
            }));

        private static void EditBase(CultCache cache, Action<VariantGear> edit) =>
            cache.Commit(batch =>
            {
                var edited = Laser();
                edit(edited);
                batch.Upsert(typeof(VariantGear), edited, BaseKey);
            });

        private static InvalidOperationException Refused(TestDelegate write) =>
            Assert.Throws<InvalidOperationException>(write)!;

        // ---- resolution ----

        [Test]
        public void ALoadedVariantResolvesAgainstItsBase()
        {
            var path = PathOf("gear.cc");
            using (var writer = Open(path))
            {
                SeedBase(writer);
                SeedBig(writer);
            }

            using var cache = Open(path);
            var big = cache.Get<VariantGear>(BigKey)!;
            Assert.That(big.Name, Is.EqualTo("laser big"));
            Assert.That(big.Power, Is.EqualTo(25));
            Assert.That(big.Code, Is.EqualTo("laser big"), "the override the unique index requires");
            Assert.That(big.Tags, Is.EqualTo(new[] { "beam" }), "an inherited list");
            Assert.That(cache.Get<VariantGear>(BaseKey)!.Power, Is.EqualTo(10));
            Assert.That(cache.GetStored(BigKey)!.Variant!.BaseKey, Is.EqualTo(BaseKey.Value));
            Assert.That(cache.GetStored(BigKey)!.Variant!.Overrides, Has.Count.EqualTo(3));
        }

        [Test]
        public void AVariantOfAVariantResolvesBaseFirstAndFollowsTheRoot()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            SeedBig(cache);
            var hugeKey = new CultRecordKey("laser-huge");
            SeedBig(cache, hugeKey, BigKey, name: "laser huge", power: 40);

            Assert.That(cache.Get<VariantGear>(hugeKey)!.Tags, Is.EqualTo(new[] { "beam" }));
            EditBase(cache, gear => gear.Tags.Add("pierce"));

            Assert.That(cache.Get<VariantGear>(hugeKey)!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
            Assert.That(cache.Get<VariantGear>(hugeKey)!.Power, Is.EqualTo(40));
        }

        [Test]
        public void ABaseEditReachesItsVariantsThroughGetNameAndIndexAndOneWatchPublication()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            SeedBig(cache);
            cache.Commit(batch => batch.UpsertVariant(new CultRecordKey("laser-coded"), BaseKey, new[]
            {
                cache.Override<VariantGear>(nameof(VariantGear.Name), "laser coded"),
                cache.Override<VariantGear>(nameof(VariantGear.Code), "l9")
            }));

            var seen = new List<(string Key, long Sequence, string BigTagsAtThatMoment, string BigNameLookupTags)>();
            using var subscription = cache.Watch<VariantGear>().Subscribe(change => seen.Add((
                change.Key.Value,
                change.Sequence,
                string.Join(",", cache.Get<VariantGear>(BigKey)!.Tags),
                string.Join(",", cache.GetByName<VariantGear>("laser big")!.Tags))));

            EditBase(cache, gear => { gear.Tags.Add("pierce"); gear.Code = "l2"; });

            Assert.That(cache.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
            Assert.That(cache.GetByName<VariantGear>("laser big")!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
            Assert.That(cache.GetByName<VariantGear>("laser big")!.Power, Is.EqualTo(25), "the override survives the base edit");
            Assert.That(cache.GetByIndex<VariantGear>("code", "l9")!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
            // the base's old code entry must be gone, not shadowing, and its new one must find the base itself.
            Assert.That(cache.GetByIndex<VariantGear>("code", "l1"), Is.Null);
            Assert.That(cache.GetByIndex<VariantGear>("code", "l2")!.Name, Is.EqualTo("laser"));
            Assert.That(cache.GetByIndex<VariantGear>("code", "laser big")!.Name, Is.EqualTo("laser big"));

            Assert.That(seen.Select(entry => entry.Key), Is.EquivalentTo(new[] { BaseKey.Value, BigKey.Value, "laser-coded" }));
            Assert.That(seen.Select(entry => entry.Sequence), Is.EqualTo(Enumerable.Range((int)seen[0].Sequence, seen.Count).Select(n => (long)n)),
                "one commit, one run of sequences with no gap");
            Assert.That(seen.Select(entry => entry.BigTagsAtThatMoment), Is.All.EqualTo("beam,pierce"),
                "a subscriber observing the base change already reads the new variant");
            Assert.That(seen.Select(entry => entry.BigNameLookupTags), Is.All.EqualTo("beam,pierce"));
        }

        [Test]
        public void AReResolvedDependentKeepsItsStoredAtAndIsAnUpdatedChange()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            SeedBig(cache);
            var before = cache.GetStored(BigKey)!;
            var changes = new List<CultCacheDocumentChange<VariantGear>>();
            using var subscription = cache.WatchRecord<VariantGear>(BigKey).Subscribe(changes.Add);

            EditBase(cache, gear => gear.Tags.Add("pierce"));

            Assert.That(cache.GetStored(BigKey)!.StoredAt, Is.EqualTo(before.StoredAt), "a base change stamps no dependent");
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(CultCacheDocumentChangeKind.Updated));
            Assert.That(changes[0].PreviousDocument!.Tags, Is.EqualTo(new[] { "beam" }));
            Assert.That(changes[0].Document!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
        }

        [Test]
        public void ExpectOnAVariantSurvivesABaseChangeButNotAChangeToItsDelta()
        {
            var path = PathOf("gear.cc");
            using (var seed = Open(path))
            {
                SeedBase(seed);
                SeedBig(seed);
            }

            using var a = Open(path);
            using var b = Open(path);
            var seen = a.Get<VariantGear>(BigKey)!;

            EditBase(b, gear => gear.Tags.Add("pierce"));
            Assert.That(a.Commit(batch =>
            {
                batch.Expect(BigKey, seen);
                batch.Upsert(typeof(VariantOther), new VariantOther { Name = "marker" }, new CultRecordKey("marker"));
            }), Is.True, "the base changed; the variant's stored delta did not");

            a.PullAllBackingStoresAsync();
            seen = a.Get<VariantGear>(BigKey)!;
            SeedBig(b, power: 26);
            Assert.That(a.Commit(batch =>
            {
                batch.Expect(BigKey, seen);
                batch.Upsert(typeof(VariantOther), new VariantOther { Name = "second" }, new CultRecordKey("second"));
            }), Is.False, "another writer changed the variant's own delta");
        }

        // ---- refusals, each naming the keys ----

        [Test]
        public void ACycleOfBasesIsRefusedNamingEveryKey()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            var a = new CultRecordKey("cycle-a");
            var b = new CultRecordKey("cycle-b");

            var staged = Refused(() => cache.Commit(batch =>
            {
                batch.UpsertVariant(a, b);
                batch.UpsertVariant(b, a);
            }));
            Assert.That(staged.Message, Does.Contain("cycle").And.Contain(a.Value).And.Contain(b.Value));

            SeedBig(cache);
            var huge = new CultRecordKey("laser-huge");
            SeedBig(cache, huge, BigKey, name: "laser huge");
            var rebased = Refused(() => cache.Commit(batch => batch.UpsertVariant(BigKey, huge)));
            Assert.That(rebased.Message, Does.Contain("cycle").And.Contain(BigKey.Value).And.Contain(huge.Value));
            Assert.That(cache.GetStored(BigKey)!.Variant!.BaseKey, Is.EqualTo(BaseKey.Value), "nothing landed");
        }

        [Test]
        public void AMissingBaseIsRefusedNamingBoth()
        {
            using var cache = Open(PathOf("gear.cc"));
            var error = Refused(() => cache.UpsertVariantAsync(BigKey, new CultRecordKey("no-such-base")).GetAwaiter().GetResult());
            Assert.That(error.Message, Does.Contain(BigKey.Value).And.Contain("no-such-base"));
            Assert.That(cache.Get(BigKey), Is.Null);
        }

        [Test]
        public void ABaseInAnotherTypeIsRefusedOnRebaseAndOnLoad()
        {
            var path = PathOf("gear.cc");
            using (var cache = Open(path))
            {
                SeedBase(cache);
                SeedBig(cache);
                cache.Commit(batch => batch.Upsert(typeof(VariantOther), new VariantOther { Name = "other" }, new CultRecordKey("other")));

                var rebase = Refused(() => cache.Commit(batch => batch.UpsertVariant(BigKey, new CultRecordKey("other"))));
                Assert.That(rebase.Message, Does.Contain(BigKey.Value).And.Contain("VariantOther"));
            }

            // A store whose variant record names another type than its base's: the loader refuses it naming both keys.
            var bad = PathOf("bad.cc");
            var gear = Registry.GetRequired<VariantGear>();
            var other = Registry.GetRequired<VariantOther>();
            File.WriteAllBytes(bad, CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
            {
                FormatVersion = CultPersistedStoreSnapshot.FormatV2,
                SchemaCatalog = new[] { gear.ToCatalogEntry(), other.ToCatalogEntry() },
                Records = new[]
                {
                    new CultPersistedRecord
                    {
                        Key = "gear-base", SchemaId = gear.SchemaId, StoredAt = "2026-01-01T00:00:00.0000000Z",
                        Payload = CultDocumentMessagePackSerialization.SerializeUntyped(Laser(), typeof(VariantGear), Registry)
                    },
                    new CultPersistedRecord
                    {
                        Key = "other-variant", SchemaId = other.SchemaId, StoredAt = "2026-01-01T00:00:00.0000000Z",
                        Variant = new CultVariantDelta("gear-base", Array.Empty<CultVariantOverride>())
                    }
                }
            }));
            var load = Refused(() => Open(bad).Dispose());
            Assert.That(load.Message, Does.Contain("other-variant").And.Contain("gear-base"));
        }

        [Test]
        public void AVariantOfAGlobalIsRefused()
        {
            using var cache = Open(PathOf("gear.cc"));
            var key = cache.UpsertAsync(new VariantGlobal { Value = "one" }).GetAwaiter().GetResult().Key;
            var error = Refused(() => cache.UpsertVariantAsync(BigKey, key).GetAwaiter().GetResult());
            Assert.That(error.Message, Does.Contain(BigKey.Value).And.Contain("global"));
        }

        [Test]
        public void RemovingABaseThatKeepsVariantsIsRefusedUnlessTheSameBatchRemovesOrRebasesThem()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            var spare = new CultRecordKey("spare");
            cache.Commit(batch => batch.Upsert(typeof(VariantGear), new VariantGear { Name = "spare", Power = 3 }, spare));
            SeedBig(cache);

            var refused = Refused(() => cache.Commit(batch => batch.Remove(BaseKey)));
            Assert.That(refused.Message, Does.Contain(BaseKey.Value).And.Contain(BigKey.Value));
            Assert.That(cache.Get(BaseKey), Is.Not.Null);
            Assert.That(cache.Get(BigKey), Is.Not.Null);

            Assert.That(cache.Commit(batch =>
            {
                batch.UpsertVariant(BigKey, spare, cache.GetStored(BigKey)!.Variant!.Overrides);
                batch.Remove(BaseKey);
            }), Is.True, "rebased in the same batch");
            Assert.That(cache.Get<VariantGear>(BigKey)!.Tags, Is.Empty, "now resolved from spare");

            Assert.That(cache.Commit(batch =>
            {
                batch.Remove(spare);
                batch.Remove(BigKey);
            }), Is.True, "removed in the same batch");

            SeedBase(cache);
            var removedInTheBatch = Refused(() => cache.Commit(batch =>
            {
                batch.Remove(BaseKey);
                batch.UpsertVariant(BigKey, BaseKey);
            }));
            Assert.That(removedInTheBatch.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value));
        }

        [Test]
        public void AVariantThatKeepsItsBasesNameIsRefused()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            var error = Refused(() => cache.UpsertVariantAsync(BigKey, BaseKey).GetAwaiter().GetResult());
            Assert.That(error.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value).And.Contain("laser"));

            var sameName = Refused(() => SeedBig(cache, name: "laser"));
            Assert.That(sameName.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value));

            SeedBig(cache);
            var grand = new CultRecordKey("laser-grand");
            var grandOfBase = Refused(() => SeedBig(cache, grand, BigKey, name: "laser"));
            Assert.That(grandOfBase.Message, Does.Contain(grand.Value).And.Contain(BaseKey.Value), "the whole chain, not only the parent");
        }

        [Test]
        public void ABaseEditThatWouldGiveAVariantItsBasesNameIsRefused()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            SeedBig(cache);

            var error = Refused(() => EditBase(cache, gear => gear.Name = "laser big"));
            Assert.That(error.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value));
            Assert.That(cache.Get<VariantGear>(BaseKey)!.Name, Is.EqualTo("laser"));
        }

        [Test]
        public void APlainWriteAtAVariantsKeyIsRefusedByEveryPathAndFlattenIsTheExplicitWay()
        {
            var path = PathOf("gear.cc");
            using var cache = Open(path);
            SeedBase(cache);
            SeedBig(cache);
            var plain = new VariantGear { Name = "laser big", Power = 99 };

            Assert.That(Refused(() => cache.UpsertAsync(typeof(VariantGear), plain, BigKey).GetAwaiter().GetResult()).Message,
                Does.Contain(BigKey.Value));
            Assert.That(Refused(() => cache.Commit(batch => batch.Upsert(typeof(VariantGear), plain, BigKey))).Message,
                Does.Contain(BigKey.Value));
            Assert.That(cache.GetStored(BigKey)!.Variant, Is.Not.Null);
            Assert.That(cache.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25));

            cache.FlattenAsync(BigKey).GetAwaiter().GetResult();
            var flat = cache.GetStored(BigKey)!;
            Assert.That(flat.Variant, Is.Null);
            Assert.That(cache.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25), "flatten keeps the resolved values");
            EditBase(cache, gear => gear.Tags.Add("pierce"));
            Assert.That(cache.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam" }), "a flattened record no longer follows its base");
            cache.UpsertAsync(typeof(VariantGear), plain, BigKey).GetAwaiter().GetResult();
            Assert.That(cache.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(99), "and is an ordinary record again");
            cache.FlushAsync().GetAwaiter().GetResult();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "no variant left in the store");
        }

        [Test]
        public void FlattenRefusesARecordThatIsNotAVariantAndAPlainRecordCannotBecomeOne()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            Assert.That(Refused(() => cache.FlattenAsync(BaseKey).GetAwaiter().GetResult()).Message, Does.Contain(BaseKey.Value));
            var spare = new CultRecordKey("spare");
            cache.Commit(batch => batch.Upsert(typeof(VariantGear), new VariantGear { Name = "spare" }, spare));
            var named = new[] { cache.Override<VariantGear>(nameof(VariantGear.Name), "spare variant") };
            Assert.That(Refused(() => cache.UpsertVariantAsync(spare, BaseKey, named).GetAwaiter().GetResult()).Message, Does.Contain(spare.Value));
            Assert.That(cache.GetStored(spare)!.Variant, Is.Null, "the plain record is still plain");
        }

        [Test]
        public void ACacheWithoutACodecHoldsNoVariants()
        {
            using var cache = new CultCache(Registry);
            cache.Commit(batch => batch.Upsert(typeof(VariantGear), Laser(), BaseKey));
            var error = Refused(() => cache.UpsertVariantAsync(BigKey, BaseKey).GetAwaiter().GetResult());
            Assert.That(error.Message, Does.Contain(BigKey.Value).And.Contain("codec"));
        }

        [Test]
        public void OnlyTopLevelSetOverridesResolveInThisCut()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            var element = new CultVariantOverride(
                CultOverrideOp.Set, new[] { new CultPathStep(3, "el-1"), new CultPathStep(0) }, string.Empty, new byte[] { 0x01 });
            var error = Refused(() => cache.UpsertVariantAsync(BigKey, BaseKey, new[] { element }).GetAwaiter().GetResult());
            Assert.That(error.Message, Does.Contain(BigKey.Value).And.Contain("top-level"));

            var insert = new CultVariantOverride(CultOverrideOp.Insert, new[] { new CultPathStep(3) }, "new", new byte[] { 0xa1, 0x78 });
            Assert.That(Refused(() => cache.UpsertVariantAsync(BigKey, BaseKey, new[] { insert }).GetAwaiter().GetResult()).Message,
                Does.Contain(BigKey.Value).And.Contain("top-level"));
        }

        // ---- soft drift: an override drifts exactly as the same member would on a plain record ----

        [Test]
        public void AnOverrideOfASlotTheTypeNoLongerHasIsIgnoredWithAWarningNamingVariantAndMember()
        {
            var path = PathOf("drift.cc");
            var wide = CultDocumentRegistry.ForTypes(new[] { typeof(DriftWide) });
            using (var writer = Open(path, registry: wide))
            {
                writer.Commit(batch =>
                {
                    batch.Upsert(typeof(DriftWide), new DriftWide { Name = "drift", Power = 1, Wing = "base wing" }, new CultRecordKey("drift-base"));
                    batch.UpsertVariant(new CultRecordKey("drift-variant"), new CultRecordKey("drift-base"), new[]
                    {
                        writer.Override<DriftWide>(nameof(DriftWide.Name), "drift variant"),
                        writer.Override<DriftWide>(nameof(DriftWide.Power), 7),
                        writer.Override<DriftWide>(nameof(DriftWide.Wing), "variant wing")
                    });
                });
            }

            var narrow = CultDocumentRegistry.ForTypes(new[] { typeof(DriftNarrow) });
            using var cache = Open(path, registry: narrow);
            var variant = cache.Get<DriftNarrow>(new CultRecordKey("drift-variant"))!;
            Assert.That(variant.Power, Is.EqualTo(7), "the override the type still has applies");
            Assert.That(variant.Name, Is.EqualTo("drift variant"));

            // The same decision, on the same surface, that the plain base record gets from the store's schema resolution.
            var reports = cache.BackingStores[0].LastSchemaMigrationReports;
            Assert.That(reports, Has.Count.EqualTo(2), "one per record: the base and the variant");
            Assert.That(reports.Select(report => report.Kind), Is.All.EqualTo(CultSchemaMigrationKind.CompatibleDrift));
            Assert.That(reports.Select(report => string.Join(",", report.IgnoredExtraSlots)), Is.All.EqualTo("2"));
            Assert.That(reports.Select(report => report.Warnings.Select(warning => warning.Code)), Has.All.Contain("ignored_extra_slot"));
        }

        private static readonly byte[] NotAnInt = { 0xa3, (byte)'a', (byte)'b', (byte)'c' };

        private static CultVariantOverride TextOverride(int slot, string value)
        {
            var bytes = new byte[value.Length + 1];
            bytes[0] = (byte)(0xa0 | value.Length);
            for (var index = 0; index < value.Length; index++)
                bytes[index + 1] = (byte)value[index];
            return CultVariantOverride.Set(slot, bytes);
        }

        private static CultPersistedRecord OverridingRecord(string key, string baseKey, params CultVariantOverride[] overrides) => new()
        {
            Key = key,
            SchemaId = Registry.GetRequired<VariantGear>().SchemaId,
            StoredAt = "2026-01-01T00:00:00.0000000Z",
            Payload = Array.Empty<byte>(),
            Variant = new CultVariantDelta(baseKey, overrides)
        };

        [Test]
        public void AnOverrideThatNoLongerDecodesRefusesTheLoadAsAPlainRecordHoldingItDoes()
        {
            // VariantGear.Power is [Key(1)] int; a string there is the nested-type-change case with the schema id unchanged.
            var plainRecord = new CultPersistedRecord
            {
                Key = "hand-plain",
                SchemaId = Registry.GetRequired<VariantGear>().SchemaId,
                StoredAt = "2026-01-01T00:00:00.0000000Z",
                Payload = new byte[] { 0x94, 0xa1, (byte)'x', 0xa3, (byte)'a', (byte)'b', (byte)'c', 0xa0, 0x90 }
            };
            Assert.That(Assert.Catch(() => Open(WriteStore(plainRecord)).Dispose()), Is.Not.Null, "the plain-record outcome is a refusal");

            var path = WriteStore(PlainGear("hand-base"), OverridingRecord("hand-variant", "hand-base", CultVariantOverride.Set(1, NotAnInt)));
            var refused = Assert.Catch(() => Open(path).Dispose())!;
            Assert.That(refused.Message, Does.Contain("hand-variant").And.Contain("Power"));
        }

        [Test]
        public void AnOverrideOfASlotTheTypeNeverHadIsIgnoredOnLoadAsAPlainRecordIgnoresAnExtraSlot()
        {
            var path = WriteStore(PlainGear("hand-base"), OverridingRecord("hand-variant", "hand-base",
                CultVariantOverride.Set(99, new byte[] { 0x01 }),
                TextOverride(0, "hand variant"),
                TextOverride(2, "hand-code")));
            using var cache = Open(path);
            Assert.That(cache.Get<VariantGear>(new CultRecordKey("hand-variant"))!.Name, Is.EqualTo("hand variant"));
            Assert.That(cache.Get<VariantGear>(new CultRecordKey("hand-variant"))!.Power, Is.EqualTo(10), "inherits the base");
        }

        // ---- a bad write is refused; drift is for type changes after the fact ----

        [Test]
        public void AnInvalidOverrideIsRefusedAtWriteNamingVariantSlotAndReasonAndNothingLands()
        {
            var path = PathOf("gear.cc");
            using var cache = Open(path);
            SeedBase(cache);

            var unknown = Refused(() => cache.Commit(batch => batch.UpsertVariant(BigKey, BaseKey, new[]
            {
                CultVariantOverride.Set(99, new byte[] { 0x01 })
            })));
            Assert.That(unknown.Message, Does.Contain(BigKey.Value).And.Contain("99").And.Contain("does not have"));

            var wrongType = Refused(() => cache.UpsertVariantAsync(BigKey, BaseKey, new[]
            {
                CultVariantOverride.Set(1, NotAnInt)
            }).GetAwaiter().GetResult());
            Assert.That(wrongType.Message, Does.Contain(BigKey.Value).And.Contain("Power").And.Contain("slot 1"));

            Assert.That(cache.Get(BigKey), Is.Null);
            cache.FlushAsync().GetAwaiter().GetResult();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "no variant was persisted");
        }

        [Test]
        public void TwoOverridesOfOneSlotAreRefusedAtWriteAndAnUnknownMemberNameIsAnArgumentException()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);

            var twice = Refused(() => cache.Commit(batch => batch.UpsertVariant(BigKey, BaseKey, new[]
            {
                cache.Override<VariantGear>(nameof(VariantGear.Power), 1),
                cache.Override<VariantGear>(nameof(VariantGear.Power), 2)
            })));
            Assert.That(twice.Message, Does.Contain(BigKey.Value).And.Contain("slot 1").And.Contain("more than once"));
            Assert.That(cache.Get(BigKey), Is.Null);

            var unknownName = Assert.Throws<ArgumentException>(() => cache.Override<VariantGear>("Nope", 1))!;
            Assert.That(unknownName.Message, Does.Contain("Nope"));
        }

        // ---- a variant may not share an indexed value with another record ----

        [Test]
        public void AVariantThatInheritsAnIndexedValueIsRefusedNamingBothKeysAndTheIndex()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);

            var inherited = Refused(() => cache.Commit(batch => batch.UpsertVariant(BigKey, BaseKey, new[]
            {
                cache.Override<VariantGear>(nameof(VariantGear.Name), "laser big")
            })));
            Assert.That(inherited.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value).And.Contain("code").And.Contain("l1"));

            var explicitly = Refused(() => cache.Commit(batch => batch.UpsertVariant(BigKey, BaseKey, new[]
            {
                cache.Override<VariantGear>(nameof(VariantGear.Name), "laser big"),
                cache.Override<VariantGear>(nameof(VariantGear.Code), "l1")
            })));
            Assert.That(explicitly.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value));
            Assert.That(cache.Get(BigKey), Is.Null, "nothing landed");
            Assert.That(cache.GetByIndex<VariantGear>("code", "l1")!.Name, Is.EqualTo("laser"));

            SeedBig(cache);
            Assert.That(cache.GetByIndex<VariantGear>("code", "l1")!.Name, Is.EqualTo("laser"));
            Assert.That(cache.GetByIndex<VariantGear>("code", "laser big")!.Name, Is.EqualTo("laser big"));
        }

        [Test]
        public void ABaseEditThatWouldGiveItsVariantsIndexedValueToTheBaseIsRefused()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            SeedBig(cache);

            var refused = Refused(() => EditBase(cache, gear => gear.Code = "laser big"));
            Assert.That(refused.Message, Does.Contain(BigKey.Value).And.Contain(BaseKey.Value).And.Contain("code"));
            Assert.That(cache.Get<VariantGear>(BaseKey)!.Code, Is.EqualTo("l1"), "nothing landed");
        }

        [Test]
        public void ALoadedVariantThatInheritsAnIndexedValueRefusesTheLoadNamingBothKeys()
        {
            var path = WriteStore(PlainGear("hand-base"), OverridingRecord("hand-variant", "hand-base", TextOverride(0, "hand variant")));
            var refused = Assert.Throws<InvalidOperationException>(() => Open(path).Dispose())!;
            Assert.That(refused.Message, Does.Contain("hand-variant").And.Contain("hand-base").And.Contain("code"));
        }

        // ---- flatten resolves against the same batch ----

        [Test]
        public void AFlattenAndABaseEditInOneBatchLandTheResolutionOfTheWholeBatch()
        {
            var path = PathOf("gear.cc");
            using (var cache = Open(path))
            {
                SeedBase(cache);
                SeedBig(cache);
                cache.Commit(batch =>
                {
                    var edited = Laser();
                    edited.Tags.Add("pierce");
                    batch.Upsert(typeof(VariantGear), edited, BaseKey);
                    batch.Flatten(BigKey);
                });

                Assert.That(cache.GetStored(BigKey)!.Variant, Is.Null);
                Assert.That(cache.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam", "pierce" }), "the edit made in the same batch");
                Assert.That(cache.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25));
            }

            using var reloaded = Open(path);
            Assert.That(reloaded.GetStored(BigKey)!.Variant, Is.Null);
            Assert.That(reloaded.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
        }

        // ---- the directory store refuses a variant page on load as it does on write ----

        [Test]
        public void TheDirectoryStoreRefusesToLoadAVariantPage()
        {
            var manifest = PathOf("dir-page.cc");
            var records = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(manifest);
            Directory.CreateDirectory(records);
            var page = CultDocumentMessagePackSerialization.SerializePersistedRecord(OverridingRecord("hand-variant", "hand-base", TextOverride(0, "x")));
            var hash = System.Security.Cryptography.SHA256.HashData(page);
            File.WriteAllBytes(Path.Combine(records, Convert.ToHexString(hash).ToLowerInvariant() + ".msgpack"), page);

            using var store = new DirectoryMessagePackBackingStore(manifest, records);
            var read = typeof(DirectoryMessagePackBackingStore).GetMethod(
                "ReadPersistedRecordPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var refused = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => read.Invoke(store, new object?[] { new CultPersistedRecord { Key = "hand-variant", Payload = hash }, null }))!;

            Assert.That(refused.InnerException, Is.TypeOf<NotSupportedException>());
            Assert.That(refused.InnerException!.Message, Does.Contain("hand-variant").And.Contain("directory store"));
        }

        // ---- persisted format ----

        private static string HeaderOf(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

        [Test]
        public void TheHeaderIsV1WithoutVariantsAndV2WithThem()
        {
            var path = PathOf("gear.cc");
            using var cache = Open(path);
            SeedBase(cache);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"));

            SeedBig(cache);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v2"));
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var variant = snapshot.Records.Single(record => record.Key == BigKey.Value);
            Assert.That(variant.Payload, Is.Empty, "the payload is the tripwire old readers refuse on");
            Assert.That(variant.Variant!.BaseKey, Is.EqualTo(BaseKey.Value));
            Assert.That(snapshot.Records.Single(record => record.Key == BaseKey.Value).Variant, Is.Null);

            cache.Commit(batch => batch.Remove(BigKey));
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "the last variant gone, the store is v1 again");
        }

        [Test]
        public async Task AStoreWithoutVariantsRewritesByteIdenticalToTheOneWrittenBeforeVariantsExisted()
        {
            var fixture = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "pre-cut2", "single-file-v1", "store.msgpack");
            var copy = PathOf("store.msgpack");
            File.Copy(fixture, copy);
            var before = File.ReadAllBytes(copy);

            using (var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(PreCut2FixtureItem), typeof(PreCut2FixtureNote) })))
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(copy));
                await cache.PullAllBackingStoresAsync();
                cache.BackingStores[0].PushAll();
            }

            Assert.That(File.ReadAllBytes(copy), Is.EqualTo(before));
        }

        [Test]
        public void AVariantStoreRoundTripsByteIdenticalThroughAFlush()
        {
            var path = PathOf("gear.cc");
            using (var cache = Open(path))
            {
                SeedBase(cache);
                SeedBig(cache);
            }

            var written = File.ReadAllBytes(path);
            using (var cache = Open(path))
            {
                cache.BackingStores[0].PushAll();
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(written));
        }

        [Test]
        public void AReadOnlyStoreResolvesItsVariants()
        {
            var path = PathOf("gear.cc");
            using (var cache = Open(path))
            {
                SeedBase(cache);
                SeedBig(cache);
            }

            using var readOnly = Open(path, readOnly: true);
            Assert.That(readOnly.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25));
            Assert.That(readOnly.Get<VariantGear>(BigKey)!.Code, Is.EqualTo("laser big"));

            // A base edit another writer commits reaches the read-only cache's variant when it pulls: a re-resolution is no write.
            using var writer = Open(path);
            EditBase(writer, gear => gear.Tags.Add("pierce"));
            var before = File.ReadAllBytes(path);
            readOnly.PullAllBackingStoresAsync();
            Assert.That(readOnly.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam", "pierce" }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        }

        [Test]
        public void ABaseEditFromAnotherWriterReachesVariantsWhenTheStoreIsPulled()
        {
            var path = PathOf("gear.cc");
            using var a = Open(path);
            SeedBase(a);
            SeedBig(a);
            using var b = Open(path);
            Assert.That(b.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam" }));

            EditBase(a, gear => gear.Tags.Add("pierce"));
            b.PullAllBackingStoresAsync();

            Assert.That(b.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "beam", "pierce" }),
                "the pull carried only the base; the variant was re-resolved in the admitting hold");
        }

        [Test]
        public void AFlattenFromAnotherWriterReachesACacheHoldingTheVariant()
        {
            var path = PathOf("gear.cc");
            using var a = Open(path);
            SeedBase(a);
            SeedBig(a);
            using var b = Open(path);
            Assert.That(b.GetStored(BigKey)!.Variant, Is.Not.Null);

            a.FlattenAsync(BigKey).GetAwaiter().GetResult();
            a.FlushAsync().GetAwaiter().GetResult();
            b.PullAllBackingStoresAsync();

            Assert.That(b.GetStored(BigKey)!.Variant, Is.Null, "a load reports what is on disk; it is not a plain write refused at a variant key");
            Assert.That(b.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25));
        }

        [Test]
        public void ARebaseResolvesFromTheNewBase()
        {
            using var cache = Open(PathOf("gear.cc"));
            SeedBase(cache);
            SeedBig(cache);
            var spare = new CultRecordKey("spare");
            cache.Commit(batch => batch.Upsert(typeof(VariantGear), new VariantGear { Name = "spare", Code = "s1", Tags = { "arc" } }, spare));

            cache.Commit(batch => batch.UpsertVariant(BigKey, spare, cache.GetStored(BigKey)!.Variant!.Overrides));

            Assert.That(cache.Get<VariantGear>(BigKey)!.Tags, Is.EqualTo(new[] { "arc" }));
            Assert.That(cache.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25));
            Assert.That(cache.GetStored(BigKey)!.Variant!.BaseKey, Is.EqualTo(spare.Value));
        }

        // ---- readers ----

        private string WriteStore(params CultPersistedRecord[] records) => WriteStore(CultPersistedStoreSnapshot.FormatV2, records);

        private string WriteStore(string formatVersion, params CultPersistedRecord[] records)
        {
            var path = PathOf($"hand-{Guid.NewGuid():N}.cc");
            var gear = Registry.GetRequired<VariantGear>();
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
            {
                FormatVersion = formatVersion,
                SchemaCatalog = new[] { gear.ToCatalogEntry() },
                Records = records
            }));
            return path;
        }

        private static CultPersistedRecord PlainGear(string key) => new()
        {
            Key = key,
            SchemaId = Registry.GetRequired<VariantGear>().SchemaId,
            StoredAt = "2026-01-01T00:00:00.0000000Z",
            Payload = CultDocumentMessagePackSerialization.SerializeUntyped(Laser(), typeof(VariantGear), Registry)
        };

        private static CultPersistedRecord VariantGearRecord(string key, string baseKey, byte[]? payload = null, CultOverrideOp op = CultOverrideOp.Set) => new()
        {
            Key = key,
            SchemaId = Registry.GetRequired<VariantGear>().SchemaId,
            StoredAt = "2026-01-01T00:00:00.0000000Z",
            Payload = payload ?? Array.Empty<byte>(),
            Variant = new CultVariantDelta(baseKey, new[]
            {
                new CultVariantOverride(op, new[] { new CultPathStep(0) }, string.Empty, new byte[] { 0xa1, (byte)'x' })
            })
        };

        [Test]
        public void AVariantRecordUnderAV1HeaderIsRefusedNamingTheRecord()
        {
            var path = WriteStore(CultPersistedStoreSnapshot.FormatV1, PlainGear("hand-base"), VariantGearRecord("hand-variant", "hand-base"));
            var error = Assert.Throws<NotSupportedException>(() => Open(path).Dispose())!;
            Assert.That(error.Message, Does.Contain("hand-variant").And.Contain("cultcache.store.v2"));
        }

        [Test]
        public void AVariantRecordWithAPayloadIsRefusedNamingTheRecord()
        {
            var path = WriteStore(PlainGear("hand-base"), VariantGearRecord("hand-variant", "hand-base", payload: new byte[] { 0x90 }));
            var error = Assert.Throws<NotSupportedException>(() => Open(path).Dispose())!;
            Assert.That(error.Message, Does.Contain("hand-variant").And.Contain("payload"));
        }

        [Test]
        public void AnOverrideWithAnUnknownOpIsRefusedNamingTheRecord()
        {
            var path = WriteStore(PlainGear("hand-base"), VariantGearRecord("hand-variant", "hand-base", op: (CultOverrideOp)7));
            var error = Assert.Throws<NotSupportedException>(() => Open(path).Dispose())!;
            Assert.That(error.Message, Does.Contain("hand-variant").And.Contain("op 7"));
        }

        // ---- the other stores refuse loudly until their own cuts ----

        [Test]
        public void TheDirectoryStoreRefusesAVariantByPushAndByCommit()
        {
            using var cache = CultCacheMessagePack.Create(PathOf("dir.cc"), new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = true });
            SeedBase(cache);

            var push = Assert.Throws<NotSupportedException>(() => cache.UpsertVariantAsync(
                BigKey, BaseKey, new[] { cache.Override<VariantGear>(nameof(VariantGear.Name), "laser big"), cache.Override<VariantGear>(nameof(VariantGear.Code), "laser big") }).GetAwaiter().GetResult())!;
            Assert.That(push.Message, Does.Contain(BigKey.Value));
            var commit = Assert.Throws<NotSupportedException>(() => SeedBig(cache))!;
            Assert.That(commit.Message, Does.Contain(BigKey.Value));
            Assert.That(cache.Get(BigKey), Is.Null);
        }

        // ---- shared vector ----

        // Written once by the code under test through the production path; every runtime's tests read the committed bytes.
        // CULTLIB_WRITE_VARIANT_VECTOR=1 prints the store as base64 instead of asserting.
        [Test]
        public void TheCommittedC1VectorResolvesAndRewritesByteIdentical()
        {
            var vector = VectorPath("variant-store.msgpack");
            var path = PathOf("gear.cc");
            if (Environment.GetEnvironmentVariable("CULTLIB_WRITE_VARIANT_VECTOR") == "1")
            {
                using (var cache = Open(path))
                {
                    SeedBase(cache);
                    SeedBig(cache);
                }

                Console.WriteLine("VARIANT-VECTOR-BASE64:" + Convert.ToBase64String(File.ReadAllBytes(path)));
                Assert.Pass("vector printed");
            }

            File.Copy(vector, path);
            using (var cache = Open(path))
            {
                Assert.That(cache.Get<VariantGear>(BigKey)!.Power, Is.EqualTo(25));
                Assert.That(cache.Get<VariantGear>(BigKey)!.Code, Is.EqualTo("laser big"));
                cache.BackingStores[0].PushAll();
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(File.ReadAllBytes(vector)));
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v2"));
        }

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c1", name);
                if (File.Exists(candidate) || Environment.GetEnvironmentVariable("CULTLIB_WRITE_VARIANT_VECTOR") == "1" && Directory.Exists(Path.GetDirectoryName(candidate)))
                    return candidate;
            }

            if (Environment.GetEnvironmentVariable("CULTLIB_WRITE_VARIANT_VECTOR") == "1")
                return name;
            throw new FileNotFoundException($"Shared vector {name} not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        [CultDocument("tests.variant_gear", "tests.variant_gear.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class VariantGear
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Power;

            [Key(2)]
            [CultIndex("code")]
            public string Code = string.Empty;

            [Key(3)]
            public List<string> Tags = new();
        }

        [CultDocument("tests.variant_other", "tests.variant_other.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class VariantOther
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.variant_global", "tests.variant_global.v1")]
        [MessagePackObject(AllowPrivate = true)]
        [CultGlobal]
        internal sealed class VariantGlobal
        {
            [Key(0)]
            public string Value = string.Empty;
        }

        // The same schema at two versions of the type: Wide has a member Narrow later dropped.
        [CultDocument("tests.variant_drift", "tests.variant_drift.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class DriftWide
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Power;

            [Key(2)]
            public string Wing = string.Empty;
        }

        [CultDocument("tests.variant_drift", "tests.variant_drift.v2")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class DriftNarrow
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Power;
        }
    }
}
