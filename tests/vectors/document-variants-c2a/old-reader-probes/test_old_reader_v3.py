import tempfile
import unittest
from pathlib import Path

from cultcache_py import SingleFileMessagePackBackingStore


# Copied into a checkout of the C0 merge (e382bb4) by old-reader-refusal.sh. Not part of this tree's suite.
class OldReaderV3(unittest.TestCase):
    def test_old_python_reader_refuses_the_v3_element_id_store_by_header(self) -> None:
        vector = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "document-variants-c2a" / "v3-base.msgpack"
        with tempfile.TemporaryDirectory() as tmp:
            store_path = Path(tmp) / "store.msgpack"
            store_path.write_bytes(vector.read_bytes())
            with self.assertRaises(ValueError) as caught:
                SingleFileMessagePackBackingStore(store_path).pull_all()
        print("OLD-READER python refused:", caught.exception)
        self.assertIn("cultcache.store.v3", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
