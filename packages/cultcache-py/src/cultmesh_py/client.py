from __future__ import annotations

import threading
from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Any, Callable
from urllib.parse import urlparse

from cultcache_py.documents import DocumentDefinition
from cultnet_py import CultNetRawClient, CultNetRawSnapshotResponse, hello

from .wire import (
    PEER_EXCHANGE_RESPONSE,
    VERSE_CATALOG_RESPONSE,
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshVerseCatalog,
    CultMeshVerseDescriptor,
    peer_from_wire,
    peer_exchange_request,
    verse_catalog_request,
    verse_from_wire,
)


@dataclass(frozen=True)
class CultMeshDiscoveryClient:
    host: str
    port: int
    timeout_seconds: float = 4.0

    def request(self, message: dict[str, Any], *, expected_schema_version: str) -> dict[str, Any]:
        return CultNetRawClient(self.host, self.port, self.timeout_seconds).request(
            message,
            expected_schema_version=expected_schema_version,
        )

    def request_verse_catalog(
        self,
        message_id: str = "cultmesh-python-verse-catalog",
        *,
        verse_ids: list[str] | None = None,
        transport_version: str | None = None,
    ) -> dict[str, Any]:
        return self.request(
            verse_catalog_request(message_id, verse_ids=verse_ids, transport_version=transport_version),
            expected_schema_version=VERSE_CATALOG_RESPONSE,
        )

    def request_peer_exchange(
        self,
        message_id: str = "cultmesh-python-peer-exchange",
        *,
        verse_id: str,
        roles: list[str] | None = None,
        known_peer_ids: list[str] | None = None,
        limit: int | None = None,
    ) -> dict[str, Any]:
        return self.request(
            peer_exchange_request(
                message_id,
                verse_id=verse_id,
                roles=roles,
                known_peer_ids=known_peer_ids,
                limit=limit,
            ),
            expected_schema_version=PEER_EXCHANGE_RESPONSE,
        )

    def fetch_verses(
        self,
        *,
        verse_ids: list[str] | None = None,
        transport_version: str | None = None,
    ) -> list[CultMeshVerseDescriptor]:
        response = self.request_verse_catalog(verse_ids=verse_ids, transport_version=transport_version)
        return [verse_from_wire(verse) for verse in response.get("verses", [])]

    def fetch_peers(
        self,
        *,
        verse_id: str,
        roles: list[str] | None = None,
        known_peer_ids: list[str] | None = None,
        limit: int | None = None,
    ) -> list[CultMeshPeerCard]:
        response = self.request_peer_exchange(
            verse_id=verse_id,
            roles=roles,
            known_peer_ids=known_peer_ids,
            limit=limit,
        )
        return [peer_from_wire(peer) for peer in response.get("peers", [])]

    def sync_verse_catalog(
        self,
        catalog: CultMeshVerseCatalog,
        *,
        verse_ids: list[str] | None = None,
        transport_version: str | None = None,
    ) -> list[CultMeshVerseDescriptor]:
        response = self.request_verse_catalog(verse_ids=verse_ids, transport_version=transport_version)
        return catalog.apply_response(response)

    def discover_verse_catalog(
        self,
        catalog: CultMeshVerseCatalog,
        *,
        endpoints: list[str] | None = None,
        transport_version: str | None = None,
    ) -> int:
        if endpoints is None:
            return len(self.sync_verse_catalog(catalog, transport_version=transport_version))
        count = 0
        for endpoint in _distinct_non_empty(endpoints):
            client = type(self).from_endpoint(endpoint, timeout_seconds=self.timeout_seconds)
            count += client.discover_verse_catalog(catalog, transport_version=transport_version)
        return count

    def fanout_verse_catalog(
        self,
        catalog: CultMeshVerseCatalog,
        *,
        transport_version: str | None = None,
    ) -> int:
        endpoints = [
            endpoint
            for verse in catalog.verses
            for endpoint in verse.discovery_endpoints
        ]
        return self.discover_verse_catalog(
            catalog,
            endpoints=endpoints,
            transport_version=transport_version,
        )

    def sync_peer_catalog(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
        known_peer_ids: list[str] | None = None,
        limit: int | None = None,
    ) -> list[CultMeshPeerCard]:
        response = self.request_peer_exchange(
            verse_id=verse_id,
            roles=roles,
            known_peer_ids=known_peer_ids,
            limit=limit,
        )
        return catalog.apply_response(response)

    def discover_peer_catalog(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        endpoints: list[str] | None = None,
        roles: list[str] | None = None,
        limit: int | None = None,
    ) -> int:
        if endpoints is None:
            known_peer_ids = [peer.peer_id for peer in catalog.find(verse_id)]
            return len(self.sync_peer_catalog(
                catalog,
                verse_id=verse_id,
                roles=roles,
                known_peer_ids=known_peer_ids,
                limit=limit,
            ))
        count = 0
        for endpoint in _distinct_non_empty(endpoints):
            client = type(self).from_endpoint(endpoint, timeout_seconds=self.timeout_seconds)
            count += client.discover_peer_catalog(catalog, verse_id=verse_id, roles=roles, limit=limit)
        return count

    def fanout_peer_catalog(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
        limit: int | None = None,
    ) -> int:
        endpoints = [
            endpoint
            for peer in catalog.find(verse_id)
            for endpoint in peer.endpoints
        ]
        return self.discover_peer_catalog(
            catalog,
            verse_id=verse_id,
            endpoints=endpoints,
            roles=roles,
            limit=limit,
        )

    def submit_simulation_observation(self, message: dict[str, Any]) -> dict[str, Any]:
        return self.request(
            message,
            expected_schema_version="cultnet.simulation_consensus_candidate.v0",
        )

    def fanout_simulation_observation(
        self,
        catalog: CultMeshPeerCatalog,
        message: dict[str, Any],
        *,
        verse_id: str,
        roles: list[str] | None = None,
        on_error: Callable[[str, Exception], None] | None = None,
    ) -> list[dict[str, Any]]:
        candidates = []
        for endpoint in self._fanout_endpoints(catalog, verse_id=verse_id, roles=roles):
            try:
                client = type(self).from_endpoint(endpoint, timeout_seconds=self.timeout_seconds)
                candidates.append(client.submit_simulation_observation(message))
            except Exception as error:
                if on_error is None:
                    raise
                on_error(endpoint, error)
        return candidates

    def fanout_snapshot_responses(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        on_error: Callable[[str, Exception], None] | None = None,
    ) -> list[CultNetRawSnapshotResponse]:
        responses: list[CultNetRawSnapshotResponse] = []
        for endpoint in self._fanout_endpoints(catalog, verse_id=verse_id, roles=roles):
            try:
                client = type(self).from_endpoint(endpoint, timeout_seconds=self.timeout_seconds)
                raw_client = CultNetRawClient(client.host, client.port, client.timeout_seconds)
                responses.append(
                    raw_client.fetch_snapshot_response(
                        schema_ids=schema_ids,
                        record_keys=record_keys,
                        shard_id=shard_id,
                        shard_epoch=shard_epoch,
                    )
                )
            except Exception as error:
                if on_error is None:
                    raise
                on_error(endpoint, error)
        return responses

    def sync_snapshots(
        self,
        database: Any,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        on_error: Callable[[str, Exception], None] | None = None,
    ) -> list[Any]:
        applied = []
        for response in self.fanout_snapshot_responses(
            catalog,
            verse_id=verse_id,
            roles=roles,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            on_error=on_error,
        ):
            applied.extend(database.apply_snapshot_response(response))
        return applied

    def sync_documents(
        self,
        database: Any,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        documents: list[tuple[DocumentDefinition[Any], str]],
        roles: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        on_error: Callable[[str, Exception], None] | None = None,
    ) -> list[Any]:
        if not documents:
            return []
        schema_ids = _distinct_non_empty([
            document.catalog_entry().schema_id
            for document, _key in documents
        ])
        record_keys = _distinct_non_empty([key for _document, key in documents])
        self.sync_snapshots(
            database,
            catalog,
            verse_id=verse_id,
            roles=roles,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            on_error=on_error,
        )
        return [
            database.get_required(document, key)
            for document, key in documents
        ]

    def _fanout_endpoints(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
    ) -> list[str]:
        requested_roles = set(roles or [])
        endpoints = [
            endpoint
            for peer in catalog.find(verse_id)
            if not requested_roles or requested_roles.intersection(peer.roles)
            for endpoint in peer.endpoints
        ]
        return _distinct_non_empty(endpoints)

    @classmethod
    def from_endpoint(cls, endpoint: str, *, timeout_seconds: float = 4.0) -> "CultMeshDiscoveryClient":
        host, port = _parse_endpoint(endpoint)
        return cls(host, port, timeout_seconds=timeout_seconds)


