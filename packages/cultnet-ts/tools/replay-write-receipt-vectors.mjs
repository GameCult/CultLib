#!/usr/bin/env node
// Replays contracts/cultnet/interop/write-receipt-vectors.json against this runtime: every `scenarios.cases` event
// replay through a fake server and the WriteTracker, and every `wire` / `schemaInvalid` frame through the real
// decoder. Prints PASS/FAIL per case; exit 1 on any failure. Runs against the built package (`npm run build` first).
//
//   node tools/replay-write-receipt-vectors.mjs [path/to/write-receipt-vectors.json]
//
// The runner interface a port (Rust, C#) must reproduce is documented in README.md, "Write receipt replay runner".

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const { decode, encode } = require("@msgpack/msgpack");
const {
  CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS, LEGACY_RESULT_WINDOW_MS, RECEIPT_RESULT_WINDOW_MS, WriteTracker,
  encodeCultNetMessageForWire, helloAdvertisesWriteReceipts, parseCultNetMessage, resultWindowMs,
} = require(path.join(here, "..", "dist", "index.js"));

const vectorsPath = process.argv[2] ?? path.join(here, "..", "..", "..", "contracts", "cultnet", "interop", "write-receipt-vectors.json");
const vectors = JSON.parse(readFileSync(vectorsPath, "utf8"));

const results = [];
const check = (name, run) => {
  try {
    const problems = run();
    results.push({ name, problems });
  } catch (error) {
    results.push({ name, problems: [`threw: ${error instanceof Error ? error.message : String(error)}`] });
  }
};
const bytesOf = (hex) => Uint8Array.from(Buffer.from(hex, "hex"));
const hexOf = (bytes) => Buffer.from(bytes).toString("hex").toUpperCase();
// A frame reaches the client as bytes: the fake server encodes it, the client decodes and validates it.
const delivered = (frame) => parseCultNetMessage(decode(encode(frame)));

check("constants", () => [
  ...(vectors.constants.RECEIPT_RESULT_WINDOW_MS === RECEIPT_RESULT_WINDOW_MS ? [] : [`RECEIPT window ${RECEIPT_RESULT_WINDOW_MS} != ${vectors.constants.RECEIPT_RESULT_WINDOW_MS}`]),
  ...(vectors.constants.LEGACY_RESULT_WINDOW_MS === LEGACY_RESULT_WINDOW_MS ? [] : [`LEGACY window ${LEGACY_RESULT_WINDOW_MS} != ${vectors.constants.LEGACY_RESULT_WINDOW_MS}`]),
]);

for (const vector of vectors.wire) {
  check(`wire/${vector.name}`, () => {
    const problems = [];
    const parsed = parseCultNetMessage(decode(bytesOf(vector.messagePackHex)));
    if (JSON.stringify(sorted(parsed)) !== JSON.stringify(sorted(vector.message))) problems.push(`decoded ${JSON.stringify(parsed)} != ${JSON.stringify(vector.message)}`);
    const reencoded = hexOf(encode(encodeCultNetMessageForWire(parsed)));
    if (reencoded !== vector.messagePackHex.toUpperCase()) problems.push(`re-encoded bytes ${reencoded} != captured ${vector.messagePackHex}`);
    return problems;
  });
}

for (const vector of vectors.schemaInvalid) {
  check(`schemaInvalid/${vector.name}`, () => {
    try {
      parseCultNetMessage(decode(bytesOf(vector.messagePackHex)));
    } catch {
      return [];
    }
    return [`accepted a frame that must be refused (${vector.why})`];
  });
}

for (const scenario of vectors.scenarios.cases) {
  check(`scenario/${scenario.name}`, () => {
    // The fake server states its capability the only way a real one does: in its hello.
    const serverHello = { schemaVersion: "cultnet.hello.v0", runtimeId: "fake-server", runtimeKind: "fake",
      ...(scenario.server === "receipt" ? { supportedMessageVersions: [...CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS] } : {}) };
    const windowMs = resultWindowMs(helloAdvertisesWriteReceipts(serverHello));
    const problems = [];
    if (windowMs !== vectors.constants[scenario.window]) problems.push(`window ${windowMs} != ${scenario.window}`);

    const told = new Map(); // what the caller was told, at settle time
    const tracker = new WriteTracker({ windowMs, onSettle: (id, settlement) => {
      if (told.has(id)) problems.push(`${id} settled twice`);
      told.set(id, settlement);
    } });
    for (const event of [...scenario.events].sort((a, b) => a.atMs - b.atMs)) {
      if ("send" in event) tracker.send(event.send, event.atMs);
      else if ("frame" in event) tracker.frame(delivered(event.frame), event.atMs);
      else if ("tick" in event) tracker.tick(event.atMs);
      else if ("sessionEnd" in event) tracker.sessionEnd(event.atMs);
      // transportAck: delivery is not an answer; the tracker has no such input and must not need one.
    }
    for (const [id, want] of Object.entries(scenario.expect)) {
      const got = told.get(id);
      if (!got) { problems.push(`${id}: never settled, wanted ${want.outcome}`); continue; }
      if (got.outcome !== want.outcome) problems.push(`${id}: told ${got.outcome}, wanted ${want.outcome}`);
      if (want.code !== undefined && got.code !== want.code) problems.push(`${id}: code ${got.code}, wanted ${want.code}`);
      if (want.reason !== undefined && got.reason !== want.reason) problems.push(`${id}: reason ${got.reason}, wanted ${want.reason}`);
      const recorded = tracker.state(id)?.recorded;
      if (recorded !== want.recorded) problems.push(`${id}: recorded ${recorded}, wanted ${want.recorded}`);
    }
    for (const id of told.keys()) if (!(id in scenario.expect)) problems.push(`${id}: settled but the scenario expects nothing for it`);
    return problems;
  });
}

function sorted(value) {
  if (Array.isArray(value)) return value.map(sorted);
  if (value && typeof value === "object") return Object.fromEntries(Object.keys(value).sort().map((k) => [k, sorted(value[k])]));
  return value;
}

let failed = 0;
for (const { name, problems } of results) {
  if (problems.length === 0) { console.log(`PASS ${name}`); continue; }
  failed++;
  console.log(`FAIL ${name}`);
  for (const problem of problems) console.log(`     ${problem}`);
}
console.log(`${results.length - failed}/${results.length} passed`);
process.exit(failed === 0 ? 0 : 1);
