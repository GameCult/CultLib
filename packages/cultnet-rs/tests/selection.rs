//! CultNet typed selection, Cut 1 commit 3 (docs/cultnet-selection-cut.md section 7/10). A toy row
//! set implementing `Row`/`RowSet` directly - D7: no `cultcache-rs` knowledge, matching S14's brief
//! ("select_over_a_toy_row_set_matches_orders_hops_pages_and_refuses") rather than reproducing every
//! one of C#'s S1-S23 fixtures 1:1. Section 10's negative greps and the parity vectors (S12, S13) are
//! here too.

use std::fs;
use std::path::Path;

use cultnet_rs::{
    Citation, FieldPredicate, Incoming, RecordRef, Row, RowSet, Selection, SelectionOperator,
    SelectionRefusal, canonical_number, select, validate,
};

// ------------------------------------------------------------------------------------------
// The fixture: two leaves under a conceptual abstract middle (mass declared on both,
// independently - Rust has no inheritance to share it through), a citer with four reference
// shapes (a single "Design" reference into the abstract middle's leaf set, a "related" many
// reference, a "components" many-dictionary reference with float payloads, and a "parent"
// single reference), and a narrow citer whose "narrow_ref" targets only leaf_a - the edge S18
// needs to reach outside its declared target.
// ------------------------------------------------------------------------------------------

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
            schema_id: "citer",
            record_key,
            ordinal,
            kind: None,
            mass: None,
            references,
        }
    }
}

impl Row for FixtureRow {
    fn schema_id(&self) -> &str {
        self.schema_id
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
            "leaf_a".into(),
            "leaf_b".into(),
            "citer".into(),
            "narrow_citer".into(),
        ]
    }
    fn declared_indexes(&self, schema_id: &str) -> Vec<String> {
        match schema_id {
            "leaf_a" | "leaf_b" => vec!["kind".into(), "mass".into()],
            _ => Vec::new(),
        }
    }
    fn declared_roles(&self, schema_id: &str) -> Vec<String> {
        match schema_id {
            "citer" => vec![
                "parent".into(),
                "related".into(),
                "components".into(),
                "Design".into(),
            ],
            "narrow_citer" => vec!["narrow_ref".into()],
            _ => Vec::new(),
        }
    }
    fn is_numeric(&self, schema_id: &str, index: &str) -> bool {
        matches!(schema_id, "leaf_a" | "leaf_b") && index == "mass"
    }
    fn target_leaves(&self, role: &str) -> Vec<String> {
        match role {
            "parent" | "Design" | "related" | "components" => {
                vec!["leaf_a".into(), "leaf_b".into()]
            }
            "narrow_ref" => vec!["leaf_a".into()],
            _ => Vec::new(),
        }
    }
}

fn rr(schema_id: &str, record_key: &str) -> RecordRef {
    RecordRef::new(schema_id, record_key)
}

fn base_rows() -> Vec<FixtureRow> {
    vec![
        FixtureRow::leaf("leaf_a", "a-lo", 1, "weapon", "4"),
        FixtureRow::leaf("leaf_a", "a-eq", 2, "weapon", "5"),
        FixtureRow::leaf("leaf_b", "b-hi", 3, "armor", "6"),
        FixtureRow::leaf("leaf_a", "shared-1", 4, "shared", "1"),
        FixtureRow::leaf("leaf_b", "shared-2", 5, "shared", "1"),
        FixtureRow::leaf("leaf_a", "shared-3", 6, "shared", "1"),
        FixtureRow::citer(
            "citer-1",
            7,
            vec![(
                "Design".to_string(),
                rr("leaf_a", "a-eq"),
                None,
            )],
        ),
    ]
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
            target: rr("leaf_a", "a-eq"),
            role: Some("Design".into()),
        }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["citer-1"]);
    assert_eq!(evaluation.edges.len(), 1);
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
            schemas: Some(vec!["leaf_a".into()]),
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
            schemas: Some(vec!["leaf_a".into()]),
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
        FixtureRow::leaf("leaf_a", "lo", 1, "k", "4"),
        FixtureRow::leaf("leaf_a", "eq", 2, "k", "5"),
        FixtureRow::leaf("leaf_a", "hi", 3, "k", "6"),
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
        FixtureRow::leaf("leaf_b", "b", 1, "k", "1"),
        FixtureRow {
            schema_id: "narrow_citer",
            record_key: "bad",
            ordinal: 2,
            kind: None,
            mass: None,
            references: vec![("narrow_ref".to_string(), rr("leaf_b", "b"), None)],
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
            ("components".to_string(), rr("leaf_a", "part-1"), Some(vec![1, 5])),
            ("components".to_string(), rr("leaf_a", "part-2"), Some(vec![2, 5])),
        ],
    );
    let payloads: std::collections::HashSet<Vec<u8>> =
        citer.references().into_iter().filter_map(|(_, _, payload)| payload).collect();
    assert_eq!(payloads.len(), 2, "the two dictionary entries must keep distinct payloads");
}

