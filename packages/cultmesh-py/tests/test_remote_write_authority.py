"""A peer's put or delete is applied only when the server's write authority grants it.

Ports the behaviours of C# RemoteWriteAuthorityTests. The C# forwarding behaviour (a refused write is never
forwarded) has no counterpart: this server does not forward non-primary writes. The handlers are reached with
the peer the connection loop would pass, and one test drives the same path over a real TCP connection.
"""

from __future__ import annotations

import json
import unittest
from pathlib import Path
from typing import Any

import msgpack
from cultcache_py import define_database_entry_type
from cultmesh_py import (
    CultMesh,
    CultMeshGrantAllWrites,
    CultMeshRemoteWriteDecision,
    CultMeshRemoteWriteOperation,
    CultMeshRemoteWriteRequest,
    CultMeshServerPeer,
    create_node,
)
from cultmesh_py.server import CultMeshLocalServer
from cultnet_py import CultNetPeerError, create_rudp_schema_transport, document_delete, document_put_raw, error_message

SCHEMA_ID = "sha256:authority-note"
ALIAS = "runtime.generated.authority-note.ui.42"
SHARD_ID = "authority-shard"
KEY_A = "tests:authority:a"
KEY_B = "tests:authority:b"
PEER = CultMeshServerPeer("127.0.0.1:50000", "tcp")

_REPO = Path(__file__).resolve().parents[3]
_VECTORS = json.loads((_REPO / "contracts/cultnet/interop/error-vectors.json").read_text(encoding="utf-8"))["vectors"]

NOTE = define_database_entry_type(
    "tests.authority_note",
    [("schema_version", 0), ("text", 1)],
    schema_id=SCHEMA_ID,
    schema_name="tests.authority_note",
    schema_version="tests.authority_note.v1",
)


class Recording:
    def __init__(self, decide: Any) -> None:
        self._decide = decide
        self.requests: list[CultMeshRemoteWriteRequest] = []

    def decide(self, request: CultMeshRemoteWriteRequest) -> Any:
        self.requests.append(request)
        return self._decide(request)


def put_of(key: str, text: str, *, schema_id: str = SCHEMA_ID) -> dict[str, Any]:
    return document_put_raw(
        message_id="wire",
        key=key,
        schema_id=schema_id,
        stored_at="2026-11-07T00:00:00Z",
        payload=NOTE.encode_payload({"schema_version": "tests.authority_note.v1", "text": text}),
        shard_id=SHARD_ID,
        shard_epoch=1,
    ).to_wire()


def delete_of(key: str, *, schema_id: str = SCHEMA_ID) -> dict[str, Any]:
    return document_delete(
        message_id="wire-delete", schema_id=schema_id, record_key=key, shard_id=SHARD_ID, shard_epoch=1
    ).to_wire()


