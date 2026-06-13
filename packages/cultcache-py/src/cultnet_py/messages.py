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
    return CultNetMessage("cultnet.document_put_raw.v0", {"record": record})


def document_delete(*, schema_id: str, record_key: str, deleted_at: str) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.document_delete.v0",
        {"schemaId": schema_id, "recordKey": record_key, "deletedAt": deleted_at},
    )


def snapshot_request(*, schema_ids: list[str] | None = None) -> CultNetMessage:
    return CultNetMessage("cultnet.snapshot_request.v0", {"schemaIds": schema_ids or []})


def schema_catalog_request(*, include_schema_json: bool = False) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.schema_catalog_request.v0",
        {"includeSchemaJson": include_schema_json},
    )
