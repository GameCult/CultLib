from __future__ import annotations

import json
import unittest
from pathlib import Path
from typing import Any

import msgpack

from cultnet_py import (
    CULTNET_ERROR_CODES,
    CULTNET_ERROR_V1_CODES,
    CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS,
    error_message,
    hello,
    hello_advertises_write_receipts,
    parse_message,
    write_content_hash,
)

# Decoder-side conformance with contracts/cultnet/interop/write-receipt-vectors.json: `contentHash`, `wire` and
# `schemaInvalid`. `dedupe` and `blankFields` are server obligations and `scenarios` is the client write tracker;
# this runtime has neither a receipt server nor a write client, so it replays none of them.
_INTEROP = Path(__file__).resolve().parents[3] / "contracts/cultnet/interop"
_VECTORS = json.loads((_INTEROP / "write-receipt-vectors.json").read_text(encoding="utf-8"))
_ERROR_V1_SCHEMA = json.loads((_INTEROP / "cultnet.error.v1.schema.json").read_text(encoding="utf-8"))


def _pack(wire: dict[str, Any]) -> bytes:
    return msgpack.packb(wire, use_bin_type=True)


def _admitted(**override: Any) -> dict[str, Any]:
    return {
        "schemaVersion": "cultnet.write_admitted.v0",
        "messageId": "put-1",
        "operation": "put",
        "schemaId": "note.v0",
        "recordKey": "note:1",
        **override,
    }


def _refused(**override: Any) -> dict[str, Any]:
    return {
        "schemaVersion": "cultnet.error.v1",
        "error": "write_not_authorized: no",
        "routingHint": None,
        "code": "write_not_authorized",
        "details": None,
        "messageId": "put-1",
        **override,
    }


class WriteReceiptVectorTests(unittest.TestCase):
    def test_vector_sections_are_the_ones_this_file_accounts_for(self) -> None:
        self.assertEqual(
            set(_VECTORS) - {"provenance", "specification"},
            {"constants", "contentHash", "dedupe", "wire", "blankFields", "schemaInvalid", "scenarios"},
        )
        self.assertEqual(len(_VECTORS["contentHash"]["cases"]), 4)
        self.assertEqual(len(_VECTORS["wire"]), 7)
        self.assertEqual(len(_VECTORS["schemaInvalid"]), 7)

    def test_content_hash_matches_every_vector(self) -> None:
        for case in _VECTORS["contentHash"]["cases"]:
            payload = None if case["payloadHex"] is None else bytes.fromhex(case["payloadHex"])
            digest = write_content_hash(case["operation"], case["schemaId"], case["recordKey"], payload)
            self.assertEqual(digest.hex(), case["sha256"], case["name"])

    def test_wire_vectors_decode_to_their_fields_in_order_and_reencode_byte_identical(self) -> None:
        for vector in _VECTORS["wire"]:
            raw = bytes.fromhex(vector["messagePackHex"])
            message = parse_message(raw)
            self.assertEqual(message.to_wire(), vector["message"], vector["name"])
            self.assertEqual(list(message.to_wire()), list(vector["message"]), vector["name"])
            self.assertEqual(message.to_bytes(), raw, vector["name"])

    def test_schema_invalid_vectors_are_refused(self) -> None:
        for vector in _VECTORS["schemaInvalid"]:
            with self.assertRaises(ValueError, msg=vector["name"]):
                parse_message(bytes.fromhex(vector["messagePackHex"]))

    def test_a_payload_that_is_not_bytes_changes_the_hash(self) -> None:
        # The payload is a bin, not an str and not a re-parsed document.
        as_bin = write_content_hash("put", "note.v0", "note:1", b"\x81\xa2id\xa11")
        self.assertNotEqual(as_bin, write_content_hash("put", "note.v0", "note:1", None))
        self.assertEqual(len(as_bin), 32)


