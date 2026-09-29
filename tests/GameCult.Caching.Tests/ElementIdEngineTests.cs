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
        private static List<string> Problems(Type type) => CultElementIds.Problems(type);

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
                Dict = { ["k"] = new HHasIds { Items = { new EIded(), new EIded { Id = "kept" } } } },
                Grid = { new List<EIded> { new EIded(), new EIded() } },
                Nest = { new ENest { Sub = { new EIded() } } },
            };
            var count = CultElementIds.Assign(root, "r", deterministic: false, refuseDuplicates: true);
            Assert.That(count, Is.EqualTo(5));
            Assert.That(root.Dict["k"].Items[1].Id, Is.EqualTo("kept"));
            var ids = new[] { root.Dict["k"].Items[0].Id }.Concat(root.Grid[0].Select(x => x.Id)).Concat(new[] { root.Nest[0].Id, root.Nest[0].Sub[0].Id }).ToArray();
            Assert.That(ids, Has.All.Matches<string>(id => id.Length == 12));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length));
            Assert.That(CultElementIds.Assign(root, "r", deterministic: false, refuseDuplicates: true), Is.EqualTo(0), "a second pass finds nothing unset");
        }

        [Test]
        public void ADryRunCountsAndChangesNothing()
        {
            var root = new HHasIds { Items = { new EIded(), new EIded() } };
            Assert.That(CultElementIds.Assign(root, "r", deterministic: true, refuseDuplicates: true, dryRun: true), Is.EqualTo(2));
            Assert.That(root.Items.Select(x => x.Id), Has.All.Empty);
        }

        [Test]
        public void ADeterministicIdDependsOnTheRecordAndThePositionAndNothingElse()
        {
            HHasIds Make() => new() { Items = { new EIded(), new EIded() } };
            string[] Ids(string key)
            {
                var root = Make();
                CultElementIds.Assign(root, key, deterministic: true, refuseDuplicates: true);
                return root.Items.Select(x => x.Id).ToArray();
            }

            Assert.That(Ids("a"), Is.EqualTo(Ids("a")));
            Assert.That(Ids("a"), Is.Not.EqualTo(Ids("b")));
            Assert.That(Ids("a")[0], Is.Not.EqualTo(Ids("a")[1]));
            Assert.That(Ids("a"), Has.All.Matches<string>(id => Regex.IsMatch(id, "^[0-9a-f]{12}$")));
            var random = Make();
            CultElementIds.Assign(random, "a", deterministic: false, refuseDuplicates: true);
            Assert.That(random.Items.Select(x => x.Id), Is.Not.EqualTo(Ids("a")), "a write mints random ids, a load mints from the path");
        }

        [Test]
        public void ADuplicateIsRefusedInAnyListShapeAndTolerableWhenNotRefusing()
        {
            var dict = new HAll { Dict = { ["k"] = new HHasIds { Items = { new EIded { Id = "x" }, new EIded { Id = "x" } } } } };
            Assert.Throws<InvalidOperationException>(() => CultElementIds.Assign(dict, "r", deterministic: true, refuseDuplicates: true));
            Assert.DoesNotThrow(() => CultElementIds.Assign(dict, "r", deterministic: true, refuseDuplicates: false));
            var grid = new HAll { Grid = { new List<EIded> { new EIded { Id = "y" }, new EIded { Id = "y" } } } };
            Assert.Throws<InvalidOperationException>(() => CultElementIds.Assign(grid, "r", deterministic: false, refuseDuplicates: true));
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
            CultElementIds.Assign(plain, "r", deterministic: true, refuseDuplicates: true);
            Assert.That(plain.Items.Select(x => x.Id), Is.EqualTo(new[] { Sha6("r|r.0/0|0"), Sha6("r|r.0/1|0") }));

            var collide = new HHasIds { Items = { new EIded(), new EIded { Id = Sha6("r|r.0/0|0") } } };
            CultElementIds.Assign(collide, "r", deterministic: true, refuseDuplicates: true);
            Assert.That(collide.Items[0].Id, Is.EqualTo(Sha6("r|r.0/0|1")));

            var dict = new HAll { Dict = { ["k"] = new HHasIds { Items = { new EIded() } } } };
            CultElementIds.Assign(dict, "r", deterministic: true, refuseDuplicates: true);
            Assert.That(dict.Dict["k"].Items[0].Id, Is.EqualTo(Sha6("r|r.0{k}.0/0|0")));
        }

        [Test]
        public void ANullElementIsSkippedAndAMintedIdNeverEqualsAnExplicitOne()
        {
            var root = new HHasIds { Items = { null!, new EIded(), new EIded { Id = "explicit" } } };
            Assert.That(CultElementIds.Assign(root, "r", deterministic: false, refuseDuplicates: true), Is.EqualTo(1));
            Assert.That(root.Items[1].Id, Is.Not.Empty.And.Not.EqualTo("explicit"));
        }

        [Test]
        public void ADerivedIdFollowsItsSourceIncludingANullableNumberAndDuplicatesAreRefused()
        {
            var root = new HDerived { Items = { new EDerived { Number = 5 }, new EDerived { Number = 12 }, new EDerived { Number = 5, Id = "own" } } };
            CultElementIds.Assign(root, "r", deterministic: false, refuseDuplicates: true);
            Assert.That(root.Items.Select(x => x.Id), Is.EqualTo(new[] { "5", "12", "own" }));
            var twice = new HDerived { Items = { new EDerived { Number = 5 }, new EDerived { Number = 5 } } };
            Assert.That(Assert.Throws<InvalidOperationException>(() => CultElementIds.Assign(twice, "r", deterministic: false, refuseDuplicates: true))!.Message, Does.Contain("'5'"));
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

        [MessagePackObject] public class EDerived { [Key(0)] public int? Number { get; set; } [Key(1)] [CultElementId(nameof(Number))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HDerived { [Key(0)] public List<EDerived> Items { get; set; } = new(); }
    }
}
