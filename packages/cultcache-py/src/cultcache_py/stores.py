from __future__ import annotations

import base64
import json
import os
import threading
from pathlib import Path
from typing import Any

from .backing_store import (
    CultCacheEnvelope,
    CultCacheSchemaCatalogEntry,
    CultCacheSchemaCatalogMember,
)

STORE_FORMAT_VERSION = "cultcache.store.v1"


class JsonLinesBackingStore:
    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = Path(path)
        self._lock = threading.RLock()

    def pull_all(self) -> list[CultCacheEnvelope]:
        with self._lock:
            if not self.path.exists():
                return []
            envelopes: list[CultCacheEnvelope] = []
            for line_number, line in enumerate(self.path.read_text(encoding="utf-8").splitlines(), start=1):
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

    def pull_all(self) -> list[CultCacheEnvelope]:
        msgpack = self._msgpack()
        with self._lock:
            if not self.path.exists():
                return []
            data = self.path.read_bytes()
            if not data:
                return []
            decoded = msgpack.unpackb(data, raw=False)
            snapshot = _decode_v1_snapshot(decoded)
            if snapshot is not None:
                return snapshot
            return _decode_legacy_envelopes(decoded, msgpack)

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
        msgpack = self._msgpack()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(self.path.suffix + ".tmp")
        temp.write_bytes(msgpack.packb(_encode_v1_snapshot(envelopes), use_bin_type=True))
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


def _encode_v1_snapshot(envelopes: list[CultCacheEnvelope]) -> list[Any]:
    catalog_by_schema_id: dict[str, CultCacheSchemaCatalogEntry] = {}
    for envelope in envelopes:
        schema_id = _schema_id_for(envelope)
        if schema_id not in catalog_by_schema_id:
            catalog_by_schema_id[schema_id] = envelope.catalog_entry or _default_catalog_entry(envelope)

    catalog = [
        _encode_catalog_entry(entry)
        for entry in sorted(catalog_by_schema_id.values(), key=lambda item: item.schema_name)
    ]
    records = [
        [envelope.key, _schema_id_for(envelope), envelope.stored_at, envelope.payload]
        for envelope in sorted(envelopes, key=lambda item: item.key)
    ]
    return [STORE_FORMAT_VERSION, catalog, records]


def _decode_v1_snapshot(decoded: Any) -> list[CultCacheEnvelope] | None:
    if not isinstance(decoded, list) or not decoded or decoded[0] != STORE_FORMAT_VERSION:
        return None
    if len(decoded) < 3 or not isinstance(decoded[1], list) or not isinstance(decoded[2], list):
        raise ValueError("CultCache v1 snapshot must contain a schema catalog and record array")

    catalog_by_schema_id: dict[str, CultCacheSchemaCatalogEntry] = {}
    for raw_entry in decoded[1]:
        entry = _decode_catalog_entry(raw_entry)
        catalog_by_schema_id[entry.schema_id] = entry
        for compatible_schema_id in entry.compatible_schema_ids:
            catalog_by_schema_id[compatible_schema_id] = entry

    envelopes: list[CultCacheEnvelope] = []
    for raw_record in decoded[2]:
        if not isinstance(raw_record, list) or len(raw_record) < 4:
            raise ValueError("CultCache persisted records must be MessagePack arrays")
        key, schema_id, stored_at, payload = raw_record[:4]
        if not isinstance(key, str) or not key:
            raise ValueError("CultCache persisted records must declare a key")
        if not isinstance(schema_id, str) or not schema_id:
            raise ValueError("CultCache persisted records must declare a schema id")
        if not isinstance(stored_at, str) or not stored_at:
            raise ValueError("CultCache persisted records must declare storedAt")
        catalog_entry = catalog_by_schema_id.get(schema_id)
        if catalog_entry is None:
            raise ValueError(f'CultCache persisted record "{key}" references missing schema id "{schema_id}"')
        envelopes.append(
            CultCacheEnvelope(
                key=key,
                type=catalog_entry.schema_name,
                payload=_normalize_payload(payload),
                stored_at=stored_at,
                schema_id=schema_id,
                catalog_entry=catalog_entry,
            )
        )
    return envelopes


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
