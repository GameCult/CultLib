from __future__ import annotations

import math
import struct
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
    leaves as t,
)
from cultcache_py.documents import _pack_tree
from cultcache_py.interop import interop_note_document

VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "numeric-leaves"
CANARY = "CANARY-leaf-9d41"
REFERENCE = "GameCult.Caching.Tests.NumericLeafVectorTests+"


def _vector(name: str) -> list:
    return msgpack.unpackb((VECTORS / name).read_bytes(), raw=False)


def _slot(leaf: t.SlotCodec):
    return define_database_entry_type("tests.leaf", [database_entry_field("v", 0, codec=leaf)])


def _doubles(bits: bytes) -> float:
    return struct.unpack(">d", bits)[0]


def _is_signalling_float32(payload: bytes) -> bool:
    if len(payload) != 6 or payload[1] != 0xCA:
        return False
    word = struct.unpack(">I", payload[2:])[0]
    return word & 0x7F800000 == 0x7F800000 and word & 0x007FFFFF != 0 and word & 0x00400000 == 0


def _kinded(node):
    """A decoded tree with its kinds kept: bool, int and float never compare equal; numeric width is not part of it."""
    if isinstance(node, bool):
        return ("bool", node)
    if isinstance(node, int):
        return ("int", node)
    if isinstance(node, float):
        return ("float", "nan" if math.isnan(node) else struct.pack(">d", node))
    if isinstance(node, (list, tuple)):
        return ("array", [_kinded(item) for item in node])
    if isinstance(node, dict):
        return ("map", [(_kinded(k), _kinded(v)) for k, v in node.items()])
    return (type(node).__name__, node)


NUMERIC = {"int32": t.int32, "int64": t.int64, "float32": t.float32, "float64": t.float64}


class NumericVectorTests(unittest.TestCase):
    def test_reads_accept_refuse_and_round_exactly_as_the_reference(self) -> None:
        rows = _vector("reads.msgpack")
        self.assertEqual(len(rows), 160)
        accepted = refused = 0
        for leaf, payload, was_accepted, bits in rows:
            label = f"{leaf} {payload.hex()}"
            document = _slot(NUMERIC[leaf])
            if not was_accepted:
                with self.assertRaises(ValueError, msg=label):
                    document.decode_payload(payload)
                refused += 1
                continue
            read = document.decode_payload(payload)["v"]
            if leaf.startswith("float"):
                if math.isnan(_doubles(bits)):
                    self.assertTrue(math.isnan(read), label)
                else:
                    self.assertEqual(struct.pack(">d", read), bits, label)
            else:
                self.assertEqual(read, int.from_bytes(bits, "big", signed=True), label)
            accepted += 1
        self.assertEqual((accepted + refused, accepted > 40, refused > 40), (160, True, True))

    def test_writes_are_the_bytes_the_reference_writes(self) -> None:
        rows = _vector("writes.msgpack")
        self.assertEqual(len(rows), 46)
        for leaf, text, expected in rows:
            value = _doubles(bytes.fromhex(text)) if leaf.startswith("float") else int(text)
            self.assertEqual(_slot(NUMERIC[leaf]).encode_payload({"v": value}), expected, f"{leaf} {text}")

    def test_the_mixed_record_is_written_byte_for_byte_and_read_back(self) -> None:
        mixed = define_database_entry_type(
            "tests.mixed",
            [
                database_entry_field("id", 0, codec=t.string),
                database_entry_field("i", 1, codec=t.int32),
                database_entry_field("l", 2, codec=t.int64),
                database_entry_field("f", 3, codec=t.float32),
                database_entry_field("d", 4, codec=t.float64),
            ],
        )
        committed = (VECTORS / "mixed-record.msgpack").read_bytes()
        value = {"id": "n1", "i": 7, "l": 8, "f": 1.5, "d": 2.5}
        self.assertEqual(mixed.encode_payload(value), committed)
        self.assertEqual(mixed.decode_payload(committed), value)

    def test_float32_infinities_and_quiet_nans_keep_their_bits_and_signalling_ones_come_back_quieted(self) -> None:
        rows = _vector("float32-nan.msgpack")
        self.assertEqual(len(rows), 6)
        document = _slot(t.float32)
        quieted = 0
        for payload, reference in rows:
            written = document.encode_payload(document.decode_payload(payload))
            if _is_signalling_float32(payload):
                # follow_up py-float32-snan-quieted: msgpack's unpacker quiets the NaN before the leaf sees it.
                self.assertEqual(reference, payload, "the reference keeps the signalling bits")
                word = struct.unpack(">I", payload[2:])[0] | 0x00400000
                self.assertEqual(written, b"\x91\xca" + struct.pack(">I", word))
                quieted += 1
            else:
                self.assertEqual(written, reference, payload.hex())
        self.assertEqual(quieted, 3)


def _inner_record() -> t.SlotCodec:
    return define_record_codec(
        "LeafInner",
        [
            database_entry_field("n", 0, default="", codec=t.string),
            database_entry_field("w", 1, default=0.0, codec=t.float32),
            database_entry_field("id", 2, default="", codec=t.string),
        ],
        type_name=REFERENCE + "LeafInner",
    )


