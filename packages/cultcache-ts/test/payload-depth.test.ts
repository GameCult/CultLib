import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { encode } from "@msgpack/msgpack";
import { z } from "zod";

import * as root from "../src";
import { inspectCultCacheBytes } from "../src/cult-cache-inspector";
import {
  CultCachePayloadDepthError,
  MAX_PAYLOAD_DEPTH,
  decodeDocumentPayload,
  defineDocumentType,
  documentPayloadBytes,
  documentPayloadFromBytes,
  encodeDocumentPayload,
} from "../src/document";
import { checkPayloadDepth } from "../src/payload-depth";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";

const L = MAX_PAYLOAD_DEPTH;
const CANARY = "CANARY-payload-depth-91d3";

const cat = (...parts: Uint8Array[]) => Uint8Array.from(parts.flatMap((part) => [...part]));
const bytes = (...values: number[]) => Uint8Array.from(values);
const repeat = (unit: Uint8Array, times: number) => {
  const out = new Uint8Array(unit.length * times);
  for (let index = 0; index < times; index += 1) out.set(unit, index * unit.length);
  return out;
};
// n containers nested in one another around a leaf; `open` is one container's header with one child (two for a map).
const chain = (open: Uint8Array, n: number, leaf: Uint8Array = bytes(0xc0)) => cat(repeat(open, n), leaf);
const arrays = (n: number, leaf?: Uint8Array) => chain(bytes(0x91), n, leaf);

const refused = (payload: Uint8Array, label: string) =>
  assert.throws(() => checkPayloadDepth(payload), (error: unknown) =>
    error instanceof CultCachePayloadDepthError && error.depth === L + 1 && error.limit === L, label);

test("the limit is 256: depth 256 passes and 257 is refused, for every array and map header width", () => {
  assert.equal(L, 256);
  const kinds: [string, Uint8Array][] = [
    ["fixarray", bytes(0x91)],
    ["array16", bytes(0xdc, 0, 1)],
    ["array32", bytes(0xdd, 0, 0, 0, 1)],
    ["fixmap", bytes(0x81, 0x00)],
    ["map16", bytes(0xde, 0, 1, 0x00)],
    ["map32", bytes(0xdf, 0, 0, 0, 1, 0x00)],
  ];
  for (const [name, open] of kinds) {
    assert.doesNotThrow(() => checkPayloadDepth(chain(open, L)), `${name} at the limit`);
    refused(chain(open, L + 1), `${name} past the limit`);
  }
});

test("an empty map or array as the innermost container counts like any other", () => {
  for (const empty of [bytes(0x90), bytes(0x80), bytes(0xdc, 0, 0), bytes(0xdd, 0, 0, 0, 0), bytes(0xde, 0, 0), bytes(0xdf, 0, 0, 0, 0)]) {
    assert.doesNotThrow(() => checkPayloadDepth(arrays(L - 1, empty)));
    refused(arrays(L, empty), "empty innermost past the limit");
  }
  // [a chain of 255, a chain of 255]: the first sibling closes before the second opens, so the depth is 256, not 511.
  const sibling = arrays(L - 1);
  assert.doesNotThrow(() => checkPayloadDepth(cat(bytes(0x92), sibling, sibling)));
  refused(cat(bytes(0x92), sibling, arrays(L)), "the second sibling is one deeper");
  // A map's key and value both count as elements: {key: chain, key: chain} closes after four elements.
  assert.doesNotThrow(() => checkPayloadDepth(cat(bytes(0x82, 0x00), sibling, bytes(0x01), sibling)));
  refused(cat(bytes(0x82, 0x00), sibling, bytes(0x01), arrays(L)), "the second map value is one deeper");
  refused(cat(bytes(0x82), arrays(L), sibling, bytes(0x01), sibling), "a map key one deeper");
});

