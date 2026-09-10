# cultnet-py

`cultnet-py` is the Python CultNet runtime: schema-v0 MessagePack wire helpers,
4-byte big-endian framing, the raw client, database subscriptions, shard
catch-up and replication, simulation observations and consensus, witness
artifact bundles, the RUDP codec/session/socket transport, and the CultMesh
wire contracts that both a node and a bare peer have to agree on.

It depends on `cultcache-py` and `msgpack`; AES-GCM helpers on `CultNetSecret`
light up with the optional `crypto` extra. `cultmesh-py` builds on it. The
layering mirrors `cultnet-rs` and `cultnet-ts`.

## Example

```python
from datetime import UTC, datetime

from cultnet_py import (
    CultNetSecret,
    CultNetServerSecurityOptions,
    CultNetRawClient,
    CultNetSimulationObservation,
    CultNetWitnessArtifactBundle,
    apply_raw_snapshot,
    apply_shard_log_response,
    compute_simulation_claim_hash,
    database_subscribe,
    decode_frame,
    encode_frame,
    hello,
    parse_message,
    shard_catalog_request,
    simulation_observation,
    witness_artifact_bundle,
)

payload = hello(runtime_id="python-runtime").to_bytes()
message = parse_message(decode_frame(encode_frame(payload)))

client = CultNetRawClient("127.0.0.1", 3075)
catalog = client.fetch_schema_catalog(kinds=["wire_message"])
snapshot = client.fetch_snapshot_response(schema_ids=["cultnet.interop-note"])
shard_catalog = client.fetch_shard_catalog(schema_ids=["cultnet.interop-note"])
# With a local CultCache and the matching registered document definitions:
# applied = apply_raw_snapshot(cache, [note_doc], snapshot)

subscription = database_subscribe(subscription_id="ui", schema_ids=["cultnet.interop-note"])
shard_request = shard_catalog_request(message_id="shards", schema_ids=["cultnet.interop-note"])
shard_log = client.fetch_shard_log_response(shard_id="interop", shard_epoch=1, after_sequence=0)
# log_changes = apply_shard_log_response(cache, [note_doc], shard_log)
with client.subscribe_database(subscription_id="ui", schema_ids=["cultnet.interop-note"]) as live:
    initial_snapshot = live.read_next_snapshot_response()

claim_hash = compute_simulation_claim_hash("frame:42", "subject:player-1", "hit")
observation = simulation_observation(
    message_id="obs-1",
    witness_runtime_id="python-runtime",
    shard_id="interop",
    shard_epoch=1,
    frame=42,
    subject_id="player-1",
    claim_kind="hit",
    claim_hash=claim_hash,
)
typed_observation = CultNetSimulationObservation.from_wire(observation.to_wire())
witness = witness_artifact_bundle(
    bundle_id="bundle-1",
    witness_kind="interop-proof",
    captured_at="2026-06-13T00:00:00Z",
    subject={"documentType": "cultnet.interop-note", "subjectId": "note:python"},
    contracts=[{"role": "payload", "schemaId": "cultnet.interop-note"}],
    artifacts=[{"role": "log", "uri": "cultcache://bundle-1/log", "mediaType": "text/plain"}],
    provenance={"pipelineId": "interop", "runId": "run-1", "runtimeId": "python-runtime"},
)
typed_witness = CultNetWitnessArtifactBundle.from_wire(witness)
witness_payload = typed_witness.to_payload()

security = CultNetServerSecurityOptions.development()
token = CultNetSecret.create_session_token(
    "318fb4b6-ff5e-4c4f-b911-d81807de53a8",
    datetime(2035, 1, 1, tzinfo=UTC),
    security,
    session_version=1,
)
session = CultNetSecret.try_validate_session_token(token, security)
```

The raw-client request surface can ride RUDP when the peer advertises a
`rudp://` endpoint and shares the connection id for that lane;
`create_rudp_schema_transport(...)` builds that transport, and
`cultmesh_py.CultMesh.create_client(endpoint="rudp://...")` selects it from an
endpoint string.

## CultMesh wire contracts

`cultnet_py.cultmesh_contracts` carries the Verse catalog, peer exchange,
authority lease, stream negotiation, and committed simulation fact contracts.
They live here rather than in `cultmesh-py` for the same reason
`CultMeshPeerCard` lives in `cultnet-rs`: the interop peer has to speak them
without owning a node. `cultmesh_py` re-exports every one of them, so callers
that already import from there keep working.

## RUDP

`CultNetRudpSession` is the shared reliable/unreliable/sequenced/ordered
session over the RUDP packet codec, with fragmentation above
`max_fragment_bytes`, a bounded reliable window, reliable expiry, and a
bounded fragment reassembly map that evicts the oldest stranded set and counts
it in `fragment_sets_evicted` rather than refusing new payloads. The
socket-backed transport, reconnect policy/controller, and reconnect loop follow
the Rust and TypeScript entrypoint pattern.

## Interop peer

The peer can serve, dial, and probe the same raw-state interop lane used by the
TypeScript, Rust, and C# test peers, and answers Verse catalog and peer exchange
requests without a CultMesh node:

```powershell
python -m cultnet_py.interop_peer serve --runtime-id python-peer --runtime-kind python --display-name "Python Peer" --agent-id python-agent --advertise-host 127.0.0.1 --tcp-port 3075 --discovery-port 4075 --discovery-group 239.77.44.11 --schema-path ..\cultnet-ts\integration\contracts\cultnet.interop-note.schema.json
python -m cultnet_py.interop_peer dial --runtime-id python-client --runtime-kind python --display-name "Python Client" --agent-id python-client --target-host 127.0.0.1 --target-port 3075 --schema-path ..\cultnet-ts\integration\contracts\cultnet.interop-note.schema.json
python -m cultnet_py.interop_peer probe --runtime-id python-prober --discovery-port 4075 --discovery-group 239.77.44.11
```

`packages/cultnet-ts/test/interop/cultnet-interop.test.ts` drives it inside the
live TS/Rust/C#/Python peer ring.

## Performance baseline

The package ships a lightweight benchmark for Python-owned hot paths. In a
CultLib source checkout, it can also run the C# reference benchmark beside it:

```powershell
python -m cultnet_py.benchmark --records 1000 --json
python -m cultnet_py.compare_csharp --records 1000 --json
```

It reports slot-indexed `DatabaseEntry` encode/decode throughput, framed
CultNet MessagePack parse throughput, raw snapshot application into a
registered cache, and public `CultCache` upsert/get throughput. The C# compare
command also runs `packages/cultnet-py/tools/GameCult.Caching.Benchmark` and
reports median Python-to-C# ratios for the shared public cache operations. It
uses three samples by default; pass `--samples` when you need a different
evidence shape. Treat the numbers as a local baseline, not a claim that Python
matches the C# reference in every workload.

## Tests

```powershell
$env:PYTHONPATH="$PWD\packages\cultcache-py\src;$PWD\packages\cultnet-py\src"
python -m unittest discover -s packages\cultnet-py\tests
```

The suite runs without `cultmesh_py` on the path. See
[docs/python-runtime-parity.md](../../docs/python-runtime-parity.md) for the
evidence map across the three Python packages.