def _shape() -> t.SlotCodec:
    circle = define_record_codec(
        "LeafCircle", [database_entry_field("r", 0, default=0.0, codec=t.float32)], type_name=REFERENCE + "LeafCircle"
    )
    return define_union("LeafShape", {0: ("LeafCircle", circle)}, type_name=REFERENCE + "ILeafShape")


# The C# slot types of leaves-reads.msgpack, each as a reference-type slot that may be null (nullable) and bare.
def _reference_leaves(nullable: bool) -> dict[str, t.SlotCodec]:
    inner, shape = _inner_record(), _shape()
    wrap = t.nullable if nullable else (lambda leaf: leaf)
    return {
        "string": wrap(t.string),
        "bytes": wrap(t.bytes),
        "list-string": wrap(t.list(t.string)),
        "array-string": wrap(t.array(t.string)),
        "list-int32": wrap(t.list(t.int32)),
        "record": wrap(inner),
        "list-record": wrap(t.list(inner)),
        "union": wrap(shape),
    }


class LeavesReadsVectorTests(unittest.TestCase):
    NIL = bytes.fromhex("91c0")

    def _check(self, leaf_name: str, leaf: t.SlotCodec, rows: list, *, nil_refused: bool = False) -> int:
        document = _slot(leaf)
        checked = 0
        for name, payload, accepted, rewrite in rows:
            if name != leaf_name:
                continue
            label = f"{name} {payload.hex()}"
            if not accepted or (nil_refused and payload == self.NIL):
                with self.assertRaises(ValueError, msg=label):
                    document.decode_payload(payload)
            else:
                value = document.decode_payload(payload)
                self.assertEqual(document.encode_payload(value), rewrite, label)
            checked += 1
        return checked

    def test_reference_type_leaves_held_as_nullable_read_and_rewrite_exactly_as_csharp(self) -> None:
        rows = _vector("leaves-reads.msgpack")
        self.assertEqual(len(rows), 308)
        extra = {"nullable-int32": t.nullable(t.int32)}
        for name, leaf in {**_reference_leaves(True), **extra}.items():
            self.assertEqual(self._check(name, leaf, rows), 28, name)
            self.assertTrue(any(row[0] == name and row[2] for row in rows) and any(row[0] == name and not row[2] for row in rows), name)

    def test_the_bare_leaf_accepts_the_same_rows_minus_nil(self) -> None:
        rows = _vector("leaves-reads.msgpack")
        for name, leaf in _reference_leaves(False).items():
            self.assertEqual(self._check(name, leaf, rows, nil_refused=True), 28, name)

    def test_boolean_is_a_bare_value_leaf(self) -> None:
        self.assertEqual(self._check("boolean", t.boolean, _vector("leaves-reads.msgpack")), 28)

    def test_any_reads_what_the_reference_reads_and_rewrites_to_the_same_value(self) -> None:
        # ruling any-rewrite-binds-kind-and-value: the kind and exact value of the rewrite, recursively; numeric width is free ("05" -> "cc05").
        rows = _vector("leaves-reads.msgpack")
        document = _slot(t.any)
        count = 0
        for name, payload, accepted, rewrite in rows:
            if name != "any":
                continue
            label = f"any {payload.hex()}"
            if not accepted:
                with self.assertRaises(ValueError, msg=label):
                    document.decode_payload(payload)
            else:
                written = document.encode_payload(document.decode_payload(payload))
                self.assertEqual(_kinded(msgpack.unpackb(written, raw=False)), _kinded(msgpack.unpackb(rewrite, raw=False)), label)
            count += 1
        self.assertEqual(count, 28)

    def test_the_any_comparison_tells_bool_int_and_float_apart_and_ignores_only_numeric_width(self) -> None:
        # ruling any-rewrite-binds-kind-and-value: Python's == makes True == 1 == 1.0; _kinded does not.
        self.assertNotEqual(_kinded([True]), _kinded([1]))
        self.assertNotEqual(_kinded([1]), _kinded([1.0]))
        self.assertNotEqual(_kinded({"k": [5]}), _kinded({"k": [5.0]}))
        self.assertNotEqual(_kinded([0.0]), _kinded([-0.0]))
        self.assertNotEqual(_kinded(["a"]), _kinded([b"a"]))
        self.assertEqual(_kinded(msgpack.unpackb(bytes.fromhex("9105"))), _kinded(msgpack.unpackb(bytes.fromhex("91cc05"))))
        self.assertEqual(
            _kinded(msgpack.unpackb(bytes.fromhex("91ca3fc00000"))), _kinded(msgpack.unpackb(bytes.fromhex("91cb3ff8000000000000")))
        )

    def test_any_refuses_an_extension_that_is_not_a_timestamp_wherever_it_nests(self) -> None:
        document = _slot(t.any)
        for payload in (bytes.fromhex("91d40100"), bytes.fromhex("9191d40100"), bytes.fromhex("9181a161d40100")):
            with self.assertRaises(ValueError, msg=payload.hex()):
                document.decode_payload(payload)
        with self.assertRaises(ValueError):
            t.any.encode({"k": [msgpack.ExtType(1, b"\x00")]})
        with self.assertRaises(ValueError) as caught:
            t.any.decode(msgpack.ExtType(1, b"\x00"))
        self.assertEqual(str(caught.exception), "expected an object, found ext")
        self.assertIsNone(t.any.decode(None))
        self.assertIsNone(t.any.encode(None))


