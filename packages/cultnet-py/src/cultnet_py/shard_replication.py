from __future__ import annotations

from dataclasses import dataclass, field
from datetime import UTC, datetime
import hashlib
from pathlib import Path
import threading
from typing import Any, Callable, Iterable, Protocol
from urllib.parse import urlparse

import msgpack

from .client import CultNetRawClient
from .shard_catalog import CultNetShardDescriptor
from .shard_log import CultNetShardLogResponse
from .snapshot import CultNetRawSnapshotResponse


class CultNetShardWriteForwarder(Protocol):
    def forward_put(self, shard: CultNetShardDescriptor, message: dict[str, Any]) -> None:
        ...

    def forward_delete(self, shard: CultNetShardDescriptor, message: dict[str, Any]) -> None:
        ...


class CultNetShardMutationLogStore(Protocol):
    def shard_ids(self) -> list[str]:
        ...

    def read(self, shard_id: str, *, after_sequence: int = 0, limit: int | None = None) -> list[dict[str, Any]]:
        ...

    def append(self, shard_id: str, entry: dict[str, Any]) -> None:
        ...

    def get_compacted_through(self, shard_id: str) -> int:
        ...

    def compact_through(self, shard_id: str, sequence: int) -> None:
        ...


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
class CultNetFileShardReplicaCursorStore:
    file_path: str | Path

    def read(self, shard_id: str) -> CultNetShardReplicaCursor | None:
        _require_non_empty(shard_id, "shard_id")
        return next((cursor for cursor in self._read_all() if cursor.shard_id == shard_id), None)

    def write(self, cursor: CultNetShardReplicaCursor) -> None:
        _require_non_empty(cursor.shard_id, "cursor.shard_id")
        cursors = [
            existing
            for existing in self._read_all()
            if existing.shard_id != cursor.shard_id
        ]
        cursors.append(cursor)
        path = Path(self.file_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(msgpack.packb(
            [self._cursor_to_wire(value) for value in sorted(cursors, key=lambda value: value.shard_id)],
            use_bin_type=True,
        ))

    def _read_all(self) -> list[CultNetShardReplicaCursor]:
        path = Path(self.file_path)
        if not path.exists() or path.stat().st_size == 0:
            return []
        value = msgpack.unpackb(path.read_bytes(), raw=False)
        if not isinstance(value, list):
            raise ValueError("CultNet shard replica cursor file must contain a MessagePack array")
        return [
            self._cursor_from_wire(item)
            for item in value
            if isinstance(item, dict)
        ]

    @staticmethod
    def _cursor_to_wire(cursor: CultNetShardReplicaCursor) -> dict[str, Any]:
        return {
            "shardId": cursor.shard_id,
            "shardEpoch": cursor.shard_epoch,
            "lastAppliedSequence": cursor.last_applied_sequence,
            "updatedAt": cursor.updated_at,
        }

    @staticmethod
    def _cursor_from_wire(value: dict[str, Any]) -> CultNetShardReplicaCursor:
        return CultNetShardReplicaCursor(
            shard_id=str(value.get("shardId") or ""),
            shard_epoch=int(value.get("shardEpoch") or 0),
            last_applied_sequence=int(value.get("lastAppliedSequence") or 0),
            updated_at=str(value.get("updatedAt") or ""),
        )


@dataclass(frozen=True)
class CultNetFileShardMutationLogStore:
    root_path: str | Path

    def __post_init__(self) -> None:
        Path(self.root_path).mkdir(parents=True, exist_ok=True)

    def shard_ids(self) -> list[str]:
        shards = []
        for path in Path(self.root_path).glob("*.meta.mpack"):
            value = msgpack.unpackb(path.read_bytes(), raw=False)
            if isinstance(value, dict) and value.get("shardId"):
                shards.append(str(value["shardId"]))
        return sorted(set(shards))

    def read(self, shard_id: str, *, after_sequence: int = 0, limit: int | None = None) -> list[dict[str, Any]]:
        _require_non_empty(shard_id, "shard_id")
        if after_sequence < 0:
            raise ValueError("after_sequence must be non-negative")
        entries = [
            entry
            for entry in self._read_entries(shard_id)
            if int(entry.get("sequence") or 0) > after_sequence
        ]
        entries.sort(key=lambda entry: int(entry.get("sequence") or 0))
        if limit is not None:
            if limit < 0:
                raise ValueError("limit must be non-negative")
            entries = entries[:limit]
        return entries

    def append(self, shard_id: str, entry: dict[str, Any]) -> None:
        _require_non_empty(shard_id, "shard_id")
        sequence = int(entry.get("sequence") or 0)
        if sequence <= 0:
            raise ValueError("shard log entry sequence must be positive")
        entries = [
            existing
            for existing in self._read_entries(shard_id)
            if int(existing.get("sequence") or 0) != sequence
        ]
        entries.append(dict(entry))
        entries.sort(key=lambda value: int(value.get("sequence") or 0))
        self._entry_path(shard_id).write_bytes(msgpack.packb(entries, use_bin_type=True))
        self._write_metadata(shard_id, self._read_metadata(shard_id))

    def get_compacted_through(self, shard_id: str) -> int:
        _require_non_empty(shard_id, "shard_id")
        metadata = self._read_metadata(shard_id)
        return int(metadata.get("compactedThrough") or 0)

    def compact_through(self, shard_id: str, sequence: int) -> None:
        _require_non_empty(shard_id, "shard_id")
        if sequence < 0:
            raise ValueError("sequence must be non-negative")
        current = self.get_compacted_through(shard_id)
        if sequence <= current:
            return
        retained = [
            entry
            for entry in self._read_entries(shard_id)
            if int(entry.get("sequence") or 0) > sequence
        ]
        self._entry_path(shard_id).write_bytes(msgpack.packb(retained, use_bin_type=True))
        self._write_metadata(shard_id, {"compactedThrough": sequence})

    def _read_entries(self, shard_id: str) -> list[dict[str, Any]]:
        path = self._entry_path(shard_id)
        if not path.exists() or path.stat().st_size == 0:
            return []
        value = msgpack.unpackb(path.read_bytes(), raw=False)
        if not isinstance(value, list):
            raise ValueError("CultNet shard mutation log file must contain a MessagePack array")
        return [dict(entry) for entry in value if isinstance(entry, dict)]

    def _read_metadata(self, shard_id: str) -> dict[str, Any]:
        path = self._metadata_path(shard_id)
        if not path.exists() or path.stat().st_size == 0:
            return {}
        value = msgpack.unpackb(path.read_bytes(), raw=False)
        if not isinstance(value, dict):
            raise ValueError("CultNet shard mutation log metadata must be a MessagePack map")
        return dict(value)

    def _write_metadata(self, shard_id: str, metadata: dict[str, Any]) -> None:
        payload = dict(metadata)
        payload["shardId"] = shard_id
        payload.setdefault("compactedThrough", 0)
        self._metadata_path(shard_id).write_bytes(msgpack.packb(payload, use_bin_type=True))

    def _entry_path(self, shard_id: str) -> Path:
        return Path(self.root_path) / f"{_hash_shard_id(shard_id)}.mpack"

    def _metadata_path(self, shard_id: str) -> Path:
        return Path(self.root_path) / f"{_hash_shard_id(shard_id)}.meta.mpack"


@dataclass(frozen=True)
class CultNetSchemaWriteForwarder:
    timeout_seconds: float = 4.0

    def forward_put(self, shard: CultNetShardDescriptor, message: dict[str, Any]) -> None:
        if message.get("schemaVersion") != "cultnet.document_put_raw.v0":
            raise ValueError("forward_put requires a cultnet.document_put_raw.v0 message")
        wire = dict(message)
        wire.setdefault("shardId", shard.shard_id)
        wire.setdefault("shardEpoch", shard.epoch)
        self._send(shard, wire)

    def forward_delete(self, shard: CultNetShardDescriptor, message: dict[str, Any]) -> None:
        if message.get("schemaVersion") != "cultnet.document_delete.v0":
            raise ValueError("forward_delete requires a cultnet.document_delete.v0 message")
        wire = dict(message)
        wire.setdefault("shardId", shard.shard_id)
        wire.setdefault("shardEpoch", shard.epoch)
        self._send(shard, wire)

    def _send(self, shard: CultNetShardDescriptor, message: dict[str, Any]) -> None:
        endpoint = _resolve_primary_endpoint(shard)
        host, port = _parse_endpoint(endpoint)
        CultNetRawClient(host, port, self.timeout_seconds).send(message)


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
    poll_interval_seconds: float = 1.0
    on_error: Callable[[Exception], None] | None = None


@dataclass
class CultNetShardReplicator:
    database: Any
    options: CultNetShardReplicatorOptions = field(default_factory=CultNetShardReplicatorOptions)
    _applied_sequences: dict[str, int] = field(default_factory=dict)
    _stop_event: threading.Event = field(default_factory=threading.Event)
    _threads: list[threading.Thread] = field(default_factory=list)
    _pulls_in_flight: set[str] = field(default_factory=set)
    _gate: threading.Lock = field(default_factory=threading.Lock)
    _disposed: bool = False

    def start(self, shards: Iterable[CultNetShardDescriptor]) -> None:
        self._throw_if_disposed()
        if self.options.fetcher is None:
            raise ValueError("A shard log fetcher is required before starting replication")
        if self.options.poll_interval_seconds <= 0:
            raise ValueError("poll_interval_seconds must be positive")
        if self._threads:
            return
        self._stop_event = threading.Event()
        for shard in shards:
            if shard.is_primary or not shard.primary_endpoints:
                continue
            thread = threading.Thread(
                target=self._pull_loop,
                args=(shard,),
                name=f"cultnet-shard-replicator-{shard.shard_id}",
                daemon=True,
            )
            self._threads.append(thread)
            thread.start()

    def stop(self, *, timeout_seconds: float | None = 2.0) -> None:
        self._stop_event.set()
        for thread in list(self._threads):
            thread.join(timeout=timeout_seconds)
        self._threads = [thread for thread in self._threads if thread.is_alive()]
        with self._gate:
            if not self._threads:
                self._pulls_in_flight.clear()

    def close(self) -> None:
        if self._disposed:
            return
        self.stop()
        self._disposed = True

    def __enter__(self) -> "CultNetShardReplicator":
        self._throw_if_disposed()
        return self

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        self.close()

    def pull_once(self, shard: CultNetShardDescriptor) -> int:
        self._throw_if_disposed()
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

    def _pull_loop(self, shard: CultNetShardDescriptor) -> None:
        while not self._stop_event.is_set():
            self._pull_if_idle(shard)
            self._stop_event.wait(self.options.poll_interval_seconds)

    def _pull_if_idle(self, shard: CultNetShardDescriptor) -> None:
        with self._gate:
            if shard.shard_id in self._pulls_in_flight:
                return
            self._pulls_in_flight.add(shard.shard_id)
        try:
            self.pull_once(shard)
        except Exception as error:
            if self.options.on_error is not None:
                self.options.on_error(error)
        finally:
            with self._gate:
                self._pulls_in_flight.discard(shard.shard_id)

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("CultNetShardReplicator is closed")


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


def _hash_shard_id(shard_id: str) -> str:
    return hashlib.sha256(shard_id.encode("utf-8")).hexdigest()
