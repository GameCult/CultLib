from __future__ import annotations

import socket
import threading
import time
from collections.abc import Callable
from collections import deque
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, BinaryIO

from .framing import read_frame, write_frame

RUDP_MAGIC = b"CNR0"
RUDP_VERSION = 0
RUDP_FIXED_HEADER_BYTES = 36
MAX_RUDP_CHANNEL_ID_BYTES = 255


@dataclass(frozen=True)
class CultNetTransportStats:
    bytes_received: int = 0
    bytes_sent: int = 0
    frames_received: int = 0
    frames_sent: int = 0


@dataclass(frozen=True)
class CultNetReconnectPolicy:
    schema_version: str = "cultnet.reconnect_policy.v0"
    policy_id: str = "default"
    base_delay_ms: int = 1000
    max_delay_ms: int = 30000
    max_jitter_ms: int = 250
    max_attempts: int | None = None

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaVersion": self.schema_version,
            "policyId": self.policy_id,
            "baseDelayMs": self.base_delay_ms,
            "maxDelayMs": self.max_delay_ms,
            "maxJitterMs": self.max_jitter_ms,
        }
        if self.max_attempts is not None:
            wire["maxAttempts"] = self.max_attempts
        return wire


def create_reconnect_policy(
    *,
    policy_id: str = "default",
    base_delay_ms: int = 1000,
    max_delay_ms: int = 30000,
    max_jitter_ms: int = 250,
    max_attempts: int | None = None,
) -> CultNetReconnectPolicy:
    return CultNetReconnectPolicy(
        policy_id=policy_id or "default",
        base_delay_ms=base_delay_ms,
        max_delay_ms=max_delay_ms,
        max_jitter_ms=max_jitter_ms,
        max_attempts=max_attempts,
    )


def compute_reconnect_delay_ms(policy: CultNetReconnectPolicy, attempt: int, jitter_ms: int = 0) -> int:
    normalized_attempt = max(1, int(attempt))
    capped_base_delay = min(policy.max_delay_ms, policy.base_delay_ms * (2 ** (normalized_attempt - 1)))
    bounded_jitter = max(0, min(policy.max_jitter_ms, int(jitter_ms)))
    return capped_base_delay + bounded_jitter


@dataclass(frozen=True)
class CultNetReconnectDecision:
    attempt: int
    should_retry: bool
    delay_ms: int = 0
    next_attempt_at_ms: int | None = None
    exhausted: bool = False


@dataclass
class CultNetReconnectController:
    policy: CultNetReconnectPolicy = field(default_factory=create_reconnect_policy)
    attempt: int = 0
    next_attempt_at_ms: int | None = None
    exhausted: bool = False

    def reset(self) -> None:
        self.attempt = 0
        self.next_attempt_at_ms = None
        self.exhausted = False

    def can_attempt(self, now_ms: int) -> bool:
        return not self.exhausted and (self.next_attempt_at_ms is None or now_ms >= self.next_attempt_at_ms)

    def record_failure(self, now_ms: int, jitter_ms: int = 0) -> CultNetReconnectDecision:
        next_attempt = self.attempt + 1
        if self.policy.max_attempts is not None and next_attempt > self.policy.max_attempts:
            self.exhausted = True
            self.next_attempt_at_ms = None
            return CultNetReconnectDecision(
                attempt=self.attempt,
                should_retry=False,
                exhausted=True,
            )

        self.attempt = next_attempt
        delay_ms = compute_reconnect_delay_ms(self.policy, self.attempt, jitter_ms)
        self.next_attempt_at_ms = int(now_ms) + delay_ms
        return CultNetReconnectDecision(
            attempt=self.attempt,
            should_retry=True,
            delay_ms=delay_ms,
            next_attempt_at_ms=self.next_attempt_at_ms,
        )


CultNetRudpReconnectScheduler = Callable[[int, Callable[[], None]], Callable[[], None]]


def _default_rudp_reconnect_scheduler(delay_ms: int, callback: Callable[[], None]) -> Callable[[], None]:
    timer = threading.Timer(max(0, delay_ms) / 1000.0, callback)
    timer.daemon = True
    timer.start()
    return timer.cancel


