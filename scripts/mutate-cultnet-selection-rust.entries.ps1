# Entries for tools/eureka-mutations.ps1 (C:\Users\Meta\.claude\skills\eureka\tools):
# CultNet typed selection, Cut 1 commit 3 - the Rust evaluator
# (src/selection.rs), mirroring the reference's rules at wire parity
# (docs/cultnet-selection-cut.md section 6/7/10). Target: src/selection.rs.
#
# Two commands are in play (the harness runs M0 once per distinct Command in the suite):
# `cargo test --test selection -- --skip
# selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust` for the
# evaluator-level rules (the integration test binary in packages/cultnet-rs/tests/selection.rs),
# and `cargo test --lib` for the canonical_number/schema_alias modules' own unit tests (not
# otherwise exercised by the integration suite). Run with CARGO_TARGET_DIR set to this worktree's
# own target directory per the campaign's build rules.
#
# The skip is this fix batch's own version of the flaky-test carve-out below: the committed
# contracts/cultnet/interop/selection-vectors.cs-written.json is stale (pre-R-B/R-C/R-D/R-E/R-I,
# written against the old name-as-schema-id fixture), and only lands green once the parallel C#
# Hands regenerates it. This suite does not touch that file (out of scope - the spec names it as
# the C# side's own artifact) and skips only that one test for M0's sake; every other test in the
# binary, including the Rust->C# vector writer, stays in M0.
#
# Not exhaustive of every rule selection.rs carries (S1-S23's Rust-side coverage is
# "select_over_a_toy_row_set_matches_orders_hops_pages_and_refuses" plus these named mutants, per
# the map's S14): order, cursor freshness and digest, the one negation, the numeric boundary, the
# out-of-target refusal, and Q-J's door/comparator/renderer - the rules a Rust caller (Huginn)
# depends on that the reference's own suite already pins on the C# side.

