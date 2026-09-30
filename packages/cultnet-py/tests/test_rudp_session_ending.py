"""A write belongs to the session generation it was issued in.

Every way a generation ends drops the writes it still owed, and a flush
belongs to the generation it started in.
"""
from __future__ import annotations

import socket
import threading
import time
import unittest

from cultnet_py.transport import (
    CultNetRudpPacketType,
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    CultNetRudpSocketMode,
    CultNetRudpSocketTransportConnection,
    CultNetRudpSocketTransportOptions,
    decode_rudp_packet,
    encode_rudp_packet,
)

CONNECTION_ID = 0x10203070


def bind_udp_socket() -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(0.02)
    return sock


def client_with_a_lost_write() -> tuple[CultNetRudpSession, CultNetRudpSession]:
    client = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=1))
    server = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=500))
    client.receive(server.accept_connect(client.create_connect(0), 0), 0)
    client.send("schema", b"owed to the old session", CultNetRudpSendOptions(reliable=True, ordered=True))
    # A fragmented write larger than the send window leaves part of it queued.
    client.send_many(
        "schema", bytes(40 * 8), CultNetRudpSendOptions(reliable=True, ordered=True), max_fragment_bytes=8
    )
    assert client.outstanding_reliable_packet_count == 41
    assert client.queued_reliable_packet_count > 0
    return client, server


def drain(sock: socket.socket) -> list:
    packets = []
    while True:
        try:
            wire, _ = sock.recvfrom(65535)
        except TimeoutError:
            return packets
        packets.append(decode_rudp_packet(wire))


def connected_client() -> tuple[CultNetRudpSocketTransportConnection, socket.socket, tuple]:
    peer_socket = bind_udp_socket()
    client = CultNetRudpSocketTransportConnection(
        CultNetRudpSocketTransportOptions(
            runtime_id="python-rudp-client",
            socket=bind_udp_socket(),
            mode=CultNetRudpSocketMode.CLIENT,
            remote_addr=peer_socket.getsockname(),
            connection_id=CONNECTION_ID,
        )
    )
    client.connect(b"hello")
    wire, client_addr = peer_socket.recvfrom(65535)
    peer = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=500))
    accept = peer.accept_connect(decode_rudp_packet(wire), 0)
    peer_socket.sendto(encode_rudp_packet(accept), client_addr)
    client.receive_once()
    assert client.connected
    return client, peer_socket, client_addr


class CultNetRudpSessionEndingTests(unittest.TestCase):
    def test_every_way_a_session_ends_drops_the_writes_it_owed(self) -> None:
        endings = {
            "peer Disconnect": lambda client, server: client.receive(server.create_disconnect(b"bye"), 1),
            "local disconnect": lambda client, server: client.create_disconnect(b"bye"),
            "timeout": lambda client, server: self.assertTrue(client.check_timeout(1_000, 10)),
            "reset": lambda client, server: client.reset_peer_state(),
        }
        for name, end in endings.items():
            with self.subTest(ending=name):
                client, server = client_with_a_lost_write()
                end(client, server)
                self.assertEqual(client.outstanding_reliable_packet_count, 0, "the write survived the end")

                next_server = CultNetRudpSession(
                    CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=900)
                )
                next_server.accept_connect(client.create_connect(2_000), 2_000)
                for resend in client.due_resends(60_000):
                    self.assertEqual(
                        next_server.receive(resend, 60_000).delivered,
                        (),
                        "the old session's write was delivered in the next session",
                    )

    def test_a_flush_fails_after_a_local_disconnect_or_a_timeout(self) -> None:
        client, peer_socket, _ = connected_client()
        try:
            client.send("schema", b"owed")
            client.disconnect(b"bye")
            with self.assertRaises(ConnectionError):
                client.flush_reliable(0.2)
        finally:
            client.close()
            peer_socket.close()

        client, peer_socket, _ = connected_client()
        try:
            client.send("schema", b"owed")
            time.sleep(0.005)
            self.assertTrue(client.check_timeout(1))
            with self.assertRaises(ConnectionError):
                client.flush_reliable(0.2)
        finally:
            client.close()
            peer_socket.close()

    def test_a_flush_that_started_before_the_end_fails_even_after_a_later_connect(self) -> None:
        client, peer_socket, _ = connected_client()
        outcome: list[BaseException | None] = []

        def flush() -> None:
            try:
                client.flush_reliable(5.0)
                outcome.append(None)
            except BaseException as error:  # noqa: BLE001 - the outcome is the assertion
                outcome.append(error)

        try:
            client.send("schema", b"owed")
            worker = threading.Thread(target=flush)
            worker.start()
            time.sleep(0.05)
            client.disconnect(b"bye")
            client.connect(b"hello again")
            worker.join(10)
            self.assertEqual(len(outcome), 1)
            self.assertIsInstance(outcome[0], ConnectionError)
        finally:
            client.close()
            peer_socket.close()


    def test_a_flush_started_after_a_reconnect_waits_on_the_new_session(self) -> None:
        client, peer_socket, client_addr = connected_client()
        try:
            client.send("schema", b"owed to the old session")
            client.disconnect(b"bye")
            client.connect(b"hello again")
            connect = [p for p in drain(peer_socket) if p.packet_type == CultNetRudpPacketType.CONNECT][-1]
            accept = CultNetRudpSession(
                CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=700)
            ).accept_connect(connect, 0)
            peer_socket.sendto(encode_rudp_packet(accept), client_addr)
            client.receive_once()

            client.flush_reliable(0.5)
        finally:
            client.close()
            peer_socket.close()

    def test_a_flush_started_after_the_server_accepts_a_new_connect_waits_on_the_new_session(self) -> None:
        server_socket = bind_udp_socket()
        peer_socket = bind_udp_socket()
        server = CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id="python-rudp-server",
                socket=server_socket,
                mode=CultNetRudpSocketMode.SERVER,
                connection_id=CONNECTION_ID,
            )
        )
        to_server = lambda packet: peer_socket.sendto(encode_rudp_packet(packet), server_socket.getsockname())
        try:
            first = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID))
            to_server(first.create_connect(0))
            server.receive_once()
            to_server(first.create_disconnect())
            server.receive_once()

            following = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID))
            to_server(following.create_connect(0))
            server.receive_once()
            accept = [p for p in drain(peer_socket) if p.packet_type == CultNetRudpPacketType.ACCEPT][-1]
            following.receive(accept, 0)
            to_server(following.create_ack())
            server.receive_once()

            server.flush_reliable(0.5)
        finally:
            server.close()
            peer_socket.close()


    def test_an_ending_does_not_forget_what_was_received_from_the_peer(self) -> None:
        client = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=1))
        server = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=500))
        client.receive(server.accept_connect(client.create_connect(0), 0), 0)
        ordered = CultNetRudpSendOptions(reliable=True, ordered=True)
        s1 = client.send("schema", b"s1", ordered)
        s2 = client.send("schema", b"s2", ordered)
        self.assertEqual(server.receive(s2, 1).delivered, (), "s1 is missing, so s2 is held")

        self.assertTrue(server.check_timeout(1_000, 10))
        self.assertEqual(
            [frame.payload for frame in server.receive(s1, 1_001).delivered],
            [b"s1", b"s2"],
            "the ending forgot the held frame",
        )
        self.assertEqual(server.receive(s2, 1_002).delivered, (), "the ending forgot what was received")


if __name__ == "__main__":
    unittest.main()
