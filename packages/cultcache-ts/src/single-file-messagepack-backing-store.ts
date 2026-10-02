import { lstat, mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { STORE_FORMAT_VERSION, SchemaConflictError, type StoreFormat, StoreUnreadableError, isStoreSnapshot, requireStoreSlots, requireV1RecordSlots } from "./store-format";
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
    // The disk decides the header: a file that is gone or legacy is not marked.
    this.#format = STORE_FORMAT_VERSION;
    const disk = await this.#readDisk();
    this.#format = disk.format;
    if (disk.repairedLegacyPayload) {
      await this.#writeAll([], disk.envelopes);
    }

    return disk.envelopes;
  }

  async push(entry: CultCacheEnvelope): Promise<void> {
    await this.#enqueue(async () => {
      const existing = await this.pullAll();
      const filtered = existing.filter(
        (candidate) => !(candidate.type === entry.type && candidate.key === entry.key),
      );
      await this.#writeAll([entry], filtered);
    });
  }

  async delete(entry: CultCacheEnvelope): Promise<void> {
    await this.#enqueue(async () => {
      const existing = await this.pullAll();
      const filtered = existing.filter(
        (candidate) => !(candidate.type === entry.type && candidate.key === entry.key),
      );
      await this.#writeAll([], filtered);
    });
  }

  async pushAll(entries: CultCacheEnvelope[], options: PushAllOptions = {}): Promise<void> {
    await this.#enqueue(async () => {
      if (options.soft && (await readStore(this.filePath)) !== undefined) {
        return;
      }

      // A flush of the whole store writes the header the file on disk carries, read now: a file marked for element ids
      // stays marked, and one that is not (or is gone or legacy) is written unmarked. The file is read by the same
      // reader as `pullAll`, so one it would refuse (not exactly one store, a variant, a body it cannot decode) is refused
      // and left as it is.
      try {
        this.#format = (await this.#readDisk()).format;
      } catch (error) {
        // Only a dangling link reaches here with ENOENT: it holds no store, so a whole-store flush has no header to keep and
        // replaces the link (R3 pins this). Every merging writer has already refused it when it read the current entries.
        if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
          throw error;
        }

        this.#format = STORE_FORMAT_VERSION;
      }

      await this.#writeAll(entries, []);
    });
  }

  // The one reader of the store file, asked by open, push, delete and flush alike. A file that is gone is an
  // empty unmarked store; a zero-byte file is not a store and is refused. Anything else must decode completely
  // or the read throws StoreUnreadableError.
  async #readDisk(): Promise<DiskStore> {
    // An empty file is not a store, since no CultCache writer leaves one, so it is decoded and refused.
    // Only a read that finds nothing at the path is an empty store.
    const data = await readStore(this.filePath);
    if (data === undefined) {
      return { format: STORE_FORMAT_VERSION, envelopes: [], repairedLegacyPayload: false };
    }

    try {
      return decodeStoreFile(data);
    } catch (error) {
      throw new StoreUnreadableError(this.filePath, error);
    }
  }

  async #enqueue<T>(operation: () => Promise<T>): Promise<T> {
    let result!: T;

    const next = this.#writeQueue.then(async () => {
      result = await operation();
    });

    this.#writeQueue = next.then(
      () => undefined,
      () => undefined,
    );

    await next;
    return result;
  }

  async #writeAll(supplied: CultCacheEnvelope[], arrived: CultCacheEnvelope[]): Promise<void> {
    await mkdir(dirname(this.filePath), { recursive: true });

    const tempPath = `${this.filePath}.tmp-${process.pid}-${Date.now()}-${Math.random()
      .toString(36)
      .slice(2)}`;

    try {
      await writeFile(tempPath, encode(encodeSnapshot(supplied, arrived, this.#format)));
      await renameWithRetry(tempPath, this.filePath);
    } catch (error) {
      await rm(tempPath, { force: true }).catch(() => undefined);
      throw error;
    }
  }
}

/**
 * Reads a store file, or returns undefined when nothing is at its path. A dangling symbolic
 * link is something: reading it as empty would let the writer rename a file over the link
 * and move the store off its volume. Any other failure to reach the file is thrown.
 */
async function readStore(path: string): Promise<Uint8Array | undefined> {
  try {
    return await readFile(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
      throw error;
    }
  }

  try {
    await lstat(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      return undefined;
    }

    throw error;
  }

  throw Object.assign(new Error(`CultCache store ${path} is a symbolic link whose target does not exist.`), { code: "ENOENT" });
}

async function renameWithRetry(source: string, destination: string): Promise<void> {
  let delayMs = 10;
  let lastError: unknown;
  for (let attempt = 0; attempt < 10; attempt++) {
    try {
      await rename(source, destination);
      return;
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code !== "EPERM" && code !== "EBUSY" && code !== "EACCES") {
        throw error;
      }

      lastError = error;
      await delay(delayMs);
      delayMs *= 2;
    }
  }

  throw lastError;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
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
  const replaced = new Set(supplied.map((entry) => `${entry.type}::${entry.key}`));
  const kept = arrived.filter((entry) => !replaced.has(`${entry.type}::${entry.key}`));
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
      entry.payload,
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

function schemaIdForEnvelope(entry: CultCacheEnvelope): string {
  return entry.schemaId ?? entry.type;
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

type DiskStore = {
  format: StoreFormat;
  envelopes: CultCacheEnvelope[];
  // A legacy file whose payloads were not bytes: reading it repairs them, and the store rewrites the file.
  repairedLegacyPayload: boolean;
};

function decodeStoreFile(data: Uint8Array): DiskStore {
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
        payload: record.payload,
        storedAt: record.storedAt,
      };
    });
    return { format: snapshot.format, envelopes, repairedLegacyPayload: false };
  }

  const legacy = decodeLegacyEnvelopeArray(decoded);
  if (!legacy) {
    throw new Error("CultCache file is not a recognized CultCache MessagePack store.");
  }

  let repairedLegacyPayload = false;
  const normalized = legacy.map((entry) => {
    const payload = normalizePayload(entry.payload);
    if (payload !== entry.payload) {
      repairedLegacyPayload = true;
    }

    return {
      ...entry,
      payload,
    };
  });
  return { format: STORE_FORMAT_VERSION, envelopes: envelopeArraySchema.parse(normalized) as CultCacheEnvelope[], repairedLegacyPayload };
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
  return { format: decoded[0], catalogBySchemaId, records };
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

  return {
    schemaId,
    schemaName,
    schemaVersion: isNonEmptyString(schemaVersion) ? schemaVersion : `${schemaName}.v1`,
    contentHash: isNonEmptyString(contentHash) ? contentHash : schemaId,
    canonicalSchemaJson: typeof canonicalSchemaJson === "string" ? canonicalSchemaJson : "",
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
