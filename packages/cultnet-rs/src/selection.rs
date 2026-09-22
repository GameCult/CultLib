//! CultNet typed selection (docs/cultnet-selection-cut.md, sections 2/6/7): the Rust spelling of
//! the same vocabulary the C# reference (`GameCult.Networking/CultNetSelection.cs`,
//! `CultNetSelectionEvaluator.cs`) owns, at wire parity, pinned by the S12 parity vectors rather than
//! by matching shapes 1:1.
//!
//! D7: this evaluator carries no `cultcache-rs` knowledge. A row's declared values and edges come
//! entirely through the [`Row`] and [`RowSet`] traits; the consumer (Huginn, or a test fixture) is the
//! row owner and supplies them the way `CultCache` supplies the C# evaluator. Nothing here reflects
//! over a document, and no type here is a serde union that skips its own tag (section 10's
//! negative grep checks for the literal attribute spelling, which this file deliberately does not
//! quote even in prose).
//!
//! Q-J (section 2 "Numbers"): every comparison number on the wire and every row's declared numeric
//! value is a canonical decimal string, never a float. [`canonical_number`] is the door's grammar and
//! the comparator; it is a pure string algorithm with no `f64`/`f32` anywhere on the comparison path.

use std::cmp::Ordering;
use std::collections::{HashMap, HashSet};
use std::fmt;

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

// ---------------------------------------------------------------------------------------------
// Canonical decimal numbers (Q-J)
// ---------------------------------------------------------------------------------------------

/// The wire's canonical decimal grammar and comparator (docs/cultnet-selection-cut.md section 2
/// "Numbers", operator ruling Q-J). A pure string algorithm - no numeric parse, no precision limit,
/// no `f64`/`f32` on the comparison path. Ported from `CultNetCanonicalNumber`
/// (`GameCult.Networking/CultNetSelection.cs`); the digit grammar is hand-rolled rather than pulled
/// in via a `regex` dependency this crate does not otherwise need.
pub mod canonical_number {
    use super::Ordering;

