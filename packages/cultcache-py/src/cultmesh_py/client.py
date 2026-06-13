from __future__ import annotations

import socket
from dataclasses import dataclass
from typing import Any

import msgpack

from cultnet_py.framing import read_frame, write_frame

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
        with socket.create_connection((self.host, self.port), timeout=self.timeout_seconds) as connection:
            connection.settimeout(self.timeout_seconds)
            stream = connection.makefile("rwb")
            write_frame(stream, msgpack.packb(message, use_bin_type=True))
            stream.flush()
            response = msgpack.unpackb(read_frame(stream), raw=False)
        if not isinstance(response, dict):
            raise ValueError("CultMesh discovery response must be a MessagePack map")
        schema_version = response.get("schemaVersion")
        if schema_version != expected_schema_version:
            raise ValueError(f"Expected {expected_schema_version}, received {schema_version!r}")
        return response

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
