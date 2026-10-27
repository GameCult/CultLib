# Changelog

All notable changes to this package are documented in this file.

## [Unreleased]

Releases as 3.0.0 (major).

### Breaking (major)

- A single-file store flush refuses a file it cannot read (empty, truncated, an unreadable stamp or body) with `CultStoreUnreadableException` and leaves the file as it is. A flush used to replace such a file. A write set cannot merge with a disk it cannot read, so the file is repaired or removed by its owner before the store writes again.
- A single-file store reads v1, v2 and v3 stamps and refuses v4 and later by name; a directory store reads v4 and v5 and refuses v6 and later by name. Each judges the stamp before the body, so an unreadable body never makes a newer store look like damage.
- A store is written in the format its content needs, and CultLib 2.0 refuses the newer ones by name. A single-file store holding a document variant is written as `cultcache.store.v2`, one holding an element id as `cultcache.store.v3`; a directory store holding an element id is written as `cultcache.store.v5` (`v4` otherwise). A store is marked only when a record actually holds an id or a variant. Upgrade readers before writers: a 2.0 reader that opens such a store is refused, not misread.
- Writes are write sets. A flush or commit replaces the records it staged and copies every other record forward as stored, under the store's lock, instead of rewriting the whole store from the cache's memory. A record the cache does not know is kept, and an untouched record keeps a dropped member's slot through every write (never reuse a dropped slot; see the schema-compatibility contract).
- A write or removal at a key whose stored record the writer does not own is refused with `CultSchemaConflictException`, and the file is left as it was. A writer owns a record whose schema id it declares (owns or lists compatible) and a record its type reads whole under compatible drift: that record is writable and is restamped under the type's own id on write, as in 2.0.1. A record a type reads only by dropping persisted slots stays read-only until its id is declared compatible, which accepts the loss (this differs from 2.0.1, which shed the slots silently). A cache owns through its registry; a cacheless CultMesh write owns through the catalog entry it writes.
- Registration refuses an owned object-list element type that has no `[CultElementId]` member (a public string with `[Key]`), naming the type and the list. Element ids let a write set merge a list by identity; a list of record references or plain values is a whole value and is not affected.
- The abstract and protected surface for store kinds outside this library changed. On `SingleFileBackingStore`, `SerializeSnapshot` and `DeserializeSnapshot` are replaced by the abstract `SerializeStore`, `SerializeRecord`, `SerializeCatalogEntry` and `ReadStore`, and the abstract `SerializePayload(object)` becomes `SerializePayload(object document, Type documentType)`: a store encodes a document as the type it is given. On `CacheBackingStore`, `Judging` is removed, `ToStoredDocument` takes the record's version (`record, version, catalog, deserialize, out report`), and these protected members are new for a store kind to implement or call: `HeaderFor`, `HeaderForWrite`, `Foreign`, `SetForeignRecords`, `Undeclared`, `RefuseUndeclared`, `RefuseUndeclaredOnDisk`, `RefuseRemovalOfUndeclared`, `Refuse`, `SameStoredIdentity`, `VersionOf`, `StoredAtOver`, `Overwrites`, `StagedChecks`, `StageChecks` and `JudgeChecks`. `MarkFlushSucceeded` is unchanged. `CacheBackingStore.ForeignRecords` is new and public: the records a store holds under a schema id its cache does not declare.
- `CultCommitRequest.Expected` is now `IReadOnlyList<(CultRecordKey Key, string? Version)>` (it was a key with a schema id and a stored-at), `CultCommitRequest.Checks` is new, and `ConditionsHold` takes `(IReadOnlyDictionary<string, string> durable, IEnumerable<CultStoredDocument> observed, IEnumerable<CultForeignRecord> foreign)` in place of `(IReadOnlyCollection<CultPersistedRecord>, IEnumerable<CultStoredDocument>)`. A store kind outside the library lists the record versions it holds and passes its foreign records.
- `GameCult.Mesh`: `AsSchemaAlias<TAlias>()` is removed from the interfaces `ICultMeshDocumentHandle` and `ICultMeshCollectionHandle` and from the classes `CultMeshDocumentHandle` and `CultMeshCollectionHandle`. Read the handle by the schema you mean.
- The element types of owned lists gain a public string `Id` member with `[CultElementId]`, so their lists merge by identity: in `GameCult.Mesh`, `CultMeshBodyDescriptor`, `CultMeshCdnChunkRef`, `CultMeshDiscoveryCandidateDocument`, `CultMeshEntityPrefabComponent`, `CultMeshEntityPrefabAssetRef` and `CultMeshEntityPrefabNode`; in `GameCult.Networking`, `CultWitnessContractPin`, `CultWitnessArtifactEntry` and `CultWitnessTimingEntry`; in `GameCult.Geometry`, `CultGeometryDomainNode`, `CultGeometryFeatureClaim` and `CultGeometryContributionRow`. Code that matched those elements positionally by shape must set or read `Id`.
- `CultNetDocumentRegistry.Register` refuses a binding that claims a document type or a schema id another binding already holds, with `CultSchemaConflictException`; binding a type to the schema id it already holds still replaces that binding.
- `InternalsVisibleTo GameCult.Mesh` is removed from `GameCult.Caching`: CultMesh's single-file document helpers use the public `ReadStoreSnapshot`, `WriteDocument`, `WriteRecord` and `CultCodec.DecodeDocument` below.

