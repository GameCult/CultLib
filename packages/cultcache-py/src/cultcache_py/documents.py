from __future__ import annotations

import json
from dataclasses import MISSING, dataclass, field
from typing import Any, Callable, Generic, Iterable, TypeVar

from .backing_store import CultCacheSchemaCatalogEntry, CultCacheSchemaCatalogMember

T = TypeVar("T")

Encoder = Callable[[T], Any]
Decoder = Callable[[Any], T]
Validator = Callable[[T], None]
Extractor = Callable[[T], str | int | float | bool | None]


def _default_encode(value: Any) -> Any:
    if hasattr(value, "model_dump"):
        return value.model_dump()
    if hasattr(value, "to_dict"):
        return value.to_dict()
    if hasattr(value, "__dict__"):
        return dict(value.__dict__)
    return value


def _default_decode(value: Any) -> Any:
    return value


def _json_payload_encoder(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True).encode("utf-8")


def _json_payload_decoder(payload: bytes) -> Any:
    return json.loads(payload.decode("utf-8"))


def _messagepack_payload_encoder(value: Any) -> bytes:
    try:
        import msgpack  # type: ignore
    except ModuleNotFoundError as exc:
        raise RuntimeError(
            "MessagePack payload encoding requires the optional 'msgpack' dependency. "
            "Install with: python -m pip install cultcache-py[msgpack]"
        ) from exc
    return msgpack.packb(value, use_bin_type=True)


def _messagepack_payload_decoder(payload: bytes) -> Any:
    try:
        import msgpack  # type: ignore
    except ModuleNotFoundError as exc:
        raise RuntimeError(
            "MessagePack payload decoding requires the optional 'msgpack' dependency. "
            "Install with: python -m pip install cultcache-py[msgpack]"
        ) from exc
    return msgpack.unpackb(payload, raw=False)


@dataclass(frozen=True)
class DatabaseEntryField:
    name: str
    key: int
    default: Any = MISSING


def database_entry_field(name: str, key: int, *, default: Any = MISSING) -> DatabaseEntryField:
    if key < 0:
        raise ValueError("DatabaseEntry field keys must be non-negative integers")
    return DatabaseEntryField(name=name, key=key, default=default)


