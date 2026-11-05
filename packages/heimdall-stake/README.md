# heimdall-stake

Heimdall's stake function: how much weight an amount of money buys one identity. Pure,
deterministic, no I/O, no dependencies; integer cents in, integer micro-weights out
(`MICRO` = 1,000,000 = a weight of 1.0), and an integer-only logarithm, so the same inputs give
the same bits on every platform. Entry points: `weight` and `splitting_gain` in `src/lib.rs`.
Live: the formula, the three Heimdall parameters, the splitting-gain report and their property
tests (`tests/properties.rs`). Not here: the estate rule that normalizes weights, the sybil
adversary, storage, and any projection. Every parameter is a placeholder until the estates rule.

## First call

```
cargo run --example first_call      # prints the Meta example and a splitting table
cargo test
```

```rust
use heimdall_stake::*;
let p = Params::meta_example();                  // unit 1 EUR, log10
assert_eq!(weight(&p, 100_00).unwrap(), 3 * MICRO);        // 100 EUR
assert_eq!(weight(&p, 10_000_00).unwrap(), 5 * MICRO);     // 10,000 EUR
let costly = Params { min_amount_per_identity_cents: 50_00, ..p };
let r = splitting_gain(&costly, 100_00, 3).unwrap();       // 100 EUR as three identities of 33.33
assert_eq!((r.one_identity, r.split_total), (3 * MICRO, 0));
```

The output feeds `heimdall-delegation`'s integer `weight`; the caller picks the scale.

## Formula

Per Meta, `weight = 1 + log_b(amount / unit)`, with the base `b` and the `unit` set by the charter.
Meta's example: unit 1 EUR, `b` = 10: 100 EUR weighs 3, 10,000 EUR weighs 5 (both tested exactly).
Amounts are integer cents; negative and zero amounts weigh 0. The result is clamped at 0 (an
amount under `unit / b` would otherwise go negative) and rounded to the nearest micro-weight.

Heimdall's parameters on top:

- `floor_cents`: an amount **at or below** the floor weighs 0. (The brief said both "below" and
  "at/below"; at-or-below is implemented and tested.)
- `per_identity_cap`: the most weight one identity can hold, in micro-weights; `None` is uncapped.
- `min_amount_per_identity_cents`: an identity holding **less** than this is ineligible and weighs 0.

`floor` and `min_amount_per_identity` both zero a weight and compose; they differ in what they are
for (the floor trims dust from the formula, the minimum prices an identity), and the estates may
want one parameter instead of two.

Properties (tested): monotone in the amount for any parameters; concave above the unit when no
floor or minimum intrudes (the floor and minimum are steps, so the whole function is not concave);
zero at and below the floor; the cap is never exceeded and binds; deterministic; total over every
`i64` including `i64::MIN`; the integer log agrees with `f64` to 2 micro-weights; bad parameters
(`base < 2`, `unit == 0`) are `Err`, not panics.

## Splitting gain

`splitting_gain(params, budget_cents, k)` puts one budget in one identity and, separately, evenly
over `k` identities, and reports both totals, the gain, how many split identities are eligible and
the smallest share. `splitting_curve` and `most_profitable_split` sweep `k`.

The log formula's `+1` is a splitting incentive: with no minimum, 100 EUR as ten identities of
10 EUR weighs 20 against 3 as one. The minimum is the only lever that prices it, and it prices it
as a cliff, not a slope: with a 50 EUR minimum, 100 EUR split in two still gains (5.40 against 3.00
in the example), and split in three weighs 0. So the minimum has to sit near the size of a typical
budget to deter splitting, and the report is how to see where it does.

## Where this sits in the estate rule

This crate does not normalize. The estate rule normalizes weights above it; the weights here are
raw. A sybil rate (how many identities an adversary splits a budget into) is an adversary the
simulation harness plays, not a parameter of this function; `splitting_gain` is its closed-form
probe for the even-split case only.

## Prior art

Sources below are from background knowledge and were not re-fetched for this README; verify before
citing.

- **Quadratic voting** (Lalley and Weyl, "Quadratic Voting: How Mechanism Design Can Radicalize
  Democracy", 2018; Posner and Weyl, *Radical Markets*, 2018): the cost of v votes is v², so the
  marginal price rises linearly and vote weight grows with the square root of spend. Lalley-Weyl
  show it is efficient in large populations under assumptions that include no collusion and no
  identity splitting, and that identity splitting is exactly what breaks it. Our weight grows
  slower still (log), so the money buys less influence at the top, and the same sybil caveat applies
  with the same remedy: a cost per identity.
- **Logarithmic weight** has the same shape as diminishing-returns utility (Bernoulli) and as the
  concave caps in some stake-weighted systems; here its role is only concavity, with the charter
  choosing how fast through the base.
- **Sybil attack** (Douceur, 2002): without a cost per identity, an open system cannot tell one
  participant from many. The per-identity minimum is that cost, in money.
