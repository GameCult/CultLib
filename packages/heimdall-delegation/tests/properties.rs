use heimdall_delegation::*;
use std::collections::BTreeSet;

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
    fn shuffle<T>(&mut self, v: &mut [T]) {
        for i in (1..v.len()).rev() {
            v.swap(i, self.below(i as u64 + 1) as usize);
        }
    }
}

const TOPIC: &str = "budget";

#[derive(Clone)]
struct World {
    config: Config,
    people: Vec<Person>,
    delegations: Vec<Delegation>,
    votes: Vec<Vote>,
}

impl World {
    fn resolve(&self) -> Resolution {
        resolve(&self.config, &self.people, &self.delegations, &self.votes, TOPIC).unwrap()
    }
}

fn person(id: u64, estate: u64, weight: u64) -> Person {
    Person { id, estate, weight }
}
fn del(from: u64, to: u64) -> Delegation {
    Delegation { from, to, scope: Scope::All, revoked: None }
}
fn vote(voter: u64, choice: &str) -> Vote {
    Vote { voter, topic: TOPIC.into(), choice: choice.into() }
}
fn only(topics: &[&str]) -> Scope {
    Scope::Only(topics.iter().map(|t| t.to_string()).collect())
}
fn world(config: Config, people: Vec<Person>, delegations: Vec<Delegation>, votes: Vec<Vote>) -> World {
    World { config, people, delegations, votes }
}
fn loose(max_depth: usize) -> Config {
    Config { max_depth, cap: Ratio { num: 1, den: 1 }, same_estate_only: false }
}
fn outcome(r: &Resolution, id: u64) -> &Outcome {
    &r.receipts.iter().find(|x| x.originator == id).unwrap().outcome
}

/// Random world: ids 1..=n, 3 estates. Delegations may form cycles, cross
/// estates, point at unseated ids, carry scopes, and be revoked. At most one
/// delegation per person per scope bucket so inputs stay valid.
fn random_world(rng: &mut Rng) -> World {
    let n = 2 + rng.below(14);
    let people: Vec<Person> = (1..=n).map(|i| person(i, rng.below(3), 1 + rng.below(9))).collect();
    let mut delegations = vec![];
    for from in 1..=n {
        match rng.below(4) {
            0 => {}
            1 | 2 => delegations.push(Delegation {
                from,
                to: 1 + rng.below(n + 2), // n+1, n+2 are unseated
                scope: Scope::All,
                revoked: (rng.below(6) == 0).then(|| rng.below(1000)),
            }),
            _ => {
                // two disjoint scoped delegations
                delegations.push(Delegation { from, to: 1 + rng.below(n), scope: only(&["budget"]), revoked: None });
                delegations.push(Delegation { from, to: 1 + rng.below(n), scope: only(&["war", "trade"]), revoked: None });
            }
        }
    }
    let mut votes = vec![];
    for v in 1..=n {
        if rng.below(3) == 0 {
            votes.push(vote(v, ["yes", "no", "abstain"][rng.below(3) as usize]));
        }
    }
    let config = Config {
        max_depth: rng.below(5) as usize,
        cap: Ratio { num: 1 + rng.below(3), den: 1 + rng.below(5) },
        same_estate_only: rng.below(4) == 0,
    };
    World { config, people, delegations, votes }
}

fn worlds(n: u64) -> impl Iterator<Item = (u64, World)> {
    (1..=n).map(|seed| (seed, random_world(&mut Rng(seed * 7919))))
}

#[test]
fn order_independence_under_shuffled_input() {
    for (seed, w) in worlds(400) {
        let base = w.resolve();
        let mut rng = Rng(seed + 1000);
        for _ in 0..5 {
            let mut s = w.clone();
            rng.shuffle(&mut s.people);
            rng.shuffle(&mut s.delegations);
            rng.shuffle(&mut s.votes);
            assert_eq!(s.resolve(), base, "seed {seed}");
        }
    }
}

