# cultcache-py

`cultcache-py` is the Python port of GameCult's CultCache persistence pattern: callers work with registered domain documents while the cache owns schema identity, routing, globals, name lookups, indexes, and backing-store envelopes.

It is intentionally small. It is not an ORM, a database, or distributed consensus wearing rented authority.

## Current Shape

- document types are registered explicitly
- each persisted envelope stores `type`, `key`, `payload`, and `stored_at`
- payloads decode only through the registered document definition for that type
- unknown persisted types fail closed
- global documents are singleton-style per type
- type-specific backing stores beat generic backing stores
- the JSONL store uses only the Python standard library for bootstrap consumers
- the MessagePack store is available when the `msgpack` package is installed
- `define_database_entry_type(...)` emits Rust/C#-style slot-indexed
  MessagePack array payloads for cross-runtime `DatabaseEntry` contracts

## Example

```python
from dataclasses import dataclass, asdict
from cultcache_py import CultCache, JsonLinesBackingStore, define_document_type

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
    .add_generic_store(JsonLinesBackingStore("state.cultcache.jsonl"))
    .build()
)

cache.pull_all_backing_stores()
cache.put_global(settings_doc, Settings(theme="ash", retries=3))
settings = cache.get_required_global(settings_doc)
```

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
- `put_global(...)`
- `update(...)`
- `update_global(...)`
- `delete(...)`
- `delete_global(...)`
- `snapshot()`

## Persistence Model

Backing stores persist envelopes, not caller domain objects. The cache decodes payloads through registered document definitions and rejects unknown type discriminators. This keeps Python's dynamic runtime from turning persistence into an open polymorphic sewer with a cheerful docstring.

`JsonLinesBackingStore` is the dependency-free control-plane store. It rewrites an atomic JSONL snapshot and base64-encodes payload bytes. It is designed for compact state spines, settings, ledgers, and bootstrap surfaces.

`SingleFileMessagePackBackingStore` follows the same envelope model using MessagePack. Install `msgpack` to use it:

```powershell
python -m pip install msgpack
```

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
