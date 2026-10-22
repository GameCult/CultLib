# Changelog

All notable changes to `cultmesh-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

- Needs the cultcache-ts release that ships with this change (its manifest range moves with that release): it imports `schemaIdentityOf` and `SchemaConflictError`, which 0.14.x does not export.

## [0.2.0]

### Added
- `cultmesh-ts/content`: an HTTPS content provider. `createContentChunkHandler`
  serves CDN chunks from a `ContentChunkStore` (`createDirectoryChunkStore` for
  a directory of chunks) with bounded reads, and `toNodeListener` mounts the
  handler on a Node HTTP server.

### Changed
- Depends on `cultnet-ts` `^0.3.0`.
