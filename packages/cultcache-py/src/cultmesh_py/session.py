from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Callable

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
from .node import CultMeshDatabaseChange
from .simulation import (
    CultMeshSimulationFact,
    CultMeshSimulationFactCommit,
    CultMeshSimulationFactCommitter,
    simulation_fact_document,
)
from .wire import CultMeshAuthorityLeaseCatalog, CultMeshPeerCatalog, CultMeshVerseCatalog

if TYPE_CHECKING:
    from .server import CultMeshLocalServer


@dataclass(frozen=True)
class CultMeshPrediction:
    key: str
    schema_id: str
    document: DocumentDefinition[Any]
    value: Any
    had_previous_value: bool = False
    previous_value: Any | None = None


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
    serve_verse_discovery: bool = True
    serve_peer_exchange: bool = True
    serve_simulation_observations: bool = True


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
        self._predictions: dict[tuple[str, str], CultMeshPrediction] = {}
        self._serve_verse_discovery = options.serve_verse_discovery
        self._serve_peer_exchange = options.serve_peer_exchange
        self._serve_simulation_observations = options.serve_simulation_observations

    def serve(
        self,
        *,
        host: str = "127.0.0.1",
        port: int = 0,
        display_name: str | None = None,
        max_snapshot_documents: int | None = None,
        max_snapshot_bytes: int | None = None,
    ) -> CultMeshLocalServer:
        from .server import CultMeshLocalServer

        return CultMeshLocalServer(
            node=self.node,
            verse_catalog=self.verse_catalog if self._serve_verse_discovery else CultMeshVerseCatalog(),
            peer_catalog=self.peer_catalog if self._serve_peer_exchange else CultMeshPeerCatalog(),
            observation_hub=self.observation_hub if self._serve_simulation_observations else None,
            host=host,
            port=port,
            display_name=display_name,
            max_snapshot_documents=max_snapshot_documents,
            max_snapshot_bytes=max_snapshot_bytes,
        ).start()

    def predict(self, document: DocumentDefinition[Any], key: str, value: Any) -> CultMeshPrediction:
        schema_id = document.catalog_entry().schema_id
        if not any(scope.matches(self.node.runtime_id, schema_id, key) for scope in self._client_authority_scopes):
            raise ValueError(
                f"Runtime {self.node.runtime_id!r} does not have client prediction authority "
                f"for schema {schema_id!r} key {key!r}"
            )
        had_previous_value = self.node.cache.get_envelope(document, key) is not None
        previous_value = self.node.get(document, key)
        self.node.put(document, key, value)
        prediction = CultMeshPrediction(
            key=key,
            schema_id=schema_id,
            document=document,
            value=value,
            had_previous_value=had_previous_value,
            previous_value=previous_value,
        )
        self._predictions[(schema_id, key)] = prediction
        return prediction

    def pending_predictions(self) -> tuple[CultMeshPrediction, ...]:
        return tuple(
            self._predictions[key]
            for key in sorted(self._predictions)
        )

    def resimulation_inputs(self) -> tuple[CultMeshPrediction, ...]:
        return self.pending_predictions()

    def rollback_prediction(
        self,
        prediction_or_schema_id: CultMeshPrediction | str,
        key: str | None = None,
    ) -> CultMeshSessionChange | None:
        schema_id, record_key = self._prediction_identity(prediction_or_schema_id, key)
        prediction = self._predictions.pop((schema_id, record_key), None)
        if prediction is None:
            return None
        if prediction.had_previous_value:
            self.node.put(prediction.document, prediction.key, prediction.previous_value)
            value = prediction.previous_value
        else:
            self.node.delete(prediction.document, prediction.key)
            value = None
        return CultMeshSessionChange(
            schema_id=prediction.schema_id,
            record_key=prediction.key,
            change_kind="rolled_back",
            value=value,
        )

    def rollback_predictions(self) -> list[CultMeshSessionChange]:
        return [
            change
            for prediction in list(self.pending_predictions())
            for change in [self.rollback_prediction(prediction)]
            if change is not None
        ]

    def watch_candidates(
        self,
        callback: Callable[[CultNetSimulationConsensusCandidate], None],
    ) -> Callable[[], None]:
        return self.observation_hub.watch_candidates(callback)

    def watch_simulation_facts(
        self,
        callback: Callable[[CultMeshDatabaseChange], None],
    ) -> Callable[[], None]:
        return self.node.database.watch(callback, document=simulation_fact_document)

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
            if key in self._predictions:
                del self._predictions[key]
                change_kind = "reconciled" if record.change_kind in {"added", "updated"} else "rolled_back"
            changes.append(CultMeshSessionChange(
                schema_id=record.schema_id,
                record_key=record.record_key,
                change_kind=change_kind,
                value=record.value,
            ))
        return changes

    @staticmethod
    def _prediction_identity(
        prediction_or_schema_id: CultMeshPrediction | str,
        key: str | None,
    ) -> tuple[str, str]:
        if isinstance(prediction_or_schema_id, CultMeshPrediction):
            if key is not None:
                raise ValueError("key must be omitted when rolling back a CultMeshPrediction")
            return prediction_or_schema_id.schema_id, prediction_or_schema_id.key
        if key is None:
            raise ValueError("key is required when rolling back by schema id")
        return prediction_or_schema_id, key
