# cultcache-py

`cultcache-py` is the Python runtime package for CultCache, CultNet, and
CultMesh. Callers work with registered domain documents while the cache owns
schema identity, routing, globals, name lookups, indexes, and backing-store
envelopes.

It is intentionally small. It is not an ORM, a database, or distributed consensus wearing rented authority.

## Current Shape

- document types are registered explicitly
- `SingleFileMessagePackBackingStore` writes `cultcache.store.v1` snapshots:
  `[format, schemaCatalog, records]`
- records store raw MessagePack payload bytes under schema-catalog identity
- payloads decode only through the registered document definition for that type
- unknown persisted types fail closed
- global documents are singleton-style per type
- type-specific backing stores beat generic backing stores
- the JSONL store uses only the Python standard library for bootstrap consumers
- `define_database_entry_type(...)` emits Rust/C#-style slot-indexed
  MessagePack array payloads for cross-runtime `DatabaseEntry` contracts
- `cultnet_py` exposes schema-v0 MessagePack helpers and 4-byte big-endian
  frame helpers for raw document state, subscriptions, shard catch-up,
  simulation observations, and witness artifact bundles
- `cultmesh_py` exposes a cache-backed local node surface for Python tools

## Example

```python
from dataclasses import dataclass, asdict
from cultcache_py import CultCache, JsonLinesBackingStore, define_document_type

@dataclass
class Settings:
    theme: str
    retries: int

settings_doc = define_document_type(
    "settings",
    encode=lambda value: asdict(value),
    decode=lambda payload: Settings(**payload),
    global_document=True,
)

cache = (
    CultCache.builder()
    .register_document_type(settings_doc)
    .add_generic_store(JsonLinesBackingStore("state.cultcache.jsonl"))
    .build()
)

cache.pull_all_backing_stores()
cache.put_global(settings_doc, Settings(theme="ash", retries=3))
settings = cache.get_required_global(settings_doc)
```

## Public Surface

- `define_document_type(...)`
- `define_database_entry_type(...)`
- `database_entry_field(...)`
- `define_document_registry(...)`
- `CultCache.builder()`
- `register_document_type(...)`
- `register_registry(...)`
- `register_name_lookup(...)`
- `register_index(...)`
- `add_backing_store(...)`
- `add_generic_store(...)`
- `pull_all_backing_stores()`
- `get(...)`
- `get_required(...)`
- `get_all(...)`
- `get_key_by_name(...)`
- `get_by_name(...)`
- `get_key_by_index(...)`
- `get_by_index(...)`
- `get_global(...)`
- `get_required_global(...)`
- `put(...)`
- `put_envelopes(...)`
- `put_global(...)`
- `update(...)`
- `update_global(...)`
- `delete(...)`
- `delete_global(...)`
- `snapshot()`

## Persistence Model

Backing stores persist envelopes, not caller domain objects. The cache decodes payloads through registered document definitions and rejects unknown type discriminators. This keeps Python's dynamic runtime from turning persistence into an open polymorphic sewer with a cheerful docstring.

`JsonLinesBackingStore` is the dependency-free control-plane store. It rewrites an atomic JSONL snapshot and base64-encodes payload bytes. It is designed for compact state spines, settings, ledgers, and bootstrap surfaces.

`SingleFileMessagePackBackingStore` follows the shared CultCache v1 store shape
used by the TypeScript, Rust, and C# runtimes:

- top-level MessagePack value: `[format, schemaCatalog, records]`
- `format`: `cultcache.store.v1`
- catalog entries: `[schemaId, schemaName, schemaVersion, contentHash,
  canonicalSchemaJson, compatibleSchemaIds, members]`
- records: `[key, schemaId, storedAt, payload]`

Large corpora should use a sharded store or a real database. A single snapshot file is a scalpel, not a forklift.

## DatabaseEntry Slot Contract

For cross-runtime cache entries, use `define_database_entry_type(...)` instead
of the generic JSON formatter:

