"""raw_record_to_envelope is the one place a replicated raw record becomes an envelope; it refuses a record whose storedAt is no stamp.

The stamp shape is tests/vectors/stored-at-stamp/stamps.msgpack (variants:ruling:raw-put-without-stamp-refused); cultmesh-py tests the
whole replication paths, this tests the function itself.
"""

from __future__ import annotations

import unittest
from pathlib import Path
from typing import Any

import msgpack
from cultcache_py import define_database_entry_type
from cultnet_py.replication import raw_record_to_envelope

VECTOR = Path(__file__).resolve().parents[3] / "tests/vectors/stored-at-stamp/stamps.msgpack"
ROWS: list[tuple[str, bool]] = [(text, parses) for text, parses in msgpack.unpackb(VECTOR.read_bytes(), raw=False)]
NOTE = define_database_entry_type(
    "tests.envelope_note",
    [("schema_version", 0), ("text", 1)],
    schema_id="sha256:envelope-note",
    schema_name="tests.envelope_note",
    schema_version="tests.envelope_note.v1",
)


def record_with(stamp: Any) -> dict[str, Any]:
    record = {
        "schemaId": "sha256:envelope-note",
        "recordKey": "note:1",
        "payload": NOTE.encode_payload({"schema_version": "tests.envelope_note.v1", "text": "t"}),
    }
    if stamp is not None:
        record["storedAt"] = stamp
    return record


class RawRecordToEnvelopeStampTests(unittest.TestCase):
    def test_every_vector_row_is_an_envelope_or_a_refusal_as_the_vector_says(self) -> None:
        self.assertGreater(len(ROWS), 30)
        for text, parses in ROWS:
            with self.subTest(text=text):
                if parses:
                    self.assertEqual(raw_record_to_envelope(NOTE, "sha256:envelope-note", record_with(text)).stored_at, text)
                else:
                    with self.assertRaises(ValueError):
                        raw_record_to_envelope(NOTE, "sha256:envelope-note", record_with(text))

    def test_a_missing_or_non_string_stamp_is_refused(self) -> None:
        for stamp in (None, 12345, b"2026-11-07T00:00:00Z"):
            with self.subTest(stamp=stamp), self.assertRaises(ValueError):
                raw_record_to_envelope(NOTE, "sha256:envelope-note", record_with(stamp))


if __name__ == "__main__":
    unittest.main()
