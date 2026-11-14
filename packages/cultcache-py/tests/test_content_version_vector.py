from __future__ import annotations

import shutil
import tempfile
import unittest
from pathlib import Path

import msgpack

from cultcache_py import SingleFileMessagePackBackingStore
from cultcache_py.stores import record_version

# variants:ruling:write-set-content-version-declared-ownership. The vector in tests/vectors/content-version is written only by
# the C# reference; every row of it is computed here from the store file, by the store's read and by record_version.
VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "content-version"


class ContentVersionVectorTests(unittest.TestCase):
    def test_every_row_of_the_vector_equals_this_runtimes_version(self) -> None:
        rows = msgpack.unpackb((VECTORS / "versions.msgpack").read_bytes(), raw=False)
        self.assertEqual([key for key, _ in rows], ["alpha", "beta"])
        directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, directory, ignore_errors=True)
        copy = directory / "store.cc"
        shutil.copyfile(VECTORS / "store.cc", copy)
        store = SingleFileMessagePackBackingStore(copy)
        by_key = {envelope.key: envelope for envelope in store.pull_all()}
        for key, version in rows:
            with self.subTest(key=key):
                found = store.read(key)
                self.assertIsNotNone(found)
                self.assertEqual(found[1], version)
                self.assertEqual(record_version(by_key[key]), version)
        self.assertIsNone(store.read("gamma"))


if __name__ == "__main__":
    unittest.main()
