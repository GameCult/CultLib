import { lstatSync, readFileSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { documentPayloadBytes, documentPayloadFromBytes } from "./document";
import { commitStoreFile, withStoreWriteLock } from "./store-file";
import { STORE_FORMAT_VERSION, SchemaConflictError, type StoreFormat, StoreUnreadableError, isStoreSnapshot, refuseUnreadStoreBytes, requireStoreSlots, requireV1RecordSlots, schemaIdForEnvelope, writtenOwns } from "./store-format";
import type {
  CacheBackingStore,
  CultCacheEnvelope,
  CultCacheSchemaCatalogEntry,
  CultCacheSchemaCatalogMember,
  PushAllOptions,
} from "./types";

const envelopeSchema = z.object({
  key: z.string().min(1),
  type: z.string().min(1),
  payload: z.instanceof(Uint8Array),
  storedAt: z.string().min(1),
  schemaId: z.string().min(1).optional(),
});

const envelopeArraySchema = z.array(envelopeSchema);
const legacyEnvelopeArraySchema = z.array(
  z.object({
    key: z.string().min(1),
    type: z.string().min(1),
    payload: z.unknown(),
    storedAt: z.string().min(1),
  }),
);

export class SingleFileMessagePackBackingStore implements CacheBackingStore {
  readonly filePath: string;

  #writeQueue: Promise<void> = Promise.resolve();

  // The header this store read; a rewrite keeps it, so a store that holds element ids never sheds its marker.
  #format: StoreFormat = STORE_FORMAT_VERSION;

  constructor(filePath: string) {
    this.filePath = resolve(filePath);
  }

  async pullAll(): Promise<CultCacheEnvelope[]> {
    // A pure read: the disk decides the header, and a legacy file is normalized in memory only. The file is rewritten
    // as v1 by the next push, delete or flush, which merges what this read returns; opening never replaces bytes the
    // consumer's codec has not yet accepted.
    const disk = this.#readDisk();
    this.#format = disk.format;
    return disk.envelopes;
  }

  async push(entry: CultCacheEnvelope): Promise<void> {
    await this.#enqueue(async () => {
      const disk = this.#readDisk();
      this.#format = disk.format;
      const existing = disk.envelopes;
      requireOneTypePerKey(this.filePath, [entry], existing);
      const filtered = existing.filter((candidate) => candidate.key !== entry.key);
      await this.#writeAll([entry], filtered);
    });
  }

  async delete(entry: CultCacheEnvelope): Promise<void> {
    await this.#enqueue(async () => {
      const disk = this.#readDisk();
      this.#format = disk.format;
      const existing = disk.envelopes;
      // The record the file stores at the key is the one removed when the removal's schema owns its stored id: the
      // arrived record's type is its schema's name, which a type of another name does not carry.
      const filtered = existing.filter(
        (candidate) => !(candidate.key === entry.key && (schemaIdForEnvelope(candidate) === schemaIdForEnvelope(entry) || writtenOwns(entry, schemaIdForEnvelope(candidate)))),
      );
      await this.#writeAll([], filtered);
    });
  }

  async pushAll(entries: CultCacheEnvelope[], options: PushAllOptions = {}): Promise<void> {
    await this.#enqueue(async () => {
      // A flush of the whole store writes the header the file on disk carries, read now: a file marked for element ids
      // stays marked, and one that is not (or is gone or legacy) is written unmarked. The file is read by the same
      // reader as `pullAll`, so one it would refuse (not exactly one store, a variant, a body it cannot decode) is refused
      // and left as it is. A soft flush asks that reader too: nothing at the path is written, a store is left alone.
      try {
        const disk = this.#readDisk();
        if (options.soft && !disk.absent) {
          return;
        }

        this.#format = disk.format;
      } catch (error) {
        // Only a dangling link reaches here with ENOENT: it holds no store, so a whole-store flush has no header to keep and
        // replaces the link (R3 pins this). A soft flush does not replace what it cannot read, and every merging writer
        // has already refused the link when it read the current entries.
        if (options.soft || (error as NodeJS.ErrnoException).code !== "ENOENT") {
          throw error;
        }

        this.#format = STORE_FORMAT_VERSION;
      }

      requireOneTypePerKey(this.filePath, entries, []);
      // A key is held once: of same-type entries sharing a key in one batch, the last is kept.
      await this.#writeAll([...new Map(entries.map((entry) => [entry.key, entry])).values()], []);
    });
  }

  // The one reader of the store file, asked by open, push, delete and flush alike; see readSingleFileStore.
  #readDisk(): SingleFileStoreRead {
    return readSingleFileStore(this.filePath);
  }

  async #enqueue<T>(operation: () => Promise<T>): Promise<T> {
    let result!: T;

    const next = this.#writeQueue.then(() =>
      withStoreWriteLock(this.filePath, async () => {
        result = await operation();
      }),
    );

    this.#writeQueue = next.then(
      () => undefined,
      () => undefined,
    );

    await next;
    return result;
  }

  async #writeAll(supplied: CultCacheEnvelope[], arrived: CultCacheEnvelope[]): Promise<void> {
    await mkdir(dirname(this.filePath), { recursive: true });

    await commitStoreFile(this.filePath, encode(encodeSnapshot(supplied, arrived, this.#format)));
  }
}

// A store holds a key once, and the reader refuses a file that holds it twice, so a write that would put a second type under a
// key (against a record on disk, or within its own batch) is refused before anything is written. Retyping a record is a delete
// of the old type followed by a put of the new one.
function requireOneTypePerKey(path: string, supplied: CultCacheEnvelope[], held: CultCacheEnvelope[]): void {
  const typeByKey = new Map<string, CultCacheEnvelope>();
  for (const entry of held) {
    typeByKey.set(entry.key, entry);
  }
  for (const entry of supplied) {
    const prior = typeByKey.get(entry.key);
    if (prior !== undefined && prior.type !== entry.type && !writtenOwns(entry, schemaIdForEnvelope(prior))) {
      throw new SchemaConflictError(
        schemaIdForEnvelope(entry),
        [prior.type, entry.type],
        entry.key,
        `CultCache store ${path} holds record "${entry.key}" as type "${prior.type}", so type "${entry.type}" cannot be written under the same key; remove the record first.`,
      );
    }
    typeByKey.set(entry.key, entry);
  }
}

/**
 * The one reader of a CultCache single-file store, for this package's store and for any consumer that must read a
 * store file (an authority bundle, say) rather than decode it itself. A path with nothing at it is an absent, empty,
 * unmarked store. A dangling symbolic link is something: reading it as empty would let a writer rename a file over
 * the link and move the store off its volume, so it throws with code ENOENT, as does any other failure to reach the
 * file. A zero-byte file is not a store and, like anything that does not decode completely, throws StoreUnreadableError.
 * The read is synchronous so a consumer that loads authority at start-up needs no second reader to stay synchronous.
 */
export function readSingleFileStore(path: string): SingleFileStoreRead {
  const data = readStoreBytes(path);
  if (data === undefined) {
    return { absent: true, format: STORE_FORMAT_VERSION, envelopes: [] };
  }

  try {
    return { absent: false, ...decodeStoreFile(data) };
  } catch (error) {
    throw new StoreUnreadableError(path, error);
  }
}

/**
 * The bytes at `path`, or undefined when nothing is there. Nothing is there only when both looks agree: the read found no
 * file and the follow-up lstat found no entry. Every other outcome is an error, because the path may have changed
 * between the two calls (a link in a parent swapped for a file, a store created or removed): an lstat failure other than
 * ENOENT is thrown as it is, and an entry the lstat does find, a dangling link or one that appeared since the read, is
 * thrown with code ENOENT rather than read as an empty store. `lstat` is a seam so a test can stage the second look;
 * this module exports the function for that test, and the package index does not.
 */
export function readStoreBytes(path: string, lstat: (path: string) => { isSymbolicLink(): boolean } = lstatSync): Uint8Array | undefined {
  try {
    return readFileSync(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
      throw error;
    }
  }

  let entry: { isSymbolicLink(): boolean };
  try {
    entry = lstat(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      return undefined;
    }

    throw error;
  }

  const what = entry.isSymbolicLink() ? "is a symbolic link whose target does not exist" : "appeared while it was being read";
  throw Object.assign(new Error(`CultCache store ${path} ${what}.`), { code: "ENOENT" });
}

type PersistedRecord = {
  key: string;
  schemaId: string;
  storedAt: string;
  payload: Uint8Array;
};

type DecodedSnapshot = {
  format: StoreFormat;
  catalogBySchemaId: Map<string, CultCacheSchemaCatalogEntry>;
  records: PersistedRecord[];
};

function encodeSnapshot(supplied: CultCacheEnvelope[], arrived: CultCacheEnvelope[], format: StoreFormat): unknown[] {
  // A key is held once: a supplied record replaces the arrived record under its key whatever the type.
  const replaced = new Set(supplied.map((entry) => entry.key));
  const kept = arrived.filter((entry) => !replaced.has(entry.key));
  const entries = [...kept, ...supplied];
  const catalog = catalogEntriesFor([
    ...kept.map((envelope) => ({ envelope, registered: false })),
    ...supplied.map((envelope) => ({ envelope, registered: true })),
  ]).sort((left, right) => compareOrdinal(left.schemaName, right.schemaName) || compareOrdinal(left.schemaId, right.schemaId));
  const records = [...entries]
    .sort((left, right) => compareOrdinal(left.key, right.key))
    .map((entry) => [
      entry.key,
      schemaIdForEnvelope(entry),
      entry.storedAt,
      documentPayloadBytes(entry.payload),
    ]);

  return [
    format,
    catalog.map(encodeCatalogEntry),
    records,
  ];
}

// The catalog a write leaves, derived from the records being written and nothing else. For each schema id a record carries, one
// entry that publishes it: the entry that owns the id (a registered one, else one that arrived with the records), else one that
// lists it as compatible (registered first). Entries are written as chosen, never merged; a registered entry wins over an arrived
// one with the same own id. Records of different types under one id, two arrived entries with the same own id and different schema
// names, or a record no chosen entry publishes, refuse the write. An envelope the caller supplied is registered; one read back
// from the file is arrived.
function catalogEntriesFor(
  records: Array<{ envelope: CultCacheEnvelope; registered: boolean }>,
): CultCacheSchemaCatalogEntry[] {
  const entries: Array<{ entry: CultCacheSchemaCatalogEntry; registered: boolean }> = [];
  const byId = new Map<string, CultCacheEnvelope[]>();
  const tiers = new Map<string, Array<{ envelope: CultCacheEnvelope; registered: boolean; entry: CultCacheSchemaCatalogEntry }>>();
  for (const { envelope, registered } of records) {
    const schemaId = schemaIdForEnvelope(envelope);
    const supplied = envelope.catalogEntry;
    const publishes =
      supplied !== undefined && (supplied.schemaId === schemaId || (supplied.compatibleSchemaIds ?? []).includes(schemaId));
    const entry = publishes && supplied ? supplied : defaultEntryFor(envelope);
    entries.push({ entry, registered });
    byId.set(schemaId, [...(byId.get(schemaId) ?? []), envelope]);
    tiers.set(schemaId, [...(tiers.get(schemaId) ?? []), { envelope, registered, entry }]);
  }

  const lists = (entry: CultCacheSchemaCatalogEntry, schemaId: string): boolean =>
    (entry.compatibleSchemaIds ?? [entry.schemaId]).includes(schemaId);
  const chosen = new Map<string, { entry: CultCacheSchemaCatalogEntry; registered: boolean }>();
  for (const schemaId of [...byId.keys()].sort(compareOrdinal)) {
    const carrying = byId.get(schemaId)!;
    const recordKey = carrying.map((envelope) => envelope.key).sort(compareOrdinal)[0]!;
    // Records of one tier under one id are one type. Across tiers the id keeps the type its records resolve to, so a write may
    // not retype records that are already there: unless the entries of both tiers own the id, which is a rename of the schema
    // under a stable id, and the registered descriptor wins. A read-back record's type is its schema's name, so tiers compare
    // the names of the entries that publish the id.
    const carried = tiers.get(schemaId)!;
    for (const tier of [true, false]) {
      const types = [...new Set(carried.filter((candidate) => candidate.registered === tier).map((candidate) => candidate.envelope.type))].sort(
        compareOrdinal,
      );
      if (types.length > 1) {
        throw new SchemaConflictError(schemaId, types, recordKey);
      }
    }

    const tierNames = (tier: boolean): string[] => [
      ...new Set(carried.filter((candidate) => candidate.registered === tier).map((candidate) => candidate.entry.schemaName)),
    ];
    const ownedBy = (tier: boolean): boolean =>
      carried.some((candidate) => candidate.registered === tier && candidate.entry.schemaId === schemaId);
    const [registeredName, arrivedName] = [tierNames(true)[0], tierNames(false)[0]];
    if (registeredName !== undefined && arrivedName !== undefined && registeredName !== arrivedName && !(ownedBy(true) && ownedBy(false))) {
      throw new SchemaConflictError(schemaId, [registeredName, arrivedName].sort(compareOrdinal), recordKey);
    }

    const ownRegistered = canonical(entries.filter((candidate) => candidate.registered && candidate.entry.schemaId === schemaId))[0];
    const ownArrived = canonical(entries.filter((candidate) => !candidate.registered && candidate.entry.schemaId === schemaId));
    // Entries of one tier that share an own id must be one schema; a registered entry settles a disagreement among arrived ones.
    const ownRegisteredAll = canonical(entries.filter((candidate) => candidate.registered && candidate.entry.schemaId === schemaId));
    const names = [
      ...new Set((ownRegisteredAll.length > 0 ? ownRegisteredAll : ownArrived).map((candidate) => candidate.entry.schemaName)),
    ].sort(compareOrdinal);
    if (names.length > 1) {
      throw new SchemaConflictError(schemaId, names, recordKey);
    }

    let pick: { entry: CultCacheSchemaCatalogEntry; registered: boolean } | undefined;
    if (ownRegistered !== undefined) {
      pick = ownRegistered;
    } else if (ownArrived.length > 0) {
      pick = ownArrived[0];
    } else {
      pick =
        canonical(entries.filter((candidate) => candidate.registered && lists(candidate.entry, schemaId)))[0] ??
        canonical(entries.filter((candidate) => !candidate.registered && lists(candidate.entry, schemaId)))[0];
    }

    if (pick === undefined) {
      continue;
    }

    const prior = chosen.get(pick.entry.schemaId);
    if (prior !== undefined && prior.registered && !pick.registered) {
      continue;
    }

    chosen.set(pick.entry.schemaId, pick);
  }

  for (const [schemaId, carrying] of byId) {
    if (![...chosen.values()].some(({ entry }) => entry.schemaId === schemaId || lists(entry, schemaId))) {
      throw new SchemaConflictError(
        schemaId,
        [...chosen.values()].map(({ entry }) => entry.schemaName).sort(compareOrdinal),
        carrying.map((envelope) => envelope.key).sort(compareOrdinal)[0]!,
      );
    }
  }

  return [...chosen.values()].map(({ entry }) => entry);
}

// Entries that tie, in one fixed order, so the catalog does not depend on the order they arrive in.
function canonical<T extends { entry: CultCacheSchemaCatalogEntry }>(candidates: T[]): T[] {
  const key = (candidate: T): string =>
    [candidate.entry.schemaName, candidate.entry.contentHash ?? "", (candidate.entry.compatibleSchemaIds ?? []).join(",")].join("\u0000");
  return [...candidates].sort((left, right) => compareOrdinal(key(left), key(right)));
}

function defaultEntryFor(entry: CultCacheEnvelope): CultCacheSchemaCatalogEntry {
  const schemaId = schemaIdForEnvelope(entry);
  return {
    schemaId,
    schemaName: entry.type,
    schemaVersion: `${entry.type}.v1`,
    contentHash: schemaId,
    canonicalSchemaJson: JSON.stringify({
      schemaName: entry.type,
      schemaVersion: `${entry.type}.v1`,
      members: [],
    }),
    compatibleSchemaIds: [schemaId],
    members: [],
  };
}

function encodeCatalogEntry(entry: CultCacheSchemaCatalogEntry): unknown[] {
  return [
    entry.schemaId,
    entry.schemaName,
    entry.schemaVersion,
    entry.contentHash,
    entry.canonicalSchemaJson,
    [...(entry.compatibleSchemaIds ?? [entry.schemaId])],
    [...(entry.members ?? [])].map(encodeCatalogMember),
  ];
}

function encodeCatalogMember(member: CultCacheSchemaCatalogMember): unknown[] {
  return [
    member.slot,
    member.memberName,
    member.typeName,
    member.isReference === true,
    member.isMany === true,
    member.targetSchemaName ?? null,
    member.isName === true,
    member.indexAlias ?? null,
  ];
}

export type SingleFileStoreRead = {
  // True when nothing is at the path; the other fields then describe an empty unmarked store.
  absent: boolean;
  format: StoreFormat;
  envelopes: CultCacheEnvelope[];
};

function decodeStoreFile(data: Uint8Array): Omit<SingleFileStoreRead, "absent"> {
  // The stamp is judged before the body is decoded, as Rust's store_header does: a newer store is refused by name.
  refuseUnreadStoreBytes(data);
  const decoded = decode(data);
  const snapshot = decodeSnapshot(decoded);
  if (snapshot) {
    const envelopes = snapshot.records.map((record): CultCacheEnvelope => {
      const catalogEntry = snapshot.catalogBySchemaId.get(record.schemaId);
      if (!catalogEntry) {
        throw new Error(`CultCache persisted record "${record.key}" references missing schema id "${record.schemaId}".`);
      }

      return {
        key: record.key,
        type: catalogEntry.schemaName,
        schemaId: record.schemaId,
        catalogEntry,
        payload: documentPayloadFromBytes(record.payload),
        storedAt: record.storedAt,
      };
    });
    return { format: snapshot.format, envelopes };
  }

  const legacy = decodeLegacyEnvelopeArray(decoded);
  if (!legacy) {
    throw new Error("CultCache file is not a recognized CultCache MessagePack store.");
  }

  requireUniqueKeys(legacy);
  const normalized = legacy.map((entry) => ({ ...entry, payload: normalizePayload(entry.payload) }));
  return { format: STORE_FORMAT_VERSION, envelopes: envelopeArraySchema.parse(normalized).map((entry) => ({ ...entry, payload: documentPayloadFromBytes(entry.payload) })) as CultCacheEnvelope[] };
}

function decodeSnapshot(decoded: unknown): DecodedSnapshot | undefined {
  if (!isStoreSnapshot(decoded)) {
    return undefined;
  }

  requireStoreSlots(decoded);
  const catalogRaw = decoded[1];
  const recordsRaw = decoded[2];
  if (!Array.isArray(catalogRaw) || !Array.isArray(recordsRaw)) {
    throw new Error("CultCache v1 snapshot must contain a schema catalog and record array.");
  }

  // A schema id names the entry that has it as its own id; only an id no entry owns names the entry that lists it as compatible.
  const catalogEntries = catalogRaw.map(decodeCatalogEntry);
  const catalogBySchemaId = new Map<string, CultCacheSchemaCatalogEntry>();
  for (const catalogEntry of catalogEntries) {
    if (!catalogBySchemaId.has(catalogEntry.schemaId)) {
      catalogBySchemaId.set(catalogEntry.schemaId, catalogEntry);
    }
  }
  for (const catalogEntry of catalogEntries) {
    for (const compatibleSchemaId of catalogEntry.compatibleSchemaIds ?? []) {
      if (!catalogBySchemaId.has(compatibleSchemaId)) {
        catalogBySchemaId.set(compatibleSchemaId, catalogEntry);
      }
    }
  }

  const records = recordsRaw.map(decodeRecord);
  requireUniqueKeys(records);
  return { format: decoded[0], catalogBySchemaId, records };
}

// A store holds a key once, whatever the type: a repeat is refused in a v1 snapshot and in a legacy envelope array alike.
function requireUniqueKeys(records: Array<{ key: string }>): void {
  const seen = new Set<string>();
  for (const { key } of records) {
    if (seen.has(key)) {
      throw new Error(`CultCache record "${key}" appears more than once.`);
    }
    seen.add(key);
  }
}

function decodeCatalogEntry(value: unknown): CultCacheSchemaCatalogEntry {
  if (!Array.isArray(value)) {
    throw new Error("CultCache schema catalog entries must be MessagePack arrays.");
  }

  const [
    schemaId = "",
    schemaName = "",
    schemaVersion = "",
    contentHash = "",
    canonicalSchemaJson = "",
    compatibleSchemaIds = [],
    members = [],
  ] = value;

  if (!isNonEmptyString(schemaId) || !isNonEmptyString(schemaName)) {
    throw new Error("CultCache schema catalog entries must declare schemaId and schemaName.");
  }

  if (!Array.isArray(compatibleSchemaIds) || !compatibleSchemaIds.every(isNonEmptyString)) {
    throw new Error(`CultCache schema catalog entry "${schemaId}" has invalid compatible schema ids.`);
  }

  if (!Array.isArray(members)) {
    throw new Error(`CultCache schema catalog entry "${schemaId}" has invalid members.`);
  }

  if (typeof canonicalSchemaJson !== "string") {
    throw new Error(`CultCache schema catalog entry "${schemaId}" has an invalid canonical schema.`);
  }

  return {
    schemaId,
    schemaName,
    schemaVersion: isNonEmptyString(schemaVersion) ? schemaVersion : `${schemaName}.v1`,
    contentHash: isNonEmptyString(contentHash) ? contentHash : schemaId,
    canonicalSchemaJson,
    compatibleSchemaIds,
    members: members.map(decodeCatalogMember),
  };
}

function decodeCatalogMember(value: unknown): CultCacheSchemaCatalogMember {
  if (!Array.isArray(value)) {
    throw new Error("CultCache schema catalog members must be MessagePack arrays.");
  }

  const [
    slot = -1,
    memberName = "",
    typeName = "",
    isReference = false,
    isMany = false,
    targetSchemaName = null,
    isName = false,
    indexAlias = null,
  ] = value;

  if (!Number.isInteger(slot) || slot < 0 || !isNonEmptyString(memberName) || !isNonEmptyString(typeName)) {
    throw new Error("CultCache schema catalog member has invalid slot, name, or type.");
  }

  return {
    slot,
    memberName,
    typeName,
    isReference: isReference === true,
    isMany: isMany === true,
    targetSchemaName: typeof targetSchemaName === "string" ? targetSchemaName : null,
    isName: isName === true,
    indexAlias: typeof indexAlias === "string" ? indexAlias : null,
  };
}

function decodeRecord(value: unknown): PersistedRecord {
  if (!Array.isArray(value)) {
    throw new Error("CultCache persisted records must be MessagePack arrays.");
  }

  requireV1RecordSlots(value);
  const [key = "", schemaId = "", storedAt = "", payload = new Uint8Array()] = value;
  if (!isNonEmptyString(key) || !isNonEmptyString(schemaId) || !isNonEmptyString(storedAt)) {
    throw new Error("CultCache persisted records must declare key, schemaId, and storedAt.");
  }

  return {
    key,
    schemaId,
    storedAt,
    payload: normalizePayload(payload),
  };
}

function decodeLegacyEnvelopeArray(decoded: unknown): Array<{
  key: string;
  type: string;
  payload: unknown;
  storedAt: string;
  schemaId?: string;
}> | undefined {
  if (!Array.isArray(decoded)) {
    return undefined;
  }

  return legacyEnvelopeArraySchema.parse(decoded) as Array<{
    key: string;
    type: string;
    payload: unknown;
    storedAt: string;
    schemaId?: string;
  }>;
}

function normalizePayload(payload: unknown): Uint8Array {
  if (payload instanceof Uint8Array) {
    return payload;
  }

  if (
    isObject(payload) &&
    payload.type === "Buffer" &&
    Array.isArray(payload.data) &&
    payload.data.every((value) => Number.isInteger(value) && value >= 0 && value <= 255)
  ) {
    return Uint8Array.from(payload.data);
  }

  if (Array.isArray(payload) && payload.every((value) => Number.isInteger(value) && value >= 0 && value <= 255)) {
    return Uint8Array.from(payload);
  }

  return encode(payload);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
