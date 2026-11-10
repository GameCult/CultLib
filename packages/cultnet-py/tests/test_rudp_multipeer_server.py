"""One UDP socket serves many peers, each in its own session, as the C# reference does.

Sockets bind port 0, so no test races another for a port.
"""
from __future__ import annotations

import errno
import socket
import time
import unittest
from pathlib import Path

from cultnet_py.rudp_server import (
    CultNetRudpSocketServerOptions,
    CultNetRudpSocketTransportServer,
)
from cultnet_py.transport import (
    CultNetRudpPacketType,
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    decode_rudp_packet,
    encode_rudp_packet,
)

CONNECTION_ID = 0x43554C54
VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "rudp-multipeer-server" / "transcript.txt"


def bind() -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(0.03)
    return sock


def make_server(**kwargs) -> CultNetRudpSocketTransportServer:
    options = dict(runtime_id="py-test", socket=bind(), connection_id=CONNECTION_ID, resend_delay_ms=5)
    options.update(kwargs)
    return CultNetRudpSocketTransportServer(CultNetRudpSocketServerOptions(**options))


def pump(server: CultNetRudpSocketTransportServer) -> list:
    """Consumes datagrams until the socket is idle, returning the frames delivered."""
    frames = []
    while True:
        progressed, frame = server.try_receive_once()
        if frame is not None:
            frames.append(frame)
        if not progressed:
            return frames


def drain(sock: socket.socket) -> list[bytes]:
    wires = []
    while True:
        try:
            wires.append(sock.recvfrom(65535)[0])
        except TimeoutError:
            return wires


class Client:
    def __init__(self, server: CultNetRudpSocketTransportServer, initial_sequence: int) -> None:
        self.server = server
        self.socket = bind()
        self.session = CultNetRudpSession(
            CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=initial_sequence)
        )

    @property
    def addr(self) -> tuple:
        return self.socket.getsockname()

    def transmit(self, packet) -> None:
        self.socket.sendto(encode_rudp_packet(packet), self.server.socket.getsockname())

    def connect(self, payload: bytes = b"") -> None:
        self.transmit(self.session.create_connect(0, payload))
        pump(self.server)
        self.receive()

    def receive(self) -> list:
        packets = [decode_rudp_packet(wire) for wire in drain(self.socket)]
        for packet in packets:
            if self.session.connected or packet.packet_type == CultNetRudpPacketType.ACCEPT:
                self.session.receive(packet, 0)
        return packets

    def write(self, text: str) -> None:
        self.transmit(
            self.session.send("schema", text.encode(), CultNetRudpSendOptions(reliable=True, ordered=True))
        )

    def peer(self):
        return next(p for p in self.server.peers if p.remote_addr == self.addr)

    def close(self) -> None:
        self.socket.close()


class MultiPeerServerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.server = make_server(initial_sequence=500, accept_payload=b"acc")
        self.addCleanup(self.server.close)
        self.clients: list[Client] = []

    def client(self, initial_sequence: int) -> Client:
        client = Client(self.server, initial_sequence)
        self.clients.append(client)
        self.addCleanup(client.close)
        return client

    def test_interleaved_peers_each_get_their_own_session_and_frames(self) -> None:
        x, y = self.client(10), self.client(20)
        x.connect(b"hx")
        y.connect(b"hy")
        self.assertEqual(len(self.server.peers), 2)
        self.assertEqual(x.peer().connect_payload, b"hx")
        self.assertEqual(y.peer().connect_payload, b"hy")

        x.write("x1")
        y.write("y1")
        x.write("x2")
        y.write("y2")
        frames = pump(self.server)
        by_peer = {}
        for item in frames:
            by_peer.setdefault(item.peer.remote_addr, []).append(item.frame.payload)
        self.assertEqual(by_peer, {x.addr: [b"x1", b"x2"], y.addr: [b"y1", b"y2"]})

        self.server.send(x.peer(), "schema", b"to-x")
        self.server.send(y.peer(), "schema", b"to-y")
        for client, expected in ((x, b"to-x"), (y, b"to-y")):
            data = [p for p in client.receive() if p.packet_type == CultNetRudpPacketType.DATA]
            self.assertEqual([p.payload for p in data], [expected])

    def test_one_peer_ending_does_not_disturb_the_other(self) -> None:
        x, y = self.client(10), self.client(20)
        x.connect()
        y.connect()
        y_peer = y.peer()
        ended = []
        self.server.peer_disconnected.append(ended.append)

        x.transmit(x.session.create_disconnect(b"bye"))
        pump(self.server)
        self.assertEqual(len(self.server.peers), 1)
        self.assertEqual([p.disconnect_reason for p in ended], [b"bye"])
        self.assertIs(y.peer(), y_peer)

        # Datagrams from the ended endpoint belong to no session.
        before = self.server.stats.packets_dropped
        x_late = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=99))
        x_late.accept_connect(x.session.create_connect(0), 0)
        x.transmit(x_late.send("schema", b"late", CultNetRudpSendOptions(reliable=True, ordered=True)))
        pump(self.server)
        self.assertEqual(self.server.stats.packets_dropped, before + 1)

        y.write("still here")
        self.assertEqual([f.frame.payload for f in pump(self.server)], [b"still here"])
        self.server.send(y_peer, "schema", b"reply")
        self.assertEqual(
            [p.payload for p in y.receive() if p.packet_type == CultNetRudpPacketType.DATA], [b"reply"]
        )

    def test_a_new_connect_from_the_same_endpoint_replaces_only_that_peer(self) -> None:
        x, y = self.client(10), self.client(20)
        x.connect()
        y.connect()
        old_y = y.peer()
        ended = []
        self.server.peer_disconnected.append(ended.append)

        y.session = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=30))
        y.connect(b"again")
        self.assertEqual(ended, [old_y])
        self.assertEqual(old_y.disconnect_reason, b"replaced by a new Connect generation")
        self.assertIsNot(y.peer(), old_y)
        self.assertEqual(y.peer().connect_payload, b"again")
        with self.assertRaises(ConnectionError):
            self.server.send(old_y, "schema", b"to the old session")
        self.assertEqual(len(self.server.peers), 2)

    def test_a_repeated_connect_is_answered_with_the_owed_accept_and_starts_nothing(self) -> None:
        x = self.client(10)
        connect = x.session.create_connect(0, b"h")
        x.transmit(connect)
        pump(self.server)
        first = x.peer()
        x.transmit(connect)
        pump(self.server)
        self.assertIs(x.peer(), first)
        accepts = [p for p in x.receive() if p.packet_type == CultNetRudpPacketType.ACCEPT]
        self.assertEqual(len(accepts), 2)
        self.assertEqual(accepts[0].sequence, accepts[1].sequence)

    def test_strays_are_dropped_and_counted(self) -> None:
        x = self.client(10)
        x.socket.sendto(b"not rudp", self.server.socket.getsockname())
        other = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID + 1, initial_sequence=1))
        x.transmit(other.create_connect(0))
        x.transmit(
            CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=1)).create_ping()
        )
        pump(self.server)
        self.assertEqual(self.server.stats.packets_dropped, 3)
        self.assertEqual(self.server.peers, ())

    def test_resends_go_only_to_peers_with_unacknowledged_writes(self) -> None:
        x, y = self.client(10), self.client(20)
        x.connect()
        y.connect()
        self.server.send(x.peer(), "schema", b"owed")
        x.receive()  # the first transmission is lost to the test: x never acks
        y.receive()
        time.sleep(0.02)
        self.server.poll_resends()
        x_data = [p for p in x.receive() if p.packet_type == CultNetRudpPacketType.DATA]
        self.assertEqual([p.payload for p in x_data], [b"owed"])
        self.assertEqual([p for p in y.receive() if p.packet_type == CultNetRudpPacketType.DATA], [])

    def test_broadcast_reaches_every_peer(self) -> None:
        x, y = self.client(10), self.client(20)
        x.connect()
        y.connect()
        self.assertEqual(self.server.broadcast("schema", b"all"), [])
        for client in (x, y):
            self.assertEqual(
                [p.payload for p in client.receive() if p.packet_type == CultNetRudpPacketType.DATA], [b"all"]
            )

    def test_an_application_ending_a_session_tells_the_peer_and_drops_its_queued_frames(self) -> None:
        x, y = self.client(10), self.client(20)
        x.connect()
        y.connect()
        x.write("x1")
        y.write("y1")
        # Both frames are decoded into the server's queue; ending x discards x's.
        progressed, first = self.server.try_receive_once()
        self.assertTrue(progressed)
        pump_rest = pump(self.server)
        frames = [first] + pump_rest if first else pump_rest
        peer_x = x.peer()
        self.assertTrue(self.server.end_peer_session(peer_x, b"server could not handle a message"))
        self.assertFalse(self.server.end_peer_session(peer_x, b"again"), "an ended session owes no second goodbye")
        goodbye = [p for p in x.receive() if p.packet_type == CultNetRudpPacketType.DISCONNECT]
        self.assertEqual([p.payload for p in goodbye], [b"server could not handle a message"])
        self.assertEqual(len(self.server.peers), 1)
        self.assertEqual({f.peer.remote_addr for f in frames}, {x.addr, y.addr})

    def test_a_datagram_too_large_to_send_is_that_callers_error_and_queues_nothing(self) -> None:
        x = self.client(10)
        x.connect()
        peer = x.peer()
        owed = peer.pending_reliable_packet_count  # the Accept, not yet acknowledged
        with self.assertRaises(OSError) as raised:
            self.server.send(peer, "schema", bytes(70_000))
        self.assertEqual(raised.exception.errno, errno.EMSGSIZE)
        self.assertEqual(peer.pending_reliable_packet_count, owed)
        self.assertEqual(len(self.server.peers), 1)
        # The refused write left no gap in the sequence space the peer is waiting on.
        self.server.send(peer, "schema", b"fine")
        data = [p for p in x.receive() if p.packet_type == CultNetRudpPacketType.DATA]
        self.assertEqual([(p.sequence, p.payload) for p in data], [(501, b"fine")])


