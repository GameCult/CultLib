"""The CultMesh local server's RUDP loop asks each peer's session whether a Connect
repeats. A retransmitted Connect keeps the peer; a restarted client on the same
address is a new one."""
from __future__ import annotations

import socket
import time
import unittest

import msgpack
from cultmesh_py import create_node
from cultmesh_py.server import CultMeshLocalServer
from cultnet_py import (
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
)
from cultnet_py.transport import decode_rudp_packet, encode_rudp_packet

CONNECTION_ID = 0x10203091
HELLO = msgpack.packb({"schemaVersion": "cultnet.hello.v0", "runtimeId": "probe", "runtimeKind": "test"}, use_bin_type=True)


class RudpServerGenerationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.server = CultMeshLocalServer(node=create_node(runtime_id="rudp-generation"), rudp_connection_id=CONNECTION_ID)
        self.server.start()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.settimeout(0.02)
        self.target = ("127.0.0.1", self.server.port)
        self.responses = 0

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
                wire, _ = self.sock.recvfrom(65535)
            except TimeoutError:
                continue
            packet = decode_rudp_packet(wire)
            result = session.receive(packet, int(time.monotonic() * 1000))
            if packet.reliable or packet.packet_type.value == "accept":
                self.to_server(session.create_ack_for_received(packet.sequence))
            for frame in result.delivered:
                if msgpack.unpackb(frame.payload, raw=False).get("schemaVersion") == "cultnet.hello.v0":
                    self.responses += 1
        return until()

    def new_session(self, initial_sequence: int) -> CultNetRudpSession:
        return CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=initial_sequence))

    def test_a_retransmitted_connect_keeps_the_peer_and_a_restarted_client_is_admitted(self) -> None:
        first = self.new_session(50)
        connect = first.create_connect(0, b"same")
        self.to_server(connect)
        self.assertTrue(self.pump(first, lambda: first.connected), "the first Accept")
        hello = first.send("schema", HELLO, CultNetRudpSendOptions(reliable=True, ordered=True))
        self.to_server(hello)
        self.assertTrue(self.pump(first, lambda: self.responses == 1), "the first answer")

        # A retransmitted Connect is a repeat: the data already delivered is not delivered, or answered, again.
        self.to_server(connect)
        self.to_server(hello)
        self.pump(first, lambda: False, 0.4)
        self.assertEqual(self.responses, 1, "the retransmitted Connect reset the peer")

        # The same client restarted on the same address, connection id and payload.
        second = self.new_session(7)
        self.to_server(second.create_connect(0, b"same"))
        self.assertTrue(self.pump(second, lambda: second.connected), "the restarted client's Accept")
        self.to_server(second.send("schema", HELLO, CultNetRudpSendOptions(reliable=True, ordered=True)))
        self.assertTrue(self.pump(second, lambda: self.responses == 2), "the restarted client's answer")


if __name__ == "__main__":
    unittest.main()