class CultNetRudpReconnectLoop:
    def __init__(
        self,
        create_transport: Callable[[], CultNetRudpSocketTransportConnection],
        *,
        reconnect_policy: CultNetReconnectPolicy | None = None,
        connect_payload: bytes = b"",
        now_ms: Callable[[], int] | None = None,
        jitter_ms: Callable[[], int] | None = None,
        scheduler: CultNetRudpReconnectScheduler | None = None,
    ) -> None:
        self.reconnect_controller = CultNetReconnectController(reconnect_policy or create_reconnect_policy())
        self._create_transport = create_transport
        self._connect_payload = bytes(connect_payload)
        self._now_ms = now_ms or _now_ms
        self._jitter_ms = jitter_ms or (lambda: 0)
        self._scheduler = scheduler or _default_rudp_reconnect_scheduler
        self._cancel_timer: Callable[[], None] | None = None
        self._stopped = True
        self.transport: CultNetRudpSocketTransportConnection | None = None

    def start(self) -> CultNetRudpSocketTransportConnection:
        self._stopped = False
        self.reconnect_controller.reset()
        return self._open_transport()

    def stop(self) -> None:
        self._stopped = True
        if self._cancel_timer is not None:
            self._cancel_timer()
            self._cancel_timer = None
        transport = self.transport
        self.transport = None
        if transport is not None:
            transport.close()
        self.reconnect_controller.reset()

    def mark_connected(self) -> None:
        self.reconnect_controller.reset()

    def handle_closed(self) -> CultNetReconnectDecision | None:
        self.transport = None
        return self._schedule_reconnect()

    def _open_transport(self) -> CultNetRudpSocketTransportConnection:
        transport = self._create_transport()
        self.transport = transport
        try:
            transport.connect(self._connect_payload)
        except Exception:
            self.transport = None
            transport.close()
            self._schedule_reconnect()
            raise
        return transport

    def _schedule_reconnect(self) -> CultNetReconnectDecision | None:
        if self._stopped or self._cancel_timer is not None:
            return None

        decision = self.reconnect_controller.record_failure(self._now_ms(), self._jitter_ms())
        if not decision.should_retry:
            return decision

        def reconnect() -> None:
            self._cancel_timer = None
            if not self._stopped and self.reconnect_controller.can_attempt(self._now_ms()):
                self._open_transport()

        self._cancel_timer = self._scheduler(decision.delay_ms, reconnect)
        return decision


@dataclass(frozen=True)
class CultNetTransportFrame:
    channel_id: str
    payload: bytes


def create_tcp_framed_transport_profile(
    runtime_id: str,
    *,
    transport_id: str = "tcp-framed",
    host: str | None = None,
    port: int | None = None,
    max_payload_bytes: int | None = None,
    max_fragment_bytes: int | None = None,
) -> dict[str, Any]:
    channel: dict[str, Any] = {
        "channelId": "schema",
        "delivery": "reliable",
        "ordering": "ordered",
    }
    if max_payload_bytes is not None:
        channel["maxPayloadBytes"] = max_payload_bytes
    if max_fragment_bytes is not None:
        channel["maxFragmentBytes"] = max_fragment_bytes

    transport: dict[str, Any] = {
        "transportId": transport_id,
        "protocol": "tcp_framed",
        "wireContracts": ["cultnet.schema.v0"],
        "channels": [channel],
    }
    if host is not None:
        transport["host"] = host
    if port is not None:
        transport["port"] = port

    return {
        "schemaVersion": "cultnet.transport_profile.v0",
        "runtimeId": runtime_id,
        "transports": [transport],
    }


class CultNetRudpPacketType(str, Enum):
    CONNECT = "connect"
    ACCEPT = "accept"
    DATA = "data"
    ACK = "ack"
    PING = "ping"
    PONG = "pong"
    DISCONNECT = "disconnect"


@dataclass(frozen=True)
class CultNetRudpPacket:
    packet_type: CultNetRudpPacketType
    connection_id: int
    sequence: int
    ack: int
    ack_mask: int
    channel_id: str
    reliable: bool = False
    ordered: bool = False
    sequenced: bool = False
    fragment_id: int = 0
    fragment_index: int = 0
    fragment_count: int = 0
    payload: bytes = b""


@dataclass(frozen=True)
class CultNetRudpDeliveredFrame:
    channel_id: str
    payload: bytes
    sequence: int


@dataclass(frozen=True)
class CultNetRudpReceiveResult:
    delivered: tuple[CultNetRudpDeliveredFrame, ...] = field(default_factory=tuple)
    ready_to_send: tuple[CultNetRudpPacket, ...] = field(default_factory=tuple)
    reply: CultNetRudpPacket | None = None
    pong: bool = False
    pong_payload: bytes = b""
    disconnected: bool = False
    disconnect_reason: bytes = b""


@dataclass(frozen=True)
class CultNetRudpSessionOptions:
    connection_id: int
    initial_sequence: int = 1
    resend_delay_ms: int = 250
    max_pending_reliable_packets: int | None = None


@dataclass(frozen=True)
class CultNetRudpSendOptions:
    reliable: bool = False
    ordered: bool = False
    sequenced: bool = False
    now_ms: int = 0


class CultNetRudpSocketMode(str, Enum):
    CLIENT = "client"
    SERVER = "server"


@dataclass(frozen=True)
class CultNetRudpSocketTransportOptions:
    runtime_id: str
    socket: socket.socket
    mode: CultNetRudpSocketMode
    connection_id: int
    remote_addr: tuple[str, int] | None = None
    initial_sequence: int = 1
    resend_delay_ms: int = 250
    transport_id: str = "rudp"
    max_payload_bytes: int | None = None
    max_fragment_bytes: int | None = None
    max_pending_reliable_packets: int | None = None
    reconnect_policy: CultNetReconnectPolicy | dict[str, Any] | None = None


@dataclass
class _PendingReliablePacket:
    packet: CultNetRudpPacket
    last_sent_at_ms: int


_RUDP_PACKET_TYPE_TO_CODE = {
    CultNetRudpPacketType.CONNECT: 1,
    CultNetRudpPacketType.ACCEPT: 2,
    CultNetRudpPacketType.DATA: 3,
    CultNetRudpPacketType.ACK: 4,
    CultNetRudpPacketType.PING: 5,
    CultNetRudpPacketType.PONG: 6,
    CultNetRudpPacketType.DISCONNECT: 7,
}
_RUDP_PACKET_TYPE_FROM_CODE = {value: key for key, value in _RUDP_PACKET_TYPE_TO_CODE.items()}