def _leaf_record_document():
    inner, shape = _inner_record(), _shape()
    document = define_database_entry_type(
        "cultcache.leaf-record",
        [
            database_entry_field("id", 0, codec=t.string),
            database_entry_field("flag", 1, codec=t.boolean),
            database_entry_field("blob", 2, codec=t.bytes),
            database_entry_field("i", 3, codec=t.int32),
            database_entry_field("l", 4, codec=t.int64),
            database_entry_field("f", 5, codec=t.float32),
            database_entry_field("d", 6, codec=t.float64),
            database_entry_field("maybe_i", 7, codec=t.nullable(t.int32)),
            database_entry_field("tags", 8, codec=t.list(t.string)),
            database_entry_field("names", 9, codec=t.array(t.string)),
            database_entry_field("any", 10, codec=t.any),
            database_entry_field("nested", 11, codec=t.nullable(inner)),
            database_entry_field("items", 12, codec=t.list(inner)),
            database_entry_field("shape", 13, codec=t.nullable(shape)),
            database_entry_field("floats", 14, codec=t.array(t.float32)),
            database_entry_field("maybe_f", 15, codec=t.nullable(t.float32)),
        ],
        name="id",
    )
    return document


class LeafRecordCatalogTests(unittest.TestCase):
    def test_a_python_mirror_derives_the_catalog_the_csharp_store_writes_and_the_payload_round_trips(self) -> None:
        document = _leaf_record_document()
        members, payload = _vector("leaf-record.msgpack")
        derived = [
            [m.slot, m.member_name, m.type_name, m.is_reference, m.is_many, m.target_schema_name, m.is_name, m.index_alias]
            for m in document.catalog_entry().members
        ]
        self.assertEqual(derived, members)
        self.assertEqual(document.encode_payload(document.decode_payload(payload)), payload)

    def test_nullable_names_a_value_type_nullable_and_a_reference_type_as_itself(self) -> None:
        self.assertEqual(t.nullable(t.int32).type_name, "System.Nullable<System.Int32>")
        self.assertEqual(t.nullable(t.boolean).type_name, "System.Nullable<System.Boolean>")
        self.assertEqual(t.nullable(t.string).type_name, "System.String")
        self.assertEqual(t.nullable(t.list(t.string)).type_name, "System.Collections.Generic.List<System.String>")
        self.assertEqual(t.array(t.nullable(t.int64)).type_name, "System.Nullable<System.Int64>[]")
        self.assertEqual(t.array(t.float32).type_name, "System.Single[]")
        self.assertEqual(t.bytes.type_name, "System.Byte[]")
        self.assertFalse(t.nullable(t.int32).value_type)
        self.assertEqual([c.value_type for c in (t.string, t.boolean, t.bytes, t.int32, t.int64, t.float32, t.float64, t.any)],
                         [False, True, False, True, True, True, True, False])

    def test_an_unnamed_record_or_union_is_listed_as_an_untyped_slot_and_a_list_of_one_too(self) -> None:
        record = define_record_codec("Plain", [("a", 0)])
        self.assertIsNone(record.type_name)
        self.assertIsNone(t.list(record).type_name)
        self.assertIsNone(t.nullable(record).type_name)
        document = define_database_entry_type(
            "tests.unnamed", [database_entry_field("r", 0, codec=record), database_entry_field("l", 1, codec=t.list(record))]
        )
        self.assertEqual([m.type_name for m in document.catalog_entry().members], ["System.Object", "System.Object"])

    def test_a_recursive_union_through_lazy_encodes_decodes_and_names_its_type(self) -> None:
        link = define_record_codec(
            "Link", [database_entry_field("next", 0, codec=t.nullable(t.lazy(lambda: chain)))], type_name="Tests.Link"
        )
        chain = define_union("Chain", {0: ("Link", link), 1: ("End", None)}, type_name="Tests.Chain")
        self.assertEqual(t.lazy(lambda: chain).type_name, "Tests.Chain")
        self.assertEqual(t.nullable(t.lazy(lambda: chain)).type_name, "Tests.Chain")
        value = UnionArm("Link", {"next": UnionArm("Link", {"next": UnionArm("End")})})
        written = chain.encode(value)
        self.assertEqual(written, [0, [[0, [[1, []]]]]])
        self.assertEqual(chain.decode(written), value)
        self.assertIs(t.lazy(lambda: t.int32).value_type, True)
        document = define_database_entry_type("tests.chain", [database_entry_field("root", 0, codec=t.nullable(chain))])
        self.assertEqual(document.catalog_entry().members[0].type_name, "Tests.Chain")

    def test_a_lazy_leaf_resolves_its_target_once(self) -> None:
        calls = []
        leaf = t.lazy(lambda: calls.append(1) or t.int32)
        self.assertEqual(calls, [])
        leaf.encode(1)
        leaf.decode(2)
        leaf.type_name
        self.assertEqual(calls, [1])


