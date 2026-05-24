from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Protocol


@dataclass(frozen=True)
class CultCacheEnvelope:
    key: str
    type: str
    payload: bytes
    stored_at: str

    @classmethod
    def create(cls, *, key: str, type: str, payload: bytes) -> "CultCacheEnvelope":
        return cls(
            key=key,
            type=type,
            payload=payload,
            stored_at=datetime.now(timezone.utc).isoformat(),
        )


class BackingStore(Protocol):
    def pull_all(self) -> list[CultCacheEnvelope]:
        ...

    def push(self, envelope: CultCacheEnvelope) -> None:
        ...

    def delete(self, type: str, key: str) -> None:
        ...

    def push_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        existing = {
            (envelope.type, envelope.key): envelope for envelope in self.pull_all()
        }
        for envelope in envelopes:
            existing[(envelope.type, envelope.key)] = envelope
        self._replace_all(list(existing.values()))

    def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        ...
