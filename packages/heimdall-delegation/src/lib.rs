//! Heimdall's delegation resolver. See README.md for the rules.

use std::collections::{BTreeMap, BTreeSet};

pub type PersonId = u64;
pub type EstateId = u64;
pub type Topic = String;
pub type Choice = String;

/// A seated person. `weight` is supplied by the caller (no stake function yet).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Person {
    pub id: PersonId,
    pub estate: EstateId,
    pub weight: u64,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Scope {
    All,
    Only(BTreeSet<Topic>),
}

impl Scope {
    pub fn covers(&self, topic: &str) -> bool {
        match self {
            Scope::All => true,
            Scope::Only(s) => s.contains(topic),
        }
    }

    pub fn narrow(&self, other: &Scope) -> Scope {
        match (self, other) {
            (Scope::All, s) | (s, Scope::All) => s.clone(),
            (Scope::Only(a), Scope::Only(b)) => Scope::Only(a.intersection(b).cloned().collect()),
        }
    }

    fn overlaps(&self, other: &Scope) -> bool {
        match (self, other) {
            (Scope::All, _) | (_, Scope::All) => true,
            (Scope::Only(a), Scope::Only(b)) => a.intersection(b).next().is_some(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Delegation {
    pub from: PersonId,
    pub to: PersonId,
    pub scope: Scope,
    /// `Some(t)` means revoked at caller-clock time `t`. Revoked delegations are
    /// inert; the resolver never reads a clock and the caller owns the ledger.
    pub revoked: Option<u64>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Vote {
    pub voter: PersonId,
    pub topic: Topic,
    pub choice: Choice,
}

/// A fraction of an estate's total weight.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Ratio {
    pub num: u64,
    pub den: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Config {
    /// Maximum hops from originator to final holder; 0 disables delegation.
    pub max_depth: usize,
    /// Per-estate cap on what one final holder may wield from that estate.
    pub cap: Ratio,
    /// Fallback switch: refuse delegation across estates.
    pub same_estate_only: bool,
}

impl Default for Config {
    fn default() -> Self {
        Config { max_depth: 3, cap: Ratio { num: 1, den: 5 }, same_estate_only: false }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum InputError {
    DuplicatePerson(PersonId),
    DuplicateVote(PersonId),
    ZeroCapDenominator,
    /// Two live delegations from one person cover a common topic.
    OverlappingDelegations(PersonId),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UncastReason {
    /// The chain ended at someone who neither voted nor delegated this topic.
    NoVote,
    Cycle,
    DepthExceeded,
    /// The next delegate is not seated.
    NotSeated,
    /// `same_estate_only` and the next delegate is in another estate.
    CrossEstate,
    /// The cap scaled this originator's whole weight away (rounded to zero).
    Trimmed,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Outcome {
    Direct { choice: Choice },
    Delegated {
        holder: PersonId,
        holder_estate: EstateId,
        /// originator, delegates..., holder
        chain: Vec<PersonId>,
        /// Intersection of every link's scope.
        scope: Scope,
        choice: Choice,
    },
    Uncast(UncastReason),
}

/// Public receipt: one per seated person per topic. `estate` is the estate the
/// weight counts in (the originator's).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Receipt {
    pub topic: Topic,
    pub originator: PersonId,
    pub estate: EstateId,
    pub weight: u64,
    /// Part of `weight` that counted for the outcome's choice. `weight` for a
    /// direct vote, `0` for an uncast one, and for a delegated one `weight`
    /// scaled by the cap; `weight - counted` is uncast.
    pub counted: u64,
    /// Latest revocation time among the originator's own revoked delegations
    /// whose scope covers this topic. Those delegations took no part in the
    /// resolution; the receipt records that they existed.
    pub revoked_at: Option<u64>,
    pub outcome: Outcome,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Tally {
    pub by_choice: BTreeMap<Choice, u64>,
    /// estate -> choice -> weight, counted in the originator's estate.
    pub by_estate: BTreeMap<EstateId, BTreeMap<Choice, u64>>,
    pub cast: u64,
    pub uncast: u64,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Resolution {
    /// Sorted by originator id.
    pub receipts: Vec<Receipt>,
    pub tally: Tally,
}

enum Walk {
    Direct(Choice),
    Held { chain: Vec<PersonId>, scope: Scope, choice: Choice },
    Uncast(UncastReason),
}

/// Resolve one topic. Pure; the order of every input slice is irrelevant.
pub fn resolve(
    config: &Config,
    people: &[Person],
    delegations: &[Delegation],
    votes: &[Vote],
    topic: &str,
) -> Result<Resolution, InputError> {
    if config.cap.den == 0 {
        return Err(InputError::ZeroCapDenominator);
    }
    let mut roster: BTreeMap<PersonId, &Person> = BTreeMap::new();
    for p in people {
        if roster.insert(p.id, p).is_some() {
            return Err(InputError::DuplicatePerson(p.id));
        }
    }
    let mut direct: BTreeMap<PersonId, &Choice> = BTreeMap::new();
    for v in votes.iter().filter(|v| v.topic == topic) {
        if direct.insert(v.voter, &v.choice).is_some() {
            return Err(InputError::DuplicateVote(v.voter));
        }
    }
    let mut live: BTreeMap<PersonId, Vec<&Delegation>> = BTreeMap::new();
    let mut revoked_at: BTreeMap<PersonId, u64> = BTreeMap::new();
    for d in delegations {
        match d.revoked {
            None => live.entry(d.from).or_default().push(d),
            Some(t) if d.scope.covers(topic) => {
                let e = revoked_at.entry(d.from).or_insert(t);
                *e = (*e).max(t);
            }
            Some(_) => {}
        }
    }
    for (from, ds) in &live {
        for (i, a) in ds.iter().enumerate() {
            if ds[i + 1..].iter().any(|b| a.scope.overlaps(&b.scope)) {
                return Err(InputError::OverlappingDelegations(*from));
            }
        }
    }

    let walk = |origin: &Person| -> Walk {
        if let Some(c) = direct.get(&origin.id) {
            return Walk::Direct((*c).clone());
        }
        let mut chain = vec![origin.id];
        let mut scope = Scope::All;
        let mut cur = origin.id;
        loop {
            let Some(d) = live.get(&cur).and_then(|ds| ds.iter().find(|d| d.scope.covers(topic))) else {
                return Walk::Uncast(UncastReason::NoVote);
            };
            let Some(next) = roster.get(&d.to) else {
                return Walk::Uncast(UncastReason::NotSeated);
            };
            if config.same_estate_only && next.estate != origin.estate {
                return Walk::Uncast(UncastReason::CrossEstate);
            }
            if chain.contains(&next.id) {
                return Walk::Uncast(UncastReason::Cycle);
            }
            if chain.len() > config.max_depth {
                return Walk::Uncast(UncastReason::DepthExceeded);
            }
            chain.push(next.id);
            scope = scope.narrow(&d.scope);
            if let Some(c) = direct.get(&next.id) {
                return Walk::Held { chain, scope, choice: (*c).clone() };
            }
            cur = next.id;
        }
    };

    let mut receipts: Vec<Receipt> = Vec::with_capacity(roster.len());
    for p in roster.values() {
        let outcome = match walk(p) {
            Walk::Direct(choice) => Outcome::Direct { choice },
            Walk::Held { chain, scope, choice } => {
                let holder = *chain.last().expect("chain has a holder");
                Outcome::Delegated { holder, holder_estate: roster[&holder].estate, chain, scope, choice }
            }
            Walk::Uncast(r) => Outcome::Uncast(r),
        };
        let counted = if matches!(outcome, Outcome::Direct { .. } | Outcome::Delegated { .. }) { p.weight } else { 0 };
        receipts.push(Receipt {
            topic: topic.to_string(),
            originator: p.id,
            estate: p.estate,
            weight: p.weight,
            counted,
            revoked_at: revoked_at.get(&p.id).copied(),
            outcome,
        });
    }

    // Cap: per (final holder, originator estate) the holder may wield at most
    // floor(estate total * cap) of delegated weight (the holder's own direct
    // vote is not part of it). An over-cap group is scaled pro rata: every
    // delegator by the same factor limit / sum, in integer weight by largest
    // remainder. Only exact remainder ties fall to ascending originator id.
    let mut estate_total: BTreeMap<EstateId, u128> = BTreeMap::new();
    for p in roster.values() {
        *estate_total.entry(p.estate).or_default() += p.weight as u128;
    }
    let mut groups: BTreeMap<(PersonId, EstateId), Vec<usize>> = BTreeMap::new();
    for (i, r) in receipts.iter().enumerate() {
        if let Outcome::Delegated { holder, .. } = r.outcome {
            groups.entry((holder, r.estate)).or_default().push(i);
        }
    }
    for ((_, estate), members) in groups {
        let limit = estate_total[&estate] * config.cap.num as u128 / config.cap.den as u128;
        let sum: u128 = members.iter().map(|&i| receipts[i].weight as u128).sum();
        if sum <= limit {
            continue;
        }
        // members are in ascending originator id (receipts are), so a stable
        // sort by descending remainder leaves ids ascending within ties.
        let mut shares: Vec<(usize, u128, u128)> = members
            .iter()
            .map(|&i| {
                let scaled = receipts[i].weight as u128 * limit;
                (i, scaled / sum, scaled % sum)
            })
            .collect();
        let leftover = limit - shares.iter().map(|s| s.1).sum::<u128>();
        let mut order: Vec<usize> = (0..shares.len()).collect();
        order.sort_by(|&a, &b| shares[b].2.cmp(&shares[a].2));
        for &k in order.iter().take(leftover as usize) {
            shares[k].1 += 1;
        }
        for (i, counted, _) in shares {
            receipts[i].counted = counted as u64;
            if counted == 0 {
                receipts[i].outcome = Outcome::Uncast(UncastReason::Trimmed);
            }
        }
    }

    let mut tally = Tally::default();
    for r in &receipts {
        match &r.outcome {
            Outcome::Direct { choice } | Outcome::Delegated { choice, .. } => {
                *tally.by_choice.entry(choice.clone()).or_default() += r.counted;
                *tally.by_estate.entry(r.estate).or_default().entry(choice.clone()).or_default() += r.counted;
                tally.cast += r.counted;
                tally.uncast += r.weight - r.counted;
            }
            Outcome::Uncast(_) => tally.uncast += r.weight,
        }
    }
    Ok(Resolution { receipts, tally })
}
