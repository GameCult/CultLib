# cultcache-py

`cultcache-py` is the Python CultCache runtime. Callers work with registered
domain documents while the cache owns schema identity, routing, globals, name
lookups, indexes, and backing-store envelopes.

It is intentionally small. It is not an ORM, a database, or distributed consensus wearing rented authority.

It is the bottom of the Python CultLib stack and depends only on `msgpack`.
`cultnet-py` (transport and wire contracts) and `cultmesh-py` (nodes,
discovery, sessions, local server) build on it, mirroring `cultcache-rs` /
`cultnet-rs` / `cultmesh-rs` and the npm packages.

## Current Shape

- document types are registered explicitly
- a registered cache can be used in memory without attaching a backing store
- `SingleFileMessagePackBackingStore` writes `cultcache.store.v1` snapshots:
  `[format, schemaCatalog, records]`
- records store raw MessagePack payload bytes under schema-catalog identity
- payloads decode only through the registered document definition for that type
- unknown persisted types fail closed
- global documents are singleton-style per type
- one home store per document type: a store added with `add_backing_store`
  owns its types, otherwise the generic store is the home; a type claimed by a
  second store, or a second generic store, raises `CultCacheError`; a write
  touches exactly one store and nothing is mirrored; once any store is
  attached, a write whose type has no home raises
- `define_document_type(...)` payloads are MessagePack by default; `JSON_PAYLOAD_CODEC` reads stores written by 0.3 and earlier
- `define_database_entry_type(...)` emits Rust/C#-style slot-indexed
  MessagePack array payloads for cross-runtime `DatabaseEntry` contracts

## Example

```python
from dataclasses import dataclass, asdict
from cultcache_py import CultCache, define_document_type

@dataclass
class Settings:
    theme: str
    retries: int

settings_doc = define_document_type(
    "settings",
    encode=lambda value: asdict(value),
    decode=lambda payload: Settings(**payload),
    global_document=True,
)

cache = (
    CultCache.builder()
    .register_document_type(settings_doc)
    .build()
)

cache.put_global(settings_doc, Settings(theme="ash", retries=3))
settings = cache.get_required_global(settings_doc)
```

Add `JsonLinesBackingStore` or `SingleFileMessagePackBackingStore` when the
cache should persist state; local in-memory cache reads and writes do not need a
store.

## Public Surface

- `define_document_type(...)`
- `define_database_entry_type(...)`
- `database_entry_field(...)`
- `leaves` (typed slot leaves)
- `define_document_registry(...)`
- `CultCache.builder()`
- `register_document_type(...)`
- `register_registry(...)`
- `register_name_lookup(...)`
- `register_index(...)`
- `add_backing_store(...)`
- `add_generic_store(...)`
- `pull_all_backing_stores()`
- `get(...)`
- `get_required(...)`
- `get_all(...)`
- `get_key_by_name(...)`
- `get_by_name(...)`
- `get_key_by_index(...)`
- `get_by_index(...)`
- `get_global(...)`
- `get_required_global(...)`
- `put(...)`
- `put_envelopes(...)`
- `put_global(...)`
- `update(...)`
- `update_global(...)`
- `delete(...)`
- `delete_global(...)`
- `snapshot()`

## Persistence Model

Backing stores persist envelopes, not caller domain objects. The cache decodes payloads through registered document definitions and rejects unknown type discriminators. This keeps Python's dynamic runtime from turning persistence into an open polymorphic sewer with a cheerful docstring.

`JsonLinesBackingStore` is the control-plane store. It rewrites an atomic JSONL snapshot and base64-encodes the payload bytes its definition's codec wrote. It is designed for compact state spines, settings, ledgers, and bootstrap surfaces.

`SingleFileMessagePackBackingStore` follows the shared CultCache v1 store shape
used by the TypeScript, Rust, and C# runtimes:

- top-level MessagePack value: `[format, schemaCatalog, records]`
- `format`: `cultcache.store.v1`
- catalog entries: `[schemaId, schemaName, schemaVersion, contentHash,
  canonicalSchemaJson, compatibleSchemaIds, members]`
