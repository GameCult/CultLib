from __future__ import annotations

from dataclasses import dataclass
from typing import Any, BinaryIO

from .framing import read_frame, write_frame


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
