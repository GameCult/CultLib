# Changelog

All notable changes to this package are documented in this file.

## [Unreleased]

### Breaking

- CultCache refuses an unknown union arm on read and an unnamed union subtype on
  write. A payload whose arm key the union does not declare used to decode to
  `null` (and the next save wrote the loss back); a value whose type the union
  does not name used to be written as `nil`. Both now throw, naming the union and
  the key or type, and a store pull names the record key and schema id. Known arms
  encode to the same bytes as before.

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