@dataclass(frozen=True)
class DocumentDefinition(Generic[T]):
    type: str
    encode: Encoder[T] = _default_encode
    decode: Decoder[T] = _default_decode
    validate: Validator[T] | None = None
    global_document: bool = False
    name: str | Extractor[T] | None = None
    indexes: dict[str, str | Extractor[T]] = field(default_factory=dict)
    payload_encoder: Callable[[Any], bytes] = _json_payload_encoder
    payload_decoder: Callable[[bytes], Any] = _json_payload_decoder
    schema_id: str | None = None
    schema_name: str | None = None
    schema_version: str | None = None
    content_hash: str | None = None
    canonical_schema_json: str | None = None
    compatible_schema_ids: tuple[str, ...] = field(default_factory=tuple)
    members: tuple[CultCacheSchemaCatalogMember, ...] = field(default_factory=tuple)

    def encode_payload(self, value: T) -> bytes:
        if self.validate:
            self.validate(value)
        return self.payload_encoder(self.encode(value))

    def decode_payload(self, payload: bytes) -> T:
        value = self.decode(self.payload_decoder(payload))
        if self.validate:
            self.validate(value)
        return value

    def catalog_entry(self) -> CultCacheSchemaCatalogEntry:
        schema_id = self.schema_id or self.type
        schema_name = self.schema_name or self.type
        schema_version = self.schema_version or f"{schema_name}.v1"
        canonical_schema_json = self.canonical_schema_json or json.dumps(
            {
                "schemaName": schema_name,
                "schemaVersion": schema_version,
                "members": [
                    {
                        "slot": member.slot,
                        "name": member.member_name,
                        "type": member.type_name,
                        "isReference": member.is_reference,
                        "many": member.is_many,
                        "targetSchemaName": member.target_schema_name,
                        "indexAlias": member.index_alias,
                        "isName": member.is_name,
                    }
                    for member in self.members
                ],
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )
        compatible_schema_ids = self.compatible_schema_ids or (schema_id,)
        return CultCacheSchemaCatalogEntry(
            schema_id=schema_id,
            schema_name=schema_name,
            schema_version=schema_version,
            content_hash=self.content_hash or schema_id,
            canonical_schema_json=canonical_schema_json,
            compatible_schema_ids=tuple(compatible_schema_ids),
            members=tuple(self.members),
        )


def define_document_type(
    type: str,
    *,
    encode: Encoder[T] = _default_encode,
    decode: Decoder[T] = _default_decode,
    validate: Validator[T] | None = None,
    global_document: bool = False,
    name: str | Extractor[T] | None = None,
    indexes: dict[str, str | Extractor[T]] | None = None,
    payload_encoder: Callable[[Any], bytes] = _json_payload_encoder,
    payload_decoder: Callable[[bytes], Any] = _json_payload_decoder,
    schema_id: str | None = None,
    schema_name: str | None = None,
    schema_version: str | None = None,
    content_hash: str | None = None,
    canonical_schema_json: str | None = None,
    compatible_schema_ids: Iterable[str] | None = None,
    members: Iterable[CultCacheSchemaCatalogMember] | None = None,
) -> DocumentDefinition[T]:
    return DocumentDefinition(
        type=type,
        encode=encode,
        decode=decode,
        validate=validate,
        global_document=global_document,
        name=name,
        indexes=indexes or {},
        payload_encoder=payload_encoder,
        payload_decoder=payload_decoder,
        schema_id=schema_id,
        schema_name=schema_name,
        schema_version=schema_version,
        content_hash=content_hash,
        canonical_schema_json=canonical_schema_json,
        compatible_schema_ids=tuple(compatible_schema_ids or ()),
        members=tuple(members or ()),
    )


def define_database_entry_type(
    type: str,
    fields: Iterable[DatabaseEntryField | tuple[str, int] | tuple[str, int, Any]],
    *,
    cls: type[T] | None = None,
    validate: Validator[T] | None = None,
    global_document: bool = False,
    name: str | Extractor[T] | None = None,
    indexes: dict[str, str | Extractor[T]] | None = None,
    schema_id: str | None = None,
    schema_name: str | None = None,
    schema_version: str | None = None,
    content_hash: str | None = None,
    canonical_schema_json: str | None = None,
    compatible_schema_ids: Iterable[str] | None = None,
) -> DocumentDefinition[T]:
    """Define a Rust/C#-style DatabaseEntry payload formatter.

    Payloads are MessagePack arrays indexed by explicit field keys. Missing
    slots are written as nil, so deleted fields can leave their slot reserved
    and newly added fields can use higher keys without rewriting older payloads.
    """

    normalized = [_normalize_database_entry_field(field) for field in fields]
    if not normalized:
        raise ValueError("DatabaseEntry document types require at least one field")
    keys = [field.key for field in normalized]
    if len(keys) != len(set(keys)):
        raise ValueError(f"DatabaseEntry document type {type!r} has duplicate field keys")
    normalized.sort(key=lambda field: field.key)
    max_key = max(keys)
    member_name_lookup = name if isinstance(name, str) else None
    index_lookup = indexes or {}
    members = tuple(
        CultCacheSchemaCatalogMember(
            slot=field.key,
            member_name=_pascal_case(field.name),
            type_name="System.Object",
            is_name=field.name == member_name_lookup,
            index_alias=next(
                (alias for alias, extractor in index_lookup.items() if extractor == field.name),
                None,
            ),
        )
        for field in normalized
    )

    def encode(value: Any) -> list[Any]:
        slots = [None] * (max_key + 1)
        for field in normalized:
            slots[field.key] = extract_value(value, field.name)
        return slots

    def decode(raw: Any) -> Any:
        if not isinstance(raw, list):
            raise ValueError(f"DatabaseEntry document type {type!r} expected a MessagePack array")
        values: dict[str, Any] = {}
        for field in normalized:
            if field.key < len(raw):
                value = raw[field.key]
            elif field.default is not MISSING:
                value = field.default() if callable(field.default) else field.default
            else:
                raise ValueError(
                    f"DatabaseEntry document type {type!r} is missing required slot {field.key}"
                )
            if value is None and field.default is not MISSING:
                value = field.default() if callable(field.default) else field.default
            values[field.name] = value
        if cls is None:
            return values
        return cls(**values)

    return define_document_type(
        type,
        encode=encode,
        decode=decode,
        validate=validate,
        global_document=global_document,
        name=name,
        indexes=indexes,
        payload_encoder=_messagepack_payload_encoder,
        payload_decoder=_messagepack_payload_decoder,
        schema_id=schema_id,
        schema_name=schema_name,
        schema_version=schema_version,
        content_hash=content_hash,
        canonical_schema_json=canonical_schema_json,
        compatible_schema_ids=compatible_schema_ids,
        members=members,
    )


def define_document_registry(*documents: DocumentDefinition[Any]) -> tuple[DocumentDefinition[Any], ...]:
    return tuple(documents)


def extract_value(value: Any, extractor: str | Extractor[Any]) -> str | int | float | bool | None:
    if callable(extractor):
        return extractor(value)
    if isinstance(value, dict):
        return value.get(extractor)
    return getattr(value, extractor, None)


def _normalize_database_entry_field(
    field: DatabaseEntryField | tuple[str, int] | tuple[str, int, Any],
) -> DatabaseEntryField:
    if isinstance(field, DatabaseEntryField):
        return field
    if len(field) == 2:
        return database_entry_field(field[0], field[1])
    if len(field) == 3:
        return database_entry_field(field[0], field[1], default=field[2])
    raise ValueError("DatabaseEntry field tuples must be (name, key) or (name, key, default)")


def _pascal_case(name: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in name.split("_") if part)