def create_rudp_transport_profile(
    runtime_id: str,
    *,
    transport_id: str = "rudp",
    host: str | None = None,
    port: int | None = None,
    max_payload_bytes: int | None = None,
    max_fragment_bytes: int | None = None,
    max_pending_reliable_packets: int | None = None,
    reconnect_policy: CultNetReconnectPolicy | dict[str, Any] | None = None,
) -> dict[str, Any]:
    def channel(channel_id: str, delivery: str, ordering: str) -> dict[str, Any]:
        value: dict[str, Any] = {
            "channelId": channel_id,
            "delivery": delivery,
            "ordering": ordering,
        }
        if max_payload_bytes is not None:
            value["maxPayloadBytes"] = max_payload_bytes
        if max_fragment_bytes is not None:
            value["maxFragmentBytes"] = max_fragment_bytes
        if max_pending_reliable_packets is not None:
            value["maxPendingReliablePackets"] = max_pending_reliable_packets
        return value

    transport: dict[str, Any] = {
        "transportId": transport_id,
        "protocol": "rudp",
        "wireContracts": ["cultnet.schema.v0"],
        "reconnectPolicy": (
            reconnect_policy.to_wire()
            if isinstance(reconnect_policy, CultNetReconnectPolicy)
            else reconnect_policy or create_reconnect_policy().to_wire()
        ),
        "channels": [
            channel("schema", "reliable", "ordered"),
            channel("latest", "unreliable", "sequenced"),
            channel("realtime", "unreliable", "unordered"),
        ],
    }
    if host is not None:
        transport["host"] = host
    if port is not None:
        transport["port"] = port

    return {
        "schemaVersion": "cultnet.transport_profile.v0",
        "runtimeId": runtime_id,
        "transports": [transport],
    }