- records: `[key, schemaId, storedAt, payload]`

Large corpora should use a sharded store or a real database. A single snapshot file is a scalpel, not a forklift.

## Payload format

A definition's `payload_codec` owns its payload bytes. The default,
`MESSAGEPACK_PAYLOAD_CODEC`, writes MessagePack; a store holds no format marker,
so the registered definition decides how every record is read, and a record in
another format is refused with `CultCachePayloadFormatError`, never guessed at.

- Map keys are `str`, integer, float, `bool` or `msgpack.Timestamp`, as the C#
  reference's object reader takes them; `None`, `bytes`, list and map keys are
  refused, on read and on write. Two keys Python holds as one are refused on read
  as `duplicate_map_key`, which includes `1` beside `1.0` or `True`: the reference
  keeps those apart and a Python `dict` cannot. The same keys are refused on
  write as `duplicate_map_key`, which includes a `Float32` key beside an equal
  int, bool or float key, and two NaN keys, which are one key: the codec never
  writes bytes its own reader refuses. One pair of keys is the exception to the reference, see Wire Parity.
- Arrays and maps nest at most 256 deep (`MAX_PAYLOAD_DEPTH`), on read and on
  write, in the codec and in the definition alike.
- Integers beyond 64 bits (outside `-(2**63)..2**64-1`) are refused on write as
  `integer_out_of_range`; MessagePack has no wider integer.
- Timestamps lie within years 1 to 9999 (`.NET` `DateTime`), and no extension type
  but the timestamp exists: a `msgpack.ExtType` value or a timestamp outside that
  range is refused on read and on write as `value_kind` or `timestamp_out_of_range`.
- A store written by cultcache-py 0.3 or earlier holds JSON payloads. Define each
  type with `payload_codec=JSON_PAYLOAD_CODEC` to read it, or write it as a new store
  with `convert(source, destination, definitions)`. Stop every writer of the source
  first. `destination` must not exist; every record of the listed definitions is
  rewritten under the definitions' own codec, records of other types keep their
  bytes, and the new file is fsynced before the call returns. Open it with the
  default definitions, then swap it in yourself. The source is never written. A
  refused record raises `CultCacheError` naming the type, record key and code, and
  leaves no destination; a killed run leaves the source as it was and a partial
  destination, so delete the destination and run again.
- A JSON payload that is a single digit (`0` to `9`) is also a valid one-byte
  MessagePack integer and reads as 48 to 57 under the default. Documents that
  are objects or lists never meet this.
- A `PayloadCodec` you build owns the nesting depth of nodes the library's walk
  cannot see (anything that is not a mapping, sequence or set), and may raise
  `PayloadCodecFault(code, kind)` for a key or integer its format cannot carry.

## DatabaseEntry Slot Contract

For cross-runtime cache entries, use `define_database_entry_type(...)`, which
fixes the MessagePack slot-array payload:

```python
from dataclasses import dataclass
from cultcache_py import define_database_entry_type

@dataclass
class Settings:
    theme: str
    retries: int = 0

settings_doc = define_database_entry_type(
    "settings",
    [
        ("theme", 0),
        ("retries", 1, 0),
    ],
    cls=Settings,
)
```

The payload is a MessagePack array. Field keys are durable slot indexes:

- key `0` writes array slot 0
- key `4` writes array slot 4
- unused slots are written as nil
- deleted fields should leave their slots reserved
- newly added fields should use new keys
- fields with defaults tolerate missing or nil slots when older payloads are read

That matches the Rust `#[derive(DatabaseEntry)]` formatter shape and the C#
`[Key(n)]` intent: schema evolution comes from stable field slots, not source
member order.

## Typed Leaves

A slot with no codec is untyped: it is an `object` in C#, cataloged `System.Object`, and takes any MessagePack value.
Give a slot a leaf from `cultcache_py.leaves` and it writes and reads exactly what the C# type does, refuses the rest
on write and on read, and carries the C# type name in the schema catalog, which the C# reference checks against its
own class.