class CultMeshVerseDiscoveryClient(CultMeshDiscoveryClient):
    def discover(
        self,
        catalog: CultMeshVerseCatalog,
        *,
        endpoints: list[str] | None = None,
        transport_version: str | None = None,
    ) -> int:
        return self.discover_verse_catalog(
            catalog,
            endpoints=endpoints,
            transport_version=transport_version,
        )

    def fanout(
        self,
        catalog: CultMeshVerseCatalog,
        *,
        transport_version: str | None = None,
    ) -> int:
        return self.fanout_verse_catalog(catalog, transport_version=transport_version)


class CultMeshPeerExchangeClient(CultMeshDiscoveryClient):
    def discover(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        endpoints: list[str] | None = None,
        roles: list[str] | None = None,
        limit: int | None = None,
    ) -> int:
        return self.discover_peer_catalog(
            catalog,
            verse_id=verse_id,
            endpoints=endpoints,
            roles=roles,
            limit=limit,
        )

    def fanout(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
        limit: int | None = None,
    ) -> int:
        return self.fanout_peer_catalog(catalog, verse_id=verse_id, roles=roles, limit=limit)


@dataclass
class CultMeshSimulationObservationFanout:
    client: CultMeshDiscoveryClient
    peer_catalog: CultMeshPeerCatalog
    verse_id: str
    roles: list[str] | None = None
    interval_seconds: float = 1.0
    on_candidate: Callable[[dict[str, Any]], None] | None = None
    on_error: Callable[[str, Exception], None] | None = None
    _pending: list[dict[str, Any]] = field(default_factory=list, init=False, repr=False)
    _lock: threading.Lock = field(default_factory=threading.Lock, init=False, repr=False)
    _stop: threading.Event = field(default_factory=threading.Event, init=False, repr=False)
    _thread: threading.Thread | None = field(default=None, init=False, repr=False)

    def enqueue(self, message: dict[str, Any]) -> None:
        with self._lock:
            self._pending.append(dict(message))

    def pending_count(self) -> int:
        with self._lock:
            return len(self._pending)

    def flush(self) -> list[dict[str, Any]]:
        with self._lock:
            messages = list(self._pending)
            self._pending.clear()
        candidates = []
        failed_messages: list[dict[str, Any]] = []
        for message in messages:
            try:
                message_candidates = self.client.fanout_simulation_observation(
                    self.peer_catalog,
                    message,
                    verse_id=self.verse_id,
                    roles=self.roles,
                    on_error=self.on_error,
                )
            except Exception:
                failed_messages.append(message)
                continue
            if not message_candidates:
                failed_messages.append(message)
                continue
            candidates.extend(message_candidates)
        if failed_messages:
            with self._lock:
                self._pending = [*failed_messages, *self._pending]
        for candidate in candidates:
            if self.on_candidate is not None:
                self.on_candidate(candidate)
        return candidates

    def start(self) -> "CultMeshSimulationObservationFanout":
        if self._thread is not None:
            return self
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(2.0, self.interval_seconds * 2.0))
            self._thread = None

    def __enter__(self) -> "CultMeshSimulationObservationFanout":
        return self.start()

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.stop()

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            self.flush()


