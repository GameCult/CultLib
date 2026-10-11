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

test("a payload is read by its definition's formatter, a codec's, or a plain map", () => {
  assert.deepEqual(decodeDocumentPayload(plain, encode({ id: "a", count: 1 })), { id: "a", count: 1 });
  assert.deepEqual(decodeDocumentPayload(codec, encode(["a", 1])), { id: "a", count: 1 });
  assert.deepEqual(decodeDocumentPayload(formatted, new Uint8Array([0xc0])), { id: "from-formatter", count: 0 });
});

test("a definition that declares both a codec and a formatter is refused where its payload is read", () => {
  const both = { ...codec, formatter: formatted.formatter } as typeof codec;
  assert.throws(() => decodeDocumentPayload(both, encode(["a", 1])), (error: Error) =>
    error.message === 'CultCache document type "t.codec" declares both a codec and a formatter.');
});