#[test]
fn conservation_one_receipt_per_person_and_no_inflation() {
    for (seed, w) in worlds(400) {
        let r = w.resolve();
        let total: u64 = w.people.iter().map(|p| p.weight).sum();
        assert_eq!(r.receipts.len(), w.people.len(), "seed {seed}");
        assert_eq!(r.tally.cast + r.tally.uncast, total, "seed {seed}");
        assert!(r.tally.cast <= total);
        assert_eq!(r.tally.by_choice.values().sum::<u64>(), r.tally.cast);
        for p in &w.people {
            let counted: u64 = r.tally.by_estate.get(&p.estate).map_or(0, |m| m.values().sum());
            let estate_total: u64 = w.people.iter().filter(|q| q.estate == p.estate).map(|q| q.weight).sum();
            assert!(counted <= estate_total, "seed {seed}: estate {} inflated", p.estate);
        }
        // Delegated weight counts in the delegator's estate, not the holder's.
        for rc in &r.receipts {
            let p = w.people.iter().find(|p| p.id == rc.originator).unwrap();
            assert_eq!((rc.estate, rc.weight), (p.estate, p.weight));
        }
    }
}

#[test]
fn direct_vote_overrides_any_delegation() {
    for (seed, w) in worlds(400) {
        let r = w.resolve();
        for v in &w.votes {
            assert_eq!(outcome(&r, v.voter), &Outcome::Direct { choice: v.choice.clone() }, "seed {seed}");
        }
    }
    // Same person with and without a delegation chain pointing elsewhere.
    let people = vec![person(1, 0, 1), person(2, 0, 1), person(3, 0, 1)];
    let votes = vec![vote(1, "yes"), vote(3, "no")];
    let with = world(loose(3), people.clone(), vec![del(1, 2), del(2, 3)], votes.clone()).resolve();
    let without = world(loose(3), people, vec![del(2, 3)], votes).resolve();
    assert_eq!(outcome(&with, 1), &Outcome::Direct { choice: "yes".into() });
    assert_eq!(with.receipts[0], without.receipts[0]);
}

#[test]
fn cycles_return_the_vote_to_every_originator_in_them() {
    let people: Vec<_> = (1..=4).map(|i| person(i, 0, 1)).collect();
    let r = world(loose(9), people.clone(), vec![del(1, 2), del(2, 3), del(3, 1), del(4, 1)], vec![]).resolve();
    for id in 1..=4 {
        assert_eq!(outcome(&r, id), &Outcome::Uncast(UncastReason::Cycle));
    }
    assert_eq!((r.tally.cast, r.tally.uncast), (0, 4));
    // Self-delegation is a cycle too.
    let r = world(loose(9), people, vec![del(1, 1)], vec![]).resolve();
    assert_eq!(outcome(&r, 1), &Outcome::Uncast(UncastReason::Cycle));
}

#[test]
fn delegated_chains_are_acyclic_and_within_depth() {
    for (seed, w) in worlds(400) {
        for rc in &w.resolve().receipts {
            if let Outcome::Delegated { chain, .. } = &rc.outcome {
                let uniq: BTreeSet<_> = chain.iter().collect();
                assert_eq!(uniq.len(), chain.len(), "seed {seed}");
                assert!(chain.len() - 1 <= w.config.max_depth, "seed {seed}");
                assert_eq!(chain[0], rc.originator);
            }
        }
    }
}

#[test]
fn depth_boundary_is_exact() {
    // 1 -> 2 -> 3 -> 4(votes): three hops.
    let people: Vec<_> = (1..=4).map(|i| person(i, 0, 1)).collect();
    let d = vec![del(1, 2), del(2, 3), del(3, 4)];
    let v = vec![vote(4, "yes")];
    let at = world(loose(3), people.clone(), d.clone(), v.clone()).resolve();
    assert!(matches!(outcome(&at, 1), Outcome::Delegated { holder: 4, .. }));
    let over = world(loose(2), people, d, v).resolve();
    assert_eq!(outcome(&over, 1), &Outcome::Uncast(UncastReason::DepthExceeded));
    assert!(matches!(outcome(&over, 2), Outcome::Delegated { holder: 4, .. }));
    assert!(matches!(outcome(&over, 3), Outcome::Delegated { holder: 4, .. }));
}