@dataclass
class CultMeshSnapshotFanout:
    client: CultMeshDiscoveryClient
    database: Any
    peer_catalog: CultMeshPeerCatalog
    verse_id: str
    roles: list[str] | None = None
    documents: list[tuple[DocumentDefinition[Any], str]] | None = None
    schema_ids: list[str] | None = None
    record_keys: list[str] | None = None
    shard_id: str | None = None
    shard_epoch: int | None = None
    interval_seconds: float = 1.0
    on_applied: Callable[[Any], None] | None = None
    on_document: Callable[[Any], None] | None = None
    on_error: Callable[[str, Exception], None] | None = None
    _stop: threading.Event = field(default_factory=threading.Event, init=False, repr=False)
    _thread: threading.Thread | None = field(default=None, init=False, repr=False)

    def sync_once(self) -> list[Any]:
        if self.documents is not None:
            values = self.client.sync_documents(
                self.database,
                self.peer_catalog,
                verse_id=self.verse_id,
                roles=self.roles,
                documents=self.documents,
                shard_id=self.shard_id,
                shard_epoch=self.shard_epoch,
                on_error=self.on_error,
            )
            for value in values:
                if self.on_document is not None:
                    self.on_document(value)
            return values
        applied = self.client.sync_snapshots(
            self.database,
            self.peer_catalog,
            verse_id=self.verse_id,
            roles=self.roles,
            schema_ids=self.schema_ids,
            record_keys=self.record_keys,
            shard_id=self.shard_id,
            shard_epoch=self.shard_epoch,
            on_error=self.on_error,
        )
        for record in applied:
            if self.on_applied is not None:
                self.on_applied(record)
        return applied

    def start(self) -> "CultMeshSnapshotFanout":
        if self._thread is not None:
            return self
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(2.0, self.interval_seconds * 2.0))
            self._thread = None

    def __enter__(self) -> "CultMeshSnapshotFanout":
        return self.start()

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.stop()

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            self.sync_once()


@dataclass(frozen=True)
class CultMeshPeerHealth:
    peer_id: str
    endpoint: str
    checked_at: datetime
    is_reachable: bool
    runtime_id: str | None = None
    runtime_kind: str | None = None
    display_name: str | None = None
    supported_document_types: tuple[str, ...] = ()
    supported_message_versions: tuple[str, ...] = ()
    supported_mutation_contracts: tuple[dict[str, Any], ...] = ()
    error: str | None = None


