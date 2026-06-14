from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable
from urllib.parse import urlparse

from cultnet_py import CultNetRawClient, CultNetRawSnapshotResponse

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
