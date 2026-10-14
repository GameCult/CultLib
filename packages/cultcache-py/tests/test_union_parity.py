from __future__ import annotations

import collections
import dataclasses
import shutil
import tempfile
import traceback
import types
import unittest
from pathlib import Path

import msgpack

from cultcache_py import (
    MAX_PAYLOAD_DEPTH,
    MAX_SLOT_KEY,
    CultCache,
    CultCachePayloadDepthError,
    DatabaseEntryField,
    SlotCodec,
    SingleFileMessagePackBackingStore,
    UnionArm,
    database_entry_field,
    define_database_entry_type,
    define_document_type,
    define_record_codec,
    define_union,
    leaves as t,
)

VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "union-parity"

_GOLDEN = "GameCult.Caching.Tests.UnionParityVectorTests+"
_node = t.nullable(t.lazy(lambda: node))
composition = define_record_codec(
    "Composition",
    [database_entry_field("name", 0, codec=t.string), database_entry_field("body", 1, codec=_node)],
    type_name=_GOLDEN + "Composition",
)
box = define_record_codec(
    "Box",
    [
        database_entry_field("id", 0, codec=t.string),
        database_entry_field("children", 1, codec=t.list(t.lazy(lambda: node))),
    ],
    type_name=_GOLDEN + "Box",
)
text = define_record_codec(
    "Text",
    [database_entry_field("id", 0, codec=t.string), database_entry_field("content", 1, codec=t.string)],
    type_name=_GOLDEN + "Text",
)
use = define_record_codec(
    "Use",
    [
        database_entry_field("id", 0, codec=t.string),
        database_entry_field("composition", 1, codec=composition),
        database_entry_field("slots", 2, codec=t.list(t.nullable(t.lazy(lambda: node)))),
    ],
    type_name=_GOLDEN + "Use",
)
image = define_record_codec(
    "Image",
    [
        database_entry_field("id", 0, codec=t.string),
        database_entry_field("uri", 1, codec=t.string),
        database_entry_field("alt", 3, codec=t.string),
    ],
    type_name=_GOLDEN + "Image",
)
wrap = define_record_codec("Wrap", [database_entry_field("inner", 0, codec=_node)], type_name=_GOLDEN + "Wrap")
node = define_union(
    "Node",
    {0: ("Box", box), 1: ("Text", text), 2: ("Use", use), 5: ("Image", image), 6: ("Wrap", wrap)},
    type_name=_GOLDEN + "Node",
)
outcome = define_union(
    "Outcome",
    {
        0: ("Pending", define_record_codec("Pending", [database_entry_field("message", 0, codec=t.string)])),
        1: ("Completed", define_record_codec("Completed", [database_entry_field("source_version", 0, codec=t.int32)])),
        2: ("Denied", None),
        -2147483648: ("Floor", None),
        2147483647: ("Ceiling", None),
    },
    type_name=_GOLDEN + "Outcome",
)
tool = define_record_codec(
    "Tool",
    [database_entry_field("name", 0, codec=t.string), database_entry_field("uses", 1, codec=t.int32)],
    type_name=_GOLDEN + "Tool",
)
part = define_record_codec(
    "Part",
    [database_entry_field("name", 0, codec=t.string), database_entry_field("count", 1, codec=t.int32)],
    type_name=_GOLDEN + "Part",
)
gem = define_record_codec("Gem", [database_entry_field("name", 0, codec=t.string)], type_name=_GOLDEN + "Gem")
item = define_union("Item", {0: ("Gem", gem), 1: ("Tool", tool), 2: ("Part", part)}, type_name=_GOLDEN + "Item")
crafted = define_union("CraftedItem", {0: ("Tool", tool), 1: ("Part", part)}, type_name=_GOLDEN + "CraftedItem")

