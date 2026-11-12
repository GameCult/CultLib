import assert from "node:assert/strict";
import { copyFile, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { decode, encode } from "@msgpack/msgpack";

import { CultCache } from "../src/cult-cache";
import { defineDocumentType, documentPayloadBytes } from "../src/document";
import { SingleFileMessagePackBackingStore, readSingleFileStore } from "../src/single-file-messagepack-backing-store";
import { SchemaConflictError, StoreUnreadableError } from "../src/store-format";
import type { CultCacheUnreadSlots } from "../src/types";
import {
  type Surface, envelopeOf, golden, hex, memoryStore, read, slotUnitDocument, surfaceCodec, surfaceDeclaringDocument, surfaceDocument, surfaceSchema, vectors,
} from "./union-surface-fixture";

// cultlib-gaps:cut:slot-readonly-ts. A payload carrying a slot past its reader's declared slots, at any depth, is read, held
// read-only, and every write or removal of its key is refused before a store is touched. The vectors are written by the C#
// reference: tests/vectors/union-parity/README.md, "Extra trailing slot" and "Slot stores".

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
    await assert.rejects(() => refused.cache.put(refused.surface, key, golden), SchemaConflictError, key);
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