class SlotNilAndDefaultTests(unittest.TestCase):
    def test_a_present_nil_goes_to_the_leaf_and_a_default_fills_only_a_slot_past_the_payloads_end(self) -> None:
        document = define_database_entry_type(
            "tests.defaults",
            [
                database_entry_field("count", 0, default=0, codec=t.int32),
                database_entry_field("label", 1, default="", codec=t.string),
                database_entry_field("maybe", 2, default="dflt", codec=t.nullable(t.string)),
                database_entry_field("tags", 3, default=list, codec=t.list(t.string)),
            ],
        )
        self.assertEqual(
            document.decode_payload(msgpack.packb([3, "x", None, ["a"]])), {"count": 3, "label": "x", "maybe": None, "tags": ["a"]}
        )
        self.assertEqual(document.decode_payload(msgpack.packb([3])), {"count": 3, "label": "", "maybe": "dflt", "tags": []})
        self.assertIsNot(
            document.decode_payload(msgpack.packb([3]))["tags"], document.decode_payload(msgpack.packb([3]))["tags"]
        )
        for bad in ([None], [3, None], [3, "x", None, None]):
            with self.assertRaises(ValueError, msg=str(bad)):
                document.decode_payload(msgpack.packb(bad))

    def test_a_write_missing_a_non_nullable_field_is_refused_naming_the_slot(self) -> None:
        document = define_database_entry_type(
            "tests.required",
            [database_entry_field("count", 0, codec=t.int32), database_entry_field("label", 1, codec=t.nullable(t.string))],
        )
        self.assertEqual(document.encode_payload({"count": 1}), msgpack.packb([1, None]))
        with self.assertRaises(ValueError) as caught:
            document.encode_payload({"label": "x"})
        self.assertEqual(str(caught.exception), "tests.required slot 0 (count): expected an int32, found nil")
        with self.assertRaises(ValueError) as nil_record:
            define_record_codec("R", [("a", 0)]).encode(None)
        self.assertIn("found nil", str(nil_record.exception))

    def test_an_untyped_definition_keeps_its_catalog_and_bytes_and_reads_a_present_nil_as_none(self) -> None:
        document = define_database_entry_type("tests.untyped", [("name", 0), ("owner_id", 1, "none"), ("tags", 3, list)])
        self.assertEqual([(m.slot, m.member_name, m.type_name) for m in document.catalog_entry().members],
                         [(0, "Name", "System.Object"), (1, "OwnerId", "System.Object"), (3, "Tags", "System.Object")])
        # Values computed with the code at base 4ec4bfb7.
        self.assertEqual(document.encode_payload({"name": "n", "owner_id": "o", "tags": ["a"]}).hex(), "94a16ea16fc091a161")
        self.assertEqual(document.decode_payload(bytes.fromhex("94a16ea16fc091a161")), {"name": "n", "owner_id": "o", "tags": ["a"]})
        self.assertEqual(document.decode_payload(bytes.fromhex("94a16ec0c0c0")), {"name": "n", "owner_id": None, "tags": None})
        self.assertEqual(document.decode_payload(bytes.fromhex("91a16e")), {"name": "n", "owner_id": "none", "tags": []})
        self.assertEqual(_pack_tree(["x", 1.5, {"k": [b"y"]}]), msgpack.packb(["x", 1.5, {"k": [b"y"]}], use_bin_type=True))

    def test_the_interop_note_catalog_is_the_one_the_hand_written_definition_had(self) -> None:
        entry = interop_note_document.catalog_entry()
        names = [(m.slot, m.member_name, m.type_name, m.is_name) for m in entry.members]
        self.assertEqual(
            names,
            [(0, "SchemaVersion", "System.String", False), (1, "DocumentId", "System.String", True),
             (2, "AuthorRuntimeId", "System.String", False), (3, "Title", "System.String", False),
             (4, "Body", "System.String", False), (5, "Tags", "System.String[]", False)],
        )
        # Captured from the hand-written definition at base 4ec4bfb7.
        self.assertEqual(entry.canonical_schema_json, _BASE_INTEROP_CATALOG)
        self.assertEqual(
            (entry.schema_id, entry.schema_name, entry.schema_version, entry.content_hash, entry.compatible_schema_ids),
            ("cultcache.interop-note", "cultcache.interop-note", "cultcache.interop_note.v1", "cultcache.interop-note",
             ("cultcache.interop-note",)),
        )
        note = {"schemaVersion": "cultcache.interop_note.v1", "documentId": "note:py", "authorRuntimeId": "py",
                "title": "t", "body": "b", "tags": ["py", "interop"]}
        self.assertEqual(interop_note_document.encode_payload(note).hex(), _BASE_INTEROP_PAYLOAD)
        self.assertEqual(interop_note_document.decode_payload(bytes.fromhex(_BASE_INTEROP_PAYLOAD)), note)
        self.assertEqual(interop_note_document.decode_payload(msgpack.packb(["v", "d", "a", "t", "b"]))["tags"], [])


