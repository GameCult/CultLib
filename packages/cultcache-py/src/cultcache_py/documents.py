from __future__ import annotations

import json
from dataclasses import MISSING, dataclass, field
from typing import Any, Callable, Generic, Iterable, Mapping, TypeVar

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
class SlotCodec:
    """Encodes and decodes the value of one slot: a nested record or a union."""

    encode: Callable[[Any], Any]
    decode: Callable[[Any], Any]


@dataclass(frozen=True)
class DatabaseEntryField:
    name: str
    key: int
    default: Any = MISSING
    codec: SlotCodec | Callable[[], SlotCodec] | None = None
    many: bool = False


def database_entry_field(
    name: str,
    key: int,
    *,
    default: Any = MISSING,
    codec: SlotCodec | Callable[[], SlotCodec] | None = None,
    many: bool = False,
) -> DatabaseEntryField:
    """A slot. `codec` makes the slot a nested record or union (a zero-argument callable
    returning the codec lets a union refer to itself); `many` makes it a list of them."""
    if key < 0:
        raise ValueError("DatabaseEntry field keys must be non-negative integers")
    if many and codec is None:
        raise ValueError("A many DatabaseEntry field requires a codec")
    return DatabaseEntryField(name=name, key=key, default=default, codec=codec, many=many)


@dataclass(frozen=True)
class UnionArm:
    """A decoded union value: the arm's declared name and its decoded record (None for a unit arm)."""

    name: str
    value: Any = None


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
    _catalog_entry: CultCacheSchemaCatalogEntry | None = field(default=None, init=False, repr=False, compare=False)

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
        if self._catalog_entry is not None:
            return self._catalog_entry
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
        catalog_entry = CultCacheSchemaCatalogEntry(
            schema_id=schema_id,
            schema_name=schema_name,
            schema_version=schema_version,
            content_hash=self.content_hash or schema_id,
            canonical_schema_json=canonical_schema_json,
            compatible_schema_ids=tuple(compatible_schema_ids),
            members=tuple(self.members),
        )
        object.__setattr__(self, "_catalog_entry", catalog_entry)
        return catalog_entry


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

    normalized = _normalize_fields(type, fields)
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

    record = _record_codec(type, normalized, cls)

    return define_document_type(
        type,
        encode=record.encode,
        decode=record.decode,
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


def define_record_codec(
    name: str,
    fields: Iterable[DatabaseEntryField | tuple[str, int] | tuple[str, int, Any]],
    *,
    cls: type[Any] | None = None,
) -> SlotCodec:
    """A nested record: the same slot array as a document payload, usable as a field's codec or a union arm."""
    return _record_codec(name, _normalize_fields(name, fields), cls)


def define_union(
    name: str,
    arms: Mapping[int, tuple[str, SlotCodec | Callable[[], SlotCodec] | None]],
) -> SlotCodec:
    """A union: nil, or `[armKey, [armSlots...]]`. Each arm is `(arm name, record codec)`; a unit arm has
    no codec and is written `[armKey, []]`. A key the union does not declare is refused, never carried."""
    by_key = dict(arms)
    by_name = {arm_name: (key, codec) for key, (arm_name, codec) in by_key.items()}
    if not by_key:
        raise ValueError(f"Union {name!r} requires at least one arm")
    if len(by_name) != len(by_key):
        raise ValueError(f"Union {name!r} has duplicate arm names")
    if any(not _is_key(key) or key < 0 for key in by_key):
        raise ValueError(f"Union {name!r} arm keys must be non-negative integers")

    def encode(value: Any) -> Any:
        if value is None:
            return None
        if not isinstance(value, UnionArm) or value.name not in by_name:
            raise ValueError(f"Union {name!r} was given a value that is not one of its arms")
        key, codec = by_name[value.name]
        if codec is None:
            if value.value is not None:
                raise ValueError(f"Union {name!r} arm {key} is a unit arm and carries no value")
            return [key, []]
        return [key, _resolve(codec).encode(value.value)]

    def decode(raw: Any) -> Any:
        if raw is None:
            return None
        if not isinstance(raw, list) or len(raw) != 2 or not isinstance(raw[1], list):
            raise ValueError(f"Union {name!r} value is malformed: expected [armKey, [armSlots]]")
        key = raw[0]
        if not _is_key(key):
            raise ValueError(f"Union {name!r} value is malformed: the arm key is not an integer")
        if key not in by_key:
            declared = ", ".join(str(declared_key) for declared_key in sorted(by_key))
            raise ValueError(f"Union {name!r} does not declare arm key {key} (declared: {declared})")
        arm_name, codec = by_key[key]
        return UnionArm(arm_name, None if codec is None else _resolve(codec).decode(raw[1]))

    return SlotCodec(encode=encode, decode=decode)


def define_document_registry(*documents: DocumentDefinition[Any]) -> tuple[DocumentDefinition[Any], ...]:
    return tuple(documents)


def extract_value(value: Any, extractor: str | Extractor[Any]) -> str | int | float | bool | None:
    if callable(extractor):
        return extractor(value)
    if isinstance(value, dict):
        return value.get(extractor)
    return getattr(value, extractor, None)


def _is_key(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _resolve(codec: SlotCodec | Callable[[], SlotCodec]) -> SlotCodec:
    return codec if isinstance(codec, SlotCodec) else codec()


def _normalize_fields(
    type: str,
    fields: Iterable[DatabaseEntryField | tuple[str, int] | tuple[str, int, Any]],
) -> list[DatabaseEntryField]:
    normalized = [_normalize_database_entry_field(field) for field in fields]
    if not normalized:
        raise ValueError("DatabaseEntry document types require at least one field")
    keys = [field.key for field in normalized]
    if len(keys) != len(set(keys)):
        raise ValueError(f"DatabaseEntry document type {type!r} has duplicate field keys")
    return sorted(normalized, key=lambda field: field.key)


def _record_codec(type: str, normalized: list[DatabaseEntryField], cls: type[Any] | None) -> SlotCodec:
    max_key = normalized[-1].key

    def encode_slot(field: DatabaseEntryField, value: Any) -> Any:
        if field.codec is None or value is None:
            return value
        codec = _resolve(field.codec)
        if field.many:
            return [None if item is None else codec.encode(item) for item in value]
        return codec.encode(value)

    def decode_slot(field: DatabaseEntryField, value: Any) -> Any:
        if field.codec is None or value is None:
            return value
        codec = _resolve(field.codec)
        if field.many:
            if not isinstance(value, list):
                raise ValueError(f"DatabaseEntry document type {type!r} slot {field.key} expected a MessagePack array")
            return [None if item is None else codec.decode(item) for item in value]
        return codec.decode(value)

    def encode(value: Any) -> list[Any]:
        slots = [None] * (max_key + 1)
        for field in normalized:
            slots[field.key] = encode_slot(field, extract_value(value, field.name))
        return slots

    def decode(raw: Any) -> Any:
        if not isinstance(raw, list):
            raise ValueError(f"DatabaseEntry document type {type!r} expected a MessagePack array")
        values: dict[str, Any] = {}
        for field in normalized:
            if field.key < len(raw):
                value = decode_slot(field, raw[field.key])
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

    return SlotCodec(encode=encode, decode=decode)


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
