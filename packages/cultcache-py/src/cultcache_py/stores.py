from __future__ import annotations

import base64
import json
import os
import threading
from pathlib import Path
from typing import Any, Callable, TypeVar

from .backing_store import (
    CultCacheEnvelope,
    CultCacheSchemaCatalogEntry,
    CultCacheSchemaCatalogMember,
)

STORE_FORMAT_VERSION = "cultcache.store.v1"
# A store that can hold element ids. A reader older than element ids refuses this header: it would skip the id slots of a
# nested element and rewrite the element without them. This runtime writes it back only for a store it read under it; it
# does not decide when a document carries ids.
STORE_FORMAT_ELEMENT_IDS = "cultcache.store.v3"
_READABLE_STORE_FORMATS = (STORE_FORMAT_VERSION, STORE_FORMAT_ELEMENT_IDS)
_STORE_FORMAT_PREFIX = "cultcache.store."
_PERSISTED_RECORD_SLOTS = 4
_Contents = TypeVar("_Contents")


class StoreUnreadableError(ValueError):
    """A store file this runtime cannot read: not exactly one complete store (truncated, bytes after it, a missing or extra
    slot), a header or record it does not know, or a body it cannot decode. Open, push, delete and push_all refuse a file
    with this error, and a refused file is left as it was. The cause is `__cause__`."""

    def __init__(self, path: Path, cause: BaseException) -> None:
        super().__init__(f"CultCache store {path} is not readable: {cause}")
        self.path = path


class SchemaConflictError(ValueError):
    """A write the catalog cannot describe: records of different types under one schema id, two arrived schemas that share an id
    and disagree on the schema name, or a record no chosen entry publishes. Nothing is written. It names the id, the names
    involved (`schema_names`) and a record key (`record_key`). A cache raises it too, with an empty record key, when a
    document registers a schema id another registered document already carries."""

    def __init__(self, schema_id: str, schema_names: list[str], record_key: str, *, message: str | None = None) -> None:
        super().__init__(
            message
            or f"Schema id {schema_id!r} cannot describe record {record_key!r}: {', '.join(repr(name) for name in schema_names)}"
        )
        self.schema_id = schema_id
        self.schema_names = schema_names
        self.record_key = record_key


class JsonLinesBackingStore:
    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = Path(path)
        self._lock = threading.RLock()

    def pull_all(self) -> list[CultCacheEnvelope]:
        with self._lock:
            text = _read_store(self.path, lambda path: path.read_text(encoding="utf-8"))
            if text is None:
                return []
            envelopes: list[CultCacheEnvelope] = []
            for line_number, line in enumerate(text.splitlines(), start=1):
                if not line.strip():
                    continue
                raw = json.loads(line)
                try:
                    payload = base64.b64decode(raw["payload"])
                    envelopes.append(
                        CultCacheEnvelope(
                            key=raw["key"],
                            type=raw["type"],
                            payload=payload,
                            stored_at=raw.get("stored_at", raw.get("storedAt")),
                            schema_id=raw.get("schema_id", raw.get("schemaId")),
                        )
                    )
                except KeyError as exc:
                    raise ValueError(f"Malformed CultCache JSONL envelope at {self.path}:{line_number}: missing {exc}") from exc
            return envelopes

    def push(self, envelope: CultCacheEnvelope) -> None:
        with self._lock:
            existing = {(item.type, item.key): item for item in self.pull_all()}
            existing[(envelope.type, envelope.key)] = envelope
            self._replace_all(list(existing.values()))

    def push_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        with self._lock:
            existing = {(item.type, item.key): item for item in self.pull_all()}
            for envelope in envelopes:
                existing[(envelope.type, envelope.key)] = envelope
            self._replace_all(list(existing.values()))

    def delete(self, type: str, key: str) -> None:
        with self._lock:
            existing = [item for item in self.pull_all() if not (item.type == type and item.key == key)]
            self._replace_all(existing)

    def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(self.path.suffix + ".tmp")
        lines = []
        for envelope in sorted(envelopes, key=lambda item: (item.type, item.key)):
            lines.append(json.dumps({
                "key": envelope.key,
                "type": envelope.type,
                "payload": base64.b64encode(envelope.payload).decode("ascii"),
                "stored_at": envelope.stored_at,
            }, ensure_ascii=False, sort_keys=True))
        temp.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
        temp.replace(self.path)


