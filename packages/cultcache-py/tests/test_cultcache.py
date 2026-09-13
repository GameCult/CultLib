from __future__ import annotations

import tempfile
import unittest
from dataclasses import asdict, dataclass
from pathlib import Path
from cultcache_py.cache import CultCacheError
from cultcache_py import (
    CultCache,
    JsonLinesBackingStore,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
)
from cultcache_py.interop import read_note, write_note


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

    def test_messagepack_store_recovers_schema_stamped_record_missing_catalog_entry(self) -> None:
        try:
            import msgpack  # type: ignore
        except ModuleNotFoundError:
            self.skipTest("msgpack optional dependency is not installed")

        with tempfile.TemporaryDirectory() as tmp:
            document = define_database_entry_type(
                "runtime-policy",
                [
                    ("schema_version", 0),
                    ("name", 1),
                    ("value", 2),
                ],
                schema_id="schema-current",
                schema_name="tests.schema_stamped_entry",
                schema_version="tests.schema_stamped_entry.v1",
            )
            store_path = Path(tmp) / "missing-catalog.msgpack"
            store_path.write_bytes(msgpack.packb([
                "cultcache.store.v1",
                [],
                [[
                    "record-1",
                    "sha256:stale-schema-id-from-cold-record",
                    "2026-06-25T12:00:00Z",
                    msgpack.packb([
                        "tests.schema_stamped_entry.v1",
                        "schema-stamped",
                        "still readable",
                    ], use_bin_type=True),
                ]],
            ], use_bin_type=True))

            cache = (
                CultCache.builder()
                .register_document_type(document)
                .add_generic_store(SingleFileMessagePackBackingStore(store_path))
                .build()
            )

            cache.pull_all_backing_stores()
            self.assertEqual(cache.get_required(document, "record-1")["value"], "still readable")
            envelope = cache.get_required_envelope(document, "record-1")
            self.assertEqual(envelope.type, "runtime-policy")
            self.assertEqual(envelope.schema_id, "sha256:stale-schema-id-from-cold-record")

    def test_interop_cli_helpers_round_trip_v1_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            store_path = str(Path(tmp) / "cache.msgpack")
            written = write_note(store_path, "python-test")
            loaded = read_note(store_path)
            self.assertEqual(loaded["documentId"], written["documentId"])
            self.assertEqual(loaded["authorRuntimeId"], "python-test")
            self.assertIn("interop", loaded["tags"])

    def test_second_generic_store_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            cache = CultCache()
            cache.add_generic_store(SingleFileMessagePackBackingStore(Path(tmp) / "a.cc"))
            with self.assertRaisesRegex(CultCacheError, "second generic store"):
                cache.add_generic_store(SingleFileMessagePackBackingStore(Path(tmp) / "b.cc"))

    def test_type_claimed_twice_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            cache = CultCache()
            cache.add_backing_store(SingleFileMessagePackBackingStore(Path(tmp) / "a.cc"), ["item"])
            with self.assertRaisesRegex(CultCacheError, "already routed"):
                cache.add_backing_store(SingleFileMessagePackBackingStore(Path(tmp) / "b.cc"), ["settings", "item"])
            # A refused registration claims nothing, so "settings" is still free.
            cache.add_backing_store(SingleFileMessagePackBackingStore(Path(tmp) / "c.cc"), ["settings"])

    def test_types_route_to_home_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            item = item_doc()
            settings = define_database_entry_type("settings", [("theme", 0)])
            generic_path = Path(tmp) / "generic.cc"
            settings_path = Path(tmp) / "settings.cc"

            def build() -> CultCache:
                return (
                    CultCache.builder()
                    .register_document_type(item)
                    .register_document_type(settings)
                    .add_backing_store(SingleFileMessagePackBackingStore(settings_path), ["settings"])
                    .add_generic_store(SingleFileMessagePackBackingStore(generic_path))
                    .build()
                )

            cache = build()
            cache.put(item, "item:potion", Item(name="Potion", category="Consumable", value=50))
            cache.put(settings, "app", {"theme": "ash"})

            generic = SingleFileMessagePackBackingStore(generic_path).pull_all()
            routed = SingleFileMessagePackBackingStore(settings_path).pull_all()
            self.assertEqual([(e.key, e.type) for e in generic], [("item:potion", "item")])
            self.assertEqual([(e.key, e.type) for e in routed], [("app", "settings")])

            cache.delete(settings, "app")
            self.assertEqual(SingleFileMessagePackBackingStore(settings_path).pull_all(), [])
            self.assertEqual(len(SingleFileMessagePackBackingStore(generic_path).pull_all()), 1)

            reloaded = build()
            reloaded.pull_all_backing_stores()
            self.assertEqual(reloaded.get_required(item, "item:potion").value, 50)
            self.assertIsNone(reloaded.get(settings, "app"))

    def test_attach_that_would_move_a_held_record_home_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_database_entry_type("settings", [("theme", 0)])
            generic_path = Path(tmp) / "generic.cc"
            settings_path = Path(tmp) / "settings.cc"
            cache = (
                CultCache.builder()
                .register_document_type(settings)
                .add_generic_store(SingleFileMessagePackBackingStore(generic_path))
                .build()
            )
            cache.put(settings, "app", {"theme": "v1-generic"})

            with self.assertRaisesRegex(
                CultCacheError, "would move settings from the generic store to the store routed to settings"
            ):
                cache.add_backing_store(SingleFileMessagePackBackingStore(settings_path), ["settings"])
            # Nothing attached: the next write still lands in the generic store.
            cache.put(settings, "app", {"theme": "v2-generic"})
            generic = SingleFileMessagePackBackingStore(generic_path).pull_all()
            self.assertEqual([(e.key, e.type) for e in generic], [("app", "settings")])
            self.assertFalse(settings_path.exists())

    def test_load_from_a_store_that_is_not_the_record_home_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_database_entry_type("settings", [("theme", 0)])
            generic_path = Path(tmp) / "generic.cc"
            settings_path = Path(tmp) / "settings.cc"
            writer = CultCache.builder().register_document_type(settings).add_generic_store(
                SingleFileMessagePackBackingStore(generic_path)
            ).build()
            writer.put(settings, "app", {"theme": "stray"})
            generic_bytes = generic_path.read_bytes()

            cache = (
                CultCache.builder()
                .register_document_type(settings)
                .add_backing_store(SingleFileMessagePackBackingStore(settings_path), ["settings"])
                .add_generic_store(SingleFileMessagePackBackingStore(generic_path))
                .build()
            )
            with self.assertRaisesRegex(
                CultCacheError,
                "settings record app was loaded from the generic store, but its home is the store routed to settings",
            ):
                cache.pull_all_backing_stores()
            self.assertIsNone(cache.get(settings, "app"))
            self.assertEqual(cache.snapshot_envelopes(), [])
            self.assertEqual(generic_path.read_bytes(), generic_bytes)
            self.assertFalse(settings_path.exists())

    def test_attach_with_empty_type_list_is_the_generic_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_database_entry_type("settings", [("theme", 0)])
            store_path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(settings).build()
            cache.add_backing_store(SingleFileMessagePackBackingStore(store_path), [])
            cache.put(settings, "y", {"theme": "t"})
            self.assertEqual(
                [(e.key, e.type) for e in SingleFileMessagePackBackingStore(store_path).pull_all()],
                [("y", "settings")],
            )
            with self.assertRaisesRegex(CultCacheError, "second generic store"):
                cache.add_backing_store(SingleFileMessagePackBackingStore(Path(tmp) / "b.cc"), [])

    def test_zero_store_cache_writes_and_deletes_in_memory(self) -> None:
        settings = define_database_entry_type("settings", [("theme", 0)])
        cache = CultCache.builder().register_document_type(settings).build()
        cache.put(settings, "x", {"theme": "mem"})
        self.assertEqual(cache.get_required(settings, "x"), {"theme": "mem"})
        cache.delete(settings, "x")
        self.assertIsNone(cache.get(settings, "x"))

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


if __name__ == "__main__":
    unittest.main()
