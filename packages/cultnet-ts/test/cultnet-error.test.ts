import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import { decode, encode } from "@msgpack/msgpack";

import {
  CULTNET_ERROR_CODES,
  cultNetSchemas,
  cultNetWriteNotAuthorizedError,
  encodeCultNetMessageForWire,
  parseCultNetMessage,
  type CultNetErrorMessage,
} from "../src";

// contracts/cultnet/interop/error-vectors.json: bytes captured from MessagePack C# 3.1.7 and pinned
// by cultnet-rs/tests/error_message.rs (Rust encodes to exactly these bytes).
const VECTORS = join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop", "error-vectors.json");
const vectors = (JSON.parse(readFileSync(VECTORS, "utf8")) as {
  vectors: { name: string; messagePackHex: string; message: unknown }[];
}).vectors;

const bytesOf = (hex: string): Uint8Array => Uint8Array.from(Buffer.from(hex, "hex"));
const hexOf = (bytes: Uint8Array): string => Buffer.from(bytes).toString("hex").toUpperCase();
const wireOf = (message: unknown): Uint8Array => encode(encodeCultNetMessageForWire(message as CultNetErrorMessage));

test("the error vector file carries a case for every closed code", () => {
  const codes = new Set(vectors.map((v) => (v.message as CultNetErrorMessage).code));
  for (const code of CULTNET_ERROR_CODES) {
    assert.ok(codes.has(code), `no vector for ${code}`);
  }
});

test("a null code with null routingHint and details is a valid error message (no captured bytes carry one)", () => {
  const message = parseCultNetMessage({
    schemaVersion: "cultnet.error.v0", error: "boom", routingHint: null, code: null, details: null,
  });
  assert.equal((message as CultNetErrorMessage).code, null);
});

test("the closed code union equals the schema's enum", () => {
  const schemaCodes = ((cultNetSchemas.errorSchema.properties.code as { enum?: (string | null)[] }).enum ?? []).filter((c) => c !== null);
  assert.deepEqual([...CULTNET_ERROR_CODES].sort(), [...schemaCodes].sort());
});

test("C#/Rust-written error bytes decode into the typed message with every key", () => {
  for (const vector of vectors) {
    const parsed = parseCultNetMessage(decode(bytesOf(vector.messagePackHex)));
    assert.deepEqual(parsed, vector.message, vector.name);
    assert.deepEqual(Object.keys(parsed), ["schemaVersion", "error", "routingHint", "code", "details"], vector.name);
  }
});

test("a parsed error message re-encodes to the same bytes", () => {
  for (const vector of vectors) {
    const parsed = parseCultNetMessage(decode(bytesOf(vector.messagePackHex)));
    assert.equal(hexOf(wireOf(parsed)), vector.messagePackHex, vector.name);
  }
});

test("dropping code or details breaks the round trip and the decode", () => {
  const vector = vectors.find((v) => v.name === "cursor_stale")!;
  for (const dropped of ["code", "details", "routingHint"]) {
    const copy = { ...(decode(bytesOf(vector.messagePackHex)) as Record<string, unknown>) };
    delete copy[dropped];
    assert.throws(() => parseCultNetMessage(copy), /cultnet\.error\.v0/, `missing ${dropped} must be refused`);
    assert.notEqual(hexOf(encode(copy)), vector.messagePackHex);
  }
});

test("a code outside the closed enum is refused, as is a malformed details object", () => {
  const base = decode(bytesOf(vectors.find((v) => v.name === "cursor_stale")!.messagePackHex)) as Record<string, unknown>;
  assert.throws(() => parseCultNetMessage({ ...base, code: "not_a_code" }), /cultnet\.error\.v0/);
  assert.throws(() => parseCultNetMessage({ ...base, code: 7 }), /cultnet\.error\.v0/);
  assert.throws(
    () => parseCultNetMessage({ ...base, details: { field: null, value: null, asOf: 1, current: 2, extra: 1 } }),
    /cultnet\.error\.v0/,
  );
  assert.throws(
    () => parseCultNetMessage({ ...base, details: { field: null, value: null, asOf: -1, current: 2 } }),
    /cultnet\.error\.v0/,
  );
});

test("the write_not_authorized factory writes the C# vector's bytes and names the key", () => {
  const vector = vectors.find((v) => v.name === "write_not_authorized")!;
  assert.equal(hexOf(wireOf(cultNetWriteNotAuthorizedError("some:key", "not granted"))), vector.messagePackHex);
  assert.deepEqual(cultNetWriteNotAuthorizedError("some:key", "not granted"), vector.message);
});

test("a write_not_authorized refusal with no reason, or a blank one, says only the code", () => {
  assert.equal(cultNetWriteNotAuthorizedError("k").error, "write_not_authorized");
  assert.equal(cultNetWriteNotAuthorizedError("k", "  ").error, "write_not_authorized");
  assert.equal(cultNetWriteNotAuthorizedError("k", null).error, "write_not_authorized");
  // C# string.IsNullOrWhiteSpace counts U+0085 as white space and not U+FEFF; JS trim() disagrees on both.
  assert.equal(cultNetWriteNotAuthorizedError("k", "\u0085").error, "write_not_authorized");
  assert.equal(cultNetWriteNotAuthorizedError("k", "\ufeff").error, "write_not_authorized: \ufeff");
});
