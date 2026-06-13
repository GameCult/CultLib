from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from cultcache_py import CultCache, SingleFileMessagePackBackingStore
from cultcache_py.documents import DocumentDefinition


@dataclass
class CultMeshNode:
    cache: CultCache = field(default_factory=CultCache)
    runtime_id: str = "python-runtime"

    def register_document(self, document: DocumentDefinition[Any]) -> None:
        self.cache.register_document_type(document)

    def put(self, document: DocumentDefinition[Any], key: str, value: Any) -> None:
        self.cache.put(document, key, value)

    def get(self, document: DocumentDefinition[Any], key: str) -> Any:
        return self.cache.get(document, key)

    def pull(self) -> None:
        self.cache.pull_all_backing_stores()


def create_node(cache_path: str | Path | None = None, *, runtime_id: str = "python-runtime") -> CultMeshNode:
    cache = CultCache()
    if cache_path is not None:
        cache.add_generic_store(SingleFileMessagePackBackingStore(cache_path))
    return CultMeshNode(cache=cache, runtime_id=runtime_id)