#[test]
fn zero_depth_disables_delegation() {
    let people = vec![person(1, 0, 1), person(2, 0, 1)];
    let r = world(loose(0), people, vec![del(1, 2)], vec![vote(2, "yes")]).resolve();
    assert_eq!(outcome(&r, 1), &Outcome::Uncast(UncastReason::DepthExceeded));
}

#[test]
fn scope_narrows_along_the_chain() {
    let people: Vec<_> = (1..=3).map(|i| person(i, 0, 1)).collect();
    let mut a = del(1, 2);
    a.scope = only(&["budget", "war"]);
    let mut b = del(2, 3);
    b.scope = only(&["budget", "trade"]);
    let r = world(loose(3), people, vec![a, b], vec![vote(3, "yes")]).resolve();
    match outcome(&r, 1) {
        Outcome::Delegated { scope, .. } => assert_eq!(scope, &only(&["budget"])),
        o => panic!("{o:?}"),
    }
    // Topic outside the second link's scope: chain ends at 2, who has no vote.
    let mut a = del(1, 2);
    a.scope = Scope::All;
    let mut b = del(2, 3);
    b.scope = only(&["trade"]);
    let people: Vec<_> = (1..=3).map(|i| person(i, 0, 1)).collect();
    let r = world(loose(3), people, vec![a, b], vec![vote(3, "yes")]).resolve();
    assert_eq!(outcome(&r, 1), &Outcome::Uncast(UncastReason::NoVote));
}

#[test]
fn every_delegated_scope_covers_the_topic() {
    for (seed, w) in worlds(400) {
        for rc in &w.resolve().receipts {
            if let Outcome::Delegated { scope, .. } = &rc.outcome {
                assert!(scope.covers(TOPIC), "seed {seed}");
            }
        }
    }
}

#[test]
fn revoked_delegation_equals_removed_delegation() {
    for (seed, w) in worlds(400) {
        let mut revoked = w.clone();
        for d in revoked.delegations.iter_mut().filter(|d| d.from % 2 == 0) {
            d.revoked = Some(d.revoked.unwrap_or(7));
        }
        let mut removed = w.clone();
        removed.delegations.retain(|d| d.from % 2 != 0);
        let (mut a, mut b) = (revoked.resolve(), removed.resolve());
        for r in a.receipts.iter_mut().chain(b.receipts.iter_mut()) {
            r.revoked_at = None;
        }
        assert_eq!(a, b, "seed {seed}");
    }
}

#[test]
fn unseated_delegate_returns_the_vote() {
    let r = world(loose(3), vec![person(1, 0, 4)], vec![del(1, 99)], vec![vote(99, "yes")]).resolve();
    assert_eq!(outcome(&r, 1), &Outcome::Uncast(UncastReason::NotSeated));
    assert_eq!(r.tally.uncast, 4);
}

#[test]
fn same_estate_only_refuses_cross_estate_hops() {
    let people = vec![person(1, 0, 1), person(2, 1, 1), person(3, 0, 1)];
    let d = vec![del(1, 2), del(3, 1)];
    let v = vec![vote(2, "yes")];
    let open = world(loose(3), people.clone(), d.clone(), v.clone()).resolve();
    assert!(matches!(outcome(&open, 1), Outcome::Delegated { holder: 2, holder_estate: 1, .. }));
    let mut c = loose(3);
    c.same_estate_only = true;
    let shut = world(c, people, d, v).resolve();
    assert_eq!(outcome(&shut, 1), &Outcome::Uncast(UncastReason::CrossEstate));
    assert_eq!(outcome(&shut, 3), &Outcome::Uncast(UncastReason::CrossEstate));
    // Delegated votes count in the delegator's estate even across estates.
    assert_eq!(open.tally.by_estate[&0]["yes"], 2);
    assert_eq!(open.tally.by_estate[&1]["yes"], 1);
}

