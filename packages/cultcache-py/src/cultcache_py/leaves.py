"""Typed slot leaves: each owns its slot's C# type name, its write check and its read check, nil included.

A leaf is a `SlotCodec`. It turns a Python value into the tree the reference runtimes write and back, refusing a
kind the C# type would refuse. Refusals read '<what was expected>, found <kind>' and never carry the value; the
record codec prefixes them with the type, slot key and field, a list with the element, a union with its arm.

Several names here (`bytes`, `list`, `any`) shadow builtins inside this module on purpose: write
`from cultcache_py import leaves as t` and use `t.string`, `t.list(t.string)`.
"""

from __future__ import annotations

import math
import struct
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any, Callable, NoReturn

try:
    from msgpack import ExtType as _ExtType
except ModuleNotFoundError:
    _ExtType = ()  # type: ignore[assignment,misc]

_bool, _bytes, _list, _int, _float, _str = bool, bytes, list, int, float, str
_BYTE_LIKE = (_bytes, bytearray, memoryview)
_INT32 = (-(2**31), 2**31 - 1)
_INT64 = (-(2**63), 2**63 - 1)
_WRITABLE_INT = (-(2**63), 2**64 - 1)


@dataclass(frozen=True)
class SlotCodec:
    """The leaf of one slot: how a value is written and read, and the C# type name the catalog carries for it.

    `type_name` is the reference's name for the slot's type (None when only the consumer knows it: a record or union
    without `type_name=`, which the catalog lists as System.Object). `value_type` is True for a C# value type, which
    the reference makes `Nullable<T>` when the slot may be nil."""

    encode: Callable[[Any], Any]
    decode: Callable[[Any], Any]
    type_name: str | None = None
    value_type: bool = False


class _Derived(SlotCodec):
    """A leaf whose name comes from another leaf, read when the catalog is built, so a `lazy` target defined later resolves."""

    def __init__(self, encode: Callable[[Any], Any], decode: Callable[[Any], Any], name: Callable[[], str | None],
                 value_type: Callable[[], _bool] = lambda: False) -> None:
        object.__setattr__(self, "encode", encode)
        object.__setattr__(self, "decode", decode)
        object.__setattr__(self, "_name", name)
        object.__setattr__(self, "_value_type", value_type)

    @property  # type: ignore[override]
    def type_name(self) -> str | None:
        return self._name()  # type: ignore[attr-defined,no-any-return]

    @property  # type: ignore[override]
    def value_type(self) -> _bool:
        return self._value_type()  # type: ignore[attr-defined,no-any-return]


@dataclass(frozen=True)
class Float32:
    """A float32 the float32 leaf writes, already rounded to float32 (an overflow is infinity, as the C# cast): the
    payload packer writes it as MessagePack float 32."""

    value: float


def _kind(raw: Any) -> str:
    if raw is None:
        return "nil"
    if isinstance(raw, _bool):
        return "boolean"
    if isinstance(raw, _int):
        return "integer"
    if isinstance(raw, (_float, Float32)):
        return "float"
    if isinstance(raw, _str):
        return "string"
    if isinstance(raw, _BYTE_LIKE):
        return "bytes"
    if isinstance(raw, _ExtType):  # a namedtuple, so before the arrays
        return "ext"
    if isinstance(raw, (_list, tuple)):
        return "array"
    return "map" if isinstance(raw, Mapping) else "other"


def _refuse(what: str, found: str) -> NoReturn:
    raise ValueError(f"expected {what}, found {found}")


def _is_int(raw: Any) -> _bool:
    return isinstance(raw, _int) and not isinstance(raw, _bool)


def _string_encode(value: Any) -> Any:
    if not isinstance(value, _str):
        _refuse("a string", _kind(value))
    return value


string = SlotCodec(_string_encode, _string_encode, "System.String")


def _boolean_check(value: Any) -> Any:
    if not isinstance(value, _bool):
        _refuse("a boolean", _kind(value))
    return value


boolean = SlotCodec(_boolean_check, _boolean_check, "System.Boolean", True)


def _bytes_encode(value: Any) -> Any:
    if not isinstance(value, _BYTE_LIKE):
        _refuse("bytes", _kind(value))
    return value if isinstance(value, _bytes) else _bytes(value)


def _bytes_decode(raw: Any) -> Any:
    # The reference's byte[] reads a MessagePack str as its UTF-8 bytes and writes bin.
    if isinstance(raw, _str):
        return raw.encode("utf-8")
    if not isinstance(raw, _bytes):
        _refuse("bytes", _kind(raw))
    return raw


bytes = SlotCodec(_bytes_encode, _bytes_decode, "System.Byte[]")


def _integer(type_name: str, what: str, low: _int, high: _int) -> SlotCodec:
    def check(value: Any) -> Any:
        if not _is_int(value):
            _refuse(what, _kind(value))
        if not low <= value <= high:
            _refuse(what, "an integer out of range")
        return value

    return SlotCodec(check, check, type_name, True)


