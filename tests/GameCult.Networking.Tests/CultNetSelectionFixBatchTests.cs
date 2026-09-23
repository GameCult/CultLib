#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Logging;
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
            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", keysOne),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", keysTwo)));

            var valuesOne = new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "a|b" } } } };
            var valuesTwo = new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "a", "b" } } } };
            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", valuesOne),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", valuesTwo)));
        }

        // S5-DigestNoFields (fix batch 3 "Surviving mutants"): the digest reads selection.Fields - a
        // mutant that drops the fields loop would digest two selections identically as long as their
        // schemas/keys/hop/projection agree, letting a cursor minted for one selection page a
        // differently-fielded one.
        [Test]
        public void ComputeDigest_DiffersWhenOnlyFieldsDiffer()
        {
            var withFieldA = new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "a" } } } };
            var withFieldB = new CultNetSelection { Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "b" } } } };
            var noFields = new CultNetSelection();

            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withFieldA),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withFieldB)));
            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withFieldA),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", noFields)));
        }

        // R-O (fix batch 3, F6 "cursors are keyed"): a cursor's digest is an HMAC under the answering
        // process's key - a page request carrying a cursor minted under a different key (forged, or
        // minted by a different process) is refused cursor_invalid, never answered.
        [Test]
        public void Page_RefusesACursorMintedUnderADifferentKey()
        {
            var registry = Registry();
            var rows = Enumerable.Range(0, 3)
                .Select(i => Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = $"n{i}", Kind = "k", Mass = i }, $"k{i}", i))
                .ToArray();
            var selection = new CultNetSelection { Limit = 1 };
            var mintingKey = CultNetSelectionCursorKey.Random();
            var otherKey = CultNetSelectionCursorKey.Random();

            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: mintingKey);
            Assert.That(first.NextCursor, Is.Not.Null);

            selection.Cursor = first.NextCursor;
            var ex = Assert.Throws<CultNetSelectionCursorException>(
                () => CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: otherKey));
            Assert.That(ex!.Code, Is.EqualTo("cursor_invalid"));
        }

        // R-O: a cursor minted before a (simulated) restart does not verify against the restarted
        // process's fresh key - the same refusal as a forged cursor, because from the answering
        // process's point of view they are indistinguishable (F6: "not surviving a restart is the
        // accepted cost, not a bug").
        [Test]
        public void Page_RefusesACursorMintedBeforeARestart()
        {
            var registry = Registry();
            var rows = Enumerable.Range(0, 3)
                .Select(i => Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = $"n{i}", Kind = "k", Mass = i }, $"k{i}", i))
                .ToArray();
            var selection = new CultNetSelection { Limit = 1 };
            var preRestartKey = CultNetSelectionCursorKey.Random();

            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: preRestartKey);
            Assert.That(first.NextCursor, Is.Not.Null);

            // A restart is a fresh process key, exactly as CultNetDatabase's constructor mints a fresh
            // CursorKey per instance unless one is supplied.
            var postRestartKey = CultNetSelectionCursorKey.Random();
            selection.Cursor = first.NextCursor;
            var ex = Assert.Throws<CultNetSelectionCursorException>(
                () => CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: postRestartKey));
            Assert.That(ex!.Code, Is.EqualTo("cursor_invalid"));
        }

        // R-Y (fix batch 4, Soul's P4 "forged cursor position, reused digest"): before this, the HMAC
        // covered only the selection, not the cursor's own body - so a forged cursor could keep a
        // legitimately minted digest and rewrite the ordinal (or schemaId/recordKey) underneath it, and
        // the server would page from wherever the forged position named instead of refusing it.
        [Test]
        public void Page_RefusesAForgedCursorThatReusesALegitimateDigestUnderARewrittenOrdinal()
        {
            var registry = Registry();
            var rows = Enumerable.Range(0, 3)
                .Select(i => Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = $"n{i}", Kind = "k", Mass = i }, $"k{i}", i))
                .ToArray();
            var selection = new CultNetSelection { Limit = 1 };
            var key = CultNetSelectionCursorKey.Random();

            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: key);
            Assert.That(first.NextCursor, Is.Not.Null);
            var legitimate = CultNetSelectionCursor.Parse(first.NextCursor!);

            var forged = ForgeCursorBody(legitimate.AsOf, ordinal: 999, legitimate.SchemaId, legitimate.RecordKey, legitimate.Digest);
            Assert.That(forged, Is.Not.EqualTo(first.NextCursor), "the forged cursor must actually differ from the legitimate one");

            selection.Cursor = forged;
            var ex = Assert.Throws<CultNetSelectionCursorException>(
                () => CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: key));
            Assert.That(ex!.Code, Is.EqualTo("cursor_invalid"));
        }

        /// <summary>
        /// Builds a cursor string carrying an arbitrary body under a caller-supplied digest, the same
        /// wire shape <see cref="CultNetSelectionCursor.Mint"/> produces - used to simulate a forged
        /// cursor that reuses a legitimately minted digest under a rewritten body (R-Y).
        /// </summary>
        private static string ForgeCursorBody(ulong asOf, long ordinal, string schemaId, string recordKey, string digest)
        {
            var body = new System.Text.StringBuilder();
            void AppendString(string value) =>
                body.Append(System.Text.Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
            AppendString(asOf.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendString(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendString(schemaId);
            AppendString(recordKey);
            AppendString(digest);
            var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToString());
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // R-O: the cursor body is length-prefixed, not delimiter-joined, so a record key carrying any
        // character at all - including U+241F, which broke the pre-fix delimiter-split parse - round-trips.
        [Test]
        public void Cursor_RoundTripsARecordKeyContainingU241F()
        {
            var registry = Registry();
            var weirdKey = "row-␟-two";
            var rows = new[]
            {
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 0 }, "a-first", 0),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "b", Kind = "k", Mass = 1 }, weirdKey, 1)
            };
            var selection = new CultNetSelection { Limit = 1 };
            var key = CultNetSelectionCursorKey.Random();

            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: key);
            Assert.That(first.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "a-first" }));
            Assert.That(first.NextCursor, Is.Not.Null);

            selection.Cursor = first.NextCursor;
            var second = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: key);
            Assert.That(second.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { weirdKey }));
            Assert.That(second.NextCursor, Is.Null);
        }

        /// <summary>
        /// Builds a cursor body whose 5th (final) field carries a raw, hostile length-prefix
        /// spelling instead of a well-formed `AppendString` field - R-AG. The first 4 fields are
        /// well-formed so <c>ReadLengthPrefixedFields</c> reaches the hostile one mid-loop rather
        /// than failing earlier for an unrelated reason.
        /// </summary>
        private static string CursorWithHostileLastField(string hostileField)
        {
            var body = new System.Text.StringBuilder();
            void AppendString(string value) =>
                body.Append(System.Text.Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
            AppendString("1");
            AppendString("5");
            AppendString("schema-a");
            AppendString("k1");
            body.Append(hostileField);
            var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToString());
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // R-AG: C#'s own proven overflow (CultNetSelectionEvaluator.cs, ReadLengthPrefixedFields,
        // before this cut) - a length prefix of int.MaxValue (2147483647) made `pos + length`
        // overflow int math and slip past the guard, so Encoding.UTF8.GetString threw
        // ArgumentOutOfRangeException instead of Parse returning the typed cursor_invalid refusal.
        [Test]
        public void HostileCursor_LengthPrefixNearInt32Max_RefusesTypedNotUntyped()
        {
            var cursor = CursorWithHostileLastField("2147483647:x");
            Assert.Throws<CultNetSelectionCursorException>(() => CultNetSelectionCursor.Parse(cursor));
        }

        // R-AG: the mirror-image proven overflow, Rust's (packages/cultnet-rs/src/selection.rs,
        // read_length_prefixed_fields, before this cut) - a length prefix of usize::MAX
        // (18446744073709551615) overflowed Rust's usize guard and panicked. "Each runtime holding
        // exactly the hole the other closed" means neither side's tests are evidence about the
        // other - fed here too. int.TryParse refuses a value this wide outright (a different
        // failure path than C#'s own overflow above), which must also refuse typed.
        [Test]
        public void HostileCursor_LengthPrefixNearUSizeMax_RefusesTypedNotUntyped()
        {
            var cursor = CursorWithHostileLastField("18446744073709551615:x");
            Assert.Throws<CultNetSelectionCursorException>(() => CultNetSelectionCursor.Parse(cursor));
        }

        // R-AG: a length prefix with more digits than any integer type holds.
        [Test]
        public void HostileCursor_LengthPrefixWiderThanAnyIntegerType_RefusesTypedNotUntyped()
        {
            var cursor = CursorWithHostileLastField("999999999999999999999999999999:x");
            Assert.Throws<CultNetSelectionCursorException>(() => CultNetSelectionCursor.Parse(cursor));
        }

        // R-AG: a negative-looking length prefix - int.TryParse(NumberStyles.None, ...) refuses the
        // leading '-' outright (NumberStyles.None permits no sign), a third failure path (neither
        // overflow nor digit-count) that must also refuse typed.
        [Test]
        public void HostileCursor_NegativeLengthPrefix_RefusesTypedNotUntyped()
        {
            var cursor = CursorWithHostileLastField("-1:x");
            Assert.Throws<CultNetSelectionCursorException>(() => CultNetSelectionCursor.Parse(cursor));
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

        private static CultDocumentRegistry NarrowAndManyRegistry() => CultDocumentRegistry.ForTypes(new[]
        {
            typeof(CultNetSelectionEvaluatorTests.SelLeafA),
            typeof(CultNetSelectionEvaluatorTests.SelLeafB),
            typeof(CultNetSelectionEvaluatorTests.SelCiterNarrow),
            typeof(CultNetSelectionEvaluatorTests.SelCiterManyUntyped)
        });

        // R-V (S-1, Soul's P2 "two schemas at one key"): a row's identity is (schemaId, recordKey), not
        // the record key alone. SelLeafA and SelLeafB rows both live at key "k"; SelCiterNarrow's
        // NarrowRef is declared to SelLeafA only, so it must always resolve to the SelLeafA row at "k",
        // never the SelLeafB one - regardless of which of the two same-keyed rows a bare-key dictionary
        // would have kept. Before this, byKey was a bare-key dictionary that kept only one of them (last
        // writer wins), so which row NarrowRef resolved to - and therefore whether the selection matched
        // or refused reference_outside_target - depended on row insertion order.
        [TestCase(false)]
        [TestCase(true)]
        public void Evaluator_CitedResolvesARowSharingItsKeyWithAnotherSchema_ByDeclaredTargetNotInsertionOrder(bool reverseRowOrder)
        {
            var registry = NarrowAndManyRegistry();
            var leafA = Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "k", 0);
            var leafB = Row(registry, new CultNetSelectionEvaluatorTests.SelLeafB { Name = "b", Kind = "k", Mass = 1 }, "k", 1);
            var citer = Row(registry, new CultNetSelectionEvaluatorTests.SelCiterNarrow
            {
                Name = "citer",
                NarrowRef = new CultRecordRef<CultNetSelectionEvaluatorTests.SelLeafA>(new CultRecordKey("k"))
            }, "citer", 2);

            var rows = reverseRowOrder
                ? new[] { citer, leafB, leafA }
                : new[] { citer, leafA, leafB };

            var selection = new CultNetSelection { Cited = new CultNetIncoming { Role = "NarrowRef", Exists = true } };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            // Only the SelLeafA row at "k" is cited - SelLeafB's row at the same key is a different
            // identity and must not match, in either row order.
            Assert.That(evaluation.Rows.Select(r => (r.Descriptor.SchemaId, r.Key.Value)),
                Is.EqualTo(new[] { (registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId, "k") }));
        }

        // R-AO: the test above covers one in-target candidate and one out - TryResolveReferenceTarget's
        // `foreach (var candidate in candidates) { ... return true; }` (CultNetSelectionEvaluator.cs)
        // picks the first match, but nothing pinned that rule specifically when *two* in-target rows
        // share a key (Soul's SM-4: "the first row in the caller's order wins" was a coincidence of
        // source, not a rule). SelCiter.Design's declared target is the abstract SelFixtureMiddle, so
        // both SelLeafA and SelLeafB are in-target - which one resolves depends only on which the
        // caller listed first in `rows` (byKey's insertion order follows `allRows`). Both orders are
        // exercised, each expecting whichever row was listed first in *that* run - proving the rule is
        // "first in the caller's order", not a fixed schema preference.
        [TestCase(false)]
        [TestCase(true)]
        public void Evaluator_CitedWithTwoInTargetCandidates_ResolvesToWhicheverTheCallerListedFirst(bool reverseRowOrder)
        {
            var registry = Registry();
            var leafA = Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "dup", 0);
            var leafB = Row(registry, new CultNetSelectionEvaluatorTests.SelLeafB { Name = "b", Kind = "k", Mass = 1 }, "dup", 1);
            var citer = Row(registry, new CultNetSelectionEvaluatorTests.SelCiter
            {
                Name = "citer",
                Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("dup"))
            }, "citer", 2);

            var rows = reverseRowOrder
                ? new[] { citer, leafB, leafA }
                : new[] { citer, leafA, leafB };
            var expectedFirstSchemaId = reverseRowOrder
                ? registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafB>().SchemaId
                : registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId;

            var selection = new CultNetSelection { Cited = new CultNetIncoming { Role = "Design", Exists = true } };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => (r.Descriptor.SchemaId, r.Key.Value)),
                Is.EqualTo(new[] { (expectedFirstSchemaId, "dup") }),
                "must resolve to whichever in-target row the caller listed first, not a fixed schema");
        }

        // R-W (S-2, Soul's P3): EnsureWithinDeclaredTarget's equivalent check must run for every edge a
        // many-reference carries, before the citation's own key filters that edge out - not only for the
        // edge the citation happens to be asking about. SelCiterManyUntyped.ManyRefs is (by inference,
        // R-T(b)) declared to SelLeafA alone; one element points at a SelLeafA row (in-target) and a
        // second at a SelLeafB row (out-of-target). Querying `cites` for a record key neither element
        // carries must still refuse: the out-of-target edge is wrong regardless of what the caller asked.
        [Test]
        public void Evaluator_CitesRefusesAnOutOfTargetEdgeEvenWhenTheQueriedKeyMatchesNeitherEdge()
        {
            var registry = NarrowAndManyRegistry();
            var leafA = Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "a", 0);
            var leafB = Row(registry, new CultNetSelectionEvaluatorTests.SelLeafB { Name = "b", Kind = "k", Mass = 1 }, "b", 1);
            var citer = Row(registry, new CultNetSelectionEvaluatorTests.SelCiterManyUntyped
            {
                Name = "citer",
                ManyRefs = new[]
                {
                    new CultRecordRef<CultNetSelectionEvaluatorTests.SelLeafA>(new CultRecordKey("a")),
                    new CultRecordRef<CultNetSelectionEvaluatorTests.SelLeafA>(new CultRecordKey("b"))
                }
            }, "citer", 2);
            var rows = new[] { leafA, leafB, citer };

            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation
                {
                    Target = new CultNetRecordRef { SchemaId = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId, RecordKey = "does-not-exist" },
                    Role = "ManyRefs"
                }
            };

            var ex = Assert.Throws<CultNetSelectionReferenceOutsideTargetException>(
                () => CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1));
            Assert.That(ex!.ToSchemaId, Is.EqualTo(registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafB>().SchemaId));
            Assert.That(ex.ToKey, Is.EqualTo("b"));
        }

        // R-AL/SM-6: the evaluator's own `!member.IsReference` guard in ReferenceMembers, pinned
        // directly. CultCache's D10b (CultCache.cs DiscoverMembers, ~1099) now refuses the colliding
        // shape at registration, so CultDocumentRegistry.ForTypes can no longer produce a descriptor
        // this bug reaches - which is D10b's own test's job, not this one's. This test builds the
        // colliding descriptor by hand, bypassing the registry entirely (reflection over the internal
        // CultDocumentDescriptor/PersistedMember constructors - the "only door" CultCache.cs:208
        // describes is a door for production code, not for pinning a caller-side guard in isolation),
        // so the guard is proven on its own: even a descriptor D10b never had a chance to validate
        // must not let a data member answer for a reference member's role.
        [Test]
        public void ReferenceMembersExcludesADataMemberSharingAReferenceMembersRoleEvenOffARegistryBypassingDescriptor()
        {
            var cachingAssembly = typeof(CultDocumentRegistry).Assembly;
            var persistedMemberType = cachingAssembly.GetType("GameCult.Caching.CultDocumentRegistry+PersistedMember", throwOnError: true)!;
            var memberDescriptorType = cachingAssembly.GetType("GameCult.Caching.CultDocumentMemberDescriptor", throwOnError: true)!;
            var descriptorCtor = typeof(CultDocumentDescriptor)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();

            object MakeMember(MemberInfo member, Type memberType, int slot, string? indexAlias, bool isReference, Type? targetType)
            {
                var instance = Activator.CreateInstance(persistedMemberType, nonPublic: true)!;
                void Set(string property, object? value) => persistedMemberType.GetProperty(property)!.SetValue(instance, value);
                Set("Member", member);
                Set("MemberType", memberType);
                Set("Slot", slot);
                Set("IsName", false);
                Set("IndexAlias", indexAlias);
                Set("IsReference", isReference);
                Set("IsMany", false);
                Set("TargetSchemaName", null);
                Set("TargetType", targetType);
                Set("IsNumeric", false);
                return instance;
            }

            // A plain data member "Owner" with no [CultIndex] (its role falls back to its bare name)
            // and a reference member whose alias is explicitly "Owner" - the exact shape Soul probed.
            var ownerMember = MakeMember(typeof(GuardProbeShape).GetField(nameof(GuardProbeShape.Owner))!,
                typeof(string), 0, indexAlias: null, isReference: false, targetType: null);
            var refMember = MakeMember(typeof(GuardProbeShape).GetField(nameof(GuardProbeShape.Ref))!,
                typeof(CultRecordRef<CultNetSelectionEvaluatorTests.SelLeafA>), 1, indexAlias: "Owner", isReference: true,
                targetType: typeof(CultNetSelectionEvaluatorTests.SelLeafA));

            var richMembers = Array.CreateInstance(persistedMemberType, 2);
            richMembers.SetValue(ownerMember, 0);
            richMembers.SetValue(refMember, 1);
            var members = Array.CreateInstance(memberDescriptorType, 0);

            var descriptor = (CultDocumentDescriptor)descriptorCtor.Invoke(new object?[]
            {
                typeof(GuardProbeShape),
                "selection.fixture.guard_probe",
                "selection.fixture.guard_probe.v1",
                "selection.fixture.guard_probe.v1",
                "sha256:guard-probe",
                "{}",
                false,
                null,
                null,
                new Dictionary<string, Func<object, string>>(),
                members,
                richMembers
            });

            var referenceMembersMethod = typeof(CultNetSelectionEvaluator)
                .GetMethod("ReferenceMembers", BindingFlags.NonPublic | BindingFlags.Static)!;
            var yielded = ((IEnumerable<CultDocumentMemberView>)referenceMembersMethod
                .Invoke(null, new object?[] { descriptor, "Owner" })!).ToArray();

            // Only the reference member answers for role "Owner" - the data member sharing that bare
            // name must not be yielded, or ReferencesOf would be asked to walk it as a reference and
            // ResolveTargetLeaves would throw on its null TargetType (the fault Soul probed).
            Assert.That(yielded.Select(m => m.MemberName), Is.EqualTo(new[] { "Ref" }));
        }

        private sealed class GuardProbeShape
        {
            public string Owner = string.Empty;
            public CultRecordRef<CultNetSelectionEvaluatorTests.SelLeafA> Ref = default;
        }

        // R-AT (Soul's SM-7, merge gate third pass): the edge comparator's `to.schemaId`/
        // `to.recordKey` components survived dropping entirely, and reversing `to.recordKey`
        // survived too, because every existing fixture ties `from`+`to` or `from`+`role` but never
        // ties `from`+`role` while leaving `to` free to vary. Hands had argued no real selection
        // could do that; Soul falsified the argument by building one: a selection carrying BOTH
        // `cites` and `cited` mixes their edges into one Vec, and a row that self-references
        // through the same role it also uses to cite a peer produces a `cites` edge (To = the
        // queried peer) and a `cited` edge (To = the anchor row itself), tied on From and Role,
        // differing only on To, in the same page-position bucket. Taken from the rig rather than
        // rebuilt.
        [CultDocument("cultnet.selection-tests.soul_self_citer", "cultnet.selection-tests.soul_self_citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SoulSelfCiter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(SoulSelfCiter), many: true)]
            public CultRecordRef<SoulSelfCiter>[] Peers = Array.Empty<CultRecordRef<SoulSelfCiter>>();
        }

        // Named "zebra"/"alpha", not the rig's "hub"/"other": CultNetSelectionEvaluator.Select runs
        // the Cited hop before the Cites hop (line ~198 vs ~206), so the self edge is always pushed
        // to CitedEdges before the peer edge is pushed to CitesEdges. With "hub" self-citing and
        // "other" as the cites target, "hub" < "other" already matches that push order, so dropping
        // the comparator's To component reproduces the right answer by coincidence and the mutation
        // survives. Naming the self-citer "zebra" and the target "alpha" makes push order
        // [zebra, alpha] disagree with the required sorted order [alpha, zebra], so only the
        // comparator's own To component - not insertion order - can produce it.
        [Test]
        public void Evaluator_EdgeOrderPinsToWhenFromAndRoleAreBothTiedByASelfCitingPeer()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SoulSelfCiter) });
            var descriptor = registry.GetRequired<SoulSelfCiter>();

            var zebra = new SoulSelfCiter
            {
                Name = "zebra",
                Peers = new[]
                {
                    new CultRecordRef<SoulSelfCiter>(new CultRecordKey("zebra")),
                    new CultRecordRef<SoulSelfCiter>(new CultRecordKey("alpha"))
                }
            };
            var alpha = new SoulSelfCiter { Name = "alpha" };

            var rows = new[]
            {
                new CultNetSelectionEvaluator.Row(descriptor, new CultRecordKey("zebra"), zebra, 1),
                new CultNetSelectionEvaluator.Row(descriptor, new CultRecordKey("alpha"), alpha, 2)
            };

            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation
                {
                    Target = new CultNetRecordRef { SchemaId = descriptor.SchemaId, RecordKey = "alpha" },
                    Role = "Peers"
                },
                Cited = new CultNetIncoming { Role = "Peers", Exists = true }
            };

            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Edges.Count, Is.EqualTo(2), "expected two edges in one page-position bucket");
            Assert.That(evaluation.Edges[0].From.Key.Value, Is.EqualTo(evaluation.Edges[1].From.Key.Value));
            Assert.That(evaluation.Edges[0].Role, Is.EqualTo(evaluation.Edges[1].Role));
            Assert.That(evaluation.Edges[0].To.Key.Value, Is.Not.EqualTo(evaluation.Edges[1].To.Key.Value),
                "To must differ - if it does not, Hands' equivalence claim survives in C# too");
            Assert.That(evaluation.Edges[0].To.Key.Value, Is.EqualTo("alpha"));
            Assert.That(evaluation.Edges[1].To.Key.Value, Is.EqualTo("zebra"));
        }

        // R-AM: HandleSnapshotRequestV1Async (CultNetDatabaseServer.cs) now answers an untyped fault
        // like its siblings HandleShardLogRequestAsync/HandlePutAsync - catch (Exception), log, try to
        // answer a CultNetErrorMessage - instead of only catching the three typed selection exceptions
        // and leaving anything else to escape into the RUDP dispatch backstop's silent swallow. The
        // handler is a private delegate keyed to the concrete sealed CultNetServerPeer (no
        // ICultNetSchemaServerPeer seam here, unlike CultNetOperationServer's handlers, so a FakePeer
        // cannot substitute), and CultNetServerPeer's own constructor needs a live LiteNetLib NetPeer
        // this test does not stand up. Reached the only way available instead: the private delegate
        // field directly, with Selection: null (a shape CultNetSnapshotRequestV1Message's own type
        // does not forbid, so a client could still send it) and an uninitialized CultNetServerPeer
        // (RuntimeHelpers.GetUninitializedObject bypasses its internal constructor). SendCultNet then
        // NREs on the peer's null Transport - a different, later exception than the
        // ArgumentNullException CreateSelectionResponse actually threw for a null selection, which is
        // exactly the proof the catch (Exception) block ran: logged the original fault, then tried and
        // failed to reply on a peer this test never gave working transport plumbing.
        [Test]
        public void CultNetDatabaseServer_SnapshotV1Handler_CatchesAnUntypedFaultAndLogsBeforeAnswering()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            using var server = new Server(cache, ServerSecurityOptions.Development());
            var logger = new CapturingLogger();
            server.Logger = logger;
            using var databaseServer = new CultNetDatabaseServer(server, database);

            var handlerField = typeof(CultNetDatabaseServer)
                .GetField("_snapshotHandlerV1", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var handler = (Func<CultNetSnapshotRequestV1Message, CultNetServerPeer, Task>)handlerField.GetValue(databaseServer)!;
            var peer = (CultNetServerPeer)RuntimeHelpers.GetUninitializedObject(typeof(CultNetServerPeer));

            var thrown = Assert.Catch(() => handler(new CultNetSnapshotRequestV1Message { Selection = null! }, peer));

            Assert.That(thrown, Is.Not.InstanceOf<ArgumentNullException>(),
                "the null-Selection ArgumentNullException must be caught inside the handler, not escape raw");
            Assert.That(logger.Errors, Has.Some.Contains("CultNet snapshot v1 request failed"));
        }

        // R-AM: the RUDP dispatch backstop (CultNetRudpSchemaServer.cs, DispatchAsync) logs every
        // untyped handler fault it swallows, always - it used to answer nothing and log nothing, which
        // is exactly what hid SM-6's ArgumentNullException regression for a whole batch. Real
        // client/server round trip over loopback UDP, the same pattern this file's other RUDP tests
        // use (CultNetSchemaClients.CreateRudp against a RudpCultNetSchemaServer with an OS-assigned
        // port), with a handler that deliberately throws an untyped exception to reach the backstop.
        [Test]
        public async Task RudpCultNetSchemaServer_DispatchBackstop_LogsEveryUntypedHandlerFault()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "dispatch-backstop-logs-server",
                Socket = socket
            });
            var logger = new CapturingLogger();
            server.Logger = logger;
            server.OnCultNet<CultNetShardCatalogRequestMessage>((_, _) =>
                throw new InvalidOperationException("dispatch-backstop-probe"));

            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            })
            { IsBackground = true };
            serverThread.Start();
            try
            {
                using var client = CultNetSchemaClients.CreateRudp("dispatch-backstop-probe-client");
                client.Connect("127.0.0.1", server.LocalEndPoint.Port);
                for (var attempt = 0; attempt < 200 && !client.Connected; attempt++)
                    await Task.Delay(10);
                Assert.That(client.Connected, Is.True, "client failed to connect to the RUDP server");

                client.SendCultNet(new CultNetShardCatalogRequestMessage { MessageId = "dispatch-backstop-probe" });

                for (var attempt = 0; attempt < 200 && logger.Errors.Count == 0; attempt++)
                    await Task.Delay(10);
            }
            finally
            {
                cancellation.Cancel();
                serverThread.Join();
            }

            Assert.That(logger.Errors, Has.Some.Contains("dispatch-backstop-probe"),
                "the backstop must log the handler's own exception message, not swallow it silently");
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Errors { get; } = new();
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) => Errors.Add(message);
            public void LogDebug(string message) { }
        }
    }
}
