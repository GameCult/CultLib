#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Self's ruling, 2026-09-22 (shared-fixture ruling, on top of the Cut 1 fix batch): both this
    // writer and packages/cultnet-rs/tests/selection.rs's write_selection_vectors_for_the_reference
    // build their rows from the one shared
    // contracts/cultnet/interop/selection-vectors.fixture.json, keyed by each schema's real
    // SHA-256 content-hash id, exactly as CultDocumentRegistry computes it for the fixture's own
    // document types below - never by schema *name* as a stand-in id, which let every vector that
    // carried an id decode on the wrong runtime's terms (Soul's whole-cut pass, F3: a name-as-id
    // fixture masked the real alias-matching gap between CultNetSchemaAliasMatching and
    // cultnet_rs::selection::schema_alias). AssertSchemaIdMatches below fails loudly, not silently,
    // if this file's reflected shape ever drifts from the committed fixture.
    public sealed class SelectionParityVectorTests
    {
        private static CultDocumentRegistry Registry() => CultDocumentRegistry.ForTypes(new[]
        {
            typeof(ParityLeafA), typeof(ParityLeafB), typeof(ParityCiter), typeof(ParityCiterNarrow)
        });

        private static CultNetSelectionEvaluator.Row Row(CultDocumentRegistry registry, object document, string key, long ordinal) =>
            new(registry.GetRequired(document.GetType()), new CultRecordKey(key), document, ordinal);

        // ----------------------------------------------------------------------------------------
        // The shared fixture: contracts/cultnet/interop/selection-vectors.fixture.json. Holds the
        // real schema ids (and their unversioned aliases) and every row either runtime needs:
        // astral/BMP-private-use keys (R-C), a float tie (R-D), a cites/cited pair, and a cites
        // target reached by alias (R-E). Both writers build from it; neither hand-rolls its own row
        // set any more.
        // ----------------------------------------------------------------------------------------

        // R-E: two alias forms, because the two runtimes' schema-alias matchers key off different
        // attributes and no candidate resolves through both (see the fixture file's "//aliases"
        // note and this file's report of the confirmed defect). NameAlias is what
        // CultNetSchemaAliasMatching's descriptor overload (the only one any C# call site uses)
        // can resolve; HashAlias is what cultnet_rs::selection::schema_alias can resolve.
        private sealed record FixtureSchema(string SchemaId, string NameAlias, string HashAlias);

        // R-AF: no TargetSchema - the wire a stored reference actually travels on carries a bare
        // record key (GameCult.Caching.MessagePack.CultRecordRefFormatter<T> writes only
        // value.Key.Value), so the fixture stopped pretending otherwise. See the fixture file's
        // "//references" note.
        private sealed record FixtureReference(string Role, string TargetKey);

        private sealed record FixtureRow(
            string Schema, string Key, long Ordinal,
            IReadOnlyDictionary<string, string> Fields,
            IReadOnlyList<FixtureReference> References);

        private static string FixturePath() =>
            Path.Combine(RepoRoot(), "contracts", "cultnet", "interop", "selection-vectors.fixture.json");

        private static (IReadOnlyDictionary<string, FixtureSchema> Schemas, IReadOnlyList<FixtureRow> Rows) LoadFixture()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
            var root = document.RootElement;

            var schemas = new Dictionary<string, FixtureSchema>(StringComparer.Ordinal);
            foreach (var entry in root.GetProperty("schemas").EnumerateObject())
            {
                schemas[entry.Name] = new FixtureSchema(
                    entry.Value.GetProperty("schemaId").GetString()!,
                    entry.Value.GetProperty("nameAlias").GetString()!,
                    entry.Value.GetProperty("hashAlias").GetString()!);
            }

            var rows = new List<FixtureRow>();
            foreach (var row in root.GetProperty("rows").EnumerateArray())
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (row.TryGetProperty("fields", out var fieldsElement))
                {
                    foreach (var field in fieldsElement.EnumerateObject())
                        fields[field.Name] = field.Value.GetString()!;
                }

                var references = new List<FixtureReference>();
                if (row.TryGetProperty("references", out var referencesElement))
                {
                    foreach (var reference in referencesElement.EnumerateArray())
                    {
                        references.Add(new FixtureReference(
                            reference.GetProperty("role").GetString()!,
                            reference.GetProperty("targetKey").GetString()!));
                    }
                }

                rows.Add(new FixtureRow(
                    row.GetProperty("schema").GetString()!,
                    row.GetProperty("key").GetString()!,
                    row.GetProperty("ordinal").GetInt64(),
                    fields,
                    references));
            }

            return (schemas, rows);
        }

        private static void AssertSchemaIdMatches(CultDocumentRegistry registry, Type type, string expected)
        {
            var actual = registry.GetRequired(type).SchemaId;
            Assert.That(actual, Is.EqualTo(expected),
                $"{type.Name}'s real schema id drifted from contracts/cultnet/interop/selection-vectors.fixture.json - " +
                "regenerate the fixture (its schemas.*.schemaId/alias fields) from CultDocumentRegistry before trusting these vectors.");
        }

        private static object BuildDocument(FixtureRow row) => row.Schema switch
        {
            "leaf_a" => new ParityLeafA
            {
                Name = row.Key, Kind = row.Fields["kind"], Mass = float.Parse(row.Fields["mass"], CultureInfo.InvariantCulture)
            },
            "leaf_b" => new ParityLeafB
            {
                Name = row.Key, Kind = row.Fields["kind"], Mass = float.Parse(row.Fields["mass"], CultureInfo.InvariantCulture)
            },
            "citer" => new ParityCiter
            {
                Name = row.Key,
                Design = new CultRecordRef<ParityMiddle>(new CultRecordKey(
                    row.References.Single(reference => reference.Role == "Design").TargetKey))
            },
            // R-AF: citer_narrow's reference is declared to ParityLeafA only (one leaf, not
            // ParityMiddle) - the shared-key shape needs a role whose declared target excludes at
            // least one of the schemas that can share a record key.
            "citer_narrow" => new ParityCiterNarrow
            {
                Name = row.Key,
                NarrowRef = new CultRecordRef<ParityLeafA>(new CultRecordKey(
                    row.References.Single(reference => reference.Role == "narrow_ref").TargetKey))
            },
            _ => throw new InvalidOperationException($"selection-vectors.fixture.json: unknown schema '{row.Schema}'")
        };

        // The one row set: both SelectionVectorsWrittenByRustDecodeAndEvaluateIdenticallyInTheReference
        // (reading rs-written) and WriteSelectionVectors (writing cs-written) build from it, so both
        // committed vector files are regenerated together whenever the fixture changes.
        private static (CultDocumentRegistry registry, CultNetSelectionEvaluator.Row[] rows, IReadOnlyDictionary<string, FixtureSchema> schemas) BuildFixture()
        {
            var registry = Registry();
            var (schemas, fixtureRows) = LoadFixture();

            AssertSchemaIdMatches(registry, typeof(ParityLeafA), schemas["leaf_a"].SchemaId);
            AssertSchemaIdMatches(registry, typeof(ParityLeafB), schemas["leaf_b"].SchemaId);
            AssertSchemaIdMatches(registry, typeof(ParityCiter), schemas["citer"].SchemaId);
            AssertSchemaIdMatches(registry, typeof(ParityCiterNarrow), schemas["citer_narrow"].SchemaId);

            var rows = fixtureRows.Select(row => Row(registry, BuildDocument(row), row.Key, row.Ordinal)).ToArray();
            return (registry, rows, schemas);
        }

        private static IEnumerable<(string Name, CultNetSelection Selection)> Cases(IReadOnlyDictionary<string, FixtureSchema> schemas)
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
            // R-E: the exact real schema id - matches on both runtimes' first (exact string)
            // comparison branch, independent of the alias-matching gap below.
            yield return ("incoming_exists_true", new CultNetSelection
            {
                Schemas = new[] { schemas["leaf_a"].SchemaId },
                Cited = new CultNetIncoming { Role = "Design", Exists = true }
            });
            yield return ("incoming_negation", new CultNetSelection
            {
                Schemas = new[] { schemas["leaf_a"].SchemaId },
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
            // R-E: the exact real schema id, mirroring cultnet-rs's hop_by_role_cites_exact_id.
            // R-AF: Keys=["citer-1"] (the fixture's one row that actually cites a-eq via Design)
            // scopes this away from citer_narrow's rows, as hygiene rather than necessity -
            // landing R-AF's vectors surfaced a real R-T/R-W parity bug this exposed: Rust's
            // matches_citation used to resolve and R-W-check every declared reference a row
            // carries before filtering by the citation's own role, where C#'s
            // ReferenceMembers(descriptor, citation.Role) filters by role first
            // (CultNetSelectionEvaluator.cs:451-458). Fixed in cultnet_rs::selection::matches_citation
            // to filter first too, so narrow-citer-refused's out-of-target narrow_ref edge no longer
            // reaches a Role="Design" citation in either runtime - this vector keeps the Keys scope
            // anyway so it never depends on that.
            yield return ("hop_by_role_cites_exact_id", new CultNetSelection
            {
                Keys = new[] { "citer-1" },
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = schemas["leaf_a"].SchemaId, RecordKey = "a-eq" }, Role = "Design" }
            });
            // R-E: the same citation, but the target is named by its C#-resolvable alias
            // ("leaf_a.v9") rather than the exact id. CultNetSchemaAliasMatching's descriptor overload
            // (CultNetDatabase.cs:74-87, the only overload any `schemas`/`cites.target.schemaId` call
            // site in src/ uses) strips the trailing ".v9" and compares "leaf_a" to
            // ParityLeafA's descriptor.SchemaName - a real match here, in the reference. S-12
            // (2026-09-22) is not a leftover defect: cultnet_rs::selection::schema_alias now resolves
            // the same way, through Row::schema_name/RowSet::schema_name standing in for
            // CultDocumentDescriptor.SchemaName (packages/cultnet-rs/src/selection.rs:452-475), so
            // this vector's expectedIds are real and non-empty in both runtimes - the alias port made
            // the two DIFFERENT alias forms deliberate (see the fixture file's "//aliases" note)
            // rather than leaving this one unresolved on the Rust side.
            // R-AF: Role="Design" plus Keys=["citer-1"] for the same reason as
            // hop_by_role_cites_exact_id above.
            yield return ("cites_target_by_name_alias", new CultNetSelection
            {
                Keys = new[] { "citer-1" },
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = schemas["leaf_a"].NameAlias, RecordKey = "a-eq" }, Role = "Design" }
            });
            // R-E: `schemas` reached by the same name alias. Same one-sided-match caveat as above.
            yield return ("schemas_by_name_alias", new CultNetSelection { Schemas = new[] { schemas["leaf_a"].NameAlias } });
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
            // R-AJ: a tied ordinal across two DIFFERENT schemas (kind=tie-schema is leaf_b/"zz-tie-
            // schema" and leaf_a/"aa-tie-schema") - the shape the row order's schemaId tiebreak
            // needs and the astral/BMP pair above never covered (both those rows are leaf_a). The
            // fixture's keys are anti-correlated with the real schema ids on purpose, so a
            // schemaId-first order and a recordKey-first order disagree: only the correct
            // composition puts leaf_b's row ("zz-tie-schema") first.
            yield return ("row_tiebreak_schema_id_before_record_key_at_tied_ordinal", new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "tie-schema" } } }
            });
            // R-AF: "dup-key" carries two rows (leaf_a and leaf_b); citer_narrow's narrow_ref
            // targets leaf_a only. Keys scopes the citer candidate set to narrow-citer-accepted
            // alone, so this vector never touches narrow-citer-refused's bad edge (R-W resolves
            // every reference member unconditionally, so leaving both citer_narrow rows in one
            // candidate set would always refuse regardless of which edge the query named). One
            // rule, derived from the wire (docs/cultnet-selection-cut.md, R-AF): resolving "the
            // row this edge names" is by record key plus the declared leaf set, not an exact
            // (schema, key) pair, so this must accept in both runtimes and resolve to the leaf_a
            // row even though a leaf_b row shares the same key.
            yield return ("shared_key_narrow_ref_resolves_leaf_within_target", new CultNetSelection
            {
                Keys = new[] { "narrow-citer-accepted" },
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = schemas["leaf_a"].SchemaId, RecordKey = "dup-key" }, Role = "narrow_ref" }
            });
        }

        // R-AF: the shared-key shape's other direction - the only row at the declared edge's key is
        // outside the reference's declared target, so evaluating the selection itself must refuse
        // (CultNetSelectionReferenceOutsideTargetException/SelectionRefusal::ReferenceOutsideTarget),
        // not merely return an empty page. Keys scopes the candidate set to narrow-citer-refused
        // alone (see the comment on shared_key_narrow_ref_resolves_leaf_within_target above).
        private static IEnumerable<(string Name, CultNetSelection Selection)> EvaluationRefusalCases(
            IReadOnlyDictionary<string, FixtureSchema> schemas)
        {
            yield return ("shared_key_narrow_ref_refuses_when_only_candidate_is_outside_target", new CultNetSelection
            {
                Keys = new[] { "narrow-citer-refused" },
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = schemas["leaf_a"].SchemaId, RecordKey = "narrow-only" }, Role = "narrow_ref" }
            });
        }

        // Self's ruling, 2026-09-22 (commit 2 fix batch): door-refusal vectors. Never evaluated on
        // either side - only decoded and handed to Validate/validate, which must refuse at exactly
        // the named field. Mirrors packages/cultnet-rs/tests/selection.rs's refusal_cases.
        //
        // refuses_unmatched_cites_target_schema and refuses_cites_target_by_hash_shaped_alias now
        // land here too: the C# matrix Hands pass added the door check
        // (CultNetSelection.cs:389-394) that CultNetSelection.Validate refuses a cites.target.schemaId
        // that CultNetSchemaAliasMatching cannot resolve to any declared schema. Under Self's ruling
        // 2026-09-22 ("the C# reference's alias rule is the rule"), C# only ever resolves a name-form
        // alias, never a hash-shaped one, so a hash-shaped cites.target.schemaId is exactly such an
        // unresolved target and is refused at the same field.
        private static IEnumerable<(string Name, CultNetSelection Selection, string ExpectedField)> RefusalCases(
            IReadOnlyDictionary<string, FixtureSchema> schemas)
        {
            yield return ("refuses_empty_schemas_list", new CultNetSelection { Schemas = Array.Empty<string>() }, "schemas");
            yield return ("refuses_blank_key_entry", new CultNetSelection { Keys = new[] { "a-eq", "   " } }, "keys");
            yield return ("refuses_unmatched_cites_target_schema", new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = "sha256:not-a-declared-schema", RecordKey = "a-eq" } }
            }, "cites.target.schemaId");
            yield return ("refuses_cites_target_by_hash_shaped_alias", new CultNetSelection
            {
                Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = schemas["leaf_a"].HashAlias, RecordKey = "a-eq" } }
            }, "cites.target.schemaId");
        }

        // R-E: id, not name - a row's cross-runtime identity is its real schema id plus its key, the
        // same shape cultnet-rs's row_id() produces.
        private static string RowId(CultNetSelectionEvaluator.Row row) => $"{row.Descriptor.SchemaId}/{row.Key.Value}";

        // Vectors written by Rust (packages/cultnet-rs/tests/selection.rs,
        // write_selection_vectors_for_the_reference) decode and evaluate identically here.
        [Test]
        public void SelectionVectorsWrittenByRustDecodeAndEvaluateIdenticallyInTheReference()
        {
            var path = Path.Combine(RepoRoot(), "contracts", "cultnet", "interop", "selection-vectors.rs-written.json");
            if (!File.Exists(path))
                Assert.Inconclusive($"{path} is missing - run cargo test -p cultnet-rs --test selection with CULTNET_WRITE_VECTORS=1 first.");

            var (registry, rows, _) = BuildFixture();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var vectors = document.RootElement.GetProperty("vectors");
            Assert.That(vectors.GetArrayLength(), Is.GreaterThan(0), "the vector file must carry at least one vector");

            // Self's ruling, 2026-09-22 (shared-fixture ruling): a real parity defect is reported in
            // full, not truncated by whichever vector happened to come first in the file - every
            // vector is judged and every mismatch collected before the test fails once, mirroring
            // packages/cultnet-rs/tests/selection.rs's own reader.
            var failures = new List<string>();
            foreach (var vector in vectors.EnumerateArray())
            {
                var name = vector.GetProperty("name").GetString();
                var selectionBytes = Convert.FromBase64String(vector.GetProperty("selectionMessagePackBase64").GetString()!);
                var selection = MessagePackSerializer.Deserialize<CultNetSelection>(selectionBytes, CultNetSchemaMessageSerialization.Options);
                var asOf = vector.GetProperty("asOf").GetUInt64();

                if (vector.TryGetProperty("expectedRefusalField", out var refusalField) && refusalField.ValueKind != JsonValueKind.Null)
                {
                    try
                    {
                        selection.Validate(registry.AllDescriptors.ToArray());
                        failures.Add($"{name}: expected refusal at '{refusalField.GetString()}' but the reference accepted it");
                    }
                    catch (CultNetSelectionInvalidException error)
                    {
                        if (error.Field != refusalField.GetString())
                            failures.Add($"{name}: refusal field - expected '{refusalField.GetString()}', got '{error.Field}'");
                    }
                    continue;
                }

                // R-AF: a small, separate signal from expectedRefusalField above - that one is the
                // door (Validate, never touches Select); this one names an evaluation-time typed
                // refusal Select itself must throw (currently only reference_outside_target, S18).
                // Deliberately narrower than R-Z/R-AH's page-bytes-and-code parity harness (not
                // this cut's job): it checks which typed refusal fires, nothing about wire bytes.
                if (vector.TryGetProperty("expectedEvaluationRefusal", out var evalRefusal) && evalRefusal.ValueKind != JsonValueKind.Null)
                {
                    var expectedKind = evalRefusal.GetString();
                    try
                    {
                        CultNetSelectionEvaluator.Select(registry, rows, selection, asOf);
                        failures.Add($"{name}: expected Select to refuse ({expectedKind}) but it accepted the selection");
                    }
                    catch (CultNetSelectionReferenceOutsideTargetException) when (expectedKind == "reference_outside_target")
                    {
                        // expected
                    }
                    catch (Exception error)
                    {
                        failures.Add($"{name}: expected Select to refuse with {expectedKind}, got {error.GetType().Name}: {error.Message}");
                    }
                    continue;
                }

                CultNetSelectionEvaluator.Evaluation evaluation;
                try
                {
                    evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf);
                }
                catch (Exception error)
                {
                    var expectedIds = vector.GetProperty("expectedIds").EnumerateArray().Select(e => e.GetString()).ToArray();
                    failures.Add($"{name}: expected real evaluation (expectedIds=[{string.Join(", ", expectedIds)}]) but Select threw {error.GetType().Name}: {error.Message}");
                    continue;
                }

                var expectedIdsList = vector.GetProperty("expectedIds").EnumerateArray().Select(e => e.GetString()).ToArray();
                var actualIds = evaluation.Rows.Select(RowId).ToArray();
                if (!actualIds.SequenceEqual(expectedIdsList))
                    failures.Add($"{name}: row ids - expected [{string.Join(", ", expectedIdsList)}], got [{string.Join(", ", actualIds)}]");

                var expectedMatched = vector.GetProperty("matched").GetUInt32();
                if ((uint)evaluation.TotalMatched != expectedMatched)
                    failures.Add($"{name}: matched - expected {expectedMatched}, got {evaluation.TotalMatched}");

                var expectedHasNext = vector.GetProperty("hasNext").GetBoolean();
                if ((evaluation.NextCursor != null) != expectedHasNext)
                    failures.Add($"{name}: hasNext - expected {expectedHasNext}, got {evaluation.NextCursor != null}");

                var expectedEdges = vector.GetProperty("expectedEdges").EnumerateArray().ToArray();
                if (evaluation.Edges.Count != expectedEdges.Length)
                {
                    failures.Add($"{name}: edge count - expected {expectedEdges.Length}, got {evaluation.Edges.Count}");
                    continue;
                }
                for (var i = 0; i < expectedEdges.Length; i++)
                {
                    var edge = evaluation.Edges[i];
                    var expectedEdge = expectedEdges[i];
                    var expectedPayload = expectedEdge.GetProperty("payloadBase64");
                    var actualPayload = edge.Payload == null ? null : Convert.ToBase64String((byte[])edge.Payload);
                    var payloadMatches = expectedPayload.ValueKind == JsonValueKind.Null
                        ? actualPayload == null
                        : actualPayload == expectedPayload.GetString();
                    if (RowId(edge.From) != expectedEdge.GetProperty("fromId").GetString() ||
                        edge.Role != expectedEdge.GetProperty("role").GetString() ||
                        RowId(edge.To) != expectedEdge.GetProperty("toId").GetString() ||
                        !payloadMatches)
                    {
                        failures.Add($"{name}: edge[{i}] mismatch");
                    }
                }
            }

            Assert.That(failures, Is.Empty,
                $"{failures.Count} of {vectors.GetArrayLength()} vectors disagreed with the reference:\n" + string.Join("\n", failures));
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

            var (registry, rows, schemas) = BuildFixture();
            var vectors = new List<object>();
            foreach (var (name, selection) in Cases(schemas))
            {
                var evaluation = CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1);
                var selectionBytes = MessagePackSerializer.Serialize(selection, CultNetSchemaMessageSerialization.Options);

                vectors.Add(new
                {
                    name,
                    selectionMessagePackBase64 = Convert.ToBase64String(selectionBytes),
                    asOf = 1UL,
                    expectedRefusalField = (string?)null,
                    expectedEvaluationRefusal = (string?)null,
                    expectedIds = evaluation.Rows.Select(RowId).ToArray(),
                    matched = (uint)evaluation.TotalMatched,
                    hasNext = evaluation.NextCursor != null,
                    expectedEdges = evaluation.Edges.Select(edge => new
                    {
                        fromId = RowId(edge.From),
                        role = edge.Role,
                        toId = RowId(edge.To),
                        // None of this file's cases hop through a many-dictionary reference (the only
                        // shape D11 gives a non-null payload), so this is always null here;
                        // packages/cultnet-rs/tests/selection.rs's
                        // dictionary_reference_entries_keep_distinct_payload_bytes pins the
                        // payload-carrying case directly against Row::references() instead.
                        payloadBase64 = edge.Payload == null ? null : Convert.ToBase64String((byte[])edge.Payload)
                    }).ToArray()
                });
            }

            // Self's ruling, 2026-09-22 (commit 2 fix batch): door-refusal vectors. Never
            // evaluated on either side - only decoded and handed to Validate, which must refuse
            // at exactly the named field.
            foreach (var (name, selection, expectedField) in RefusalCases(schemas))
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
                    expectedEvaluationRefusal = (string?)null,
                    expectedIds = Array.Empty<string>(),
                    matched = 0U,
                    hasNext = false,
                    expectedEdges = Array.Empty<object>()
                });
            }

            // R-AF: evaluation-time refusal vectors. Never handed to Validate - the selection
            // decodes and passes the door; only Select itself refuses, and only once the shared-key
            // resolution rule actually runs. Asserted here before being committed, mirroring how
            // RefusalCases above asserts Validate refuses before its vectors are written.
            foreach (var (name, selection) in EvaluationRefusalCases(schemas))
            {
                Assert.Throws<CultNetSelectionReferenceOutsideTargetException>(
                    () => CultNetSelectionEvaluator.Select(registry, rows, selection, asOf: 1),
                    $"{name} must be refused by Select before it can be committed as an evaluation-refusal vector");

                var selectionBytes = MessagePackSerializer.Serialize(selection, CultNetSchemaMessageSerialization.Options);
                vectors.Add(new
                {
                    name,
                    selectionMessagePackBase64 = Convert.ToBase64String(selectionBytes),
                    asOf = 1UL,
                    expectedRefusalField = (string?)null,
                    expectedEvaluationRefusal = (string?)"reference_outside_target",
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

        // Dumps each fixture schema's real, reference-computed id (and lets a maintainer derive the
        // unversioned alias by stripping the trailing ".v1") so
        // contracts/cultnet/interop/selection-vectors.fixture.json can be regenerated after a shape
        // change. Not part of the parity contract itself.
        [Test]
        public void DumpFixtureSchemaIds()
        {
            if (Environment.GetEnvironmentVariable("CULTNET_DUMP_SCHEMA_IDS") != "1")
                Assert.Ignore("Set CULTNET_DUMP_SCHEMA_IDS=1 to print the fixture's real schema ids.");

            var registry = Registry();
            Console.WriteLine("SCHEMA_ID leaf_a=" + registry.GetRequired(typeof(ParityLeafA)).SchemaId);
            Console.WriteLine("SCHEMA_ID leaf_b=" + registry.GetRequired(typeof(ParityLeafB)).SchemaId);
            Console.WriteLine("SCHEMA_ID citer=" + registry.GetRequired(typeof(ParityCiter)).SchemaId);
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

        // R-AF: a citer whose declared reference target is one leaf only, so a record key shared
        // by a ParityLeafA row and a ParityLeafB row has exactly one row inside the declared
        // target and exactly one outside it - see the fixture file's "citer_narrow" schema.
        [CultDocument("citer_narrow", "cultnet.selection-parity.citer_narrow.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ParityCiterNarrow
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultIndex("narrow_ref")]
            [CultReference(typeof(ParityLeafA))]
            public CultRecordRef<ParityLeafA> NarrowRef;
        }
    }
}
