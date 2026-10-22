# Changelog

All notable changes to this package are documented in this file.

## [2.0.0] - 2026-10-22

### Breaking

- `CultMathJson` writes `CultPhasor` as `{"cosine":..,"sine":..}` and still reads
  the old `cos`/`sin` keys. A reader from before this change does not know the
  new keys: with default options it skips them and decodes zeros, and with
  `UnmappedMemberHandling.Disallow` it throws. Upgrade readers before writers.
  MessagePack bytes are unchanged.
- CultCache refuses an unknown union arm on read and an unnamed union subtype on
  write. A payload whose arm key the union does not declare used to decode to
  `null` (and the next save wrote the loss back); a value whose type the union
  does not name used to be written as `nil`. Both now throw, naming the union and
  the key or type, and a store pull names the record key and schema id. Known arms
  encode to the same bytes as before.
- A payload that fails to decode during a store pull (single-file or directory)
  now throws `InvalidDataException` naming the record key and schema id, with the
  original exception (for example `MessagePackSerializationException`) as its
  `InnerException`. Callers that caught the codec's exception type around a store
  open or pull must catch `InvalidDataException`. An arm key outside Int32 or not
  an integer is refused with the same union-naming refusal, and a variant
  override holding an unknown arm names the schema id.
- Removed or retyped public API, each with its replacement (measured against 1.0.61):
  - `GameCult.Caching`: the `CultCache(CultDocumentRegistry?)` constructor is now
    `CultCache(CultDocumentRegistry? registry = null, CultCodec? codec = null)`; code
    that bound the old signature by reflection or a method group must rebind.
    `CacheBackingStore.ToStoredDocument(CultPersistedRecord, IReadOnlyCollection<..>, ..)`
    gains an `out` report of the schema drift it shed. The
    `CultInspectorModel(registry, Func serialize, Func deserialize)` constructor is
    now `CultInspectorModel(registry, CultCodec codec)`.
  - `GameCult.Mesh`: `CultMeshPeerSnapshotDocumentOptions.SchemaIds`,
    `CultMeshSnapshotRequestOptions.SchemaIds` and `.RecordKeys` are removed; set
    `Selection` (a `CultNetSelection`) instead. `CultMeshHotBodySubscription.SchemaIds`
    and `.RecordKeys` are removed; read `Selection`.
  - `GameCult.Networking`: `CultNetDatabaseSubscriptionServer` no longer takes the
    `projectRecord` constructor argument; a subscription is narrowed by its
    selection and `authorizeRecord`.
  - `InitialSequence` is `uint?` instead of `uint` on `CultMeshRudpSocketOptions`,
    `RudpCultNetSchemaServerOptions`, `CultNetRudpSessionOptions`,
    `CultNetRudpSocketTransportOptions` and `CultNetRudpSocketTransportServerOptions`.
    Unset, a session draws a random initial sequence in [1, 2^31) (the old defaults
    were 1 and 100); set a value to keep a fixed one.
- A data member aliased the same name as a reference member in one document type
  (for example a member aliased `Owner` beside a reference member named `Owner`)
  used to register and is now refused at registration, naming the clash.
- A store header other than one this runtime knows, and a record with more slots
  than its header allows, are refused by name on read instead of being skipped or
  read as `cultcache.store.v1`. A reader from before this change silently dropped
  the extra slot (for example a variant) and the next save wrote the loss back.
  `cultcache.store.v1` and `cultcache.store.v2` are the formats this release reads;
  a store stamped with a newer one is refused, so this release never rewrites it.
- `CultNetDatabase.PutAsync` and `DeleteAsync` for a schema no shard owns throw
  `CultNetUnownedSchemaException` (an `InvalidOperationException` naming the schema)
  before the cache is touched. They used to cache the write and never log it. The
  fallback shard is gone: a database with shards that do not cover a schema serves
  and writes only what its shards own.
- RUDP: `CultMeshRudpSocketOptions.BindHost` is nullable and unset by default. A
  client now binds loopback for a loopback endpoint and the unspecified address of
  the endpoint's family otherwise (it used to bind `0.0.0.0` for every client, so an
  IPv6 endpoint could not be reached); a server still binds `0.0.0.0`. An explicit
  `BindHost` wins for both.
