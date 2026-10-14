import assert from "node:assert/strict";
import { test } from "node:test";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import {
  decodeDocumentPayload,
  defineDocumentType,
  documentPayloadBytes,
  documentPayloadFromBytes,
  encodeDocumentPayload,
  type AnyCultCacheDocumentDefinition,
  type CultCacheEnvelope,
  type DocumentPayload,
} from "../src";

// The rule: a document payload is read only through its definition. The type is the enforcement, so this file is the
// proof: tsc compiles it only if every line below that carries @ts-expect-error is an error. A line that starts to
// compile makes its @ts-expect-error unused, which is itself an error, so the suite stops until the payload is opaque
// again. The function is never called; the lines are not meant to run.
export function refusedReaders(envelope: CultCacheEnvelope, definition: AnyCultCacheDocumentDefinition, payload: DocumentPayload): void {
  // @ts-expect-error a plain msgpack decoder does not accept a document payload
  decode(envelope.payload);
  // @ts-expect-error a payload is not assignable to bytes
  const bytes: Uint8Array = envelope.payload;
  // @ts-expect-error nor is it a byte source a copy may be taken from
  new Uint8Array(envelope.payload);
  // @ts-expect-error a definition's formatter reads bytes, not a payload; the owner unwraps for it
  definition.formatter!.decode(envelope.payload);
  // @ts-expect-error the owner needs the definition that says how the bytes are laid out
  decodeDocumentPayload(undefined, payload);
  // @ts-expect-error bytes are not a payload until they enter by name
  const forged: DocumentPayload = new Uint8Array();
  void [bytes, forged];
}

const note = defineDocumentType({
  type: "t.note",
  schemaId: "t.note.v0",
  schema: z.object({ id: z.string(), count: z.number() }),
  name: "id",
});

test("a payload is the same bytes at run time, so frames and stores see no change", () => {
  const bytes = encode({ id: "a", count: 1 });
  const payload = documentPayloadFromBytes(bytes);
  assert.equal(documentPayloadBytes(payload), bytes);
  assert.equal(payload.byteLength, bytes.byteLength);
});

test("the owner writes a payload through the definition's schema and reads it back through the same one", () => {
  const payload = encodeDocumentPayload(note, { id: "a", count: 1 });
  assert.deepEqual(decode(documentPayloadBytes(payload)), { id: "a", count: 1 });
  assert.deepEqual(decodeDocumentPayload(note, payload), { id: "a", count: 1 });
  // A refusal names the field and never echoes the value it refused.
  const refusesWithoutEchoing = (action: () => unknown) =>
    assert.throws(action, (error: Error) => /count/.test(error.message) && !error.message.includes("canary-value"));
  refusesWithoutEchoing(() => encodeDocumentPayload(note, { id: "a", count: "canary-value" }));
  refusesWithoutEchoing(() => decodeDocumentPayload(note, documentPayloadFromBytes(encode({ id: "a", count: "canary-value" }))));
});