class CultNetRudpSession:
    RELIABLE_SEND_WINDOW_PACKETS = 32
    RECEIVED_SEQUENCE_WINDOW = 4_096

    def __init__(self, options: CultNetRudpSessionOptions) -> None:
        self.connection_id = _uint32(options.connection_id, "connection_id")
        self.resend_delay_ms = options.resend_delay_ms
        if options.max_pending_reliable_packets is not None and options.max_pending_reliable_packets <= 0:
            raise ValueError("RUDP max_pending_reliable_packets must be greater than zero")
        self.max_pending_reliable_packets = options.max_pending_reliable_packets
        self._next_sequence = _uint32(options.initial_sequence, "initial_sequence")
        self._next_fragment_id = 1
        self._connected = False
        self._last_received_at_ms: int | None = None
        self._highest_received_sequence: int | None = None
        self._received_sequences: set[int] = set()
        self._pending_reliable: dict[int, _PendingReliablePacket] = {}
        self._queued_reliable: deque[CultNetRudpPacket] = deque()
        self._ordered_next_sequence_by_channel: dict[str, int] = {}
        self._ordered_buffers: dict[str, dict[int, tuple[CultNetRudpDeliveredFrame, int]]] = {}
        self._fragment_buffers: dict[tuple[str, int], dict[str, Any]] = {}

    @property
    def connected(self) -> bool:
        return self._connected

    @property
    def last_received_at_ms(self) -> int | None:
        return self._last_received_at_ms

    @property
    def pending_reliable_sequences(self) -> tuple[int, ...]:
        return tuple(sorted(self._pending_reliable))

    @property
    def queued_reliable_packet_count(self) -> int:
        return len(self._queued_reliable)

    @property
    def outstanding_reliable_packet_count(self) -> int:
        return len(self._pending_reliable) + len(self._queued_reliable)

    def create_connect(self, now_ms: int = 0, payload: bytes = b"") -> CultNetRudpPacket:
        self._ensure_reliable_capacity(1)
        packet = self._create_packet(
            CultNetRudpPacketType.CONNECT,
            "control",
            payload,
            reliable=True,
            ordered=True,
        )
        self._track_reliable(packet, now_ms)
        return packet

    def accept_connect(
        self,
        packet: CultNetRudpPacket,
        now_ms: int = 0,
        payload: bytes = b"",
    ) -> CultNetRudpPacket:
        self._require_connection(packet)
        if packet.packet_type != CultNetRudpPacketType.CONNECT:
            raise ValueError(f"Expected RUDP connect packet, got {packet.packet_type.value}")

        self._ensure_reliable_capacity(1)
        self._remember_received(packet.sequence)
        self._connected = True
        response = self._create_packet(
            CultNetRudpPacketType.ACCEPT,
            "control",
            payload,
            reliable=True,
            ordered=True,
        )
        self._track_reliable(response, now_ms)
        return response

    def send(
        self,
        channel_id: str,
        payload: bytes,
        options: CultNetRudpSendOptions | None = None,
    ) -> CultNetRudpPacket:
        resolved = options or CultNetRudpSendOptions()
        if resolved.reliable and len(self._pending_reliable) >= self.RELIABLE_SEND_WINDOW_PACKETS:
            raise ValueError("RUDP reliable send window is full; receive acknowledgements before sending")
        return self.send_many(channel_id, payload, resolved)[0]

    def send_many(
        self,
        channel_id: str,
        payload: bytes,
        options: CultNetRudpSendOptions | None = None,
        *,
        max_fragment_bytes: int | None = None,
    ) -> tuple[CultNetRudpPacket, ...]:
        if not self._connected:
            raise ValueError("Cannot send RUDP data before the session is connected")

        options = options or CultNetRudpSendOptions()
        if max_fragment_bytes is not None and max_fragment_bytes <= 0:
            raise ValueError("RUDP max_fragment_bytes must be greater than zero")

        if max_fragment_bytes is None or len(payload) <= max_fragment_bytes:
            self._ensure_reliable_capacity(1 if options.reliable else 0)
            packet = self._create_packet(
                CultNetRudpPacketType.DATA,
                channel_id,
                payload,
                reliable=options.reliable,
                ordered=options.ordered,
                sequenced=options.sequenced,
            )
            return self._admit_reliable_packets((packet,), options.now_ms) if packet.reliable else (packet,)

        fragment_count = (len(payload) + max_fragment_bytes - 1) // max_fragment_bytes
        if fragment_count > 0xFFFF:
            raise ValueError("RUDP payload requires more than 65535 fragments")
        self._ensure_reliable_capacity(fragment_count if options.reliable else 0)

        fragment_id = self._allocate_fragment_id()
        packets: list[CultNetRudpPacket] = []
        for index in range(fragment_count):
            start = index * max_fragment_bytes
            packet = self._create_packet(
                CultNetRudpPacketType.DATA,
                channel_id,
                payload[start:start + max_fragment_bytes],
                reliable=options.reliable,
                ordered=options.ordered,
                sequenced=options.sequenced,
                fragment_id=fragment_id,
                fragment_index=index,
                fragment_count=fragment_count,
            )
            packets.append(packet)
        return self._admit_reliable_packets(tuple(packets), options.now_ms) if options.reliable else tuple(packets)

    def receive(self, packet: CultNetRudpPacket, now_ms: int = 0) -> CultNetRudpReceiveResult:
        self._require_connection(packet)
        self._apply_acknowledgements(packet)
        ready_to_send = self._promote_queued_reliable(now_ms)
        self._last_received_at_ms = now_ms
        expected_sequence_if_uninitialized = (
            packet.sequence
            if self._highest_received_sequence is None
            else self._highest_received_sequence + 1
        )

        if packet.packet_type == CultNetRudpPacketType.ACCEPT:
            self._remember_received(packet.sequence)
            self._connected = True
            return CultNetRudpReceiveResult(ready_to_send=ready_to_send)

        if packet.packet_type == CultNetRudpPacketType.PING:
            self._remember_received(packet.sequence)
            return CultNetRudpReceiveResult(
                ready_to_send=ready_to_send,
                reply=self._create_packet(CultNetRudpPacketType.PONG, "control", packet.payload)
            )

        if packet.packet_type in (CultNetRudpPacketType.ACK, CultNetRudpPacketType.PONG):
            self._remember_received(packet.sequence)
            return CultNetRudpReceiveResult(
                ready_to_send=ready_to_send,
                pong=packet.packet_type == CultNetRudpPacketType.PONG,
                pong_payload=bytes(packet.payload) if packet.packet_type == CultNetRudpPacketType.PONG else b"",
            )

        if packet.packet_type == CultNetRudpPacketType.DISCONNECT:
            self._remember_received(packet.sequence)
            self._connected = False
            return CultNetRudpReceiveResult(
                ready_to_send=ready_to_send,
                disconnected=True,
                disconnect_reason=bytes(packet.payload),
            )

        if packet.packet_type != CultNetRudpPacketType.DATA:
            return CultNetRudpReceiveResult(ready_to_send=ready_to_send)

        duplicate = packet.sequence in self._received_sequences or (
            self._highest_received_sequence is not None
            and packet.sequence < self._highest_received_sequence
            and self._highest_received_sequence - packet.sequence >= self.RECEIVED_SEQUENCE_WINDOW
        )
        self._remember_received(packet.sequence)
        if duplicate:
            return CultNetRudpReceiveResult(ready_to_send=ready_to_send)

        reassembled = self._reassemble(packet)
        if reassembled is None:
            return CultNetRudpReceiveResult(ready_to_send=ready_to_send)
        frame, ordered, next_sequence = reassembled
        if not ordered:
            return CultNetRudpReceiveResult(delivered=(frame,), ready_to_send=ready_to_send)
        return CultNetRudpReceiveResult(
            delivered=tuple(
                self._deliver_ordered(frame, next_sequence, expected_sequence_if_uninitialized)
            ),
            ready_to_send=ready_to_send,
        )

    def create_ack(self) -> CultNetRudpPacket:
        return self._create_packet(CultNetRudpPacketType.ACK, "control", b"")

    def create_ack_for(self, sequence: int) -> CultNetRudpPacket:
        return CultNetRudpPacket(
            packet_type=CultNetRudpPacketType.ACK,
            connection_id=self.connection_id,
            sequence=0,
            ack=_uint32(sequence, "ack sequence"),
            ack_mask=0,
            channel_id="control",
            payload=b"",
        )

    def create_ping(self, payload: bytes = b"") -> CultNetRudpPacket:
        return self._create_packet(CultNetRudpPacketType.PING, "control", payload)

    def create_disconnect(self, reason: bytes = b"") -> CultNetRudpPacket:
        self._connected = False
        return self._create_packet(CultNetRudpPacketType.DISCONNECT, "control", reason)

    def check_timeout(self, now_ms: int, timeout_ms: int) -> bool:
        if not self._connected or self._last_received_at_ms is None:
            return False
        if now_ms - self._last_received_at_ms <= timeout_ms:
            return False
        self._connected = False
        return True

    def due_resends(self, now_ms: int) -> tuple[CultNetRudpPacket, ...]:
        due: list[CultNetRudpPacket] = []
        for pending in self._pending_reliable.values():
            if now_ms - pending.last_sent_at_ms >= self.resend_delay_ms:
                pending.last_sent_at_ms = now_ms
                due.append(pending.packet)
        return tuple(sorted(due, key=lambda packet: packet.sequence))

    def _create_packet(
        self,
        packet_type: CultNetRudpPacketType,
        channel_id: str,
        payload: bytes,
        *,
        reliable: bool = False,
        ordered: bool = False,
        sequenced: bool = False,
        fragment_id: int = 0,
        fragment_index: int = 0,
        fragment_count: int = 0,
    ) -> CultNetRudpPacket:
        sequence = self._next_sequence
        self._next_sequence = _uint32(self._next_sequence + 1, "sequence")
        ack, ack_mask = self._ack_state()
        return CultNetRudpPacket(
            packet_type=packet_type,
            connection_id=self.connection_id,
            sequence=sequence,
            ack=ack,
            ack_mask=ack_mask,
            channel_id=channel_id,
            reliable=reliable,
            ordered=ordered,
            sequenced=sequenced,
            fragment_id=fragment_id,
            fragment_index=fragment_index,
            fragment_count=fragment_count,
            payload=bytes(payload),
        )

    def _track_reliable(self, packet: CultNetRudpPacket, now_ms: int) -> None:
        self._pending_reliable[packet.sequence] = _PendingReliablePacket(
            packet=CultNetRudpPacket(
                packet_type=packet.packet_type,
                connection_id=packet.connection_id,
                sequence=packet.sequence,
                ack=packet.ack,
                ack_mask=packet.ack_mask,
                channel_id=packet.channel_id,
                reliable=packet.reliable,
                ordered=packet.ordered,
                sequenced=packet.sequenced,
                fragment_id=packet.fragment_id,
                fragment_index=packet.fragment_index,
                fragment_count=packet.fragment_count,
                payload=bytes(packet.payload),
            ),
            last_sent_at_ms=now_ms,
        )

    def _admit_reliable_packets(
        self,
        packets: tuple[CultNetRudpPacket, ...],
        now_ms: int,
    ) -> tuple[CultNetRudpPacket, ...]:
        available = max(0, self.RELIABLE_SEND_WINDOW_PACKETS - len(self._pending_reliable))
        ready = packets[:available]
        for packet in ready:
            self._track_reliable(packet, now_ms)
        self._queued_reliable.extend(packets[available:])
        return ready

    def _promote_queued_reliable(self, now_ms: int) -> tuple[CultNetRudpPacket, ...]:
        available = max(0, self.RELIABLE_SEND_WINDOW_PACKETS - len(self._pending_reliable))
        ready: list[CultNetRudpPacket] = []
        while len(ready) < available and self._queued_reliable:
            packet = self._queued_reliable.popleft()
            self._track_reliable(packet, now_ms)
            ready.append(packet)
        return tuple(ready)

    def _ensure_reliable_capacity(self, packet_count: int) -> None:
        if packet_count == 0 or self.max_pending_reliable_packets is None:
            return
        if self.outstanding_reliable_packet_count + packet_count > self.max_pending_reliable_packets:
            raise ValueError("RUDP reliable send queue is full")

    def _apply_acknowledgements(self, packet: CultNetRudpPacket) -> None:
        self._pending_reliable.pop(packet.ack, None)
        for bit in range(32):
            if packet.ack_mask & (1 << bit):
                self._pending_reliable.pop(packet.ack - bit - 1, None)

    def _remember_received(self, sequence: int) -> None:
        self._received_sequences.add(sequence)
        if self._highest_received_sequence is None or sequence > self._highest_received_sequence:
            self._highest_received_sequence = sequence
        if len(self._received_sequences) > self.RECEIVED_SEQUENCE_WINDOW:
            keep_from = max(0, (self._highest_received_sequence or sequence) - self.RECEIVED_SEQUENCE_WINDOW + 1)
            self._received_sequences = {received for received in self._received_sequences if received >= keep_from}

    def _ack_state(self) -> tuple[int, int]:
        ack = self._highest_received_sequence or 0
        ack_mask = 0
        for bit in range(32):
            if ack > bit and (ack - bit - 1) in self._received_sequences:
                ack_mask |= 1 << bit
        return ack, ack_mask

    def _reassemble(self, packet: CultNetRudpPacket) -> tuple[CultNetRudpDeliveredFrame, bool, int] | None:
        if packet.fragment_count == 0:
            return (
                CultNetRudpDeliveredFrame(packet.channel_id, bytes(packet.payload), packet.sequence),
                packet.ordered,
                packet.sequence + 1,
            )
        if packet.fragment_id == 0:
            raise ValueError("RUDP fragmented packet must have a non-zero fragment id")
        if packet.fragment_index >= packet.fragment_count:
            raise ValueError("RUDP fragment index must be lower than fragment count")

        key = (packet.channel_id, packet.fragment_id)
        buffer = self._fragment_buffers.setdefault(
            key,
            {
                "fragment_count": packet.fragment_count,
                "ordered": packet.ordered,
                "payloads": {},
                "sequences": {},
            },
        )
        if buffer["fragment_count"] != packet.fragment_count or buffer["ordered"] != packet.ordered:
            raise ValueError("RUDP fragment metadata changed within a fragment set")
        buffer["payloads"][packet.fragment_index] = bytes(packet.payload)
        buffer["sequences"][packet.fragment_index] = packet.sequence
        if len(buffer["payloads"]) < packet.fragment_count:
            return None

        payload = b"".join(buffer["payloads"][index] for index in range(packet.fragment_count))
        sequences = [buffer["sequences"][index] for index in range(packet.fragment_count)]
        del self._fragment_buffers[key]
        return (
            CultNetRudpDeliveredFrame(packet.channel_id, payload, min(sequences)),
            bool(buffer["ordered"]),
            max(sequences) + 1,
        )

    def _deliver_ordered(
        self,
        frame: CultNetRudpDeliveredFrame,
        next_after_frame: int,
        expected_sequence_if_uninitialized: int,
    ) -> list[CultNetRudpDeliveredFrame]:
        next_sequence = self._ordered_next_sequence_by_channel.get(frame.channel_id)
        if next_sequence is None:
            next_sequence = min(expected_sequence_if_uninitialized, frame.sequence)
            self._ordered_next_sequence_by_channel[frame.channel_id] = next_sequence
        while (
            frame.sequence > next_sequence
            and next_sequence in self._received_sequences
            and next_sequence not in self._ordered_buffers.get(frame.channel_id, {})
        ):
            next_sequence += 1
            self._ordered_next_sequence_by_channel[frame.channel_id] = next_sequence
        if frame.sequence < next_sequence:
            return []
        if frame.sequence > next_sequence:
            self._ordered_buffers.setdefault(frame.channel_id, {})[frame.sequence] = (frame, next_after_frame)
            return []

        self._ordered_next_sequence_by_channel[frame.channel_id] = next_after_frame
        return [frame, *self._drain_ordered(frame.channel_id)]

    def _drain_ordered(self, channel_id: str) -> list[CultNetRudpDeliveredFrame]:
        delivered: list[CultNetRudpDeliveredFrame] = []
        buffer = self._ordered_buffers.get(channel_id)
        if buffer is None:
            return delivered

        while True:
            next_sequence = self._ordered_next_sequence_by_channel[channel_id]
            pending = buffer.pop(next_sequence, None)
            if pending is None:
                break
            frame, next_after_frame = pending
            delivered.append(frame)
            self._ordered_next_sequence_by_channel[channel_id] = next_after_frame
            self._skip_received_non_channel_sequences(channel_id)
        return delivered

    def _skip_received_non_channel_sequences(self, channel_id: str) -> None:
        next_sequence = self._ordered_next_sequence_by_channel.get(channel_id)
        while (
            next_sequence is not None
            and next_sequence in self._received_sequences
            and next_sequence not in self._ordered_buffers.get(channel_id, {})
        ):
            next_sequence += 1
            self._ordered_next_sequence_by_channel[channel_id] = next_sequence

    def _allocate_fragment_id(self) -> int:
        fragment_id = self._next_fragment_id
        self._next_fragment_id += 1
        if self._next_fragment_id > 0xFFFF:
            self._next_fragment_id = 1
        return fragment_id

    def _require_connection(self, packet: CultNetRudpPacket) -> None:
        if packet.connection_id != self.connection_id:
            raise ValueError(
                f"RUDP packet belongs to connection {packet.connection_id}, expected {self.connection_id}"
            )


