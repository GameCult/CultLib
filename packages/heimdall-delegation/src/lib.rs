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
    /// Revoked delegations are inert.
    pub revoked: bool,
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
    /// The holder's cap for the originator's estate was reached.
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
    for d in delegations.iter().filter(|d| !d.revoked) {
        live.entry(d.from).or_default().push(d);
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
        receipts.push(Receipt { topic: topic.to_string(), originator: p.id, estate: p.estate, weight: p.weight, outcome });
    }

    // Cap: per (holder, originator estate), keep originators in ascending id
    // order until the next would overflow; the rest are trimmed.
    let mut estate_total: BTreeMap<EstateId, u128> = BTreeMap::new();
    for p in roster.values() {
        *estate_total.entry(p.estate).or_default() += p.weight as u128;
    }
    let mut used: BTreeMap<(PersonId, EstateId), u128> = BTreeMap::new();
    let mut full: BTreeSet<(PersonId, EstateId)> = BTreeSet::new();
    for r in receipts.iter_mut() {
        let Outcome::Delegated { holder, .. } = r.outcome else { continue };
        let key = (holder, r.estate);
        let limit = estate_total[&r.estate] * config.cap.num as u128 / config.cap.den as u128;
        let after = used.get(&key).copied().unwrap_or(0) + r.weight as u128;
        if full.contains(&key) || after > limit {
            full.insert(key);
            r.outcome = Outcome::Uncast(UncastReason::Trimmed);
        } else {
            used.insert(key, after);
        }
    }

    let mut tally = Tally::default();
    for r in &receipts {
        match &r.outcome {
            Outcome::Direct { choice } | Outcome::Delegated { choice, .. } => {
                *tally.by_choice.entry(choice.clone()).or_default() += r.weight;
                *tally.by_estate.entry(r.estate).or_default().entry(choice.clone()).or_default() += r.weight;
                tally.cast += r.weight;
            }
            Outcome::Uncast(_) => tally.uncast += r.weight,
        }
    }
    Ok(Resolution { receipts, tally })
}
