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
      `CultCache store format ${describeHeader(decoded[0])} is not one this runtime reads; it reads "${STORE_FORMAT_VERSION}" only.`,
    );
  }

  return true;
}

/**
 * A store header as a refusal may show it: echoed in the shape `cultcache.store.v<digits>`,
 * and otherwise described only by its length, since the bytes are the store's.
 */
function describeHeader(header: string): string {
  return /^cultcache\.store\.v[0-9]+$/u.test(header)
    ? `"${header}"`
    : `an unrecognised ${STORE_FORMAT_PREFIX}* header of ${new TextEncoder().encode(header).length} bytes`;
}

/**
 * A record key or schema id as a refusal may show it: a string is an identity and is named;
 * anything else is described by its type, since the value is the store's.
 */
function describeIdentity(value: unknown): string {
  if (typeof value === "string") {
    return `"${value}"`;
  }

  const kind = Array.isArray(value)
    ? "array"
    : value instanceof Uint8Array
      ? "bytes"
      : value === null
        ? "nil"
        : typeof value;
  return `<${kind}>`;
}

/** Refuses a persisted record with more slots than v1 defines, naming its key and schema id. */
export function requireV1RecordSlots(record: unknown[]): void {
  if (record.length > PERSISTED_RECORD_SLOTS) {
    throw new Error(
      `CultCache record ${describeIdentity(record[0])} (schema ${describeIdentity(record[1])}) has ${record.length} slots, more than ` +
        `the ${PERSISTED_RECORD_SLOTS} of a ${STORE_FORMAT_VERSION} record, so this is not a valid store.`,
    );
  }
}