test("every scalar is skipped by its length, whatever bytes it holds", () => {
  const filler = (count: number) => repeat(bytes(0x91), count);
  const scalars: [string, Uint8Array][] = [
    ["nil", bytes(0xc0)], ["false", bytes(0xc2)], ["true", bytes(0xc3)], ["positive fixint", bytes(0x7f)], ["negative fixint", bytes(0xe0)],
    ["uint8", bytes(0xcc, 0x91)], ["uint16", cat(bytes(0xcd), filler(2))], ["uint32", cat(bytes(0xce), filler(4))], ["uint64", cat(bytes(0xcf), filler(8))],
    ["int8", bytes(0xd0, 0x91)], ["int16", cat(bytes(0xd1), filler(2))], ["int32", cat(bytes(0xd2), filler(4))], ["int64", cat(bytes(0xd3), filler(8))],
    ["float32", cat(bytes(0xca), filler(4))], ["float64", cat(bytes(0xcb), filler(8))],
    ["fixext1", cat(bytes(0xd4), filler(2))], ["fixext2", cat(bytes(0xd5), filler(3))], ["fixext4", cat(bytes(0xd6), filler(5))],
    ["fixext8", cat(bytes(0xd7), filler(9))], ["fixext16", cat(bytes(0xd8), filler(17))],
    ["fixstr", cat(bytes(0xa3), filler(3))], ["str8", cat(bytes(0xd9, 3), filler(3))], ["str16", cat(bytes(0xda, 0, 3), filler(3))],
    ["str32", cat(bytes(0xdb, 0, 0, 0, 3), filler(3))],
    ["bin8", cat(bytes(0xc4, 3), filler(3))], ["bin16", cat(bytes(0xc5, 0, 3), filler(3))], ["bin32", cat(bytes(0xc6, 0, 0, 0, 3), filler(3))],
    ["ext8", cat(bytes(0xc7, 3, 0x7f), filler(3))], ["ext16", cat(bytes(0xc8, 0, 3, 0x7f), filler(3))],
    ["ext32", cat(bytes(0xc9, 0, 0, 0, 3, 0x7f), filler(3))],
    ["str16 of 300 bytes", cat(bytes(0xda, 0x01, 0x2c), filler(300))],
  ];
  for (const [name, scalar] of scalars) {
    assert.doesNotThrow(() => checkPayloadDepth(cat(bytes(0x92), scalar, arrays(L - 1))), `${name} then a chain at the limit`);
    refused(cat(bytes(0x92), scalar, arrays(L)), `${name} then a chain past it`);
    assert.doesNotThrow(() => checkPayloadDepth(cat(bytes(0x92), arrays(L - 1), scalar)), `${name} after a chain at the limit`);
  }
});

test("a payload that ends early, is empty or carries trailing bytes is the reader's business, not the walk's", () => {
  for (const early of [[], [0xd9], [0xda, 0], [0xdb, 0, 0, 0], [0xc4], [0xc5, 0], [0xc6, 0, 0, 0], [0xc7], [0xc8, 0], [0xc9, 0, 0, 0], [0xdc, 0], [0xdd, 0, 0, 0], [0xde, 0], [0xdf, 0, 0, 0]]) {
    assert.doesNotThrow(() => checkPayloadDepth(bytes(...early)), early.map((value) => value.toString(16)).join(" "));
  }
  assert.doesNotThrow(() => checkPayloadDepth(arrays(L - 1, bytes(0x91))), "a chain cut off before its leaf");
  assert.doesNotThrow(() => checkPayloadDepth(cat(bytes(0x90), arrays(L + 50))), "bytes after the first complete value are not walked");
  assert.doesNotThrow(() => checkPayloadDepth(bytes(0xc0)));
});

test("the walk is iterative: 100000 nested arrays are refused with the depth error, not a RangeError", () => {
  refused(arrays(100_000), "100000 nested arrays");
  refused(chain(bytes(0x81, 0x00), 100_000), "100000 nested maps");
});

test("a refusal names the subject, the depth and the limit and never a byte of the payload", () => {
  const payload = chain(bytes(0x91), L + 1, cat(bytes(0xd9, CANARY.length), new TextEncoder().encode(CANARY)));
  assert.throws(
    () => checkPayloadDepth(payload, 'document type "t.note" payload'),
    (error: unknown) => error instanceof CultCachePayloadDepthError
      && error.name === "CultCachePayloadDepthError"
      && error.message === 'document type "t.note" payload nests 257 MessagePack containers deep; the limit is 256.',
  );
  const canaryPayload = payload;
  assert.throws(() => checkPayloadDepth(canaryPayload), (error: Error) => !error.message.includes(CANARY) && !JSON.stringify(error).includes(CANARY));
});

