# CultNet Distributed Database Research Summary

Purpose: ground CultNet's distributed realtime database design in prior art
before adding more machinery. The goal is a coherent local-first mesh database
over CultCache, not a hand-rolled distributed-systems costume party.

## Sources Stored Here

- `hashgraph-swirlds-tr-2016-01.pdf`
  - Source: <https://www.swirlds.com/downloads/SWIRLDS-TR-2016-01.pdf>
  - Topic: gossip-about-gossip, virtual voting, fair ordering, asynchronous BFT.
- `raft-extended.html`
  - Source: <https://yygcode.com/papers/consensus-raft-extended-version.html>
  - Topic: understandable consensus, leader election, log replication, safety.
- `raft-extended.pdf`
  - Source: <https://web.stanford.edu/~ouster/cgi-bin/papers/raft-extended.pdf>
  - Topic: canonical Raft paper copy.
- `dynamo-amazon-science.html`
  - Source: <https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store>
  - Topic: Dynamo publication page and abstract.
- `dynamo-sosp2007.pdf`
  - Source: <https://web.stanford.edu/class/cs244/papers/amazon-dynamo-sosp2007.pdf>
  - Topic: highly available key-value store, consistent hashing, versioning,
    quorums, hinted handoff, anti-entropy.
- `swim.pdf`
  - Source: <https://www.cs.cornell.edu/projects/quicksilver/public_pdfs/SWIM.pdf>
  - Topic: scalable weakly consistent process-group membership.
- `rethinkdb-changefeeds.html`
  - Source: <https://rethinkdb.com/docs/changefeeds/java/>
  - Topic: realtime query/changefeed ergonomics.
- `firebase-realtime-offline.html`
  - Source: <https://firebase.google.com/docs/database/web/offline-capabilities>
  - Topic: offline behavior, presence, server-side disconnect operations.
- `crdt-arxiv-1805.06358.html`
  - Source: <https://arxiv.org/abs/1805.06358>
  - Topic: conflict-free replicated data types and deterministic convergence.

## Design Takeaways

### RethinkDB

Keep: changefeeds as the product feel. Subscribers should receive document
changes continuously, with enough old/new context to render, reconcile, or
debug. Point subscriptions and filtered subscriptions are both first-class.

CultNet implication: database subscriptions should be explicit schema-v0
messages. The server should stream raw document changes that clients can apply
through the same CultCache reconciliation path as snapshots.

### Firebase Realtime Database

Keep: realtime sync and local/offline ergonomics. Presence and disconnect
behavior are database features, not application afterthoughts.

Defer: full offline writes. CultNet should not pretend arbitrary offline
multi-writer changes merge safely. Offline/local-first behavior needs declared
per-document conflict policy.

CultNet implication: add presence/disconnect records later as ordinary
CultCache documents with server authority, not as hidden transport state.

### Raft

Keep: the understandable authority model. One leader/primary owns ordered
writes for a shard. Decompose the problem into ownership, log/mutation
replication, and safety.

Defer: full automatic leader election and replicated logs until shard catalogs
and explicit epochs exist.

CultNet implication: the current primary-shard policy is the correct first
foundation. Every write should either hit the primary, be forwarded to the
primary, or fail with routing information.

### Dynamo

Keep: partitioning, replication metadata, vector-ish causality, hinted recovery,
anti-entropy, and application-visible conflicts.

Reject for now: "always writeable" semantics. That is attractive, expensive,
and easy to lie about.

CultNet implication: shard descriptors should grow into a shard catalog with
owner runtime id, epoch, schema/key ranges, and later replica/preference-list
metadata. Conflicts must surface as data, not disappear into last-writer-wins
unless a document explicitly chose that policy.

### SWIM

Keep: gossip-shaped membership once the mesh grows beyond a small static
cluster. SWIM separates failure detection from membership dissemination and
keeps per-node message load stable.

Defer: membership implementation until there is a shard catalog to disseminate.

CultNet implication: do not build peer discovery as a side-channel registry.
When it arrives, it should update membership and shard-catalog state together.

### CRDTs

Keep: CRDTs for documents whose merge law is explicit and deterministic.

Reject: generic automatic merge for arbitrary domain objects. That is a
language cop with a nicer hat.

CultNet implication: CRDT support belongs in schema metadata or document
contract metadata. A document type can opt into a known merge strategy; the
default distributed write policy remains primary authority.

### Hashgraph

Keep: gossip-about-gossip as an idea for compact event provenance and possible
fair ordering research. Virtual voting is interesting when every member sees
the same gossip history.

Reject for now: adopting hashgraph consensus as CultNet's core. The public
ledger/crypto smell is not the main issue; the issue is that CultNet does not
currently need asynchronous Byzantine total ordering to become a good mesh
database. Dragging that in now would make the machine harder to explain.

CultNet implication: if we later need decentralized event ordering, use a
dedicated design pass. Do not smuggle hashgraph metadata into ordinary document
replication because it sounds powerful.

## Current CultNet Direction

The coherent path is:

1. Primary-shard authority.
2. Explicit schema-v0 snapshot, put, delete, subscribe, unsubscribe, and change
   messages.
3. Shard catalog exchange with owner runtime id and epoch.
4. Optional forwarding from non-owner nodes to owners.
5. Membership/failure detection, likely SWIM-shaped.
6. Replication and failover, likely Raft-shaped per shard.
7. Optional CRDT policies for document types that deserve offline multi-writer
   semantics.
8. Optional gossip-history research if fair decentralized ordering becomes a
   real requirement.

## Live Invariants

- CultCache owns document identity, schema compatibility, local indexes, and
  reconciliation.
- CultNet owns transport, shard authority, subscriptions, and remote mutation
  delivery.
- Raw wire records must pass through `CultNetDatabase` before mutating local
  cache state.
- A write without authority is rejected or explicitly forwarded. It is never
  silently applied.
- Realtime change streams publish domain changes, not storage implementation
  details.
- Conflict policy must be declared before conflicting writes are accepted.

## Current Cut

Shard catalog exchange now exists:

- `cultnet.shard_catalog_request.v0`
- `cultnet.shard_catalog_response.v0`
- per-shard id, owner runtime id, epoch, schema ids, key prefix/range
- stale epoch rejection on writes
- routing error that tells clients where the primary lives when known
- injectable non-primary write forwarding policy
- concrete schema-v0 write forwarder for `cultnet://host:port` primary
  endpoints
- in-memory per-shard mutation logs and catch-up by last seen sequence
- wire-level shard mutation log catch-up with raw put/delete entries
- replica-side application of shard log responses with epoch checks, gap
  rejection, and idempotent replay
- background shard log replicator plus schema-v0 fetcher for primary endpoints
- restart-safe replica cursor store with a local MessagePack file
  implementation
- client authority scopes for predicted local input documents
- predicted/reconciled change events for client-side prediction
- simulation witness observations and deterministic consensus candidates
- schema-v0 simulation observation and candidate messages
- reactive observation hub for witness gossip and consensus candidates
- server-side observation bridge for observation messages and candidate replies

## Next Cut

Build durable authoritative mutation logs:

- persist per-shard primary logs
- define the snapshot fallback boundary for compacted history
- return explicit resync-required responses when requested history is gone
- then add simulation-frame rollback and resimulation helpers on top of
  predicted input streams
- add peer-to-peer fanout for observation gossip and candidate propagation

That is the next foundation needed before membership, Raft-style failover, or
CRDT policy work can be honest.
