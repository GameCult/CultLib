# Entries for tools/eureka-mutations.ps1 (C:\Users\Meta\.claude\skills\eureka\tools):
# CultNet typed selection, Cut 1 commit 3 - the Rust evaluator
# (src/selection.rs), mirroring the reference's rules at wire parity
# (docs/cultnet-selection-cut.md section 6/7/10). Target: src/selection.rs.
#
# Two commands are in play (the harness runs M0 once per distinct Command in the suite):
# `cargo test --test selection` for the evaluator-level rules (the integration test
# binary in packages/cultnet-rs/tests/selection.rs), and `cargo test --lib` for the
# canonical_number module's own unit tests (its render_*/canonicalize_decimal_digits functions are
# not otherwise exercised by the integration suite - Row::number already returns pre-rendered
# strings there). Run with CARGO_TARGET_DIR set to this worktree's own target directory per the
# campaign's build rules.
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
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if descending { ordering.reverse() } else { ordering }'
        New    = 'let _ = descending; ordering'
    },
    @{
        Id     = 'RS-CursorStale-Revert'
        Rule   = 'A cursor from a moved asOf is refused cursor_stale.'
        Test   = 'pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if cursor.as_of != as_of {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-CursorStale-Loosening'
        Rule   = 'Staleness compares equality, not only "cursor is newer than the current asOf".'
        Test   = 'pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if cursor.as_of != as_of {'
        New    = 'if cursor.as_of > as_of {'
    },
    @{
        Id     = 'RS-CursorDigest-Revert'
        Rule   = 'A cursor whose digest does not match this selection is refused cursor_invalid.'
        Test   = 'pages_exactly_once_and_refuses_a_stale_or_mismatched_cursor'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if cursor.digest != Cursor::compute_digest(selection) {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-S7-Revert'
        Rule   = 'S7: cited { exists } is honoured, not ignored.'
        Test   = 'cited_exists_is_the_one_negation'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'candidates.retain(|row| incoming.contains(row.record_key()) == exists);'
        New    = 'candidates.retain(|row| incoming.contains(row.record_key()) || true);'
    },
    @{
        Id     = 'RS-S7-Loosening'
        Rule   = 'exists:true and exists:false must answer opposite sets, not the same test inverted twice.'
        Test   = 'cited_exists_is_the_one_negation'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'candidates.retain(|row| incoming.contains(row.record_key()) == exists);'
        New    = 'candidates.retain(|row| incoming.contains(row.record_key()) != exists);'
    },
    @{
        Id     = 'RS-S16-Le-Revert'
        Rule   = 'S16: le includes the equal boundary.'
        Test   = 'compares_numbers_at_the_boundary_for_all_four_operators'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'SelectionOperator::Le => ordering != Ordering::Greater,'
        New    = 'SelectionOperator::Le => ordering == Ordering::Less,'
    },
    @{
        Id     = 'RS-S16-Ge-Revert'
        Rule   = 'S16: ge includes the equal boundary.'
        Test   = 'compares_numbers_at_the_boundary_for_all_four_operators'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'SelectionOperator::Ge => ordering != Ordering::Less,'
        New    = 'SelectionOperator::Ge => ordering == Ordering::Greater,'
    },
    @{
        Id     = 'RS-S18-Revert'
        Rule   = "D9/S18: an edge naming a row outside its reference's declared target refuses the selection."
        Test   = 'refuses_an_edge_outside_its_declared_target'
        Command = 'cargo test --test selection'
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
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'Some(number) if !canonical_number::is_canonical(number) => {'
        New    = 'Some(number) if false && !canonical_number::is_canonical(number) => {'
    },
    @{
        Id     = 'RS-QJ-Door-NegativeZero-Revert'
        Rule   = 'Q-J: "-0" is excluded even though the digit grammar alone matches it.'
        Test   = 'validation_refuses_non_canonical_number_spellings'
        Command = 'cargo test --test selection'
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
        Command = 'cargo test --test selection'
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
        Command = 'cargo test --test selection'
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
        # Not killed through render_f64/render_f32: probe_f64_display_never_uses_exponent_notation
        # (this module's own tests) established that Rust's f64/f32 Display never produces exponent
        # notation, so no real render_f64 input reaches the branch this reverts - unlike C#'s
        # ToString(), which does. Killed directly against expand_scientific_notation instead, which
        # pins the algorithm on hand-built exponent strings regardless of whether Display ever
        # supplies one today.
        Id     = 'RS-QJ-ExponentExpansion-Revert'
        Rule   = 'Q-J: a string in exponent notation is rewritten to positional digits.'
        Test   = 'selection::canonical_number::tests::expand_scientific_notation_rewrites_hand_built_exponent_forms'
        Command = 'cargo test --lib'
        File   = 'src/selection.rs'
        Old    = "if value.find(['e', 'E']).is_none() {
            return value.to_string();
        }"
        New    = 'return value.to_string();'
    },
    @{
        Id     = 'RS-S2-Revert'
        Rule   = 'S2: an any_of predicate matches row-value membership, not "any value at all".'
        Test   = 'conjoins_any_of_and_comparison_predicates'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if !values.iter().any(|v| wanted.iter().any(|w| w == v)) {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-S6-Revert'
        Rule   = 'S6: cites.role, when present, filters which declared reference the hop follows.'
        Test   = 'cites_role_excludes_a_second_reference_at_the_same_target'
        Command = 'cargo test --test selection'
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
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if matches!(&selection.schemas, Some(schemas) if schemas.is_empty()) {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-EmptyKeys-Revert'
        Rule   = 'S1: an empty (but present) keys list is refused, never answered as an empty page.'
        Test   = 'validation_refuses_undeclared_index_role_and_empty_lists'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if matches!(&selection.keys, Some(keys) if keys.is_empty()) {'
        New    = 'if false {'
    },
    # Self's ruling, 2026-09-22 (docs/cultnet-selection-cut.md, commit 2 fix batch).
    @{
        Id     = 'RS-BlankSchema-Revert'
        Rule   = 'The door refuses a blank entry inside a present schemas list, not only an empty list.'
        Test   = 'validation_refuses_a_blank_entry_in_schemas_or_keys'
        Command = 'cargo test --test selection'
        File   = 'src/selection.rs'
        Old    = 'if schema.trim().is_empty() {'
        New    = 'if false {'
    },
    @{
        Id     = 'RS-BlankKey-Revert'
        Rule   = 'The door refuses a blank entry inside a present keys list.'
        Test   = 'validation_refuses_a_blank_entry_in_schemas_or_keys'
        Command = 'cargo test --test selection'
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