_BASE_INTEROP_CATALOG = (
    '{"schemaName":"cultcache.interop-note","schemaVersion":"cultcache.interop_note.v1","members":['
    '{"slot":0,"name":"SchemaVersion","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},'
    '{"slot":1,"name":"DocumentId","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":true},'
    '{"slot":2,"name":"AuthorRuntimeId","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},'
    '{"slot":3,"name":"Title","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},'
    '{"slot":4,"name":"Body","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},'
    '{"slot":5,"name":"Tags","type":"System.String[]","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false}]}'
)
_BASE_INTEROP_PAYLOAD = "96" + "".join(
    msgpack.packb(v, use_bin_type=True).hex()
    for v in ("cultcache.interop_note.v1", "note:py", "py", "t", "b", ["py", "interop"])
)


class LeafRuleTests(unittest.TestCase):
    def _refusal(self, run) -> str:
        with self.assertRaises(ValueError) as caught:
            run()
        return str(caught.exception)

    def test_refusals_name_the_slot_and_the_kind_found_and_never_the_value(self) -> None:
        cases = [
            (t.int32, CANARY, "expected an int32, found string"),
            (t.int64, 1.5, "expected an int64, found float"),
            (t.string, b"bin", "expected a string, found bytes"),
            (t.boolean, 1, "expected a boolean, found integer"),
            (t.bytes, 5, "expected bytes, found integer"),
            (t.float32, CANARY, "expected a float32, found string"),
            (t.float64, [CANARY], "expected a float64, found array"),
            (t.list(t.string), {CANARY: 1}, "expected an array, found map"),
            (t.string, None, "expected a string, found nil"),
            (t.int32, True, "expected an int32, found boolean"),
            (t.int32, 2**31, "expected an int32, found an integer out of range"),
        ]
        for leaf, value, expected in cases:
            document = _slot(leaf)
            with self.subTest(expected=expected, direction="write"):
                self.assertEqual(self._refusal(lambda: document.encode_payload({"v": value})), f"tests.leaf slot 0 (v): {expected}")
            with self.subTest(expected=expected, direction="read"):
                payload = msgpack.packb([value], use_bin_type=True)
                message = self._refusal(lambda: document.decode_payload(payload))
                self.assertTrue(message.startswith("tests.leaf slot 0 (v): expected "), message)
                self.assertNotIn(CANARY, message)

    def test_a_list_element_refusal_names_the_element_and_a_union_arm_refusal_names_the_arm(self) -> None:
        self.assertEqual(
            self._refusal(lambda: t.list(t.int32).decode([1, "x"])), "element 1: expected an int32, found string"
        )
        self.assertEqual(
            self._refusal(lambda: t.array(t.list(t.int32)).encode([[1], [2, None]])),
            "element 1: element 1: expected an int32, found nil",
        )
        arm = define_union("U", {3: ("A", define_record_codec("A", [database_entry_field("n", 0, codec=t.int32)]))})
        self.assertEqual(
            self._refusal(lambda: arm.decode([3, [CANARY]])), "Union 'U' arm 3: A slot 0 (n): expected an int32, found string"
        )
        self.assertEqual(
            self._refusal(lambda: arm.encode(UnionArm("A", {"n": None}))), "Union 'U' arm 3: A slot 0 (n): expected an int32, found nil"
        )

    def test_string_boolean_and_bytes_domains(self) -> None:
        self.assertEqual(t.string.encode("é"), "é")
        self.assertIs(t.boolean.decode(True), True)
        self.assertEqual(t.bytes.encode(bytearray(b"ab")), b"ab")
        self.assertEqual(t.bytes.encode(memoryview(b"ab")), b"ab")
        self.assertIs(type(t.bytes.encode(bytearray(b"ab"))), bytes)
        self.assertEqual(t.bytes.decode("é"), "é".encode("utf-8"))
        self.assertEqual(t.bytes.decode(b"\x00"), b"\x00")
        self.assertEqual(t.bytes.decode(""), b"")
        for refuse in (lambda: t.bytes.decode(5), lambda: t.bytes.encode("str")):
            self.assertIn("expected bytes", self._refusal(refuse))

    def test_integer_leaves_hold_their_ranges_and_refuse_floats_and_booleans(self) -> None:
        self.assertEqual((t.int32.encode(-(2**31)), t.int32.encode(2**31 - 1)), (-(2**31), 2**31 - 1))
        self.assertEqual((t.int64.decode(-(2**63)), t.int64.decode(2**63 - 1)), (-(2**63), 2**63 - 1))
        for leaf, value in ((t.int32, -(2**31) - 1), (t.int32, 2**31), (t.int64, -(2**63) - 1), (t.int64, 2**63), (t.int64, 2**64 - 1)):
            self.assertIn("out of range", self._refusal(lambda: leaf.decode(value)))
            self.assertIn("out of range", self._refusal(lambda: leaf.encode(value)))
        for bad in (1.0, True, False, "1", None):
            self.assertIn("expected an int32", self._refusal(lambda: t.int32.decode(bad)))

    def test_float_leaves_take_ints_and_floats_round_once_and_refuse_booleans(self) -> None:
        self.assertEqual(t.float32.decode(0.1), struct.unpack(">f", struct.pack(">f", 0.1))[0])
        self.assertEqual(t.float32.decode(2**24 + 1), float(2**24))
        self.assertEqual(t.float32.decode(1e300), math.inf)
        self.assertEqual(t.float32.decode(-1e300), -math.inf)
        self.assertEqual(t.float64.decode(2**53 + 1), float(2**53))
        # past 2**53 a double then a float32 rounding can differ from one rounding: 2**62 + 2**38 + 1 is above the tie.
        value = 2**62 + 2**38 + 1
        self.assertEqual(t.float32.decode(value), float(2**62 + 2**39))
        self.assertEqual(t.float32.decode(-value), -float(2**62 + 2**39))
        tie_even, tie_odd = 2**62 + 2**38, 2**62 + 2**39 + 2**38
        self.assertEqual(t.float32.decode(tie_even), float(2**62))
        self.assertEqual(t.float32.decode(tie_odd), float(2**62 + 2**40))
        self.assertEqual(t.float32.encode(2**64 - 1), t.Float32(float(2**64)))
        self.assertEqual(t.float32.encode(1.5), t.Float32(1.5))
        self.assertEqual(t.float64.encode(3), 3.0)
        self.assertIs(type(t.float64.encode(3)), float)
        for leaf in (t.float32, t.float64):
            for bad in (True, "1", None, [1.0]):
                self.assertIn("expected a float", self._refusal(lambda: leaf.decode(bad)))
                self.assertIn("expected a float", self._refusal(lambda: leaf.encode(bad)))
            self.assertIn("out of range", self._refusal(lambda: leaf.encode(2**64)))
            self.assertIn("out of range", self._refusal(lambda: leaf.encode(-(2**63) - 1)))

    def test_list_and_array_write_lists_and_tuples_and_read_only_arrays(self) -> None:
        self.assertEqual(t.list(t.int32).encode((1, 2)), [1, 2])
        self.assertEqual(t.array(t.string).decode(["a"]), ["a"])
        self.assertEqual(t.list(t.int32).encode([]), [])
        self.assertIn("expected an array", self._refusal(lambda: t.list(t.int32).encode("12")))
        self.assertIn("expected an array", self._refusal(lambda: t.list(t.int32).decode((1,))))
        self.assertIn("expected an array", self._refusal(lambda: t.array(t.int32).decode(None)))

    def test_nullable_passes_none_and_otherwise_defers(self) -> None:
        maybe = t.nullable(t.int32)
        self.assertIsNone(maybe.encode(None))
        self.assertIsNone(maybe.decode(None))
        self.assertEqual((maybe.encode(0), maybe.decode(0)), (0, 0))
        self.assertIn("expected an int32", self._refusal(lambda: maybe.decode("x")))
        self.assertEqual((t.nullable(t.boolean).encode(False), t.nullable(t.string).decode("")), (False, ""))

    def test_a_field_codec_must_be_a_slot_codec(self) -> None:
        self.assertIn("must be a SlotCodec", self._refusal(lambda: database_entry_field("a", 0, codec=lambda: t.int32)))  # type: ignore[arg-type]


