import assert from "node:assert/strict";
import { copyFile, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { codecs } from "../src";
import { CultCache } from "../src/cult-cache";
import { defineDocumentType, documentPayloadBytes, documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore, readSingleFileStore } from "../src/single-file-messagepack-backing-store";
import { SchemaConflictError, StoreUnreadableError } from "../src/store-format";
import type { CacheBackingStore, CultCacheEnvelope, CultCacheUnreadSlots } from "../src/types";
import {
  type Surface, envelopeOf, golden, hex, memoryStore, read, slotUnitDocument, surfaceCodec, surfaceDeclaringDocument, surfaceDocument, surfaceSchema, vectors,
} from "./union-surface-fixture";

// cultlib-gaps:cut:slot-readonly-ts. A payload carrying a slot past its reader's declared slots, at any depth, is read, held
// read-only, and every write or removal of its key is refused before a store is touched. The vectors are written by the C#
// reference: tests/vectors/union-parity/README.md, "Extra trailing slot" and "Slot stores".

const { slots, string } = codecs;
const SLOT_VALUE = "extra-slot";

function mark(typeName: string, slotCount: number, declaredCount: number, depth0Only: boolean, explained = false, occurrences = 1): CultCacheUnreadSlots {
  return { typeName, slotCount, declaredCount, occurrences, depth0Only, explained };
}

// File, then the mark the README table gives for it.
const PAYLOAD_ROWS: [string, CultCacheUnreadSlots][] = [
  ["extra-slot-record.msgpack", mark("UnionSurface", 8, 7, true)],
  ["extra-slot-nested-record.msgpack", mark("Composition", 3, 2, false)],
  ["extra-slot-arm.msgpack", mark("Box", 3, 2, false)],
  ["extra-slot-list-element.msgpack", mark("Text", 3, 2, false)],
  ["depth-skipped-at-limit.msgpack", mark("UnionSurface", 8, 7, true)],
];

async function readOnlyFiles(name: string): Promise<string[]> {
  const rows = decode(await read(name)) as unknown[][];
  return rows.filter((row) => row[1] === "read-only").map((row) => row[0] as string);
}

test("the read-only payload vectors are exactly the ones this walker names: four in outcomes, one in bounds", async () => {
  const listed = [...(await readOnlyFiles("outcomes.msgpack")), ...(await readOnlyFiles("bounds-outcomes.msgpack"))];
  assert.deepEqual(listed.sort(), PAYLOAD_ROWS.map(([file]) => file).sort());
});

for (const [file, expected] of PAYLOAD_ROWS) {
  test(`${file}: read past the extra slot, held read-only with the mark, every write and removal of the key refused`, async () => {
    const document = surfaceDocument();
    const entries = [envelopeOf(await read(file))];
    const before = hex(documentPayloadBytes(entries[0]!.payload));
    const cache = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore(entries)).build();
    await cache.pullAllBackingStores();

    assert.deepEqual(cache.get(document, "surface"), golden, "the extra slot is read past");
    assert.deepEqual(cache.unreadSlots(document, "surface"), expected);
    assert.equal(cache.isWritable(document, "surface"), false);
    assert.equal(cache.isWritable(document, "another"), true);

    const refused = (error: unknown) => {
      assert.ok(error instanceof SchemaConflictError, "a SchemaConflictError");
      assert.deepEqual(error.unreadSlots, expected);
      assert.equal(error.recordKey, "surface");
      assert.ok(error.message.includes(expected.typeName), error.message);
      assert.ok(error.message.includes(`read with ${expected.slotCount} slots where this build declares ${expected.declaredCount} (${expected.occurrences} object(s)`), error.message);
      assert.ok(!error.message.includes(SLOT_VALUE), "the refusal never holds a slot value");
      return true;
    };
    await assert.rejects(() => cache.put(document, "surface", golden), refused);
    await assert.rejects(() => cache.update(document, "surface", (value) => ({ ...value!, version: 4 })), refused);
    const replacement = envelopeOf(await read("union-surface.msgpack"));
    await assert.rejects(() => cache.putEnvelope(document, replacement), refused);
    await assert.rejects(() => cache.delete(document, "surface"), refused);
    assert.equal(entries.length, 1, "nothing reached the store");
    assert.equal(hex(documentPayloadBytes(entries[0]!.payload)), before, "the held bytes are untouched");
    assert.deepEqual(cache.get(document, "surface"), golden, "the record is still held");

    await cache.put(document, "another", golden);
    assert.equal(entries.length, 2, "another key of the same store writes");
  });
}

