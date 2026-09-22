//! CultNet typed selection, Cut 1 commit 3 (docs/cultnet-selection-cut.md section 7/10). A toy row
//! set implementing `Row`/`RowSet` directly - D7: no `cultcache-rs` knowledge, matching S14's brief
//! ("select_over_a_toy_row_set_matches_orders_hops_pages_and_refuses") rather than reproducing every
//! one of C#'s S1-S23 fixtures 1:1. Section 10's negative greps and the parity vectors (S12, S13) are
//! here too.

use std::fs;
use std::path::Path;

use cultnet_rs::{
    Citation, Cursor, CultNetMessage, CultNetWireContract, Edge, FieldPredicate, Incoming,
    RawDocumentHeader, RecordRef, Row, RowSet, Selection, SelectionDocumentRecord, SelectionOperator,
    SelectionPage, SelectionRefusal, canonical_number, decode_cultnet_message_from_slice,
    encode_cultnet_message_to_vec, schema_alias, select, select_page, validate,
};

// ------------------------------------------------------------------------------------------
// The fixture: two leaves under a conceptual abstract middle (mass declared on both,
// independently - Rust has no inheritance to share it through), a citer with four reference
// shapes (a single "Design" reference into the abstract middle's leaf set, a "related" many
// reference, a "components" many-dictionary reference with float payloads, and a "parent"
// single reference), and a narrow citer whose "narrow_ref" targets only leaf_a - the edge S18
// needs to reach outside its declared target.
//
// Soul's finding (soul-ss4-notes.md, F3): the earlier fixture used the schema *names* ("leaf_a",
// "leaf_b", ...) as their own schema ids, so every `schemas`/`cites.target` selection matched by
// plain string equality and never exercised the alias matcher at all - the real gap (C# resolves
// `["leaf_a"]` against a schema's declared name; Rust's exact-id compare gave 0 rows) was
// invisible. leaf_a()/leaf_b()/citer() below are real SHA-256-shaped ids
// (`sha256:<hex>`, no version suffix of their own - see the fixture-loading block's comment) - the
// same ids `CultCache.cs`'s `Sha256(semanticFingerprint)` produces for the shared fixture's C#
// document types. NARROW_CITER (just below) is the one id this file still invents locally; it is
// never compared across runtimes.
// ------------------------------------------------------------------------------------------

// R-E/shared-fixture ruling (docs/cultnet-selection-cut.md, Self's rulings for the Cut 1 fix batch,
// 2026-09-22): leaf_a/leaf_b/citer's real ids come from the one shared fixture,
// contracts/cultnet/interop/selection-vectors.fixture.json, loaded lazily below - real SHA-256
// content-hash ids, exactly as GameCult.Caching.CultDocumentRegistry computes them for the
// fixture's C# document types (tests/GameCult.Networking.Tests/SelectionParityVectorTests.cs). They
// carry no ".vN" suffix of their own - CultCache.cs's Sha256(semanticFingerprint) folds the version
// into the hash rather than appending it as text. leaf_a_hash_alias() is the same hash with a
// synthetic ".v1" appended, the one alias form this crate's own schema_alias module can resolve
// (see its module doc); leaf_a_name_alias() is the one alias form the C# reference can resolve
// instead ("leaf_a.v9") - the two runtimes' alias matchers key off different attributes of a
// descriptor and neither candidate resolves through both. NARROW_CITER stays a local, unshared id:
// it only ever appears in this file's own S18 test, never in a cross-runtime vector.
const NARROW_CITER: &str = "sha256:356840f80f14bda0186a33eb7aed41c1e644fd4ffc3b925ad0d7f10fe77b52cb.v1";

#[derive(serde::Deserialize)]
struct FixtureSchemaDef {
    #[serde(rename = "schemaId")]
    schema_id: String,
    #[serde(rename = "nameAlias")]
    name_alias: String,
    #[serde(rename = "hashAlias")]
    hash_alias: String,
}

#[derive(serde::Deserialize)]
struct FixtureReferenceDef {
    role: String,
    #[serde(rename = "targetSchema")]
    target_schema: String,
    #[serde(rename = "targetKey")]
    target_key: String,
}

#[derive(serde::Deserialize)]
struct FixtureRowDef {
    schema: String,
    key: String,
    ordinal: i64,
    #[serde(default)]
    fields: std::collections::BTreeMap<String, String>,
    #[serde(default)]
    references: Vec<FixtureReferenceDef>,
}

#[derive(serde::Deserialize)]
struct FixtureFile {
    schemas: std::collections::BTreeMap<String, FixtureSchemaDef>,
    rows: Vec<FixtureRowDef>,
}

static FIXTURE: std::sync::OnceLock<FixtureFile> = std::sync::OnceLock::new();

fn fixture() -> &'static FixtureFile {
    FIXTURE.get_or_init(|| {
        let path = contracts_dir().join("selection-vectors.fixture.json");
        let text = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("{} is missing or unreadable: {error}", path.display()));
        serde_json::from_str(&text)
            .unwrap_or_else(|error| panic!("{} is not valid fixture JSON: {error}", path.display()))
    })
}

fn fixture_schema(name: &str) -> &'static FixtureSchemaDef {
    fixture()
        .schemas
        .get(name)
        .unwrap_or_else(|| panic!("selection-vectors.fixture.json: no schema named '{name}'"))
}

fn leaf_a() -> &'static str {
    &fixture_schema("leaf_a").schema_id
}
fn leaf_b() -> &'static str {
    &fixture_schema("leaf_b").schema_id
}
fn citer() -> &'static str {
    &fixture_schema("citer").schema_id
}
// R-E/shared-fixture ruling: the fixture's map key ("leaf_a", "leaf_b", "citer") *is* each
// schema's declared name (C#'s CultDocumentDescriptor.SchemaName) - what RowSet::schema_name/
// Row::schema_name now expose, and what nameAlias's ".v9" suffix resolves against.
fn leaf_a_name() -> &'static str {
    "leaf_a"
}
fn leaf_b_name() -> &'static str {
    "leaf_b"
}
fn citer_name() -> &'static str {
    "citer"
}
const NARROW_CITER_NAME: &str = "narrow_citer";
// R-E, Self's ruling 2026-09-22 ("the C# reference's alias rule is the rule"): a hash id with a
// synthetic ".v1" appended is not a wire form either runtime's production rule resolves -
// CultNetSchemaAliasMatching's descriptor overload strips the ".v1" and compares the bare hash
// text against the declared *name* ("leaf_a"), never equal. This is now a shared negative check
// (see does_not_match_a_hash_shaped_alias below and the parity vectors this file writes).
fn leaf_a_hash_alias() -> &'static str {
    &fixture_schema("leaf_a").hash_alias
}
// The alias form the C# reference's descriptor overload resolves ("leaf_a.v9") - and, as of this
// cut's fix, the alias form this crate's schema_alias resolves too, through Row::schema_name/
// RowSet::schema_name now standing in for CultDocumentDescriptor.SchemaName.
fn leaf_a_name_alias() -> &'static str {
    &fixture_schema("leaf_a").name_alias
}

fn leak(value: String) -> &'static str {
    Box::leak(value.into_boxed_str())
}

fn build_row(row: &FixtureRowDef) -> FixtureRow {
    match row.schema.as_str() {
        "leaf_a" | "leaf_b" => {
            let schema_id = if row.schema == "leaf_a" { leaf_a() } else { leaf_b() };
            FixtureRow::leaf(
                schema_id,
                leak(row.key.clone()),
                row.ordinal,
                leak(row.fields["kind"].clone()),
                leak(row.fields["mass"].clone()),
            )
        }
        "citer" => {
            let references = row
                .references
                .iter()
                .map(|reference| {
                    let target_id = match reference.target_schema.as_str() {
                        "leaf_a" => leaf_a(),
                        "leaf_b" => leaf_b(),
                        other => panic!("selection-vectors.fixture.json: unknown reference target schema '{other}'"),
                    };
                    (reference.role.clone(), rr(target_id, leak(reference.target_key.clone())), None)
                })
                .collect();
            FixtureRow::citer(leak(row.key.clone()), row.ordinal, references)
        }
        other => panic!("selection-vectors.fixture.json: unknown schema '{other}'"),
    }
}

