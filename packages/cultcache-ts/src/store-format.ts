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
 * The header of a decoded store file about to be rewritten: `undefined` for a legacy envelope array (an array whose first
 * slot is not a string, empty included), the header otherwise. A value that is not an array, or whose first slot is a string
 * but not a header this runtime reads, is refused, so a writer never overwrites a file it cannot see. Bytes after the array
 * are refused by `decode` itself.
 */
export function storeHeader(decoded: unknown): StoreFormat | undefined {
  if (!Array.isArray(decoded)) {
    throw new Error("CultCache store is not a MessagePack array; it is not rewritten.");
  }

  if (typeof decoded[0] !== "string") {
    return undefined;
  }

  if (!isStoreSnapshot(decoded)) {
    throw new Error(`CultCache store's first slot is the string "${decoded[0]}", not a store header; it is not rewritten.`);
  }

  return decoded[0];
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
