use heimdall_stake::*;

const VECTORS: &str = include_str!("vectors/stake_golden.csv");

/// Pins `weight` and `total_weight` to values computed outside this crate (see the fixture header).
/// Split rows are the sybil argument in numbers: the same budget in k identities weighs more.
#[test]
fn golden_vectors() {
    let mut rows = 0;
    let mut split_rows = 0;
    for line in VECTORS.lines().filter(|l| !l.starts_with('#') && !l.trim().is_empty()) {
        let f: Vec<&str> = line.split(',').collect();
        let (base, unit, amount, k, expected): (u32, u64, i64, usize, u64) =
            (f[1].parse().unwrap(), f[2].parse().unwrap(), f[3].parse().unwrap(), f[4].parse().unwrap(), f[5].parse().unwrap());
        let p = Params { base, unit_cents: unit, floor_cents: 0, per_identity_cap: None, min_amount_per_identity_cents: 0 };
        let got = match f[0] {
            "single" => weight(&p, amount).unwrap(),
            "split" => {
                split_rows += 1;
                let (q, r) = (amount / k as i64, (amount % k as i64) as usize);
                let shares: Vec<i64> = (0..k).map(|i| q + i64::from(i < r)).collect();
                total_weight(&p, &shares).unwrap()
            }
            other => panic!("unknown vector kind {other}"),
        };
        assert_eq!(got, expected, "vector `{line}`");
        rows += 1;
    }
    assert!(rows >= 20 && split_rows >= 12, "fixture shrank: {rows} rows, {split_rows} split");
}
