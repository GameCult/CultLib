from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


VERSE_CATALOG_REQUEST = "cultmesh.verse_catalog_request.v0"
VERSE_CATALOG_RESPONSE = "cultmesh.verse_catalog_response.v0"
PEER_EXCHANGE_REQUEST = "cultmesh.peer_exchange_request.v0"
PEER_EXCHANGE_RESPONSE = "cultmesh.peer_exchange_response.v0"


@dataclass(frozen=True)
class CultMeshVerseCompatibility:
    transport_version: str
    rules_hash: str
    compatible_verse_ids: tuple[str, ...] = ()
    required_plugin_ids: tuple[str, ...] = ()
    optional_plugin_ids: tuple[str, ...] = ()

    def to_wire(self) -> dict[str, Any]:
        return {
            "transportVersion": self.transport_version,
            "rulesHash": self.rules_hash,
            "compatibleVerseIds": list(self.compatible_verse_ids),
            "requiredPluginIds": list(self.required_plugin_ids),
            "optionalPluginIds": list(self.optional_plugin_ids),
        }


@dataclass(frozen=True)
class CultMeshVerseDescriptor:
    verse_id: str
    display_name: str
    authority_model: str
    compatibility: CultMeshVerseCompatibility
    discovery_endpoints: tuple[str, ...] = ()
    authority_runtime_ids: tuple[str, ...] = ()
    parent_verse_id: str | None = None
    description: str | None = None

    def to_wire(self) -> dict[str, Any]:
        return {
            "verseId": self.verse_id,
            "displayName": self.display_name,
            "authorityModel": self.authority_model,
            "compatibility": self.compatibility.to_wire(),
            "discoveryEndpoints": list(self.discovery_endpoints),
            "authorityRuntimeIds": list(self.authority_runtime_ids),
            "parentVerseId": self.parent_verse_id,
            "description": self.description,
        }


@dataclass(frozen=True)
class CultMeshPeerCard:
    peer_id: str
    verse_id: str
    endpoints: tuple[str, ...]
    roles: tuple[str, ...] = ()
    shard_ids: tuple[str, ...] = ()
    region: str | None = None
    authority_lease_id: str | None = None
    expires_at: str | None = None
    signature: str | None = None

    def to_wire(self) -> dict[str, Any]:
        return {
            "peerId": self.peer_id,
            "verseId": self.verse_id,
            "endpoints": list(self.endpoints),
            "roles": list(self.roles),
            "shardIds": list(self.shard_ids),
            "region": self.region,
            "authorityLeaseId": self.authority_lease_id,
            "expiresAt": self.expires_at,
            "signature": self.signature,
        }


@dataclass
class CultMeshVerseCatalog:
    _verses: dict[str, CultMeshVerseDescriptor] = field(default_factory=dict)

    def upsert(self, verse: CultMeshVerseDescriptor) -> None:
        require_non_empty(verse.verse_id, "verse.verse_id")
        self._verses[verse.verse_id] = verse

    def create_response(self, request: dict[str, Any]) -> dict[str, Any]:
        requested = set(request.get("verseIds") or [])
        transport_version = request.get("transportVersion")
        verses = []
        for verse in sorted(self._verses.values(), key=lambda item: item.verse_id):
            if requested and verse.verse_id not in requested:
                continue
            if transport_version and verse.compatibility.transport_version != transport_version:
                continue
            verses.append(verse.to_wire())
        return {
            "schemaVersion": VERSE_CATALOG_RESPONSE,
            "messageId": request.get("messageId", ""),
            "verses": verses,
        }


@dataclass
class CultMeshPeerCatalog:
    _peers: dict[str, CultMeshPeerCard] = field(default_factory=dict)

    def upsert(self, peer: CultMeshPeerCard) -> None:
        require_non_empty(peer.peer_id, "peer.peer_id")
        require_non_empty(peer.verse_id, "peer.verse_id")
        self._peers[peer.peer_id] = peer

    def create_response(self, request: dict[str, Any]) -> dict[str, Any]:
        verse_id = request.get("verseId", "")
        roles = set(request.get("roles") or [])
        known_peer_ids = set(request.get("knownPeerIds") or [])
        limit = request.get("limit")
        peers = []
        for peer in sorted(self._peers.values(), key=lambda item: item.peer_id):
            if peer.verse_id != verse_id:
                continue
            if peer.peer_id in known_peer_ids:
                continue
            if roles and not roles.intersection(peer.roles):
                continue
            peers.append(peer.to_wire())
            if isinstance(limit, int) and len(peers) >= limit:
                break
        return {
            "schemaVersion": PEER_EXCHANGE_RESPONSE,
            "messageId": request.get("messageId", ""),
            "peers": peers,
        }


def verse_catalog_request(message_id: str, *, verse_ids: list[str] | None = None, transport_version: str | None = None) -> dict[str, Any]:
    return {
        "schemaVersion": VERSE_CATALOG_REQUEST,
        "messageId": message_id,
        "verseIds": verse_ids,
        "transportVersion": transport_version,
    }


def peer_exchange_request(
    message_id: str,
    *,
    verse_id: str,
    roles: list[str] | None = None,
    known_peer_ids: list[str] | None = None,
    limit: int | None = None,
) -> dict[str, Any]:
    return {
        "schemaVersion": PEER_EXCHANGE_REQUEST,
        "messageId": message_id,
        "verseId": verse_id,
        "roles": roles,
        "knownPeerIds": known_peer_ids,
        "limit": limit,
    }


def require_non_empty(value: str, name: str) -> None:
    if not value or not value.strip():
        raise ValueError(f"{name} must be non-empty")
