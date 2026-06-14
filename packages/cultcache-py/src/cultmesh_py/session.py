from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from cultcache_py.documents import DocumentDefinition
from cultnet_py import (
    CultNetAppliedRecord,
    CultNetClientAuthorityScope,
    CultNetRawClient,
    CultNetShardLogResponse,
    CultNetSimulationConsensusCandidate,
    CultNetSimulationObservation,
    CultNetSimulationConsensusOptions,
    CultNetSimulationObservationHub,
)

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
    schema_id: str
    document: DocumentDefinition[Any]
    value: Any


@dataclass(frozen=True)
class CultMeshSessionChange:
    schema_id: str
    record_key: str
    change_kind: str
    value: Any | None = None


@dataclass
class CultMeshGameSessionOptions:
    verse_catalog: CultMeshVerseCatalog | None = None
    peer_catalog: CultMeshPeerCatalog | None = None
    authority_leases: CultMeshAuthorityLeaseCatalog | None = None
    consensus_options: CultNetSimulationConsensusOptions | None = None
    client_authority_scopes: tuple[CultNetClientAuthorityScope, ...] = ()


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
        self._client_authority_scopes = tuple(options.client_authority_scopes)
        self._predicted_keys: set[tuple[str, str]] = set()

    def predict(self, document: DocumentDefinition[Any], key: str, value: Any) -> CultMeshPrediction:
        schema_id = document.catalog_entry().schema_id
        if not any(scope.matches(self.node.runtime_id, schema_id, key) for scope in self._client_authority_scopes):
            raise ValueError(
                f"Runtime {self.node.runtime_id!r} does not have client prediction authority "
                f"for schema {schema_id!r} key {key!r}"
            )
        self.node.put(document, key, value)
        self._predicted_keys.add((schema_id, key))
        return CultMeshPrediction(key=key, schema_id=schema_id, document=document, value=value)

    def apply_shard_log_response(
        self,
        response: dict[str, Any] | CultNetShardLogResponse,
    ) -> list[CultMeshSessionChange]:
        applied = self.node.database.apply_shard_log_response(response)
        return self._reconcile_applied(applied)

    def sync_shard_log(
        self,
        client: CultNetRawClient,
        *,
        shard_id: str,
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> list[CultMeshSessionChange]:
        response = client.fetch_shard_log_response(
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        )
        return self.apply_shard_log_response(response)

    def submit_observation_candidates(
        self,
        observation_or_message: dict[str, Any] | CultNetSimulationObservation,
    ) -> list[CultNetSimulationConsensusCandidate]:
        return self.observation_hub.submit_candidate_objects(observation_or_message)

    def submit_observation(self, observation_or_message: dict[str, Any] | CultNetSimulationObservation) -> list[dict[str, Any]]:
        return self.observation_hub.submit(observation_or_message)

    def submit_and_commit(self, observation_or_message: dict[str, Any] | CultNetSimulationObservation) -> list[CultMeshSimulationFactCommit]:
        return self.commit_quorum_candidates(self.submit_observation(observation_or_message))

    def commit_quorum_candidate_objects(
        self,
        candidates: list[CultNetSimulationConsensusCandidate],
    ) -> list[CultMeshSimulationFactCommit]:
        return self.commit_quorum_candidates([candidate.to_wire() for candidate in candidates])

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

    def _reconcile_applied(self, applied: list[CultNetAppliedRecord]) -> list[CultMeshSessionChange]:
        changes: list[CultMeshSessionChange] = []
        for record in applied:
            key = (record.schema_id, record.record_key)
            change_kind = record.change_kind
            if key in self._predicted_keys and record.change_kind in {"added", "updated"}:
                self._predicted_keys.remove(key)
                change_kind = "reconciled"
            changes.append(CultMeshSessionChange(
                schema_id=record.schema_id,
                record_key=record.record_key,
                change_kind=change_kind,
                value=record.value,
            ))
        return changes
