# Python Runtime Parity Map

This file is the current audit map for `cultcache-py` as a Python runtime for
CultCache, CultNet, and CultMesh. It is evidence, not confetti.

## Proven Surfaces

- CultCache v1 store parity:
  `packages/cultcache-ts/test/cult-cache.test.ts` includes Python in the shared
  TypeScript/Rust/C#/Python store matrix.
- CultNet schema-v0 live interop:
  `packages/cultnet-ts/test/interop/cultnet-interop.test.ts` runs a live
  TS/Rust/C#/Python peer ring.
  The Python interop peer filters shard-scoped raw snapshots by its live
  shard-log membership before stamping shard cursor metadata.
- Python CultNet raw client:
  Fetches schema catalogs, raw snapshots, shard catalogs, shard logs, and live
  database subscription changes.
- Python CultNet raw snapshot:
  Provides typed `CultNetRawSnapshotResponse` and `CultNetRawDocumentRecord`
  helpers plus `CultNetRawClient.fetch_snapshot_response(...)` for inspecting
  peer snapshots before handing them to the existing replication helpers.
  `apply_raw_document_record(...)`, `apply_raw_snapshot(...)`,
  `CultMeshDatabase.apply_snapshot_response(...)`, and sync paths accept typed
  records/responses while preserving the existing replication owner.
  `CultMeshDatabase.build_snapshot_response(...)` projects local envelopes into
  the same typed response shape; the older `create_snapshot_response(...)`
  remains the wire-dict compatibility projection.
- Python CultNet schema catalog:
  Exposes shared wire-message schema descriptors from the package surface so
  both the interop peer and CultMesh local server describe the CultNet/CultMesh
  messages they speak through the same catalog authority. Python callers can
  also ingest and filter remote schema catalog responses through typed
  `CultNetSchemaCatalog` and `CultNetSchemaDescriptor` helpers, including
  `CultNetRawClient.sync_schema_catalog(...)`.
- Python CultNet shard catalog:
  Provides typed `CultNetShardCatalog` and `CultNetShardDescriptor` helpers for
  ingesting, filtering, and emitting shard topology responses, plus
  `CultNetRawClient.sync_shard_catalog(...)` for peer topology discovery over
  the same framed CultNet pipe.
- Python CultNet shard log:
  Provides typed `CultNetShardLogResponse` and `CultNetShardLogEntry` helpers
  for inspecting mutation-log entries, resync pressure, and sequence cursors,
  including typed put-document and delete identity projections on individual
  entries while cache mutation remains owned by the existing replication
  helpers.
  `apply_shard_log_response(...)`, `CultMeshDatabase`, and
  `CultMeshGameSession` can consume typed shard-log responses directly for
  database sync and prediction reconciliation.
  `CultMeshDatabase.build_shard_log_response(...)` creates the typed response
  from the package-local mutation log and reports resync-required responses for
  unknown shards or stale epoch cursors; `create_shard_log_response(...)`
  remains the raw wire-dict compatibility projection.
- Python CultNet shard replication:
  Provides schema-v0 shard-log and shard-snapshot fetchers, a schema-v0
  write-forwarder for raw put/delete messages, plus a
  `CultNetShardReplicator` that tracks replica cursors, applies log responses
  through the existing database owner, runs explicit background pull loops for
  non-primary shards with advertised primaries, reports loop errors through a
  caller-owned callback, and recovers from compacted-history resync pressure by
  fetching a shard snapshot. Replica cursor storage can be in-memory or
  restart-safe through a local MessagePack cursor file.
- Python CultNet database subscription changes:
  Provides typed `CultNetDatabaseChange` parsing plus
  `CultNetDatabaseSubscription.read_next_change(...)` / `iter_changes(...)` for
  live raw database subscriptions. Put/update changes expose their contained
  raw document as a typed `CultNetRawDocumentRecord` while preserving the legacy
  dict-shaped `document` projection. Initial snapshot frames can be read as
  typed `CultNetRawSnapshotResponse` values through
  `read_next_snapshot_response(...)` / `iter_snapshot_responses(...)`, while
  raw message reads remain available for callers that own their own dispatch
  loop.
- Python CultNet witness artifacts:
  Provides typed `CultNetWitnessArtifactBundle` ownership for the C#-compatible
  8-slot witness artifact payload while preserving dict helpers for existing
  wire callers.
- Python CultMesh discovery client:
  Fetches Verse catalogs and peer exchange responses from live endpoints over
  CultNet frames. It can also parse `cultnet://host:port` endpoints, discover
  from endpoint lists, de-duplicate discovery targets, fan out one-shot
  discovery from endpoints already advertised in local Verse and peer catalogs,
  fan out simulation observations to peer-card endpoints, and upsert results
  into local catalogs with the same count-returning ergonomics as the C#
  discovery clients.
- Python CultMesh facade:
  Exposes C#-matching client entrypoints for Verse discovery, peer exchange,
  raw CultNet client construction, local nodes, catalogs, sessions, streams,
  authority leases, local TCP node serving, simulation fact commits, and the
  durable shard-log node option that attaches a file-backed authoritative log
  store beside the cache file by default.
- Python CultMesh authority leases:
  Lease objects own validity and peer/role/shard coverage checks; the catalog
  owns sorted listing, lookup, upsert, signature verification policy, and
  authorization delegation, matching the C# reference authority boundary.
  Callers can require verified signatures and use the packaged HMAC verifier to
  issue and verify canonical lease payloads keyed by issuer runtime.
