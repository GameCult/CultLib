from __future__ import annotations

import collections
import contextlib
import inspect
import json
import os
import shutil
import tempfile
import traceback
import unittest
from pathlib import Path
from unittest import mock

import msgpack

from cultcache_py import (
    JSON_PAYLOAD_CODEC,
    MESSAGEPACK_PAYLOAD_CODEC,
    CultCache,
    CultCacheEnvelope,
    CultCachePayloadDepthError,
    CultCachePayloadFormatError,
    DocumentDefinition,
    JsonLinesBackingStore,
    PayloadCodec,
    PayloadCodecFault,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
    convert,
)
from cultcache_py.cache import CultCacheError

CANARY = "CANARY-payload-codec-5b81e0"
LEGACY_JSON = b'{"n": 1, "schema": "x"}'
KEY_KINDS = "str, int, float, bool or timestamp keys"

# The rows of the C# reference's object slot (map P2, jobK): the bytes of one map.
ACCEPTED_KEY_ROWS = {
    "int": "810101",
    "negint": "81ff01",
    "u64max": "81cfffffffffffffffff01",
    "i64min": "81d3800000000000000001",
    "f32": "81ca3fc0000001",
    "f64": "81cb3ff800000000000001",
    "bool": "81c301",
    "tskey": "81d6ff0000000001",
    "str-and-int": "82a1610102 02".replace(" ", ""),
}
REFUSED_KIND_ROWS = {
    "nil": ("81c001", "nil"),
    "bin": ("81c4016b01", "bin"),
    "array": ("81910101", "array"),
    "map": ("8181010101", "map"),
    "ext": ("81d4010001", "ext"),
}
DUPLICATE_ROWS = {
    "dup-int": "82010101 02".replace(" ", ""),
    "dup-str": "82a16101a16102",
    "fix1-u8-1": "8201 01cc0102".replace(" ", ""),
    "int1-f64-1": "820101cb3ff000000000000002",
    "int1-true": "820101c302",
}


def _store_with(path: Path, payload: bytes) -> SingleFileMessagePackBackingStore:
    store = SingleFileMessagePackBackingStore(path)
    store.push(CultCacheEnvelope.create(key="k", type="tests.legacy", payload=payload))
    return store


def _chain_text(error: BaseException) -> str:
    return "".join(traceback.format_exception(error)) + str(error) + repr(error)


class PayloadCodecTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory, True)

    def _cache(self, document, store) -> CultCache:
        return CultCache.builder().register_document_type(document).add_generic_store(store).build()

    def _refused(self, call, *args) -> CultCachePayloadFormatError:
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            call(*args)
        return caught.exception

    def test_the_default_is_messagepack_for_both_definitions(self) -> None:
        document = define_document_type("tests.x")
        self.assertIs(document.payload_codec, MESSAGEPACK_PAYLOAD_CODEC)
        self.assertEqual(document.encode_payload({"a": [1]}), bytes.fromhex("81a1619101"))
        self.assertIs(define_database_entry_type("tests.entry", [("a", 0)]).payload_codec, MESSAGEPACK_PAYLOAD_CODEC)

    def test_the_default_has_one_author(self) -> None:
        self.assertIs(DocumentDefinition("tests.x").payload_codec, MESSAGEPACK_PAYLOAD_CODEC)
        self.assertIs(define_document_type("tests.x").payload_codec, MESSAGEPACK_PAYLOAD_CODEC)
        self.assertIs(define_document_type("tests.x", payload_codec=JSON_PAYLOAD_CODEC).payload_codec, JSON_PAYLOAD_CODEC)
        self.assertIsNone(inspect.signature(define_document_type).parameters["payload_codec"].default)

    def test_the_json_codec_writes_the_0_3_bytes(self) -> None:
        written = JSON_PAYLOAD_CODEC.encode({"b": "é", "a": [1, 2]})
        self.assertEqual(written, b'{"a": [1, 2], "b": "\xc3\xa9"}')
        self.assertEqual(JSON_PAYLOAD_CODEC.decode(written), {"a": [1, 2], "b": "é"})
        self.assertEqual(JSON_PAYLOAD_CODEC.encode({1: "a", 3: 2**70}), b'{"1": "a", "3": 1180591620717411303424}')

    def test_a_json_store_is_refused_typed_and_loads_nothing(self) -> None:
        store = _store_with(self.directory / "legacy.cc", LEGACY_JSON)
        document = define_document_type("tests.legacy")
        cache = self._cache(document, store)
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            cache.pull_all_backing_stores()
        message = str(caught.exception)
        self.assertEqual((caught.exception.type_name, caught.exception.codec, caught.exception.direction), ("tests.legacy", "MessagePack", "read"))
        self.assertEqual(caught.exception.code, "not_one_value")
        self.assertIn("tests.legacy", message)
        self.assertIn("MessagePack", message)
        self.assertNotIn("schema", message)
        self.assertEqual(cache.get_all(document), [])

    def test_the_refusal_texts_are_exact_with_their_codes(self) -> None:
        read = self._refused(define_document_type("tests.text").decode_payload, LEGACY_JSON)
        self.assertEqual(str(read), "tests.text payload is not one MessagePack value (ExtraData); refused on read.")
        self.assertEqual(read.code, "not_one_value")
        entry = self._refused(define_database_entry_type("tests.entry", [("a", 0)]).decode_payload, LEGACY_JSON)
        self.assertEqual(str(entry), "tests.entry payload is not one MessagePack value (ExtraData); refused on read.")
        json_side = self._refused(define_document_type("tests.text", payload_codec=JSON_PAYLOAD_CODEC).decode_payload, b"\x81\xa1a")
        self.assertEqual(str(json_side), "tests.text payload is not one JSON value (UnicodeDecodeError); refused on read.")
        self.assertEqual((json_side.type_name, json_side.codec, json_side.direction), ("tests.text", "JSON", "read"))
        nil = self._refused(define_document_type("tests.text").decode_payload, bytes.fromhex("81c001"))
        self.assertEqual(
            str(nil),
            "tests.text payload has a map key of kind nil; MessagePack payloads take str, int, float, bool or timestamp keys (refused on read).",
        )
        self.assertEqual((nil.code, nil.direction), ("map_key_kind", "read"))
        duplicate = self._refused(define_document_type("tests.text").decode_payload, bytes.fromhex("82010101 02".replace(" ", "")))
        self.assertEqual(str(duplicate), "tests.text payload has two map keys Python holds as one; refused on read.")
        self.assertEqual(duplicate.code, "duplicate_map_key")
        key = self._refused(define_document_type("tests.text", encode=lambda value: value).encode_payload, {b"k": 1})
        self.assertEqual(
            str(key),
            "tests.text payload has a map key of kind bin; MessagePack payloads take str, int, float, bool or timestamp keys (refused on write).",
        )
        self.assertEqual((key.type_name, key.codec, key.direction, key.code), ("tests.text", "MessagePack", "write", "map_key_kind"))
        big = self._refused(define_document_type("tests.text", encode=lambda value: value).encode_payload, {"a": 2**64})
        self.assertEqual(str(big), "tests.text payload has an integer beyond 64 bits; refused on write.")
        self.assertEqual((big.code, big.direction), ("integer_out_of_range", "write"))

    def test_a_consumer_codec_fault_with_its_own_code_is_a_typed_refusal_naming_it(self) -> None:
        def encode(tree: object) -> bytes:
            raise PayloadCodecFault("no_sets", "set")

        document = define_document_type("tests.own", payload_codec=PayloadCodec("Own", encode, lambda payload: None))
        refusal = self._refused(document.encode_payload, {"a": 1})
        self.assertEqual(str(refusal), "tests.own payload is refused by the Own codec (no_sets); refused on write.")
        self.assertEqual((refusal.codec, refusal.code, refusal.direction), ("Own", "no_sets", "write"))
        fault = PayloadCodecFault("no_sets", "set")
        self.assertEqual((fault.code, fault.kind), ("no_sets", "set"))
        self.assertIsInstance(fault, ValueError)

    def test_a_json_store_opted_in_reads_and_convert_writes_it_anew(self) -> None:
        path = self.directory / "legacy.cc"
        store = _store_with(path, LEGACY_JSON)
        legacy = define_document_type("tests.legacy", payload_codec=JSON_PAYLOAD_CODEC)
        old = self._cache(legacy, store)
        old.pull_all_backing_stores()
        self.assertEqual(old.get(legacy, "k"), {"n": 1, "schema": "x"})
        modern = define_document_type("tests.legacy")
        destination = self.directory / "converted.cc"
        original = path.read_bytes()
        self.assertEqual(convert(store, destination, [modern]), 1)
        self.assertEqual(path.read_bytes(), original)
        reread = self._cache(modern, SingleFileMessagePackBackingStore(destination))
        reread.pull_all_backing_stores()
        self.assertEqual(reread.get(modern, "k"), {"n": 1, "schema": "x"})
        self.assertEqual(reread.get_envelope(modern, "k").payload, MESSAGEPACK_PAYLOAD_CODEC.encode({"n": 1, "schema": "x"}))

    def test_wrong_format_payloads_are_typed_refusals_on_both_sides(self) -> None:
        document = define_document_type("tests.faults")
        for payload in (b"[1,2]", b'"hi"', b"true", b"null", b"1.5", b"-3", b"12", b"\x93\x01\x02", b"\xc1", b"\xa2\xff\xfe"):
            with self.subTest(payload=payload):
                with self.assertRaises(CultCachePayloadFormatError):
                    document.decode_payload(payload)
        json_document = define_document_type("tests.faults", payload_codec=JSON_PAYLOAD_CODEC)
        caught = self._refused(json_document.decode_payload, MESSAGEPACK_PAYLOAD_CODEC.encode({"a": 1}))
        self.assertNotIn("JSON_PAYLOAD_CODEC", str(caught))
        self.assertIn("JSON", str(caught))

    def test_a_one_digit_json_text_reads_as_a_messagepack_integer(self) -> None:
        self.assertEqual(define_document_type("tests.digit").decode_payload(b"5"), 53)

    def test_depth_faults_belong_to_the_codec(self) -> None:
        def deep(payload: bytes) -> object:
            raise RecursionError

        declaring = define_document_type("tests.d", payload_codec=PayloadCodec("t", lambda tree: b"", deep, depth_faults=(RecursionError,)))
        with self.assertRaises(CultCachePayloadDepthError):
            declaring.decode_payload(b"x")
        silent = define_document_type("tests.d", payload_codec=PayloadCodec("t", lambda tree: b"", deep))
        with self.assertRaises(RecursionError) as caught:
            silent.decode_payload(b"x")
        self.assertNotIsInstance(caught.exception, CultCachePayloadDepthError)

    def test_a_codec_decode_fault_that_is_a_value_error_is_a_format_error_naming_its_class(self) -> None:
        def refuse(payload: bytes) -> object:
            raise KeyError(CANARY)

        document = define_document_type("tests.v", payload_codec=PayloadCodec("t", lambda tree: b"", lambda payload: int("x")))
        self.assertIn("(ValueError)", str(self._refused(document.decode_payload, b"x")))
        passthrough = define_document_type("tests.v", payload_codec=PayloadCodec("t", lambda tree: b"", refuse))
        with self.assertRaises(KeyError):
            passthrough.decode_payload(b"x")

    def test_a_refused_key_reaches_no_store(self) -> None:
        document = define_document_type("tests.keys", encode=lambda value: value)
        store = SingleFileMessagePackBackingStore(self.directory / "keys.cc")
        cache = self._cache(document, store)
        with self.assertRaises(CultCachePayloadFormatError):
            cache.put(document, "k", {b"k": "a"})
        self.assertEqual(store.pull_all(), [])

    def test_the_library_codecs_refuse_nodes_the_depth_walk_cannot_see(self) -> None:
        class OnlyIter:
            def __iter__(self):
                return iter([1])

        nodes = (OnlyIter(), collections.deque([1]), collections.UserDict(a=1), frozenset([1]))
        for codec in (MESSAGEPACK_PAYLOAD_CODEC, JSON_PAYLOAD_CODEC):
            document = define_document_type("tests.unseen", encode=lambda value: value, payload_codec=codec)
            for node in nodes:
                with self.subTest(codec=codec.name, node=type(node).__name__):
                    with self.assertRaises(TypeError):
                        document.encode_payload({"a": node})

    # The key rule is the C# reference's (map P2), both ways.

    def test_reference_key_rule_reads_through_an_any_slot(self) -> None:
        entry = define_database_entry_type("tests.any", [("v", 0)])
        for name, row in ACCEPTED_KEY_ROWS.items():
            with self.subTest(row=name):
                value = entry.decode_payload(b"\x91" + bytes.fromhex(row))["v"]
                again = entry.decode_payload(entry.encode_payload({"v": value}))["v"]
                self.assertEqual(again, value)
                self.assertEqual([type(key) for key in again], [type(key) for key in value])
        sixty_four = entry.decode_payload(b"\x91" + bytes.fromhex(ACCEPTED_KEY_ROWS["u64max"]))["v"]
        self.assertEqual(list(sixty_four), [2**64 - 1])
        self.assertEqual(list(entry.decode_payload(b"\x91" + bytes.fromhex(ACCEPTED_KEY_ROWS["i64min"]))["v"]), [-(2**63)])
        self.assertEqual(list(entry.decode_payload(b"\x91" + bytes.fromhex(ACCEPTED_KEY_ROWS["bool"]))["v"]), [True])
        self.assertEqual(list(entry.decode_payload(b"\x91" + bytes.fromhex(ACCEPTED_KEY_ROWS["f64"]))["v"]), [1.5])
        self.assertEqual(list(entry.decode_payload(b"\x91" + bytes.fromhex(ACCEPTED_KEY_ROWS["tskey"]))["v"]), [msgpack.Timestamp(0, 0)])
        mixed = entry.decode_payload(b"\x91" + bytes.fromhex(ACCEPTED_KEY_ROWS["str-and-int"]))["v"]
        self.assertEqual(mixed, {"a": 1, 2: 2})
        for name, (row, kind) in REFUSED_KIND_ROWS.items():
            with self.subTest(row=name):
                refusal = self._refused(entry.decode_payload, b"\x91" + bytes.fromhex(row))
                self.assertEqual((refusal.code, refusal.direction), ("map_key_kind", "read"))
                self.assertIn(f"map key of kind {kind};", str(refusal))
        for name, row in DUPLICATE_ROWS.items():
            with self.subTest(row=name):
                refusal = self._refused(entry.decode_payload, b"\x91" + bytes.fromhex(row))
                self.assertEqual(refusal.code, "duplicate_map_key")

    def test_reference_key_rule_writes_and_reads_back_through_a_document_type(self) -> None:
        document = define_document_type("tests.keys", encode=lambda value: value)
        for tree in ({1: "a"}, {True: 1}, {1.5: 1}, {msgpack.Timestamp(0, 0): 1}, {"a": {2: 3}}, {"a": [{1.5: 1}]}, {-(2**63): 1}, {2**64 - 1: 1}):
            with self.subTest(tree=tree):
                self.assertEqual(document.decode_payload(document.encode_payload(tree)), tree)
        refused = (
            ({b"k": 1}, "bin"),
            ({None: 1}, "nil"),
            ({(1,): 1}, "array"),
            ({frozenset(): 1}, "array"),
            ({msgpack.ExtType(1, b"x"): 1}, "ext"),
            ({object(): 1}, "unsupported"),
            ({"a": [{b"k": 1}]}, "bin"),
        )
        for tree, kind in refused:
            with self.subTest(tree=tree):
                refusal = self._refused(document.encode_payload, tree)
                self.assertEqual((refusal.code, refusal.direction), ("map_key_kind", "write"))
                self.assertIn(f"map key of kind {kind};", str(refusal))
        legacy = define_document_type("tests.keys", encode=lambda value: value, payload_codec=JSON_PAYLOAD_CODEC)
        self.assertEqual(legacy.encode_payload({1: "a"}), b'{"1": "a"}')

    def test_a_float32_key_writes_as_a_float_the_reference_reads(self) -> None:
        from cultcache_py.leaves import Float32

        written = MESSAGEPACK_PAYLOAD_CODEC.encode({Float32(1.5): 1})
        self.assertEqual(written, bytes.fromhex("81ca3fc0000001"))
        self.assertEqual(MESSAGEPACK_PAYLOAD_CODEC.decode(written), {1.5: 1})

    def test_the_codec_owns_its_keys_both_ways(self) -> None:
        codec = MESSAGEPACK_PAYLOAD_CODEC
        for tree, code, kind in (
            ({b"k": 1}, "map_key_kind", "bin"),
            ({None: 1}, "map_key_kind", "nil"),
            ({"a": 2**64}, "integer_out_of_range", ""),
        ):
            with self.subTest(tree=tree):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.encode(tree)
                self.assertEqual((caught.exception.code, caught.exception.kind), (code, kind))
        for name, (row, kind) in REFUSED_KIND_ROWS.items():
            with self.subTest(row=name):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.decode(bytes.fromhex(row))
                self.assertEqual((caught.exception.code, caught.exception.kind), ("map_key_kind", kind))
        for name, row in DUPLICATE_ROWS.items():
            with self.subTest(row=name):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.decode(bytes.fromhex(row))
                self.assertEqual((caught.exception.code, caught.exception.kind), ("duplicate_map_key", ""))
        for tree in ({1: "a"}, {True: 1}, {1.5: [2**64 - 1, -(2**63)]}, {"a": {-1: None}}, [{2: 3}]):
            with self.subTest(tree=tree):
                self.assertEqual(codec.decode(codec.encode(tree)), tree)

    def test_the_codec_carries_no_extension_type_but_the_timestamp(self) -> None:
        codec = MESSAGEPACK_PAYLOAD_CODEC
        ext = msgpack.ExtType(1, b"x")
        for name, tree in (("root", ext), ("map-value", {"a": ext}), ("nested-item", [[ext]])):
            with self.subTest(encode=name):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.encode(tree)
                self.assertEqual((caught.exception.code, caught.exception.kind), ("value_kind", "ext"))
        for name, row in (("root", "d40100"), ("map-value", "81a161d40100"), ("nested-item", "91d40100")):
            with self.subTest(decode=name):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.decode(bytes.fromhex(row))
                self.assertEqual((caught.exception.code, caught.exception.kind), ("value_kind", "ext"))
        document = define_document_type("tests.ext", encode=lambda value: value)
        text = "tests.ext payload holds a value of kind ext; MessagePack payloads carry no extension type but the timestamp (refused on {})."
        self.assertEqual(str(self._refused(document.encode_payload, ext)), text.format("write"))
        refusal = self._refused(document.decode_payload, bytes.fromhex("91d40100"))
        self.assertEqual((refusal.code, str(refusal)), ("value_kind", text.format("read")))

    def test_mixed_width_nan_keys_are_a_documented_refusal(self) -> None:
        # The bytes the C# reference writes for a map holding float.NaN and double.NaN keys: it reads them as two keys.
        row = "82 ca ffc00000 01 cb fff8000000000000 02"
        with self.assertRaises(PayloadCodecFault) as caught:
            MESSAGEPACK_PAYLOAD_CODEC.decode(bytes.fromhex(row))
        self.assertEqual((caught.exception.code, caught.exception.kind), ("duplicate_map_key", ""))
        document = define_document_type("tests.nan", encode=lambda value: value)
        self.assertEqual(self._refused(document.decode_payload, bytes.fromhex(row)).code, "duplicate_map_key")
        readme = (Path(__file__).resolve().parents[1] / "README.md").read_text(encoding="utf-8")
        section = readme.split("## Wire Parity", 1)[1].split("\n## ", 1)[0]
        self.assertIn("duplicate_map_key", section)
        self.assertIn(row, section)

    def test_every_nan_key_is_one_key_both_ways(self) -> None:
        from cultcache_py.leaves import Float32

        codec = MESSAGEPACK_PAYLOAD_CODEC
        collisions = (
            {float("nan"): 1, float("nan"): 2},
            {Float32(float("nan")): 1, Float32(float("nan")): 2},
            {float("nan"): 1, Float32(float("nan")): 2},
        )
        for tree in collisions:
            for wrapped in (tree, {"n": [tree]}):
                with self.subTest(tree=wrapped):
                    with self.assertRaises(PayloadCodecFault) as caught:
                        codec.encode(wrapped)
                    self.assertEqual((caught.exception.code, caught.exception.kind), ("duplicate_map_key", ""))
        for name, row in (
            ("f64-pair", "82cb7ff800000000000001cb7ff800000000000002"),
            ("f32-pair", "82ca7fc0000001ca7fc0000002"),
            ("mixed-pair", "82cb7ff800000000000001ca7fc0000002"),
        ):
            with self.subTest(row=name):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.decode(bytes.fromhex(row))
                self.assertEqual((caught.exception.code, caught.exception.kind), ("duplicate_map_key", ""))
        for single in (float("nan"), Float32(float("nan"))):
            with self.subTest(single=type(single).__name__):
                (key,) = codec.decode(codec.encode({single: 1}))
                self.assertNotEqual(key, key)

    def test_a_float32_key_beside_a_key_the_reader_holds_equal_is_refused_on_write(self) -> None:
        from cultcache_py.leaves import Float32

        codec = MESSAGEPACK_PAYLOAD_CODEC
        collisions = (
            {1: "a", Float32(1.0): "b"},
            {True: "a", Float32(1.0): "b"},
            {1.5: "a", Float32(1.5): "b"},
            {0: "a", Float32(-0.0): "b"},
        )
        for tree in collisions:
            for wrapped in (tree, {"n": [tree]}):
                with self.subTest(tree=wrapped):
                    with self.assertRaises(PayloadCodecFault) as caught:
                        codec.encode(wrapped)
                    self.assertEqual((caught.exception.code, caught.exception.kind), ("duplicate_map_key", ""))
        document = define_document_type("tests.f32", encode=lambda value: value)
        store = SingleFileMessagePackBackingStore(self.directory / "f32.cc")
        cache = self._cache(document, store)
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            cache.put(document, "k", collisions[0])
        refusal = caught.exception
        self.assertEqual((refusal.code, refusal.direction), ("duplicate_map_key", "write"))
        self.assertIn("refused on write", str(refusal))
        self.assertFalse(store.path.exists())
        for tree in (
            {0.1: "a", Float32(0.1): "b"},
            {1: "a", Float32(1.5): "b"},
            {"a": 1, Float32(1.0): 2},
        ):
            with self.subTest(accepted=tree):
                self.assertEqual(len(codec.decode(codec.encode(tree))), 2)

    def test_the_codec_write_bound_is_the_payload_bound(self) -> None:
        from cultcache_py.leaves import Float32

        def lists(depth: int, leaf: object = 1) -> object:
            tree = leaf
            for _ in range(depth):
                tree = [tree]
            return tree

        def maps(depth: int, leaf: object = 1) -> object:
            tree = leaf
            for _ in range(depth):
                tree = {"a": tree}
            return tree

        codec = MESSAGEPACK_PAYLOAD_CODEC
        for build in (lists, maps):
            for leaf in (1, Float32(1.5)):
                with self.subTest(build=build.__name__, leaf=leaf):
                    self.assertIsNotNone(codec.decode(codec.encode(build(256, leaf))))
                    with self.assertRaises(PayloadCodecFault) as caught:
                        codec.encode(build(257, leaf))
                    self.assertEqual((caught.exception.code, caught.exception.kind), ("depth_limit", ""))
        document = define_document_type("tests.depth", encode=lambda value: value)
        with self.assertRaises(CultCachePayloadDepthError):
            document.encode_payload(lists(257))
        with self.assertRaises(CultCachePayloadDepthError):
            document.decode_payload(b"\x91" * 257 + b"\x01")
        self.assertEqual(document.decode_payload(b"\x91" * 256 + b"\x01"), lists(256))

    def test_the_codec_read_bound_is_the_payload_bound(self) -> None:
        codec = MESSAGEPACK_PAYLOAD_CODEC
        document = define_document_type("tests.depth", encode=lambda value: value)
        for name, unit, leaf in (("lists", b"\x91", b"\x01"), ("maps", b"\x81\xa1a", b"\x01")):
            with self.subTest(shape=name):
                self.assertIsNotNone(codec.decode(unit * 256 + leaf))
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.decode(unit * 257 + leaf)
                self.assertEqual((caught.exception.code, caught.exception.kind), ("depth_limit", ""))
                with self.assertRaises(CultCachePayloadDepthError):
                    document.decode_payload(unit * 257 + leaf)

    def test_timestamps_lie_within_datetime_both_ways(self) -> None:
        codec = MESSAGEPACK_PAYLOAD_CODEC
        stamp = msgpack.Timestamp
        for ts in (stamp(-62135596800, 0), stamp(253402300799, 999999999)):
            for tree in (ts, {"a": ts}, {ts: 1}):
                with self.subTest(inside=tree):
                    self.assertEqual(codec.decode(codec.encode(tree)), tree)
        document = define_document_type("tests.ts", encode=lambda value: value)
        text = "tests.ts payload holds a timestamp outside years 1 to 9999; refused on {}."
        for ts in (stamp(-62135596801, 999999999), stamp(253402300800, 0)):
            for tree in (ts, {"a": ts}, {ts: 1}):
                with self.subTest(outside=tree):
                    with self.assertRaises(PayloadCodecFault) as caught:
                        codec.encode(tree)
                    self.assertEqual((caught.exception.code, caught.exception.kind), ("timestamp_out_of_range", ""))
                    packed = msgpack.packb(tree, use_bin_type=True)
                    with self.assertRaises(PayloadCodecFault) as caught:
                        codec.decode(packed)
                    self.assertEqual((caught.exception.code, caught.exception.kind), ("timestamp_out_of_range", ""))
                    self.assertEqual(str(self._refused(document.encode_payload, tree)), text.format("write"))
                    self.assertEqual(str(self._refused(document.decode_payload, packed)), text.format("read"))

    def test_the_integer_range_is_closed_at_both_ends_at_the_root_too(self) -> None:
        codec = MESSAGEPACK_PAYLOAD_CODEC
        for value in (2**64 - 1, -(2**63), 0):
            with self.subTest(accepted=value):
                self.assertEqual(codec.decode(codec.encode(value)), value)
        for value in (2**64, -(2**63) - 1):
            with self.subTest(refused=value):
                with self.assertRaises(PayloadCodecFault) as caught:
                    codec.encode(value)
                self.assertEqual(caught.exception.code, "integer_out_of_range")

    def test_a_consumer_codec_that_refuses_a_nesting_gets_the_depth_refusal_naming_the_direction(self) -> None:
        def encode(tree: object) -> bytes:
            raise PayloadCodecFault("depth_limit")

        def decode(payload: bytes) -> object:
            raise PayloadCodecFault("depth_limit")

        document = define_document_type("tests.own_depth", encode=lambda value: value, payload_codec=PayloadCodec("Own", encode, decode))
        for direction, call, argument in (("write", document.encode_payload, {"a": 1}), ("read", document.decode_payload, b"x")):
            with self.subTest(direction=direction):
                with self.assertRaises(CultCachePayloadDepthError) as caught:
                    call(argument)
                self.assertEqual(
                    str(caught.exception),
                    f"tests.own_depth payload nests 257 containers deep; the limit is 256 (refused on {direction}).",
                )

    def test_a_codec_fault_prints_its_code_only(self) -> None:
        self.assertEqual(str(PayloadCodecFault("some_code", "some_kind")), "some_code")
        self.assertEqual(str(PayloadCodecFault("some_code")), "some_code")
        self.assertEqual(PayloadCodecFault("some_code").kind, "")

    def test_define_document_type_forwards_every_schema_field(self) -> None:
        document = define_document_type(
            "tests.schema",
            schema_id="sid",
            schema_name="sname",
            schema_version="sname.v9",
            content_hash="hash",
            canonical_schema_json='{"canonical":true}',
            compatible_schema_ids=["sid", "older"],
        )
        self.assertEqual(
            (document.schema_id, document.schema_name, document.schema_version, document.content_hash),
            ("sid", "sname", "sname.v9", "hash"),
        )
        self.assertEqual((document.canonical_schema_json, document.compatible_schema_ids), ('{"canonical":true}', ("sid", "older")))
        bare = define_document_type("tests.schema")
        self.assertEqual((bare.schema_id, bare.compatible_schema_ids), (None, ()))

    def test_a_cyclic_tree_is_not_walked_forever_by_the_codec(self) -> None:
        cycle: dict = {}
        cycle["a"] = cycle
        with self.assertRaises(PayloadCodecFault) as caught:
            MESSAGEPACK_PAYLOAD_CODEC.encode(cycle)
        self.assertEqual(caught.exception.code, "depth_limit")
        with self.assertRaises(CultCachePayloadDepthError):
            define_document_type("tests.cycle", encode=lambda value: value).encode_payload(cycle)

    # Integers beyond 64 bits are a format limit with a typed refusal.

    def test_integers_beyond_64_bits_are_refused_typed_on_write(self) -> None:
        document = define_document_type("tests.big", encode=lambda value: value)
        store = SingleFileMessagePackBackingStore(self.directory / "big.cc")
        cache = self._cache(document, store)
        for value in (2**64, -(2**63) - 1, 2**64 + 987654):
            for name, tree in (("value", {"a": value}), ("list", {"a": [[value]]}), ("key", {value: 1}), ("root", value)):
                with self.subTest(value=value, place=name):
                    refusal = self._refused(document.encode_payload, tree)
                    self.assertEqual((refusal.code, refusal.direction), ("integer_out_of_range", "write"))
                    text = _chain_text(refusal)
                    self.assertNotIn(str(value), text)
                    self.assertNotIn(str(value)[:10], text)
            with self.assertRaises(CultCachePayloadFormatError):
                cache.put(document, "k", {"a": value})
        self.assertEqual(store.pull_all(), [])
        for value in (2**64 - 1, -(2**63)):
            for tree in ({"a": value}, {"a": [value]}, {value: 1}):
                with self.subTest(value=value, tree=tree):
                    self.assertEqual(document.decode_payload(document.encode_payload(tree)), tree)

    # A refusal carries no payload byte or value, on its text or on its exception chain.

    def test_refusals_carry_no_payload_on_their_text_or_exception_chain(self) -> None:
        raw = CANARY.encode()
        text_key = bytes([0xA0 + len(raw)]) + raw
        read_rows = {
            "extra-data": (define_document_type("tests.c"), b"\x01" + raw),
            "utf8": (define_document_type("tests.c", payload_codec=JSON_PAYLOAD_CODEC), b"\xff" + raw),
            "json-text": (define_document_type("tests.c", payload_codec=JSON_PAYLOAD_CODEC), b'{"' + raw + b'": '),
            "map-key-kind": (define_document_type("tests.c"), b"\x81\xc4" + bytes([len(raw)]) + raw + b"\x01"),
            "duplicate-key": (define_document_type("tests.c"), b"\x82" + text_key + b"\x01" + text_key + b"\x02"),
            "depth": (define_document_type("tests.c"), b"\x91" * 300 + text_key),
            "not-msgpack": (define_document_type("tests.c"), b"\xc1" + raw),
        }
        for name, (document, payload) in read_rows.items():
            with self.subTest(row=name):
                with self.assertRaises(ValueError) as caught:
                    document.decode_payload(payload)
                self.assertIsNone(caught.exception.__context__)
                self.assertIsNone(caught.exception.__cause__)
                self.assertNotIn(CANARY, _chain_text(caught.exception))
        write = define_document_type("tests.c", encode=lambda value: value)
        for name, tree in (
            ("map-key-kind", {raw: 1}),
            ("integer", {"k": 2**64 + 987654, CANARY: 1}),
            ("nested", {"a": [{raw: CANARY}]}),
        ):
            with self.subTest(row=name):
                with self.assertRaises(CultCachePayloadFormatError) as caught:
                    write.encode_payload(tree)
                self.assertIsNone(caught.exception.__context__)
                self.assertIsNone(caught.exception.__cause__)
                self.assertNotIn(CANARY, _chain_text(caught.exception))
        with self.assertRaises(CultCachePayloadDepthError) as deep:
            define_document_type("tests.c", encode=lambda value: value).encode_payload(self._nested(300, CANARY))
        self.assertIsNone(deep.exception.__context__)
        self.assertNotIn(CANARY, _chain_text(deep.exception))

    @staticmethod
    def _nested(depth: int, leaf: object) -> object:
        tree = leaf
        for _ in range(depth):
            tree = [tree]
        return tree

    # The legacy envelope reader extracts bytes; the registered codec still decides.

    def test_a_legacy_inline_payload_is_messagepack_and_a_json_definition_refuses_it(self) -> None:
        path = self.directory / "inline.cc"
        path.write_bytes(
            msgpack.packb([{"key": "k", "type": "tests.legacy", "payload": {"n": 1}, "storedAt": "2026-01-01T00:00:00Z"}], use_bin_type=True)
        )
        document = define_document_type("tests.legacy")
        cache = self._cache(document, SingleFileMessagePackBackingStore(path))
        cache.pull_all_backing_stores()
        self.assertEqual(cache.get(document, "k"), {"n": 1})
        json_document = define_document_type("tests.legacy", payload_codec=JSON_PAYLOAD_CODEC)
        refused = self._cache(json_document, SingleFileMessagePackBackingStore(path))
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            refused.pull_all_backing_stores()
        self.assertEqual(caught.exception.code, "not_one_value")

    def test_a_legacy_inline_payload_key_is_judged_by_the_codec_alone(self) -> None:
        def store_of(payload: object, name: str) -> SingleFileMessagePackBackingStore:
            path = self.directory / f"inline-{name}.cc"
            path.write_bytes(
                msgpack.packb(
                    [{"key": "k", "type": "tests.legacy", "payload": payload, "storedAt": "2026-01-01T00:00:00Z"}],
                    use_bin_type=True,
                )
            )
            return SingleFileMessagePackBackingStore(path)

        document = define_document_type("tests.legacy")
        cache = self._cache(document, store_of({1: "a"}, "int"))
        cache.pull_all_backing_stores()
        self.assertEqual(cache.get(document, "k"), {1: "a"})
        with self.assertRaises(PayloadCodecFault) as caught:
            store_of({None: "a"}, "nil").pull_all()
        self.assertEqual((caught.exception.code, caught.exception.kind), ("map_key_kind", "nil"))


