from __future__ import annotations

import functools
import threading
from dataclasses import dataclass, field, replace
from typing import Any, Callable, Generic, TypeVar

from .backing_store import BackingStore, CultCacheEnvelope
from .documents import DocumentDefinition, extract_value

T = TypeVar("T")
F = TypeVar("F", bound=Callable[..., Any])
GLOBAL_KEY = "__global__"


def _locked(method: F) -> F:
    """Runs a read holding the cache's lock. Reads are reentrant and may run inside a mutation."""

    @functools.wraps(method)
    def wrapper(self: "CultCache", *args: Any, **kwargs: Any) -> Any:
        with self._lock:
            return method(self, *args, **kwargs)

    return wrapper  # type: ignore[return-value]


def _mutating(method: F) -> F:
    """Runs an attach, pull, registration, write or delete holding the cache's lock, so its validation,
    store I/O and apply cannot interleave with another thread's. One started on a thread that is already
    inside a mutation of this cache (from a store call, decoder, encoder, extractor or updater) raises
    before touching anything."""

    @functools.wraps(method)
    def wrapper(self: "CultCache", *args: Any, **kwargs: Any) -> Any:
        if self._mutating_thread == threading.get_ident():
            raise CultCacheError(
                f"CultCache refuses a re-entrant {method.__name__}: a mutation cannot start from inside a store call, "
                "decoder, encoder, extractor or updater of another mutation on the same cache"
            )
        with self._lock:
            self._mutating_thread = threading.get_ident()
            try:
                return method(self, *args, **kwargs)
            finally:
                self._mutating_thread = None

    return wrapper  # type: ignore[return-value]


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
    # The name and index values each held record produced when it was admitted. Removing a
    # record reads these and runs no extractor, so removal has no fallible step.
    lookup_keys: dict[str, dict[str, "_LookupKeys"]] = field(default_factory=dict)
    # Load compatibility shim: a global persisted under a key other than GLOBAL_KEY is held as that
    # type's global under GLOBAL_KEY, and its persisted key is kept here so the first write or delete
    # of that global removes it from the home store. Remove once no store holds a global under a key
    # other than GLOBAL_KEY.
    legacy_global_keys: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class _LookupKeys:
    name: str | None
    indexes: dict[str, str]