- Python CultMesh local server:
  Serves a `CultMeshNode` plus Verse and peer catalogs over the same framed
  MessagePack request/response lane used by `CultNetRawClient` and
  `CultMeshDiscoveryClient`. It also supports live raw database subscriptions
  for snapshot, put, and delete notifications on a connected client stream.
  When given a `CultNetSimulationObservationHub`, it accepts
  `cultnet.simulation_observation.v0` messages and replies with current
  `cultnet.simulation_consensus_candidate.v0` messages through the same framed
  lane.
  Its schema catalog includes both registered document payload schemas and
  shared wire-message descriptors for the CultNet/CultMesh messages it handles.
  Hello and payload schema descriptors advertise the raw snapshot, put, delete,
  and shard-log mutation surfaces the server actually serves.
  Its shard catalog advertises the database-owned shard ids, schema ids, and
  latest shard epochs so discovery and shard-log catch-up agree on the same
  authority.
  Public mesh edges can set snapshot document and encoded-byte limits; oversized
  snapshot responses are rejected as `cultnet.error.v0` before the payload is
  sent.
- Python CultMesh reactive catalogs:
  Verse and peer catalogs expose sorted local views, `get(...)`, response
  application, and `watch(...)` callbacks for local discovery updates. Verse
  descriptors can evaluate transfer compatibility with the same transport/rules
  shape as the C# reference surface.
- Python CultMesh database facade:
  `node.database` owns the local typed document read/write/snapshot/sync surface
  so Python callers do not need to reach through the node composition wrapper.
  It includes global document helpers that delegate through the same singleton
  key invariant as `CultCache`.
  It also exposes local `watch(...)`, `watch_record(...)`, `watch_global(...)`,
  `watch_by_name(...)`, and `watch_by_index(...)` callbacks for database changes,
  including authoritative shard-log reconciliation applied through the session
  facade.
  Change notifications carry previous values when the database can observe the
  old record before local or replicated application.
  It can project local envelopes into schema-v0 raw snapshot responses and apply
  those responses back through the shared CultNet replication path. Typed
  builders own the local response shape and raw dict methods serialize that
  shape for compatibility with older callers and wire handlers.
  Shard-scoped raw snapshots filter by shard-log membership when the database
  has an authoritative log for the requested shard.
  Raw shard-scoped writes append a package-local mutation log that can be
  projected into schema-v0 shard-log responses for peer catch-up.
- Python CultMesh committed simulation facts:
  The live interop harness sends Python a simulation observation, receives a
  consensus candidate, then requests the committed `gamecult.mesh.simulation_fact`
  document back over the raw snapshot lane and checks its MessagePack slots.
  Python exposes typed `CultNetSimulationObservation` and
  `CultNetSimulationConsensusCandidate` objects for local consensus and
  game-session commit paths while preserving the schema-v0 dict projections
  used by the wire harness.
- Python CultMesh prediction reconciliation:
  The live interop harness runs a Python CultMesh session client, writes a local
  prediction, pulls the live Python peer shard log, applies the authoritative
  entry, and verifies the session reports the record as reconciled.
  Python sessions also expose pending prediction inspection, resimulation input
  listing, explicit rollback of one or all pending predictions, and
  authoritative-delete rollback when a shard log rejects speculative local
  input.
- Python CultMesh session watches:
  `CultMeshGameSession.watch_candidates(...)` and
  `watch_simulation_facts(...)` expose the same gameplay-facing observation and
  committed-fact watch points as the C# session facade.
- Python package health:
  `cultcache-py-verify --json` checks the public CultCache/CultNet/CultMesh
  export surface, `py.typed` markers, a live local CultMesh framed-wire smoke
  over hello/schema/snapshot/shard catalog/shard log, peer-advertised mutation
  contracts, payload schema wire contracts, and benchmark sanity for
  Python-owned hot paths.
- Python/C# public cache baseline:
  `cultcache_py.compare_csharp` runs Python and C# `CultCache` upsert/get
  benchmarks with the same operation names and reports median Python-to-C# ratios
  from three samples by default.

## Local Python Gates

```powershell
$env:PYTHONPATH="$PWD\packages\cultcache-py\src"
python -m unittest discover -s packages\cultcache-py\tests
python -m cultcache_py.verify --json
python -m cultcache_py.benchmark --records 1000 --json
python -m cultcache_py.compare_csharp --records 1000 --samples 3 --json
python -m pip wheel --no-deps -w $env:TEMP packages\cultcache-py
```

## Cross-Runtime Gate

```powershell
$env:PYTHON='C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
npx tsc -p packages\cultnet-ts\tsconfig.test.json --pretty false
node --test packages\cultnet-ts\dist-test\test\interop\cultnet-interop.test.js
```

## Still Not Claimed

- Python does not claim to match the C# runtime for raw throughput. It has a
  measured local baseline plus a shared public `CultCache` upsert/get comparison
  harness, not a performance victory certificate.
- Python does not own LiteNetLib security/session behavior; it speaks the
  schema-v0 interop lane used by the package harness.
- Python does not implement the full C# daemon/server stack. Its current role is
  package runtime, raw interop peer, local CultMesh session facade, and typed
  state participant.
- Python does not claim the full C# shard mutation-log service surface. It can
  create/apply raw snapshots, create/apply package-local raw shard-log
  responses, consume remote shard-log responses, and run pull-once replica
  catch-up with snapshot recovery, a file-backed replica cursor store, and
  schema-v0 write forwarding. It also has an explicit package-level background
  pull loop for caller-provided shard descriptors. Its authoritative shard-log
  store is file-backed, supports compaction watermarks, and can be attached by
  `CultMeshNodeOptions(enable_durable_shard_logs=True)`, but it is still
  package-local rather than the full C# daemon/server stack.
