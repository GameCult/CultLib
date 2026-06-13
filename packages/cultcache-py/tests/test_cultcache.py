from __future__ import annotations

import json
import socket
import tempfile
import threading
import unittest
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

from cultcache_py import (
    CultCache,
    JsonLinesBackingStore,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
)
from cultcache_py.interop import read_note, write_note
from cultnet_py import (
    compute_simulation_claim_hash,
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
    shard_catalog_request,
    shard_log_request,
    simulation_observation,
    snapshot_request,
    witness_artifact_bundle,
)
from cultnet_py.interop_peer import wire_message_schema_descriptors
from cultmesh_py import create_node
from cultmesh_py import (
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshAuthorityLease,
    CultMeshAuthorityLeaseCatalog,
    CultMeshDiscoveryClient,
    CultMeshStreamCatalog,
    CultMeshStreamConsumerProfile,
    CultMeshStreamDescriptor,
    CultMeshStreamFrameHandle,
    CultMeshVerseCatalog,
    CultMeshVerseCompatibility,
    CultMeshVerseDescriptor,
    peer_exchange_request,
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

        consensus_schema = json.loads(by_version["cultnet.simulation_consensus_candidate.v0"]["schemaJson"])
        for required_field in ["witnessCount", "supportWeight", "totalWeight", "hasQuorum", "confidence"]:
            self.assertIn(required_field, consensus_schema["required"])

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

    def test_cultnet_witness_artifact_bundle_uses_csharp_slot_order(self) -> None:
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
        self.assertEqual(decoded["bundleId"], "bundle-1")
        self.assertEqual(decoded["witnessKind"], "interop-proof")
        self.assertEqual(decoded["subject"]["subjectId"], "note:python")
        self.assertEqual(decoded["contracts"][0]["schemaId"], "schema-a")
        self.assertEqual(decoded["artifacts"][0]["mediaType"], "text/plain")
        self.assertEqual(decoded["provenance"]["runtimeId"], "python-test")

    def test_cultmesh_node_uses_cultcache_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            document = define_database_entry_type("mesh.note", [("body", 0)])
            store_path = Path(tmp) / "mesh.cc"
            node = create_node(store_path, runtime_id="mesh-test")
            node.register_document(document)
            node.put(document, "note:1", {"body": "hello"})

            reopened = create_node(store_path, runtime_id="mesh-test")
            reopened.register_document(document)
            reopened.pull()
            self.assertEqual(reopened.get(document, "note:1")["body"], "hello")

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
