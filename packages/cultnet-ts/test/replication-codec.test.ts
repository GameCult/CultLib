import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

import { z } from "zod";
import { CultCache, codecs, defineDocumentType, SingleFileMessagePackBackingStore } from "@gamecult/cultcache-ts";

import { CultNetDocumentRegistry, defineCultNetDocumentBinding } from "../src";

const { float32, float64, int32, int64, slots, string } = codecs;

// tests/vectors/numeric-leaves/mixed-record.msgpack is the bytes the C# reference writes for Mixed { "n1", 7, 8, 1.5f, 2.5 }
// (NumericLeafVectorTests). A payload equal to it is a payload the reference reads.
function referenceMixedRecord(): Uint8Array {
  for (let dir = __dirname; ; dir = resolve(dir, "..")) {
    const file = join(dir, "tests", "vectors", "numeric-leaves", "mixed-record.msgpack");
    if (existsSync(file)) return new Uint8Array(readFileSync(file));
    if (resolve(dir, "..") === dir) throw new Error("tests/vectors/numeric-leaves not found above the test.");
  }
}

const mixedCodec = slots<{ id: string; i: number; l: number | bigint; f: number; d: number }>({
  0: ["id", string],
  1: ["i", int32],
  2: ["l", int64],
  3: ["f", float32],
  4: ["d", float64],
});

const mixedDefinition = defineDocumentType({
  type: "tests.mixed_record",
  schemaId: "tests.mixed_record.v1",
  schemaName: "tests.mixed_record",
  schemaVersion: "tests.mixed_record.v1",
  schema: z.object({ id: z.string(), i: z.number(), l: z.union([z.number(), z.bigint()]), f: z.number(), d: z.number() }),
  codec: mixedCodec,
  name: "id",
});

const value = { id: "n1", i: 7, l: 8, f: 1.5, d: 2.5 };
const hex = (data: Uint8Array) => Buffer.from(data).toString("hex");

function registry() {
  return new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: mixedDefinition })]);
}

test("a codec document replicates as the bytes the C# reference writes, and a snapshot of it applies on another cache", async () => {
  const dir = mkdtempSync(join(tmpdir(), "cultnet-codec-"));
  try {
    const reference = referenceMixedRecord();
    const documents = registry();
    const origin = CultCache.builder().withDocumentType(mixedDefinition).withGenericStore(new SingleFileMessagePackBackingStore(join(dir, "origin.cc"))).build();
    const target = CultCache.builder().withDocumentType(mixedDefinition).withGenericStore(new SingleFileMessagePackBackingStore(join(dir, "target.cc"))).build();

    const put = documents.createRawDocumentPutMessage(documents.get("tests.mixed_record")!, "put-1", "n1", value);
    assert.equal(hex(put.document.payload), hex(reference), "a raw put carries the reference's record, not a map");

    await documents.applyRawDocumentPutMessage(origin, put);
    assert.equal(hex(origin.getRequiredEnvelope(mixedDefinition, "n1").payload), hex(reference));

    const raw = documents.createRawSnapshotResponse(origin, "snapshot-raw");
    assert.equal(hex(raw.documents[0].payload), hex(reference));
    await documents.applyRawSnapshotResponse(target, raw);
    assert.deepEqual(target.getRequired(mixedDefinition, "n1"), value);
    assert.equal(hex(target.getRequiredEnvelope(mixedDefinition, "n1").payload), hex(reference));

    // The value-level snapshot decodes the stored record through the codec, so a peer applying it parses a record.
    const values = documents.createSnapshotResponse(origin, "snapshot-values");
    assert.deepEqual(values.documents[0].payload, value);
    const other = CultCache.builder().withDocumentType(mixedDefinition).withGenericStore(new SingleFileMessagePackBackingStore(join(dir, "other.cc"))).build();
    await documents.applySnapshotResponse(other, values);
    assert.equal(hex(other.getRequiredEnvelope(mixedDefinition, "n1").payload), hex(reference));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("a definition that carries a codec and a formatter is refused where replication reads it", () => {
  const both = {
    ...mixedDefinition,
    formatter: { encode: () => new Uint8Array(), decode: () => value },
  } as unknown as typeof mixedDefinition;
  const documents = new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: both })]);
  assert.throws(
    () => documents.createRawDocumentPutMessage(documents.get("tests.mixed_record")!, "put-2", "n1", value),
    { message: 'CultCache document type "tests.mixed_record" declares both a codec and a formatter.' },
  );
});

test("a document with neither a codec nor a formatter replicates as a map, as before", () => {
  const plain = defineDocumentType({
    type: "tests.plain_record",
    schemaId: "tests.plain_record.v1",
    schemaName: "tests.plain_record",
    schemaVersion: "tests.plain_record.v1",
    schema: z.object({ id: z.string() }),
    name: "id",
  });
  const documents = new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: plain })]);
  const put = documents.createRawDocumentPutMessage(documents.get("tests.plain_record")!, "put-3", "n1", { id: "n1" });
  assert.equal(hex(put.document.payload), "81a26964a26e31");
});