#[derive(Clone)]
struct FixtureRow {
    schema_id: &'static str,
    record_key: &'static str,
    ordinal: i64,
    kind: Option<&'static str>,
    mass: Option<&'static str>,
    references: Vec<(String, RecordRef, Option<Vec<u8>>)>,
}

impl FixtureRow {
    fn leaf(schema_id: &'static str, record_key: &'static str, ordinal: i64, kind: &'static str, mass: &'static str) -> Self {
        Self {
            schema_id,
            record_key,
            ordinal,
            kind: Some(kind),
            mass: Some(mass),
            references: Vec::new(),
        }
    }

    fn citer(record_key: &'static str, ordinal: i64, references: Vec<(String, RecordRef, Option<Vec<u8>>)>) -> Self {
        Self {
            schema_id: citer(),
            record_key,
            ordinal,
            kind: None,
            mass: None,
            references,
        }
    }
}

// Shared by Row::schema_name and RowSet::schema_name below - one place that knows which fixture
// schema id carries which declared name.
fn schema_name_for(schema_id: &str) -> Option<&'static str> {
    if schema_id == leaf_a() {
        Some(leaf_a_name())
    } else if schema_id == leaf_b() {
        Some(leaf_b_name())
    } else if schema_id == citer() {
        Some(citer_name())
    } else if schema_id == NARROW_CITER {
        Some(NARROW_CITER_NAME)
    } else {
        None
    }
}

impl Row for FixtureRow {
    fn schema_id(&self) -> &str {
        self.schema_id
    }
    fn schema_name(&self) -> &str {
        schema_name_for(self.schema_id).unwrap_or(self.schema_id)
    }
    fn record_key(&self) -> &str {
        self.record_key
    }
    fn ordinal(&self) -> i64 {
        self.ordinal
    }
    fn values(&self, index: &str) -> Vec<String> {
        match index {
            "kind" => self.kind.map(|v| vec![v.to_string()]).unwrap_or_default(),
            _ => Vec::new(),
        }
    }
    fn number(&self, index: &str) -> Option<String> {
        if index == "mass" {
            self.mass.map(str::to_string)
        } else {
            None
        }
    }
    fn references(&self) -> Vec<(String, RecordRef, Option<Vec<u8>>)> {
        self.references.clone()
    }
}

struct FixtureRowSet;

impl RowSet for FixtureRowSet {
    fn all_schema_ids(&self) -> Vec<String> {
        vec![
            leaf_a().into(),
            leaf_b().into(),
            citer().into(),
            NARROW_CITER.into(),
        ]
    }
    fn schema_name(&self, schema_id: &str) -> Option<String> {
        schema_name_for(schema_id).map(str::to_string)
    }
    fn declared_indexes(&self, schema_id: &str) -> Vec<String> {
        if schema_id == leaf_a() || schema_id == leaf_b() {
            vec!["kind".into(), "mass".into()]
        } else {
            Vec::new()
        }
    }
    fn declared_roles(&self, schema_id: &str) -> Vec<String> {
        if schema_id == citer() {
            vec![
                "parent".into(),
                "related".into(),
                "components".into(),
                "Design".into(),
            ]
        } else if schema_id == NARROW_CITER {
            vec!["narrow_ref".into()]
        } else {
            Vec::new()
        }
    }
    fn is_numeric(&self, schema_id: &str, index: &str) -> bool {
        (schema_id == leaf_a() || schema_id == leaf_b()) && index == "mass"
    }
    fn target_leaves(&self, role: &str) -> Vec<String> {
        match role {
            "parent" | "Design" | "related" | "components" => {
                vec![leaf_a().into(), leaf_b().into()]
            }
            "narrow_ref" => vec![leaf_a().into()],
            _ => Vec::new(),
        }
    }
}

fn rr(schema_id: &str, record_key: &str) -> RecordRef {
    RecordRef::new(schema_id, record_key)
}

// S2-S23's original 7-row set: the shared fixture's first 7 rows (a-lo..citer-1), unchanged in
// value or order from before the shared fixture - every test below that enumerates base_rows()'s
// exact key order or count (S3, S4/S5, matched_is_the_total_count_not_the_page_page, ...) still
// holds. The parity-vector tests use all_fixture_rows() instead, which adds the fix-batch rows
// (float tie, astral/BMP keys) the shared fixture also carries.
fn base_rows() -> Vec<FixtureRow> {
    fixture().rows.iter().take(7).map(build_row).collect()
}

fn all_fixture_rows() -> Vec<FixtureRow> {
    fixture().rows.iter().map(build_row).collect()
}

// S2: fields conjoin over declared indexes.
#[test]
fn conjoins_any_of_and_comparison_predicates() {
    let rows = base_rows();
    let selection = Selection {
        fields: Some(vec![
            FieldPredicate {
                index: "kind".into(),
                op: "any_of".into(),
                values: Some(vec!["weapon".into()]),
                number: None,
            },
            FieldPredicate {
                index: "mass".into(),
                op: "ge".into(),
                values: None,
                number: Some("5".into()),
            },
        ]),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).expect("selection is valid");
    let ids: Vec<&str> = evaluation.rows.iter().map(Row::record_key).collect();
    assert_eq!(ids, vec!["a-eq"]);
}

// S3: order is (ordinal, schemaId, recordKey) ascending, reversed under descending.
#[test]
fn orders_by_ordinal_then_identity_and_reverses() {
    let rows = base_rows();
    let ascending = select(&FixtureRowSet, &rows, &Selection::default(), 1).unwrap();
    assert_eq!(
        ascending.rows.iter().map(Row::record_key).collect::<Vec<_>>(),
        vec!["a-lo", "a-eq", "b-hi", "shared-1", "shared-2", "shared-3", "citer-1"]
    );

    let descending = select(
        &FixtureRowSet,
        &rows,
        &Selection {
            descending: true,
            ..Selection::default()
        },
        1,
    )
    .unwrap();
    assert_eq!(
        descending.rows.iter().map(Row::record_key).collect::<Vec<_>>(),
        vec!["citer-1", "shared-3", "shared-2", "shared-1", "b-hi", "a-eq", "a-lo"]
    );
}

// S4/S5: a page walk visits every row exactly once, the last page carries no cursor, and a
// cursor is refused when it does not decode, its digest does not match, or asOf has moved.
#[test]
fn pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor() {
    let rows = base_rows();
    let mut selection = Selection {
        limit: Some(2),
        ..Selection::default()
    };
    let mut seen = Vec::new();
    for _ in 0..10 {
        let page = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
        seen.extend(page.rows.iter().map(|r| r.record_key.to_string()));
        match page.next_cursor {
            Some(cursor) => selection.cursor = Some(cursor),
            None => break,
        }
    }
    assert_eq!(
        seen,
        vec!["a-lo", "a-eq", "b-hi", "shared-1", "shared-2", "shared-3", "citer-1"]
    );

    let first = select(&FixtureRowSet, &rows, &Selection { limit: Some(1), ..Selection::default() }, 1).unwrap();
    let cursor = first.next_cursor.expect("more than one row");

    let stale = select(
        &FixtureRowSet,
        &rows,
        &Selection { limit: Some(1), cursor: Some(cursor.clone()), ..Selection::default() },
        2,
    );
    assert!(matches!(stale, Err(SelectionRefusal::CursorStale { .. })));

    let mismatched = select(
        &FixtureRowSet,
        &rows,
        &Selection { limit: Some(1), cursor: Some(cursor), descending: true, ..Selection::default() },
        1,
    );
    assert!(matches!(mismatched, Err(SelectionRefusal::CursorInvalid { .. })));

    let garbage = select(
        &FixtureRowSet,
        &rows,
        &Selection { cursor: Some("not-base64!!".into()), ..Selection::default() },
        1,
    );
    assert!(matches!(garbage, Err(SelectionRefusal::CursorInvalid { .. })));
}

