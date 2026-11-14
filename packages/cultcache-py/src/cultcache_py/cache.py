from __future__ import annotations

import enum
import functools
import math
import threading
from decimal import Decimal
from dataclasses import dataclass, field, replace
from typing import Any, Callable, Generic, Iterable, TypeVar

from .backing_store import BackingStore, CultCacheEnvelope
from .documents import DocumentDefinition, UnreadSlots, extract_value
from .stores import SchemaConflictError, _parse_stored_at, retyped, stored_at_over, written_owns

T = TypeVar("T")
F = TypeVar("F", bound=Callable[..., Any])
GLOBAL_KEY = "__global__"


class UpdateOutcome(enum.Enum):
    UPDATED = "updated"
    DECLINED = "declined"
    LOST_RACE = "lost_race"


@dataclass(frozen=True)
class UpdateResult(Generic[T]):
    """What `CultCache.update` did and the cache's record at the key after it (None when it holds nothing). The document decides
    nothing: it is a read after the outcome."""

    outcome: UpdateOutcome
    document: T | None


@dataclass(frozen=True)
class _Expected:
    """What an update's commit requires to still hold. A cache with no store requires the held envelope object at the key (None:
    nothing held); a store-backed one requires the type's home to be `store` and the record there to have content version
    `version` (None: no record)."""

    held: CultCacheEnvelope | None = None
    store: BackingStore | None = None
    version: str | None = None


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


# Blank is no value: empty or only C# White_Space characters (the 25 chars string.IsNullOrWhiteSpace is true for).
# An explicit set, not str.isspace(): Python also counts U+001C-001F, which C# does not, and omits nothing C# counts.
_BLANK_INDEX_CHARS = frozenset(
    chr(code)
    for code in (
        *range(0x09, 0x0E),
        0x20,
        0x85,
        0xA0,
        0x1680,
        *range(0x2000, 0x200B),
        0x2028,
        0x2029,
        0x202F,
        0x205F,
        0x3000,
    )
)


def _utf16_order(text: str) -> bytes:
    """Sorts as C# StringComparer.Ordinal does: by UTF-16 code unit, not code point."""
    return text.encode("utf-16-be", "surrogatepass")


def index_text(value: Any) -> str | None:
    """The canonical text of an index value, as C# IndexValue and RenderCanonicalNumber, or None when it names no
    value: a string is as is (never trimmed), a boolean is `True`/`False`, an integer is its digits, a non-integral
    finite float is its exact decimal expansion, NaN and infinities and blank strings are no value."""
    if value is None:
        return None
    if isinstance(value, bool):  # before int: bool is an int subclass
        return "True" if value else "False"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            return None
        if value.is_integer():
            return str(int(value))  # -0.0 is 0
        text = format(Decimal(value), "f")  # Decimal(float) is exact
        return text.rstrip("0").rstrip(".") if "." in text else text
    text = value if isinstance(value, str) else str(value)
    return None if all(char in _BLANK_INDEX_CHARS for char in text) else text


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
    # The records the last pull read that no registered type claims, sorted by key. Derived from the pull and swapped with the
    # rest of the state, written nowhere else; it decides only refusals at its keys. A store keeps their bytes.
    foreign: list["ForeignRecord"] = field(default_factory=list)
    # The records held read-only, by key (a key holds one record) with the type that holds it: the payload carried slots past the
    # declared ones and nothing consents to the loss. Derived from the load or the write that admitted the record and swapped with
    # the rest of the state; it decides only refusals at its keys, and any other key is absent.
    unread: dict[str, tuple[str, UnreadSlots]] = field(default_factory=dict)


