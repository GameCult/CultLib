# CultCache Persistence Format

This document is the canonical design receipt for the next CultCache storage
format. `GameCult.Caching` in C# is the source of truth for this contract. Rust,
TypeScript, and other siblings should follow once the shape stabilizes here.

The goal is to preserve CultCache's best property: application code should work
with domain data, not with a little parade of storage metadata stapled onto
every object until the object forgets what it was for.

## Why This Exists

The current stack has two conflicting virtues:

- domain ergonomics: users want plain typed objects
- interop durability: caches in different languages need enough persisted
  information to read each other's data, compare schemas, and perform soft
  migration when shapes drift

The old C# shape leaned hard toward ergonomics by exposing `DatabaseEntry.ID`
directly and letting backing stores carry a lot of implicit responsibility. The
newer Rust draft leaned hard toward persistence machinery by wrapping payloads
in a heavier envelope. Both capture part of the truth. Neither is the final
machine.

The intended format keeps storage metadata out of domain objects while making
the persisted store self-describing enough for cross-language readers and
training-data archaeology.

## Core Rule

Domain payloads contain domain truth only.

These are **not** domain fields and should not be exposed on app-facing types:

- CultCache record key / GUID
- persistence schema id
- storage timestamp

Those belong to the CultCache persistence layer.

## Canonical Store Shape

A persisted CultCache file should contain:

1. store header
2. embedded schema catalog
3. record set

High-level shape:

```json
{
  "formatVersion": "cultcache.store.v1",
  "catalog": {
    "catalogVersion": "cultcache.schema_catalog.v1",
    "schemas": []
  },
  "records": []
}
```

The on-disk encoding does not have to be JSON. MessagePack is the expected
default. The shape above is explanatory.

## Store Header

Required header fields:

- `formatVersion`
- `catalog`
- `records`

Optional header fields:

- `createdAt`
- `updatedAt`
- `storeId`
- `producer`
- `notes`

These are store-level bookkeeping fields, not per-record domain metadata.

## Embedded Schema Catalog

The store must embed a catalog entry for every schema referenced by the records
it contains.

This is not decorative paperwork. It is what allows another CultCache
implementation to inspect an old store, resolve the schema for each record, and
decide whether it can:

- read directly
- soft-migrate with warnings
- or refuse the data honestly

### Schema Catalog Entry

Each schema descriptor should contain at least:

- `schemaId`
- `schemaName`
- `schemaVersion`
- `contentHash`
- `canonicalSchema`

Recommended optional fields:

- `migrationFamily`
- `supersedesSchemaIds`
- `compatibleReaderFamilies`
- `persistenceEncoding`
- `notes`

Example:

```json
{
  "schemaId": "sha256:8b52c6...",
  "schemaName": "gamecult.player-data",
  "schemaVersion": "1.2.0",
  "contentHash": "sha256:52ff9c...",
  "migrationFamily": "player-data",
  "supersedesSchemaIds": ["sha256:0ce41a..."],
  "persistenceEncoding": "messagepack-array-v1",
  "canonicalSchema": {
    "kind": "messagepack-array-object",
    "fields": [
      { "slot": 0, "name": "Name", "type": "string", "required": true },
      { "slot": 1, "name": "Email", "type": "string", "required": true },
      { "slot": 2, "name": "Level", "type": "int32", "required": false, "default": 0 }
    ]
  }
}
```

### `schemaId`

`schemaId` is the stable machine key. It should be derived from the canonical
semantic persistence schema, not from:

- repo path
- C# type name
- Rust type name
- TypeScript interface name
- generated file name

If two implementations describe the same persisted shape, they should derive
the same `schemaId` regardless of origin language.

### `contentHash`

`contentHash` is separate from `schemaId`.

- `schemaId` answers: "is this the same semantic persistence shape?"
- `contentHash` answers: "is this the exact same canonical schema document?"

This distinction matters because descriptions, examples, and other non-semantic
surface details may change without changing the actual persisted shape.

## Record Shape

Each persisted record should carry only the storage metadata needed to route and
reconstruct the domain object:

- `key`
- `schemaId`
- `storedAt`
- `payload`

Example:

