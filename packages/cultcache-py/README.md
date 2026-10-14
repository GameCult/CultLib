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
- the JSONL store uses only the Python standard library for bootstrap consumers
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

`JsonLinesBackingStore` is the dependency-free control-plane store. It rewrites an atomic JSONL snapshot and base64-encodes payload bytes. It is designed for compact state spines, settings, ledgers, and bootstrap surfaces.

`SingleFileMessagePackBackingStore` follows the shared CultCache v1 store shape
used by the TypeScript, Rust, and C# runtimes:

- top-level MessagePack value: `[format, schemaCatalog, records]`
- `format`: `cultcache.store.v1`
- catalog entries: `[schemaId, schemaName, schemaVersion, contentHash,
  canonicalSchemaJson, compatibleSchemaIds, members]`
- records: `[key, schemaId, storedAt, payload]`

Large corpora should use a sharded store or a real database. A single snapshot file is a scalpel, not a forklift.

## DatabaseEntry Slot Contract

For cross-runtime cache entries, use `define_database_entry_type(...)` instead
of the generic JSON formatter:

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
