from __future__ import annotations

import json
from dataclasses import MISSING, dataclass, field
from collections.abc import Mapping, Sequence, Set
from itertools import chain
from typing import Any, Callable, Generic, Iterable, TypeVar

import msgpack

from .backing_store import CultCacheSchemaCatalogEntry, CultCacheSchemaCatalogMember
from .leaves import Float32, SlotCodec
from .leaves import any as _any_leaf

T = TypeVar("T")

Encoder = Callable[[T], Any]
Decoder = Callable[[Any], T]
Validator = Callable[[T], None]
Extractor = Callable[[T], str | int | float | bool | None]

MAX_PAYLOAD_DEPTH = 256
"""The payload nesting limit, equal to the C# reference's `MaximumPayloadDepth`: every array and map counts, read
or skipped, the outermost one is depth 1; depth 256 is accepted and 257 refused, on read and on write."""

MAX_SLOT_KEY = 65535
"""The largest slot key a record may define: the largest MessagePack array16 length."""


_CONTAINERS = (Mapping, Sequence, Set)
_SCALAR_SEQUENCES = (str, bytes, bytearray, memoryview)


class CultCachePayloadDepthError(ValueError):
    """A payload nests deeper than `MAX_PAYLOAD_DEPTH`. Names the type, the depth reached and the limit; never the payload."""

    def __init__(self, type_name: str, direction: str) -> None:
        self.depth = MAX_PAYLOAD_DEPTH + 1
        self.limit = MAX_PAYLOAD_DEPTH
        super().__init__(
            f"{type_name} payload nests {self.depth} containers deep; the limit is {self.limit} (refused on {direction})."
        )


class PayloadCodecFault(ValueError):
    """A codec raises this for a payload its format cannot carry. `code` names the rule (the library's are
    `map_key_kind`, `duplicate_map_key` and `integer_out_of_range`) and `kind` the kind of node at fault (a MessagePack
    kind such as `nil` or `bin`); neither is ever a payload value. A consumer-built codec may raise it."""

    def __init__(self, code: str, kind: str = "") -> None:
        self.code = code
        self.kind = kind
        super().__init__(code)


class CultCachePayloadFormatError(ValueError):
    """A payload is not one value of its definition's codec (read), or holds a key or integer the codec cannot carry.
    Names the type, the codec, the direction and a `code`; `detail` is the fault's class or the key's kind. Never a
    payload byte or value."""

    def __init__(self, type_name: str, codec: str, direction: str, code: str, detail: str) -> None:
        self.type_name = type_name
        self.codec = codec
        self.direction = direction
        self.code = code
        if code == "not_one_value":
            message = f"{type_name} payload is not one {codec} value ({detail}); refused on read."
        elif code == "map_key_kind":
            message = (
                f"{type_name} payload has a map key of kind {detail}; "
                f"{codec} payloads take str, int, float, bool or timestamp keys (refused on {direction})."
            )
        elif code == "duplicate_map_key":
            message = f"{type_name} payload has two map keys Python holds as one; refused on read."
        elif code == "integer_out_of_range":
            message = f"{type_name} payload has an integer beyond 64 bits; refused on write."
        else:
            message = f"{type_name} payload is refused by the {codec} codec ({code}); refused on {direction}."
        super().__init__(message)


def _check_payload_tree(tree: Any, type_name: str, direction: str) -> None:
    """The one depth walk over a payload tree, for every codec. Iterative (every mapping, sequence and set that is not
    text or bytes is a container): refuses the first container past `MAX_PAYLOAD_DEPTH`."""
    stack = [(tree, 1)] if _is_container(tree) else []
    while stack:
        node, depth = stack.pop()
        if depth > MAX_PAYLOAD_DEPTH:
            raise CultCachePayloadDepthError(type_name, direction)
        children = chain(node.keys(), node.values()) if isinstance(node, Mapping) else node
        stack.extend((child, depth + 1) for child in children if _is_container(child))