test("a clean payload and a short one are writable: missing trailing slots keep the defaults and mark nothing", async () => {
  const document = surfaceDocument();
  const short = encode([...(decode(await read("union-surface.msgpack")) as unknown[]).slice(0, 6)]);
  const entries = [envelopeOf(await read("union-surface.msgpack")), { ...envelopeOf(short), key: "short" }];
  const cache = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore(entries)).build();
  await cache.pullAllBackingStores();
  for (const key of ["surface", "short"]) {
    assert.equal(cache.unreadSlots(document, key), undefined);
    assert.equal(cache.isWritable(document, key), true);
  }
  await cache.put(document, "short", golden);
});

test("a payload past the nesting limit is refused, never marked", async () => {
  const entries = [envelopeOf(await read("depth-skipped-past-limit.msgpack"))];
  const cache = CultCache.builder().withDocumentType(surfaceDocument()).withGenericStore(memoryStore(entries)).build();
  await assert.rejects(() => cache.pullAllBackingStores(), /nesting|depth/iu);
});

test("putEnvelope of a payload carrying extra slots lands the bytes and holds the record read-only", async () => {
  const document = surfaceDocument();
  const entries: ReturnType<typeof envelopeOf>[] = [];
  const cache = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore(entries)).build();
  const marked = envelopeOf(await read("extra-slot-nested-record.msgpack"));
  await cache.putEnvelope(document, marked);
  assert.equal(hex(documentPayloadBytes(entries[0]!.payload)), hex(await read("extra-slot-nested-record.msgpack")), "the bytes are carried");
  assert.deepEqual(cache.unreadSlots(document, "surface"), mark("Composition", 3, 2, false));
  assert.equal(cache.isWritable(document, "surface"), false);
  await assert.rejects(() => cache.put(document, "surface", golden), SchemaConflictError);
});

test("a key a store carries foreign is not writable", async () => {
  const document = surfaceDocument();
  const foreign = { ...envelopeOf(await read("union-surface.msgpack")), key: "stranger", type: "tests.unknown", schemaId: "tests.unknown.v1" };
  const cache = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore([foreign])).build();
  await cache.pullAllBackingStores();
  assert.equal(cache.isWritable(document, "stranger"), false);
  assert.equal(cache.isWritable(document, "other"), true);
});

test("a write by a type that owns the held record's id restamps it, and is refused when that record carries unread slots", async () => {
  const typed = (type: string, schemaId: string, compatibleSchemaIds: string[] = []) =>
    defineDocumentType({ type, schemaId, schemaName: type, schemaVersion: `${type}.v1`, compatibleSchemaIds, schema: surfaceSchema, codec: surfaceCodec, name: "id" });
  const older = typed("tests.surface_older", "tests.surface_older.v1");
  const newer = typed("tests.surface_newer", "tests.surface_newer.v1", ["tests.surface_older.v1"]);
  for (const [file, lands] of [["union-surface.msgpack", true], ["extra-slot-record.msgpack", false]] as const) {
    const held = { ...envelopeOf(await read(file)), type: older.type, schemaId: "tests.surface_older.v1" };
    const cache = CultCache.builder().withDocumentType(older).withDocumentType(newer).withGenericStore(memoryStore([held])).build();
    await cache.pullAllBackingStores();
    assert.equal(cache.isWritable(newer, "surface"), lands, `${file}: isWritable agrees with the write by the type that owns the id`);
    const write = cache.put(newer, "surface", golden);
    if (lands) {
      await write;
    } else {
      await assert.rejects(() => write, (error: unknown) => error instanceof SchemaConflictError && error.unreadSlots?.typeName === "UnionSurface");
    }
  }
});

