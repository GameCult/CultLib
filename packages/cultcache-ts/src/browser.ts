// Browser-safe surface: everything the package root exports except the node-only single-file store
// (node:fs, koffi). Bring your own CacheBackingStore, or none.
export * as codecs from "./codec";
export type { Codec, SlotsCodec } from "./codec";
export { CultCache, CultCacheBuilder, type CultCacheForeignRecord } from "./cult-cache";
export { inspectCultCacheBytes } from "./cult-cache-inspector";
export {
  decodeDocumentPayload,
  defineDocumentRegistry,
  defineDocumentType,
  documentPayloadBytes,
  documentPayloadFromBytes,
  encodeDocumentPayload,
  schemaIdentityOf,
  type CultCacheSchemaIdentity,
} from "./document";
export { CultCachePayloadDepthError, MAX_PAYLOAD_DEPTH } from "./payload-depth";
export type { DocumentPayload } from "./document";
export { isStoredAtStamp } from "./stored-at";
export { SchemaConflictError, StoreUnreadableError } from "./store-format";
export type {
  AnyCultCacheDocumentDefinition,
  CacheBackingStore,
  CultCacheDocumentAccessor,
  CultCacheDocumentDefinition,
  CultCacheDocumentFieldName,
  CultCacheDocumentFormatter,
  CultCacheDocumentIndexDefinition,
  CultCacheDocumentRegistry,
  CultCacheSchemaCatalogEntry,
  CultCacheSchemaCatalogMember,
  CultCacheSchema,
  CultCacheDocumentValue,
  CultCacheEnvelope,
  CultCacheIndexScalar,
  InferCultCacheSchemaValue,
  CultCacheStoreRegistration,
  CultCacheUnreadSlots,
  CultCacheUpdateOptions,
  CultCacheUpdateResult,
  PushAllOptions,
} from "./types";
export type {
  CultCacheInspection,
  InspectedCatalogEntry,
  InspectedCatalogMember,
  InspectedRecord,
} from "./cult-cache-inspector";
export {
  defineSwarmDocuments,
  type DefineDocumentType,
  type SwarmDocumentCatalog,
} from "./swarm-documents";
