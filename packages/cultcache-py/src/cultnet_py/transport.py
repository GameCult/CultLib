from __future__ import annotations

import socket
import time
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
    reply: CultNetRudpPacket | None = None


@dataclass(frozen=True)
class CultNetRudpSessionOptions:
    connection_id: int
    initial_sequence: int = 1
    resend_delay_ms: int = 250


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
        return value

    transport: dict[str, Any] = {
        "transportId": transport_id,
        "protocol": "rudp",
        "wireContracts": ["cultnet.schema.v0"],
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
    def __init__(self, options: CultNetRudpSessionOptions) -> None:
        self.connection_id = _uint32(options.connection_id, "connection_id")
        self.resend_delay_ms = options.resend_delay_ms
        self._next_sequence = _uint32(options.initial_sequence, "initial_sequence")
        self._connected = False
        self._highest_received_sequence: int | None = None
        self._received_sequences: set[int] = set()
        self._pending_reliable: dict[int, _PendingReliablePacket] = {}
        self._ordered_next_sequence_by_channel: dict[str, int] = {}
        self._ordered_buffers: dict[str, dict[int, CultNetRudpDeliveredFrame]] = {}

    @property
    def connected(self) -> bool:
        return self._connected

    @property
    def pending_reliable_sequences(self) -> tuple[int, ...]:
        return tuple(sorted(self._pending_reliable))

    def create_connect(self, now_ms: int = 0, payload: bytes = b"") -> CultNetRudpPacket:
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
        if not self._connected:
            raise ValueError("Cannot send RUDP data before the session is connected")

        options = options or CultNetRudpSendOptions()
        packet = self._create_packet(
            CultNetRudpPacketType.DATA,
            channel_id,
            payload,
            reliable=options.reliable,
            ordered=options.ordered,
            sequenced=options.sequenced,
        )
        if packet.reliable:
            self._track_reliable(packet, options.now_ms)
        return packet

    def receive(self, packet: CultNetRudpPacket, now_ms: int = 0) -> CultNetRudpReceiveResult:
        del now_ms
        self._require_connection(packet)
        self._apply_acknowledgements(packet)

        if packet.packet_type == CultNetRudpPacketType.ACCEPT:
            self._remember_received(packet.sequence)
            self._connected = True
            return CultNetRudpReceiveResult()

        if packet.packet_type == CultNetRudpPacketType.PING:
            self._remember_received(packet.sequence)
            return CultNetRudpReceiveResult(
                reply=self._create_packet(CultNetRudpPacketType.PONG, "control", packet.payload)
            )

        if packet.packet_type in (CultNetRudpPacketType.ACK, CultNetRudpPacketType.PONG):
            self._remember_received(packet.sequence)
            return CultNetRudpReceiveResult()

        if packet.packet_type != CultNetRudpPacketType.DATA:
            return CultNetRudpReceiveResult()

        duplicate = packet.sequence in self._received_sequences
        self._remember_received(packet.sequence)
        if duplicate:
            return CultNetRudpReceiveResult()

        frame = CultNetRudpDeliveredFrame(
            channel_id=packet.channel_id,
            payload=bytes(packet.payload),
            sequence=packet.sequence,
        )
        if not packet.ordered:
            return CultNetRudpReceiveResult(delivered=(frame,))
        return CultNetRudpReceiveResult(delivered=tuple(self._deliver_ordered(frame)))

    def create_ack(self) -> CultNetRudpPacket:
        return self._create_packet(CultNetRudpPacketType.ACK, "control", b"")

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

    def _apply_acknowledgements(self, packet: CultNetRudpPacket) -> None:
        self._pending_reliable.pop(packet.ack, None)
        for bit in range(32):
            if packet.ack_mask & (1 << bit):
                self._pending_reliable.pop(packet.ack - bit - 1, None)

    def _remember_received(self, sequence: int) -> None:
        self._received_sequences.add(sequence)
        if self._highest_received_sequence is None or sequence > self._highest_received_sequence:
            self._highest_received_sequence = sequence

    def _ack_state(self) -> tuple[int, int]:
        ack = self._highest_received_sequence or 0
        ack_mask = 0
        for bit in range(32):
            if ack > bit and (ack - bit - 1) in self._received_sequences:
                ack_mask |= 1 << bit
        return ack, ack_mask

    def _deliver_ordered(self, frame: CultNetRudpDeliveredFrame) -> list[CultNetRudpDeliveredFrame]:
        next_sequence = self._ordered_next_sequence_by_channel.get(frame.channel_id)
        if next_sequence is None:
            self._ordered_next_sequence_by_channel[frame.channel_id] = frame.sequence + 1
            return [frame, *self._drain_ordered(frame.channel_id)]
        if frame.sequence < next_sequence:
            return []
        if frame.sequence > next_sequence:
            self._ordered_buffers.setdefault(frame.channel_id, {})[frame.sequence] = frame
            return []

        self._ordered_next_sequence_by_channel[frame.channel_id] = next_sequence + 1
        return [frame, *self._drain_ordered(frame.channel_id)]

    def _drain_ordered(self, channel_id: str) -> list[CultNetRudpDeliveredFrame]:
        delivered: list[CultNetRudpDeliveredFrame] = []
        buffer = self._ordered_buffers.get(channel_id)
        if buffer is None:
            return delivered

        while True:
            next_sequence = self._ordered_next_sequence_by_channel[channel_id]
            frame = buffer.pop(next_sequence, None)
            if frame is None:
                break
            delivered.append(frame)
            self._ordered_next_sequence_by_channel[channel_id] = next_sequence + 1
        return delivered

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
        self.session = CultNetRudpSession(
            CultNetRudpSessionOptions(
                connection_id=options.connection_id,
                initial_sequence=options.initial_sequence,
                resend_delay_ms=options.resend_delay_ms,
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
        )
        self._bytes_received = 0
        self._bytes_sent = 0
        self._frames_received = 0
        self._frames_sent = 0
        self._delivered_frames: deque[CultNetTransportFrame] = deque()
        self._closed = False

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

    def connect(self, payload: bytes = b"") -> None:
        if self.mode != CultNetRudpSocketMode.CLIENT:
            raise ValueError("Only a client RUDP socket transport can initiate connect")
        self._send_packet(self.session.create_connect(_now_ms(), payload))

    def send(self, channel_id: str, payload: bytes) -> None:
        packet = self.session.send(
            channel_id,
            payload,
            _channel_send_options(channel_id, _now_ms()),
        )
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

        for frame in result.delivered:
            self._delivered_frames.append(
                CultNetTransportFrame(channel_id=frame.channel_id, payload=frame.payload)
            )
            self._frames_received += 1

        frame = self._delivered_frames.popleft() if self._delivered_frames else None
        if packet.packet_type == CultNetRudpPacketType.ACCEPT or frame is not None:
            self._send_packet(self.session.create_ack())
        return frame

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
