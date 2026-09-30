export const STORE_FORMAT_VERSION = "cultcache.store.v1";

/**
 * A store that can hold element ids. A reader older than element ids refuses this header: it would skip the id slots
 * of a nested element and rewrite the element without them. This runtime writes it back only for a store it read
 * under it; it does not decide when a document carries ids.
 */
export const STORE_FORMAT_ELEMENT_IDS = "cultcache.store.v3";

export type StoreFormat = typeof STORE_FORMAT_VERSION | typeof STORE_FORMAT_ELEMENT_IDS;

const STORE_FORMAT_PREFIX = "cultcache.store.";
const PERSISTED_RECORD_SLOTS = 4;
const STORE_SLOTS = 3;

/**
 * True for a v1 or v3 snapshot, false for anything that is not a CultCache store header (the legacy
 * envelope array). Any other `cultcache.store.*` header is refused by name.
 */
export function isStoreSnapshot(decoded: unknown): decoded is [StoreFormat, ...unknown[]] {
  if (!Array.isArray(decoded) || typeof decoded[0] !== "string" || !decoded[0].startsWith(STORE_FORMAT_PREFIX)) {
    return false;
  }

  if (decoded[0] !== STORE_FORMAT_VERSION && decoded[0] !== STORE_FORMAT_ELEMENT_IDS) {
    throw new Error(
      `CultCache store format "${decoded[0]}" is not readable; this runtime reads "${STORE_FORMAT_VERSION}" and "${STORE_FORMAT_ELEMENT_IDS}" only. ` +
        "The store needs a runtime that resolves document variants.",
    );
  }

  return true;
}

/**
 * A write the catalog cannot describe: records of different types under one schema id, two arrived schemas that share an id and
 * disagree on the schema name, or a record no chosen entry publishes. Nothing is written. It names the id, the names involved and
 * a record key. A cache throws it too, with an empty record key, when a definition cannot register beside one it already holds,
 * and when a record's schema id names no single registered definition.
 */
export class SchemaConflictError extends Error {
  readonly schemaId: string;
  readonly schemaNames: string[];
  readonly recordKey: string;

  constructor(schemaId: string, schemaNames: string[], recordKey: string, message?: string) {
    super(message ?? `Schema id "${schemaId}" cannot describe record "${recordKey}": ${schemaNames.map((name) => `"${name}"`).join(", ")}.`);
    this.name = "SchemaConflictError";
    this.schemaId = schemaId;
    this.schemaNames = schemaNames;
    this.recordKey = recordKey;
  }
}

/**
 * A store file this runtime cannot read: not exactly one complete store (truncated, bytes after it, a missing or extra slot),
 * a header or record it does not know, or a body it cannot decode. Open, flush and every rewrite refuse a file with this
 * error, and a refused file is left as it was. `cause` is what the reader choked on.
 */
export class StoreUnreadableError extends Error {
  readonly filePath: string;

  constructor(filePath: string, cause: unknown) {
    super(`CultCache store ${filePath} is not readable: ${cause instanceof Error ? cause.message : String(cause)}`, { cause });
    this.name = "StoreUnreadableError";
    this.filePath = filePath;
  }
}

/** Refuses a persisted record with more slots than v1 defines, naming its key and schema id. */
export function requireV1RecordSlots(record: unknown[]): void {
  if (record.length > PERSISTED_RECORD_SLOTS) {
    throw new Error(
      `CultCache record "${String(record[0])}" (schema "${String(record[1])}") has ${record.length} slots; ` +
        `this runtime reads ${PERSISTED_RECORD_SLOTS}. The store needs a runtime that resolves document variants.`,
    );
  }
}

/** Refuses a store array with more or fewer slots than the header, the schema catalog and the records; a header alone is not a store. */
export function requireStoreSlots(decoded: unknown[]): void {
  if (decoded.length !== STORE_SLOTS) {
    throw new Error(`CultCache store has ${decoded.length} top-level slots; this runtime reads ${STORE_SLOTS} (header, schema catalog, records).`);
  }
}
