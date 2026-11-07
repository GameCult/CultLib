from __future__ import annotations

import unittest
from pathlib import Path

import msgpack

from cultcache_py import database_entry_field, define_database_entry_type, define_record_codec, leaves as t

# tests/vectors/slot-nil is written only by the C# reference (SlotNilVectorTests): what it reads and rewrites for a nil, a
# str in a byte[] slot, and a record short of its last slots. This runtime accepts what the reference accepts and rewrites
# the same bytes.
VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "slot-nil"


def _probe():
    inner = define_record_codec("SlotNilInner", [database_entry_field("x", 0, default="", codec=t.string)])
    return define_database_entry_type(
        "tests.slot-nil",
        [
            database_entry_field("s", 0, default="", codec=t.nullable(t.string)),
            database_entry_field("i", 1, default=0, codec=t.int32),
            database_entry_field("b", 2, default=b"", codec=t.nullable(t.bytes)),
            database_entry_field("r", 3, default=None, codec=t.nullable(inner)),
            database_entry_field("l", 4, default=[], codec=t.nullable(t.list(t.string))),
        ],
    )


class SlotNilVectorTests(unittest.TestCase):
    def test_slots_read_and_rewrite_exactly_what_the_reference_does_for_nil_str_in_bytes_and_short_records(self) -> None:
        rows = msgpack.unpackb((VECTORS / "reads.msgpack").read_bytes(), raw=False)
        probe = _probe()
        self.assertEqual(17, len(rows))
        accepted = 0
        for name, payload, was_accepted, rewritten in rows:
            with self.subTest(name):
                if was_accepted:
                    self.assertEqual(rewritten.hex(), probe.encode_payload(probe.decode_payload(payload)).hex())
                    accepted += 1
                else:
                    with self.assertRaises(Exception):
                        probe.decode_payload(payload)
        self.assertEqual(15, accepted)

    def test_a_character_outside_the_bmp_is_four_utf8_bytes(self) -> None:
        rows = {name: (payload, rewritten) for name, payload, _, rewritten in msgpack.unpackb((VECTORS / "reads.msgpack").read_bytes(), raw=False)}
        probe = _probe()
        self.assertEqual("\U0001F600", probe.decode_payload(rows["string-astral"][0])["s"])
        self.assertEqual(b"\xf0\x9f\x98\x80", probe.decode_payload(rows["bytes-str-astral"][0])["b"])


if __name__ == "__main__":
    unittest.main()
