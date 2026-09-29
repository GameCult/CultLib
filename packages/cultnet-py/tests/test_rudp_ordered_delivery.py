"""Ordered delivery has one owner: the contiguous received-through watermark.

A frame is held for exactly one reason, a reliable sequence below it has not
arrived, and it is delivered in the call that fills the last such gap,
whichever channel the gap belonged to.
"""
from __future__ import annotations

import unittest

from cultnet_py.transport import (
    CultNetRudpPacket,
    CultNetRudpPacketType,
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
)


def handshake() -> tuple[CultNetRudpSession, CultNetRudpSession]:
    client = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=410, initial_sequence=1))
    server = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=410, initial_sequence=500))
    accept = server.accept_connect(client.create_connect(0), 0)
    client.receive(accept, 0)
    return client, server


def send(session: CultNetRudpSession, channel: str, payload: str, ordered: bool) -> CultNetRudpPacket:
    return session.send(channel, payload.encode(), CultNetRudpSendOptions(reliable=True, ordered=ordered))


def names(result) -> list[str]:
    return [frame.payload.decode() for frame in result.delivered]


class CultNetRudpOrderedDeliveryTests(unittest.TestCase):
    def test_ordered_frame_waits_for_a_gap_filled_by_another_channel(self) -> None:
        sender, receiver = handshake()
        o1 = send(sender, "schema", "o1", True)
        u = send(sender, "rel", "u", False)
        o2 = send(sender, "schema", "o2", True)
        o3 = send(sender, "schema", "o3", True)

        self.assertEqual(names(receiver.receive(o1, 1)), ["o1"])
        self.assertEqual(names(receiver.receive(o2, 2)), [], "u is missing")
        self.assertEqual(names(receiver.receive(o3, 3)), [], "u is missing")
        self.assertEqual(names(receiver.receive(u, 4)), ["u", "o2", "o3"])

    def test_two_ordered_channels_release_each_other_in_sequence_order(self) -> None:
        sender, receiver = handshake()
        a1 = send(sender, "schema", "A1", True)
        b1 = send(sender, "other", "B1", True)
        a2 = send(sender, "schema", "A2", True)
        a3 = send(sender, "schema", "A3", True)

        delivered = names(receiver.receive(a1, 1)) + names(receiver.receive(a2, 2)) + names(receiver.receive(a3, 3))
        self.assertEqual(delivered, ["A1"], "B1 is missing, so A2 and A3 wait")
        self.assertEqual(names(receiver.receive(b1, 4)), ["B1", "A2", "A3"])

    def test_channel_first_used_after_other_traffic_loses_nothing(self) -> None:
        sender, receiver = handshake()
        c1 = send(sender, "late", "C1", True)
        c2 = send(sender, "late", "C2", True)
        x = send(sender, "schema", "X", True)

        self.assertEqual(names(receiver.receive(x, 1)), [], "C1 is missing")
        self.assertEqual(names(receiver.receive(c2, 2)), [], "C1 is missing")
        self.assertEqual(names(receiver.receive(c1, 3)), ["C1", "C2", "X"])

    def test_accept_seeds_the_watermark_of_the_connecting_side(self) -> None:
        client, server = handshake()
        s1 = send(server, "schema", "s1", True)
        s2 = send(server, "schema", "s2", True)

        self.assertEqual(names(client.receive(s2, 1)), [])
        self.assertEqual(names(client.receive(s1, 2)), ["s1", "s2"])

    def test_connect_seeds_the_watermark_of_the_accepting_side(self) -> None:
        client, server = handshake()
        first = send(client, "schema", "first", True)
        second = send(client, "schema", "second", True)

        self.assertEqual(names(server.receive(second, 1)), [])
        self.assertEqual(names(server.receive(first, 2)), ["first", "second"])

    def test_duplicate_of_a_held_frame_is_not_delivered_twice(self) -> None:
        sender, receiver = handshake()
        s1 = send(sender, "schema", "s1", True)
        s2 = send(sender, "schema", "s2", True)

        self.assertEqual(names(receiver.receive(s2, 1)), [])
        self.assertEqual(names(receiver.receive(s2, 2)), [])
        self.assertEqual(names(receiver.receive(s1, 3)), ["s1", "s2"])
        self.assertEqual(names(receiver.receive(s2, 4)), [])

    def test_reset_peer_state_forgets_held_frames_and_the_watermark(self) -> None:
        sender, receiver = handshake()
        s1 = send(sender, "schema", "s1", True)
        s2 = send(sender, "schema", "s2", True)
        self.assertEqual(names(receiver.receive(s2, 1)), [])

        receiver.reset_peer_state()
        receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 410, 1, 0, 0, "control"), 2)

        self.assertEqual(names(receiver.receive(s1, 3)), ["s1"], "the held s2 died with the reset")


if __name__ == "__main__":
    unittest.main()