    /// `true` for a canonical decimal string: matches `^-?(0|[1-9][0-9]*)(\.[0-9]*[1-9])?$` and is
    /// not `"-0"` (excluded explicitly; the grammar alone matches it). Forbids leading zeros, a
    /// trailing fractional zero or bare point, an explicit `+`, exponent notation, and whitespace.
    pub fn is_canonical(value: &str) -> bool {
        if value == "-0" {
            return false;
        }
        let bytes = value.as_bytes();
        if bytes.is_empty() {
            return false;
        }
        let mut i = 0usize;
        if bytes[0] == b'-' {
            i += 1;
        }
        if i >= bytes.len() || !bytes[i].is_ascii_digit() {
            return false;
        }
        if bytes[i] == b'0' {
            i += 1;
            // A leading zero is canonical only as the whole integer part: "0" or "0.x", never "01".
            if i < bytes.len() && bytes[i].is_ascii_digit() {
                return false;
            }
        } else {
            i += 1;
            while i < bytes.len() && bytes[i].is_ascii_digit() {
                i += 1;
            }
        }
        if i == bytes.len() {
            return true;
        }
        if bytes[i] != b'.' {
            return false;
        }
        i += 1;
        let fraction_start = i;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            i += 1;
        }
        if i != bytes.len() {
            return false;
        }
        let fraction_len = i - fraction_start;
        // (\.[0-9]*[1-9])?: at least one fraction digit, and the last one is never zero.
        fraction_len > 0 && bytes[i - 1] != b'0'
    }

    /// Compares two canonical decimal strings by sign, then the integer part's length, then its
    /// digits, then the fraction digits padded on the right to equal length. Callers validate both
    /// operands with [`is_canonical`] first; this does not re-validate. Mirrors
    /// `CultNetCanonicalNumber.Compare` exactly (docs/cultnet-selection-cut.md section 2 "Numbers").
    pub fn compare(left: &str, right: &str) -> Ordering {
        let (negative_left, integer_left, fraction_left) = decompose(left);
        let (negative_right, integer_right, fraction_right) = decompose(right);

        if negative_left != negative_right {
            return if negative_left {
                Ordering::Less
            } else {
                Ordering::Greater
            };
        }

        let magnitude = if integer_left.len() != integer_right.len() {
            integer_left.len().cmp(&integer_right.len())
        } else {
            let integer_cmp = integer_left.cmp(integer_right);
            if integer_cmp != Ordering::Equal {
                integer_cmp
            } else {
                let width = fraction_left.len().max(fraction_right.len());
                pad_right(fraction_left, width).cmp(&pad_right(fraction_right, width))
            }
        };

        if negative_left {
            magnitude.reverse()
        } else {
            magnitude
        }
    }

    fn decompose(value: &str) -> (bool, &str, &str) {
        let negative = value.starts_with('-');
        let unsigned = if negative { &value[1..] } else { value };
        match unsigned.find('.') {
            Some(dot) => (negative, &unsigned[..dot], &unsigned[dot + 1..]),
            None => (negative, unsigned, ""),
        }
    }

    fn pad_right(value: &str, width: usize) -> String {
        let mut owned = value.to_string();
        while owned.len() < width {
            owned.push('0');
        }
        owned
    }

    /// The one canonicalizer every rendered number passes through: strips leading integer zeros and
    /// trailing fractional zeros, and collapses a negative value that reduces to zero into `"0"`.
    /// Mirrors `CultCache.cs`'s `CanonicalizeDecimalDigits`. Exposed so a `Row` implementation can
    /// render its own numeric values into this grammar without re-deriving the algorithm.
    pub fn canonicalize_decimal_digits(signed_positional: &str) -> String {
        let negative = signed_positional.starts_with('-');
        let unsigned = if negative {
            &signed_positional[1..]
        } else {
            signed_positional
        };
        let (mut integer_part, fraction_part) = match unsigned.find('.') {
            Some(dot) => (&unsigned[..dot], &unsigned[dot + 1..]),
            None => (unsigned, ""),
        };
        integer_part = integer_part.trim_start_matches('0');
        if integer_part.is_empty() {
            integer_part = "0";
        }
        let fraction_part = fraction_part.trim_end_matches('0');

        let is_zero = integer_part == "0" && fraction_part.is_empty();
        let result = if fraction_part.is_empty() {
            integer_part.to_string()
        } else {
            format!("{integer_part}.{fraction_part}")
        };
        if negative && !is_zero {
            format!("-{result}")
        } else {
            result
        }
    }

    /// Renders a signed 64-bit integer as its exact canonical decimal - no 2^53 limit, because this
    /// never passes through a float.
    pub fn render_i64(value: i64) -> String {
        canonicalize_decimal_digits(&value.to_string())
    }

    /// Renders an unsigned 64-bit integer as its exact canonical decimal.
    pub fn render_u64(value: u64) -> String {
        canonicalize_decimal_digits(&value.to_string())
    }

    // R-D (docs/cultnet-selection-cut.md, "Self's rulings for the Cut 1 fix batch"): a float
    // renders as its exact decimal expansion, computed from mantissa and exponent, not as a
    // shortest round-trip form (the previous `f64::to_string()` path rounded ties the wrong way
    // relative to .NET - Soul's parity vectors). Every finite f32/f64 is an exact dyadic rational
    // `mantissa * 2^exponent`; this is a small fixed-point bignum (decimal digits, little-endian)
    // that computes that expansion exactly, with no precision loss and no new dependency.

    /// One decimal digit per element, least-significant first.
    type BigDigits = Vec<u8>;

    fn big_from_u64(mut value: u64) -> BigDigits {
        if value == 0 {
            return vec![0];
        }
        let mut digits = Vec::new();
        while value > 0 {
            digits.push((value % 10) as u8);
            value /= 10;
        }
        digits
    }

    /// Multiplies `digits` in place by a one-digit factor (2 or 5, here - the two primes 2^k and
    /// 5^k need to turn a binary mantissa into an exact decimal one).
    /// Multiplies `digits` in place by a single-digit `factor` (only ever called with 2 or 5, the
    /// two primes that turn a binary mantissa's magnitude into a decimal one). A single-digit
    /// factor against a single digit plus a carry that is itself always a single digit
    /// (inductively: it starts at 0) never produces a carry of 10 or more, so the leftover carry
    /// needs at most one new leading digit - never a loop (the mutation harness's own
    /// RS-D-BigMulCarry-Loosening entry proved a `while` here is unreachable generality: nothing
    /// distinguishes it from a plain `if`).
    fn big_mul_small(digits: &mut BigDigits, factor: u8) {
        debug_assert!(factor <= 9, "big_mul_small takes a single decimal digit as its factor");
        let mut carry: u32 = 0;
        for digit in digits.iter_mut() {
            let product = (*digit as u32) * (factor as u32) + carry;
            *digit = (product % 10) as u8;
            carry = product / 10;
        }
        if carry > 0 {
            digits.push(carry as u8);
        }
    }

    fn big_to_most_significant_first(digits: &BigDigits) -> String {
        digits.iter().rev().map(|d| (b'0' + d) as char).collect()
    }

    /// The exact decimal expansion of `(-1)^negative * mantissa * 2^exponent`. `mantissa` carries
    /// no implicit bit; the caller has already folded it in (or not, for a subnormal).
    fn exact_decimal_from_mantissa(mantissa: u64, exponent: i32, negative: bool) -> String {
        // No mantissa == 0 short-circuit: the general path already collapses to "0" for a zero
        // mantissa (multiplying zero stays zero at every step, and `canonicalize_decimal_digits`
        // collapses the signed "-0" case too) - a dedicated early return here duplicated that
        // without changing the answer, and the mutation harness's own RS-D-ZeroMantissa-Revert
        // entry proved it: removing the guard entirely left the test green.
        let mut digits = big_from_u64(mantissa);
        let unsigned = if exponent >= 0 {
            for _ in 0..exponent {
                big_mul_small(&mut digits, 2);
            }
            big_to_most_significant_first(&digits)
        } else {
            // mantissa / 2^k == (mantissa * 5^k) / 10^k: multiplying by 5^k turns the binary
            // fraction into a decimal one with exactly k fraction digits.
            let k = (-exponent) as usize;
            for _ in 0..k {
                big_mul_small(&mut digits, 5);
            }
            let mut rendered = big_to_most_significant_first(&digits);
            if rendered.len() <= k {
                rendered = format!("{}{rendered}", "0".repeat(k + 1 - rendered.len()));
            }
            let split = rendered.len() - k;
            format!("{}.{}", &rendered[..split], &rendered[split..])
        };
        canonicalize_decimal_digits(&if negative {
            format!("-{unsigned}")
        } else {
            unsigned
        })
    }

    /// Renders a `f64` as its canonical decimal, or `None` for NaN/infinity ("a NaN or infinite
    /// member matches no comparison"). Built from IEEE-754's own mantissa and exponent (R-D) -
    /// never from `Display`'s shortest round-trip form, which rounds ties differently than .NET's
    /// `ToString()` (Soul's parity vectors, 394/200k f32 and 48/200k f64 values at ties).
    pub fn render_f64(value: f64) -> Option<String> {
        if value.is_nan() || value.is_infinite() {
            return None;
        }
        let bits = value.to_bits();
        let negative = (bits >> 63) & 1 == 1;
        let biased_exponent = ((bits >> 52) & 0x7FF) as i32;
        let fraction = bits & ((1u64 << 52) - 1);
        let (mantissa, exponent) = if biased_exponent == 0 {
            (fraction, -1074) // subnormal: 1 - 1023 (bias) - 52 (fraction bits)
        } else {
            (fraction | (1u64 << 52), biased_exponent - 1023 - 52)
        };
        Some(exact_decimal_from_mantissa(mantissa, exponent, negative))
    }

    /// Renders a `f32` as its canonical decimal, or `None` for NaN/infinity.
    pub fn render_f32(value: f32) -> Option<String> {
        if value.is_nan() || value.is_infinite() {
            return None;
        }
        let bits = value.to_bits();
        let negative = (bits >> 31) & 1 == 1;
        let biased_exponent = ((bits >> 23) & 0xFF) as i32;
        let fraction = (bits & ((1u32 << 23) - 1)) as u64;
        let (mantissa, exponent) = if biased_exponent == 0 {
            (fraction, -149) // subnormal: 1 - 127 (bias) - 23 (fraction bits)
        } else {
            (fraction | (1u64 << 23), biased_exponent - 127 - 23)
        };
        Some(exact_decimal_from_mantissa(mantissa, exponent, negative))
    }

    #[cfg(test)]
    mod tests {
        // R-D no longer renders through `Display` at all (render_f64/render_f32 build the exact
        // decimal straight from mantissa and exponent), so this probe no longer guards a
        // production path - it is kept anyway, on the map's instruction, as a standing record of
        // what Rust's `Display` does for a value this module used to hand it.
        #[test]
        fn probe_f64_display_never_uses_exponent_notation() {
            let huge = 1e21_f64;
            let tiny = 1e-7_f64;
            let huge_text = huge.to_string();
            let tiny_text = tiny.to_string();
            assert!(
                !huge_text.contains(['e', 'E']),
                "expected no exponent notation, got {huge_text}"
            );
            assert!(
                !tiny_text.contains(['e', 'E']),
                "expected no exponent notation, got {tiny_text}"
            );
            assert_eq!(huge_text, "1000000000000000000000");
            assert_eq!(tiny_text, "0.0000001");
        }

        // Q-J: an i64 past 2^53 (where f64 loses integer precision) renders exactly - this is the
        // row-rendering mutant docs/cultnet-selection-cut.md section 2 "Numbers" names: a value
        // rendered through float64 would silently become "9007199254740992" here.
        #[test]
        fn render_i64_past_2_pow_53_is_exact() {
            assert_eq!(super::render_i64(9_007_199_254_740_993), "9007199254740993");
            assert_eq!(super::render_i64(-9_007_199_254_740_993), "-9007199254740993");
            assert_eq!(super::render_i64(0), "0");
        }

        #[test]
        fn render_u64_is_exact() {
            assert_eq!(super::render_u64(18_446_744_073_709_551_615), "18446744073709551615");
        }

        #[test]
        fn canonicalize_decimal_digits_strips_leading_and_trailing_zeros() {
            assert_eq!(super::canonicalize_decimal_digits("13.500"), "13.5");
            assert_eq!(super::canonicalize_decimal_digits("007"), "7");
            assert_eq!(super::canonicalize_decimal_digits("0.000"), "0");
            assert_eq!(super::canonicalize_decimal_digits("-0.000"), "0");
        }

        #[test]
        fn render_f64_rewrites_exponent_notation_to_positional_digits() {
            // 1e21 and 3.5 are both exact dyadic rationals, so their exact expansion is also
            // their shortest form.
            assert_eq!(super::render_f64(1e21).unwrap(), "1000000000000000000000");
            assert_eq!(super::render_f64(3.5).unwrap(), "3.5");
            // 1e-7 is not exactly representable in binary - R-D renders the value actually
            // stored, not the shortest decimal that would round-trip back to it (verified
            // independently outside this crate: 1e-7_f64.to_bits() decomposes to mantissa
            // 5764607523034235 * 2^-106, whose exact decimal is this 79-digit expansion).
            assert_eq!(
                super::render_f64(1e-7).unwrap(),
                "0.0000000999999999999999954748111825886258685613938723690807819366455078125"
            );
        }

        #[test]
        fn render_f64_and_f32_answer_none_for_nan_and_infinity() {
            assert_eq!(super::render_f64(f64::NAN), None);
            assert_eq!(super::render_f64(f64::INFINITY), None);
            assert_eq!(super::render_f32(f32::NAN), None);
            assert_eq!(super::render_f32(f32::NEG_INFINITY), None);
        }

        // R-D: an f32 of 3e20 is not "3e20" or "300000000000000000000", it is the exact IEEE-754
        // value nearest 3e20. The map's own worked example (and Soul's notes) give this as
        // "300000002010536247296" - that literal is wrong: 3e20_f32.to_bits() is
        // 0b01100001100000100001101010110001 (sign 0, exponent 195, fraction 137905), so mantissa
        // 8526513 * 2^45 - verified independently in Node with BigInt bit-shifting outside this
        // crate - is 300000006012263202816, which is what this algorithm (and a plain `mantissa
        // << exponent`) computes. Flagged as a discrepancy in the fix-batch report; this test
        // pins the value this algorithm can prove, not the map's transcription.
        #[test]
        fn render_f32_3e20_is_the_exact_ieee754_value_not_the_shortest_form() {
            assert_eq!(super::render_f32(3e20_f32).unwrap(), "300000006012263202816");
        }

        // R-D: 0.1 has no exact binary representation, so its f32/f64 renderings are long and
        // differ from each other - a shortest-form renderer would print "0.1" for both.
        #[test]
        fn render_f32_and_f64_of_0_1_render_the_distinct_exact_binary_values() {
            let as_f32 = super::render_f32(0.1_f32).unwrap();
            let as_f64 = super::render_f64(0.1_f64).unwrap();
            assert_ne!(as_f32, as_f64, "f32 and f64 round 0.1 to different exact values");
            // Round-trip through Rust's own correctly-rounded parser: the exact decimal expansion
            // must parse back to the identical bit pattern it was rendered from.
            assert_eq!(as_f32.parse::<f32>().unwrap().to_bits(), 0.1_f32.to_bits());
            assert_eq!(as_f64.parse::<f64>().unwrap().to_bits(), 0.1_f64.to_bits());
            assert_ne!(as_f32, "0.1", "the exact expansion is far longer than the shortest form");
        }

        // R-D: subnormals (the smallest representable magnitudes, below the normal range) still
        // render exactly, and round-trip through a correctly-rounded parser.
        #[test]
        fn render_f32_and_f64_subnormals_round_trip() {
            let smallest_f32 = f32::from_bits(1); // smallest positive subnormal f32
            let smallest_f64 = f64::from_bits(1); // smallest positive subnormal f64
            let rendered_f32 = super::render_f32(smallest_f32).unwrap();
            let rendered_f64 = super::render_f64(smallest_f64).unwrap();
            assert_eq!(rendered_f32.parse::<f32>().unwrap().to_bits(), smallest_f32.to_bits());
            assert_eq!(rendered_f64.parse::<f64>().unwrap().to_bits(), smallest_f64.to_bits());
            assert!(!rendered_f32.contains(['e', 'E']));
            assert!(!rendered_f64.contains(['e', 'E']));
        }

        // R-D: the largest finite magnitudes round-trip too - these are the widest bignums this
        // algorithm produces (f64::MAX has 309 integer digits).
        #[test]
        fn render_f32_and_f64_max_round_trip() {
            let rendered_f32 = super::render_f32(f32::MAX).unwrap();
            let rendered_f64 = super::render_f64(f64::MAX).unwrap();
            assert_eq!(rendered_f32.parse::<f32>().unwrap().to_bits(), f32::MAX.to_bits());
            assert_eq!(rendered_f64.parse::<f64>().unwrap().to_bits(), f64::MAX.to_bits());
            assert!(!rendered_f32.starts_with('-'));
            assert!(!rendered_f64.starts_with('-'));
        }

        // R-D: positive and negative zero both render as the canonical "0", never "-0".
        #[test]
        fn render_f32_and_f64_positive_and_negative_zero_render_as_canonical_zero() {
            assert_eq!(super::render_f32(0.0_f32).unwrap(), "0");
            assert_eq!(super::render_f32(-0.0_f32).unwrap(), "0");
            assert_eq!(super::render_f64(0.0_f64).unwrap(), "0");
            assert_eq!(super::render_f64(-0.0_f64).unwrap(), "0");
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Schema-alias matching (R-E)
// ---------------------------------------------------------------------------------------------

/// The one schema-identity rule (R-E), ported from the C# reference's
/// `CultNetSchemaAliasMatching.Matches(string, string)` / `MatchesAny`. `selection.schemas` and
/// `cites.target.schemaId` both go through this - D7: Rust carries no `CultDocumentDescriptor`
/// knowledge, so (unlike the C# reference's richer descriptor-aware overload) this only ever
/// compares two schema-id strings, exactly or by their inferred name (the part before a trailing
/// `.v<digits>`).
pub mod schema_alias {
    /// `true` when `candidate` names `schema_id` exactly, or the two share an inferred name.
    /// Mirrors `CultNetSchemaAliasMatching.Matches(string candidate, string schemaId)`.
    pub fn matches(candidate: &str, schema_id: &str) -> bool {
        if candidate == schema_id {
            return true;
        }
        let candidate_name = infer_schema_name(candidate).unwrap_or(candidate);
        let schema_name = infer_schema_name(schema_id).unwrap_or(schema_id);
        candidate_name == schema_name
    }

    /// `true` when `candidates` is empty (no filter) or any candidate matches `schema_id`.
    /// Mirrors `CultNetSchemaAliasMatching.MatchesAny(IReadOnlyList<string>, string)`.
    pub fn matches_any(candidates: &[String], schema_id: &str) -> bool {
        candidates.is_empty() || candidates.iter().any(|candidate| matches(candidate, schema_id))
    }

    /// The part of `schema_id` before a trailing `.v<digits>` marker, or `None` when `schema_id`
    /// carries no such marker (real hashed ids, e.g. `"sha256:<hex>"`, carry none on their own -
    /// aliasing only bridges two ids that share the same unversioned name). Mirrors
    /// `CultNetSchemaAliasMatching.InferSchemaName`.
    fn infer_schema_name(schema_id: &str) -> Option<&str> {
        let marker = schema_id.rfind(".v")?;
        if marker == 0 {
            return None;
        }
        let version = &schema_id[marker + 2..];
        if version.is_empty() || !version.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        Some(&schema_id[..marker])
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn matches_exact_ids() {
            assert!(matches("sha256:abc", "sha256:abc"));
            assert!(!matches("sha256:abc", "sha256:def"));
        }

        #[test]
        fn matches_by_inferred_name_across_versions() {
            assert!(matches("sha256:abc.v1", "sha256:abc.v2"));
            assert!(matches("sha256:abc", "sha256:abc.v1"));
            assert!(matches("sha256:abc.v1", "sha256:abc"));
        }

        #[test]
        fn does_not_match_a_real_hashed_id_with_no_shared_name() {
            assert!(!matches("sha256:abc", "sha256:xyz"));
        }

        #[test]
        fn matches_any_empty_candidates_matches_everything() {
            assert!(matches_any(&[], "sha256:anything"));
        }

        #[test]
        fn infer_schema_name_refuses_a_leading_marker_and_a_non_numeric_or_empty_version() {
            assert_eq!(infer_schema_name(".v1"), None);
            assert_eq!(infer_schema_name("name.vX"), None);
            assert_eq!(infer_schema_name("name.v"), None);
            assert_eq!(infer_schema_name("name.v1"), Some("name"));
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Wire types (section 2)
// ---------------------------------------------------------------------------------------------

/// The four comparison and one any-of operator a field predicate may carry. `FieldPredicate::op` is
/// the wire's raw string (never a serde-tagged union); this enum exists for evaluator ergonomics
/// only, mirroring `CultNetSelectionOperator`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SelectionOperator {
    AnyOf,
    Lt,
    Le,
    Ge,
    Gt,
}

impl SelectionOperator {
    /// Parses a wire operator string; `None` for anything else.
    pub fn parse(wire: &str) -> Option<Self> {
        match wire {
            "any_of" => Some(Self::AnyOf),
            "lt" => Some(Self::Lt),
            "le" => Some(Self::Le),
            "ge" => Some(Self::Ge),
            "gt" => Some(Self::Gt),
            _ => None,
        }
    }

    /// The wire spelling of this operator.
    pub fn as_wire_str(self) -> &'static str {
        match self {
            Self::AnyOf => "any_of",
            Self::Lt => "lt",
            Self::Le => "le",
            Self::Ge => "ge",
            Self::Gt => "gt",
        }
    }

    /// `true` for the four comparison operators (everything but `any_of`).
    pub fn is_comparison(self) -> bool {
        !matches!(self, Self::AnyOf)
    }
}

/// `"header"` under [`Selection::projection`].
pub const PROJECTION_HEADER: &str = "header";
/// `"document"` under [`Selection::projection`].
pub const PROJECTION_DOCUMENT: &str = "document";

fn default_projection() -> String {
    PROJECTION_HEADER.to_string()
}

/// Identifies one row on the wire by schema and record key.
#[derive(Clone, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordRef {
    pub schema_id: String,
    pub record_key: String,
}

impl RecordRef {
    pub fn new(schema_id: impl Into<String>, record_key: impl Into<String>) -> Self {
        Self {
            schema_id: schema_id.into(),
            record_key: record_key.into(),
        }
    }
}

/// One predicate over a single declared index alias (section 2).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FieldPredicate {
    /// A declared index alias, reachable on some schema the selection can reach.
    pub index: String,
    /// `"any_of"`, `"lt"`, `"le"`, `"ge"`, or `"gt"`.
    pub op: String,
    /// Present iff `op = any_of`; non-empty.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub values: Option<Vec<String>>,
    /// Present iff `op` is a comparison; a canonical decimal string (Q-J).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub number: Option<String>,
}

/// Selects rows whose declared reference names a target row.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Citation {
    pub target: RecordRef,
    /// The reference's declared alias; absent matches any declared reference.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub role: Option<String>,
}

