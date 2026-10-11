from __future__ import annotations

import shutil
import tempfile
import unittest
from pathlib import Path

import msgpack

from cultcache_py import (
    CultCache,
    SingleFileMessagePackBackingStore,
    UnionArm,
    database_entry_field,
    define_database_entry_type,
    define_record_codec,
    define_union,
)

VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "union-parity"

composition = define_record_codec(
    "Composition", [("name", 0), database_entry_field("body", 1, codec=lambda: node)]
)
box = define_record_codec("Box", [("id", 0), database_entry_field("children", 1, codec=lambda: node, many=True)])
text = define_record_codec("Text", [("id", 0), ("content", 1)])
use = define_record_codec(
    "Use",
    [
        ("id", 0),
        database_entry_field("composition", 1, codec=composition),
        database_entry_field("slots", 2, codec=lambda: node, many=True),
    ],
)
image = define_record_codec("Image", [("id", 0), ("uri", 1), ("alt", 3)])
node = define_union("Node", {0: ("Box", box), 1: ("Text", text), 2: ("Use", use), 5: ("Image", image)})
outcome = define_union(
    "Outcome",
    {
        0: ("Pending", define_record_codec("Pending", [("message", 0)])),
        1: ("Completed", define_record_codec("Completed", [("source_version", 0)])),
        2: ("Denied", None),
    },
)
tool = define_record_codec("Tool", [("name", 0), ("uses", 1)])
part = define_record_codec("Part", [("name", 0), ("count", 1)])
gem = define_record_codec("Gem", [("name", 0)])
item = define_union("Item", {0: ("Gem", gem), 1: ("Tool", tool), 2: ("Part", part)})
crafted = define_union("CraftedItem", {0: ("Tool", tool), 1: ("Part", part)})

surface_document = define_database_entry_type(
    "tests.union_surface",
    [
        ("id", 0),
        ("version", 1),
        database_entry_field("root", 2, codec=node),
        database_entry_field("outcomes", 3, codec=outcome, many=True),
        database_entry_field("part", 4, codec=item),
        database_entry_field("crafted", 5, codec=crafted),
        database_entry_field("missing", 6, codec=item),
    ],
)


def _surface_value() -> dict:
    return {
        "id": "surface",
        "version": 3,
        "root": UnionArm(
            "Box",
            {
                "id": "root",
                "children": [
                    UnionArm("Text", {"id": "t1", "content": "hello"}),
                    UnionArm(
                        "Use",
                        {
                            "id": "u1",
                            "composition": {
                                "name": "comp",
                                "body": UnionArm(
                                    "Box",
                                    {
                                        "id": "inner",
                                        "children": [
                                            UnionArm("Text", {"id": "deep", "content": "x"}),
                                            UnionArm("Image", {"id": "img", "uri": "uri://a", "alt": "alt"}),
                                        ],
                                    },
                                ),
                            },
                            "slots": [
                                UnionArm("Text", {"id": "s1", "content": "slot"}),
                                None,
                                UnionArm("Box", {"id": "b2", "children": []}),
                            ],
                        },
                    ),
                    UnionArm("Image", {"id": "i2", "uri": "uri://b", "alt": ""}),
                ],
            },
        ),
        "outcomes": [
            UnionArm("Pending", {"message": "wait"}),
            UnionArm("Completed", {"source_version": 7}),
            UnionArm("Denied"),
        ],
        "part": UnionArm("Tool", {"name": "hammer", "uses": 12}),
        "crafted": UnionArm("Part", {"name": "gear", "count": 4}),
        "missing": None,
    }


def _vector(name: str) -> bytes:
    return (VECTORS / name).read_bytes()


