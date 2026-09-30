"""A Connect's sequence says whether it repeats the one a session accepted, and
the handshake alone seeds the watermark that orders delivery."""
from __future__ import annotations

import unittest
from dataclasses import replace

from cultnet_py import random_initial_sequence
from cultnet_py.transport import (
    CultNetRudpPacketType,
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    CultNetRudpSocketMode,
    CultNetRudpSocketTransportOptions,
)

CONNECTION_ID = 0x10203090


def session(initial_sequence: int) -> CultNetRudpSession:
    return CultNetRudpSession(CultNetRudpSessionOptions(connection_id=CONNECTION_ID, initial_sequence=initial_sequence))


def send(from_session: CultNetRudpSession, text: str, *, ordered: bool = True, channel: str = "schema"):
    return from_session.send(channel, text.encode(), CultNetRudpSendOptions(reliable=True, ordered=ordered))


def names(result) -> list[str]:
    return [frame.payload.decode() for frame in result.delivered]


def handshake(client: CultNetRudpSession, server: CultNetRudpSession):
    connect = client.create_connect(0)
    accept = server.accept_connect(connect, 0)
    client.receive(accept, 0)
    assert client.connected and server.connected
    return connect, accept


class CultNetRudpConnectGenerationTests(unittest.TestCase):
    def test_a_reconnect_on_the_same_session_is_not_stranded_by_a_write_the_ended_session_owed(self) -> None:
        client, server = session(1), session(500)
        handshake(client, server)
        send(server, "lost")
        server.create_disconnect(b"gone")

        client.receive(server.accept_connect(client.create_connect(1), 1), 1)
        self.assertEqual(names(client.receive(send(server, "after"), 2)), ["after"])

    def test_a_server_that_accepts_a_new_client_forgets_the_old_clients_sequences(self) -> None:
        old_client, server = session(50), session(500)
        handshake(old_client, server)
        for name in ("c1", "c2", "c3"):
            self.assertEqual(names(server.receive(send(old_client, name), 1)), [name])
        server.create_disconnect(b"restart")

        new_client = session(2)
        new_client.receive(server.accept_connect(new_client.create_connect(2), 2), 2)
        self.assertEqual(names(server.receive(send(new_client, "fresh"), 3)), ["fresh"])

    def test_a_retransmitted_connect_after_data_has_flowed_does_not_reset_the_session(self) -> None:
        client, server = session(1), session(500)
        connect, _ = handshake(client, server)
        frame = send(client, "once")
        self.assertEqual(names(server.receive(frame, 1)), ["once"])

        self.assertTrue(server.connect_repeats(connect))
        self.assertEqual(server.accept_connect(connect, 2).packet_type, CultNetRudpPacketType.ACK)
        self.assertEqual(server.outstanding_reliable_packet_count, 0, "a repeat queued something")
        self.assertEqual(
            names(server.receive(frame, 3)), [], "a retransmitted Connect made the server forget what it had delivered"
        )

    def test_a_connect_with_another_sequence_is_not_a_repeat_whatever_it_carries(self) -> None:
        server = session(500)
        connect = session(1).create_connect(0, b"same payload")
        server.accept_connect(connect, 0)
        self.assertTrue(server.connect_repeats(connect))

        self.assertFalse(server.connect_repeats(session(9).create_connect(0, b"same payload")))
        self.assertFalse(server.connect_repeats(replace(connect, packet_type=CultNetRudpPacketType.PING)))
        server.create_disconnect()
        self.assertFalse(server.connect_repeats(connect), "an ended session repeats nothing")

    def test_an_accept_that_does_not_name_the_pending_connect_is_ignored(self) -> None:
        client, server = session(10), session(500)
        connect = client.create_connect(0)
        accept = server.accept_connect(connect, 0)

        client.receive(replace(accept, ack=connect.sequence + 50, ack_mask=0), 1)
        self.assertFalse(client.connected, "a stale Accept connected the session")
        self.assertEqual(client.pending_reliable_sequences, (connect.sequence,))

        client.receive(replace(accept, ack=connect.sequence + 3, ack_mask=1 << 2), 2)
        self.assertTrue(client.connected, "the mask names the Connect")

    def test_an_accept_after_a_local_disconnect_does_not_reconnect_the_session(self) -> None:
        client, server = session(1), session(500)
        _, accept = handshake(client, server)
        client.create_disconnect(b"bye")
        self.assertFalse(client.connected)

        client.receive(accept, 5)
        self.assertFalse(client.connected, "a late Accept revived an ended session")

    def test_a_reconnect_forgets_what_the_old_server_sent(self) -> None:
        client, old_server = session(1), session(500)
        handshake(client, old_server)
        for name in ("d1", "d2", "d3"):
            self.assertEqual(names(client.receive(send(old_server, name), 1)), [name])

        new_server = session(501)
        client.receive(new_server.accept_connect(client.create_connect(2), 2), 2)
        self.assertEqual(names(client.receive(send(new_server, "fresh"), 3)), ["fresh"])

    def test_an_accept_for_an_abandoned_connect_does_not_connect_the_session(self) -> None:
        client, server = session(1), session(500)
        accept = server.accept_connect(client.create_connect(0), 0)
        client.create_disconnect(b"never mind")

        client.receive(accept, 1)
        self.assertFalse(client.connected, "an Accept revived an abandoned Connect")

    def test_a_full_queue_owed_to_a_vanished_peer_does_not_refuse_a_new_generation(self) -> None:
        def bounded(initial_sequence: int) -> CultNetRudpSession:
            return CultNetRudpSession(
                CultNetRudpSessionOptions(
                    connection_id=CONNECTION_ID, initial_sequence=initial_sequence, max_pending_reliable_packets=1
                )
            )

        server = bounded(500)
        server.accept_connect(session(1).create_connect(0), 0)
        self.assertEqual(server.outstanding_reliable_packet_count, 1, "the Accept is owed and never acknowledged")
        following = session(9).create_connect(1)
        server.accept_connect(following, 1)
        self.assertTrue(server.connect_repeats(following))

        client = bounded(1)
        client.create_connect(0)
        client.create_connect(1)
        self.assertEqual(client.outstanding_reliable_packet_count, 1, "only the new Connect is owed")

    def test_a_duplicate_accept_does_not_reseed_the_watermark(self) -> None:
        client, server = session(1), session(500)
        _, accept = handshake(client, server)
        s1, s2 = send(server, "s1"), send(server, "s2")
        client.receive(replace(accept, sequence=s2.sequence), 1)
        self.assertEqual(names(client.receive(s2, 2)), [], "s1 is still missing")
        self.assertEqual(names(client.receive(s1, 3)), ["s1", "s2"])

    def test_reliable_data_before_the_accept_is_neither_delivered_nor_acknowledged(self) -> None:
        client, server = session(1), session(500)
        accept = server.accept_connect(client.create_connect(0), 0)
        a, b = send(server, "A"), send(server, "B")

        for early in (b, a):
            self.assertEqual(names(client.receive(early, 1)), [])
            ack = client.create_ack_for_received(early.sequence)
            self.assertEqual((ack.ack, ack.ack_mask), (0, 0), "an unhandled packet was acknowledged")
        client.receive(accept, 2)
        self.assertTrue(client.connected)
        self.assertEqual(server.outstanding_reliable_packet_count, 3, "nothing was acknowledged")

        self.assertEqual(names(client.receive(a, 3)), ["A"])
        self.assertEqual(names(client.receive(b, 3)), ["B"])

    def test_ordered_frames_after_the_accept_are_delivered_in_order_whatever_arrives_first(self) -> None:
        client, server = session(1), session(500)
        handshake(client, server)
        a, b = send(server, "A"), send(server, "B")
        self.assertEqual(names(client.receive(b, 1)), [])
        self.assertEqual(names(client.receive(a, 2)), ["A", "B"])

    def test_an_unordered_reliable_frame_is_delivered_on_receipt_never_held_behind_a_gap(self) -> None:
        client, server = session(1), session(500)
        handshake(client, server)
        send(server, "lost")
        unordered = send(server, "now", ordered=False, channel="media")
        ordered = send(server, "later")
        self.assertEqual(names(client.receive(unordered, 1)), ["now"])
        self.assertEqual(names(client.receive(ordered, 2)), [], "the ordered frame waits for the gap")

    def test_a_peer_disconnect_does_not_forget_what_the_peer_sent(self) -> None:
        client, server = session(1), session(500)
        handshake(client, server)
        s1, s2 = send(client, "s1"), send(client, "s2")
        self.assertEqual(names(server.receive(s2, 1)), [], "s1 is missing, so s2 is held")

        server.receive(client.create_disconnect(b"bye"), 2)
        self.assertEqual(names(server.receive(s1, 3)), ["s1", "s2"], "the Disconnect forgot the held frame")
        self.assertEqual(names(server.receive(s2, 4)), [], "the Disconnect forgot what was received")

    def test_default_initial_sequences_are_drawn_at_random_from_one_to_two_to_the_thirty_first(self) -> None:
        def assert_random(label: str, draw) -> None:
            draws = {draw() for _ in range(64)}
            self.assertGreater(len(draws), 32, f"{label} does not draw at random")
            self.assertTrue(all(1 <= value < 2**31 for value in draws), f"{label} left [1, 2^31)")

        assert_random("random_initial_sequence", random_initial_sequence)
        assert_random("session options", lambda: CultNetRudpSessionOptions(connection_id=1).initial_sequence)
        assert_random(
            "transport options",
            lambda: CultNetRudpSocketTransportOptions(
                runtime_id="r", socket=None, mode=CultNetRudpSocketMode.CLIENT, connection_id=1
            ).initial_sequence,
        )
        assert_random(
            "a default session's first Connect",
            lambda: CultNetRudpSession(CultNetRudpSessionOptions(connection_id=1)).create_connect(0).sequence,
        )


if __name__ == "__main__":
    unittest.main()
