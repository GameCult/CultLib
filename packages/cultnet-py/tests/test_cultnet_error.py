from __future__ import annotations

import json
import unittest
from pathlib import Path

import msgpack

from cultnet_py import CULTNET_ERROR_CODES, error_message, parse_message

# contracts/cultnet/interop/error-vectors.json: bytes captured from MessagePack C# 3.1.7 and pinned
# by cultnet-rs/tests/error_message.rs (Rust encodes to exactly these bytes).
_REPO = Path(__file__).resolve().parents[3]
_VECTORS = json.loads((_REPO / "contracts/cultnet/interop/error-vectors.json").read_text(encoding="utf-8"))["vectors"]
_SCHEMA = json.loads((_REPO / "contracts/cultnet/cultnet.error.schema.json").read_text(encoding="utf-8"))


class CultNetErrorMessageTests(unittest.TestCase):
    def test_closed_code_set_equals_the_schema_enum(self) -> None:
        schema_codes = [c for c in _SCHEMA["properties"]["code"]["enum"] if c is not None]
        self.assertEqual(sorted(CULTNET_ERROR_CODES), sorted(schema_codes))

    def test_vectors_cover_every_code(self) -> None:
        self.assertEqual({v["message"]["code"] for v in _VECTORS}, set(CULTNET_ERROR_CODES))

    def test_decodes_csharp_and_rust_written_bytes_with_every_key(self) -> None:
        for vector in _VECTORS:
            message = parse_message(bytes.fromhex(vector["messagePackHex"]))
            self.assertEqual(message.to_wire(), vector["message"], vector["name"])
            self.assertEqual(
                list(message.to_wire()), ["schemaVersion", "error", "routingHint", "code", "details"], vector["name"]
            )

    def test_reencode_is_byte_identical(self) -> None:
        for vector in _VECTORS:
            expected = bytes.fromhex(vector["messagePackHex"])
            self.assertEqual(parse_message(expected).to_bytes(), expected, vector["name"])

    def test_builder_produces_the_csharp_bytes(self) -> None:
        by_name = {v["name"]: bytes.fromhex(v["messagePackHex"]) for v in _VECTORS}
        stale = error_message(
            "cursor_stale: cursor is stale", code="cursor_stale", details={"asOf": 1, "current": 2}
        )
        self.assertEqual(stale.to_bytes(), by_name["cursor_stale"])
        invalid = error_message("cursor_invalid: cannot parse cursor", code="cursor_invalid")
        self.assertEqual(invalid.to_bytes(), by_name["cursor_invalid"])
        selection = error_message(
            "selection_invalid: blank key entry",
            code="selection_invalid",
            details={"field": "keys", "value": "   "},
        )
        self.assertEqual(selection.to_bytes(), by_name["selection_invalid_field_value"])

    def test_builder_writes_nil_keys_and_accepts_a_null_code(self) -> None:
        wire = error_message("boom").to_wire()
        self.assertEqual(wire, {"schemaVersion": "cultnet.error.v0", "error": "boom", "routingHint": None, "code": None, "details": None})

    def test_unknown_code_is_refused_by_builder_and_decoder(self) -> None:
        with self.assertRaises(ValueError):
            error_message("x", code="unsupported_schema_version")
        bad = msgpack.packb(
            {"schemaVersion": "cultnet.error.v0", "error": "x", "routingHint": None, "code": "nope", "details": None},
            use_bin_type=True,
        )
        with self.assertRaises(ValueError):
            parse_message(bad)

    def test_dropping_code_or_details_is_refused(self) -> None:
        vector = next(v for v in _VECTORS if v["name"] == "cursor_stale")
        for dropped in ("code", "details", "routingHint"):
            wire = dict(vector["message"])
            del wire[dropped]
            with self.assertRaises(ValueError, msg=dropped):
                parse_message(msgpack.packb(wire, use_bin_type=True))

    def test_malformed_details_are_refused(self) -> None:
        base = dict(next(v for v in _VECTORS if v["name"] == "cursor_stale")["message"])
        for details in (
            {"field": None, "value": None, "asOf": 1},
            {"field": None, "value": None, "asOf": -1, "current": 2},
            {"field": None, "value": None, "asOf": 1, "current": 2, "extra": 1},
        ):
            with self.assertRaises(ValueError, msg=str(details)):
                parse_message(msgpack.packb({**base, "details": details}, use_bin_type=True))
        with self.assertRaises(ValueError):
            error_message("x", code="cursor_stale", details={"asOf": 1, "bogus": 2})


if __name__ == "__main__":
    unittest.main()
