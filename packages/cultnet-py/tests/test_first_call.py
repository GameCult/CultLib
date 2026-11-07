"""The Python CultNet first call from docs/start/README.md. The docs show the region between the
`snippet` markers verbatim, and test_docs_show_the_tested_snippet fails when they differ."""

import unittest
from pathlib import Path

# snippet:begin
import socket
from dataclasses import asdict, dataclass

from cultcache_py import CultCache, define_document_type
from cultnet_py import (
    apply_raw_document_record,
    document_put_raw,
    encode_frame,
    parse_message,
    read_frame,
    schema_document_map,
)


@dataclass
class Note:
    title: str
    body: str


note_doc = define_document_type(
    "note",
    encode=lambda note: asdict(note),
    decode=lambda raw: Note(**raw),
    name="title",
)


def first_call() -> Note:
    sender, receiver = socket.socketpair()  # an in-process pipe; use socket.create_connection for TCP

    # Send: a typed document as a cultnet.document_put_raw.v0 message in one length-prefixed frame.
    put = document_put_raw(
        message_id="put-1",
        key="note:hello",
        schema_id=note_doc.catalog_entry().schema_id,
        stored_at="2026-01-01T00:00:00Z",
        payload=note_doc.encode_payload(Note("Hello", "sent over CultNet")),
    )
    sender.sendall(encode_frame(put.to_bytes()))

    # Receive: read the frame, parse the message, apply its record to a second cache.
    message = parse_message(read_frame(receiver.makefile("rb")))
    assert message.schema_version == "cultnet.document_put_raw.v0"
    inbox = CultCache.builder().register_document_type(note_doc).build()
    apply_raw_document_record(inbox, schema_document_map([note_doc]), message.body["document"])
    sender.close()
    receiver.close()
    return inbox.get_required(note_doc, "note:hello")
# snippet:end


class FirstCallTests(unittest.TestCase):
    def test_a_typed_document_crosses_a_pipe_into_a_second_cache(self) -> None:
        self.assertEqual(first_call(), Note("Hello", "sent over CultNet"))

    def test_docs_show_the_tested_snippet(self) -> None:
        source = Path(__file__).read_text(encoding="utf-8")
        snippet = source.split("# snippet:begin\n", 1)[1].split("# snippet:end\n", 1)[0]
        docs = (Path(__file__).parents[3] / "docs" / "start" / "README.md").read_text(encoding="utf-8")
        self.assertIn("```python\n" + snippet + "```", docs)


if __name__ == "__main__":
    unittest.main()