```python
from dataclasses import dataclass
from cultcache_py import define_database_entry_type

@dataclass
class Settings:
    theme: str
    retries: int = 0

settings_doc = define_database_entry_type(
    "settings",
    [
        ("theme", 0),
        ("retries", 1, 0),
    ],
    cls=Settings,
)
```

The payload is a MessagePack array. Field keys are durable slot indexes:

- key `0` writes array slot 0
- key `4` writes array slot 4
- unused slots are written as nil
- deleted fields should leave their slots reserved
- newly added fields should use new keys
- fields with defaults tolerate missing or nil slots when older payloads are read

That matches the Rust `#[derive(DatabaseEntry)]` formatter shape and the C#
`[Key(n)]` intent: schema evolution comes from stable field slots, not source
member order.

## CultNet And CultMesh

`cultnet_py` provides the Python schema-v0 wire helpers and an interop peer:

```python
from cultnet_py import (
    CultNetRawClient,
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
snapshot = client.fetch_snapshot(schema_ids=["cultnet.interop-note"])
shard_catalog = client.fetch_shard_catalog(schema_ids=["cultnet.interop-note"])
# With a local CultCache and the matching registered document definitions:
# applied = apply_raw_snapshot(cache, [note_doc], snapshot)

subscription = database_subscribe(subscription_id="ui", schema_ids=["cultnet.interop-note"])
shard_request = shard_catalog_request(message_id="shards", schema_ids=["cultnet.interop-note"])
shard_log = client.fetch_shard_log(shard_id="interop", shard_epoch=1, after_sequence=0)
# log_changes = apply_shard_log_response(cache, [note_doc], shard_log)
with client.subscribe_database(subscription_id="ui", schema_ids=["cultnet.interop-note"]) as live:
    initial_snapshot = live.read_next()
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
witness = witness_artifact_bundle(
    bundle_id="bundle-1",
    witness_kind="interop-proof",
    captured_at="2026-06-13T00:00:00Z",
    subject={"documentType": "cultnet.interop-note", "subjectId": "note:python"},
    contracts=[{"role": "payload", "schemaId": "cultnet.interop-note"}],
    artifacts=[{"role": "log", "uri": "cultcache://bundle-1/log", "mediaType": "text/plain"}],
    provenance={"pipelineId": "interop", "runId": "run-1", "runtimeId": "python-runtime"},
)
```

The peer can serve, dial, and probe the same raw-state interop lane used by the
TypeScript, Rust, and C# test peers:

```powershell
python -m cultnet_py.interop_peer serve --runtime-id python-peer --runtime-kind python --display-name "Python Peer" --agent-id python-agent --advertise-host 127.0.0.1 --tcp-port 3075 --discovery-port 4075 --discovery-group 239.77.44.11 --schema-path ..\cultnet-ts\integration\contracts\cultnet.interop-note.schema.json
python -m cultnet_py.interop_peer dial --runtime-id python-client --runtime-kind python --display-name "Python Client" --agent-id python-client --target-host 127.0.0.1 --target-port 3075 --schema-path ..\cultnet-ts\integration\contracts\cultnet.interop-note.schema.json
python -m cultnet_py.interop_peer probe --runtime-id python-prober --discovery-port 4075 --discovery-group 239.77.44.11
```

`cultmesh_py` includes a local cache-backed node, schema-v0 helpers for the
CultMesh Verse catalog and peer exchange wire messages, local authority lease
checks, stream transport negotiation, committed simulation fact documents, and
local prediction reconciliation:

