# CultNet Distributed Database Notes

CultNet's distributed database path is a typed realtime database over CultCache
documents. The design should borrow proven distributed-systems ideas without
copying their entire machine or laundering vague consistency through impressive
words.

## Prior Art To Keep

- RethinkDB changefeeds: subscriptions should emit old/new document views and
  support point, table/schema, and filtered watches.
- Firebase Realtime Database: client ergonomics should make realtime sync feel
  like the default path, but presence, disconnect handling, and server-side
  authority still need explicit ownership.
- Dynamo: partitioning, replication metadata, vector-ish causality, hinted
  recovery, and application-visible conflict resolution are useful. "Always
  writeable" is not the first CultNet promise.
- Raft: primary authority per shard is the first understandable consistency
  model. Leader election/log replication can come later if the cluster needs
  automatic failover.
- SWIM: peer membership and failure suspicion should be gossip-shaped when the
  mesh grows beyond small static clusters.
- CRDTs: offline multi-writer documents are valid only for types with declared,
  deterministic merge semantics.
- Hashgraph: gossip-about-gossip is interesting for compact event provenance
  and fair ordering research. CultNet should not adopt public-ledger machinery
  or crypto-token assumptions as a shortcut to database correctness.

## Current Cut

Implemented:

- `CultNetDatabase`: database-style facade over a `CultCache`
- primary-shard write checks
- typed R3 watch streams
- raw CultNet put/delete application through shard policy
- `CultNetDatabaseServer`: schema-v0 bridge for snapshot, put, and delete
- `Client` and `Server` schema-v0 dispatch hooks
- `CultNetHost.Database` and `CultNetHost.DatabaseServer`
- subscription request/cancel/change messages
- server-side live change fanout for schema/key-filtered subscriptions

Not implemented yet:

- shard catalog exchange
- membership/failure detection
- leader election or automatic shard failover
- replicated log
- declared CRDT merge policies

## Live Invariants

- CultCache owns document schema, identity, local indexes, and local diffing.
- CultNet owns transport, shard authority, remote mutation delivery, and
  subscription fanout.
- One primary owns writes for a shard until a stronger policy exists.
- Non-owner writes are rejected or explicitly forwarded. They are not silently
  applied.
- Raw snapshot/put/delete messages must pass through `CultNetDatabase`, not
  bypass shard policy.
- Realtime subscriptions publish domain changes, not storage envelopes.

## Next Coherent Slice

Add shard catalog exchange:

- advertise shard ids, schema filters, key ranges/prefixes, owner runtime ids,
  and epochs
- let clients ask any node where a write belongs
- reject stale-epoch writes explicitly
- prepare forwarding without making forwarding mandatory

That gives the subscription surface a real routing model before membership,
leader election, or replicated logs enter the room and start charging rent.
