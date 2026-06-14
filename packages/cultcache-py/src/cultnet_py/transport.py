from __future__ import annotations

from dataclasses import dataclass
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