class SingleFileMessagePackBackingStore:
    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = Path(path)
        self._lock = threading.RLock()
        # The header this store read; a rewrite keeps it, so a store that holds element ids never sheds its marker.
        self._format = STORE_FORMAT_VERSION

    def pull_all(self) -> list[CultCacheEnvelope]:
        msgpack = self._msgpack()
        with self._lock:
            # An empty file is not a store (no writer leaves one), so it is decoded and refused.
            # The disk decides the header: a file that is gone is not marked.
            self._format = STORE_FORMAT_VERSION
            data = _read_store(self.path, Path.read_bytes)
            if data is None:
                return []
            try:
                decoded = msgpack.unpackb(data, raw=False)
                snapshot = _decode_snapshot(decoded)
                if snapshot is not None:
                    self._format, envelopes = snapshot
                    return envelopes
                return _decode_legacy_envelopes(decoded, msgpack)
            except Exception as exc:
                raise StoreUnreadableError(self.path, exc) from exc

    def push(self, envelope: CultCacheEnvelope) -> None:
        with self._lock:
            self._replace_all([envelope], self.pull_all())

    def push_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        with self._lock:
            self._replace_all(list(envelopes), self.pull_all())

    def delete(self, type: str, key: str) -> None:
        with self._lock:
            self._replace_all([], [item for item in self.pull_all() if not (item.type == type and item.key == key)])

    def _replace_all(self, supplied: list[CultCacheEnvelope], arrived: list[CultCacheEnvelope]) -> None:
        msgpack = self._msgpack()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(self.path.suffix + ".tmp")
        temp.write_bytes(msgpack.packb(_encode_snapshot(supplied, arrived, self._format), use_bin_type=True))
        temp.replace(self.path)

    @staticmethod
    def _msgpack() -> Any:
        try:
            import msgpack  # type: ignore
        except ModuleNotFoundError as exc:
            raise RuntimeError(
                "SingleFileMessagePackBackingStore requires the optional 'msgpack' dependency. "
                "Install with: python -m pip install cultcache-py[msgpack]"
            ) from exc
        return msgpack


def canonical(candidates: Any) -> list[CultCacheSchemaCatalogEntry]:
    """Entries that tie, in one fixed order, so the catalog does not depend on the order they arrive in."""
    return sorted(candidates, key=lambda entry: (entry.schema_name, entry.content_hash, ",".join(entry.compatible_schema_ids)))


