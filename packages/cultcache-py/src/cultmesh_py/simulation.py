from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
from typing import Any

from cultcache_py import define_database_entry_type
from cultcache_py.documents import DocumentDefinition

from .node import CultMeshNode

SIMULATION_FACT_DOCUMENT_TYPE = "gamecult.mesh.simulation_fact"
SIMULATION_FACT_SCHEMA_VERSION = "gamecult.mesh.simulation_fact.v1"


@dataclass(frozen=True)
class CultMeshSimulationFact:
    fact_id: str
    shard_id: str
    shard_epoch: int
    frame: int
    subject_id: str
    claim_kind: str
    claim_hash: str
    claim_summary: str | None
    witness_count: int
    support_weight: float
    total_weight: float
    confidence: float
    committed_at: str

    @staticmethod
    def compute_fact_id(candidate: dict[str, Any]) -> str:
        canonical = "\x1f".join([
            _candidate_string(candidate, "shardId", "shard_id"),
            str(_candidate_int(candidate, "shardEpoch", "shard_epoch")),
            str(_candidate_int(candidate, "frame", "frame")),
            _candidate_string(candidate, "subjectId", "subject_id"),
            _candidate_string(candidate, "claimKind", "claim_kind"),
        ])
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()

    @staticmethod
    def create_record_key(candidate: dict[str, Any]) -> str:
        return f"simulation:{CultMeshSimulationFact.compute_fact_id(candidate)}"

    @staticmethod
    def from_candidate(candidate: dict[str, Any], *, committed_at: str | None = None) -> "CultMeshSimulationFact":
        return CultMeshSimulationFact(
            fact_id=CultMeshSimulationFact.compute_fact_id(candidate),
            shard_id=_candidate_string(candidate, "shardId", "shard_id"),
            shard_epoch=_candidate_int(candidate, "shardEpoch", "shard_epoch"),
            frame=_candidate_int(candidate, "frame", "frame"),
            subject_id=_candidate_string(candidate, "subjectId", "subject_id"),
            claim_kind=_candidate_string(candidate, "claimKind", "claim_kind"),
            claim_hash=_candidate_string(candidate, "claimHash", "claim_hash"),
            claim_summary=_candidate_optional_string(candidate, "claimSummary", "claim_summary"),
            witness_count=_candidate_int(candidate, "witnessCount", "witness_count"),
            support_weight=_candidate_float(candidate, "supportWeight", "support_weight"),
            total_weight=_candidate_float(candidate, "totalWeight", "total_weight"),
            confidence=_candidate_float(candidate, "confidence", "confidence"),
            committed_at=committed_at or datetime.now(UTC).isoformat(),
        )


simulation_fact_document: DocumentDefinition[CultMeshSimulationFact] = define_database_entry_type(
    SIMULATION_FACT_DOCUMENT_TYPE,
    [
        ("fact_id", 0),
        ("shard_id", 1),
        ("shard_epoch", 2),
        ("frame", 3),
        ("subject_id", 4),
        ("claim_kind", 5),
        ("claim_hash", 6),
        ("claim_summary", 7, None),
        ("witness_count", 8),
        ("support_weight", 9),
        ("total_weight", 10),
        ("confidence", 11),
        ("committed_at", 12),
    ],
    cls=CultMeshSimulationFact,
    name="fact_id",
    indexes={"shard": "shard_id", "frame": "frame", "subject": "subject_id", "claim_kind": "claim_kind"},
    schema_name=SIMULATION_FACT_DOCUMENT_TYPE,
    schema_version=SIMULATION_FACT_SCHEMA_VERSION,
)


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


def _candidate_string(candidate: dict[str, Any], camel: str, snake: str) -> str:
    value = candidate.get(camel, candidate.get(snake, ""))
    if value is None:
        return ""
    return str(value)


def _candidate_optional_string(candidate: dict[str, Any], camel: str, snake: str) -> str | None:
    value = candidate.get(camel, candidate.get(snake))
    if value is None:
        return None
    return str(value)


def _candidate_int(candidate: dict[str, Any], camel: str, snake: str) -> int:
    return int(candidate.get(camel, candidate.get(snake, 0)))


def _candidate_float(candidate: dict[str, Any], camel: str, snake: str) -> float:
    return float(candidate.get(camel, candidate.get(snake, 0.0)))