class UnsendableAfterFirstFragmentSocket:
    """Delegates to a real socket; fails permanently once its budget of sends to one address is spent."""

    def __init__(self, real: socket.socket, addr: tuple, budget: int) -> None:
        self._real, self._addr, self._budget = real, addr, budget

    def sendto(self, wire: bytes, addr: tuple) -> int:
        if tuple(addr) == self._addr:
            if self._budget == 0:
                raise OSError(errno.EMSGSIZE, "Message too long")
            self._budget -= 1
        return self._real.sendto(wire, addr)

    def __getattr__(self, name: str):
        return getattr(self._real, name)


class UnsendableSessionTests(unittest.TestCase):
    def test_a_later_fragment_that_cannot_be_sent_ends_that_session_only(self) -> None:
        server = make_server(initial_sequence=500, max_fragment_bytes=8)
        self.addCleanup(server.close)
        x, y = Client(server, 10), Client(server, 20)
        self.addCleanup(x.close)
        self.addCleanup(y.close)
        x.connect()
        y.connect()
        x_peer = x.peer()
        server.socket = UnsendableAfterFirstFragmentSocket(server.socket, x.addr, budget=1)
        ended = []
        server.peer_disconnected.append(ended.append)

        with self.assertRaises(OSError):
            server.send(x_peer, "schema", bytes(24))
        self.assertEqual(ended, [x_peer])
        self.assertEqual(x_peer.disconnect_reason, b"packet could not be sent: EMSGSIZE")
        self.assertEqual(len(server.peers), 1)
        # The goodbye could not be sent either (same address), so the peer is only retired; y is unharmed.
        server.send(y.peer(), "schema", b"ok")
        self.assertEqual(
            [p.payload for p in y.receive() if p.packet_type == CultNetRudpPacketType.DATA], [b"ok"]
        )


class ReferenceTranscriptTests(unittest.TestCase):
    """Replays the datagrams the C# reference listener was captured answering, byte for byte."""

    def test_the_listener_answers_with_the_bytes_the_reference_does(self) -> None:
        server = make_server(
            connection_id=0x43554C54, initial_sequence=500, max_fragment_bytes=8, accept_payload=b"acc", resend_delay_ms=250
        )
        self.addCleanup(server.close)
        sockets = {label: bind() for label in ("X", "Y")}
        for sock in sockets.values():
            self.addCleanup(sock.close)
        target = server.socket.getsockname()

        steps: list[tuple[list[str], list[tuple[str, str]]]] = []
        for line in VECTORS.read_text().splitlines():
            if line.startswith("#") or not line.strip():
                continue
            fields = line.split(" ")
            if fields[0] == "out":
                steps[-1][1].append((fields[1], fields[2]))
            else:
                steps.append((fields, []))
        self.assertGreater(len(steps), 10)

        for action, expected in steps:
            if action[0] == "in":
                sockets[action[1]].sendto(bytes.fromhex(action[2]), target)
                while not server.try_receive_once()[0]:
                    time.sleep(0.001)
            else:
                peer = next(p for p in server.peers if p.remote_addr == sockets[action[1]].getsockname())
                server.send(peer, action[2], bytes.fromhex(action[3]))
            actual = [(label, wire.hex()) for label in ("X", "Y") for wire in drain(sockets[label])]
            self.assertEqual(actual, expected, f"after {' '.join(action[:2])}")
        self.assertEqual(len(server.peers), 1)
        self.assertEqual(server.stats.packets_dropped, 1)


if __name__ == "__main__":
    unittest.main()