class CultNetRudpSocketTransportConnection:
    def __init__(self, options: CultNetRudpSocketTransportOptions) -> None:
        self.socket = options.socket
        self.mode = CultNetRudpSocketMode(options.mode)
        self.remote_addr = options.remote_addr
        self.max_fragment_bytes = options.max_fragment_bytes
        self.session = CultNetRudpSession(
            CultNetRudpSessionOptions(
                connection_id=options.connection_id,
                initial_sequence=options.initial_sequence,
                resend_delay_ms=options.resend_delay_ms,
                max_pending_reliable_packets=options.max_pending_reliable_packets,
            )
        )
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
        self._bytes_received = 0
        self._bytes_sent = 0
        self._frames_received = 0
        self._frames_sent = 0
        self._delivered_frames: deque[CultNetTransportFrame] = deque()
        self._closed = False
        self.disconnect_reason: bytes | None = None
        self.pong_payloads: deque[bytes] = deque()

    def __enter__(self) -> "CultNetRudpSocketTransportConnection":
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.close()

    @property
    def connected(self) -> bool:
        return self.session.connected

    @property
    def stats(self) -> CultNetTransportStats:
        return CultNetTransportStats(
            bytes_received=self._bytes_received,
            bytes_sent=self._bytes_sent,
            frames_received=self._frames_received,
            frames_sent=self._frames_sent,
        )

    @property
    def outstanding_reliable_packet_count(self) -> int:
        return self.session.outstanding_reliable_packet_count

    def connect(self, payload: bytes = b"") -> None:
        if self.mode != CultNetRudpSocketMode.CLIENT:
            raise ValueError("Only a client RUDP socket transport can initiate connect")
        self._send_packet(self.session.create_connect(_now_ms(), payload))

    def send(self, channel_id: str, payload: bytes) -> None:
        packets = self.session.send_many(
            channel_id,
            payload,
            _channel_send_options(channel_id, _now_ms()),
            max_fragment_bytes=self.max_fragment_bytes,
        )
        for packet in packets:
            self._send_packet(packet)
        self._frames_sent += 1

    def receive_once(self) -> CultNetTransportFrame | None:
        if self._delivered_frames:
            return self._delivered_frames.popleft()

        try:
            wire, remote_addr = self.socket.recvfrom(65535)
        except TimeoutError:
            return None
        except BlockingIOError:
            return None
        self._bytes_received += len(wire)

        if self.remote_addr is None:
            self.remote_addr = remote_addr
        elif remote_addr != self.remote_addr:
            return None

        packet = decode_rudp_packet(wire)
        if self.mode == CultNetRudpSocketMode.SERVER and packet.packet_type == CultNetRudpPacketType.CONNECT:
            self._send_packet(self.session.accept_connect(packet, _now_ms()))
            return None

        result = self.session.receive(packet, _now_ms())
        if result.reply is not None:
            self._send_packet(result.reply)
        for ready in result.ready_to_send:
            self._send_packet(ready)
        if result.pong:
            self.pong_payloads.append(result.pong_payload)
        if result.disconnected:
            self.disconnect_reason = result.disconnect_reason
            return None

        for frame in result.delivered:
            self._delivered_frames.append(
                CultNetTransportFrame(channel_id=frame.channel_id, payload=frame.payload)
            )
            self._frames_received += 1

        frame = self._delivered_frames.popleft() if self._delivered_frames else None
        if packet.reliable or packet.packet_type == CultNetRudpPacketType.ACCEPT or frame is not None:
            self._send_packet(self.session.create_ack_for(packet.sequence))
        return frame

    def receive(self, timeout_seconds: float | None = None) -> CultNetTransportFrame:
        deadline = None if timeout_seconds is None else time.monotonic() + timeout_seconds
        while True:
            frame = self.receive_once()
            if frame is not None:
                return frame
            self.poll_resends()
            if deadline is not None and time.monotonic() >= deadline:
                raise TimeoutError("Timed out waiting for RUDP schema frame")

    def flush_reliable(self, timeout_seconds: float = 30.0) -> None:
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        original_timeout = self.socket.gettimeout()
        poll_timeout = min(original_timeout, 0.01) if original_timeout is not None else 0.01
        preserved = deque(self._delivered_frames)
        self._delivered_frames.clear()
        self.socket.settimeout(poll_timeout)
        try:
            while self.session.outstanding_reliable_packet_count > 0:
                if time.monotonic() >= deadline:
                    raise TimeoutError(
                        "RUDP reliable flush timed out with "
                        f"{self.session.outstanding_reliable_packet_count} packets outstanding"
                    )
                frame = self.receive_once()
                if frame is not None:
                    preserved.append(frame)
                self.poll_resends()
        finally:
            preserved.extend(self._delivered_frames)
            self._delivered_frames = preserved
            self.socket.settimeout(original_timeout)

    def disconnect(self, reason: bytes = b"") -> None:
        self._send_packet(self.session.create_disconnect(reason))

    def ping(self, payload: bytes = b"") -> None:
        self._send_packet(self.session.create_ping(payload))

    def check_timeout(self, timeout_ms: int, now_ms: int | None = None) -> bool:
        return self.session.check_timeout(_now_ms() if now_ms is None else now_ms, timeout_ms)

    def poll_resends(self) -> None:
        for packet in self.session.due_resends(_now_ms()):
            self._send_packet(packet)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self.socket.close()

    def _send_packet(self, packet: CultNetRudpPacket) -> None:
        if self.remote_addr is None:
            raise ValueError("RUDP socket transport does not have a remote endpoint")
        wire = encode_rudp_packet(packet)
        sent = self.socket.sendto(wire, self.remote_addr)
        self._bytes_sent += sent


