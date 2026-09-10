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
- type-specific backing stores beat generic backing stores
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
