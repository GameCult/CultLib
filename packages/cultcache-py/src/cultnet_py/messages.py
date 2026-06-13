from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Any

import msgpack


@dataclass(frozen=True)
class CultNetMessage:
    schema_version: str
    body: dict[str, Any]

    def to_wire(self) -> dict[str, Any]:
        return {"schemaVersion": self.schema_version, **self.body}

    def to_bytes(self) -> bytes:
        return msgpack.packb(self.to_wire(), use_bin_type=True)


def parse_message(payload: bytes) -> CultNetMessage:
    decoded = msgpack.unpackb(payload, raw=False)
    if not isinstance(decoded, dict):
        raise ValueError("CultNet schema-v0 messages must be MessagePack maps")
    schema_version = decoded.get("schemaVersion")
    if not isinstance(schema_version, str) or not schema_version:
        raise ValueError("CultNet schema-v0 messages must declare schemaVersion")
    body = dict(decoded)
    del body["schemaVersion"]
    return CultNetMessage(schema_version=schema_version, body=body)


def hello(
    *,
    runtime_id: str,
    supported_wire_contracts: list[str] | None = None,
    supported_schema_versions: list[str] | None = None,
    supported_mutation_contracts: list[dict[str, Any]] | None = None,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.hello.v0",
        {
            "runtimeId": runtime_id,
            "supportedWireContracts": supported_wire_contracts or ["cultnet.schema.v0"],
            "supportedSchemaVersions": supported_schema_versions or [],
            "supportedMutationContracts": supported_mutation_contracts or [],
        },
    )


def document_put_raw(
    *,
    message_id: str = "",
    key: str,
    schema_id: str,
    stored_at: str,
    payload: bytes,
    source_runtime_id: str | None = None,
    shard_id: str | None = None,
    shard_epoch: int | None = None,
) -> CultNetMessage:
    record: dict[str, Any] = {
        "schemaId": schema_id,
        "recordKey": key,
        "storedAt": stored_at,
        "payloadEncoding": "messagepack",
        "payload": payload,
    }
    if source_runtime_id:
        record["sourceRuntimeId"] = source_runtime_id
    body: dict[str, Any] = {"messageId": message_id, "document": record}
    if shard_id is not None:
        body["shardId"] = shard_id
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    return CultNetMessage("cultnet.document_put_raw.v0", body)


def document_delete(
    *,
    message_id: str = "",
    schema_id: str,
    record_key: str,
    shard_id: str | None = None,
    shard_epoch: int | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {"messageId": message_id, "schemaId": schema_id, "recordKey": record_key}
    if shard_id is not None:
        body["shardId"] = shard_id
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    return CultNetMessage("cultnet.document_delete.v0", body)


def snapshot_request(
    *,
    message_id: str = "",
    schema_ids: list[str] | None = None,
    record_keys: list[str] | None = None,
    shard_id: str | None = None,
    shard_epoch: int | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {"messageId": message_id, "schemaIds": schema_ids or [], "recordKeys": record_keys or []}
    if shard_id is not None:
        body["shardId"] = shard_id
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    return CultNetMessage("cultnet.snapshot_request.v0", body)


def schema_catalog_request(
    *,
    message_id: str = "",
    include_schema_json: bool = False,
    schema_ids: list[str] | None = None,
    kinds: list[str] | None = None,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.schema_catalog_request.v0",
        {
            "messageId": message_id,
            "includeSchemaJson": include_schema_json,
            "schemaIds": schema_ids or [],
            "kinds": kinds or [],
        },
    )


def database_subscribe(
    *,
    subscription_id: str,
    message_id: str = "",
    schema_ids: list[str] | None = None,
    record_keys: list[str] | None = None,
    include_snapshot: bool = True,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.database_subscribe.v0",
        {
            "messageId": message_id,
            "subscriptionId": subscription_id,
            "schemaIds": schema_ids or [],
            "recordKeys": record_keys or [],
            "includeSnapshot": include_snapshot,
        },
    )


def database_unsubscribe(*, subscription_id: str, message_id: str = "") -> CultNetMessage:
    return CultNetMessage(
        "cultnet.database_unsubscribe.v0",
        {"messageId": message_id, "subscriptionId": subscription_id},
    )


def shard_catalog_request(
    *,
    message_id: str = "",
    schema_ids: list[str] | None = None,
    record_keys: list[str] | None = None,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.shard_catalog_request.v0",
        {"messageId": message_id, "schemaIds": schema_ids or [], "recordKeys": record_keys or []},
    )


def shard_log_request(
    *,
    shard_id: str,
    message_id: str = "",
    shard_epoch: int | None = None,
    after_sequence: int = 0,
    limit: int | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {
        "messageId": message_id,
        "shardId": shard_id,
        "afterSequence": after_sequence,
    }
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    if limit is not None:
        body["limit"] = limit
    return CultNetMessage("cultnet.shard_log_request.v0", body)


def compute_simulation_claim_hash(*parts: str) -> str:
    canonical = "\x1f".join(part or "" for part in parts)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def simulation_observation(
    *,
    message_id: str,
    witness_runtime_id: str,
    shard_id: str,
    shard_epoch: int,
    frame: int,
    subject_id: str,
    claim_kind: str,
    claim_hash: str,
    claim_summary: str | None = None,
    weight: float = 1.0,
    observed_at: str = "",
) -> CultNetMessage:
    observation: dict[str, Any] = {
        "witnessRuntimeId": witness_runtime_id,
        "shardId": shard_id,
        "shardEpoch": shard_epoch,
        "frame": frame,
        "subjectId": subject_id,
        "claimKind": claim_kind,
        "claimHash": claim_hash,
        "weight": weight,
        "observedAt": observed_at,
    }
    if claim_summary is not None:
        observation["claimSummary"] = claim_summary
    return CultNetMessage(
        "cultnet.simulation_observation.v0",
        {"messageId": message_id, "observation": observation},
    )


def witness_artifact_bundle(
    *,
    bundle_id: str,
    witness_kind: str,
    captured_at: str,
    subject: dict[str, Any],
    contracts: list[dict[str, Any]],
    artifacts: list[dict[str, Any]],
    provenance: dict[str, Any],
    timing_witnesses: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    return {
        "bundleId": bundle_id,
        "witnessKind": witness_kind,
        "capturedAt": captured_at,
        "subject": subject,
        "contracts": contracts,
        "artifacts": artifacts,
        "timingWitnesses": timing_witnesses or [],
        "provenance": provenance,
    }


def encode_witness_artifact_bundle_payload(bundle: dict[str, Any]) -> bytes:
    return msgpack.packb([
        bundle["bundleId"],
        bundle["witnessKind"],
        bundle["capturedAt"],
        bundle["subject"],
        bundle["contracts"],
        bundle["artifacts"],
        bundle.get("timingWitnesses", []),
        bundle["provenance"],
    ], use_bin_type=True)


def decode_witness_artifact_bundle_payload(payload: bytes) -> dict[str, Any]:
    slots = msgpack.unpackb(payload, raw=False)
    if not isinstance(slots, list) or len(slots) < 8:
        raise ValueError("CultNet witness artifact bundle payload must be an 8-slot MessagePack array")
    return {
        "bundleId": slots[0],
        "witnessKind": slots[1],
        "capturedAt": slots[2],
        "subject": slots[3],
        "contracts": slots[4],
        "artifacts": slots[5],
        "timingWitnesses": slots[6],
        "provenance": slots[7],
    }
