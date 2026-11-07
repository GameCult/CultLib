"""Multi-peer RUDP listener: one UDP socket, one reliable session per remote endpoint.

Matches the C# `CultNetRudpSocketTransportServer` (the reference): a Connect opens a peer's
session, a Connect from the same endpoint that is not a repeat replaces only that peer, and
a peer that ends, is refused or cannot be answered never disturbs the others. The caller
drives it: `try_receive_once` consumes one datagram, `poll_resends` sends what is due.
Everything runs on the caller's thread, so no session needs a lock.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import socket

from .transport import (
    REFUSED_PACKET_REASON,
    CultNetRudpPacket,
    CultNetRudpPacketType,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    CultNetReconnectPolicy,
    CultNetTransportFrame,
    CultNetTransportStats,
    _channel_send_options,
    _now_ms,
    create_rudp_transport_profile,
    decode_rudp_packet,
    encode_rudp_packet,
    is_permanent_send_error,
)

REPLACED_PEER_REASON = b"replaced by a new Connect generation"


@dataclass
class CultNetRudpSocketServerOptions:
    runtime_id: str
    socket: socket.socket
    connection_id: int
    initial_sequence: int | None = None
    resend_delay_ms: int = 250
    transport_id: str = "rudp"
    max_payload_bytes: int | None = None
    max_fragment_bytes: int | None = None
    max_pending_reliable_packets: int | None = None
    reconnect_policy: CultNetReconnectPolicy | None = None
    accept_payload: bytes = b""


class CultNetRudpSocketServerPeer:
    """One remote endpoint's session. A peer that reconnects from the same endpoint is a new object."""

    def __init__(self, remote_addr: tuple, connect_payload: bytes, session: CultNetRudpSession) -> None:
        self.remote_addr = remote_addr
        self.connect_payload = connect_payload
        self.session = session
        # The last transport-level reason the session ended with, once it has.
        self.disconnect_reason: bytes | None = None

    @property
    def connected(self) -> bool:
        return self.session.connected

    @property
    def pending_reliable_packet_count(self) -> int:
        return self.session.outstanding_reliable_packet_count


@dataclass(frozen=True)
class CultNetRudpSocketServerFrame:
    peer: CultNetRudpSocketServerPeer
    frame: CultNetTransportFrame


def _remote_key(addr: tuple) -> tuple:
    return tuple(addr[:2])