def encode_rudp_packet(packet: CultNetRudpPacket) -> bytes:
    channel_id = packet.channel_id.encode("utf-8")
    if len(channel_id) > MAX_RUDP_CHANNEL_ID_BYTES:
        raise ValueError("CultNet RUDP channel id cannot exceed 255 UTF-8 bytes")
    payload = bytes(packet.payload)
    header_bytes = RUDP_FIXED_HEADER_BYTES + len(channel_id)
    return b"".join(
        [
            RUDP_MAGIC,
            RUDP_VERSION.to_bytes(1, "big"),
            _RUDP_PACKET_TYPE_TO_CODE[packet.packet_type].to_bytes(1, "big"),
            _encode_rudp_flags(packet).to_bytes(1, "big"),
            header_bytes.to_bytes(1, "big"),
            _uint32(packet.connection_id, "connection_id").to_bytes(4, "big"),
            _uint32(packet.sequence, "sequence").to_bytes(4, "big"),
            _uint32(packet.ack, "ack").to_bytes(4, "big"),
            _uint32(packet.ack_mask, "ack_mask").to_bytes(4, "big"),
            _uint16(packet.fragment_id, "fragment_id").to_bytes(2, "big"),
            _uint16(packet.fragment_index, "fragment_index").to_bytes(2, "big"),
            _uint16(packet.fragment_count, "fragment_count").to_bytes(2, "big"),
            _uint32(len(payload), "payload length").to_bytes(4, "big"),
            len(channel_id).to_bytes(1, "big"),
            b"\x00",
            channel_id,
            payload,
        ]
    )


