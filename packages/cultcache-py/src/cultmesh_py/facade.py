from __future__ import annotations

from pathlib import Path

from .node import CultMeshNode, create_node
from .simulation import CultMeshSimulationFactCommitter
from .wire import (
    CultMeshAuthorityLeaseCatalog,
    CultMeshPeerCatalog,
    CultMeshStreamCatalog,
    CultMeshVerseCatalog,
)


class CultMesh:
    @staticmethod
    def create_node(cache_path: str | Path | None = None, *, runtime_id: str = "python-runtime") -> CultMeshNode:
        return create_node(cache_path, runtime_id=runtime_id)

    @staticmethod
    def start_node(cache_path: str | Path | None = None, *, runtime_id: str = "python-runtime") -> CultMeshNode:
        return create_node(cache_path, runtime_id=runtime_id)

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
    def create_simulation_fact_committer(node: CultMeshNode) -> CultMeshSimulationFactCommitter:
        return CultMeshSimulationFactCommitter(node)
