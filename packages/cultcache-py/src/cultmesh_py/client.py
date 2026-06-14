from __future__ import annotations

from dataclasses import dataclass
from typing import Any
from urllib.parse import urlparse

from cultnet_py import CultNetRawClient

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
