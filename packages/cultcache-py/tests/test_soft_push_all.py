from __future__ import annotations

import shutil
import tempfile
import unittest
from pathlib import Path

from cultcache_py import JsonLinesBackingStore, SingleFileMessagePackBackingStore, StoreUnreadableError
from cultcache_py.backing_store import CultCacheEnvelope


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


if __name__ == "__main__":
    unittest.main()
