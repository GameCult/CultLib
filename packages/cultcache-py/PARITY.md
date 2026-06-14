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
- Python CultMesh discovery client:
  Fetches Verse catalogs and peer exchange responses from the live Python
  endpoint over CultNet frames.
- Python CultMesh committed simulation facts:
  The live interop harness sends Python a simulation observation, receives a
  consensus candidate, then requests the committed `gamecult.mesh.simulation_fact`
  document back over the raw snapshot lane and checks its MessagePack slots.
- Python CultMesh prediction reconciliation:
  The live interop harness runs a Python CultMesh session client, writes a local
  prediction, pulls the live Python peer shard log, applies the authoritative
  entry, and verifies the session reports the record as reconciled.
- Python package health:
  `cultcache-py-verify --json` checks public exports, `py.typed` markers, and
  benchmark sanity for Python-owned hot paths.

## Local Python Gates

```powershell
$env:PYTHONPATH="$PWD\packages\cultcache-py\src"
python -m unittest discover -s packages\cultcache-py\tests
python -m cultcache_py.verify --json
python -m cultcache_py.benchmark --records 1000 --json
python -m pip wheel --no-deps -w $env:TEMP packages\cultcache-py
```

## Cross-Runtime Gate

```powershell
$env:PYTHON='C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
npx tsc -p packages\cultnet-ts\tsconfig.test.json --pretty false
node --test packages\cultnet-ts\dist-test\test\interop\cultnet-interop.test.js
```

## Still Not Claimed

- Python does not match the C# runtime for raw throughput. It has a measured
  baseline, not a performance victory certificate.
- Python does not own LiteNetLib security/session behavior; it speaks the
  schema-v0 interop lane used by the package harness.
- Python does not implement the full C# daemon/server stack. Its current role is
  package runtime, raw interop peer, local CultMesh session facade, and typed
  state participant.
