from __future__ import annotations

import collections
import shutil
import tempfile
import unittest
from pathlib import Path

from cultcache_py import (
    JSON_PAYLOAD_CODEC,
    MESSAGEPACK_PAYLOAD_CODEC,
    CultCache,
    CultCacheEnvelope,
    CultCachePayloadDepthError,
    CultCachePayloadFormatError,
    PayloadCodec,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
)

CANARY = "CANARY-payload-codec-5b81e0"
LEGACY_JSON = b'{"n": 1, "schema": "x"}'


def _store_with(path: Path, payload: bytes) -> SingleFileMessagePackBackingStore:
    store = SingleFileMessagePackBackingStore(path)
    store.push(CultCacheEnvelope.create(key="k", type="tests.legacy", payload=payload))
    return store


class PayloadCodecTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory, True)

    def _cache(self, document, store) -> CultCache:
        return CultCache.builder().register_document_type(document).add_generic_store(store).build()

    def test_the_default_is_messagepack_for_both_definitions(self) -> None:
        document = define_document_type("tests.x")
        self.assertIs(document.payload_codec, MESSAGEPACK_PAYLOAD_CODEC)
        self.assertEqual(document.encode_payload({"a": [1]}), bytes.fromhex("81a1619101"))
        self.assertIs(define_database_entry_type("tests.entry", [("a", 0)]).payload_codec, MESSAGEPACK_PAYLOAD_CODEC)

    def test_the_json_codec_writes_the_0_3_bytes(self) -> None:
        written = JSON_PAYLOAD_CODEC.encode({"b": "é", "a": [1, 2]})
        self.assertEqual(written, b'{"a": [1, 2], "b": "\xc3\xa9"}')
        self.assertEqual(JSON_PAYLOAD_CODEC.decode(written), {"a": [1, 2], "b": "é"})

    def test_a_json_store_is_refused_typed_with_the_remedy_and_loads_nothing(self) -> None:
        store = _store_with(self.directory / "legacy.cc", LEGACY_JSON)
        document = define_document_type("tests.legacy")
        cache = self._cache(document, store)
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            cache.pull_all_backing_stores()
        message = str(caught.exception)
        self.assertEqual((caught.exception.type_name, caught.exception.codec, caught.exception.direction), ("tests.legacy", "MessagePack", "read"))
        self.assertIn("tests.legacy", message)
        self.assertIn("MessagePack", message)
        self.assertIn("payload_codec=JSON_PAYLOAD_CODEC", message)
        self.assertNotIn("schema", message)
        self.assertEqual(cache.get_all(document), [])

    def test_a_json_store_opted_in_reads_and_migrates_to_messagepack(self) -> None:
        path = self.directory / "legacy.cc"
        store = _store_with(path, LEGACY_JSON)
        legacy = define_document_type("tests.legacy", payload_codec=JSON_PAYLOAD_CODEC)
        old = self._cache(legacy, store)
        old.pull_all_backing_stores()
        self.assertEqual(old.get(legacy, "k"), {"n": 1, "schema": "x"})
        modern = define_document_type("tests.legacy")
        new = self._cache(modern, SingleFileMessagePackBackingStore(path))
        new.put(modern, "k", old.get(legacy, "k"))
        reread = self._cache(modern, SingleFileMessagePackBackingStore(path))
        reread.pull_all_backing_stores()
        self.assertEqual(reread.get(modern, "k"), {"n": 1, "schema": "x"})
        self.assertEqual(reread.get_envelope(modern, "k").payload, MESSAGEPACK_PAYLOAD_CODEC.encode({"n": 1, "schema": "x"}))

    def test_the_read_refusal_text_is_exact_under_each_codec(self) -> None:
        with self.assertRaises(CultCachePayloadFormatError) as default:
            define_document_type("tests.text").decode_payload(LEGACY_JSON)
        self.assertEqual(
            str(default.exception),
            "tests.text payload is not one MessagePack value (ExtraData); refused on read. A record written by cultcache-py 0.3 "
            "or earlier through define_document_type's JSON default is read by defining tests.text with payload_codec=JSON_PAYLOAD_CODEC.",
        )
        with self.assertRaises(CultCachePayloadFormatError) as json_side:
            define_document_type("tests.text", payload_codec=JSON_PAYLOAD_CODEC).decode_payload(b"\x81\xa1a")
        self.assertEqual(str(json_side.exception), "tests.text payload is not one JSON value (UnicodeDecodeError); refused on read.")
        self.assertEqual((json_side.exception.type_name, json_side.exception.codec, json_side.exception.direction), ("tests.text", "JSON", "read"))

    def test_the_write_refusal_carries_its_type_codec_and_direction(self) -> None:
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            define_document_type("tests.attrs", encode=lambda value: value).encode_payload({1: "a"})
        self.assertEqual((caught.exception.type_name, caught.exception.codec, caught.exception.direction), ("tests.attrs", "MessagePack", "write"))

    def test_wrong_format_payloads_are_typed_refusals_on_both_sides(self) -> None:
        document = define_document_type("tests.faults")
        for payload in (b"[1,2]", b'"hi"', b"true", b"null", b"1.5", b"-3", b"12", b"\x93\x01\x02", b"\xc1", b"\xa2\xff\xfe"):
            with self.subTest(payload=payload):
                with self.assertRaises(CultCachePayloadFormatError):
                    document.decode_payload(payload)
        json_document = define_document_type("tests.faults", payload_codec=JSON_PAYLOAD_CODEC)
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            json_document.decode_payload(MESSAGEPACK_PAYLOAD_CODEC.encode({"a": 1}))
        self.assertNotIn("JSON_PAYLOAD_CODEC", str(caught.exception))
        self.assertIn("JSON", str(caught.exception))

    def test_the_refusals_never_echo_the_payload_or_the_key(self) -> None:
        document = define_document_type("tests.canary")
        with self.assertRaises(CultCachePayloadFormatError) as read:
            document.decode_payload(('{"' + CANARY + '": 1}').encode())
        self.assertNotIn(CANARY, str(read.exception))
        with self.assertRaises(CultCachePayloadFormatError) as write:
            define_document_type("tests.canary", encode=lambda value: value).encode_payload({7351904: CANARY})
        self.assertNotIn("7351904", str(write.exception))
        self.assertNotIn(CANARY, str(write.exception))
        self.assertIsNone(read.exception.__cause__)

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
        with self.assertRaises(CultCachePayloadFormatError) as caught:
            document.decode_payload(b"x")
        self.assertIn("(ValueError)", str(caught.exception))
        passthrough = define_document_type("tests.v", payload_codec=PayloadCodec("t", lambda tree: b"", refuse))
        with self.assertRaises(KeyError):
            passthrough.decode_payload(b"x")

    def test_map_keys_the_default_cannot_read_back_are_refused_on_write(self) -> None:
        document = define_document_type("tests.keys", encode=lambda value: value)
        for tree, key_type in (({1: "a"}, "int"), ({True: 1}, "bool"), ({"a": {2: 3}}, "int"), ({"a": [{1.5: 1}]}, "float")):
            with self.subTest(tree=tree):
                with self.assertRaises(CultCachePayloadFormatError) as caught:
                    document.encode_payload(tree)
                self.assertEqual(
                    str(caught.exception),
                    f"tests.keys payload has a map key of type {key_type}; MessagePack payloads take str or bytes keys (refused on write).",
                )
        tree = {"a": 1, b"b": {"c": [2]}}
        self.assertEqual(document.decode_payload(document.encode_payload(tree)), tree)
        legacy = define_document_type("tests.keys", encode=lambda value: value, payload_codec=JSON_PAYLOAD_CODEC)
        self.assertEqual(legacy.encode_payload({1: "a"}), b'{"1": "a"}')

    def test_a_refused_key_reaches_no_store(self) -> None:
        document = define_document_type("tests.keys", encode=lambda value: value)
        store = SingleFileMessagePackBackingStore(self.directory / "keys.cc")
        cache = self._cache(document, store)
        with self.assertRaises(CultCachePayloadFormatError):
            cache.put(document, "k", {1: "a"})
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


if __name__ == "__main__":
    unittest.main()
