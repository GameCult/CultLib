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
- Python CultNet raw client:
  Fetches schema catalogs, raw snapshots, shard catalogs, shard logs, and live
  database subscription changes.
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
- Python CultMesh discovery client:
  Fetches Verse catalogs and peer exchange responses from the live Python
  endpoint over CultNet frames.
- Python CultMesh facade:
  Exposes C#-matching client entrypoints for Verse discovery, peer exchange,
  raw CultNet client construction, local nodes, catalogs, sessions, streams,
  authority leases, local TCP node serving, and simulation fact commits.
- Python CultMesh local server:
  Serves a `CultMeshNode` plus Verse and peer catalogs over the same framed
  MessagePack request/response lane used by `CultNetRawClient` and
  `CultMeshDiscoveryClient`. It also supports live raw database subscriptions
  for snapshot, put, and delete notifications on a connected client stream.
  Its schema catalog includes both registered document payload schemas and
  shared wire-message descriptors for the CultNet/CultMesh messages it handles.
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
  those responses back through the shared CultNet replication path.
  Raw shard-scoped writes append a package-local mutation log that can be
  projected into schema-v0 shard-log responses for peer catch-up.
- Python CultMesh committed simulation facts:
  The live interop harness sends Python a simulation observation, receives a
  consensus candidate, then requests the committed `gamecult.mesh.simulation_fact`
  document back over the raw snapshot lane and checks its MessagePack slots.
- Python CultMesh prediction reconciliation:
  The live interop harness runs a Python CultMesh session client, writes a local
  prediction, pulls the live Python peer shard log, applies the authoritative
  entry, and verifies the session reports the record as reconciled.
- Python package health:
  `cultcache-py-verify --json` checks the public CultCache/CultNet/CultMesh
  export surface, `py.typed` markers, and benchmark sanity for Python-owned hot
  paths.
- Python/C# public cache baseline:
  `cultcache_py.compare_csharp` runs Python and C# `CultCache` upsert/get
  benchmarks with the same operation names and reports Python-to-C# ratios. Use
  `--samples` when a performance claim needs median evidence instead of a single
  noisy run.

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
  responses, and consume remote shard-log responses, while the live interop peer
  owns its harness mutation log.
