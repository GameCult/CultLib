//! Heimdall's stake function. See README.md for the formula and its placeholders.
//!
//! `weight = 1 + log_b(amount / unit)`, with a floor, a per-identity cap and a minimum amount per
//! identity. Pure and integer-only: amounts are integer cents, weights are integer micro-weights
//! (`MICRO` = 1.0), and the logarithm is computed in fixed point with no floating point, so the
//! same inputs give the same bits on every platform.

use std::fmt;

/// Weight in millionths: `MICRO` is a weight of 1.0. Feeds `heimdall-delegation`'s integer weights
/// after the caller picks a scale.
pub type Weight = u64;
pub const MICRO: Weight = 1_000_000;

/// All of these are placeholders until the estates rule; the charter sets `base` and `unit_cents`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Params {
    /// Logarithm base `b`, an integer of at least 2.
    pub base: u32,
    /// The amount worth weight 1.0, in cents; at least 1.
    pub unit_cents: u64,
    /// Amounts at or below this weigh 0.
    pub floor_cents: u64,
    /// Highest weight one identity can hold, in micro-weights; `None` is uncapped.
    pub per_identity_cap: Option<Weight>,
    /// An identity holding less than this is not eligible and weighs 0 (the splitting cost).
    pub min_amount_per_identity_cents: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ParamError {
    BaseBelowTwo,
    UnitZero,
}

impl fmt::Display for ParamError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ParamError::BaseBelowTwo => write!(f, "log base must be at least 2"),
            ParamError::UnitZero => write!(f, "unit must be at least 1 cent"),
        }
    }
}

impl std::error::Error for ParamError {}

impl Params {
    /// Meta's example: unit 1 EUR, log10, no floor, no cap, no minimum.
    pub fn meta_example() -> Params {
        Params { base: 10, unit_cents: 100, floor_cents: 0, per_identity_cap: None, min_amount_per_identity_cents: 0 }
    }

    pub fn validate(&self) -> Result<(), ParamError> {
        if self.base < 2 {
            return Err(ParamError::BaseBelowTwo);
        }
        if self.unit_cents == 0 {
            return Err(ParamError::UnitZero);
        }
        Ok(())
    }
}

const FRAC: u32 = 40;

/// log2(n) for n >= 1 in Q40 fixed point, by the square-and-compare method: no floats, so it is
/// the same everywhere. Error is below 2^-38.
fn log2_q40(n: u64) -> i128 {
    let k = 63 - n.leading_zeros();
    let mut m: u128 = if k >= 60 { (n >> (k - 60)) as u128 } else { (n as u128) << (60 - k) }; // [1,2) in Q60
    let mut frac: u128 = 0;
    for bit in (0..FRAC).rev() {
        m = (m * m) >> 60;
        if m >= 1u128 << 61 {
            m >>= 1;
            frac |= 1u128 << bit;
        }
    }
    ((k as i128) << FRAC) | frac as i128
}

/// Weight of one identity holding `amount_cents`. Zero for zero and negative amounts, for amounts at
/// or below the floor, and for amounts under the per-identity minimum. Never panics on any input.
pub fn weight(params: &Params, amount_cents: i64) -> Result<Weight, ParamError> {
    params.validate()?;
    if amount_cents <= 0 {
        return Ok(0);
    }
    let amount = amount_cents as u64;
    if amount <= params.floor_cents || amount < params.min_amount_per_identity_cents {
        return Ok(0);
    }
    // micro = 1e6 * (1 + (log2 a - log2 u) / log2 b), rounded to nearest.
    let num = (log2_q40(amount) - log2_q40(params.unit_cents)) * MICRO as i128;
    let den = log2_q40(params.base as u64);
    let half = den / 2;
    let rounded = if num >= 0 { (num + half) / den } else { (num - half) / den };
    let raw = (MICRO as i128 + rounded).max(0) as u128;
    let capped = match params.per_identity_cap {
        Some(cap) => raw.min(cap as u128),
        None => raw,
    };
    Ok(capped.min(Weight::MAX as u128) as Weight)
}

/// Total weight of several identities, each weighed on its own (the cap and minimum are per identity).
pub fn total_weight(params: &Params, amounts_cents: &[i64]) -> Result<Weight, ParamError> {
    params.validate()?;
    let mut total: Weight = 0;
    for &a in amounts_cents {
        total = total.saturating_add(weight(params, a)?);
    }
    Ok(total)
}

/// One budget held as a single identity against the same budget split evenly over `k` identities
/// (the first `budget % k` identities hold one extra cent).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SplitReport {
    pub budget_cents: i64,
    pub k: u64,
    pub one_identity: Weight,
    pub split_total: Weight,
    /// `split_total - one_identity`: positive means splitting pays.
    pub gain: i128,
    /// Identities of the split that clear the floor and the minimum.
    pub eligible_identities: u64,
    /// Smallest amount any identity of the split holds.
    pub smallest_share_cents: i64,
}

/// What splitting `budget_cents` over `k` identities does to total weight. `k` of 0 is treated as 1.
pub fn splitting_gain(params: &Params, budget_cents: i64, k: u64) -> Result<SplitReport, ParamError> {
    params.validate()?;
    let k = k.max(1);
    let one_identity = weight(params, budget_cents)?;
    let (base_share, extra, smallest) = if budget_cents <= 0 {
        (0i64, 0u64, budget_cents)
    } else {
        let b = budget_cents as u64;
        ((b / k) as i64, b % k, (b / k) as i64)
    };
    // Identities are of two sizes, so weigh the two sizes instead of looping over k.
    let mut split_total: u128 = 0;
    let mut eligible = 0u64;
    let big = k.min(extra);
    let small = k - big;
    for (count, amount) in [(big, base_share.saturating_add(1)), (small, base_share)] {
        if count == 0 {
            continue;
        }
        let w = weight(params, amount)?;
        if w > 0 {
            eligible += count;
        }
        split_total += w as u128 * count as u128;
    }
    let split_total = split_total.min(Weight::MAX as u128) as Weight;
    Ok(SplitReport {
        budget_cents,
        k,
        one_identity,
        split_total,
        gain: split_total as i128 - one_identity as i128,
        eligible_identities: eligible,
        smallest_share_cents: smallest,
    })
}

/// `splitting_gain` for k = 1..=max_k.
pub fn splitting_curve(params: &Params, budget_cents: i64, max_k: u64) -> Result<Vec<SplitReport>, ParamError> {
    (1..=max_k.max(1)).map(|k| splitting_gain(params, budget_cents, k)).collect()
}

/// The k in 1..=max_k with the largest split total (smallest k on ties), and its report.
pub fn most_profitable_split(params: &Params, budget_cents: i64, max_k: u64) -> Result<SplitReport, ParamError> {
    let mut best = splitting_gain(params, budget_cents, 1)?;
    for k in 2..=max_k {
        let r = splitting_gain(params, budget_cents, k)?;
        if r.split_total > best.split_total {
            best = r;
        }
    }
    Ok(best)
}
