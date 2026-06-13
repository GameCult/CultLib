from __future__ import annotations

import tempfile
import unittest
from dataclasses import asdict, dataclass
from pathlib import Path

from cultcache_py import (
    CultCache,
    JsonLinesBackingStore,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
)
from cultcache_py.interop import read_note, write_note
from cultnet_py import decode_frame, encode_frame, hello, parse_message
from cultmesh_py import create_node
from cultmesh_py import (
    CultMeshPeerCard,
    CultMeshPeerCatalog,
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


if __name__ == "__main__":
    unittest.main()