int32 = _integer("System.Int32", "an int32", *_INT32)
int64 = _integer("System.Int64", "an int64", *_INT64)


def _round_to_float32(value: _float) -> _float:
    try:
        return struct.unpack(">f", struct.pack(">f", value))[0]  # type: ignore[no-any-return]
    except OverflowError:
        return math.copysign(math.inf, value)


def _int_to_float32(value: _int) -> _float:
    """An integer to the nearest float32 in one rounding, ties to even, as the reference's cast does: through a double
    first, the two roundings can disagree past 2**53."""
    if -(2**53) <= value <= 2**53:
        return _round_to_float32(_float(value))
    magnitude = -value if value < 0 else value
    excess = magnitude.bit_length() - 24
    kept = magnitude >> excess
    rest = magnitude - (kept << excess)
    half = 1 << (excess - 1)
    rounded = _float((kept + 1 if rest > half or (rest == half and kept & 1) else kept) << excess)
    return -rounded if value < 0 else rounded


def _number(what: str, value: Any, *, writing: _bool) -> _float:
    """A number as a double, or the float32-rounded double for `float32`; refuses what C# refuses."""
    if isinstance(value, _float):
        return value
    if not _is_int(value):
        _refuse(what, _kind(value))
    if writing and not _WRITABLE_INT[0] <= value <= _WRITABLE_INT[1]:
        _refuse(what, "an integer out of range")
    return _float(value)


def _float32_encode(value: Any) -> Any:
    if _is_int(value):
        if not _WRITABLE_INT[0] <= value <= _WRITABLE_INT[1]:
            _refuse("a float32", "an integer out of range")
        return Float32(_int_to_float32(value))
    return Float32(_round_to_float32(_number("a float32", value, writing=True)))


def _float32_decode(raw: Any) -> Any:
    if _is_int(raw):
        return _int_to_float32(raw)
    return _round_to_float32(_number("a float32", raw, writing=False))


def _float64_encode(value: Any) -> Any:
    return _number("a float64", value, writing=True)


def _float64_decode(raw: Any) -> Any:
    return _number("a float64", raw, writing=False)


float32 = SlotCodec(_float32_encode, _float32_decode, "System.Single", True)
float64 = SlotCodec(_float64_encode, _float64_decode, "System.Double", True)


def _refuse_ext(tree: Any) -> None:
    """The reference reads an `object` slot as a plain value or a timestamp; any other extension is refused."""
    stack = [tree]
    while stack:
        node = stack.pop()
        if isinstance(node, _ExtType):
            _refuse("an object", "ext")
        if isinstance(node, Mapping):
            stack.extend(node.keys())
            stack.extend(node.values())
        elif isinstance(node, (_list, tuple)):
            stack.extend(node)


def _any_check(value: Any) -> Any:
    _refuse_ext(value)
    return value


any = SlotCodec(_any_check, _any_check, "System.Object")


def _sequence(item: SlotCodec, wrap: Callable[[str], str]) -> SlotCodec:
    def encode(value: Any) -> Any:
        if not isinstance(value, (_list, tuple)):
            _refuse("an array", _kind(value))
        out = []
        for index, element in enumerate(value):
            try:
                out.append(item.encode(element))
            except ValueError as error:
                raise ValueError(f"element {index}: {error}") from None
        return out

    def decode(raw: Any) -> Any:
        if not isinstance(raw, _list):
            _refuse("an array", _kind(raw))
        out = []
        for index, element in enumerate(raw):
            try:
                out.append(item.decode(element))
            except ValueError as error:
                raise ValueError(f"element {index}: {error}") from None
        return out

    return _Derived(encode, decode, lambda: None if item.type_name is None else wrap(item.type_name))


def list(item: SlotCodec) -> SlotCodec:
    """C# `List<T>`."""
    return _sequence(item, lambda name: f"System.Collections.Generic.List<{name}>")


def array(item: SlotCodec) -> SlotCodec:
    """C# `T[]`: the same wire as `list`."""
    return _sequence(item, lambda name: f"{name}[]")


def nullable(inner: SlotCodec) -> SlotCodec:
    """A slot that may be nil: None both ways, otherwise `inner`. A C# reference type (string, byte[], List, array,
    class, interface) is nullable as it is; a value type is `Nullable<T>`."""

    def name() -> str | None:
        if inner.type_name is not None and inner.value_type:
            return f"System.Nullable<{inner.type_name}>"
        return inner.type_name

    return _Derived(
        lambda value: None if value is None else inner.encode(value),
        lambda raw: None if raw is None else inner.decode(raw),
        name,
    )


def lazy(get: Callable[[], SlotCodec]) -> SlotCodec:
    """A leaf that names its target on first use, so a union can refer to itself or to a definition made later."""
    resolved: _list[SlotCodec] = []

    def target() -> SlotCodec:
        if not resolved:
            resolved.append(get())
        return resolved[0]

    return _Derived(
        lambda value: target().encode(value),
        lambda raw: target().decode(raw),
        lambda: target().type_name,
        lambda: target().value_type,
    )
