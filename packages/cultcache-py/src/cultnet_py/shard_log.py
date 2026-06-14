from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class CultNetShardLogEntry:
    sequence: int
    change_kind: str
    put: dict[str, Any] | None = None
    delete: dict[str, Any] | None = None
    committed_at: str | None = None

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetShardLogEntry":
        sequence = int(value.get("sequence") or 0)
        change_kind = str(value.get("changeKind") or "")
        if sequence <= 0:
            raise ValueError("shard log entry sequence must be positive")
        if change_kind not in {"added", "updated", "removed"}:
            raise ValueError(f"unsupported shard log changeKind {change_kind!r}")
        put = value.get("put")
        delete = value.get("delete")
        return cls(
            sequence=sequence,
            change_kind=change_kind,
            put=dict(put) if isinstance(put, dict) else None,
            delete=dict(delete) if isinstance(delete, dict) else None,
            committed_at=_optional_string(value.get("committedAt")),
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "sequence": self.sequence,
            "changeKind": self.change_kind,
        }
        if self.put is not None:
            wire["put"] = self.put
        if self.delete is not None:
            wire["delete"] = self.delete
        if self.committed_at is not None:
            wire["committedAt"] = self.committed_at
        return wire


@dataclass(frozen=True)
class CultNetShardLogResponse:
    message_id: str
    shard_id: str
    shard_epoch: int
    entries: tuple[CultNetShardLogEntry, ...]
    resync_required: bool = False
    reason: str | None = None
    compacted_through: int | None = None

    @classmethod
    def from_wire(cls, response: dict[str, Any]) -> "CultNetShardLogResponse":
        if response.get("schemaVersion") != "cultnet.shard_log_response.v0":
            raise ValueError(f"Expected cultnet.shard_log_response.v0, received {response.get('schemaVersion')!r}")
        entries = tuple(
            CultNetShardLogEntry.from_wire(entry)
            for entry in response.get("entries") or []
            if isinstance(entry, dict)
        )
        return cls(
            message_id=str(response.get("messageId") or ""),
            shard_id=str(response.get("shardId") or ""),
            shard_epoch=int(response.get("shardEpoch") or 0),
            entries=entries,
            resync_required=response.get("resyncRequired") is True,
            reason=_optional_string(response.get("reason")),
            compacted_through=_optional_int(response.get("compactedThrough")),
        )

    @property
    def last_sequence(self) -> int:
        if not self.entries:
            return self.compacted_through or 0
        return max(entry.sequence for entry in self.entries)

    def require_usable(self) -> "CultNetShardLogResponse":
        if self.resync_required:
            raise ValueError(f"Shard log response requires resync: {self.reason or 'unspecified'}")
        return self

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": self.message_id,
            "shardId": self.shard_id,
            "shardEpoch": self.shard_epoch,
            "entries": [entry.to_wire() for entry in self.entries],
            "resyncRequired": self.resync_required,
        }
        if self.reason is not None:
            wire["reason"] = self.reason
        if self.compacted_through is not None:
            wire["compactedThrough"] = self.compacted_through
        return wire


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None


def _optional_int(value: Any) -> int | None:
    if value is None:
        return None
    return int(value)
