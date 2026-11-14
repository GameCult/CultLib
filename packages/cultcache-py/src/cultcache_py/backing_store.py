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
    """The members every store has. A store may also offer `read(key) -> tuple[CultCacheEnvelope, str] | None` (the record at the
    key, of any type, and its content version, from one read) and `commit_if(envelope, expected: str | None) -> str | None` (write
    only if the record at the key is still the version `expected`, None meaning no record; returns the stored_at written, or None
    with nothing written). They are looked up with getattr, not Protocol members, and `CultCache.update` needs both. `push` and
    `push_all` may report the stored_at they wrote (`str` and `dict[str, str]` by key); None keeps the cache's own stamp. A store
    stamps a record that replaces another strictly later than the one it replaces."""

    def pull_all(self) -> list[CultCacheEnvelope]:
        ...

    def push(self, envelope: CultCacheEnvelope) -> str | None:
        ...

    def delete(self, type: str, key: str) -> None:
        ...

    def pull_or_absent(self) -> list[CultCacheEnvelope] | None:
        """The envelopes the reader reads (empty included), None when nothing is at the path; raises when the path cannot be read.

        The default is pull_all, which cannot say absent: a store that inherits it is always a store the reader read. A store kind
        that can tell nothing-at-the-path overrides this, and the default push_all then writes where nothing is on a soft push."""
        return self.pull_all()

    def push_all(self, envelopes: list[CultCacheEnvelope], soft: bool = False) -> dict[str, str] | None:
        # Soft: write only where pull_or_absent says nothing is; skip over a store it read, empty included; its raise refuses.
        pulled = self.pull_or_absent()
        if soft and pulled is not None:
            return
        pulled = pulled or []
        existing = {(envelope.type, envelope.key): envelope for envelope in pulled}
        for envelope in envelopes:
            existing[(envelope.type, envelope.key)] = envelope
        self._replace_all(list(existing.values()))

    def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        ...
