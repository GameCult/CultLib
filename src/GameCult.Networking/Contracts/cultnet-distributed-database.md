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
- shard catalog request/response messages
- shard descriptors with owner runtime id, epoch, schema filters, key prefix,
  primary endpoints, replica endpoints, read replica endpoints, and region
- epoch-aware raw put/delete messages
- routing hints on shard authority errors
- injectable non-primary write forwarding policy
- schema-v0 write forwarder that dials advertised `cultnet://host:port`
  primary endpoints
- in-memory per-shard mutation logs with sequence numbers, epochs, mutation
  kind, schema id, key, and document references
- mutation-log catch-up by last seen sequence

Not implemented yet:

- membership/failure detection
- leader election or automatic shard failover
- wire-level replica log request/response messages
- durable log persistence and compaction
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

Add wire-level replica log catch-up:

- `cultnet.shard_log_request.v0`
- `cultnet.shard_log_response.v0`
- serialize accepted mutations as raw put/delete records
- let replicas ask for entries after a known sequence
- return an explicit resync-required error when the requested sequence has
  already been compacted away

The in-memory log shape now exists. The next useful layer is moving that log
over the wire. Membership and leader election should wait until replication
catch-up is boring.
