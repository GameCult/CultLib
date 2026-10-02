from __future__ import annotations

import os
import tempfile
import threading
import unittest
from dataclasses import asdict, dataclass, replace
from pathlib import Path
from cultcache_py.backing_store import CultCacheEnvelope
from cultcache_py.cache import CultCacheError
from cultcache_py import (
    CultCache,
    JsonLinesBackingStore,
    SingleFileMessagePackBackingStore,
    StoreUnreadableError,
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

    def test_messagepack_store_refuses_a_record_whose_schema_the_catalog_does_not_publish(self) -> None:
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

            # The payload opens with a schema version, and the catalog is the store's own description: no recovery.
            with self.assertRaises(StoreUnreadableError):
                cache.pull_all_backing_stores()

    def _foreign_id_document(self):
        return define_database_entry_type(
            "tests.foreign-id",
            [("name", 0)],
            schema_id="tests.foreign-id.current",
            schema_name="tests.foreign-id",
            schema_version="tests.foreign_id.v1",
            compatible_schema_ids=["tests.foreign-id.current", "tests.foreign-id.older"],
        )

    def _open_foreign_id_store(self, document, path: Path) -> CultCache:
        cache = CultCache.builder().register_document_type(document).add_generic_store(
            SingleFileMessagePackBackingStore(path)
        ).build()
        cache.pull_all_backing_stores()
        return cache

    # A record put under an id its schema only lists as compatible is stamped with the registered id, and the store the write
    # leaves opens and takes the next write.
    def test_put_envelope_under_a_compatible_schema_id_writes_a_store_that_reopens(self) -> None:
        import msgpack  # type: ignore
        from cultcache_py import CultCacheSchemaCatalogEntry

        document = self._foreign_id_document()
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            cache = self._open_foreign_id_store(document, path)
            payload = document.encode_payload({"name": "foreign"})
            foreign_entry = CultCacheSchemaCatalogEntry(
                schema_id="tests.foreign-id.older",
                schema_name="tests.foreign-id",
                schema_version="tests.foreign_id.v1",
                content_hash="tests.foreign-id.older",
                canonical_schema_json="",
                compatible_schema_ids=("tests.foreign-id.older",),
                members=(),
            )
            cache.put_envelope(
                document,
                CultCacheEnvelope(
                    key="foreign",
                    type="tests.foreign-id",
                    payload=payload,
                    stored_at="2026-09-30T00:00:00Z",
                    schema_id="tests.foreign-id.older",
                    catalog_entry=foreign_entry,
                ),
            )
            stored = msgpack.unpackb(path.read_bytes(), raw=False)
            self.assertEqual([entry[0] for entry in stored[1]], ["tests.foreign-id.current"])
            self.assertEqual(stored[2][0][1], "tests.foreign-id.current")
            reopened = self._open_foreign_id_store(document, path)
            self.assertEqual(reopened.get_required(document, "foreign")["name"], "foreign")
            reopened.put(document, "next", {"name": "next"})
            self.assertEqual(self._open_foreign_id_store(document, path).get_required(document, "next")["name"], "next")

    def test_the_store_publishes_a_records_schema_id_even_when_the_envelopes_catalog_entry_does_not(self) -> None:
        import msgpack  # type: ignore
        from cultcache_py import CultCacheSchemaCatalogEntry

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            store = SingleFileMessagePackBackingStore(path)
            store.push(CultCacheEnvelope(
                key="k",
                type="tests.store-publishes",
                payload=msgpack.packb({"name": "k"}, use_bin_type=True),
                stored_at="2026-09-30T00:00:00Z",
                schema_id="tests.store-publishes.record",
                catalog_entry=CultCacheSchemaCatalogEntry(
                    schema_id="tests.store-publishes.other",
                    schema_name="tests.store-publishes",
                    schema_version="tests.store_publishes.v1",
                    content_hash="tests.store-publishes.other",
                    canonical_schema_json="",
                    compatible_schema_ids=("tests.store-publishes.other",),
                    members=(),
                ),
            ))
            stored = msgpack.unpackb(path.read_bytes(), raw=False)
            self.assertEqual(stored[2][0][1], "tests.store-publishes.record")
            self.assertEqual([entry[0] for entry in stored[1]], ["tests.store-publishes.record"])
            self.assertEqual(len(SingleFileMessagePackBackingStore(path).pull_all()), 1)

    # The catalog a write leaves is derived from its records: one entry per carried id, chosen and never merged, the same whatever
    # order the records arrive in. A registered (supplied) entry wins over an arrived (read back) entry with the same own id.
    @staticmethod
    def _writer_entry(schema_id: str, name: str, content_hash: str, compatible: tuple[str, ...]):
        from cultcache_py import CultCacheSchemaCatalogEntry

        return CultCacheSchemaCatalogEntry(
            schema_id=schema_id, schema_name=name, schema_version=name + ".v1", content_hash=content_hash,
            canonical_schema_json="", compatible_schema_ids=compatible, members=(),
        )

    def _writer_record(self, key: str, type: str, schema_id: str, catalog_entry=None) -> CultCacheEnvelope:
        import msgpack  # type: ignore

        return CultCacheEnvelope(
            key=key, type=type, payload=msgpack.packb({"key": key}, use_bin_type=True),
            stored_at="2026-09-30T00:00:00Z", schema_id=schema_id, catalog_entry=catalog_entry,
        )

    @staticmethod
    def _raw_entry(entry) -> list:
        return [entry.schema_id, entry.schema_name, entry.schema_version, entry.content_hash, "", list(entry.compatible_schema_ids), []]

    def test_a_registered_entry_is_written_as_it_is_over_the_arrived_entries_with_the_same_own_id_in_either_order(self) -> None:
        import msgpack  # type: ignore

        stale1 = self._writer_entry("tests.x", "tests.n", "a-stale-1", ("tests.x", "old"))
        stale2 = self._writer_entry("tests.x", "tests.n", "a-stale-2", ("tests.x",))
        registered = self._writer_record("a", "tests.n", "tests.x", self._writer_entry("tests.x", "tests.n", "fresh", ("tests.x",)))
        for entries in ([stale1, stale2], [stale2, stale1]):
            with tempfile.TemporaryDirectory() as tmp:
                path = Path(tmp) / "store.msgpack"
                path.write_bytes(msgpack.packb([
                    "cultcache.store.v1", [self._raw_entry(entry) for entry in entries],
                    [["b", "tests.x", "2026-09-30T00:00:00Z", msgpack.packb({"b": 1}, use_bin_type=True)]],
                ], use_bin_type=True))
                SingleFileMessagePackBackingStore(path).push(registered)
                catalog = msgpack.unpackb(path.read_bytes(), raw=False)[1]
                self.assertEqual(len(catalog), 1)
                self.assertEqual(catalog[0][3], "fresh")
                self.assertEqual(catalog[0][5], ["tests.x"], "written as chosen, no union with the arrived entries' ids")

    def test_a_rename_with_a_stable_schema_id_a_whole_view_write_names_the_registered_schema(self) -> None:
        import msgpack  # type: ignore

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            path.write_bytes(msgpack.packb([
                "cultcache.store.v1", [self._raw_entry(self._writer_entry("tests.x", "tests.old", "stale", ("tests.x",)))],
                [["a", "tests.x", "2026-09-30T00:00:00Z", msgpack.packb({"a": 1}, use_bin_type=True)]],
            ], use_bin_type=True))
            SingleFileMessagePackBackingStore(path).push_all(
                [self._writer_record("a", "tests.old", "tests.x", self._writer_entry("tests.x", "tests.new", "fresh", ("tests.x",)))]
            )
            catalog = msgpack.unpackb(path.read_bytes(), raw=False)[1]
            self.assertEqual([(entry[1], entry[3]) for entry in catalog], [("tests.new", "fresh")])

    def test_two_entries_of_one_tier_that_share_an_own_id_and_disagree_on_the_schema_name_refuse_the_write_typed(self) -> None:
        from cultcache_py import SchemaConflictError

        first = self._writer_record("b", "tests.first", "tests.x", self._writer_entry("tests.x", "tests.first", "h1", ("tests.x",)))
        second = self._writer_record("a", "tests.first", "tests.x", self._writer_entry("tests.x", "tests.second", "h2", ("tests.x",)))
        for order in ([first, second], [second, first]):
            with tempfile.TemporaryDirectory() as tmp:
                path = Path(tmp) / "store.msgpack"
                with self.assertRaises(SchemaConflictError) as refused:
                    SingleFileMessagePackBackingStore(path).push_all(order)
                self.assertEqual(refused.exception.schema_id, "tests.x")
                self.assertEqual(refused.exception.record_key, "a")
                self.assertEqual(refused.exception.schema_names, ["tests.first", "tests.second"])
                self.assertFalse(path.exists())

    def test_records_of_different_types_under_one_schema_id_refuse_the_write_typed_and_no_record_changes_type(self) -> None:
        from cultcache_py import SchemaConflictError

        a = self._writer_record("a", "tests.n", "tests.y", self._writer_entry("tests.x", "tests.n", "h1", ("tests.x", "tests.y")))
        b = self._writer_record("b", "tests.m", "tests.y", self._writer_entry("tests.y", "tests.m", "h2", ("tests.y",)))
        for order in ([a, b], [b, a]):
            with tempfile.TemporaryDirectory() as tmp:
                with self.assertRaises(SchemaConflictError) as refused:
                    SingleFileMessagePackBackingStore(Path(tmp) / "store.msgpack").push_all(order)
                self.assertEqual(refused.exception.schema_id, "tests.y")
                self.assertEqual(refused.exception.schema_names, ["tests.m", "tests.n"])

    def test_an_entry_that_owns_an_id_is_chosen_over_one_that_lists_it(self) -> None:
        import msgpack  # type: ignore

        owner = self._writer_record("a", "tests.n", "tests.y", self._writer_entry("tests.y", "tests.n", "h1", ("tests.y",)))
        lister = self._writer_record("b", "tests.n", "tests.x", self._writer_entry("tests.x", "tests.n", "h2", ("tests.x", "tests.y")))
        for order in ([lister, owner], [owner, lister]):
            with tempfile.TemporaryDirectory() as tmp:
                path = Path(tmp) / "store.msgpack"
                SingleFileMessagePackBackingStore(path).push_all(order)
                catalog = msgpack.unpackb(path.read_bytes(), raw=False)[1]
                self.assertEqual(sorted((entry[0], entry[5]) for entry in catalog), [("tests.x", ["tests.x", "tests.y"]), ("tests.y", ["tests.y"])])
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            SingleFileMessagePackBackingStore(path).push_all([owner])
            self.assertEqual([entry[0] for entry in msgpack.unpackb(path.read_bytes(), raw=False)[1]], ["tests.y"])

    def test_entries_of_one_tier_that_tie_are_taken_in_one_fixed_order_and_a_registered_lister_beats_an_arrived_one(self) -> None:
        import msgpack  # type: ignore

        # The content hash orders them against the order their compatible ids would give: h1 sorts first, its list sorts last.
        one = self._writer_record("a", "tests.n", "tests.x", self._writer_entry("tests.x", "tests.n", "h2", ("tests.x",)))
        two = self._writer_record("b", "tests.n", "tests.x", self._writer_entry("tests.x", "tests.n", "h1", ("tests.x", "tests.y")))
        for order in ([one, two], [two, one]):
            with tempfile.TemporaryDirectory() as tmp:
                path = Path(tmp) / "store.msgpack"
                SingleFileMessagePackBackingStore(path).push_all(order)
                self.assertEqual([entry[3] for entry in msgpack.unpackb(path.read_bytes(), raw=False)[1]], ["h1"])

        # z sits under old, which the arrived entry (id x) lists; the supplied entry (id r) lists it too: the registered one publishes it.
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            path.write_bytes(msgpack.packb([
                "cultcache.store.v1", [self._raw_entry(self._writer_entry("tests.x", "tests.n", "stale", ("tests.x", "old")))],
                [["z", "old", "2026-09-30T00:00:00Z", msgpack.packb({"z": 1}, use_bin_type=True)]],
            ], use_bin_type=True))
            SingleFileMessagePackBackingStore(path).push(
                self._writer_record("a", "tests.n", "tests.r", self._writer_entry("tests.r", "tests.n", "fresh", ("tests.r", "old")))
            )
            self.assertEqual([entry[0] for entry in msgpack.unpackb(path.read_bytes(), raw=False)[1]], ["tests.r"])

    # A registered entry that owns an id keeps it against an arrived entry that owns it too and lists the id another record sits
    # under, whichever of the two ids sorts first: the arrived entry's list is not merged in, so the record under the listed id is
    # published by no chosen entry.
    def test_an_arrived_entry_that_owns_a_registered_id_does_not_replace_it_nor_publish_what_it_lists(self) -> None:
        import msgpack  # type: ignore
        from cultcache_py import SchemaConflictError

        for own_id, listed_id in (("tests.a", "tests.b"), ("tests.b", "tests.a")):
            with tempfile.TemporaryDirectory() as tmp:
                path = Path(tmp) / "store.msgpack"
                path.write_bytes(msgpack.packb([
                    "cultcache.store.v1", [self._raw_entry(self._writer_entry(own_id, "tests.arrived", "stale", (own_id, listed_id)))],
                    [["k2", listed_id, "2026-09-30T00:00:00Z", msgpack.packb({"k": 2}, use_bin_type=True)]],
                ], use_bin_type=True))
                before = path.read_bytes()
                with self.assertRaises(SchemaConflictError) as refused:
                    SingleFileMessagePackBackingStore(path).push(
                        self._writer_record("k1", "tests.registered", own_id, self._writer_entry(own_id, "tests.registered", "fresh", (own_id,)))
                    )
                self.assertEqual(refused.exception.schema_id, listed_id)
                self.assertEqual(refused.exception.record_key, "k2")
                self.assertEqual(path.read_bytes(), before, "the file is left as it was")

    # Ids are taken in sorted order, so which entry survives, and so which id the refusal names, does not depend on the order the
    # records arrive in.
    def test_the_refusal_names_the_same_id_whatever_order_the_records_arrive_in(self) -> None:
        from cultcache_py import SchemaConflictError

        one = self._writer_record("r1", "tests.n", "tests.y", self._writer_entry("tests.x", "tests.n", "h1", ("tests.x", "tests.y")))
        two = self._writer_record("r2", "tests.n", "tests.z", self._writer_entry("tests.x", "tests.n", "h2", ("tests.x", "tests.z")))
        for order in ([one, two], [two, one]):
            with tempfile.TemporaryDirectory() as tmp:
                with self.assertRaises(SchemaConflictError) as refused:
                    SingleFileMessagePackBackingStore(Path(tmp) / "store.msgpack").push_all(order)
                self.assertEqual(refused.exception.schema_id, "tests.y")
                self.assertEqual(refused.exception.record_key, "r1")

    # A write may not retype records already in the file: across tiers the id keeps the type its records resolve to, unless the
    # entries of both tiers own it, which is a rename under a stable id and the registered descriptor wins.
    def test_a_write_that_would_change_the_type_an_existing_record_resolves_to_refuses_typed_and_leaves_the_file(self) -> None:
        import msgpack  # type: ignore
        from cultcache_py import SchemaConflictError

        cases = [
            ("registered entry lists the id, arrived entry owns it",
             self._writer_entry("tests.x", "tests.arrived", "h1", ("tests.x",)),
             self._writer_record("a", "tests.registered", "tests.x", self._writer_entry("tests.r", "tests.registered", "h2", ("tests.r", "tests.x")))),
            ("registered entry owns the id, arrived entry lists it",
             self._writer_entry("tests.r", "tests.arrived", "h1", ("tests.r", "tests.x")),
             self._writer_record("a", "tests.registered", "tests.x", self._writer_entry("tests.x", "tests.registered", "h2", ("tests.x",)))),
        ]
        payload = msgpack.packb({"z": 1}, use_bin_type=True)
        with tempfile.TemporaryDirectory() as tmp:
            for name, catalog, registered in cases:
                path = Path(tmp) / (name.split(",")[0].replace(" ", "-") + ".msgpack")
                path.write_bytes(msgpack.packb(
                    ["cultcache.store.v1", [self._raw_entry(catalog)], [["z", "tests.x", "2026-09-30T00:00:00Z", payload]]], use_bin_type=True))
                before = path.read_bytes()
                with self.assertRaises(SchemaConflictError, msg=name) as refused:
                    SingleFileMessagePackBackingStore(path).push(registered)
                self.assertEqual(refused.exception.schema_id, "tests.x", name)
                self.assertEqual(sorted(refused.exception.schema_names), ["tests.arrived", "tests.registered"], name)
                self.assertEqual(path.read_bytes(), before, name)

            # The same registered record renames the schema when both tiers own the id: it writes, and the reader finds every record.
            path = Path(tmp) / "rename.msgpack"
            path.write_bytes(msgpack.packb([
                "cultcache.store.v1", [self._raw_entry(self._writer_entry("tests.x", "tests.arrived", "h1", ("tests.x",)))],
                [["z", "tests.x", "2026-09-30T00:00:00Z", payload]],
            ], use_bin_type=True))
            SingleFileMessagePackBackingStore(path).push(
                self._writer_record("a", "tests.registered", "tests.x", self._writer_entry("tests.x", "tests.registered", "h2", ("tests.x",)))
            )
            self.assertEqual([(entry[0], entry[1]) for entry in msgpack.unpackb(path.read_bytes(), raw=False)[1]], [("tests.x", "tests.registered")])
            self.assertEqual(len(SingleFileMessagePackBackingStore(path).pull_all()), 2)

    def test_a_record_no_chosen_entry_publishes_refuses_the_write_typed_and_leaves_the_file(self) -> None:
        import msgpack  # type: ignore
        from cultcache_py import SchemaConflictError

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            path.write_bytes(msgpack.packb([
                "cultcache.store.v1", [self._raw_entry(self._writer_entry("tests.x", "tests.legacy", "stale", ("tests.x", "old")))],
                [["z", "old", "2026-09-30T00:00:00Z", msgpack.packb({"z": 1}, use_bin_type=True)]],
            ], use_bin_type=True))
            before = path.read_bytes()
            with self.assertRaises(SchemaConflictError) as refused:
                SingleFileMessagePackBackingStore(path).push(
                    self._writer_record("a", "tests.legacy", "tests.x", self._writer_entry("tests.x", "tests.legacy", "fresh", ("tests.x",)))
                )
            self.assertEqual(refused.exception.schema_id, "old")
            self.assertEqual(refused.exception.record_key, "z")
            self.assertEqual(path.read_bytes(), before)

    def test_an_id_an_entry_owns_names_that_entry_not_one_that_lists_it_as_compatible(self) -> None:
        for vector in ("own-id-over-compatible-v3.bin", "compatible-before-owner-v3.bin"):
            store = SingleFileMessagePackBackingStore(self._C2A_VECTORS / "readability" / vector)
            self.assertEqual([envelope.type for envelope in store.pull_all()], ["vectors.item", "vectors.item"], vector)

    def test_a_record_loaded_under_a_compatible_schema_id_is_written_back_under_the_id_its_catalog_entry_carries(self) -> None:
        import msgpack  # type: ignore

        document = self._foreign_id_document()
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            path.write_bytes(msgpack.packb([
                "cultcache.store.v1",
                [["tests.foreign-id.current", "tests.foreign-id", "tests.foreign_id.v1", "tests.foreign-id.current", "",
                  ["tests.foreign-id.current", "tests.foreign-id.older"], []]],
                [["old", "tests.foreign-id.older", "2026-09-30T00:00:00Z", document.encode_payload({"name": "old"})]],
            ], use_bin_type=True))
            cache = self._open_foreign_id_store(document, path)
            self.assertEqual(cache.get_required_envelope(document, "old").schema_id, "tests.foreign-id.current")
            cache.put(document, "next", {"name": "next"})
            reopened = self._open_foreign_id_store(document, path)
            self.assertEqual(reopened.get_required(document, "old")["name"], "old")
            self.assertEqual(reopened.get_required(document, "next")["name"], "next")

    # The cache restamps a record on load, so a store's writer never has to: a record loaded under an id the registered document
    # only lists is held, and written back by a whole write, under the id its catalog entry carries. A document that does not list
    # the id a record sits under cannot have that record published by a write that leaves it in place: the put is refused, typed.
    def test_a_cache_that_does_not_list_the_id_a_loaded_record_sits_under_is_refused_a_put_and_the_file_is_left(self) -> None:
        import msgpack  # type: ignore
        from cultcache_py import SchemaConflictError

        document = define_database_entry_type(
            "tests.foreign-id", [("name", 0)], schema_id="tests.foreign-id.current",
            schema_name="tests.foreign-id", schema_version="tests.foreign_id.v1",
        )
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            path.write_bytes(msgpack.packb([
                "cultcache.store.v1",
                [["tests.foreign-id.current", "tests.foreign-id", "tests.foreign_id.v1", "tests.foreign-id.current", "",
                  ["tests.foreign-id.current", "tests.foreign-id.older"], []]],
                [["old", "tests.foreign-id.older", "2026-09-30T00:00:00Z", document.encode_payload({"name": "old"})]],
            ], use_bin_type=True))
            before = path.read_bytes()
            cache = self._open_foreign_id_store(document, path)
            self.assertEqual(cache.get_required_envelope(document, "old").schema_id, "tests.foreign-id.current")
            with self.assertRaises(SchemaConflictError) as refused:
                cache.put(document, "next", {"name": "next"})
            self.assertEqual(refused.exception.schema_id, "tests.foreign-id.older")
            self.assertEqual(refused.exception.record_key, "old")
            self.assertEqual(path.read_bytes(), before)

    # A record resolves to a local document by its schema id, never by the name its catalog entry carries: a schema renamed under
    # a stable id opens, and an id that names one document is not overruled by a name that names another.
    def _store_of(self, path: Path, catalog: list, records: list) -> None:
        import msgpack  # type: ignore

        path.write_bytes(msgpack.packb(["cultcache.store.v1", catalog, records], use_bin_type=True))

    def test_a_schema_renamed_under_a_stable_id_opens_by_its_id(self) -> None:
        document = define_database_entry_type(
            "tests.old", [("name", 0)], schema_id="tests.stable", schema_name="tests.old", schema_version="tests.old.v1",
        )
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.msgpack"
            self._store_of(
                path,
                [["tests.stable", "tests.renamed", "tests.renamed.v1", "renamed", "", ["tests.stable"], []]],
                [["a", "tests.stable", "2026-09-30T00:00:00Z", document.encode_payload({"name": "a"})]],
            )
            cache = self._open_foreign_id_store(document, path)
            self.assertEqual(cache.get_required(document, "a")["name"], "a")

    def test_a_schema_id_that_names_one_document_is_not_overruled_by_a_name_that_names_another(self) -> None:
        first = define_database_entry_type("tests.a", [("name", 0)], schema_id="id.a", schema_name="tests.a", schema_version="tests.a.v1")
        second = define_database_entry_type(
            "tests.b", [("name", 0)], schema_id="id.b", schema_name="tests.b", schema_version="tests.b.v1", compatible_schema_ids=["id.b", "id.b.older"],
        )
        with tempfile.TemporaryDirectory() as tmp:
            for schema_id in ("id.b", "id.b.older"):
                path = Path(tmp) / (schema_id + ".msgpack")
                # The entry that publishes the id carries the other document's name.
                self._store_of(
                    path,
                    [["id.b", "tests.a", "tests.b.v1", "h", "", ["id.b", "id.b.older"], []]],
                    [["k", schema_id, "2026-09-30T00:00:00Z", second.encode_payload({"name": "k"})]],
                )
                cache = CultCache.builder().register_document_type(first).register_document_type(second).add_generic_store(
                    SingleFileMessagePackBackingStore(path)
                ).build()
                cache.pull_all_backing_stores()
                self.assertEqual(cache.get_required(second, "k")["name"], "k", schema_id)
                self.assertIsNone(cache.get(first, "k"), schema_id)

    def _assert_second_claim_refused(self, first, second, schema_id: str) -> None:
        """A registration refusal is typed, names the id and both schema names and document types, and the cache keeps what it
        had."""
        from cultcache_py import SchemaConflictError

        cache = CultCache()
        cache.register_document_type(first)
        with self.assertRaises(SchemaConflictError) as refused:
            cache.register_document_type(second)
        self.assertEqual(refused.exception.schema_id, schema_id)
        self.assertEqual(refused.exception.schema_names, [first.catalog_entry().schema_name, second.catalog_entry().schema_name])
        self.assertIn(first.type, str(refused.exception))
        self.assertIn(second.type, str(refused.exception))
        if second is not first:
            with self.assertRaisesRegex(CultCacheError, "not registered"):
                cache.put(second, "k", {"name": "k"})

    def _pull(self, documents: list, catalog: list, records: list) -> CultCache:
        tmp = tempfile.mkdtemp()
        path = Path(tmp) / "store.msgpack"
        self._store_of(path, catalog, records)
        builder = CultCache.builder()
        for document in documents:
            builder = builder.register_document_type(document)
        cache = builder.add_generic_store(SingleFileMessagePackBackingStore(path)).build()
        cache.pull_all_backing_stores()
        return cache

    def test_a_document_listing_an_id_another_owns_registers_beside_it_in_either_order_and_the_owner_wins(self) -> None:
        owner = define_database_entry_type("tests.owner", [("name", 0)], schema_id="id.owned", schema_name="tests.owner", schema_version="tests.owner.v1")
        lister = define_database_entry_type(
            "tests.lister", [("name", 0)], schema_id="id.lister", schema_name="tests.lister", schema_version="tests.lister.v1",
            compatible_schema_ids=["id.lister", "id.owned"],
        )
        catalog = [
            ["id.owned", "tests.owner", "tests.owner.v1", "h1", "", ["id.owned"], []],
            ["id.lister", "tests.lister", "tests.lister.v1", "h2", "", ["id.lister", "id.owned"], []],
        ]
        records = [
            ["o", "id.owned", "2026-09-30T00:00:00Z", owner.encode_payload({"name": "o"})],
            ["l", "id.lister", "2026-09-30T00:00:00Z", lister.encode_payload({"name": "l"})],
        ]
        for order in ([owner, lister], [lister, owner]):
            cache = self._pull(order, catalog, records)
            self.assertEqual(cache.get_required(owner, "o")["name"], "o")
            self.assertIsNone(cache.get(lister, "o"))
            self.assertEqual(cache.get_required(lister, "l")["name"], "l")
        self.assertEqual(self._pull([lister], catalog, records[:1]).get_required(lister, "o")["name"], "o")

    def test_two_documents_listing_one_id_register_and_a_record_under_it_needs_an_owner(self) -> None:
        from cultcache_py import SchemaConflictError

        a = define_database_entry_type(
            "tests.list-a", [("name", 0)], schema_id="id.a", schema_name="tests.list-a", compatible_schema_ids=["id.a", "id.shared"],
        )
        b = define_database_entry_type(
            "tests.list-b", [("name", 0)], schema_id="id.b", schema_name="tests.list-b", compatible_schema_ids=["id.b", "id.shared"],
        )
        owner = define_database_entry_type("tests.shared", [("name", 0)], schema_id="id.shared", schema_name="tests.shared")
        catalog = [["id.shared", "tests.shared", "tests.shared.v1", "h", "", ["id.shared"], []]]
        records = [["k", "id.shared", "2026-09-30T00:00:00Z", owner.encode_payload({"name": "k"})]]
        for listers in ([a, b], [b, a]):
            with self.assertRaises(SchemaConflictError) as refused:
                self._pull(listers, catalog, records)
            self.assertEqual(refused.exception.schema_id, "id.shared")
            self.assertEqual(refused.exception.record_key, "k")
            self.assertEqual(refused.exception.schema_names, ["tests.list-a", "tests.list-b"])
            for documents in ([*listers, owner], [owner, *listers]):
                cache = self._pull(documents, catalog, records)
                self.assertEqual(cache.get_required(owner, "k")["name"], "k")

    def test_two_documents_owning_one_schema_id_are_refused_in_either_order(self) -> None:
        # Each declares an older id and not its own, so the id they share is only ever their own.
        a = define_database_entry_type("tests.own-a", [("name", 0)], schema_id="id.same", schema_name="tests.own-a", compatible_schema_ids=["id.a-older"])
        b = define_database_entry_type("tests.own-b", [("name", 0)], schema_id="id.same", schema_name="tests.own-b", compatible_schema_ids=["id.b-older"])
        self._assert_second_claim_refused(a, b, "id.same")
        self._assert_second_claim_refused(b, a, "id.same")

    def test_every_registration_refusal_is_a_schema_conflict(self) -> None:
        first = define_database_entry_type("tests.named", [("name", 0)], schema_id="id.first", schema_name="tests.named")
        same_name = define_database_entry_type("tests.renamed", [("name", 0)], schema_id="id.second", schema_name="tests.named")
        self._assert_second_claim_refused(first, same_name, "id.second")
        self._assert_second_claim_refused(first, first, "id.first")

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
            # The cache already holds a record, in its home store, before the refused load.
            cache.put(settings, "held", {"theme": "home"})
            before = cache.snapshot_envelopes()
            settings_bytes = settings_path.read_bytes()
            with self.assertRaisesRegex(
                CultCacheError,
                "settings record app was loaded from the generic store, but its home is the store routed to settings",
            ):
                cache.pull_all_backing_stores()
            self.assertIsNone(cache.get(settings, "app"))
            self.assertEqual(cache.snapshot_envelopes(), before)
            self.assertEqual(cache.get_required(settings, "held"), {"theme": "home"})
            self.assertEqual(generic_path.read_bytes(), generic_bytes)
            self.assertEqual(settings_path.read_bytes(), settings_bytes)

    def test_pull_with_duplicate_global_leaves_cache_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            item = item_doc()
            settings = define_document_type("settings", global_document=True)
            store_path = Path(tmp) / "generic.cc"
            cache = (
                CultCache.builder()
                .register_document_type(item)
                .register_document_type(settings)
                .add_generic_store(SingleFileMessagePackBackingStore(store_path))
                .build()
            )
            cache.put(item, "item:potion", Item(name="Potion", category="Consumable", value=50))
            cache.put_global(settings, {"theme": "ash"})
            before = cache.snapshot_envelopes()

            # A second record of the global type lands in the store file behind the cache's back.
            stray = replace(cache.get_required_envelope(settings, CultCache.GLOBAL_KEY), key="stray")
            SingleFileMessagePackBackingStore(store_path).push(stray)

            with self.assertRaisesRegex(CultCacheError, "Duplicate global document for type: settings"):
                cache.pull_all_backing_stores()
            self.assertEqual(cache.snapshot_envelopes(), before)
            self.assertEqual(cache.get_required(item, "item:potion").value, 50)
            self.assertEqual(cache.get_key_by_name(item, "Potion"), "item:potion")
            self.assertEqual(cache.get_required_global(settings)["theme"], "ash")

    def test_pull_with_throwing_index_accessor_leaves_cache_unchanged(self) -> None:
        def category(item: Item) -> str:
            if item.name == "Bomb":
                raise ValueError("category accessor refuses Bomb")
            return item.category

        with tempfile.TemporaryDirectory() as tmp:
            item = define_document_type(
                "item",
                encode=lambda value: asdict(value),
                decode=lambda raw: Item(**raw),
                name="name",
                indexes={"category": category},
            )
            settings = define_document_type("settings", global_document=True)
            store_path = Path(tmp) / "generic.cc"
            cache = (
                CultCache.builder()
                .register_document_type(item)
                .register_document_type(settings)
                .add_generic_store(SingleFileMessagePackBackingStore(store_path))
                .build()
            )
            cache.put(item, "item:potion", Item(name="Potion", category="Consumable", value=50))
            cache.put_global(settings, {"theme": "ash"})
            before_envelopes = cache.snapshot_envelopes()
            before_values = cache.snapshot()

            # A record whose index accessor throws lands in the store file behind the cache's back.
            bomb = replace(
                cache.get_required_envelope(item, "item:potion"),
                key="item:bomb",
                payload=item.encode_payload(Item(name="Bomb", category="Explosive", value=1)),
            )
            SingleFileMessagePackBackingStore(store_path).push(bomb)

            with self.assertRaisesRegex(ValueError, "category accessor refuses Bomb"):
                cache.pull_all_backing_stores()
            self.assertEqual(cache.snapshot_envelopes(), before_envelopes)
            self.assertEqual(cache.snapshot(), before_values)
            self.assertEqual(cache.get_required(item, "item:potion").value, 50)
            self.assertIsNone(cache.get(item, "item:bomb"))
            self.assertEqual(cache.get_key_by_name(item, "Potion"), "item:potion")
            self.assertIsNone(cache.get_key_by_name(item, "Bomb"))
            self.assertEqual(cache.get_key_by_index(item, "category", "Consumable"), "item:potion")
            self.assertEqual(cache.get_required_global(settings)["theme"], "ash")

    def _store_state(self, path: Path) -> list[tuple[str, str, bytes]]:
        return sorted((e.type, e.key, e.payload) for e in SingleFileMessagePackBackingStore(path).pull_all())

    def test_put_with_throwing_name_extractor_changes_neither_store_nor_cache(self) -> None:
        def name(value: dict) -> str:
            if value["n"] == "bad":
                raise ValueError("extractor refuses bad")
            return value["n"]

        with tempfile.TemporaryDirectory() as tmp:
            item = define_document_type("item", name=name)
            path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(item).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            cache.put(item, "k", {"n": "good"})
            store_before, cache_before = self._store_state(path), cache.snapshot_envelopes()

            with self.assertRaisesRegex(ValueError, "extractor refuses bad"):
                cache.put(item, "k", {"n": "bad"})
            self.assertEqual(self._store_state(path), store_before)
            self.assertEqual(cache.snapshot_envelopes(), cache_before)
            self.assertEqual(cache.get_required(item, "k"), {"n": "good"})
            self.assertEqual(cache.get_key_by_name(item, "good"), "k")

    def test_overwrite_under_a_registered_extractor_that_now_raises_changes_neither_store_nor_cache(self) -> None:
        refusing = {"on": False}

        def category(value: dict) -> str:
            if refusing["on"]:
                raise ValueError("extractor refuses now")
            return value["c"]

        with tempfile.TemporaryDirectory() as tmp:
            item = define_document_type("item")
            path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(item).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            cache.put(item, "k", {"c": "old"})
            cache.register_index(item, "category", category)
            refusing["on"] = True
            store_before, cache_before = self._store_state(path), cache.snapshot_envelopes()

            with self.assertRaisesRegex(ValueError, "extractor refuses now"):
                cache.put(item, "k", {"c": "new"})
            self.assertEqual(self._store_state(path), store_before)
            self.assertEqual(cache.snapshot_envelopes(), cache_before)
            self.assertEqual(cache.get_key_by_index(item, "category", "old"), "k")
            # Deleting runs no extractor: it uses the lookup keys stored when the record was admitted.
            cache.delete(item, "k")
            self.assertIsNone(cache.get_key_by_index(item, "category", "old"))

    def test_registering_an_extractor_that_raises_on_a_held_value_installs_nothing(self) -> None:
        def name(value: dict) -> str:
            if value["n"] == "bad":
                raise ValueError("extractor refuses bad")
            return value["n"]

        with tempfile.TemporaryDirectory() as tmp:
            item = define_document_type("item")
            path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(item).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            cache.put(item, "old", {"n": "bad"})
            with self.assertRaisesRegex(ValueError, "extractor refuses bad"):
                cache.register_name_lookup(item, name)
            cache.put(item, "old", {"n": "fine"})
            self.assertEqual(cache.get_required(item, "old"), {"n": "fine"})
            self.assertIsNone(cache.get_key_by_name(item, "fine"))
            self.assertEqual([item.decode_payload(e.payload) for e in SingleFileMessagePackBackingStore(path).pull_all()], [{"n": "fine"}])

    def test_put_envelopes_with_throwing_extractor_changes_neither_store_nor_cache(self) -> None:
        def name(value: dict) -> str:
            if value["n"] == "bad":
                raise ValueError("extractor refuses bad")
            return value["n"]

        with tempfile.TemporaryDirectory() as tmp:
            item = define_document_type("item", name=name)
            path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(item).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            cache.put(item, "seed", {"n": "seed"})
            seed = cache.get_required_envelope(item, "seed")
            first = replace(seed, key="a", payload=item.encode_payload({"n": "a"}))
            second = replace(seed, key="b", payload=item.encode_payload({"n": "bad"}))
            store_before, cache_before = self._store_state(path), cache.snapshot_envelopes()

            with self.assertRaisesRegex(ValueError, "extractor refuses bad"):
                cache.put_envelopes(item, [first, second])
            self.assertEqual(self._store_state(path), store_before)
            self.assertEqual(cache.snapshot_envelopes(), cache_before)
            self.assertIsNone(cache.get_key_by_name(item, "a"))

    def test_global_replace_with_failing_store_push_keeps_old_global(self) -> None:
        class FailingPushStore(SingleFileMessagePackBackingStore):
            fail = False

            def push(self, envelope):
                if self.fail:
                    raise OSError("disk full")
                super().push(envelope)

        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            path = Path(tmp) / "generic.cc"
            store = FailingPushStore(path)
            cache = CultCache.builder().register_document_type(settings).add_generic_store(store).build()
            cache.put_global(settings, {"theme": "base"})
            store_before, cache_before = self._store_state(path), cache.snapshot_envelopes()
            store.fail = True

            with self.assertRaisesRegex(OSError, "disk full"):
                cache.put_global(settings, {"theme": "new"})
            with self.assertRaisesRegex(CultCacheError, "must use key __global__"):
                cache.put(settings, "other", {"theme": "new"})
            self.assertEqual(self._store_state(path), store_before)
            self.assertEqual(cache.snapshot_envelopes(), cache_before)
            self.assertEqual(cache.get_required_global(settings), {"theme": "base"})

    @staticmethod
    def _pausing_store(path: Path, barrier: threading.Barrier, resume: threading.Event):
        """A store whose first push writes, meets the other thread at the barrier, then waits until that
        thread's call has returned (or a timeout: a locked cache makes the other thread wait on us)."""

        class PausingStore(SingleFileMessagePackBackingStore):
            pushes = 0
            paused = False
            other_reached_store_while_paused = False

            def push(self, envelope):
                self.other_reached_store_while_paused |= self.paused
                self.pushes += 1
                super().push(envelope)
                if self.pushes == 1:
                    self.paused = True
                    barrier.wait(timeout=5)
                    resume.wait(timeout=0.5)
                    self.paused = False

        return PausingStore(path)

    @staticmethod
    def _run_racing(first, second, resume: threading.Event) -> list[BaseException]:
        errors: list[BaseException] = []

        def run(action, signal):
            try:
                action()
            except BaseException as error:  # noqa: BLE001 - reported to the test
                errors.append(error)
            finally:
                if signal:
                    resume.set()

        threads = [threading.Thread(target=run, args=(first, False)), threading.Thread(target=run, args=(second, True))]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=10)
        return errors

    def test_concurrent_put_and_attach_cannot_land_record_in_non_home_store(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_database_entry_type("settings", [("theme", 0)])
            generic_path, settings_path = Path(tmp) / "generic.cc", Path(tmp) / "settings.cc"
            barrier, resume = threading.Barrier(2), threading.Event()
            generic = self._pausing_store(generic_path, barrier, resume)
            cache = CultCache.builder().register_document_type(settings).add_generic_store(generic).build()

            def attach() -> None:
                barrier.wait(timeout=5)
                cache.add_backing_store(SingleFileMessagePackBackingStore(settings_path), ["settings"])

            errors = self._run_racing(lambda: cache.put(settings, "app", {"theme": "t"}), attach, resume)

            # Either the attach was refused (the record is held, so its home cannot move) or it won;
            # in every case each held record must be in the store that is its home.
            home = SingleFileMessagePackBackingStore(generic_path if errors else settings_path)
            self.assertEqual([(e.key, e.type) for e in home.pull_all()], [("app", "settings")])
            self.assertTrue(all(isinstance(error, CultCacheError) for error in errors), errors)

    def test_concurrent_global_puts_cannot_both_reach_disk(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            path = Path(tmp) / "generic.cc"
            barrier, resume = threading.Barrier(2), threading.Event()
            store = self._pausing_store(path, barrier, resume)
            cache = CultCache.builder().register_document_type(settings).add_generic_store(store).build()

            def second() -> None:
                barrier.wait(timeout=5)
                cache.put_global(settings, {"theme": "second"})

            errors = self._run_racing(lambda: cache.put_global(settings, {"theme": "first"}), second, resume)

            self.assertEqual(errors, [])
            # While the first write was between its store push and its apply, the second never reached disk.
            self.assertFalse(store.other_reached_store_while_paused)
            on_disk = SingleFileMessagePackBackingStore(path).pull_all()
            self.assertEqual(len(on_disk), 1)
            self.assertEqual(on_disk[0].payload, cache.get_required_envelope(settings, CultCache.GLOBAL_KEY).payload)
            self.assertEqual(cache.get_required_global(settings), {"theme": "second"})

    @staticmethod
    def _envelope(document, key: str, value) -> CultCacheEnvelope:
        catalog_entry = document.catalog_entry()
        return CultCacheEnvelope.create(
            key=key,
            type=document.type,
            payload=document.encode_payload(value),
            schema_id=catalog_entry.schema_id,
            catalog_entry=catalog_entry,
        )

    def test_legacy_key_global_loads_without_writing_and_first_write_leaves_one_global(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            path = Path(tmp) / "generic.cc"

            def build() -> CultCache:
                return CultCache.builder().register_document_type(settings).add_generic_store(
                    SingleFileMessagePackBackingStore(path)
                ).build()

            SingleFileMessagePackBackingStore(path).push(self._envelope(settings, "legacy", {"theme": "old"}))
            stored_bytes = path.read_bytes()

            cache = build()
            cache.pull_all_backing_stores()
            self.assertEqual(cache.get_global(settings), {"theme": "old"})
            self.assertEqual(cache.get(settings, CultCache.GLOBAL_KEY), {"theme": "old"})
            self.assertEqual(path.read_bytes(), stored_bytes)

            cache.put_global(settings, {"theme": "new"})
            self.assertEqual([e.key for e in SingleFileMessagePackBackingStore(path).pull_all()], [CultCache.GLOBAL_KEY])
            reloaded = build()
            reloaded.pull_all_backing_stores()
            self.assertEqual(reloaded.get_required_global(settings), {"theme": "new"})

    def test_deleting_a_legacy_key_global_removes_the_legacy_record(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            path = Path(tmp) / "generic.cc"
            SingleFileMessagePackBackingStore(path).push(self._envelope(settings, "legacy", {"theme": "old"}))
            cache = CultCache.builder().register_document_type(settings).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            cache.pull_all_backing_stores()

            cache.delete_global(settings)
            self.assertEqual(SingleFileMessagePackBackingStore(path).pull_all(), [])
            self.assertIsNone(cache.get_global(settings))

    def test_two_globals_of_one_type_on_disk_are_refused_under_any_keys(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            for keys in (["one", "two"], ["legacy", CultCache.GLOBAL_KEY]):
                path = Path(tmp) / f"{keys[0]}.cc"
                store = SingleFileMessagePackBackingStore(path)
                store.push_all([self._envelope(settings, key, {"theme": key}) for key in keys])
                cache = CultCache.builder().register_document_type(settings).add_generic_store(store).build()
                with self.assertRaisesRegex(CultCacheError, "Duplicate global document for type: settings"):
                    cache.pull_all_backing_stores()
                self.assertIsNone(cache.get_global(settings))

    def test_decoder_writing_during_pull_raises_and_changes_neither_store_nor_cache(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "generic.cc"
            holder: dict[str, CultCache] = {}

            def decode(raw):
                holder["cache"].put(item, "side", {"name": "side"})
                return raw

            item = define_document_type("item", decode=decode, encode=lambda value: value)
            store = SingleFileMessagePackBackingStore(path)
            store.push(self._envelope(item, "a", {"name": "a"}))
            stored_bytes = path.read_bytes()
            cache = holder["cache"] = CultCache.builder().register_document_type(item).add_generic_store(store).build()

            with self.assertRaisesRegex(CultCacheError, "re-entrant put"):
                cache.pull_all_backing_stores()
            self.assertEqual(path.read_bytes(), stored_bytes)
            self.assertEqual(cache.snapshot_envelopes(), [])
            self.assertIsNone(cache.get(item, "side"))
            # The refusal released the cache: an ordinary write still works.
            cache.put(item, "b", {"name": "b"})
            self.assertEqual(cache.get(item, "b"), {"name": "b"})

    def test_index_extractor_writing_during_register_index_raises_and_installs_nothing(self) -> None:
        thing = define_document_type("thing")
        cache = CultCache.builder().register_document_type(thing).build()
        cache.put(thing, "k1", {"n": "one", "cat": "x"})

        def category(value: dict) -> str:
            cache.put(thing, "k2", {"n": "two", "cat": "y"})
            return value["cat"]

        with self.assertRaisesRegex(CultCacheError, "re-entrant put"):
            cache.register_index(thing, "cat", category)
        self.assertIsNone(cache.get(thing, "k2"))
        self.assertNotIn("thing", cache._state.index_extractors)
        self.assertIsNone(cache.get_key_by_index(thing, "cat", "x"))

    def test_put_envelope_refuses_a_global_under_another_key(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            settings = define_document_type("settings", global_document=True)
            path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(settings).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            cache.put_global(settings, {"theme": "base"})
            store_before, cache_before = self._store_state(path), cache.snapshot_envelopes()
            stray = replace(cache.get_required_envelope(settings, CultCache.GLOBAL_KEY), key="stray")

            with self.assertRaisesRegex(CultCacheError, "must use key __global__"):
                cache.put_envelope(settings, stray)
            with self.assertRaisesRegex(CultCacheError, "must be non-empty"):
                cache.put_envelope(settings, replace(stray, key=""))
            self.assertEqual(self._store_state(path), store_before)
            self.assertEqual(cache.snapshot_envelopes(), cache_before)

    def test_refused_put_leaves_no_empty_type_entry(self) -> None:
        def refuse(value: dict) -> dict:
            raise ValueError("encode refuses")

        with tempfile.TemporaryDirectory() as tmp:
            other = define_document_type("other", encode=refuse)
            path = Path(tmp) / "generic.cc"
            cache = CultCache.builder().register_document_type(other).add_generic_store(
                SingleFileMessagePackBackingStore(path)
            ).build()
            with self.assertRaisesRegex(ValueError, "encode refuses"):
                cache.put(other, "x", {"a": 1})
            self.assertEqual(cache.snapshot(), {})
            self.assertFalse(path.exists())

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


    # Shared refusal vectors: tests/vectors/document-variants-c0, read by every runtime's tests.
    _VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "document-variants-c0"
    _ITEM_SCHEMA_ID = "sha256:88d3fdf0a927acf3b163940d8f8c7fe62b3316542ce771a67ec8bc038f594788"

    def _pull_vector(self, name: str) -> list[CultCacheEnvelope]:
        with tempfile.TemporaryDirectory() as tmp:
            store_path = Path(tmp) / "store.msgpack"
            store_path.write_bytes((self._VECTORS / name).read_bytes())
            return SingleFileMessagePackBackingStore(store_path).pull_all()

    def test_single_file_refuses_unknown_header_by_name(self) -> None:
        with self.assertRaises(ValueError) as caught:
            self._pull_vector("unknown-header.msgpack")
        self.assertIn("cultcache.store.v9", str(caught.exception))

    def test_single_file_refuses_extra_record_slot_naming_the_record(self) -> None:
        with self.assertRaises(ValueError) as caught:
            self._pull_vector("extra-slot-full-payload.msgpack")
        self.assertIn("item:anvil", str(caught.exception))
        self.assertIn(self._ITEM_SCHEMA_ID, str(caught.exception))

    def test_single_file_refuses_variant_store_by_version_or_record(self) -> None:
        with self.assertRaises(ValueError) as caught:
            self._pull_vector("variant-v2.msgpack")
        message = str(caught.exception)
        self.assertTrue("cultcache.store.v2" in message or "item:anvil-big" in message, message)

    def test_single_file_refuses_the_csharp_written_variant_store_by_version_or_record(self) -> None:
        with self.assertRaises(ValueError) as caught:
            self._pull_vector("../document-variants-c1/variant-store.msgpack")
        message = str(caught.exception)
        self.assertTrue("cultcache.store.v2" in message or "laser-big" in message, message)

    def test_v1_store_written_at_the_base_commit_still_reads_byte_for_byte(self) -> None:
        envelopes = self._pull_vector("v1-base.msgpack")
        self.assertEqual([(e.key, e.type) for e in envelopes], [("alpha", "vectors.item"), ("beta", "vectors.item")])
        self.assertEqual(envelopes[0].payload, b"\x92\xa5alpha\x01")
        self.assertEqual(envelopes[1].payload, b"\x92\xa4beta\x02")

    def test_single_file_refusals_say_what_was_found_without_blaming_variants(self) -> None:
        with self.assertRaises(ValueError) as unsupported:
            self._pull_vector("unknown-header.msgpack")
        self.assertIn("not one this runtime reads", str(unsupported.exception))
        self.assertNotIn("variant", str(unsupported.exception))
        with self.assertRaises(ValueError) as invalid:
            self._pull_vector("extra-slot-full-payload.msgpack")
        self.assertIn("not a valid store", str(invalid.exception))
        self.assertNotIn("variant", str(invalid.exception))

    def test_single_file_only_a_missing_store_reads_as_empty(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(SingleFileMessagePackBackingStore(Path(tmp) / "store.cc").pull_all(), [])

            # An existing empty file is not a store: no CultCache writer leaves one.
            empty = Path(tmp) / "empty.cc"
            empty.write_bytes(b"")
            with self.assertRaises(ValueError):
                SingleFileMessagePackBackingStore(empty).pull_all()
            self.assertEqual(empty.read_bytes(), b"")

            # An empty array is the legacy envelope array with no envelopes: no header, no format claimed.
            legacy_empty = Path(tmp) / "legacy-empty.cc"
            legacy_empty.write_bytes(b"\x90")
            self.assertEqual(SingleFileMessagePackBackingStore(legacy_empty).pull_all(), [])

    def test_single_file_refusals_never_echo_a_value_from_the_store(self) -> None:
        import msgpack  # type: ignore

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.cc"
            cases = [
                (["cultcache.store.v12", [], []], True),
                (["cultcache.store.SECRET-HEADER", [], []], False),
                (["cultcache.store.v12SECRET", [], []], False),
                (["cultcache.store.v", [], []], False),
                (["cultcache.store.v1", ["SECRET-CATALOG-TEXT"], []], None),
            ]
            for value, echoed in cases:
                path.write_bytes(msgpack.packb(value, use_bin_type=True))
                with self.assertRaises(ValueError) as caught:
                    SingleFileMessagePackBackingStore(path).pull_all()
                message = str(caught.exception)
                if echoed:
                    self.assertIn(repr(value[0]), message)
                else:
                    self.assertNotIn("SECRET", message)
                    if echoed is False:
                        self.assertNotIn(repr(value[0]), message)
                        self.assertIn(f"of {len(value[0])} bytes", message)

    def test_single_file_refusals_say_exactly_what_was_found_and_what_is_read(self) -> None:
        import msgpack  # type: ignore

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.cc"
            reads = "it reads 'cultcache.store.v1' and 'cultcache.store.v3' only"
            for header, found in (
                ("cultcache.store.v12", "'cultcache.store.v12'"),
                ("cultcache.store.SECRET", "an unrecognised cultcache.store.* header of 22 bytes"),
                ("cultcache.store.v", "an unrecognised cultcache.store.* header of 17 bytes"),
            ):
                path.write_bytes(msgpack.packb([header, [], []], use_bin_type=True))
                with self.assertRaises(ValueError) as caught:
                    SingleFileMessagePackBackingStore(path).pull_all()
                self.assertIn(f"CultCache store format {found} is not one this runtime reads; {reads}", str(caught.exception))

            path.write_bytes(msgpack.packb(["cultcache.store.v1", [], [["k", "s", "t", b"", "extra"]]], use_bin_type=True))
            with self.assertRaises(ValueError) as caught:
                SingleFileMessagePackBackingStore(path).pull_all()
            self.assertIn(
                "CultCache record 'k' (schema 's') has 5 slots, more than the 4 of a cultcache.store.v1 record, so this is not a valid store",
                str(caught.exception),
            )

    def test_single_file_record_refusal_names_only_a_string_key(self) -> None:
        import msgpack  # type: ignore

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.cc"
            for key, schema in ((["SECRET-IN-LIST"], "s"), (b"SECRET-BYTES", "s"), ("k", {"SECRET": 1})):
                path.write_bytes(msgpack.packb(
                    ["cultcache.store.v1", [], [[key, schema, "t", b"\x90", "extra"]]], use_bin_type=True))
                with self.assertRaises(ValueError) as caught:
                    SingleFileMessagePackBackingStore(path).pull_all()
                self.assertNotIn("SECRET", str(caught.exception))
                self.assertIn("not a valid store", str(caught.exception))

    # What a write does through a symbolic link today. R3 decides whether a write resolves
    # the link or refuses it; this pins the current behaviour so that change is deliberate.
    @unittest.skipIf(os.name != "posix", "symbolic links need POSIX here")
    def test_r3_decides_resolve_or_refuse_a_push_through_a_live_link_replaces_the_link(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            volume = Path(tmp) / "volume"
            volume.mkdir()
            target = volume / "store.cc"
            envelope = CultCacheEnvelope(key="a", type="t", payload=b"\x90", stored_at="2026-09-30T00:00:00Z")
            SingleFileMessagePackBackingStore(target).push(envelope)
            before = target.read_bytes()
            link = Path(tmp) / "store.cc"
            link.symlink_to(target)
            SingleFileMessagePackBackingStore(link).push(replace(envelope, key="b"))
            self.assertFalse(link.is_symlink())
            self.assertTrue(link.is_file())
            self.assertEqual(target.read_bytes(), before)

    @unittest.skipIf(os.name != "posix", "symbolic links need POSIX here")
    def test_a_dangling_symlink_at_a_store_path_is_an_os_error_and_nothing_is_written(self) -> None:
        for store_type in (SingleFileMessagePackBackingStore, JsonLinesBackingStore):
            with tempfile.TemporaryDirectory() as tmp:
                volume = Path(tmp) / "volume"
                volume.mkdir()
                path = Path(tmp) / "store.cc"
                path.symlink_to(volume / "store.cc")
                store = store_type(path)
                with self.assertRaises(FileNotFoundError):
                    store.pull_all()
                with self.assertRaises(FileNotFoundError):
                    store.push(CultCacheEnvelope(key="k", type="t", payload=b"\x90", stored_at="2026-09-30T00:00:00Z"))
                self.assertTrue(path.is_symlink())
                self.assertEqual(list(volume.iterdir()), [])

    @unittest.skipIf(os.name != "posix", "symlink loops and file-as-parent errors are POSIX errno cases")
    def test_json_lines_store_that_cannot_be_reached_is_an_os_error(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(JsonLinesBackingStore(Path(tmp) / "missing.jsonl").pull_all(), [])
            loop = Path(tmp) / "loop.jsonl"
            loop.symlink_to(loop)
            parent = Path(tmp) / "body"
            parent.write_bytes(b"not a directory")
            for path in (loop, parent / "store.jsonl"):
                with self.assertRaises(OSError) as caught:
                    JsonLinesBackingStore(path).pull_all()
                self.assertNotIsInstance(caught.exception, FileNotFoundError)

    @unittest.skipIf(os.name != "posix", "symlink loops and file-as-parent errors are POSIX errno cases")
    def test_single_file_store_that_cannot_be_reached_is_an_os_error(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            loop = Path(tmp) / "loop.cc"
            loop.symlink_to(loop)
            parent = Path(tmp) / "body"
            parent.write_bytes(b"not a directory")
            for path in (loop, parent / "store.cc"):
                with self.assertRaises(OSError) as caught:
                    SingleFileMessagePackBackingStore(path).pull_all()
                self.assertNotIsInstance(caught.exception, FileNotFoundError)


    # The element-id marker: tests/vectors/document-variants-c2a/v3-base.msgpack is v1-base with its header replaced.
    _C2A_VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "document-variants-c2a"

    def _header_after_rewrite(self, vector: Path) -> str:
        import msgpack

        with tempfile.TemporaryDirectory() as tmp:
            store_path = Path(tmp) / "store.msgpack"
            store_path.write_bytes(vector.read_bytes())
            store = SingleFileMessagePackBackingStore(store_path)
            envelopes = store.pull_all()
            self.assertEqual([e.key for e in envelopes], ["alpha", "beta"])
            store.push(envelopes[0])
            return msgpack.unpackb(store_path.read_bytes(), raw=False)[0]

    def test_single_file_reads_a_v3_element_id_store_and_a_rewrite_keeps_the_marker(self) -> None:
        self.assertEqual(self._header_after_rewrite(self._C2A_VECTORS / "v3-base.msgpack"), "cultcache.store.v3")

    def test_single_file_rewrite_of_a_v1_store_stays_v1(self) -> None:
        self.assertEqual(self._header_after_rewrite(self._VECTORS / "v1-base.msgpack"), "cultcache.store.v1")

    def test_single_file_flush_after_the_marked_file_is_gone_writes_unmarked(self) -> None:
        import msgpack

        with tempfile.TemporaryDirectory() as tmp:
            store_path = Path(tmp) / "store.msgpack"
            store_path.write_bytes((self._C2A_VECTORS / "v3-base.msgpack").read_bytes())
            store = SingleFileMessagePackBackingStore(store_path)
            envelopes = store.pull_all()
            store_path.unlink()
            store.push_all(envelopes)
            self.assertEqual(msgpack.unpackb(store_path.read_bytes(), raw=False)[0], "cultcache.store.v1")

    def test_single_file_push_after_the_marked_file_is_gone_writes_unmarked(self) -> None:
        import msgpack

        with tempfile.TemporaryDirectory() as tmp:
            store_path = Path(tmp) / "store.msgpack"
            store_path.write_bytes((self._C2A_VECTORS / "v3-base.msgpack").read_bytes())
            store = SingleFileMessagePackBackingStore(store_path)
            envelopes = store.pull_all()
            store_path.unlink()
            store.push(envelopes[0])
            self.assertEqual(msgpack.unpackb(store_path.read_bytes(), raw=False)[0], "cultcache.store.v1")

    def test_single_file_push_all_refuses_a_store_whose_header_it_cannot_read_and_leaves_it_untouched(self) -> None:
        envelopes = SingleFileMessagePackBackingStore(self._C2A_VECTORS / "v3-base.msgpack").pull_all()
        vectors = self._VECTORS.parent
        for vector in (
            self._VECTORS / "unknown-header.msgpack",
            self._VECTORS / "variant-v2.msgpack",
            vectors / "document-variants-c1" / "variant-store.msgpack",
        ):
            with tempfile.TemporaryDirectory() as tmp:
                store_path = Path(tmp) / "store.msgpack"
                store_path.write_bytes(vector.read_bytes())
                before = store_path.read_bytes()
                with self.assertRaisesRegex(StoreUnreadableError, "is not readable"):
                    SingleFileMessagePackBackingStore(store_path).push_all(envelopes)
                self.assertEqual(store_path.read_bytes(), before, f"{vector.name} was rewritten")

    # A file is replaced by a rewrite exactly when this runtime's own reader opens it: one verdict per file, asked by
    # pull_all, push_all, push and delete alike. The bytes and every runtime's verdict:
    # tests/vectors/document-variants-c2a/readability.
    def test_single_file_replaces_a_file_exactly_when_it_reads(self) -> None:
        import msgpack  # type: ignore

        root = self._C2A_VECTORS / "readability"
        rows = [
            line.split()
            for line in (root / "manifest.txt").read_text(encoding="utf-8").splitlines()
            if line and not line.startswith("#")
        ]
        # Every vector in the folder has a manifest row.
        self.assertEqual(
            sorted(path.name for path in root.glob("*.bin")),
            sorted(vector for vector, *_ in rows if not vector.startswith("..")),
        )
        envelopes = SingleFileMessagePackBackingStore(self._C2A_VECTORS / "v3-base.msgpack").pull_all()
        for vector, _, _, _, python in rows:
            content = (root / vector).read_bytes()
            for operation in ("pull_all", "push_all", "push", "delete"):
                with tempfile.TemporaryDirectory() as tmp:
                    store_path = Path(tmp) / "store.msgpack"
                    store_path.write_bytes(content)
                    store = SingleFileMessagePackBackingStore(store_path)
                    run = {
                        "pull_all": store.pull_all,
                        "push_all": lambda: store.push_all(envelopes),
                        "push": lambda: store.push(envelopes[0]),
                        "delete": lambda: store.delete(envelopes[0].type, envelopes[0].key),
                    }[operation]
                    if python == "reads":
                        run()
                        if operation == "pull_all":
                            continue
                        # The replaced file is a store: it carries the header its old content decides (a v3 store keeps its
                        # marker, all else is v1), and it holds what the operation wrote.
                        expected = "cultcache.store.v3" if "v3" in vector else "cultcache.store.v1"
                        self.assertEqual(msgpack.unpackb(store_path.read_bytes(), raw=False)[0], expected, f"{vector} {operation}")
                        keys = [envelope.key for envelope in SingleFileMessagePackBackingStore(store_path).pull_all()]
                        if operation == "push_all":
                            self.assertEqual(sorted(set(keys) & {e.key for e in envelopes}), sorted(e.key for e in envelopes), f"{vector} {operation}")
                        elif operation == "push":
                            self.assertIn(envelopes[0].key, keys, f"{vector} {operation}")
                        else:
                            self.assertNotIn(envelopes[0].key, keys, f"{vector} {operation}")
                    else:
                        with self.assertRaises(StoreUnreadableError, msg=f"{vector} {operation}") as refused:
                            run()
                        self.assertIsNotNone(refused.exception.__cause__, f"{vector} {operation}")
                        self.assertEqual(store_path.read_bytes(), content, f"{vector} {operation} rewrote a file it cannot read")



if __name__ == "__main__":
    unittest.main()