```python
from cultcache_py import define_database_entry_type
from cultnet_py import CultNetClientAuthorityScope, CultNetRawClient
from cultmesh_py import CultMesh, CultMeshDiscoveryClient, CultMeshGameSessionOptions, peer_exchange_request

note_doc = define_database_entry_type("mesh.note", [("body", 0)])
node = CultMesh.start_node("mesh.cc", runtime_id="python-runtime")
node.register_document(note_doc)
node.put(note_doc, "note:1", {"body": "hello"})
live_note = node.get_required(note_doc, "note:1")
put_message = node.put_raw_message(note_doc, "note:2", {"body": "wire me"}, shard_id="interop", shard_epoch=1)
delete_message = node.delete_raw_message(note_doc, "note:2", shard_id="interop", shard_epoch=1)

peers = CultMesh.create_peer_catalog()
response = peers.create_response(peer_exchange_request("pex-1", verse_id="local"))

client = CultMeshDiscoveryClient("127.0.0.1", 3075)
verses = client.fetch_verses(transport_version="cultmesh.v0")
mesh_peers = client.fetch_peers(verse_id="python-interop", roles=["read-replica"])
client.sync_peer_catalog(peers, verse_id="python-interop", roles=["read-replica"])

raw_client = CultNetRawClient("127.0.0.1", 3075)
node.sync_snapshot(raw_client, schema_ids=[note_doc.catalog_entry().schema_id])
node.sync_shard_log(raw_client, shard_id="interop", shard_epoch=1)

streams = CultMesh.create_stream_catalog()

claim_hash = "accepted-claim-hash"
facts = CultMesh.create_simulation_fact_committer(node)
committed = facts.commit({
    "shardId": "arena",
    "shardEpoch": 4,
    "frame": 100,
    "subjectId": "bob",
    "claimKind": "hit",
    "claimHash": claim_hash,
    "witnessCount": 2,
    "supportWeight": 2.0,
    "totalWeight": 2.0,
    "confidence": 1.0,
    "hasQuorum": True,
})

session = CultMesh.create_game_session(
    node,
    CultMeshGameSessionOptions(
        client_authority_scopes=(
            CultNetClientAuthorityScope("python-runtime", schema_ids=(note_doc.catalog_entry().schema_id,), key_prefix="input:python"),
        ),
    ),
)
session_commits = session.submit_and_commit(observation.to_wire())
prediction = session.predict(note_doc, "input:python:move", {"body": "predicted input"})
```

## Wire Parity

The Python interop peer is `cultcache_py.interop`:

```powershell
python -m cultcache_py.interop write --file cache.cc --runtime-id python
python -m cultcache_py.interop read --file cache.cc
```

Current receipts:

- `packages/cultcache-ts/test/cult-cache.test.ts` includes Python in the shared
  CultCache v1 parity matrix with TypeScript, Rust, and C#.
- `packages/cultnet-ts/test/interop/cultnet-interop.test.ts` includes Python in
  the live TS/Rust/C#/Python schema-v0 peer ring: discovery, hello, schema
  catalog, wire-message catalog discovery, raw snapshot, raw document
  put/delete, mutation receipt, fire-command receipt, database subscription
  changes, shard catalog, shard log catch-up, simulation consensus candidates,
  committed simulation fact snapshots, and witness artifact bundle round-trips.
  The same test asks the Python peer for CultMesh Verse catalog and peer
  exchange responses over the CultNet pipe, then verifies the public
  `CultMeshDiscoveryClient` can fetch typed Python Verse and peer descriptors
  from that live endpoint. It also verifies `CultNetRawClient` can fetch the
  live Python peer's schema catalog, raw snapshot, and shard catalog.
- `packages/cultcache-py/tests/test_cultcache.py` covers Python CultMesh
  Verse catalog, peer exchange, authority lease, stream negotiation, CultNet
  helper shapes, raw snapshot/shard-log application, simulation claim hashing,
  consensus aggregation, game-session fact commits, local prediction
  reconciliation, committed simulation fact payload slots, and witness artifact
  bundle payload slots.
- `cultcache_py`, `cultnet_py`, and `cultmesh_py` ship `py.typed` markers so
  downstream type checkers can inspect the package surface instead of treating
  the runtime as an untyped xenos swamp.

## Performance Baseline

The package ships a lightweight benchmark for Python-owned hot paths:

```powershell
python -m cultcache_py.benchmark --records 1000 --json
```

It reports slot-indexed `DatabaseEntry` encode/decode throughput, framed
CultNet MessagePack parse throughput, and raw snapshot application into a
registered cache. Treat it as a local baseline, not a claim that Python matches
the C# reference in every workload.
