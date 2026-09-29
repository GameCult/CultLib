#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The id engine's two halves, driven directly: which element types a document type reaches, and how unset ids are filled.
    public class ElementIdEngineTests
    {
        private static List<string> Problems(Type type) => CultElementIds.Inspect(type).Problems;

        private static int Assign(object root, string key, bool deterministic)
        {
            var plan = CultElementIds.Plan(root, key, deterministic);
            plan.Apply();
            return plan.Count;
        }

        private static void RefusesBare(Type holder) =>
            Assert.That(Problems(holder), Has.Count.EqualTo(1).And.All.Contain("EBare").And.All.Contain("[CultElementId]"), holder.Name);

        [Test]
        public void EveryShapeThatCanHoldAListReachesItsElements()
        {
            foreach (var holder in new[]
            {
                typeof(HList), typeof(HArray), typeof(HDictionary), typeof(HNested), typeof(HListOfLists),
                typeof(HElementNest), typeof(HUnion), typeof(HNullableInner)
            })
                RefusesBare(holder);
            Assert.That(Problems(typeof(HFine)), Is.Empty);
        }

        [Test]
        public void EachWayAnIdCanBeWrongIsNamed()
        {
            Assert.That(Problems(typeof(HTwo)).Single(), Does.Contain("two [CultElementId] members").And.Contain("A").And.Contain("B"));
            Assert.That(Problems(typeof(HInt)).Single(), Does.Contain("not a string").And.Contain("Id"));
            Assert.That(Problems(typeof(HNoKey)).Single(), Does.Contain("no integer [Key]").And.Contain("Id"));
            Assert.That(Problems(typeof(HGetOnly)).Single(), Does.Contain("cannot be assigned").And.Contain("Id"));
            Assert.That(Problems(typeof(HMissingSource)).Single(), Does.Contain("Nope").And.Contain("not a keyed member"));
            Assert.That(Problems(typeof(HBadSourceKind)).Single(), Does.Contain("Tags").And.Contain("not a string or a number"));
            Assert.That(Problems(typeof(HUnkeyedSource)).Single(), Does.Contain("Loose").And.Contain("not a keyed member"));
            Assert.That(Problems(typeof(HNullableSource)), Is.Empty, "a nullable number is a number");
        }

        [Test]
        public void AssignFillsEveryListShapeAndOnlyTheUnsetOnes()
        {
            var root = new HAll
            {
                Dict = { ["k"] = new HHasIds { Items = { new EIded(), new EIded { Id = "0123456789ab" } } } },
                Grid = { new List<EIded> { new EIded(), new EIded() } },
                Nest = { new ENest { Sub = { new EIded() } } },
            };
            var count = Assign(root, "r", deterministic: false);
            Assert.That(count, Is.EqualTo(5));
            Assert.That(root.Dict["k"].Items[1].Id, Is.EqualTo("0123456789ab"));
            var ids = new[] { root.Dict["k"].Items[0].Id }.Concat(root.Grid[0].Select(x => x.Id)).Concat(new[] { root.Nest[0].Id, root.Nest[0].Sub[0].Id }).ToArray();
            Assert.That(ids, Has.All.Matches<string>(id => id.Length == 12));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length));
            Assert.That(Assign(root, "r", deterministic: false), Is.EqualTo(0), "a second pass finds nothing unset");
        }

        [Test]
        public void APlanCountsAndChangesNothingUntilApplied()
        {
            var root = new HHasIds { Items = { new EIded(), new EIded() } };
            var plan = CultElementIds.Plan(root, "r", deterministic: true);
            Assert.That(plan.Count, Is.EqualTo(2));
            Assert.That(root.Items.Select(x => x.Id), Has.All.Empty);
            plan.Apply();
            Assert.That(root.Items.Select(x => x.Id), Has.All.Not.Empty);
            plan.Undo();
            Assert.That(root.Items.Select(x => x.Id), Has.All.Empty);
        }

        [Test]
        public void ADeterministicIdDependsOnTheRecordAndThePositionAndNothingElse()
        {
            HHasIds Make() => new() { Items = { new EIded(), new EIded() } };
            string[] Ids(string key)
            {
                var root = Make();
                Assign(root, key, deterministic: true);
                return root.Items.Select(x => x.Id).ToArray();
            }

            Assert.That(Ids("a"), Is.EqualTo(Ids("a")));
            Assert.That(Ids("a"), Is.Not.EqualTo(Ids("b")));
            Assert.That(Ids("a")[0], Is.Not.EqualTo(Ids("a")[1]));
            Assert.That(Ids("a"), Has.All.Matches<string>(id => Regex.IsMatch(id, "^[0-9a-f]{12}$")));
            var random = Make();
            Assign(random, "a", deterministic: false);
            Assert.That(random.Items.Select(x => x.Id), Is.Not.EqualTo(Ids("a")), "a write mints random ids, a load mints from the path");
        }

        [Test]
        public void ADuplicateIsRefusedInAnyListShapeNamingTheRecordTheListAndTheId()
        {
            var dict = new HAll { Dict = { ["k"] = new HHasIds { Items = { new EIded { Id = "aaaaaaaaaaaa" }, new EIded { Id = "aaaaaaaaaaaa" } } } } };
            var refusal = Assert.Throws<CultElementIdException>(() => Assign(dict, "rec", deterministic: true))!;
            Assert.That(refusal.RecordKey, Is.EqualTo("rec"));
            Assert.That(refusal.ElementId, Is.EqualTo("aaaaaaaaaaaa"));
            Assert.That(refusal.ListPath, Is.EqualTo("rec.0{k}.0"));
            Assert.That(refusal.Message, Does.Contain("rec").And.Contain("aaaaaaaaaaaa").And.Contain("rec.0{k}.0"));
            var grid = new HAll { Grid = { new List<EIded> { new EIded { Id = "bbbbbbbbbbbb" }, new EIded { Id = "bbbbbbbbbbbb" } } } };
            Assert.Throws<CultElementIdException>(() => Assign(grid, "r", deterministic: false));
        }

        [Test]
        public void ARandomIdMustBeTwelveLowercaseHexCharactersAndADerivedIdNeedOnlyBeText()
        {
            foreach (var bad in new[] { "x", "ABCDEF012345", "0123456789a", "0123456789abc", "0123456789ag", "0123456789 a" })
            {
                var refusal = Assert.Throws<CultElementIdException>(() => Assign(new HHasIds { Items = { new EIded { Id = bad } } }, "r", deterministic: false), bad)!;
                Assert.That(refusal.Message, Does.Contain("12 lowercase hex").And.Contain(bad), bad);
                Assert.That(refusal.ElementId, Is.EqualTo(bad));
            }

            Assert.That(Assign(new HHasIds { Items = { new EIded { Id = "0123456789ab" }, new EIded { Id = "abcdef012345" } } }, "r", deterministic: false), Is.EqualTo(0));
            Assert.That(Assign(new HDerived { Items = { new EDerived { Number = 5, Id = "not hex, not the source" } } }, "r", deterministic: false), Is.EqualTo(0));
        }

        [Test]
        public void ADerivedIdWhoseSourceIsNullOrEmptyIsRefusedNamingTheSourceMember()
        {
            foreach (var name in new string?[] { null, "" })
            {
                var refusal = Assert.Throws<CultElementIdException>(
                    () => Assign(new HDerivedText { Items = { new EDerivedText { Name = "fine" }, new EDerivedText { Name = name } } }, "rec", deterministic: false),
                    name ?? "null")!;
                Assert.That(refusal.Member, Is.EqualTo("Name"));
                Assert.That(refusal.RecordKey, Is.EqualTo("rec"));
                Assert.That(refusal.Message, Does.Contain("Name").And.Contain("null or empty"));
            }

            Assert.That(Assign(new HDerivedText { Items = { new EDerivedText { Name = "a" }, new EDerivedText { Name = "b" } } }, "r", deterministic: false), Is.EqualTo(2));
        }

        [Test]
        public void APlanThatRefusesHasChangedNothingAndAnUndoRestoresWhatWasThere()
        {
            var root = new HAll
            {
                Dict = { ["k"] = new HHasIds { Items = { new EIded(), new EIded() } } },
                Grid = { new List<EIded> { new EIded { Id = "cccccccccccc" }, new EIded { Id = "cccccccccccc" } } },
            };
            Assert.Throws<CultElementIdException>(() => Assign(root, "r", deterministic: false));
            Assert.That(root.Dict["k"].Items.Select(x => x.Id), Has.All.Empty, "the list before the refused one was not minted into");

            var ok = new HHasIds { Items = { new EIded(), new EIded { Id = "dddddddddddd" } } };
            var plan = CultElementIds.Plan(ok, "r", deterministic: false);
            plan.Apply();
            plan.Undo();
            Assert.That(ok.Items.Select(x => x.Id), Is.EqualTo(new[] { "", "dddddddddddd" }));
        }

        [Test]
        public void ADeterministicIdIsTheSameUnderEveryCultureIncludingDictionaryKeys()
        {
            var expected = new[]
            {
                Sha6("r|r.0{1.5}.0/0|0"), Sha6("r|r.1{-3}.0/0|0"), Sha6("r|r.2{09/29/2026 12:00:00}.0/0|0")
            };
            string[] Mint()
            {
                var root = new HCulture
                {
                    ByDouble = { [1.5] = new HHasIds { Items = { new EIded() } } },
                    ByInt = { [-3] = new HHasIds { Items = { new EIded() } } },
                    ByDate = { [new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Unspecified)] = new HHasIds { Items = { new EIded() } } },
                };
                Assign(root, "r", deterministic: true);
                return new[] { root.ByDouble[1.5].Items[0].Id, root.ByInt[-3].Items[0].Id, root.ByDate.Values.Single().Items[0].Id };
            }

            var saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                foreach (var name in new[] { "en-US", "de-DE", "sv-SE", "ar-SA" })
                {
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(name);
                    if (name == "de-DE")
                        Assert.That(1.5.ToString(), Is.EqualTo("1,5"), "the runtime has no culture data, so this test would prove nothing");
                    Assert.That(Mint(), Is.EqualTo(expected), name);
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }

        [Test]
        public void AnInterfaceUnionIsJudgedOnItsConcreteTypesAndMintedIntoThem()
        {
            Assert.That(Problems(typeof(HIfaceBad)).Single(), Does.Contain("IfaceBad").And.Contain("no [CultElementId] member"));
            Assert.That(Problems(typeof(HIfaceGood)), Is.Empty);
            var root = new HIfaceGood { Items = { new IfaceGood(), new IfaceGood() } };
            Assert.That(Assign(root, "r", deterministic: false), Is.EqualTo(2));
            Assert.That(root.Items.Select(x => ((IfaceGood)x).Id), Has.All.Not.Empty);
        }

        [Test]
        public void AnAbstractUnionBaseIsNotJudgedAndASubtypeListedTwiceIsJudgedOnce()
        {
            Assert.That(Problems(typeof(HDup)).Single(), Does.Contain("DupSub").And.Contain("no [CultElementId] member"));
            Assert.That(Problems(typeof(HAbstractNoId)), Is.Empty, "the abstract base has no id, and no element can be one");
        }

        [Test]
        public void ASubtypeThatReusesTheBaseIdsKeyIsRefusedAtRegistration()
        {
            var problem = Problems(typeof(HKeyClash)).Single();
            Assert.That(problem, Does.Contain("KeyClash").And.Contain("Id").And.Contain("Clash").And.Contain("Key(0)"));
        }

        private static string Sha6(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text)).Take(6).Select(b => b.ToString("x2")));
        }

        [Test]
        public void TheDeterministicIdIsThePinnedDerivationAndARetryFollowsACollision()
        {
            var plain = new HHasIds { Items = { new EIded(), new EIded() } };
            Assign(plain, "r", deterministic: true);
            Assert.That(plain.Items.Select(x => x.Id), Is.EqualTo(new[] { Sha6("r|r.0/0|0"), Sha6("r|r.0/1|0") }));

            var collide = new HHasIds { Items = { new EIded(), new EIded { Id = Sha6("r|r.0/0|0") } } };
            Assign(collide, "r", deterministic: true);
            Assert.That(collide.Items[0].Id, Is.EqualTo(Sha6("r|r.0/0|1")));

            var dict = new HAll { Dict = { ["k"] = new HHasIds { Items = { new EIded() } } } };
            Assign(dict, "r", deterministic: true);
            Assert.That(dict.Dict["k"].Items[0].Id, Is.EqualTo(Sha6("r|r.0{k}.0/0|0")));
        }

        [Test]
        public void ANullElementIsSkippedAndAMintedIdNeverEqualsAnExplicitOne()
        {
            var root = new HHasIds { Items = { null!, new EIded(), new EIded { Id = "abcdef012345" } } };
            Assert.That(Assign(root, "r", deterministic: false), Is.EqualTo(1));
            Assert.That(root.Items[1].Id, Is.Not.Empty.And.Not.EqualTo("explicit"));
        }

        [Test]
        public void ADerivedIdFollowsItsSourceIncludingANullableNumberAndDuplicatesAreRefused()
        {
            var root = new HDerived { Items = { new EDerived { Number = 5 }, new EDerived { Number = 12 }, new EDerived { Number = 5, Id = "own" } } };
            Assign(root, "r", deterministic: false);
            Assert.That(root.Items.Select(x => x.Id), Is.EqualTo(new[] { "5", "12", "own" }));
            var twice = new HDerived { Items = { new EDerived { Number = 5 }, new EDerived { Number = 5 } } };
            Assert.That(Assert.Throws<CultElementIdException>(() => Assign(twice, "r", deterministic: false))!.Message, Does.Contain("'5'"));
        }

        // ---- shapes ----
        [MessagePackObject] public class EBare { [Key(0)] public string X { get; set; } = ""; }
        [MessagePackObject] public class EIded { [Key(0)] public string X { get; set; } = ""; [Key(1)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class ENest { [Key(0)] [CultElementId] public string Id { get; set; } = ""; [Key(1)] public List<EIded> Sub { get; set; } = new(); }
        [MessagePackObject] public class EBareNest { [Key(0)] [CultElementId] public string Id { get; set; } = ""; [Key(1)] public List<EBare> Sub { get; set; } = new(); }

        [MessagePackObject] public class HList { [Key(0)] public List<EBare> Items { get; set; } = new(); }
        [MessagePackObject] public class HArray { [Key(0)] public EBare[] Items { get; set; } = Array.Empty<EBare>(); }
        [MessagePackObject] public class HDictionary { [Key(0)] public Dictionary<string, HList> Map { get; set; } = new(); }
        [MessagePackObject] public class HNested { [Key(0)] public HList Inner { get; set; } = new(); }
        [MessagePackObject] public class HNullableInner { [Key(0)] public HList? Inner { get; set; } }
        [MessagePackObject] public class HListOfLists { [Key(0)] public List<List<EBare>> Grid { get; set; } = new(); }
        [MessagePackObject] public class HElementNest { [Key(0)] public List<EBareNest> Items { get; set; } = new(); }
        [MessagePackObject] public class HFine { [Key(0)] public List<ENest> Items { get; set; } = new(); [Key(1)] public List<string> Tags { get; set; } = new(); [Key(2)] public int[] Numbers { get; set; } = Array.Empty<int>(); }

        [MessagePackObject]
        [Union(0, typeof(UnionSub))]
        public abstract class UnionBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class UnionSub : UnionBase { [Key(1)] public List<EBare> Hidden { get; set; } = new(); }
        [MessagePackObject] public class HUnion { [Key(0)] public List<UnionBase> Items { get; set; } = new(); }

        [MessagePackObject] public class ETwo { [Key(0)] [CultElementId] public string A { get; set; } = ""; [Key(1)] [CultElementId] public string B { get; set; } = ""; }
        [MessagePackObject] public class HTwo { [Key(0)] public List<ETwo> Items { get; set; } = new(); }
        [MessagePackObject] public class EInt { [Key(0)] [CultElementId] public int Id { get; set; } }
        [MessagePackObject] public class HInt { [Key(0)] public List<EInt> Items { get; set; } = new(); }
        [MessagePackObject] public class ENoKey { [IgnoreMember] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HNoKey { [Key(0)] public List<ENoKey> Items { get; set; } = new(); }
        [MessagePackObject] public class EGetOnly { [Key(0)] [CultElementId] public string Id { get; } = ""; }
        [MessagePackObject] public class HGetOnly { [Key(0)] public List<EGetOnly> Items { get; set; } = new(); }
        [MessagePackObject] public class EMissingSource { [Key(0)] [CultElementId("Nope")] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HMissingSource { [Key(0)] public List<EMissingSource> Items { get; set; } = new(); }
        [MessagePackObject] public class EBadSourceKind { [Key(0)] public List<string> Tags { get; set; } = new(); [Key(1)] [CultElementId(nameof(Tags))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HBadSourceKind { [Key(0)] public List<EBadSourceKind> Items { get; set; } = new(); }
        [MessagePackObject] public class EUnkeyedSource { [IgnoreMember] public string Loose { get; set; } = ""; [Key(0)] [CultElementId(nameof(Loose))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HUnkeyedSource { [Key(0)] public List<EUnkeyedSource> Items { get; set; } = new(); }
        [MessagePackObject] public class ENullableSource { [Key(0)] public int? Number { get; set; } [Key(1)] [CultElementId(nameof(Number))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HNullableSource { [Key(0)] public List<ENullableSource> Items { get; set; } = new(); }

        [MessagePackObject] public class HHasIds { [Key(0)] public List<EIded> Items { get; set; } = new(); }
        [MessagePackObject]
        public class HAll
        {
            [Key(0)] public Dictionary<string, HHasIds> Dict { get; set; } = new();
            [Key(1)] public List<List<EIded>> Grid { get; set; } = new();
            [Key(2)] public List<ENest> Nest { get; set; } = new();
        }

        [MessagePackObject] public class EDerivedText { [Key(0)] public string? Name { get; set; } [Key(1)] [CultElementId(nameof(Name))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HDerivedText { [Key(0)] public List<EDerivedText> Items { get; set; } = new(); }

        [MessagePackObject]
        public class HCulture
        {
            [Key(0)] public Dictionary<double, HHasIds> ByDouble { get; set; } = new();
            [Key(1)] public Dictionary<int, HHasIds> ByInt { get; set; } = new();
            [Key(2)] public Dictionary<DateTime, HHasIds> ByDate { get; set; } = new();
        }

        // An interface union: the interface declares the id, the concrete type does not, so nothing would ever mint into it.
        [Union(0, typeof(IfaceBad))]
        public interface IUnionIface { [Key(0)] [CultElementId] string Id { get; set; } }
        [MessagePackObject] public class IfaceBad : IUnionIface { [Key(0)] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HIfaceBad { [Key(0)] public List<IUnionIface> Items { get; set; } = new(); }

        [Union(0, typeof(IfaceGood))]
        public interface IUnionIfaceGood { }
        [MessagePackObject] public class IfaceGood : IUnionIfaceGood { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HIfaceGood { [Key(0)] public List<IUnionIfaceGood> Items { get; set; } = new(); }

        // A subtype that reuses the base id's key.
        [MessagePackObject]
        [Union(0, typeof(KeyClash))]
        public abstract class KeyClashBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class KeyClash : KeyClashBase { [Key(0)] public string Clash { get; set; } = ""; }
        [MessagePackObject] public class HKeyClash { [Key(0)] public List<KeyClashBase> Items { get; set; } = new(); }

        [MessagePackObject]
        [Union(0, typeof(DupSub))]
        [Union(1, typeof(DupSub))]
        public abstract class DupBase { }
        [MessagePackObject] public class DupSub : DupBase { [Key(0)] public string X { get; set; } = ""; }
        [MessagePackObject] public class HDup { [Key(0)] public List<DupBase> Items { get; set; } = new(); }

        [MessagePackObject]
        [Union(0, typeof(NoIdSub))]
        public abstract class NoIdBase { }
        [MessagePackObject] public class NoIdSub : NoIdBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HAbstractNoId { [Key(0)] public List<NoIdBase> Items { get; set; } = new(); }

        [MessagePackObject] public class EDerived { [Key(0)] public int? Number { get; set; } [Key(1)] [CultElementId(nameof(Number))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HDerived { [Key(0)] public List<EDerived> Items { get; set; } = new(); }
    }
}
