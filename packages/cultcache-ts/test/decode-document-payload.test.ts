import assert from "node:assert/strict";
import { test } from "node:test";

import { encode } from "@msgpack/msgpack";
import { z } from "zod";

import { codecs, decodeDocumentPayload, defineDocumentType } from "../src";

const note = z.object({ id: z.string(), count: z.number() });
const plain = defineDocumentType({ type: "t.plain", schemaId: "t.plain.v0", schema: note, name: "id" });
const codec = defineDocumentType({
  type: "t.codec",
  schemaId: "t.codec.v0",
  schema: note,
  name: "id",
  codec: codecs.slots<{ id: string; count: number }>({ 0: ["id", codecs.string], 1: ["count", codecs.int32] }, "T.Note"),
});
const formatted = defineDocumentType({
  type: "t.formatted",
  schemaId: "t.formatted.v0",
  schema: note,
  name: "id",
  formatter: { encode: (value) => encode([(value as { id: string }).id]), decode: () => ({ id: "from-formatter", count: 0 }) },
});

test("a payload is read by its definition's formatter, a codec's, or as a plain value without a definition", () => {
  assert.deepEqual(decodeDocumentPayload(undefined, encode({ id: "a", count: 1 })), { id: "a", count: 1 });
  assert.deepEqual(decodeDocumentPayload(plain, encode({ id: "a", count: 1 })), { id: "a", count: 1 });
  assert.deepEqual(decodeDocumentPayload(codec, encode(["a", 1])), { id: "a", count: 1 });
  assert.deepEqual(decodeDocumentPayload(formatted, new Uint8Array([0xc0])), { id: "from-formatter", count: 0 });
  // Without the definition the codec payload is its positional array, which is why a caller that holds one passes it.
  assert.deepEqual(decodeDocumentPayload(undefined, encode(["a", 1])), ["a", 1]);
});
