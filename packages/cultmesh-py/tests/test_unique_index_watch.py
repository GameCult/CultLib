from __future__ import annotations

import re
import unittest
from pathlib import Path

from cultcache_py import define_database_entry_type
from cultcache_py.cache import CultCacheError
from cultmesh_py import CultMesh

# variants:ruling:unique-indexes. `indexes` are unique lookups, `roles` are selection aliases. A watch by index reads the
# lookup's own canonical text (cultcache_py.cache.index_text), never a second rendering of the value.


class WatchByIndexTests(unittest.TestCase):
    def test_the_watch_and_the_lookup_agree_on_the_text_of_a_value(self) -> None:
        document = define_database_entry_type("mesh.priced", [("price", 0)], indexes={"price": "price"})
        node = CultMesh.create_node(runtime_id="mesh-watch-text")
        node.database.register_document(document)
        cases = [
            (2.0, "2", "2.0"),
            (0.1, "0.1000000000000000055511151231257827021181583404541015625", "0.1"),
            (-0.0, "0", "-0.0"),
        ]
        for index, (value, text, str_text) in enumerate(cases):
            with self.subTest(value=value):
                key = f"item:{index}"
                heard: list[str] = []
                misheard: list[str] = []
                node.database.watch_by_index(document, "price", text, lambda change: heard.append(change.record_key))
                node.database.watch_by_index(document, "price", str_text, lambda change: misheard.append(change.record_key))
                node.database.put(document, key, {"price": value})
                self.assertEqual(node.database.get_by_index(document, "price", text), {"price": value})
                self.assertEqual(heard, [key])
                if str_text != text:
                    self.assertEqual(misheard, [])
                    self.assertIsNone(node.database.get_by_index(document, "price", str_text))

    def test_a_watch_hears_a_record_leaving_a_value_by_the_canonical_text(self) -> None:
        document = define_database_entry_type("mesh.leaving", [("price", 0)], indexes={"price": "price"})
        node = CultMesh.create_node(runtime_id="mesh-watch-leaving")
        node.database.register_document(document)
        node.database.put(document, "item:1", {"price": 2.0})
        heard: list[str] = []
        node.database.watch_by_index(document, "price", "2", lambda change: heard.append(change.record_key))
        node.database.put(document, "item:1", {"price": 3.0})
        self.assertEqual(heard, ["item:1"])

    def test_a_blank_value_is_never_indexed_so_never_watched(self) -> None:
        document = define_database_entry_type("mesh.blank", [("sku", 0)], indexes={"sku": "sku"})
        node = CultMesh.create_node(runtime_id="mesh-watch-blank")
        node.database.register_document(document)
        heard: list[str] = []
        node.database.watch_by_index(document, "sku", " ", lambda change: heard.append(change.record_key))
        node.database.put(document, "item:1", {"sku": " "})
        self.assertEqual(heard, [])

    def test_a_role_alias_is_not_watchable_by_index(self) -> None:
        document = define_database_entry_type("mesh.roles", [("kind", 0)], roles={"kind": "kind"})
        node = CultMesh.create_node(runtime_id="mesh-watch-role")
        node.database.register_document(document)
        with self.assertRaisesRegex(ValueError, "has no index"):
            node.database.watch_by_index(document, "kind", "guide", lambda _: None)


class ReadmeTests(unittest.TestCase):
    def test_the_readme_note_document_admits_many_notes_of_one_kind(self) -> None:
        readme = (Path(__file__).parents[1] / "README.md").read_text(encoding="utf-8")
        declaration = re.search(r"^note_doc = define_database_entry_type\(\n.*?^\)\n", readme, re.S | re.M)
        self.assertIsNotNone(declaration)
        scope: dict = {"define_database_entry_type": define_database_entry_type}
        exec(declaration.group(0), scope)  # noqa: S102 - the README's own declaration
        note_doc = scope["note_doc"]
        node = CultMesh.create_node(runtime_id="mesh-readme")
        node.database.register_document(note_doc)
        node.database.put(note_doc, "note:1", {"body": "a", "name": "one", "kind": "guide", "slug": "one"})
        node.database.put(note_doc, "note:2", {"body": "b", "name": "two", "kind": "guide", "slug": "two"})
        self.assertEqual(node.database.get_by_index(note_doc, "slug", "two")["body"], "b")
        with self.assertRaises(CultCacheError):
            node.database.put(note_doc, "note:3", {"body": "c", "name": "three", "kind": "guide", "slug": "one"})


if __name__ == "__main__":
    unittest.main()