def _derive_catalog(records: list[tuple[CultCacheEnvelope, bool]]) -> list[CultCacheSchemaCatalogEntry]:
    """The catalog a write leaves, derived from the records being written and nothing else. For each schema id a record carries,
    one entry that publishes it: the entry that owns the id (a registered one, else one that arrived with the records), else one
    that lists it as compatible (registered first). Entries are written as chosen, never merged; a registered entry wins over an
    arrived one with the same own id. Records of different types under one id, two arrived entries with the same own id and
    different schema names, or a record no chosen entry publishes, refuse the write. `records` are (envelope, registered): an
    envelope the caller supplied is registered, one read back from the file is arrived."""
    entries: list[tuple[CultCacheSchemaCatalogEntry, bool]] = []
    by_id: dict[str, list[CultCacheEnvelope]] = {}
    tiers: dict[str, list[tuple[CultCacheEnvelope, bool, CultCacheSchemaCatalogEntry]]] = {}
    for envelope, registered in records:
        schema_id = _schema_id_for(envelope)
        supplied = envelope.catalog_entry
        publishes = supplied is not None and (supplied.schema_id == schema_id or schema_id in supplied.compatible_schema_ids)
        entry = supplied if publishes else _default_catalog_entry(envelope)
        entries.append((entry, registered))
        by_id.setdefault(schema_id, []).append(envelope)
        tiers.setdefault(schema_id, []).append((envelope, registered, entry))

    chosen: dict[str, tuple[CultCacheSchemaCatalogEntry, bool]] = {}
    for schema_id in sorted(by_id):
        carrying = by_id[schema_id]
        record_key = min(envelope.key for envelope in carrying)
        # Records of one tier under one id are one type. Across tiers the id keeps the type its records resolve to, so a write may
        # not retype records that are already there: unless the entries of both tiers own the id, which is a rename of the schema
        # under a stable id, and the registered descriptor wins. A read-back record's type is its schema's name, so tiers compare
        # the names of the entries that publish the id.
        for tier in (True, False):
            types = sorted({envelope.type for envelope, registered, _ in tiers[schema_id] if registered == tier})
            if len(types) > 1:
                raise SchemaConflictError(schema_id, types, record_key)
        registered_names = sorted({entry.schema_name for _, registered, entry in tiers[schema_id] if registered})
        arrived_names = sorted({entry.schema_name for _, registered, entry in tiers[schema_id] if not registered})
        owned_by = lambda tier: any(entry.schema_id == schema_id for _, registered, entry in tiers[schema_id] if registered == tier)
        if registered_names and arrived_names and registered_names[0] != arrived_names[0] and not (owned_by(True) and owned_by(False)):
            raise SchemaConflictError(schema_id, sorted([registered_names[0], arrived_names[0]]), record_key)
        own_registered = next(iter(canonical(entry for entry, registered in entries if registered and entry.schema_id == schema_id)), None)
        own_arrived = canonical(entry for entry, registered in entries if not registered and entry.schema_id == schema_id)
        own_registered_all = canonical(entry for entry, registered in entries if registered and entry.schema_id == schema_id)
        # Entries of one tier that share an own id must be one schema; a registered entry settles a disagreement among arrived ones.
        names = sorted({entry.schema_name for entry in (own_registered_all or own_arrived)})
        if len(names) > 1:
            raise SchemaConflictError(schema_id, names, record_key)
        if own_registered is not None:
            pick, registered_pick = own_registered, True
        elif own_arrived:
            pick, registered_pick = own_arrived[0], False
        else:
            listed_registered = next(iter(canonical(entry for entry, registered in entries if registered and schema_id in entry.compatible_schema_ids)), None)
            listed_arrived = next(iter(canonical(entry for entry, registered in entries if not registered and schema_id in entry.compatible_schema_ids)), None)
            pick = listed_registered if listed_registered is not None else listed_arrived
            registered_pick = listed_registered is not None
        if pick is None:
            continue
        prior = chosen.get(pick.schema_id)
        if prior is not None and prior[1] and not registered_pick:
            continue
        chosen[pick.schema_id] = (pick, registered_pick)

    for schema_id in sorted(by_id):
        if not any(entry.schema_id == schema_id or schema_id in entry.compatible_schema_ids for entry, _ in chosen.values()):
            raise SchemaConflictError(
                schema_id, sorted(entry.schema_name for entry, _ in chosen.values()), min(envelope.key for envelope in by_id[schema_id])
            )
    return [entry for entry, _ in chosen.values()]


def _encode_snapshot(
    supplied: list[CultCacheEnvelope], arrived: list[CultCacheEnvelope], format_version: str
) -> list[Any]:
    replaced = {(envelope.type, envelope.key) for envelope in supplied}
    kept = [envelope for envelope in arrived if (envelope.type, envelope.key) not in replaced]
    envelopes = [*kept, *supplied]
    catalog = [
        _encode_catalog_entry(entry)
        for entry in sorted(
            _derive_catalog([*((envelope, False) for envelope in kept), *((envelope, True) for envelope in supplied)]),
            key=lambda item: (item.schema_name, item.schema_id),
        )
    ]
    records = [
        [envelope.key, _schema_id_for(envelope), envelope.stored_at, envelope.payload]
        for envelope in sorted(envelopes, key=lambda item: item.key)
    ]
    return [format_version, catalog, records]


def _read_store(path: Path, read: Callable[[Path], _Contents]) -> _Contents | None:
    """Reads a store file, or returns None when nothing is at its path.

    Only nothing is absent. A dangling symbolic link is something: reading it as
    empty would let the writer replace the link and move the store off its volume.
    Any other failure to reach the file stays an OSError.
    """
    try:
        return read(path)
    except FileNotFoundError:
        if path.is_symlink():
            raise
        return None


def _describe_identity(value: Any) -> str:
    """A record key or schema id as a refusal may show it: a string is an identity and is
    named; anything else is described by its type, since the value is the store's."""
    if isinstance(value, str):
        return repr(value)
    return f"<{type(value).__name__}>"


def _describe_header(header: str) -> str:
    """Echoes a header only in the shape cultcache.store.v<digits>; the bytes are the store's."""
    version = header[len(_STORE_FORMAT_PREFIX) + 1:] if header.startswith(_STORE_FORMAT_PREFIX + "v") else ""
    if version.isascii() and version.isdigit():
        return repr(header)
    return f"an unrecognised {_STORE_FORMAT_PREFIX}* header of {len(header.encode('utf-8'))} bytes"