// The hop's own edges: a `cites` selection's page holds the citer, so its edges (From = citer)
// survive the page filter in `select` - the direction where edges are visible end to end, mirroring
// `EvaluatorHopsOneEdgeByDeclaredReferenceAndRole`. A `cited` selection's page holds the *citee*
// instead (section 2/8: candidates are filtered to rows the incoming index contains), so its edges
// (also From = citer) do not survive that same filter unless the citer independently matches the
// selection too - this is the reference's actual behaviour (CultNetSelectionEvaluator.Select's
// `pageKeys.Contains(edge.From.Key.Value)`), not a Rust-side gap, and is pinned here rather than
// silently relied upon.
#[test]
fn cited_selections_page_the_citee_so_their_edges_do_not_survive_the_page_filter() {
    let rows = vec![
        FixtureRow::leaf("leaf_a", "part-1", 1, "k", "1"),
        FixtureRow::citer(
            "assembly",
            2,
            vec![("components".to_string(), rr("leaf_a", "part-1"), Some(vec![9]))],
        ),
    ];
    let selection = Selection {
        cited: Some(Incoming { role: "components".into(), exists: true }),
        ..Selection::default()
    };
    let evaluation = select(&FixtureRowSet, &rows, &selection, 1).unwrap();
    assert_eq!(evaluation.rows.iter().map(Row::record_key).collect::<Vec<_>>(), vec!["part-1"]);
    assert!(evaluation.edges.is_empty(), "the citer (assembly) is not itself on the page");
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
        schemas: Some(vec!["leaf_a".into(), "  ".into()]),
        ..Selection::default()
    };
    assert_eq!(validate(&blank_schema, &FixtureRowSet).unwrap_err().field, "schemas[1]");

    let blank_key = Selection {
        keys: Some(vec!["".into()]),
        ..Selection::default()
    };
    assert_eq!(validate(&blank_key, &FixtureRowSet).unwrap_err().field, "keys[0]");
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
        schemas: Some(vec!["leaf_a".into(), "leaf_b".into()]),
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
        cites: Some(Citation { target: rr("leaf_a", "a-eq"), role: Some("Design".into()) }),
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

fn evaluate_vector(vector: &Vector) -> (Vec<String>, u32, bool, Vec<VectorEdge>) {
    use base64::Engine;
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&vector.selection_message_pack_base64)
        .expect("vector selection bytes are valid base64");
    let selection: Selection =
        rmp_serde::from_slice(&bytes).expect("vector selection bytes decode as MessagePack");
    let rows = base_rows();
    let evaluation = select(&FixtureRowSet, &rows, &selection, vector.as_of)
        .expect("this campaign's vectors are all valid selections");
    let ids: Vec<String> = evaluation
        .rows
        .iter()
        .map(|row| row_id(row.schema_id(), row.record_key()))
        .collect();
    let matched = ids.len() as u32;
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
    (ids, matched, has_next, edges)
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
    for vector in &file.vectors {
        if let Some(expected_field) = &vector.expected_refusal_field {
            assert_refusal_vector(vector, expected_field);
            continue;
        }
        let (ids, matched, has_next, edges) = evaluate_vector(vector);
        assert_eq!(ids, vector.expected_ids, "vector {:?}: row ids", vector.name);
        assert_eq!(matched, vector.matched, "vector {:?}: matched", vector.name);
        assert_eq!(has_next, vector.has_next, "vector {:?}: hasNext", vector.name);
        assert_eq!(
            edges.len(),
            vector.expected_edges.len(),
            "vector {:?}: edge count",
            vector.name
        );
        for (actual, expected) in edges.iter().zip(vector.expected_edges.iter()) {
            assert_eq!(actual.from_id, expected.from_id, "vector {:?}: edge.from", vector.name);
            assert_eq!(actual.role, expected.role, "vector {:?}: edge.role", vector.name);
            assert_eq!(actual.to_id, expected.to_id, "vector {:?}: edge.to", vector.name);
            assert_eq!(
                actual.payload_base64, expected.payload_base64,
                "vector {:?}: edge.payload",
                vector.name
            );
        }
    }
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
            // Not "hop_by_role"/cites: MatchesCitation on the C# side compares
            // Citation.Target.SchemaId against the real (SHA-256) schema id exactly, with no
            // SchemaName fallback, so a cites-based vector cannot cross this fixture pair (see
            // SelectionParityVectorTests's header comment in the C# test file). `cited` matches
            // by role and record key only - no schema id comparison - so it is the hop direction
            // both runtimes can agree on here.
            "incoming_exists_true",
            Selection {
                schemas: Some(vec!["leaf_a".into()]),
                cited: Some(Incoming { role: "Design".into(), exists: true }),
                ..Selection::default()
            },
        ),
        (
            "incoming_negation",
            Selection {
                schemas: Some(vec!["leaf_a".into()]),
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
    ];

    let mut vectors = Vec::new();
    for (name, selection) in cases {
        let bytes = rmp_serde::to_vec_named(&selection).expect("encodes");
        let rows = base_rows();
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
            matched: ids.len() as u32,
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
            "keys[1]",
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
