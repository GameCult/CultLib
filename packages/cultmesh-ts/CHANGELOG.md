# Changelog

All notable changes to `@gamecult/cultmesh-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Breaking

- The package is renamed `cultmesh-ts` to `@gamecult/cultmesh-ts` (the repo already scopes `@gamecult/cultcache-ts`), so the unscoped name cannot be claimed by someone else. Import from `@gamecult/cultmesh-ts` and its subpaths; the API is unchanged. Depends on `@gamecult/cultnet-ts`.
- `asSchemaAlias` is removed from `CultMeshDocumentHandle`, `CultMeshCollectionHandle` and their bound forms. Read the handle by the schema you mean.
- `CultMeshDocumentCatalog` (`document`, `tryDocument`, `latest`, `replace`, `submitPrediction`, `authoritativeWriter`, `predictionWriter`, `watch`) and `CultMeshCollectionCatalog` (`collection`, `tryCollection`, `latest`, `watchChanges`) take `<TDocument = never, TSchema>` and yield `CultMeshCatalogValue<TDocument, TSchema>`: an explicit type argument wins, a CultCache document definition names its own value type, and a bare schema descriptor yields `unknown`. A value type is no longer inferred from a value or an expected result. The `parse` option is removed from these calls, and a lookup by type answers with that type's handle or none, not an alias of a compatible schema.
- `createRudpDocumentServer` takes `writeAuthorityTimeoutMs` (default `CULTMESH_RUDP_DOCUMENT_SERVER_DEFAULT_DECIDE_TIMEOUT_MS`, 5000; a value outside 1..2147483647 throws `RangeError`): a write authority whose `decide()` has not settled by then refuses the write like one that throws (`write_not_authorized`, and `onError` hears of it), so a hung authority no longer holds up the session's later frames.
- `createRudpDocumentServer` refuses every peer put and delete unless it is given a `writeAuthority`. A refused write is answered with the typed `write_not_authorized` naming the record key (the bytes pinned in `error-vectors.json`), and `onDocumentPutRaw` is never called; pass `writeAuthority: new CultMeshGrantAllWrites()` to keep the old accept-everything behaviour. The authority is asked before the put is sized or decoded and before any handler runs, with the canonical schema id and the peer's address (the schema id is resolved to its canonical form first, which is the timing residual below); one that throws, rejects or answers anything but `granted: true` refuses. A frame the server cannot read is judged as the write it looks like (a put when it does not decode at all), so an ungranted peer gets `write_not_authorized` for it too; a granted peer gets `onError` and a typed `message_invalid` refusal (`details.field` is `message`). A put carrying a wrong-typed `shardId` or `shardEpoch` is judged with null shard fields and, once granted, refused with `message_invalid` naming that field. A refusal's record key and reason are clipped (64 and 96 UTF-8 bytes) so the refusal is at most 309 bytes; a `maxPayloadBytes` below that cannot carry it, and the peer then hears nothing (the failure goes to `onError`). Residual: ungranted peers can still distinguish registered from unregistered schemas by timing. A peer `document_delete` now reaches the new `onDocumentDeleteRaw` once granted (without it, a text refusal) instead of the unsupported-request error.
- Adding a document or collection to a catalog under a schema id or schema name another type already holds is refused with `SchemaConflictError`.

### Added

- `createRudpDocumentServer` ends a session after `CULTMESH_RUDP_DOCUMENT_SERVER_MAX_CONSECUTIVE_REFUSED_FRAMES` (8) consecutive undecodable frames or consecutive deletes the write authority refuses; fewer are answered and the session is kept. Any decoded frame resets the undecodable run, any decoded non-refused-delete frame resets the delete run. The same cap holds in every runtime.
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