/// Selects rows some other row does or does not name in `role` - the one negation.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Incoming {
    pub role: String,
    pub exists: bool,
}

/// One typed selection, carried by `cultnet.snapshot_request.v1` and
/// `cultnet.database_subscribe.v1` (section 2).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Selection {
    /// Kinds: schema ids. Absent = every schema. (No alias matching in this runtime - D7: Rust's
    /// row owner has no schema-alias registry; a caller wanting an alias expands it before calling.)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schemas: Option<Vec<String>>,
    /// Record-key allowlist. Absent = every key.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub keys: Option<Vec<String>>,
    /// Conjunction; each is any-of over one declared index alias.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fields: Option<Vec<FieldPredicate>>,
    /// Rows whose declared reference names `target`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cites: Option<Citation>,
    /// Rows some row does or does not name in `role`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cited: Option<Incoming>,
    /// `"header"` or `"document"`; default `"header"`.
    #[serde(default = "default_projection")]
    pub projection: String,
    /// Reverses the one order.
    #[serde(default)]
    pub descending: bool,
    /// Clamped 1..=200.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<u32>,
    /// Opaque; minted by the answering server.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cursor: Option<String>,
}

impl Default for Selection {
    fn default() -> Self {
        Self {
            schemas: None,
            keys: None,
            fields: None,
            cites: None,
            cited: None,
            projection: default_projection(),
            descending: false,
            limit: None,
            cursor: None,
        }
    }
}

