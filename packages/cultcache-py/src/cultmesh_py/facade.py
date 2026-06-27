from __future__ import annotations

import socket as socket_module
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from cultcache_py.documents import DocumentDefinition
from cultnet_py import (
    CultNetRawClient,
    CultNetReconnectPolicy,
    CultNetRudpSocketMode,
    CultNetRudpSocketTransportConnection,
    CultNetRudpSocketTransportOptions,
    CultNetSchemaCatalog,
    CultNetShardCatalog,
    CultNetSimulationObservationHub,
    create_rudp_schema_transport,
    wire_message_schema_catalog,
)

from .client import CultMeshDocumentSubscription, CultMeshPeerExchangeClient, CultMeshVerseDiscoveryClient
from .node import (
    CultMeshDocumentPublicationSource,
    CultMeshNode,
    CultMeshNodeOptions,
    CultMeshPublicationDocumentBinding,
    CultMeshReactiveDocument,
    CultMeshReactiveDocumentOptions,
    create_node,
)
from .server import CultMeshLocalServer
from .session import CultMeshGameSession, CultMeshGameSessionOptions
from .simulation import CultMeshSimulationFactCommitter
from .wire import (
    AuthorityLeaseVerifier,
    CultMeshAuthorityLeaseCatalog,
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshStreamCatalog,
    CultMeshVerseCatalog,
)


@dataclass(frozen=True)
class CultMeshRudpEndpoint:
    host: str
    port: int
    uri: str