// The store vectors. TS has no variant records: extra-slot-store.cc carries one (marked-variant), so the whole file is refused
// by name, and the walk runs over the file without that record.
async function copyOf(name: string): Promise<string> {
  const path = join(await mkdtemp(join(tmpdir(), "slot-readonly-")), name);
  await copyFile(join(vectors, name), path);
  return path;
}

async function withoutVariant(path: string): Promise<void> {
  const [format, catalog, records] = decode(await readFile(path)) as [unknown, unknown[], unknown[][]];
  await writeFile(path, encode([format, catalog, records.filter((record) => record[0] !== "marked-variant")]));
}

type SlotRow = [string, string, boolean, boolean | null, string | null, number | null, number | null, number | null, string | null];

async function slotRows(): Promise<SlotRow[]> {
  return decode(await read("slot-outcomes.msgpack")) as SlotRow[];
}

// The catalog entry the file publishes for a schema: its id and the reference's content hash, which a store rewrites with the
// definition's own.
async function catalogOf(file: string, schemaName: string): Promise<[string, string]> {
  const [, catalog] = decode(await read(file)) as [unknown, string[][]];
  const entry = catalog.find((candidate) => candidate[1] === schemaName)!;
  return [entry[0]!, entry[3]!];
}

async function openStore(file: string, consent: boolean) {
  const path = await copyOf(file);
  if (file === "extra-slot-store.cc") await withoutVariant(path);
  const surface = consent ? surfaceDeclaringDocument() : surfaceDocument(...(await catalogOf(file, "tests.union_surface")));
  const slotUnit = consent ? undefined : slotUnitDocument(...(await catalogOf(file, "tests.slot_unit")));
  const builder = CultCache.builder().withDocumentType(surface).withGenericStore(new SingleFileMessagePackBackingStore(path));
  const cache = (slotUnit ? builder.withDocumentType(slotUnit) : builder).build();
  await cache.pullAllBackingStores();
  return { path, cache, surface, slotUnit };
}

test("extra-slot-store.cc carries a variant, which this runtime refuses by name; the rest of the file is walked", async () => {
  const path = await copyOf("extra-slot-store.cc");
  assert.throws(() => readSingleFileStore(path), StoreUnreadableError);
});

test("every slot-outcomes row holds: the mark, IsWritable, and the removal refused exactly where the record is not writable", async () => {
  const rows = await slotRows();
  assert.equal(rows.length, 8);
  let walked = 0;
  for (const [file, key, writable, explained, typeName, slots, declared, occurrences, flattenRefusedBy] of rows) {
    if (flattenRefusedBy !== null) continue; // a variant: this runtime holds none
    walked += 1;
    const { path, cache, surface, slotUnit } = await openStore(file!, file === "extra-slot-consent.cc");
    const document = key === "unit-arm" ? slotUnit! : surface;
    const label = `${file} ${key}`;
    const found = cache.unreadSlots(document, key!);
    if (typeName === null) {
      assert.equal(found, undefined, label);
    } else {
      assert.ok(found, label);
      assert.deepEqual([found.explained, found.typeName, found.slotCount, found.declaredCount, found.occurrences], [explained, typeName, slots, declared, occurrences], label);
    }
    assert.equal(cache.isWritable(document, key!), writable, label);

    const original = await readFile(path);
    if (writable) {
      assert.equal(await cache.delete(document, key!), true, label);
    } else {
      await assert.rejects(
        () => cache.delete(document, key!),
        (error: unknown) => error instanceof SchemaConflictError && error.unreadSlots?.typeName === typeName && !error.message.includes(SLOT_VALUE),
        label,
      );
      assert.equal(Buffer.compare(original, await readFile(path)), 0, `${label}: the file is unchanged`);
    }
  }
  assert.equal(walked, 7);
});

