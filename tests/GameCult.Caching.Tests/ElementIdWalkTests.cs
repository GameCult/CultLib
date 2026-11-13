#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The id walk's own contracts, one mutant-sized fact at a time: the order of the id path, a union that names itself, a list of
    // values never counting as an id, and the attribute predicates that decide whether a type is walked or written whole.
    public class ElementIdWalkTests
    {
        // The codec asks MessagePack about the id path in order and refuses at the first type it cannot allow, so the order is what
        // an error names first: the document, then the collection types from the outermost in, then the element and its arms.
        [Test]
        public void TheIdPathListsCollectionsFromTheOutermostIn()
        {
            Assert.That(CultElementIds.Inspect(typeof(MsMapMap)).IdPath, Is.EqualTo(new[]
            {
                typeof(MsMapMap), typeof(Dictionary<string, Dictionary<string, List<ElementIdEngineTests.EIded>>>),
                typeof(Dictionary<string, List<ElementIdEngineTests.EIded>>), typeof(List<ElementIdEngineTests.EIded>), typeof(ElementIdEngineTests.EIded)
            }), "maps nested in maps");
            Assert.That(CultElementIds.Inspect(typeof(MsListDeep)).IdPath, Is.EqualTo(new[]
            {
                typeof(MsListDeep), typeof(List<List<List<ElementIdEngineTests.EIded>>>), typeof(List<List<ElementIdEngineTests.EIded>>),
                typeof(List<ElementIdEngineTests.EIded>), typeof(ElementIdEngineTests.EIded)
            }), "lists nested in lists");
        }

        // ConcreteTypes stops at a type it has already expanded, so a union that lists its own base is finished, not a stack overflow.
        [Test]
        public void AUnionThatNamesItselfIsWalkedOnce()
        {
            var survey = CultElementIds.Inspect(typeof(MsSelfHolder));
            Assert.That(survey.Problems, Is.Empty);
            Assert.That(survey.IdPath, Is.EquivalentTo(new[] { typeof(MsSelfHolder), typeof(List<MsSelfBase>), typeof(MsSelfBase), typeof(MsSelfArm) }));
        }

        // Value lists hold no ids however they are nested, and a root that is itself a value list holds none either.
        [Test]
        public void ListsOfValuesHoldNoIds()
        {
            var values = new MsValues { Numbers = { 1 }, Tags = { "a" }, Grid = { new List<int> { 1 } }, Map = { ["k"] = new List<int> { 2 } } };
            Assert.That(CultElementIds.Holds(typeof(MsValues), values), Is.False);
            Assert.That(CultElementIds.Holds(typeof(List<int>), new List<int> { 1, 2 }), Is.False);
            Assert.That(CultElementIds.Holds(typeof(List<List<int>>), new List<List<int>> { new() { 1 } }), Is.False);
            Assert.That(CultElementIds.Holds(typeof(List<string>), new List<string> { "a" }), Is.False);
            Assert.That(CultElementIds.Holds(typeof(MsBoom), new MsBoom()), Is.False, "a sequence of values is not enumerated");
        }

        // A nullable struct is walked as the struct it wraps; a nullable value is not walked at all.
        [Test]
        public void ANullableKeyedStructWalksAsItsOwner()
        {
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsKeyedStruct)), Is.True);
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsKeyedStruct?)), Is.True);
            Assert.That(CultElementIds.WalksAsOwner(typeof(int?)), Is.False);
            Assert.That(CultElementIds.WalksAsOwner(typeof(int)), Is.False);
        }

        // [Key] is read through an override, so an element whose id property overrides a keyed base member is still an id with a key.
        [Test]
        public void AKeyOnTheBaseOfAnOverrideCountsForTheOverride()
        {
            Assert.That(CultElementIds.Inspect(typeof(MsOverrideHolder)).Problems, Is.Empty);
            Assert.That(CultElementIds.Inspect(typeof(MsOverrideHolder)).IdPath, Does.Contain(typeof(MsOverrideEl)));
        }

        // A type is walked by key when it is [MessagePackObject] or [Union], read through its bases for the former; it is one whole
        // value when it declares its own formatter, is [DataContract] without being [MessagePackObject], or is keyed by name.
        [Test]
        public void KeyedTypesWalkAndWholeTypesDoNot()
        {
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsMpBase)), Is.True, "[MessagePackObject]");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsUBase)), Is.True, "[Union]");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsMpDerived)), Is.True, "[MessagePackObject] inherited");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsPlain)), Is.False, "[Key] members without [MessagePackObject] or [Union]");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsUArm)), Is.True, "an arm that is itself [MessagePackObject]");

            Assert.That(CultElementIds.WalksAsOwner(typeof(MsDc)), Is.False, "[DataContract] alone");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsDcMp)), Is.True, "[DataContract] beside [MessagePackObject]");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsDcOverMpBase)), Is.True, "[DataContract] over an inherited [MessagePackObject]");

            Assert.That(CultElementIds.WalksAsOwner(typeof(MsPropName)), Is.False, "KeyAsPropertyName");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsPropNameDerived)), Is.False, "KeyAsPropertyName inherited");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsStringKey)), Is.False, "a string [Key]");

            Assert.That(CultElementIds.WalksAsOwner(typeof(MsFormattedBase)), Is.False, "[MessagePackFormatter]");
            Assert.That(CultElementIds.WalksAsOwner(typeof(MsFormattedDerived)), Is.False, "[MessagePackFormatter] inherited");
        }

        [MessagePackObject] public class MsMapMap { [Key(0)] public Dictionary<string, Dictionary<string, List<ElementIdEngineTests.EIded>>> M { get; set; } = new(); }
        [MessagePackObject] public class MsListDeep { [Key(0)] public List<List<List<ElementIdEngineTests.EIded>>> G { get; set; } = new(); }

        [Union(0, typeof(MsSelfBase)), Union(1, typeof(MsSelfArm))] public abstract class MsSelfBase { }
        [MessagePackObject] public class MsSelfArm : MsSelfBase { [Key(0)] [CultElementId] public string Id { get; set; } = ""; }
        [MessagePackObject] public class MsSelfHolder { [Key(0)] public List<MsSelfBase> Items { get; set; } = new(); }

        [MessagePackObject]
        public class MsValues
        {
            [Key(0)] public List<int> Numbers { get; set; } = new();
            [Key(1)] public List<string> Tags { get; set; } = new();
            [Key(2)] public List<List<int>> Grid { get; set; } = new();
            [Key(3)] public Dictionary<string, List<int>> Map { get; set; } = new();
        }

        public sealed class MsBoom : IEnumerable<int>
        {
            public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("enumerated");
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [MessagePackObject] public struct MsKeyedStruct { [Key(0)] public int X; }

        [MessagePackObject]
        public class MsOverrideBase
        {
            [Key(0)] [CultElementId] public virtual string Id { get; set; } = "";
        }

        [MessagePackObject]
        public class MsOverrideEl : MsOverrideBase
        {
            public override string Id { get; set; } = "";
        }

        [MessagePackObject] public class MsOverrideHolder { [Key(0)] public List<MsOverrideEl> Items { get; set; } = new(); }

        [MessagePackObject] public class MsMpBase { [Key(0)] public int A { get; set; } }
        public class MsMpDerived : MsMpBase { }
        public class MsPlain { [Key(0)] public int A { get; set; } }
        [Union(0, typeof(MsUArm))] public abstract class MsUBase { }
        [MessagePackObject] public class MsUArm : MsUBase { [Key(0)] public int A { get; set; } }

        [DataContract] public class MsDc { [DataMember] public int A { get; set; } }
        [DataContract] [MessagePackObject] public class MsDcMp { [Key(0)] public int A { get; set; } }
        [DataContract] public class MsDcOverMpBase : MsMpBase { }

        [MessagePackObject(keyAsPropertyName: true)] public class MsPropName { public int A { get; set; } }
        public class MsPropNameDerived : MsPropName { }
        [MessagePackObject] public class MsStringKey { [Key("a")] public int A { get; set; } }

        public sealed class MsNoFormatter { }
        [MessagePackFormatter(typeof(MsNoFormatter))] [MessagePackObject] public class MsFormattedBase { [Key(0)] public int A { get; set; } }
        [MessagePackObject] public class MsFormattedDerived : MsFormattedBase { [Key(1)] public int B { get; set; } }
    }
}
