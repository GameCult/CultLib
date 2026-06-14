from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True)
class CultNetSimulationObservation:
    witness_runtime_id: str
    shard_id: str
    shard_epoch: int
    frame: int
    subject_id: str
    claim_kind: str
    claim_hash: str
    claim_summary: str | None = None
    weight: float = 1.0
    observed_at: str = ""

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetSimulationObservation":
        observation = value.get("observation", value)
        if not isinstance(observation, dict):
            raise ValueError("CultNet simulation observation must be a map")
        return cls(
            witness_runtime_id=str(observation.get("witnessRuntimeId") or ""),
            shard_id=str(observation.get("shardId") or ""),
            shard_epoch=int(observation.get("shardEpoch") or 0),
            frame=int(observation.get("frame") or 0),
            subject_id=str(observation.get("subjectId") or ""),
            claim_kind=str(observation.get("claimKind") or ""),
            claim_hash=str(observation.get("claimHash") or ""),
            claim_summary=_optional_string(observation.get("claimSummary")),
            weight=max(float(observation.get("weight", 1.0) or 0.0), 0.0),
            observed_at=str(observation.get("observedAt") or ""),
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "witnessRuntimeId": self.witness_runtime_id,
            "shardId": self.shard_id,
            "shardEpoch": self.shard_epoch,
            "frame": self.frame,
            "subjectId": self.subject_id,
            "claimKind": self.claim_kind,
            "claimHash": self.claim_hash,
            "weight": self.weight,
            "observedAt": self.observed_at,
        }
        if self.claim_summary is not None:
            wire["claimSummary"] = self.claim_summary
        return wire

    def to_message_wire(self, *, message_id: str) -> dict[str, Any]:
        return {
            "schemaVersion": "cultnet.simulation_observation.v0",
            "messageId": message_id,
            "observation": self.to_wire(),
        }


@dataclass(frozen=True)
class CultNetSimulationConsensusCandidate:
    shard_id: str
    shard_epoch: int
    frame: int
    subject_id: str
    claim_kind: str
    claim_hash: str
    witness_count: int
    support_weight: float
    total_weight: float
    has_quorum: bool
    confidence: float
    message_id: str = ""
    claim_summary: str | None = None

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetSimulationConsensusCandidate":
        if value.get("schemaVersion") != "cultnet.simulation_consensus_candidate.v0":
            raise ValueError(f"Expected cultnet.simulation_consensus_candidate.v0, received {value.get('schemaVersion')!r}")
        return cls(
            message_id=str(value.get("messageId") or ""),
            shard_id=str(value.get("shardId") or ""),
            shard_epoch=int(value.get("shardEpoch") or 0),
            frame=int(value.get("frame") or 0),
            subject_id=str(value.get("subjectId") or ""),
            claim_kind=str(value.get("claimKind") or ""),
            claim_hash=str(value.get("claimHash") or ""),
            claim_summary=_optional_string(value.get("claimSummary")),
            witness_count=int(value.get("witnessCount") or 0),
            support_weight=float(value.get("supportWeight") or 0.0),
            total_weight=float(value.get("totalWeight") or 0.0),
            has_quorum=value.get("hasQuorum") is True,
            confidence=float(value.get("confidence") or 0.0),
        )

    def to_wire(self) -> dict[str, Any]:
        return {
            "schemaVersion": "cultnet.simulation_consensus_candidate.v0",
            "messageId": self.message_id,
            "shardId": self.shard_id,
            "shardEpoch": self.shard_epoch,
            "frame": self.frame,
            "subjectId": self.subject_id,
            "claimKind": self.claim_kind,
            "claimHash": self.claim_hash,
            "claimSummary": self.claim_summary,
            "witnessCount": self.witness_count,
            "supportWeight": self.support_weight,
            "totalWeight": self.total_weight,
            "hasQuorum": self.has_quorum,
            "confidence": self.confidence,
        }


@dataclass(frozen=True)
class CultNetSimulationConsensusOptions:
    minimum_witnesses: int = 1
    minimum_weight: float = 1.0
    quorum_ratio: float = 0.5


