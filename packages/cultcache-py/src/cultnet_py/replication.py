from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Iterable

from cultcache_py import CultCache, CultCacheEnvelope
from cultcache_py.documents import DocumentDefinition

from .shard_log import CultNetShardLogResponse
from .snapshot import CultNetRawDocumentRecord, CultNetRawSnapshotResponse


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
    record: dict[str, Any] | CultNetRawDocumentRecord,
) -> CultNetAppliedRecord:
    wire = record.to_wire() if isinstance(record, CultNetRawDocumentRecord) else record
    schema_id = str(wire["schemaId"])
    document = documents_by_schema_id[schema_id]
    envelope = raw_record_to_envelope(document, schema_id, wire)
    value = cache.put_envelope(document, envelope)
    return CultNetAppliedRecord(
        schema_id=schema_id,
        record_key=envelope.key,
        change_kind="added",
        value=value,
    )


def raw_record_to_envelope(
    document: DocumentDefinition[Any],
    schema_id: str,
    record: dict[str, Any],
) -> CultCacheEnvelope:
    return CultCacheEnvelope(
        key=str(record["recordKey"]),
        type=document.type,
        schema_id=schema_id,
        payload=bytes(record["payload"]),
        stored_at=str(record.get("storedAt") or ""),
        catalog_entry=document.catalog_entry(),
    )


def apply_raw_snapshot(
    cache: CultCache,
    documents: Iterable[DocumentDefinition[Any]],
    response: dict[str, Any] | CultNetRawSnapshotResponse,
) -> list[CultNetAppliedRecord]:
    response = response.to_wire() if isinstance(response, CultNetRawSnapshotResponse) else response
    if response.get("schemaVersion") != "cultnet.snapshot_response_raw.v0":
        raise ValueError(f"Expected cultnet.snapshot_response_raw.v0, received {response.get('schemaVersion')!r}")
    documents_by_schema_id = schema_document_map(documents)
    batches: dict[str, tuple[DocumentDefinition[Any], list[tuple[str, CultCacheEnvelope]]]] = {}
    order: list[tuple[DocumentDefinition[Any], str, CultCacheEnvelope]] = []
    for record in response.get("documents", []):
        if not isinstance(record, dict):
            continue
        schema_id = str(record["schemaId"])
        document = documents_by_schema_id[schema_id]
        envelope = raw_record_to_envelope(document, schema_id, record)
        batch = batches.setdefault(document.type, (document, []))
        batch[1].append((schema_id, envelope))
        order.append((document, schema_id, envelope))

    values_by_key: dict[tuple[str, str], Any] = {}
    for document, entries in batches.values():
        envelopes = [envelope for _, envelope in entries]
        values = cache.put_envelopes(document, envelopes)
        for (_, envelope), value in zip(entries, values):
            values_by_key[(document.type, envelope.key)] = value

    return [
        CultNetAppliedRecord(
            schema_id=schema_id,
            record_key=envelope.key,
            change_kind="added",
            value=values_by_key[(document.type, envelope.key)],
        )
        for document, schema_id, envelope in order
    ]


def apply_shard_log_response(
    cache: CultCache,
    documents: Iterable[DocumentDefinition[Any]],
    response: dict[str, Any] | CultNetShardLogResponse,
) -> list[CultNetAppliedRecord]:
    response = response.to_wire() if isinstance(response, CultNetShardLogResponse) else response
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
            schema_id = str(put["document"]["schemaId"])
            if schema_id not in documents_by_schema_id:
                continue
            result = apply_raw_document_record(cache, documents_by_schema_id, put["document"])
            applied.append(CultNetAppliedRecord(result.schema_id, result.record_key, str(change_kind), result.value))
        elif change_kind == "removed" and isinstance(entry.get("delete"), dict):
            delete = entry["delete"]
            schema_id = str(delete["schemaId"])
            record_key = str(delete["recordKey"])
            if schema_id not in documents_by_schema_id:
                continue
            document = documents_by_schema_id[schema_id]
            cache.delete(document, record_key)
            applied.append(CultNetAppliedRecord(schema_id, record_key, "removed", None))
    return applied