impl Selection {
    /// `true` when this selection follows an edge (section 2: "Edges in the answer").
    pub fn has_hop(&self) -> bool {
        self.cites.is_some() || self.cited.is_some()
    }
}

/// One edge a hop traversed, carried on a selection page.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Edge {
    /// The citing row.
    pub from: RecordRef,
    /// The declared reference alias the citer used.
    pub role: String,
    /// The cited row, its schema resolved to the leaf actually stored.
    pub to: RecordRef,
    /// `"messagepack"` when `payload` is present.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub payload_encoding: Option<String>,
    /// The value attached to the edge (a dictionary reference's value); present under `document`
    /// projection only.
    #[serde(default, skip_serializing_if = "Option::is_none", with = "optional_bytes")]
    pub payload: Option<Vec<u8>>,
}

/// `serde_bytes` has no `Option<Vec<u8>>` submodule; this is the same MessagePack `bin`/`nil`
/// encoding, spelled for the optional case.
mod optional_bytes {
    use serde::{Deserialize, Deserializer, Serializer};
    use serde_bytes::{ByteBuf, Bytes};

    pub fn serialize<S: Serializer>(value: &Option<Vec<u8>>, serializer: S) -> Result<S::Ok, S::Error> {
        match value {
            Some(bytes) => serializer.serialize_some(Bytes::new(bytes)),
            None => serializer.serialize_none(),
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Option<Vec<u8>>, D::Error> {
        Ok(Option::<ByteBuf>::deserialize(deserializer)?.map(ByteBuf::into_vec))
    }
}

/// A header record: [`SelectionDocumentRecord`] minus its payload and payload encoding.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RawDocumentHeader {
    pub schema_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_content_hash: Option<String>,
    pub record_key: String,
    pub stored_at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_runtime_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_agent_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_role: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tags: Option<Vec<String>>,
}

/// The full record `cultnet.snapshot_response_raw.v1` carries under `document` projection. Distinct
/// from `contracts::CultNetRawDocumentRecord` (the v0 raw record), which lacks `schemaName`/
/// `schemaVersion`/`schemaContentHash` - a pre-existing gap in the v0 Rust struct relative to the C#
/// reference's `CultNetRawDocumentRecord` that this cut does not touch (out of scope: v0's wire is
/// unchanged). v1 reuses the reference's richer, already-existing C# type, so this Rust type matches
/// that one, not v0's.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SelectionDocumentRecord {
    pub schema_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_content_hash: Option<String>,
    pub record_key: String,
    pub stored_at: String,
    #[serde(default = "messagepack_encoding")]
    pub payload_encoding: String,
    #[serde(with = "serde_bytes")]
    pub payload: Vec<u8>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_runtime_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_agent_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_role: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tags: Option<Vec<String>>,
}

fn messagepack_encoding() -> String {
    "messagepack".to_string()
}

// ---------------------------------------------------------------------------------------------
// Refusals (section 2/3, typed at the door)
// ---------------------------------------------------------------------------------------------

/// Refused typed at the door: a name, list, or comparison the declared shape cannot support.
/// Mirrors `CultNetSelectionInvalidException`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SelectionInvalid {
    /// The selection field that failed (`"schemas"`, `"keys"`, `"fields[i].index"`, ...).
    pub field: String,
    /// The offending value, when there is one string worth naming.
    pub value: Option<String>,
    pub message: String,
}

impl SelectionInvalid {
    fn new(field: impl Into<String>, value: Option<String>, message: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            value,
            message: message.into(),
        }
    }
}

impl fmt::Display for SelectionInvalid {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "selection_invalid[{}]: {}", self.field, self.message)
    }
}

impl std::error::Error for SelectionInvalid {}

/// Every way `select`/`matches` can refuse rather than answer (section 2/3/8).
#[derive(Clone, Debug, PartialEq)]
pub enum SelectionRefusal {
    Invalid(SelectionInvalid),
    /// The cursor's `asOf` does not match the snapshot being answered.
    CursorStale { as_of: u64, current: u64 },
    /// The cursor does not decode, or its digest is not this selection's.
    CursorInvalid { message: String },
    /// A stored edge names a row whose schema is outside the reference's declared target (D9/S18).
    ReferenceOutsideTarget {
        from_schema_id: String,
        from_key: String,
        role: String,
        to_schema_id: String,
        to_key: String,
    },
}

impl fmt::Display for SelectionRefusal {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Invalid(inner) => write!(f, "{inner}"),
            Self::CursorStale { as_of, current } => write!(
                f,
                "cursor_stale: the cursor was minted at asOf {as_of}; this server answers as of {current}."
            ),
            Self::CursorInvalid { message } => write!(f, "cursor_invalid: {message}"),
            Self::ReferenceOutsideTarget {
                from_schema_id,
                from_key,
                role,
                to_schema_id,
                to_key,
            } => write!(
                f,
                "reference_outside_target: {from_schema_id}/{from_key} names {to_schema_id}/{to_key} through role \"{role}\", which is outside the reference's declared target."
            ),
        }
    }
}

impl std::error::Error for SelectionRefusal {}

impl From<SelectionInvalid> for SelectionRefusal {
    fn from(value: SelectionInvalid) -> Self {
        Self::Invalid(value)
    }
}

// ---------------------------------------------------------------------------------------------
// Row / RowSet (D7): the consumer's declarations and values, reflected over nothing
// ---------------------------------------------------------------------------------------------

