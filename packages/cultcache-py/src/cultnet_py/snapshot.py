from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class CultNetRawDocumentRecord:
    schema_id: str
    record_key: str
    payload: bytes
    stored_at: str | None = None
    payload_encoding: str = "messagepack"
    source_runtime_id: str | None = None
    source_agent_id: str | None = None
    source_role: str | None = None
    tags: tuple[str, ...] = ()

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetRawDocumentRecord":
        schema_id = str(value.get("schemaId") or "")
        record_key = str(value.get("recordKey") or "")
        if not schema_id:
            raise ValueError("raw document record schemaId must be non-empty")
        if not record_key:
            raise ValueError("raw document record recordKey must be non-empty")
        return cls(
            schema_id=schema_id,
            record_key=record_key,
            payload=bytes(value.get("payload") or b""),
            stored_at=_optional_string(value.get("storedAt")),
            payload_encoding=str(value.get("payloadEncoding") or "messagepack"),
            source_runtime_id=_optional_string(value.get("sourceRuntimeId")),
            source_agent_id=_optional_string(value.get("sourceAgentId")),
            source_role=_optional_string(value.get("sourceRole")),
            tags=tuple(str(tag) for tag in value.get("tags") or ()),
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaId": self.schema_id,
            "recordKey": self.record_key,
            "payloadEncoding": self.payload_encoding,
            "payload": self.payload,
        }
        if self.stored_at is not None:
            wire["storedAt"] = self.stored_at
        if self.source_runtime_id is not None:
            wire["sourceRuntimeId"] = self.source_runtime_id
        if self.source_agent_id is not None:
            wire["sourceAgentId"] = self.source_agent_id
        if self.source_role is not None:
            wire["sourceRole"] = self.source_role
        if self.tags:
            wire["tags"] = list(self.tags)
        return wire


@dataclass(frozen=True)
class CultNetRawSnapshotResponse:
    message_id: str
    documents: tuple[CultNetRawDocumentRecord, ...]
    shard_id: str | None = None
    shard_epoch: int | None = None
    shard_log_sequence: int | None = None

    @classmethod
    def from_wire(cls, response: dict[str, Any]) -> "CultNetRawSnapshotResponse":
        if response.get("schemaVersion") != "cultnet.snapshot_response_raw.v0":
            raise ValueError(f"Expected cultnet.snapshot_response_raw.v0, received {response.get('schemaVersion')!r}")
        return cls(
            message_id=str(response.get("messageId") or ""),
            documents=tuple(
                CultNetRawDocumentRecord.from_wire(document)
                for document in response.get("documents") or ()
                if isinstance(document, dict)
            ),
            shard_id=_optional_string(response.get("shardId")),
            shard_epoch=_optional_int(response.get("shardEpoch")),
            shard_log_sequence=_optional_int(response.get("shardLogSequence")),
        )

    def filter(
        self,
        *,
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
    ) -> tuple[CultNetRawDocumentRecord, ...]:
        requested_schema_ids = set(schema_ids or [])
        requested_record_keys = set(record_keys or [])
        return tuple(
            document
            for document in self.documents
            if (not requested_schema_ids or document.schema_id in requested_schema_ids)
            and (not requested_record_keys or document.record_key in requested_record_keys)
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": self.message_id,
            "documents": [document.to_wire() for document in self.documents],
        }
        if self.shard_id is not None:
            wire["shardId"] = self.shard_id
        if self.shard_epoch is not None:
            wire["shardEpoch"] = self.shard_epoch
        if self.shard_log_sequence is not None:
            wire["shardLogSequence"] = self.shard_log_sequence
        return wire


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None


def _optional_int(value: Any) -> int | None:
    if value is None:
        return None
    return int(value)
