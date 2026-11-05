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
- Cap: the final holder may wield at most `cap` (default 1/5) of an estate's
  total weight delegated in from that estate. Over the cap, originators are
  kept in ascending id order until the next would overflow; the rest are
  trimmed and stay uncast. The ordering is a placeholder for Heimdall's ruling.
- Revoked delegations are inert. Resolution never raises the total: every
  seated person yields exactly one receipt, cast + uncast = total weight.
- Input order never matters; duplicate roster entries, duplicate direct votes
  and overlapping delegations from one person are input errors.
