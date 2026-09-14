from __future__ import annotations

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


if __name__ == "__main__":
    unittest.main()
