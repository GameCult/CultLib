from __future__ import annotations

import json
import socket
import tempfile
import threading
import unittest
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from uuid import uuid4

from cultcache_py import (
    CultCache,
    JsonLinesBackingStore,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
)
from cultcache_py.benchmark import run_benchmark
from cultcache_py.compare_csharp import DEFAULT_SAMPLE_COUNT, _median_result
from cultcache_py.interop import read_note, write_note
from cultcache_py.verify import verify
from cultnet_py.interop_peer import append_shard_log_put, build_state, raw_snapshot_response
from cultnet_py import (
    compute_simulation_claim_hash,
    CultNetDatabaseChange,
    CultNetClientAuthorityScope,
    CultNetRawClient,
    CultNetRawDocumentRecord,
    CultNetRawSnapshotResponse,
    CultNetSchemaCatalog,
    CultNetSchemaDescriptor,
    CultNetShardCatalog,
    CultNetShardDescriptor,
    CultNetShardLogEntry,
    CultNetShardLogResponse,
    CultNetSimulationConsensusOptions,
    CultNetSimulationConsensusCandidate,
    CultNetSimulationObservation,
    CultNetWitnessArtifactBundle,
    apply_raw_document_record,
    apply_raw_snapshot,
    apply_shard_log_response,
    database_subscribe,
    database_unsubscribe,
    decode_frame,
    decode_witness_artifact_bundle_payload,
    document_delete,
    document_put_raw,
    encode_frame,
    encode_witness_artifact_bundle_payload,
    hello,
    parse_message,
    schema_catalog_request,
    schema_document_map,
    shard_catalog_request,
    shard_log_request,
    simulation_observation,
    snapshot_request,
    wire_message_schema_catalog,
    wire_message_schema_descriptors,
    witness_artifact_bundle,
)
from cultmesh_py import create_node
from cultmesh_py import (
    CultMesh,
    CultMeshDatabase,
    CultMeshDatabaseChange,
    CultMeshGameSessionOptions,
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshSimulationFact,
    CultMeshAuthorityLease,
    CultMeshAuthorityLeaseCatalog,
    CultMeshDiscoveryClient,
    CultMeshPeerExchangeClient,
    CultMeshStreamCatalog,
    CultMeshStreamConsumerProfile,
    CultMeshStreamDescriptor,
    CultMeshStreamFrameHandle,
    CultMeshVerseCatalog,
    CultMeshVerseCompatibility,
    CultMeshVerseDescriptor,
    CultMeshVerseDiscoveryClient,
    peer_exchange_request,
    simulation_fact_document,
    verse_catalog_request,
)


@dataclass
class Item:
    name: str
    category: str
    value: int


def item_doc():
    return define_document_type(
        "item",
        encode=lambda item: asdict(item),
        decode=lambda raw: Item(**raw),
        name="name",
        indexes={"category": "category"},
    )