### Added

- A cacheless `WriteRecord` or `WriteDocument`, and an attach, hold the store's gate while they run, and user code they run (a document's property getters, a constructor while a copied record is read) now throws `InvalidOperationException` if it enters a cache it does not hold or a second store; it used to wait forever against an attach of the same store. A store call that queued on an attach that then failed runs under the gate that owns the store, not beside a cacheless call or a second cache's attach. An attach that fails takes its registry back, so a `PullAll` on the store afterwards is refused as unattached instead of loading records the next cache never sees. A cacheless `WriteRecord` or `WriteDocument` made while the same thread already holds that store from user code another cacheless write of the store runs, or from a journal an attach's pull runs (then "The store is attached to a cache, write through the cache.", as after the attach) throws `InvalidOperationException`; it used to write the file behind the attaching cache and take that cache's registry, so the cache could never flush or pull the store again. A journal that calls its cache's store (`Push`, `Delete`, `PushAll`, `CommitBatch`, `PullAll`, `ReadStoreSnapshot`, `JudgeChecks`) while that cache's `AddBackingStore` runs the journal now throws "A journal entered the cache it journals", as it does after the attach; it used to stage or write records the cache never admitted, so the cache and the file disagreed.
- `SingleFileBackingStore.ReadStoreSnapshot()` reads a store file once, under the store's in-process gate, with the reader a cache's store opens with, and returns the durable snapshot or null when nothing is at the path. `SingleFileBackingStore.WriteRecord(record, catalogEntry, holdsIds, registry)` writes one record without a cache: under the store's lock every other record and catalog entry is copied as stored, the file is replaced atomically, and the header is decided as a flush decides it. It refuses on a store attached to a cache.
- `SingleFileBackingStore.WriteDocument<T>(key, document, storedAt, registry)` writes one typed document without a cache, as a cache write does: it plans element ids, encodes the document as `T` (the type its schema id and ids are taken from, also for a derived instance) and marks the store when it holds an id; a failed write gives the minted ids back. It refuses on a store attached to a cache. The `registry` of `WriteRecord` and `WriteDocument` serves that call only; a store keeps none.
- `CultCodec.DecodeDocument<T>(key, payload)` decodes a stored payload as a cache load does (`CultCacheMessagePack.CreateCodec(registry)` makes the codec). It reads no file and holds no state. `CultElementIds` stays internal.
- `CultPersistedStoreSnapshot.FormatV3` names the element-id store header.
- `CultDocumentAttribute.CompatibleSchemaIds` and `CultDocumentDescriptor.CompatibleSchemaIds`: the schema ids besides its own a document type claims, so a record stored under one of them loads as that type.
- `CultStoredDocument.StoredSchemaId` (the schema id the record carries in its store, which may be an older one) and `CultStoredDocument.HoldsIds`.
- `CultCache.MintElementIds()` rewrites, once, the records of a store written before element ids whose ids exist only in memory, and returns how many it rewrote.
- `CultElementIdAttribute` declares an element id (derived from a named member when given), `CultForeignRecord` is a record a store carries untouched because no registered type reads it, and `CultElementIdException`, `CultSchemaConflictException`, `CultStoreUnreadableException` and `CultWriteConflictException` are the refusals named above.
- A store written by 1.0.x with a record-reference list in a slot still opens (from 2.0.1).