#[test]
fn same_estate_switch_only_ever_removes_delegated_votes() {
    for (seed, w) in worlds(400) {
        let mut shut = w.clone();
        shut.config.same_estate_only = true;
        for rc in &shut.resolve().receipts {
            if let Outcome::Delegated { holder_estate, .. } = rc.outcome {
                assert_eq!(holder_estate, rc.estate, "seed {seed}");
            }
        }
    }
}

#[test]
fn cap_scales_every_delegator_by_the_same_factor() {
    // Estate 0: ten people of weight 1 plus a weight-10 whale (total 20);
    // cap 1/5 -> 4 from estate 0. Eleven delegators into holder 12 (estate 1):
    // sum 20 > 4, factor 1/5: the whale keeps 2, the ten keep 0.2 each; the
    // two leftover units go to the largest remainders (equal, so ids 1 and 2).
    let mut people: Vec<_> = (1..=10).map(|i| person(i, 0, 1)).collect();
    people.push(person(11, 0, 10));
    people.push(person(12, 1, 1));
    let d: Vec<_> = (1..=11).map(|i| del(i, 12)).collect();
    let mut c = loose(1);
    c.cap = Ratio { num: 1, den: 5 };
    let r = world(c, people, d, vec![vote(12, "yes")]).resolve();
    let counted: Vec<u64> = r.receipts.iter().filter(|x| x.estate == 0).map(|x| x.counted).collect();
    assert_eq!(counted, vec![1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2]);
    assert_eq!(outcome(&r, 3), &Outcome::Uncast(UncastReason::Trimmed));
    assert_eq!(r.tally.by_estate[&0]["yes"], 4);
    // The holder's own direct vote is outside the cap and untouched.
    assert_eq!(r.tally.by_estate[&1]["yes"], 1);
    assert_eq!((r.tally.cast, r.tally.uncast), (5, 16));
}

#[test]
fn partial_trim_keeps_the_delegation_and_reports_the_uncast_part() {
    let people = vec![person(1, 0, 6), person(2, 0, 4), person(3, 1, 1)];
    let mut c = loose(1);
    c.cap = Ratio { num: 1, den: 2 }; // limit 5 of estate 0 (total 10)
    let r = world(c, people, vec![del(1, 3), del(2, 3)], vec![vote(3, "yes")]).resolve();
    let rc = |id| r.receipts.iter().find(|x| x.originator == id).unwrap();
    assert_eq!((rc(1).counted, rc(2).counted), (3, 2)); // exactly 6/10 and 4/10 of 5
    assert!(matches!(rc(1).outcome, Outcome::Delegated { holder: 3, .. }));
    assert_eq!(r.tally.uncast, 5);
}

#[test]
fn cap_is_per_estate_and_applies_to_the_final_holder() {
    // Two estates of 5. Cap 1/5 -> 1 each. Holder 20 gets 1 from each estate.
    let mut people: Vec<_> = (1..=5).map(|i| person(i, 0, 1)).collect();
    people.extend((6..=10).map(|i| person(i, 1, 1)));
    people.push(person(20, 2, 1));
    let d: Vec<_> = (1..=10).map(|i| del(i, 20)).collect();
    let mut c = loose(1);
    c.cap = Ratio { num: 1, den: 5 };
    let r = world(c, people, d, vec![vote(20, "yes")]).resolve();
    for e in [0, 1] {
        let got: u64 = r.receipts.iter().filter(|x| x.estate == e).map(|x| x.counted).sum();
        assert_eq!(got, 1, "estate {e}");
    }
}

