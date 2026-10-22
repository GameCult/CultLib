# Changelog

All notable changes to `cultnet-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

- Needs the cultcache-ts release that ships with this change (its manifest range moves with that release): the Idunn runtime authority reader calls `readSingleFileStore`, which 0.14.x does not export. An absent authority file is now a plain `Error` without a code.

## [0.3.0]

### Added
- `cultnet-ts/content`: the CultMesh CDN content plane. Manifest and chunk
  types, canonical manifest encode and decode, `packContent`, SHA-256 helpers,
  and the chunk and artifact record keys, with wire parity to the C# reference.

### Changed
- Depends on `@gamecult/cultcache-ts` `^0.14.1`, which exports
  `checkPayloadDepth`.
