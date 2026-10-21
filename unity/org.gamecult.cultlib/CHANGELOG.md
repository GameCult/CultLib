# Changelog

All notable changes to this package are documented in this file.

## [Unreleased]

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

### Added

- `CultStoredDocument.PersistedSchemaId`: the schema id a record is durably
  stored under, as its backing store last read or wrote it. It differs from
  `Descriptor.SchemaId` only for a record loaded under compatible schema drift
  that its store has not rewritten since.
- `CacheBackingStore.MarkWritten(CultStoredDocument)` (protected instance
  method): a backing store records that its completed durable write stored a
  record under its descriptor, so `PersistedSchemaId` follows the rewrite. It
  throws `ArgumentException` for an entry that is not the one the store holds
  in its own `Entries`. The single-file and directory stores mark every record
  they rewrite; a custom store calls it for each record its own write rewrote.

### Changed

- After another writer's whole-file flush or unconditional commit rewrites a
  drifted record under a schema id this cache did not read it under, keeping its
  `storedAt`, an `Expect` captured before fails until this cache re-reads the
  store (`PullAll`), as for any other lost race.

### Fixed

- `Expect` and `ExpectUnchanged` hold on an unchanged record loaded under
  compatible schema drift (written under one version of its type, read by a
  cache that knows another). Before, they always failed: conditional commit
  compared the record's persisted schema id with the id this cache decodes it as.

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