// S6: the hop follows one declared reference by role.
#[test]
fn hops_one_edge_by_declared_role() {
    let rows = base_rows();
    let selection = Selection {
        cites: Some(Citation {
            target: rr(leaf_a(), "a-eq"),
            role: Some("Design".into()),
        }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["citer-1"]);
    assert_eq!(evaluation.edges.len(), 1);
    assert_eq!(evaluation.edges[0].role, "Design");
}

// S6, isolated: a citer with two references at the *same* target under different roles - the
// role filter must exclude the one the caller did not ask for. base_rows()'s citer-1 only ever
// carries one reference, so a mutant that drops the role filter entirely is invisible there (it
// has nothing else to wrongly match); this fixture gives it something to wrongly match.
#[test]
fn cites_role_excludes_a_second_reference_at_the_same_target() {
    let rows = vec![
        FixtureRow::leaf(leaf_a(), "a-eq", 1, "weapon", "5"),
        FixtureRow::citer(
            "double-citer",
            2,
            vec![
                ("Design".to_string(), rr(leaf_a(), "a-eq"), None),
                ("OtherRole".to_string(), rr(leaf_a(), "a-eq"), None),
            ],
        ),
    ];
    let selection = Selection {
        cites: Some(Citation {
            target: rr(leaf_a(), "a-eq"),
            role: Some("Design".into()),
        }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.edges.len(), 1, "OtherRole's reference must not also match");
    assert_eq!(evaluation.edges[0].role, "Design");
}

// S7: cited { exists } is the one negation, and the two directions answer opposite sets.
#[test]
fn cited_exists_is_the_one_negation() {
    let rows = base_rows();
    let cited = select(
        &FixtureRowSet,
        &rows,
        &Selection {
            schemas: Some(vec![leaf_a().into()]),
            cited: Some(Incoming { role: "Design".into(), exists: true }),
            ..Selection::default()
        },
        1,
    )
    .unwrap();
    assert_eq!(cited.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["a-eq"]);

    let uncited = select(
        &FixtureRowSet,
        &rows,
        &Selection {
            schemas: Some(vec![leaf_a().into()]),
            cited: Some(Incoming { role: "Design".into(), exists: false }),
            ..Selection::default()
        },
        1,
    )
    .unwrap();
    let mut ids: Vec<&str> = uncited.rows.iter().map(Row::record_key).collect();
    ids.sort_unstable();
    assert_eq!(ids, vec!["a-lo", "shared-1", "shared-3"]);
}

// S16: the four comparisons at the boundary, including the row whose value equals the compared
// number exactly.
#[test]
fn compares_numbers_at_the_boundary_for_all_four_operators() {
    let rows = vec![
        FixtureRow::leaf(leaf_a(), "lo", 1, "k", "4"),
        FixtureRow::leaf(leaf_a(), "eq", 2, "k", "5"),
        FixtureRow::leaf(leaf_a(), "hi", 3, "k", "6"),
    ];
    let matches_for = |op: &str| {
        let selection = Selection {
            fields: Some(vec![FieldPredicate {
                index: "mass".into(),
                op: op.into(),
                values: None,
                number: Some("5".into()),
            }]),
            ..Selection::default()
        };
        let mut ids: Vec<String> = select(&FixtureRowSet, &rows, &selection, 1)
            .unwrap()
            .rows
            .iter()
            .map(|r| r.record_key.to_string())
            .collect();
        ids.sort();
        ids
    };
    assert_eq!(matches_for("lt"), vec!["lo"]);
    assert_eq!(matches_for("le"), vec!["eq", "lo"]);
    assert_eq!(matches_for("ge"), vec!["eq", "hi"]);
    assert_eq!(matches_for("gt"), vec!["hi"]);
}

// S18: an edge naming a row outside the reference's declared target refuses the selection.
#[test]
fn refuses_an_edge_outside_its_declared_target() {
    let rows = vec![
        FixtureRow::leaf(leaf_b(), "b", 1, "k", "1"),
        FixtureRow {
            schema_id: NARROW_CITER,
            record_key: "bad",
            ordinal: 2,
            kind: None,
            mass: None,
            references: vec![("narrow_ref".to_string(), rr(leaf_b(), "b"), None)],
        },
    ];
    let selection = Selection {
        cited: Some(Incoming { role: "narrow_ref".into(), exists: true }),
        ..Selection::default()
    };
    let result = select(&FixtureRowSet, &rows, &selection, 1);
    assert!(matches!(result, Err(SelectionRefusal::ReferenceOutsideTarget { .. })));
}

// S19-equivalent (D11): a dictionary reference's two entries keep distinct payload bytes.
// Row::references() is where Rust exposes this - there is no separate cache layer to test it
// against, unlike C#'s ReferencesOf/Cache_EnumeratesADictionaryReferenceAsEdgesCarryingItsValues.
#[test]
fn dictionary_reference_entries_keep_distinct_payload_bytes() {
    let citer = FixtureRow::citer(
        "assembly",
        3,
        vec![
            ("components".to_string(), rr(leaf_a(), "part-1"), Some(vec![1, 5])),
            ("components".to_string(), rr(leaf_a(), "part-2"), Some(vec![2, 5])),
        ],
    );
    let payloads: std::collections::HashSet<Vec<u8>> =
        citer.references().into_iter().filter_map(|(_, _, payload)| payload).collect();
    assert_eq!(payloads.len(), 2, "the two dictionary entries must keep distinct payloads");
}

// R-B: hop edges follow the hop's direction. A `cites` selection's page holds the citer, so its
// edges (From = citer) survive the page filter, mirroring
// `EvaluatorHopsOneEdgeByDeclaredReferenceAndRole`. A `cited` selection's page holds the *citee*
// instead, so - as of R-B - its edges survive when their *To* is on the page, not their *From*:
// the citer ("assembly") need not itself be on the page at all. (Before R-B, Rust filtered both
// hop kinds by `edge.From` on the page, so a `cited` selection's edges were silently empty
// whenever the citer itself did not separately match - Soul's F2 finding.)
#[test]
fn cited_selections_page_the_citee_and_their_edges_survive_the_page_filter() {
    let rows = vec![
        FixtureRow::leaf(leaf_a(), "part-1", 1, "k", "1"),
        FixtureRow::citer(
            "assembly",
            2,
            vec![("components".to_string(), rr(leaf_a(), "part-1"), Some(vec![9]))],
        ),
    ];
    let selection = Selection {
        cited: Some(Incoming { role: "components".into(), exists: true }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["part-1"]);
    assert_eq!(evaluation.edges.len(), 1, "the citee (part-1) is on the page, so its incoming edge survives");
    assert_eq!(evaluation.edges[0].from.record_key, "assembly");
    assert_eq!(evaluation.edges[0].to.record_key, "part-1");
    assert_eq!(evaluation.edges[0].payload, Some(vec![9]));
}

// R-B: the converse of the test above - a `cites` selection's edges still key off the *citer*
// being on the page (unchanged by R-B), so an edge whose citee is on the page but whose citer is
// not filtered out does not leak in under `cites`.
#[test]
fn cites_selections_still_page_the_citer_and_key_edges_off_it() {
    let rows = vec![
        FixtureRow::leaf(leaf_a(), "a-eq", 1, "weapon", "5"),
        FixtureRow::citer("citer-1", 2, vec![("Design".to_string(), rr(leaf_a(), "a-eq"), None)]),
    ];
    let selection = Selection {
        cites: Some(Citation { target: rr(leaf_a(), "a-eq"), role: Some("Design".into()) }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["citer-1"]);
    assert_eq!(evaluation.edges.len(), 1);
    assert_eq!(evaluation.edges[0].from.record_key, "citer-1");
}

// S23: the evaluator matches every row sharing an index value - not a cache's last-writer-wins
// unique-index lookup (Rust's toy Row set never had that bug, but the rule is pinned here too:
// nothing in select() short-circuits after the first match).
#[test]
fn matches_every_row_sharing_an_index_value() {
    let rows = base_rows();
    let selection = Selection {
        fields: Some(vec![FieldPredicate {
            index: "kind".into(),
            op: "any_of".into(),
            values: Some(vec!["shared".into()]),
            number: None,
        }]),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    let mut ids: Vec<&str> = evaluation.rows.iter().map(Row::record_key).collect();
    ids.sort_unstable();
    assert_eq!(ids, vec!["shared-1", "shared-2", "shared-3"]);
}

// S1: the door refuses typed rather than answering an empty page.
#[test]
fn validation_refuses_undeclared_index_role_and_empty_lists() {
    let empty_schemas = Selection {
        schemas: Some(Vec::new()),
        ..Selection::default()
    };
    assert_eq!(validate(&empty_schemas, &FixtureRowSet).unwrap_err().field, "schemas");

    let empty_keys = Selection {
        keys: Some(Vec::new()),
        ..Selection::default()
    };
    assert_eq!(validate(&empty_keys, &FixtureRowSet).unwrap_err().field, "keys");

    let undeclared_index = Selection {
        fields: Some(vec![FieldPredicate {
            index: "not_declared".into(),
            op: "any_of".into(),
            values: Some(vec!["x".into()]),
            number: None,
        }]),
        ..Selection::default()
    };
    assert_eq!(
        validate(&undeclared_index, &FixtureRowSet).unwrap_err().field,
        "fields[0].index"
    );

    let undeclared_role = Selection {
        cited: Some(Incoming { role: "no_such_role".into(), exists: true }),
        ..Selection::default()
    };
    assert_eq!(validate(&undeclared_role, &FixtureRowSet).unwrap_err().field, "cited.role");

    let non_numeric_compare = Selection {
        fields: Some(vec![FieldPredicate {
            index: "kind".into(),
            op: "gt".into(),
            values: None,
            number: Some("1".into()),
        }]),
        ..Selection::default()
    };
    assert_eq!(
        validate(&non_numeric_compare, &FixtureRowSet).unwrap_err().field,
        "fields[0].index"
    );
}

// Self's ruling, 2026-09-22 (docs/cultnet-selection-cut.md, commit 2 fix batch): the door refuses
// a blank entry inside a present schemas/keys list, not only an empty list - the evaluator must
// never have to decide what "" or "  " means.
#[test]
fn validation_refuses_a_blank_entry_in_schemas_or_keys() {
    let blank_schema = Selection {
        schemas: Some(vec![leaf_a().into(), "  ".into()]),
        ..Selection::default()
    };
    assert_eq!(validate(&blank_schema, &FixtureRowSet).unwrap_err().field, "schemas");

    let blank_key = Selection {
        keys: Some(vec!["".into()]),
        ..Selection::default()
    };
    assert_eq!(validate(&blank_key, &FixtureRowSet).unwrap_err().field, "keys");
}

// Q-J: the door refuses every named non-canonical spelling of a comparison number.
#[test]
fn validation_refuses_non_canonical_number_spellings() {
    for spelling in ["+1", "1.0", "01", "1e3", "-0"] {
        let selection = Selection {
            fields: Some(vec![FieldPredicate {
                index: "mass".into(),
                op: "gt".into(),
                values: None,
                number: Some(spelling.into()),
            }]),
            ..Selection::default()
        };
        let error = validate(&selection, &FixtureRowSet)
            .expect_err(&format!("{spelling:?} must be refused"));
        assert_eq!(error.field, "fields[0].number", "spelling {spelling:?}");
    }
}

// S13a: nothing on the wire is serde-untagged (docs/cultnet-selection-cut.md section 2).
#[test]
fn selection_is_never_untagged() {
    let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    for entry in fs::read_dir(&src_dir).expect("src directory exists") {
        let entry = entry.expect("readable directory entry");
        let path = entry.path();
        if path.extension().and_then(|ext| ext.to_str()) != Some("rs") {
            continue;
        }
        let text = fs::read_to_string(&path).unwrap_or_default();
        assert!(
            !text.contains("untagged"),
            "{} must not contain #[serde(untagged)] (section 2's rule)",
            path.display()
        );
    }
}

// S13b: the selection types round-trip through the named-map MessagePack encoding every runtime
// on this wire uses (rmp_serde::to_vec_named / from_slice), across every shape this cut adds.
#[test]
fn selection_round_trips_through_named_messagepack() {
    let selection = Selection {
        schemas: Some(vec![leaf_a().into(), leaf_b().into()]),
        keys: Some(vec!["k1".into()]),
        fields: Some(vec![
            FieldPredicate {
                index: "kind".into(),
                op: "any_of".into(),
                values: Some(vec!["weapon".into()]),
                number: None,
            },
            FieldPredicate {
                index: "mass".into(),
                op: "ge".into(),
                values: None,
                number: Some("5".into()),
            },
        ]),
        cites: Some(Citation { target: rr(leaf_a(), "a-eq"), role: Some("Design".into()) }),
        cited: Some(Incoming { role: "Design".into(), exists: true }),
        projection: "document".into(),
        descending: true,
        limit: Some(50),
        cursor: Some("opaque".into()),
    };
    let bytes = rmp_serde::to_vec_named(&selection).expect("encodes");
    let decoded: Selection = rmp_serde::from_slice(&bytes).expect("decodes");
    assert_eq!(decoded, selection);
}

// ------------------------------------------------------------------------------------------
// S12: parity vectors. contracts/cultnet/interop/selection-vectors.cs-written.json is written by
// the C# reference (SelectionParityVectorTests.WriteVectors, gated on CULTNET_WRITE_VECTORS=1)
// and judged here; selection-vectors.rs-written.json is the mirror this binary writes (gated the
// same way) for the C# side to judge. A within-runtime round trip pins nothing - both directions
// run against the fixture's *own* rows, decoding only the Selection bytes across the wire.
// ------------------------------------------------------------------------------------------

#[derive(serde::Serialize, serde::Deserialize)]
struct VectorFile {
    vectors: Vec<Vector>,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct Vector {
    name: String,
    #[serde(rename = "selectionMessagePackBase64")]
    selection_message_pack_base64: String,
    #[serde(rename = "asOf")]
    as_of: u64,
    // Present iff this vector is a door refusal (Self's ruling, 2026-09-22): the selection must
    // be refused by `validate`/`CultNetSelection.Validate` with exactly this field name, and no
    // evaluation runs. Absent for an ordinary evaluated vector.
    #[serde(rename = "expectedRefusalField", default, skip_serializing_if = "Option::is_none")]
    expected_refusal_field: Option<String>,
    #[serde(rename = "expectedIds", default)]
    expected_ids: Vec<String>,
    #[serde(default)]
    matched: u32,
    #[serde(rename = "hasNext", default)]
    has_next: bool,
    #[serde(rename = "expectedEdges", default)]
    expected_edges: Vec<VectorEdge>,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct VectorEdge {
    #[serde(rename = "fromId")]
    from_id: String,
    role: String,
    #[serde(rename = "toId")]
    to_id: String,
    #[serde(rename = "payloadBase64")]
    payload_base64: Option<String>,
}

fn row_id(schema_id: &str, record_key: &str) -> String {
    format!("{schema_id}/{record_key}")
}

fn contracts_dir() -> std::path::PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("contracts")
        .join("cultnet")
        .join("interop")
}

// Self's ruling, 2026-09-22 (shared-fixture ruling): a real parity defect must be reported, not
// papered over by aborting the run at the first one. Returns Err(description) instead of
// panicking when `select` refuses a vector this file did not mark as a refusal vector - R-F wires
// the door inside `select`, and the C# reference's Validate has no equivalent check for an
// unresolved `cites.target.schemaId` (CultNetSelection.cs:361-391), so a vector the reference
// evaluates for real can still be one this crate's door refuses. The caller collects every such
// mismatch instead of stopping at the first one.
fn evaluate_vector(vector: &Vector) -> Result<(Vec<String>, u32, bool, Vec<VectorEdge>), String> {
    use base64::Engine;
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&vector.selection_message_pack_base64)
        .expect("vector selection bytes are valid base64");
    let selection: Selection =
        rmp_serde::from_slice(&bytes).expect("vector selection bytes decode as MessagePack");
    let rows = all_fixture_rows();
    let evaluation = select(&FixtureRowSet, &rows, &selection, vector.as_of)
        .map_err(|refusal| format!("select() refused: {refusal:?}"))?;
    let ids: Vec<String> = evaluation
        .rows
        .iter()
        .map(|row| row_id(row.schema_id(), row.record_key()))
        .collect();
    // R-G/P-1: matched is the total match count, not the page count - the two differ whenever a
    // vector's limit clips the page.
    let matched = evaluation.matched;
    let has_next = evaluation.next_cursor.is_some();
    let edges = evaluation
        .edges
        .iter()
        .map(|edge| VectorEdge {
            from_id: row_id(edge.from.schema_id(), edge.from.record_key()),
            role: edge.role.clone(),
            to_id: row_id(edge.to.schema_id(), edge.to.record_key()),
            payload_base64: edge
                .payload
                .as_ref()
                .map(|bytes| base64::engine::general_purpose::STANDARD.encode(bytes)),
        })
        .collect();
    Ok((ids, matched, has_next, edges))
}

// Self's ruling, 2026-09-22: a door-refusal vector decodes its (necessarily malformed, by the
// v1 rule) selection bytes and asserts `validate` refuses it at exactly the named field, rather
// than evaluating it - "the evaluator never sees []" means these selections are never run
// through `select` at all, on either side of the vector.
fn assert_refusal_vector(vector: &Vector, expected_field: &str) {
    use base64::Engine;
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&vector.selection_message_pack_base64)
        .expect("vector selection bytes are valid base64");
    let selection: Selection =
        rmp_serde::from_slice(&bytes).expect("vector selection bytes decode as MessagePack");
    let error = validate(&selection, &FixtureRowSet)
        .expect_err(&format!("vector {:?} must be refused", vector.name));
    assert_eq!(error.field, expected_field, "vector {:?}: refusal field", vector.name);
}

/// Vectors written by the reference (C#) decode and evaluate identically in Rust.
#[test]
fn selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust() {
    let path = contracts_dir().join("selection-vectors.cs-written.json");
    let Ok(text) = fs::read_to_string(&path) else {
        panic!(
            "{} is missing - run the C# writer (SelectionParityVectorTests, CULTNET_WRITE_VECTORS=1) first",
            path.display()
        );
    };
    let file: VectorFile = serde_json::from_str(&text).expect("vector file is valid JSON");
    assert!(!file.vectors.is_empty(), "the vector file must carry at least one vector");

    // Collect every mismatch instead of stopping at the first one (see evaluate_vector's comment) -
    // a real parity defect is reported in full, not truncated by whichever vector happened to come
    // first in the file.
    let mut failures: Vec<String> = Vec::new();
    for vector in &file.vectors {
        if let Some(expected_field) = &vector.expected_refusal_field {
            let bytes_ok = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                assert_refusal_vector(vector, expected_field)
            }));
            if let Err(panic) = bytes_ok {
                let message = panic
                    .downcast_ref::<String>()
                    .cloned()
                    .or_else(|| panic.downcast_ref::<&str>().map(|s| s.to_string()))
                    .unwrap_or_else(|| "<non-string panic>".into());
                failures.push(format!("{}: {}", vector.name, message));
            }
            continue;
        }

        let evaluated = match evaluate_vector(vector) {
            Ok(evaluated) => evaluated,
            Err(reason) => {
                failures.push(format!(
                    "{}: expected real evaluation (expectedIds={:?}) but {reason}",
                    vector.name, vector.expected_ids
                ));
                continue;
            }
        };
        let (ids, matched, has_next, edges) = evaluated;

        if ids != vector.expected_ids {
            failures.push(format!("{}: row ids - expected {:?}, got {:?}", vector.name, vector.expected_ids, ids));
        }
        if matched != vector.matched {
            failures.push(format!("{}: matched - expected {}, got {}", vector.name, vector.matched, matched));
        }
        if has_next != vector.has_next {
            failures.push(format!("{}: hasNext - expected {}, got {}", vector.name, vector.has_next, has_next));
        }
        if edges.len() != vector.expected_edges.len() {
            failures.push(format!(
                "{}: edge count - expected {}, got {}",
                vector.name,
                vector.expected_edges.len(),
                edges.len()
            ));
            continue;
        }
        for (i, (actual, expected)) in edges.iter().zip(vector.expected_edges.iter()).enumerate() {
            if actual.from_id != expected.from_id
                || actual.role != expected.role
                || actual.to_id != expected.to_id
                || actual.payload_base64 != expected.payload_base64
            {
                failures.push(format!("{}: edge[{i}] mismatch", vector.name));
            }
        }
    }

    assert!(
        failures.is_empty(),
        "{} of {} vectors disagreed with the reference:\n{}",
        failures.len(),
        file.vectors.len(),
        failures.join("\n")
    );
}

/// The Rust->C# mirror: builds `selection-vectors.rs-written.json` from this binary's own
/// selections and its own evaluation of the same fixture, for the C# test
/// `SelectionVectorsWrittenByRustDecodeAndEvaluateIdenticallyInTheReference` to judge. Committed,
/// not regenerated on every run - rerun with `CULTNET_WRITE_VECTORS=1` after a real change to the
/// evaluator or the vocabulary.
#[test]
fn write_selection_vectors_for_the_reference() {
    if std::env::var("CULTNET_WRITE_VECTORS").as_deref() != Ok("1") {
        return;
    }
    use base64::Engine;

    let cases: Vec<(&str, Selection)> = vec![
        (
            "any_of_plus_ge_conjunction",
            Selection {
                fields: Some(vec![
                    FieldPredicate {
                        index: "kind".into(),
                        op: "any_of".into(),
                        values: Some(vec!["weapon".into()]),
                        number: None,
                    },
                    FieldPredicate {
                        index: "mass".into(),
                        op: "ge".into(),
                        values: None,
                        number: Some("5".into()),
                    },
                ]),
                ..Selection::default()
            },
        ),
        (
            "descending_with_limit",
            Selection {
                descending: true,
                limit: Some(3),
                ..Selection::default()
            },
        ),
        (
            "gt_numeric_boundary",
            Selection {
                fields: Some(vec![FieldPredicate {
                    index: "mass".into(),
                    op: "gt".into(),
                    values: None,
                    number: Some("1".into()),
                }]),
                ..Selection::default()
            },
        ),
        (
            "incoming_exists_true",
            Selection {
                schemas: Some(vec![leaf_a().into()]),
                cited: Some(Incoming { role: "Design".into(), exists: true }),
                ..Selection::default()
            },
        ),
        (
            "incoming_negation",
            Selection {
                schemas: Some(vec![leaf_a().into()]),
                cited: Some(Incoming { role: "Design".into(), exists: false }),
                ..Selection::default()
            },
        ),
        (
            "shared_index_value_three_rows",
            Selection {
                fields: Some(vec![FieldPredicate {
                    index: "kind".into(),
                    op: "any_of".into(),
                    values: Some(vec!["shared".into()]),
                    number: None,
                }]),
                ..Selection::default()
            },
        ),
        // R-E/Soul "Settled": with real SHA-256 schema ids in the fixture, `cites.target.schemaId`
        // agrees byte for byte across runtimes - the earlier name-as-id fixture masked this. This
        // is the exact-id form.
        (
            "hop_by_role_cites_exact_id",
            Selection {
                cites: Some(Citation { target: rr(leaf_a(), "a-eq"), role: Some("Design".into()) }),
                ..Selection::default()
            },
        ),
        // R-E: the same citation, but the target is named by the alias form C#'s production rule
        // resolves ("leaf_a.v9", stripped to "leaf_a" and compared against the schema's declared
        // name) - as of this cut's fix, Row::schema_name/RowSet::schema_name give this crate's own
        // schema_alias the same name to resolve against, so this now agrees with the reference too.
        (
            "hop_by_role_cites_name_alias",
            Selection {
                cites: Some(Citation { target: rr(leaf_a_name_alias(), "a-eq"), role: Some("Design".into()) }),
                ..Selection::default()
            },
        ),
        // R-E, Self's ruling 2026-09-22 ("the C# reference's alias rule is the rule"): a hash id
        // with a synthetic ".v1" appended is not a wire form either runtime's production rule
        // resolves - it strips the ".v1" and compares the bare hash text against the schema's
        // declared *name* ("leaf_a"), never equal. This is a shared negative check now: both
        // runtimes give an empty reachable set for `schemas`, hence empty expected_ids here.
        (
            "schemas_by_hash_alias",
            Selection {
                schemas: Some(vec![leaf_a_hash_alias().into()]),
                ..Selection::default()
            },
        ),
        // R-E: `schemas` reaches a schema by its name alias - as of this cut's fix, this crate's
        // schema_alias resolves it the same way CultNetSchemaAliasMatching's descriptor overload
        // does (the only overload any C# call site uses): strip the trailing ".v9" and compare
        // "leaf_a" to the schema's declared name. Previously this crate's alias matcher had no
        // concept of a schema's name at all and gave an empty result here, disagreeing with the
        // reference - that cross-runtime defect is what this cut fixes.
        (
            "schemas_by_name_alias",
            Selection {
                schemas: Some(vec![leaf_a_name_alias().into()]),
                ..Selection::default()
            },
        ),
        // R-D: the row's canonical rendering of 2233759.25f32 is its exact decimal expansion, not a
        // shortest round-trip form that could round the tie the other way - ge against the exact
        // value must match. Mirrors SelectionParityVectorTests.Cases's float_tie_exact_ge.
        (
            "float_tie_exact_ge",
            Selection {
                fields: Some(vec![FieldPredicate {
                    index: "mass".into(),
                    op: "ge".into(),
                    values: None,
                    number: Some("2233759.25".into()),
                }]),
                ..Selection::default()
            },
        ),
        // R-C: the astral/BMP-private-use key pair sorts by code point, not UTF-16 code unit.
        // Mirrors SelectionParityVectorTests.Cases's astral_key_code_point_order.
        (
            "astral_key_code_point_order",
            Selection {
                fields: Some(vec![FieldPredicate {
                    index: "kind".into(),
                    op: "any_of".into(),
                    values: Some(vec!["unicode".into()]),
                    number: None,
                }]),
                ..Selection::default()
            },
        ),
    ];

    let mut vectors = Vec::new();
    for (name, selection) in cases {
        let bytes = rmp_serde::to_vec_named(&selection).expect("encodes");
        let rows = all_fixture_rows();
        let evaluation = select(&FixtureRowSet, &rows, &selection, 1).expect("valid selection");
        let ids: Vec<String> = evaluation
            .rows
            .iter()
            .map(|row| row_id(row.schema_id(), row.record_key()))
            .collect();
        let edges = evaluation
            .edges
            .iter()
            .map(|edge| VectorEdge {
                from_id: row_id(edge.from.schema_id(), edge.from.record_key()),
                role: edge.role.clone(),
                to_id: row_id(edge.to.schema_id(), edge.to.record_key()),
                payload_base64: edge
                    .payload
                    .as_ref()
                    .map(|bytes| base64::engine::general_purpose::STANDARD.encode(bytes)),
            })
            .collect();
        vectors.push(Vector {
            name: name.to_string(),
            selection_message_pack_base64: base64::engine::general_purpose::STANDARD.encode(&bytes),
            as_of: 1,
            expected_refusal_field: None,
            matched: evaluation.matched,
            has_next: evaluation.next_cursor.is_some(),
            expected_ids: ids,
            expected_edges: edges,
        });
    }

    // Self's ruling, 2026-09-22 (commit 2 fix batch): door-refusal vectors. These selections are
    // never evaluated on either side - only decoded and handed to validate/Validate, which must
    // refuse at exactly the named field.
    let refusal_cases: Vec<(&str, Selection, &str)> = vec![
        (
            "refuses_empty_schemas_list",
            Selection {
                schemas: Some(Vec::new()),
                ..Selection::default()
            },
            "schemas",
        ),
        (
            "refuses_blank_key_entry",
            Selection {
                keys: Some(vec!["a-eq".into(), "   ".into()]),
                ..Selection::default()
            },
            "keys",
        ),
        // R-E: an unmatched cites.target.schemaId is refused at the door, never silently answered
        // with an empty page.
        (
            "refuses_unmatched_cites_target_schema",
            Selection {
                cites: Some(Citation {
                    target: rr("sha256:not-a-known-schema", "a-eq"),
                    role: Some("Design".into()),
                }),
                ..Selection::default()
            },
            "cites.target.schemaId",
        ),
        // R-E, Self's ruling 2026-09-22: a hash-shaped alias resolves to no known schema in either
        // runtime now (see schemas_by_hash_alias above), so naming one as a cites.target is the
        // same "unmatched target" refusal as any other unresolvable schema id.
        (
            "refuses_cites_target_by_hash_shaped_alias",
            Selection {
                cites: Some(Citation {
                    target: rr(leaf_a_hash_alias(), "a-eq"),
                    role: Some("Design".into()),
                }),
                ..Selection::default()
            },
            "cites.target.schemaId",
        ),
    ];
    for (name, selection, expected_field) in refusal_cases {
        let bytes = rmp_serde::to_vec_named(&selection).expect("encodes");
        vectors.push(Vector {
            name: name.to_string(),
            selection_message_pack_base64: base64::engine::general_purpose::STANDARD.encode(&bytes),
            as_of: 1,
            expected_refusal_field: Some(expected_field.to_string()),
            matched: 0,
            has_next: false,
            expected_ids: Vec::new(),
            expected_edges: Vec::new(),
        });
    }

    let file = VectorFile { vectors };
    let json = serde_json::to_string_pretty(&file).expect("serializes");
    let dir = contracts_dir();
    fs::create_dir_all(&dir).expect("contracts/cultnet/interop exists or is created");
    fs::write(dir.join("selection-vectors.rs-written.json"), json).expect("writes the vector file");
}

// A canary that the canonical_number module used above stays reachable from an integration test
// (it is exercised indirectly through Selection.fields[].number already, but this names the rule
// directly): a lexicographic compare would put "10" below "9".
#[test]
fn canonical_number_compares_by_magnitude() {
    assert_eq!(
        canonical_number::compare("10", "9"),
        std::cmp::Ordering::Greater
    );
    assert!(canonical_number::is_canonical("10"));
    assert!(!canonical_number::is_canonical("+10"));
}

// Q-J: a negative value orders below a positive one of the same magnitude - a comparator that
// ignores sign would treat "-1" and "1" as equal (same integer/fraction digits).
#[test]
fn canonical_number_compares_by_sign() {
    assert_eq!(canonical_number::compare("-1", "1"), std::cmp::Ordering::Less);
    assert_eq!(canonical_number::compare("1", "-1"), std::cmp::Ordering::Greater);
    assert_eq!(canonical_number::compare("-10", "-2"), std::cmp::Ordering::Less);
}

#[allow(dead_code)]
fn unused_operator_reference(op: SelectionOperator) -> &'static str {
    op.as_wire_str()
}