/// One row available to the evaluator: what the row owner already knows about it. Implemented by
/// the consumer (Huginn, or a test fixture) - the Rust spelling of what `CultCache`'s public read
/// surface supplies the C# evaluator.
pub trait Row {
    fn schema_id(&self) -> &str;
    fn record_key(&self) -> &str;
    /// The sequence of the commit that last wrote this row. The row owner supplies it; the
    /// evaluator never reads a clock.
    fn ordinal(&self) -> i64;
    /// Every declared value at `index` on this row (usually zero or one; `any_of` matches
    /// membership, matching the cache's own string-valued indexes).
    fn values(&self, index: &str) -> Vec<String>;
    /// This row's declared numeric value at `index`, as its exact canonical decimal (Q-J) - never
    /// `f64`. `None` when the row has no value, or the value is NaN/infinite.
    fn number(&self, index: &str) -> Option<String>;
    /// Every declared reference edge this row carries: `(role, target, payload)`. A one-reference
    /// yields one entry, a many-reference one per element, a dictionary reference one per key with
    /// its value as the payload (D11).
    fn references(&self) -> Vec<(String, RecordRef, Option<Vec<u8>>)>;
}

impl<T: Row + ?Sized> Row for &T {
    fn schema_id(&self) -> &str {
        (**self).schema_id()
    }
    fn record_key(&self) -> &str {
        (**self).record_key()
    }
    fn ordinal(&self) -> i64 {
        (**self).ordinal()
    }
    fn values(&self, index: &str) -> Vec<String> {
        (**self).values(index)
    }
    fn number(&self, index: &str) -> Option<String> {
        (**self).number(index)
    }
    fn references(&self) -> Vec<(String, RecordRef, Option<Vec<u8>>)> {
        (**self).references()
    }
}

/// The consumer's schema-level declarations: what `select`/`validate` need to know about the shape
/// of the rows, independent of any one row's values.
pub trait RowSet {
    /// Every schema id this row set knows about - the universe `selection.schemas` narrows.
    fn all_schema_ids(&self) -> Vec<String>;
    /// The declared index aliases a schema carries (inherited or own).
    fn declared_indexes(&self, schema_id: &str) -> Vec<String>;
    /// The declared reference roles a schema carries.
    fn declared_roles(&self, schema_id: &str) -> Vec<String>;
    /// `true` when `index` is declared numeric on `schema_id`.
    fn is_numeric(&self, schema_id: &str, index: &str) -> bool;
    /// The schema ids a reference named `role` may target. An empty result means the role carries
    /// no restriction the hop must enforce (mirrors C#'s `TargetType == null` skipping the check) -
    /// a `RowSet` with a real, closed target set returns it non-empty.
    fn target_leaves(&self, role: &str) -> Vec<String>;
}

// ---------------------------------------------------------------------------------------------
// Validation (S1, the door)
// ---------------------------------------------------------------------------------------------

/// Validates a [`Selection`] against the declared shape of the schemas it can reach - the door
/// refusals of section 2/3 (S1). Mirrors `CultNetSelectionValidation.Validate`.
pub fn validate(selection: &Selection, row_set: &impl RowSet) -> Result<(), SelectionInvalid> {
    if matches!(&selection.schemas, Some(schemas) if schemas.is_empty()) {
        return Err(SelectionInvalid::new(
            "schemas",
            None,
            "selection.schemas is present and empty; omit it to reach every schema.",
        ));
    }
    if matches!(&selection.keys, Some(keys) if keys.is_empty()) {
        return Err(SelectionInvalid::new(
            "keys",
            None,
            "selection.keys is present and empty; omit it to reach every key.",
        ));
    }
    // Self's ruling, 2026-09-22 (docs/cultnet-selection-cut.md, commit 2 fix batch): section 2
    // already said an empty list is refused at the door; the door must refuse a blank entry
    // inside a present list too, so the evaluator never has to decide what "" or "  " means.
    // `null` (Rust: `None`) means no filter, everywhere - an empty-or-blank list is never
    // silently treated as "no filter" by the evaluator (that was CultNetSelectionEvaluator.cs's
    // pre-fix bug, section 2's S2-3 finding, and this Rust evaluator never had it).
    // Field name matches the C# reference exactly (568e8e3): plain "schemas"/"keys", not an
    // indexed "schemas[i]" - CultNetSelectionInvalidException("schemas", schema, ...) names the
    // list, not the position, and the parity vectors compare this string.
    if let Some(schemas) = &selection.schemas {
        for schema in schemas {
            if schema.trim().is_empty() {
                return Err(SelectionInvalid::new(
                    "schemas",
                    Some(schema.clone()),
                    "selection.schemas carries an empty or whitespace entry.",
                ));
            }
        }
    }
    if let Some(keys) = &selection.keys {
        for key in keys {
            if key.trim().is_empty() {
                return Err(SelectionInvalid::new(
                    "keys",
                    Some(key.clone()),
                    "selection.keys carries an empty or whitespace entry.",
                ));
            }
        }
    }
    if selection.projection != PROJECTION_HEADER && selection.projection != PROJECTION_DOCUMENT {
        return Err(SelectionInvalid::new(
            "projection",
            Some(selection.projection.clone()),
            format!(
                "selection.projection {:?} is neither \"header\" nor \"document\".",
                selection.projection
            ),
        ));
    }

    let reachable = reachable_schemas(selection, row_set);

    if let Some(fields) = &selection.fields {
        for (index, field) in fields.iter().enumerate() {
            validate_field(field, index, &reachable, row_set)?;
        }
    }

    if let Some(cites) = &selection.cites {
        if cites.target.schema_id.is_empty() || cites.target.record_key.is_empty() {
            return Err(SelectionInvalid::new(
                "cites.target",
                None,
                "selection.cites.target requires schemaId and recordKey.",
            ));
        }
        // R-E: cites.target.schemaId goes through the one schema-identity rule too. An unmatched
        // target is refused at the door, never silently answered with an empty page.
        if !row_set
            .all_schema_ids()
            .iter()
            .any(|schema_id| schema_alias::matches(&cites.target.schema_id, schema_id))
        {
            return Err(SelectionInvalid::new(
                "cites.target.schemaId",
                Some(cites.target.schema_id.clone()),
                format!(
                    "selection.cites.target.schemaId {:?} matches no known schema.",
                    cites.target.schema_id
                ),
            ));
        }
        if let Some(role) = &cites.role
            && !any_schema_declares_role(row_set, role)
        {
            return Err(SelectionInvalid::new(
                "cites.role",
                Some(role.clone()),
                format!("selection.cites.role {role:?} is not declared by any schema."),
            ));
        }
    }

    if let Some(cited) = &selection.cited {
        if cited.role.is_empty() {
            return Err(SelectionInvalid::new(
                "cited.role",
                None,
                "selection.cited.role must be non-empty.",
            ));
        }
        if !any_schema_declares_role(row_set, &cited.role) {
            return Err(SelectionInvalid::new(
                "cited.role",
                Some(cited.role.clone()),
                format!(
                    "selection.cited.role {:?} is not declared by any schema.",
                    cited.role
                ),
            ));
        }
    }

    Ok(())
}

/// The schemas [`Selection::schemas`] reaches - every schema when absent.
pub fn reachable_schemas(selection: &Selection, row_set: &impl RowSet) -> Vec<String> {
    let all = row_set.all_schema_ids();
    match &selection.schemas {
        None => all,
        // R-E: reachability goes through the alias matcher, not exact equality.
        Some(schemas) => all
            .into_iter()
            .filter(|id| schema_alias::matches_any(schemas, id))
            .collect(),
    }
}

fn any_schema_declares_role(row_set: &impl RowSet, role: &str) -> bool {
    row_set
        .all_schema_ids()
        .iter()
        .any(|schema_id| row_set.declared_roles(schema_id).iter().any(|r| r == role))
}