class LeafBoundaryTests(unittest.TestCase):
    def _refusal(self, run) -> str:
        with self.assertRaises(ValueError) as caught:
            run()
        return str(caught.exception)

    def test_float32_writes_a_subnormal_as_a_float_32_with_its_exact_bits(self) -> None:
        document = _slot(t.float32)
        for value, expected in (
            (1e-40, "91ca000116c2"),
            (-1e-40, "91ca800116c2"),
            (1.401298464324817e-45, "91ca00000001"),
            (7e-46, "91ca00000000"),
            (1.1754942106924411e-38, "91ca007fffff"),
        ):
            self.assertEqual(document.encode_payload({"v": value}).hex(), expected, repr(value))

    def test_float_leaves_write_an_int_across_the_whole_64_bit_range_and_refuse_beyond_it(self) -> None:
        for leaf in (t.float32, t.float64):
            for ok in (3, 0, -(2**63), 2**64 - 1):
                leaf.encode(ok)
            for bad in (-(2**63) - 1, 2**64):
                what = "a float32" if leaf is t.float32 else "a float64"
                expected = f"expected {what}, found an integer out of range"
                self.assertEqual(self._refusal(lambda: leaf.encode(bad)), expected)
                self.assertEqual(self._refusal(lambda: leaf.decode(bad)), expected)
        self.assertEqual(t.float32.encode(3), t.Float32(3.0))
        self.assertEqual(t.float32.encode(-(2**63)), t.Float32(-float(2**63)))
        self.assertEqual(t.float64.encode(-(2**63)), -float(2**63))
        self.assertEqual(t.float64.encode(2**64 - 1), float(2**64))

    def test_an_integer_past_2_to_53_rounds_to_float32_once_not_through_a_double(self) -> None:
        # 2**53 + 2**29 + 1 is above the float32 tie, but rounding to a double first lands on the tie and rounds down.
        value = 2**53 + 2**29 + 1
        self.assertEqual(t.float32.decode(value), float(2**53 + 2**30))
        self.assertEqual(t.float32.decode(-value), -float(2**53 + 2**30))
        self.assertEqual(t.float32.encode(value), t.Float32(float(2**53 + 2**30)))
        self.assertEqual(t.float32.decode(2**53), float(2**53))
        self.assertEqual(t.float32.decode(-(2**53)), -float(2**53))

    def test_the_kind_a_refusal_reports(self) -> None:
        for value, kind in ((msgpack.ExtType(1, b""), "ext"), (object(), "other"), (t.Float32(1.0), "float"),
                            (bytearray(1), "bytes"), ((1,), "array"), ({}, "map"), (True, "boolean")):
            self.assertEqual(self._refusal(lambda: t.int32.encode(value)), f"expected an int32, found {kind}")

    def test_definition_time_refusals_say_what_is_wrong(self) -> None:
        self.assertEqual(
            self._refusal(lambda: database_entry_field("a", 0, codec=object)),  # type: ignore[arg-type]
            "DatabaseEntry field codec must be a SlotCodec: a leaf from cultcache_py.leaves, or leaves.lazy",
        )
        self.assertEqual(
            self._refusal(lambda: define_union("Loose", {0: ("A", object)})),  # type: ignore[dict-item]
            "Union 'Loose' arm codecs must be SlotCodecs or None",
        )
        self.assertEqual(
            self._refusal(lambda: define_record_codec("R", [("a", 0)]).encode(None)),
            "DatabaseEntry document type 'R': expected a record, found nil",
        )

    def test_value_objects_are_immutable_and_unnamed_by_default(self) -> None:
        import dataclasses

        plain = t.SlotCodec(lambda v: v, lambda v: v)
        self.assertIsNone(plain.type_name)
        self.assertIs(plain.value_type, False)
        self.assertIs(t.nullable(plain).value_type, False)
        self.assertIs(t.list(plain).value_type, False)
        self.assertIsNone(t.nullable(plain).type_name)
        for frozen, field_name in ((plain, "encode"), (t.Float32(1.0), "value"), (t.nullable(t.int32), "encode")):
            with self.assertRaises(dataclasses.FrozenInstanceError):
                setattr(frozen, field_name, None)

    def test_a_definition_made_without_members_has_an_empty_catalog(self) -> None:
        from cultcache_py.documents import DocumentDefinition

        self.assertEqual(DocumentDefinition(type="tests.bare").catalog_entry().members, ())


