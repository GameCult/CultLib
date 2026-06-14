from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class CultNetDatabaseChange:
    message_id: str
    subscription_id: str
    change_kind: str
    document: dict[str, Any] | None = None
    schema_id: str | None = None
    record_key: str | None = None

    @classmethod
    def from_wire(cls, message: dict[str, Any]) -> "CultNetDatabaseChange":
        if message.get("schemaVersion") != "cultnet.database_change_raw.v0":
            raise ValueError(f"Expected cultnet.database_change_raw.v0, received {message.get('schemaVersion')!r}")
        change_kind = str(message.get("changeKind") or "")
        if change_kind not in {"added", "updated", "removed"}:
            raise ValueError(f"unsupported database changeKind {change_kind!r}")
        document = message.get("document")
        document_record = dict(document) if isinstance(document, dict) else None
        schema_id = _optional_string(message.get("schemaId"))
        record_key = _optional_string(message.get("recordKey"))
        if document_record is not None:
            schema_id = schema_id or _optional_string(document_record.get("schemaId"))
            record_key = record_key or _optional_string(document_record.get("recordKey"))
        return cls(
            message_id=str(message.get("messageId") or ""),
            subscription_id=str(message.get("subscriptionId") or ""),
            change_kind=change_kind,
            document=document_record,
            schema_id=schema_id,
            record_key=record_key,
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": self.message_id,
            "subscriptionId": self.subscription_id,
            "changeKind": self.change_kind,
        }
        if self.document is not None:
            wire["document"] = self.document
        if self.schema_id is not None:
            wire["schemaId"] = self.schema_id
        if self.record_key is not None:
            wire["recordKey"] = self.record_key
        return wire


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