class ErrorV1Tests(unittest.TestCase):
    def test_code_set_equals_the_schema_enum_and_v0_is_unchanged(self) -> None:
        schema_codes = [c for c in _ERROR_V1_SCHEMA["properties"]["code"]["enum"] if c is not None]
        self.assertEqual(sorted(CULTNET_ERROR_V1_CODES), sorted(schema_codes))
        self.assertNotIn("limit_reached", CULTNET_ERROR_CODES)
        self.assertEqual(CULTNET_ERROR_V1_CODES[-1], "limit_reached")

    def test_every_v1_code_decodes_and_an_unknown_one_does_not(self) -> None:
        for code in CULTNET_ERROR_V1_CODES:
            self.assertEqual(parse_message(_pack(_refused(code=code))).body["code"], code)
        parse_message(_pack(_refused(code=None)))
        with self.assertRaises(ValueError):
            parse_message(_pack(_refused(code="invented")))
        with self.assertRaises(ValueError):
            parse_message(_pack(_refused(code=7)))

    def test_limit_reached_is_v1_only(self) -> None:
        v0 = {k: v for k, v in _refused(code="limit_reached", schemaVersion="cultnet.error.v0").items() if k != "messageId"}
        with self.assertRaises(ValueError):
            parse_message(_pack(v0))
        with self.assertRaises(ValueError):
            error_message("x", code="limit_reached")

    def test_message_id_is_required_and_is_a_nonempty_string_or_null(self) -> None:
        self.assertEqual(parse_message(_pack(_refused(messageId="w-é 1"))).body["messageId"], "w-é 1")
        self.assertIsNone(parse_message(_pack(_refused(messageId=None))).body["messageId"])
        omitted = {k: v for k, v in _refused().items() if k != "messageId"}
        with self.assertRaises(ValueError):
            parse_message(_pack(omitted))
        for bad in ("", 5, b"put-1", ["put-1"], True):
            with self.assertRaises(ValueError, msg=repr(bad)):
                parse_message(_pack(_refused(messageId=bad)))

    def test_v0_stays_closed_to_message_id(self) -> None:
        v0 = _refused(schemaVersion="cultnet.error.v0", code="message_invalid")
        with self.assertRaises(ValueError):
            parse_message(_pack(v0))
        del v0["messageId"]
        self.assertEqual(parse_message(_pack(v0)).schema_version, "cultnet.error.v0")

    def test_v1_is_closed_to_unknown_keys_and_keeps_the_shape_rules_of_v0(self) -> None:
        for bad in (
            _refused(extra=1),
            _refused(error=""),
            _refused(error=3),
            _refused(routingHint={"shard": None}),
            _refused(routingHint={"shard": 1, "reason": "r"}),
            _refused(routingHint={"shard": None, "reason": 1}),
            _refused(details={"field": None, "value": None, "asOf": None}),
            _refused(details={"field": 1, "value": None, "asOf": None, "current": None}),
            _refused(details={"field": None, "value": None, "asOf": -1, "current": None}),
            _refused(details={"field": None, "value": None, "asOf": None, "current": True}),
        ):
            with self.assertRaises(ValueError, msg=str(bad)):
                parse_message(_pack(bad))
        for good in (
            _refused(routingHint={"shard": None, "reason": "r"}),
            _refused(details={"field": "messageId", "value": "x", "asOf": 1, "current": 2}),
        ):
            parse_message(_pack(good))

    def test_the_refusal_reason_names_its_version(self) -> None:
        with self.assertRaisesRegex(ValueError, "cultnet.error.v1 code"):
            parse_message(_pack(_refused(code="invented")))
        with self.assertRaisesRegex(ValueError, r"routingHint must be null or \{shard, reason\}"):
            parse_message(_pack(_refused(routingHint={"shard": None})))