- RUDP wire behaviour changed in step across the C#, Rust, TypeScript and Python
  runtimes: one received-through watermark per session owns ordered delivery; a
  Connect repeats only when it carries the sequence that opened the generation, and
  reliable data before the handshake is neither acknowledged nor delivered; the
  sender admits a packet only within 1,023 sequences and 4 MiB above the lowest
  unacknowledged one. A peer on 1.0.x interoperates only for traffic that stays
  inside those bounds.
- Variants (record-level wire and store format): a store holding at least one
  variant declares `cultcache.store.v2` and its variant records carry a fifth slot.
  A reader from before this change refuses the store (unknown header) rather than
  reading it. A store with no variant stays `cultcache.store.v1`, byte-identical to
  1.0.x. CultMesh single-file reads, the directory store and CultNet refuse a variant
  record by name; only the C# CultCache resolves variants.
- `CultMesh.WriteSingleFileDocument` and `WriteSingleFileDocumentPayload` read the
  file they replace, under the store's `<path>.lock`, and refuse (without touching
  it) a file this runtime cannot read, a store stamped with a newer format version,
  or one holding any record besides the key being written. They used to replace the
  file whole. Write to a store that holds several records through a `CultCache`.

### Added

- Variants: a record that stores only its difference from a base record, resolved in
  memory when the cache admits it (`cultcache.store.v2`; see the persistence-format
  contract). A plain write at a variant's key is refused, and `Flatten` replaces a
  variant with a plain record.
- CultNet selection v1: one `CultNetSelection` shape and evaluator (schemas, keys,
  field predicates, cites, cursors) used by CultMesh snapshot requests, the database
  server and subscriptions, with typed refusals and row identity of
  `(schemaId, recordKey)`. v0 filters are lowered onto it with their pre-existing
  answers.
- Record grouping: a document type declares its inspector group, and the inspector
  model exposes the group tree, create-in-group and reference labels.
- `CultMath` value types have canonical MessagePack formatters and `System.Text.Json`
  converters, including `CultPhasor`, `CultSiteCells`, `SpanNoise`, `SpanPath`,
  `SpanStage` and the `ExprGraph` expression graph resolver.
- CultNet publishes every committed cache change once, in `Sequence` order, from the
  cache journal; `CultNetDatabase.Dispose` publishes what a writer logged and had
  not yet published.
- `CultCache` takes an optional `CultCodec`, and isolates every observer so a
  throwing one cannot stop the others.

### Changed

- Requires `org.gamecult.cultmath` exactly 0.5.0.
- After another writer's whole-file flush or unconditional commit rewrites a
  drifted record under a schema id this cache did not read it under, keeping its
  `storedAt`, an `Expect` captured before fails until this cache re-reads the
  store (`PullAll`), as for any other lost race.

### Fixed

- The native QUIC plugins' import settings enable `Standalone: Win64` and disable the
  32-bit target (the 1.0.61 fix, now on main).
- `Expect` and `ExpectUnchanged` hold on an unchanged record loaded under
  compatible schema drift (written under one version of its type, read by a
  cache that knows another). Before, they always failed: conditional commit
  compared the record's persisted schema id with the id this cache decodes it as.

## [1.0.61] - 2026-10-03

### Fixed

- The native QUIC plugins under `Runtime/Plugins/x86_64` (`gamecult_mesh_quic_native.dll`
  and `msquic.dll`) are enabled for 64-bit Windows players (`Standalone: Win64`).
  Their import settings named only `Standalone: Win`, the 32-bit target, so a
  Win64 player build left both DLLs out and the native QUIC bridge was
  unavailable at runtime. They are now disabled for 32-bit Windows, which they
  cannot load.

## [1.0.60]

### Changed

- Native QUIC bridge: Schannel replaced by OpenSSL MsQuic 2.5.9, statically
  linked, first shipping in this package with this release. `msquic.dll` grew from
  536,928 to 4,181,856 bytes and the bridge from 40,448 to 302,080 bytes as a
  result of the static CRT and the statically linked OpenSSL build. The
  bridge keeps its v1 exports on a v2 event-queue runtime. An OpenSSL notice
  was added under `Third Party Notices`.

### Breaking

- `CultInspectorAssetPath` is removed. Inspector annotations that stored an
  asset by path now use `CultInspectorAssetGuid`, whose value is a
  `CultAssetGuidKey`: the bare Addressables GUID for a main asset, or
  `guid[subAssetName]` for a picked sub-asset.
