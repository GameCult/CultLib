// The reference's own bytes for the cultnet.error.v0 code/details shape and the selection/v1
// carriers (`contracts/cultnet/interop/carrier-vectors.cs-written.json`, written by the C#
// CarrierVectorTests). Each vector is decoded, parsed, and re-encoded: the re-encoding must be the
// reference's exact bytes, and the decoded message must equal the reference's own JSON of the object.

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { decode, encode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, parseCultNetMessage } from "../src/contracts";

const FILE = join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop", "carrier-vectors.cs-written.json");
type Vector = { name: string; schemaVersion: string; messagePackBase64: string; expected: unknown };
const vectors: Vector[] = JSON.parse(readFileSync(FILE, "utf8")).vectors;
const vectorsFor = (prefix: string) => vectors.filter(v => v.name.startsWith(prefix));
const bytesOf = (v: Vector) => new Uint8Array(Buffer.from(v.messagePackBase64, "base64"));

/** Uint8Array to base64, recursively, so a decoded message compares with the reference's JSON. */
function jsonShape(value: unknown): unknown {
  if (value instanceof Uint8Array) return Buffer.from(value).toString("base64");
  if (Array.isArray(value)) return value.map(jsonShape);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, jsonShape(v)]));
  }
  return value;
}

const reencode = (message: Parameters<typeof encodeCultNetMessageForWire>[0]) =>
  new Uint8Array(encode(encodeCultNetMessageForWire(message), { ignoreUndefined: true }));

test("every error vector keeps the reference's code and details through parse and re-encode", () => {
  const errors = vectorsFor("error_");
  assert.ok(errors.length >= 5);
  for (const v of errors) {
    const raw = decode(bytesOf(v));
    assert.deepEqual(jsonShape(raw), v.expected, `${v.name}: decoded bytes`);
    const message = parseCultNetMessage(raw);
    assert.equal(message.schemaVersion, "cultnet.error.v0");
    assert.deepEqual(jsonShape(message), v.expected, `${v.name}: parsed message`);
    assert.deepEqual(reencode(message), bytesOf(v), `${v.name}: re-encoded bytes`);
  }
});

test("an error whose code is outside the schema's closed enum is refused", () => {
  const raw = decode(bytesOf(vectors.find(v => v.name === "error_cursor_stale")!)) as Record<string, unknown>;
  assert.throws(() => parseCultNetMessage({ ...raw, code: "made_up_code" }), /cultnet\.error\.v0/);
});
