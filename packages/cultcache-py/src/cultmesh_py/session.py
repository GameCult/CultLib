from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from cultcache_py.documents import DocumentDefinition
from cultnet_py import CultNetSimulationConsensusOptions, CultNetSimulationObservationHub

from .node import CultMeshNode
from .simulation import (
    CultMeshSimulationFact,
    CultMeshSimulationFactCommit,
    CultMeshSimulationFactCommitter,
    simulation_fact_document,
)
from .wire import CultMeshAuthorityLeaseCatalog, CultMeshPeerCatalog, CultMeshVerseCatalog


@dataclass(frozen=True)
class CultMeshPrediction:
    key: str
    document: DocumentDefinition[Any]
    value: Any


@dataclass
class CultMeshGameSessionOptions:
    verse_catalog: CultMeshVerseCatalog | None = None
    peer_catalog: CultMeshPeerCatalog | None = None
    authority_leases: CultMeshAuthorityLeaseCatalog | None = None
    consensus_options: CultNetSimulationConsensusOptions | None = None


@dataclass
class CultMeshGameSession:
    node: CultMeshNode
    options: CultMeshGameSessionOptions | None = None

    def __post_init__(self) -> None:
        options = self.options or CultMeshGameSessionOptions()
        self.verse_catalog = options.verse_catalog or CultMeshVerseCatalog()
        self.peer_catalog = options.peer_catalog or CultMeshPeerCatalog()
        self.authority_leases = options.authority_leases or CultMeshAuthorityLeaseCatalog()
        self.observation_hub = CultNetSimulationObservationHub(
            options.consensus_options or CultNetSimulationConsensusOptions()
        )
        self.fact_committer = CultMeshSimulationFactCommitter(self.node)
        self._committed_fact_ids: set[str] = set()

    def predict(self, document: DocumentDefinition[Any], key: str, value: Any) -> CultMeshPrediction:
        self.node.put(document, key, value)
        return CultMeshPrediction(key=key, document=document, value=value)

    def submit_observation(self, observation_or_message: dict[str, Any]) -> list[dict[str, Any]]:
        return self.observation_hub.submit(observation_or_message)

    def submit_and_commit(self, observation_or_message: dict[str, Any]) -> list[CultMeshSimulationFactCommit]:
        return self.commit_quorum_candidates(self.submit_observation(observation_or_message))

    def commit_quorum_candidates(self, candidates: list[dict[str, Any]]) -> list[CultMeshSimulationFactCommit]:
        commits: list[CultMeshSimulationFactCommit] = []
        for candidate in candidates:
            if not bool(candidate.get("hasQuorum")):
                continue
            fact_id = CultMeshSimulationFact.compute_fact_id(candidate)
            if fact_id in self._committed_fact_ids:
                continue
            key = CultMeshSimulationFact.create_record_key(candidate)
            if self.node.get(simulation_fact_document, key) is not None:
                self._committed_fact_ids.add(fact_id)
                continue
            commit = self.fact_committer.commit(candidate)
            self._committed_fact_ids.add(fact_id)
            commits.append(commit)
        return commits
