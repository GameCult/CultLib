from __future__ import annotations

import dataclasses
import shutil
import tempfile
import unittest
from pathlib import Path

import msgpack

from cultcache_py import (
    CultCache,
    DatabaseEntryField,
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
        database_entry_field("missing", 6, codec=node),
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

    def test_the_missing_slot_is_a_node_and_agrees_with_the_csharp_written_root(self) -> None:
        payload = _vector("union-surface.msgpack")
        slots = msgpack.unpackb(payload, raw=False)
        slots[6] = slots[2]
        decoded = surface_document.decode_payload(msgpack.packb(slots, use_bin_type=True))
        self.assertEqual(decoded["missing"], _surface_value()["root"])
        self.assertEqual(surface_document.encode_payload(decoded), msgpack.packb(slots, use_bin_type=True))
        with self.assertRaises(ValueError):
            surface_document.encode_payload({**_surface_value(), "missing": UnionArm("Part", {"name": "gear", "count": 4})})

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
        self.assertEqual(str(caught.exception), "Union 'Outcome' arm 2 is a unit arm and carries no value")

    def test_arm_keys_are_written_as_declared(self) -> None:
        self.assertEqual(node.encode(UnionArm("Image", {"id": "a", "uri": "b", "alt": "c"})), [5, ["a", "b", None, "c"]])
        self.assertEqual(node.encode(UnionArm("Text", {"id": "a", "content": "b"})), [1, ["a", "b"]])

    def test_encode_refuses_an_arm_the_union_does_not_declare_without_echoing_it(self) -> None:
        with self.assertRaises(ValueError) as caught:
            crafted.encode(UnionArm("Gem", {"name": self.CANARY}))
        self.assertEqual(
            str(caught.exception), "Union 'CraftedItem' was given a value that is not one of its arms"
        )
        with self.assertRaises(ValueError):
            node.encode({"id": "a"})

    def test_unknown_key_names_the_union_the_key_and_the_declared_keys_never_the_payload(self) -> None:
        with self.assertRaises(ValueError) as caught:
            node.decode([4, [self.CANARY]])
        message = str(caught.exception)
        self.assertEqual(message, "Union 'Node' does not declare arm key 4 (declared: 0, 1, 2, 5)")

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
                message = str(caught.exception)
                self.assertTrue(message.startswith("Union 'Node' value is malformed: "), message)
                self.assertNotIn(self.CANARY, message)

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
        self.assertEqual(str(caught.exception), "DatabaseEntry document type 'Use' slot 2 expected a MessagePack array")

    def test_a_list_of_nested_records_keeps_a_nil_element_as_csharp_writes_it(self) -> None:
        row = define_record_codec("Row", [("id", 0), database_entry_field("cells", 1, codec=text, many=True)])
        cells = [None, {"id": "c", "content": "x"}, None]
        encoded = row.encode({"id": "r", "cells": cells})
        self.assertEqual(encoded, ["r", [None, ["c", "x"], None]])
        self.assertEqual(row.decode(encoded), {"id": "r", "cells": cells})

    def test_a_unit_arm_with_extra_slots_is_read_past(self) -> None:
        self.assertEqual(outcome.decode([2, ["extra"]]), UnionArm("Denied"))

    def test_union_arms_are_immutable_hashable_values(self) -> None:
        arm = UnionArm("Denied")
        with self.assertRaises(dataclasses.FrozenInstanceError):
            arm.name = "Pending"  # type: ignore[misc]
        self.assertEqual(len({UnionArm("Denied"), UnionArm("Denied"), UnionArm("Pending", "wait")}), 2)

    def test_a_nested_record_with_a_missing_required_slot_is_refused(self) -> None:
        with self.assertRaises(ValueError) as caught:
            node.decode([1, ["only-id"]])
        self.assertEqual(str(caught.exception), "DatabaseEntry document type 'Text' is missing required slot 1")

    def test_definition_rules(self) -> None:
        cases = [
            ("Empty", {}, "Union 'Empty' requires at least one arm"),
            ("Dup", {0: ("A", None), 1: ("A", None)}, "Union 'Dup' has duplicate arm names"),
            ("Wide", {2**31: ("A", None)}, "Union 'Wide' arm keys must be integers within Int32"),
            ("Narrow", {-(2**31) - 1: ("A", None)}, "Union 'Narrow' arm keys must be integers within Int32"),
            ("Flag", {True: ("A", None), "x": ("B", None)}, "Union 'Flag' arm keys must be integers within Int32"),
        ]
        for union_name, arms, expected in cases:
            with self.subTest(union=union_name):
                with self.assertRaises(ValueError) as caught:
                    define_union(union_name, arms)
                self.assertEqual(str(caught.exception), expected)
        edge = define_union("Edge", {-(2**31): ("Low", None), -1: ("Negative", None), 2**31 - 1: ("High", None)})
        for arm_name, key in (("Low", -(2**31)), ("Negative", -1), ("High", 2**31 - 1)):
            self.assertEqual(edge.encode(UnionArm(arm_name)), [key, []])
            self.assertEqual(edge.decode([key, []]), UnionArm(arm_name))
        with self.assertRaises(ValueError) as caught:
            database_entry_field("loose", 1, many=True)
        self.assertEqual(str(caught.exception), "A many DatabaseEntry field requires a codec")
        database_entry_field("fine", 1, codec=node, many=True)


class RecordCodecTests(unittest.TestCase):
    def test_defaults_fill_a_missing_or_nil_slot_and_a_callable_default_is_called(self) -> None:
        record = define_record_codec(
            "Defaulted",
            [("id", 0), ("label", 1, "none"), ("tags", 2, list), ("plain", 3, 0)],
        )
        self.assertEqual(record.decode(["a"]), {"id": "a", "label": "none", "tags": [], "plain": 0})
        self.assertEqual(
            record.decode(["a", None, None, None]), {"id": "a", "label": "none", "tags": [], "plain": 0}
        )
        self.assertEqual(record.decode(["a", "x", ["t"], 5]), {"id": "a", "label": "x", "tags": ["t"], "plain": 5})

    def test_a_document_type_names_its_members_and_marks_the_name_and_index_slots(self) -> None:
        document = define_database_entry_type(
            "tests.members",
            [("id", 0), ("source_version", 1), ("owner", 2)],
            name="id",
            indexes={"by_owner": "owner"},
        )
        members = {member.slot: member for member in document.members}
        self.assertEqual(
            [members[slot].member_name for slot in (0, 1, 2)], ["Id", "SourceVersion", "Owner"]
        )
        self.assertEqual([members[slot].is_name for slot in (0, 1, 2)], [True, False, False])
        self.assertEqual([members[slot].index_alias for slot in (0, 1, 2)], [None, None, "by_owner"])

    def test_a_document_without_a_string_name_marks_no_member_as_the_name(self) -> None:
        document = define_database_entry_type("tests.named", [("id", 0)], name=lambda value: value["id"])
        self.assertFalse(document.members[0].is_name)

    def test_a_record_that_is_not_an_array_is_refused_naming_the_type_only(self) -> None:
        for refuse in (lambda r: r.decode({"id": "canary-7f3a91"}), lambda r: r.decode("canary-7f3a91")):
            record = define_record_codec("Plain", [("id", 0)])
            with self.assertRaises(ValueError) as caught:
                refuse(record)
            self.assertEqual(str(caught.exception), "DatabaseEntry document type 'Plain' expected a MessagePack array")
            self.assertNotIn("canary-7f3a91", str(caught.exception))

    def test_field_tuples_of_the_wrong_length_are_refused(self) -> None:
        for fields in ([("id",)], [("id", 0, 1, 2)]):
            with self.assertRaises(ValueError) as caught:
                define_record_codec("Bad", fields)
            self.assertEqual(
                str(caught.exception), "DatabaseEntry field tuples must be (name, key) or (name, key, default)"
            )
        self.assertEqual(define_record_codec("Pair", [("id", 0)]).decode(["a"]), {"id": "a"})
        self.assertEqual(define_record_codec("Triple", [("id", 1, "d")]).decode([None]), {"id": "d"})

    def test_duplicate_and_empty_field_lists_are_refused_naming_the_type(self) -> None:
        with self.assertRaises(ValueError) as caught:
            define_record_codec("Twice", [("a", 0), ("b", 0)])
        self.assertEqual(str(caught.exception), "DatabaseEntry document type 'Twice' has duplicate field keys")
        with self.assertRaises(ValueError) as empty:
            define_record_codec("Nothing", [])
        self.assertEqual(str(empty.exception), "DatabaseEntry document types require at least one field")

    def test_a_plain_field_helper_makes_a_required_slot_with_no_codec(self) -> None:
        plain = define_record_codec("Plain", [database_entry_field("id", 0), database_entry_field("label", 1)])
        self.assertEqual(plain.decode(plain.encode({"id": "a", "label": "b"})), {"id": "a", "label": "b"})
        with self.assertRaises(ValueError) as caught:
            plain.decode(["a"])
        self.assertEqual(str(caught.exception), "DatabaseEntry document type 'Plain' is missing required slot 1")
        field = database_entry_field("id", 0)
        self.assertIsNone(field.codec)
        self.assertFalse(field.many)

    def test_a_directly_constructed_field_is_required_plain_and_single(self) -> None:
        field = DatabaseEntryField(name="id", key=0)
        self.assertIs(field.default, dataclasses.MISSING)
        self.assertIsNone(field.codec)
        self.assertIs(field.many, False)

    def test_slot_codecs_and_fields_are_immutable(self) -> None:
        with self.assertRaises(dataclasses.FrozenInstanceError):
            node.encode = None  # type: ignore[misc]
        with self.assertRaises(dataclasses.FrozenInstanceError):
            database_entry_field("id", 0).key = 1  # type: ignore[misc]

    def test_a_negative_slot_key_is_refused_but_zero_is_not(self) -> None:
        database_entry_field("zero", 0)
        with self.assertRaises(ValueError) as caught:
            database_entry_field("negative", -1)
        self.assertEqual(str(caught.exception), "DatabaseEntry field keys must be non-negative integers")

    def test_slot_order_follows_keys_not_declaration_order(self) -> None:
        record = define_record_codec("Ordered", [("b", 2), ("a", 0)])
        self.assertEqual(record.encode({"a": "x", "b": "y"}), ["x", None, "y"])


if __name__ == "__main__":
    unittest.main()
