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
    documents_by_schema_name: dict[str, DocumentDefinition[Any]] = field(default_factory=dict)
    values: dict[str, dict[str, Any]] = field(default_factory=dict)
    envelopes: dict[str, dict[str, CultCacheEnvelope]] = field(default_factory=dict)
    stores_by_type: dict[str, BackingStore] = field(default_factory=dict)
    generic_store: BackingStore | None = None
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
        schema_name = document.catalog_entry().schema_name
        if schema_name in self._state.documents_by_schema_name:
            raise CultCacheError(f"Document schema name already registered: {schema_name}")
        self._state.documents[document.type] = document
        self._state.documents_by_schema_name[schema_name] = document
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
        """Makes the store home to the given types, or the generic store when there are none.

        A type has exactly one home store, and attaching cannot move the home of a record the cache holds.
        """
        types = list(types)
        if not types:
            self.add_generic_store(store)
            return
        for type in types:
            if type in self._state.stores_by_type:
                raise CultCacheError(
                    f"Document type {type} is already routed to another backing store; it cannot also route to this one"
                )
        routes = dict(self._state.stores_by_type)
        routes.update({type: store for type in types})
        self._refuse_home_moves(routes, self._state.generic_store)
        self._state.stores_by_type = routes

    def add_generic_store(self, store: BackingStore) -> None:
        """Makes the store home to every type no other store claims. A cache has at most one."""
        if self._state.generic_store is not None:
            raise CultCacheError(
                "Backing store would be a second generic store; name the types it is home to"
            )
        self._refuse_home_moves(self._state.stores_by_type, store)
        self._state.generic_store = store

    def _refuse_home_moves(self, routes: dict[str, BackingStore], generic: BackingStore | None) -> None:
        for type, values in self._state.values.items():
            if not values:
                continue
            before = self._state.stores_by_type.get(type, self._state.generic_store)
            after = routes.get(type, generic)
            if before is not after:
                raise CultCacheError(
                    f"Attaching this backing store would move {type} from {self._describe(before, routes, generic)} "
                    f"to {self._describe(after, routes, generic)}; attach routed stores before the generic store"
                )

    @staticmethod
    def _describe(store: BackingStore | None, routes: dict[str, BackingStore], generic: BackingStore | None) -> str:
        if store is None:
            return "memory"
        if store is generic:
            return "the generic store"
        return "the store routed to " + ", ".join(sorted(type for type, routed in routes.items() if routed is store))

    def pull_all_backing_stores(self) -> None:
        """Reads every store into a staged image; the cache's view changes only if all of them load."""
        loaded_values: dict[str, dict[str, Any]] = {}
        loaded_envelopes: dict[str, dict[str, CultCacheEnvelope]] = {}
        routes, generic = self._state.stores_by_type, self._state.generic_store
        seen_globals: set[str] = set()
        stores: list[BackingStore] = []
        for candidate in [*self._state.stores_by_type.values(), self._state.generic_store]:
            if candidate is not None and all(candidate is not store for store in stores):
                stores.append(candidate)
        for store in stores:
            for envelope in store.pull_all():
                document = self._resolve_document_for_envelope(envelope)
                if document is None:
                    raise CultCacheError(f"Unknown persisted document type: {envelope.type}")
                if envelope.type != document.type:
                    envelope = CultCacheEnvelope(
                        key=envelope.key,
                        type=document.type,
                        payload=envelope.payload,
                        stored_at=envelope.stored_at,
                        schema_id=envelope.schema_id,
                        catalog_entry=envelope.catalog_entry,
                    )
                home = routes.get(envelope.type, generic)
                if home is not store:
                    raise CultCacheError(
                        f"{envelope.type} record {envelope.key} was loaded from {self._describe(store, routes, generic)}, "
                        f"but its home is {self._describe(home, routes, generic)}"
                    )
                if document.global_document:
                    if envelope.type in seen_globals and envelope.key == GLOBAL_KEY:
                        raise CultCacheError(f"Duplicate global document for type: {envelope.type}")
                    seen_globals.add(envelope.type)
                value = document.decode_payload(envelope.payload)
                loaded_values.setdefault(envelope.type, {})[envelope.key] = value
                loaded_envelopes.setdefault(envelope.type, {})[envelope.key] = envelope
        self._state.values = loaded_values
        self._state.envelopes = loaded_envelopes
        self._rebuild_indexes()

    def get(self, document: DocumentDefinition[T], key: str) -> T | None:
        self._assert_registered(document)
        values = self._state.values.get(document.type)
        return None if values is None else values.get(key)

    def get_required(self, document: DocumentDefinition[T], key: str) -> T:
        value = self.get(document, key)
        if value is None:
            raise CultCacheError(f"Missing {document.type}:{key}")
        return value

    def get_all(self, document: DocumentDefinition[T]) -> list[T]:
        self._assert_registered(document)
        return list(self._state.values.get(document.type, {}).values())

    def get_envelope(self, document: DocumentDefinition[Any], key: str) -> CultCacheEnvelope | None:
        self._assert_registered(document)
        envelopes = self._state.envelopes.get(document.type)
        return None if envelopes is None else envelopes.get(key)

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
        values = self._state.values.setdefault(document.type, {})
        envelopes = self._state.envelopes.setdefault(document.type, {})
        old_value = values.get(key)
        catalog_entry = document.catalog_entry()
        envelope = CultCacheEnvelope.create(
            key=key,
            type=document.type,
            payload=document.encode_payload(value),
            schema_id=catalog_entry.schema_id,
            catalog_entry=catalog_entry,
        )
        store = self._store_for_type(document.type)
        if store is not None:
            store.push(envelope)
        if old_value is not None:
            self._remove_value_indexes(document.type, key, old_value)
        values[key] = value
        envelopes[key] = envelope
        self._add_value_indexes(document.type, key, value)

    def put_envelope(self, document: DocumentDefinition[T], envelope: CultCacheEnvelope) -> T:
        self._assert_registered(document)
        if envelope.type != document.type:
            raise CultCacheError(
                f"Envelope type {envelope.type} does not match document type {document.type}"
            )
        values = self._state.values.setdefault(document.type, {})
        envelopes = self._state.envelopes.setdefault(document.type, {})
        old_value = values.get(envelope.key)
        value = document.decode_payload(envelope.payload)
        store = self._store_for_type(document.type)
        if store is not None:
            store.push(envelope)
        if old_value is not None:
            self._remove_value_indexes(document.type, envelope.key, old_value)
        values[envelope.key] = value
        envelopes[envelope.key] = envelope
        self._add_value_indexes(document.type, envelope.key, value)
        return value

    def put_envelopes(self, document: DocumentDefinition[T], envelopes: list[CultCacheEnvelope]) -> list[T]:
        self._assert_registered(document)
        values: list[T] = []
        for envelope in envelopes:
            if envelope.type != document.type:
                raise CultCacheError(
                    f"Envelope type {envelope.type} does not match document type {document.type}"
                )
            if document.global_document and envelope.key != GLOBAL_KEY:
                raise CultCacheError(f"Global document {document.type} must use key {GLOBAL_KEY}")
            values.append(document.decode_payload(envelope.payload))

        store = self._store_for_type(document.type)
        if store is not None:
            store.push_all(envelopes)
        values_by_key = self._state.values.setdefault(document.type, {})
        envelopes_by_key = self._state.envelopes.setdefault(document.type, {})
        for envelope, value in zip(envelopes, values):
            old_value = values_by_key.get(envelope.key)
            if old_value is not None:
                self._remove_value_indexes(document.type, envelope.key, old_value)
            values_by_key[envelope.key] = value
            envelopes_by_key[envelope.key] = envelope
            self._add_value_indexes(document.type, envelope.key, value)
        return values

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
        store = self._store_for_type(document.type)
        if store is not None:
            store.delete(document.type, key)
        values = self._state.values.get(document.type)
        old_value = None if values is None else values.pop(key, None)
        if values == {}:
            self._state.values.pop(document.type, None)
        envelopes = self._state.envelopes.get(document.type)
        if envelopes is not None:
            envelopes.pop(key, None)
            if envelopes == {}:
                self._state.envelopes.pop(document.type, None)
        if old_value is not None:
            self._remove_value_indexes(document.type, key, old_value)

    def delete_global(self, document: DocumentDefinition[Any]) -> None:
        self._assert_global(document)
        self.delete(document, GLOBAL_KEY)

    def snapshot(self) -> dict[str, dict[str, Any]]:
        out: dict[str, dict[str, Any]] = {}
        for type, values in self._state.values.items():
            out[type] = dict(values)
        return out

    def snapshot_envelopes(self) -> list[CultCacheEnvelope]:
        return [
            envelope
            for envelopes in self._state.envelopes.values()
            for envelope in envelopes.values()
        ]

    def _store_for_type(self, type: str) -> BackingStore | None:
        """The store that claims the type, else the generic store.

        Returns None only for a cache with no stores, which is an in-memory cache.
        """
        store = self._state.stores_by_type.get(type, self._state.generic_store)
        if store is None and self._state.stores_by_type:
            raise CultCacheError(f"No backing store is home to document type: {type}")
        return store

    def _rebuild_indexes(self) -> None:
        self._state.names.clear()
        self._state.indexes.clear()
        for type, values in self._state.values.items():
            for key, value in values.items():
                self._add_value_indexes(type, key, value)

    def _add_value_indexes(self, type: str, key: str, value: Any) -> None:
        name_extractor = self._state.name_extractors.get(type)
        if name_extractor is not None:
            name = extract_value(value, name_extractor)
            if name is not None:
                self._state.names[(type, str(name))] = key
        for index, extractor in self._state.index_extractors.get(type, {}).items():
            index_value = extract_value(value, extractor)
            if index_value is not None:
                self._state.indexes[(type, index, str(index_value))] = key

    def _remove_value_indexes(self, type: str, key: str, value: Any) -> None:
        name_extractor = self._state.name_extractors.get(type)
        if name_extractor is not None:
            name = extract_value(value, name_extractor)
            if name is not None:
                self._remove_name_index(type, key, str(name))
        for index, extractor in self._state.index_extractors.get(type, {}).items():
            index_value = extract_value(value, extractor)
            if index_value is not None:
                self._remove_secondary_index(type, key, index, str(index_value))

    def _remove_name_index(self, type: str, key: str, name: str) -> None:
        lookup_key = (type, name)
        if self._state.names.get(lookup_key) != key:
            return
        self._state.names.pop(lookup_key, None)
        name_extractor = self._state.name_extractors.get(type)
        if name_extractor is None:
            return
        for candidate_key, candidate in self._state.values.get(type, {}).items():
            candidate_name = extract_value(candidate, name_extractor)
            if candidate_key != key and candidate_name is not None and str(candidate_name) == name:
                self._state.names[lookup_key] = candidate_key

    def _remove_secondary_index(self, type: str, key: str, index: str, value: str) -> None:
        lookup_key = (type, index, value)
        if self._state.indexes.get(lookup_key) != key:
            return
        self._state.indexes.pop(lookup_key, None)
        extractor = self._state.index_extractors.get(type, {}).get(index)
        if extractor is None:
            return
        for candidate_key, candidate in self._state.values.get(type, {}).items():
            candidate_value = extract_value(candidate, extractor)
            if candidate_key != key and candidate_value is not None and str(candidate_value) == value:
                self._state.indexes[lookup_key] = candidate_key

    def _assert_registered(self, document: DocumentDefinition[Any]) -> None:
        if self._state.documents.get(document.type) is not document:
            raise CultCacheError(f"Document type is not registered on this cache: {document.type}")

    def _assert_global(self, document: DocumentDefinition[Any]) -> None:
        self._assert_registered(document)
        if not document.global_document:
            raise CultCacheError(f"Document type is not global: {document.type}")

    def _resolve_document_for_envelope(self, envelope: CultCacheEnvelope) -> DocumentDefinition[Any] | None:
        document = self._state.documents.get(envelope.type)
        if document is not None:
            return document
        return self._state.documents_by_schema_name.get(envelope.type)
