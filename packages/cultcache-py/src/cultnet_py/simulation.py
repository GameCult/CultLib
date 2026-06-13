from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True)
class CultNetSimulationConsensusOptions:
    minimum_witnesses: int = 1
    minimum_weight: float = 1.0
    quorum_ratio: float = 0.5


@dataclass
class CultNetSimulationConsensus:
    options: CultNetSimulationConsensusOptions = field(default_factory=CultNetSimulationConsensusOptions)

    def build_candidates(self, observations: list[dict[str, Any]]) -> list[dict[str, Any]]:
        clean = _dedupe_observations([observation for observation in observations if _is_valid_observation(observation)])
        groups: dict[tuple[str, int, int, str, str], list[dict[str, Any]]] = {}
        for observation in clean:
            groups.setdefault(_subject_key(observation), []).append(observation)
        return [
            self._build_candidate(group)
            for _, group in sorted(groups.items(), key=lambda item: item[0])
        ]

    def _build_candidate(self, observations: list[dict[str, Any]]) -> dict[str, Any]:
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
        return {
            "schemaVersion": "cultnet.simulation_consensus_candidate.v0",
            "messageId": "",
            "shardId": str(first.get("shardId") or ""),
            "shardEpoch": int(first.get("shardEpoch") or 0),
            "frame": int(first.get("frame") or 0),
            "subjectId": str(first.get("subjectId") or ""),
            "claimKind": str(first.get("claimKind") or ""),
            "claimHash": claim_hash,
            "claimSummary": first.get("claimSummary"),
            "witnessCount": witness_count,
            "supportWeight": support_weight,
            "totalWeight": total_weight,
            "hasQuorum": (
                witness_count >= self.options.minimum_witnesses
                and support_weight >= self.options.minimum_weight
                and total_weight > 0.0
                and confidence >= self.options.quorum_ratio
            ),
            "confidence": confidence,
        }


@dataclass
class CultNetSimulationObservationHub:
    options: CultNetSimulationConsensusOptions = field(default_factory=CultNetSimulationConsensusOptions)
    observations: list[dict[str, Any]] = field(default_factory=list)

    def __post_init__(self) -> None:
        self._consensus = CultNetSimulationConsensus(self.options)

    def submit(self, observation_or_message: dict[str, Any]) -> list[dict[str, Any]]:
        observation = observation_or_message.get("observation", observation_or_message)
        if not isinstance(observation, dict):
            raise ValueError("Simulation observation must be a map")
        self.observations.append(observation)
        return [
            candidate
            for candidate in self._consensus.build_candidates(self.observations)
            if _candidate_matches_observation(candidate, observation)
        ]


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
