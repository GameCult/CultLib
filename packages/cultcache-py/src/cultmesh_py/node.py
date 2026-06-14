from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from cultcache_py import CultCache, CultCacheEnvelope, SingleFileMessagePackBackingStore
from cultcache_py.documents import DocumentDefinition
from cultnet_py import (
    CultNetAppliedRecord,
    CultNetMessage,
    CultNetRawClient,
    apply_raw_snapshot,
    apply_shard_log_response,
    document_delete,
    document_put_raw,
)


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

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self.database.delete(document, key)

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


class CultMeshDatabase:
    def __init__(self, node: CultMeshNode) -> None:
        self._node = node

    @property
    def cache(self) -> CultCache:
        return self._node.cache

    @property
    def documents(self) -> list[DocumentDefinition[Any]]:
        return self._node.documents

    @property
    def runtime_id(self) -> str:
        return self._node.runtime_id

    def register_document(self, document: DocumentDefinition[Any]) -> None:
        self.cache.register_document_type(document)
        self.documents.append(document)

    def put(self, document: DocumentDefinition[Any], key: str, value: Any) -> None:
        self.cache.put(document, key, value)

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
        envelope = CultCacheEnvelope.create(
            key=key,
            type=document.type,
            payload=document.encode_payload(value),
            schema_id=catalog_entry.schema_id,
            catalog_entry=catalog_entry,
        )
        self.cache.put_envelope(document, envelope)
        return document_put_raw(
            message_id=message_id,
            key=key,
            schema_id=envelope.schema_id or catalog_entry.schema_id,
            stored_at=envelope.stored_at,
            payload=envelope.payload,
            source_runtime_id=self.runtime_id,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    def get(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get(document, key)

    def get_required(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get_required(document, key)

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self.cache.delete(document, key)

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
        return document_delete(
            message_id=message_id,
            schema_id=schema_id,
            record_key=key,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )

    def snapshot(self) -> dict[str, dict[str, Any]]:
        return self.cache.snapshot()

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
        response = client.fetch_snapshot(
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        )
        return apply_raw_snapshot(self.cache, self.documents, response)

    def sync_shard_log(
        self,
        client: CultNetRawClient,
        *,
        shard_id: str,
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> list[CultNetAppliedRecord]:
        response = client.fetch_shard_log(
            shard_id=shard_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        )
        return apply_shard_log_response(self.cache, self.documents, response)


def create_node(cache_path: str | Path | None = None, *, runtime_id: str = "python-runtime") -> CultMeshNode:
    cache = CultCache()
    if cache_path is not None:
        cache.add_generic_store(SingleFileMessagePackBackingStore(cache_path))
    return CultMeshNode(cache=cache, runtime_id=runtime_id)