/// Worlds built to hit the cap: few holders, most people delegate straight
/// to them, varied weights, tight caps.
fn crowded_world(rng: &mut Rng) -> World {
    let n = 6 + rng.below(20);
    let holders = 1 + rng.below(3);
    let people: Vec<Person> = (1..=n).map(|i| person(i, rng.below(3), 1 + rng.below(12))).collect();
    let mut delegations = vec![];
    let mut votes = vec![];
    for i in 1..=n {
        if i <= holders {
            votes.push(vote(i, "yes"));
        } else if rng.below(8) != 0 {
            delegations.push(del(i, 1 + rng.below(holders)));
        }
    }
    let config = Config { max_depth: 1, cap: Ratio { num: 1, den: 2 + rng.below(6) }, same_estate_only: false };
    World { config, people, delegations, votes }
}

fn crowded(n: u64) -> impl Iterator<Item = (u64, World)> {
    (1..=n).map(|seed| (seed, crowded_world(&mut Rng(seed * 104729))))
}

fn groups(w: &World, r: &Resolution) -> std::collections::BTreeMap<(u64, u64), Vec<Receipt>> {
    let _ = w;
    let mut g: std::collections::BTreeMap<(u64, u64), Vec<Receipt>> = Default::default();
    for rc in &r.receipts {
        if let Outcome::Delegated { holder, .. } = rc.outcome {
            g.entry((holder, rc.estate)).or_default().push(rc.clone());
        }
    }
    g
}

fn limit(w: &World, estate: u64) -> u128 {
    let total: u128 = w.people.iter().filter(|p| p.estate == estate).map(|p| p.weight as u128).sum();
    total * w.config.cap.num as u128 / w.config.cap.den as u128
}

#[test]
fn trim_is_order_independent_under_shuffles() {
    let mut trimmed = 0;
    for (seed, w) in crowded(400) {
        let base = w.resolve();
        trimmed += base.receipts.iter().filter(|r| r.counted < r.weight).count();
        let mut rng = Rng(seed + 5000);
        for _ in 0..5 {
            let mut s = w.clone();
            rng.shuffle(&mut s.people);
            rng.shuffle(&mut s.delegations);
            rng.shuffle(&mut s.votes);
            assert_eq!(s.resolve(), base, "seed {seed}");
        }
    }
    assert!(trimmed > 500, "generator must actually exercise the cap, got {trimmed}");
}

#[test]
fn delegated_weight_never_exceeds_the_cap_and_fills_it_when_over() {
    for (seed, w) in worlds(400).chain(crowded(400)) {
        let r = w.resolve();
        for ((_, estate), members) in groups(&w, &r) {
            let sum: u128 = members.iter().map(|m| m.weight as u128).sum();
            let got: u128 = members.iter().map(|m| m.counted as u128).sum();
            let lim = limit(&w, estate);
            assert!(got <= lim, "seed {seed}: {got} > {lim}");
            if sum > lim {
                assert_eq!(got, lim, "seed {seed}: over-cap group must use exactly the cap");
            } else {
                assert_eq!(got, sum, "seed {seed}: under-cap group is untouched");
            }
        }
    }
}

#[test]
fn no_delegator_is_scaled_more_than_another() {
    for (seed, w) in crowded(400) {
        let r = w.resolve();
        for ((_, estate), members) in groups(&w, &r).into_iter().chain(trimmed_groups(&w, &r)) {
            let sum: u128 = members.iter().map(|m| m.weight as u128).sum();
            let lim = limit(&w, estate);
            if sum <= lim {
                continue;
            }
            for m in &members {
                // counted is the exact pro-rata share rounded down or up, never further.
                let exact = m.weight as u128 * lim;
                let (lo, hi) = (exact / sum, exact.div_ceil(sum));
                assert!((lo..=hi).contains(&(m.counted as u128)), "seed {seed}: {m:?}");
            }
            // Equal weights are scaled equally to within one unit of rounding,
            // and a unit of rounding goes to the lower originator id on a tie.
            for a in &members {
                for b in &members {
                    if a.weight == b.weight {
                        assert!(a.counted.abs_diff(b.counted) <= 1, "seed {seed}");
                        if a.counted < b.counted {
                            assert!(a.originator > b.originator, "seed {seed}: tie broke against ascending id");
                        }
                    }
                }
            }
        }
    }
}