surface_document = define_database_entry_type(
    "tests.union_surface",
    [
        database_entry_field("id", 0, codec=t.string),
        database_entry_field("version", 1, codec=t.int32),
        database_entry_field("root", 2, codec=_node),
        database_entry_field("outcomes", 3, codec=t.list(outcome)),
        database_entry_field("part", 4, codec=t.nullable(item)),
        database_entry_field("crafted", 5, codec=t.nullable(crafted)),
        database_entry_field("missing", 6, codec=_node),
    ],
    name="id",
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

    def test_a_union_refuses_nil_both_ways_unless_it_is_wrapped_in_nullable(self) -> None:
        for refuse in (lambda: node.encode(None), lambda: node.decode(None)):
            with self.assertRaises(ValueError) as caught:
                refuse()
            self.assertEqual(str(caught.exception), "Union 'Node': expected an arm, found nil")
        maybe = t.nullable(node)
        self.assertIsNone(maybe.encode(None))
        self.assertIsNone(maybe.decode(None))
        self.assertEqual(maybe.decode(maybe.encode(UnionArm("Text", {"id": "a", "content": "b"}))), UnionArm("Text", {"id": "a", "content": "b"}))
        with self.assertRaises(ValueError) as write:
            surface_document.encode_payload({**_surface_value(), "outcomes": [None]})
        self.assertEqual(
            str(write.exception),
            "tests.union_surface slot 3 (outcomes): element 0: Union 'Outcome': expected an arm, found nil",
        )

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
        self.assertEqual(message, "Union 'Node' does not declare arm key 4 (declared: 0, 1, 2, 5, 6)")

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

    def test_a_list_slot_requires_an_array_and_keeps_nil_elements_of_a_nullable_item(self) -> None:
        slots = [UnionArm("Text", {"id": "a", "content": "b"}), None]
        inner = {"name": "c", "body": None}
        encoded = use.encode({"id": "u", "composition": inner, "slots": slots})
        self.assertEqual(encoded, ["u", ["c", None], [[1, ["a", "b"]], None]])
        self.assertEqual(use.decode(encoded)["slots"], slots)
        with self.assertRaises(ValueError) as caught:
            use.decode(["u", ["c", None], self.CANARY])
        self.assertEqual(str(caught.exception), "Use slot 2 (slots): expected an array, found string")
        with self.assertRaises(ValueError) as nil_element:
            box.decode(["b", [None]])
        self.assertEqual(
            str(nil_element.exception),
            "Box slot 1 (children): element 0: Union 'Node': expected an arm, found nil",
        )

    def test_a_list_of_nested_records_keeps_a_nil_element_as_csharp_writes_it(self) -> None:
        row = define_record_codec(
            "Row", [("id", 0), database_entry_field("cells", 1, codec=t.list(t.nullable(text)))]
        )
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

    def test_a_short_record_is_read_with_nil_for_the_slots_it_lacks_as_the_reference_reads_it(self) -> None:
        self.assertEqual(node.decode([1, ["only-id"]]), UnionArm("Text", {"id": "only-id", "content": None}))
        self.assertEqual(node.decode([0, ["sapphire"]]), UnionArm("Box", {"id": "sapphire", "children": None}))

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
        for union_name, key in (("TrueKey", True), ("FalseKey", False)):
            with self.assertRaises(ValueError) as caught:
                define_union(union_name, {key: ("A", None)})
            self.assertEqual(str(caught.exception), f"Union {union_name!r} arm keys must be integers within Int32")
        edge = define_union("Edge", {-(2**31): ("Low", None), -1: ("Negative", None), 2**31 - 1: ("High", None)})
        for arm_name, key in (("Low", -(2**31)), ("Negative", -1), ("High", 2**31 - 1)):
            self.assertEqual(edge.encode(UnionArm(arm_name)), [key, []])
            self.assertEqual(edge.decode([key, []]), UnionArm(arm_name))
        for loose in (lambda: node, node.encode, "node", object):
            with self.assertRaises(ValueError) as caught:
                database_entry_field("loose", 1, codec=loose)  # type: ignore[arg-type]
            self.assertIn("must be a SlotCodec", str(caught.exception))
        with self.assertRaises(ValueError):
            define_union("Loose", {0: ("A", lambda: node)})  # type: ignore[dict-item]


class RecordCodecTests(unittest.TestCase):
    def test_a_default_fills_a_slot_past_the_payloads_end_and_a_callable_default_is_called(self) -> None:
        record = define_record_codec(
            "Defaulted",
            [("id", 0), ("label", 1, "none"), ("tags", 2, list), ("plain", 3, 0)],
        )
        self.assertEqual(record.decode(["a"]), {"id": "a", "label": "none", "tags": [], "plain": 0})
        self.assertEqual(record.decode(["a", "x"]), {"id": "a", "label": "x", "tags": [], "plain": 0})
        self.assertEqual(record.decode(["a", None, None, None]), {"id": "a", "label": None, "tags": None, "plain": None})
        self.assertEqual(record.decode(["a", "x", ["t"], 5]), {"id": "a", "label": "x", "tags": ["t"], "plain": 5})

    def test_a_document_type_names_its_members_and_marks_the_name_and_index_slots(self) -> None:
        document = define_database_entry_type(
            "tests.members",
            [("id", 0), ("source_version", 1), ("owner", 2)],
            name="id",
            indexes={"by_owner": "owner"},
        )
        members = {member.slot: member for member in document.catalog_entry().members}
        self.assertEqual(
            [members[slot].member_name for slot in (0, 1, 2)], ["Id", "SourceVersion", "Owner"]
        )
        self.assertEqual([members[slot].is_name for slot in (0, 1, 2)], [True, False, False])
        self.assertEqual([members[slot].index_alias for slot in (0, 1, 2)], [None, None, "by_owner"])

    def test_a_document_without_a_string_name_marks_no_member_as_the_name(self) -> None:
        document = define_database_entry_type("tests.named", [("id", 0)], name=lambda value: value["id"])
        self.assertFalse(document.catalog_entry().members[0].is_name)

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

    def test_a_plain_field_helper_makes_an_untyped_slot(self) -> None:
        plain = define_record_codec("Plain", [database_entry_field("id", 0), database_entry_field("label", 1)])
        self.assertEqual(plain.decode(plain.encode({"id": "a", "label": "b"})), {"id": "a", "label": "b"})
        self.assertEqual(plain.decode(["a"]), {"id": "a", "label": None})
        field = database_entry_field("id", 0)
        self.assertIs(field.codec, t.any)

    def test_a_directly_constructed_field_requires_a_default_and_is_untyped(self) -> None:
        with self.assertRaises(TypeError):
            DatabaseEntryField(name="id", key=0)  # type: ignore[call-arg]
        field = DatabaseEntryField(name="id", key=0, default=dataclasses.MISSING)
        self.assertIs(field.codec, t.any)

    def test_slot_codecs_and_fields_are_immutable(self) -> None:
        with self.assertRaises(dataclasses.FrozenInstanceError):
            node.encode = None  # type: ignore[misc]
        with self.assertRaises(dataclasses.FrozenInstanceError):
            database_entry_field("id", 0).key = 1  # type: ignore[misc]

    def test_slot_keys_are_integers_within_zero_and_the_cap_however_the_field_is_made(self) -> None:
        message = "DatabaseEntry field keys must be integers within 0..65535"
        self.assertEqual(MAX_SLOT_KEY, 65535)
        database_entry_field("zero", 0)
        top = define_record_codec("Top", [("last", MAX_SLOT_KEY)])
        self.assertEqual(len(top.encode({"last": "x"})), MAX_SLOT_KEY + 1)
        self.assertEqual(top.decode(top.encode({"last": "x"})), {"last": "x"})
        for make in (
            lambda key: database_entry_field("bad", key),
            lambda key: DatabaseEntryField(name="bad", key=key, default=dataclasses.MISSING),
            lambda key: define_record_codec("Bad", [("bad", key)]),
            lambda key: define_record_codec("Bad", [("bad", key, "d")]),
        ):
            for key in (-1, MAX_SLOT_KEY + 1, 2**31, True, 1.0, "1"):
                with self.subTest(key=key):
                    with self.assertRaises(ValueError) as caught:
                        make(key)
                    self.assertEqual(str(caught.exception), message)

    def test_slot_order_follows_keys_not_declaration_order(self) -> None:
        record = define_record_codec("Ordered", [("b", 2), ("a", 0)])
        self.assertEqual(record.encode({"a": "x", "b": "y"}), ["x", None, "y"])


CANARY = "CANARY-union-golden-4c2a"


def _wrap_chain(wraps: int, leaf: UnionArm) -> UnionArm:
    value = leaf
    for _ in range(wraps):
        value = UnionArm("Wrap", {"inner": value})
    return value


class BoundsVectorTests(unittest.TestCase):
    def test_every_bounds_outcome_is_held(self) -> None:
        entries = msgpack.unpackb(_vector("bounds-outcomes.msgpack"), raw=False)
        self.assertEqual(entries[0], ["limit", MAX_PAYLOAD_DEPTH])
        counts = {"accepted": 0, "refused": 0, "read-only": 0}
        for file, outcome_kind, union, key, detail in entries[1:]:
            counts[outcome_kind] += 1
            payload = _vector(file)
            if outcome_kind == "accepted":
                self.assertEqual(
                    surface_document.encode_payload(surface_document.decode_payload(payload)), _vector(detail), file
                )
            elif outcome_kind == "read-only":
                self.assertEqual(surface_document.decode_payload(payload), _surface_value(), file)
            else:
                with self.assertRaises(ValueError, msg=file) as caught:
                    surface_document.decode_payload(payload)
                message = str(caught.exception)
                self.assertNotIn(CANARY, message, file)
                if detail == "depth":
                    self.assertIsInstance(caught.exception, CultCachePayloadDepthError, file)
                    self.assertIn(surface_document.type, message, file)
                    self.assertIn("nests 257 containers deep", message, file)
                    self.assertIn("the limit is 256", message, file)
                    self.assertIn("refused on read", message, file)
                    continue
                self.assertNotIsInstance(caught.exception, CultCachePayloadDepthError, file)
                self.assertIn(f"Union '{union}' ", message, file)
                if detail == "key-out-of-range":
                    self.assertIn("outside Int32", message, file)
                    self.assertNotIn("does not declare", message, file)
                elif detail == "key-not-integer":
                    self.assertIn("non-integer", message, file)
                else:
                    self.assertEqual(detail, "nil-slots")
                    self.assertIn(f"arm {key} has nil slots", message, file)
        self.assertEqual(counts, {"accepted": 4, "refused": 7, "read-only": 1})

    def test_the_value_at_the_limit_writes_the_vector_and_the_value_past_it_is_refused_on_write(self) -> None:
        at_limit = {**_surface_value(), "root": _wrap_chain(126, UnionArm("Box", {"id": "leaf", "children": []}))}
        self.assertEqual(surface_document.encode_payload(at_limit), _vector("depth-at-limit.msgpack"))
        past = {**_surface_value(), "root": _wrap_chain(127, UnionArm("Text", {"id": "leaf", "content": CANARY}))}
        with self.assertRaises(CultCachePayloadDepthError) as caught:
            surface_document.encode_payload(past)
        message = str(caught.exception)
        self.assertEqual((caught.exception.depth, caught.exception.limit), (257, 256))
        self.assertIn("the limit is 256", message)
        self.assertIn("refused on write", message)
        self.assertNotIn(CANARY, message)

    def test_a_cache_put_of_the_value_past_the_limit_refuses_and_stores_nothing(self) -> None:
        past = {**_surface_value(), "root": _wrap_chain(127, UnionArm("Box", {"id": "leaf", "children": []}))}
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.cc"
            cache = (
                CultCache.builder()
                .register_document_type(surface_document)
                .add_generic_store(SingleFileMessagePackBackingStore(path))
                .build()
            )
            with self.assertRaises(CultCachePayloadDepthError):
                cache.put(surface_document, "surface", past)
            self.assertIsNone(cache.get(surface_document, "surface"))

    def test_a_payload_beyond_the_c_unpackers_own_stack_is_the_depth_refusal(self) -> None:
        deep = b"\x91" * 2000 + b"\xc0"
        with self.assertRaises(CultCachePayloadDepthError) as caught:
            surface_document.decode_payload(deep)
        self.assertEqual(str(caught.exception).count("refused on read"), 1)
        huge = b"\x98" + b"\xc0" * 7 + b"\x91" * 100000 + b"\xc0"
        with self.assertRaises(CultCachePayloadDepthError):
            surface_document.decode_payload(huge)

    def test_a_deep_value_is_refused_by_the_depth_error_not_a_recursion_error(self) -> None:
        tree = define_document_type("tests.deep_tree", encode=lambda value: value)
        with self.assertRaises(CultCachePayloadDepthError) as caught:
            tree.encode_payload(_nest(5000))
        self.assertIn("refused on write", str(caught.exception))
        document = define_document_type("tests.json_tree")
        with self.assertRaises(CultCachePayloadDepthError) as read:
            document.decode_payload(b"[" * 300 + b"]" * 300)
        self.assertIn("refused on read", str(read.exception))

    def test_a_codec_that_exhausts_the_interpreter_stack_keeps_its_own_recursion_error_on_each_side(self) -> None:
        def boom(value: object) -> object:
            return boom(value)

        document = define_document_type("tests.boom", encode=boom, decode=boom)
        for run in (lambda: document.encode_payload("x"), lambda: document.decode_payload(b'"x"')):
            try:
                run()
            except RecursionError as caught:
                self.assertNotIsInstance(caught, CultCachePayloadDepthError)
                self.assertIn("boom", [frame.name for frame in traceback.extract_tb(caught.__traceback__)])
            else:
                self.fail("the codec's RecursionError was swallowed")
        with self.assertRaises(CultCachePayloadDepthError) as stack:
            surface_document.decode_payload(bytes([0x91]) * 2000 + bytes([0xC0]))
        self.assertEqual(
            str(stack.exception),
            "tests.union_surface payload nests 257 containers deep; the limit is 256 (refused on read).",
        )

    def test_a_json_payload_past_the_interpreters_own_stack_keeps_the_decoders_recursion_error(self) -> None:
        document = define_document_type("tests.json_tree")
        with self.assertRaises(RecursionError) as caught:
            document.decode_payload(b"[" * 100000)
        self.assertNotIsInstance(caught.exception, CultCachePayloadDepthError)

    def test_a_refusal_reports_the_depth_where_the_walk_stopped_whatever_the_payloads_real_depth(self) -> None:
        document = define_document_type("tests.json_tree", encode=lambda value: value)
        for depth in (257, 300, 900):
            with self.assertRaises(CultCachePayloadDepthError) as write:
                document.encode_payload(_nest(depth))
            self.assertEqual((write.exception.depth, write.exception.limit), (257, 256), depth)
            with self.assertRaises(CultCachePayloadDepthError) as read:
                document.decode_payload(b"[" * depth + b"]" * depth)
            self.assertEqual((read.exception.depth, read.exception.limit), (257, 256), depth)

    def test_the_validator_sees_the_value_on_write_and_the_decoded_value_on_read(self) -> None:
        seen: list[object] = []
        document = define_document_type("tests.validated", validate=seen.append)
        document.decode_payload(document.encode_payload({"a": 1}))
        self.assertEqual(seen, [{"a": 1}, {"a": 1}])

    def test_a_cycle_is_refused_as_depth(self) -> None:
        loop: list = []
        loop.append(loop)
        document = define_document_type("tests.cycle", encode=lambda value: value)
        with self.assertRaises(CultCachePayloadDepthError):
            document.encode_payload(loop)


def _nest(depth: int, mixed: bool = False) -> object:
    value: object = "leaf"
    for level in range(depth):
        value = {"k": value} if mixed and level % 2 == 0 else [value]
    return value


def _as_tuple(tree: object) -> object:
    return tuple(_as_tuple(child) for child in tree) if isinstance(tree, list) else tree


class PayloadDepthRuleTests(unittest.TestCase):
    """The nesting rule at its seam, on definitions that are not unions: the same routine serves any payload codec."""

    def test_the_limit_is_256_and_depth_256_is_accepted_on_read_and_write_while_257_is_refused(self) -> None:
        self.assertEqual(MAX_PAYLOAD_DEPTH, 256)
        document = define_document_type("tests.json_tree", encode=lambda value: value)
        for mixed in (False, True):
            at_limit = _nest(256, mixed)
            self.assertEqual(document.decode_payload(document.encode_payload(at_limit)), at_limit)
            past = _nest(257, mixed)
            with self.assertRaises(CultCachePayloadDepthError):
                document.encode_payload(past)
            with self.assertRaises(CultCachePayloadDepthError):
                document.decode_payload(document.payload_encoder(past))

    def test_a_tuple_and_a_container_dict_key_count_as_containers(self) -> None:
        document = define_document_type("tests.tuple_tree", encode=lambda value: value, payload_encoder=lambda tree: b"ok")
        self.assertEqual(document.encode_payload(_as_tuple(_nest(256))), b"ok")
        with self.assertRaises(CultCachePayloadDepthError):
            document.encode_payload(_as_tuple(_nest(257)))
        self.assertEqual(document.encode_payload({_as_tuple(_nest(255)): 1}), b"ok")
        with self.assertRaises(CultCachePayloadDepthError):
            document.encode_payload({_as_tuple(_nest(256)): 1})

    def test_every_mapping_sequence_and_set_counts_not_only_list_tuple_and_dict(self) -> None:
        wrappers = {
            "deque": lambda inner: collections.deque([inner]),
            "mappingproxy": lambda inner: types.MappingProxyType({"k": inner}),
            "frozenset": lambda inner: frozenset([inner]),
            "userlist": lambda inner: collections.UserList([inner]),
        }
        for name, wrap in wrappers.items():
            document = define_document_type(f"tests.{name}", encode=lambda value: value, payload_encoder=lambda tree: b"ok")
            at_limit: object = "leaf"
            for _ in range(256):
                at_limit = wrap(at_limit)
            self.assertEqual(document.encode_payload(at_limit), b"ok", name)
            with self.assertRaises(CultCachePayloadDepthError, msg=name):
                document.encode_payload(wrap(at_limit))
            decoding = define_document_type(f"tests.{name}.read", payload_decoder=lambda payload, w=wrap: w(at_limit))
            with self.assertRaises(CultCachePayloadDepthError, msg=name):
                decoding.decode_payload(b"x")

    def test_text_and_bytes_are_leaves_however_long(self) -> None:
        document = define_document_type("tests.leaves", encode=lambda value: value, payload_encoder=lambda tree: b"ok")
        self.assertEqual(document.encode_payload([["x" * 100000, b"y" * 100000, bytearray(5), memoryview(b"z")]]), b"ok")

    def test_a_scalar_payload_has_no_depth(self) -> None:
        document = define_document_type("tests.scalar", encode=lambda value: value)
        for scalar in ("x", 5, None):
            self.assertEqual(document.decode_payload(document.encode_payload(scalar)), scalar)

    def test_the_refusal_names_the_type_the_depth_and_the_limit_and_never_the_payload(self) -> None:
        document = define_document_type("tests.json_tree", encode=lambda value: value)
        value: object = CANARY
        for _ in range(257):
            value = [value]
        with self.assertRaises(CultCachePayloadDepthError) as caught:
            document.encode_payload(value)
        self.assertEqual(
            str(caught.exception),
            "tests.json_tree payload nests 257 containers deep; the limit is 256 (refused on write).",
        )
        self.assertIsInstance(caught.exception, ValueError)


class UnionDecodeBoundsTests(unittest.TestCase):
    def test_the_arm_key_refusals_name_the_union_and_say_what_is_wrong(self) -> None:
        cases = [
            ([2**31, []], "Union 'Node' value is malformed: the arm key is outside Int32"),
            ([-(2**31) - 1, []], "Union 'Node' value is malformed: the arm key is outside Int32"),
            ([1.0, []], "Union 'Node' value is malformed: the arm key is non-integer"),
            ([True, []], "Union 'Node' value is malformed: the arm key is non-integer"),
            ([1, None], "Union 'Node' arm 1 has nil slots"),
            ([1, CANARY], "Union 'Node' value is malformed: expected [armKey, [armSlots]]"),
        ]
        for raw, expected in cases:
            with self.subTest(key=raw[0], slots=type(raw[1]).__name__):
                with self.assertRaises(ValueError) as caught:
                    node.decode(raw)
                self.assertEqual(str(caught.exception), expected)

    def test_the_ends_of_the_int32_key_domain_are_unit_arms_that_read_and_write(self) -> None:
        self.assertEqual(outcome.decode([-(2**31), []]), UnionArm("Floor"))
        self.assertEqual(outcome.encode(UnionArm("Ceiling")), [2**31 - 1, []])


if __name__ == "__main__":
    unittest.main()
