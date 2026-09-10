from __future__ import annotations

from typing import BinaryIO


def encode_frame(payload: bytes) -> bytes:
    if len(payload) > 0xFFFFFFFF:
        raise ValueError("CultNet frames cannot exceed 4GiB")
    return len(payload).to_bytes(4, "big") + payload


def decode_frame(frame: bytes) -> bytes:
    if len(frame) < 4:
        raise ValueError("CultNet frame is shorter than the 4-byte length prefix")
    length = int.from_bytes(frame[:4], "big")
    payload = frame[4:]
    if len(payload) != length:
        raise ValueError(f"CultNet frame declared {length} bytes but carried {len(payload)}")
    return payload


def read_frame(stream: BinaryIO) -> bytes:
    prefix = stream.read(4)
    if prefix == b"":
        raise EOFError("No CultNet frame prefix available")
    if len(prefix) != 4:
        raise EOFError("Partial CultNet frame prefix")
    length = int.from_bytes(prefix, "big")
    payload = stream.read(length)
    if len(payload) != length:
        raise EOFError("Partial CultNet frame payload")
    return payload


def write_frame(stream: BinaryIO, payload: bytes) -> None:
    stream.write(encode_frame(payload))
