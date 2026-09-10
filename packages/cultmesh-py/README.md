# cultmesh-py

`cultmesh-py` is the Python CultMesh runtime: a cache-backed local node,
reactive and observed documents, the branded `CultMesh` facade, Verse and peer
discovery clients, snapshot and observation fan-out, peer health monitoring,
game sessions with prediction reconciliation, committed simulation facts, the
local TCP/RUDP server, and a package daemon.

It depends on `cultcache-py` and `cultnet-py`, mirroring `cultmesh-rs` and
`cultmesh-ts`. The Verse catalog, peer exchange, authority lease, stream
negotiation, and simulation fact contracts are defined in
`cultnet_py.cultmesh_contracts` and re-exported here.

This is the package a Python process embeds to serve a Verse — the Brokkr
Blender add-on serves its editor mirror through `CultMesh.serve_node(...)`
from inside Blender's own interpreter.

## Example

```python
from datetime import UTC, datetime, timedelta

from cultcache_py import define_database_entry_type
from cultnet_py import (
    CultNetClientAuthorityScope,
    CultNetFileShardReplicaCursorStore,
    CultNetRawClient,
    CultNetSchemaShardLogFetcher,
    CultNetSchemaShardSnapshotFetcher,
    CultNetShardDescriptor,
    CultNetShardReplicator,
    CultNetShardReplicatorOptions,
)
from cultmesh_py import (
    CultMesh,
    CultMeshAuthorityLease,
    CultMeshGameSessionOptions,
    CultMeshHmacAuthorityLeaseVerifier,
    CultMeshNodeOptions,
    CultMeshSnapshotFanout,
    peer_exchange_request,
)

note_doc = define_database_entry_type(
    "mesh.note",
    [("body", 0), ("name", 1), ("kind", 2)],
    name="name",
    indexes={"kind": "kind"},
)
node = CultMesh.start_node(
    "mesh.cc",
    runtime_id="python-runtime",
    options=CultMeshNodeOptions(enable_durable_shard_logs=True),
)
node.database.register_document(note_doc)
unsubscribe = node.database.watch_record(note_doc, "note:1", lambda change: print(change.change_kind))
unsubscribe_named = node.database.watch_by_name(note_doc, "intro", lambda change: print(change.record_key))
unsubscribe_kind = node.database.watch_by_index(note_doc, "kind", "guide", lambda change: print(change.record_key))
node.database.put(note_doc, "note:1", {"body": "hello", "name": "intro", "kind": "guide"})
live_note = node.database.get_required(note_doc, "note:1")
put_message = node.database.put_raw_message(
    note_doc,
    "note:2",
    {"body": "wire me", "name": "wire", "kind": "interop"},
    shard_id="interop",
    shard_epoch=1,
)
snapshot_response = node.database.build_snapshot_response(schema_ids=[note_doc.catalog_entry().schema_id])
snapshot_wire = node.database.create_snapshot_response(schema_ids=[note_doc.catalog_entry().schema_id])
delete_message = node.database.delete_raw_message(note_doc, "note:2", shard_id="interop", shard_epoch=1)
shard_log_response = node.database.build_shard_log_response(shard_id="interop", shard_epoch=1)
shard_log_wire = node.database.create_shard_log_response(shard_id="interop", shard_epoch=1)

station_stock_doc = define_database_entry_type(
    "aetheria.station_stock",
    [("missiles", 0), ("coolant", 1)],
    schema_id="gamecult.aetheria.station_stock.v1",
)
station_stock_ui_doc = define_database_entry_type(
    "aetheria.station_stock.ui",
    [("missiles", 0), ("coolant", 1)],
    schema_id="gamecult.aetheria.station_stock.v1",
)
node.database.register_document(station_stock_doc)
node.database.put(station_stock_ui_doc, "station:starbridge:stock", {"missiles": 24, "coolant": 80})
stock = node.database.get_required(station_stock_ui_doc, "station:starbridge:stock")

peers = CultMesh.create_peer_catalog()
response = peers.create_response(peer_exchange_request("pex-1", verse_id="local"))
lease_verifier = CultMeshHmacAuthorityLeaseVerifier({"odin": b"shared-lease-key"})
lease_catalog = CultMesh.create_authority_lease_catalog(
    signature_verifier=lease_verifier.verify,
    require_verified_signatures=True,
)
lease_valid_from = datetime.now(UTC)
lease = lease_verifier.issue(CultMeshAuthorityLease(
    lease_id="lease:python-runtime",
    verse_id="local",
    peer_id="python-runtime",
    roles=("shard-primary",),
    valid_from=lease_valid_from,
    expires_at=lease_valid_from + timedelta(minutes=5),
    shard_ids=("interop",),
    issuer_runtime_id="odin",
))
lease_catalog.upsert(lease)
session = CultMesh.create_game_session(
    node,
    CultMeshGameSessionOptions(
        client_authority_scopes=(
            CultNetClientAuthorityScope("python-runtime", schema_ids=(note_doc.catalog_entry().schema_id,), key_prefix="input:python"),
        ),
    ),
)
server = CultMesh.serve_node(
    node,
    peer_catalog=peers,
    observation_hub=session.observation_hub,
    port=3075,
    max_snapshot_documents=1000,
    max_snapshot_bytes=4 * 1024 * 1024,
)
```

