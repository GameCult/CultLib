import tempfile
import unittest
from pathlib import Path

from cultcache_py import CultCacheEnvelope, SingleFileMessagePackBackingStore


# Copied into a checkout of cultcache-rs-v0.3.1 (02cf04b5) by old-reader-refusal.sh. Not part of this tree's suite.
# The vector is the store C2aGateWriter wrote through CultCacheMessagePack.Create.
class OldReaderV3(unittest.TestCase):
    def setUp(self) -> None:
        vector = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "document-variants-c2a" / "c2a-written-ids.msgpack"
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.path = Path(self._tmp.name) / "store.msgpack"
        self.bytes = vector.read_bytes()
        self.path.write_bytes(self.bytes)
        self.envelope = CultCacheEnvelope(key="probe", type="probe", payload=b"\x80", stored_at="2026-10-22T00:00:00Z")

    def refuses_v3(self, label: str, action) -> None:
        with self.assertRaises(ValueError) as caught:
            action(SingleFileMessagePackBackingStore(self.path))
        print(f"OLD-READER python refused {label}:", caught.exception)
        self.assertIn("cultcache.store.v3", str(caught.exception))
        self.assertEqual(self.path.read_bytes(), self.bytes)

    def test_old_python_reader_refuses_the_c2a_element_id_store_at_read(self) -> None:
        self.refuses_v3("pull_all", lambda store: store.pull_all())

    def test_old_python_reader_refuses_a_per_record_push(self) -> None:
        self.refuses_v3("push", lambda store: store.push(self.envelope))

    def test_old_python_reader_refuses_a_whole_store_push_all(self) -> None:
        self.refuses_v3("push_all", lambda store: store.push_all([self.envelope]))

    def test_old_python_reader_refuses_a_delete(self) -> None:
        self.refuses_v3("delete", lambda store: store.delete("probe", "probe"))


if __name__ == "__main__":
    unittest.main()