// ------------------------------------------------------------------------------------------
// R-F: the door is inside `select` - it validates first and returns the typed refusal, and the
// single-row fast path refuses (loudly, not just in debug builds) to answer a hop-bearing
// selection at all.
// ------------------------------------------------------------------------------------------

#[test]
fn select_refuses_an_invalid_selection_instead_of_evaluating_it() {
    let rows = base_rows();
    let selection = Selection { schemas: Some(Vec::new()), ..Selection::default() };
    let result = select(&FixtureRowSet, &rows, &selection, 1);
    assert!(matches!(result, Err(SelectionRefusal::Invalid(ref invalid)) if invalid.field == "schemas"));
}

// A plain assert (not #[should_panic]) so a mutant that removes the panic reads as an ordinary
// FAILED test rather than the harness having to special-case should_panic's own output shape.
#[test]
fn matches_refuses_a_hop_bearing_selection_even_in_a_release_style_assert() {
    let row = FixtureRow::leaf(leaf_a(), "a-eq", 1, "weapon", "5");
    let selection = Selection {
        cited: Some(Incoming { role: "Design".into(), exists: true }),
        ..Selection::default()
    };
    let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        cultnet_rs::matches(&row, &selection);
    }));
    let panicked = outcome.is_err();
    assert!(panicked, "matches() must panic on a hop-bearing selection, not answer wrong");
}

