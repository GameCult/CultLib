// isStoredAtStamp agrees with every row of tests/vectors/stored-at-stamp/stamps.msgpack, the one shape a peer's storedAt must parse as.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { decode } from "@msgpack/msgpack";
import { isStoredAtStamp } from "../src";

// dist-test/test -> the repository root.
export const STAMP_VECTOR = join(__dirname, "..", "..", "..", "..", "tests", "vectors", "stored-at-stamp", "stamps.msgpack");

test("isStoredAtStamp answers every row of the stored-at-stamp vector", () => {
  const rows = decode(readFileSync(STAMP_VECTOR)) as [string, boolean][];
  assert.ok(rows.length > 20, "the vector was read");
  for (const [text, parses] of rows) assert.equal(isStoredAtStamp(text), parses, JSON.stringify(text));
});

test("a stamp is a string: anything else is not one", () => {
  for (const value of [undefined, null, 0, 1_700_000_000_000, true, {}, [], new Date()]) assert.equal(isStoredAtStamp(value), false);
});