```python
from cultcache_py import database_entry_field, define_database_entry_type, leaves as t

settings_doc = define_database_entry_type(
    "settings",
    [
        database_entry_field("theme", 0, codec=t.string),
        database_entry_field("retries", 1, codec=t.int32, default=0),
        database_entry_field("tags", 2, codec=t.list(t.string), default=list),
    ],
)
```

| Leaf | C# type | Python value |
| --- | --- | --- |
| `t.string` | `string` | `str` |
| `t.boolean` | `bool` | `bool` |
| `t.bytes` | `byte[]` | `bytes`, `bytearray`, `memoryview`; a MessagePack str reads as its UTF-8 bytes |
| `t.int32`, `t.int64` | `int`, `long` | `int` within range (not `bool`, not `float`) |
| `t.float32`, `t.float64` | `float`, `double` | `int` or `float`; a float32 is written as MessagePack float 32 |
| `t.list(x)` | `List<T>` | `list` or `tuple`, each element through `x` |
| `t.array(x)` | `T[]` | the same wire as `t.list(x)` |
| `t.any` | `object` | any plain MessagePack value or a timestamp; the default of an untyped slot |
| `t.nullable(x)` | `T?` | `None`, or whatever `x` takes |
| `t.lazy(get)` | | a leaf resolved on first use, for a union that refers to itself |

Nil is refused unless the slot is wrapped in `t.nullable`. Wrap a C# `string`, `byte[]`, `List`, array, class or
interface slot that may hold null (`t.nullable(t.string)`), and a value type that may hold null (`int?` is
`t.nullable(t.int32)`, cataloged `System.Nullable<System.Int32>`); a reference type keeps its own name. A `default`
fills a slot the payload is too short to have, never a nil that is present.

A record or union is named for C# with `type_name=`, the class's `FullName` (a nested class joins with `+`):

```python
inner = define_record_codec("Inner", [database_entry_field("n", 0, codec=t.string)], type_name="My.App.Doc+Inner")
shape = define_union("Shape", {0: ("Circle", circle)}, type_name="My.App.Doc+IShape")
```

Without `type_name=` the slot is cataloged `System.Object`. A refusal reads `<type> slot <key> (<field>): expected
<what>, found <kind>`, prefixed `element <i>: ` inside a list and `Union '<name>' arm <key>: ` inside a union, and never
carries the value.

## Wire Parity

See [docs/python-runtime-parity.md](../../docs/python-runtime-parity.md) for
the evidence map across the three Python packages.

The store interop peer is `cultcache_py.interop`:

```powershell
python -m cultcache_py.interop write --file cache.cc --runtime-id python
python -m cultcache_py.interop read --file cache.cc
```

One deliberate exception to leaf acceptance following the reference: Python reads `float32` and `float64` map keys as
one float, so a map holding a `float32` NaN key beside a `float64` NaN key is refused on read and on write as
`duplicate_map_key`. C# reads that map as two keys, from the bytes `82 ca ffc00000 01 cb fff8000000000000 02` (fixint
values) and from the bytes it writes itself, `82 ca ffc00000 d2 00000001 cb fff8000000000000 d2 00000002` (`float.NaN`
and `double.NaN` keys with values 1 and 2, written as int32); every pair of NaN keys of one width is refused by both runtimes.

`packages/cultcache-ts/test/cult-cache.test.ts` includes Python in the shared
CultCache v1 parity matrix with TypeScript, Rust, and C#. `cultcache_py` ships
a `py.typed` marker so downstream type checkers can inspect the package surface.

## Tests

```powershell
$env:PYTHONPATH="$PWD\packages\cultcache-py\src"
python -m unittest discover -s packages\cultcache-py\tests
```

The suite runs with only this package on the path. If a test here needs
`cultnet_py` or `cultmesh_py`, it belongs in that package.