def _is_container(node: Any) -> bool:
    return isinstance(node, _CONTAINERS) and not isinstance(node, _SCALAR_SEQUENCES)


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


def _pack_tree(tree: Any) -> bytes:
    """The bytes of the tree the leaves produced. `packb` writes everything but a `Float32`, which it refuses with a
    TypeError; only then does an iterative walk pack each array and map header and each value, a `Float32` through a
    single-float packer."""
    try:
        return msgpack.packb(tree, use_bin_type=True)  # type: ignore[no-any-return]
    except TypeError:
        pass
    plain = msgpack.Packer(use_bin_type=True)
    single = msgpack.Packer(use_bin_type=True, use_single_float=True)
    out: list[bytes] = []
    stack = [tree]
    while stack:
        node = stack.pop()
        if isinstance(node, Float32):
            out.append(single.pack(node.value))
        elif isinstance(node, (list, tuple)):
            out.append(plain.pack_array_header(len(node)))
            stack.extend(reversed(node))
        elif isinstance(node, dict):
            out.append(plain.pack_map_header(len(node)))
            stack.extend(reversed([part for pair in node.items() for part in pair]))
        else:
            out.append(plain.pack(node))
    return b"".join(out)


@dataclass(frozen=True)
class PayloadCodec:
    """A codec owns one payload format: how a tree becomes bytes and back, which of its decode faults mean the payload
    nests past the interpreter's own stack, and which trees it can carry: it raises `PayloadCodecFault` for a key or an
    integer its format cannot hold, and nothing it writes is refused by its own `decode`. A consumer-built codec owns
    the depth of nodes the depth walk cannot see (anything that is not a Mapping, Sequence or Set)."""

    name: str
    encode: Callable[[Any], bytes]
    decode: Callable[[bytes], Any]
    depth_faults: tuple[type[BaseException], ...] = ()


_INTEGER_RANGE = (-(2**63), 2**64 - 1)
_KEY_TYPES = (str, int, float, Float32, msgpack.Timestamp)


def _node_kind(node: Any) -> str:
    """The MessagePack kind of a node, for a refusal: a kind, never a value."""
    if node is None:
        return "nil"
    if isinstance(node, (bytes, bytearray, memoryview)):
        return "bin"
    if isinstance(node, Mapping):
        return "map"
    if isinstance(node, msgpack.ExtType):
        return "ext"
    if isinstance(node, _CONTAINERS):
        return "array"
    return "unsupported"


def _checked_map(pairs: list[tuple[Any, Any]]) -> dict[Any, Any]:
    """The read hook of every MessagePack map: the keys the C# reference's object reader accepts (str, int, float,
    bool, timestamp) and no duplicate."""
    for key, _ in pairs:
        if not isinstance(key, _KEY_TYPES):
            raise PayloadCodecFault("map_key_kind", _node_kind(key))
    held = dict(pairs)
    if len(held) != len(pairs):
        raise PayloadCodecFault("duplicate_map_key")
    return held


def _check_writable(tree: Any) -> None:
    """The write side of the key and integer rule: what `_checked_map` refuses on read is refused here, so nothing
    this codec writes is refused by its own decode. Iterative; it stops at the depth limit, which the definition rules."""
    stack = [(tree, 1)]
    while stack:
        node, depth = stack.pop()
        if depth > MAX_PAYLOAD_DEPTH + 1:
            raise RecursionError("payload nests past the depth limit")
        if isinstance(node, int) and not _INTEGER_RANGE[0] <= node <= _INTEGER_RANGE[1]:
            raise PayloadCodecFault("integer_out_of_range")
        if isinstance(node, Mapping):
            for key in node:
                if not isinstance(key, _KEY_TYPES):
                    raise PayloadCodecFault("map_key_kind", _node_kind(key))
            stack.extend((child, depth + 1) for child in chain(node.keys(), node.values()))
        elif isinstance(node, (list, tuple)):
            stack.extend((child, depth + 1) for child in node)


def _unpack_tree(payload: bytes) -> Any:
    return msgpack.unpackb(payload, raw=False, strict_map_key=False, object_pairs_hook=_checked_map)