test("consent: a write of the explained record lands; the over-consent write is refused; the nested mark is refused", async () => {
  const consented = await openStore("extra-slot-consent.cc", true);
  const value = consented.cache.get(consented.surface, "consented") as Surface;
  assert.deepEqual(value, golden);
  await consented.cache.put(consented.surface, "consented", value);
  assert.equal(consented.cache.unreadSlots(consented.surface, "consented"), undefined, "the write dropped the explained slot and the mark with it");
  assert.equal(readSingleFileStore(consented.path).envelopes.find((e) => e.key === "consented")!.schemaId, "tests.union_surface.v2");

  for (const key of ["over-consent", "nested"]) {
    const refused = await openStore("extra-slot-consent.cc", true);
    await assert.rejects(
      () => refused.cache.put(refused.surface, key, golden),
      (error: unknown) => error instanceof SchemaConflictError && error.schemaId === "tests.union_surface.wide",
      `${key}: the refusal names the id the record was stored under`,
    );
  }
});

test("carried: after writing another record, the marked record's bytes, schema id, stored-at and every catalog entry are the originals", async () => {
  const { path, cache, surface } = await openStore("extra-slot-store.cc", false);
  const originals = readSingleFileStore(path);
  await cache.put(surface, "clean", golden);
  const after = readSingleFileStore(path);
  for (const key of ["marked", "unit-arm"]) {
    const was = originals.envelopes.find((e) => e.key === key)!;
    const now = after.envelopes.find((e) => e.key === key)!;
    assert.equal(hex(documentPayloadBytes(now.payload)), hex(documentPayloadBytes(was.payload)), key);
    assert.equal(now.schemaId, was.schemaId, key);
    assert.equal(now.storedAt, was.storedAt, key);
    assert.deepEqual(now.catalogEntry, was.catalogEntry, key);
  }
});

// A writer that adds a slot and keeps its schema id (TS ids are hand-written; C# ids hash the members): the reader of that
// same id has declared nothing, so it consents to nothing.
test("a wider writer under the reader's own schema id is read-only, and a real round trip does not shed the slot", async () => {
  const doc = (columns: 1 | 2, compatibleSchemaIds: string[] = []) => defineDocumentType({
    type: "tests.own_id",
    schemaId: "tests.own_id.v1",
    schemaName: "tests.own_id",
    compatibleSchemaIds,
    schema: z.object({ n: z.string(), extra: z.string().optional() }),
    codec: columns === 2
      ? slots<{ n: string; extra: string }>({ 0: ["n", string], 1: ["extra", string] }, "Own")
      : slots<{ n: string }>({ 0: ["n", string] }, "Own"),
    name: "n",
  });
  const path = join(await mkdtemp(join(tmpdir(), "slot-readonly-")), "own.cc");
  const wide = doc(2);
  const writer = CultCache.builder().withDocumentType(wide).withGenericStore(new SingleFileMessagePackBackingStore(path)).build();
  await writer.put(wide, "k", { n: "n1", extra: "kept" });
  const bytes = await readFile(path);

  for (const compatible of [[], ["tests.own_id.v1"]]) {
    const reader = doc(1, compatible);
    const cache = CultCache.builder().withDocumentType(reader).withGenericStore(new SingleFileMessagePackBackingStore(path)).build();
    await cache.pullAllBackingStores();
    assert.deepEqual(cache.unreadSlots(reader, "k"), mark("Own", 2, 1, true), "unexplained: the arrived id is the reader's own");
    assert.equal(cache.isWritable(reader, "k"), false);
    await assert.rejects(() => cache.put(reader, "k", { n: "n2" }), SchemaConflictError);
    await assert.rejects(() => cache.delete(reader, "k"), SchemaConflictError);
    assert.equal(Buffer.compare(bytes, await readFile(path)), 0, "the file still holds the slot");
  }
});