class CultMesh:
    @staticmethod
    def create_node(
        cache_path: str | Path | None = None,
        *,
        runtime_id: str = "python-runtime",
        options: CultMeshNodeOptions | None = None,
        enable_durable_shard_logs: bool | None = None,
        shard_log_path: str | Path | None = None,
    ) -> CultMeshNode:
        return create_node(
            cache_path,
            runtime_id=runtime_id,
            options=options,
            enable_durable_shard_logs=enable_durable_shard_logs,
            shard_log_path=shard_log_path,
        )

    @staticmethod
    def start_node(
        cache_path: str | Path | None = None,
        *,
        runtime_id: str = "python-runtime",
        options: CultMeshNodeOptions | None = None,
        enable_durable_shard_logs: bool | None = None,
        shard_log_path: str | Path | None = None,
    ) -> CultMeshNode:
        return create_node(
            cache_path,
            runtime_id=runtime_id,
            options=options,
            enable_durable_shard_logs=enable_durable_shard_logs,
            shard_log_path=shard_log_path,
        )

    @staticmethod
    def create_verse_catalog() -> CultMeshVerseCatalog:
        return CultMeshVerseCatalog()

    @staticmethod
    def create_peer_catalog() -> CultMeshPeerCatalog:
        return CultMeshPeerCatalog()

    @staticmethod
    def create_authority_lease_catalog(
        *,
        signature_verifier: AuthorityLeaseVerifier | None = None,
        require_verified_signatures: bool = False,
    ) -> CultMeshAuthorityLeaseCatalog:
        return CultMeshAuthorityLeaseCatalog(
            signature_verifier=signature_verifier,
            require_verified_signatures=require_verified_signatures,
        )

    @staticmethod
    def create_stream_catalog() -> CultMeshStreamCatalog:
        return CultMeshStreamCatalog()

    @staticmethod
    def create_schema_catalog() -> CultNetSchemaCatalog:
        return CultNetSchemaCatalog()

    @staticmethod
    def create_builtin_schema_catalog(
        *,
        include_schema_json: bool = True,
        schema_ids: list[str] | None = None,
        kinds: list[str] | None = None,
    ) -> CultNetSchemaCatalog:
        catalog = CultNetSchemaCatalog()
        builtins = wire_message_schema_catalog(include_schema_json=include_schema_json)
        for descriptor in builtins.list(schema_ids=schema_ids, kinds=kinds):
            catalog.upsert(descriptor)
        return catalog

    @staticmethod
    def create_shard_catalog() -> CultNetShardCatalog:
        return CultNetShardCatalog()

    @staticmethod
    def parse_rudp_endpoint(endpoint: str) -> CultMeshRudpEndpoint:
        if not endpoint:
            raise ValueError("RUDP endpoint must be non-empty")
        parsed = urlparse(endpoint)
        if parsed.scheme.lower() != "rudp":
            raise ValueError("RUDP endpoint must use the rudp:// scheme")
        if not parsed.hostname or parsed.port is None:
            raise ValueError("RUDP endpoint must include a host and port")
        if parsed.port <= 0 or parsed.port > 65535:
            raise ValueError("RUDP endpoint port must be between 1 and 65535")
        host = parsed.hostname
        uri_host = f"[{host}]" if ":" in host and not host.startswith("[") else host
        return CultMeshRudpEndpoint(
            host=host,
            port=parsed.port,
            uri=f"rudp://{uri_host}:{parsed.port}",
        )

    @staticmethod
    def create_rudp_server(
        runtime_id: str,
        connection_id: int,
        *,
        bind_host: str = "127.0.0.1",
        bind_port: int = 0,
        socket: socket_module.socket | None = None,
        initial_sequence: int = 1,
        resend_delay_ms: int = 250,
        transport_id: str = "rudp",
        max_payload_bytes: int | None = None,
        max_fragment_bytes: int | None = None,
        max_pending_reliable_packets: int | None = None,
        reconnect_policy: CultNetReconnectPolicy | dict[str, Any] | None = None,
    ) -> CultNetRudpSocketTransportConnection:
        transport_socket = socket or _bind_rudp_socket(bind_host, bind_port)
        return CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id=runtime_id,
                socket=transport_socket,
                mode=CultNetRudpSocketMode.SERVER,
                connection_id=connection_id,
                initial_sequence=initial_sequence,
                resend_delay_ms=resend_delay_ms,
                transport_id=transport_id,
                max_payload_bytes=max_payload_bytes,
                max_fragment_bytes=max_fragment_bytes,
                max_pending_reliable_packets=max_pending_reliable_packets,
                reconnect_policy=reconnect_policy,
            )
        )

    @staticmethod
    def create_rudp_client(
        runtime_id: str,
        connection_id: int,
        endpoint: str | CultMeshRudpEndpoint,
        *,
        bind_host: str = "127.0.0.1",
        bind_port: int = 0,
        socket: socket_module.socket | None = None,
        initial_sequence: int = 1,
        resend_delay_ms: int = 250,
        transport_id: str = "rudp",
        max_payload_bytes: int | None = None,
        max_fragment_bytes: int | None = None,
        max_pending_reliable_packets: int | None = None,
        reconnect_policy: CultNetReconnectPolicy | dict[str, Any] | None = None,
    ) -> CultNetRudpSocketTransportConnection:
        parsed_endpoint = (
            CultMesh.parse_rudp_endpoint(endpoint)
            if isinstance(endpoint, str)
            else endpoint
        )
        transport_socket = socket or _bind_rudp_socket(bind_host, bind_port)
        return CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id=runtime_id,
                socket=transport_socket,
                mode=CultNetRudpSocketMode.CLIENT,
                remote_addr=(parsed_endpoint.host, parsed_endpoint.port),
                connection_id=connection_id,
                initial_sequence=initial_sequence,
                resend_delay_ms=resend_delay_ms,
                transport_id=transport_id,
                max_payload_bytes=max_payload_bytes,
                max_fragment_bytes=max_fragment_bytes,
                max_pending_reliable_packets=max_pending_reliable_packets,
                reconnect_policy=reconnect_policy,
            )
        )

    @staticmethod
    def create_rudp_client_for_peer(
        runtime_id: str,
        connection_id: int,
        peer: CultMeshPeerCard,
        **options: Any,
    ) -> CultNetRudpSocketTransportConnection:
        endpoint = next(
            (value for value in peer.endpoints if value.lower().startswith("rudp://")),
            None,
        )
        if endpoint is None:
            raise ValueError(f"Peer {peer.peer_id!r} does not advertise a RUDP endpoint")
        return CultMesh.create_rudp_client(runtime_id, connection_id, endpoint, **options)

    @staticmethod
    def create_rudp_client_for_authorized_peer(
        runtime_id: str,
        connection_id: int,
        peers: CultMeshPeerCatalog,
        leases: CultMeshAuthorityLeaseCatalog,
        verse_id: str,
        role: str,
        *,
        shard_id: str | None = None,
        at: datetime | None = None,
        **options: Any,
    ) -> CultNetRudpSocketTransportConnection:
        peer = peers.first_authorized(verse_id, role, leases, shard_id=shard_id, at=at)
        if peer is None:
            raise ValueError(f"No authorized RUDP peer for role {role!r} in Verse {verse_id!r}")
        return CultMesh.create_rudp_client_for_peer(
            runtime_id,
            connection_id,
            peer,
            **options,
        )

    @staticmethod
    def create_verse_discovery_client(
        host: str = "localhost",
        port: int = 3075,
        *,
        timeout_seconds: float = 4.0,
    ) -> CultMeshVerseDiscoveryClient:
        return CultMeshVerseDiscoveryClient(host, port, timeout_seconds)

    @staticmethod
    def create_peer_exchange_client(
        host: str = "localhost",
        port: int = 3075,
        *,
        timeout_seconds: float = 4.0,
    ) -> CultMeshPeerExchangeClient:
        return CultMeshPeerExchangeClient(host, port, timeout_seconds)

    @staticmethod
    def create_simulation_fact_committer(node: CultMeshNode) -> CultMeshSimulationFactCommitter:
        return CultMeshSimulationFactCommitter(node)

    @staticmethod
    def sync_document(
        node: CultMeshNode,
        client: CultNetRawClient,
        document: DocumentDefinition[Any],
        key: str,
        *,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> Any:
        return node.sync_document(
            client,
            document,
            key,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    @staticmethod
    def reactive_document(
        node: CultMeshNode,
        document: DocumentDefinition[Any],
        key: str,
        options: CultMeshReactiveDocumentOptions | None = None,
    ) -> CultMeshReactiveDocument:
        return node.reactive_document(document, key, options)

    @staticmethod
    def publication_source_from_peer_snapshot(
        client: CultNetRawClient,
        *,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> CultMeshDocumentPublicationSource:
        return CultMeshDocumentPublicationSource.peer_snapshot(
            client,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    @staticmethod
    def publication_source_from_single_file(path: str | Path) -> CultMeshDocumentPublicationSource:
        return CultMeshDocumentPublicationSource.single_file(path)

    @staticmethod
    def publication_document(
        document: DocumentDefinition[Any],
        key: str,
        *,
        source: CultMeshDocumentPublicationSource | None = None,
    ) -> CultMeshPublicationDocumentBinding:
        if not key:
            raise ValueError("key must be non-empty")
        return CultMeshPublicationDocumentBinding(document=document, key=key, source=source)

    @staticmethod
    def sync_document_from_publication(
        node: CultMeshNode,
        source: CultMeshDocumentPublicationSource,
        document: DocumentDefinition[Any],
        key: str,
    ) -> Any:
        return node.sync_document_from_publication(source, document, key)

    @staticmethod
    def sync_documents_from_publication(
        node: CultMeshNode,
        source: CultMeshDocumentPublicationSource,
        bindings: list[CultMeshPublicationDocumentBinding] | tuple[CultMeshPublicationDocumentBinding, ...],
    ) -> list[Any]:
        return node.sync_documents_from_publication(source, bindings)

    @staticmethod
    def subscribe_document(
        node: CultMeshNode,
        client: CultNetRawClient,
        document: DocumentDefinition[Any],
        key: str,
        *,
        subscription_id: str | None = None,
        message_id: str = "cultmesh-python-document-subscribe",
        include_snapshot: bool = True,
    ) -> CultMeshDocumentSubscription:
        return CultMeshDocumentSubscription(
            client=client,
            database=node.database,
            document=document,
            key=key,
            subscription_id=subscription_id,
            message_id=message_id,
            include_snapshot=include_snapshot,
        )

    @staticmethod
    def create_game_session(
        node: CultMeshNode,
        options: CultMeshGameSessionOptions | None = None,
    ) -> CultMeshGameSession:
        return CultMeshGameSession(node, options)

    @staticmethod
    def serve_node(
        node: CultMeshNode,
        *,
        verse_catalog: CultMeshVerseCatalog | None = None,
        peer_catalog: CultMeshPeerCatalog | None = None,
        observation_hub: CultNetSimulationObservationHub | None = None,
        host: str = "127.0.0.1",
        port: int = 0,
        display_name: str | None = None,
        max_snapshot_documents: int | None = None,
        max_snapshot_bytes: int | None = None,
        enable_rudp: bool = True,
        rudp_connection_id: int = 0x43554C54,
        rudp_resend_delay_ms: int = 25,
    ) -> CultMeshLocalServer:
        return CultMeshLocalServer(
            node=node,
            verse_catalog=verse_catalog or CultMeshVerseCatalog(),
            peer_catalog=peer_catalog or CultMeshPeerCatalog(),
            observation_hub=observation_hub,
            host=host,
            port=port,
            display_name=display_name,
            max_snapshot_documents=max_snapshot_documents,
            max_snapshot_bytes=max_snapshot_bytes,
            enable_rudp=enable_rudp,
            rudp_connection_id=rudp_connection_id,
            rudp_resend_delay_ms=rudp_resend_delay_ms,
        ).start()

    @staticmethod
    def create_client(
        host: str = "localhost",
        port: int = 3075,
        *,
        timeout_seconds: float = 4.0,
        endpoint: str | CultMeshRudpEndpoint | None = None,
        connection_id: int = 0x43554C54,
        runtime_id: str = "cultmesh-python-rudp-client",
        bind_host: str = "127.0.0.1",
        bind_port: int = 0,
    ) -> CultNetRawClient:
        if endpoint is None and host.lower().startswith("rudp://"):
            endpoint = host
        if endpoint is None:
            return CultNetRawClient(host, port, timeout_seconds)
        parsed_endpoint = (
            CultMesh.parse_rudp_endpoint(endpoint)
            if isinstance(endpoint, str)
            else endpoint
        )
        return CultNetRawClient(
            parsed_endpoint.host,
            parsed_endpoint.port,
            timeout_seconds,
            create_transport=lambda: create_rudp_schema_transport(
                host=parsed_endpoint.host,
                port=parsed_endpoint.port,
                connection_id=connection_id,
                timeout_seconds=timeout_seconds,
                runtime_id=runtime_id,
                bind_host=bind_host,
                bind_port=bind_port,
            ),
        )

    @staticmethod
    def connect_client(
        host: str = "localhost",
        port: int = 3075,
        *,
        timeout_seconds: float = 4.0,
        endpoint: str | CultMeshRudpEndpoint | None = None,
        connection_id: int = 0x43554C54,
        runtime_id: str = "cultmesh-python-rudp-client",
        bind_host: str = "127.0.0.1",
        bind_port: int = 0,
    ) -> CultNetRawClient:
        return CultMesh.create_client(
            host,
            port,
            timeout_seconds=timeout_seconds,
            endpoint=endpoint,
            connection_id=connection_id,
            runtime_id=runtime_id,
            bind_host=bind_host,
            bind_port=bind_port,
        )


def _bind_rudp_socket(bind_host: str, bind_port: int) -> socket_module.socket:
    transport_socket = socket_module.socket(socket_module.AF_INET, socket_module.SOCK_DGRAM)
    transport_socket.bind((bind_host, bind_port))
    transport_socket.settimeout(0.02)
    return transport_socket
