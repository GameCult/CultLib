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

  refuseUnreadFormat(decoded[0]);
  return true;
}

/**
 * Refuses a store whose header names a format this runtime does not read, judging the header
 * alone: the leading array's first element, when it is a complete string. Bytes with no such
 * header (a legacy array, an empty, truncated or garbled file) are not refused here.
 */
export function refuseUnreadStoreBytes(bytes: Uint8Array): void {
  const header = peekHeader(bytes);
  if (header !== undefined && header.startsWith(STORE_FORMAT_PREFIX)) {
    refuseUnreadFormat(header);
  }
}

function refuseUnreadFormat(header: string): void {
  if (header !== STORE_FORMAT_VERSION) {
    throw new Error(
      `CultCache store format ${describeHeader(header)} is not one this runtime reads; it reads "${STORE_FORMAT_VERSION}" only.`,
    );
  }
}

// The first element of a leading non-empty MessagePack array when it is a complete UTF-8
// string; anything else is undefined.
function peekHeader(bytes: Uint8Array): string | undefined {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const lead = bytes[0];
  let offset: number;
  if (lead === undefined) return undefined;
  if (lead >= 0x91 && lead <= 0x9f) offset = 1;
  else if (lead === 0xdc && bytes.length >= 3 && view.getUint16(1) > 0) offset = 3;
  else if (lead === 0xdd && bytes.length >= 5 && view.getUint32(1) > 0) offset = 5;
  else return undefined;
  const tag = bytes[offset];
  let length: number;
  if (tag === undefined) return undefined;
  if (tag >= 0xa0 && tag <= 0xbf) { length = tag & 0x1f; offset += 1; }
  else if (tag === 0xd9 && bytes.length >= offset + 2) { length = bytes[offset + 1]!; offset += 2; }
  else if (tag === 0xda && bytes.length >= offset + 3) { length = view.getUint16(offset + 1); offset += 3; }
  else if (tag === 0xdb && bytes.length >= offset + 5) { length = view.getUint32(offset + 1); offset += 5; }
  else return undefined;
  if (bytes.length < offset + length) return undefined;
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes.subarray(offset, offset + length));
  } catch {
    return undefined;
  }
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