```json
{
  "key": "e1439d7e-8da8-4b63-a0b1-238c815a8a17",
  "schemaId": "sha256:8b52c6...",
  "storedAt": "2026-05-08T12:00:00Z",
  "payload": "<messagepack bytes>"
}
```

The payload is the domain object only. It does not repeat the key, schema id,
or stored timestamp.

## Canonical Semantic Schema Hash

`schemaId` should be derived from a canonical semantic schema hash.

The canonicalization step must ignore noise that should not change storage
identity, such as:

- whitespace
- key ordering
- documentation prose
- examples
- source-generator quirks

It must preserve the parts that *do* define persistence compatibility:

- field slot numbers
- field types
- required versus defaulted presence
- nullability
- collection/container semantics
- enum/value constraints when those affect stored meaning
- persistence encoding shape, such as MessagePack array layout

The output should be a stable string such as:

- `sha256:<hex>`

This is the value persisted in each record's `schemaId`.

## Soft Migration

Soft migration is a first-class requirement. The format must support a reader
encountering a record whose embedded schema is not byte-identical to its local
native schema while still consuming the record safely when the slot contract is
compatible enough.

Reader flow:

1. Read record header.
2. Resolve `schemaId` against the embedded catalog.
3. Resolve the local registered schema for the requested native type.
4. Compare embedded versus local canonical persistence schemas.
5. Choose one:
   - `exact_match`
   - `soft_migrate`
   - `reject`

### Exact Match

Read directly when:

- schema ids match exactly
- or equivalent schema comparison says the persisted and local canonical shapes
  are identical

### Soft Migration

Allowed when:

- persisted schema has missing slots that the local schema can default
- persisted schema has extra slots the local reader can safely ignore or preserve
- slot meanings remain compatible

Soft migration should emit warnings, not silent shrugs.

Typical warnings:

- missing slot defaulted
- unknown extra slot ignored
- nullable/default behavior widened
- field preserved but not surfaced natively

### Reject

Reject when:

- slot meanings conflict
- required local data cannot be reconstructed
- stored type and local expected type are incompatible
- persistence encoding families differ in a non-compatible way

Do not bluff here. A false clean read is worse than a refusal.

## API Ergonomics

The public CultCache API should hide persistence metadata by default.

Desired feel:

- callers work with `PlayerData`, not `PlayerData` plus storage sludge
- cache methods own key generation or key routing
- global and named-entry flows stay ergonomic
- advanced callers can still inspect raw records and catalogs when needed

This implies a split between:

- domain-facing API
- raw persistence/interop API

### Domain-Facing Surface

Domain callers should interact with:

- typed adds/puts
- typed gets
- named lookups
- index lookups
- global entry helpers

They should not have to care about `schemaId`, `storedAt`, or raw payload bytes
unless they are explicitly doing migration, tooling, or replication work.

### Raw Persistence Surface

The lower-level API should still expose:

- record inspection
- schema catalog inspection
- raw record import/export
- migration diagnostics
- exact payload preservation for bit-compatible neighbors

That is the cache's responsibility, not the domain model's.

## CultNet Alignment

CultCache and CultNet should not grow rival schema religions.

The schema descriptor shape used by the CultCache embedded catalog should align
with the CultNet schema catalog as closely as practical. In particular:

- `schemaId`
- `schemaVersion`
- `contentHash`
- canonical schema body

should mean the same thing across persistence and wire discovery.

Where CultNet needs additional wire-oriented metadata, extend around the shared
core instead of redefining the core.

## Migration From Current C# Shape

The current C# `DatabaseEntry` base type exposes `ID` directly. That is useful
today but not the desired long-term boundary.

Planned direction:

1. define the v1 persistence format here
2. implement store header, catalog, and record shape in C#
3. move storage identity out of the domain-facing `DatabaseEntry` contract
4. keep compatibility shims long enough to read older stores
5. let Rust, TypeScript, and other siblings follow the stabilized C# contract

The transition should preserve the existing soft-migration advantage rather than
trading it for fake purity.

## Near-Term Rules

Until this format is implemented:

- treat this document as the canonical target
- do not add new storage metadata to domain payloads
- prefer schema catalogs over repeated per-document metadata
- preserve the distinction between semantic schema identity and exact schema
  document hash
- keep cross-language interop as a first-class design constraint, not a later
  apology
