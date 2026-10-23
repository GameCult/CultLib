# Changelog

All notable changes to `cultmesh-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Breaking

- `asSchemaAlias` is removed from `CultMeshDocumentHandle`, `CultMeshCollectionHandle` and their bound forms. Read the handle by the schema you mean.
- `CultMeshDocumentCatalog` (`document`, `tryDocument`, `latest`, `replace`, `submitPrediction`, `authoritativeWriter`, `predictionWriter`, `watch`) and `CultMeshCollectionCatalog` (`collection`, `tryCollection`, `latest`, `watchChanges`) take `<TDocument = never, TSchema>` and yield `CultMeshCatalogValue<TDocument, TSchema>`: an explicit type argument wins, a CultCache document definition names its own value type, and a bare schema descriptor yields `unknown`. A value type is no longer inferred from a value or an expected result. The `parse` option is removed from these calls, and a lookup by type answers with that type's handle or none, not an alias of a compatible schema.
- Adding a document or collection to a catalog under a schema id or schema name another type already holds is refused with `SchemaConflictError`.

### Added

- `CultMeshCatalogValue<TDocument, TSchema>`, exported.
- `documentById` and `tryDocumentById` on `CultMeshDocumentCatalog`, which address one document by its document id when a catalog holds several documents of one type.

### Dependencies

- Needs the cultcache-ts release that ships with this change (its manifest range moves with that release): it imports `schemaIdentityOf` and `SchemaConflictError`, which 0.14.x does not export.

## [0.2.0]

### Added
- `cultmesh-ts/content`: an HTTPS content provider. `createContentChunkHandler`
  serves CDN chunks from a `ContentChunkStore` (`createDirectoryChunkStore` for
  a directory of chunks) with bounded reads, and `toNodeListener` mounts the
  handler on a Node HTTP server.

### Changed
- Depends on `cultnet-ts` `^0.3.0`.