@(
    @{
        Id     = 'RS-S3-Revert'
        Rule   = 'S3: order reverses under descending.'
        Test   = 'orders_by_ordinal_then_identity_and_reverses'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if descending { ordering.reverse() } else { ordering }'
        New    = 'let _ = descending; ordering'
    },
    @{
        Id     = 'RS-CursorStale-Revert'
        Rule   = 'A cursor from a moved asOf is refused cursor_stale.'
        Test   = 'pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if cursor.as_of != as_of {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-CursorStale-Loosening'
        Rule   = 'Staleness compares equality, not only "cursor is newer than the current asOf".'
        Test   = 'pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if cursor.as_of != as_of {'
        New    = 'if cursor.as_of > as_of {'
    },
    @{
        Id     = 'RS-CursorDigest-Revert'
        Rule   = 'A cursor whose digest does not match this selection is refused cursor_invalid.'
        Test   = 'pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if cursor.digest != Cursor::compute_digest(selection) {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-S7-Revert'
        Rule   = 'S7: cited { exists } is honoured, not ignored.'
        Test   = 'cited_exists_is_the_one_negation'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'candidates.retain(|row| incoming.contains(row.record_key()) == exists);'
        New    = 'candidates.retain(|row| incoming.contains(row.record_key()) || true);'
    },
    @{
        Id     = 'RS-S7-Loosening'
        Rule   = 'exists:true and exists:false must answer opposite sets, not the same test inverted twice.'
        Test   = 'cited_exists_is_the_one_negation'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'candidates.retain(|row| incoming.contains(row.record_key()) == exists);'
        New    = 'candidates.retain(|row| incoming.contains(row.record_key()) != exists);'
    },
    @{
        Id     = 'RS-S16-Le-Revert'
        Rule   = 'S16: le includes the equal boundary.'
        Test   = 'compares_numbers_at_the_boundary_for_all_four_operators'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'SelectionOperator::Le => ordering != Ordering::Greater,'
        New    = 'SelectionOperator::Le => ordering == Ordering::Less,'
    },
    @{
        Id     = 'RS-S16-Ge-Revert'
        Rule   = 'S16: ge includes the equal boundary.'
        Test   = 'compares_numbers_at_the_boundary_for_all_four_operators'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'SelectionOperator::Ge => ordering != Ordering::Less,'
        New    = 'SelectionOperator::Ge => ordering == Ordering::Greater,'
    },
    @{
        Id     = 'RS-S18-Revert'
        Rule   = "D9/S18: an edge naming a row outside its reference's declared target refuses the selection."
        Test   = 'refuses_an_edge_outside_its_declared_target'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if leaves.is_empty() || leaves.iter().any(|leaf| leaf == to.schema_id()) {
        return Ok(());
    }'
        New    = 'if true {
        return Ok(());
    }'
    },
    @{
        Id     = 'RS-QJ-Door-Regex-Revert'
        Rule   = 'Q-J: the door refuses a non-canonical number spelling rather than accepting it.'
        Test   = 'validation_refuses_non_canonical_number_spellings'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'Some(number) if !canonical_number::is_canonical(number) => {'
        New    = 'Some(number) if false && !canonical_number::is_canonical(number) => {'
    },
    @{
        Id     = 'RS-QJ-Door-NegativeZero-Revert'
        Rule   = 'Q-J: "-0" is excluded even though the digit grammar alone matches it.'
        Test   = 'validation_refuses_non_canonical_number_spellings'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if value == "-0" {
            return false;
        }'
        New    = ''
    },
    @{
        Id     = 'RS-QJ-Compare-IntegerLength-Revert'
        Rule   = "Q-J: the comparator checks the integer part's length before its digits."
        Test   = 'canonical_number_compares_by_magnitude'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'let magnitude = if integer_left.len() != integer_right.len() {
            integer_left.len().cmp(&integer_right.len())
        } else {'
        New    = 'let magnitude = if false {
            integer_left.len().cmp(&integer_right.len())
        } else {'
    },
    @{
        Id     = 'RS-QJ-Compare-Sign-Revert'
        Rule   = 'Q-J: the comparator orders a negative value below a positive one.'
        Test   = 'canonical_number_compares_by_sign'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if negative_left != negative_right {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-QJ-RenderInteger-Float64-Revert'
        Rule   = 'Q-J: an i64 renders its exact decimal, never through f64 (2^53 boundary).'
        Test   = 'selection::canonical_number::tests::render_i64_past_2_pow_53_is_exact'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = 'pub fn render_i64(value: i64) -> String {
        canonicalize_decimal_digits(&value.to_string())
    }'
        New    = 'pub fn render_i64(value: i64) -> String {
        canonicalize_decimal_digits(&(value as f64).to_string())
    }'
    },
    @{
        Id     = 'RS-QJ-DecimalTrailingZeros-Revert'
        Rule   = 'Q-J: canonicalize_decimal_digits strips trailing fractional zeros.'
        Test   = 'selection::canonical_number::tests::canonicalize_decimal_digits_strips_leading_and_trailing_zeros'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = "let fraction_part = fraction_part.trim_end_matches('0');"
        New    = 'let fraction_part = fraction_part;'
    },
    @{
        # R-M deleted expand_scientific_notation (dead: no render_f64/f32 input ever reached it,
        # per probe_f64_display_never_uses_exponent_notation) along with its own test. R-D replaced
        # the whole shortest-form-plus-expansion path with an exact decimal expansion computed
        # straight from mantissa and exponent - these entries pin that algorithm instead.
        # Not "New = if false", which the first run of this suite proved hangs: that mutation
        # routes every value (including f64::MAX's exponent of ~971) into the fraction branch,
        # whose `k = (-exponent) as usize` wraps a positive exponent's negation to a huge usize and
        # spins `for _ in 0..k` effectively forever - the run had to kill that process by PID.
        # `if true` is the safe direction: it routes negative-exponent values (0.1, subnormals)
        # into the *shift* branch instead, where `for _ in 0..exponent` with a negative i32 is
        # simply an empty range (no cast, no loop) - wrong digits, not a hang.
        Id     = 'RS-D-ExponentBranch-Revert'
        Rule   = "R-D: exponent >= 0 shifts left (mantissa * 2^exponent); a negative exponent must take the fraction branch, not the shift branch."
        Test   = 'selection::canonical_number::tests::render_f32_and_f64_of_0_1_render_the_distinct_exact_binary_values'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = 'let unsigned = if exponent >= 0 {'
        New    = 'let unsigned = if true {'
    },
    @{
        Id     = 'RS-D-SubnormalExponent-Revert'
        Rule   = 'R-D: a subnormal f64 uses exponent -1074 (1 - bias - fraction bits), not the normal formula on a zero-implicit-bit mantissa.'
        Test   = 'selection::canonical_number::tests::render_f32_and_f64_subnormals_round_trip'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = '(fraction, -1074) // subnormal: 1 - 1023 (bias) - 52 (fraction bits)'
        New    = '(fraction, biased_exponent - 1023 - 52)'
    },
    # RS-D-ZeroMantissa-Revert and RS-D-BigMulCarry-Loosening are gone, not fixed in place: the
    # first run proved both structurally unkillable, not fixture gaps to patch around.
    # RS-D-ZeroMantissa-Revert reverted `exact_decimal_from_mantissa`'s `if mantissa == 0 { return
    # "0" }` guard - removing it left the test green, because the general bignum path already
    # collapses a zero mantissa to "0" at every step (multiplying zero stays zero, and
    # canonicalize_decimal_digits collapses the signed "-0" case). The dead guard is deleted from
    # src/selection.rs; there is no longer a rule here to pin.
    # RS-D-BigMulCarry-Loosening replaced the carry-drain `while` with a single `if` - also green,
    # because `big_mul_small` only ever multiplies by 2 or 5 (single digits) against a digit that
    # is itself always < 10, so the carry can never reach two digits and the `while` never ran more
    # than once. `big_mul_small` is simplified to the single `if` (plus a `debug_assert!` naming
    # the now-explicit single-digit-factor precondition) in src/selection.rs instead of carrying a
    # mutation entry for a branch that was never reachable.
    @{
        Id     = 'RS-E-Alias-Revert'
        Rule   = 'R-E: schema-alias matching bridges two ids by their inferred name, not only exact equality.'
        Test   = 'selection::schema_alias::tests::matches_by_inferred_name_across_versions'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = 'let candidate_name = infer_schema_name(candidate).unwrap_or(candidate);
        let schema_name = infer_schema_name(schema_id).unwrap_or(schema_id);
        candidate_name == schema_name'
        New    = 'false'
    },
    @{
        Id     = 'RS-E-InferSchemaName-LeadingMarker-Revert'
        Rule   = 'R-E: a schema id that IS just ".vN" (marker at position 0) infers no name.'
        Test   = 'selection::schema_alias::tests::infer_schema_name_refuses_a_leading_marker_and_a_non_numeric_or_empty_version'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = 'if marker == 0 {
            return None;
        }'
        New    = ''
    },
    @{
        Id     = 'RS-E-CitesTarget-Revert'
        Rule   = 'R-E: cites.target.schemaId is refused at the door when it matches no known schema.'
        Test   = 'validate_refuses_a_cites_target_schema_that_matches_no_known_schema'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if !row_set
            .all_schema_ids()
            .iter()
            .any(|schema_id| schema_alias::matches(&cites.target.schema_id, schema_id))
        {'
        New    = 'if false {'
    },
    # The first run of this suite found this call site's own real bug, via a pre-existing entry
    # (RS-V0Lowering-Revert) that unexpectedly survived: matches_schema_keys_fields used to read
    # schemas through schema_alias::matches_any, whose own "empty candidates matches everything"
    # is correct for the door/reachability call sites (ported faithfully from the C# reference's
    # MatchesAny) but wrong here - v0's snapshot server calls matches() directly, without
    # validate(), so an empty-but-present schemas list must still match nothing. Fixed in
    # src/selection.rs; this entry pins the fix directly rather than only through v0's own lowering.
    @{
        Id     = 'RS-E-EmptySchemasNeverMatchesEverything-Revert'
        Rule   = 'R-E: an empty-but-present schemas list matches nothing at the fast path, not everything.'
        Test   = 'matches_treats_an_empty_but_present_schemas_list_as_matching_nothing'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if let Some(schemas) = &selection.schemas
        && !schemas.iter().any(|candidate| schema_alias::matches(candidate, row.schema_id()))
    {
        return false;
    }'
        New    = 'if let Some(schemas) = &selection.schemas
        && !schema_alias::matches_any(schemas, row.schema_id())
    {
        return false;
    }'
    },
    @{
        Id     = 'RS-F-SelectValidatesFirst-Revert'
        Rule   = 'R-F: select() validates the selection before evaluating it, and returns the typed refusal.'
        Test   = 'select_refuses_an_invalid_selection_instead_of_evaluating_it'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'validate(selection, row_set)?;'
        New    = ''
    },
    @{
        Id     = 'RS-F-MatchesAssert-Revert'
        Rule   = "R-F: matches()'s hop guard is a real assert!, not a debug_assert! a release build compiles out."
        Test   = 'matches_refuses_a_hop_bearing_selection_even_in_a_release_style_assert'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'assert!(
        !selection.has_hop(),
        "a hop-bearing selection (cites/cited) is set-dependent and cannot use the single-row \
         matches fast path; reconcile the full row set with select() instead"
    );'
        New    = ''
    },
    @{
        Id     = 'RS-G-Matched-Revert'
        Rule   = 'R-G/P-1: Evaluation.matched is the total match count, not the page count.'
        Test   = 'matched_is_the_total_count_not_the_page_count'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'let matched_total = matched.len() as u32;'
        New    = 'let matched_total = 0u32;'
    },
    @{
        Id     = 'RS-H-DigestLengthPrefix-Revert'
        Rule   = 'R-H: the cursor digest length-prefixes every list, so two differently split lists never collide.'
        Test   = 'cursor_digest_does_not_collide_on_differently_split_lists'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'fn write_length_prefixed_list(buffer: &mut String, values: &[String]) {
    buffer.push_str(&values.len().to_string());
    buffer.push(''['');
    for value in values {
        write_length_prefixed(buffer, value);
    }
    buffer.push('']'');
}'
        New    = 'fn write_length_prefixed_list(buffer: &mut String, values: &[String]) {
    buffer.push_str(&values.join(","));
}'
    },
    @{
        Id     = 'RS-B-CitedEdgeDirection-Revert'
        Rule   = "R-B: under `cited`, an edge's owning page row is its To (the citee), not its From (the citer)."
        Test   = 'cited_selections_page_the_citee_and_their_edges_survive_the_page_filter'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'let cited_direction = selection.cited.is_some();'
        New    = 'let cited_direction = false;'
    },
    @{
        Id     = 'RS-B-CitesEdgeDirection-Revert'
        Rule   = "R-B: under `cites`, an edge's owning page row is its From (the citer) - the converse of RS-B-CitedEdgeDirection-Revert."
        Test   = 'cites_selections_still_page_the_citer_and_key_edges_off_it'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'let cited_direction = selection.cited.is_some();'
        New    = 'let cited_direction = true;'
    },
    @{
        Id     = 'RS-I-HeaderCarriesNoEdgePayload-Revert'
        Rule   = "S20/R-I: a header-projection page's edges carry no payload, even when the underlying edge has one."
        Test   = 'select_page_header_projection_carries_no_payload_in_rows_or_edges'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'payload: if want_document { edge.payload.clone() } else { None },'
        New    = 'payload: edge.payload.clone(),'
    },
    @{
        Id     = 'RS-I-ProjectionChoosesHeadersOrDocuments-Revert'
        Rule   = 'R-I: document projection carries documents, not headers - the two are never both Some.'
        Test   = 'select_page_document_projection_carries_payload_in_rows_and_edges'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'let want_document = selection.projection == PROJECTION_DOCUMENT;'
        New    = 'let want_document = false;'
    },
    @{
        Id     = 'RS-S2-Revert'
        Rule   = 'S2: an any_of predicate matches row-value membership, not "any value at all".'
        Test   = 'conjoins_any_of_and_comparison_predicates'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if !values.iter().any(|v| wanted.iter().any(|w| w == v)) {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-S6-Revert'
        Rule   = 'S6: cites.role, when present, filters which declared reference the hop follows.'
        Test   = 'cites_role_excludes_a_second_reference_at_the_same_target'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = "if let Some(wanted_role) = &citation.role
            && *wanted_role != role
        {"
        New    = 'if false {'
    },
    @{
        Id     = 'RS-EmptySchemas-Revert'
        Rule   = 'S1: an empty (but present) schemas list is refused, never answered as an empty page.'
        Test   = 'validation_refuses_undeclared_index_role_and_empty_lists'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if matches!(&selection.schemas, Some(schemas) if schemas.is_empty()) {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-EmptyKeys-Revert'
        Rule   = 'S1: an empty (but present) keys list is refused, never answered as an empty page.'
        Test   = 'validation_refuses_undeclared_index_role_and_empty_lists'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if matches!(&selection.keys, Some(keys) if keys.is_empty()) {'
        New    = 'if false {'
    },
    # Self's ruling, 2026-09-22 (docs/cultnet-selection-cut.md, commit 2 fix batch).
    @{
        Id     = 'RS-BlankSchema-Revert'
        Rule   = 'The door refuses a blank entry inside a present schemas list, not only an empty list.'
        Test   = 'validation_refuses_a_blank_entry_in_schemas_or_keys'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if schema.trim().is_empty() {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-BlankKey-Revert'
        Rule   = 'The door refuses a blank entry inside a present keys list.'
        Test   = 'validation_refuses_a_blank_entry_in_schemas_or_keys'
        Command = 'cargo test --test selection -- --skip selection_vectors_written_by_the_reference_decode_and_evaluate_identically_in_rust'
        File   = 'src/selection.rs'
        Old    = 'if key.trim().is_empty() {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-V0Lowering-Revert'
        Rule   = 'v0''s own cleaning: an empty or all-blank v0 list lowers to null (no filter), not to a list the door or the evaluator would have to refuse or misread.'
        Test   = 'serve_read_only_raw_snapshot_lowers_an_empty_or_blank_v0_schema_list_to_no_filter'
        # Narrowed to this test alone, not the whole tests/cultnet.rs binary: that binary carries
        # reactive_document_coalesces_direct_same_schema_alias_member_writes, a pre-existing test
        # unrelated to this campaign that fails under a full-binary run (both parallel and
        # RUST_TEST_THREADS=1) but passes every time in isolation - state pollution from another
        # test in that binary, not a correctness question this entry is about. Filtering M0 and
        # the entry run to this test's own name keeps this suite from depending on that binary's
        # unrelated health; the flake itself is a separate finding, not fixed here.
        Command = 'cargo test --test cultnet serve_read_only_raw_snapshot'
        File   = 'src/snapshot_query.rs'
        Old    = 'if filtered.is_empty() { None } else { Some(filtered) }'
        New    = 'Some(filtered)'
    }
)
