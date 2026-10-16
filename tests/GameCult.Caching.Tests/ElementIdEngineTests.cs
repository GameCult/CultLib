#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
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
            var plan = CultElementIds.Plan(root.GetType(), root, key, deterministic);
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
                typeof(HElementNest), typeof(HUnion), typeof(HNullableInner), typeof(HStruct), typeof(HStructList), typeof(HNullableStruct),
                typeof(HReadOnlyMap)
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

        // Holds is what a store marks its header by: an id anywhere a list can sit counts, and an unset one does not.
        [Test]
        public void HoldsFindsASetIdWhereverAListCanSitAndNothingElse()
        {
            const string id = "0123456789ab";
            Assert.That(CultElementIds.Holds(typeof(HAll), null), Is.False);
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll
            {
                Dict = { ["k"] = new HHasIds { Items = { new EIded() } } },
                Grid = { new List<EIded> { new EIded() } },
                Nest = { new ENest { Sub = { new EIded() } } },
            }), Is.False, "unset ids hold nothing");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Dict = { ["k"] = new HHasIds { Items = { new EIded { Id = id } } } } }), Is.True, "a dictionary value");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Grid = { new List<EIded> { new EIded { Id = id } } } }), Is.True, "a list of lists");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Nest = { new ENest { Sub = { new EIded { Id = id } } } } }), Is.True, "a list under an unset element");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Nest = { new ENest { Id = id } } }), Is.True, "an element");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Dict = { ["a"] = new HHasIds { Items = { new EIded() } }, ["b"] = new HHasIds { Items = { new EIded { Id = id } } } } }), Is.True, "the second dictionary value");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Grid = { new List<EIded> { new EIded() }, new List<EIded> { new EIded { Id = id } } } }), Is.True, "the second list");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Nest = { new ENest(), new ENest { Sub = { new EIded { Id = id } } } } }), Is.True, "the second item of a list of elements");
            Assert.That(CultElementIds.Holds(typeof(HAll), new HAll { Grid = { new List<EIded> { new EIded(), new EIded { Id = id } } } }), Is.True, "the second element of a list");
        }

        [Test]
        public void APlanCountsAndChangesNothingUntilApplied()
        {
            var root = new HHasIds { Items = { new EIded(), new EIded() } };
            var plan = CultElementIds.Plan(typeof(HHasIds), root, "r", deterministic: true);
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
        public void TheHexRefusalNamesTheDerivedFormForReadableIds()
        {
            var refusal = Assert.Throws<CultElementIdException>(() => Assign(new HHasIds { Items = { new EIded { Id = "root" } } }, "r", deterministic: false))!;
            Assert.That(refusal.Message, Does.Contain("12 lowercase hex").And.Contain("[CultElementId(nameof(Member))]"));
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
            var plan = CultElementIds.Plan(typeof(HHasIds), ok, "r", deterministic: false);
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

        // A list element is written by its [Key] layout or its id never reaches disk. A union arm without [MessagePackObject]
        // is written by some other formatter; a list of a type that is not keyed at all is a value list, replaced whole.
        [Test]
        public void ANonKeyedArmOfAListUnionIsRefusedAtRegistration()
        {
            var problem = Problems(typeof(HUnkeyedArmList)).Single();
            Assert.That(problem, Does.Contain("UnkeyedArm").And.Contain("formatter the cache does not own").And.Contain("HUnkeyedArmList"));
            Assert.That(Problems(typeof(HUnkeyedArmField)), Is.Empty, "the same union as a field has no element id to lose");
            Assert.That(Problems(typeof(HValueList)), Is.Empty, "a list of a non-keyed type is a value list");
        }

        // [MessagePackFormatter] on an element type, an owner, a collection class or a member makes that value one whole value: it
        // has no element ids and is off the id path. The attribute on the id member itself is the one refusal that stays.
        [Test]
        public void AFormatterAttributeMakesAWholeValueExceptOnTheId()
        {
            Assert.That(Problems(typeof(HFormattedId)).Single(), Does.Contain("Id").And.Contain("[MessagePackFormatter]"));
            foreach (var (type, holder) in new (Type, object)[]
            {
                (typeof(HFormattedElement), new HFormattedElement { Items = { new FormattedElement() } }),
                (typeof(HFormattedList), new HFormattedList { Items = { new EIded() } }),
                (typeof(HFormattedOwner), new HFormattedOwner { Owner = { Items = { new EIded() } } }),
                (typeof(HFormattedMember), new HFormattedMember { Inner = { Items = { new EIded() } } }),
                (typeof(HFormattedDerived), new HFormattedDerived { Items = { new FormattedDerived() } }),
            })
            {
                Assert.That(Problems(type), Is.Empty, type.Name);
                Assert.That(CultElementIds.Inspect(type).IdPath, Is.Empty, type.Name);
                Assert.That(Assign(holder, "r", deterministic: false), Is.EqualTo(0), type.Name);
            }

            Assert.That(Problems(typeof(HFormattedField)), Is.Empty, "a formatted type with no list below it is a field");
        }

        // The collection types between a member and its object list are written by some formatter too, so they sit on the id path.
        // A value list (of a struct, a primitive or a non-keyed class) never reaches an object, so it does not.
        [Test]
        public void ACollectionTypeOnTheWayToAnObjectListIsOnTheIdPath()
        {
            var path = CultElementIds.Inspect(typeof(HCollections)).IdPath;
            Assert.That(path, Is.EquivalentTo(new[]
            {
                typeof(HCollections), typeof(EIded), typeof(HHasIds),
                typeof(List<EIded>), typeof(EIded[]), typeof(Dictionary<string, HHasIds>), typeof(List<List<EIded>>)
            }));

            var valueLists = CultElementIds.Inspect(typeof(HValueListsBesideObjects)).IdPath;
            Assert.That(valueLists, Is.EquivalentTo(new[] { typeof(HValueListsBesideObjects), typeof(List<EIded>), typeof(EIded) }));
        }

        [Test]
        public void AFormatterOnACollectionClassOfAnObjectListIsAWholeValue()
        {
            Assert.That(Problems(typeof(HRoles)), Is.Empty);
            Assert.That(Assign(new HRoles { Items = { new EIded() } }, "r", deterministic: false), Is.EqualTo(0), "its elements carry no ids and are never minted");
            Assert.That(Problems(typeof(HFormattedValueCollection)), Is.Empty, "a collection class of a non-keyed element is a value list");
        }

        // MemberInfo.IsDefined ignores inherit for properties: an override sees its base's attribute only through Declares.
        [Test]
        public void AnInheritedFormatterOnAnOverriddenMemberIsHonoured()
        {
            Assert.That(Problems(typeof(HOverFormattedId)).Single(), Does.Contain("Id").And.Contain("[MessagePackFormatter]"));
            Assert.That(Problems(typeof(OwnerOverFormatted)), Is.Empty, "the override inherits its base's formatter, so the member is a whole value");
            Assert.That(CultElementIds.Inspect(typeof(OwnerOverFormatted)).IdPath, Is.Empty, "a walked override would put its list on the id path");
            Assert.That(Assign(new OwnerOverFormatted { Items = { new EIded() } }, "r", deterministic: false), Is.EqualTo(0), "and mint into a list its formatter may drop ids from");

            Assert.That(Problems(typeof(HIdOverride)), Is.Empty, "a base [CultElementId] is the override's id");
            var root = new HIdOverride { Items = { new IdOverride() } };
            Assert.That(Assign(root, "r", deterministic: false), Is.EqualTo(1));
            Assert.That(root.Items[0].Id, Has.Length.EqualTo(12));
        }

        // The union arm has a valid id, so IdProblem passes it and only the rule that an element is written by its [Key] layout
        // can refuse it; it is refused once per document however many lists hold it.
        [Test]
        public void ANonKeyedArmWithAValidIdIsRefusedOncePerType()
        {
            Assert.That(Problems(typeof(HUnkeyedArmList)).Single(), Does.Contain("UnkeyedArm").And.Contain("formatter the cache does not own"));
            Assert.That(Problems(typeof(HUnkeyedTwoLists)).Single(), Does.Contain("UnkeyedArm"));
            Assert.That(Problems(typeof(HUnkeyedArmField)), Is.Empty);
        }

        // The id path is a fixed point, not the stack at the moment a list is found: a type in a cycle with the list's owner, and
        // a type reached twice, are both on the way to it.
        [Test]
        public void TheIdPathHoldsEveryTypeThatReachesAnObjectList()
        {
            var path = CultElementIds.Inspect(typeof(HDiamond)).IdPath;
            Assert.That(path, Is.EquivalentTo(new[] { typeof(HDiamond), typeof(HLeft), typeof(HRight), typeof(HCycle), typeof(HCycleBack), typeof(EIded), typeof(List<EIded>) }));
            Assert.That(CultElementIds.Inspect(typeof(HFine)).IdPath, Is.EquivalentTo(new[] { typeof(HFine), typeof(ENest), typeof(EIded), typeof(List<ENest>), typeof(List<EIded>) }));
            Assert.That(CultElementIds.Inspect(typeof(HFormattedField)).IdPath, Is.Empty);

            // A union base is on the path both as the type a list holds (its formatter writes the arm) and as a field type whose
            // arm holds the list.
            Assert.That(CultElementIds.Inspect(typeof(HPlainBaseList)).IdPath, Is.EquivalentTo(new[] { typeof(HPlainBaseList), typeof(PlainBase), typeof(PlainArm), typeof(List<PlainBase>) }));
            Assert.That(CultElementIds.Inspect(typeof(HIdBaseOwner)).IdPath, Is.EquivalentTo(new[] { typeof(HIdBaseOwner), typeof(IdBase), typeof(IdArm), typeof(EIded), typeof(List<EIded>) }));
        }

        // A keyed struct is a value MessagePack writes by key, and an object list inside one carries element ids like any other.
        // It is walked as an owner and is never an element: a boxed copy cannot carry a minted id.
        [Test]
        public void AKeyedStructIsWalkedAsAnOwnerAndNeverAnElement()
        {
            var root = new HStructIded
            {
                Hold = new SHoldIded { Items = new List<EIded> { new EIded() } },
                Many = { new SHoldIded { Items = new List<EIded> { new EIded() } } },
            };
            Assert.That(Problems(typeof(HStructIded)), Is.Empty);
            Assert.That(CultElementIds.Inspect(typeof(HStructIded)).IdPath,
                Is.EquivalentTo(new[] { typeof(HStructIded), typeof(SHoldIded), typeof(List<SHoldIded>), typeof(List<EIded>), typeof(EIded) }));
            Assert.That(CultElementIds.Holds(typeof(HStructIded), root), Is.False);
            Assert.That(Assign(root, "r", deterministic: false), Is.EqualTo(2));
            Assert.That(root.Hold.Items[0].Id, Is.Not.Empty, "visible through the struct copy");
            Assert.That(root.Many[0].Items[0].Id, Is.Not.Empty, "visible through the struct in a list");
            Assert.That(CultElementIds.Holds(typeof(HStructIded), root), Is.True);
            var one = new HStructIded { Hold = new SHoldIded { Items = new List<EIded> { new EIded { Id = "0123456789ab" } } } };
            Assert.That(CultElementIds.Holds(typeof(HStructIded), one), Is.True);

            Assert.That(Problems(typeof(HStructValues)), Is.Empty);
            Assert.That(CultElementIds.Inspect(typeof(HStructValues)).IdPath,
                Is.EquivalentTo(new[] { typeof(HStructValues), typeof(List<EIded>), typeof(EIded) }), "a list of keyed structs is a value list");
            var values = new HStructValues { Structs = { new SValue { N = 1 } }, Objects = { new EIded() } };
            Assert.That(Assign(values, "r", deterministic: false), Is.EqualTo(1));

            var nullable = new HNullableStructIded { Hold = new SHoldIded { Items = new List<EIded> { new EIded() } } };
            Assert.That(Assign(nullable, "r", deterministic: false), Is.EqualTo(1), "a nullable struct is walked as its struct");
            Assert.That(CultElementIds.Holds(typeof(HNullableStructIded), nullable), Is.True);
        }

        // A type is an owner or an element only when MessagePack serializes it by key ([MessagePackObject], inherited, or
        // [Union]). A struct without either is a value however many [Key] members it carries.
        [Test]
        public void OnlyAKeyedTypeIsWalked()
        {
            Assert.That(Problems(typeof(HUnkeyedStruct)), Is.Empty);
            Assert.That(CultElementIds.Inspect(typeof(HUnkeyedStruct)).IdPath, Is.Empty);
            Assert.That(Problems(typeof(HInheritedBare)).Single(), Does.Contain("InheritedBare").And.Contain("[CultElementId]"),
                "[MessagePackObject] reaches a subclass through inheritance");
        }

        // Every dictionary interface MessagePack writes as a map is walked by one path, and the minted path text is the one a
        // store loaded before ids already holds, so its deterministic ids do not move.
        [Test]
        public void EveryMapInterfaceIsWalkedByTheSamePath()
        {
            var element = new Func<HHasIds>(() => new HHasIds { Items = { new EIded() } });
            var viaInterface = new HReadOnlyIded { Map = new Dictionary<string, HHasIds> { ["k"] = element() } };
            var viaClass = new HReadOnlyIded { Map = new RoMap(new Dictionary<string, HHasIds> { ["k"] = element() }) };
            var viaDictionary = new HMapIded { Map = { ["k"] = element() } };
            Assert.That(Assign(viaInterface, "r", deterministic: true), Is.EqualTo(1));
            Assert.That(Assign(viaClass, "r", deterministic: true), Is.EqualTo(1), "a class implementing only IReadOnlyDictionary<,>");
            Assert.That(Assign(viaDictionary, "r", deterministic: true), Is.EqualTo(1));
            var id = viaDictionary.Map["k"].Items[0].Id;
            Assert.That(viaInterface.Map["k"].Items[0].Id, Is.EqualTo(id));
            Assert.That(viaClass.Map["k"].Items[0].Id, Is.EqualTo(id));

            // SHA-256 of "<record key>|<element path>|0": slot 0, the key in braces, slot 0, the index.
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("r|r.0{k}.0/0|0"));
            Assert.That(id, Is.EqualTo(Convert.ToHexString(hash, 0, 6).ToLowerInvariant()), "the path text is unchanged");

            var held = new HReadOnlyIded { Map = new RoMap(new Dictionary<string, HHasIds> { ["k"] = new HHasIds { Items = { new EIded { Id = "0123456789ab" } } } }) };
            Assert.That(CultElementIds.Holds(typeof(HReadOnlyIded), held), Is.True);
            Assert.That(CultElementIds.Holds(typeof(HReadOnlyIded), viaInterface), Is.True);
            Assert.That(CultElementIds.Holds(typeof(HMapIded), new HMapIded { Map = { ["k"] = new HHasIds { Items = { new EIded() } } } }), Is.False);
        }

        // Registration judges the declared shape, so minting and reading follow it: a member declared object is untyped on disk,
        // and what a subclass adds under a base-typed member is not written, so neither is walked.
        [Test]
        public void TheWalkFollowsTheDeclaredShape()
        {
            var derived = new HBase { B = new DerC { L = { new EIded(), new EIded { Id = "0123456789ab" } } } };
            Assert.That(CultElementIds.Plan(typeof(HBase), derived, "r", deterministic: false).Count, Is.Zero);
            Assert.That(CultElementIds.Holds(typeof(HBase), derived), Is.False);
            var opaque = new HObject { O = new HHasIds { Items = { new EIded(), new EIded { Id = "0123456789ab" } } } };
            Assert.That(CultElementIds.Plan(typeof(HObject), opaque, "r", deterministic: false).Count, Is.Zero);
            Assert.That(CultElementIds.Holds(typeof(HObject), opaque), Is.False);
            foreach (var type in new[] { typeof(HBase), typeof(HObject) })
            {
                Assert.That(Problems(type), Is.Empty, type.Name);
                Assert.That(CultElementIds.Inspect(type).IdPath, Is.Empty, type.Name);
            }

            // A union base still writes its arm, so the arm is walked.
            var arm = new HIdBaseOwner { Root = new IdArm { Items = { new EIded() } } };
            Assert.That(Assign(arm, "r", deterministic: false), Is.EqualTo(1));
        }

        // MessagePack writes a non-public [Key] member only under AllowPrivate, so a non-public member of a type without it is nobody's
        // id business; under AllowPrivate it is written, read and minted like any other member.
        [Test]
        public void ANonPublicMemberIsWalkedOnlyUnderAllowPrivate()
        {
            foreach (var type in new[] { typeof(HHiddenList), typeof(HHiddenOwner), typeof(HHiddenFromBase) })
            {
                Assert.That(Problems(type), Is.Empty, type.Name);
                Assert.That(CultElementIds.Inspect(type).IdPath, Is.Empty, type.Name);
            }

            Assert.That(Problems(typeof(HOptHidden)), Is.Empty);
            Assert.That(CultElementIds.Inspect(typeof(HOptHidden)).IdPath, Is.EquivalentTo(new[] { typeof(HOptHidden), typeof(List<EIded>), typeof(EIded) }));
            var opt = new HOptHidden();
            opt.Peek.Add(new EIded());
            opt.Peek.Add(new EIded());
            Assert.That(Assign(opt, "r", deterministic: false), Is.EqualTo(2));
            Assert.That(opt.Peek.Select(item => item.Id), Has.All.Match("^[0-9a-f]{12}$"));

            Assert.That(Problems(typeof(HHiddenLeaf)), Is.Empty);
            Assert.That(CultElementIds.Inspect(typeof(HHiddenLeaf)).IdPath, Is.EquivalentTo(new[] { typeof(HHiddenLeaf), typeof(List<EIded>), typeof(EIded) }),
                "a non-public member adds nothing to the id path");
        }

        // MessagePack writes a [MessagePackObject] class that is only IEnumerable<T> by its keys, so the walk reads it by key. A
        // keyed tree node that enumerates its own children is the shape that overflowed the stack when enumeration came first.
        [Test]
        public void ATypeThatEnumeratesItselfIsWalkedByKeyAndTerminates()
        {
            var survey = CultElementIds.Inspect(typeof(HKNode));
            Assert.That(survey.Problems, Is.Empty);
            Assert.That(survey.IdPath, Is.EquivalentTo(new[] { typeof(HKNode), typeof(List<KNode>), typeof(KNode) }));

            var root = new HKNode { Nodes = { new KNode { Children = { new KNode { Children = { new KNode() } } } } } };
            Assert.That(Assign(root, "r", deterministic: true), Is.EqualTo(3));
            Assert.That(root.Nodes[0].Id, Has.Length.EqualTo(12));
            Assert.That(root.Nodes[0].Children[0].Children[0].Id, Has.Length.EqualTo(12));
            var options = GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.Options;
            var back = MessagePackSerializer.Deserialize<HKNode>(MessagePackSerializer.Serialize(root, options), options);
            Assert.That(back.Nodes[0].Id, Is.EqualTo(root.Nodes[0].Id), "MessagePack writes the node by key, ids included");
            Assert.That(back.Nodes[0].Children[0].Children[0].Id, Is.EqualTo(root.Nodes[0].Children[0].Children[0].Id));

            // An unkeyed type that enumerates itself holds no element list and ends the type walk.
            var unkeyed = CultElementIds.Inspect(typeof(HUNode));
            Assert.That(unkeyed.Problems, Is.Empty);
            Assert.That(unkeyed.IdPath, Is.Empty);

            // A keyed class that only enumerates is read by its keyed lists, so both are judged and minted.
            Assert.That(Problems(typeof(KBag)).Single(), Does.Contain("EBare"));
            var bag = new KBagIded { Items = { new EIded(), new EIded() } };
            Assert.That(Assign(bag, "r", deterministic: false), Is.EqualTo(2));
            Assert.That(CultElementIds.Holds(typeof(KBagIded), bag), Is.True);
        }

        // The walk reads the members MessagePack writes: without AllowPrivate a public field or a property with a public getter, with
        // it every member up the base chain, the most derived [MessagePackObject] declaration deciding; no [IgnoreMember].
        [Test]
        public void TheWalkReadsTheMembersMessagePackWrites()
        {
            Assert.That(Problems(typeof(PubIgn)), Is.Empty, "an ignored member is never written, so its list is nobody's business");
            Assert.That(Problems(typeof(ApIgn)), Is.Empty, "an ignored private member is not written");
            Assert.That(Problems(typeof(PgHold)), Is.Empty, "a list behind a private getter is not written without AllowPrivate");

            foreach (var type in new[] { typeof(EIded), typeof(HHiddenLeaf), typeof(PubIgn), typeof(ApIgn), typeof(PgHold), typeof(MemberMix), typeof(HOptHidden), typeof(ApDerived), typeof(NoApDerived), typeof(ApInheritor), typeof(ApIgnAp), typeof(PSkH), typeof(ISkH) })
            {
                var allowPrivate = type.GetCustomAttribute<MessagePackObjectAttribute>(true)!.AllowPrivate;
                Assert.That(CultElementIds.WrittenKeyedMembers(type).Select(member => member.Name),
                    Is.EquivalentTo(MessagePackMembers(type, allowPrivate)), type.Name);
            }

            Assert.That(CultElementIds.WrittenKeyedMembers(typeof(NoApDerived)).Select(member => member.Name), Is.EquivalentTo(new[] { "N" }),
                "the derived declaration decides, so an AllowPrivate base adds nothing");
        }

        // The names of the members MessagePack's own discovery reads for a type, by reflection into its internal type: the
        // oracle the walk's member set is pinned to.
        private static List<string> MessagePackMembers(Type type, bool allowPrivate)
        {
            const string moved = "MessagePack moved its member discovery; re-derive MembersOf";
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                                                       System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var info = typeof(MessagePackSerializer).Assembly.GetType("MessagePack.Internal.ObjectSerializationInfo");
            var create = info?.GetMethod("CreateOrNull", all);
            if (create == null || create.GetParameters().Length != 4)
            {
                Assert.Fail(moved);
                return new List<string>();
            }

            var result = create.Invoke(null, new object[] { type, false, false, allowPrivate });
            var members = result?.GetType().GetProperty("Members", all)?.GetValue(result) as System.Collections.IEnumerable;
            if (members == null)
            {
                Assert.Fail(moved);
                return new List<string>();
            }

            var names = new List<string>();
            foreach (var member in members)
            {
                var readable = member.GetType().GetProperty("IsReadable", all)?.GetValue(member);
                var name = member.GetType().GetProperty("Name", all)?.GetValue(member) as string;
                if (readable is not bool || name == null)
                    Assert.Fail(moved);
                if ((bool)readable!)
                    names.Add(name!);
            }

            return names;
        }

        // An id MessagePack writes but cannot read back would be re-minted differently on every load; one it does not write
        // never reaches disk.
        [Test]
        public void AnIdMessagePackCannotReadBackIsRefused()
        {
            Assert.That(Problems(typeof(HPrivSet)).Single(), Does.Contain("setter is not public").And.Contain("Id"));
            Assert.That(Problems(typeof(HIgnId)).Single(), Does.Contain("does not write").And.Contain("Id"));
            Assert.That(Problems(typeof(HPrivateId)).Single(), Does.Contain("does not write").And.Contain("id"));
            Assert.That(Problems(typeof(HInit)), Is.Empty);
            Assert.That(Problems(typeof(HCtorId)), Is.Empty);

            foreach (var holder in new object[]
            {
                new HInit { Items = { new EInit(), new EInit() } },
                new HCtorId { Items = { new ECtorId(null), new ECtorId(null) } },
            })
            {
                Assert.That(Assign(holder, "r", deterministic: false), Is.EqualTo(2), holder.GetType().Name);
                var bytes = MessagePackSerializer.Serialize(holder.GetType(), holder, GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.Options);
                var back = MessagePackSerializer.Deserialize(holder.GetType(), bytes, GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.Options);
                Assert.That(Ids(back), Is.EqualTo(Ids(holder)), holder.GetType().Name);
                Assert.That(Ids(back), Has.All.Match("^[0-9a-f]{12}$"), holder.GetType().Name);
            }
        }

        // Every shape the walk does not own is one whole value: no problem, off the id path, never minted into and never counted.
        [Test]
        public void AShapeTheWalkDoesNotOwnIsOneWholeValue()
        {
            const string Set = "aaaaaaaaaaaa";
            EIded E(string id) => new() { Id = id };
            var holders = new (Type Type, Func<string, object> Make)[]
            {
                (typeof(Whole<ImmutableArray<EIded>>), id => new Whole<ImmutableArray<EIded>> { Value = ImmutableArray.Create(E(id)) }),
                (typeof(Whole<ImmutableArray<EIded>?>), id => new Whole<ImmutableArray<EIded>?> { Value = ImmutableArray.Create(E(id)) }),
                (typeof(Whole<ArraySegment<EIded>>), id => new Whole<ArraySegment<EIded>> { Value = new ArraySegment<EIded>(new[] { E(id) }) }),
                (typeof(Whole<Memory<EIded>>), id => new Whole<Memory<EIded>> { Value = new[] { E(id) } }),
                (typeof(Whole<ReadOnlyMemory<EIded>>), id => new Whole<ReadOnlyMemory<EIded>> { Value = new[] { E(id) } }),
                (typeof(Whole<KeyValuePair<string, List<EIded>>>), id => new Whole<KeyValuePair<string, List<EIded>>> { Value = new("k", new List<EIded> { E(id) }) }),
                (typeof(Whole<(List<EIded>, int)>), id => new Whole<(List<EIded>, int)> { Value = (new List<EIded> { E(id) }, 1) }),
                (typeof(Whole<Tuple<List<EIded>, int>>), id => new Whole<Tuple<List<EIded>, int>> { Value = Tuple.Create(new List<EIded> { E(id) }, 1) }),
                (typeof(Whole<Lazy<List<EIded>>>), id => new Whole<Lazy<List<EIded>>> { Value = new Lazy<List<EIded>>(() => new List<EIded> { E(id) }) }),
                (typeof(Whole<NkHold>), id => new Whole<NkHold> { Value = new NkHold { Items = { E(id) } } }),
                (typeof(Whole<SkHold>), id => new Whole<SkHold> { Value = new SkHold { Items = { E(id) } } }),
                (typeof(Whole<DcHold>), id => new Whole<DcHold> { Value = new DcHold { Items = { E(id) } } }),
                (typeof(Whole<KpBase>), id => new Whole<KpBase> { Value = new KpBase { Items = { E(id) } } }),
                (typeof(Whole<KpDerived>), id => new Whole<KpDerived> { Value = new KpDerived { Items = { E(id) } } }),
                (typeof(HRoles), id => new HRoles { Items = { E(id) } }),
                (typeof(HFormattedList), id => new HFormattedList { Items = { E(id) } }),
                (typeof(HHiddenList), id => new HHiddenList()),
            };
            foreach (var (type, make) in holders)
            {
                Assert.That(Problems(type), Is.Empty, type.ToString());
                Assert.That(CultElementIds.Inspect(type).IdPath, Is.Empty, type.ToString());
                Assert.That(CultElementIds.CanHoldIds(type), Is.False, type.ToString());
                Assert.That(Assign(make(string.Empty), "r", deterministic: false), Is.EqualTo(0), type.ToString());
                Assert.That(Assign(make(string.Empty), "r", deterministic: true), Is.EqualTo(0), type.ToString());
                Assert.That(CultElementIds.Holds(type, make(Set)), Is.False, type.ToString());
            }

            // A [DataContract] subclass of a [MessagePackObject] type is still read by key: the attribute it inherits keeps it an owner.
            Assert.That(CultElementIds.Inspect(typeof(Whole<DcOverMp>)).IdPath, Does.Contain(typeof(List<EIded>)));

            // Elements of a string-keyed type carry no id either, and an owned list of a bare element is still refused.
            Assert.That(Problems(typeof(Whole<List<SkElem>>)), Is.Empty);
            RefusesBare(typeof(Whole<List<EBare>>));
        }

        // A string key counts only on a member MessagePack writes: a private or [IgnoreMember] one leaves the type integer-keyed and owned.
        [Test]
        public void AStringKeyOnAMemberMessagePackDoesNotWriteLeavesTheTypeOwned()
        {
            foreach (var holder in new object[] { new PSkH { Items = { new EIded(), new EIded() } }, new ISkH { Items = { new EIded(), new EIded() } } })
            {
                Assert.That(Problems(holder.GetType()), Is.Empty, holder.GetType().Name);
                Assert.That(CultElementIds.Inspect(holder.GetType()).IdPath, Does.Contain(holder.GetType()), holder.GetType().Name);
                Assert.That(Assign(holder, "r", deterministic: false), Is.EqualTo(2), holder.GetType().Name);
                Assert.That(CultElementIds.Holds(holder.GetType(), holder), Is.True, holder.GetType().Name);
            }

            Assert.That(CultElementIds.WalksAsOwner(typeof(SkOver)), Is.False, "a string key inherited by an override still makes the type one whole value");
            Assert.That(CultElementIds.WalksAsOwner(typeof(PSkH)), Is.True);
            Assert.That(Problems(typeof(Whole<List<NoIdPlain>>)).Single(), Does.Contain("[CultElementId]"));
            Assert.That(Problems(typeof(Whole<List<NoIdEl>>)).Single(), Does.Contain("[CultElementId]"), "an element whose only string key is ignored is as bare as its twin");
        }

        // The AllowPrivate edges: an [IgnoreMember] member is not written even under it, and only the element's own most derived
        // declaration lets a private setter be read back.
        [Test]
        public void TheAllowPrivateEdgesFollowWhatMessagePackWrites()
        {
            Assert.That(CultElementIds.WrittenKeyedMembers(typeof(ApIgnAp)), Is.Empty, "an ignored member is not written under AllowPrivate");
            Assert.That(Problems(typeof(HApIgnId)).Single(), Does.Contain("does not write").And.Contain("Id"));
            Assert.That(Problems(typeof(HNoApPrivSet)).Single(), Does.Contain("setter is not public").And.Contain("Id"),
                "the derived plain [MessagePackObject] decides: its private setter is not read back, whatever its base declares");
        }

        // MessagePack writes an AllowPrivate type with every member up its base chain, and the most derived [MessagePackObject]
        // declaration decides whether that is so.
        [Test]
        public void AnAllowPrivateTypeIsWalkedByTheMembersMessagePackWrites()
        {
            Assert.That(CultElementIds.WrittenKeyedMembers(typeof(ApDerived)).Select(member => member.Name), Is.EquivalentTo(new[] { "baseStash", "own", "N", "Pg" }));
            Assert.That(CultElementIds.WrittenKeyedMembers(typeof(NoApDerived)).Select(member => member.Name), Is.EquivalentTo(new[] { "N" }));
            Assert.That(CultElementIds.WrittenKeyedMembers(typeof(ApInheritor)).Select(member => member.Name), Is.EquivalentTo(new[] { "baseStash", "own", "N", "Pg" }),
                "AllowPrivate is inherited by a subclass that declares no attribute of its own");

            var options = GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.Options;
            var held = new HOptHidden();
            held.Peek.Add(new EIded());
            Assert.That(Assign(held, "r", deterministic: false), Is.EqualTo(1));
            var back = MessagePackSerializer.Deserialize<HOptHidden>(MessagePackSerializer.Serialize(held, options), options);
            Assert.That(back.Peek.Single().Id, Is.EqualTo(held.Peek.Single().Id), "the minted id of a private list survives a round trip");

            Assert.That(Problems(typeof(HApPrivSet)), Is.Empty, "MessagePack reads an AllowPrivate type's private setter back");
            var privSet = new HApPrivSet { Items = { new EApPrivSet(), new EApPrivSet() } };
            Assert.That(Assign(privSet, "r", deterministic: false), Is.EqualTo(2));
            var round = MessagePackSerializer.Deserialize<HApPrivSet>(MessagePackSerializer.Serialize(privSet, options), options);
            Assert.That(round.Items.Select(item => item.Id), Is.EqualTo(privSet.Items.Select(item => item.Id)));
            Assert.That(round.Items.Select(item => item.Id), Has.All.Match("^[0-9a-f]{12}$"));
            Assert.That(Problems(typeof(HPrivSet)).Single(), Does.Contain("setter is not public"), "without AllowPrivate it is still refused");
        }

        private static string?[] Ids(object holder) => holder switch
        {
            HInit init => init.Items.Select(item => item.Id).ToArray(),
            HCtorId ctor => ctor.Items.Select(item => item.Id).ToArray(),
            _ => throw new ArgumentException(holder.GetType().Name),
        };

        // A subclass that redeclares the id member does not change which member the declared element writes.
        [Test]
        public void AnElementIsReadByItsDeclaredTypeNotItsRuntimeType()
        {
            Assert.That(Problems(typeof(HDoc2)), Is.Empty);
            static HE Hd(string? declared = null, string? redeclared = null)
            {
                var item = new HD { Id = redeclared };
                ((HE)item).Id = declared;
                return item;
            }

            var minted = new HDoc2 { Items = { Hd() } };
            Assert.That(Assign(minted, "r", deterministic: false), Is.EqualTo(1));
            Assert.That(minted.Items[0].Id, Does.Match("^[0-9a-f]{12}$"));
            Assert.That(((HD)minted.Items[0]).Id, Is.Null, "the redeclared member is never written, so it is not minted");

            Assert.That(() => CultElementIds.Plan(typeof(HDoc2), new HDoc2 { Items = { Hd(declared: "not-hex") } }, "r", false),
                Throws.TypeOf<CultElementIdException>(), "the declared member is checked");
            Assert.That(() => CultElementIds.Plan(typeof(HDoc2), new HDoc2 { Items = { Hd(redeclared: "not-hex") } }, "r", false),
                Throws.Nothing, "the redeclared member is never read");

            Assert.That(CultElementIds.Holds(typeof(HDoc2), new HDoc2 { Items = { Hd(declared: "0123456789ab") } }), Is.True);
            Assert.That(CultElementIds.Holds(typeof(HDoc2), new HDoc2 { Items = { Hd(redeclared: "0123456789ab") } }), Is.False);
        }

        // ---- shapes ----
        [MessagePackObject] public class PubIgn { [Key(0)] [IgnoreMember] public List<EBare> L { get; set; } = new(); }
#pragma warning disable CS0169, CS0414, CS0649
        [MessagePackObject] public class ApIgn { [Key(0)] [IgnoreMember] private List<EIded> l = new(); }
#pragma warning restore CS0169, CS0414, CS0649
        [MessagePackObject] public class PgHold { [Key(0)] public List<EIded> Items { private get; set; } = new(); }
        [MessagePackObject]
        public class MemberMix
        {
            [Key(0)] public int A;
            [Key(1)] [IgnoreMember] public List<EBare>? Ign;
            [Key(2)] public string? PS { get; private set; }
            [Key(3)] public string? GetOnly { get; }
#pragma warning disable CS0169, CS0414, CS0649
            [Key(4)] private int priv;
#pragma warning restore CS0169, CS0414, CS0649
            [Key(5)] public string? InitOnly { get; init; }
            [IgnoreMember] public int Priv => priv;
        }

        [MessagePackObject] public class EPrivSet { [Key(0)] [CultElementId] public string? Id { get; private set; } }
        [MessagePackObject] public class HPrivSet { [Key(0)] public List<EPrivSet> Items { get; set; } = new(); }
        [MessagePackObject] public class EIgnId { [Key(0)] [IgnoreMember] [CultElementId] public string? Id { get; set; } }
        [MessagePackObject] public class HIgnId { [Key(0)] public List<EIgnId> Items { get; set; } = new(); }
        #pragma warning disable CS0169, CS0649
        [MessagePackObject] public class EPrivateId { [Key(0)] [CultElementId] private string? id; }
#pragma warning restore CS0169, CS0649
        [MessagePackObject] public class HPrivateId { [Key(0)] public List<EPrivateId> Items { get; set; } = new(); }
        [MessagePackObject] public class EInit { [Key(0)] [CultElementId] public string? Id { get; init; } }
        [MessagePackObject] public class HInit { [Key(0)] public List<EInit> Items { get; set; } = new(); }
        [MessagePackObject]
        public class ECtorId
        {
            [SerializationConstructor] public ECtorId(string? id) { Id = id; }
            [Key(0)] [CultElementId] public string? Id { get; set; }
        }
        [MessagePackObject] public class HCtorId { [Key(0)] public List<ECtorId> Items { get; set; } = new(); }

        [MessagePackObject] public class HE { [Key(0)] [CultElementId] public string? Id { get; set; } }
        [MessagePackObject] public class HD : HE { [Key(2)] [CultElementId] public new string? Id { get; set; } }
        [MessagePackObject] public class HDoc2 { [Key(0)] public List<HE> Items { get; set; } = new(); }

        [MessagePackObject]
        public class KNode : IEnumerable<KNode>
        {
            [Key(0)] [CultElementId] public string? Id { get; set; }
            [Key(1)] public List<KNode> Children { get; set; } = new();
            public IEnumerator<KNode> GetEnumerator() => Children.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        [MessagePackObject] public class HKNode { [Key(0)] public List<KNode> Nodes { get; set; } = new(); }
        public class UNode : IEnumerable<UNode>
        {
            public IEnumerator<UNode> GetEnumerator() { yield break; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        [MessagePackObject] public class HUNode { [Key(0)] public UNode? N { get; set; } }
        [MessagePackObject]
        public class KBag : IEnumerable<EIded>
        {
            [Key(0)] public List<EIded> Items { get; set; } = new();
            [Key(1)] public List<EBare> Other { get; set; } = new();
            public IEnumerator<EIded> GetEnumerator() { yield break; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        [MessagePackObject]
        public class KBagIded : IEnumerable<EIded>
        {
            [Key(0)] public List<EIded> Items { get; set; } = new();
            public IEnumerator<EIded> GetEnumerator() { yield break; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [MessagePackObject] public class EBare { [Key(0)] public string X { get; set; } = ""; }
        [MessagePackObject] public class EIded { [Key(0)] public string X { get; set; } = ""; [Key(1)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class ENest { [Key(0)] [CultElementId] public string Id { get; set; } = ""; [Key(1)] public List<EIded> Sub { get; set; } = new(); }
        [MessagePackObject] public class EBareNest { [Key(0)] [CultElementId] public string Id { get; set; } = ""; [Key(1)] public List<EBare> Sub { get; set; } = new(); }

        public sealed class NoFormatter { }
        public class UnkeyedArm : ArmBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [Union(0, typeof(KeyedArm)), Union(1, typeof(UnkeyedArm))] public abstract class ArmBase { }
        [MessagePackObject] public class KeyedArm : ArmBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HUnkeyedArmList { [Key(0)] public List<ArmBase> Items { get; set; } = new(); }
        [MessagePackObject] public class HUnkeyedArmField { [Key(0)] public ArmBase? Root { get; set; } }
        [MessagePackObject]
        public class HCollections
        {
            [Key(0)] public List<EIded> A { get; set; } = new();
            [Key(1)] public EIded[] B { get; set; } = Array.Empty<EIded>();
            [Key(2)] public Dictionary<string, HHasIds> C { get; set; } = new();
            [Key(3)] public List<List<EIded>> D { get; set; } = new();
        }

        [MessagePackObject]
        public class HValueListsBesideObjects
        {
            [Key(0)] public List<EIded> Objects { get; set; } = new();
            [Key(1)] public List<int> Numbers { get; set; } = new();
            [Key(2)] public List<PlainValue> Plain { get; set; } = new();
            [Key(3)] public List<Pair> Points { get; set; } = new();
        }

        [MessagePackFormatter(typeof(NoFormatter))] public class Roles : List<EIded> { }
        [MessagePackObject] public class HRoles { [Key(0)] public Roles Items { get; set; } = new(); }
        [MessagePackFormatter(typeof(NoFormatter))] public class PlainValues : List<PlainValue> { }
        [MessagePackObject] public class HFormattedValueCollection { [Key(0)] public List<EIded> Objects { get; set; } = new(); [Key(1)] public PlainValues Plain { get; set; } = new(); }

        [MessagePackObject]
        public abstract class FormattedIdBase
        {
            [Key(0)] [MessagePackFormatter(typeof(NoFormatter))] public virtual string Id { get; set; } = "";
        }

        [MessagePackObject]
        public class FormattedIdOverride : FormattedIdBase
        {
            [Key(0)] [CultElementId] public override string Id { get; set; } = "";
        }

        [MessagePackObject] public class HOverFormattedId { [Key(0)] public List<FormattedIdOverride> Items { get; set; } = new(); }

        [MessagePackObject]
        public class OwnerBaseFormatted
        {
            [Key(0)] [MessagePackFormatter(typeof(NoFormatter))] public virtual List<EIded> Items { get; set; } = new();
        }

        [MessagePackObject]
        public class OwnerOverFormatted : OwnerBaseFormatted
        {
            [Key(0)] public override List<EIded> Items { get; set; } = new();
        }

        [MessagePackObject]
        public class IdVirtual
        {
            [Key(0)] public string X { get; set; } = "";
            [Key(1)] [CultElementId] public virtual string Id { get; set; } = "";
        }

        [MessagePackObject] public class IdOverride : IdVirtual { [Key(1)] public override string Id { get; set; } = ""; }
        [MessagePackObject] public class HIdOverride { [Key(0)] public List<IdOverride> Items { get; set; } = new(); }

        [MessagePackObject] public class HUnkeyedTwoLists { [Key(0)] public List<ArmBase> A { get; set; } = new(); [Key(1)] public List<ArmBase> B { get; set; } = new(); }
        public class PlainValue { public string X { get; set; } = ""; }
        [MessagePackObject] public class HValueList { [Key(0)] public List<PlainValue> Items { get; set; } = new(); }

        [MessagePackObject] [MessagePackFormatter(typeof(NoFormatter))] public class FormattedElement { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HFormattedElement { [Key(0)] public List<FormattedElement> Items { get; set; } = new(); }
        [MessagePackObject] public class HFormattedList { [Key(0)] [MessagePackFormatter(typeof(NoFormatter))] public List<EIded> Items { get; set; } = new(); }
        [MessagePackObject] public class FormattedIdElement { [Key(0)] [CultElementId] [MessagePackFormatter(typeof(NoFormatter))] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HFormattedId { [Key(0)] public List<FormattedIdElement> Items { get; set; } = new(); }
        [MessagePackObject] [MessagePackFormatter(typeof(NoFormatter))] public class FormattedOwner { [Key(0)] public List<EIded> Items { get; set; } = new(); }
        [MessagePackObject] public class HFormattedOwner { [Key(0)] public FormattedOwner Owner { get; set; } = new(); }
        [MessagePackObject] public class HFormattedMember { [Key(0)] [MessagePackFormatter(typeof(NoFormatter))] public HHasIds Inner { get; set; } = new(); }
        [MessagePackObject] [MessagePackFormatter(typeof(NoFormatter))] public class FormattedLeaf { [Key(0)] public int N { get; set; } }
        [MessagePackObject] public class HFormattedField { [Key(0)] public FormattedLeaf Leaf { get; set; } = new(); [Key(1)] public List<string> Tags { get; set; } = new(); }

        [MessagePackObject] public class FormattedDerived : FormattedElement { }
        [MessagePackObject] public class HFormattedDerived { [Key(0)] public List<FormattedDerived> Items { get; set; } = new(); }
        [Union(0, typeof(IdArm))] public abstract class IdBase { }
        [MessagePackObject] public class IdArm : IdBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; [Key(1)] public List<EIded> Items { get; set; } = new(); }
        [Union(0, typeof(PlainArm))] public abstract class PlainBase { }
        [MessagePackObject] public class PlainArm : PlainBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HPlainBaseList { [Key(0)] public List<PlainBase> Items { get; set; } = new(); }
        [MessagePackObject] public class HIdBaseOwner { [Key(0)] public IdBase? Root { get; set; } }

        [MessagePackObject] public class HLeft { [Key(0)] public HCycle Cycle { get; set; } = new(); }
        [MessagePackObject] public class HRight { [Key(0)] public HCycle Cycle { get; set; } = new(); }
        [MessagePackObject] public class HCycle { [Key(0)] public HCycleBack Back { get; set; } = new(); }
        [MessagePackObject] public class HCycleBack { [Key(0)] public HCycle? Up { get; set; } [Key(1)] public List<EIded> Items { get; set; } = new(); }
        [MessagePackObject] public class HDiamond { [Key(0)] public HLeft Left { get; set; } = new(); [Key(1)] public HRight Right { get; set; } = new(); }

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

        [MessagePackObject] public struct SHold { [Key(0)] public List<EBare> Items; }
        [MessagePackObject] public struct SHoldIded { [Key(0)] public List<EIded> Items; }
        [MessagePackObject] public struct SValue { [Key(0)] public int N; }
        public struct UnkeyedS { [Key(0)] public List<EBare> Items; }
        [MessagePackObject] public class HUnkeyedStruct { [Key(0)] public UnkeyedS Value; }
        public class InheritedBare : EBare { }
        [MessagePackObject] public class HInheritedBare { [Key(0)] public List<InheritedBare> Items { get; set; } = new(); }
        [MessagePackObject] public class HNullableStructIded { [Key(0)] public SHoldIded? Hold; }
        [MessagePackObject] public class HStruct { [Key(0)] public SHold Hold; }
        [MessagePackObject] public class HStructList { [Key(0)] public List<SHold> Holds { get; set; } = new(); }
        [MessagePackObject] public class HNullableStruct { [Key(0)] public SHold? Hold; }
        [MessagePackObject] public class HStructIded { [Key(0)] public SHoldIded Hold; [Key(1)] public List<SHoldIded> Many = new(); }
        [MessagePackObject] public class HStructValues { [Key(0)] public List<SValue> Structs = new(); [Key(1)] public List<EIded> Objects = new(); }

        [MessagePackObject] public class HReadOnlyMap { [Key(0)] public IReadOnlyDictionary<string, HList> Map { get; set; } = new Dictionary<string, HList>(); }
        [MessagePackObject] public class HReadOnlyIded { [Key(0)] public IReadOnlyDictionary<string, HHasIds> Map { get; set; } = new Dictionary<string, HHasIds>(); }
        [MessagePackObject] public class HMapIded { [Key(0)] public Dictionary<string, HHasIds> Map { get; set; } = new(); }
        public sealed class RoMap : IReadOnlyDictionary<string, HHasIds>
        {
            private readonly Dictionary<string, HHasIds> _inner;
            public RoMap(Dictionary<string, HHasIds> inner) => _inner = inner;
            public HHasIds this[string key] => _inner[key];
            public IEnumerable<string> Keys => _inner.Keys;
            public IEnumerable<HHasIds> Values => _inner.Values;
            public int Count => _inner.Count;
            public bool ContainsKey(string key) => _inner.ContainsKey(key);
            public bool TryGetValue(string key, out HHasIds value) => _inner.TryGetValue(key, out value!);
            public IEnumerator<KeyValuePair<string, HHasIds>> GetEnumerator() => _inner.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [MessagePackObject] public class BaseC { [Key(0)] public int X { get; set; } }
        [MessagePackObject] public class DerC : BaseC { [Key(1)] public List<EIded> L { get; set; } = new(); }
        [MessagePackObject] public class HBase { [Key(0)] public BaseC B { get; set; } = new(); }
        [MessagePackObject] public class HObject { [Key(0)] public object? O { get; set; } }

#pragma warning disable CS0169, CS0414, CS0649
        [MessagePackObject] public class HHiddenList { [Key(0)] private List<EIded> Stash { get; set; } = new(); }
        [MessagePackObject] public class HHiddenOwner { [Key(0)] private HHasIds stash = new(); }
        [MessagePackObject(AllowPrivate = true)] public class HOptHidden { [Key(0)] private List<EIded> stash = new(); [IgnoreMember] public List<EIded> Peek => stash; }
        [MessagePackObject] public class HHiddenBase { [Key(0)] private List<EIded> stash = new(); }
        [MessagePackObject] public class HHiddenFromBase : HHiddenBase { [Key(1)] public int N { get; set; } }
        [MessagePackObject] public class HHiddenLeaf { [Key(0)] public List<EIded> Items { get; set; } = new(); [Key(1)] private int secret = 7; }
        [MessagePackObject] public class ApBase { [Key(0)] private List<EIded> baseStash = new(); }
        [MessagePackObject(AllowPrivate = true)] public class ApDerived : ApBase { [Key(1)] private List<EIded> own = new(); [Key(2)] public int N { get; set; } [Key(3)] public List<EIded> Pg { private get; set; } = new(); }
        [MessagePackObject(AllowPrivate = true)] public class ApBase2 { [Key(0)] private List<EIded> baseStash = new(); }
        [MessagePackObject] public class NoApDerived : ApBase2 { [Key(1)] public int N { get; set; } }
#pragma warning restore CS0169, CS0414, CS0649

        [MessagePackObject] public class Whole<T> { [Key(0)] public T? Value { get; set; } }
        public class NkHold { public List<EIded> Items = new(); }
        [MessagePackObject] public class SkHold { [Key("items")] public List<EIded> Items = new(); }
        [MessagePackObject(keyAsPropertyName: true)] public class KpBase { [Key(0)] public List<EIded> Items = new(); }
        public class KpDerived : KpBase { }
        [MessagePackObject] public class MpBase { [Key(0)] public List<EIded> Items = new(); }
        [System.Runtime.Serialization.DataContract] public class DcOverMp : MpBase { }
        public class ApInheritor : ApDerived { }
        [MessagePackObject] public class SkElem { [Key("x")] public string X { get; set; } = ""; }
        [System.Runtime.Serialization.DataContract] public class DcHold { [System.Runtime.Serialization.DataMember(Order = 0)] public List<EIded> Items = new(); }
        [MessagePackObject(AllowPrivate = true)] public class ApIgnAp { [Key(0)] [IgnoreMember] private List<EIded> l = new(); }
        [MessagePackObject] public class PSkH { [Key(0)] public List<EIded> Items = new(); [Key("x")] private int x = 1; }
        [MessagePackObject] public class ISkH { [Key(0)] public List<EIded> Items = new(); [IgnoreMember] [Key("x")] public int X { get; set; } }
        [MessagePackObject] public class SkOverBase { [Key("items")] public virtual List<EIded> Items { get; set; } = new(); }
        [MessagePackObject] public class SkOver : SkOverBase { public override List<EIded> Items { get; set; } = new(); }
        [MessagePackObject] public class NoIdEl { [Key(0)] public int N { get; set; } [IgnoreMember] [Key("x")] public int X { get; set; } }
        [MessagePackObject] public class NoIdPlain { [Key(0)] public int N { get; set; } }
        [MessagePackObject(AllowPrivate = true)] public class EApIgnId { [Key(0)] public int N { get; set; } [Key(1)] [IgnoreMember] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class HApIgnId { [Key(0)] public List<EApIgnId> Items { get; set; } = new(); }
        [MessagePackObject(AllowPrivate = true)] public class ApIdBase { [Key(0)] public int N { get; set; } }
        [MessagePackObject] public class ENoApPrivSet : ApIdBase { [Key(1)] [CultElementId] public string? Id { get; private set; } }
        [MessagePackObject] public class HNoApPrivSet { [Key(0)] public List<ENoApPrivSet> Items { get; set; } = new(); }
        [MessagePackObject(AllowPrivate = true)] public class EApPrivSet { [Key(0)] [CultElementId] public string? Id { get; private set; } }
        [MessagePackObject] public class HApPrivSet { [Key(0)] public List<EApPrivSet> Items { get; set; } = new(); }

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