def decode_rudp_packet(wire: bytes) -> CultNetRudpPacket:
    if len(wire) < RUDP_FIXED_HEADER_BYTES:
        raise ValueError("CultNet RUDP packet is shorter than the fixed header")
    if wire[:4] != RUDP_MAGIC:
        raise ValueError("CultNet RUDP packet has the wrong magic")
    if wire[4] != RUDP_VERSION:
        raise ValueError(f"Unsupported CultNet RUDP packet version {wire[4]}")
    try:
        packet_type = _RUDP_PACKET_TYPE_FROM_CODE[wire[5]]
    except KeyError as error:
        raise ValueError(f"Unsupported CultNet RUDP packet type {wire[5]}") from error

    header_bytes = wire[7]
    channel_id_length = wire[34]
    if header_bytes != RUDP_FIXED_HEADER_BYTES + channel_id_length:
        raise ValueError("CultNet RUDP packet header length does not match the channel id length")
    payload_length = int.from_bytes(wire[30:34], "big")
    if len(wire) != header_bytes + payload_length:
        raise ValueError("CultNet RUDP packet payload length does not match the packet size")

    flags = wire[6]
    return CultNetRudpPacket(
        packet_type=packet_type,
        connection_id=int.from_bytes(wire[8:12], "big"),
        sequence=int.from_bytes(wire[12:16], "big"),
        ack=int.from_bytes(wire[16:20], "big"),
        ack_mask=int.from_bytes(wire[20:24], "big"),
        fragment_id=int.from_bytes(wire[24:26], "big"),
        fragment_index=int.from_bytes(wire[26:28], "big"),
        fragment_count=int.from_bytes(wire[28:30], "big"),
        channel_id=wire[RUDP_FIXED_HEADER_BYTES:header_bytes].decode("utf-8"),
        reliable=bool(flags & 0b0000_0001),
        ordered=bool(flags & 0b0000_0010),
        sequenced=bool(flags & 0b0000_0100),
        payload=wire[header_bytes:],
    )