// ------------------------------------------------------------------------------------------
// R-H: the cursor digest is length-prefixed, so no separator character can make two distinct
// selections collide (Soul: `["a|b"]` and `["a","b"]` used to digest the same).
// ------------------------------------------------------------------------------------------

#[test]
fn cursor_digest_does_not_collide_on_differently_split_lists() {
    let one_joined_value = Selection {
        fields: Some(vec![FieldPredicate {
            index: "kind".into(),
            op: "any_of".into(),
            values: Some(vec!["a|b".into()]),
            number: None,
        }]),
        ..Selection::default()
    };
    let two_split_values = Selection {
        fields: Some(vec![FieldPredicate {
            index: "kind".into(),
            op: "any_of".into(),
            values: Some(vec!["a".into(), "b".into()]),
            number: None,
        }]),
        ..Selection::default()
    };
    assert_ne!(
        Cursor::compute_digest(&one_joined_value),
        Cursor::compute_digest(&two_split_values),
        "a length-prefixed digest must not let list-splitting collide"
    );

    let one_key = Selection { keys: Some(vec!["a,b".into()]), ..Selection::default() };
    let two_keys = Selection { keys: Some(vec!["a".into(), "b".into()]), ..Selection::default() };
    assert_ne!(Cursor::compute_digest(&one_key), Cursor::compute_digest(&two_keys));
}

