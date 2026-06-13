from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from cultcache_py import CultCache, SingleFileMessagePackBackingStore
from cultcache_py.documents import DocumentDefinition
from cultnet_py import CultNetAppliedRecord, CultNetRawClient, apply_raw_snapshot, apply_shard_log_response


@dataclass
class CultMeshNode:
    cache: CultCache = field(default_factory=CultCache)
    runtime_id: str = "python-runtime"
    documents: list[DocumentDefinition[Any]] = field(default_factory=list)

    def register_document(self, document: DocumentDefinition[Any]) -> None:
        self.cache.register_document_type(document)
        self.documents.append(document)

    def put(self, document: DocumentDefinition[Any], key: str, value: Any) -> None:
        self.cache.put(document, key, value)

    def get(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get(document, key)

    def get_required(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get_required(document, key)

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self.cache.delete(document, key)

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
