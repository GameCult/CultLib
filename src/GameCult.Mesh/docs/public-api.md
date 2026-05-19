# CultMesh Public API

Package: `GameCult.Mesh`

Brand: CultMesh

Namespace: `GameCult.Mesh`

## Entry Points

- `CultMesh.CreateNodeAsync(...)`
- `CultMesh.StartNodeAsync(...)`
- `CultMesh.CreateVerseCatalog()`
- `CultMesh.CreateClient(...)`
- `CultMesh.ConnectClient(...)`

`CultMeshNode` wraps the current local runtime pieces:

- `Cache`: the underlying `CultCache`
- `Server`: the underlying CultNet server
- `Database`: the distributed realtime database facade
- `DatabaseServer`: the schema-v0 database bridge

`CultMeshNodeOptions.EnableDurableShardLogs` attaches a file-backed
authoritative shard-log store when `DatabaseOptions.MutationLogStore` is not
already supplied. `ShardLogPath` can override the location; otherwise CultMesh
uses a `.cultmesh/shard-logs` directory beside the cache file.

The package home and primary entrypoint are `GameCult.Mesh` / `CultMesh`.
Some lower-level database and wire-contract types still retain `CultNet` names
because they are shared with the transport package. Treat those as plumbing,
not the brand surface.

## Core Behaviors

### Verses

`CultMeshVerseDescriptor` describes a rule-bearing consensus graph. It includes
transport compatibility, rules hash, authority model, discovery endpoints,
known authority runtimes, optional parent Verse id, and plugin requirements.

`CultMeshVerseCatalog` is the local reactive catalog for discovered Verses.
It can publish discovery updates and find compatible transfer targets.

`CultMesh.ServeVerseCatalog(node, catalog)` attaches schema-v0 Verse discovery
responses to a node. The wire contracts are
`cultmesh.verse_catalog_request.v0` and `cultmesh.verse_catalog_response.v0`.
Consumers can apply a response directly with
`CultMeshVerseCatalog.Upsert(CultMeshVerseCatalogResponseMessage)`.

### Shard Authority

Each shard has one primary writer for now. Non-primary writes are rejected or
explicitly forwarded. Stale epochs fail loudly.

### Reactive Documents

Consumers subscribe to typed document changes through `CultNetDatabase` watch
methods. Subscriptions receive domain changes rather than storage envelopes.

### Client Prediction

`CultNetClientAuthorityScope` declares input documents a runtime may predict.
`PutPredictedAsync` writes local state and emits `Predicted`. When the
authoritative log arrives, the database emits `Reconciled`.

### Replica Catch-Up

Shard logs can be requested over schema-v0 and applied by replicas. Replica
cursors can be stored with `ICultNetShardReplicaCursorStore`. Authoritative
shard logs can be persisted with `ICultNetShardMutationLogStore`; the file
implementation stores one MessagePack log per shard so catch-up survives a
primary restart. Compacted history returns an explicit resync requirement so a
replica can fall back to a shard-bounded snapshot before applying newer log
entries. Applying that snapshot replaces the local shard view and advances the
replica cursor to the snapshot's represented log sequence.

### Simulation Witness Consensus

`CultNetSimulationObservation` records what a node saw for a simulation fact.
`CultNetSimulationObservationHub` collects those observations and emits
`CultNetSimulationConsensusCandidate` updates.

Candidates are opinions. Final world state still belongs in the authoritative
shard log.
