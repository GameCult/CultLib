from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Generic, TypeVar

from .backing_store import BackingStore, CultCacheEnvelope
from .documents import DocumentDefinition, extract_value

T = TypeVar("T")
GLOBAL_KEY = "__global__"


class CultCacheError(RuntimeError):
    pass


@dataclass
class _State:
    documents: dict[str, DocumentDefinition[Any]] = field(default_factory=dict)
    values: dict[tuple[str, str], Any] = field(default_factory=dict)
    envelopes: dict[tuple[str, str], CultCacheEnvelope] = field(default_factory=dict)
    stores_by_type: dict[str, list[BackingStore]] = field(default_factory=dict)
    generic_stores: list[BackingStore] = field(default_factory=list)
    name_extractors: dict[str, str | Any] = field(default_factory=dict)
    index_extractors: dict[str, dict[str, str | Any]] = field(default_factory=dict)
    names: dict[tuple[str, str], str] = field(default_factory=dict)
    indexes: dict[tuple[str, str, str], str] = field(default_factory=dict)


class CultCacheBuilder:
    def __init__(self) -> None:
        self._cache = CultCache()

    def register_document_type(self, document: DocumentDefinition[Any]) -> "CultCacheBuilder":
        self._cache.register_document_type(document)
        return self

    def register_registry(self, documents: list[DocumentDefinition[Any]] | tuple[DocumentDefinition[Any], ...]) -> "CultCacheBuilder":
        self._cache.register_registry(documents)
        return self

    def register_name_lookup(self, document: DocumentDefinition[Any], extractor: str | Any) -> "CultCacheBuilder":
        self._cache.register_name_lookup(document, extractor)
        return self

    def register_index(self, document: DocumentDefinition[Any], index: str, extractor: str | Any) -> "CultCacheBuilder":
        self._cache.register_index(document, index, extractor)
        return self

    def add_backing_store(self, store: BackingStore, types: list[str] | tuple[str, ...] | set[str]) -> "CultCacheBuilder":
        self._cache.add_backing_store(store, types)
        return self

    def add_generic_store(self, store: BackingStore) -> "CultCacheBuilder":
        self._cache.add_generic_store(store)
        return self

    def build(self) -> "CultCache":
        return self._cache