// ------------------------------------------------------------------------------------------
// R-E: the alias matcher, exercised at the `select`/`validate` level (the module's own unit
// tests in cultnet_rs::selection::schema_alias::tests cover the string algorithm directly).
// ------------------------------------------------------------------------------------------

#[test]
fn schemas_reaches_a_schema_by_its_unversioned_name_alias() {
    let rows = base_rows();
    let selection = Selection {
        schemas: Some(vec![leaf_a_name_alias().into()]),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    let mut ids: Vec<&str> = evaluation.rows.iter().map(Row::record_key).collect();
    ids.sort_unstable();
    assert_eq!(ids, vec!["a-eq", "a-lo", "shared-1", "shared-3"], "every leaf_a row, by name alias");
}

// R-E, Self's ruling 2026-09-22: a hash-shaped alias ("<hash>.v1") is not a wire form the C#
// reference's production rule ever resolves - it strips the ".v1" and compares the bare hash text
// against the schema's declared *name*, never equal. `schemas` never refuses (unlike cites.target
// below), so an unresolved alias just narrows reachability to nothing.
#[test]
fn schemas_does_not_reach_a_schema_by_a_hash_shaped_alias() {
    let rows = base_rows();
    let selection = Selection {
        schemas: Some(vec![leaf_a_hash_alias().into()]),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert!(evaluation.rows.is_empty(), "a hash-shaped alias must not resolve to leaf_a");
}

// R-E, at the fast path `matches()` uses directly (the v0 snapshot server's own caller,
// serve_read_only_raw_snapshot, never calls validate/select - v0 has its own lowering instead).
// An empty-but-present schemas list must match nothing there too, the same as the door's own
// rule for select() - schema_alias::matches_any's own "empty candidates matches everything"
// convention (ported faithfully from the C# reference's MatchesAny, for the door/reachability
// call sites that already refuse or normalise an empty list before this ever runs) must not leak
// into this call site, which has no such guarantee upstream of it.
#[test]
fn matches_treats_an_empty_but_present_schemas_list_as_matching_nothing() {
    let row = FixtureRow::leaf(leaf_a(), "a-eq", 1, "weapon", "5");
    let selection = Selection { schemas: Some(Vec::new()), ..Selection::default() };
    assert!(
        !cultnet_rs::matches(&row, &selection),
        "an empty-but-present schemas list must never be read as \"no filter\" by the evaluator"
    );
}

#[test]
fn cites_target_resolves_through_the_name_alias_matcher() {
    let rows = base_rows();
    let selection = Selection {
        cites: Some(Citation { target: rr(leaf_a_name_alias(), "a-eq"), role: Some("Design".into()) }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["citer-1"]);
}

// R-E, Self's ruling 2026-09-22: a hash-shaped alias resolves to no known schema (see
// schemas_does_not_reach_a_schema_by_a_hash_shaped_alias above), so a cites.target naming one is
// refused at the door (R-E's unmatched-target rule), not silently answered with an empty page.
#[test]
fn cites_target_by_a_hash_shaped_alias_is_refused_as_unmatched() {
    let selection = Selection {
        cites: Some(Citation { target: rr(leaf_a_hash_alias(), "a-eq"), role: Some("Design".into()) }),
        ..Selection::default()
    };
    let error = validate(&selection, &FixtureRowSet).expect_err("hash-shaped alias must be refused");
    assert_eq!(error.field, "cites.target.schemaId");
}

#[test]
fn validate_refuses_a_cites_target_schema_that_matches_no_known_schema() {
    let selection = Selection {
        cites: Some(Citation {
            target: rr("sha256:not-a-known-schema", "a-eq"),
            role: Some("Design".into()),
        }),
        ..Selection::default()
    };
    let error = validate(&selection, &FixtureRowSet).expect_err("unmatched target must be refused");
    assert_eq!(error.field, "cites.target.schemaId");
}

#[test]
fn schema_alias_module_is_reachable_from_the_crate_root() {
    assert!(schema_alias::matches(leaf_a_name_alias(), leaf_a(), leaf_a_name()));
    assert!(!schema_alias::matches(leaf_a_name_alias(), leaf_a(), leaf_b_name()));
    assert!(!schema_alias::matches(leaf_a_hash_alias(), leaf_a(), leaf_a_name()));
}

// ------------------------------------------------------------------------------------------
// R-G/P-1: `matched` is the total match count, not the page count.
// ------------------------------------------------------------------------------------------

#[test]
fn matched_is_the_total_count_not_the_page_count() {
    let rows = base_rows();
    let selection = Selection { limit: Some(2), ..Selection::default() };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.len(), 2, "the page is clipped to the limit");
    assert_eq!(evaluation.matched, 7, "matched counts every row base_rows() carries");
}

// ------------------------------------------------------------------------------------------
// R-I: `select_page` projects `select`'s generic rows into the wire's `SelectionPage` - a header
// projection carries no payload, in the page's rows and in its edges alike (S20).
// ------------------------------------------------------------------------------------------

fn document_record_for(row: &FixtureRow) -> SelectionDocumentRecord {
    SelectionDocumentRecord {
        schema_id: row.schema_id.to_string(),
        schema_name: None,
        schema_version: None,
        schema_content_hash: None,
        record_key: row.record_key.to_string(),
        stored_at: "2026-09-22T00:00:00Z".to_string(),
        payload_encoding: "messagepack".to_string(),
        payload: vec![0xC0], // a nonempty payload every header-projection assertion must not see
        source_runtime_id: None,
        source_agent_id: None,
        source_role: None,
        tags: None,
    }
}

#[test]
fn select_page_header_projection_carries_no_payload_in_rows_or_edges() {
    let rows = vec![
        FixtureRow::leaf(leaf_a(), "a-eq", 1, "weapon", "5"),
        FixtureRow::citer("citer-1", 2, vec![("Design".to_string(), rr(leaf_a(), "a-eq"), Some(vec![7]))]),
    ];
    let selection = Selection {
        cites: Some(Citation { target: rr(leaf_a(), "a-eq"), role: Some("Design".into()) }),
        projection: "header".into(),
        ..Selection::default()
    };
    let page: SelectionPage =
        select_page(&FixtureRowSet, &rows, &selection, 1, document_record_for).unwrap();
    assert!(page.documents.is_none());
    let headers = page.headers.expect("header projection carries headers");
    assert_eq!(headers.len(), 1);
    assert_eq!(headers[0].record_key, "citer-1");
    let edges = page.edges.expect("a hop-bearing selection carries edges");
    assert_eq!(edges.len(), 1);
    assert_eq!(edges[0].payload, None, "S20: a header projection's edges carry no payload");
    assert_eq!(edges[0].payload_encoding, None);
}

#[test]
fn select_page_document_projection_carries_payload_in_rows_and_edges() {
    let rows = vec![
        FixtureRow::leaf(leaf_a(), "a-eq", 1, "weapon", "5"),
        FixtureRow::citer("citer-1", 2, vec![("Design".to_string(), rr(leaf_a(), "a-eq"), Some(vec![7]))]),
    ];
    let selection = Selection {
        cites: Some(Citation { target: rr(leaf_a(), "a-eq"), role: Some("Design".into()) }),
        projection: "document".into(),
        ..Selection::default()
    };
    let page: SelectionPage =
        select_page(&FixtureRowSet, &rows, &selection, 1, document_record_for).unwrap();
    assert!(page.headers.is_none());
    let documents = page.documents.expect("document projection carries documents");
    assert_eq!(documents.len(), 1);
    assert_eq!(documents[0].payload, vec![0xC0]);
    let edges = page.edges.expect("a hop-bearing selection carries edges");
    assert_eq!(edges[0].payload, Some(vec![7]));
    assert_eq!(edges[0].payload_encoding.as_deref(), Some("messagepack"));
}

#[test]
fn select_page_matched_is_the_total_count() {
    let rows = base_rows();
    let selection = Selection { limit: Some(2), ..Selection::default() };
    let page: SelectionPage = select_page(&FixtureRowSet, &rows, &selection, 1, document_record_for).unwrap();
    assert_eq!(page.matched, 7);
    assert_eq!(page.headers.unwrap().len(), 2);
}

// ------------------------------------------------------------------------------------------
// R-M: `SelectionDocumentRecord`/`RawDocumentHeader`/`Edge`/`RecordRef` used to be encoded and
// decoded by ~335 lines of hand-written `rmpv::Value` construction (contracts.rs). R-M replaced
// that with the generic `rmpv::ext::from_value`/`to_value` bridge over their own serde derives.
// This proves the replacement round-trips through the real message envelope
// (`cultnet.snapshot_response_raw.v1`, MessagePack, named maps) both ways - headers, documents
// with a real payload, and edges with and without a payload.
// ------------------------------------------------------------------------------------------

#[test]
fn snapshot_response_raw_v1_round_trips_headers_documents_and_edges_through_the_generic_codec() {
    let message = CultNetMessage::SnapshotResponseRawV1 {
        message_id: "msg-1".to_string(),
        matched: 3,
        as_of: 42,
        next: Some("cursor-1".to_string()),
        // A message carries exactly one of headers/documents (validate_message's own invariant,
        // matching Selection::projection); this vector exercises the document side, the richer of
        // the two.
        headers: None,
        documents: Some(vec![SelectionDocumentRecord {
            schema_id: leaf_a().to_string(),
            schema_name: None,
            schema_version: None,
            schema_content_hash: None,
            record_key: "a-eq".to_string(),
            stored_at: "2026-09-22T00:00:00Z".to_string(),
            payload_encoding: "messagepack".to_string(),
            payload: vec![1, 2, 3, 4, 5],
            source_runtime_id: None,
            source_agent_id: None,
            source_role: None,
            tags: None,
        }]),
        edges: Some(vec![
            Edge {
                from: rr(citer(), "citer-1"),
                role: "Design".to_string(),
                to: rr(leaf_a(), "a-eq"),
                payload_encoding: Some("messagepack".to_string()),
                payload: Some(vec![9, 9]),
            },
            Edge {
                from: rr(citer(), "citer-1"),
                role: "parent".to_string(),
                to: rr(leaf_b(), "b-hi"),
                payload_encoding: None,
                payload: None,
            },
        ]),
        shard_id: Some("shard-1".to_string()),
        shard_epoch: Some(5),
        shard_log_sequence: Some(100),
    };

    let bytes =
        encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).expect("encodes");
    let decoded = decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0)
        .expect("decodes");
    assert_eq!(decoded, message);
}

// The header side of the same message shape (S20: no payload field at all, since
// `RawDocumentHeader` has none to carry).
#[test]
fn snapshot_response_raw_v1_round_trips_headers_through_the_generic_codec() {
    let message = CultNetMessage::SnapshotResponseRawV1 {
        message_id: "msg-2".to_string(),
        matched: 1,
        as_of: 7,
        next: None,
        headers: Some(vec![RawDocumentHeader {
            schema_id: leaf_a().to_string(),
            schema_name: Some("leaf_a".to_string()),
            schema_version: Some("v1".to_string()),
            schema_content_hash: Some("hash".to_string()),
            record_key: "a-eq".to_string(),
            stored_at: "2026-09-22T00:00:00Z".to_string(),
            source_runtime_id: Some("runtime-1".to_string()),
            source_agent_id: None,
            source_role: None,
            tags: Some(vec!["tag-a".to_string()]),
        }]),
        documents: None,
        edges: None,
        shard_id: None,
        shard_epoch: None,
        shard_log_sequence: None,
    };

    let bytes =
        encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).expect("encodes");
    let decoded = decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0)
        .expect("decodes");
    assert_eq!(decoded, message);
}