fn validate_field(
    field: &FieldPredicate,
    index: usize,
    reachable: &[String],
    row_set: &impl RowSet,
) -> Result<(), SelectionInvalid> {
    let prefix = format!("fields[{index}]");
    if field.index.is_empty() {
        return Err(SelectionInvalid::new(
            format!("{prefix}.index"),
            None,
            format!("{prefix}.index must be non-empty."),
        ));
    }
    let Some(op) = SelectionOperator::parse(&field.op) else {
        return Err(SelectionInvalid::new(
            format!("{prefix}.op"),
            Some(field.op.clone()),
            format!("{prefix}.op {:?} is not any_of/lt/le/ge/gt.", field.op),
        ));
    };

    if op == SelectionOperator::AnyOf {
        match &field.values {
            None => {
                return Err(SelectionInvalid::new(
                    format!("{prefix}.values"),
                    None,
                    format!("{prefix}.values must be non-empty for op any_of."),
                ));
            }
            Some(values) if values.is_empty() => {
                return Err(SelectionInvalid::new(
                    format!("{prefix}.values"),
                    None,
                    format!("{prefix}.values must be non-empty for op any_of."),
                ));
            }
            _ => {}
        }
        if field.number.is_some() {
            return Err(SelectionInvalid::new(
                format!("{prefix}.number"),
                None,
                format!("{prefix} carries both values and number; any_of takes only values."),
            ));
        }
    } else {
        match &field.number {
            None => {
                return Err(SelectionInvalid::new(
                    format!("{prefix}.number"),
                    None,
                    format!("{prefix}.number is required for op {}.", field.op),
                ));
            }
            Some(number) if !canonical_number::is_canonical(number) => {
                return Err(SelectionInvalid::new(
                    format!("{prefix}.number"),
                    Some(number.clone()),
                    format!(
                        "{prefix}.number {number:?} is not a canonical decimal string (Q-J): it must match {} and not be \"-0\".",
                        r"^-?(0|[1-9][0-9]*)(\.[0-9]*[1-9])?$"
                    ),
                ));
            }
            _ => {}
        }
        if field.values.is_some() {
            return Err(SelectionInvalid::new(
                format!("{prefix}.values"),
                None,
                format!("{prefix} carries both values and number; a comparison takes only number."),
            ));
        }
    }

    let declaring: Vec<&String> = reachable
        .iter()
        .filter(|schema_id| {
            row_set
                .declared_indexes(schema_id)
                .iter()
                .any(|alias| alias == &field.index)
        })
        .collect();
    if declaring.is_empty() {
        return Err(SelectionInvalid::new(
            format!("{prefix}.index"),
            Some(field.index.clone()),
            format!(
                "{prefix}.index {:?} is not declared by any schema this selection can reach.",
                field.index
            ),
        ));
    }
    if op.is_comparison() {
        for schema_id in &declaring {
            if !row_set.is_numeric(schema_id, &field.index) {
                return Err(SelectionInvalid::new(
                    format!("{prefix}.index"),
                    Some(field.index.clone()),
                    format!(
                        "{prefix}.index {:?} is declared non-numeric on a schema this selection can reach; comparisons need a numeric declaration everywhere the alias is reachable.",
                        field.index
                    ),
                ));
            }
        }
    }

    Ok(())
}

// ---------------------------------------------------------------------------------------------
// Evaluation (D6/D7: the single-row fast path and the full Select)
// ---------------------------------------------------------------------------------------------

/// The lowest and highest limit a page may carry (section 2).
pub const LIMIT_MIN: u32 = 1;
pub const LIMIT_MAX: u32 = 200;

/// The single-row fast path (D6): schemas, keys and fields only. A hop-bearing selection
/// ([`Selection::has_hop`]) is set-dependent and must not use this path - the caller reconciles the
/// full row set with [`select`] instead.
///
/// R-F: this is a real, always-on `assert!`, not a `debug_assert!` - a release build must not
/// silently ignore `cites`/`cited` by skipping straight to the schema/keys/fields comparison; a
/// caller that reaches this with a hop-bearing selection has a programming error to fix, not a
/// page to answer wrong.
pub fn matches<R: Row>(row: &R, selection: &Selection) -> bool {
    assert!(
        !selection.has_hop(),
        "a hop-bearing selection (cites/cited) is set-dependent and cannot use the single-row \
         matches fast path; reconcile the full row set with select() instead"
    );
    matches_schema_keys_fields(row, selection)
}

fn matches_schema_keys_fields<R: Row>(row: &R, selection: &Selection) -> bool {
    // R-E, but deliberately not `schema_alias::matches_any`: that helper's `candidates.is_empty()`
    // shortcut mirrors the C# reference's own `MatchesAny(schemaId)` on purpose (R-E), but this is
    // the evaluator's fast path, reached without going through `validate` on the v0 lowering path
    // (`serve_read_only_raw_snapshot`, which never calls `select`/`validate` by design - v0 has its
    // own null-collapsing). An empty-but-present `schemas` must never be silently read as "every
    // schema" here - only `None` means no filter. A plain `.any()` over an empty slice is already
    // `false` (matches nothing), which is what a present-but-empty list must do until something
    // upstream (the v1 door, or v0's own lowering) turns it into `None` or refuses it outright.
    if let Some(schemas) = &selection.schemas
        && !schemas.iter().any(|candidate| schema_alias::matches(candidate, row.schema_id()))
    {
        return false;
    }
    if let Some(keys) = &selection.keys
        && !keys.iter().any(|k| k == row.record_key())
    {
        return false;
    }
    let Some(fields) = &selection.fields else {
        return true;
    };

    for field in fields {
        let Some(op) = SelectionOperator::parse(&field.op) else {
            return false;
        };
        if op == SelectionOperator::AnyOf {
            let values = row.values(&field.index);
            let Some(wanted) = &field.values else {
                return false;
            };
            if !values.iter().any(|v| wanted.iter().any(|w| w == v)) {
                return false;
            }
        } else {
            let Some(number) = row.number(&field.index) else {
                return false;
            };
            let Some(compared) = &field.number else {
                return false;
            };
            if !compare_number(&number, op, compared) {
                return false;
            }
        }
    }
    true
}

fn compare_number(left: &str, op: SelectionOperator, right: &str) -> bool {
    let ordering = canonical_number::compare(left, right);
    match op {
        SelectionOperator::Lt => ordering == Ordering::Less,
        SelectionOperator::Le => ordering != Ordering::Greater,
        SelectionOperator::Ge => ordering != Ordering::Less,
        SelectionOperator::Gt => ordering == Ordering::Greater,
        SelectionOperator::AnyOf => unreachable!("any_of never reaches compare_number"),
    }
}

/// One edge a hop traversed, resolved against the row set in hand.
#[derive(Clone, Debug)]
pub struct EdgeMatch<R: Row + Clone> {
    pub from: R,
    pub role: String,
    pub to: R,
    pub payload: Option<Vec<u8>>,
}

/// Evaluated, ordered, paged rows plus the edges a hop traversed.
#[derive(Clone, Debug)]
pub struct Evaluation<R: Row + Clone> {
    pub rows: Vec<R>,
    /// The total number of matches (C5, R-G/P-1) - not the page count. One evaluation answers
    /// both v0 and shard paging as a single snapshot.
    pub matched: u32,
    pub edges: Vec<EdgeMatch<R>>,
    pub next_cursor: Option<String>,
}