@dataclass
class CultNetSimulationConsensus:
    options: CultNetSimulationConsensusOptions = field(default_factory=CultNetSimulationConsensusOptions)

    def build_candidate_objects(
        self,
        observations: list[dict[str, Any] | CultNetSimulationObservation],
    ) -> list[CultNetSimulationConsensusCandidate]:
        normalized = [_observation_wire(observation) for observation in observations]
        clean = _dedupe_observations([observation for observation in normalized if _is_valid_observation(observation)])
        groups: dict[tuple[str, int, int, str, str], list[dict[str, Any]]] = {}
        for observation in clean:
            groups.setdefault(_subject_key(observation), []).append(observation)
        return [
            self._build_candidate(group)
            for _, group in sorted(groups.items(), key=lambda item: item[0])
        ]

    def build_candidates(self, observations: list[dict[str, Any] | CultNetSimulationObservation]) -> list[dict[str, Any]]:
        return [candidate.to_wire() for candidate in self.build_candidate_objects(observations)]

    def _build_candidate(self, observations: list[dict[str, Any]]) -> CultNetSimulationConsensusCandidate:
        total_weight = sum(_weight_of(observation) for observation in observations)
        claim_groups: dict[str, list[dict[str, Any]]] = {}
        for observation in observations:
            claim_groups.setdefault(str(observation.get("claimHash") or ""), []).append(observation)
        claim_hash, best_observations = sorted(
            claim_groups.items(),
            key=lambda item: (-sum(_weight_of(observation) for observation in item[1]), item[0]),
        )[0]
        first = sorted(best_observations, key=lambda observation: str(observation.get("witnessRuntimeId") or ""))[0]
        support_weight = sum(_weight_of(observation) for observation in best_observations)
        witness_count = len({str(observation.get("witnessRuntimeId") or "") for observation in best_observations})
        confidence = 0.0 if total_weight <= 0.0 else support_weight / total_weight
        return CultNetSimulationConsensusCandidate(
            shard_id=str(first.get("shardId") or ""),
            shard_epoch=int(first.get("shardEpoch") or 0),
            frame=int(first.get("frame") or 0),
            subject_id=str(first.get("subjectId") or ""),
            claim_kind=str(first.get("claimKind") or ""),
            claim_hash=claim_hash,
            claim_summary=_optional_string(first.get("claimSummary")),
            witness_count=witness_count,
            support_weight=support_weight,
            total_weight=total_weight,
            has_quorum=(
                witness_count >= self.options.minimum_witnesses
                and support_weight >= self.options.minimum_weight
                and total_weight > 0.0
                and confidence >= self.options.quorum_ratio
            ),
            confidence=confidence,
        )


@dataclass
class CultNetSimulationObservationHub:
    options: CultNetSimulationConsensusOptions = field(default_factory=CultNetSimulationConsensusOptions)
    observations: list[dict[str, Any]] = field(default_factory=list)

    def __post_init__(self) -> None:
        self._consensus = CultNetSimulationConsensus(self.options)

    def submit_candidate_objects(
        self,
        observation_or_message: dict[str, Any] | CultNetSimulationObservation,
    ) -> list[CultNetSimulationConsensusCandidate]:
        observation = _observation_wire(observation_or_message)
        self.observations.append(observation)
        return [
            candidate
            for candidate in self._consensus.build_candidate_objects(self.observations)
            if _candidate_matches_observation(candidate.to_wire(), observation)
        ]

    def submit(self, observation_or_message: dict[str, Any] | CultNetSimulationObservation) -> list[dict[str, Any]]:
        return [candidate.to_wire() for candidate in self.submit_candidate_objects(observation_or_message)]


def _observation_wire(observation_or_message: dict[str, Any] | CultNetSimulationObservation) -> dict[str, Any]:
    if isinstance(observation_or_message, CultNetSimulationObservation):
        return observation_or_message.to_wire()
    observation = observation_or_message.get("observation", observation_or_message)
    if not isinstance(observation, dict):
        raise ValueError("Simulation observation must be a map")
    return CultNetSimulationObservation.from_wire(observation).to_wire()


def _dedupe_observations(observations: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[tuple[str, str, int, int, str, str], list[dict[str, Any]]] = {}
    for observation in observations:
        groups.setdefault(_dedupe_key(observation), []).append(observation)
    return [
        sorted(group, key=lambda observation: (-_weight_of(observation), str(observation.get("witnessRuntimeId") or "")))[0]
        for _, group in sorted(groups.items(), key=lambda item: item[0])
    ]


def _is_valid_observation(observation: dict[str, Any]) -> bool:
    return (
        bool(str(observation.get("witnessRuntimeId") or "").strip())
        and bool(str(observation.get("shardId") or "").strip())
        and bool(str(observation.get("subjectId") or "").strip())
        and bool(str(observation.get("claimKind") or "").strip())
        and bool(str(observation.get("claimHash") or "").strip())
        and _weight_of(observation) > 0.0
    )


def _weight_of(observation: dict[str, Any]) -> float:
    return max(float(observation.get("weight", 1.0) or 0.0), 0.0)


def _dedupe_key(observation: dict[str, Any]) -> tuple[str, str, int, int, str, str]:
    return (
        str(observation.get("witnessRuntimeId") or ""),
        str(observation.get("shardId") or ""),
        int(observation.get("shardEpoch") or 0),
        int(observation.get("frame") or 0),
        str(observation.get("subjectId") or ""),
        str(observation.get("claimKind") or ""),
    )


def _subject_key(observation: dict[str, Any]) -> tuple[str, int, int, str, str]:
    return (
        str(observation.get("shardId") or ""),
        int(observation.get("shardEpoch") or 0),
        int(observation.get("frame") or 0),
        str(observation.get("subjectId") or ""),
        str(observation.get("claimKind") or ""),
    )


def _candidate_matches_observation(candidate: dict[str, Any], observation: dict[str, Any]) -> bool:
    return (
        candidate.get("shardId") == observation.get("shardId")
        and candidate.get("shardEpoch") == observation.get("shardEpoch")
        and candidate.get("frame") == observation.get("frame")
        and candidate.get("subjectId") == observation.get("subjectId")
        and candidate.get("claimKind") == observation.get("claimKind")
    )


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
