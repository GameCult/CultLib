"""The Python CultCache first call from docs/start/README.md. The docs show the region between the
`snippet` markers verbatim, and test_docs_show_the_tested_snippet fails when they differ."""

import unittest
from pathlib import Path

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


class FirstCallTests(unittest.TestCase):
    def test_first_call_round_trips_through_a_second_cache(self) -> None:
        self.assertEqual(first_call(), Item("Potion", "Consumable", 50))

    def test_docs_show_the_tested_snippet(self) -> None:
        source = Path(__file__).read_text(encoding="utf-8")
        snippet = source.split("# snippet:begin\n", 1)[1].split("# snippet:end\n", 1)[0]
        docs = (Path(__file__).parents[3] / "docs" / "start" / "README.md").read_text(encoding="utf-8")
        self.assertIn("```python\n" + snippet + "```", docs)


if __name__ == "__main__":
    unittest.main()
