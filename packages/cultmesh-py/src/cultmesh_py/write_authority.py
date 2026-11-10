"""The one question a CultMesh server asks before it applies a peer's write.

Mirrors C# ``ICultNetRemoteWriteAuthority``: a server with no authority refuses every peer put and
delete, so serving a node never lets a connected peer overwrite its documents by default.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import Protocol


class CultMeshRemoteWriteOperation(Enum):
    PUT = "put"
    DELETE = "delete"


@dataclass(frozen=True)
class CultMeshServerPeer:
    """The connection a write arrived on: its endpoint and transport, nothing the peer claimed."""

    endpoint: str
    transport: str


@dataclass(frozen=True)
class CultMeshRemoteWriteRequest:
    """One peer write as the authority sees it.

    ``schema_id`` is the canonical id the node resolved for the write, never an alias the peer sent.
    ``shard_id`` and ``shard_epoch`` are as the peer sent them.
    """

    peer: CultMeshServerPeer
    operation: CultMeshRemoteWriteOperation
    schema_id: str
    record_key: str
    shard_id: str | None = None
    shard_epoch: int | None = None


@dataclass(frozen=True)
class CultMeshRemoteWriteDecision:
    is_granted: bool
    reason: str | None = None

    @staticmethod
    def grant() -> CultMeshRemoteWriteDecision:
        return CultMeshRemoteWriteDecision(True)

    @staticmethod
    def refuse(reason: str | None = None) -> CultMeshRemoteWriteDecision:
        return CultMeshRemoteWriteDecision(False, reason)


class CultMeshRemoteWriteAuthority(Protocol):
    def decide(self, request: CultMeshRemoteWriteRequest) -> CultMeshRemoteWriteDecision: ...


class CultMeshGrantAllWrites:
    """Grants every peer write. For a host that has already authenticated its peers (or a test rig); it is
    an explicit choice because serving without an authority refuses writes."""

    def decide(self, request: CultMeshRemoteWriteRequest) -> CultMeshRemoteWriteDecision:
        return CultMeshRemoteWriteDecision.grant()