## [2.0.1] - 2026-10-22

### Fixed

- A store written by 1.0.x with a list of record references (`CultRecordRef<T>[]` or any `IEnumerable<CultRecordRef<T>>` slot)
  opens again. 2.0.0 classified such a slot as a reference to `T`, 1.0.x had persisted it as a plain value, and the schema
  comparison refused the store with "changed reference semantics" and "changed target schema". The payload is the same array
  of keys, so gaining the reference classification on an otherwise identical slot is now compatible drift (migration warning
  `gained_reference_semantics`). A reference that is lost or retargeted, and any change of type or cardinality, still refuses.

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
- CultCache payload nesting is bounded at 256 on read and on write (it was 500), and a
  union arm slot `[k, nil]` is refused. A store holding a record nested 257 to 500
  deep, which 1.0.61 read and wrote, no longer opens, and a value nested that deep
  can no longer be saved; flatten such a record before upgrading.
- RUDP: `SendSchema` throws the `SocketException` for a send that can never
  succeed as built (`MessageSize`, `InvalidArgument`, `AddressFamilyNotSupported`),
  queues nothing, and a permanent failure after an earlier fragment left ends the
  session. It used to count every send failure as a lost datagram and return. Inside
  a poll such a failure ends only that peer's session (`PeerDisconnected` carries a
  reason naming the error) and the poll is not failed.
- RUDP sessions end where they used to carry on: a peer whose message cannot be
  decoded or handled has its session ended with a goodbye, instead of throwing out
  of `PollAvailableAsync`/`PollOnceAsync`; a session that refuses a packet ends;
  `FlushReliable` throws for writes an ended session forgot; the listener replaces a
  peer on a Connect only when its connect payload differs (reporting
  `PeerDisconnected` for the old one) where it used to replace it on every Connect;
  server mode is claimed only by a Connect; an `InitialSequence` of `uint.MaxValue`
  is rejected; malformed, foreign-connection and refused packets are dropped and
  counted in `Stats.PacketsDropped` instead of throwing out of `TryReceiveOnce`.
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
- A plain flush (`SingleFileBackingStore.PushAll`) reads the file under the lock before it
  rewrites it, by the same rule attach and conditional commit use (the format stamp in
  the header decides, before the body is parsed), and throws
  `NotSupportedException` instead of writing when the store is one this runtime
  refuses to read: stamped by another process with a newer format, or with a stamp the
  reader refuses. A read that fails with an I/O or access error also refuses. It used to
  overwrite the file from this cache's older view. A directory store throws the same
  exception, from the manifest parse every write already ran, for a manifest stamped
  `cultcache.store.v<N>` with N > 4 (it threw `InvalidOperationException`). A flush
  rewrites any file whose stamp it can read as v1 or v2, or cannot read at all (empty,
  truncated, not a snapshot), which includes a v1 file whose body is corrupt or truncated.
  It refuses any other string stamp, a nil or binary stamp, and an I/O failure on the read.
  The public `DeserializeSnapshot(byte[])` still parses any string stamp, but refuses a nil
  or binary stamp (1.0.61 read a nil stamp as v1).
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