const note = z.object({ id: z.string(), count: z.number() });
const formatterCalls: string[] = [];
const guarded = defineDocumentType({
  type: "t.guarded",
  schemaId: "t.guarded.v0",
  schema: note,
  name: "id",
  formatter: {
    encode: () => (formatterCalls.push("encode"), arrays(L + 1)),
    decode: () => (formatterCalls.push("decode"), { id: "a", count: 1 }),
  },
});

test("the owner refuses a payload past the limit before its formatter reads it, and a formatter's bytes before they are wrapped", () => {
  assert.throws(() => decodeDocumentPayload(guarded, documentPayloadFromBytes(arrays(L + 1))), CultCachePayloadDepthError);
  assert.deepEqual(formatterCalls, [], "the formatter never saw the deep payload");
  assert.deepEqual(decodeDocumentPayload(guarded, documentPayloadFromBytes(arrays(L))), { id: "a", count: 1 });

  assert.throws(() => encodeDocumentPayload(guarded, { id: "a", count: 1 }), (error: Error) =>
    error instanceof CultCachePayloadDepthError && error.message === 'document type "t.guarded" value nests 257 MessagePack containers deep; the limit is 256.');
});

test("a document without a codec or formatter writes a value L deep and refuses L+1 by the walk", () => {
  const anything = defineDocumentType({ type: "t.any", schemaId: "t.any.v0", schema: z.any(), name: "id" });
  const nest = (n: number): unknown => (n === 1 ? [] : [nest(n - 1)]);
  const atLimit = encodeDocumentPayload(anything, nest(L));
  assert.deepEqual(documentPayloadBytes(atLimit), encode(nest(L), { maxDepth: 1000 }));
  assert.deepEqual(decodeDocumentPayload(anything, atLimit), nest(L));
  assert.throws(() => encodeDocumentPayload(anything, nest(L + 1)), (error: unknown) =>
    error instanceof CultCachePayloadDepthError && error.depth === L + 1 && error.limit === L);
  assert.throws(() => decodeDocumentPayload(anything, documentPayloadFromBytes(arrays(L + 1))), CultCachePayloadDepthError);
});

test("the limit and its error are exported from the root, and the walk itself is not", () => {
  assert.equal(root.MAX_PAYLOAD_DEPTH, 256);
  assert.equal(root.CultCachePayloadDepthError, CultCachePayloadDepthError);
  assert.equal("checkPayloadDepth" in root, false);
});

// A v1 store whose one record carries `payload` as bin and no catalog entry, so the record's schema is inferred from the
// payload's first element.
async function storeWith(payload: Uint8Array): Promise<string> {
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-payload-depth-")), "deep.cc");
  await writeFile(file, encode(["cultcache.store.v1", [], [["record-1", "sha256:stale", "2026-06-25T12:00:00Z", payload]]]));
  return file;
}
const stamped = (containersInsideTheStamp: number) =>
  cat(bytes(0x92), encode("tests.schema_stamped_entry.v1"), arrays(containersInsideTheStamp));

test("the inspector previews a payload at the limit and reports one past it, or 100000 deep, as a decode error", async () => {
  const atLimit = await storeWith(stamped(L - 1));
  const previewed = inspectCultCacheBytes(atLimit, await readFile(atLimit)).records[0]!;
  assert.equal(previewed.payloadDecodeError, undefined);

  for (const inside of [L, 100_000]) {
    const file = await storeWith(stamped(inside));
    const record = inspectCultCacheBytes(file, await readFile(file)).records[0]!;
    assert.equal(record.payloadDecodeError, "document payload nests 257 MessagePack containers deep; the limit is 256.");
  }
});

test("the single-file store infers a legacy record's schema from a payload at the limit and from none past it", async () => {
  const atLimit = await storeWith(stamped(L - 1));
  const [read] = await new SingleFileMessagePackBackingStore(atLimit).pullAll();
  assert.equal(read.catalogEntry?.schemaVersion, "tests.schema_stamped_entry.v1");

  const past = await storeWith(stamped(L));
  await assert.rejects(() => new SingleFileMessagePackBackingStore(past).pullAll(), /missing schema id/u);
});