The same local server can be launched as a package daemon when another runtime
or operator process needs a CultNet endpoint without embedding Python glue:

```powershell
python -m cultmesh_py.daemon --runtime-id python-runtime --display-name "Python Runtime" --host 127.0.0.1 --port 3075 --cache-file mesh.cc --enable-durable-shard-logs --seed-interop-note --seed-shard-id interop --verse-id local --role shard-primary --ready-file mesh.ready.json
```

The ready file and stdout line are process readiness hints. The served state and
capability truth still live on the framed CultNet/CultMesh endpoint. Use
`--register-interop-note` to expose the package interop-note schema without
seeding a note, or `--seed-interop-note` to register it and publish one local
record for wire probes. Seeded records are committed through the raw mutation
path, so peers can read them through both snapshot and shard-log catch-up. Add
`--verse-id` and one or more `--role` values to publish the launched endpoint in
its own Verse and peer catalogs for discovery clients. When the daemon is
launched with `--cache-file`, `--enable-durable-shard-logs`, and
`--shard-log-file`, a later daemon process can rehydrate and serve the persisted
snapshot and shard log without reseeding.

`CultMeshPeerHealthMonitor` probes peer-card endpoints with `cultnet.hello.v0`
and preserves the runtime id, display name, document types, message versions,
and mutation contracts reported by the peer.

```python
client = CultMesh.create_verse_discovery_client("127.0.0.1", 3075)
verses = client.fetch_verses(transport_version="cultmesh.v0")
mesh_peers = client.fetch_peers(verse_id="python-interop", roles=["read-replica"])
client.sync_peer_catalog(peers, verse_id="python-interop", roles=["read-replica"])
client.fanout_peer_catalog(peers, verse_id="python-interop", roles=["read-replica"])
replica_notes = client.sync_documents(
    node.database,
    peers,
    verse_id="python-interop",
    roles=["read-replica"],
    documents=[(note_doc, "note:remote")],
)
snapshot_fanout = CultMeshSnapshotFanout(
    client,
    node.database,
    peers,
    verse_id="python-interop",
    roles=["read-replica"],
    documents=[(note_doc, "note:remote")],
    on_document=lambda note: print(note),
)
snapshot_fanout.sync_once()

raw_client = CultNetRawClient("127.0.0.1", 3075)
synced_note = node.database.sync_document(raw_client, note_doc, "note:remote")
synced_stock = CultMesh.sync_document(
    node,
    raw_client,
    station_stock_ui_doc,
    "station:starbridge:stock",
)
with CultMesh.subscribe_document(
    node,
    raw_client,
    station_stock_ui_doc,
    "station:starbridge:stock",
) as stock_subscription:
    stock = stock_subscription.sync_initial()
    next_change = stock_subscription.read_next_change()
node.database.sync_shard_log(raw_client, shard_id="interop", shard_epoch=1)
replicator = CultNetShardReplicator(
    node.database,
    CultNetShardReplicatorOptions(
        fetcher=CultNetSchemaShardLogFetcher(),
        snapshot_fetcher=CultNetSchemaShardSnapshotFetcher(),
        cursor_store=CultNetFileShardReplicaCursorStore("mesh.replica-cursors.msgpack"),
        poll_interval_seconds=1.0,
        on_error=lambda error: print(error),
    ),
)
replica_shard = CultNetShardDescriptor(
    shard_id="interop",
    owner_runtime_id="primary-runtime",
    epoch=1,
    schema_ids=(note_doc.catalog_entry().schema_id,),
    primary_endpoints=("cultnet://127.0.0.1:3075",),
)
replicator.pull_once(replica_shard)
replicator.start([replica_shard])
replicator.stop()
server.stop()

streams = CultMesh.create_stream_catalog()

facts = CultMesh.create_simulation_fact_committer(node)
committed = facts.commit({
    "shardId": "arena",
    "shardEpoch": 4,
    "frame": 100,
    "subjectId": "bob",
    "claimKind": "hit",
    "claimHash": "accepted-claim-hash",
    "witnessCount": 2,
    "supportWeight": 2.0,
    "totalWeight": 2.0,
    "confidence": 1.0,
    "hasQuorum": True,
})
prediction = session.predict(note_doc, "input:python:move", {"body": "predicted input"})
unsubscribe()
```

## Verify

`cultmesh_py.verify` checks the public exports and `py.typed` markers of all
three Python packages, smokes a local node over the wire, checks the served
capability truth, and runs the benchmark for sanity:

```powershell
python -m cultmesh_py.verify --json
```

## Tests

```powershell
$env:PYTHONPATH="$PWD\packages\cultcache-py\src;$PWD\packages\cultnet-py\src;$PWD\packages\cultmesh-py\src"
python -m unittest discover -s packages\cultmesh-py\tests
```

`packages\cultnet-ts\test\interop\cultnet-interop.test.ts` verifies the public
`CultMeshDiscoveryClient` can fetch typed Verse and peer descriptors from the
live Python peer. See
[docs/python-runtime-parity.md](../../docs/python-runtime-parity.md) for the
evidence map across the three Python packages.
