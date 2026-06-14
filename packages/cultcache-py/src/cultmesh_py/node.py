from __future__ import annotations

from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Callable

from cultcache_py import CultCache, CultCacheEnvelope, SingleFileMessagePackBackingStore
from cultcache_py.documents import DocumentDefinition, extract_value
from cultnet_py import (
    CultNetAppliedRecord,
    CultNetMessage,
    CultNetRawClient,
    CultNetRawSnapshotResponse,
    CultNetShardLogResponse,
    apply_raw_snapshot as apply_cultnet_raw_snapshot,
    apply_shard_log_response as apply_cultnet_shard_log_response,
    document_delete,
    document_put_raw,
    schema_document_map,
)


@dataclass(frozen=True)
class CultMeshDatabaseChange:
    document: DocumentDefinition[Any]
    schema_id: str
    record_key: str
    change_kind: str
    value: Any | None = None
    previous_value: Any | None = None


@dataclass
class CultMeshNode:
    cache: CultCache = field(default_factory=CultCache)
    runtime_id: str = "python-runtime"
    documents: list[DocumentDefinition[Any]] = field(default_factory=list)
    database: "CultMeshDatabase" = field(init=False)

    def __post_init__(self) -> None:
        self.database = CultMeshDatabase(self)

    def register_document(self, document: DocumentDefinition[Any]) -> None:
        self.database.register_document(document)

    def put(self, document: DocumentDefinition[Any], key: str, value: Any) -> None:
        self.database.put(document, key, value)

    def put_global(self, document: DocumentDefinition[Any], value: Any) -> None:
        self.database.put_global(document, value)

    def put_raw_message(
        self,
        document: DocumentDefinition[Any],
        key: str,
        value: Any,
        *,
        message_id: str = "",
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> CultNetMessage:
        return self.database.put_raw_message(
            document,
            key,
            value,
            message_id=message_id,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    def get(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.database.get(document, key)

    def get_required(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.database.get_required(document, key)

    def get_global(self, document: DocumentDefinition[Any]) -> Any:
        return self.database.get_global(document)

    def get_required_global(self, document: DocumentDefinition[Any]) -> Any:
        return self.database.get_required_global(document)

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self.database.delete(document, key)

    def delete_global(self, document: DocumentDefinition[Any]) -> None:
        self.database.delete_global(document)

    def delete_raw_message(
        self,
        document: DocumentDefinition[Any],
        key: str,
        *,
        message_id: str = "",
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> CultNetMessage:
        return self.database.delete_raw_message(
            document,
            key,
            message_id=message_id,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    def snapshot(self) -> dict[str, dict[str, Any]]:
        return self.database.snapshot()

    def create_snapshot_response(
        self,
        *,
        message_id: str = "",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        shard_log_sequence: int | None = None,
    ) -> dict[str, Any]:
        return self.database.create_snapshot_response(
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            shard_log_sequence=shard_log_sequence,
        )

    def pull(self) -> None:
        self.database.pull()

    def sync_snapshot(
        self,
        client: CultNetRawClient,
        *,
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> list[CultNetAppliedRecord]:
        return self.database.sync_snapshot(
            client,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    def sync_shard_log(
        self,
        client: CultNetRawClient,
        *,
        shard_id: str,
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> list[CultNetAppliedRecord]:
        return self.database.sync_shard_log(
            client,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        )

    def create_shard_log_response(
        self,
        *,
        shard_id: str,
        message_id: str = "",
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> dict[str, Any]:
        return self.database.create_shard_log_response(
            shard_id=shard_id,
            message_id=message_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        )


class CultMeshDatabase:
    def __init__(self, node: CultMeshNode) -> None:
        self._node = node
        self._subscribers: list[tuple[DocumentDefinition[Any] | None, str | None, Callable[[CultMeshDatabaseChange], None]]] = []
        self._shard_logs: dict[str, list[dict[str, Any]]] = {}

    @property
    def cache(self) -> CultCache:
        return self._node.cache

    @property
    def documents(self) -> list[DocumentDefinition[Any]]:
        return self._node.documents

    @property
    def runtime_id(self) -> str:
        return self._node.runtime_id

    def watch(
        self,
        callback: Callable[[CultMeshDatabaseChange], None],
        *,
        document: DocumentDefinition[Any] | None = None,
        key: str | None = None,
    ) -> Callable[[], None]:
        subscriber = (document, key, callback)
        self._subscribers.append(subscriber)

        def unsubscribe() -> None:
            if subscriber in self._subscribers:
                self._subscribers.remove(subscriber)

        return unsubscribe

    def watch_record(
        self,
        document: DocumentDefinition[Any],
        key: str,
        callback: Callable[[CultMeshDatabaseChange], None],
    ) -> Callable[[], None]:
        return self.watch(callback, document=document, key=key)

    def watch_global(
        self,
        document: DocumentDefinition[Any],
        callback: Callable[[CultMeshDatabaseChange], None],
    ) -> Callable[[], None]:
        return self.watch_record(document, self.cache.GLOBAL_KEY, callback)

    def watch_by_name(
        self,
        document: DocumentDefinition[Any],
        name: str,
        callback: Callable[[CultMeshDatabaseChange], None],
    ) -> Callable[[], None]:
        if not name:
            raise ValueError("Name watch values must be non-empty")
        if document.name is None:
            raise ValueError(f"Document type {document.type!r} has no name lookup")

        def filtered(change: CultMeshDatabaseChange) -> None:
            if self._change_matches_extractor(change, document.name, name):
                callback(change)

        return self.watch(filtered, document=document)

    def watch_by_index(
        self,
        document: DocumentDefinition[Any],
        index: str,
        value: str,
        callback: Callable[[CultMeshDatabaseChange], None],
    ) -> Callable[[], None]:
        if not index:
            raise ValueError("Index watch aliases must be non-empty")
        if not value:
            raise ValueError("Index watch values must be non-empty")
        extractor = document.indexes.get(index)
        if extractor is None:
            raise ValueError(f"Document type {document.type!r} has no index {index!r}")

        def filtered(change: CultMeshDatabaseChange) -> None:
            if self._change_matches_extractor(change, extractor, value):
                callback(change)

        return self.watch(filtered, document=document)

    def register_document(self, document: DocumentDefinition[Any]) -> None:
        for registered in self.documents:
            if registered.type != document.type:
                continue
            if registered == document:
                return
            raise ValueError(f"Document type {document.type!r} is already registered with a different definition")
        self.cache.register_document_type(document)
        self.documents.append(document)

    def put(self, document: DocumentDefinition[Any], key: str, value: Any) -> None:
        previous = self.cache.get(document, key)
        self.cache.put(document, key, value)
        self._publish_local_change(document, key, "added" if previous is None else "updated", value, previous)

    def put_global(self, document: DocumentDefinition[Any], value: Any) -> None:
        self.put(document, self.cache.GLOBAL_KEY, value)

    def put_raw_message(
        self,
        document: DocumentDefinition[Any],
        key: str,
        value: Any,
        *,
        message_id: str = "",
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> CultNetMessage:
        catalog_entry = document.catalog_entry()
        previous = self.cache.get(document, key)
        envelope = CultCacheEnvelope.create(
            key=key,
            type=document.type,
            payload=document.encode_payload(value),
            schema_id=catalog_entry.schema_id,
            catalog_entry=catalog_entry,
        )
        value = self.cache.put_envelope(document, envelope)
        self._publish_local_change(document, key, "added" if previous is None else "updated", value, previous)
        message = document_put_raw(
            message_id=message_id,
            key=key,
            schema_id=envelope.schema_id or catalog_entry.schema_id,
            stored_at=envelope.stored_at,
            payload=envelope.payload,
            source_runtime_id=self.runtime_id,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )
        self._append_shard_log_put(message, "added" if previous is None else "updated")
        return message

    def get(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get(document, key)

    def get_required(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get_required(document, key)

    def get_global(self, document: DocumentDefinition[Any]) -> Any:
        return self.cache.get_global(document)

    def get_required_global(self, document: DocumentDefinition[Any]) -> Any:
        return self.cache.get_required_global(document)

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        previous = self.cache.get(document, key)
        self.cache.delete(document, key)
        if previous is not None:
            self._publish_local_change(document, key, "removed", None, previous)

    def delete_global(self, document: DocumentDefinition[Any]) -> None:
        self.delete(document, self.cache.GLOBAL_KEY)

    def delete_raw_message(
        self,
        document: DocumentDefinition[Any],
        key: str,
        *,
        message_id: str = "",
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> CultNetMessage:
        schema_id = document.catalog_entry().schema_id
        self.delete(document, key)
        message = document_delete(
            message_id=message_id,
            schema_id=schema_id,
            record_key=key,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )
        self._append_shard_log_delete(message)
        return message

    def apply_raw_put_message(self, message: dict[str, Any]) -> CultMeshDatabaseChange | None:
        document_record = message.get("document")
        if not isinstance(document_record, dict):
            return None
        schema_id = str(document_record.get("schemaId") or "")
        document = self._document_for_schema(schema_id)
        if document is None:
            return None
        record_key = str(document_record["recordKey"])
        previous = self.cache.get(document, record_key)
        envelope = CultCacheEnvelope(
            key=record_key,
            type=document.type,
            schema_id=schema_id,
            payload=bytes(document_record["payload"]),
            stored_at=str(document_record.get("storedAt") or ""),
            catalog_entry=document.catalog_entry(),
        )
        value = self.cache.put_envelope(document, envelope)
        change_kind = "added" if previous is None else "updated"
        change = self._publish_local_change(document, record_key, change_kind, value, previous)
        self._append_shard_log_put(CultNetMessage(
            "cultnet.document_put_raw.v0",
            {key: value for key, value in message.items() if key != "schemaVersion"},
        ), change_kind)
        return change

    def apply_raw_delete_message(self, message: dict[str, Any]) -> CultMeshDatabaseChange | None:
        schema_id = str(message.get("schemaId") or "")
        record_key = str(message.get("recordKey") or "")
        document = self._document_for_schema(schema_id)
        if document is None or not record_key:
            return None
        previous = self.cache.get(document, record_key)
        if previous is None:
            return None
        self.cache.delete(document, record_key)
        change = self._publish_local_change(document, record_key, "removed", None, previous)
        self._append_shard_log_delete(CultNetMessage(
            "cultnet.document_delete.v0",
            {key: value for key, value in message.items() if key != "schemaVersion"},
        ))
        return change

    def snapshot(self) -> dict[str, dict[str, Any]]:
        return self.cache.snapshot()

    def create_snapshot_response(
        self,
        *,
        message_id: str = "",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        shard_log_sequence: int | None = None,
    ) -> dict[str, Any]:
        requested_schema_ids = set(schema_ids or [])
        requested_record_keys = set(record_keys or [])
        documents = []
        for envelope in self.cache.snapshot_envelopes():
            schema_id = envelope.schema_id or envelope.type
            if requested_schema_ids and schema_id not in requested_schema_ids:
                continue
            if requested_record_keys and envelope.key not in requested_record_keys:
                continue
            documents.append({
                "schemaId": schema_id,
                "recordKey": envelope.key,
                "storedAt": envelope.stored_at,
                "payloadEncoding": "messagepack",
                "payload": envelope.payload,
            })

        response: dict[str, Any] = {
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": message_id,
            "documents": documents,
        }
        if shard_id is not None:
            response["shardId"] = shard_id
        if shard_epoch is not None:
            response["shardEpoch"] = shard_epoch
        if shard_log_sequence is not None:
            response["shardLogSequence"] = shard_log_sequence
        return response

    def pull(self) -> None:
        self.cache.pull_all_backing_stores()

    def sync_snapshot(
        self,
        client: CultNetRawClient,
        *,
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> list[CultNetAppliedRecord]:
        response = client.fetch_snapshot_response(
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )
        return self.apply_snapshot_response(response)

    def sync_shard_log(
        self,
        client: CultNetRawClient,
        *,
        shard_id: str,
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> list[CultNetAppliedRecord]:
        response = client.fetch_shard_log_response(
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        )
        return self.apply_shard_log_response(response)

    def create_shard_log_response(
        self,
        *,
        shard_id: str,
        message_id: str = "",
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> dict[str, Any]:
        if not shard_id:
            raise ValueError("shard_id must be non-empty")
        if after_sequence < 0:
            raise ValueError("after_sequence must be non-negative")
        entries = [
            dict(entry)
            for entry in self._shard_logs.get(shard_id, [])
            if int(entry.get("sequence") or 0) > after_sequence
        ]
        if limit is not None:
            if limit < 0:
                raise ValueError("limit must be non-negative")
            entries = entries[:limit]
        response: dict[str, Any] = {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": message_id,
            "shardId": shard_id,
            "entries": entries,
            "resyncRequired": False,
        }
        if shard_epoch is not None:
            response["shardEpoch"] = shard_epoch
        else:
            response["shardEpoch"] = self._latest_shard_epoch(shard_id) or 0
        return response

    def apply_snapshot_response(
        self,
        response: dict[str, Any] | CultNetRawSnapshotResponse,
    ) -> list[CultNetAppliedRecord]:
        wire = response.to_wire() if isinstance(response, CultNetRawSnapshotResponse) else response
        previous = self._previous_values_for_snapshot_response(wire)
        applied = apply_cultnet_raw_snapshot(self.cache, self.documents, wire)
        self._publish_applied_records(applied, previous)
        return applied

    def apply_shard_log_response(
        self,
        response: dict[str, Any] | CultNetShardLogResponse,
    ) -> list[CultNetAppliedRecord]:
        wire = response.to_wire() if isinstance(response, CultNetShardLogResponse) else response
        previous = self._previous_values_for_shard_log_response(wire)
        applied = apply_cultnet_shard_log_response(self.cache, self.documents, wire)
        self._publish_applied_records(applied, previous)
        return applied

    def _publish_local_change(
        self,
        document: DocumentDefinition[Any],
        key: str,
        change_kind: str,
        value: Any | None,
        previous: Any | None,
    ) -> CultMeshDatabaseChange:
        change = CultMeshDatabaseChange(
            document=document,
            schema_id=document.catalog_entry().schema_id,
            record_key=key,
            change_kind=change_kind,
            value=value,
            previous_value=previous,
        )
        self._publish(change)
        return change

    def _publish_applied_records(
        self,
        applied: list[CultNetAppliedRecord],
        previous: dict[tuple[str, str], Any],
    ) -> None:
        documents_by_schema_id = schema_document_map(self.documents)
        for record in applied:
            document = documents_by_schema_id.get(record.schema_id)
            if document is None:
                continue
            previous_value = previous.get((record.schema_id, record.record_key))
            change_kind = record.change_kind
            if change_kind == "added" and previous_value is not None:
                change_kind = "updated"
            self._publish(CultMeshDatabaseChange(
                document=document,
                schema_id=record.schema_id,
                record_key=record.record_key,
                change_kind=change_kind,
                value=record.value,
                previous_value=previous_value,
            ))

    def _publish(self, change: CultMeshDatabaseChange) -> None:
        for document, key, callback in list(self._subscribers):
            if document is not None and document.type != change.document.type:
                continue
            if key is not None and key != change.record_key:
                continue
            callback(change)

    def _append_shard_log_put(self, message: CultNetMessage, change_kind: str) -> None:
        wire = message.to_wire()
        shard_id = wire.get("shardId")
        if not shard_id:
            return
        self._append_shard_log_entry(
            str(shard_id),
            {
                "changeKind": change_kind,
                "put": wire,
            },
        )

    def _append_shard_log_delete(self, message: CultNetMessage) -> None:
        wire = message.to_wire()
        shard_id = wire.get("shardId")
        if not shard_id:
            return
        self._append_shard_log_entry(
            str(shard_id),
            {
                "changeKind": "removed",
                "delete": wire,
            },
        )

    def _append_shard_log_entry(self, shard_id: str, entry: dict[str, Any]) -> None:
        log = self._shard_logs.setdefault(shard_id, [])
        entry["sequence"] = len(log) + 1
        entry["committedAt"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
        log.append(entry)

    def _latest_shard_epoch(self, shard_id: str) -> int | None:
        log = self._shard_logs.get(shard_id, [])
        for entry in reversed(log):
            put = entry.get("put")
            if isinstance(put, dict) and isinstance(put.get("shardEpoch"), int):
                return put["shardEpoch"]
            delete = entry.get("delete")
            if isinstance(delete, dict) and isinstance(delete.get("shardEpoch"), int):
                return delete["shardEpoch"]
        return None

    def _document_for_schema(self, schema_id: str) -> DocumentDefinition[Any] | None:
        for document in self.documents:
            entry = document.catalog_entry()
            if schema_id == entry.schema_id or schema_id in entry.compatible_schema_ids:
                return document
        return None

    def _change_matches_extractor(
        self,
        change: CultMeshDatabaseChange,
        extractor: str | Callable[[Any], str | int | float | bool | None],
        expected: str,
    ) -> bool:
        return (
            self._value_matches_extractor(change.value, extractor, expected)
            or self._value_matches_extractor(change.previous_value, extractor, expected)
        )

    def _value_matches_extractor(
        self,
        value: Any | None,
        extractor: str | Callable[[Any], str | int | float | bool | None],
        expected: str,
    ) -> bool:
        if value is None:
            return False
        actual = extract_value(value, extractor)
        return actual is not None and str(actual) == expected

    def _previous_values_for_snapshot_response(self, response: dict[str, Any]) -> dict[tuple[str, str], Any]:
        if response.get("schemaVersion") != "cultnet.snapshot_response_raw.v0":
            return {}
        return self._previous_values_for_raw_records(response.get("documents", []))

    def _previous_values_for_shard_log_response(self, response: dict[str, Any]) -> dict[tuple[str, str], Any]:
        if response.get("schemaVersion") != "cultnet.shard_log_response.v0":
            return {}
        records: list[dict[str, Any]] = []
        deletes: list[dict[str, Any]] = []
        for entry in response.get("entries", []):
            if not isinstance(entry, dict):
                continue
            if isinstance(entry.get("put"), dict) and isinstance(entry["put"].get("document"), dict):
                records.append(entry["put"]["document"])
            elif isinstance(entry.get("delete"), dict):
                deletes.append(entry["delete"])
        previous = self._previous_values_for_raw_records(records)
        documents_by_schema_id = schema_document_map(self.documents)
        for delete in deletes:
            schema_id = str(delete.get("schemaId"))
            document = documents_by_schema_id.get(schema_id)
            if document is None:
                continue
            record_key = str(delete.get("recordKey"))
            previous[(schema_id, record_key)] = self.cache.get(document, record_key)
        return previous

    def _previous_values_for_raw_records(self, records: Any) -> dict[tuple[str, str], Any]:
        previous: dict[tuple[str, str], Any] = {}
        documents_by_schema_id = schema_document_map(self.documents)
        for record in records:
            if not isinstance(record, dict):
                continue
            schema_id = str(record.get("schemaId"))
            document = documents_by_schema_id.get(schema_id)
            if document is None:
                continue
            record_key = str(record.get("recordKey"))
            previous[(schema_id, record_key)] = self.cache.get(document, record_key)
        return previous


def create_node(cache_path: str | Path | None = None, *, runtime_id: str = "python-runtime") -> CultMeshNode:
    cache = CultCache()
    if cache_path is not None:
        cache.add_generic_store(SingleFileMessagePackBackingStore(cache_path))
    return CultMeshNode(cache=cache, runtime_id=runtime_id)