def _decode_snapshot(decoded: Any) -> tuple[str, list[CultCacheEnvelope]] | None:
    if not isinstance(decoded, list) or not decoded:
        return None
    if isinstance(decoded[0], str) and decoded[0].startswith(_STORE_FORMAT_PREFIX) and decoded[0] not in _READABLE_STORE_FORMATS:
        raise ValueError(
            f"CultCache store format {_describe_header(decoded[0])} is not one this runtime reads; "
            f"it reads {STORE_FORMAT_VERSION!r} and {STORE_FORMAT_ELEMENT_IDS!r} only"
        )
    if decoded[0] not in _READABLE_STORE_FORMATS:
        return None
    if len(decoded) != 3:
        raise ValueError(f"CultCache store has {len(decoded)} top-level slots; this runtime reads 3 (header, schema catalog, records)")
    if not isinstance(decoded[1], list) or not isinstance(decoded[2], list):
        raise ValueError("CultCache v1 snapshot must contain a schema catalog and record array")

    # An id names the entry that owns it; only an id no entry owns names the entry that lists it as compatible.
    entries = [_decode_catalog_entry(raw_entry) for raw_entry in decoded[1]]
    catalog_by_schema_id: dict[str, CultCacheSchemaCatalogEntry] = {}
    for entry in entries:
        catalog_by_schema_id.setdefault(entry.schema_id, entry)
    for entry in entries:
        for compatible_schema_id in entry.compatible_schema_ids:
            catalog_by_schema_id.setdefault(compatible_schema_id, entry)

    envelopes: list[CultCacheEnvelope] = []
    for raw_record in decoded[2]:
        if not isinstance(raw_record, list) or len(raw_record) < 4:
            raise ValueError("CultCache persisted records must be MessagePack arrays")
        if len(raw_record) > _PERSISTED_RECORD_SLOTS:
            raise ValueError(
                f"CultCache record {_describe_identity(raw_record[0])} (schema {_describe_identity(raw_record[1])}) "
                f"has {len(raw_record)} slots, more than "
                f"the {_PERSISTED_RECORD_SLOTS} of a {STORE_FORMAT_VERSION} record, so this is not a valid store"
            )
        key, schema_id, stored_at, payload = raw_record
        if not isinstance(key, str) or not key:
            raise ValueError("CultCache persisted records must declare a key")
        if not isinstance(schema_id, str) or not schema_id:
            raise ValueError("CultCache persisted records must declare a schema id")
        if not isinstance(stored_at, str) or not stored_at:
            raise ValueError("CultCache persisted records must declare storedAt")
        catalog_entry = catalog_by_schema_id.get(schema_id)
        if catalog_entry is None:
            raise ValueError(f"CultCache persisted record '{key}' references schema id '{schema_id}', which the store's catalog does not publish")
        normalized_payload = _normalize_payload(payload)
        envelopes.append(
            CultCacheEnvelope(
                key=key,
                type=catalog_entry.schema_name,
                payload=normalized_payload,
                stored_at=stored_at,
                schema_id=schema_id,
                catalog_entry=catalog_entry,
            )
        )
    return decoded[0], envelopes


def _decode_legacy_envelopes(decoded: Any, msgpack: Any) -> list[CultCacheEnvelope]:
    if not isinstance(decoded, list):
        raise ValueError("CultCache MessagePack store is not a recognized snapshot")
    envelopes: list[CultCacheEnvelope] = []
    for item in decoded:
        if not isinstance(item, dict):
            raise ValueError("CultCache legacy envelopes must be MessagePack maps")
        try:
            payload = item["payload"]
            envelopes.append(
                CultCacheEnvelope(
                    key=item["key"],
                    type=item["type"],
                    payload=payload if isinstance(payload, bytes) else msgpack.packb(payload, use_bin_type=True),
                    stored_at=item.get("storedAt", item.get("stored_at")),
                    schema_id=item.get("schemaId", item.get("schema_id")),
                )
            )
        except KeyError as exc:
            raise ValueError(f"Malformed CultCache legacy envelope: missing {exc}") from exc
    return envelopes


def _encode_catalog_entry(entry: CultCacheSchemaCatalogEntry) -> list[Any]:
    return [
        entry.schema_id,
        entry.schema_name,
        entry.schema_version,
        entry.content_hash,
        entry.canonical_schema_json,
        list(entry.compatible_schema_ids or (entry.schema_id,)),
        [_encode_catalog_member(member) for member in entry.members],
    ]


