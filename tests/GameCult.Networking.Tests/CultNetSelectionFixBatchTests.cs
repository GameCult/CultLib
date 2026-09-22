#nullable enable
using System;
using System.Linq;
using GameCult.Caching;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // CultNet typed selection, Cut 1, second Hands pass on the fix batch (docs/cultnet-selection-cut.md,
    // "Self's rulings for the Cut 1 fix batch" and "The C# half of the fix batch landed"): regression
    // tests for the rules the first Hands pass fixed but left proven only by 7903853..3d32c67's own
    // (unnamed, un-mutated) coverage or, for R-E's cites.target.schemaId door refusal, left entirely
    // unimplemented. Independent of any live server/client transport, same style as
    // CultNetSelectionEvaluatorTests - a hand-built row set against CultNetSelectionEvaluator directly.
    // Reuses CultNetSelectionEvaluatorTests' public fixture types (SelLeafA/SelLeafB/SelFixtureMiddle/
    // SelCiter/SelCiterNarrow) rather than declaring a fourth near-identical set.
    public sealed class CultNetSelectionFixBatchTests
    {
        private static CultDocumentRegistry Registry() => CultDocumentRegistry.ForTypes(new[]
        {
            typeof(CultNetSelectionEvaluatorTests.SelLeafA),
            typeof(CultNetSelectionEvaluatorTests.SelLeafB),
            typeof(CultNetSelectionEvaluatorTests.SelCiter)
        });

        private static CultNetSelectionEvaluator.Row Row(CultDocumentRegistry registry, object document, string key, long ordinal) =>
            new(registry.GetRequired(document.GetType()), new CultRecordKey(key), document, ordinal);

        // R-B: edges are anchored to the row the hop direction actually reports against - the cited row
        // under `cited`, not the citer - and edge order is deterministic: page-row order, then
        // (from, role, to) in code-point order. Two citers citing the same target through the same role
        // put two edges on one page row (`target`), so only a real (from, role, to) tie-break - not
        // insertion order - can produce "citer-a" before "citer-b".
        [Test]
        public void Evaluator_AnchorsCitedEdgesToTheCitedRowAndOrdersThemByFromThenRole()
        {
            var registry = Registry();
            var target = new CultNetSelectionEvaluatorTests.SelLeafA { Name = "target", Kind = "k", Mass = 1 };
            var citerB = new CultNetSelectionEvaluatorTests.SelCiter { Name = "b", Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("target")) };
            var citerA = new CultNetSelectionEvaluatorTests.SelCiter { Name = "a", Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("target")) };
            // Supplied out of code-point order (b before a) on purpose - the assertion only passes if
            // EdgesFor's own tie-break sorts them, not if it merely preserves row-supply order.
            var rows = new[]
            {
                Row(registry, target, "target", 1),
                Row(registry, citerB, "citer-b", 2),
                Row(registry, citerA, "citer-a", 3)
            };

            var selection = new CultNetSelection
            {
                Schemas = new[] { registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId },
                Cited = new CultNetIncoming { Role = "Design", Exists = true }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "target" }));
            // Anchor: both edges must be reported (dropped entirely if wrongly anchored to the citer,
            // which is not on this page under `cited`).
            Assert.That(evaluation.Edges.Select(e => e.From.Key.Value), Is.EqualTo(new[] { "citer-a", "citer-b" }));
            Assert.That(evaluation.Edges.All(e => e.To.Key.Value == "target"), Is.True);
        }

        // R-C: every ordered comparison the vocabulary makes uses Unicode code-point order, not UTF-16
        // code-unit order. U+E000 (BMP private use) sorts before U+1F602 (astral, "😂") in code-point
        // order despite the astral character's leading UTF-16 surrogate (0xD83D) being numerically
        // smaller than 0xE000 - a code-unit ("ordinal") compare sorts it the other way.
        private const string BmpKey = "-row";
        private const string AstralKey = "😂-row";

        [Test]
        public void Evaluator_OrdersAstralKeysByCodePointNotUtf16CodeUnit()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "astral", Kind = "k", Mass = 1 }, AstralKey, 1),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "bmp", Kind = "k", Mass = 1 }, BmpKey, 1)
            };

            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, new CultNetSelection(), asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { BmpKey, AstralKey }));
        }

        // R-C, the cursor half: the cursor's position search must compare by the same code-point order
        // the row tiebreak uses, or a page walk over astral/BMP-boundary keys can skip or repeat rows.
        [Test]
        public void Evaluator_CursorPositionUsesCodePointOrderMatchingTheRowTiebreak()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "astral", Kind = "k", Mass = 1 }, AstralKey, 1),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "bmp", Kind = "k", Mass = 1 }, BmpKey, 1)
            };
            var selection = new CultNetSelection { Limit = 1 };

            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
            Assert.That(first.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { BmpKey }));
            Assert.That(first.NextCursor, Is.Not.Null);

            selection.Cursor = first.NextCursor;
            var second = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
            Assert.That(second.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { AstralKey }));
        }

        // R-E: cites.target.schemaId is matched through the same alias matcher `schemas` uses (a
        // SchemaName, not only the real content-hash SchemaId), and a target whose schema matches
        // nothing declared is refused at the door - never answered with a quietly empty page.
        [Test]
        public void Evaluator_MatchesCitesTargetThroughTheAliasMatcher()
        {
            var registry = Registry();
            var target = new CultNetSelectionEvaluatorTests.SelLeafA { Name = "target", Kind = "k", Mass = 1 };
            var citer = new CultNetSelectionEvaluatorTests.SelCiter { Name = "citer", Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("target")) };
            var rows = new[] { Row(registry, target, "target", 1), Row(registry, citer, "citer", 2) };

            var alias = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaName;
            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = alias, RecordKey = "target" } }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "citer" }));
        }

        [Test]
        public void Validation_RefusesACitesTargetSchemaThatMatchesNoDeclaredSchema()
        {
            var descriptors = Registry().AllDescriptors.ToArray();
            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = "no-such-schema", RecordKey = "target" } }
            };

            var ex = Assert.Throws<CultNetSelectionInvalidException>(() => selection.Validate(descriptors));
            Assert.That(ex!.Field, Is.EqualTo("cites.target.schemaId"));
        }

        // R-E: cites.target.schemaId is resolved against every declared schema, not only the schemas
        // `selection.Schemas` itself reaches - a cites target is a separate structural reference, not
        // filtered by the selection's own schema allowlist. A plausible weaker implementation reuses the
        // `reachable` set already computed for Fields validation and wrongly refuses a valid target whose
        // schema sits outside it.
        [Test]
        public void Validation_ResolvesACitesTargetSchemaEvenWhenNotInTheSelectionsOwnSchemasFilter()
        {
            var registry = Registry();
            var descriptors = registry.AllDescriptors.ToArray();
            var citerSchemaId = registry.GetRequired<CultNetSelectionEvaluatorTests.SelCiter>().SchemaId;
            var leafAlias = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaName;

            var selection = new CultNetSelection
            {
                Schemas = new[] { citerSchemaId },
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = leafAlias, RecordKey = "target" } }
            };

            Assert.DoesNotThrow(() => selection.Validate(descriptors));
        }

        // R-F: the door is inside Select/EvaluateAll unconditionally - there is no public entry point
        // that evaluates a selection without validating it first.
        [Test]
        public void Evaluator_SelectValidatesFirstAndNeverAnswersAnInvalidSelection()
        {
            var registry = Registry();
            var rows = new[] { Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "a", 1) };
            var invalid = new CultNetSelection { Schemas = Array.Empty<string>() };

            Assert.Throws<CultNetSelectionInvalidException>(() => CultNetSelectionEvaluator.Select(registry, rows, invalid, asOf: 1));
        }

        // R-H: the cursor digest length-prefixes every string and list, so no delimiter choice can make
        // two different selections collide. The pre-fix scheme joined keys with "," and a field
        // predicate's values with "|" with no length prefix - Keys=["a,b"] (one string containing a
        // literal comma) digested the same as Keys=["a","b"] (two strings), and a field's
        // Values=["a|b"] digested the same as Values=["a","b"].
        [Test]
        public void ComputeDigest_DoesNotCollideOnDelimiterAmbiguousKeysOrFieldValues()
        {
            var keysOne = new CultNetSelection { Keys = new[] { "a,b" } };
            var keysTwo = new CultNetSelection { Keys = new[] { "a", "b" } };
            Assert.That(CultNetSelectionCursor.ComputeDigest(keysOne), Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(keysTwo)));

            var valuesOne = new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "a|b" } } } };
            var valuesTwo = new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "a", "b" } } } };
            Assert.That(CultNetSelectionCursor.ComputeDigest(valuesOne), Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(valuesTwo)));
        }

        // R-J: any_of on a numeric alias compares TryGetIndexNumber's canonical rendering, not the
        // string getter's culture-dependent ToString() - a float large enough that .NET's default
        // ToString() renders exponent notation ("1E+21") must still match its canonical decimal form.
        [Test]
        public void Evaluator_AnyOfOnANumericAliasComparesTheCanonicalRenderingNotToString()
        {
            var registry = Registry();
            var row = new CultNetSelectionEvaluatorTests.SelLeafA { Name = "big", Kind = "k", Mass = 1e21f };
            var rows = new[] { Row(registry, row, "big", 1) };
            var descriptor = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>();
            Assert.That(descriptor.TryGetIndexNumber(row, "mass", out var canonical), Is.True);
            // .NET's default ToString() on this magnitude renders exponent notation ("1E+21"); the
            // canonical decimal never does - if the two happened to be textually equal this assertion,
            // not the mutant, would be the problem, so pin that precondition rather than assume it.
            Assert.That(canonical, Does.Not.Contain("E"));
            Assert.That(row.Mass.ToString(System.Globalization.CultureInfo.InvariantCulture), Is.Not.EqualTo(canonical));

            var selection = new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "any_of", Values = new[] { canonical! } } }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "big" }));
        }
    }
}
