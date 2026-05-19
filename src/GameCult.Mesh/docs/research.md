# CultMesh Research Notes

CultMesh follows the research trail captured in
`research/cultnet-distributed-database/summary.md`.

The current design keeps these borrowed pieces:

- RethinkDB-style changefeeds for reactive document subscriptions.
- Firebase-like realtime ergonomics, without pretending offline multi-writer
  conflicts are free.
- Dynamo-inspired shard metadata, hinted recovery, and anti-entropy direction.
- Raft-shaped primary authority per shard before automatic failover.
- SWIM-shaped membership later, once there is enough state worth gossiping.
- CRDTs only for document types with declared deterministic merge laws.
- Hashgraph's gossip-about-gossip as an event-provenance idea, not as a crypto
  ledger import.

## Live Model

CultMesh is currently primary-shard authority plus replica catch-up:

1. A primary shard accepts ordered writes.
2. Followers pull shard logs and apply committed entries.
3. Clients can predict their own input documents inside explicit authority
   scopes.
4. Witnesses publish immutable observations about simulation facts.
5. Nodes aggregate observations into deterministic consensus candidates.
6. Authoritative world state is still committed through shard logs.
7. Verses describe rule-bearing consensus graphs so compatible branches can
   share transport without pretending their rules or authority are identical.

## Next Research Cut

The next hard problem is not more vocabulary. It is durable authority:

- persist authoritative shard logs
- define compaction and snapshot fallback
- add observation fanout across peers
- add Verse discovery gossip and subscription policy
- add simulation rollback/resimulation helpers
- then revisit membership, failover, and consensus protocol depth
