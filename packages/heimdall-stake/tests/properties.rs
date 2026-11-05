use heimdall_stake::*;

/// xorshift64*: seeded, dependency-free, reproducible.
struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545F4914F6CDD1D)
    }
    fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }
}

fn random_params(r: &mut Rng) -> Params {
    Params {
        base: 2 + r.below(15) as u32,
        unit_cents: 1 + r.below(100_000),
        floor_cents: if r.below(2) == 0 { 0 } else { r.below(50_000) },
        per_identity_cap: if r.below(2) == 0 { None } else { Some(MICRO + r.below(8 * MICRO)) },
        min_amount_per_identity_cents: if r.below(2) == 0 { 0 } else { r.below(20_000) },
    }
}

/// Log-uniform amounts in cents, so every magnitude is exercised.
fn random_amount(r: &mut Rng) -> i64 {
    let bits = 1 + r.below(40);
    (r.next() >> (64 - bits)) as i64
}

#[test]
fn meta_example_unit_one_euro_log10() {
    let p = Params::meta_example();
    assert_eq!(weight(&p, 100_00).unwrap(), 3 * MICRO); // 100 EUR
    assert_eq!(weight(&p, 10_000_00).unwrap(), 5 * MICRO); // 10,000 EUR
    assert_eq!(weight(&p, 100).unwrap(), MICRO); // 1 EUR = the unit = weight 1
    assert_eq!(weight(&p, 10_00).unwrap(), 2 * MICRO); // 10 EUR
}

#[test]
fn base_two_and_other_units_hit_exact_powers() {
    let p = Params { base: 2, unit_cents: 250, ..Params::meta_example() };
    assert_eq!(weight(&p, 250 * 8).unwrap(), 4 * MICRO); // 1 + log2(8)
    let q = Params { base: 7, unit_cents: 1, ..Params::meta_example() };
    assert_eq!(weight(&q, 7u64.pow(9) as i64).unwrap(), 10 * MICRO);
}

#[test]
fn integer_log_agrees_with_floating_point_to_two_micro() {
    let mut r = Rng(0xC0FFEE);
    for _ in 0..20_000 {
        let p = Params { base: 2 + r.below(15) as u32, unit_cents: 1 + r.below(1_000_000), floor_cents: 0, per_identity_cap: None, min_amount_per_identity_cents: 0 };
        let a = 1 + random_amount(&mut r).abs();
        let expect = 1.0 + (a as f64 / p.unit_cents as f64).ln() / (p.base as f64).ln();
        let expect = (expect.max(0.0) * MICRO as f64).round();
        let got = weight(&p, a).unwrap() as f64;
        assert!((got - expect).abs() <= 2.0, "base {} unit {} amount {a}: {got} vs {expect}", p.base, p.unit_cents);
    }
}

#[test]
fn monotone_in_amount_for_any_parameters() {
    let mut r = Rng(1);
    for _ in 0..300 {
        let p = random_params(&mut r);
        let mut amounts: Vec<i64> = (0..60).map(|_| random_amount(&mut r)).collect();
        amounts.sort();
        let ws: Vec<Weight> = amounts.iter().map(|&a| weight(&p, a).unwrap()).collect();
        assert!(ws.windows(2).all(|w| w[0] <= w[1]), "{p:?} {amounts:?} {ws:?}");
    }
}

#[test]
fn concave_above_the_unit_when_no_floor_or_minimum_intrudes() {
    // Second differences of 1 + log_b(a/u) are non-positive; rounding to a micro-weight allows 2.
    let mut r = Rng(2);
    for _ in 0..300 {
        let mut p = random_params(&mut r);
        p.floor_cents = 0;
        p.min_amount_per_identity_cents = 0;
        let a = p.unit_cents as i64 + random_amount(&mut r) % 1_000_000_000;
        let d = 1 + (r.below(1_000_000) as i64);
        let (w0, w1, w2) = (weight(&p, a).unwrap() as i128, weight(&p, a + d).unwrap() as i128, weight(&p, a + 2 * d).unwrap() as i128);
        assert!((w2 - w1) <= (w1 - w0) + 2, "{p:?} a {a} d {d}: {w0} {w1} {w2}");
    }
}

#[test]
fn zero_at_and_below_the_floor_and_positive_just_above() {
    let mut r = Rng(3);
    for _ in 0..300 {
        let mut p = random_params(&mut r);
        p.min_amount_per_identity_cents = 0;
        p.per_identity_cap = None;
        p.floor_cents = 1 + r.below(1_000_000);
        for a in [p.floor_cents as i64, p.floor_cents as i64 - 1, 1] {
            assert_eq!(weight(&p, a).unwrap(), 0, "{p:?} amount {a}");
        }
        // Far enough above both floor and unit that the formula is positive.
        let above = (p.floor_cents.max(p.unit_cents) * 2) as i64;
        assert!(weight(&p, above).unwrap() > 0, "{p:?} amount {above}");
    }
}

