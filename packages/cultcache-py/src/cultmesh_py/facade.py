from __future__ import annotations

from pathlib import Path

from cultnet_py import CultNetRawClient

from .client import CultMeshPeerExchangeClient, CultMeshVerseDiscoveryClient
from .node import CultMeshNode, CultMeshNodeOptions, create_node
from .server import CultMeshLocalServer
from .session import CultMeshGameSession, CultMeshGameSessionOptions
from .simulation import CultMeshSimulationFactCommitter
from .wire import (
    CultMeshAuthorityLeaseCatalog,
    CultMeshPeerCatalog,
    CultMeshStreamCatalog,
    CultMeshVerseCatalog,
)


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
    def create_authority_lease_catalog() -> CultMeshAuthorityLeaseCatalog:
        return CultMeshAuthorityLeaseCatalog()

    @staticmethod
    def create_stream_catalog() -> CultMeshStreamCatalog:
        return CultMeshStreamCatalog()

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
        host: str = "127.0.0.1",
        port: int = 0,
        display_name: str | None = None,
    ) -> CultMeshLocalServer:
        return CultMeshLocalServer(
            node=node,
            verse_catalog=verse_catalog or CultMeshVerseCatalog(),
            peer_catalog=peer_catalog or CultMeshPeerCatalog(),
            host=host,
            port=port,
            display_name=display_name,
        ).start()

    @staticmethod
    def create_client(
        host: str = "localhost",
        port: int = 3075,
        *,
        timeout_seconds: float = 4.0,
    ) -> CultNetRawClient:
        return CultNetRawClient(host, port, timeout_seconds)

    @staticmethod
    def connect_client(
        host: str = "localhost",
        port: int = 3075,
        *,
        timeout_seconds: float = 4.0,
    ) -> CultNetRawClient:
        return CultNetRawClient(host, port, timeout_seconds)