class UnionSurfaceVectorTests(unittest.TestCase):
    def test_surface_payload_decodes_to_the_value_and_re_encodes_byte_identical(self) -> None:
        payload = _vector("union-surface.msgpack")
        decoded = surface_document.decode_payload(payload)
        self.assertEqual(decoded, _surface_value())
        self.assertEqual(surface_document.encode_payload(decoded), payload)

    def test_the_csharp_written_store_reads_and_the_cache_rewrites_the_same_payload(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "union-surface.cc"
            shutil.copyfile(VECTORS / "union-surface.cc", path)
            cache = (
                CultCache.builder()
                .register_document_type(surface_document)
                .add_generic_store(SingleFileMessagePackBackingStore(path))
                .build()
            )
            cache.pull_all_backing_stores()
            self.assertEqual(cache.get(surface_document, "surface"), _surface_value())
            fresh = Path(tmp) / "fresh.cc"
            writer = (
                CultCache.builder()
                .register_document_type(surface_document)
                .add_generic_store(SingleFileMessagePackBackingStore(fresh))
                .build()
            )
            writer.pull_all_backing_stores()
            writer.put(surface_document, "surface", _surface_value())
            [envelope] = SingleFileMessagePackBackingStore(fresh).pull_all()
            self.assertEqual(envelope.payload, _vector("union-surface.msgpack"))

    def test_the_store_vector_payload_equals_the_surface_payload(self) -> None:
        [envelope] = SingleFileMessagePackBackingStore(VECTORS / "union-surface.cc").pull_all()
        self.assertEqual(envelope.payload, _vector("union-surface.msgpack"))

    def test_every_outcome_in_the_manifest_is_held(self) -> None:
        outcomes = msgpack.unpackb(_vector("outcomes.msgpack"), raw=False)
        self.assertEqual(len(outcomes), 9)
        refused = read_only = 0
        for file, outcome_kind, union, key, detail in outcomes:
            payload = _vector(file)
            if outcome_kind == "refused":
                refused += 1
                with self.assertRaises(ValueError, msg=file) as caught:
                    surface_document.decode_payload(payload)
                message = str(caught.exception)
                self.assertIn(union, message, file)
                if detail == "unknown-arm":
                    self.assertIn(f"arm key {key} ", message, file)
                else:
                    self.assertEqual(detail, "arity")
                    self.assertIn("malformed", message, file)
            else:
                read_only += 1
                self.assertEqual(outcome_kind, "read-only", file)
                self.assertEqual(surface_document.decode_payload(payload), _surface_value(), file)
        self.assertEqual((refused, read_only), (5, 4))


class UnionCodecRuleTests(unittest.TestCase):
    CANARY = "canary-7f3a91"

    def test_nil_union_is_nil_both_ways(self) -> None:
        self.assertIsNone(node.encode(None))
        self.assertIsNone(node.decode(None))

    def test_unit_arm_is_written_with_empty_slots_and_reads_back(self) -> None:
        self.assertEqual(outcome.encode(UnionArm("Denied")), [2, []])
        self.assertEqual(outcome.decode([2, []]), UnionArm("Denied", None))

    def test_unit_arm_refuses_a_value_to_carry(self) -> None:
        with self.assertRaises(ValueError) as caught:
            outcome.encode(UnionArm("Denied", {"message": self.CANARY}))
        self.assertIn("Outcome", str(caught.exception))
        self.assertNotIn(self.CANARY, str(caught.exception))

    def test_arm_keys_are_written_as_declared(self) -> None:
        self.assertEqual(node.encode(UnionArm("Image", {"id": "a", "uri": "b", "alt": "c"})), [5, ["a", "b", None, "c"]])
        self.assertEqual(node.encode(UnionArm("Text", {"id": "a", "content": "b"})), [1, ["a", "b"]])

    def test_encode_refuses_an_arm_the_union_does_not_declare_without_echoing_it(self) -> None:
        with self.assertRaises(ValueError) as caught:
            crafted.encode(UnionArm("Gem", {"name": self.CANARY}))
        self.assertIn("CraftedItem", str(caught.exception))
        self.assertNotIn(self.CANARY, str(caught.exception))
        with self.assertRaises(ValueError):
            node.encode({"id": "a"})

    def test_unknown_key_names_the_union_the_key_and_the_declared_keys_never_the_payload(self) -> None:
        with self.assertRaises(ValueError) as caught:
            node.decode([4, [self.CANARY]])
        message = str(caught.exception)
        self.assertIn("'Node'", message)
        self.assertIn("arm key 4 ", message)
        self.assertIn("declared: 0, 1, 2, 5", message)
        self.assertNotIn(self.CANARY, message)

    def test_a_malformed_union_value_is_refused_without_echoing_it(self) -> None:
        for raw in (
            [1],
            [1, ["a"], "extra"],
            [1, self.CANARY],
            self.CANARY,
            {"k": self.CANARY},
            [self.CANARY, []],
            [True, []],
            [1.0, []],
        ):
            with self.subTest(raw=type(raw).__name__):
                with self.assertRaises(ValueError) as caught:
                    node.decode(raw)
                self.assertIn("'Node'", str(caught.exception))
                self.assertNotIn(self.CANARY, str(caught.exception))

    def test_extra_trailing_arm_slots_are_read(self) -> None:
        self.assertEqual(
            node.decode([1, ["a", "b", "extra"]]),
            UnionArm("Text", {"id": "a", "content": "b"}),
        )

    def test_many_slot_requires_a_list_and_keeps_nil_elements(self) -> None:
        slots = [UnionArm("Text", {"id": "a", "content": "b"}), None]
        encoded = use.encode({"id": "u", "composition": None, "slots": slots})
        self.assertEqual(encoded, ["u", None, [[1, ["a", "b"]], None]])
        self.assertEqual(use.decode(encoded)["slots"], slots)
        with self.assertRaises(ValueError) as caught:
            use.decode(["u", None, self.CANARY])
        self.assertNotIn(self.CANARY, str(caught.exception))

    def test_a_nested_record_with_a_missing_required_slot_is_refused(self) -> None:
        with self.assertRaises(ValueError) as caught:
            node.decode([1, ["only-id"]])
        self.assertIn("Text", str(caught.exception))
        self.assertIn("slot 1", str(caught.exception))

    def test_definition_rules(self) -> None:
        with self.assertRaises(ValueError):
            define_union("Empty", {})
        with self.assertRaises(ValueError):
            define_union("Dup", {0: ("A", None), 1: ("A", None)})
        with self.assertRaises(ValueError):
            define_union("Neg", {-1: ("A", None)})
        with self.assertRaises(ValueError):
            define_union("Flag", {True: ("A", None), "x": ("B", None)})
        with self.assertRaises(ValueError):
            database_entry_field("loose", 1, many=True)


if __name__ == "__main__":
    unittest.main()
