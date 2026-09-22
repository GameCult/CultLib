#nullable enable
using System;
using System.Linq;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // CultNet typed selection, Cut 1: behavioural tests for the Stryker survivors Soul triaged as real
    // against CultNetSelection.cs/CultNetSelectionEvaluator.cs (docs/cultnet-selection-cut.md, S-5/R-AA,
    // "Self's rulings for fix batch 4"). Each test targets one named survivor by the rule it pins, not
    // the mutated line's spelling - see the method-level comment for the rule and where the pre-fix
    // mutant differs in the answer, not in the code's shape. Fixtures are new where the survivor's own
    // absence-of-fixture is the reason it survived (a mixed-numeric alias, an index alias that differs
    // from its member name, two reference members that resolve to one target).
    public sealed class CultNetSelectionSurvivorTests
    {
        private static CultNetSelectionEvaluator.Row Row(CultDocumentRegistry registry, object document, string key, long ordinal) =>
            new(registry.GetRequired(document.GetType()), new CultRecordKey(key), document, ordinal);

        // ---------------------------------------------------------------------------------------------
        // Survivor 1: CultNetSelection.cs, ValidateField - `declaring.Any(member => !member.IsNumeric)`
        // mutated to `.All(...)`. Rule (section 2, "Inheritance"): "a numeric comparison on an alias that
        // is numeric on one reachable leaf and a string on another is refused at the door, because the
        // row owner cannot compare a string." The mutant only refuses when EVERY reachable leaf declares
        // the alias non-numeric - the exact mixed case the rule exists for was untested.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void Validation_RefusesAComparisonOnAnAliasNumericOnOneReachableLeafAndStringOnAnother()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorNumericLeaf), typeof(SurvivorStringLeaf) });
            var descriptors = registry.AllDescriptors.ToArray();

            var selection = new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "level", Op = "gt", Number = "5" } }
            };

            var ex = Assert.Throws<CultNetSelectionInvalidException>(() => selection.Validate(descriptors));
            Assert.That(ex!.Field, Is.EqualTo("fields[0].index"));
        }

        // The any_of counterpart must still pass on the same mixed pair - only comparisons need the
        // alias numeric everywhere reachable.
        [Test]
        public void Validation_AllowsAnyOfOnAMixedNumericStringAliasAcrossReachableLeaves()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorNumericLeaf), typeof(SurvivorStringLeaf) });
            var descriptors = registry.AllDescriptors.ToArray();

            var selection = new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "level", Op = "any_of", Values = new[] { "5" } } }
            };

            Assert.DoesNotThrow(() => selection.Validate(descriptors));
        }

        [CultDocument("cultnet.selection-tests.survivor_numeric_leaf", "cultnet.selection-tests.survivor_numeric_leaf.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SurvivorNumericLeaf
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] [CultIndex("level")] public int Level;
        }

        [CultDocument("cultnet.selection-tests.survivor_string_leaf", "cultnet.selection-tests.survivor_string_leaf.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SurvivorStringLeaf
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] [CultIndex("level")] public string Level = string.Empty;
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 2: CultNetSelection.cs, Validate - the `cites.target` blank-field door mutated `||`
        // to `&&`. A half-blank target (schemaId present, recordKey blank, or the reverse) must be
        // refused; the mutant only refuses when BOTH are blank.
        // ---------------------------------------------------------------------------------------------
        [TestCase("some-schema", "")]
        [TestCase("", "some-key")]
        public void Validation_RefusesACitesTargetWithOnlyOneOfSchemaIdOrRecordKeyBlank(string schemaId, string recordKey)
        {
            var descriptors = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorNumericLeaf) }).AllDescriptors.ToArray();
            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = schemaId, RecordKey = recordKey } }
            };

            var ex = Assert.Throws<CultNetSelectionInvalidException>(() => selection.Validate(descriptors));
            Assert.That(ex!.Field, Is.EqualTo("cites.target"));
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 3: `member.IndexAlias ?? member.MemberName` mutated to `member.MemberName` at all
        // four sites (CultNetSelectionEvaluator MatchesCitation, BuildIncomingIndex, ReferenceMembers;
        // CultNetSelection.AnySchemaDeclaresRole). Rule (section 2, "The hop"): "role = the member's
        // index alias or name". SurvivorAliasedCiter.Link declares alias "linkRole", which differs from
        // its member name "Link" - the exact case with no prior fixture.
        // ---------------------------------------------------------------------------------------------

        // Door: CultNetSelection.AnySchemaDeclaresRole (line 445). Only the declared alias satisfies
        // cited.role/cites.role - the bare member name does not.
        [TestCase("linkRole", true)]
        [TestCase("Link", false)]
        public void Validation_RoleDoorAcceptsTheDeclaredIndexAliasNotTheMemberName(string role, bool shouldValidate)
        {
            var descriptors = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorAliasTarget), typeof(SurvivorAliasedCiter) }).AllDescriptors.ToArray();
            var selection = new CultNetSelection { Cited = new CultNetIncoming { Role = role, Exists = true } };

            if (shouldValidate)
                Assert.DoesNotThrow(() => selection.Validate(descriptors));
            else
                Assert.Throws<CultNetSelectionInvalidException>(() => selection.Validate(descriptors));
        }

        // Evaluator, cites direction: MatchesCitation (role resolution) and ReferenceMembers (the role
        // filter) both read the alias. The citation names the role by its alias; the edge reported back
        // must also carry the alias, not the member name.
        [Test]
        public void Evaluator_CitesMatchesAndReportsTheDeclaredIndexAliasAsTheEdgeRole()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorAliasTarget), typeof(SurvivorAliasedCiter) });
            var target = new SurvivorAliasTarget { Name = "target" };
            var citer = new SurvivorAliasedCiter { Name = "citer", Link = new CultRecordRef<SurvivorAliasTarget>(new CultRecordKey("target")) };
            var rows = new[] { Row(registry, target, "target", 1), Row(registry, citer, "citer", 2) };

            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation
                {
                    Target = new CultNetRecordRef { SchemaId = registry.GetRequired<SurvivorAliasTarget>().SchemaId, RecordKey = "target" },
                    Role = "linkRole"
                }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "citer" }));
            Assert.That(evaluation.Edges.Select(e => e.Role), Is.EqualTo(new[] { "linkRole" }));
        }

        // Evaluator, cited direction: BuildIncomingIndex (role resolution) and ReferenceMembers again -
        // the same alias, read on the incoming-edge path instead of the outgoing one.
        [Test]
        public void Evaluator_CitedMatchesAndReportsTheDeclaredIndexAliasAsTheEdgeRole()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorAliasTarget), typeof(SurvivorAliasedCiter) });
            var target = new SurvivorAliasTarget { Name = "target" };
            var citer = new SurvivorAliasedCiter { Name = "citer", Link = new CultRecordRef<SurvivorAliasTarget>(new CultRecordKey("target")) };
            var rows = new[] { Row(registry, target, "target", 1), Row(registry, citer, "citer", 2) };

            var selection = new CultNetSelection { Cited = new CultNetIncoming { Role = "linkRole", Exists = true } };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "target" }));
            Assert.That(evaluation.Edges.Select(e => e.Role), Is.EqualTo(new[] { "linkRole" }));
        }

        [CultDocument("cultnet.selection-tests.survivor_alias_target", "cultnet.selection-tests.survivor_alias_target.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SurvivorAliasTarget
        {
            [Key(0)] [CultName] public string Name = string.Empty;
        }

        [CultDocument("cultnet.selection-tests.survivor_aliased_citer", "cultnet.selection-tests.survivor_aliased_citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SurvivorAliasedCiter
        {
            [Key(0)] [CultName] public string Name = string.Empty;

            // The declared index alias ("linkRole") differs from the member name ("Link") - the exact
            // shape section 2's role-naming rule exists for, and no prior fixture had one.
            [Key(1)]
            [CultIndex("linkRole")]
            [CultReference(typeof(SurvivorAliasTarget))]
            public CultRecordRef<SurvivorAliasTarget> Link;
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 4: CultNetSelectionEvaluator.EvaluateAll - `ThenBy`/`ThenByDescending` on schemaId
        // and recordKey mutated, on both the ascending and descending sort expressions. Rule (section 2,
        // "Order"): "(ordinal, schemaId, recordKey) ascending, or the reverse under descending." Every
        // prior test used distinct ordinals, so the schemaId/recordKey tiebreak comparators were never
        // actually invoked by the sort (LINQ's composed comparer short-circuits once a key differs).
        // Three rows share one ordinal so the sort must fall through to schemaId, then recordKey.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void Evaluator_BreaksAnOrdinalTieBySchemaIdThenRecordKeyInBothDirections()
        {
            var registry = CultDocumentRegistry.ForTypes(new[]
            {
                typeof(CultNetSelectionEvaluatorTests.SelLeafA), typeof(CultNetSelectionEvaluatorTests.SelLeafB)
            });
            var schemaA = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId;
            var schemaB = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafB>().SchemaId;

            // All three rows share ordinal 1: two SelLeafA rows (keys "a"/"b") and one SelLeafB row
            // (key "a"). Expected order is derived from the same comparer the evaluator uses, so this
            // test does not depend on which of schemaA/schemaB happens to sort first.
            var rows = new[]
            {
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a-b", Kind = "k", Mass = 1 }, "b", 1),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a-a", Kind = "k", Mass = 1 }, "a", 1),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafB { Name = "b-a", Kind = "k", Mass = 1 }, "a", 1)
            };

            var expectedAscending = rows
                .OrderBy(r => r.Descriptor.SchemaId, CultNetCodePointComparer.Instance)
                .ThenBy(r => r.Key.Value, CultNetCodePointComparer.Instance)
                .Select(r => (r.Descriptor.SchemaId, r.Key.Value))
                .ToArray();

            var ascending = CultNetSelectionEvaluator.Select(registry, rows, new CultNetSelection(), asOf: 1);
            Assert.That(ascending.Rows.Select(r => (r.Descriptor.SchemaId, r.Key.Value)), Is.EqualTo(expectedAscending));

            var descending = CultNetSelectionEvaluator.Select(registry, rows, new CultNetSelection { Descending = true }, asOf: 1);
            Assert.That(descending.Rows.Select(r => (r.Descriptor.SchemaId, r.Key.Value)), Is.EqualTo(expectedAscending.Reverse()));
            // sanity: schemaA/schemaB really are distinct, or this test would not exercise the tiebreak.
            Assert.That(schemaA, Is.Not.EqualTo(schemaB));
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 5: CultNetSelectionEvaluator.EdgesFor - the five ordering keys after page-row
        // position (From.SchemaId, From.Key, Role, To.SchemaId, To.Key) each mutated ThenBy<->
        // ThenByDescending. Rule (R-B): "page-row order, then (from, role, to) in code-point order."
        // Two behavioural angles: the From.SchemaId tiebreak (two differently-schema'd citers, same
        // key, citing one target under the same role), and the Role tiebreak (one citer with two
        // reference members that both resolve to the same target, so From and To tie and only Role
        // differs). To.SchemaId/To.Key could not be isolated: within one hop direction, an edge's
        // (From, Role) already determines its To deterministically (one declared value per member), so
        // two edges sharing an anchor, From and Role always share To too - see the equivalence note at
        // the bottom of this file.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void EdgesFor_BreaksATiedAnchorByFromSchemaIdWhenFromKeysAreEqual()
        {
            var registry = CultDocumentRegistry.ForTypes(new[]
            {
                typeof(CultNetSelectionEvaluatorTests.SelLeafA),
                typeof(CultNetSelectionEvaluatorTests.SelCiter),
                typeof(SurvivorAltCiter)
            });
            var target = new CultNetSelectionEvaluatorTests.SelLeafA { Name = "target", Kind = "k", Mass = 1 };
            // Both citers use record key "same" - only their schemaId differs, isolating that tiebreak
            // from the already-covered From.Key case (CultNetSelectionFixBatchTests, citer-a/citer-b).
            var citerOriginal = new CultNetSelectionEvaluatorTests.SelCiter
            {
                Name = "orig",
                Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("target"))
            };
            var citerAlt = new SurvivorAltCiter { Name = "alt", Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("target")) };
            var rows = new[]
            {
                Row(registry, target, "target", 1),
                Row(registry, citerOriginal, "same", 2),
                Row(registry, citerAlt, "same", 3)
            };

            var schemaOriginal = registry.GetRequired<CultNetSelectionEvaluatorTests.SelCiter>().SchemaId;
            var schemaAlt = registry.GetRequired<SurvivorAltCiter>().SchemaId;
            var expectedFirst = CultNetCodePointComparer.Instance.Compare(schemaOriginal, schemaAlt) <= 0 ? schemaOriginal : schemaAlt;
            var expectedSecond = expectedFirst == schemaOriginal ? schemaAlt : schemaOriginal;

            var selection = new CultNetSelection
            {
                Schemas = new[] { registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId },
                Cited = new CultNetIncoming { Role = "Design", Exists = true }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "target" }));
            Assert.That(evaluation.Edges.Select(e => e.From.Descriptor.SchemaId), Is.EqualTo(new[] { expectedFirst, expectedSecond }));
        }

        [Test]
        public void EdgesFor_BreaksATiedAnchorByRoleWhenFromAndToAreEqual()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SurvivorAliasTarget), typeof(SurvivorDualRoleCiter) });
            var target = new SurvivorAliasTarget { Name = "target" };
            // Both reference members on the one citer point at the same target row, so From and To tie
            // for both edges and only Role can order them.
            var citer = new SurvivorDualRoleCiter
            {
                Name = "citer",
                RefZ = new CultRecordRef<SurvivorAliasTarget>(new CultRecordKey("target")),
                RefA = new CultRecordRef<SurvivorAliasTarget>(new CultRecordKey("target"))
            };
            var rows = new[] { Row(registry, target, "target", 1), Row(registry, citer, "citer", 2) };

            var selection = new CultNetSelection
            {
                Cites = new CultNetCitation
                {
                    Target = new CultNetRecordRef { SchemaId = registry.GetRequired<SurvivorAliasTarget>().SchemaId, RecordKey = "target" }
                    // Role left null: matches any declared reference, so both RefZ (alias "zLastRole")
                    // and RefA (alias "aFirstRole") contribute an edge.
                }
            };
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);

            Assert.That(evaluation.Rows.Select(r => r.Key.Value), Is.EqualTo(new[] { "citer" }));
            // "aFirstRole" sorts before "zLastRole" in code-point order - the opposite of RefZ/RefA's
            // declaration order (Key(1)/Key(2)), so a correct answer cannot come from preserving
            // declaration order by accident.
            Assert.That(evaluation.Edges.Select(e => e.Role), Is.EqualTo(new[] { "aFirstRole", "zLastRole" }));
        }

        [CultDocument("cultnet.selection-tests.survivor_alt_citer", "cultnet.selection-tests.survivor_alt_citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SurvivorAltCiter
        {
            [Key(0)] [CultName] public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(CultNetSelectionEvaluatorTests.SelFixtureMiddle))]
            public CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle> Design;
        }

        [CultDocument("cultnet.selection-tests.survivor_dual_role_citer", "cultnet.selection-tests.survivor_dual_role_citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class SurvivorDualRoleCiter
        {
            [Key(0)] [CultName] public string Name = string.Empty;

            [Key(1)]
            [CultIndex("zLastRole")]
            [CultReference(typeof(SurvivorAliasTarget))]
            public CultRecordRef<SurvivorAliasTarget> RefZ;

            [Key(2)]
            [CultIndex("aFirstRole")]
            [CultReference(typeof(SurvivorAliasTarget))]
            public CultRecordRef<SurvivorAliasTarget> RefA;
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 6: CultNetSelectionCursor.ComputeDigest - the `cites`/`cited` branches (and the
        // field-number presence flag) had no coverage at all. Rule (R-H): "every string and list is
        // length-prefixed, so no delimiter choice can make two different selections collide" - extended
        // here to the branch flags themselves: two selections differing only in whether they carry a
        // hop, or only in a field's number-vs-no-number flag, must not collide.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void ComputeDigest_DiffersWhenOnlyCitesPresenceOrItsRoleDiffers()
        {
            var withoutCites = new CultNetSelection();
            var withCitesNoRole = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = "s", RecordKey = "k" } }
            };
            var withCitesRole = new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = "s", RecordKey = "k" }, Role = "r" }
            };

            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withoutCites),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withCitesNoRole)));
            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withCitesNoRole),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withCitesRole)));
        }

        [Test]
        public void ComputeDigest_DiffersWhenOnlyCitedPresenceOrItsExistsFlagDiffers()
        {
            var withoutCited = new CultNetSelection();
            var citedExistsTrue = new CultNetSelection { Cited = new CultNetIncoming { Role = "r", Exists = true } };
            var citedExistsFalse = new CultNetSelection { Cited = new CultNetIncoming { Role = "r", Exists = false } };

            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", withoutCited),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", citedExistsTrue)));
            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", citedExistsTrue),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", citedExistsFalse)));
        }

        // The number-presence flag: Number = null (an any_of predicate never carries one) must not
        // digest the same as Number = "" (an empty-but-present string) - AppendString alone would render
        // both as a zero-length field, so the trailing '0'/'1' flag is the only thing that tells them
        // apart.
        [Test]
        public void ComputeDigest_DiffersBetweenAnAbsentFieldNumberAndAnEmptyOne()
        {
            var absentNumber = new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "any_of", Values = new[] { "x" } } }
            };
            var emptyNumber = new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "any_of", Values = new[] { "x" }, Number = "" } }
            };

            Assert.That(
                CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", absentNumber),
                Is.Not.EqualTo(CultNetSelectionCursor.ComputeDigest(0, 0, "s", "k", emptyNumber)));
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 7: CultNetSelectionCursor.Parse / ReadLengthPrefixedFields had no coverage at all on
        // attacker-supplied base64. A length prefix claiming more bytes than the payload actually holds,
        // and trailing bytes left over after the five fields, must both refuse cursor_invalid rather
        // than throw an unhandled exception or silently accept the extra bytes.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void CursorParse_RefusesALengthPrefixClaimingMoreBytesThanArePresent()
        {
            // "999:" claims a 999-byte field but supplies only "x" - the door must refuse, not read
            // past the end of the buffer.
            var hostile = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes("999:x"));

            var ex = Assert.Throws<CultNetSelectionCursorException>(() => CultNetSelectionCursor.Parse(hostile));
            Assert.That(ex!.Code, Is.EqualTo("cursor_invalid"));
        }

        [Test]
        public void CursorParse_RefusesTrailingBytesAfterTheFiveFields()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(CultNetSelectionEvaluatorTests.SelLeafA) });
            var rows = new[] { Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "a", 1) };
            var selection = new CultNetSelection { Limit = 1 };
            var key = CultNetSelectionCursorKey.Random();
            var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1, cursorKey: key);
            Assert.That(evaluation.NextCursor, Is.Null, "a single-row set with limit 1 has no next page to mint a cursor from");

            // Mint a real cursor over a two-row set instead, so there is one to tamper with.
            var rows2 = new[]
            {
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "a", 1),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "b", Kind = "k", Mass = 1 }, "b", 2)
            };
            var page = CultNetSelectionEvaluator.Select(registry, rows2, selection, asOf: 1, cursorKey: key);
            Assert.That(page.NextCursor, Is.Not.Null);

            var raw = Base64UrlDecode(page.NextCursor!);
            var withTrailingGarbage = new byte[raw.Length + 3];
            Array.Copy(raw, withTrailingGarbage, raw.Length);
            withTrailingGarbage[raw.Length] = (byte)'x';
            withTrailingGarbage[raw.Length + 1] = (byte)'y';
            withTrailingGarbage[raw.Length + 2] = (byte)'z';
            var tampered = Base64UrlEncode(withTrailingGarbage);

            var ex = Assert.Throws<CultNetSelectionCursorException>(() => CultNetSelectionCursor.Parse(tampered));
            Assert.That(ex!.Code, Is.EqualTo("cursor_invalid"));
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 0: break;
                default: throw new FormatException("Invalid base64url string length.");
            }

            return Convert.FromBase64String(padded);
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 8: FindCursorPosition (descending walk) and ComparePosition (the schemaId-vs-key
        // tiebreak) had no test exercising an equal-ordinal boundary. Two angles: a full descending page
        // walk (mirroring the existing ascending-only walk test), and a cursor boundary where two rows
        // share both ordinal and record key but differ only by schemaId.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void Evaluator_PagesDescendingExactlyOnceEachRow()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(CultNetSelectionEvaluatorTests.SelLeafA) });
            var rows = Enumerable.Range(0, 5)
                .Select(i => Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = $"n{i}", Kind = "k", Mass = i }, $"k{i}", i))
                .ToArray();

            var selection = new CultNetSelection { Limit = 2, Descending = true };
            var seen = new System.Collections.Generic.List<string>();
            string? cursor = null;
            for (var guard = 0; guard < 10; guard++)
            {
                selection.Cursor = cursor;
                var page = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
                seen.AddRange(page.Rows.Select(r => r.Key.Value));
                if (page.NextCursor == null) break;
                cursor = page.NextCursor;
            }

            Assert.That(seen, Is.EqualTo(new[] { "k4", "k3", "k2", "k1", "k0" }));
        }

        // ComparePosition's ordinal.CompareTo ties (both rows ordinal 1, both keyed "m") - only the
        // schemaId comparison can tell the cursor which row it already returned.
        [Test]
        public void Page_ResolvesAnEqualOrdinalEqualKeyCursorBoundaryBySchemaId()
        {
            var registry = CultDocumentRegistry.ForTypes(new[]
            {
                typeof(CultNetSelectionEvaluatorTests.SelLeafA), typeof(CultNetSelectionEvaluatorTests.SelLeafB)
            });
            var rows = new[]
            {
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafA { Name = "a", Kind = "k", Mass = 1 }, "m", 1),
                Row(registry, new CultNetSelectionEvaluatorTests.SelLeafB { Name = "b", Kind = "k", Mass = 1 }, "m", 1)
            };
            var schemaA = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafA>().SchemaId;
            var schemaB = registry.GetRequired<CultNetSelectionEvaluatorTests.SelLeafB>().SchemaId;
            var expectedOrder = CultNetCodePointComparer.Instance.Compare(schemaA, schemaB) <= 0
                ? new[] { schemaA, schemaB }
                : new[] { schemaB, schemaA };

            var selection = new CultNetSelection { Limit = 1 };
            var first = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
            Assert.That(first.Rows.Select(r => r.Descriptor.SchemaId), Is.EqualTo(new[] { expectedOrder[0] }));
            Assert.That(first.NextCursor, Is.Not.Null);

            selection.Cursor = first.NextCursor;
            var second = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
            Assert.That(second.Rows.Select(r => r.Descriptor.SchemaId), Is.EqualTo(new[] { expectedOrder[1] }));
            Assert.That(second.NextCursor, Is.Null);
        }

        // ---------------------------------------------------------------------------------------------
        // Survivor 9: CultNetCodePointComparer's private CodePointAt - the unpaired (lone) surrogate
        // branch. R-C's existing astral-key test always supplies a complete, valid surrogate pair, so
        // the "not actually a pair" branch (documented on the type: "a comparer must not throw on it")
        // was never reached: a high surrogate at the very end of the string (no following char at all),
        // and a high surrogate followed by a non-low-surrogate character.
        // ---------------------------------------------------------------------------------------------
        [Test]
        public void CodePointComparer_OrdersALoneHighSurrogateAtStringEndAsItsOwnCodeUnit()
        {
            // U+D800 (lone high surrogate) as its own value (55296) sorts before U+E000 (57344, BMP
            // private-use) - the same relative order code points would give, confirming the lone
            // surrogate is compared by its raw unit value rather than causing a crash or being skipped.
            var withLoneSurrogateAtEnd = "x\uD800";
            var bmpPrivateUse = "x";

            Assert.That(CultNetCodePointComparer.Instance.Compare(withLoneSurrogateAtEnd, bmpPrivateUse), Is.LessThan(0));
            Assert.That(CultNetCodePointComparer.Instance.Compare(bmpPrivateUse, withLoneSurrogateAtEnd), Is.GreaterThan(0));
        }

        [Test]
        public void CodePointComparer_TreatsAHighSurrogateNotFollowedByALowSurrogateAsOneCodeUnit()
        {
            // The high surrogate is immediately followed by an ordinary BMP character, not a low
            // surrogate - IsLowSurrogate must fail and the comparer must advance by one unit, not two,
            // continuing on to compare "A" vs "B" rather than treating \uD800 + 'A' as a surrogate pair.
            var first = "x\uD800A";
            var second = "x\uD800B";

            Assert.That(CultNetCodePointComparer.Instance.Compare(first, second), Is.LessThan(0));
            Assert.That(CultNetCodePointComparer.Instance.Compare(second, first), Is.GreaterThan(0));
        }
    }
}
