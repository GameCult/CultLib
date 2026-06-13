from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Iterable

from cultcache_py import CultCache, CultCacheEnvelope
from cultcache_py.documents import DocumentDefinition


@dataclass(frozen=True)
class CultNetAppliedRecord:
    schema_id: str
    record_key: str
    change_kind: str
    value: Any | None = None


def schema_document_map(documents: Iterable[DocumentDefinition[Any]]) -> dict[str, DocumentDefinition[Any]]:
    mapped: dict[str, DocumentDefinition[Any]] = {}
    for document in documents:
        catalog_entry = document.catalog_entry()
        mapped[catalog_entry.schema_id] = document
        for compatible_schema_id in catalog_entry.compatible_schema_ids:
            mapped[compatible_schema_id] = document
    return mapped


def apply_raw_document_record(
    cache: CultCache,
    documents_by_schema_id: dict[str, DocumentDefinition[Any]],
    record: dict[str, Any],
) -> CultNetAppliedRecord:
    schema_id = str(record["schemaId"])
    document = documents_by_schema_id[schema_id]
    envelope = CultCacheEnvelope(
        key=str(record["recordKey"]),
        type=document.type,
        schema_id=schema_id,
        payload=bytes(record["payload"]),
        stored_at=str(record.get("storedAt") or ""),
        catalog_entry=document.catalog_entry(),
    )
    value = cache.put_envelope(document, envelope)
    return CultNetAppliedRecord(
        schema_id=schema_id,
        record_key=envelope.key,
        change_kind="added",
        value=value,
    )


def apply_raw_snapshot(
    cache: CultCache,
    documents: Iterable[DocumentDefinition[Any]],
    response: dict[str, Any],
) -> list[CultNetAppliedRecord]:
    if response.get("schemaVersion") != "cultnet.snapshot_response_raw.v0":
        raise ValueError(f"Expected cultnet.snapshot_response_raw.v0, received {response.get('schemaVersion')!r}")
    documents_by_schema_id = schema_document_map(documents)
    return [
        apply_raw_document_record(cache, documents_by_schema_id, record)
        for record in response.get("documents", [])
        if isinstance(record, dict)
    ]


def apply_shard_log_response(
    cache: CultCache,
    documents: Iterable[DocumentDefinition[Any]],
    response: dict[str, Any],
) -> list[CultNetAppliedRecord]:
    if response.get("schemaVersion") != "cultnet.shard_log_response.v0":
        raise ValueError(f"Expected cultnet.shard_log_response.v0, received {response.get('schemaVersion')!r}")
    if response.get("resyncRequired") is True:
        reason = response.get("reason") or "unspecified"
        raise ValueError(f"Shard log response requires resync: {reason}")
    documents_by_schema_id = schema_document_map(documents)
    applied: list[CultNetAppliedRecord] = []
    for entry in response.get("entries", []):
        if not isinstance(entry, dict):
            continue
        change_kind = entry.get("changeKind")
        if change_kind in ("added", "updated") and isinstance(entry.get("put"), dict):
            put = entry["put"]
            result = apply_raw_document_record(cache, documents_by_schema_id, put["document"])
            applied.append(CultNetAppliedRecord(result.schema_id, result.record_key, str(change_kind), result.value))
        elif change_kind == "removed" and isinstance(entry.get("delete"), dict):
            delete = entry["delete"]
            schema_id = str(delete["schemaId"])
            record_key = str(delete["recordKey"])
            document = documents_by_schema_id[schema_id]
            cache.delete(document, record_key)
            applied.append(CultNetAppliedRecord(schema_id, record_key, "removed", None))
    return applied

