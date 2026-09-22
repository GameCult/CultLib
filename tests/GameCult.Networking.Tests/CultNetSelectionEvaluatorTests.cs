#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // CultNet typed selection, Cut 1, commit 1 (docs/cultnet-selection-cut.md): the shape, its
    // validation door and the one evaluator. Independent of any live server/client transport - these
    // exercise CultNetSelection/CultNetSelectionEvaluator directly against a registry and a hand-built
    // row set, matching section 10's S1-S23 naming.
    public sealed class CultNetSelectionEvaluatorTests
    {
        private static CultDocumentRegistry Registry() => CultDocumentRegistry.ForTypes(new[]
        {
            typeof(SelLeafA), typeof(SelLeafB), typeof(SelCiter)
        });

        private static CultNetSelectionEvaluator.Row Row(CultDocumentRegistry registry, object document, string key, long ordinal) =>
            new(registry.GetRequired(document.GetType()), new CultRecordKey(key), document, ordinal);

        // S1: an index no reachable schema declares, an empty values list, and an empty schemas/keys
        // list are refused typed at the door, never answered as an empty page.
        [Test]
        public void ValidationRefusesUndeclaredIndexRoleAndEmptyLists()
        {
            var descriptors = Registry().AllDescriptors.ToArray();

            AssertInvalid(new CultNetSelection { Schemas = Array.Empty<string>() }, descriptors, "schemas");
            AssertInvalid(new CultNetSelection { Keys = Array.Empty<string>() }, descriptors, "keys");
            AssertInvalid(
                new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "not_declared", Op = "any_of", Values = new[] { "x" } } } },
                descriptors,
                "fields[0].index");
            AssertInvalid(
                new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "any_of", Values = Array.Empty<string>() } } },
                descriptors,
                "fields[0].values");
            AssertInvalid(
                new CultNetSelection { Cited = new CultNetIncoming { Role = "no_such_role", Exists = true } },
                descriptors,
                "cited.role");
        }

        private static void AssertInvalid(CultNetSelection selection, IReadOnlyList<CultDocumentDescriptor> descriptors, string expectedField)
        {
            var ex = Assert.Throws<CultNetSelectionInvalidException>(() => selection.Validate(descriptors));
            Assert.That(ex!.Field, Is.EqualTo(expectedField));
        }

        // S2: fields conjoin; any_of matches on membership.
        [Test]
        public void EvaluatorConjoinsAnyOfPredicatesOverDeclaredIndexes()
        {
            var registry = Registry();
            var a = new SelLeafA { Name = "sword", Kind = "weapon", Mass = 3 };
            var b = new SelLeafA { Name = "shield", Kind = "armor", Mass = 3 };
            var rows = new[] { Row(registry, a, "a", 1), Row(registry, b, "b", 2) };
            var selection = new CultNetSelection
            {
                Fields = new[]
                {
                    new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "weapon" } },
                    new CultNetFieldPredicate { Index = "mass", Op = "ge", Number = 3 }
                }
            };

            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "a" }));
        }

        // S3: order is (ordinal, schemaId, recordKey) ascending, reversed under descending.
        [Test]
        public void EvaluatorOrdersByOrdinalThenIdentityAndReverses()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new SelLeafA { Name = "c", Kind = "k", Mass = 1 }, "c", 3),
                Row(registry, new SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "a", 1),
                Row(registry, new SelLeafA { Name = "b", Kind = "k", Mass = 1 }, "b", 2)
            };

            var ascending = CultNetSelectionEvaluator.Select(registry, rows, new CultNetSelection(), asOf: 1);
            Assert.That(ascending.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "a", "b", "c" }));

            var descending = CultNetSelectionEvaluator.Select(registry, rows, new CultNetSelection { Descending = true }, asOf: 1);
            Assert.That(descending.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "c", "b", "a" }));
        }

        // S4: a page walk visits every row exactly once and the last page carries no cursor.
        [Test]
        public void EvaluatorPagesExactlyOnceAndTheLastPageSaysSo()
        {
            var registry = Registry();
            var rows = Enumerable.Range(0, 5)
                .Select(i => Row(registry, new SelLeafA { Name = $"n{i}", Kind = "k", Mass = i }, $"k{i}", i))
                .ToArray();

            var selection = new CultNetSelection { Limit = 2 };
            var seen = new List<string>();
            string? cursor = null;
            for (var guard = 0; guard < 10; guard++)
            {
                selection.Cursor = cursor;
                var page = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
                seen.AddRange(page.Rows.Select(r => r.Key.Value));
                if (page.NextCursor == null) break;
                cursor = page.NextCursor;
            }

            Assert.That(seen, Is.EqualTo(new[] { "k0", "k1", "k2", "k3", "k4" }));
        }

        // S5: a cursor answers only the asOf and the selection it was minted for.
        [Test]
        public void EvaluatorRefusesAStaleCursorAndAMismatchedSelection()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "a", 1),
                Row(registry, new SelLeafA { Name = "b", Kind = "k", Mass = 1 }, "b", 2)
            };
            var selection = new CultNetSelection { Limit = 1 };
            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
            Assert.That(first.NextCursor, Is.Not.Null);

            selection.Cursor = first.NextCursor;
            Assert.That(
                () => CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 2),
                Throws.TypeOf<CultNetSelectionCursorException>()
                    .With.Property(nameof(CultNetSelectionCursorException.Code)).EqualTo("cursor_stale"));

            var otherSelection = new CultNetSelection { Limit = 1, Cursor = first.NextCursor, Descending = true };
            Assert.That(
                () => CultNetSelectionEvaluator.Select(registry, rows, otherSelection, asOf: 1),
                Throws.TypeOf<CultNetSelectionCursorException>()
                    .With.Property(nameof(CultNetSelectionCursorException.Code)).EqualTo("cursor_invalid"));
        }

        // S6: the hop follows one declared reference by role; a second role is not followed.
        [Test]
        public void EvaluatorHopsOneEdgeByDeclaredReferenceAndRole()
        {
            var registry = Registry();
            var target = new SelLeafA { Name = "target", Kind = "k", Mass = 1 };
            var citer = new SelCiter { Name = "citer", Design = new CultRecordRef<SelFixtureMiddle>(new CultRecordKey("target")) };
            var rows = new[] { Row(registry, target, "target", 1), Row(registry, citer, "citer", 2) };

            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = registry.GetRequired<SelLeafA>().SchemaId, RecordKey = "target" }, Role = "Design" }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "citer" }));
            Assert.That(evaluation.Edges.Single().Role, Is.EqualTo("Design"));

            var wrongRole = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = registry.GetRequired<SelLeafA>().SchemaId, RecordKey = "target" }, Role = "NotARole" }
            };
            // Validate refuses an undeclared role before evaluation ever runs (S1).
            Assert.Throws<CultNetSelectionInvalidException>(() => wrongRole.Validate(registry.AllDescriptors.ToArray()));
        }

        // S7: cited { exists: false } is the one negation.
        [Test]
        public void EvaluatorNegatedIncomingEdgeIsTheOnlyNegation()
        {
            var registry = Registry();
            var cited = new SelLeafA { Name = "cited", Kind = "k", Mass = 1 };
            var uncited = new SelLeafA { Name = "uncited", Kind = "k", Mass = 1 };
            var citer = new SelCiter { Name = "citer", Design = new CultRecordRef<SelFixtureMiddle>(new CultRecordKey("cited")) };
            var rows = new[] { Row(registry, cited, "cited", 1), Row(registry, uncited, "uncited", 2), Row(registry, citer, "citer", 3) };

            var exists = CultNetSelectionEvaluator.Select(registry, rows,
                new CultNetSelection { Schemas = new[] { registry.GetRequired<SelLeafA>().SchemaId }, Cited = new CultNetIncoming { Role = "Design", Exists = true } },
                asOf: 1);
            Assert.That(exists.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "cited" }));

            var absent = CultNetSelectionEvaluator.Select(registry, rows,
                new CultNetSelection { Schemas = new[] { registry.GetRequired<SelLeafA>().SchemaId }, Cited = new CultNetIncoming { Role = "Design", Exists = false } },
                asOf: 1);
            Assert.That(absent.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "uncited" }));
        }

        // S8: header projection carries no payload; edges under header carry no payload either (S20).
        [Test]
        public void HeaderProjectionCarriesNoPayload()
        {
            var record = new CultNetRawDocumentRecord
            {
                SchemaId = "s", RecordKey = "k", StoredAt = "now", PayloadEncoding = "messagepack", Payload = new byte[] { 1, 2, 3 }
            };
            var header = CultNetRawDocumentHeader.FromRecord(record);
            Assert.That(header.SchemaId, Is.EqualTo("s"));
            // CultNetRawDocumentHeader has no Payload/PayloadEncoding members at all - a compile-time
            // guarantee, not merely a null check; this test exists to name the rule.
        }

        // S16: the four comparisons at the boundary, including equal-to-the-compared-number rows.
        [Test]
        public void EvaluatorComparesNumbersAtTheBoundaryForEachOfTheFourOperators()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new SelLeafA { Name = "lo", Kind = "k", Mass = 4 }, "lo", 1),
                Row(registry, new SelLeafA { Name = "eq", Kind = "k", Mass = 5 }, "eq", 2),
                Row(registry, new SelLeafA { Name = "hi", Kind = "k", Mass = 6 }, "hi", 3)
            };

            string[] Matches(string op) => CultNetSelectionEvaluator.Select(
                registry, rows,
                new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = op, Number = 5 } } },
                asOf: 1).Rows.Select(r => r.Key.Value).OrderBy(k => k, StringComparer.Ordinal).ToArray();

            Assert.That(Matches("lt"), Is.EqualTo(new[] { "lo" }));
            Assert.That(Matches("le"), Is.EqualTo(new[] { "eq", "lo" }));
            Assert.That(Matches("ge"), Is.EqualTo(new[] { "eq", "hi" }));
            Assert.That(Matches("gt"), Is.EqualTo(new[] { "hi" }));
        }

        // S17: an alias declared once on the abstract middle reaches every leaf that inherits it.
        [Test]
        public void EvaluatorAppliesAnInheritedAliasToEveryReachableLeaf()
        {
            var registry = Registry();
            var a = new SelLeafA { Name = "a", Kind = "k", Mass = 20 };
            var b = new SelLeafB { Name = "b", Kind = "k", Mass = 20 };
            var rows = new[] { Row(registry, a, "a", 1), Row(registry, b, "b", 2) };

            var evaluation = CultNetSelectionEvaluator.Select(
                registry, rows,
                new CultNetSelection
                {
                    Schemas = new[] { registry.GetRequired<SelLeafA>().SchemaId, registry.GetRequired<SelLeafB>().SchemaId },
                    Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "gt", Number = 10 } }
                },
                asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value).OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { "a", "b" }));
        }

        // S18: an edge naming a row outside the reference's declared target refuses the selection.
        [Test]
        public void EvaluatorRefusesAnEdgeOutsideItsDeclaredTarget()
        {
            var registry = Registry();
            var leafB = new SelLeafB { Name = "b", Kind = "k", Mass = 1 };
            // Design's declared target is SelFixtureMiddle (an abstract type with no schema of its own),
            // and both leaves are within that target, so this citer is a legitimate in-target edge.
            var okCiter = new SelCiter { Name = "ok", Design = new CultRecordRef<SelFixtureMiddle>(new CultRecordKey("b")) };
            var refA = registry.GetRequired<SelLeafA>();
            var offTargetCiter = new SelCiterNarrow { Name = "bad", NarrowRef = new CultRecordRef<SelLeafA>(new CultRecordKey("b")) };
            var narrowRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(SelLeafA), typeof(SelLeafB), typeof(SelCiterNarrow) });
            var rows = new[]
            {
                new CultNetSelectionEvaluator.Row(narrowRegistry.GetRequired<SelLeafB>(), new CultRecordKey("b"), leafB, 1),
                new CultNetSelectionEvaluator.Row(narrowRegistry.GetRequired<SelCiterNarrow>(), new CultRecordKey("bad"), offTargetCiter, 2)
            };

            var selection = new CultNetSelection
            {
                Cited = new CultNetIncoming { Role = "NarrowRef", Exists = true }
            };
            Assert.Throws<CultNetSelectionReferenceOutsideTargetException>(
                () => CultNetSelectionEvaluator.Select(narrowRegistry, rows, selection, asOf: 1));
        }

        // S23: the evaluator matches every row sharing an index value, never the cache's own
        // last-writer-wins unique-index map. Three rows share "shared" here.
        [Test]
        public void EvaluatorMatchesEveryRowSharingAnIndexValueNotAWinner()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new SelLeafA { Name = "1", Kind = "shared", Mass = 1 }, "1", 1),
                Row(registry, new SelLeafA { Name = "2", Kind = "shared", Mass = 1 }, "2", 2),
                Row(registry, new SelLeafA { Name = "3", Kind = "shared", Mass = 1 }, "3", 3)
            };

            var evaluation = CultNetSelectionEvaluator.Select(
                registry, rows,
                new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "shared" } } } },
                asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value).OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { "1", "2", "3" }));
        }

        public abstract class SelFixtureMiddle
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultIndex("mass")]
            public float Mass;

            [Key(2)]
            [CultIndex("kind")]
            public string Kind = string.Empty;
        }

        [CultDocument("cultnet.selection-tests.leaf_a", "cultnet.selection-tests.leaf_a.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SelLeafA : SelFixtureMiddle
        {
        }

        [CultDocument("cultnet.selection-tests.leaf_b", "cultnet.selection-tests.leaf_b.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SelLeafB : SelFixtureMiddle
        {
        }

        [CultDocument("cultnet.selection-tests.citer", "cultnet.selection-tests.citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SelCiter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(SelFixtureMiddle))]
            public CultRecordRef<SelFixtureMiddle> Design;
        }

        [CultDocument("cultnet.selection-tests.citer_narrow", "cultnet.selection-tests.citer_narrow.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SelCiterNarrow
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            // Declared target is the single leaf SelLeafA, not the abstract middle - a value pointing at
            // a SelLeafB row is outside this reference's declared target set (S18).
            [Key(1)]
            [CultReference(typeof(SelLeafA))]
            public CultRecordRef<SelLeafA> NarrowRef;
        }
    }
}
