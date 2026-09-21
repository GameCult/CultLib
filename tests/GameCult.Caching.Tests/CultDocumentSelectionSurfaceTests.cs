#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // CultNet typed selection, Cut 1, commit 0: the cache's public read surface over declared members
    // (docs/cultnet-selection-cut.md, section 6). These tests are independent of GameCult.Networking -
    // the evaluator that will consume this surface does not exist yet in this commit.
    public sealed class CultDocumentSelectionSurfaceTests
    {
        // S19: a dictionary reference is enumerated as edges carrying its values, not merely its keys.
        [Test]
        public void ReferencesOfEnumeratesADictionaryReferenceAsEdgesCarryingItsValues()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA), typeof(FixtureLeafB), typeof(FixtureCiter) });
            var citer = registry.GetRequired<FixtureCiter>();
            var a1 = new CultRecordRef<FixtureLeafA>(new CultRecordKey("leaf-a-1"));
            var a2 = new CultRecordRef<FixtureLeafA>(new CultRecordKey("leaf-a-2"));
            var document = new FixtureCiter
            {
                Components = new Dictionary<CultRecordRef<FixtureLeafA>, float> { [a1] = 1.5f, [a2] = 2.5f }
            };

            var edges = citer.ReferencesOf(document, "Components").ToArray();

            Assert.That(edges.Select(edge => edge.Target.Value).OrderBy(v => v, StringComparer.Ordinal),
                Is.EqualTo(new[] { "leaf-a-1", "leaf-a-2" }));
            Assert.That(edges.Single(edge => edge.Target.Value == "leaf-a-1").Payload, Is.EqualTo(1.5f));
            Assert.That(edges.Single(edge => edge.Target.Value == "leaf-a-2").Payload, Is.EqualTo(2.5f));
            // Two entries with different payload bytes: a shape-only assertion (keys enumerated, values
            // dropped) would pass without this distinguishing the payloads (spec section 10, S19).
            Assert.That(edges.Select(edge => edge.Payload).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void ReferencesOfEnumeratesAManyListReferenceWithNoPayload()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA), typeof(FixtureLeafB), typeof(FixtureCiter) });
            var citer = registry.GetRequired<FixtureCiter>();
            var document = new FixtureCiter
            {
                Related = new List<CultRecordRef<FixtureLeafA>>
                {
                    new(new CultRecordKey("leaf-a-3")),
                    new(new CultRecordKey("leaf-a-4"))
                }
            };

            var edges = citer.ReferencesOf(document, "Related").ToArray();

            Assert.That(edges.Select(edge => edge.Target.Value), Is.EqualTo(new[] { "leaf-a-3", "leaf-a-4" }));
            Assert.That(edges.All(edge => edge.Payload == null), Is.True);
        }

        [Test]
        public void ReferencesOfEnumeratesASingleReferenceToAnAbstractTarget()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA), typeof(FixtureLeafB), typeof(FixtureCiter) });
            var citer = registry.GetRequired<FixtureCiter>();
            var document = new FixtureCiter { Design = new CultRecordRef<FixtureMiddle>(new CultRecordKey("leaf-b-1")) };

            var edges = citer.ReferencesOf(document, "Design").ToArray();

            Assert.That(edges.Single().Target.Value, Is.EqualTo("leaf-b-1"));
            Assert.That(edges.Single().Payload, Is.Null);
        }

        // D9: the abstract middle carries no [CultDocument] and no catalog entry (TargetSchemaName is
        // null for exactly this reference), so the leaf set must be resolved by assignability against
        // the live registry, not read off a persisted name.
        [Test]
        public void ResolveTargetLeavesFindsBothLeavesUnderAnAbstractMiddleWithNoSchemaOfItsOwn()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA), typeof(FixtureLeafB), typeof(FixtureCiter) });
            var citer = registry.GetRequired<FixtureCiter>();
            var designMember = citer.DeclaredMembers.Single(member => member.MemberName == nameof(FixtureCiter.Design));

            Assert.That(designMember.TargetType, Is.EqualTo(typeof(FixtureMiddle)));
            Assert.That(typeof(FixtureMiddle).GetCustomAttribute<CultDocumentAttribute>(), Is.Null);

            var leaves = registry.ResolveTargetLeaves(designMember.TargetType!);

            Assert.That(leaves.Select(d => d.DocumentType).OrderBy(t => t.Name),
                Is.EqualTo(new[] { typeof(FixtureLeafA), typeof(FixtureLeafB) }.OrderBy(t => t.Name)));
        }

        [Test]
        public void ResolveTargetLeavesSeesALeafRegisteredAfterAnEarlierDescriptorWasBuilt()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureCiter), typeof(FixtureLeafA) });
            var citer = registry.GetRequired<FixtureCiter>();
            var designMember = citer.DeclaredMembers.Single(member => member.MemberName == nameof(FixtureCiter.Design));

            var before = registry.ResolveTargetLeaves(designMember.TargetType!);
            Assert.That(before.Select(d => d.DocumentType), Does.Not.Contain(typeof(FixtureLeafB)));

            registry.GetRequired<FixtureLeafB>();
            var after = registry.ResolveTargetLeaves(designMember.TargetType!);
            Assert.That(after.Select(d => d.DocumentType), Does.Contain(typeof(FixtureLeafB)));
        }

        // D8's inherited-alias case, read through the public surface: mass is declared once on the
        // abstract middle and reachable on both leaves.
        [Test]
        public void TryGetIndexValueAndNumberReadAnInheritedAliasOnEachLeaf()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA), typeof(FixtureLeafB) });
            var leafA = registry.GetRequired<FixtureLeafA>();
            var documentA = new FixtureLeafA { Name = "sword", Mass = 3.5f };

            Assert.That(leafA.TryGetIndexValue(documentA, "mass", out var text), Is.True);
            Assert.That(text, Is.EqualTo("3.5"));
            Assert.That(leafA.TryGetIndexNumber(documentA, "mass", out var number), Is.True);
            Assert.That(number, Is.EqualTo(3.5d));
        }

        [Test]
        public void TryGetIndexValueRefusesAnUndeclaredAlias()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA) });
            var leafA = registry.GetRequired<FixtureLeafA>();

            Assert.That(leafA.TryGetIndexValue(new FixtureLeafA(), "not_declared", out _), Is.False);
        }

        // S22: IsNumeric is derived from the CLR set (sbyte..decimal, Nullable<T> unwrapped) and must
        // agree for a nullable numeric member and a decimal member - the two authorities D8 names.
        [Test]
        public void IsNumericAgreesForANullableNumericMemberAndADecimalMember()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafB) });
            var leafB = registry.GetRequired<FixtureLeafB>();
            var weight = leafB.DeclaredMembers.Single(member => member.MemberName == nameof(FixtureLeafB.Weight));

            Assert.That(weight.IsNumeric, Is.True);
            var document = new FixtureLeafB { Weight = 4.25m };
            Assert.That(leafB.TryGetIndexNumber(document, "weight", out var number), Is.True);
            Assert.That(number, Is.EqualTo(4.25d));
        }

        [Test]
        public void IsNumericIsFalseForAStringAlias()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(FixtureLeafA) });
            var leafA = registry.GetRequired<FixtureLeafA>();
            var name = leafA.DeclaredMembers.Single(member => member.MemberName == nameof(FixtureLeafA.Name));

            Assert.That(name.IsNumeric, Is.False);
        }

        // D10 and D11 registration refusals: emitted into a dynamic assembly, invisible to registry
        // discovery by attribute scan, exactly as DocumentShapeTests does for every other shape refusal.
        private static readonly ModuleBuilder Rejected = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("RejectedSelectionShapes"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("RejectedSelectionShapes");

        [Test]
        public void DuplicateIndexAliasOnTwoDifferentlyNamedMembersIsRefusedByName()
        {
            var type = Document("DupAlias");
            Field(type, "First", typeof(string), Key(0), IndexAttr("shared"));
            Field(type, "Second", typeof(string), Key(1), IndexAttr("shared"));
            var message = Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.ForTypes(new[] { type.CreateType()! }))!.Message;

            Assert.That(message, Does.Contain("DupAlias"));
            Assert.That(message, Does.Contain("First"));
            Assert.That(message, Does.Contain("Second"));
            Assert.That(message, Does.Contain("shared"));
        }

        [Test]
        public void ManyReferenceOfAnElementTypeThatIsNotACultRecordRefIsRefused()
        {
            var type = Document("BadMany");
            Field(type, "Values", typeof(List<int>), Key(0), ReferenceAttr(null, many: true));
            var message = Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.ForTypes(new[] { type.CreateType()! }))!.Message;

            Assert.That(message, Does.Contain("BadMany"));
            Assert.That(message, Does.Contain("Values"));
        }

        [Test]
        public void SingleReferenceWhoseMemberTypeIsNotACultRecordRefIsRefused()
        {
            var type = Document("BadSingle");
            Field(type, "Target", typeof(int), Key(0), ReferenceAttr(typeof(FixtureLeafA), many: false));
            var message = Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.ForTypes(new[] { type.CreateType()! }))!.Message;

            Assert.That(message, Does.Contain("BadSingle"));
            Assert.That(message, Does.Contain("Target"));
        }

        private static TypeBuilder Document(string name)
        {
            var type = Rejected.DefineType(name, TypeAttributes.Public);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
                new object[] { "selection.reject." + name, "selection.reject." + name + ".v1" }));
            type.SetCustomAttribute(new CustomAttributeBuilder(typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false }));
            return type;
        }

        private static void Field(TypeBuilder type, string name, Type fieldType, params CustomAttributeBuilder[] attributes)
        {
            var field = type.DefineField(name, fieldType, FieldAttributes.Public);
            foreach (var attribute in attributes) field.SetCustomAttribute(attribute);
        }

        private static CustomAttributeBuilder Key(int slot) =>
            new(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { slot });

        private static CustomAttributeBuilder IndexAttr(string alias) =>
            new(typeof(CultIndexAttribute).GetConstructor(new[] { typeof(string) })!, new object?[] { alias });

        private static CustomAttributeBuilder ReferenceAttr(Type? targetType, bool many) =>
            new(typeof(CultReferenceAttribute).GetConstructor(new[] { typeof(Type), typeof(bool) })!, new object?[] { targetType, many });

        public abstract class FixtureMiddle
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultIndex("mass")]
            public float Mass;
        }

        [CultDocument("selection.fixture.leaf_a", "selection.fixture.leaf_a.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class FixtureLeafA : FixtureMiddle
        {
            [Key(2)]
            public int Extra;
        }

        [CultDocument("selection.fixture.leaf_b", "selection.fixture.leaf_b.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class FixtureLeafB : FixtureMiddle
        {
            [Key(2)]
            [CultIndex("weight")]
            public decimal? Weight;
        }

        [CultDocument("selection.fixture.citer", "selection.fixture.citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class FixtureCiter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(FixtureMiddle))]
            public CultRecordRef<FixtureMiddle> Design;

            [Key(2)]
            [CultReference(typeof(FixtureLeafA), many: true)]
            public Dictionary<CultRecordRef<FixtureLeafA>, float> Components = new();

            [Key(3)]
            [CultReference(typeof(FixtureLeafA), many: true)]
            public List<CultRecordRef<FixtureLeafA>> Related = new();
        }
    }
}
