from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from cultcache_py.documents import DocumentDefinition
from cultnet_py.cultmesh_contracts import (
    SIMULATION_FACT_DOCUMENT_TYPE,
    SIMULATION_FACT_SCHEMA_VERSION,
    CultMeshSimulationFact,
    simulation_fact_document,
)

from .node import CultMeshNode

__all__ = [
    "SIMULATION_FACT_DOCUMENT_TYPE",
    "SIMULATION_FACT_SCHEMA_VERSION",
    "CultMeshSimulationFact",
    "CultMeshSimulationFactCommit",
    "CultMeshSimulationFactCommitter",
    "simulation_fact_document",
]


@dataclass(frozen=True)
class CultMeshSimulationFactCommit:
    key: str
    fact: CultMeshSimulationFact


@dataclass
class CultMeshSimulationFactCommitter:
    node: CultMeshNode
    document: DocumentDefinition[CultMeshSimulationFact] = simulation_fact_document

    def __post_init__(self) -> None:
        if all(document.type != self.document.type for document in self.node.documents):
            self.node.register_document(self.document)

    def commit(self, candidate: dict[str, Any], *, committed_at: str | None = None) -> CultMeshSimulationFactCommit:
        if not bool(candidate.get("hasQuorum") if "hasQuorum" in candidate else candidate.get("has_quorum")):
            raise ValueError("Simulation candidate cannot be committed before quorum")
        key = CultMeshSimulationFact.create_record_key(candidate)
        fact = CultMeshSimulationFact.from_candidate(candidate, committed_at=committed_at)
        self.node.put(self.document, key, fact)
        return CultMeshSimulationFactCommit(key=key, fact=fact)
