from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Protocol


@dataclass(frozen=True)
class CultCacheSchemaCatalogMember:
    slot: int
    member_name: str
    type_name: str
    is_reference: bool = False
    is_many: bool = False
    target_schema_name: str | None = None
    is_name: bool = False
    index_alias: str | None = None


@dataclass(frozen=True)
class CultCacheSchemaCatalogEntry:
    schema_id: str
    schema_name: str
    schema_version: str
    content_hash: str
    canonical_schema_json: str
    compatible_schema_ids: tuple[str, ...] = field(default_factory=tuple)
    members: tuple[CultCacheSchemaCatalogMember, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class CultCacheEnvelope:
    key: str
    type: str
    payload: bytes
    stored_at: str
    schema_id: str | None = None
    catalog_entry: CultCacheSchemaCatalogEntry | None = None

    @classmethod
    def create(
        cls,
        *,
        key: str,
        type: str,
        payload: bytes,
        schema_id: str | None = None,
        catalog_entry: CultCacheSchemaCatalogEntry | None = None,
    ) -> "CultCacheEnvelope":
        return cls(
            key=key,
            type=type,
            payload=payload,
            stored_at=datetime.now(timezone.utc).isoformat(),
            schema_id=schema_id,
            catalog_entry=catalog_entry,
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
