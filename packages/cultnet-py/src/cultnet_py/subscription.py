from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from .snapshot import CultNetRawDocumentRecord


@dataclass(frozen=True)
class CultNetDatabaseChange:
    message_id: str
    subscription_id: str
    change_kind: str
    document: dict[str, Any] | None = None
    raw_document: CultNetRawDocumentRecord | None = None
    schema_id: str | None = None
    record_key: str | None = None
    commit_id: str | None = None
    """Set on every change of one multi-record commit, the same for all of them; None outside one (C# writes nil)."""
    commit_count: int | None = None
    """How many changes the commit holds (>= 2); None outside one. Carried data: Python applies no batch."""

    @classmethod
    def from_wire(cls, message: dict[str, Any]) -> "CultNetDatabaseChange":
        if message.get("schemaVersion") != "cultnet.database_change_raw.v0":
            raise ValueError(f"Expected cultnet.database_change_raw.v0, received {message.get('schemaVersion')!r}")
        change_kind = str(message.get("changeKind") or "")
        if change_kind not in {"added", "updated", "removed"}:
            raise ValueError(f"unsupported database changeKind {change_kind!r}")
        document = message.get("document")
        document_record = dict(document) if isinstance(document, dict) else None
        raw_document = CultNetRawDocumentRecord.from_wire(document_record) if document_record is not None else None
        schema_id = _optional_string(message.get("schemaId"))
        record_key = _optional_string(message.get("recordKey"))
        if raw_document is not None:
            schema_id = schema_id or raw_document.schema_id
            record_key = record_key or raw_document.record_key
        commit_id = message.get("commitId")
        if commit_id is not None and not (isinstance(commit_id, str) and commit_id):
            raise ValueError(f"database change commitId must be a non-empty string, received {commit_id!r}")
        commit_count = message.get("commitCount")
        if commit_count is not None and (isinstance(commit_count, bool) or not isinstance(commit_count, int) or commit_count < 2):
            raise ValueError(f"database change commitCount must be an integer of at least 2, received {commit_count!r}")
        return cls(
            message_id=str(message.get("messageId") or ""),
            subscription_id=str(message.get("subscriptionId") or ""),
            change_kind=change_kind,
            document=document_record,
            raw_document=raw_document,
            schema_id=schema_id,
            record_key=record_key,
            commit_id=commit_id,
            commit_count=commit_count,
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": self.message_id,
            "subscriptionId": self.subscription_id,
            "changeKind": self.change_kind,
        }
        if self.raw_document is not None:
            wire["document"] = self.raw_document.to_wire()
        elif self.document is not None:
            wire["document"] = self.document
        if self.schema_id is not None:
            wire["schemaId"] = self.schema_id
        if self.record_key is not None:
            wire["recordKey"] = self.record_key
        if self.commit_id is not None:
            wire["commitId"] = self.commit_id
        if self.commit_count is not None:
            wire["commitCount"] = self.commit_count
        return wire


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
