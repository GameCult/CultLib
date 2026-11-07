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

// ---- selection/v1 carriers ----

for (const prefix of ["snapshot_request_v1", "database_subscribe_v1", "snapshot_response_raw_v1"]) {
  test(`${prefix} vectors: decode, parse, and re-encode to the reference's exact bytes`, () => {
    const group = vectorsFor(prefix);
    assert.ok(group.length >= 2, `${prefix}: vectors present`);
    for (const v of group) {
      const raw = decode(bytesOf(v));
      assert.deepEqual(jsonShape(raw), v.expected, `${v.name}: decoded bytes`);
      const message = parseCultNetMessage(raw);
      assert.equal(message.schemaVersion, v.schemaVersion);
      assert.deepEqual(reencode(message), bytesOf(v), `${v.name}: re-encoded bytes`);
    }
  });
}

test("a parsed v1 message drops the nil an absent member travels as", () => {
  const full = parseCultNetMessage(decode(bytesOf(vectors.find(v => v.name === "snapshot_request_v1_defaults")!))) as unknown as
    { selection: Record<string, unknown>; shardId?: unknown };
  assert.deepEqual(Object.keys(full.selection).sort(), ["descending", "projection"]);
  assert.equal("shardId" in full, false);
  const page = parseCultNetMessage(decode(bytesOf(vectors.find(v => v.name === "snapshot_response_raw_v1_headers")!))) as unknown as
    { headers: Record<string, unknown>[]; documents?: unknown };
  assert.equal("documents" in page, false);
  assert.equal("tags" in page.headers[0], false);
  assert.deepEqual(page.headers[1].tags, ["alpha", "beta"]);
});

test("edge payloads and document payloads arrive as bytes, not text", () => {
  const page = parseCultNetMessage(decode(bytesOf(vectors.find(v => v.name === "snapshot_response_raw_v1_documents_and_edges")!))) as unknown as
    { edges: { payload?: Uint8Array }[]; documents: { payload: Uint8Array }[] };
  assert.deepEqual(Array.from(page.edges[1].payload!), [1, 2, 3]);
  assert.equal("payload" in page.edges[0], false);
  assert.deepEqual(Array.from(page.documents[0].payload), [0x81, 0xa1, 0x6b, 0x01]);
});

const request = () => decode(bytesOf(vectors.find(v => v.name === "snapshot_request_v1_full_selection_and_shard")!)) as
  { selection: Record<string, unknown> } & Record<string, unknown>;
const refusedField = (message: unknown): string | undefined => {
  try {
    parseCultNetMessage(message);
  } catch (error) {
    return (error as { field?: string }).field;
  }
  return "accepted";
};

test("the door refuses an empty or blank schemas/keys entry with a typed field, never as every-schema", () => {
  const withSelection = (patch: Record<string, unknown>) => ({ ...request(), selection: { ...request().selection, ...patch } });
  assert.equal(refusedField(withSelection({ schemas: [] })), "schemas");
  assert.equal(refusedField(withSelection({ keys: ["a-eq", "   "] })), "keys");
  assert.equal(refusedField(withSelection({ projection: "everything" })), "projection");
});

test("the schema refuses a comparison whose number is not a canonical decimal, and a predicate that mixes values and number", () => {
  const withField = (field: Record<string, unknown>) => {
    const message = request();
    return { ...message, selection: { ...message.selection, fields: [field] } };
  };
  for (const number of ["05", "1.50", "+1", "1e3", "-0", " 1"]) {
    assert.throws(() => parseCultNetMessage(withField({ index: "mass", op: "ge", values: null, number })), /snapshot_request\.v1/, number);
  }
  assert.throws(() => parseCultNetMessage(withField({ index: "mass", op: "ge", values: ["1"], number: "1" })), /snapshot_request\.v1/);
  assert.throws(() => parseCultNetMessage(withField({ index: "mass", op: "near", values: null, number: "1" })), /snapshot_request\.v1/);
});

test("a selection page with both or neither of headers and documents is refused", () => {
  const page = decode(bytesOf(vectors.find(v => v.name === "snapshot_response_raw_v1_headers")!)) as Record<string, unknown>;
  assert.throws(() => parseCultNetMessage({ ...page, documents: [] }), /exactly one of headers and documents/);
  assert.throws(() => parseCultNetMessage({ ...page, headers: null }), /exactly one of headers and documents/);
});

// ---- selection vectors: the reference's and Rust's selection bytes ----

const INTEROP = join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop");
type SelectionVector = { name: string; selectionMessagePackBase64: string; expectedRefusalField?: string | null };
const selectionVectors = (file: string): SelectionVector[] =>
  JSON.parse(readFileSync(join(INTEROP, file), "utf8")).vectors;

import { CultNetSelectionInvalidError, validateSelectionShape, type CultNetSelection } from "../src/selection";

for (const file of ["selection-vectors.cs-written.json", "selection-vectors.rs-written.json"]) {
  test(`${file}: every selection decodes inside a v1 request and the schema door agrees with the vector`, () => {
    const all = selectionVectors(file);
    assert.ok(all.length >= 20);
    for (const v of all) {
      const selection = decode(new Uint8Array(Buffer.from(v.selectionMessagePackBase64, "base64")));
      const message = { schemaVersion: "cultnet.snapshot_request.v1", messageId: "m", selection, shardId: null, shardEpoch: null };
      if (v.expectedRefusalField === "schemas" || v.expectedRefusalField === "keys") {
        assert.equal(refusedField(message), v.expectedRefusalField, `${v.name}: shape refusal`);
        continue;
      }
      if (v.expectedRefusalField) {
        // Declaration-dependent refusals (cites target, index aliases) need the selection engine's schemas.
        const parsed = parseCultNetMessage(message) as unknown as { selection: CultNetSelection };
        validateSelectionShape(parsed.selection);
        continue;
      }
      const parsed = parseCultNetMessage(message) as unknown as { selection: CultNetSelection };
      validateSelectionShape(parsed.selection);
      const rewritten = new Uint8Array(encode(
        (encodeCultNetMessageForWire(parsed as never) as { selection: unknown }).selection));
      if (file.includes("cs-written")) {
        // The reference's bytes are the nil-keyed shape: our encoder must reproduce them exactly.
        assert.deepEqual(rewritten, new Uint8Array(Buffer.from(v.selectionMessagePackBase64, "base64")),
          `${v.name}: selection re-encodes to the reference's bytes`);
      } else {
        // Rust omits absent members; what it means must survive our nil-keyed rewrite unchanged.
        const again = parseCultNetMessage({ ...message, selection: decode(rewritten) }) as unknown as { selection: CultNetSelection };
        assert.deepEqual(again.selection, parsed.selection, `${v.name}: selection meaning survives re-encode`);
      }
    }
  });
}

test("CultNetSelectionInvalidError carries the field and offending value", () => {
  assert.throws(
    () => validateSelectionShape({ keys: ["ok", ""] }),
    (error: unknown) => error instanceof CultNetSelectionInvalidError && error.field === "keys" && error.value === "",
  );
});