@dataclass(frozen=True)
class ForeignRecord:
    """A record a store holds that no registered type claims: the cache does not serve it, refuses a write or a removal at its
    key, and a store keeps its bytes. Registering a type that declares its schema id claims it at the next load."""

    key: str
    schema_id: str
    schema_name: str
    stored_at: str


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
        # Every registration refusal is a SchemaConflictError naming the new document's schema id and both schema names.
        entry = document.catalog_entry()
        schema_name = entry.schema_name

        def refuse(registered: DocumentDefinition[Any], what: str) -> SchemaConflictError:
            return SchemaConflictError(
                entry.schema_id,
                [registered.catalog_entry().schema_name, schema_name],
                "",
                message=f"{what} is already registered to document type {registered.type!r} and cannot also be claimed by "
                f"{document.type!r}.",
            )

        if document.type in self._state.documents:
            raise refuse(self._state.documents[document.type], f"Document type {document.type!r}")
        if schema_name in self._state.documents_by_schema_name:
            raise refuse(self._state.documents_by_schema_name[schema_name], f"Schema name {schema_name!r}")
        # One document owns each schema id. Others may list it as compatible beside its owner: a record under the id resolves to
        # its owner, and to a lister only when nothing owns it. Neither depends on the order documents were registered in.
        for registered in self._state.documents.values():
            if registered.catalog_entry().schema_id == entry.schema_id:
                raise refuse(registered, f"Schema id {entry.schema_id!r}")
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
        if index in document.roles:
            raise CultCacheError(f"Index '{index}' of {document.type} is declared as a role; a role is never a unique index.")
        index_extractors = {type: dict(indexes) for type, indexes in self._state.index_extractors.items()}
        index_extractors.setdefault(document.type, {})[index] = extractor
        self._install_lookups(self._state.name_extractors, index_extractors)

    def _install_lookups(
        self, name_extractors: dict[str, str | Any], index_extractors: dict[str, dict[str, str | Any]]
    ) -> None:
        """Derives every lookup under the candidate extractors, then installs extractors and lookups
        together. An extractor that raises on a held value installs nothing."""
        names, indexes, lookup_keys = self._derive_lookups(
            [(type, key, value) for type, values in self._state.values.items() for key, value in values.items()],
            name_extractors,
            index_extractors,
        )
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
        read_values: dict[tuple[str, str], Any] = {}  # (type, key) -> value, one per key, the last read wins; the judge is order-free
        loaded_envelopes: dict[str, dict[str, CultCacheEnvelope]] = {}
        loaded_legacy_global_keys: dict[str, str] = {}
        routes, generic = self._state.stores_by_type, self._state.generic_store
        seen_globals: set[str] = set()
        foreign: list[ForeignRecord] = []
        loaded_unread: dict[str, tuple[str, UnreadSlots]] = {}
        type_at_key: dict[str, tuple[str, int]] = {}  # one record per key across every store: its type (or schema name) and store
        stores: list[BackingStore] = []
        for candidate in [*self._state.stores_by_type.values(), self._state.generic_store]:
            if candidate is not None and all(candidate is not store for store in stores):
                stores.append(candidate)
        for position, store in enumerate(stores):
            for envelope in store.pull_all():
                document = self._resolve_document_for_envelope(envelope)
                if document is None:
                    entry = envelope.catalog_entry
                    held_name = entry.schema_name if entry is not None else envelope.type
                    self._claim_key(type_at_key, envelope.key, held_name, position)
                    foreign.append(
                        ForeignRecord(envelope.key, envelope.schema_id or envelope.type, held_name, envelope.stored_at)
                    )
                    continue
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
                self._claim_key(type_at_key, envelope.key, envelope.type, position)
                arrived, envelope = envelope, self._stamped(document, envelope)
                value, unread = self._decode_marked(document, arrived)
                if unread is not None:
                    loaded_unread[envelope.key] = (envelope.type, unread)
                loaded_values.setdefault(envelope.type, {})[envelope.key] = value
                read_values[(envelope.type, envelope.key)] = value
                loaded_envelopes.setdefault(envelope.type, {})[envelope.key] = envelope
        loaded_names, loaded_indexes, loaded_lookup_keys = self._derive_lookups(
            [(type, key, value) for (type, key), value in read_values.items()], self._state.name_extractors, self._state.index_extractors
        )
        # Plain attribute assignments: nothing fallible runs once the swap begins.
        (
            self._state.values,
            self._state.envelopes,
            self._state.names,
            self._state.indexes,
            self._state.lookup_keys,
            self._state.legacy_global_keys,
            self._state.foreign,
            self._state.unread,
        ) = (
            loaded_values,
            loaded_envelopes,
            loaded_names,
            loaded_indexes,
            loaded_lookup_keys,
            loaded_legacy_global_keys,
            sorted(foreign, key=lambda record: _utf16_order(record.key)),
            loaded_unread,
        )

    @staticmethod
    def _claim_key(type_at_key: dict[str, tuple[str, int]], key: str, type: str, store: int) -> None:
        """A key holds one record whatever its type, across every store a load reads. The refusal names the key and both types,
        never a payload."""
        held, held_store = type_at_key.setdefault(key, (type, store))
        if held != type or held_store != store:
            raise CultCacheError(f"Record {key!r} is held by both {held} and {type}: a key holds one record; the load admitted nothing.")

    def foreign_records(self) -> list[ForeignRecord]:
        """The records the last load read that no registered type claims, sorted by key (UTF-16 code-unit order)."""
        return list(self._state.foreign)

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

    def _put(self, document: DocumentDefinition[T], key: str, value: T, expected: _Expected | None = None) -> str:
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
        return self._write(document, [(envelope, value)], batch=False, expected=expected)

    @_mutating
    def put_envelope(self, document: DocumentDefinition[T], envelope: CultCacheEnvelope) -> T:
        self._assert_registered(document)
        self._check_envelope(document, envelope)
        arrived, envelope = envelope, self._stamped(document, envelope)
        value, unread = self._decode_marked(document, envelope)
        self._write(document, [(envelope, value)], batch=False, unread={envelope.key: unread})
        return value

    @_mutating
    def put_envelopes(self, document: DocumentDefinition[T], envelopes: list[CultCacheEnvelope]) -> list[T]:
        self._assert_registered(document)
        values: list[T] = []
        stamped: list[CultCacheEnvelope] = []
        unread: dict[str, UnreadSlots | None] = {}
        for envelope in envelopes:
            self._check_envelope(document, envelope)
            stamped.append(self._stamped(document, envelope))
            value, unread[envelope.key] = self._decode_marked(document, stamped[-1])
            values.append(value)
        self._write(document, list(zip(stamped, values)), batch=True, unread=unread)
        return values

    @staticmethod
    def _decode_marked(document: DocumentDefinition[Any], held_as: CultCacheEnvelope) -> tuple[Any, UnreadSlots | None]:
        """The payload decoded under the document, with the mark of any slot it carried past the declared ones, or no mark when the
        loss is consented. `held_as` is the envelope under the id the record is stored under: a load's arrival, a write's stamped
        envelope, so a key is writable in memory exactly when it is after a reload. A depth-0 mark is consented only when that id is
        one the document lists compatible and its catalog entry has slots past the document's that cover the mark's slot count (the
        loss a compatible-id declaration consents to, cultlib-gaps:ruling:slot-mark-consent). Any other mark keeps the record read-only."""
        value, slots = document.decode_payload_marked(held_as.payload)
        if slots is None:
            return value, None
        entry, stored = document.catalog_entry(), held_as.catalog_entry
        if slots.depth0_only and stored is not None and held_as.schema_id in entry.compatible_schema_ids and held_as.schema_id != entry.schema_id:
            declared = {member.slot for member in entry.members}
            reported = [member.slot for member in stored.members if member.slot not in declared]
            if reported and slots.slot_count <= max(reported) + 1:
                return value, None
        return value, slots

    @staticmethod
    def _stamped(document: DocumentDefinition[Any], envelope: CultCacheEnvelope) -> CultCacheEnvelope:
        """The record under its registered schema's id, whatever id the envelope arrived under, so the catalog entry a store
        writes is the one its schema id names."""
        entry = document.catalog_entry()
        return replace(envelope, schema_id=entry.schema_id, catalog_entry=entry)

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
        self,
        document: DocumentDefinition[Any],
        records: list[tuple[CultCacheEnvelope, Any]],
        *,
        batch: bool,
        unread: dict[str, UnreadSlots | None] | None = None,
        expected: _Expected | None = None,
    ) -> str:
        """Validates every record (home and name and index extractors included) before the store is
        touched; once the store accepts, applying to the cache has no fallible step. With `expected` (an update's commit) the write
        also requires the record to be unchanged since it was read and returns 'mismatch', having written and installed nothing,
        when it is not; otherwise 'committed'. A record is held under the stored_at its store reports writing."""
        type = document.type
        # A batch naming a key twice lands the last record for it, as C# CultCacheBatch stages one operation per key. The
        # judge, the store and the apply all read this one list.
        records = list({envelope.key: (envelope, value) for envelope, value in records}.values())
        self._refuse_unread([envelope.key for envelope, _ in records])
        name_extractor = self._state.name_extractors.get(type)
        index_extractors = self._state.index_extractors.get(type, {})
        admitted = [
            (envelope, value, self._lookup_keys_for(value, name_extractor, index_extractors))
            for envelope, value in records
        ]
        displaced = self._refuse_retype(document, [envelope for envelope, _ in records])
        self._refuse_index_sharing(
            [(type, envelope.key, keys) for envelope, _, keys in admitted],
            self._state.indexes,
            {(type, envelope.key) for envelope, _ in records},
        )
        store = self._store_for_type(type)
        legacy_key = self._state.legacy_global_keys.get(type)
        stamps: dict[str, Any] = {}
        if expected is not None:
            if legacy_key is not None:
                raise self._legacy_global_refusal(document, legacy_key)
            if expected.store is None:
                held = self._state.envelopes.get(type, {}).get(records[0][0].key)
                if store is not None or held is not expected.held:
                    return "mismatch"
            elif store is not expected.store:
                return "mismatch"
            else:
                stamp = store.commit_if(records[0][0], expected.version)  # type: ignore[attr-defined]
                if stamp is None:
                    return "mismatch"
                stamps[records[0][0].key] = stamp
        elif store is not None:
            if batch:
                reported = store.push_all([envelope for envelope, _ in records])
                stamps = reported if isinstance(reported, dict) else {}
            else:
                stamps[records[0][0].key] = store.push(records[0][0])
            if legacy_key is not None:
                self._retire_legacy_global(store, type, legacy_key)
        else:
            for envelope, _ in records:
                held_at = next((held for held in (by_type.get(envelope.key) for by_type in self._state.envelopes.values()) if held is not None), None)
                if held_at is not None:
                    stamps[envelope.key] = stored_at_over(envelope.stored_at, held_at.stored_at)
        self._state.legacy_global_keys.pop(type, None)
        values = self._state.values.setdefault(type, {})
        envelopes = self._state.envelopes.setdefault(type, {})
        lookup_keys = self._state.lookup_keys.setdefault(type, {})
        for other, key in displaced:
            self._drop(other, key)
        for envelope, value, keys in admitted:
            stamp = stamps.get(envelope.key)
            if isinstance(stamp, str) and _parse_stored_at(stamp) is not None:
                envelope = replace(envelope, stored_at=stamp)
            self._remove_lookups(type, envelope.key)
            values[envelope.key] = value
            envelopes[envelope.key] = envelope
            # A held mark refused this write above, so a key reaching here carries none to clear.
            if (mark := (unread or {}).get(envelope.key)) is not None:
                self._state.unread[envelope.key] = (type, mark)
            lookup_keys[envelope.key] = keys
            if keys.name is not None:
                self._state.names[(type, keys.name)] = envelope.key
            for index, index_value in keys.indexes.items():
                self._state.indexes[(type, index, index_value)] = envelope.key
        return "committed"

    def _refuse_retype(self, document: DocumentDefinition[Any], written: list[CultCacheEnvelope]) -> list[tuple[str, str]]:
        """The one key-identity judge (variants:ruling:cache-refuses-retype): a key holds one record whatever its type, so a write
        may not land at a key this cache holds another type's record at, unless the written type owns that record's schema id
        (the single-file store's commit guard asks the same question of the file), and may not land at a key a store carries
        foreign. Returns the (type, key) records an owning write replaces; refuses, changing nothing, otherwise."""
        replaced: list[tuple[str, str]] = []
        foreign = {record.key: record for record in self._state.foreign}
        for envelope in written:
            carried = foreign.get(envelope.key)
            if carried is not None:
                raise self._foreign_refusal(carried)
            for other, held_envelopes in self._state.envelopes.items():
                held = held_envelopes.get(envelope.key)
                if other == document.type or held is None:
                    continue
                if not written_owns(held, envelope) or self._store_for_type(other) is not self._store_for_type(document.type):
                    raise retyped(held, envelope)
                replaced.append((other, envelope.key))
        return replaced

    @_locked
    def is_writable(self, document: DocumentDefinition[Any], key: str) -> bool:
        """Whether a write or removal of the key by this document would pass the judges that run before any store is touched: False for
        a record held read-only (its payload carried slots past the declared ones, at any depth, and nothing consents to the loss, so
        writing it would drop data a newer writer stored), for a key a store carries foreign, and for a key held as another type's
        record this document does not own. Advisory (the write still decides): it answers at call time, and a key nothing holds is
        writable. It does not look at store paths: a link at a store path is not predicted, and the write refuses it with
        `StoreLinkedError`."""
        self._assert_registered(document)
        try:
            self._refuse_at(document, key)
        except (SchemaConflictError, CultCacheError):
            return False
        return True

    def _refuse_at(self, document: DocumentDefinition[Any], key: str) -> list[tuple[str, str]]:
        """The judges a write or removal of the key by this document passes before any store is touched, asked by `is_writable` and
        by `delete`: a global document writes only its global key, a record held read-only is never replaced, and a key held as
        another type's record or carried foreign is refused (`_refuse_retype`)."""
        if document.global_document and key != GLOBAL_KEY:
            raise CultCacheError(f"Global document {document.type} must use key {GLOBAL_KEY}")
        self._refuse_unread([key])
        entry = document.catalog_entry()
        probe = CultCacheEnvelope.create(key=key, type=document.type, payload=b"", schema_id=entry.schema_id, catalog_entry=entry)
        return self._refuse_retype(document, [probe])

    def _refuse_unread(self, keys: list[str]) -> None:
        """The one check every write and removal asks before any store is touched."""
        for key in keys:
            held = self._state.unread.get(key)
            if held is None:
                continue
            type, slots = held
            raise self._read_only_refusal(self._state.documents[type], key, self._state.envelopes[type][key].schema_id, slots)

    @staticmethod
    def _read_only_refusal(
        document: DocumentDefinition[Any], key: str, schema_id: str | None, slots: UnreadSlots
    ) -> SchemaConflictError:
        """The one refusal of a write to a record that carries slots this build would drop: a record the cache holds read-only,
        or one an update reads from the store."""
        name = document.catalog_entry().schema_name
        return SchemaConflictError(
            schema_id or document.type,
            [name],
            key,
            message=(
                f"{name} record {key} is read-only: {slots.type_name} was read with "
                f"{slots.slot_count} slots where this build declares {slots.declared_count} ({slots.occurrences} object(s) in the "
                "record). Writing it would drop data a newer writer stored; read it with types that declare those slots, or "
                "write it from that build."
            ),
            unread_slots=slots,
        )

    @staticmethod
    def _legacy_global_refusal(document: DocumentDefinition[Any], legacy_key: str) -> CultCacheError:
        return CultCacheError(
            f"Global {document.type} is held under the legacy key {legacy_key!r}; update cannot copy it. "
            "put_global writes the global and retires the legacy key."
        )

    @staticmethod
    def _foreign_refusal(record: ForeignRecord) -> SchemaConflictError:
        return SchemaConflictError(
            record.schema_id,
            [record.schema_name],
            record.key,
            message=(
                f"Record {record.key!r} is held by the store as a {record.schema_name} (schema id {record.schema_id!r}) that no "
                "registered type claims; a write or removal there would replace what this cache cannot read. Register the type "
                "that owns the schema id, or remove the record with the runtime that wrote it."
            ),
        )

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

    def update(
        self, document: DocumentDefinition[T], key: str, apply: Callable[[T | None], T | None], *, max_attempts: int = 5
    ) -> UpdateResult[T]:
        """Reads the record at the key as its home store holds it, applies `apply` to a fresh copy of it, and commits the result
        only if the record is still the one that was read; when another writer got there first it reads again and runs `apply`
        again, up to `max_attempts` times.

        `apply` may run up to `max_attempts` times, on a fresh copy of the record as the store holds it now (None when the key
        holds nothing), which it may change and return. It must not write the same key through this cache or have side effects.
        Returning None writes nothing (DECLINED). A lost race that outlasts `max_attempts` returns LOST_RACE and does not raise;
        a refusal, or a raise from `apply`, propagates and is not retried. The write is durable when update returns. The
        result's document is the cache's record after the outcome. `put` is the blind write. A cache with no store copies its
        held instance; a store without `read` and `commit_if` (the JSON lines store among them) refuses before `apply` runs."""
        if not callable(apply):
            raise TypeError("CultCache update needs a function to apply.")
        if not isinstance(max_attempts, int) or isinstance(max_attempts, bool):
            raise TypeError(f"CultCache update max_attempts must be an int, not {type(max_attempts).__name__}.")
        if max_attempts < 1:
            raise ValueError(f"CultCache update max_attempts must be at least 1, not {max_attempts}.")
        for attempt in range(1, max_attempts + 1):
            copy, expected = self._read_for_update(document, key)
            # Outside the cache's lock and the store's: apply may take its time and may write other keys through this cache.
            next_value = apply(copy)
            if next_value is None:
                return UpdateResult(UpdateOutcome.DECLINED, self.get(document, key))
            if self._commit_update(document, key, next_value, expected) == "committed":
                return UpdateResult(UpdateOutcome.UPDATED, self.get(document, key))
        # Display only: the cache shows what the winner left.
        if self._state.stores_by_type or self._state.generic_store is not None:
            self.pull_all_backing_stores()
        return UpdateResult(UpdateOutcome.LOST_RACE, self.get(document, key))

    @_mutating
    def _read_for_update(self, document: DocumentDefinition[T], key: str) -> tuple[T | None, _Expected]:
        """One attempt's read: the copy to hand `apply` and the condition its commit holds. Every refusal that does not depend on
        what `apply` returns is raised here, so `apply` never runs for a write that cannot land. Installs nothing."""
        self._assert_registered(document)
        self._refuse_at(document, key)
        legacy_key = self._state.legacy_global_keys.get(document.type)
        if legacy_key is not None:
            raise self._legacy_global_refusal(document, legacy_key)
        store = self._store_for_type(document.type)
        if store is None:
            held = self._state.envelopes.get(document.type, {}).get(key)
            return (None if held is None else document.decode_payload(held.payload)), _Expected(held=held)
        read, commit_if = getattr(store, "read", None), getattr(store, "commit_if", None)
        if read is None or commit_if is None:
            raise CultCacheError(
                f"Backing store {type(store).__name__} for {document.type} cannot write conditionally; "
                "update needs a store with read and commit_if."
            )
        if document.global_document:
            for envelope in store.pull_all():
                if envelope.key != GLOBAL_KEY and self._resolve_document_for_envelope(envelope) is document:
                    raise self._legacy_global_refusal(document, envelope.key)
        found = read(key)
        if found is None:
            return None, _Expected(store=store)
        envelope, version = found
        if self._resolve_document_for_envelope(envelope) is not document:
            raise CultCacheError(f"Record {key} is not a {document.type}; update cannot copy it.")
        value, unread = self._decode_marked(document, envelope)
        if unread is not None:
            raise self._read_only_refusal(document, key, envelope.schema_id, unread)
        return value, _Expected(store=store, version=version)

    @_mutating
    def _commit_update(self, document: DocumentDefinition[T], key: str, value: T, expected: _Expected) -> str:
        return self._put(document, key, value, expected)

    def update_global(
        self, document: DocumentDefinition[T], apply: Callable[[T | None], T | None], *, max_attempts: int = 5
    ) -> UpdateResult[T]:
        self._assert_global(document)
        return self.update(document, GLOBAL_KEY, apply, max_attempts=max_attempts)

    @_mutating
    def delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self._delete(document, key)

    def _delete(self, document: DocumentDefinition[Any], key: str) -> None:
        self._assert_registered(document)
        # A key held as another type's record this document owns is removed, as a write of the key would replace it (C# Remove is by key).
        owned = self._refuse_at(document, key)
        store = self._store_for_type(document.type)
        legacy_key = self._state.legacy_global_keys.get(document.type) if key == GLOBAL_KEY else None
        if store is not None:
            # One record per key: the owned record when this document does not hold the key, else its own, in one store call.
            holder = owned[0][0] if owned else document.type
            store.delete(holder, key if legacy_key is None else legacy_key)
        if legacy_key is not None:
            del self._state.legacy_global_keys[document.type]
        for other, _ in owned:
            self._drop(other, key)
        self._drop(document.type, key)

    def _drop(self, type: str, key: str) -> None:
        """Removes the held record and its lookups from the cache's maps; the store is the caller's."""
        self._remove_lookups(type, key)
        for by_type in (self._state.values, self._state.envelopes, self._state.lookup_keys):
            held = by_type.get(type)
            if held is not None:
                held.pop(key, None)
                if not held:
                    by_type.pop(type, None)

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
            index_value = index_text(extract_value(value, extractor))
            if index_value is not None:
                indexes[index] = index_value
        return _LookupKeys(None if name is None else str(name), indexes)

    def _derive_lookups(
        self,
        records: list[tuple[str, str, Any]],
        name_extractors: dict[str, str | Any],
        index_extractors: dict[str, dict[str, str | Any]],
    ) -> tuple[_Names, _Indexes, _LookupKeysByType]:
        """Builds name and index lookups for the given (type, key, value) records, judged whole and order-free, in fresh dicts, touching no cache state."""
        names: _Names = {}
        indexes: _Indexes = {}
        lookup_keys: _LookupKeysByType = {}
        landing: list[tuple[str, str, _LookupKeys]] = []
        for type, key, value in records:
            keys = self._lookup_keys_for(value, name_extractors.get(type), index_extractors.get(type, {}))
            lookup_keys.setdefault(type, {})[key] = keys
            landing.append((type, key, keys))
        # Judged whole and order-free, over every type at once and on the file alone (never against what the cache held): a held value can
        # have been mutated in place since it was admitted, and another process may have moved a value between records.
        self._refuse_index_sharing(landing, {}, ())
        for type in lookup_keys:
            for key, keys in lookup_keys.get(type, {}).items():
                if keys.name is not None:
                    names[(type, keys.name)] = key
                for index, index_value in keys.indexes.items():
                    indexes[(type, index, index_value)] = key
        return names, indexes, lookup_keys

    def _refuse_index_sharing(
        self,
        landing: list[tuple[str, str, _LookupKeys]],
        held: dict[tuple[str, str, str], str],
        replaced: Iterable[tuple[str, str]],
    ) -> None:
        """The one index rule (variants:ruling:unique-indexes): within one type and one unique alias, no two records hold one
        non-blank value, judged on the state the admission would leave and never on staging, file or pull order. The holders
        of a value are the landing keys projecting it plus the held key unless this admission replaces it. Of every
        (type, alias, value) with two or more distinct holders, the refusal names the least by (alias, its least key, its
        second-least key, schema name), each compared by UTF-16 code unit, never the value."""
        released = set(replaced)
        landed: dict[tuple[str, str, str], list[str]] = {}
        for type, key, keys in landing:
            for index, index_value in keys.indexes.items():
                landed.setdefault((type, index, index_value), []).append(key)
        violations: list[tuple[bytes, bytes, bytes, bytes, str, str, str, str]] = []
        for (type, index, index_value), keys in landed.items():
            held_key = held.get((type, index, index_value))
            if held_key is not None and (type, held_key) not in released:
                keys = [*keys, held_key]
            distinct = sorted(keys, key=_utf16_order)
            if len(distinct) > 1:
                document = self._state.documents.get(type)
                schema_name = (document.schema_name if document is not None else None) or type
                violations.append(
                    (_utf16_order(index), _utf16_order(distinct[0]), _utf16_order(distinct[1]), _utf16_order(schema_name),
                     index, distinct[0], distinct[1], schema_name)
                )
        if violations:
            *_, index, first, second, schema_name = min(violations, key=lambda violation: violation[:4])
            raise CultCacheError(
                f"Records {first} and {second} ({schema_name}) both hold unique index '{index}'; "
                "an index value names one record."
            )

    def _remove_lookups(self, type: str, key: str) -> None:
        """Removes the lookups the held record produced at admission, handing a name to another held
        record that produced it too (an index value has one holder). Reads stored lookup keys only; runs no extractor."""
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

    def _assert_registered(self, document: DocumentDefinition[Any]) -> None:
        if self._state.documents.get(document.type) is not document:
            raise CultCacheError(f"Document type is not registered on this cache: {document.type}")

    def _assert_global(self, document: DocumentDefinition[Any]) -> None:
        self._assert_registered(document)
        if not document.global_document:
            raise CultCacheError(f"Document type is not global: {document.type}")

    def _resolve_document_for_envelope(self, envelope: CultCacheEnvelope) -> DocumentDefinition[Any] | None:
        """A record resolves by its schema id: the document that owns the id, else the one document that lists it as compatible.
        Several listers and no owner name no single document, and the record is refused. The name its catalog carries is
        metadata; it names a document only when the id names none (a store from a runtime whose schema ids this one cannot
        know)."""
        if envelope.schema_id:
            entries = [(document, document.catalog_entry()) for document in self._state.documents.values()]
            for document, entry in entries:
                if entry.schema_id == envelope.schema_id:
                    return document
            listers = [document for document, entry in entries if envelope.schema_id in entry.compatible_schema_ids]
            if len(listers) > 1:
                raise SchemaConflictError(
                    envelope.schema_id,
                    sorted(document.catalog_entry().schema_name for document in listers),
                    envelope.key,
                    message=f"Schema id {envelope.schema_id!r} of record {envelope.key!r} is owned by no registered document and "
                    f"declared compatible by several ({', '.join(sorted(document.type for document in listers))}), so it resolves "
                    "to none. Register the document that owns the id, or declare it on one document.",
                )
            if listers:
                return listers[0]
        document = self._state.documents.get(envelope.type)
        if document is not None:
            return document
        return self._state.documents_by_schema_name.get(envelope.type)
