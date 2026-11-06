use heimdall_stake::*;
fn main() {
    let p = Params::meta_example();
    println!("100 EUR     -> {}", weight(&p, 100_00).unwrap() as f64 / MICRO as f64);
    println!("10,000 EUR  -> {}", weight(&p, 10_000_00).unwrap() as f64 / MICRO as f64);
    let costly = Params { min_amount_per_identity_cents: 50_00, ..p };
    for k in [1, 2, 3, 10] {
        let r = splitting_gain(&costly, 100_00, k).unwrap();
        println!("100 EUR in {k:>2} identities: {:.2} vs {:.2} (gain {:+.2})", r.split_total as f64 / 1e6, r.one_identity as f64 / 1e6, r.gain as f64 / 1e6);
    }
}
