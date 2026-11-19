"""The Python CultCache first call from docs/start/README.md. The docs show the region between the
`snippet` markers verbatim, and test_docs_show_the_tested_snippet fails when they differ."""

import tempfile
import unittest
from pathlib import Path

import cultcache_py
from cultcache_py import CultCacheEnvelope, JsonLinesBackingStore, UpdateOutcome

# snippet:begin
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path

from cultcache_py import CultCache, SingleFileMessagePackBackingStore, define_document_type


@dataclass
class Item:
    name: str
    category: str
    value: int


item_doc = define_document_type(
    "item",
    encode=lambda item: asdict(item),
    decode=lambda raw: Item(**raw),
    name="name",
)


def first_call() -> Item:
    path = Path(tempfile.mkdtemp()) / "first-call.cc"

    cache = (
        CultCache.builder()
        .register_document_type(item_doc)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    cache.pull_all_backing_stores()
    cache.put(item_doc, "item:potion", Item("Potion", "Consumable", 50))  # written to the .cc file

    reopened = (
        CultCache.builder()
        .register_document_type(item_doc)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    reopened.pull_all_backing_stores()  # read it back
    assert reopened.get_by_name(item_doc, "Potion") == reopened.get_required(item_doc, "item:potion")
    return reopened.get_required(item_doc, "item:potion")
# snippet:end


# snippet:update:begin
def restock(path: Path):
    cache = (
        CultCache.builder()
        .register_document_type(item_doc)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    cache.pull_all_backing_stores()
    return cache.update(item_doc, "item:potion", lambda item: Item("Potion", "Consumable", (item.value if item else 0) + 10))
# snippet:update:end


class FirstCallTests(unittest.TestCase):
    def test_first_call_round_trips_through_a_second_cache(self) -> None:
        self.assertEqual(first_call(), Item("Potion", "Consumable", 50))

    def test_update_with_retry_through_a_second_cache(self) -> None:
        path = Path(tempfile.mkdtemp()) / "restock.cc"
        restock(path)
        result = restock(path)
        self.assertIs(result.outcome, UpdateOutcome.UPDATED)
        reopened = (
            CultCache.builder()
            .register_document_type(item_doc)
            .add_generic_store(SingleFileMessagePackBackingStore(path))
            .build()
        )
        reopened.pull_all_backing_stores()
        self.assertEqual(reopened.get_required(item_doc, "item:potion"), Item("Potion", "Consumable", 20))

    def test_docs_show_the_tested_snippets(self) -> None:
        source = Path(__file__).read_text(encoding="utf-8")
        docs = (Path(__file__).parents[3] / "docs" / "start" / "README.md").read_text(encoding="utf-8")
        for region in ("snippet", "snippet:update"):
            with self.subTest(region=region):
                snippet = source.split(f"# {region}:begin\n", 1)[1].split(f"# {region}:end\n", 1)[0]
                self.assertIn("```python\n" + snippet + "```", docs)


class RootExportTests(unittest.TestCase):
    def test_cultcacheerror_from_the_root_catches_a_convert_refusal(self) -> None:
        from cultcache_py import CultCacheError, convert
        from cultcache_py.cache import CultCacheError as private

        self.assertIs(CultCacheError, private)
        self.assertIn("CultCacheError", cultcache_py.__all__)
        document = define_document_type("tests.root_export")
        with tempfile.TemporaryDirectory() as directory:
            source = SingleFileMessagePackBackingStore(Path(directory) / "source.cc")
            source.push(CultCacheEnvelope.create(key="k", type=document.type, payload=document.encode_payload({"a": 1})))
            destination = Path(directory) / "destination.cc"
            destination.write_bytes(b"in the way")
            with self.assertRaises(CultCacheError) as caught:
                convert(source, destination, [document])
            self.assertIn("destination_exists", str(caught.exception))
            self.assertEqual(destination.read_bytes(), b"in the way")

    def test_the_cache_write_path_refuses_a_payload_tail_and_writes_nothing(self) -> None:
        document = define_document_type("tests.tail")
        with tempfile.TemporaryDirectory() as directory:
            for store in (
                SingleFileMessagePackBackingStore(Path(directory) / "tail.cc"),
                JsonLinesBackingStore(Path(directory) / "tail.jsonl"),
            ):
                cache = CultCache.builder().register_document_type(document).add_generic_store(store).build()
                tailed = document.encode_payload({"a": 1}) + b"\xc0"
                with self.assertRaisesRegex(ValueError, r"1 bytes after its first MessagePack value \(refused on read\)"):
                    cache.put_envelope(document, CultCacheEnvelope.create(key="k", type=document.type, payload=tailed))
                self.assertEqual(store.pull_all(), [], type(store).__name__)

    def test_a_batch_with_one_tailed_envelope_is_refused_whole_and_earlier_records_stay(self) -> None:
        document = define_document_type("tests.tail_batch")
        with tempfile.TemporaryDirectory() as directory:
            for store in (
                SingleFileMessagePackBackingStore(Path(directory) / "batch.cc"),
                JsonLinesBackingStore(Path(directory) / "batch.jsonl"),
            ):
                name = type(store).__name__
                cache = CultCache.builder().register_document_type(document).add_generic_store(store).build()
                cache.put(document, "early", {"a": 0})
                before = store.pull_all()
                good = document.encode_payload({"a": 1})
                with self.assertRaisesRegex(ValueError, r"1 bytes after its first MessagePack value \(refused on read\)"):
                    cache.put_envelopes(
                        document,
                        [
                            CultCacheEnvelope.create(key="good", type=document.type, payload=good),
                            CultCacheEnvelope.create(key="bad", type=document.type, payload=good + b"\xc0"),
                        ],
                    )
                self.assertEqual(store.pull_all(), before, name)
                self.assertEqual([item.key for item in before], ["early"], name)
                self.assertIsNone(cache.get(document, "good"), name)


if __name__ == "__main__":
    unittest.main()