@dataclass
class CultMeshPeerHealthMonitor:
    runtime_id: str
    timeout_seconds: float = 2.0
    interval_seconds: float = 5.0
    on_update: Callable[[CultMeshPeerHealth], None] | None = None
    _latest: dict[tuple[str, str], CultMeshPeerHealth] = field(default_factory=dict, init=False, repr=False)
    _lock: threading.Lock = field(default_factory=threading.Lock, init=False, repr=False)
    _stop: threading.Event = field(default_factory=threading.Event, init=False, repr=False)
    _thread: threading.Thread | None = field(default=None, init=False, repr=False)

    def probe_peer(self, peer: CultMeshPeerCard) -> list[CultMeshPeerHealth]:
        return [
            self._record(self._probe_endpoint(peer.peer_id, endpoint))
            for endpoint in _distinct_non_empty(list(peer.endpoints))
        ]

    def probe_catalog(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
    ) -> list[CultMeshPeerHealth]:
        requested_roles = set(roles or [])
        results = []
        for peer in catalog.find(verse_id):
            if requested_roles and not requested_roles.intersection(peer.roles):
                continue
            results.extend(self.probe_peer(peer))
        return results

    def latest(self) -> tuple[CultMeshPeerHealth, ...]:
        with self._lock:
            return tuple(
                self._latest[key]
                for key in sorted(self._latest)
            )

    def start(
        self,
        catalog: CultMeshPeerCatalog,
        *,
        verse_id: str,
        roles: list[str] | None = None,
    ) -> "CultMeshPeerHealthMonitor":
        if self._thread is not None:
            return self
        self._stop.clear()
        self._thread = threading.Thread(
            target=self._run,
            args=(catalog, verse_id, roles),
            daemon=True,
        )
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(2.0, self.interval_seconds * 2.0))
            self._thread = None

    def __enter__(self) -> "CultMeshPeerHealthMonitor":
        if self._thread is None:
            raise RuntimeError("CultMeshPeerHealthMonitor.start(...) must be called before entering context")
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.stop()

    def _run(
        self,
        catalog: CultMeshPeerCatalog,
        verse_id: str,
        roles: list[str] | None,
    ) -> None:
        self.probe_catalog(catalog, verse_id=verse_id, roles=roles)
        while not self._stop.wait(self.interval_seconds):
            self.probe_catalog(catalog, verse_id=verse_id, roles=roles)

    def _probe_endpoint(self, peer_id: str, endpoint: str) -> CultMeshPeerHealth:
        checked_at = datetime.now(UTC)
        try:
            host, port = _parse_endpoint(endpoint)
            response = CultNetRawClient(host, port, self.timeout_seconds).request(
                hello(runtime_id=self.runtime_id),
                expected_schema_version="cultnet.hello.v0",
            )
            return CultMeshPeerHealth(
                peer_id=peer_id,
                endpoint=endpoint,
                checked_at=checked_at,
                is_reachable=True,
                runtime_id=str(response.get("runtimeId") or ""),
                runtime_kind=_optional_string(response.get("runtimeKind")),
                display_name=_optional_string(response.get("displayName")),
                supported_document_types=tuple(
                    str(value)
                    for value in response.get("supportedDocumentTypes") or ()
                    if value is not None
                ),
                supported_message_versions=tuple(
                    str(value)
                    for value in response.get("supportedMessageVersions") or ()
                    if value is not None
                ),
                supported_mutation_contracts=tuple(
                    dict(value)
                    for value in response.get("supportedMutationContracts") or ()
                    if isinstance(value, dict)
                ),
            )
        except Exception as error:
            return CultMeshPeerHealth(
                peer_id=peer_id,
                endpoint=endpoint,
                checked_at=checked_at,
                is_reachable=False,
                error=str(error),
            )

    def _record(self, health: CultMeshPeerHealth) -> CultMeshPeerHealth:
        with self._lock:
            self._latest[(health.peer_id, health.endpoint)] = health
        if self.on_update is not None:
            self.on_update(health)
        return health


def _parse_endpoint(endpoint: str) -> tuple[str, int]:
    if not endpoint or not endpoint.strip():
        raise ValueError("endpoint must be non-empty")
    parsed = urlparse(endpoint if "://" in endpoint else f"cultnet://{endpoint}")
    if not parsed.hostname or parsed.port is None:
        raise ValueError(f"CultMesh endpoint must include host and port: {endpoint!r}")
    return parsed.hostname, parsed.port


def _distinct_non_empty(values: list[str]) -> list[str]:
    clean: list[str] = []
    seen: set[str] = set()
    for value in values:
        if not value or not value.strip() or value in seen:
            continue
        clean.append(value)
        seen.add(value)
    return clean


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