/// Evaluates a selection over the full row set: schemas/keys/fields, the hop (`cites`/`cited`, with
/// its edges and the `reference_outside_target` refusal), order, cursor and paging. Mirrors
/// `CultNetSelectionEvaluator.Select`.
///
/// R-F: the door is inside `select` - this validates first and returns the typed refusal; there is
/// no public entry point that evaluates an unvalidated selection.
pub fn select<R: Row + Clone>(
    row_set: &impl RowSet,
    all_rows: &[R],
    selection: &Selection,
    as_of: u64,
) -> Result<Evaluation<R>, SelectionRefusal> {
    validate(selection, row_set)?;

    let by_key: HashMap<&str, &R> = all_rows.iter().map(|row| (row.record_key(), row)).collect();

    let mut candidates: Vec<&R> = all_rows
        .iter()
        .filter(|row| matches_schema_keys_fields(*row, selection))
        .collect();

    let mut edges: Vec<EdgeMatch<R>> = Vec::new();

    if let Some(cited) = &selection.cited {
        let incoming = build_incoming_index(row_set, all_rows, &by_key, &cited.role, &mut edges)?;
        let exists = cited.exists;
        candidates.retain(|row| incoming.contains(row.record_key()) == exists);
    }

    if let Some(cites) = &selection.cites {
        let mut kept = Vec::with_capacity(candidates.len());
        for row in candidates {
            if matches_citation(row_set, &by_key, row, cites, &mut edges)? {
                kept.push(row);
            }
        }
        candidates = kept;
    }

    let mut matched = candidates;
    matched.sort_by(|a, b| order_rows(*a, *b, selection.descending));
    let matched_total = matched.len() as u32;

    let mut start_index = 0usize;
    if let Some(cursor_text) = selection.cursor.as_deref()
        && !cursor_text.is_empty()
    {
        let cursor = Cursor::parse(cursor_text)?;
        if cursor.digest != Cursor::compute_digest(selection) {
            return Err(SelectionRefusal::CursorInvalid {
                message: "The cursor's selection digest does not match this selection.".into(),
            });
        }
        if cursor.as_of != as_of {
            return Err(SelectionRefusal::CursorStale {
                as_of: cursor.as_of,
                current: as_of,
            });
        }
        start_index = find_cursor_position(&matched, &cursor, selection.descending);
    }

    let limit = selection
        .limit
        .unwrap_or(LIMIT_MAX)
        .clamp(LIMIT_MIN, LIMIT_MAX) as usize;
    let page: Vec<R> = matched
        .iter()
        .skip(start_index)
        .take(limit)
        .map(|row| (*row).clone())
        .collect();
    let has_next = start_index + page.len() < matched.len();
    let next_cursor = if has_next && !page.is_empty() {
        Some(Cursor::mint(as_of, page.last().expect("checked non-empty"), selection))
    } else {
        None
    };

    // R-B: hop edges follow the hop's direction. Under `cites`, the edges are the ones *from*
    // page rows (the citer is what is paged); under `cited`, they are the ones *into* page rows
    // (the citee is what is paged). Order is deterministic: page-row order, then (from, role, to)
    // in code-point order - never a `HashMap`'s iteration order.
    let page_edges = if selection.has_hop() {
        let page_position: HashMap<&str, usize> = page
            .iter()
            .enumerate()
            .map(|(index, row)| (row.record_key(), index))
            .collect();
        let cited_direction = selection.cited.is_some();
        let mut kept: Vec<EdgeMatch<R>> = edges
            .into_iter()
            .filter(|edge| {
                let owner = if cited_direction { edge.to.record_key() } else { edge.from.record_key() };
                page_position.contains_key(owner)
            })
            .collect();
        kept.sort_by(|a, b| {
            let owner = |edge: &EdgeMatch<R>| -> usize {
                let key = if cited_direction { edge.to.record_key() } else { edge.from.record_key() };
                page_position[key]
            };
            owner(a)
                .cmp(&owner(b))
                .then_with(|| a.from.schema_id().cmp(b.from.schema_id()))
                .then_with(|| a.from.record_key().cmp(b.from.record_key()))
                .then_with(|| a.role.cmp(&b.role))
                .then_with(|| a.to.schema_id().cmp(b.to.schema_id()))
                .then_with(|| a.to.record_key().cmp(b.to.record_key()))
        });
        kept
    } else {
        Vec::new()
    };

    Ok(Evaluation {
        rows: page,
        matched: matched_total,
        edges: page_edges,
        next_cursor,
    })
}

// ---------------------------------------------------------------------------------------------
// Page projection (R-I): `cultnet.snapshot_response_raw.v1`'s wire shape
// ---------------------------------------------------------------------------------------------

/// `cultnet.snapshot_response_raw.v1`'s page (section 2 `SelectionPage`): `matched`, the page
/// itself under [`Selection::projection`], and the hop's edges. Carried on the wire by the
/// consumer's own message envelope (`shardId`/`shardEpoch`/`shardLogSequence`, `messageId`, ... -
/// declared once already in `contracts.rs`'s `CultNetMessage::SnapshotResponseRawV1`); this type
/// is the page body those fields wrap.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SelectionPage {
    pub matched: u32,
    pub as_of: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub headers: Option<Vec<RawDocumentHeader>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub documents: Option<Vec<SelectionDocumentRecord>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub edges: Option<Vec<Edge>>,
}

/// Evaluates a selection and projects it into the wire's `SelectionPage` shape (R-I): `select`'s
/// generic rows become `headers` or `documents` under [`Selection::projection`], never both, and a
/// header projection carries no payload - in the page's rows and in its edges alike (S20). The
/// caller supplies `document_record`, the one place a full wire record is built from an `R` (D7:
/// the evaluator never reflects over a row; the row owner renders it).
pub fn select_page<R: Row + Clone>(
    row_set: &impl RowSet,
    all_rows: &[R],
    selection: &Selection,
    as_of: u64,
    document_record: impl Fn(&R) -> SelectionDocumentRecord,
) -> Result<SelectionPage, SelectionRefusal> {
    let evaluation = select(row_set, all_rows, selection, as_of)?;
    let want_document = selection.projection == PROJECTION_DOCUMENT;

    let (headers, documents) = if want_document {
        (None, Some(evaluation.rows.iter().map(&document_record).collect()))
    } else {
        (
            Some(
                evaluation
                    .rows
                    .iter()
                    .map(|row| header_from_record(&document_record(row)))
                    .collect(),
            ),
            None,
        )
    };

    let edges = if selection.has_hop() {
        Some(
            evaluation
                .edges
                .iter()
                .map(|edge| Edge {
                    from: RecordRef::new(edge.from.schema_id(), edge.from.record_key()),
                    role: edge.role.clone(),
                    to: RecordRef::new(edge.to.schema_id(), edge.to.record_key()),
                    // S20: a header projection carries no payload, in edges too.
                    payload_encoding: if want_document && edge.payload.is_some() {
                        Some(messagepack_encoding())
                    } else {
                        None
                    },
                    payload: if want_document { edge.payload.clone() } else { None },
                })
                .collect(),
        )
    } else {
        None
    };

    Ok(SelectionPage {
        matched: evaluation.matched,
        as_of,
        next: evaluation.next_cursor,
        headers,
        documents,
        edges,
    })
}

/// A header carries every field a document record does, minus its payload and payload encoding
/// (private: R-M deleted the public, zero-caller `RawDocumentHeader::from_document`; this is the
/// one real caller, folded in rather than resurrecting that associated function under a new name).
fn header_from_record(document: &SelectionDocumentRecord) -> RawDocumentHeader {
    RawDocumentHeader {
        schema_id: document.schema_id.clone(),
        schema_name: document.schema_name.clone(),
        schema_version: document.schema_version.clone(),
        schema_content_hash: document.schema_content_hash.clone(),
        record_key: document.record_key.clone(),
        stored_at: document.stored_at.clone(),
        source_runtime_id: document.source_runtime_id.clone(),
        source_agent_id: document.source_agent_id.clone(),
        source_role: document.source_role.clone(),
        tags: document.tags.clone(),
    }
}

fn order_rows<R: Row>(a: &R, b: &R, descending: bool) -> Ordering {
    let ordering = a
        .ordinal()
        .cmp(&b.ordinal())
        .then_with(|| a.schema_id().cmp(b.schema_id()))
        .then_with(|| a.record_key().cmp(b.record_key()));
    if descending { ordering.reverse() } else { ordering }
}

fn find_cursor_position<R: Row>(ordered: &[R], cursor: &Cursor, descending: bool) -> usize {
    for (index, row) in ordered.iter().enumerate() {
        let position = compare_position(row, cursor);
        let compares_after = if descending {
            position == Ordering::Less
        } else {
            position == Ordering::Greater
        };
        if compares_after {
            return index;
        }
    }
    ordered.len()
}

fn compare_position<R: Row>(row: &R, cursor: &Cursor) -> Ordering {
    row.ordinal()
        .cmp(&cursor.ordinal)
        .then_with(|| row.schema_id().cmp(cursor.schema_id.as_str()))
        .then_with(|| row.record_key().cmp(cursor.record_key.as_str()))
}