class CultNetRudpSocketTransportServer:
    def __init__(self, options: CultNetRudpSocketServerOptions) -> None:
        if not options.runtime_id.strip():
            raise ValueError("Runtime id is required.")
        self.socket = options.socket
        self._options = options
        host, port = self.socket.getsockname()[:2]
        self.profile = create_rudp_transport_profile(
            options.runtime_id,
            transport_id=options.transport_id,
            host=host,
            port=port,
            max_payload_bytes=options.max_payload_bytes,
            max_fragment_bytes=options.max_fragment_bytes,
            max_pending_reliable_packets=options.max_pending_reliable_packets,
            reconnect_policy=options.reconnect_policy,
        )
        self._peers: dict[tuple, CultNetRudpSocketServerPeer] = {}
        self._delivered: deque[CultNetRudpSocketServerFrame] = deque()
        self._bytes_received = 0
        self._bytes_sent = 0
        self._frames_received = 0
        self._frames_sent = 0
        self._packets_dropped = 0
        self._send_failures = 0
        self._closed = False
        self.peer_disconnected: list = []  # callbacks taking the peer

    @property
    def peers(self) -> tuple[CultNetRudpSocketServerPeer, ...]:
        return tuple(self._peers.values())

    @property
    def stats(self) -> CultNetTransportStats:
        return CultNetTransportStats(
            bytes_received=self._bytes_received,
            bytes_sent=self._bytes_sent,
            frames_received=self._frames_received,
            frames_sent=self._frames_sent,
            packets_dropped=self._packets_dropped,
            send_failures=self._send_failures,
        )

    def send(self, peer: CultNetRudpSocketServerPeer, channel_id: str, payload: bytes) -> None:
        """Sends to one peer. Raises when that peer's session has ended or was replaced, or when a
        datagram can never be sent as built: the first fragment failing queues nothing, a later one
        ends the session, as the peer has seen part of the frame."""
        if self._peers.get(_remote_key(peer.remote_addr)) is not peer:
            raise ConnectionError("RUDP peer is not connected.")
        first_sequence = peer.session.next_sequence
        packets = peer.session.send_many(
            channel_id,
            payload,
            _channel_send_options(channel_id, _now_ms()),
            max_fragment_bytes=self._options.max_fragment_bytes,
        )
        for index, packet in enumerate(packets):
            error = self._send_packet(peer.remote_addr, packet)
            if error is None:
                continue
            if index == 0:
                peer.session.withdraw_send(first_sequence)
            else:
                self._end_unsendable_peer(peer, error)
            raise error
        self._frames_sent += 1

    def send_schema(self, peer: CultNetRudpSocketServerPeer, payload: bytes) -> None:
        self.send(peer, "schema", payload)

    def broadcast(self, channel_id: str, payload: bytes) -> list[CultNetRudpSocketServerPeer]:
        """Sends to every connected peer and returns the peers that could not be sent to; one
        peer's failure never stops the others."""
        failed = []
        for peer in self.peers:
            try:
                self.send(peer, channel_id, payload)
            except (OSError, ConnectionError, ValueError):
                failed.append(peer)
        return failed

    def disconnect(self, peer: CultNetRudpSocketServerPeer, reason: bytes = b"") -> bool:
        """Ends one peer's session, telling it why. False when the session had already ended."""
        return self.end_peer_session(peer, reason)

    def end_peer_session(self, peer: CultNetRudpSocketServerPeer, reason: bytes) -> bool:
        """Ends the session of a peer whose delivered frame the application could not decode or
        handle, with a goodbye carrying the application's reason. The frame is already
        acknowledged, so the session cannot be kept; the peer's frames still queued are not
        delivered. A session that already ended owes no goodbye, since the endpoint may belong
        to a new session; the call then returns False."""
        self._packets_dropped += 1
        if peer.session.ended:
            return False
        self._send_packet(peer.remote_addr, peer.session.end_with(reason))
        self._delivered = deque(item for item in self._delivered if item.peer is not peer)
        self._retire(peer, reason)
        return True

    def receive_once(self) -> CultNetRudpSocketServerFrame | None:
        return self.try_receive_once()[1]

    def try_receive_once(self) -> tuple[bool, CultNetRudpSocketServerFrame | None]:
        """Consumes one transport work item. Returns whether progress was made, independently of
        whether it completed an application frame."""
        if self._delivered:
            return True, self._delivered.popleft()
        try:
            wire, remote = self.socket.recvfrom(65535)
        except (TimeoutError, BlockingIOError, ConnectionResetError):
            return False, None
        self._bytes_received += len(wire)

        try:
            packet = decode_rudp_packet(wire)
        except ValueError:
            self._packets_dropped += 1
            return True, None
        if packet.connection_id != self._options.connection_id:
            self._packets_dropped += 1
            return True, None

        key = _remote_key(remote)
        if packet.packet_type == CultNetRudpPacketType.CONNECT:
            self._receive_connect(packet, remote, key)
            return True, None
        peer = self._peers.get(key)
        if peer is None:
            self._packets_dropped += 1
            return True, None
        self._receive_from_peer(peer, key, packet)
        return True, (self._delivered.popleft() if self._delivered else None)

    def poll_resends(self) -> None:
        """Sends the reliable packets whose resend timers are due, for every peer."""
        for peer in self.peers:
            if self._peers.get(_remote_key(peer.remote_addr)) is not peer:
                continue
            for packet in peer.session.due_resends(_now_ms()):
                error = self._send_packet(peer.remote_addr, packet)
                if error is not None:
                    self._end_unsendable_peer(peer, error)
                    break

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self.socket.close()

    def __enter__(self) -> "CultNetRudpSocketTransportServer":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def _receive_connect(self, packet: CultNetRudpPacket, remote: tuple, key: tuple) -> None:
        admitted = self._peers.get(key)
        accept_payload = self._options.accept_payload
        # The admitted session decides whether this Connect repeats the one it accepted: a repeat
        # gets the Accept already owed and queues nothing. Any other Connect is a new generation
        # and ends the admitted session here.
        if admitted is not None:
            if admitted.session.connect_repeats(packet):
                try:
                    reply = admitted.session.accept_connect(packet, _now_ms(), accept_payload)
                except ValueError:
                    self._packets_dropped += 1
                    return
                error = self._send_packet(admitted.remote_addr, reply)
                if error is not None:
                    self._end_unsendable_peer(admitted, error)
                return
            admitted.session.reset_peer_state()

        peer = CultNetRudpSocketServerPeer(
            tuple(remote),
            packet.payload,
            CultNetRudpSession(
                CultNetRudpSessionOptions(
                    connection_id=self._options.connection_id,
                    initial_sequence=self._options.initial_sequence,
                    resend_delay_ms=self._options.resend_delay_ms,
                    max_pending_reliable_packets=self._options.max_pending_reliable_packets,
                )
            ),
        )
        try:
            accept = peer.session.accept_connect(packet, _now_ms(), accept_payload)
        except ValueError:
            self._packets_dropped += 1
            accept = None
        # A peer that cannot be answered gets no session, and the one it replaced has already ended.
        if accept is None or self._send_packet(peer.remote_addr, accept) is not None:
            self._peers.pop(key, None)
            if accept is not None:
                self._packets_dropped += 1
        else:
            self._peers[key] = peer
        if admitted is not None:
            admitted.disconnect_reason = REPLACED_PEER_REASON
            self._notify_disconnected(admitted)

    def _receive_from_peer(self, peer: CultNetRudpSocketServerPeer, key: tuple, packet: CultNetRudpPacket) -> None:
        session = peer.session
        try:
            result = session.receive(packet, _now_ms())
        except ValueError:
            # receive() has already recorded the packet's reliable sequence, so the session cannot
            # be kept: a retransmit would be acknowledged and the frame silently lost.
            self._packets_dropped += 1
            self._send_packet(peer.remote_addr, session.end_with(REFUSED_PACKET_REASON))
            self._retire(peer, REFUSED_PACKET_REASON)
            return
        error = None
        if result.reply is not None:
            error = self._send_packet(peer.remote_addr, result.reply)
        for ready in result.ready_to_send:
            if error is None:
                error = self._send_packet(peer.remote_addr, ready)
        if result.disconnected:
            self._retire(peer, result.disconnect_reason)
            return
        for frame in result.delivered:
            self._delivered.append(
                CultNetRudpSocketServerFrame(peer, CultNetTransportFrame(frame.channel_id, frame.payload))
            )
            self._frames_received += 1
        if error is None and packet.packet_type == CultNetRudpPacketType.DATA:
            error = self._send_packet(peer.remote_addr, session.create_ack_for_received(packet.sequence))
        if error is not None:
            self._end_unsendable_peer(peer, error)

    def _end_unsendable_peer(self, peer: CultNetRudpSocketServerPeer, error: OSError) -> None:
        """An admitted peer owes a packet that can never be sent as built: its session ends, never
        the poll, the peer is told, and the disconnect reason names the error. A session that has
        already ended owes no goodbye, since the endpoint may belong to a new session."""
        if peer.session.ended:
            return
        goodbye = peer.session.end_unsendable_session(error)
        self._send_packet(peer.remote_addr, goodbye)
        self._retire(peer, goodbye.payload)

    def _retire(self, peer: CultNetRudpSocketServerPeer, reason: bytes | None) -> None:
        # A peer a new Connect already replaced was reported then; its key now names the successor.
        key = _remote_key(peer.remote_addr)
        if self._peers.get(key) is not peer:
            return
        del self._peers[key]
        peer.disconnect_reason = reason
        self._notify_disconnected(peer)

    def _notify_disconnected(self, peer: CultNetRudpSocketServerPeer) -> None:
        for callback in list(self.peer_disconnected):
            callback(peer)

    def _send_packet(self, remote_addr: tuple, packet: CultNetRudpPacket) -> OSError | None:
        """A transient failure is that peer's lost datagram: counted, never raised. A permanent
        one (`is_permanent_send_error`) is returned for the caller to refuse or end the session."""
        wire = encode_rudp_packet(packet)
        try:
            self._bytes_sent += self.socket.sendto(wire, remote_addr)
        except OSError as error:
            if is_permanent_send_error(error):
                return error
            self._send_failures += 1
        return None