def _decode_catalog_entry(value: Any) -> CultCacheSchemaCatalogEntry:
    if not isinstance(value, list):
        raise ValueError("CultCache schema catalog entries must be MessagePack arrays")
    schema_id = value[0] if len(value) > 0 else ""
    schema_name = value[1] if len(value) > 1 else ""
    schema_version = value[2] if len(value) > 2 else ""
    content_hash = value[3] if len(value) > 3 else ""
    canonical_schema_json = value[4] if len(value) > 4 else ""
    compatible_schema_ids = value[5] if len(value) > 5 else []
    members = value[6] if len(value) > 6 else []
    if not isinstance(schema_id, str) or not schema_id or not isinstance(schema_name, str) or not schema_name:
        raise ValueError("CultCache schema catalog entries must declare schemaId and schemaName")
    if not isinstance(compatible_schema_ids, list) or not all(isinstance(item, str) and item for item in compatible_schema_ids):
        raise ValueError(f'CultCache schema catalog entry "{schema_id}" has invalid compatible schema ids')
    if not isinstance(members, list):
        raise ValueError(f'CultCache schema catalog entry "{schema_id}" has invalid members')
    return CultCacheSchemaCatalogEntry(
        schema_id=schema_id,
        schema_name=schema_name,
        schema_version=schema_version if isinstance(schema_version, str) and schema_version else f"{schema_name}.v1",
        content_hash=content_hash if isinstance(content_hash, str) and content_hash else schema_id,
        canonical_schema_json=canonical_schema_json if isinstance(canonical_schema_json, str) else "",
        compatible_schema_ids=tuple(compatible_schema_ids),
        members=tuple(_decode_catalog_member(member) for member in members),
    )


def _encode_catalog_member(member: CultCacheSchemaCatalogMember) -> list[Any]:
    return [
        member.slot,
        member.member_name,
        member.type_name,
        member.is_reference,
        member.is_many,
        member.target_schema_name,
        member.is_name,
        member.index_alias,
    ]


def _decode_catalog_member(value: Any) -> CultCacheSchemaCatalogMember:
    if not isinstance(value, list):
        raise ValueError("CultCache schema catalog members must be MessagePack arrays")
    slot = value[0] if len(value) > 0 else -1
    member_name = value[1] if len(value) > 1 else ""
    type_name = value[2] if len(value) > 2 else ""
    if not isinstance(slot, int) or slot < 0 or not isinstance(member_name, str) or not member_name:
        raise ValueError("CultCache schema catalog member has invalid slot or name")
    if not isinstance(type_name, str) or not type_name:
        raise ValueError("CultCache schema catalog member has invalid type")
    return CultCacheSchemaCatalogMember(
        slot=slot,
        member_name=member_name,
        type_name=type_name,
        is_reference=(value[3] if len(value) > 3 else False) is True,
        is_many=(value[4] if len(value) > 4 else False) is True,
        target_schema_name=value[5] if len(value) > 5 and isinstance(value[5], str) else None,
        is_name=(value[6] if len(value) > 6 else False) is True,
        index_alias=value[7] if len(value) > 7 and isinstance(value[7], str) else None,
    )


def _schema_id_for(envelope: CultCacheEnvelope) -> str:
    return envelope.schema_id or envelope.type


def _default_catalog_entry(envelope: CultCacheEnvelope) -> CultCacheSchemaCatalogEntry:
    schema_id = _schema_id_for(envelope)
    return CultCacheSchemaCatalogEntry(
        schema_id=schema_id,
        schema_name=envelope.type,
        schema_version=f"{envelope.type}.v1",
        content_hash=schema_id,
        canonical_schema_json=json.dumps(
            {"schemaName": envelope.type, "schemaVersion": f"{envelope.type}.v1", "members": []},
            separators=(",", ":"),
        ),
        compatible_schema_ids=(schema_id,),
    )


def _normalize_payload(payload: Any) -> bytes:
    if isinstance(payload, bytes):
        return payload
    if isinstance(payload, bytearray):
        return bytes(payload)
    if isinstance(payload, list) and all(isinstance(item, int) and 0 <= item <= 255 for item in payload):
        return bytes(payload)
    raise ValueError("CultCache record payload must be binary MessagePack bytes")