/// Delegators whose whole share rounded to zero are `Uncast(Trimmed)`, so they
/// are not in `groups`; rebuild their groups by following the chain they would
/// have taken (depth 1: their sole live delegation).
fn trimmed_groups(w: &World, r: &Resolution) -> std::collections::BTreeMap<(u64, u64), Vec<Receipt>> {
    let mut g = groups(w, r);
    for rc in &r.receipts {
        if rc.outcome == Outcome::Uncast(UncastReason::Trimmed) {
            let to = w.delegations.iter().find(|d| d.from == rc.originator && d.revoked.is_none()).unwrap().to;
            g.entry((to, rc.estate)).or_default().push(rc.clone());
        }
    }
    g
}

#[test]
fn receipts_carry_the_revocation_time_of_the_originators_own_revoked_delegation() {
    let people = vec![person(1, 0, 1), person(2, 0, 1), person(3, 0, 1)];
    let mut a = del(1, 2);
    a.revoked = Some(40);
    let mut b = del(1, 3);
    b.revoked = Some(90);
    b.scope = only(&["budget"]);
    let mut c = del(1, 3);
    c.revoked = Some(500);
    c.scope = only(&["war"]); // other topic: not reported
    let r = world(loose(2), people, vec![a, b, c], vec![vote(3, "yes")]).resolve();
    let rc = |id| r.receipts.iter().find(|x| x.originator == id).unwrap();
    assert_eq!(rc(1).revoked_at, Some(90)); // latest covering revocation
    assert_eq!(outcome(&r, 1), &Outcome::Uncast(UncastReason::NoVote)); // and it stayed inert
    assert_eq!(rc(2).revoked_at, None);
    // A live delegation alongside an older revoked one still resolves.
    let people = vec![person(1, 0, 1), person(2, 0, 1)];
    let mut old = del(1, 2);
    old.revoked = Some(3);
    let r = world(loose(2), people, vec![old, del(1, 2)], vec![vote(2, "yes")]).resolve();
    assert!(matches!(outcome(&r, 1), Outcome::Delegated { holder: 2, .. }));
    assert_eq!(r.receipts[0].revoked_at, Some(3));
}

#[test]
fn invalid_input_is_an_error_not_a_guess() {
    let p = vec![person(1, 0, 1), person(1, 0, 1)];
    assert_eq!(resolve(&loose(1), &p, &[], &[], TOPIC), Err(InputError::DuplicatePerson(1)));
    let p = vec![person(1, 0, 1)];
    let v = vec![vote(1, "a"), vote(1, "b")];
    assert_eq!(resolve(&loose(1), &p, &[], &v, TOPIC), Err(InputError::DuplicateVote(1)));
    let mut a = del(1, 2);
    a.scope = only(&["budget"]);
    let b = del(1, 3);
    assert_eq!(resolve(&loose(1), &p, &[a, b], &[], TOPIC), Err(InputError::OverlappingDelegations(1)));
    let mut c = loose(1);
    c.cap.den = 0;
    assert_eq!(resolve(&c, &p, &[], &[], TOPIC), Err(InputError::ZeroCapDenominator));
}

#[test]
fn votes_on_other_topics_are_ignored() {
    let people = vec![person(1, 0, 1), person(2, 0, 1)];
    let other = Vote { voter: 2, topic: "war".into(), choice: "yes".into() };
    let r = resolve(&loose(2), &people, &[del(1, 2)], &[other], TOPIC).unwrap();
    assert_eq!(r.tally.cast, 0);
}
