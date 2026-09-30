"""A Connect's sequence says whether it repeats the one a session accepted, and
the handshake alone seeds the watermark that orders delivery."""
from __future__ import annotations

import unittest
from dataclasses import replace

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

    def test_sessions_from_one_options_object_each_draw_their_own_initial_sequence(self) -> None:
        self.assertIsNone(CultNetRudpSessionOptions(connection_id=1).initial_sequence)
        self.assertIsNone(
            CultNetRudpSocketTransportOptions(
                runtime_id="r", socket=None, mode=CultNetRudpSocketMode.CLIENT, connection_id=1
            ).initial_sequence
        )
        options = CultNetRudpSessionOptions(connection_id=1)
        firsts = {CultNetRudpSession(options).create_connect(0).sequence for _ in range(64)}
        self.assertGreater(len(firsts), 32, "a default session does not draw its own sequence")
        self.assertTrue(all(1 <= value < 2**31 for value in firsts), "a draw left [1, 2^31)")

    # Ack Cut 1d: a frame from an earlier generation is a duplicate, a delayed
    # Connect from an earlier attempt is stale, a Connect from a new endpoint is
    # a new client.

    @staticmethod
    def names_sequence(ack, sequence: int) -> bool:
        return ack.ack == sequence or any(
            ack.ack_mask & (1 << bit) and ack.ack > bit and ack.ack - bit - 1 == sequence for bit in range(32)
        )

    def test_a_frame_from_the_clients_earlier_generation_is_acknowledged_not_delivered_again(self) -> None:
        client, server = session(10), session(500)
        handshake(client, server)
        old = send(client, "old")
        self.assertEqual(names(server.receive(old, 1)), ["old"])

        connect = client.create_connect(2)
        client.receive(server.accept_connect(connect, 2), 2)
        self.assertLess(old.sequence, connect.sequence)
        self.assertEqual(names(server.receive(old, 3)), [], "an earlier generation's frame was delivered again")
        self.assertTrue(self.names_sequence(server.create_ack_for_received(old.sequence), old.sequence))
        self.assertEqual(names(server.receive(send(client, "fresh"), 4)), ["fresh"])

    def test_a_frame_from_the_servers_earlier_generation_is_acknowledged_not_delivered_again(self) -> None:
        client, server = session(10), session(500)
        handshake(client, server)
        old = send(server, "old")
        self.assertEqual(names(client.receive(old, 1)), ["old"])

        client.receive(server.accept_connect(client.create_connect(2), 2), 2)
        self.assertEqual(names(client.receive(old, 3)), [], "an earlier generation's frame was delivered again")
        self.assertTrue(self.names_sequence(client.create_ack_for_received(old.sequence), old.sequence))
        self.assertEqual(names(client.receive(send(server, "fresh"), 4)), ["fresh"])

    def test_a_duplicate_below_the_receive_window_is_acknowledged_by_name(self) -> None:
        client, server = session(10), session(500)
        handshake(client, server)
        first = send(client, "first")
        self.assertEqual(names(server.receive(first, 1)), ["first"])
        for _ in range(4_200):
            server.receive(send(client, "x"), 1)
            client.receive(server.create_ack(), 1)
        self.assertEqual(names(server.receive(first, 2)), [])
        self.assertEqual(
            server.create_ack_for_received(first.sequence).ack,
            first.sequence,
            "a duplicate below the window was not acknowledged by name",
        )

    def test_a_delayed_connect_from_an_earlier_attempt_does_not_strand_the_client(self) -> None:
        client, server = session(10), session(500)
        earlier = client.create_connect(0)
        retried = client.create_connect(300)
        accept = server.accept_connect(retried, 301)
        client.receive(accept, 302)
        server.receive(client.create_ack_for_received(accept.sequence), 302)
        self.assertTrue(client.connected)
        a = send(client, "a")
        self.assertEqual(names(server.receive(a, 303)), ["a"])
        client.receive(server.create_ack_for_received(a.sequence), 303)

        self.assertTrue(server.connect_repeats(earlier), "the delayed Connect starts nothing")
        reply = server.accept_connect(earlier, 304)
        self.assertEqual(reply.packet_type, CultNetRudpPacketType.ACK)
        client.receive(reply, 305)
        self.assertTrue(client.connected)

        b = send(client, "b")
        self.assertEqual(names(server.receive(b, 306)), ["b"], "the delayed Connect reset the server")
        client.receive(server.create_ack_for_received(b.sequence), 307)
        self.assertNotIn(b.sequence, client.pending_reliable_sequences)

    def test_a_repeated_connect_refreshes_liveness_and_a_stale_one_does_not(self) -> None:
        client, server = session(10), session(500)
        earlier = client.create_connect(0)
        current = client.create_connect(1)
        server.accept_connect(current, 0)

        server.accept_connect(current, 900)
        self.assertFalse(server.check_timeout(1_000, 500), "a repeated Connect did not refresh liveness")

        server.accept_connect(earlier, 1_400)
        self.assertTrue(server.check_timeout(1_600, 500), "a stale Connect refreshed liveness")

    def test_stale_connects_are_recognised_by_serial_arithmetic(self) -> None:
        server = session(500)
        server.accept_connect(session(3).create_connect(0), 0)

        def connect(sequence: int):
            return session(sequence).create_connect(0)

        self.assertTrue(server.connect_repeats(connect(2**32 - 2)), "just before the wrap")
        self.assertFalse(server.connect_repeats(connect(4)), "just after")
        self.assertFalse(server.connect_repeats(connect(3 + 4_096)), "far after")
        self.assertTrue(server.connect_repeats(connect(2**32 - 4_092)), "the window's edge")
        self.assertFalse(server.connect_repeats(connect(2**32 - 4_093)), "past the window")

    def test_a_restarted_client_whose_first_sequence_is_stale_connects_after_redrawing(self) -> None:
        server, old = session(500), session(1_000)
        old_accept = server.accept_connect(old.create_connect(0), 0)
        old.receive(old_accept, 0)

        restarted = session(900)
        first = restarted.create_connect(0, b"join")
        # The old client has not acknowledged its Accept yet, so that is the
        # reply: it names the old Connect, not this one.
        early = server.accept_connect(first, 1)
        self.assertEqual(early.packet_type, CultNetRudpPacketType.ACCEPT)
        restarted.receive(early, 1)
        self.assertFalse(restarted.connected, "an Accept for another Connect connected the client")
        server.receive(old.create_ack_for_received(old_accept.sequence), 1)
        reply = server.accept_connect(first, 2)
        self.assertEqual(reply.packet_type, CultNetRudpPacketType.ACK)
        restarted.receive(reply, 2)
        self.assertFalse(restarted.connected)

        retransmitted = restarted.due_resends(1_000)
        self.assertEqual(len(retransmitted), 1)
        self.assertEqual(retransmitted[0].sequence, first.sequence, "the attempt is still young")

        fresh = restarted.due_resends(3_500)
        self.assertEqual(len(fresh), 1)
        self.assertEqual(fresh[0].packet_type, CultNetRudpPacketType.CONNECT)
        self.assertNotEqual(fresh[0].sequence, first.sequence, "the same Connect was retransmitted for ever")
        self.assertEqual(fresh[0].payload, b"join", "the fresh attempt lost the Connect payload")

        accept = server.accept_connect(fresh[0], 3_500)
        self.assertEqual(accept.packet_type, CultNetRudpPacketType.ACCEPT)
        restarted.receive(accept, 3_500)
        self.assertTrue(restarted.connected)
        self.assertEqual(names(server.receive(send(restarted, "hello"), 3_501)), ["hello"])

    def test_an_answered_connect_is_never_replaced_by_the_attempt_timeout(self) -> None:
        client, server = session(10), session(500)
        connect, _ = handshake(client, server)
        resent = client.due_resends(60_000)
        self.assertTrue(
            all(p.packet_type != CultNetRudpPacketType.CONNECT or p.sequence == connect.sequence for p in resent),
            "a connected client started a new attempt",
        )
        self.assertTrue(client.connected)

    def test_an_accepted_peer_that_goes_silent_times_out(self) -> None:
        server = session(500)
        server.accept_connect(session(10).create_connect(0), 0)
        self.assertTrue(server.check_timeout(100_000, 1_000))


if __name__ == "__main__":
    unittest.main()
