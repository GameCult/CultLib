from __future__ import annotations

import threading
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Callable

from cultcache_py import CultCache, CultCacheEnvelope, SingleFileMessagePackBackingStore
from cultcache_py.cache import CultCacheError
from cultcache_py.documents import DocumentDefinition, extract_value
from cultnet_py import (
    CultNetAppliedRecord,
    CultNetFileShardMutationLogStore,
    CultNetMessage,
    CultNetRawClient,
    CultNetRawDocumentRecord,
    CultNetRawSnapshotResponse,
    CultNetShardLogEntry,
    CultNetShardLogResponse,
    CultNetShardMutationLogStore,
    apply_raw_snapshot as apply_cultnet_raw_snapshot,
    apply_shard_log_response as apply_cultnet_shard_log_response,
    document_delete,
    document_put_raw,
    resolve_document_and_schema_id_for_raw_record,
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


@dataclass(frozen=True)
class CultMeshDocumentPublicationSource:
    kind: str
    client: CultNetRawClient | None = None
    path: Path | None = None
    shard_id: str | None = None
    shard_epoch: int | None = None

    @staticmethod
    def peer_snapshot(
        client: CultNetRawClient,
        *,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> "CultMeshDocumentPublicationSource":
        return CultMeshDocumentPublicationSource(
            kind="peer_snapshot",
            client=client,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    @staticmethod
    def single_file(path: str | Path) -> "CultMeshDocumentPublicationSource":
        return CultMeshDocumentPublicationSource(kind="single_file", path=Path(path))


@dataclass(frozen=True)
class CultMeshPublicationDocumentBinding:
    document: DocumentDefinition[Any]
    key: str
    source: CultMeshDocumentPublicationSource | None = None


@dataclass(frozen=True)
class CultMeshReactiveDocumentReconciliation:
    canonical: Any
    predicted: Any
    delta: dict[str, Any]
    version: int
    received_at: datetime


@dataclass(frozen=True)
class CultMeshReactiveDocumentOptions:
    flush_delay_seconds: float = 0.016
    detect_local_changes: bool = True
    replace_dirty_current_on_canonical_snapshot: bool = False


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

    def get_all(self, document: DocumentDefinition[Any]) -> list[Any]:
        return self.database.get_all(document)

    def get_by_name(self, document: DocumentDefinition[Any], name: str) -> Any:
        return self.database.get_by_name(document, name)

    def get_by_index(self, document: DocumentDefinition[Any], index: str, value: str) -> Any:
        return self.database.get_by_index(document, index, value)

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
        return self.build_snapshot_response(
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            shard_log_sequence=shard_log_sequence,
        ).to_wire()

    def build_snapshot_response(
        self,
        *,
        message_id: str = "",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        shard_log_sequence: int | None = None,
    ) -> CultNetRawSnapshotResponse:
        return self.database.build_snapshot_response(
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

    def sync_document(
        self,
        client: CultNetRawClient,
        document: DocumentDefinition[Any],
        key: str,
        *,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> Any:
        return self.database.sync_document(
            client,
            document,
            key,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    def reactive_document(
        self,
        document: DocumentDefinition[Any],
        key: str,
        options: CultMeshReactiveDocumentOptions | None = None,
    ) -> "CultMeshReactiveDocument":
        return self.database.reactive_document(document, key, options)

    def sync_document_from_publication(
        self,
        source: CultMeshDocumentPublicationSource,
        document: DocumentDefinition[Any],
        key: str,
    ) -> Any:
        return self.database.sync_document_from_publication(source, document, key)

    def sync_documents_from_publication(
        self,
        source: CultMeshDocumentPublicationSource,
        bindings: list[CultMeshPublicationDocumentBinding] | tuple[CultMeshPublicationDocumentBinding, ...],
    ) -> list[Any]:
        return self.database.sync_documents_from_publication(source, bindings)

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
        return self.build_shard_log_response(
            shard_id=shard_id,
            message_id=message_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        ).to_wire()

    def build_shard_log_response(
        self,
        *,
        shard_id: str,
        message_id: str = "",
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> CultNetShardLogResponse:
        return self.database.build_shard_log_response(
            shard_id=shard_id,
            message_id=message_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        )


@dataclass(frozen=True)
class CultMeshNodeOptions:
    enable_durable_shard_logs: bool = False
    shard_log_path: str | Path | None = None


class CultMeshDatabase:
    def __init__(self, node: CultMeshNode) -> None:
        self._node = node
        self._subscribers: list[tuple[DocumentDefinition[Any] | None, str | None, Callable[[CultMeshDatabaseChange], None]]] = []
        self._shard_logs: dict[str, list[dict[str, Any]]] = {}
        self._shard_log_store: CultNetShardMutationLogStore | None = None

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
        requested = document
        registered = None if document is None else self._resolve_document_alias(document)

        def converted(change: CultMeshDatabaseChange) -> None:
            if requested is None:
                callback(change)
            else:
                callback(self._change_as_document(change, requested))

        subscriber = (registered, key, converted)
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
        requested = document
        registered = self._resolve_document_alias(document)

        def converted(change: CultMeshDatabaseChange) -> None:
            callback(self._change_as_document(change, requested))

        return self.watch(converted, document=registered, key=key)

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
        requested = document
        document = self._resolve_document_alias(document)
        if not name:
            raise ValueError("Name watch values must be non-empty")
        if document.name is None:
            raise ValueError(f"Document type {document.type!r} has no name lookup")

        def filtered(change: CultMeshDatabaseChange) -> None:
            if self._change_matches_extractor(change, document.name, name):
                callback(self._change_as_document(change, requested))

        return self.watch(filtered, document=document)

    def watch_by_index(
        self,
        document: DocumentDefinition[Any],
        index: str,
        value: str,
        callback: Callable[[CultMeshDatabaseChange], None],
    ) -> Callable[[], None]:
        requested = document
        document = self._resolve_document_alias(document)
        if not index:
            raise ValueError("Index watch aliases must be non-empty")
        if not value:
            raise ValueError("Index watch values must be non-empty")
        extractor = document.indexes.get(index)
        if extractor is None:
            raise ValueError(f"Document type {document.type!r} has no index {index!r}")

        def filtered(change: CultMeshDatabaseChange) -> None:
            if self._change_matches_extractor(change, extractor, value):
                callback(self._change_as_document(change, requested))

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

    def use_shard_mutation_log_store(self, store: CultNetShardMutationLogStore) -> None:
        self._shard_log_store = store

    def put(self, document: DocumentDefinition[Any], key: str, value: Any) -> None:
        registered = self._resolve_document_alias(document)
        parsed = self._convert_document_value(value, document, registered)
        previous = self.cache.get(registered, key)
        self.cache.put(registered, key, parsed)
        self._publish_local_change(registered, key, "added" if previous is None else "updated", parsed, previous)

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
        registered = self._resolve_document_alias(document)
        parsed = self._convert_document_value(value, document, registered)
        catalog_entry = registered.catalog_entry()
        previous = self.cache.get(registered, key)
        envelope = CultCacheEnvelope.create(
            key=key,
            type=registered.type,
            payload=registered.encode_payload(parsed),
            schema_id=catalog_entry.schema_id,
            catalog_entry=catalog_entry,
        )
        value = self.cache.put_envelope(registered, envelope)
        self._publish_local_change(registered, key, "added" if previous is None else "updated", value, previous)
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
        registered = self._resolve_document_alias(document)
        value = self.cache.get(registered, key)
        return None if value is None else self._convert_document_value(value, registered, document)

    def get_required(self, document: DocumentDefinition[Any], key: str) -> Any:
        value = self.get(document, key)
        if value is None:
            raise CultCacheError(f"Missing {document.type}:{key}")
        return value

    def get_all(self, document: DocumentDefinition[Any]) -> list[Any]:
        registered = self._resolve_document_alias(document)
        return [
            self._convert_document_value(value, registered, document)
            for value in self.cache.get_all(registered)
        ]

    def get_by_name(self, document: DocumentDefinition[Any], name: str) -> Any:
        registered = self._resolve_document_alias(document)
        value = self.cache.get_by_name(registered, name)
        return None if value is None else self._convert_document_value(value, registered, document)

    def get_by_index(self, document: DocumentDefinition[Any], index: str, value: str) -> Any:
        registered = self._resolve_document_alias(document)
        result = self.cache.get_by_index(registered, index, value)
        return None if result is None else self._convert_document_value(result, registered, document)

    def get_global(self, document: DocumentDefinition[Any]) -> Any:
        return self.get(document, self.cache.GLOBAL_KEY)

    def get_required_global(self, document: DocumentDefinition[Any]) -> Any:
        return self.get_required(document, self.cache.GLOBAL_KEY)

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        registered = self._resolve_document_alias(document)
        previous = self.cache.get(registered, key)
        self.cache.delete(registered, key)
        if previous is not None:
            self._publish_local_change(registered, key, "removed", None, previous)

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
        registered = self._resolve_document_alias(document)
        schema_id = registered.catalog_entry().schema_id
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
        documents_by_schema_id = schema_document_map(self.documents)
        try:
            document, resolved_schema_id = resolve_document_and_schema_id_for_raw_record(
                documents_by_schema_id,
                schema_id,
                document_record,
            )
        except KeyError:
            return None
        record_key = str(document_record["recordKey"])
        previous = self.cache.get(document, record_key)
        envelope = CultCacheEnvelope(
            key=record_key,
            type=document.type,
            schema_id=resolved_schema_id,
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

    def shard_epoch(self, shard_id: str) -> int:
        return self._latest_shard_epoch(shard_id) or 0

    def shard_ids(self) -> list[str]:
        if self._shard_log_store is not None:
            return sorted(set(self._shard_logs) | set(self._shard_log_store.shard_ids()))
        return sorted(self._shard_logs)

    def shard_schema_ids(self, shard_id: str) -> list[str]:
        schema_ids: set[str] = set()
        for entry in self._shard_log_entries(shard_id):
            put = entry.get("put")
            if isinstance(put, dict) and isinstance(put.get("document"), dict):
                schema_id = put["document"].get("schemaId")
                if schema_id:
                    schema_ids.add(str(schema_id))
            delete = entry.get("delete")
            if isinstance(delete, dict):
                schema_id = delete.get("schemaId")
                if schema_id:
                    schema_ids.add(str(schema_id))
        return sorted(schema_ids)

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
        return self.build_snapshot_response(
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            shard_log_sequence=shard_log_sequence,
        ).to_wire()

    def build_snapshot_response(
        self,
        *,
        message_id: str = "",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
        shard_log_sequence: int | None = None,
    ) -> CultNetRawSnapshotResponse:
        requested_schema_ids = set(schema_ids or [])
        requested_record_keys = set(record_keys or [])
        documents_by_type = {document.type: document for document in self.documents}
        documents_by_schema_id = schema_document_map(self.documents)
        shard_record_keys = (
            self._live_shard_record_keys(shard_id)
            if shard_id is not None and shard_id in self._shard_logs
            else None
        )
        documents: list[CultNetRawDocumentRecord] = []
        for envelope in self.cache.snapshot_envelopes():
            schema_id = envelope.schema_id or envelope.type
            document = documents_by_type.get(envelope.type) or documents_by_schema_id.get(schema_id)
            if requested_schema_ids and not self._schema_matches_request(schema_id, document, requested_schema_ids):
                continue
            if requested_record_keys and envelope.key not in requested_record_keys:
                continue
            if shard_record_keys is not None and (schema_id, envelope.key) not in shard_record_keys:
                continue
            documents.append(CultNetRawDocumentRecord(
                schema_id=schema_id,
                record_key=envelope.key,
                stored_at=envelope.stored_at,
                payload_encoding="messagepack",
                payload=envelope.payload,
            ))

        return CultNetRawSnapshotResponse(
            message_id=message_id,
            documents=tuple(documents),
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            shard_log_sequence=shard_log_sequence,
        )

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

    def sync_document(
        self,
        client: CultNetRawClient,
        document: DocumentDefinition[Any],
        key: str,
        *,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> Any:
        if not key:
            raise ValueError("key must be non-empty")
        requested = document
        registered = self._resolve_document_alias(document)
        schema_id = registered.catalog_entry().schema_id
        self.sync_snapshot(
            client,
            schema_ids=[schema_id],
            record_keys=[key],
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )
        return self.get_required(requested, key)

    def reactive_document(
        self,
        document: DocumentDefinition[Any],
        key: str,
        options: CultMeshReactiveDocumentOptions | None = None,
    ) -> "CultMeshReactiveDocument":
        if not key:
            raise ValueError("key must be non-empty")
        if not any(self._documents_alias(document, registered) for registered in self.documents):
            self.register_document(document)
        return CultMeshReactiveDocument(self, document, key, options)

    def sync_document_from_publication(
        self,
        source: CultMeshDocumentPublicationSource,
        document: DocumentDefinition[Any],
        key: str,
    ) -> Any:
        if not key:
            raise ValueError("key must be non-empty")
        if source.kind == "peer_snapshot":
            if source.client is None:
                raise ValueError("peer snapshot publication sources require a client")
            return self.sync_document(
                source.client,
                document,
                key,
                shard_id=source.shard_id,
                shard_epoch=source.shard_epoch,
            )
        if source.kind == "single_file":
            if source.path is None:
                raise ValueError("single-file publication sources require a path")
            registered = self._resolve_document_alias(document)
            cache = CultCache()
            cache.register_document_type(registered)
            cache.add_generic_store(SingleFileMessagePackBackingStore(source.path))
            cache.pull_all_backing_stores()
            value = cache.get_required(registered, key)
            requested = self._convert_document_value(value, registered, document)
            self.put(document, key, requested)
            return self.get_required(document, key)
        raise ValueError(f"Unsupported CultMesh document publication source: {source.kind!r}")

    def sync_documents_from_publication(
        self,
        source: CultMeshDocumentPublicationSource,
        bindings: list[CultMeshPublicationDocumentBinding] | tuple[CultMeshPublicationDocumentBinding, ...],
    ) -> list[Any]:
        if source is None:
            raise ValueError("publication source is required")
        if bindings is None:
            raise ValueError("publication bindings are required")
        return [
            self.sync_document_from_publication(
                binding.source or source,
                binding.document,
                binding.key,
            )
            for binding in bindings
        ]

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
        return self.build_shard_log_response(
            shard_id=shard_id,
            message_id=message_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        ).to_wire()

    def build_shard_log_response(
        self,
        *,
        shard_id: str,
        message_id: str = "",
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> CultNetShardLogResponse:
        if not shard_id:
            raise ValueError("shard_id must be non-empty")
        if after_sequence < 0:
            raise ValueError("after_sequence must be non-negative")
        latest_epoch = self._latest_shard_epoch(shard_id)
        if latest_epoch is None:
            return CultNetShardLogResponse(
                message_id=message_id,
                shard_id=shard_id,
                shard_epoch=0,
                entries=(),
                resync_required=True,
                reason="unknown_shard",
            )
        if shard_epoch is not None and shard_epoch != latest_epoch:
            return CultNetShardLogResponse(
                message_id=message_id,
                shard_id=shard_id,
                shard_epoch=latest_epoch,
                entries=(),
                resync_required=True,
                reason="stale_epoch",
            )
        compacted_through = self._shard_log_compacted_through(shard_id)
        if after_sequence < compacted_through:
            return CultNetShardLogResponse(
                message_id=message_id,
                shard_id=shard_id,
                shard_epoch=latest_epoch,
                entries=(),
                resync_required=True,
                reason="compacted_history",
                compacted_through=compacted_through,
            )
        entries = [
            CultNetShardLogEntry.from_wire(entry)
            for entry in self._shard_log_entries(shard_id, after_sequence=after_sequence, limit=limit)
            if int(entry.get("sequence") or 0) > after_sequence
        ]
        return CultNetShardLogResponse(
            message_id=message_id,
            shard_id=shard_id,
            shard_epoch=latest_epoch,
            entries=tuple(entries),
        )

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
            if document is not None and not self._documents_alias(document, change.document):
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
        latest_sequence = max(
            [int(item.get("sequence") or 0) for item in log]
            + [int(item.get("sequence") or 0) for item in self._stored_shard_log_entries(shard_id)]
            + [self._shard_log_compacted_through(shard_id)]
        )
        entry["sequence"] = latest_sequence + 1
        entry["committedAt"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
        if self._shard_log_store is not None:
            self._shard_log_store.append(shard_id, entry)
        else:
            log.append(entry)

    def _latest_shard_epoch(self, shard_id: str) -> int | None:
        log = self._shard_log_entries(shard_id)
        for entry in reversed(log):
            put = entry.get("put")
            if isinstance(put, dict) and isinstance(put.get("shardEpoch"), int):
                return put["shardEpoch"]
            delete = entry.get("delete")
            if isinstance(delete, dict) and isinstance(delete.get("shardEpoch"), int):
                return delete["shardEpoch"]
        return None

    def _live_shard_record_keys(self, shard_id: str) -> set[tuple[str, str]]:
        records: set[tuple[str, str]] = set()
        for entry in self._shard_log_entries(shard_id):
            put = entry.get("put")
            if isinstance(put, dict) and isinstance(put.get("document"), dict):
                document = put["document"]
                schema_id = document.get("schemaId")
                record_key = document.get("recordKey")
                if schema_id and record_key:
                    records.add(self._canonical_shard_record_key(str(schema_id), str(record_key), document))
            delete = entry.get("delete")
            if isinstance(delete, dict):
                schema_id = delete.get("schemaId")
                record_key = delete.get("recordKey")
                if schema_id and record_key:
                    records.discard(self._canonical_shard_record_key(str(schema_id), str(record_key)))
        return records

    def _canonical_shard_record_key(
        self,
        schema_id: str,
        record_key: str,
        raw_record: dict[str, Any] | None = None,
    ) -> tuple[str, str]:
        documents_by_schema_id = schema_document_map(self.documents)
        if raw_record is not None:
            try:
                _, resolved_schema_id = resolve_document_and_schema_id_for_raw_record(
                    documents_by_schema_id,
                    schema_id,
                    raw_record,
                )
                return resolved_schema_id, record_key
            except KeyError:
                pass
        document = documents_by_schema_id.get(schema_id)
        if document is not None:
            return document.catalog_entry().schema_id, record_key
        return schema_id, record_key

    def _shard_log_entries(
        self,
        shard_id: str,
        *,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> list[dict[str, Any]]:
        if self._shard_log_store is not None:
            return self._shard_log_store.read(shard_id, after_sequence=after_sequence, limit=limit)
        entries = [
            entry
            for entry in self._shard_logs.get(shard_id, [])
            if int(entry.get("sequence") or 0) > after_sequence
        ]
        if limit is not None:
            if limit < 0:
                raise ValueError("limit must be non-negative")
            entries = entries[:limit]
        return entries

    def _stored_shard_log_entries(self, shard_id: str) -> list[dict[str, Any]]:
        if self._shard_log_store is None:
            return []
        return self._shard_log_store.read(shard_id, after_sequence=0)

    def _shard_log_compacted_through(self, shard_id: str) -> int:
        if self._shard_log_store is None:
            return 0
        return self._shard_log_store.get_compacted_through(shard_id)

    def _document_for_schema(self, schema_id: str) -> DocumentDefinition[Any] | None:
        for document in self.documents:
            if _document_matches_schema_id(document, schema_id):
                return document
        return None

    def _resolve_document_alias(self, document: DocumentDefinition[Any]) -> DocumentDefinition[Any]:
        for registered in self.documents:
            if self._documents_alias(document, registered):
                return registered
        return document

    def _convert_document_value(
        self,
        value: Any,
        source: DocumentDefinition[Any],
        target: DocumentDefinition[Any],
    ) -> Any:
        return target.decode_payload(source.encode_payload(value))

    def _change_as_document(
        self,
        change: CultMeshDatabaseChange,
        target: DocumentDefinition[Any],
    ) -> CultMeshDatabaseChange:
        if change.document.type == target.type:
            return change
        if not self._documents_alias(change.document, target):
            return change
        return CultMeshDatabaseChange(
            document=target,
            schema_id=target.catalog_entry().schema_id,
            record_key=change.record_key,
            change_kind=change.change_kind,
            value=None if change.value is None else self._convert_document_value(
                change.value,
                change.document,
                target,
            ),
            previous_value=None if change.previous_value is None else self._convert_document_value(
                change.previous_value,
                change.document,
                target,
            ),
        )

    def _documents_alias(
        self,
        left: DocumentDefinition[Any],
        right: DocumentDefinition[Any],
    ) -> bool:
        if left.type == right.type:
            return True
        left_entry = left.catalog_entry()
        right_entry = right.catalog_entry()
        if left_entry.schema_id == right_entry.schema_id:
            return True
        if left_entry.schema_name == right_entry.schema_name and left_entry.schema_version == right_entry.schema_version:
            return True
        if left_entry.schema_id in right_entry.compatible_schema_ids:
            return True
        return right_entry.schema_id in left_entry.compatible_schema_ids

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
            resolved_schema_id = document.catalog_entry().schema_id
            previous[(resolved_schema_id, record_key)] = self.cache.get(document, record_key)
        return previous

    def _previous_values_for_raw_records(self, records: Any) -> dict[tuple[str, str], Any]:
        previous: dict[tuple[str, str], Any] = {}
        documents_by_schema_id = schema_document_map(self.documents)
        for record in records:
            if not isinstance(record, dict):
                continue
            schema_id = str(record.get("schemaId"))
            try:
                document, resolved_schema_id = resolve_document_and_schema_id_for_raw_record(
                    documents_by_schema_id,
                    schema_id,
                    record,
                )
            except KeyError:
                continue
            record_key = str(record.get("recordKey"))
            previous[(resolved_schema_id, record_key)] = self.cache.get(document, record_key)
        return previous

    @staticmethod
    def _schema_matches_request(
        schema_id: str,
        document: DocumentDefinition[Any] | None,
        requested_schema_ids: set[str],
    ) -> bool:
        if not requested_schema_ids or schema_id in requested_schema_ids:
            return True
        if document is None:
            return False
        entry = document.catalog_entry()
        if entry.schema_id in requested_schema_ids or entry.schema_name in requested_schema_ids:
            return True
        if any(schema_id in requested_schema_ids for schema_id in entry.compatible_schema_ids):
            return True
        return any(_infer_schema_name(schema_id) == entry.schema_name for schema_id in requested_schema_ids)


def _infer_schema_name(schema_id: str) -> str | None:
    marker = schema_id.rfind(".v")
    if marker <= 0 or marker + 2 >= len(schema_id):
        return None
    version = schema_id[marker + 2:]
    return schema_id[:marker] if version.isdigit() else None


def _document_matches_schema_id(document: DocumentDefinition[Any], schema_id: str) -> bool:
    entry = document.catalog_entry()
    if schema_id in (entry.schema_id, entry.schema_name, entry.schema_version):
        return True
    if schema_id in entry.compatible_schema_ids:
        return True
    return _infer_schema_name(schema_id) == entry.schema_name


class CultMeshReactiveDocument:
    def __init__(
        self,
        database: CultMeshDatabase,
        document: DocumentDefinition[Any],
        key: str,
        options: CultMeshReactiveDocumentOptions | None = None,
    ) -> None:
        self._database = database
        self._document = document
        self._key = key
        self._options = options or CultMeshReactiveDocumentOptions()
        self._lock = threading.RLock()
        self._disposed = False
        self._dirty = False
        self._flushing = False
        self._flush_queued = False
        self._flush_timer: threading.Timer | None = None
        self._detect_timer: threading.Timer | None = None
        self._reconciliation_version = 0
        self.reconciliation: CultMeshReactiveDocumentReconciliation | None = None
        self.current = self._clone(self._database.get_required(document, key))
        self._last_clean_payload = self._serialize(self.current)
        self._unsubscribe = self._database.watch_record(document, key, self._apply_canonical_change)
        if self._options.detect_local_changes:
            self._schedule_detection()

    @property
    def document(self) -> DocumentDefinition[Any]:
        return self._document

    @property
    def key(self) -> str:
        return self._key

    @property
    def is_dirty(self) -> bool:
        with self._lock:
            return self._dirty

    def update(self, callback: Callable[[Any], None]) -> Any:
        with self._lock:
            self._raise_if_disposed()
            callback(self.current)
            self._dirty = True
            self._schedule_flush_locked()
            return self.current

    def set_current(self, value: Any) -> Any:
        with self._lock:
            self._raise_if_disposed()
            self.current = self._clone(value)
            self._dirty = True
            self._schedule_flush_locked()
            return self.current

    def mark_dirty(self) -> None:
        with self._lock:
            self._raise_if_disposed()
            self._dirty = True
            self._schedule_flush_locked()

    def refresh(self) -> Any:
        with self._lock:
            self._raise_if_disposed()
            self.current = self._clone(self._database.get_required(self._document, self._key))
            self._last_clean_payload = self._serialize(self.current)
            self._dirty = False
            self.reconciliation = None
            return self.current

    def flush(self) -> None:
        with self._lock:
            self._raise_if_disposed()
            if not self._dirty:
                self._detect_local_changes_locked()
            if not self._dirty:
                return
            if self._flushing:
                self._flush_queued = True
                return
            if self._flush_timer is not None:
                self._flush_timer.cancel()
                self._flush_timer = None
            self._flushing = True
            self._dirty = False
            predicted = self._clone(self.current)
            self._last_clean_payload = self._serialize(predicted)

        try:
            self._database.put(self._document, self._key, predicted)
        finally:
            should_flush_again = False
            with self._lock:
                self._flushing = False
                self._detect_local_changes_locked()
                should_flush_again = self._flush_queued or self._dirty
                self._flush_queued = False
            if should_flush_again:
                self.flush()

    def clear_reconciliation(self) -> None:
        with self._lock:
            self._raise_if_disposed()
            self.reconciliation = None

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
            if self._flush_timer is not None:
                self._flush_timer.cancel()
            if self._detect_timer is not None:
                self._detect_timer.cancel()
        self._unsubscribe()

    def __enter__(self) -> "CultMeshReactiveDocument":
        return self

    def __exit__(self, _exc_type: Any, _exc: Any, _tb: Any) -> None:
        self.dispose()

    def _apply_canonical_change(self, change: CultMeshDatabaseChange) -> None:
        if change.value is None:
            return
        with self._lock:
            if self._disposed:
                return
            canonical = self._clone(change.value)
            if self._dirty or self._flushing:
                predicted = self._clone(self.current)
                delta = self._create_delta(predicted, canonical)
                if delta:
                    self._reconciliation_version += 1
                    self.reconciliation = CultMeshReactiveDocumentReconciliation(
                        canonical=canonical,
                        predicted=predicted,
                        delta=delta,
                        version=self._reconciliation_version,
                        received_at=datetime.now(UTC),
                    )
                else:
                    self.reconciliation = None
                if not self._options.replace_dirty_current_on_canonical_snapshot:
                    return
            self.current = canonical
            self._last_clean_payload = self._serialize(self.current)
            self.reconciliation = None

    def _schedule_detection(self) -> None:
        delay = max(self._options.flush_delay_seconds, 0.001)
        timer = threading.Timer(delay, self._detect_timer_elapsed)
        timer.daemon = True
        self._detect_timer = timer
        timer.start()

    def _detect_timer_elapsed(self) -> None:
        with self._lock:
            if self._disposed:
                return
            if not self._dirty and not self._flushing:
                self._detect_local_changes_locked()
                if self._dirty:
                    self._schedule_flush_locked()
            if not self._disposed and self._options.detect_local_changes:
                self._schedule_detection()

    def _detect_local_changes_locked(self) -> None:
        if self._serialize(self.current) != self._last_clean_payload:
            self._dirty = True

    def _schedule_flush_locked(self) -> None:
        if self._options.flush_delay_seconds <= 0:
            threading.Thread(target=self.flush, daemon=True).start()
            return
        if self._flush_timer is not None:
            self._flush_timer.cancel()
        timer = threading.Timer(self._options.flush_delay_seconds, self.flush)
        timer.daemon = True
        self._flush_timer = timer
        timer.start()

    def _serialize(self, value: Any) -> bytes:
        return self._document.encode_payload(value)

    def _clone(self, value: Any) -> Any:
        return self._document.decode_payload(self._document.encode_payload(value))

    @staticmethod
    def _create_delta(predicted: Any, canonical: Any) -> dict[str, Any]:
        delta: dict[str, Any] = {}
        canonical_members = _public_document_members(canonical)
        for name, predicted_value in _public_document_members(predicted).items():
            canonical_value = canonical_members.get(name)
            if predicted_value == canonical_value:
                continue
            if isinstance(predicted_value, (int, float)) and isinstance(canonical_value, (int, float)):
                delta[name] = predicted_value - canonical_value
            else:
                delta[name] = predicted_value
        return delta

    def _raise_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("CultMeshReactiveDocument has been disposed")


def _public_document_members(value: Any) -> dict[str, Any]:
    if hasattr(value, "__dataclass_fields__"):
        return {
            name: getattr(value, name)
            for name in value.__dataclass_fields__  # type: ignore[attr-defined]
            if not name.startswith("_")
        }
    if isinstance(value, dict):
        return {str(key): item for key, item in value.items()}
    return {
        name: item
        for name, item in vars(value).items()
        if not name.startswith("_")
    }


def create_node(
    cache_path: str | Path | None = None,
    *,
    runtime_id: str = "python-runtime",
    options: CultMeshNodeOptions | None = None,
    enable_durable_shard_logs: bool | None = None,
    shard_log_path: str | Path | None = None,
) -> CultMeshNode:
    resolved_options = _resolve_node_options(
        options,
        enable_durable_shard_logs=enable_durable_shard_logs,
        shard_log_path=shard_log_path,
    )
    cache = CultCache()
    if cache_path is not None:
        cache.add_generic_store(SingleFileMessagePackBackingStore(cache_path))
    node = CultMeshNode(cache=cache, runtime_id=runtime_id)
    if resolved_options.enable_durable_shard_logs:
        node.database.use_shard_mutation_log_store(CultNetFileShardMutationLogStore(
            _resolve_shard_log_path(cache_path, resolved_options.shard_log_path)
        ))
    return node


def _resolve_node_options(
    options: CultMeshNodeOptions | None,
    *,
    enable_durable_shard_logs: bool | None,
    shard_log_path: str | Path | None,
) -> CultMeshNodeOptions:
    if options is None:
        return CultMeshNodeOptions(
            enable_durable_shard_logs=enable_durable_shard_logs is True,
            shard_log_path=shard_log_path,
        )
    return CultMeshNodeOptions(
        enable_durable_shard_logs=options.enable_durable_shard_logs
        if enable_durable_shard_logs is None
        else enable_durable_shard_logs,
        shard_log_path=shard_log_path if shard_log_path is not None else options.shard_log_path,
    )


def _resolve_shard_log_path(cache_path: str | Path | None, shard_log_path: str | Path | None) -> Path:
    if shard_log_path is not None:
        return Path(shard_log_path)
    if cache_path is None:
        raise ValueError("cache_path or shard_log_path is required when durable shard logs are enabled")
    path = Path(cache_path)
    directory = path.parent if path.parent != Path("") else Path.cwd()
    return directory / f"{path.stem}.cultmesh" / "shard-logs"
