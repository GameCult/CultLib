from __future__ import annotations

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
    return CultNetMessage("cultnet.document_put_raw.v0", {"messageId": message_id, "document": record})


def document_delete(*, schema_id: str, record_key: str, deleted_at: str) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.document_delete.v0",
        {"schemaId": schema_id, "recordKey": record_key, "deletedAt": deleted_at},
    )


def snapshot_request(*, message_id: str = "", schema_ids: list[str] | None = None, record_keys: list[str] | None = None) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.snapshot_request.v0",
        {"messageId": message_id, "schemaIds": schema_ids or [], "recordKeys": record_keys or []},
    )


def schema_catalog_request(*, message_id: str = "", include_schema_json: bool = False) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.schema_catalog_request.v0",
        {"messageId": message_id, "includeSchemaJson": include_schema_json},
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
