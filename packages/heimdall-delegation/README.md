# heimdall-delegation

Heimdall's delegation resolver: given who is seated, who delegated to whom, and
who voted directly on a topic, it decides whose vote counts where, and publishes
a receipt for every seated person. Pure and deterministic; no I/O, no
dependencies. Entry point: `resolve` in `src/lib.rs`. Live: the resolver and its
property tests (`tests/properties.rs`). Not here: a stake function (the caller
supplies each person's `weight` until the estates' formula exists), storage, and
any CultMesh projection.

## Rules

- A person may delegate to any seated person. Delegates may re-delegate, up to
  `Config::max_depth` hops.
- A delegated vote counts in the **delegator's** estate.
- Scope narrows along a chain: topic `t` flows only if every link's scope
  contains `t` (the receipt carries the intersection).
- A direct vote on the topic overrides the voter's own delegation.
- Cycles, depth violations, unseated or (with `same_estate_only`) cross-estate
  delegates, and chains that end without a vote return the vote **uncast** to
  its originator. Nothing is re-routed.
- Cap (**placeholder**): per (final holder, originator estate) the holder may
  wield at most `cap` (default 1/5) of that estate's total weight. The holder's
  own direct vote is not counted against it. Over the cap, every delegator into
  that holder from that estate is scaled by the same factor `limit / sum`, in
  integer weight by largest remainder; only exact remainder ties fall to
  ascending originator id. A receipt's `counted` is its scaled weight and
  `weight - counted` is uncast; a share that rounds to zero is
  `Uncast(Trimmed)`. Cast + uncast = total weight.
- Revocation (**placeholder**): `Delegation::revoked` is `Some(time)` when
  revoked. A revoked delegation is inert; the resolver reads no clock and the
  caller owns the ledger. Each receipt carries `revoked_at`, the latest
  revocation time among the originator's own revoked delegations covering the
  topic (revocations further down a chain are not reported).
- Input order never matters; duplicate roster entries, duplicate direct votes
  and overlapping delegations from one person are input errors (**placeholder**: overlapping scopes are rejected, not resolved by precedence).

## Placeholders until the estates rule

Four choices are Heimdall's #swarm rulings of 2026-11-05 standing in for the
estates' own decision, and change if they rule otherwise: (1) the cap is
pro-rata, (2) the cap excludes the holder's own direct vote, (3) overlapping
scopes are an input error, (4) revocation is a flag with a time and the caller
owns the ledger.

## Prior art

Sources were fetched on 2026-11-05; claims below are what those pages say.

- **LiquidFeedback, cycles.** The platform treats a cycle as a non-problem: if
  everyone in a loop only delegates nobody votes, and if anyone in it acts
  directly their delegation is suspended, so the loop breaks
  ([Liquid Democracy Journal 3, "Circular Delegations: Myth or Disaster"](https://liquid-democracy-journal.org/issue/3/The_Liquid_Democracy_Journal-Issue003-02-Circular_Delegations_-_Myth_or_Disaster.html)).
  Delegations are per area and a single area's set must not form cycles
  ([Kling et al. 2015](https://ar5iv.arxiv.org/html/1503.07723)). *Consequence:*
  we keep a direct vote overriding the voter's own delegation, and a
  cycle with no direct vote returns every member's weight uncast to its
  originator rather than re-routing it. We do not forbid cycles at input time
  (the ledger is the caller's and may form them); we resolve them.
- **Kling, Kunegis, Hartmann, Strohmaier, Staab 2015** ([ICWSM](https://ojs.aaai.org/index.php/ICWSM/article/view/14618),
  [arXiv:1503.07723](https://arxiv.org/abs/1503.07723)), a study of
  LiquidFeedback in the German Pirate Party. Weight is power-law distributed
  (38 voters held more than 100 delegations); delegation is transitive; super-voters
  have high theoretical power but exercise less of it (mean exercised/theoretical
  ratio 0.34), and the paper argues potential power is the safer measure. It
  proposes no cap. *Consequence:* a cap is our own addition, justified by the
  potential-power argument, not by that paper; keep the cap a separate,
  swappable `Config` field so the estates can set it to 1/1 and turn it off.
  Transitive delegation with `max_depth` stays.
- **Gölz, Kahng, Mackenzie, Procaccia 2018** ([The Fluid Mechanics of Liquid
  Democracy](https://arxiv.org/abs/1808.01906), WINE 2018; TEAC 2021). With single
  delegation the maximum guru weight grows polynomially in the electorate;
  letting voters list more than one option and routing by confluent flow brings
  it to O(log log t). It handles cycles in general graphs by processing strongly
  connected components, and notes that people who delegate only among themselves
  have weight that reaches nobody. It considers caps and rejects them: voters
  can coordinate outside the system and copy a delegate's ballot, so a cap
  "would not alleviate the problem of super-voters, just reduce
  transparency" ([full text](https://ar5iv.arxiv.org/html/1808.01906)).
  *Consequences:* (a) a cap bounds what the platform records, not what a
  holder can persuade; the receipts' `counted`/uncast split keeps the trimmed
  part visible instead of hiding it, which is the transparency their objection
  turns on. (b) Pro-rata scaling treats equal delegators equally without a
  priority order a holder could game by who delegates first; ascending-id
  trimming did not. (c) Multi-option delegation is the research direction for
  reducing concentration without a cap; it is out of scope here and would need
  the same preference-vs-equal-treatment question the Liquid Democracy Journal
  piece raises about preferential delegation.
- **Not checked:** this section does not cover other liquid-democracy
  implementations (e.g. Loomio, Democracy Earth) or the cycle literature after
  2018.