#[test]
fn cap_is_never_exceeded_and_binds_when_the_formula_passes_it() {
    let mut r = Rng(4);
    for _ in 0..300 {
        let mut p = random_params(&mut r);
        let cap = MICRO + r.below(8 * MICRO);
        p.per_identity_cap = Some(cap);
        for _ in 0..40 {
            assert!(weight(&p, random_amount(&mut r)).unwrap() <= cap);
        }
        let uncapped = Params { per_identity_cap: None, ..p };
        let big = i64::MAX;
        if weight(&uncapped, big).unwrap() > cap && p.min_amount_per_identity_cents <= big as u64 {
            assert_eq!(weight(&p, big).unwrap(), cap);
        }
    }
}

#[test]
fn deterministic_and_total_over_every_input() {
    let mut r = Rng(5);
    for _ in 0..200 {
        let p = random_params(&mut r);
        for a in [i64::MIN, -1, 0, 1, i64::MAX, random_amount(&mut r)] {
            assert_eq!(weight(&p, a), weight(&p, a));
        }
        let budget = random_amount(&mut r);
        assert_eq!(splitting_gain(&p, budget, 7), splitting_gain(&p, budget, 7));
        assert_eq!(splitting_curve(&p, budget, 5), splitting_curve(&p, budget, 5));
    }
    let p = Params::meta_example();
    for a in [i64::MIN, -5, -1, 0] {
        assert_eq!(weight(&p, a).unwrap(), 0);
    }
    assert!(weight(&p, i64::MAX).unwrap() > 0);
}

#[test]
fn bad_parameters_are_errors_not_panics() {
    let mut p = Params::meta_example();
    p.base = 1;
    assert_eq!(weight(&p, 100), Err(ParamError::BaseBelowTwo));
    p.base = 0;
    assert_eq!(total_weight(&p, &[1]), Err(ParamError::BaseBelowTwo));
    p = Params { unit_cents: 0, ..Params::meta_example() };
    assert_eq!(splitting_gain(&p, 100, 2), Err(ParamError::UnitZero));
}

#[test]
fn splitting_report_matches_summing_the_shares_by_hand() {
    let mut r = Rng(6);
    for _ in 0..200 {
        let p = random_params(&mut r);
        let budget = 1 + random_amount(&mut r);
        let k = 1 + r.below(40);
        let rep = splitting_gain(&p, budget, k).unwrap();
        let shares: Vec<i64> = (0..k).map(|i| (budget as u64 / k + u64::from(i < budget as u64 % k)) as i64).collect();
        assert_eq!(shares.iter().sum::<i64>(), budget);
        assert_eq!(rep.split_total, total_weight(&p, &shares).unwrap());
        assert_eq!(rep.one_identity, weight(&p, budget).unwrap());
        assert_eq!(rep.gain, rep.split_total as i128 - rep.one_identity as i128);
        assert_eq!(rep.smallest_share_cents, *shares.iter().min().unwrap());
        assert_eq!(rep.eligible_identities, shares.iter().filter(|&&s| weight(&p, s).unwrap() > 0).count() as u64);
    }
}

#[test]
fn without_a_minimum_splitting_pays_and_with_one_it_stops_paying() {
    // 100 EUR, unit 1 EUR, log10: one identity weighs 3. Ten identities of 10 EUR weigh 2 each = 20.
    let free = Params::meta_example();
    let rep = splitting_gain(&free, 100_00, 10).unwrap();
    assert_eq!((rep.one_identity, rep.split_total), (3 * MICRO, 20 * MICRO));
    assert!(rep.gain > 0);
    assert_eq!(most_profitable_split(&free, 100_00, 50).unwrap().k, rep_best_k(&free, 100_00, 50));

    // Each identity must hold 50 EUR: two shares of 50 EUR are eligible, three of 33.33 are not.
    let costly = Params { min_amount_per_identity_cents: 50_00, ..free };
    assert_eq!(splitting_gain(&costly, 100_00, 2).unwrap().eligible_identities, 2);
    let three = splitting_gain(&costly, 100_00, 3).unwrap();
    assert_eq!((three.split_total, three.eligible_identities), (0, 0));
    assert_eq!(three.gain, -(3 * MICRO as i128));
    // Past the minimum the curve is dead flat at zero, so the best split is never past budget/min.
    let best = most_profitable_split(&costly, 100_00, 200).unwrap();
    assert!(best.k <= 2 && best.split_total > 0);
    for rep in splitting_curve(&costly, 100_00, 200).unwrap().iter().filter(|r| r.k > 2) {
        assert_eq!(rep.split_total, 0);
    }
}

fn rep_best_k(p: &Params, budget: i64, max_k: u64) -> u64 {
    splitting_curve(p, budget, max_k).unwrap().iter().max_by_key(|r| (r.split_total, std::cmp::Reverse(r.k))).unwrap().k
}
