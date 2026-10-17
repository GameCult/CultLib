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