MIGRATION_RECORDS = {
    "k1": b'{"n": 1}',
    "k2": b'["a", 2]',
    "k3": b'{"s": "\xc3\xa9"}',
    "k4": b'{"a": [1, 2], "b": {"c": true}}',
    "k5": b'{"t": null, "f": 1.5}',
    "k6": b'{"x": {"y": [[0, 1], []]}}',
    "k7": b'{"big": 1180591620717411303424}',
}
BIG_DIGITS = "1180591620717411303424"
SOURCE_MISSING = "convert: argument source does not exist (source_missing); there is nothing to convert."
DESTINATION_EXISTS = (
    "convert: argument destination already exists (destination_exists); convert writes only a new store, "
    "and the source is unchanged."
)


class ConvertTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory, True)

    def _stores(self):
        yield "single-file", lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc")
        yield "jsonl", lambda name: JsonLinesBackingStore(self.directory / f"{name}.jsonl")

    def _filled(self, make, name: str, records: dict[str, bytes]):
        store = make(name)
        for key, payload in records.items():
            store.push(CultCacheEnvelope.create(key=key, type="tests.mig", payload=payload))
        return store

    def _pull_with(self, store, codec):
        document = define_document_type("tests.mig", payload_codec=codec, encode=lambda value: value)
        cache = CultCache.builder().register_document_type(document).add_generic_store(store).build()
        cache.pull_all_backing_stores()
        return cache.get_all(document)

    def test_convert_writes_a_new_store_of_the_same_kind(self) -> None:
        records = {"k1": b"5", "k2": b"53", "k3": b'{"n": 1}'}
        definition = define_document_type("tests.mig", encode=lambda value: value)
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"src-{label}", records)
                other = CultCacheEnvelope.create(key="o", type="tests.other", payload=b"opaque\x00bytes")
                store.push(other)
                destination = self.directory / f"dst-{label}.out"
                self.assertEqual(convert(store, destination, [definition]), 3)
                opened = type(store)(destination)
                kept = next(item for item in opened.pull_all() if item.type == "tests.other")
                self.assertEqual((kept.payload, kept.stored_at), (other.payload, other.stored_at))
                self.assertEqual(
                    {item.key: definition.decode_payload(item.payload) for item in opened.pull_all() if item.type == "tests.mig"},
                    {"k1": 5, "k2": 53, "k3": {"n": 1}},
                )
                plain = self._filled(make, f"plain-{label}", records)
                plain_destination = self.directory / f"plain-{label}.out"
                convert(plain, plain_destination, [definition])
                self.assertEqual(
                    sorted(self._pull_with(type(store)(plain_destination), MESSAGEPACK_PAYLOAD_CODEC), key=str), [5, 53, {"n": 1}]
                )
                raw = destination.read_bytes()
                if label == "jsonl":
                    self.assertEqual(raw[:1], b"{")
                else:
                    self.assertIsInstance(msgpack.unpackb(raw, raw=False, strict_map_key=False), list)

    def test_a_source_with_no_listed_record_still_writes_the_destination_with_count_zero(self) -> None:
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"zero-{label}", {"k1": b'{"n": 1}'})
                destination = self.directory / f"zero-{label}.out"
                self.assertEqual(convert(store, destination, [define_document_type("tests.nothing")]), 0)
                self.assertTrue(destination.exists())
                self.assertEqual(
                    [(item.key, item.payload) for item in type(store)(destination).pull_all()], [("k1", b'{"n": 1}')]
                )

    def test_convert_never_writes_its_source(self) -> None:
        for label, make in self._stores():
            for case, records in (("ok", {"k1": b'{"n": 1}'}), ("refused", {"k1": b'{"n": 1}', "k7": MIGRATION_RECORDS["k7"]})):
                with self.subTest(store=label, case=case):
                    store = self._filled(make, f"ro-{label}-{case}", records)
                    before = (store.path.read_bytes(), store.path.stat().st_mtime_ns)
                    destination = self.directory / f"ro-{label}-{case}.out"
                    with contextlib.ExitStack() as stack:
                        for name in ("push", "push_all", "delete", "_replace_all"):
                            stack.enter_context(mock.patch.object(type(store), name, side_effect=AssertionError(name)))
                        try:
                            convert(store, destination, [define_document_type("tests.mig")])
                        except CultCacheError:
                            self.assertEqual(case, "refused")
                        else:
                            self.assertEqual(case, "ok")
                    self.assertEqual((store.path.read_bytes(), store.path.stat().st_mtime_ns), before)

    def test_a_writer_after_the_read_keeps_its_record_and_convert_returns_normally(self) -> None:
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"late-{label}", {"k1": b'{"n": 1}'})
                writer = type(store)(store.path)
                real = type(store).pull_all
                interleaved: list[bool] = []

                def pull_all(self_store):
                    result = real(self_store)
                    if not interleaved:
                        interleaved.append(True)
                        writer.push(CultCacheEnvelope.create(key="w", type="tests.mig", payload=b'{"late": true}'))
                    return result

                destination = self.directory / f"late-{label}.out"
                with mock.patch.object(type(store), "pull_all", pull_all):
                    count = convert(store, destination, [define_document_type("tests.mig")])
                self.assertEqual(count, 1)
                self.assertEqual({item.key for item in store.pull_all()}, {"k1", "w"})
                self.assertEqual({item.key for item in type(store)(destination).pull_all()}, {"k1"})

    def test_an_existing_destination_is_refused_before_the_source_is_read(self) -> None:
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"taken-{label}", {"k1": b'{"n": 1}'})
                definitions = [define_document_type("tests.mig")]
                earlier = self.directory / f"earlier-{label}.out"
                convert(store, earlier, definitions)
                empty = self.directory / f"empty-{label}.out"
                empty.write_bytes(b"")
                for destination in (store.path, earlier, empty):
                    before = destination.read_bytes()
                    source_before = store.path.read_bytes()
                    with mock.patch.object(type(store), "pull_all") as pull_all:
                        with self.assertRaises(CultCacheError) as caught:
                            convert(store, destination, definitions)
                        with self.assertRaises(CultCacheError) as again:
                            convert(store, destination, definitions)
                    self.assertEqual(str(caught.exception), DESTINATION_EXISTS)
                    self.assertEqual(str(again.exception), str(caught.exception))
                    self.assertIsNone(caught.exception.__context__)
                    self.assertEqual(pull_all.call_count, 0)
                    self.assertEqual((destination.read_bytes(), store.path.read_bytes()), (before, source_before))

    def test_a_refusal_leaves_no_destination_and_names_only_the_class(self) -> None:
        for refusal_class in (AssertionError, TypeError, KeyError):
            with self.subTest(refusal=refusal_class.__name__):

                def validate(value: object) -> None:
                    raise refusal_class(CANARY)

                store = self._filled(
                    lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc"),
                    f"validated-{refusal_class.__name__}",
                    {"k1": b'{"n": 1}'},
                )
                destination = self.directory / f"validated-{refusal_class.__name__}.out"
                with self.assertRaises(CultCacheError) as caught:
                    convert(store, destination, [define_document_type("tests.mig", validate=validate)])
                self.assertEqual(
                    str(caught.exception),
                    f"Cannot convert tests.mig record k1: {refusal_class.__name__}; no store was written and the source is unchanged.",
                )
                self.assertNotIn(CANARY, _chain_text(caught.exception))
                self.assertIsNone(caught.exception.__context__)
                self.assertIsNone(caught.exception.__cause__)
                self.assertFalse(destination.exists())

    def test_an_integer_beyond_64_bits_is_refused_by_code_and_the_fixed_record_converts(self) -> None:
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"big-{label}", MIGRATION_RECORDS)
                destination = self.directory / f"big-{label}.out"
                definition = define_document_type("tests.mig")
                with self.assertRaises(CultCacheError) as caught:
                    convert(store, destination, [definition])
                for part in ("tests.mig", "k7", "integer_out_of_range"):
                    self.assertIn(part, str(caught.exception))
                self.assertNotIn(BIG_DIGITS[:8], _chain_text(caught.exception))
                self.assertIsNone(caught.exception.__context__)
                self.assertFalse(destination.exists())
                store.push(CultCacheEnvelope.create(key="k7", type="tests.mig", payload=b'{"big": 1}'))
                self.assertEqual(convert(store, destination, [definition]), 7)
                self.assertEqual(len(self._pull_with(type(store)(destination), MESSAGEPACK_PAYLOAD_CODEC)), 7)

    def test_the_destination_is_fsynced_once_and_a_failed_sync_leaves_none(self) -> None:
        store = self._filled(
            lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc"), "sync", {"k1": b'{"n": 1}'}
        )
        definitions = [define_document_type("tests.mig")]
        destination = self.directory / "sync.out"
        seen: list[int] = []
        real = os.fsync

        def fsync(descriptor: int) -> None:
            seen.append(os.fstat(descriptor).st_size)
            real(descriptor)

        with mock.patch("os.fsync", fsync):
            convert(store, destination, definitions)
        self.assertEqual(seen, [destination.stat().st_size])
        self.assertGreater(seen[0], 0)
        failing = self.directory / "sync-failed.out"
        with mock.patch("os.fsync", side_effect=OSError("disk")):
            with self.assertRaises(OSError):
                convert(store, failing, definitions)
        self.assertFalse(failing.exists())

    def test_the_json_lines_bytes_are_key_sorted_utf8_lines_ending_in_lf(self) -> None:
        later = CultCacheEnvelope(key="é", type="b.t", payload=b"x", stored_at="T1")
        earlier = CultCacheEnvelope(key="a", type="a.t", payload=b"yy", stored_at="T2")
        expected = (
            '{"key": "a", "payload": "eXk=", "stored_at": "T2", "type": "a.t"}\n'
            '{"key": "é", "payload": "eA==", "stored_at": "T1", "type": "b.t"}\n'
        ).encode("utf-8")
        self.assertEqual(JsonLinesBackingStore._file_bytes([later, earlier]), expected)
        self.assertEqual(JsonLinesBackingStore._file_bytes([]), b"")

    @unittest.skipIf(os.name != "posix", "an open file cannot be removed by someone else on Windows")
    def test_a_destination_removed_by_someone_else_does_not_mask_the_refusal(self) -> None:
        store = self._filled(
            lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc"), "vanished", {"k1": b'{"n": 1}'}
        )
        destination = self.directory / "vanished.out"
        real = SingleFileMessagePackBackingStore.pull_all

        def pull_all(self_store):
            result = real(self_store)
            destination.unlink()
            return result

        def validate(value: object) -> None:
            raise AssertionError(CANARY)

        with mock.patch.object(SingleFileMessagePackBackingStore, "pull_all", pull_all):
            with self.assertRaises(CultCacheError) as caught:
                convert(store, destination, [define_document_type("tests.mig", validate=validate)])
        self.assertIn("AssertionError", str(caught.exception))
        self.assertIsNone(caught.exception.__context__)

    def test_convert_finds_a_record_by_its_catalog_schema_name_and_skips_unlisted_types_anywhere(self) -> None:
        store = SingleFileMessagePackBackingStore(self.directory / "names.cc")
        store.push(CultCacheEnvelope.create(key="a", type="aaa.unlisted", payload=b"\x00opaque"))
        store.push(CultCacheEnvelope.create(key="n", type="tests.by-name", payload=b'{"n": 1}'))
        document = define_document_type("tests.by-type", schema_name="tests.by-name")
        self.assertEqual(convert(store, self.directory / "names.out", [document]), 1)
        converted = {item.key: item.payload for item in SingleFileMessagePackBackingStore(self.directory / "names.out").pull_all()}
        self.assertEqual(converted["n"], MESSAGEPACK_PAYLOAD_CODEC.encode({"n": 1}))
        self.assertEqual(converted["a"], b"\x00opaque")

    def test_convert_reads_under_the_source_codec_and_writes_under_the_definitions(self) -> None:
        store = self._filled(
            lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc"),
            "codec",
            {"k1": MESSAGEPACK_PAYLOAD_CODEC.encode({"n": 1})},
        )
        definition = define_document_type("tests.mig", payload_codec=JSON_PAYLOAD_CODEC)
        destination = self.directory / "codec.out"
        self.assertEqual(convert(store, destination, [definition], source_codec=MESSAGEPACK_PAYLOAD_CODEC), 1)
        self.assertEqual(SingleFileMessagePackBackingStore(destination).pull_all()[0].payload, b'{"n": 1}')

    def test_convert_takes_only_existing_library_file_stores(self) -> None:
        destination = self.directory / "foreign.out"
        with self.assertRaises(TypeError) as caught:
            convert(object(), destination, [define_document_type("tests.mig")])
        self.assertEqual(str(caught.exception), "convert takes a library file store, not object")
        self.assertFalse(destination.exists())
        missing = SingleFileMessagePackBackingStore(self.directory / "missing.cc")
        with self.assertRaises(CultCacheError) as caught:
            convert(missing, destination, [define_document_type("tests.mig")])
        self.assertEqual(str(caught.exception), SOURCE_MISSING)
        self.assertFalse(destination.exists())

    def test_convert_refusals_name_no_path(self) -> None:
        for label, make in self._stores():
            with self.subTest(store=label):
                folder = self.directory / label / f"{CANARY}-dir"
                folder.mkdir(parents=True)
                definitions = [define_document_type("tests.mig")]
                store = type(make("x"))(folder / f"{CANARY}-src")
                taken = type(store)(folder / f"{CANARY}-taken")
                taken.push(CultCacheEnvelope.create(key="k", type="tests.mig", payload=b'{"n": 1}'))
                store.push(CultCacheEnvelope.create(key="k", type="tests.mig", payload=b'{"n": 1}'))
                missing = type(store)(folder / f"{CANARY}-missing")
                for expected, call in (
                    (SOURCE_MISSING, lambda: convert(missing, folder / f"{CANARY}-out", definitions)),
                    (DESTINATION_EXISTS, lambda: convert(store, taken.path, definitions)),
                ):
                    with self.assertRaises(CultCacheError) as caught:
                        call()
                    refusal = caught.exception
                    self.assertEqual(str(refusal), expected)
                    for text in (str(refusal), repr(refusal), repr(refusal.args), repr(vars(refusal)), _chain_text(refusal)):
                        self.assertNotIn(CANARY, text)
                        self.assertNotIn(str(folder), text)
                    self.assertIsNone(refusal.__context__)
                    self.assertIsNone(refusal.__cause__)

    def _one_record(self, label, name):
        store = self._filled(dict(self._stores())[label], name, {"k1": b'{"n": 1}'})
        return store, self.directory / f"{name}.out", [define_document_type("tests.mig")]

    def test_convert_interrupt_leaves_no_destination(self) -> None:
        import cultcache_py.cache as cache_module

        real_open = open

        class FirstWriteInterrupts:
            def __init__(self, handle) -> None:
                self.handle = handle

            def __enter__(self):
                self.handle.__enter__()
                return self

            def __exit__(self, *exc):
                return self.handle.__exit__(*exc)

            def write(self, data):
                raise KeyboardInterrupt

            def __getattr__(self, name):
                return getattr(self.handle, name)

        def created_then_interrupted(path, mode):
            real_open(path, mode).close()
            raise KeyboardInterrupt

        def created_then_first_write_interrupted(path, mode):
            return FirstWriteInterrupts(real_open(path, mode))

        for label, _ in self._stores():
            for case, seam in (
                ("raises-after-create", created_then_interrupted),
                ("first-write-raises", created_then_first_write_interrupted),
            ):
                with self.subTest(store=label, case=case):
                    store, destination, definitions = self._one_record(label, f"int-{label}-{case}")
                    before = store.path.read_bytes()
                    with mock.patch.object(cache_module, "open", seam, create=True):
                        with self.assertRaises(KeyboardInterrupt):
                            convert(store, destination, definitions)
                    self.assertFalse(destination.exists())
                    self.assertEqual(store.path.read_bytes(), before)
                    self.assertEqual(convert(store, destination, definitions), 1)
                    self.assertTrue(destination.exists())

    def test_convert_never_removes_a_destination_it_did_not_create(self) -> None:
        import cultcache_py.cache as cache_module

        for label, _ in self._stores():
            with self.subTest(store=label):
                store, destination, definitions = self._one_record(label, f"theirs-{label}")
                destination.write_bytes(b"theirs")
                with mock.patch.object(cache_module, "open", side_effect=KeyboardInterrupt, create=True):
                    with self.assertRaises(CultCacheError) as caught:
                        convert(store, destination, definitions)
                self.assertEqual(str(caught.exception), DESTINATION_EXISTS)
                self.assertEqual(destination.read_bytes(), b"theirs")
                # Another process creates the destination between the existence check and the create.
                other = self.directory / f"raced-{label}.out"
                real_exists = Path.exists

                def exists(path):
                    if path == other:
                        other.write_bytes(b"raced")
                        return False
                    return real_exists(path)

                with mock.patch.object(Path, "exists", exists):
                    with self.assertRaises(CultCacheError) as caught:
                        convert(store, other, definitions)
                self.assertEqual(str(caught.exception), DESTINATION_EXISTS)
                self.assertEqual(other.read_bytes(), b"raced")

    def test_a_destination_under_a_missing_directory_is_created(self) -> None:
        store = self._filled(
            lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc"), "nested", {"k1": b'{"n": 1}'}
        )
        destination = self.directory / "a" / "b" / "nested.out"
        self.assertEqual(convert(store, destination, [define_document_type("tests.mig")]), 1)
        self.assertTrue(destination.exists())


if __name__ == "__main__":
    unittest.main()