class CultCacheTests(unittest.TestCase):
    def test_round_trips_registered_documents(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            document = item_doc()
            cache = (
                CultCache.builder()
                .register_document_type(document)
                .add_generic_store(JsonLinesBackingStore(Path(tmp) / "cache.jsonl"))
                .build()
            )
            cache.pull_all_backing_stores()
            cache.put(document, "item:potion", Item(name="Potion", category="Consumable", value=50))

            loaded = (
                CultCache.builder()
                .register_document_type(document)
                .add_generic_store(JsonLinesBackingStore(Path(tmp) / "cache.jsonl"))
                .build()
            )
            loaded.pull_all_backing_stores()

            self.assertEqual(loaded.get_required(document, "item:potion").value, 50)
            self.assertEqual(loaded.get_key_by_name(document, "Potion"), "item:potion")
            self.assertEqual(loaded.get_by_index(document, "category", "Consumable").name, "Potion")

    def test_global_document_uses_singleton_key(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            cache = (
                CultCache.builder()
                .register_document_type(settings)
                .add_generic_store(JsonLinesBackingStore(Path(tmp) / "cache.jsonl"))
                .build()
            )
            cache.pull_all_backing_stores()
            cache.put_global(settings, {"theme": "ash"})
            self.assertEqual(cache.get_required_global(settings)["theme"], "ash")

    def test_update_and_delete(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            document = item_doc()
            cache = (
                CultCache.builder()
                .register_document_type(document)
                .add_generic_store(JsonLinesBackingStore(Path(tmp) / "cache.jsonl"))
                .build()
            )
            cache.pull_all_backing_stores()
            cache.put(document, "item:potion", Item(name="Potion", category="Consumable", value=50))
            cache.update(document, "item:potion", lambda item: Item(item.name, item.category, item.value + 5))
            self.assertEqual(cache.get_required(document, "item:potion").value, 55)
            cache.delete(document, "item:potion")
            self.assertIsNone(cache.get(document, "item:potion"))

    def test_incremental_lookup_updates_replace_stale_name_and_index_entries(self) -> None:
        document = item_doc()
        cache = CultCache()
        cache.register_document_type(document)

        cache.put(document, "item:potion", Item(name="Potion", category="Consumable", value=50))
        cache.put(document, "item:potion", Item(name="Elixir", category="Rare", value=80))

        self.assertIsNone(cache.get_key_by_name(document, "Potion"))
        self.assertIsNone(cache.get_key_by_index(document, "category", "Consumable"))
        self.assertEqual(cache.get_key_by_name(document, "Elixir"), "item:potion")
        self.assertEqual(cache.get_by_index(document, "category", "Rare").name, "Elixir")

    def test_incremental_lookup_delete_restores_duplicate_owner(self) -> None:
        document = item_doc()
        cache = CultCache()
        cache.register_document_type(document)

        cache.put(document, "item:first", Item(name="Potion", category="Consumable", value=50))
        cache.put(document, "item:second", Item(name="Potion", category="Consumable", value=60))
        self.assertEqual(cache.get_key_by_name(document, "Potion"), "item:second")
        self.assertEqual(cache.get_key_by_index(document, "category", "Consumable"), "item:second")

        cache.delete(document, "item:second")

        self.assertEqual(cache.get_key_by_name(document, "Potion"), "item:first")
        self.assertEqual(cache.get_key_by_index(document, "category", "Consumable"), "item:first")

    def test_database_entry_formatter_uses_slot_indexed_messagepack_array(self) -> None:
        try:
            import msgpack  # type: ignore
        except ModuleNotFoundError:
            self.skipTest("msgpack optional dependency is not installed")

        @dataclass
        class Settings:
            theme: str
            retries: int = 0

        document = define_database_entry_type(
            "settings",
            [
                ("theme", 0),
                ("retries", 2, 0),
            ],
            cls=Settings,
        )

        payload = document.encode_payload(Settings(theme="ash", retries=3))
        self.assertEqual(msgpack.unpackb(payload, raw=False), ["ash", None, 3])
        self.assertEqual(document.decode_payload(msgpack.packb(["ash"], use_bin_type=True)).retries, 0)

    def test_document_catalog_entry_is_cached_after_first_derivation(self) -> None:
        document = define_database_entry_type(
            "settings",
            [
                ("theme", 0),
                ("retries", 2, 0),
            ],
        )

        first = document.catalog_entry()
        second = document.catalog_entry()

        self.assertIs(first, second)
        self.assertEqual([member.slot for member in first.members], [0, 2])

    def test_messagepack_store_writes_v1_snapshot(self) -> None:
        try:
            import msgpack  # type: ignore
        except ModuleNotFoundError:
            self.skipTest("msgpack optional dependency is not installed")

        with tempfile.TemporaryDirectory() as tmp:
            document = define_database_entry_type(
                "settings",
                [("theme", 0)],
                schema_id="settings",
                schema_name="settings",
                schema_version="settings.v1",
            )
            store_path = Path(tmp) / "cache.msgpack"
            cache = (
                CultCache.builder()
                .register_document_type(document)
                .add_generic_store(SingleFileMessagePackBackingStore(store_path))
                .build()
            )
            cache.pull_all_backing_stores()
            cache.put(document, "app", {"theme": "ash"})

            raw = msgpack.unpackb(store_path.read_bytes(), raw=False)
            self.assertEqual(raw[0], "cultcache.store.v1")
            self.assertEqual(raw[1][0][0], "settings")
            self.assertEqual(raw[2][0][0], "app")
            self.assertEqual(raw[2][0][1], "settings")

    def test_interop_cli_helpers_round_trip_v1_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            store_path = str(Path(tmp) / "cache.msgpack")
            written = write_note(store_path, "python-test")
            loaded = read_note(store_path)
            self.assertEqual(loaded["documentId"], written["documentId"])
            self.assertEqual(loaded["authorRuntimeId"], "python-test")
            self.assertIn("interop", loaded["tags"])

    def test_benchmark_reports_core_hot_path_metrics(self) -> None:
        result = run_benchmark(8)
        metric_names = {metric["name"] for metric in result["metrics"]}
        self.assertEqual(result["records"], 8)
        self.assertEqual(result["wireMessageCount"], 8)
        self.assertEqual(metric_names, {
            "database_entry_encode",
            "database_entry_decode",
            "cultnet_frame_parse",
            "raw_snapshot_apply",
            "cache_upsert",
            "cache_get",
        })
        self.assertTrue(all(metric["opsPerSecond"] > 0 for metric in result["metrics"]))

    def test_compare_csharp_median_result_summarizes_samples(self) -> None:
        self.assertEqual(DEFAULT_SAMPLE_COUNT, 3)
        samples = [
            {
                "runtime": "python",
                "records": 3,
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 30.0, "opsPerSecond": 100.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 60.0, "opsPerSecond": 50.0},
                ],
            },
            {
                "runtime": "python",
                "records": 3,
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 10.0, "opsPerSecond": 300.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 20.0, "opsPerSecond": 150.0},
                ],
            },
            {
                "runtime": "python",
                "records": 3,
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 20.0, "opsPerSecond": 200.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 40.0, "opsPerSecond": 75.0},
                ],
            },
        ]

        summarized = _median_result(samples)

        self.assertEqual(summarized["sampleCount"], 3)
        self.assertEqual(summarized["metrics"][0]["name"], "cache_get")
        self.assertEqual(summarized["metrics"][0]["elapsedMs"], 20.0)
        self.assertEqual(summarized["metrics"][0]["opsPerSecond"], 200.0)
        self.assertEqual(summarized["metrics"][1]["opsPerSecond"], 75.0)

    def test_cache_put_without_backing_store_keeps_in_memory_value(self) -> None:
        document = define_database_entry_type(
            "bench.memory_item",
            [
                ("name", 0),
                ("value", 1, 0),
            ],
        )
        cache = CultCache()
        cache.register_document_type(document)

        cache.put(document, "item:one", {"name": "one", "value": 1})

        self.assertEqual(cache.get(document, "item:one"), {"name": "one", "value": 1})
        self.assertEqual(cache.get_required_envelope(document, "item:one").key, "item:one")

    def test_verify_reports_python_runtime_surface_health(self) -> None:
        result = verify(records=4)

        self.assertEqual(result["status"], "ok")
        self.assertEqual(
            {check["name"] for check in result["checks"]},
            {"public_exports", "typed_markers", "local_cultmesh_wire_smoke", "benchmark_sanity"},
        )

    def test_cultnet_schema_message_frame_round_trip(self) -> None:
        message = hello(runtime_id="python-test", supported_schema_versions=["cultnet.hello.v0"])
        parsed = parse_message(decode_frame(encode_frame(message.to_bytes())))
        self.assertEqual(parsed.schema_version, "cultnet.hello.v0")
        self.assertEqual(parsed.body["runtimeId"], "python-test")

    def test_cultnet_database_subscription_helpers_match_schema_v0_shape(self) -> None:
        subscribe = database_subscribe(
            message_id="sub-message",
            subscription_id="sub-1",
            schema_ids=["schema-a"],
            record_keys=["record-a"],
            include_snapshot=False,
        ).to_wire()
        self.assertEqual(subscribe["schemaVersion"], "cultnet.database_subscribe.v0")
        self.assertEqual(subscribe["messageId"], "sub-message")
        self.assertEqual(subscribe["subscriptionId"], "sub-1")
        self.assertEqual(subscribe["schemaIds"], ["schema-a"])
        self.assertEqual(subscribe["recordKeys"], ["record-a"])
        self.assertFalse(subscribe["includeSnapshot"])

        unsubscribe = database_unsubscribe(message_id="unsub-message", subscription_id="sub-1").to_wire()
        self.assertEqual(unsubscribe["schemaVersion"], "cultnet.database_unsubscribe.v0")
        self.assertEqual(unsubscribe["messageId"], "unsub-message")
        self.assertEqual(unsubscribe["subscriptionId"], "sub-1")

    def test_cultnet_database_change_parses_put_and_delete_shapes(self) -> None:
        put_change = CultNetDatabaseChange.from_wire({
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": "change-put",
            "subscriptionId": "sub-1",
            "changeKind": "added",
            "document": {
                "schemaId": "schema-note",
                "recordKey": "note:1",
                "payload": b"payload",
            },
        })
        self.assertEqual(put_change.schema_id, "schema-note")
        self.assertEqual(put_change.record_key, "note:1")
        self.assertIsInstance(put_change.raw_document, CultNetRawDocumentRecord)
        self.assertEqual(put_change.raw_document.payload, b"payload")
        self.assertEqual(put_change.to_wire()["document"]["recordKey"], "note:1")

        delete_change = CultNetDatabaseChange.from_wire({
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": "change-delete",
            "subscriptionId": "sub-1",
            "changeKind": "removed",
            "schemaId": "schema-note",
            "recordKey": "note:1",
        })
        self.assertIsNone(delete_change.document)
        self.assertIsNone(delete_change.raw_document)
        self.assertEqual(delete_change.schema_id, "schema-note")
        self.assertEqual(delete_change.to_wire()["recordKey"], "note:1")

    def test_cultnet_raw_put_helper_carries_message_id(self) -> None:
        put = document_put_raw(
            message_id="put-1",
            key="record-a",
            schema_id="schema-a",
            stored_at="2026-06-13T00:00:00Z",
            payload=b"payload",
            source_runtime_id="python-test",
            shard_id="interop",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(put["schemaVersion"], "cultnet.document_put_raw.v0")
        self.assertEqual(put["messageId"], "put-1")
        self.assertEqual(put["document"]["recordKey"], "record-a")
        self.assertEqual(put["document"]["sourceRuntimeId"], "python-test")
        self.assertEqual(put["shardId"], "interop")
        self.assertEqual(put["shardEpoch"], 1)

    def test_cultnet_catalog_and_snapshot_helpers_accept_filters(self) -> None:
        catalog = schema_catalog_request(
            message_id="catalog-1",
            include_schema_json=True,
            schema_ids=["schema-a"],
            kinds=["wire_message"],
        ).to_wire()
        self.assertEqual(catalog["schemaVersion"], "cultnet.schema_catalog_request.v0")
        self.assertEqual(catalog["messageId"], "catalog-1")
        self.assertTrue(catalog["includeSchemaJson"])
        self.assertEqual(catalog["schemaIds"], ["schema-a"])
        self.assertEqual(catalog["kinds"], ["wire_message"])

        snapshot = snapshot_request(
            message_id="snapshot-1",
            schema_ids=["schema-a"],
            record_keys=["record-a"],
            shard_id="interop",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(snapshot["schemaVersion"], "cultnet.snapshot_request.v0")
        self.assertEqual(snapshot["messageId"], "snapshot-1")
        self.assertEqual(snapshot["schemaIds"], ["schema-a"])
        self.assertEqual(snapshot["recordKeys"], ["record-a"])
        self.assertEqual(snapshot["shardId"], "interop")
        self.assertEqual(snapshot["shardEpoch"], 1)

    def test_cultnet_raw_snapshot_response_filters_and_roundtrips_records(self) -> None:
        response = CultNetRawSnapshotResponse.from_wire({
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": "snapshot-1",
            "shardId": "notes",
            "shardEpoch": 3,
            "shardLogSequence": 8,
            "documents": [
                CultNetRawDocumentRecord(
                    schema_id="schema-note",
                    record_key="note:1",
                    stored_at="2026-06-14T00:00:00Z",
                    payload=b"note",
                    source_runtime_id="python-test",
                    tags=("snapshot",),
                ).to_wire(),
                {
                    "schemaId": "schema-fact",
                    "recordKey": "fact:1",
                    "payloadEncoding": "messagepack",
                    "payload": b"fact",
                },
            ],
        })
        self.assertEqual(response.shard_id, "notes")
        self.assertEqual(response.shard_epoch, 3)
        self.assertEqual(response.shard_log_sequence, 8)
        self.assertEqual(response.documents[0].source_runtime_id, "python-test")
        self.assertEqual(response.filter(schema_ids=["schema-note"])[0].record_key, "note:1")
        self.assertEqual(response.filter(record_keys=["fact:1"])[0].schema_id, "schema-fact")
        self.assertEqual(response.to_wire()["documents"][0]["tags"], ["snapshot"])

    def test_cultnet_raw_client_fetches_schema_snapshot_and_shard_reads(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        received_versions: list[str] = []
        ready = threading.Event()
        server_error: list[BaseException] = []
        port_holder: list[int] = []

        def serve_requests() -> None:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", 0))
                    port_holder.append(server.getsockname()[1])
                    server.listen(4)
                    ready.set()
                    for _ in range(4):
                        connection, _ = server.accept()
                        with connection:
                            stream = connection.makefile("rwb")
                            request = msgpack.unpackb(read_frame(stream), raw=False)
                            received_versions.append(request["schemaVersion"])
                            if request["schemaVersion"] == "cultnet.schema_catalog_request.v0":
                                self.assertEqual(request["kinds"], ["wire_message"])
                                response = {"schemaVersion": "cultnet.schema_catalog_response.v0", "messageId": request["messageId"], "schemas": []}
                            elif request["schemaVersion"] == "cultnet.snapshot_request.v0":
                                self.assertEqual(request["shardId"], "interop")
                                response = {"schemaVersion": "cultnet.snapshot_response_raw.v0", "messageId": request["messageId"], "documents": []}
                            elif request["schemaVersion"] == "cultnet.shard_catalog_request.v0":
                                response = {
                                    "schemaVersion": "cultnet.shard_catalog_response.v0",
                                    "messageId": request["messageId"],
                                    "shards": [{
                                        "shardId": "interop",
                                        "ownerRuntimeId": "socket-test",
                                        "epoch": 1,
                                        "schemaIds": request["schemaIds"],
                                        "keyPrefix": "record-",
                                        "primaryEndpoints": ["cultnet://127.0.0.1:1"],
                                    }],
                                }
                            elif request["schemaVersion"] == "cultnet.shard_log_request.v0":
                                self.assertEqual(request["afterSequence"], 7)
                                response = {
                                    "schemaVersion": "cultnet.shard_log_response.v0",
                                    "messageId": request["messageId"],
                                    "shardId": request["shardId"],
                                    "shardEpoch": request["shardEpoch"],
                                    "entries": [],
                                    "resyncRequired": False,
                                }
                            else:
                                raise AssertionError(f"unexpected request {request['schemaVersion']}")
                            write_frame(stream, msgpack.packb(response, use_bin_type=True))
                            stream.flush()
            except BaseException as error:
                server_error.append(error)
                ready.set()

        thread = threading.Thread(target=serve_requests, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))
        self.assertFalse(server_error)

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        self.assertEqual(client.fetch_schema_catalog(kinds=["wire_message"])["schemaVersion"], "cultnet.schema_catalog_response.v0")
        typed_snapshot = client.fetch_snapshot_response(shard_id="interop", shard_epoch=1)
        self.assertEqual(typed_snapshot.shard_id, None)
        self.assertEqual(typed_snapshot.documents, ())
        shard_catalog = CultNetShardCatalog()
        synced_shards = client.sync_shard_catalog(shard_catalog, schema_ids=["schema-a"])
        self.assertEqual(synced_shards[0].shard_id, "interop")
        self.assertEqual(shard_catalog.get("interop"), synced_shards[0])
        shard_log = client.fetch_shard_log_response(shard_id="interop", shard_epoch=1, after_sequence=7)
        self.assertEqual(shard_log.shard_id, "interop")
        self.assertFalse(shard_log.resync_required)
        self.assertEqual(shard_log.last_sequence, 0)

        thread.join(2.0)
        self.assertFalse(server_error)
        self.assertEqual(received_versions, [
            "cultnet.schema_catalog_request.v0",
            "cultnet.snapshot_request.v0",
            "cultnet.shard_catalog_request.v0",
            "cultnet.shard_log_request.v0",
        ])

    def test_cultnet_database_subscription_reads_snapshot_and_change(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        document = define_database_entry_type(
            "sub.item",
            [
                ("name", 0),
                ("category", 1),
                ("value", 2, 0),
            ],
            cls=Item,
        )
        schema_id = document.catalog_entry().schema_id
        ready = threading.Event()
        server_error: list[BaseException] = []
        port_holder: list[int] = []
        received_versions: list[str] = []

        def serve_subscription() -> None:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", 0))
                    port_holder.append(server.getsockname()[1])
                    server.listen(1)
                    ready.set()
                    connection, _ = server.accept()
                    with connection:
                        stream = connection.makefile("rwb")
                        subscribe = msgpack.unpackb(read_frame(stream), raw=False)
                        received_versions.append(subscribe["schemaVersion"])
                        self.assertEqual(subscribe["subscriptionId"], "sub-1")
                        snapshot = {
                            "schemaVersion": "cultnet.snapshot_response_raw.v0",
                            "messageId": subscribe["messageId"],
                            "documents": [],
                        }
                        write_frame(stream, msgpack.packb(snapshot, use_bin_type=True))
                        stream.flush()

                        put = msgpack.unpackb(read_frame(stream), raw=False)
                        received_versions.append(put["schemaVersion"])
                        change = {
                            "schemaVersion": "cultnet.database_change_raw.v0",
                            "messageId": "change-1",
                            "subscriptionId": "sub-1",
                            "changeKind": "added",
                            "document": put["document"],
                        }
                        write_frame(stream, msgpack.packb(change, use_bin_type=True))
                        stream.flush()

                        unsubscribe = msgpack.unpackb(read_frame(stream), raw=False)
                        received_versions.append(unsubscribe["schemaVersion"])
            except BaseException as error:
                server_error.append(error)
                ready.set()

        thread = threading.Thread(target=serve_subscription, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))
        self.assertFalse(server_error)

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        put = document_put_raw(
            message_id="put-sub",
            key="item:sub",
            schema_id=schema_id,
            stored_at="2026-06-14T00:00:00Z",
            payload=document.encode_payload(Item("orb", "gear", 8)),
        )
        with client.subscribe_database(subscription_id="sub-1", schema_ids=[schema_id]) as subscription:
            snapshot = subscription.read_next_snapshot_response()
            subscription.send(put)
            change = subscription.read_next_change()

        thread.join(2.0)
        self.assertFalse(server_error)
        self.assertIsInstance(snapshot, CultNetRawSnapshotResponse)
        self.assertEqual(snapshot.message_id, "cultnet-python-subscribe")
        self.assertEqual(snapshot.to_wire()["schemaVersion"], "cultnet.snapshot_response_raw.v0")
        self.assertEqual(change.change_kind, "added")
        self.assertEqual(change.record_key, "item:sub")
        self.assertIsInstance(change.raw_document, CultNetRawDocumentRecord)
        self.assertEqual(change.raw_document.record_key, "item:sub")
        self.assertEqual(received_versions, [
            "cultnet.database_subscribe.v0",
            "cultnet.document_put_raw.v0",
            "cultnet.database_unsubscribe.v0",
        ])

    def test_cultnet_replication_helpers_apply_raw_snapshot_and_shard_log(self) -> None:
        document = define_database_entry_type(
            "replica.item",
            [
                ("name", 0),
                ("category", 1),
                ("value", 2, 0),
            ],
            cls=Item,
        )
        cache = CultCache()
        cache.register_document_type(document)
        schema_id = document.catalog_entry().schema_id

        snapshot = {
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": "snapshot-1",
            "documents": [
                {
                    "schemaId": schema_id,
                    "recordKey": "item:1",
                    "storedAt": "2026-06-13T00:00:00Z",
                    "payloadEncoding": "messagepack",
                    "payload": document.encode_payload(Item("sword", "gear", 3)),
                }
            ],
        }
        applied_snapshot = apply_raw_snapshot(cache, [document], snapshot)
        self.assertEqual(applied_snapshot[0].record_key, "item:1")
        self.assertEqual(cache.get_required(document, "item:1").value, 3)
        typed_snapshot = CultNetRawSnapshotResponse.from_wire(snapshot)
        direct_applied = apply_raw_document_record(cache, schema_document_map([document]), typed_snapshot.documents[0])
        self.assertEqual(direct_applied.schema_id, schema_id)
        self.assertEqual(direct_applied.record_key, "item:1")
        typed_snapshot_applied = apply_raw_snapshot(cache, [document], typed_snapshot)
        self.assertEqual(typed_snapshot_applied[0].record_key, "item:1")

        shard_log = {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log-1",
            "shardId": "interop",
            "shardEpoch": 1,
            "resyncRequired": False,
            "entries": [
                {
                    "sequence": 0,
                    "changeKind": "updated",
                    "put": {
                        "schemaVersion": "cultnet.document_put_raw.v0",
                        "messageId": "put-unknown",
                        "document": {
                            "schemaId": "replica.unknown.v1",
                            "recordKey": "unknown:1",
                            "storedAt": "2026-06-13T00:00:01Z",
                            "payloadEncoding": "messagepack",
                            "payload": document.encode_payload(Item("ignored", "unknown", 0)),
                        },
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                },
                {
                    "sequence": 1,
                    "changeKind": "updated",
                    "put": {
                        "schemaVersion": "cultnet.document_put_raw.v0",
                        "messageId": "put-1",
                        "document": {
                            "schemaId": schema_id,
                            "recordKey": "item:1",
                            "storedAt": "2026-06-13T00:00:01Z",
                            "payloadEncoding": "messagepack",
                            "payload": document.encode_payload(Item("sword", "gear", 12)),
                        },
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                },
                {
                    "sequence": 2,
                    "changeKind": "removed",
                    "delete": {
                        "schemaVersion": "cultnet.document_delete.v0",
                        "messageId": "delete-1",
                        "schemaId": schema_id,
                        "recordKey": "item:1",
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                },
            ],
        }
        applied_log = apply_shard_log_response(cache, [document], shard_log)
        self.assertEqual([change.change_kind for change in applied_log], ["updated", "removed"])
        self.assertIsNone(cache.get(document, "item:1"))

        cache.put(document, "item:1", Item("sword", "gear", 3))
        typed_shard_log = CultNetShardLogResponse.from_wire({
            **shard_log,
            "entries": shard_log["entries"][1:],
        })
        typed_applied_log = apply_shard_log_response(cache, [document], typed_shard_log)
        self.assertEqual([change.change_kind for change in typed_applied_log], ["updated", "removed"])
        self.assertIsNone(cache.get(document, "item:1"))

    def test_cultmesh_node_syncs_snapshot_and_shard_log_through_cultnet_client(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        document = define_database_entry_type(
            "mesh.sync_item",
            [
                ("name", 0),
                ("category", 1),
                ("value", 2, 0),
            ],
            cls=Item,
        )
        node = create_node(runtime_id="python-node")
        node.register_document(document)
        schema_id = document.catalog_entry().schema_id
        ready = threading.Event()
        server_error: list[BaseException] = []
        port_holder: list[int] = []

        def serve_requests() -> None:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", 0))
                    port_holder.append(server.getsockname()[1])
                    server.listen(2)
                    ready.set()
                    for _ in range(2):
                        connection, _ = server.accept()
                        with connection:
                            stream = connection.makefile("rwb")
                            request = msgpack.unpackb(read_frame(stream), raw=False)
                            if request["schemaVersion"] == "cultnet.snapshot_request.v0":
                                response = {
                                    "schemaVersion": "cultnet.snapshot_response_raw.v0",
                                    "messageId": request["messageId"],
                                    "documents": [
                                        {
                                            "schemaId": schema_id,
                                            "recordKey": "item:node",
                                            "storedAt": "2026-06-13T00:00:00Z",
                                            "payloadEncoding": "messagepack",
                                            "payload": document.encode_payload(Item("node", "mesh", 1)),
                                        }
                                    ],
                                }
                            elif request["schemaVersion"] == "cultnet.shard_log_request.v0":
                                response = {
                                    "schemaVersion": "cultnet.shard_log_response.v0",
                                    "messageId": request["messageId"],
                                    "shardId": request["shardId"],
                                    "shardEpoch": request["shardEpoch"],
                                    "resyncRequired": False,
                                    "entries": [
                                        {
                                            "sequence": 1,
                                            "changeKind": "updated",
                                            "put": {
                                                "schemaVersion": "cultnet.document_put_raw.v0",
                                                "messageId": "put-node",
                                                "document": {
                                                    "schemaId": schema_id,
                                                    "recordKey": "item:node",
                                                    "storedAt": "2026-06-13T00:00:01Z",
                                                    "payloadEncoding": "messagepack",
                                                    "payload": document.encode_payload(Item("node", "mesh", 9)),
                                                },
                                                "shardId": request["shardId"],
                                                "shardEpoch": request["shardEpoch"],
                                            },
                                        }
                                    ],
                                }
                            else:
                                raise AssertionError(f"unexpected request {request['schemaVersion']}")
                            write_frame(stream, msgpack.packb(response, use_bin_type=True))
                            stream.flush()
            except BaseException as error:
                server_error.append(error)
                ready.set()

        thread = threading.Thread(target=serve_requests, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))
        self.assertFalse(server_error)

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        snapshot_changes = node.sync_snapshot(client, schema_ids=[schema_id])
        log_changes = node.sync_shard_log(client, shard_id="mesh", shard_epoch=1)

        thread.join(2.0)
        self.assertFalse(server_error)
        self.assertEqual(snapshot_changes[0].record_key, "item:node")
        self.assertEqual(log_changes[0].change_kind, "updated")
        self.assertEqual(node.get_required(document, "item:node").value, 9)

    def test_cultmesh_node_emits_raw_put_and_delete_messages_for_local_writes(self) -> None:
        document = define_database_entry_type(
            "mesh.emit_item",
            [
                ("name", 0),
                ("category", 1),
                ("value", 2, 0),
            ],
            cls=Item,
        )
        node = create_node(runtime_id="python-node")
        node.register_document(document)

        put = node.put_raw_message(
            document,
            "item:emit",
            Item("wand", "gear", 4),
            message_id="put-emit",
            shard_id="mesh",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(node.get_required(document, "item:emit").name, "wand")
        self.assertEqual(put["schemaVersion"], "cultnet.document_put_raw.v0")
        self.assertEqual(put["messageId"], "put-emit")
        self.assertEqual(put["document"]["schemaId"], document.catalog_entry().schema_id)
        self.assertEqual(put["document"]["recordKey"], "item:emit")
        self.assertEqual(put["document"]["sourceRuntimeId"], "python-node")
        self.assertEqual(put["shardId"], "mesh")
        self.assertEqual(put["shardEpoch"], 1)

        delete = node.delete_raw_message(
            document,
            "item:emit",
            message_id="delete-emit",
            shard_id="mesh",
            shard_epoch=1,
        ).to_wire()
        self.assertIsNone(node.get(document, "item:emit"))
        self.assertEqual(delete["schemaVersion"], "cultnet.document_delete.v0")
        self.assertEqual(delete["messageId"], "delete-emit")
        self.assertEqual(delete["schemaId"], document.catalog_entry().schema_id)
        self.assertEqual(delete["recordKey"], "item:emit")
        self.assertEqual(delete["shardId"], "mesh")
        self.assertEqual(delete["shardEpoch"], 1)

    def test_cultnet_document_delete_helper_matches_schema_v0_shape(self) -> None:
        message = document_delete(
            message_id="delete-1",
            schema_id="schema-a",
            record_key="record-a",
            shard_id="interop",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(message["schemaVersion"], "cultnet.document_delete.v0")
        self.assertEqual(message["messageId"], "delete-1")
        self.assertEqual(message["schemaId"], "schema-a")
        self.assertEqual(message["recordKey"], "record-a")
        self.assertEqual(message["shardId"], "interop")
        self.assertEqual(message["shardEpoch"], 1)

    def test_cultnet_wire_catalog_describes_python_handled_messages(self) -> None:
        descriptors = wire_message_schema_descriptors(include_schema_json=True)
        by_version = {descriptor["schemaVersion"]: descriptor for descriptor in descriptors}
        for schema_version in [
            "cultnet.document_delete.v0",
            "cultnet.database_change_raw.v0",
            "cultnet.shard_log_response.v0",
            "cultnet.simulation_consensus_candidate.v0",
            "cultmesh.peer_exchange_response.v0",
        ]:
            self.assertIn(schema_version, by_version)
            self.assertEqual(by_version[schema_version]["kind"], "wire_message")
            self.assertIn("cultnet.schema.v0", by_version[schema_version]["wireContracts"])
            self.assertIn(schema_version, by_version[schema_version]["schemaJson"])
            self.assertEqual(len(by_version[schema_version]["contentHash"]), 64)

        delete_schema = json.loads(by_version["cultnet.document_delete.v0"]["schemaJson"])
        self.assertEqual(
            delete_schema["required"],
            ["schemaVersion", "messageId", "schemaId", "recordKey"],
        )
        self.assertEqual(delete_schema["properties"]["recordKey"]["type"], "string")

        change_schema = json.loads(by_version["cultnet.database_change_raw.v0"]["schemaJson"])
        self.assertEqual(change_schema["properties"]["changeKind"]["enum"], ["added", "updated", "removed"])
        self.assertIn("document", change_schema["properties"])
        self.assertIn("schemaId", change_schema["properties"])
        self.assertIn("recordKey", change_schema["properties"])

        log_schema = json.loads(by_version["cultnet.shard_log_response.v0"]["schemaJson"])
        self.assertIn("entries", log_schema["required"])
        self.assertIn("resyncRequired", log_schema["required"])
        self.assertIn("compactedThrough", log_schema["properties"])

        hello_schema = json.loads(by_version["cultnet.hello.v0"]["schemaJson"])
        mutation_contract = hello_schema["properties"]["supportedMutationContracts"]["items"]
        self.assertEqual(mutation_contract["type"], "object")
        self.assertIn("operations", mutation_contract["properties"])

        consensus_schema = json.loads(by_version["cultnet.simulation_consensus_candidate.v0"]["schemaJson"])
        for required_field in ["witnessCount", "supportWeight", "totalWeight", "hasQuorum", "confidence"]:
            self.assertIn(required_field, consensus_schema["required"])

    def test_cultnet_schema_catalog_applies_filters_and_responses(self) -> None:
        catalog = wire_message_schema_catalog(include_schema_json=True)
        descriptor = catalog.get("https://github.com/GameCult/cultnet-ts/contracts/cultnet.document-put-raw.schema.json")
        self.assertIsNotNone(descriptor)
        assert descriptor is not None
        self.assertEqual(descriptor.kind, "wire_message")
        self.assertEqual(descriptor.schema_version, "cultnet.document_put_raw.v0")
        self.assertIsNotNone(descriptor.schema_json)

        filtered = catalog.list(kinds=["wire_message"], schema_ids=[descriptor.schema_id])
        self.assertEqual(filtered, [descriptor])

        response = catalog.create_response(
            message_id="catalog-response",
            include_schema_json=False,
            schema_ids=[descriptor.schema_id],
            kinds=["wire_message"],
        )
        self.assertEqual(response["schemaVersion"], "cultnet.schema_catalog_response.v0")
        self.assertNotIn("schemaJson", response["schemas"][0])

        remote = CultNetSchemaCatalog()
        applied = remote.apply_response({
            "schemaVersion": "cultnet.schema_catalog_response.v0",
            "messageId": "remote",
            "schemas": [
                CultNetSchemaDescriptor(
                    schema_id="schema:custom",
                    kind="shared_contract",
                    schema_version="custom.v0",
                    wire_contracts=("cultnet.schema.v0",),
                    content_hash="hash",
                    schema_json="{}",
                ).to_wire()
            ],
        })
        self.assertEqual(applied[0].schema_id, "schema:custom")
        self.assertEqual(remote.get("schema:custom"), applied[0])

    def test_cultnet_shard_helpers_match_schema_v0_shape(self) -> None:
        catalog = shard_catalog_request(
            message_id="catalog-1",
            schema_ids=["schema-a"],
            record_keys=["record-a"],
        ).to_wire()
        self.assertEqual(catalog["schemaVersion"], "cultnet.shard_catalog_request.v0")
        self.assertEqual(catalog["messageId"], "catalog-1")
        self.assertEqual(catalog["schemaIds"], ["schema-a"])
        self.assertEqual(catalog["recordKeys"], ["record-a"])

        log = shard_log_request(
            message_id="log-1",
            shard_id="interop",
            shard_epoch=7,
            after_sequence=3,
            limit=2,
        ).to_wire()
        self.assertEqual(log["schemaVersion"], "cultnet.shard_log_request.v0")
        self.assertEqual(log["messageId"], "log-1")
        self.assertEqual(log["shardId"], "interop")
        self.assertEqual(log["shardEpoch"], 7)
        self.assertEqual(log["afterSequence"], 3)
        self.assertEqual(log["limit"], 2)

    def test_cultnet_shard_catalog_applies_filters_and_responses(self) -> None:
        catalog = CultNetShardCatalog()
        primary = catalog.upsert(CultNetShardDescriptor(
            shard_id="notes",
            owner_runtime_id="python-runtime",
            epoch=2,
            is_primary=True,
            schema_ids=("schema-note",),
            key_prefix="note:",
            primary_endpoints=("cultnet://127.0.0.1:3075",),
            read_replica_endpoints=("cultnet://127.0.0.1:3075",),
            region="local",
        ))
        catalog.upsert({
            "shardId": "facts",
            "ownerRuntimeId": "python-runtime",
            "epoch": 1,
            "schemaIds": ["schema-fact"],
            "keyPrefix": "fact:",
            "primaryEndpoints": ["cultnet://127.0.0.1:3076"],
        })

        self.assertTrue(primary.serves(schema_id="schema-note", record_key="note:1"))
        self.assertFalse(primary.serves(schema_id="schema-note", record_key="fact:1"))
        self.assertEqual(catalog.list(schema_ids=["schema-note"]), [primary])
        self.assertEqual(catalog.list(record_keys=["note:1"]), [primary])

        response = catalog.create_response(
            message_id="shards",
            schema_ids=["schema-note"],
            record_keys=["note:1"],
        )
        self.assertEqual(response["schemaVersion"], "cultnet.shard_catalog_response.v0")
        self.assertEqual(response["shards"][0]["shardId"], "notes")
        self.assertEqual(response["shards"][0]["ownerRuntimeId"], "python-runtime")

        remote = CultNetShardCatalog()
        applied = remote.apply_response(response)
        self.assertEqual(applied[0].shard_id, "notes")
        self.assertEqual(remote.get("notes"), applied[0])

    def test_cultnet_shard_log_response_tracks_cursor_and_resync_state(self) -> None:
        response = CultNetShardLogResponse.from_wire({
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log",
            "shardId": "notes",
            "shardEpoch": 2,
            "entries": [
                CultNetShardLogEntry(
                    sequence=2,
                    change_kind="updated",
                    put={"schemaVersion": "cultnet.document_put_raw.v0", "messageId": "put", "document": {"schemaId": "schema-note", "recordKey": "note:1", "payload": b"p"}},
                    committed_at="2026-06-14T00:00:00Z",
                ).to_wire(),
                {"sequence": 3, "changeKind": "removed", "delete": {"schemaId": "schema-note", "recordKey": "note:1"}},
            ],
            "resyncRequired": False,
        })
        self.assertEqual(response.last_sequence, 3)
        self.assertFalse(response.resync_required)
        self.assertEqual(response.entries[0].put["document"]["recordKey"], "note:1")
        self.assertIsInstance(response.entries[0].raw_document, CultNetRawDocumentRecord)
        self.assertEqual(response.entries[0].raw_document.record_key, "note:1")
        self.assertEqual(response.entries[1].delete_schema_id, "schema-note")
        self.assertEqual(response.entries[1].delete_record_key, "note:1")
        self.assertEqual(response.to_wire()["entries"][1]["delete"]["recordKey"], "note:1")
        self.assertIs(response.require_usable(), response)

        resync = CultNetShardLogResponse.from_wire({
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log-resync",
            "shardId": "notes",
            "shardEpoch": 2,
            "entries": [],
            "resyncRequired": True,
            "reason": "compacted",
            "compactedThrough": 12,
        })
        self.assertEqual(resync.last_sequence, 12)
        with self.assertRaisesRegex(ValueError, "compacted"):
            resync.require_usable()

    def test_cultnet_simulation_observation_helper_matches_schema_v0_shape(self) -> None:
        claim_hash = compute_simulation_claim_hash("frame:42", "subject:player-1", "hit")
        self.assertEqual(len(claim_hash), 64)
        message = simulation_observation(
            message_id="obs-1",
            witness_runtime_id="python-test",
            shard_id="interop",
            shard_epoch=1,
            frame=42,
            subject_id="player-1",
            claim_kind="hit",
            claim_hash=claim_hash,
            claim_summary="player-1 hit target-a",
            observed_at="2026-06-13T00:00:02Z",
        ).to_wire()
        self.assertEqual(message["schemaVersion"], "cultnet.simulation_observation.v0")
        self.assertEqual(message["messageId"], "obs-1")
        self.assertEqual(message["observation"]["witnessRuntimeId"], "python-test")
        self.assertEqual(message["observation"]["claimHash"], claim_hash)
        self.assertEqual(message["observation"]["weight"], 1.0)
        observation = CultNetSimulationObservation.from_wire(message)
        self.assertEqual(observation.witness_runtime_id, "python-test")
        self.assertEqual(observation.claim_summary, "player-1 hit target-a")
        self.assertEqual(observation.to_message_wire(message_id="obs-2")["observation"], message["observation"])

    def test_cultnet_witness_artifact_bundle_uses_csharp_slot_order(self) -> None:
        import msgpack  # type: ignore

        bundle = witness_artifact_bundle(
            bundle_id="bundle-1",
            witness_kind="interop-proof",
            captured_at="2026-06-13T00:00:03Z",
            subject={"documentType": "cultnet.interop-note", "subjectId": "note:python"},
            contracts=[{"role": "payload", "schemaId": "schema-a"}],
            artifacts=[{"role": "log", "uri": "cultcache://bundle-1/log", "mediaType": "text/plain"}],
            timing_witnesses=[{"stage": "roundtrip", "startedAt": "2026-06-13T00:00:03Z", "completedAt": "2026-06-13T00:00:04Z", "latencyMs": 1.0}],
            provenance={"pipelineId": "interop", "runId": "run-1", "runtimeId": "python-test"},
        )
        payload = encode_witness_artifact_bundle_payload(bundle)
        decoded = decode_witness_artifact_bundle_payload(payload)
        typed = CultNetWitnessArtifactBundle.from_wire(bundle)
        typed_payload = typed.to_payload()
        typed_decoded = CultNetWitnessArtifactBundle.from_payload(typed_payload)
        slots = msgpack.unpackb(payload, raw=False)

        self.assertEqual(payload, typed_payload)
        self.assertEqual(slots[0], "bundle-1")
        self.assertEqual(slots[1], "interop-proof")
        self.assertEqual(slots[2], "2026-06-13T00:00:03Z")
        self.assertEqual(slots[3]["subjectId"], "note:python")
        self.assertEqual(slots[4][0]["schemaId"], "schema-a")
        self.assertEqual(slots[5][0]["uri"], "cultcache://bundle-1/log")
        self.assertEqual(slots[6][0]["latencyMs"], 1.0)
        self.assertEqual(slots[7]["runtimeId"], "python-test")
        self.assertEqual(decoded["bundleId"], "bundle-1")
        self.assertEqual(decoded["witnessKind"], "interop-proof")
        self.assertEqual(decoded["subject"]["subjectId"], "note:python")
        self.assertEqual(decoded["contracts"][0]["schemaId"], "schema-a")
        self.assertEqual(decoded["artifacts"][0]["mediaType"], "text/plain")
        self.assertEqual(decoded["provenance"]["runtimeId"], "python-test")
        self.assertEqual(typed.bundle_id, "bundle-1")
        self.assertEqual(typed_decoded.to_wire(), bundle)

    def test_cultmesh_node_uses_cultcache_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            document = define_database_entry_type("mesh.note", [("body", 0)])
            store_path = Path(tmp) / "mesh.cc"
            node = create_node(store_path, runtime_id="mesh-test")
            self.assertIsInstance(node.database, CultMeshDatabase)
            node.database.register_document(document)
            node.database.put(document, "note:1", {"body": "hello"})
            self.assertEqual(node.get_required(document, "note:1")["body"], "hello")

            reopened = create_node(store_path, runtime_id="mesh-test")
            reopened.database.register_document(document)
            reopened.database.pull()
            self.assertEqual(reopened.database.get(document, "note:1")["body"], "hello")

    def test_cultmesh_database_watchers_observe_local_changes_and_unsubscribe(self) -> None:
        document = define_database_entry_type("mesh.watch", [("body", 0)])
        node = CultMesh.create_node(runtime_id="mesh-watch")
        node.database.register_document(document)
        changes: list[CultMeshDatabaseChange] = []
        all_changes: list[CultMeshDatabaseChange] = []

        unsubscribe_record = node.database.watch_record(document, "note:1", changes.append)
        unsubscribe_all = node.database.watch(all_changes.append, document=document)

        node.database.put(document, "note:1", {"body": "hello"})
        node.database.put(document, "note:2", {"body": "skip"})
        node.database.put(document, "note:1", {"body": "updated"})
        node.database.delete(document, "note:1")
        unsubscribe_record()
        node.database.put(document, "note:1", {"body": "after-unsubscribe"})
        unsubscribe_all()

        self.assertEqual([change.change_kind for change in changes], ["added", "updated", "removed"])
        self.assertEqual([change.record_key for change in changes], ["note:1", "note:1", "note:1"])
        self.assertEqual(changes[1].previous_value, {"body": "hello"})
        self.assertIsNone(changes[2].value)
        self.assertEqual([change.record_key for change in all_changes], ["note:1", "note:2", "note:1", "note:1", "note:1"])

    def test_cultmesh_database_register_document_is_idempotent(self) -> None:
        document = define_database_entry_type("mesh.idempotent", [("body", 0)])
        conflicting = define_database_entry_type("mesh.idempotent", [("title", 0)])
        node = CultMesh.create_node(runtime_id="mesh-idempotent")

        node.database.register_document(document)
        node.database.register_document(document)
        node.database.put(document, "note:1", {"body": "still registered once"})

        self.assertEqual(node.database.get_required(document, "note:1")["body"], "still registered once")
        self.assertEqual([registered.type for registered in node.documents], ["mesh.idempotent"])
        with self.assertRaises(ValueError):
            node.database.register_document(conflicting)

    def test_cultmesh_database_global_document_facade_uses_singleton_key_and_watchers(self) -> None:
        document = define_database_entry_type(
            "mesh.global_settings",
            [("theme", 0)],
            global_document=True,
        )
        node = CultMesh.create_node(runtime_id="mesh-global")
        node.database.register_document(document)
        changes: list[CultMeshDatabaseChange] = []

        node.database.watch_global(document, changes.append)
        node.database.put_global(document, {"theme": "ash"})
        node.put_global(document, {"theme": "ember"})
        node.delete_global(document)

        self.assertIsNone(node.database.get_global(document))
        self.assertEqual([change.record_key for change in changes], [node.cache.GLOBAL_KEY] * 3)
        self.assertEqual([change.change_kind for change in changes], ["added", "updated", "removed"])
        self.assertEqual(changes[1].previous_value, {"theme": "ash"})
        self.assertEqual(changes[2].previous_value, {"theme": "ember"})
        with self.assertRaises(Exception):
            node.database.put(document, "settings:wrong", {"theme": "bad"})

    def test_cultmesh_database_creates_raw_snapshot_response_from_envelopes(self) -> None:
        document = define_database_entry_type(
            "mesh.snapshot_note",
            [("body", 0)],
            schema_id="mesh.snapshot_note.v1",
        )
        source = CultMesh.create_node(runtime_id="mesh-snapshot-source")
        source.database.register_document(document)
        source.database.put(document, "note:1", {"body": "include"})
        source.database.put(document, "note:2", {"body": "skip"})

        typed_response = source.database.build_snapshot_response(
            message_id="snapshot-1",
            schema_ids=["mesh.snapshot_note.v1"],
            record_keys=["note:1"],
            shard_id="notes",
            shard_epoch=4,
            shard_log_sequence=7,
        )
        response = source.database.create_snapshot_response(
            message_id="snapshot-1",
            schema_ids=["mesh.snapshot_note.v1"],
            record_keys=["note:1"],
            shard_id="notes",
            shard_epoch=4,
            shard_log_sequence=7,
        )

        self.assertIsInstance(typed_response, CultNetRawSnapshotResponse)
        self.assertEqual(typed_response.message_id, "snapshot-1")
        self.assertEqual(typed_response.documents[0].record_key, "note:1")
        self.assertEqual(typed_response.to_wire(), response)
        self.assertEqual(source.build_snapshot_response(schema_ids=["mesh.snapshot_note.v1"]).documents[0].record_key, "note:1")
        self.assertEqual(response["schemaVersion"], "cultnet.snapshot_response_raw.v0")
        self.assertEqual(response["messageId"], "snapshot-1")
        self.assertEqual(response["shardId"], "notes")
        self.assertEqual(response["shardEpoch"], 4)
        self.assertEqual(response["shardLogSequence"], 7)
        self.assertEqual([record["recordKey"] for record in response["documents"]], ["note:1"])
        self.assertEqual(response["documents"][0]["schemaId"], "mesh.snapshot_note.v1")
        self.assertEqual(response["documents"][0]["payloadEncoding"], "messagepack")

        target = CultMesh.create_node(runtime_id="mesh-snapshot-target")
        target.database.register_document(document)
        applied = target.database.apply_snapshot_response(response)
        self.assertEqual([(record.schema_id, record.record_key) for record in applied], [("mesh.snapshot_note.v1", "note:1")])
        self.assertEqual(target.database.get_required(document, "note:1"), {"body": "include"})
        self.assertIsNone(target.database.get(document, "note:2"))

        typed_target = CultMesh.create_node(runtime_id="mesh-snapshot-typed-target")
        typed_target.database.register_document(document)
        typed_applied = typed_target.database.apply_snapshot_response(typed_response)
        self.assertEqual([(record.schema_id, record.record_key) for record in typed_applied], [("mesh.snapshot_note.v1", "note:1")])
        self.assertEqual(typed_target.database.get_required(document, "note:1"), {"body": "include"})

    def test_cultmesh_database_filters_snapshot_by_logged_shard_membership(self) -> None:
        document = define_database_entry_type(
            "mesh.sharded_snapshot_note",
            [("body", 0)],
            schema_id="mesh.sharded_snapshot_note.v1",
        )
        source = CultMesh.create_node(runtime_id="mesh-sharded-snapshot-source")
        source.database.register_document(document)
        source.database.put_raw_message(document, "note:notes", {"body": "include"}, shard_id="notes", shard_epoch=2)
        source.database.put_raw_message(document, "note:other", {"body": "skip"}, shard_id="other", shard_epoch=1)

        response = source.database.build_snapshot_response(
            schema_ids=["mesh.sharded_snapshot_note.v1"],
            shard_id="notes",
            shard_epoch=2,
        )

        self.assertEqual(response.shard_id, "notes")
        self.assertEqual(response.shard_epoch, 2)
        self.assertEqual([record.record_key for record in response.documents], ["note:notes"])

    def test_python_interop_peer_filters_shard_snapshot_by_logged_membership(self) -> None:
        runtime_id = f"python-interop-test-{uuid4().hex}"
        state = build_state(
            runtime_id=runtime_id,
            runtime_kind="python",
            display_name="Python Interop Test",
            agent_id="python-interop-test-agent",
            schema_path=str(Path("packages/cultnet-ts/integration/contracts/cultnet.interop-note.schema.json")),
        )
        binding = state.bindings["note"]
        logged_value = {
            "schemaVersion": "cultnet.interop_note.v0",
            "documentId": "note:logged",
            "authorRuntimeId": runtime_id,
            "title": "Logged",
            "body": "This record belongs to the shard log.",
            "tags": ["interop", "logged"],
        }
        unlogged_value = {
            **logged_value,
            "documentId": "note:unlogged",
            "title": "Unlogged",
            "body": "This record is in cache but not in the shard log.",
        }
        state.cache.put(binding.document, "note:logged", logged_value)
        state.cache.put(binding.document, "note:unlogged", unlogged_value)
        logged_record = {
            "schemaId": state.note_schema_id,
            "recordKey": "note:logged",
            "storedAt": "2026-06-14T00:00:00Z",
            "payloadEncoding": "messagepack",
            "payload": binding.document.encode_payload(logged_value),
        }
        append_shard_log_put(
            state,
            {"messageId": "logged-put"},
            logged_record,
        )

        response = raw_snapshot_response(
            state,
            {
                "schemaVersion": "cultnet.snapshot_request.v0",
                "messageId": "interop-shard-snapshot",
                "schemaIds": [state.note_schema_id],
                "shardId": state.shard_id,
            },
        )

        self.assertEqual(response["shardId"], state.shard_id)
        self.assertEqual(response["shardLogSequence"], 1)
        self.assertEqual([record["recordKey"] for record in response["documents"]], ["note:logged"])

    def test_cultmesh_database_creates_shard_log_response_from_raw_mutations(self) -> None:
        document = define_database_entry_type(
            "mesh.log_note",
            [("body", 0)],
            schema_id="mesh.log_note.v1",
        )
        source = CultMesh.create_node(runtime_id="mesh-log-source")
        source.database.register_document(document)
        source.database.put_raw_message(document, "note:1", {"body": "first"}, shard_id="notes", shard_epoch=3)
        source.database.put_raw_message(document, "note:1", {"body": "second"}, shard_id="notes", shard_epoch=3)
        source.database.put_raw_message(document, "note:2", {"body": "other"}, shard_id="other", shard_epoch=1)
        source.database.delete_raw_message(document, "note:1", shard_id="notes", shard_epoch=3)

        self.assertEqual(source.database.shard_ids(), ["notes", "other"])
        self.assertEqual(source.database.shard_schema_ids("notes"), ["mesh.log_note.v1"])

        typed_response = source.database.build_shard_log_response(
            message_id="log-1",
            shard_id="notes",
            shard_epoch=3,
            after_sequence=1,
            limit=2,
        )
        response = source.database.create_shard_log_response(
            message_id="log-1",
            shard_id="notes",
            shard_epoch=3,
            after_sequence=1,
            limit=2,
        )

        self.assertIsInstance(typed_response, CultNetShardLogResponse)
        self.assertEqual(typed_response.message_id, "log-1")
        self.assertEqual([entry.sequence for entry in typed_response.entries], [2, 3])
        self.assertEqual(typed_response.to_wire(), response)
        self.assertEqual([entry.sequence for entry in source.build_shard_log_response(shard_id="notes").entries], [1, 2, 3])
        self.assertEqual(response["schemaVersion"], "cultnet.shard_log_response.v0")
        self.assertEqual(response["messageId"], "log-1")
        self.assertEqual(response["shardId"], "notes")
        self.assertEqual(response["shardEpoch"], 3)
        self.assertFalse(response["resyncRequired"])
        self.assertEqual([entry["sequence"] for entry in response["entries"]], [2, 3])
        self.assertEqual([entry["changeKind"] for entry in response["entries"]], ["updated", "removed"])
        self.assertEqual(response["entries"][0]["put"]["document"]["recordKey"], "note:1")
        self.assertEqual(response["entries"][1]["delete"]["recordKey"], "note:1")
        empty = source.database.create_shard_log_response(shard_id="missing")
        self.assertEqual(empty["shardEpoch"], 0)
        self.assertEqual(empty["entries"], [])
        self.assertTrue(empty["resyncRequired"])
        self.assertEqual(empty["reason"], "unknown_shard")
        stale = source.database.create_shard_log_response(shard_id="notes", shard_epoch=2)
        self.assertEqual(stale["shardEpoch"], 3)
        self.assertEqual(stale["entries"], [])
        self.assertTrue(stale["resyncRequired"])
        self.assertEqual(stale["reason"], "stale_epoch")
        with self.assertRaises(ValueError):
            source.database.create_shard_log_response(shard_id="notes", after_sequence=-1)
        with self.assertRaises(ValueError):
            source.database.create_shard_log_response(shard_id="notes", limit=-1)

        target = CultMesh.create_node(runtime_id="mesh-log-target")
        target.database.register_document(document)
        target.database.put(document, "note:1", {"body": "first"})
        applied = target.database.apply_shard_log_response(response)
        self.assertEqual([record.change_kind for record in applied], ["updated", "removed"])
        self.assertIsNone(target.database.get(document, "note:1"))
        self.assertIsNone(target.database.get(document, "note:2"))

        typed_target = CultMesh.create_node(runtime_id="mesh-log-typed-target")
        typed_target.database.register_document(document)
        typed_target.database.put(document, "note:1", {"body": "first"})
        typed_applied = typed_target.database.apply_shard_log_response(typed_response)
        self.assertEqual([record.change_kind for record in typed_applied], ["updated", "removed"])
        self.assertIsNone(typed_target.database.get(document, "note:1"))

    def test_cultmesh_database_watchers_observe_name_and_index_changes(self) -> None:
        document = define_database_entry_type(
            "mesh.named_watch",
            [("name", 0), ("kind", 1)],
            name="name",
            indexes={"kind": "kind"},
        )
        node = CultMesh.create_node(runtime_id="mesh-named-watch")
        node.database.register_document(document)
        name_changes: list[CultMeshDatabaseChange] = []
        index_changes: list[CultMeshDatabaseChange] = []

        node.database.watch_by_name(document, "Potion", name_changes.append)
        node.database.watch_by_index(document, "kind", "consumable", index_changes.append)

        node.database.put(document, "item:1", {"name": "Potion", "kind": "consumable"})
        node.database.put(document, "item:2", {"name": "Sword", "kind": "weapon"})
        node.database.put(document, "item:1", {"name": "Elixir", "kind": "rare"})
        node.database.delete(document, "item:1")

        self.assertEqual([change.change_kind for change in name_changes], ["added", "updated"])
        self.assertEqual([change.record_key for change in name_changes], ["item:1", "item:1"])
        self.assertEqual(name_changes[1].previous_value, {"name": "Potion", "kind": "consumable"})
        self.assertEqual([change.change_kind for change in index_changes], ["added", "updated"])
        self.assertEqual([change.record_key for change in index_changes], ["item:1", "item:1"])
        self.assertEqual(index_changes[1].value, {"name": "Elixir", "kind": "rare"})

    def test_cultmesh_database_watch_by_name_and_index_validate_lookup_shape(self) -> None:
        document = define_database_entry_type("mesh.unnamed_watch", [("body", 0)])
        node = CultMesh.create_node(runtime_id="mesh-watch-validation")
        node.database.register_document(document)

        with self.assertRaises(ValueError):
            node.database.watch_by_name(document, "missing", lambda _: None)
        with self.assertRaises(ValueError):
            node.database.watch_by_index(document, "missing", "value", lambda _: None)
        with self.assertRaises(ValueError):
            node.database.watch_by_index(document, "", "value", lambda _: None)
        with self.assertRaises(ValueError):
            node.database.watch_by_index(document, "missing", "", lambda _: None)

    def test_cultmesh_facade_matches_peer_runtime_entrypoints(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            store_path = Path(tmp) / "mesh.cc"
            node = CultMesh.start_node(store_path, runtime_id="mesh-facade")

            self.assertEqual(node.runtime_id, "mesh-facade")
            self.assertIsInstance(CultMesh.create_node(), type(node))
            self.assertIsInstance(CultMesh.create_verse_catalog(), CultMeshVerseCatalog)
            self.assertIsInstance(CultMesh.create_peer_catalog(), CultMeshPeerCatalog)
            self.assertIsInstance(CultMesh.create_authority_lease_catalog(), CultMeshAuthorityLeaseCatalog)
            self.assertIsInstance(CultMesh.create_stream_catalog(), CultMeshStreamCatalog)
            verse_client = CultMesh.create_verse_discovery_client("127.0.0.1", 4010, timeout_seconds=1.5)
            peer_client = CultMesh.create_peer_exchange_client("127.0.0.1", 4011, timeout_seconds=1.5)
            raw_client = CultMesh.create_client("127.0.0.1", 4012, timeout_seconds=1.5)
            connected_client = CultMesh.connect_client("127.0.0.1", 4013, timeout_seconds=1.5)
            self.assertIsInstance(verse_client, CultMeshVerseDiscoveryClient)
            self.assertIsInstance(peer_client, CultMeshPeerExchangeClient)
            self.assertIsInstance(verse_client, CultMeshDiscoveryClient)
            self.assertIsInstance(peer_client, CultMeshDiscoveryClient)
            self.assertIsInstance(raw_client, CultNetRawClient)
            self.assertIsInstance(connected_client, CultNetRawClient)
            self.assertEqual((verse_client.host, verse_client.port, verse_client.timeout_seconds), ("127.0.0.1", 4010, 1.5))
            self.assertEqual((peer_client.host, peer_client.port, peer_client.timeout_seconds), ("127.0.0.1", 4011, 1.5))
            self.assertEqual((raw_client.host, raw_client.port, raw_client.timeout_seconds), ("127.0.0.1", 4012, 1.5))
            self.assertEqual((connected_client.host, connected_client.port, connected_client.timeout_seconds), ("127.0.0.1", 4013, 1.5))

    def test_cultmesh_simulation_fact_uses_csharp_slot_contract(self) -> None:
        import msgpack  # type: ignore

        candidate = {
            "shardId": "arena",
            "shardEpoch": 4,
            "frame": 100,
            "subjectId": "bob",
            "claimKind": "hit",
            "claimHash": compute_simulation_claim_hash("hit", "alice", "bob", "frame:100"),
            "claimSummary": "alice shot bob first",
            "witnessCount": 2,
            "supportWeight": 2.0,
            "totalWeight": 2.0,
            "confidence": 1.0,
            "hasQuorum": True,
        }

        fact = CultMeshSimulationFact.from_candidate(candidate, committed_at="2026-06-13T00:00:00Z")
        payload = simulation_fact_document.encode_payload(fact)
        slots = msgpack.unpackb(payload, raw=False)

        self.assertEqual(CultMeshSimulationFact.create_record_key(candidate), f"simulation:{fact.fact_id}")
        self.assertEqual(slots[0], fact.fact_id)
        self.assertEqual(slots[1], "arena")
        self.assertEqual(slots[2], 4)
        self.assertEqual(slots[3], 100)
        self.assertEqual(slots[4], "bob")
        self.assertEqual(slots[5], "hit")
        self.assertEqual(slots[6], candidate["claimHash"])
        self.assertEqual(slots[7], "alice shot bob first")
        self.assertEqual(slots[8], 2)
        self.assertEqual(slots[9], 2.0)
        self.assertEqual(slots[10], 2.0)
        self.assertEqual(slots[11], 1.0)
        self.assertEqual(slots[12], "2026-06-13T00:00:00Z")

    def test_cultmesh_simulation_fact_committer_rejects_without_quorum_and_stores_fact(self) -> None:
        candidate = {
            "shardId": "arena",
            "shardEpoch": 4,
            "frame": 100,
            "subjectId": "bob",
            "claimKind": "hit",
            "claimHash": compute_simulation_claim_hash("hit", "alice", "bob", "frame:100"),
            "claimSummary": "alice shot bob first",
            "witnessCount": 2,
            "supportWeight": 2.0,
            "totalWeight": 2.0,
            "confidence": 1.0,
            "hasQuorum": True,
        }

        with tempfile.TemporaryDirectory() as tmp:
            node = CultMesh.create_node(Path(tmp) / "facts.cc", runtime_id="mesh-facts")
            committer = CultMesh.create_simulation_fact_committer(node)

            rejected = dict(candidate)
            rejected["hasQuorum"] = False
            with self.assertRaisesRegex(ValueError, "before quorum"):
                committer.commit(rejected)

            committed = committer.commit(candidate, committed_at="2026-06-13T00:00:00Z")
            stored = node.get_required(simulation_fact_document, committed.key)

            self.assertEqual(stored.claim_hash, candidate["claimHash"])
            self.assertEqual(stored.committed_at, "2026-06-13T00:00:00Z")
            self.assertEqual(committed.fact.fact_id, stored.fact_id)

    def test_cultnet_simulation_consensus_dedupes_witnesses_and_requires_quorum(self) -> None:
        from cultnet_py import CultNetSimulationConsensus

        consensus = CultNetSimulationConsensus(
            CultNetSimulationConsensusOptions(minimum_witnesses=2, quorum_ratio=1.0)
        )
        claim_hash = compute_simulation_claim_hash("hit", "alice", "bob", "frame:100")
        observations = [
            {
                "witnessRuntimeId": "watcher-1",
                "shardId": "arena",
                "shardEpoch": 4,
                "frame": 100,
                "subjectId": "bob",
                "claimKind": "hit",
                "claimHash": claim_hash,
                "claimSummary": "alice shot bob first",
                "weight": 1.0,
            },
            {
                "witnessRuntimeId": "watcher-1",
                "shardId": "arena",
                "shardEpoch": 4,
                "frame": 100,
                "subjectId": "bob",
                "claimKind": "hit",
                "claimHash": "stale-duplicate",
                "weight": 0.5,
            },
            {
                "witnessRuntimeId": "watcher-2",
                "shardId": "arena",
                "shardEpoch": 4,
                "frame": 100,
                "subjectId": "bob",
                "claimKind": "hit",
                "claimHash": claim_hash,
                "weight": 1.0,
            },
        ]

        candidates = consensus.build_candidates(observations)
        typed_candidates = consensus.build_candidate_objects([
            CultNetSimulationObservation.from_wire(observation)
            for observation in observations
        ])

        self.assertEqual(len(candidates), 1)
        self.assertEqual(candidates[0]["claimHash"], claim_hash)
        self.assertEqual(candidates[0]["witnessCount"], 2)
        self.assertEqual(candidates[0]["supportWeight"], 2.0)
        self.assertTrue(candidates[0]["hasQuorum"])
        self.assertEqual(len(typed_candidates), 1)
        self.assertIsInstance(typed_candidates[0], CultNetSimulationConsensusCandidate)
        self.assertEqual(typed_candidates[0].claim_hash, claim_hash)
        self.assertEqual(typed_candidates[0].to_wire(), candidates[0])
        self.assertEqual(CultNetSimulationConsensusCandidate.from_wire(candidates[0]), typed_candidates[0])

    def test_cultmesh_game_session_submits_observations_and_commits_quorum_once(self) -> None:
        claim_hash = compute_simulation_claim_hash("hit", "alice", "bob", "frame:100")
        first = simulation_observation(
            message_id="obs-1",
            witness_runtime_id="watcher-1",
            shard_id="arena",
            shard_epoch=4,
            frame=100,
            subject_id="bob",
            claim_kind="hit",
            claim_hash=claim_hash,
            claim_summary="alice shot bob first",
        ).to_wire()
        second = simulation_observation(
            message_id="obs-2",
            witness_runtime_id="watcher-2",
            shard_id="arena",
            shard_epoch=4,
            frame=100,
            subject_id="bob",
            claim_kind="hit",
            claim_hash=claim_hash,
            claim_summary="alice shot bob first",
        ).to_wire()

        with tempfile.TemporaryDirectory() as tmp:
            node = CultMesh.create_node(Path(tmp) / "session.cc", runtime_id="mesh-session")
            session = CultMesh.create_game_session(
                node,
                CultMeshGameSessionOptions(
                    consensus_options=CultNetSimulationConsensusOptions(minimum_witnesses=2, quorum_ratio=1.0)
                ),
            )

            self.assertEqual(session.submit_and_commit(CultNetSimulationObservation.from_wire(first)), [])
            typed_candidates = session.submit_observation_candidates(CultNetSimulationObservation.from_wire(second))
            commits = session.commit_quorum_candidate_objects(typed_candidates)
            replay = session.commit_quorum_candidates(session.submit_observation(second))

            self.assertEqual(len(typed_candidates), 1)
            self.assertTrue(typed_candidates[0].has_quorum)
            self.assertEqual(len(commits), 1)
            self.assertEqual(replay, [])
            stored = node.get_required(simulation_fact_document, commits[0].key)
            self.assertEqual(stored.claim_hash, claim_hash)
            self.assertEqual(stored.witness_count, 2)

    def test_cultmesh_game_session_prediction_requires_scope_and_reconciles_shard_log(self) -> None:
        note_doc = define_database_entry_type(
            "mesh.input",
            [("body", 0)],
            schema_id="mesh.input.v1",
        )
        with tempfile.TemporaryDirectory() as tmp:
            node = CultMesh.create_node(Path(tmp) / "prediction.cc", runtime_id="client-a")
            node.register_document(note_doc)
            session = CultMesh.create_game_session(
                node,
                CultMeshGameSessionOptions(
                    client_authority_scopes=(
                        CultNetClientAuthorityScope(
                            "client-a",
                            schema_ids=("mesh.input.v1",),
                            key_prefix="input:client-a",
                        ),
                    )
                ),
            )

            with self.assertRaisesRegex(ValueError, "does not have client prediction authority"):
                session.predict(note_doc, "input:other", {"body": "nope"})

            prediction = session.predict(note_doc, "input:client-a:move", {"body": "predicted"})
            put = document_put_raw(
                message_id="authoritative",
                key=prediction.key,
                schema_id=prediction.schema_id,
                stored_at="2026-06-13T00:00:00Z",
                payload=note_doc.encode_payload({"body": "authoritative"}),
                shard_id="inputs",
                shard_epoch=1,
            )
            authoritative_response = {
                "schemaVersion": "cultnet.shard_log_response.v0",
                "messageId": "inputs-log",
                "shardId": "inputs",
                "shardEpoch": 1,
                "entries": [
                    {
                        "sequence": 1,
                        "committedAt": "2026-06-13T00:00:00Z",
                        "changeKind": "updated",
                        "put": put.to_wire(),
                    }
                ],
                "resyncRequired": False,
            }
            changes = session.apply_shard_log_response(CultNetShardLogResponse.from_wire(authoritative_response))

            self.assertEqual(changes[0].change_kind, "reconciled")
            self.assertEqual(node.get_required(note_doc, prediction.key)["body"], "authoritative")

    def test_cultmesh_database_watchers_observe_authoritative_shard_log_reconciliation(self) -> None:
        note_doc = define_database_entry_type(
            "mesh.watched_input",
            [("body", 0)],
            schema_id="mesh.watched_input.v1",
        )
        node = CultMesh.create_node(runtime_id="client-watch")
        node.database.register_document(note_doc)
        seen: list[CultMeshDatabaseChange] = []
        node.database.watch_record(note_doc, "input:client-watch:move", seen.append)
        session = CultMesh.create_game_session(
            node,
            CultMeshGameSessionOptions(
                client_authority_scopes=(
                    CultNetClientAuthorityScope(
                        "client-watch",
                        schema_ids=("mesh.watched_input.v1",),
                        key_prefix="input:client-watch",
                    ),
                )
            ),
        )

        prediction = session.predict(note_doc, "input:client-watch:move", {"body": "predicted"})
        put = document_put_raw(
            message_id="authoritative-watch",
            key=prediction.key,
            schema_id=prediction.schema_id,
            stored_at="2026-06-13T00:00:00Z",
            payload=note_doc.encode_payload({"body": "authoritative"}),
            shard_id="inputs",
            shard_epoch=1,
        )
        session_changes = session.apply_shard_log_response({
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "inputs-watch-log",
            "shardId": "inputs",
            "shardEpoch": 1,
            "entries": [
                {
                    "sequence": 1,
                    "committedAt": "2026-06-13T00:00:00Z",
                    "changeKind": "updated",
                    "put": put.to_wire(),
                }
            ],
            "resyncRequired": False,
        })

        self.assertEqual([change.change_kind for change in seen], ["added", "updated"])
        self.assertEqual(seen[1].value, {"body": "authoritative"})
        self.assertEqual(seen[1].previous_value, {"body": "predicted"})
        self.assertEqual(session_changes[0].change_kind, "reconciled")

    def test_cultmesh_verse_catalog_response_matches_schema_v0_wire_shape(self) -> None:
        import msgpack  # type: ignore

        catalog = CultMeshVerseCatalog()
        catalog.upsert(
            CultMeshVerseDescriptor(
                verse_id="aetheria-main",
                display_name="Aetheria",
                authority_model="OperatorCluster",
                compatibility=CultMeshVerseCompatibility(
                    transport_version="cultmesh.v0",
                    rules_hash="rules",
                    compatible_verse_ids=("aetheria-modded",),
                    required_plugin_ids=("core",),
                    optional_plugin_ids=("skylands",),
                ),
                discovery_endpoints=("cultmesh://aetheria.example.test:3075",),
                authority_runtime_ids=("runtime-a",),
                description="main branch",
            )
        )

        response = catalog.create_response(verse_catalog_request("verses-1"))
        decoded = msgpack.unpackb(msgpack.packb(response, use_bin_type=True), raw=False)
        self.assertEqual(decoded["schemaVersion"], "cultmesh.verse_catalog_response.v0")
        self.assertEqual(decoded["messageId"], "verses-1")
        self.assertEqual(decoded["verses"][0]["verseId"], "aetheria-main")
        self.assertEqual(decoded["verses"][0]["compatibility"]["requiredPluginIds"], ["core"])

    def test_cultmesh_verse_catalog_watches_updates_and_finds_transfer_targets(self) -> None:
        source = CultMeshVerseDescriptor(
            verse_id="aetheria-main",
            display_name="Aetheria",
            authority_model="federated",
            compatibility=CultMeshVerseCompatibility(
                transport_version="cultmesh.v0",
                rules_hash="rules-main",
            ),
        )
        compatible = CultMeshVerseDescriptor(
            verse_id="aetheria-modded",
            display_name="Aetheria Modded",
            authority_model="federated",
            compatibility=CultMeshVerseCompatibility(
                transport_version="cultmesh.v0",
                rules_hash="rules-modded",
                compatible_verse_ids=("aetheria-main",),
            ),
        )
        incompatible = CultMeshVerseDescriptor(
            verse_id="old-world",
            display_name="Old World",
            authority_model="solo",
            compatibility=CultMeshVerseCompatibility(
                transport_version="cultmesh.v0",
                rules_hash="rules-old",
            ),
        )
        catalog = CultMeshVerseCatalog()
        seen: list[str] = []
        unsubscribe = catalog.watch(lambda verse: seen.append(verse.verse_id))

        catalog.upsert(source)
        catalog.apply_response({
            "schemaVersion": "cultmesh.verse_catalog_response.v0",
            "messageId": "verses-watch",
            "verses": [compatible.to_wire(), incompatible.to_wire()],
        })
        unsubscribe()
        catalog.upsert(CultMeshVerseDescriptor(
            verse_id="after-unsubscribe",
            display_name="After",
            authority_model="none",
            compatibility=CultMeshVerseCompatibility("cultmesh.v0", "rules-after"),
        ))

        self.assertEqual(seen, ["aetheria-main", "aetheria-modded", "old-world"])
        self.assertEqual([verse.verse_id for verse in catalog.verses], ["aetheria-main", "aetheria-modded", "after-unsubscribe", "old-world"])
        self.assertEqual(catalog.get("aetheria-modded"), compatible)
        self.assertEqual([verse.verse_id for verse in catalog.find_transfer_targets(source)], ["aetheria-modded"])

    def test_cultmesh_peer_exchange_response_matches_schema_v0_wire_shape(self) -> None:
        import msgpack  # type: ignore

        catalog = CultMeshPeerCatalog()
        catalog.upsert(
            CultMeshPeerCard(
                peer_id="peer-a",
                verse_id="aetheria-main",
                endpoints=("cultnet://peer-a.example.test:3075",),
                roles=("discovery", "read-replica"),
                shard_ids=("players",),
                region="eu-west",
                authority_lease_id="lease-1",
                expires_at="2026-05-20T12:00:00.0000000Z",
                signature="sig",
            )
        )

        response = catalog.create_response(
            peer_exchange_request("pex-1", verse_id="aetheria-main", roles=["read-replica"])
        )
        decoded = msgpack.unpackb(msgpack.packb(response, use_bin_type=True), raw=False)
        self.assertEqual(decoded["schemaVersion"], "cultmesh.peer_exchange_response.v0")
        self.assertEqual(decoded["messageId"], "pex-1")
        self.assertEqual(decoded["peers"][0]["peerId"], "peer-a")
        self.assertIn("read-replica", decoded["peers"][0]["roles"])
        self.assertEqual(decoded["peers"][0]["authorityLeaseId"], "lease-1")

    def test_cultmesh_peer_catalog_watches_updates_and_gets_peers(self) -> None:
        first = CultMeshPeerCard(
            peer_id="peer-a",
            verse_id="aetheria-main",
            endpoints=("cultnet://peer-a.example.test:3075",),
            roles=("discovery", "read-replica"),
        )
        second = CultMeshPeerCard(
            peer_id="peer-b",
            verse_id="aetheria-main",
            endpoints=("cultnet://peer-b.example.test:3075",),
            roles=("shard-primary",),
        )
        catalog = CultMeshPeerCatalog()
        seen: list[str] = []
        unsubscribe = catalog.watch(lambda peer: seen.append(peer.peer_id))

        catalog.upsert(first)
        catalog.apply_response({
            "schemaVersion": "cultmesh.peer_exchange_response.v0",
            "messageId": "peers-watch",
            "peers": [second.to_wire()],
        })
        unsubscribe()
        catalog.upsert(CultMeshPeerCard(
            peer_id="peer-c",
            verse_id="aetheria-main",
            endpoints=("cultnet://peer-c.example.test:3075",),
            roles=("read-replica",),
        ))

        self.assertEqual(seen, ["peer-a", "peer-b"])
        self.assertTrue(first.has_role("read-replica"))
        self.assertEqual([peer.peer_id for peer in catalog.peers], ["peer-a", "peer-b", "peer-c"])
        self.assertEqual(catalog.get("peer-b"), second)
        self.assertEqual([peer.peer_id for peer in catalog.find("aetheria-main", role="read-replica")], ["peer-a", "peer-c"])

    def test_cultmesh_discovery_client_fetches_typed_catalogs_over_cultnet_frames(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        verses = CultMeshVerseCatalog()
        verses.upsert(
            CultMeshVerseDescriptor(
                verse_id="aetheria-main",
                display_name="Aetheria",
                authority_model="federated",
                compatibility=CultMeshVerseCompatibility(
                    transport_version="cultmesh.v0",
                    rules_hash="rules-1",
                    required_plugin_ids=("core",),
                ),
                discovery_endpoints=("cultmesh://aetheria.example.test:3075",),
                authority_runtime_ids=("runtime-a",),
            )
        )
        peers = CultMeshPeerCatalog()
        peers.upsert(
            CultMeshPeerCard(
                peer_id="peer-a",
                verse_id="aetheria-main",
                endpoints=("cultnet://peer-a.example.test:3075",),
                roles=("read-replica",),
                shard_ids=("players",),
            )
        )

        ready = threading.Event()
        server_error: list[BaseException] = []

        def serve_requests() -> None:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", 0))
                    port_holder.append(server.getsockname()[1])
                    server.listen(2)
                    ready.set()
                    for _ in range(4):
                        connection, _ = server.accept()
                        with connection:
                            stream = connection.makefile("rwb")
                            request = msgpack.unpackb(read_frame(stream), raw=False)
                            if request["schemaVersion"] == "cultmesh.verse_catalog_request.v0":
                                response = verses.create_response(request)
                            elif request["schemaVersion"] == "cultmesh.peer_exchange_request.v0":
                                response = peers.create_response(request)
                            else:
                                raise AssertionError(f"unexpected request {request['schemaVersion']}")
                            write_frame(stream, msgpack.packb(response, use_bin_type=True))
                            stream.flush()
            except BaseException as error:
                server_error.append(error)
                ready.set()

        port_holder: list[int] = []
        thread = threading.Thread(target=serve_requests, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))
        self.assertFalse(server_error)

        client = CultMeshDiscoveryClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        fetched_verses = client.fetch_verses(transport_version="cultmesh.v0")
        fetched_peers = client.fetch_peers(verse_id="aetheria-main", roles=["read-replica"])
        local_verses = CultMeshVerseCatalog()
        local_peers = CultMeshPeerCatalog()
        synced_verses = client.sync_verse_catalog(local_verses, transport_version="cultmesh.v0")
        synced_peers = client.sync_peer_catalog(local_peers, verse_id="aetheria-main", roles=["read-replica"])

        thread.join(2.0)
        self.assertFalse(server_error)
        self.assertEqual(fetched_verses[0].verse_id, "aetheria-main")
        self.assertEqual(fetched_verses[0].compatibility.required_plugin_ids, ("core",))
        self.assertEqual(fetched_peers[0].peer_id, "peer-a")
        self.assertEqual(fetched_peers[0].shard_ids, ("players",))
        self.assertEqual(synced_verses[0].verse_id, "aetheria-main")
        self.assertEqual(local_peers.find("aetheria-main", role="read-replica")[0].peer_id, "peer-a")
        self.assertEqual(synced_peers[0].roles, ("read-replica",))

    def test_cultmesh_local_server_serves_node_and_catalogs_over_clients(self) -> None:
        document = define_database_entry_type(
            "mesh.server_note",
            [("body", 0)],
            schema_id="mesh.server_note.v1",
        )
        node = CultMesh.create_node(runtime_id="mesh-server")
        node.database.register_document(document)
        node.database.put_raw_message(document, "note:1", {"body": "served"}, shard_id="notes", shard_epoch=1)
        verses = CultMeshVerseCatalog()
        verses.upsert(
            CultMeshVerseDescriptor(
                verse_id="server-verse",
                display_name="Server Verse",
                authority_model="local",
                compatibility=CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            )
        )
        peers = CultMeshPeerCatalog()
        peers.upsert(
            CultMeshPeerCard(
                peer_id="mesh-server",
                verse_id="server-verse",
                endpoints=("cultnet://127.0.0.1:0",),
                roles=("read-replica",),
            )
        )

        server = CultMesh.serve_node(node, verse_catalog=verses, peer_catalog=peers, display_name="Mesh Server")
        try:
            raw_client = CultMesh.create_client("127.0.0.1", server.port, timeout_seconds=2.0)
            hello_response = raw_client.request(hello(runtime_id="probe"), expected_schema_version="cultnet.hello.v0")
            schema_response = raw_client.fetch_schema_catalog(schema_ids=["mesh.server_note.v1"], include_schema_json=True)
            wire_schema_response = raw_client.fetch_schema_catalog(kinds=["wire_message"], include_schema_json=True)
            synced_schema_catalog = CultNetSchemaCatalog()
            synced_wire_descriptors = raw_client.sync_schema_catalog(
                synced_schema_catalog,
                kinds=["wire_message"],
                include_schema_json=True,
            )
            snapshot_response = raw_client.fetch_snapshot(schema_ids=["mesh.server_note.v1"])
            typed_snapshot_response = raw_client.fetch_snapshot_response(schema_ids=["mesh.server_note.v1"])
            shard_catalog = raw_client.fetch_shard_catalog(schema_ids=["mesh.server_note.v1"])
            synced_shard_catalog = CultNetShardCatalog()
            synced_shards = raw_client.sync_shard_catalog(synced_shard_catalog, schema_ids=["mesh.server_note.v1"])
            shard_log = raw_client.fetch_shard_log(shard_id="notes", shard_epoch=1)
            typed_shard_log = raw_client.fetch_shard_log_response(shard_id="notes", shard_epoch=1)
            stale_shard_log = raw_client.fetch_shard_log(shard_id="notes", shard_epoch=0)
            discovery_client = CultMesh.create_verse_discovery_client("127.0.0.1", server.port, timeout_seconds=2.0)
            fetched_verses = discovery_client.fetch_verses(transport_version="cultmesh.v0")
            fetched_peers = discovery_client.fetch_peers(verse_id="server-verse", roles=["read-replica"])
            with raw_client.subscribe_database(subscription_id="sub-server", schema_ids=["mesh.server_note.v1"]) as subscription:
                subscription_snapshot = subscription.read_next_snapshot_response()
                subscription.send(document_put_raw(
                    message_id="server-put",
                    key="note:2",
                    schema_id="mesh.server_note.v1",
                    stored_at="2026-06-14T00:00:00Z",
                    payload=document.encode_payload({"body": "subscribed"}),
                    shard_id="primary",
                    shard_epoch=1,
                ))
                subscription_change = subscription.read_next_change()
                subscription.send(document_delete(
                    message_id="server-delete",
                    schema_id="mesh.server_note.v1",
                    record_key="note:2",
                    shard_id="primary",
                    shard_epoch=1,
                ))
                subscription_delete = subscription.read_next_change()
        finally:
            server.stop()

        self.assertEqual(hello_response["runtimeId"], "mesh-server")
        self.assertEqual(hello_response["displayName"], "Mesh Server")
        self.assertIn("cultnet.database_subscribe.v0", hello_response["supportedMessageVersions"])
        self.assertIn("cultnet.document_put_raw.v0", hello_response["supportedMessageVersions"])
        self.assertEqual(hello_response["supportedMutationContracts"][0]["documentType"], "mesh.server_note")
        self.assertIn("documentDelete", hello_response["supportedMutationContracts"][0]["operations"])
        self.assertIn("shardLog", hello_response["supportedMutationContracts"][0]["operations"])
        self.assertEqual(schema_response["schemas"][0]["schemaId"], "mesh.server_note.v1")
        self.assertIn("schemaJson", schema_response["schemas"][0])
        self.assertIn("cultnet.document_delete.v0", schema_response["schemas"][0]["wireContracts"])
        self.assertIn("cultnet.shard_log_response.v0", schema_response["schemas"][0]["wireContracts"])
        wire_descriptors = {schema["schemaVersion"]: schema for schema in wire_schema_response["schemas"]}
        self.assertEqual(wire_descriptors["cultnet.document_put_raw.v0"]["kind"], "wire_message")
        self.assertIn("schemaJson", wire_descriptors["cultnet.document_put_raw.v0"])
        self.assertIn("cultmesh.peer_exchange_response.v0", wire_descriptors)
        self.assertIn("cultnet.document_put_raw.v0", {descriptor.schema_version for descriptor in synced_wire_descriptors})
        self.assertIsNotNone(synced_schema_catalog.get("https://github.com/GameCult/cultnet-ts/contracts/cultnet.document-put-raw.schema.json"))
        self.assertEqual(snapshot_response["documents"][0]["recordKey"], "note:1")
        self.assertEqual(typed_snapshot_response.documents[0].record_key, "note:1")
        self.assertEqual(typed_snapshot_response.documents[0].schema_id, "mesh.server_note.v1")
        self.assertEqual(shard_catalog["shards"][0]["schemaIds"], ["mesh.server_note.v1"])
        self.assertEqual(shard_catalog["shards"][0]["epoch"], 1)
        self.assertEqual(shard_catalog["shards"][0]["shardId"], "notes")
        self.assertEqual(synced_shards[0].shard_id, "notes")
        self.assertEqual(synced_shards[0].epoch, 1)
        self.assertEqual(synced_shard_catalog.get("notes"), synced_shards[0])
        self.assertEqual(shard_log["entries"][0]["changeKind"], "added")
        self.assertTrue(stale_shard_log["resyncRequired"])
        self.assertEqual(stale_shard_log["reason"], "stale_epoch")
        self.assertEqual(stale_shard_log["shardEpoch"], 1)
        self.assertEqual(stale_shard_log["entries"], [])
        self.assertEqual(typed_shard_log.last_sequence, 1)
        self.assertEqual(typed_shard_log.entries[0].put["document"]["recordKey"], "note:1")
        self.assertIsInstance(typed_shard_log.entries[0].raw_document, CultNetRawDocumentRecord)
        self.assertEqual(typed_shard_log.entries[0].raw_document.record_key, "note:1")
        self.assertEqual(fetched_verses[0].verse_id, "server-verse")
        self.assertEqual(fetched_peers[0].peer_id, "mesh-server")
        self.assertIsInstance(subscription_snapshot, CultNetRawSnapshotResponse)
        self.assertEqual(subscription_snapshot.to_wire()["schemaVersion"], "cultnet.snapshot_response_raw.v0")
        self.assertEqual(subscription_change.change_kind, "added")
        self.assertEqual(subscription_change.record_key, "note:2")
        self.assertEqual(subscription_change.document["recordKey"], "note:2")
        self.assertIsInstance(subscription_change.raw_document, CultNetRawDocumentRecord)
        self.assertEqual(subscription_change.raw_document.schema_id, "mesh.server_note.v1")
        self.assertEqual(subscription_delete.change_kind, "removed")
        self.assertEqual(subscription_delete.record_key, "note:2")
        self.assertIsNone(subscription_delete.raw_document)
        self.assertIsNone(node.database.get(document, "note:2"))

    def test_cultmesh_authority_lease_requires_live_matching_lease(self) -> None:
        peer = CultMeshPeerCard(
            peer_id="voidbot-local",
            verse_id="local",
            endpoints=("cultmesh://localhost",),
            roles=("shard-primary",),
            authority_lease_id="lease:voidbot-local",
        )
        leases = CultMeshAuthorityLeaseCatalog()
        now = datetime.now(UTC)
        self.assertFalse(leases.is_authorized(peer, "shard-primary", at=now))

        leases.upsert(
            CultMeshAuthorityLease(
                lease_id="lease:voidbot-local",
                verse_id="local",
                peer_id="voidbot-local",
                roles=("shard-primary",),
                valid_from=now - timedelta(seconds=1),
                expires_at=now + timedelta(seconds=1),
            )
        )

        self.assertTrue(leases.is_authorized(peer, "shard-primary", at=now))
        self.assertFalse(leases.is_authorized(peer, "read-replica", at=now))

    def test_cultmesh_stream_catalog_negotiates_transport_and_latest_frame(self) -> None:
        streams = CultMeshStreamCatalog()
        streams.declare(
            CultMeshStreamDescriptor(
                stream_id="mimir:kiyo-pro",
                verse_id="studio",
                owner_peer_id="starfire",
                kind="video",
                label="Kiyo Pro",
                clock={"clockDomainId": "starfire-qpc", "confidence": 0.25},
                video={"width": 1920, "height": 1080, "pixelFormat": "YUY2", "framesPerSecond": 30},
                preferred_transports=("shared-d3d12-texture", "shared-memory", "cultcache-page"),
                max_in_flight_frames=3,
            )
        )

        negotiation = streams.negotiate(
            "mimir:kiyo-pro",
            CultMeshStreamConsumerProfile(
                peer_id="fensalir",
                verse_id="studio",
                supported_transports=("shared-d3d12-texture", "cultcache-page"),
                accepted_kinds=("video",),
                can_import_gpu_handles=True,
                max_in_flight_frames=2,
            ),
        )

        self.assertEqual(negotiation.transport, "shared-d3d12-texture")
        self.assertEqual(negotiation.max_in_flight_frames, 2)
        self.assertEqual(negotiation.copy_budget, "zero-copy-target")

        streams.publish_frame(
            CultMeshStreamFrameHandle(
                stream_id="mimir:kiyo-pro",
                sequence=42,
                timestamp_ns=1_000_000_000,
                duration_ns=33_333_334,
                transport="shared-d3d12-texture",
                native_handle="0xfeed",
                fence_handle="0xbeef",
                fence_value=7,
                unavoidable_copy_count=0,
            )
        )
        self.assertEqual(streams.latest_frame("mimir:kiyo-pro").sequence, 42)


if __name__ == "__main__":
    unittest.main()
