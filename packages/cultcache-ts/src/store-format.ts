export const STORE_FORMAT_VERSION = "cultcache.store.v1";

const STORE_FORMAT_PREFIX = "cultcache.store.";
const PERSISTED_RECORD_SLOTS = 4;

/**
 * True for a v1 snapshot, false for anything that is not a CultCache store header (the legacy
 * envelope array). Any other `cultcache.store.*` header is refused by name.
 */
export function isV1Snapshot(decoded: unknown): decoded is unknown[] {
  if (!Array.isArray(decoded) || typeof decoded[0] !== "string" || !decoded[0].startsWith(STORE_FORMAT_PREFIX)) {
    return false;
  }

  if (decoded[0] !== STORE_FORMAT_VERSION) {
    throw new Error(
      `CultCache store format "${decoded[0]}" is not one this runtime reads; it reads "${STORE_FORMAT_VERSION}" only.`,
    );
  }

  return true;
}

/** Refuses a persisted record with more slots than v1 defines, naming its key and schema id. */
export function requireV1RecordSlots(record: unknown[]): void {
  if (record.length > PERSISTED_RECORD_SLOTS) {
    throw new Error(
      `CultCache record "${String(record[0])}" (schema "${String(record[1])}") has ${record.length} slots, more than ` +
        `the ${PERSISTED_RECORD_SLOTS} of a ${STORE_FORMAT_VERSION} record, so this is not a valid store.`,
    );
  }
}
