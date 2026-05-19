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
- wire-level shard log request/response messages
- raw put/delete representation for accepted mutation-log entries
- replica-side shard log application with epoch checks, gap rejection, and
  idempotent replay
- shard log replicator that pulls non-primary shards from catalog-advertised
  primary endpoints
- schema-v0 shard log fetcher for `cultnet://host:port` endpoints
- restart-safe replica cursor store, including a local MessagePack file-backed
  implementation
- durable authoritative shard-log storage behind
  `ICultNetShardMutationLogStore`, including a per-shard MessagePack file
  implementation
- client-authority scopes for locally predicted input documents
- predicted and reconciled database change kinds for client-side prediction
- simulation witness observations for mesh-side opinions about frame facts
- deterministic consensus candidates derived from witness observations
- schema-v0 simulation observation and consensus candidate messages
- reactive simulation observation hub that receives witness gossip and emits
  consensus candidates
- server-side observation bridge that accepts observation messages and replies
  with current candidate messages

Not implemented yet:

- membership/failure detection
- leader election or automatic shard failover
- log compaction
- snapshot fallback for compacted log history
- peer-to-peer fanout for observation gossip and candidate propagation
- rollback/resimulation helpers for simulation frames
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
- The accepted shard log is stored in replica catch-up wire form. Object logs
  are for local inspection; wire logs are the durable replication authority.
- Realtime subscriptions publish domain changes, not storage envelopes.
- Client-owned input documents may be predicted locally only inside explicit
  `CultNetClientAuthorityScope` declarations.
- Predicted input state is not authoritative shared state. The shard log still
  decides the committed ordering and emits reconciliation when it corrects or
  confirms local prediction.
- Witness observations are immutable reports about a shard epoch, frame,
  subject, claim kind, and claim hash.
- Consensus candidates are derived from observations. They may inform the
  authoritative commit path, but they are not themselves committed world state
  until a shard owner writes them into the log.

## Next Coherent Slice

Add compaction-aware snapshot fallback and discovery fanout:

- define the compaction boundary that turns old log reads into snapshot reads
- return `ResyncRequired` when a replica asks for compacted history
- expose operator-facing defaults for durable shard-log paths in CultMesh nodes
- add Verse discovery over the schema-v0 mesh lane
- after that, add simulation-frame rollback/resimulation helpers around
  predicted input streams
- add peer-to-peer fanout for observation gossip and candidate propagation

The log is wire-readable, replicas can apply it explicitly, a pull loop can
drive non-primary shards from primary endpoints, and replica cursors can survive
restart. Client-owned input documents can now be predicted locally and
reconciled when the authoritative log arrives. Nodes can also aggregate witness
observations into deterministic consensus candidates for simulation facts like
"who shot first," and those reports now have schema-v0 wire contracts. The next
useful layer is making authoritative mutation logs durable and compaction-aware.
Membership and leader election should wait until basic replica catch-up is
boring. Authoritative mutation logs can now survive process restart through
`ICultNetShardMutationLogStore`; compaction and snapshot fallback are the next
places where the machine needs an honest boundary.
