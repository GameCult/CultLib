from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
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

    def find(self, verse_id: str, role: str | None = None) -> list[CultMeshPeerCard]:
        require_non_empty(verse_id, "verse_id")
        return [
            peer
            for peer in sorted(self._peers.values(), key=lambda item: item.peer_id)
            if peer.verse_id == verse_id and (role is None or role in peer.roles)
        ]


@dataclass(frozen=True)
class CultMeshAuthorityLease:
    lease_id: str
    verse_id: str
    peer_id: str
    roles: tuple[str, ...]
    valid_from: datetime
    expires_at: datetime
    shard_ids: tuple[str, ...] = ()


@dataclass
class CultMeshAuthorityLeaseCatalog:
    _leases: dict[str, CultMeshAuthorityLease] = field(default_factory=dict)

    def upsert(self, lease: CultMeshAuthorityLease) -> None:
        require_non_empty(lease.lease_id, "lease.lease_id")
        require_non_empty(lease.verse_id, "lease.verse_id")
        require_non_empty(lease.peer_id, "lease.peer_id")
        if lease.expires_at <= lease.valid_from:
            raise ValueError("CultMesh authority lease expiry must be after valid_from")
        self._leases[lease.lease_id] = lease

    def is_authorized(
        self,
        peer: CultMeshPeerCard,
        role: str,
        shard_id: str | None = None,
        at: datetime | None = None,
    ) -> bool:
        require_non_empty(role, "role")
        if not peer.authority_lease_id:
            return False
        lease = self._leases.get(peer.authority_lease_id)
        if lease is None:
            return False
        checked_at = at or datetime.now(lease.valid_from.tzinfo)
        return (
            lease.valid_from <= checked_at < lease.expires_at
            and lease.verse_id == peer.verse_id
            and lease.peer_id == peer.peer_id
            and role in lease.roles
            and role in peer.roles
            and (shard_id is None or not lease.shard_ids or shard_id in lease.shard_ids)
        )


ZERO_COPY_TRANSPORTS = {
    "shared-memory",
    "shared-d3d12-texture",
    "shared-d3d11-texture",
    "dma-buf",
    "iosurface",
    "ahardwarebuffer",
}


@dataclass(frozen=True)
class CultMeshStreamDescriptor:
    stream_id: str
    verse_id: str
    owner_peer_id: str
    kind: str
    clock: dict[str, Any]
    preferred_transports: tuple[str, ...]
    label: str | None = None
    audio: dict[str, Any] | None = None
    video: dict[str, Any] | None = None
    required_access: str = "read"
    max_in_flight_frames: int | None = None
    metadata_schema_id: str | None = None


@dataclass(frozen=True)
class CultMeshStreamConsumerProfile:
    peer_id: str
    verse_id: str
    supported_transports: tuple[str, ...]
    accepted_kinds: tuple[str, ...] = ()
    can_import_gpu_handles: bool = False
    can_map_shared_memory: bool = False
    max_in_flight_frames: int | None = None


@dataclass(frozen=True)
class CultMeshStreamNegotiation:
    stream_id: str
    producer_peer_id: str
    consumer_peer_id: str
    transport: str
    access: str
    max_in_flight_frames: int
    copy_budget: str


@dataclass(frozen=True)
class CultMeshStreamFrameHandle:
    stream_id: str
    sequence: int
    timestamp_ns: int
    transport: str
    duration_ns: int | None = None
    byte_length: int | None = None
    native_handle: str | None = None
    resource_key: str | None = None
    page_ref: str | None = None
    fence_handle: str | None = None
    fence_value: int | None = None
    unavoidable_copy_count: int | None = None
    metadata: dict[str, Any] | None = None


@dataclass
class CultMeshStreamCatalog:
    _streams: dict[str, CultMeshStreamDescriptor] = field(default_factory=dict)
    _latest_frames: dict[str, CultMeshStreamFrameHandle] = field(default_factory=dict)

    @property
    def streams(self) -> list[CultMeshStreamDescriptor]:
        return [self._streams[key] for key in sorted(self._streams)]

    def declare(self, stream: CultMeshStreamDescriptor) -> CultMeshStreamDescriptor:
        require_non_empty(stream.stream_id, "stream.stream_id")
        require_non_empty(stream.verse_id, "stream.verse_id")
        require_non_empty(stream.owner_peer_id, "stream.owner_peer_id")
        require_non_empty(str(stream.clock.get("clockDomainId", "")), "stream.clock.clockDomainId")
        if not stream.preferred_transports:
            raise ValueError("stream.preferred_transports must not be empty")
        self._streams[stream.stream_id] = stream
        return stream

    def get(self, stream_id: str) -> CultMeshStreamDescriptor | None:
        require_non_empty(stream_id, "stream_id")
        return self._streams.get(stream_id)

    def find(self, verse_id: str, kind: str | None = None) -> list[CultMeshStreamDescriptor]:
        require_non_empty(verse_id, "verse_id")
        return [
            stream
            for stream in self.streams
            if stream.verse_id == verse_id and (kind is None or stream.kind == kind)
        ]

    def negotiate(self, stream_id: str, consumer: CultMeshStreamConsumerProfile) -> CultMeshStreamNegotiation:
        stream = self.get(stream_id)
        if stream is None:
            raise ValueError(f"Unknown CultMesh stream {stream_id!r}")
        if consumer.verse_id != stream.verse_id:
            raise ValueError("stream and consumer must belong to the same Verse")
        if consumer.accepted_kinds and stream.kind not in consumer.accepted_kinds:
            raise ValueError(f"consumer does not accept {stream.kind} streams")
        transport = next(
            (candidate for candidate in stream.preferred_transports if candidate in consumer.supported_transports),
            None,
        )
        if transport is None:
            raise ValueError("stream and consumer have no compatible body transport")
        stream_max = stream.max_in_flight_frames if stream.max_in_flight_frames is not None else 2**53 - 1
        consumer_max = consumer.max_in_flight_frames if consumer.max_in_flight_frames is not None else 2**53 - 1
        return CultMeshStreamNegotiation(
            stream_id=stream.stream_id,
            producer_peer_id=stream.owner_peer_id,
            consumer_peer_id=consumer.peer_id,
            transport=transport,
            access=stream.required_access,
            max_in_flight_frames=min(stream_max, consumer_max),
            copy_budget=copy_budget_for(transport),
        )

    def publish_frame(self, frame: CultMeshStreamFrameHandle) -> CultMeshStreamFrameHandle:
        require_non_empty(frame.stream_id, "frame.stream_id")
        if frame.stream_id not in self._streams:
            raise ValueError(f"Unknown CultMesh stream {frame.stream_id!r}")
        self._latest_frames[frame.stream_id] = frame
        return frame

    def latest_frame(self, stream_id: str) -> CultMeshStreamFrameHandle | None:
        require_non_empty(stream_id, "stream_id")
        return self._latest_frames.get(stream_id)


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


def copy_budget_for(transport: str) -> str:
    if transport in ZERO_COPY_TRANSPORTS:
        return "zero-copy-target"
    if transport == "cultcache-page":
        return "one-copy-fallback"
    if transport == "inline-bytes":
        return "opaque-runtime"
    return "opaque-runtime"