class CultCache:
    GLOBAL_KEY = GLOBAL_KEY

    @classmethod
    def builder(cls) -> CultCacheBuilder:
        return CultCacheBuilder()

    def __init__(self) -> None:
        self._state = _State()

    def register_document_type(self, document: DocumentDefinition[Any]) -> None:
        if document.type in self._state.documents:
            raise CultCacheError(f"Document type already registered: {document.type}")
        self._state.documents[document.type] = document
        if document.name is not None:
            self.register_name_lookup(document, document.name)
        for index, extractor in document.indexes.items():
            self.register_index(document, index, extractor)

    def register_registry(self, documents: list[DocumentDefinition[Any]] | tuple[DocumentDefinition[Any], ...]) -> None:
        for document in documents:
            self.register_document_type(document)

    def register_name_lookup(self, document: DocumentDefinition[Any], extractor: str | Any) -> None:
        self._assert_registered(document)
        self._state.name_extractors[document.type] = extractor
        self._rebuild_indexes()

    def register_index(self, document: DocumentDefinition[Any], index: str, extractor: str | Any) -> None:
        self._assert_registered(document)
        self._state.index_extractors.setdefault(document.type, {})[index] = extractor
        self._rebuild_indexes()

    def add_backing_store(self, store: BackingStore, types: list[str] | tuple[str, ...] | set[str]) -> None:
        for type in types:
            self._state.stores_by_type.setdefault(type, []).append(store)

    def add_generic_store(self, store: BackingStore) -> None:
        self._state.generic_stores.append(store)

    def pull_all_backing_stores(self) -> None:
        self._state.values.clear()
        self._state.envelopes.clear()
        seen_globals: set[str] = set()
        for store in [*self._all_specific_stores(), *self._state.generic_stores]:
            for envelope in store.pull_all():
                document = self._state.documents.get(envelope.type)
                if document is None:
                    raise CultCacheError(f"Unknown persisted document type: {envelope.type}")
                if document.global_document:
                    if envelope.type in seen_globals and envelope.key == GLOBAL_KEY:
                        raise CultCacheError(f"Duplicate global document for type: {envelope.type}")
                    seen_globals.add(envelope.type)
                value = document.decode_payload(envelope.payload)
                self._state.values[(envelope.type, envelope.key)] = value
                self._state.envelopes[(envelope.type, envelope.key)] = envelope
        self._rebuild_indexes()

    def get(self, document: DocumentDefinition[T], key: str) -> T | None:
        self._assert_registered(document)
        return self._state.values.get((document.type, key))

    def get_required(self, document: DocumentDefinition[T], key: str) -> T:
        value = self.get(document, key)
        if value is None:
            raise CultCacheError(f"Missing {document.type}:{key}")
        return value

    def get_all(self, document: DocumentDefinition[T]) -> list[T]:
        self._assert_registered(document)
        return [value for (type, _), value in self._state.values.items() if type == document.type]

    def get_envelope(self, document: DocumentDefinition[Any], key: str) -> CultCacheEnvelope | None:
        self._assert_registered(document)
        return self._state.envelopes.get((document.type, key))

    def get_required_envelope(self, document: DocumentDefinition[Any], key: str) -> CultCacheEnvelope:
        envelope = self.get_envelope(document, key)
        if envelope is None:
            raise CultCacheError(f"Missing envelope {document.type}:{key}")
        return envelope

    def get_global(self, document: DocumentDefinition[T]) -> T | None:
        self._assert_global(document)
        return self.get(document, GLOBAL_KEY)

    def get_required_global(self, document: DocumentDefinition[T]) -> T:
        self._assert_global(document)
        return self.get_required(document, GLOBAL_KEY)

    def get_key_by_name(self, document: DocumentDefinition[Any], name: str) -> str | None:
        self._assert_registered(document)
        return self._state.names.get((document.type, name))

    def get_by_name(self, document: DocumentDefinition[T], name: str) -> T | None:
        key = self.get_key_by_name(document, name)
        return None if key is None else self.get(document, key)

    def get_key_by_index(self, document: DocumentDefinition[Any], index: str, value: str) -> str | None:
        self._assert_registered(document)
        return self._state.indexes.get((document.type, index, value))

    def get_by_index(self, document: DocumentDefinition[T], index: str, value: str) -> T | None:
        key = self.get_key_by_index(document, index, value)
        return None if key is None else self.get(document, key)

    def put(self, document: DocumentDefinition[T], key: str, value: T) -> None:
        self._assert_registered(document)
        if document.global_document and key != GLOBAL_KEY:
            raise CultCacheError(f"Global document {document.type} must use key {GLOBAL_KEY}")
        catalog_entry = document.catalog_entry()
        envelope = CultCacheEnvelope.create(
            key=key,
            type=document.type,
            payload=document.encode_payload(value),
            schema_id=catalog_entry.schema_id,
            catalog_entry=catalog_entry,
        )
        stores = self._stores_for_type(document.type)
        if not stores:
            raise CultCacheError(f"No backing store registered for {document.type}")
        for store in stores:
            store.push(envelope)
        self._state.values[(document.type, key)] = value
        self._state.envelopes[(document.type, key)] = envelope
        self._rebuild_indexes()

    def put_envelope(self, document: DocumentDefinition[T], envelope: CultCacheEnvelope) -> T:
        self._assert_registered(document)
        if envelope.type != document.type:
            raise CultCacheError(
                f"Envelope type {envelope.type} does not match document type {document.type}"
            )
        value = document.decode_payload(envelope.payload)
        for store in self._stores_for_type(document.type):
            store.push(envelope)
        self._state.values[(document.type, envelope.key)] = value
        self._state.envelopes[(document.type, envelope.key)] = envelope
        self._rebuild_indexes()
        return value

    def put_global(self, document: DocumentDefinition[T], value: T) -> None:
        self._assert_global(document)
        self.put(document, GLOBAL_KEY, value)

    def update(self, document: DocumentDefinition[T], key: str, updater: Any) -> T:
        current = self.get_required(document, key)
        updated = updater(current)
        self.put(document, key, updated)
        return updated

    def update_global(self, document: DocumentDefinition[T], updater: Any) -> T:
        self._assert_global(document)
        return self.update(document, GLOBAL_KEY, updater)

    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self._assert_registered(document)
        for store in self._stores_for_type(document.type):
            store.delete(document.type, key)
        self._state.values.pop((document.type, key), None)
        self._state.envelopes.pop((document.type, key), None)
        self._rebuild_indexes()

    def delete_global(self, document: DocumentDefinition[Any]) -> None:
        self._assert_global(document)
        self.delete(document, GLOBAL_KEY)

    def snapshot(self) -> dict[str, dict[str, Any]]:
        out: dict[str, dict[str, Any]] = {}
        for (type, key), value in self._state.values.items():
            out.setdefault(type, {})[key] = value
        return out

    def _stores_for_type(self, type: str) -> list[BackingStore]:
        specific = self._state.stores_by_type.get(type)
        return specific if specific else self._state.generic_stores

    def _all_specific_stores(self) -> list[BackingStore]:
        stores: list[BackingStore] = []
        seen: set[int] = set()
        for routed in self._state.stores_by_type.values():
            for store in routed:
                marker = id(store)
                if marker not in seen:
                    stores.append(store)
                    seen.add(marker)
        return stores

    def _rebuild_indexes(self) -> None:
        self._state.names.clear()
        self._state.indexes.clear()
        for (type, key), value in self._state.values.items():
            name_extractor = self._state.name_extractors.get(type)
            if name_extractor is not None:
                name = extract_value(value, name_extractor)
                if name is not None:
                    self._state.names[(type, str(name))] = key
            for index, extractor in self._state.index_extractors.get(type, {}).items():
                index_value = extract_value(value, extractor)
                if index_value is not None:
                    self._state.indexes[(type, index, str(index_value))] = key

    def _assert_registered(self, document: DocumentDefinition[Any]) -> None:
        if self._state.documents.get(document.type) is not document:
            raise CultCacheError(f"Document type is not registered on this cache: {document.type}")

    def _assert_global(self, document: DocumentDefinition[Any]) -> None:
        self._assert_registered(document)
        if not document.global_document:
            raise CultCacheError(f"Document type is not global: {document.type}")