test("a record that arrived under an id the reader does not list compatible is never consented to", async () => {
  const path = await copyOf("extra-slot-consent.cc");
  const reader = surfaceDocument("tests.union_surface.v2"); // resolves by type name; lists nothing
  const cache = CultCache.builder().withDocumentType(reader).withGenericStore(new SingleFileMessagePackBackingStore(path)).build();
  await cache.pullAllBackingStores();
  assert.equal(cache.unreadSlots(reader, "consented")?.explained, false);
  assert.equal(cache.isWritable(reader, "consented"), false);
});

// isWritable is the write's own judge, not a second one: for every key state it answers what put does.
test("isWritable equals the outcome of put for every key state the write judges", async () => {
  const typed = (type: string, schemaId: string, extra: Record<string, unknown> = {}) =>
    defineDocumentType({ type, schemaId, schemaName: type, schemaVersion: `${type}.v1`, schema: surfaceSchema, codec: surfaceCodec, name: "id", ...extra });
  const older = typed("tests.surface_older", "tests.surface_older.v1");
  const newer = typed("tests.surface_newer", "tests.surface_newer.v1", { compatibleSchemaIds: ["tests.surface_older.v1"] });
  const stranger = typed("tests.surface_stranger", "tests.surface_stranger.v1");
  const global = typed("tests.surface_global", "tests.surface_global.v1", { global: true });
  const clean = (key: string, type: string, schemaId: string) => ({ ...envelopeOf(new Uint8Array()), key, type, schemaId });
  const held = async (key: string, file: string, type: string, schemaId: string) =>
    ({ ...envelopeOf(await read(file)), key, type, schemaId });

  type Row = { name: string; build: () => Promise<{ cache: CultCache; target: typeof older; key: string }> };
  const rows: Row[] = [
    { name: "owned clean record in the writer's own store", build: async () => {
      const cache = CultCache.builder().withDocumentType(older).withDocumentType(newer)
        .withGenericStore(memoryStore([await held("k", "union-surface.msgpack", older.type, older.schemaId!)])).build();
      return { cache, target: newer, key: "k" };
    } },
    { name: "owned record held in a store other than the writer's home", build: async () => {
      const cache = CultCache.builder().withDocumentType(older).withDocumentType(newer)
        .withBackingStore(memoryStore([await held("k", "union-surface.msgpack", older.type, older.schemaId!)]), older)
        .withBackingStore(memoryStore([]), newer).build();
      return { cache, target: newer, key: "k" };
    } },
    { name: "a record of a type that does not own the writer's id", build: async () => {
      const cache = CultCache.builder().withDocumentType(older).withDocumentType(stranger)
        .withGenericStore(memoryStore([await held("k", "union-surface.msgpack", older.type, older.schemaId!)])).build();
      return { cache, target: stranger, key: "k" };
    } },
    { name: "a global type at a key other than the global key", build: async () => {
      const cache = CultCache.builder().withDocumentType(global).withGenericStore(memoryStore([])).build();
      return { cache, target: global, key: "x" };
    } },
    { name: "a key a store carries foreign", build: async () => {
      const cache = CultCache.builder().withDocumentType(older)
        .withGenericStore(memoryStore([{ ...clean("k", "tests.unknown", "tests.unknown.v1"), payload: documentPayloadFromBytes(await read("union-surface.msgpack")) }])).build();
      return { cache, target: older, key: "k" };
    } },
    { name: "a marked record of the writer's own type", build: async () => {
      const cache = CultCache.builder().withDocumentType(older)
        .withGenericStore(memoryStore([await held("k", "extra-slot-record.msgpack", older.type, older.schemaId!)])).build();
      return { cache, target: older, key: "k" };
    } },
    { name: "an unheld key", build: async () => ({ cache: CultCache.builder().withDocumentType(older).withGenericStore(memoryStore([])).build(), target: older, key: "k" }) },
  ];
  const outcomes: [string, boolean, boolean][] = [];
  for (const row of rows) {
    const { cache, target, key } = await row.build();
    await cache.pullAllBackingStores();
    const predicted = cache.isWritable(target, key);
    const landed = await cache.put(target, key === "x" ? "x" : key, golden).then(() => true, () => false);
    assert.equal(predicted, landed, row.name);
    outcomes.push([row.name, predicted, landed]);
  }
  assert.deepEqual(outcomes.map(([, writable]) => writable), [true, false, false, false, false, false, true]);
  // The same judge refuses a removal of a key held read-only or carried foreign.
  const removals = CultCache.builder().withDocumentType(older)
    .withGenericStore(memoryStore([await held("k", "extra-slot-record.msgpack", older.type, older.schemaId!)])).build();
  await removals.pullAllBackingStores();
  await assert.rejects(() => removals.delete(older, "k"), SchemaConflictError);
  const stranger2 = CultCache.builder().withDocumentType(older)
    .withGenericStore(memoryStore([{ ...envelopeOf(await read("union-surface.msgpack")), key: "f", type: "tests.unknown", schemaId: "tests.unknown.v1" }])).build();
  await stranger2.pullAllBackingStores();
  await assert.rejects(() => stranger2.delete(older, "f"), SchemaConflictError);
});

