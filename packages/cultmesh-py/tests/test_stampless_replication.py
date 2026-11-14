"""A record a peer replicates to this node is stored under the stamp it carries, so one with no stamp is refused where it becomes an envelope.

The stamp shape is tests/vectors/stored-at-stamp/stamps.msgpack, held to the same answers as the raw put of the server
(variants:ruling:raw-put-without-stamp-refused). A snapshot response is refused whole with ValueError before anything is stored (as a record of an unregistered schema is refused whole). A shard log is
refused whole too: every put it would apply is judged for its stamp before the first entry is stored (a put of an unregistered schema is skipped, as ever, and is not judged). The refusal of a put is
message_invalid (field `document`), tested in test_remote_write_authority.py.
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from typing import Any

import msgpack
from cultcache_py import define_database_entry_type
from cultcache_py.stores import parse_stored_at
from cultmesh_py import CultMeshGrantAllWrites, CultMeshServerPeer, create_node
from cultmesh_py.client import CultMeshDocumentSubscription
from cultmesh_py.server import CultMeshLocalServer
from cultnet_py import CultNetDatabaseChange, document_put_raw

SCHEMA_ID = "sha256:stampless-note"
SHARD_ID = "stampless-shard"
PEER = CultMeshServerPeer("127.0.0.1:50000", "tcp")
VECTOR = Path(__file__).resolve().parents[3] / "tests/vectors/stored-at-stamp/stamps.msgpack"
ROWS: list[tuple[str, bool]] = [(text, parses) for text, parses in msgpack.unpackb(VECTOR.read_bytes(), raw=False)]
# The vector's texts, plus the shapes a record can lack a stamp in: the field missing, and not a string at all.
REFUSED: list[tuple[str, Any]] = [(f"vector {text!r}", text) for text, parses in ROWS if not parses] + [
    ("missing", None),
    ("not a string", 12345),
]

NOTE = define_database_entry_type(
    "tests.stampless_note",
    [("schema_version", 0), ("text", 1)],
    schema_id=SCHEMA_ID,
    schema_name="tests.stampless_note",
    schema_version="tests.stampless_note.v1",
)


def record_of(key: str, text: str, stamp: Any = "2026-11-07T00:00:00Z") -> dict[str, Any]:
    record = {
        "schemaId": SCHEMA_ID,
        "recordKey": key,
        "payloadEncoding": "messagepack",
        "payload": NOTE.encode_payload({"schema_version": "tests.stampless_note.v1", "text": text}),
    }
    if stamp is not None:
        record["storedAt"] = stamp
    return record


def snapshot_of(*records: dict[str, Any]) -> dict[str, Any]:
    return {"schemaVersion": "cultnet.snapshot_response_raw.v0", "messageId": "snapshot", "documents": list(records)}


def shard_log_of(*records: dict[str, Any]) -> dict[str, Any]:
    entries = [
        {
            "sequence": index + 1,
            "committedAt": "2026-11-07T00:00:00Z",
            "changeKind": "added",
            "put": document_put_raw(
                message_id=f"put-{index}", key=record["recordKey"], schema_id=SCHEMA_ID, stored_at="2026-11-07T00:00:00Z",
                payload=record["payload"], shard_id=SHARD_ID, shard_epoch=1,
            ).to_wire() | {"document": record},
        }
        for index, record in enumerate(records)
    ]
    return {
        "schemaVersion": "cultnet.shard_log_response.v0", "messageId": "log", "shardId": SHARD_ID, "shardEpoch": 1,
        "entries": entries, "resyncRequired": False,
    }


class StampVectorTests(unittest.TestCase):
    def test_python_parses_every_row_of_the_vector_as_the_vector_says(self) -> None:
        self.assertGreater(len(ROWS), 30)
        for text, parses in ROWS:
            with self.subTest(text=text):
                self.assertEqual(parse_stored_at(text) is not None, parses)

    def test_every_vector_row_through_the_server_put_handler_is_stored_or_refused_as_the_vector_says(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            node = create_node(Path(directory) / "node.cc", runtime_id="vector-server")
            node.database.register_document(NOTE)
            server = CultMeshLocalServer(node=node, port=0, write_authority=CultMeshGrantAllWrites())
            sized = CultMeshLocalServer(node=node, port=0, write_authority=CultMeshGrantAllWrites(), max_snapshot_bytes=10**6)
            for index, (text, parses) in enumerate(ROWS):
                for label, handler, over_rudp in (("plain", server, False), ("sized", sized, False), ("rudp", server, True)):
                    with self.subTest(text=text, path=label):
                        key = f"vector:{index}:{label}"
                        message = document_put_raw(
                            message_id="wire", key=key, schema_id=SCHEMA_ID, stored_at=text,
                            payload=record_of(key, "peer")["payload"], shard_id=SHARD_ID, shard_epoch=1,
                        ).to_wire()
                        replies = handler._handle_connection_message(message, {}, over_rudp=over_rudp, peer=PEER)
                        if parses:
                            self.assertEqual(replies, [])
                            self.assertEqual(node.cache.get(NOTE, key)["text"], "peer")
                        else:
                            self.assertEqual([(r["code"], r["details"]["field"]) for r in replies], [("message_invalid", "document")])
                            self.assertIsNone(node.cache.get(NOTE, key))


class StamplessSnapshotTests(unittest.TestCase):
    def build(self, directory: str) -> Any:
        node = create_node(Path(directory) / "node.cc", runtime_id="stampless-client")
        node.database.register_document(NOTE)
        node.database.pull()
        return node

    def test_a_snapshot_with_a_stampless_record_is_refused_whole_and_the_store_survives_a_reopen(self) -> None:
        for label, stamp in REFUSED:
            with self.subTest(label), tempfile.TemporaryDirectory() as directory:
                node = self.build(directory)
                good, bad = record_of("note:good", "good"), record_of("note:bad", "bad", stamp)
                for order in ((good, bad), (bad, good)):
                    with self.assertRaises(ValueError):
                        node.database.apply_snapshot_response(snapshot_of(*order))
                    self.assertIsNone(node.cache.get(NOTE, "note:good"), "a refused snapshot stores none of its records")
                    self.assertIsNone(node.cache.get(NOTE, "note:bad"))
                # The store file reads back, and is still writable by a snapshot of stamped records.
                reopened = self.build(directory)
                self.assertIsNone(reopened.cache.get(NOTE, "note:bad"))
                applied = reopened.database.apply_snapshot_response(snapshot_of(good))
                self.assertEqual([a.record_key for a in applied], ["note:good"])
                self.assertEqual(self.build(directory).cache.get(NOTE, "note:good")["text"], "good")

    def test_a_snapshot_of_every_stamped_vector_row_is_stored(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            node = self.build(directory)
            rows = [(index, text) for index, (text, parses) in enumerate(ROWS) if parses]
            node.database.apply_snapshot_response(snapshot_of(*[record_of(f"stamped:{i}", "ok", text) for i, text in rows]))
            self.assertEqual(len(self.build(directory).cache.get_all(NOTE)), len(rows))
            for index, _ in rows:
                self.assertEqual(node.cache.get(NOTE, f"stamped:{index}")["text"], "ok")


class StamplessShardLogTests(unittest.TestCase):
    def test_a_shard_log_put_with_no_stamp_is_refused_and_the_store_stays_writable(self) -> None:
        for label, stamp in REFUSED:
            with self.subTest(label), tempfile.TemporaryDirectory() as directory:
                node = create_node(Path(directory) / "node.cc", runtime_id="stampless-log")
                node.database.register_document(NOTE)
                with self.assertRaises(ValueError):
                    node.database.apply_shard_log_response(shard_log_of(record_of("note:bad", "bad", stamp)))
                self.assertIsNone(node.cache.get(NOTE, "note:bad"))
                reopened = create_node(Path(directory) / "node.cc", runtime_id="stampless-log")
                reopened.database.register_document(NOTE)
                reopened.database.pull()
                reopened.database.apply_shard_log_response(shard_log_of(record_of("note:good", "good")))
                self.assertEqual(reopened.cache.get(NOTE, "note:good")["text"], "good")
                again = create_node(Path(directory) / "node.cc", runtime_id="stampless-log")
                again.database.register_document(NOTE)
                again.database.pull()
                self.assertEqual(again.cache.get(NOTE, "note:good")["text"], "good")


    def test_a_shard_log_with_a_bad_entry_in_the_middle_stores_nothing_and_tells_no_watcher(self) -> None:
        for label, stamp in REFUSED:
            with self.subTest(label), tempfile.TemporaryDirectory() as directory:
                node = create_node(Path(directory) / "node.cc", runtime_id="stampless-multi")
                node.database.register_document(NOTE)
                seen: list[Any] = []
                node.database.watch(seen.append)
                log = shard_log_of(record_of("note:a", "a"), record_of("note:b", "b", stamp), record_of("note:c", "c"))
                with self.assertRaises(ValueError):
                    node.database.apply_shard_log_response(log)
                for key in ("note:a", "note:b", "note:c"):
                    self.assertIsNone(node.cache.get(NOTE, key), f"{key} was not stored")
                self.assertEqual(seen, [], "no watcher heard of a refused log")
                node.database.apply_shard_log_response(shard_log_of(record_of("note:a", "a"), record_of("note:c", "c")))
                self.assertEqual([node.cache.get(NOTE, k)["text"] for k in ("note:a", "note:c")], ["a", "c"])
                self.assertEqual(len(seen), 2, "a later good log applies and is heard")


class _StubSubscription:
    def __init__(self, change: CultNetDatabaseChange) -> None:
        self._change = change

    def read_next_change(self) -> CultNetDatabaseChange:
        return self._change


class StamplessClientReplayTests(unittest.TestCase):
    def test_a_subscribed_put_with_no_stamp_is_refused_by_read_next_change_and_nothing_is_stored(self) -> None:
        for label, stamp in REFUSED:
            with self.subTest(label), tempfile.TemporaryDirectory() as directory:
                node = create_node(Path(directory) / "node.cc", runtime_id="stampless-subscriber")
                node.database.register_document(NOTE)
                subscription = CultMeshDocumentSubscription(client=None, database=node.database, document=NOTE, key="note:bad")  # type: ignore[arg-type]

                def deliver(record: dict[str, Any]) -> Any:
                    message = {
                        "schemaVersion": "cultnet.database_change_raw.v0", "messageId": "change", "changeKind": "added",
                        "schemaId": SCHEMA_ID, "recordKey": record["recordKey"], "document": record,
                    }
                    subscription._subscription = _StubSubscription(CultNetDatabaseChange.from_wire(message))  # type: ignore[assignment]
                    return subscription.read_next_change()

                with self.assertRaises(ValueError):
                    deliver(record_of("note:bad", "bad", stamp))
                self.assertIsNone(node.cache.get(NOTE, "note:bad"))
                self.assertEqual(deliver(record_of("note:bad", "stamped")).value["text"], "stamped")


if __name__ == "__main__":
    unittest.main()