class RemoteWriteAuthorityTests(unittest.TestCase):
    def build(self, authority: Any = None) -> CultMeshLocalServer:
        node = create_node(runtime_id="authority-server")
        node.database.register_document(NOTE)
        return CultMeshLocalServer(node=node, port=0, write_authority=authority)

    def text_of(self, server: CultMeshLocalServer, key: str) -> str | None:
        value = server.node.cache.get(NOTE, key)
        return None if value is None else value["text"]

    def send(self, server: CultMeshLocalServer, message: dict[str, Any]) -> list[dict[str, Any]]:
        subscriptions: dict[str, Any] = {}
        return server._handle_connection_message(message, subscriptions, peer=PEER)

    def assert_refused(self, replies: list[dict[str, Any]], key: str, *, error: str | None = None) -> None:
        self.assertEqual(len(replies), 1)
        reply = replies[0]
        self.assertEqual(reply["schemaVersion"], "cultnet.error.v0")
        self.assertEqual(reply["code"], "write_not_authorized")
        self.assertTrue(reply["error"].startswith("write_not_authorized"))
        self.assertEqual(reply["details"]["field"], "recordKey")
        self.assertEqual(reply["details"]["value"], key)
        if error is not None:
            self.assertEqual(reply["error"], error)

    def seed(self, server: CultMeshLocalServer, key: str, text: str) -> None:
        # The host writes its own record through its own door, which asks no remote authority.
        self.assertEqual(server.node.database.apply_raw_put_message(put_of(key, text)).change_kind, "added")

    def test_with_no_authority_a_peers_put_and_delete_are_refused_and_change_nothing(self) -> None:
        server = self.build(None)
        self.seed(server, KEY_A, "server")

        with self.assertLogs("cultmesh_py.server", level="WARNING") as logs:
            self.assert_refused(self.send(server, put_of(KEY_A, "peer")), KEY_A)
            self.assert_refused(self.send(server, delete_of(KEY_A)), KEY_A)
            self.assert_refused(self.send(server, put_of(KEY_B, "peer")), KEY_B)

        self.assertEqual(self.text_of(server, KEY_A), "server")
        self.assertIsNone(self.text_of(server, KEY_B))
        self.assertEqual(sum("write not authorized" in line for line in logs.output), 3)

    def test_a_refusal_publishes_no_change_to_a_subscriber(self) -> None:
        server = self.build(None)
        server.start()
        try:
            client = CultMesh.create_client("127.0.0.1", server.port, timeout_seconds=2.0)
            with client.subscribe_database(subscription_id="s", schema_ids=[SCHEMA_ID], include_snapshot=False) as sub:
                with self.assertRaises(CultNetPeerError) as raised:
                    sub.send(put_of(KEY_A, "peer"))
                    sub.read_next()
        finally:
            server.stop()
        self.assertEqual(raised.exception.response["code"], "write_not_authorized")
        self.assertIsNone(self.text_of(server, KEY_A))

    def test_a_granted_put_and_delete_commit_and_the_authority_saw_this_peer(self) -> None:
        authority = Recording(lambda _: CultMeshRemoteWriteDecision.grant())
        server = self.build(authority)

        put_replies = self.send(server, put_of(KEY_A, "peer"))
        self.assertEqual(self.text_of(server, KEY_A), "peer")
        self.assertEqual(put_replies, [])
        self.send(server, delete_of(KEY_A))
        self.assertIsNone(self.text_of(server, KEY_A))

        self.assertEqual(len(authority.requests), 2)
        self.assertTrue(all(request.peer is PEER for request in authority.requests))

    def test_the_request_carries_the_resolved_write_and_scoped_grants_refuse_their_neighbours(self) -> None:
        authority = Recording(
            lambda request: CultMeshRemoteWriteDecision.grant()
            if request.operation is CultMeshRemoteWriteOperation.PUT and request.record_key == KEY_A
            else CultMeshRemoteWriteDecision.refuse("scoped")
        )
        server = self.build(authority)
        self.seed(server, KEY_A, "server")

        self.send(server, put_of(KEY_A, "aliased", schema_id=ALIAS))

        request = authority.requests[0]
        self.assertEqual(request.schema_id, SCHEMA_ID, "the authority sees the canonical id, not the alias as sent")
        self.assertIs(request.operation, CultMeshRemoteWriteOperation.PUT)
        self.assertEqual(request.record_key, KEY_A)
        self.assertEqual(request.shard_id, SHARD_ID)
        self.assertEqual(request.shard_epoch, 1)
        self.assertEqual(self.text_of(server, KEY_A), "aliased")

        self.assert_refused(self.send(server, put_of(KEY_B, "other-key")), KEY_B)
        self.assert_refused(self.send(server, delete_of(KEY_A)), KEY_A)
        self.assertIsNone(self.text_of(server, KEY_B), "a grant scoped to key a refuses key b")
        self.assertEqual(self.text_of(server, KEY_A), "aliased", "a grant scoped to put refuses delete")
        self.assertIs(authority.requests[-1].operation, CultMeshRemoteWriteOperation.DELETE)
        self.assertEqual(authority.requests[-1].schema_id, SCHEMA_ID)

    def test_refusal_precedes_every_other_judgment_of_the_put(self) -> None:
        refusing = self.build(None)
        unowned = put_of("unrelated:authority", "x", schema_id="sha256:nobody-owns-this")
        unowned["document"]["payload"] = msgpack.packb(["nobody.owns.this.v1", "x"], use_bin_type=True)
        self.assert_refused(self.send(refusing, unowned), "unrelated:authority")
        blank_key = put_of("", "x")
        self.assert_refused(self.send(refusing, blank_key), "")

        granting = self.build(Recording(lambda _: CultMeshRemoteWriteDecision.grant()))
        reply = self.send(granting, unowned)
        self.assertEqual(reply[0]["code"], "unregistered_document_put", "a granted put still meets the store's refusal")

    def test_an_authority_that_raises_refuses_and_is_logged_as_an_error(self) -> None:
        def explode(_: Any) -> Any:
            raise RuntimeError("authority exploded")

        server = self.build(Recording(explode))
        self.seed(server, KEY_A, "server")

        with self.assertLogs("cultmesh_py.server", level="ERROR") as logs:
            self.assert_refused(self.send(server, put_of(KEY_A, "peer")), KEY_A)
            self.assert_refused(self.send(server, delete_of(KEY_A)), KEY_A)

        self.assertEqual(self.text_of(server, KEY_A), "server", "fail closed")
        self.assertTrue(any("RuntimeError" in line for line in logs.output))

    def test_a_refusal_with_no_reason_still_says_why(self) -> None:
        server = self.build(Recording(lambda _: CultMeshRemoteWriteDecision.refuse()))
        self.assert_refused(self.send(server, put_of(KEY_A, "peer")), KEY_A, error="write_not_authorized")

    def test_the_refusal_names_the_key_and_the_code_in_the_pinned_bytes(self) -> None:
        vector = next(v for v in _VECTORS if v["name"] == "write_not_authorized")
        message = error_message(
            "write_not_authorized: not granted",
            code="write_not_authorized",
            details={"field": "recordKey", "value": "some:key"},
        )
        self.assertEqual(message.to_bytes(), bytes.fromhex(vector["messagePackHex"]))

        server = self.build(Recording(lambda _: CultMeshRemoteWriteDecision.refuse("not granted")))
        reply = self.send(server, put_of("some:key", "peer"))[0]
        self.assertEqual(msgpack.packb(reply, use_bin_type=True), bytes.fromhex(vector["messagePackHex"]))

    def test_the_peer_receives_the_typed_refusal_over_tcp_and_a_grant_sends_nothing_back(self) -> None:
        authority = Recording(
            lambda request: CultMeshRemoteWriteDecision.grant()
            if request.record_key == KEY_A
            else CultMeshRemoteWriteDecision.refuse("only a")
        )
        server = self.build(authority)
        server.start()
        try:
            client = CultMesh.create_client("127.0.0.1", server.port, timeout_seconds=2.0)
            with client.subscribe_database(subscription_id="s", schema_ids=[SCHEMA_ID], include_snapshot=False) as sub:
                sub.send(put_of(KEY_A, "peer"))
                self.assertEqual(sub.read_next_change().record_key, KEY_A, "the granted write was answered by its change, not an error")
                sub.send(put_of(KEY_B, "peer"))
                with self.assertRaises(CultNetPeerError) as raised:
                    sub.read_next()
        finally:
            server.stop()

        reply = raised.exception.response
        self.assertEqual(reply["error"], "write_not_authorized: only a")
        self.assertEqual(reply["details"]["value"], KEY_B)
        self.assertTrue(authority.requests[0].peer.endpoint.startswith("127.0.0.1:"))
        self.assertEqual(authority.requests[0].peer.transport, "tcp")
        self.assertEqual(self.text_of(server, KEY_A), "peer")
        self.assertIsNone(self.text_of(server, KEY_B))

    def test_a_delete_sent_under_a_schema_alias_asks_the_authority_with_the_canonical_id(self) -> None:
        authority = Recording(lambda _: CultMeshRemoteWriteDecision.grant())
        server = self.build(authority)
        self.seed(server, KEY_A, "server")

        self.send(server, delete_of(KEY_A, schema_id="tests.authority_note.v1"))

        request = authority.requests[0]
        self.assertIs(request.operation, CultMeshRemoteWriteOperation.DELETE)
        self.assertEqual(request.schema_id, SCHEMA_ID)
        self.assertIsNone(self.text_of(server, KEY_A), "the aliased delete was granted and applied")

    def test_a_missing_decision_refuses_put_and_delete(self) -> None:
        server = self.build(Recording(lambda _: None))
        self.seed(server, KEY_A, "server")

        self.assert_refused(self.send(server, put_of(KEY_A, "peer")), KEY_A)
        self.assert_refused(self.send(server, delete_of(KEY_A)), KEY_A)
        self.assert_refused(self.send(server, put_of(KEY_B, "peer")), KEY_B)

        self.assertEqual(self.text_of(server, KEY_A), "server")
        self.assertIsNone(self.text_of(server, KEY_B))

    def test_a_peer_over_rudp_is_refused_with_its_rudp_endpoint_and_the_server_serves_on(self) -> None:
        authority = Recording(lambda _: CultMeshRemoteWriteDecision.refuse("rudp no"))
        server = self.build(authority)
        server.start()
        try:
            transport = create_rudp_schema_transport(
                host="127.0.0.1", port=server.port, connection_id=server.rudp_connection_id, timeout_seconds=4.0
            )
            try:
                transport.send("schema", msgpack.packb(put_of(KEY_A, "peer"), use_bin_type=True))
                reply = msgpack.unpackb(transport.receive(timeout_seconds=4.0).payload, raw=False)
                transport.send("schema", msgpack.packb(delete_of(KEY_A), use_bin_type=True))
                delete_reply = msgpack.unpackb(transport.receive(timeout_seconds=4.0).payload, raw=False)
            finally:
                transport.close()
        finally:
            server.stop()
        self.assertEqual(reply["error"], "write_not_authorized: rudp no")
        self.assertEqual(delete_reply["code"], "write_not_authorized")
        self.assertEqual([r.peer.transport for r in authority.requests], ["rudp", "rudp"])
        self.assertTrue(all(r.peer.endpoint.startswith("127.0.0.1:") for r in authority.requests))
        self.assertIsNone(self.text_of(server, KEY_A))

    def test_a_put_with_an_absurd_payload_is_refused_by_the_gate_not_crashed_on(self) -> None:
        server = self.build(None)
        absurd = put_of(KEY_A, "x")
        absurd["document"]["payload"] = 2**70
        self.assert_refused(self.send(server, absurd), KEY_A)

    def test_an_ungranted_peers_huge_payload_is_refused_without_allocating_it(self) -> None:
        import tracemalloc

        server = self.build(None)
        huge = put_of(KEY_A, "x")
        huge["document"]["payload"] = 10**9  # bytes(10**9) would allocate a gigabyte
        tracemalloc.start()
        try:
            replies = self.send(server, huge)
            _, peak = tracemalloc.get_traced_memory()
        finally:
            tracemalloc.stop()
        self.assert_refused(replies, KEY_A)
        self.assertLess(peak, 10_000_000)

    def test_wrong_typed_shard_fields_are_refused_before_the_gate_and_write_nothing(self) -> None:
        authority = Recording(lambda _: CultMeshRemoteWriteDecision.grant())
        server = self.build(authority)
        self.seed(server, KEY_B, "server")
        shards_before = server.node.database.shard_ids()
        for field, value in (("shardId", {"not": "a string"}), ("shardId", 5), ("shardEpoch", "7"), ("shardEpoch", True), ("shardEpoch", 1.5)):
            for operation, code in (
                ("put", "malformed_document_put"),
                ("delete", "malformed_document_delete"),
            ):
                with self.subTest(field=field, value=value, operation=operation):
                    message = put_of(KEY_A, "x") if operation == "put" else delete_of(KEY_B)
                    message[field] = value
                    replies = self.send(server, message)
                    self.assertEqual([reply["code"] for reply in replies], [code])
        self.assertEqual(authority.requests, [], "the authority was never asked")
        self.assertIsNone(self.text_of(server, KEY_A), "nothing was written")
        self.assertEqual(self.text_of(server, KEY_B), "server", "nothing was deleted")
        self.assertEqual(server.node.database.shard_ids(), shards_before, "no shard or log entry was filed")

    def test_well_typed_shard_fields_reach_the_authority_as_sent(self) -> None:
        authority = Recording(lambda _: CultMeshRemoteWriteDecision.grant())
        server = self.build(authority)
        self.send(server, put_of(KEY_A, "x"))
        self.assertEqual((authority.requests[0].shard_id, authority.requests[0].shard_epoch), (SHARD_ID, 1))

    def test_grant_all_is_an_explicit_choice_that_grants_every_write(self) -> None:
        server = self.build(CultMeshGrantAllWrites())
        self.send(server, put_of(KEY_A, "peer"))
        self.assertEqual(self.text_of(server, KEY_A), "peer")


if __name__ == "__main__":
    unittest.main()