// A removal is by key (C# Remove): the owner of a held record's id removes the one record that exists, in one store call and
// with the cache dropped only once the store has; a type that does not own it is refused like put, and isWritable agrees.
test("delete of a key held as another type: the owner removes it once, a stranger is refused, a failing store leaves cache and store agreeing", async () => {
  const typed = (type: string, schemaId: string, extra: Record<string, unknown> = {}) =>
    defineDocumentType({ type, schemaId, schemaName: type, schemaVersion: `${type}.v1`, schema: surfaceSchema, codec: surfaceCodec, name: "id", ...extra });
  const older = typed("tests.surface_older", "tests.surface_older.v1");
  const newer = typed("tests.surface_newer", "tests.surface_newer.v1", { compatibleSchemaIds: ["tests.surface_older.v1"] });
  const stranger = typed("tests.surface_stranger", "tests.surface_stranger.v1");
  const heldRecord = async (): Promise<CultCacheEnvelope> => ({ ...envelopeOf(await read("union-surface.msgpack")), key: "k", type: older.type, schemaId: older.schemaId });
  const open = async (failing = false) => {
    const rows = [await heldRecord()];
    const calls: string[] = [];
    const store: CacheBackingStore = {
      async pullAll() { return rows.map((row) => ({ ...row })); },
      async push(entry) { rows.push(entry); },
      async delete(entry) {
        calls.push(entry.key);
        if (failing) throw new Error("store refused the removal");
        rows.splice(rows.findIndex((row) => row.key === entry.key), 1);
      },
    };
    const cache = CultCache.builder().withDocumentType(older).withDocumentType(newer).withDocumentType(stranger).withGenericStore(store).build();
    await cache.pullAllBackingStores();
    return { cache, rows, calls };
  };

  const owner = await open();
  assert.equal(owner.cache.isWritable(newer, "k"), true);
  assert.equal(await owner.cache.delete(newer, "k"), true, "the owner's removal removes the one record");
  assert.deepEqual([owner.rows.length, owner.calls.length, owner.cache.get(older, "k")], [0, 1, undefined], "one store call, record gone from store and cache");

  const refused = await open();
  assert.equal(refused.cache.isWritable(stranger, "k"), false);
  await assert.rejects(() => refused.cache.delete(stranger, "k"), SchemaConflictError);
  assert.deepEqual([refused.rows.length, refused.calls.length, refused.cache.get(older, "k") !== undefined], [1, 0, true]);

  const failing = await open(true);
  await assert.rejects(() => failing.cache.delete(newer, "k"), /store refused/u);
  assert.deepEqual([failing.rows.length, failing.calls.length, failing.cache.get(older, "k") !== undefined], [1, 1, true], "the store failed: neither side dropped it");

  const same = await open();
  assert.equal(await same.cache.delete(older, "k"), true);
  assert.equal(await same.cache.delete(older, "k"), false, "an unheld key is still the plain false");
});
