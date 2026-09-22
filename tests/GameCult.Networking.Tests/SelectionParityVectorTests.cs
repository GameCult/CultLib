#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // CultNet typed selection, Cut 1 commit 3/4 (docs/cultnet-selection-cut.md section 6/10, S12):
    // the C#<->Rust parity vectors. New file; no existing C# source is modified.
    //
    // "schemaId" inside the committed vector files is each fixture document's *SchemaName*
    // ("leaf_a", "leaf_b", "citer"), never CultDocumentDescriptor.SchemaId - a SHA-256 content
    // hash over the type's canonical shape that Rust has no way to reproduce independently, and
    // that the reference itself does not run through alias matching everywhere (MatchesCitation
    // compares Citation.Target.SchemaId against the real hash exactly, with no SchemaName
    // fallback). The vectors below therefore only exercise `cited` (role/existence, matched by
    // record key alone - no schema id comparison at all) for the hop, never `cites` (which would
    // need a real cross-runtime-agreed schema id this fixture pair cannot produce). This is a
    // fixture-identity convention local to these vectors, not a claim about the real wire.
    public sealed class SelectionParityVectorTests
    {
        private static CultDocumentRegistry Registry() => CultDocumentRegistry.ForTypes(new[]
        {
            typeof(ParityLeafA), typeof(ParityLeafB), typeof(ParityCiter)
        });

        private static CultNetSelectionEvaluator.Row Row(CultDocumentRegistry registry, object document, string key, long ordinal) =>
            new(registry.GetRequired(document.GetType()), new CultRecordKey(key), document, ordinal);

        // R-C: two rows sharing one ordinal so the tiebreak - not the ordinal - decides their order.
        // U+E000 is a BMP private-use character (code point 0xE000); U+1F602 is astral, encoded as the
        // surrogate pair 😂 whose high half (0xD83D) is less than 0xE000. A UTF-16 code-unit
        // compare (the pre-R-C bug) therefore sorts the astral key first; code-point order sorts it
        // after, since 0x1F602 > 0xE000.
        private const string BmpPrivateUseKey = "-row";
        private const string AstralKey = "😂-row";

        // Unchanged: this exact row set is what the committed selection-vectors.rs-written.json was
        // computed against on the Rust side (an independently constructed but value-identical fixture,
        // per the comment on Cases() below) - SelectionVectorsWrittenByRustDecodeAndEvaluateIdenticallyInTheReference
        // reads that committed file, so this fixture cannot grow without breaking that comparison until
        // the Rust side's own fixture grows to match. New rows for the fix-batch vectors live in
        // BuildExtendedFixture, used only by WriteSelectionVectors (this cut's own cs-written.json).
        private static (CultDocumentRegistry registry, CultNetSelectionEvaluator.Row[] rows) BuildFixture()
        {
            var registry = Registry();
            var rows = new[]
            {
                Row(registry, new ParityLeafA { Name = "a-lo", Kind = "weapon", Mass = 4 }, "a-lo", 1),
                Row(registry, new ParityLeafA { Name = "a-eq", Kind = "weapon", Mass = 5 }, "a-eq", 2),
                Row(registry, new ParityLeafB { Name = "b-hi", Kind = "armor", Mass = 6 }, "b-hi", 3),
                Row(registry, new ParityLeafA { Name = "shared-1", Kind = "shared", Mass = 1 }, "shared-1", 4),
                Row(registry, new ParityLeafB { Name = "shared-2", Kind = "shared", Mass = 1 }, "shared-2", 5),
                Row(registry, new ParityLeafA { Name = "shared-3", Kind = "shared", Mass = 1 }, "shared-3", 6),
                Row(registry, new ParityCiter { Name = "citer-1", Design = new CultRecordRef<ParityMiddle>(new CultRecordKey("a-eq")) }, "citer-1", 7),
            };
            return (registry, rows);
        }

        // R-B/R-C/R-D/R-E/R-I fix-batch rows, additive over BuildFixture so every pre-existing
        // selection's match set is unchanged (a new row reachable by an unfiltered or loosely filtered
        // case, such as descending_with_limit or gt_numeric_boundary, would otherwise silently change
        // that case's expected page). Only WriteSelectionVectors (this cut's cs-written.json) uses this.
        private static (CultDocumentRegistry registry, CultNetSelectionEvaluator.Row[] rows) BuildExtendedFixture()
        {
            var (registry, baseRows) = BuildFixture();
            var extraRows = new[]
            {
                // R-D: 2233759.25f is exactly representable and sits on a round-to-even tie between two
                // .NET-shortest-form spellings ("2233759.2" vs "2233759.3" between runtimes/versions);
                // its exact canonical decimal is "2233759.25" either way. Kind "tie" so it cannot be
                // swept up by an existing weapon/armor/shared-kind case.
                Row(registry, new ParityLeafA { Name = "tie-mass", Kind = "tie", Mass = 2233759.25f }, "tie-mass", 8),
                // R-C fixture rows: same ordinal, astral vs BMP-private-use keys (see the constants
                // above). Kind "unicode" and Mass 0 so neither an unfiltered nor a mass>1 case reaches them.
                Row(registry, new ParityLeafA { Name = "bmp", Kind = "unicode", Mass = 0 }, BmpPrivateUseKey, 9),
                Row(registry, new ParityLeafA { Name = "astral", Kind = "unicode", Mass = 0 }, AstralKey, 9),
            };
            return (registry, baseRows.Concat(extraRows).ToArray());
        }

        // The same six selections packages/cultnet-rs/tests/selection.rs's
        // write_selection_vectors_for_the_reference builds against its own fixture rows (which
        // carry the same schema names, record keys, ordinals and declared values as BuildFixture
        // above, independently constructed - a within-runtime round trip pins nothing, section 10).
        private static IEnumerable<(string Name, CultNetSelection Selection)> Cases()
        {
            yield return ("any_of_plus_ge_conjunction", new CultNetSelection
            {
                Fields = new[]
                {
                    new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "weapon" } },
                    new CultNetFieldPredicate { Index = "mass", Op = "ge", Number = "5" }
                }
            });
            yield return ("descending_with_limit", new CultNetSelection { Descending = true, Limit = 3 });
            yield return ("gt_numeric_boundary", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "gt", Number = "1" } }
            });
            yield return ("incoming_exists_true", new CultNetSelection
            {
                Schemas = new[] { "leaf_a" },
                Cited = new CultNetIncoming { Role = "Design", Exists = true }
            });
            yield return ("incoming_negation", new CultNetSelection
            {
                Schemas = new[] { "leaf_a" },
                Cited = new CultNetIncoming { Role = "Design", Exists = false }
            });
            yield return ("shared_index_value_three_rows", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "shared" } } }
            });
            // R-D: the row's canonical rendering of 2233759.25f is its exact decimal expansion, not a
            // shortest round-trip form that could round the tie the other way - ge against the exact
            // value must match.
            yield return ("float_tie_exact_ge", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "mass", Op = "ge", Number = "2233759.25" } }
            });
            // R-E: cites.target.schemaId goes through the alias matcher, so "leaf_a" (ParityLeafA's
            // SchemaName) matches a-eq's real content-hash SchemaId, same as `schemas` already does.
            yield return ("cites_target_by_alias", new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = "leaf_a", RecordKey = "a-eq" } }
            });
            // R-C: BmpPrivateUseKey (U+E000) sorts before AstralKey (U+1F602) in code-point order,
            // despite AstralKey's leading UTF-16 code unit (0xD83D) being numerically smaller.
            yield return ("astral_key_code_point_order", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "unicode" } } }
            });
            // R-I: page bytes under each projection, over the same match set.
            yield return ("header_projection", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "unicode" } } },
                Projection = CultNetSelectionProjections.Header
            });
            yield return ("document_projection", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "unicode" } } },
                Projection = CultNetSelectionProjections.Document
            });
        }

        // Self's ruling, 2026-09-22 (commit 2 fix batch): door-refusal vectors. Never evaluated on
        // either side - only decoded and handed to Validate/validate, which must refuse at exactly
        // the named field. Mirrors packages/cultnet-rs/tests/selection.rs's refusal_cases.
        private static IEnumerable<(string Name, CultNetSelection Selection, string ExpectedField)> RefusalCases()
        {
            yield return ("refuses_empty_schemas_list", new CultNetSelection { Schemas = Array.Empty<string>() }, "schemas");
            yield return ("refuses_blank_key_entry", new CultNetSelection { Keys = new[] { "a-eq", "   " } }, "keys");
        }

        private static string RowId(CultNetSelectionEvaluator.Row row) => $"{row.Descriptor.SchemaName}/{row.Key.Value}";

        // Vectors written by Rust (packages/cultnet-rs/tests/selection.rs,
        // write_selection_vectors_for_the_reference) decode and evaluate identically here.
        [Test]
        public void SelectionVectorsWrittenByRustDecodeAndEvaluateIdenticallyInTheReference()
        {
            var path = Path.Combine(RepoRoot(), "contracts", "cultnet", "interop", "selection-vectors.rs-written.json");
            if (!File.Exists(path))
                Assert.Inconclusive($"{path} is missing - run cargo test -p cultnet-rs --test selection with CULTNET_WRITE_VECTORS=1 first.");

            var (registry, rows) = BuildFixture();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var vectors = document.RootElement.GetProperty("vectors");
            Assert.That(vectors.GetArrayLength(), Is.GreaterThan(0), "the vector file must carry at least one vector");

            foreach (var vector in vectors.EnumerateArray())
            {
                var name = vector.GetProperty("name").GetString();
                var selectionBytes = Convert.FromBase64String(vector.GetProperty("selectionMessagePackBase64").GetString()!);
                var selection = MessagePackSerializer.Deserialize<CultNetSelection>(selectionBytes, CultNetSchemaMessageSerialization.Options);
                var asOf = vector.GetProperty("asOf").GetUInt64();

                if (vector.TryGetProperty("expectedRefusalField", out var refusalField) && refusalField.ValueKind != JsonValueKind.Null)
                {
                    var error = Assert.Throws<CultNetSelectionInvalidException>(() => selection.Validate(registry.AllDescriptors.ToArray()), $"vector {name} must be refused");
                    Assert.That(error!.Field, Is.EqualTo(refusalField.GetString()), $"vector {name}: refusal field");
                    continue;
                }

                var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf);

                var expectedIds = vector.GetProperty("expectedIds").EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.That(evaluation.Rows.Select(RowId).ToArray(), Is.EqualTo(expectedIds), $"vector {name}: row ids");
                Assert.That((uint)evaluation.Rows.Count, Is.EqualTo(vector.GetProperty("matched").GetUInt32()), $"vector {name}: matched");
                Assert.That(evaluation.NextCursor != null, Is.EqualTo(vector.GetProperty("hasNext").GetBoolean()), $"vector {name}: hasNext");

                var expectedEdges = vector.GetProperty("expectedEdges").EnumerateArray().ToArray();
                Assert.That(evaluation.Edges.Count, Is.EqualTo(expectedEdges.Length), $"vector {name}: edge count");
                for (var i = 0; i < expectedEdges.Length; i++)
                {
                    var edge = evaluation.Edges[i];
                    var expectedEdge = expectedEdges[i];
                    Assert.That(RowId(edge.From), Is.EqualTo(expectedEdge.GetProperty("fromId").GetString()), $"vector {name}: edge[{i}].from");
                    Assert.That(edge.Role, Is.EqualTo(expectedEdge.GetProperty("role").GetString()), $"vector {name}: edge[{i}].role");
                    Assert.That(RowId(edge.To), Is.EqualTo(expectedEdge.GetProperty("toId").GetString()), $"vector {name}: edge[{i}].to");
                    var expectedPayload = expectedEdge.GetProperty("payloadBase64");
                    if (expectedPayload.ValueKind == JsonValueKind.Null)
                        Assert.That(edge.Payload, Is.Null, $"vector {name}: edge[{i}].payload");
                    else
                        Assert.That(Convert.ToBase64String((byte[])edge.Payload!), Is.EqualTo(expectedPayload.GetString()), $"vector {name}: edge[{i}].payload");
                }
            }
        }

        // Writes contracts/cultnet/interop/selection-vectors.cs-written.json for Rust's
        // selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust to
        // judge. Gated the same way the QUIC campaign's C#-written vectors are
        // (docs/typescript-quic-realtime-cut.md:15-21): committed, not regenerated on every run.
        [Test]
        public void WriteSelectionVectors()
        {
            if (Environment.GetEnvironmentVariable("CULTNET_WRITE_VECTORS") != "1")
                Assert.Ignore("Set CULTNET_WRITE_VECTORS=1 to (re)write the committed vector file.");

            var (registry, rows) = BuildExtendedFixture();
            var vectors = new List<object>();
            foreach (var (name, selection) in Cases())
            {
                var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
                var selectionBytes = MessagePackSerializer.Serialize(selection, CultNetSchemaMessageSerialization.Options);

                vectors.Add(new
                {
                    name,
                    selectionMessagePackBase64 = Convert.ToBase64String(selectionBytes),
                    asOf = 1UL,
                    expectedRefusalField = (string?)null,
                    expectedIds = evaluation.Rows.Select(RowId).ToArray(),
                    matched = (uint)evaluation.Rows.Count,
                    hasNext = evaluation.NextCursor != null,
                    expectedEdges = evaluation.Edges.Select(edge => new
                    {
                        fromId = RowId(edge.From),
                        role = edge.Role,
                        toId = RowId(edge.To),
                        // None of this file's six vectors hop through a many-dictionary reference
                        // (the only shape D11 gives a non-null payload), so this is always null
                        // here; packages/cultnet-rs/tests/selection.rs's
                        // dictionary_reference_entries_keep_distinct_payload_bytes pins the
                        // payload-carrying case directly against Row::references() instead.
                        payloadBase64 = edge.Payload == null ? null : Convert.ToBase64String((byte[])edge.Payload)
                    }).ToArray()
                });
            }

            // Self's ruling, 2026-09-22 (commit 2 fix batch): door-refusal vectors. Never
            // evaluated on either side - only decoded and handed to Validate, which must refuse
            // at exactly the named field.
            foreach (var (name, selection, expectedField) in RefusalCases())
            {
                var error = Assert.Throws<CultNetSelectionInvalidException>(
                    () => selection.Validate(registry.AllDescriptors.ToArray()),
                    $"{name} must be refused by the reference before it can be committed as a refusal vector");
                Assert.That(error!.Field, Is.EqualTo(expectedField), $"{name}: refusal field");

                var selectionBytes = MessagePackSerializer.Serialize(selection, CultNetSchemaMessageSerialization.Options);
                vectors.Add(new
                {
                    name,
                    selectionMessagePackBase64 = Convert.ToBase64String(selectionBytes),
                    asOf = 1UL,
                    expectedRefusalField = (string?)expectedField,
                    expectedIds = Array.Empty<string>(),
                    matched = 0U,
                    hasNext = false,
                    expectedEdges = Array.Empty<object>()
                });
            }

            var directory = Path.Combine(RepoRoot(), "contracts", "cultnet", "interop");
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new { vectors }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "selection-vectors.cs-written.json"), json);
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
        }

        // A conceptual abstract middle (no [CultDocument]), matching Design's declared target so
        // D9's leaf-set resolution reaches both ParityLeafA and ParityLeafB. SchemaName is the
        // literal fixture-identity string ("leaf_a", "leaf_b", "citer") the vectors compare by -
        // see this file's header comment.
        public abstract class ParityMiddle
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultIndex("kind")]
            public string Kind = string.Empty;

            [Key(2)]
            [CultIndex("mass")]
            public float Mass;
        }

        [CultDocument("leaf_a", "cultnet.selection-parity.leaf_a.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ParityLeafA : ParityMiddle
        {
        }

        [CultDocument("leaf_b", "cultnet.selection-parity.leaf_b.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ParityLeafB : ParityMiddle
        {
        }

        [CultDocument("citer", "cultnet.selection-parity.citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ParityCiter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(ParityMiddle))]
            public CultRecordRef<ParityMiddle> Design;
        }
    }
}
