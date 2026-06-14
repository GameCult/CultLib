from __future__ import annotations

from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Any, Protocol
from urllib.parse import urlparse

from .client import CultNetRawClient
from .shard_catalog import CultNetShardDescriptor
from .shard_log import CultNetShardLogResponse
from .snapshot import CultNetRawSnapshotResponse


class CultNetShardLogFetcher(Protocol):
    def fetch(
        self,
        shard: CultNetShardDescriptor,
        *,
        after_sequence: int,
        limit: int | None = None,
    ) -> CultNetShardLogResponse:
        ...


class CultNetShardSnapshotFetcher(Protocol):
    def fetch(self, shard: CultNetShardDescriptor) -> CultNetRawSnapshotResponse:
        ...


@dataclass(frozen=True)
class CultNetShardReplicaCursor:
    shard_id: str
    shard_epoch: int
    last_applied_sequence: int
    updated_at: str


class CultNetShardReplicaCursorStore(Protocol):
    def read(self, shard_id: str) -> CultNetShardReplicaCursor | None:
        ...

    def write(self, cursor: CultNetShardReplicaCursor) -> None:
        ...


@dataclass
class CultNetInMemoryShardReplicaCursorStore:
    _cursors: dict[str, CultNetShardReplicaCursor] = field(default_factory=dict)

    def read(self, shard_id: str) -> CultNetShardReplicaCursor | None:
        _require_non_empty(shard_id, "shard_id")
        return self._cursors.get(shard_id)

    def write(self, cursor: CultNetShardReplicaCursor) -> None:
        _require_non_empty(cursor.shard_id, "cursor.shard_id")
        self._cursors[cursor.shard_id] = cursor


@dataclass(frozen=True)
class CultNetSchemaShardLogFetcher:
    timeout_seconds: float = 4.0

    def fetch(
        self,
        shard: CultNetShardDescriptor,
        *,
        after_sequence: int,
        limit: int | None = None,
    ) -> CultNetShardLogResponse:
        if after_sequence < 0:
            raise ValueError("after_sequence must be non-negative")
        endpoint = _resolve_primary_endpoint(shard)
        host, port = _parse_endpoint(endpoint)
        return CultNetRawClient(host, port, self.timeout_seconds).fetch_shard_log_response(
            shard_id=shard.shard_id,
            shard_epoch=shard.epoch,
            after_sequence=after_sequence,
            limit=limit,
        )


@dataclass(frozen=True)
class CultNetSchemaShardSnapshotFetcher:
    timeout_seconds: float = 4.0

    def fetch(self, shard: CultNetShardDescriptor) -> CultNetRawSnapshotResponse:
        endpoint = _resolve_primary_endpoint(shard)
        host, port = _parse_endpoint(endpoint)
        return CultNetRawClient(host, port, self.timeout_seconds).fetch_snapshot_response(
            schema_ids=list(shard.schema_ids) or None,
            shard_id=shard.shard_id,
            shard_epoch=shard.epoch,
        )


@dataclass
class CultNetShardReplicatorOptions:
    fetcher: CultNetShardLogFetcher | None = None
    snapshot_fetcher: CultNetShardSnapshotFetcher | None = None
    cursor_store: CultNetShardReplicaCursorStore | None = None
    batch_size: int | None = 256


@dataclass
class CultNetShardReplicator:
    database: Any
    options: CultNetShardReplicatorOptions = field(default_factory=CultNetShardReplicatorOptions)
    _applied_sequences: dict[str, int] = field(default_factory=dict)

    def pull_once(self, shard: CultNetShardDescriptor) -> int:
        if self.options.fetcher is None:
            raise ValueError("A shard log fetcher is required before pulling replication")
        if shard.is_primary:
            raise ValueError(f"Shard {shard.shard_id!r} is primary on this node and does not need replica pulling")
        after_sequence = self._after_sequence(shard)
        response = self.options.fetcher.fetch(
            shard,
            after_sequence=after_sequence,
            limit=self.options.batch_size,
        )
        if response.resync_required and response.reason == "compacted_history":
            if self.options.snapshot_fetcher is None:
                raise ValueError(
                    f"Shard {shard.shard_id!r} requires snapshot resync, but no shard snapshot fetcher is configured"
                )
            snapshot = self.options.snapshot_fetcher.fetch(shard)
            self.database.apply_snapshot_response(snapshot)
            sequence = snapshot.shard_log_sequence or 0
            self._write_cursor(shard, sequence)
            return sequence
        self.database.apply_shard_log_response(response)
        sequence = max(after_sequence, response.last_sequence)
        self._write_cursor(shard, sequence)
        return sequence

    def _after_sequence(self, shard: CultNetShardDescriptor) -> int:
        current = self._applied_sequences.get(shard.shard_id, 0)
        if current > 0 or self.options.cursor_store is None:
            return current
        cursor = self.options.cursor_store.read(shard.shard_id)
        if cursor is None or cursor.shard_epoch != shard.epoch:
            return current
        self._applied_sequences[shard.shard_id] = cursor.last_applied_sequence
        return cursor.last_applied_sequence

    def _write_cursor(self, shard: CultNetShardDescriptor, sequence: int) -> None:
        self._applied_sequences[shard.shard_id] = sequence
        if self.options.cursor_store is None:
            return
        self.options.cursor_store.write(CultNetShardReplicaCursor(
            shard_id=shard.shard_id,
            shard_epoch=shard.epoch,
            last_applied_sequence=sequence,
            updated_at=datetime.now(UTC).isoformat(),
        ))


def _resolve_primary_endpoint(shard: CultNetShardDescriptor) -> str:
    if not shard.primary_endpoints:
        raise ValueError(f"Shard {shard.shard_id!r} does not advertise a primary endpoint")
    return shard.primary_endpoints[0]


def _parse_endpoint(endpoint: str) -> tuple[str, int]:
    _require_non_empty(endpoint, "endpoint")
    parsed = urlparse(endpoint if "://" in endpoint else f"cultnet://{endpoint}")
    if not parsed.hostname or parsed.port is None:
        raise ValueError(f"CultNet endpoint must include host and port: {endpoint!r}")
    return parsed.hostname, parsed.port


def _require_non_empty(value: str, name: str) -> None:
    if not value or not value.strip():
        raise ValueError(f"{name} must be non-empty")