def _pack_checked_tree(tree: Any) -> bytes:
    _check_writable(tree)
    return _pack_tree(tree)


def _dump_json(tree: Any) -> bytes:
    return json.dumps(tree, ensure_ascii=False, sort_keys=True).encode("utf-8")


def _load_json(payload: bytes) -> Any:
    return json.loads(payload.decode("utf-8"))


MESSAGEPACK_PAYLOAD_CODEC = PayloadCodec(
    "MessagePack", _pack_checked_tree, _unpack_tree, depth_faults=(msgpack.exceptions.StackError,)
)
"""The default payload format. It carries what the C# reference's object reader accepts: str, int, float, bool and
timestamp keys, no duplicate key, and integers within -(2**63)..2**64-1. It refuses the rest on read through its map
hook and on write through its encode, so it never writes a record it cannot read."""

JSON_PAYLOAD_CODEC = PayloadCodec("JSON", _dump_json, _load_json, depth_faults=(RecursionError,))
"""The 0.3 default, kept for stores written before MessagePack: it writes exactly the 0.3 bytes. Never a default."""


@dataclass(frozen=True)
class DatabaseEntryField:
    name: str
    key: int
    default: Any = MISSING
    codec: SlotCodec = _any_leaf

    def __post_init__(self) -> None:
        if not _is_key(self.key) or not 0 <= self.key <= MAX_SLOT_KEY:
            raise ValueError(f"DatabaseEntry field keys must be integers within 0..{MAX_SLOT_KEY}")
        if not isinstance(self.codec, SlotCodec):
            raise ValueError("DatabaseEntry field codec must be a SlotCodec: a leaf from cultcache_py.leaves, or leaves.lazy")


def database_entry_field(
    name: str,
    key: int,
    *,
    default: Any = MISSING,
    codec: SlotCodec = _any_leaf,
) -> DatabaseEntryField:
    """A slot. `codec` is the slot's leaf (cultcache_py.leaves, a record or a union); with none the slot is untyped,
    an `object` in C#. A `default` fills a slot the payload is too short to have, never a nil."""
    return DatabaseEntryField(name=name, key=key, default=default, codec=codec)


@dataclass(frozen=True)
class UnionArm:
    """A decoded union value: the arm's declared name and its decoded record (None for a unit arm)."""

    name: str
    value: Any = None


def _fault_refusal(type_name: str, codec: PayloadCodec, direction: str, fault: PayloadCodecFault) -> CultCachePayloadFormatError:
    return CultCachePayloadFormatError(type_name, codec.name, direction, fault.code, fault.kind)


