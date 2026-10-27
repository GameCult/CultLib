# Changelog

All notable changes to `cultnet-rs` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases are recorded
only in git history.

## Unreleased

### Breaking

- `CultNetDocumentRegistry::register` returns `Result<&mut Self>` where it returned `&mut Self`. A binding that claims a document type or a schema id another binding already holds is refused with `cultcache_rs::SchemaConflictError` (naming the new binding's schema id and both document types) and nothing is bound; binding a type to the schema id it already holds still replaces that binding. Callers chain `register(...)?`.
- `CultMeshCdnChunkRef` gains a public `id` field, the decimal text of `offset` (the reference's element id, slot 4). A manifest written before ids reads `""`; encoding writes the slot.
