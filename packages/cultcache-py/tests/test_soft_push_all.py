from __future__ import annotations

import shutil
import tempfile
import unittest
from pathlib import Path

from cultcache_py import JsonLinesBackingStore, SingleFileMessagePackBackingStore, StoreUnreadableError
from cultcache_py.backing_store import BackingStore, CultCacheEnvelope


def envelope(key: str) -> CultCacheEnvelope:
    return CultCacheEnvelope(key=key, type="t", payload=b"\x01", stored_at="2026-11-06T00:00:00Z")


class SoftPushAllTests(unittest.TestCase):
    """A soft push writes where nothing is, skips a store it reads, and refuses one it cannot read (TS and Rust parity)."""

    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.path = self.dir / "state.cc"

    def test_over_a_readable_store_it_leaves_the_file_byte_for_byte(self) -> None:
        for make in (SingleFileMessagePackBackingStore, JsonLinesBackingStore):
            with self.subTest(make.__name__):
                path = self.dir / make.__name__
                store = make(path)
                store.push_all([envelope("a")])
                before = path.read_bytes()
                store.push_all([envelope("b")], soft=True)
                self.assertEqual(before, path.read_bytes())
                self.assertEqual(["a"], [item.key for item in make(path).pull_all()])
                store.push_all([envelope("b")])  # a plain push still lands what the caller holds
                self.assertEqual(["a", "b"], sorted(item.key for item in make(path).pull_all()))

    def test_over_a_readable_store_holding_no_records_it_leaves_the_file_byte_for_byte(self) -> None:
        for make in (SingleFileMessagePackBackingStore, JsonLinesBackingStore):
            with self.subTest(make.__name__):
                path = self.dir / make.__name__
                store = make(path)
                store.push_all([envelope("a")])
                store.delete("t", "a")
                self.assertTrue(path.exists())
                self.assertEqual([], make(path).pull_all())
                before = path.read_bytes()
                store.push_all([envelope("b")], soft=True)
                self.assertEqual(before, path.read_bytes())
                self.assertEqual([], make(path).pull_all())

    def test_over_an_absent_path_it_writes(self) -> None:
        for make in (SingleFileMessagePackBackingStore, JsonLinesBackingStore):
            with self.subTest(make.__name__):
                path = self.dir / make.__name__
                make(path).push_all([envelope("a")], soft=True)
                self.assertEqual(["a"], [item.key for item in make(path).pull_all()])

    def test_over_an_unreadable_file_it_refuses_and_leaves_it(self) -> None:
        junk = b"\xc1\x01\x02\x03"
        self.path.write_bytes(junk)
        with self.assertRaises(StoreUnreadableError):
            SingleFileMessagePackBackingStore(self.path).push_all([envelope("a")], soft=True)
        self.assertEqual(junk, self.path.read_bytes())

    def test_over_an_unreadable_jsonl_file_it_refuses_and_leaves_it(self) -> None:
        junk = b"this is not json\n"
        self.path.write_bytes(junk)
        with self.assertRaises(ValueError):
            JsonLinesBackingStore(self.path).push_all([envelope("a")], soft=True)
        self.assertEqual(junk, self.path.read_bytes())

    def test_the_protocol_default_push_all_decides_soft_by_what_pull_or_absent_says(self) -> None:
        class Memory(BackingStore):
            def __init__(self) -> None:
                self.items: list[CultCacheEnvelope] | None = None  # None: nothing at the path

            def pull_all(self) -> list[CultCacheEnvelope]:
                return list(self.items or [])

            def pull_or_absent(self) -> list[CultCacheEnvelope] | None:
                return None if self.items is None else list(self.items)

            def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
                self.items = list(envelopes)

        store = Memory()
        store.push_all([envelope("a")], soft=True)  # nothing at the path: writes
        self.assertEqual(["a"], [item.key for item in store.items or []])
        store.push_all([envelope("b")], soft=True)  # a store with records: skips
        self.assertEqual(["a"], [item.key for item in store.items or []])
        store.items = []
        store.push_all([envelope("b")], soft=True)  # a store the reader read, empty: skips
        self.assertEqual([], store.items)
        store.push_all([envelope("b")])  # plain: lands
        self.assertEqual(["b"], [item.key for item in store.items])

    def test_a_store_that_only_pulls_all_is_read_so_a_soft_push_skips_it(self) -> None:
        class PullOnly(BackingStore):
            def __init__(self) -> None:
                self.items: list[CultCacheEnvelope] = []

            def pull_all(self) -> list[CultCacheEnvelope]:
                return list(self.items)

            def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
                self.items = list(envelopes)

        store = PullOnly()
        store.push_all([envelope("a")], soft=True)
        self.assertEqual([], store.items)

    def test_the_single_file_and_jsonl_stores_say_absent_and_read_through_pull_or_absent(self) -> None:
        for make in (SingleFileMessagePackBackingStore, JsonLinesBackingStore):
            with self.subTest(make.__name__):
                path = self.dir / make.__name__
                store = make(path)
                self.assertIsNone(store.pull_or_absent())
                store.push_all([envelope("a")])
                self.assertEqual(["a"], [item.key for item in store.pull_or_absent() or []])

    def test_the_protocol_default_push_all_refuses_where_its_pull_refuses(self) -> None:
        class Unreadable(BackingStore):
            replaced = False

            def pull_all(self) -> list[CultCacheEnvelope]:
                raise StoreUnreadableError(Path("x"), ValueError("junk"))

            def pull_or_absent(self) -> list[CultCacheEnvelope] | None:
                raise StoreUnreadableError(Path("x"), ValueError("junk"))

            def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
                Unreadable.replaced = True

        with self.assertRaises(StoreUnreadableError):
            Unreadable().push_all([envelope("a")], soft=True)
        self.assertFalse(Unreadable.replaced)


if __name__ == "__main__":
    unittest.main()