@dataclass(frozen=True)
class DocumentDefinition(Generic[T]):
    type: str
    encode: Encoder[T] = _default_encode
    decode: Decoder[T] = _default_decode
    validate: Validator[T] | None = None
    global_document: bool = False
    name: str | Extractor[T] | None = None
    indexes: dict[str, str | Extractor[T]] = field(default_factory=dict)
    payload_codec: PayloadCodec = MESSAGEPACK_PAYLOAD_CODEC
    schema_id: str | None = None
    schema_name: str | None = None
    schema_version: str | None = None
    content_hash: str | None = None
    canonical_schema_json: str | None = None
    compatible_schema_ids: tuple[str, ...] = field(default_factory=tuple)
    members: tuple[CultCacheSchemaCatalogMember, ...] | Callable[[], tuple[CultCacheSchemaCatalogMember, ...]] = field(
        default_factory=tuple
    )
    """The catalog rows, or a function returning them: a document made of leaves derives them when the catalog is first
    built, so a `lazy` leaf can name a definition made later."""
    _catalog_entry: CultCacheSchemaCatalogEntry | None = field(default=None, init=False, repr=False, compare=False)

    def encode_payload(self, value: T) -> bytes:
        if self.validate:
            self.validate(value)
        tree = self.encode(value)
        _check_payload_tree(tree, self.type, "write")
        codec = self.payload_codec
        try:
            return codec.encode(tree)
        except PayloadCodecFault as fault:
            refusal = _fault_refusal(self.type, codec, "write", fault)
        raise refusal

    def decode_payload(self, payload: bytes) -> T:
        codec = self.payload_codec
        try:
            tree = codec.decode(payload)
        except codec.depth_faults:
            refusal: Exception = CultCachePayloadDepthError(self.type, "read")
        except PayloadCodecFault as fault:
            refusal = _fault_refusal(self.type, codec, "read", fault)
        except ValueError as fault:
            refusal = CultCachePayloadFormatError(self.type, codec.name, "read", "not_one_value", type(fault).__name__)
        else:
            _check_payload_tree(tree, self.type, "read")
            value = self.decode(tree)
            if self.validate:
                self.validate(value)
            return value
        raise refusal

    def catalog_entry(self) -> CultCacheSchemaCatalogEntry:
        if self._catalog_entry is not None:
            return self._catalog_entry
        members = tuple(self.members() if callable(self.members) else self.members)
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
                    for member in members
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
            members=members,
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
    payload_codec: PayloadCodec | None = None,
    schema_id: str | None = None,
    schema_name: str | None = None,
    schema_version: str | None = None,
    content_hash: str | None = None,
    canonical_schema_json: str | None = None,
    compatible_schema_ids: Iterable[str] | None = None,
    members: Iterable[CultCacheSchemaCatalogMember] | Callable[[], tuple[CultCacheSchemaCatalogMember, ...]] | None = None,
) -> DocumentDefinition[T]:
    """Define a document type. Its payload is the MessagePack of what `encode` returns; pass
    `payload_codec=JSON_PAYLOAD_CODEC` to read a store written by cultcache-py 0.3 or earlier, and `migrate_payloads` to
    rewrite it."""
    codec = {} if payload_codec is None else {"payload_codec": payload_codec}
    return DocumentDefinition(
        type=type,
        encode=encode,
        decode=decode,
        validate=validate,
        global_document=global_document,
        name=name,
        indexes=indexes or {},
        **codec,
        schema_id=schema_id,
        schema_name=schema_name,
        schema_version=schema_version,
        content_hash=content_hash,
        canonical_schema_json=canonical_schema_json,
        compatible_schema_ids=tuple(compatible_schema_ids or ()),
        members=members if callable(members) else tuple(members or ()),
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

    def members() -> tuple[CultCacheSchemaCatalogMember, ...]:
        return tuple(
            CultCacheSchemaCatalogMember(
                slot=field.key,
                member_name=_pascal_case(field.name),
                type_name=field.codec.type_name or _any_leaf.type_name,
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
    type_name: str | None = None,
) -> SlotCodec:
    """A nested record: the same slot array as a document payload, usable as a field's codec or a union arm.
    `type_name` is the C# FullName of the class it mirrors, for the catalog; without it the catalog names the slot as an untyped one."""
    return _record_codec(name, _normalize_fields(name, fields), cls, type_name)


def define_union(
    name: str,
    arms: Mapping[int, tuple[str, SlotCodec | None]],
    *,
    type_name: str | None = None,
) -> SlotCodec:
    """A union: `[armKey, [armSlots...]]`. Each arm is `(arm name, record codec)`; a unit arm has no codec and is
    written `[armKey, []]`. A key the union does not declare is refused, never carried, and so is nil: wrap the union
    in `leaves.nullable` for a slot that may be null. `type_name` is the C# FullName of the interface, for the catalog."""
    by_key = dict(arms)
    by_name = {arm_name: (key, codec) for key, (arm_name, codec) in by_key.items()}
    if not by_key:
        raise ValueError(f"Union {name!r} requires at least one arm")
    if len(by_name) != len(by_key):
        raise ValueError(f"Union {name!r} has duplicate arm names")
    if any(not _is_key(key) or not _INT32_MIN <= key <= _INT32_MAX for key in by_key):
        raise ValueError(f"Union {name!r} arm keys must be integers within Int32")
    if any(codec is not None and not isinstance(codec, SlotCodec) for _, codec in by_key.values()):
        raise ValueError(f"Union {name!r} arm codecs must be SlotCodecs or None")

    def encode(value: Any) -> Any:
        if value is None:
            raise ValueError(f"Union {name!r}: expected an arm, found nil")
        if not isinstance(value, UnionArm) or value.name not in by_name:
            raise ValueError(f"Union {name!r} was given a value that is not one of its arms")
        key, codec = by_name[value.name]
        if codec is None:
            if value.value is not None:
                raise ValueError(f"Union {name!r} arm {key} is a unit arm and carries no value")
            return [key, []]
        try:
            return [key, codec.encode(value.value)]
        except ValueError as error:
            raise ValueError(f"Union {name!r} arm {key}: {error}") from None

    def decode(raw: Any) -> Any:
        if raw is None:
            raise ValueError(f"Union {name!r}: expected an arm, found nil")
        if not isinstance(raw, list) or len(raw) != 2:
            raise ValueError(f"Union {name!r} value is malformed: expected [armKey, [armSlots]]")
        key = raw[0]
        if not _is_key(key):
            raise ValueError(f"Union {name!r} value is malformed: the arm key is non-integer")
        if not _INT32_MIN <= key <= _INT32_MAX:
            raise ValueError(f"Union {name!r} value is malformed: the arm key is outside Int32")
        if key not in by_key:
            declared = ", ".join(str(declared_key) for declared_key in sorted(by_key))
            raise ValueError(f"Union {name!r} does not declare arm key {key} (declared: {declared})")
        if raw[1] is None:
            raise ValueError(f"Union {name!r} arm {key} has nil slots")
        if not isinstance(raw[1], list):
            raise ValueError(f"Union {name!r} value is malformed: expected [armKey, [armSlots]]")
        arm_name, codec = by_key[key]
        if codec is None:
            return UnionArm(arm_name, None)
        try:
            return UnionArm(arm_name, codec.decode(raw[1]))
        except ValueError as error:
            raise ValueError(f"Union {name!r} arm {key}: {error}") from None

    return SlotCodec(encode=encode, decode=decode, type_name=type_name)


def define_document_registry(*documents: DocumentDefinition[Any]) -> tuple[DocumentDefinition[Any], ...]:
    return tuple(documents)


def extract_value(value: Any, extractor: str | Extractor[Any]) -> str | int | float | bool | None:
    if callable(extractor):
        return extractor(value)
    if isinstance(value, dict):
        return value.get(extractor)
    return getattr(value, extractor, None)


_INT32_MIN = -(2**31)
_INT32_MAX = 2**31 - 1


def _is_key(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


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


def _record_codec(
    type: str, normalized: list[DatabaseEntryField], cls: type[Any] | None, type_name: str | None = None
) -> SlotCodec:
    max_key = normalized[-1].key

    def encode(value: Any) -> list[Any]:
        if value is None:
            raise ValueError(f"DatabaseEntry document type {type!r}: expected a record, found nil")
        slots = [None] * (max_key + 1)
        for field in normalized:
            try:
                slots[field.key] = field.codec.encode(extract_value(value, field.name))
            except ValueError as error:
                raise ValueError(f"{type} slot {field.key} ({field.name}): {error}") from None
        return slots

    def decode(raw: Any) -> Any:
        if not isinstance(raw, list):
            raise ValueError(f"DatabaseEntry document type {type!r} expected a MessagePack array")
        values: dict[str, Any] = {}
        for field in normalized:
            if field.key < len(raw):
                try:
                    values[field.name] = field.codec.decode(raw[field.key])
                except ValueError as error:
                    raise ValueError(f"{type} slot {field.key} ({field.name}): {error}") from None
            elif field.default is MISSING:
                values[field.name] = None
            else:
                values[field.name] = field.default() if callable(field.default) else field.default
        if cls is None:
            return values
        return cls(**values)

    return SlotCodec(encode=encode, decode=decode, type_name=type_name)


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