class RefusalTextTests(unittest.TestCase):
    """A refusal names its frame version and the rule it broke; a caller reads this text in a log."""

    def assertRefused(self, wire: dict[str, Any], pattern: str) -> None:
        with self.assertRaisesRegex(ValueError, pattern):
            parse_message(_pack(wire))

    def test_error_frames_name_their_own_version(self) -> None:
        v0 = {k: v for k, v in _refused(schemaVersion="cultnet.error.v0").items() if k != "messageId"}
        self.assertRefused({**v0, "code": "nope"}, r"^cultnet\.error\.v0 code")
        self.assertRefused({**v0, "extra": 1}, r"^cultnet\.error\.v0 must carry exactly")
        self.assertRefused(_refused(extra=1), r"^cultnet\.error\.v1 must carry exactly")
        self.assertRefused(_refused(error=""), r"^cultnet\.error\.v1 error must be")
        self.assertRefused(_refused(messageId=""), r"^cultnet\.error\.v1 messageId must be")
        self.assertRefused(_refused(details={"field": None}), r"^cultnet\.error\.v1 details must be null or carry exactly")
        self.assertRefused(
            _refused(details={"field": 1, "value": None, "asOf": None, "current": None}), r"^cultnet\.error\.v1 details\.field must be"
        )
        self.assertRefused(
            _refused(details={"field": None, "value": None, "asOf": -1, "current": None}), r"^cultnet\.error\.v1 details\.asOf must be"
        )
        with self.assertRaisesRegex(ValueError, r"^cultnet\.error\.v0 code"):
            error_message("x", code="bogus")

    def test_write_admitted_names_the_broken_rule(self) -> None:
        self.assertRefused(_admitted(extra=1), r"^cultnet\.write_admitted\.v0 must carry exactly")
        self.assertRefused(_admitted(recordKey=""), r"^cultnet\.write_admitted\.v0 recordKey must be a non-empty string")
        self.assertRefused(_admitted(operation="upsert"), r"^cultnet\.write_admitted\.v0 operation must be put or delete$")


class WriteAdmittedTests(unittest.TestCase):
    def test_both_operations_decode(self) -> None:
        for operation in ("put", "delete"):
            self.assertEqual(parse_message(_pack(_admitted(operation=operation))).body["operation"], operation)

    def test_every_key_is_required_and_no_other_is_allowed(self) -> None:
        for key in ("messageId", "operation", "schemaId", "recordKey"):
            with self.assertRaises(ValueError, msg=key):
                parse_message(_pack({k: v for k, v in _admitted().items() if k != key}))
        for extra in ("error", "code", "details"):
            with self.assertRaises(ValueError, msg=extra):
                parse_message(_pack(_admitted(**{extra: "x"})))

    def test_each_string_is_nonempty_and_a_string(self) -> None:
        for key in ("messageId", "schemaId", "recordKey"):
            for bad in ("", None, 3, b"x"):
                with self.assertRaises(ValueError, msg=f"{key}={bad!r}"):
                    parse_message(_pack(_admitted(**{key: bad})))

    def test_operation_is_put_or_delete(self) -> None:
        for bad in ("upsert", "PUT", "", None, 1):
            with self.assertRaises(ValueError, msg=repr(bad)):
                parse_message(_pack(_admitted(operation=bad)))

    def test_message_id_is_echoed_byte_for_byte(self) -> None:
        self.assertEqual(parse_message(_pack(_admitted(messageId="  w é 1 "))).body["messageId"], "  w é 1 ")


class HelloCapabilityTests(unittest.TestCase):
    def test_capability_needs_both_frames(self) -> None:
        both = list(CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS)
        self.assertEqual(both, ["cultnet.write_admitted.v0", "cultnet.error.v1"])
        self.assertTrue(hello_advertises_write_receipts(hello(runtime_id="r", supported_message_versions=both)))
        self.assertTrue(hello_advertises_write_receipts(hello(runtime_id="r", supported_message_versions=[*both, "x"])))
        self.assertFalse(hello_advertises_write_receipts(hello(runtime_id="r", supported_message_versions=both[:1])))
        self.assertFalse(hello_advertises_write_receipts(hello(runtime_id="r", supported_message_versions=both[1:])))
        self.assertFalse(hello_advertises_write_receipts(hello(runtime_id="r", supported_message_versions=[])))
        self.assertFalse(hello_advertises_write_receipts(hello(runtime_id="r")))

    def test_a_non_list_listing_is_not_a_capability(self) -> None:
        wire = hello(runtime_id="r").to_wire()
        wire["supportedMessageVersions"] = "cultnet.write_admitted.v0 cultnet.error.v1"
        self.assertFalse(hello_advertises_write_receipts(parse_message(_pack(wire))))

    def test_hello_writes_the_listing_only_when_given_and_round_trips(self) -> None:
        self.assertNotIn("supportedMessageVersions", hello(runtime_id="r").body)
        listed = hello(runtime_id="r", supported_message_versions=list(CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS))
        self.assertEqual(parse_message(listed.to_bytes()), listed)


if __name__ == "__main__":
    unittest.main()