// D9: a reference's target set is every schema `RowSet::target_leaves` names for that role. A
// stored edge naming a row whose schema is outside that set refuses the selection (S18).
fn ensure_within_declared_target<R: Row>(
    row_set: &impl RowSet,
    role: &str,
    from: &R,
    to: &R,
) -> Result<(), SelectionRefusal> {
    let leaves = row_set.target_leaves(role);
    if leaves.is_empty() || leaves.iter().any(|leaf| leaf == to.schema_id()) {
        return Ok(());
    }
    Err(SelectionRefusal::ReferenceOutsideTarget {
        from_schema_id: from.schema_id().to_string(),
        from_key: from.record_key().to_string(),
        role: role.to_string(),
        to_schema_id: to.schema_id().to_string(),
        to_key: to.record_key().to_string(),
    })
}

fn matches_citation<R: Row + Clone>(
    row_set: &impl RowSet,
    by_key: &HashMap<&str, &R>,
    citer: &R,
    citation: &Citation,
    edge_sink: &mut Vec<EdgeMatch<R>>,
) -> Result<bool, SelectionRefusal> {
    let mut found = false;
    for (role, target, payload) in citer.references() {
        if let Some(wanted_role) = &citation.role
            && *wanted_role != role
        {
            continue;
        }
        if target.record_key != citation.target.record_key {
            continue;
        }
        let Some(resolved) = by_key.get(target.record_key.as_str()) else {
            continue;
        };
        ensure_within_declared_target(row_set, &role, citer, *resolved)?;
        // R-E: the target's schema id is matched through the one alias rule, not exact equality.
        if !schema_alias::matches(&citation.target.schema_id, resolved.schema_id()) {
            continue;
        }
        edge_sink.push(EdgeMatch {
            from: citer.clone(),
            role,
            to: (*resolved).clone(),
            payload,
        });
        found = true;
    }
    Ok(found)
}

// R-B: iterates `all_rows` (the caller's own `Vec` order), never `by_key.values()` - a `HashMap`'s
// iteration order is not something this function's output may depend on, even transiently, given
// `select`'s own final sort relies on nothing here having silently picked up map order.
fn build_incoming_index<R: Row + Clone>(
    row_set: &impl RowSet,
    all_rows: &[R],
    by_key: &HashMap<&str, &R>,
    role: &str,
    edge_sink: &mut Vec<EdgeMatch<R>>,
) -> Result<HashSet<String>, SelectionRefusal> {
    let mut incoming = HashSet::new();
    for citer in all_rows {
        for (edge_role, target, payload) in citer.references() {
            if edge_role != role {
                continue;
            }
            let Some(resolved) = by_key.get(target.record_key.as_str()) else {
                continue;
            };
            ensure_within_declared_target(row_set, &edge_role, citer, *resolved)?;
            incoming.insert(target.record_key.clone());
            edge_sink.push(EdgeMatch {
                from: citer.clone(),
                role: edge_role,
                to: (*resolved).clone(),
                payload,
            });
        }
    }
    Ok(incoming)
}

// ---------------------------------------------------------------------------------------------
// Cursor (section 2: "Snapshot")
// ---------------------------------------------------------------------------------------------

/// The opaque cursor: `asOf`, the last `(ordinal, schemaId, recordKey)`, and a digest of the
/// selection with `cursor`/`limit` cleared. Minted only by the answering server. This runtime's own
/// mint/parse/digest algorithm is internal to it - a cursor is never handed across runtimes (each
/// server answers its own pages), so it need not (and does not) byte-match the C# reference's; the
/// *rule* it enforces (section 2 "Snapshot") does.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Cursor {
    pub as_of: u64,
    pub ordinal: i64,
    pub schema_id: String,
    pub record_key: String,
    pub digest: String,
}

const CURSOR_FIELD_SEPARATOR: char = '\u{1}';

impl Cursor {
    pub fn mint<R: Row>(as_of: u64, last_row: &R, selection: &Selection) -> String {
        let digest = Self::compute_digest(selection);
        let raw = [
            as_of.to_string(),
            last_row.ordinal().to_string(),
            last_row.schema_id().to_string(),
            last_row.record_key().to_string(),
            digest,
        ]
        .join(&CURSOR_FIELD_SEPARATOR.to_string());
        URL_SAFE_NO_PAD.encode(raw.as_bytes())
    }

    pub fn parse(cursor: &str) -> Result<Self, SelectionRefusal> {
        let invalid = || SelectionRefusal::CursorInvalid {
            message: "The cursor does not decode.".into(),
        };
        let bytes = URL_SAFE_NO_PAD.decode(cursor).map_err(|_| invalid())?;
        let raw = String::from_utf8(bytes).map_err(|_| invalid())?;
        let parts: Vec<&str> = raw.split(CURSOR_FIELD_SEPARATOR).collect();
        if parts.len() != 5 {
            return Err(invalid());
        }
        let as_of: u64 = parts[0].parse().map_err(|_| invalid())?;
        let ordinal: i64 = parts[1].parse().map_err(|_| invalid())?;
        Ok(Self {
            as_of,
            ordinal,
            schema_id: parts[2].to_string(),
            record_key: parts[3].to_string(),
            digest: parts[4].to_string(),
        })
    }

    /// A digest of the selection with `cursor` and `limit` cleared, so a cursor is bound to the
    /// selection that minted it.
    ///
    /// R-H: every string and list is length-prefixed, so no separator character can ever make two
    /// distinct selections collide (Soul found `["a|b"]` and `["a","b"]` digesting the same under
    /// the previous separator-joined scheme). This need not (and does not) byte-match the C#
    /// reference's own digest - each server answers its own pages (see this type's doc comment) -
    /// only the collision-freedom is load-bearing.
    pub fn compute_digest(selection: &Selection) -> String {
        let mut sorted_schemas = selection.schemas.clone().unwrap_or_default();
        sorted_schemas.sort();
        let mut sorted_keys = selection.keys.clone().unwrap_or_default();
        sorted_keys.sort();

        let mut canonical = String::new();
        write_length_prefixed_list(&mut canonical, &sorted_schemas);
        write_length_prefixed_list(&mut canonical, &sorted_keys);

        let fields = selection.fields.as_deref().unwrap_or_default();
        canonical.push_str(&fields.len().to_string());
        canonical.push('{');
        for field in fields {
            write_length_prefixed(&mut canonical, &field.index);
            write_length_prefixed(&mut canonical, &field.op);
            write_length_prefixed_list(&mut canonical, field.values.as_deref().unwrap_or_default());
            write_length_prefixed(&mut canonical, field.number.as_deref().unwrap_or_default());
        }
        canonical.push('}');

        match &selection.cites {
            Some(cites) => {
                canonical.push('1');
                write_length_prefixed(&mut canonical, &cites.target.schema_id);
                write_length_prefixed(&mut canonical, &cites.target.record_key);
                write_length_prefixed(&mut canonical, cites.role.as_deref().unwrap_or_default());
            }
            None => canonical.push('0'),
        }

        match &selection.cited {
            Some(cited) => {
                canonical.push('1');
                write_length_prefixed(&mut canonical, &cited.role);
                canonical.push(if cited.exists { 'T' } else { 'F' });
            }
            None => canonical.push('0'),
        }

        write_length_prefixed(&mut canonical, &selection.projection);
        canonical.push(if selection.descending { 'D' } else { 'A' });

        let mut hasher = Sha256::new();
        hasher.update(canonical.as_bytes());
        hasher
            .finalize()
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect()
    }
}

/// Writes `value` as `<byte length>:<value>` - unambiguous regardless of what characters `value`
/// itself carries, including the separator this module would otherwise use.
fn write_length_prefixed(buffer: &mut String, value: &str) {
    buffer.push_str(&value.len().to_string());
    buffer.push(':');
    buffer.push_str(value);
}

/// Writes a list as `<count>[<len-prefixed item><len-prefixed item>...]`.
fn write_length_prefixed_list(buffer: &mut String, values: &[String]) {
    buffer.push_str(&values.len().to_string());
    buffer.push('[');
    for value in values {
        write_length_prefixed(buffer, value);
    }
    buffer.push(']');
}