def _encode_rudp_flags(packet: CultNetRudpPacket) -> int:
    return (
        (0b0000_0001 if packet.reliable else 0)
        | (0b0000_0010 if packet.ordered else 0)
        | (0b0000_0100 if packet.sequenced else 0)
        | (0b0000_1000 if packet.fragment_count > 0 else 0)
    )


def _uint32(value: int, field_name: str) -> int:
    if value < 0 or value > 0xFFFFFFFF:
        raise ValueError(f"CultNet RUDP {field_name} must fit in uint32")
    return value


def _uint16(value: int, field_name: str) -> int:
    if value < 0 or value > 0xFFFF:
        raise ValueError(f"CultNet RUDP {field_name} must fit in uint16")
    return value


def _channel_send_options(channel_id: str, now_ms: int) -> CultNetRudpSendOptions:
    if channel_id == "schema":
        return CultNetRudpSendOptions(reliable=True, ordered=True, sequenced=False, now_ms=now_ms)
    if channel_id == "latest":
        return CultNetRudpSendOptions(reliable=False, ordered=False, sequenced=True, now_ms=now_ms)
    return CultNetRudpSendOptions(reliable=False, ordered=False, sequenced=False, now_ms=now_ms)


def _now_ms() -> int:
    return int(time.time() * 1000)


class TcpFramedTransportConnection:
    def __init__(self, stream: BinaryIO, *, profile: dict[str, Any]) -> None:
        self.stream = stream
        self.profile = dict(profile)
        self._bytes_received = 0
        self._bytes_sent = 0
        self._frames_received = 0
        self._frames_sent = 0

    def __enter__(self) -> "TcpFramedTransportConnection":
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.close()

    @property
    def stats(self) -> CultNetTransportStats:
        return CultNetTransportStats(
            bytes_received=self._bytes_received,
            bytes_sent=self._bytes_sent,
            frames_received=self._frames_received,
            frames_sent=self._frames_sent,
        )

    def send(self, channel_id: str, payload: bytes) -> None:
        if channel_id != "schema":
            raise ValueError(f"tcp_framed transport only supports the schema channel, got {channel_id!r}")
        before = len(payload) + 4
        write_frame(self.stream, payload)
        flush = getattr(self.stream, "flush", None)
        if callable(flush):
            flush()
        self._bytes_sent += before
        self._frames_sent += 1

    def receive(self) -> CultNetTransportFrame:
        payload = read_frame(self.stream)
        self._bytes_received += len(payload) + 4
        self._frames_received += 1
        return CultNetTransportFrame(channel_id="schema", payload=payload)

    def close(self) -> None:
        close = getattr(self.stream, "close", None)
        if callable(close):
            close()
