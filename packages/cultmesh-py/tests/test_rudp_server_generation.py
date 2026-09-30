"""The CultMesh local server's RUDP loop asks each peer's session whether a Connect
repeats. A retransmitted Connect keeps the peer; a restarted client on the same
address is a new one, with none of the old one's subscriptions."""
from __future__ import annotations

import socket
import time
import unittest
from dataclasses import dataclass

import msgpack
from cultcache_py import define_database_entry_type
from cultmesh_py import create_node
from cultmesh_py.server import CultMeshLocalServer
from cultnet_py import (
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    document_put_raw,
)
from cultnet_py.transport import CultNetRudpPacketType, decode_rudp_packet, encode_rudp_packet

CONNECTION_ID = 0x10203091
SCHEMA_ID = "mesh.rudp_generation_note.v1"
RELIABLE = CultNetRudpSendOptions(reliable=True, ordered=True)


@dataclass
class Note:
    body: str


NOTE = define_database_entry_type(
    "mesh.rudp_generation_note",
    [("body", 0)],
    cls=Note,
    schema_id=SCHEMA_ID,
    schema_name="mesh.rudp_generation_note",
    schema_version=SCHEMA_ID,
)


def wire(message: dict) -> bytes:
    return msgpack.packb(message, use_bin_type=True)


class RudpServerGenerationTests(unittest.TestCase):
    def setUp(self) -> None:
        node = create_node(runtime_id="rudp-generation")
        node.database.register_document(NOTE)
        self.server = CultMeshLocalServer(node=node, rudp_connection_id=CONNECTION_ID)
        self.server.start()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.settimeout(0.02)
        self.target = ("127.0.0.1", self.server.port)
        # Every schema data packet the server sent, by sequence, whether or not the receiving
        # session delivered it: a new server-side session numbers its packets afresh.
        self.sent: dict[int, str] = {}

    def tearDown(self) -> None:
        self.sock.close()
        self.server.stop()

    def to_server(self, packet) -> None:
        self.sock.sendto(encode_rudp_packet(packet), self.target)

    def pump(self, session: CultNetRudpSession, until, seconds: float = 2.0) -> bool:
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if until():
                return True
            try:
                data, _ = self.sock.recvfrom(65535)
            except TimeoutError:
                continue
            packet = decode_rudp_packet(data)
            session.receive(packet, int(time.monotonic() * 1000))
            if packet.reliable or packet.packet_type == CultNetRudpPacketType.ACCEPT:
                self.to_server(session.create_ack_for_received(packet.sequence))
            if packet.packet_type == CultNetRudpPacketType.DATA and packet.channel_id == "schema":
                self.sent[packet.sequence] = msgpack.unpackb(packet.payload, raw=False).get("schemaVersion")
        return until()

    def count(self, schema_version: str) -> int:
        return sum(1 for value in self.sent.values() if value == schema_version)

    def new_session(self, initial_sequence: int) -> CultNetRudpSession:
        return CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=initial_sequence))

    def test_a_retransmitted_connect_keeps_the_peer_and_a_restarted_client_is_a_new_one(self) -> None:
        first = self.new_session(50)
        connect = first.create_connect(0, b"same")
        self.to_server(connect)
        self.assertTrue(self.pump(first, lambda: first.connected), "the first Accept")
        hello = first.send(
            "schema", wire({"schemaVersion": "cultnet.hello.v0", "runtimeId": "probe", "runtimeKind": "test"}), RELIABLE
        )
        self.to_server(hello)
        self.assertTrue(self.pump(first, lambda: self.count("cultnet.hello.v0") == 1), "the first answer")
        self.to_server(
            first.send(
                "schema",
                wire({
                    "schemaVersion": "cultnet.database_subscribe.v0",
                    "messageId": "subscribe",
                    "subscriptionId": "old-incarnation",
                    "schemaIds": [SCHEMA_ID],
                    "includeSnapshot": False,
                }),
                RELIABLE,
            )
        )
        self.pump(first, lambda: False, 0.2)
        first_put = document_put_raw(
            message_id="first-put",
            key="note:a",
            schema_id=SCHEMA_ID,
            stored_at="2026-09-30T00:00:00Z",
            payload=NOTE.encode_payload(Note("first")),
        )
        self.to_server(first.send("schema", first_put.to_bytes(), RELIABLE))
        self.assertTrue(
            self.pump(first, lambda: self.count("cultnet.database_change_raw.v0") == 1),
            "the subscription did not notify its own connection",
        )

        # A retransmitted Connect repeats: the data already delivered is not delivered, or answered, again.
        self.to_server(connect)
        self.to_server(hello)
        self.pump(first, lambda: False, 0.4)
        self.assertEqual(self.count("cultnet.hello.v0"), 1, "the retransmitted Connect reset the peer")

        # The same client restarted on the same address, connection id and payload: a new peer.
        second = self.new_session(7)
        self.to_server(second.create_connect(0, b"same"))
        self.assertTrue(self.pump(second, lambda: second.connected), "the restarted client's Accept")
        self.to_server(
            second.send(
                "schema", wire({"schemaVersion": "cultnet.hello.v0", "runtimeId": "probe", "runtimeKind": "test"}), RELIABLE
            )
        )
        self.assertTrue(self.pump(second, lambda: self.count("cultnet.hello.v0") == 2), "the restarted client's answer")

        # It inherited none of the old incarnation's subscriptions.
        put = document_put_raw(
            message_id="second-put",
            key="note:a",
            schema_id=SCHEMA_ID,
            stored_at="2026-09-30T00:00:01Z",
            payload=NOTE.encode_payload(Note("second")),
        )
        self.to_server(second.send("schema", put.to_bytes(), RELIABLE))
        self.pump(second, lambda: False, 0.4)
        self.assertEqual(self.count("cultnet.database_change_raw.v0"), 1, "the new peer inherited a subscription")


if __name__ == "__main__":
    unittest.main()
