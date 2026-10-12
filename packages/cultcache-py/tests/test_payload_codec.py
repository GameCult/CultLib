from __future__ import annotations

import collections
import inspect
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
    migrate_payloads,
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

    def test_a_json_store_opted_in_reads_and_migrate_payloads_rewrites_it(self) -> None:
        path = self.directory / "legacy.cc"
        store = _store_with(path, LEGACY_JSON)
        legacy = define_document_type("tests.legacy", payload_codec=JSON_PAYLOAD_CODEC)
        old = self._cache(legacy, store)
        old.pull_all_backing_stores()
        self.assertEqual(old.get(legacy, "k"), {"n": 1, "schema": "x"})
        modern = define_document_type("tests.legacy")
        self.assertEqual(migrate_payloads(SingleFileMessagePackBackingStore(path), [modern]), 1)
        reread = self._cache(modern, SingleFileMessagePackBackingStore(path))
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

    def test_reference_map_keys_read_through_an_any_slot(self) -> None:
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

    def test_reference_map_keys_write_and_read_back_through_a_document_type(self) -> None:
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

    def test_a_cyclic_tree_is_not_walked_forever_by_the_codec(self) -> None:
        cycle: dict = {}
        cycle["a"] = cycle
        with self.assertRaises(ValueError):
            MESSAGEPACK_PAYLOAD_CODEC.encode(cycle)
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


class MigratePayloadsTests(unittest.TestCase):
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
        document = define_document_type("tests.mig", payload_codec=codec)
        cache = CultCache.builder().register_document_type(document).add_generic_store(store).build()
        cache.pull_all_backing_stores()
        return cache.get_all(document)

    def test_migration_is_refused_whole_and_leaves_the_store_untouched(self) -> None:
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"refused-{label}", MIGRATION_RECORDS)
                before = store.path.read_bytes()
                with self.assertRaises(CultCacheError) as caught:
                    migrate_payloads(store, [define_document_type("tests.mig")])
                message = str(caught.exception)
                for part in ("tests.mig", "k7", "integer_out_of_range"):
                    self.assertIn(part, message)
                self.assertNotIn(BIG_DIGITS[:8], _chain_text(caught.exception))
                self.assertIsNone(caught.exception.__context__)
                self.assertEqual(store.path.read_bytes(), before)
                self.assertEqual(len(self._pull_with(store, JSON_PAYLOAD_CODEC)), 7)

    def test_migration_rewrites_every_listed_record_in_one_write(self) -> None:
        records = {key: payload for key, payload in MIGRATION_RECORDS.items() if key != "k7"}
        for label, make in self._stores():
            with self.subTest(store=label):
                store = self._filled(make, f"ok-{label}", records)
                other = CultCacheEnvelope.create(key="o", type="tests.other", payload=b"opaque\x00bytes")
                store.push(other)
                with mock.patch.object(store, "push_all", wraps=store.push_all) as push_all, mock.patch.object(
                    store, "push", wraps=store.push
                ) as push:
                    count = migrate_payloads(store, [define_document_type("tests.mig")])
                self.assertEqual(count, 6)
                self.assertEqual(push_all.call_count, 1)
                self.assertEqual(push.call_count, 0)
                kept = next(item for item in store.pull_all() if item.type == "tests.other")
                self.assertEqual((kept.payload, kept.stored_at), (other.payload, other.stored_at))
                default = self._pull_with(store, MESSAGEPACK_PAYLOAD_CODEC)
                self.assertEqual(len(default), 6)
                with self.assertRaises(CultCachePayloadFormatError):
                    self._pull_with(store, JSON_PAYLOAD_CODEC)
                migrated = {item.key: item.payload for item in store.pull_all() if item.type == "tests.mig"}
                self.assertEqual(migrated["k1"], MESSAGEPACK_PAYLOAD_CODEC.encode({"n": 1}))
                self.assertEqual(migrated["k2"], MESSAGEPACK_PAYLOAD_CODEC.encode(["a", 2]))
                after = store.path.read_bytes()
                with self.assertRaises(CultCacheError):
                    migrate_payloads(store, [define_document_type("tests.mig")])
                self.assertEqual(store.path.read_bytes(), after)

    def test_migration_names_a_source_codec_and_skips_unlisted_types(self) -> None:
        store = self._filled(lambda name: SingleFileMessagePackBackingStore(self.directory / f"{name}.cc"), "src", {"k1": b'{"n": 1}'})
        self.assertEqual(migrate_payloads(store, [define_document_type("tests.nothing")]), 0)
        self.assertEqual(store.pull_all()[0].payload, b'{"n": 1}')
        self.assertEqual(migrate_payloads(store, [define_document_type("tests.mig")], source_codec=JSON_PAYLOAD_CODEC), 1)
        self.assertEqual(store.pull_all()[0].payload, MESSAGEPACK_PAYLOAD_CODEC.encode({"n": 1}))


if __name__ == "__main__":
    unittest.main()