class BytesAtTheDepthSeamTests(unittest.TestCase):
    def test_bytes_bytearray_and_memoryview_leaves_at_depth_256_are_accepted_on_write_and_read(self) -> None:
        blob = define_record_codec("Blob", [database_entry_field("data", 0, codec=t.list(t.bytes))])
        wrap = define_record_codec("Wrap", [database_entry_field("inner", 0, codec=t.nullable(t.lazy(lambda: deep_union)))])
        deep_union = define_union("Deep", {0: ("Wrap", wrap), 1: ("Blob", blob)})
        document = define_database_entry_type("tests.deep_bytes", [database_entry_field("root", 0, codec=deep_union)])
        for data in (b"\x01\x02", bytearray(b"\x01\x02"), memoryview(b"\x01\x02")):
            value = UnionArm("Blob", {"data": [data]})
            for _ in range(126):
                value = UnionArm("Wrap", {"inner": value})
            payload = document.encode_payload({"root": value})
            decoded = document.decode_payload(payload)
            self.assertEqual(document.encode_payload(decoded), payload)
            leaf = decoded["root"]
            while leaf.name == "Wrap":
                leaf = leaf.value["inner"]
            self.assertEqual(leaf.value["data"], [b"\x01\x02"])


class PackTreeTests(unittest.TestCase):
    def test_a_float32_anywhere_in_the_tree_is_written_as_float_32_and_everything_else_as_packb_does(self) -> None:
        tree = [t.Float32(1.5), {"k": t.Float32(0.1), "b": b"\x01", "s": "x", "n": None}, (t.Float32(2.0), [True, 7, 2.5])]
        packer = msgpack.Packer(use_bin_type=True)
        single = msgpack.Packer(use_bin_type=True, use_single_float=True)
        expected = (
            packer.pack_array_header(3) + single.pack(1.5)
            + packer.pack_map_header(4) + packer.pack("k") + single.pack(0.1) + packer.pack("b") + packer.pack(b"\x01")
            + packer.pack("s") + packer.pack("x") + packer.pack("n") + packer.pack(None)
            + packer.pack_array_header(2) + single.pack(2.0) + packer.pack([True, 7, 2.5])
        )
        self.assertEqual(_pack_tree(tree), expected)
        self.assertEqual(_pack_tree(t.Float32(1.5)), bytes.fromhex("ca3fc00000"))

    def test_a_float32_overflow_is_infinity_and_a_value_nothing_can_pack_is_still_a_type_error(self) -> None:
        self.assertEqual(_pack_tree([t.float32.encode(1e300)]), bytes.fromhex("91ca7f800000"))
        self.assertEqual(_pack_tree([t.float32.encode(-1e300)]), bytes.fromhex("91caff800000"))
        with self.assertRaises(TypeError):
            _pack_tree([t.Float32(1.0), object()])
        with self.assertRaises(TypeError):
            _pack_tree([object()])

    def test_a_tree_with_no_float32_is_packb_unchanged(self) -> None:
        tree = {"a": [1, 2.5, "x", b"y", None, True, {"b": -1}], "c": (3, 4)}
        self.assertEqual(_pack_tree(tree), msgpack.packb(tree, use_bin_type=True))

    def test_the_union_golden_scalar_direction_is_refused_as_the_reference_refuses_it(self) -> None:
        from test_union_parity import node  # the fixture with the C# golden's leaves

        with self.assertRaises(ValueError) as caught:
            node.decode([1, ["hammer", 12]])
        self.assertEqual(str(caught.exception), "Union 'Node' arm 1: Text slot 1 (content): expected a string, found integer")
        self.assertNotIn("hammer", str(caught.exception))
        self.assertEqual(node.decode([1, ["hammer", "x"]]), UnionArm("Text", {"id": "hammer", "content": "x"}))