_Names = dict[tuple[str, str], str]
_Indexes = dict[tuple[str, str, str], str]
_LookupKeysByType = dict[str, dict[str, _LookupKeys]]


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
        # Mutations hold this lock and refuse to start on the thread already inside one (see _mutating).
        # It is reentrant so a mutation's extractors, decoders and updaters may still read the cache.
        # Single-structure reads (get, get_all, get_envelope, key lookups) stay unlocked. The guarantee is
        # per call: each reads one dict under the GIL and sees it wholly before or after a write's install.
        # Two unlocked calls can straddle a write or pull, and see different states.
        self._lock = threading.RLock()
        self._mutating_thread: int | None = None

    @_mutating
    def register_document_type(self, document: DocumentDefinition[Any]) -> None:
        self._register_document_type(document)

    def _register_document_type(self, document: DocumentDefinition[Any]) -> None:
        if document.type in self._state.documents:
            raise CultCacheError(f"Document type already registered: {document.type}")
        schema_name = document.catalog_entry().schema_name
        if schema_name in self._state.documents_by_schema_name:
            raise CultCacheError(f"Document schema name already registered: {schema_name}")
        name_extractors = dict(self._state.name_extractors)
        index_extractors = {type: dict(indexes) for type, indexes in self._state.index_extractors.items()}
        if document.name is not None:
            name_extractors[document.type] = document.name
        if document.indexes:
            index_extractors.setdefault(document.type, {}).update(document.indexes)
        self._install_lookups(name_extractors, index_extractors)
        self._state.documents[document.type] = document
        self._state.documents_by_schema_name[schema_name] = document

    @_mutating
    def register_registry(self, documents: list[DocumentDefinition[Any]] | tuple[DocumentDefinition[Any], ...]) -> None:
        for document in documents:
            self._register_document_type(document)

    @_mutating
    def register_name_lookup(self, document: DocumentDefinition[Any], extractor: str | Any) -> None:
        self._assert_registered(document)
        name_extractors = dict(self._state.name_extractors)
        name_extractors[document.type] = extractor
        self._install_lookups(name_extractors, self._state.index_extractors)

    @_mutating
    def register_index(self, document: DocumentDefinition[Any], index: str, extractor: str | Any) -> None:
        self._assert_registered(document)
        index_extractors = {type: dict(indexes) for type, indexes in self._state.index_extractors.items()}
        index_extractors.setdefault(document.type, {})[index] = extractor
        self._install_lookups(self._state.name_extractors, index_extractors)

    def _install_lookups(
        self, name_extractors: dict[str, str | Any], index_extractors: dict[str, dict[str, str | Any]]
    ) -> None:
        """Derives every lookup under the candidate extractors, then installs extractors and lookups
        together. An extractor that raises on a held value installs nothing."""
        names, indexes, lookup_keys = self._derive_lookups(self._state.values, name_extractors, index_extractors)
        (
            self._state.name_extractors,
            self._state.index_extractors,
            self._state.names,
            self._state.indexes,
            self._state.lookup_keys,
        ) = (name_extractors, index_extractors, names, indexes, lookup_keys)

    @_mutating
    def add_backing_store(self, store: BackingStore, types: list[str] | tuple[str, ...] | set[str]) -> None:
        """Makes the store home to the given types, or the generic store when there are none.

        A type has exactly one home store, and attaching cannot move the home of a record the cache holds.
        """
        types = list(types)
        if not types:
            self._add_generic_store(store)
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

    @_mutating
    def add_generic_store(self, store: BackingStore) -> None:
        """Makes the store home to every type no other store claims. A cache has at most one."""
        self._add_generic_store(store)

    def _add_generic_store(self, store: BackingStore) -> None:
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

    @_mutating
    def pull_all_backing_stores(self) -> None:
        """Builds the complete next state (values, envelopes, name and index lookups) in local
        structures, running every check and every user extractor; the cache's state is replaced
        only once that build finishes, so a refused load admits nothing."""
        loaded_values: dict[str, dict[str, Any]] = {}
        loaded_envelopes: dict[str, dict[str, CultCacheEnvelope]] = {}
        loaded_legacy_global_keys: dict[str, str] = {}
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
                    if envelope.type in seen_globals:
                        raise CultCacheError(f"Duplicate global document for type: {envelope.type}")
                    seen_globals.add(envelope.type)
                    if envelope.key != GLOBAL_KEY:
                        loaded_legacy_global_keys[envelope.type] = envelope.key
                        envelope = replace(envelope, key=GLOBAL_KEY)
                value = document.decode_payload(envelope.payload)
                loaded_values.setdefault(envelope.type, {})[envelope.key] = value
                loaded_envelopes.setdefault(envelope.type, {})[envelope.key] = envelope
        loaded_names, loaded_indexes, loaded_lookup_keys = self._derive_lookups(
            loaded_values, self._state.name_extractors, self._state.index_extractors
        )
        # Plain attribute assignments: nothing fallible runs once the swap begins.
        (
            self._state.values,
            self._state.envelopes,
            self._state.names,
            self._state.indexes,
            self._state.lookup_keys,
            self._state.legacy_global_keys,
        ) = (loaded_values, loaded_envelopes, loaded_names, loaded_indexes, loaded_lookup_keys, loaded_legacy_global_keys)

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

    # Name and index resolution read the lookup and then the values, two structures a pull swaps and a
    # write updates in sequence, so they hold the lock to resolve a key and its value from one state.
    @_locked
    def get_by_name(self, document: DocumentDefinition[T], name: str) -> T | None:
        key = self.get_key_by_name(document, name)
        return None if key is None else self.get(document, key)

    def get_key_by_index(self, document: DocumentDefinition[Any], index: str, value: str) -> str | None:
        self._assert_registered(document)
        return self._state.indexes.get((document.type, index, value))

    @_locked
    def get_by_index(self, document: DocumentDefinition[T], index: str, value: str) -> T | None:
        key = self.get_key_by_index(document, index, value)
        return None if key is None else self.get(document, key)

    @_mutating
    def put(self, document: DocumentDefinition[T], key: str, value: T) -> None:
        self._put(document, key, value)

    def _put(self, document: DocumentDefinition[T], key: str, value: T) -> None:
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
        self._write(document, [(envelope, value)], batch=False)

    @_mutating
    def put_envelope(self, document: DocumentDefinition[T], envelope: CultCacheEnvelope) -> T:
        self._assert_registered(document)
        self._check_envelope(document, envelope)
        value = document.decode_payload(envelope.payload)
        self._write(document, [(envelope, value)], batch=False)
        return value

    @_mutating
    def put_envelopes(self, document: DocumentDefinition[T], envelopes: list[CultCacheEnvelope]) -> list[T]:
        self._assert_registered(document)
        values: list[T] = []
        for envelope in envelopes:
            self._check_envelope(document, envelope)
            values.append(document.decode_payload(envelope.payload))
        self._write(document, list(zip(envelopes, values)), batch=True)
        return values

    @staticmethod
    def _check_envelope(document: DocumentDefinition[Any], envelope: CultCacheEnvelope) -> None:
        if envelope.type != document.type:
            raise CultCacheError(
                f"Envelope type {envelope.type} does not match document type {document.type}"
            )
        if not envelope.key or not envelope.key.strip():
            raise CultCacheError(f"Envelope key for document type {document.type} must be non-empty")
        if document.global_document and envelope.key != GLOBAL_KEY:
            raise CultCacheError(f"Global document {document.type} must use key {GLOBAL_KEY}")

    def _write(
        self, document: DocumentDefinition[Any], records: list[tuple[CultCacheEnvelope, Any]], *, batch: bool
    ) -> None:
        """Validates every record (home and name and index extractors included) before the store is
        touched; once the store accepts, applying to the cache has no fallible step."""
        type = document.type
        name_extractor = self._state.name_extractors.get(type)
        index_extractors = self._state.index_extractors.get(type, {})
        admitted = [
            (envelope, value, self._lookup_keys_for(value, name_extractor, index_extractors))
            for envelope, value in records
        ]
        store = self._store_for_type(type)
        legacy_key = self._state.legacy_global_keys.get(type)
        if store is not None:
            if batch:
                store.push_all([envelope for envelope, _ in records])
            else:
                store.push(records[0][0])
            if legacy_key is not None:
                self._retire_legacy_global(store, type, legacy_key)
        self._state.legacy_global_keys.pop(type, None)
        values = self._state.values.setdefault(type, {})
        envelopes = self._state.envelopes.setdefault(type, {})
        lookup_keys = self._state.lookup_keys.setdefault(type, {})
        for envelope, value, keys in admitted:
            self._remove_lookups(type, envelope.key)
            values[envelope.key] = value
            envelopes[envelope.key] = envelope
            lookup_keys[envelope.key] = keys
            if keys.name is not None:
                self._state.names[(type, keys.name)] = envelope.key
            for index, index_value in keys.indexes.items():
                self._state.indexes[(type, index, index_value)] = envelope.key

    @staticmethod
    def _retire_legacy_global(store: BackingStore, type: str, legacy_key: str) -> None:
        """The first write of a global loaded from a legacy key removes that record in the same write. If
        the removal fails, the new record is taken back out, so the store keeps only the legacy record."""
        try:
            store.delete(type, legacy_key)
        except BaseException:
            try:
                store.delete(type, GLOBAL_KEY)
            except Exception:  # noqa: BLE001 - the removal failure is the error reported
                pass
            raise

    @_mutating
    def put_global(self, document: DocumentDefinition[T], value: T) -> None:
        self._assert_global(document)
        self._put(document, GLOBAL_KEY, value)

    @_mutating
    def update(self, document: DocumentDefinition[T], key: str, updater: Any) -> T:
        return self._update(document, key, updater)

    def _update(self, document: DocumentDefinition[T], key: str, updater: Any) -> T:
        current = self.get_required(document, key)
        updated = updater(current)
        self._put(document, key, updated)
        return updated

    @_mutating
    def update_global(self, document: DocumentDefinition[T], updater: Any) -> T:
        self._assert_global(document)
        return self._update(document, GLOBAL_KEY, updater)

    @_mutating
    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self._delete(document, key)

    def _delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self._assert_registered(document)
        store = self._store_for_type(document.type)
        legacy_key = self._state.legacy_global_keys.get(document.type) if key == GLOBAL_KEY else None
        if store is not None:
            store.delete(document.type, key if legacy_key is None else legacy_key)
        if legacy_key is not None:
            del self._state.legacy_global_keys[document.type]
        self._remove_lookups(document.type, key)
        for by_type in (self._state.values, self._state.envelopes, self._state.lookup_keys):
            held = by_type.get(document.type)
            if held is not None:
                held.pop(key, None)
                if not held:
                    by_type.pop(document.type, None)

    @_mutating
    def delete_global(self, document: DocumentDefinition[Any]) -> None:
        self._assert_global(document)
        self._delete(document, GLOBAL_KEY)

    # Snapshots iterate dicts a concurrent write would resize mid-iteration, so they hold the lock.
    @_locked
    def snapshot(self) -> dict[str, dict[str, Any]]:
        out: dict[str, dict[str, Any]] = {}
        for type, values in self._state.values.items():
            out[type] = dict(values)
        return out

    @_locked
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

    @staticmethod
    def _lookup_keys_for(
        value: Any, name_extractor: str | Any | None, index_extractors: dict[str, str | Any]
    ) -> _LookupKeys:
        name = None if name_extractor is None else extract_value(value, name_extractor)
        indexes: dict[str, str] = {}
        for index, extractor in index_extractors.items():
            index_value = extract_value(value, extractor)
            if index_value is not None:
                indexes[index] = str(index_value)
        return _LookupKeys(None if name is None else str(name), indexes)

    def _derive_lookups(
        self,
        values_by_type: dict[str, dict[str, Any]],
        name_extractors: dict[str, str | Any],
        index_extractors: dict[str, dict[str, str | Any]],
    ) -> tuple[_Names, _Indexes, _LookupKeysByType]:
        """Builds name and index lookups for the given values in fresh dicts, touching no cache state."""
        names: _Names = {}
        indexes: _Indexes = {}
        lookup_keys: _LookupKeysByType = {}
        for type, values in values_by_type.items():
            name_extractor = name_extractors.get(type)
            type_index_extractors = index_extractors.get(type, {})
            for key, value in values.items():
                keys = self._lookup_keys_for(value, name_extractor, type_index_extractors)
                lookup_keys.setdefault(type, {})[key] = keys
                if keys.name is not None:
                    names[(type, keys.name)] = key
                for index, index_value in keys.indexes.items():
                    indexes[(type, index, index_value)] = key
        return names, indexes, lookup_keys

    def _remove_lookups(self, type: str, key: str) -> None:
        """Removes the lookups the held record produced at admission, handing a name or index value to
        another held record that produced it too. Reads stored lookup keys only; runs no extractor."""
        held = self._state.lookup_keys.get(type, {})
        keys = held.get(key)
        if keys is None:
            return
        if keys.name is not None and self._state.names.get((type, keys.name)) == key:
            del self._state.names[(type, keys.name)]
            for candidate_key, candidate in held.items():
                if candidate_key != key and candidate.name == keys.name:
                    self._state.names[(type, keys.name)] = candidate_key
        for index, index_value in keys.indexes.items():
            lookup_key = (type, index, index_value)
            if self._state.indexes.get(lookup_key) != key:
                continue
            del self._state.indexes[lookup_key]
            for candidate_key, candidate in held.items():
                if candidate_key != key and candidate.indexes.get(index) == index_value:
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