PY_STORES = VECTORS / "py-store"
# slot -> the C# type name a wrong catalog gives it; the reference refuses each ("slot N changed type").
WRONG_NAMES = {3: "System.Int64", 5: "System.Double", 7: "System.Int32", 14: "System.Double[]"}


def _python_authored_leaf_record() -> dict:
    return {
        "id": "py", "flag": False, "blob": bytearray([1, 255]), "i": -(2**31), "l": 2**63 - 1, "f": 0.1, "d": 1e-300,
        "maybe_i": None, "tags": [], "names": ("\u00e9", "z"), "any": {"k": [1, True, None, 2.5, "s"], "n": -3},
        "nested": None, "items": [{"n": "one", "w": 2**53 + 2**29 + 1, "id": "000000000021"}, {"n": "two", "w": 1e-40, "id": "000000000022"}],
        "shape": UnionArm("LeafCircle", {"r": 0.1}), "floats": [0.1, 3, -0.0, 1e39], "maybe_f": None,
    }


def _write_store(path: Path, document, value: dict) -> None:
    path.unlink(missing_ok=True)
    cache = CultCache.builder().register_document_type(document).add_generic_store(SingleFileMessagePackBackingStore(path)).build()
    cache.pull_all_backing_stores()
    cache.put(document, value["id"], value)


def _wrong_catalog(document, slot: int):
    import dataclasses

    good = tuple(document.catalog_entry().members)
    return dataclasses.replace(
        document, members=lambda: tuple(dataclasses.replace(m, type_name=WRONG_NAMES[slot]) if m.slot == slot else m for m in good)
    )


class PythonWrittenStoreVectorTests(unittest.TestCase):
    """The stores under tests/vectors/numeric-leaves/py-store are written only by this class, with CULTCACHE_WRITE_VECTORS=1;
    GameCult.Caching.Tests (PythonWrittenStoreTests) opens them with the C# reference and asserts every field."""

    def test_the_committed_stores_are_what_the_definition_writes_and_read_back(self) -> None:
        import os
        import tempfile

        document = _leaf_record_document()
        value = _python_authored_leaf_record()
        if os.environ.get("CULTCACHE_WRITE_VECTORS"):
            PY_STORES.mkdir(exist_ok=True)
            _write_store(PY_STORES / "leaf-record.cc", document, value)
            for slot in WRONG_NAMES:
                _write_store(PY_STORES / f"wrong-slot-{slot}.cc", _wrong_catalog(document, slot), value)
        with tempfile.TemporaryDirectory() as tmp:
            fresh = Path(tmp) / "fresh.cc"
            _write_store(fresh, document, value)
            [committed] = SingleFileMessagePackBackingStore(PY_STORES / "leaf-record.cc").pull_all()
            [again] = SingleFileMessagePackBackingStore(fresh).pull_all()
            self.assertEqual(committed.payload, again.payload)
            self.assertEqual(committed.catalog_entry.members, again.catalog_entry.members)
        self.assertEqual(committed.payload, document.encode_payload(value))
        for slot, name in WRONG_NAMES.items():
            [wrong] = SingleFileMessagePackBackingStore(PY_STORES / f"wrong-slot-{slot}.cc").pull_all()
            self.assertEqual(wrong.catalog_entry.members[slot].type_name, name)
            self.assertEqual(wrong.payload, committed.payload)


class StoreRoundTripTests(unittest.TestCase):
    def test_a_float32_slot_survives_a_single_file_store(self) -> None:
        import tempfile

        document = define_database_entry_type(
            "tests.store", [database_entry_field("id", 0, codec=t.string), database_entry_field("f", 1, codec=t.float32)], name="id"
        )
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "store.cc"
            cache = CultCache.builder().register_document_type(document).add_generic_store(SingleFileMessagePackBackingStore(path)).build()
            cache.pull_all_backing_stores()
            cache.put(document, "a", {"id": "a", "f": 0.1})
            [envelope] = SingleFileMessagePackBackingStore(path).pull_all()
            self.assertEqual(envelope.payload, bytes.fromhex("92a161ca3dcccccd"))
            self.assertEqual(envelope.catalog_entry.members[1].type_name, "System.Single")


if __name__ == "__main__":
    unittest.main()
