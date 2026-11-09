import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { decode } from "@msgpack/msgpack";
import {
  CULTNET_ERROR_CODES,
  CULTNET_ERROR_V1_CODES,
  CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS,
  LEGACY_RESULT_WINDOW_MS,
  RECEIPT_RESULT_WINDOW_MS,
  WriteTracker,
  cultNetSchemas,
  helloAdvertisesWriteReceipts,
  parseCultNetMessage,
  resultWindowMs,
  type CultNetErrorV1Message,
  type CultNetMessage,
  type CultNetWriteAdmittedMessage,
  type WriteSettlement,
} from "../src";

const admitted = (messageId: string): CultNetWriteAdmittedMessage =>
  ({ schemaVersion: "cultnet.write_admitted.v0", messageId, operation: "put", schemaId: "note.v0", recordKey: "note:1" });
const refusedV1 = (messageId: string | null, code: CultNetErrorV1Message["code"] = "write_not_authorized"): CultNetErrorV1Message =>
  ({ schemaVersion: "cultnet.error.v1", error: "no", routingHint: null, code, details: null, messageId });
const errorV0 = (): CultNetMessage =>
  ({ schemaVersion: "cultnet.error.v0", error: "no", routingHint: null, code: "write_not_authorized", details: null });

test("error.v1 accepts every code of its closed enum, limit_reached included, and refuses any other", () => {
  const schemaCodes = ((cultNetSchemas.errorV1Schema.properties.code as { enum: (string | null)[] }).enum).filter((c) => c !== null);
  assert.deepEqual([...CULTNET_ERROR_V1_CODES].sort(), [...schemaCodes].sort());
  for (const code of CULTNET_ERROR_V1_CODES) parseCultNetMessage({ ...refusedV1("w1"), code });
  assert.throws(() => parseCultNetMessage({ ...refusedV1("w1"), code: "invented" }));
});

test("error.v1 echoes messageId byte for byte, allows nil, and requires the key", () => {
  assert.equal((parseCultNetMessage(refusedV1("w-é 1")) as CultNetErrorV1Message).messageId, "w-é 1");
  assert.equal((parseCultNetMessage(refusedV1(null)) as CultNetErrorV1Message).messageId, null);
  const { messageId: _omitted, ...withoutId } = refusedV1("w1");
  assert.throws(() => parseCultNetMessage(withoutId));
  assert.throws(() => parseCultNetMessage(refusedV1("")));
});

test("error.v0 is unaffected: still closed, no messageId, no limit_reached", () => {
  assert.deepEqual([...CULTNET_ERROR_CODES].includes("limit_reached" as never), false);
  assert.throws(() => parseCultNetMessage({ ...errorV0(), code: "limit_reached" }));
  assert.throws(() => parseCultNetMessage({ ...errorV0(), messageId: "w1" }));
  assert.equal(parseCultNetMessage(errorV0()).schemaVersion, "cultnet.error.v0");
});

test("write_admitted needs all five keys, a known operation and no error key", () => {
  parseCultNetMessage(admitted("w1"));
  parseCultNetMessage({ ...admitted("w1"), operation: "delete" });
  assert.throws(() => parseCultNetMessage({ ...admitted("w1"), operation: "upsert" }));
  assert.throws(() => parseCultNetMessage({ ...admitted("w1"), error: "nope" }));
  const { recordKey: _omitted, ...short } = admitted("w1");
  assert.throws(() => parseCultNetMessage(short));
});

test("a hello is receipt-capable only when it lists both frames", () => {
  assert.equal(helloAdvertisesWriteReceipts({ supportedMessageVersions: [...CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS] }), true);
  assert.equal(helloAdvertisesWriteReceipts({ supportedMessageVersions: ["cultnet.write_admitted.v0"] }), false);
  assert.equal(helloAdvertisesWriteReceipts({ supportedMessageVersions: ["cultnet.error.v1"] }), false);
  assert.equal(helloAdvertisesWriteReceipts({}), false);
  const hello = parseCultNetMessage({ schemaVersion: "cultnet.hello.v0", runtimeId: "r", runtimeKind: "k", supportedMessageVersions: [...CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS] });
  assert.equal(hello.schemaVersion, "cultnet.hello.v0");
});

test("the window is 5000 ms for a receipt-capable server and 500 ms otherwise", () => {
  assert.equal(resultWindowMs(true), RECEIPT_RESULT_WINDOW_MS);
  assert.equal(resultWindowMs(false), LEGACY_RESULT_WINDOW_MS);
  assert.equal(RECEIPT_RESULT_WINDOW_MS, 5000);
  assert.equal(LEGACY_RESULT_WINDOW_MS, 500);
});

function tracker(windowMs = 500) {
  const told: [string, WriteSettlement][] = [];
  return { told, t: new WriteTracker({ windowMs, onSettle: (id, s) => told.push([id, s]) }) };
}

test("tracker: the window is inclusive at exactly windowMs and open one ms before", () => {
  const { t, told } = tracker();
  t.send("w1", 100);
  t.tick(599);
  assert.equal(t.state("w1")?.outcome, "pending");
  t.tick(600);
  assert.deepEqual(told, [["w1", { outcome: "unconfirmed", reason: "window_elapsed" }]]);
});

test("tracker: each write's window runs from its own send", () => {
  const { t } = tracker();
  t.send("w1", 0);
  t.send("w2", 300);
  t.tick(500);
  assert.equal(t.state("w1")?.outcome, "unconfirmed");
  assert.equal(t.state("w2")?.outcome, "pending");
});

test("tracker: a frame arriving after the window cannot admit the write; it is recorded late", () => {
  const { t, told } = tracker();
  t.send("w1", 0);
  t.frame(admitted("w1"), 500);
  assert.deepEqual(t.state("w1"), { outcome: "unconfirmed", reason: "window_elapsed", recorded: "admitted_late" });
  assert.equal(told.length, 1);
});

test("tracker: a write settles once; later frames neither change it nor flip a late record", () => {
  const { t, told } = tracker();
  t.send("w1", 0);
  t.frame(refusedV1("w1"), 10);
  t.frame(admitted("w1"), 20);
  t.frame(refusedV1("w1", "message_invalid"), 30);
  t.sessionEnd(40);
  assert.equal(told.length, 1);
  assert.deepEqual(told[0][1], { outcome: "refused", code: "write_not_authorized", error: "no", response: refusedV1("w1") });
  t.send("w2", 100);
  t.tick(600);
  t.frame(refusedV1("w2"), 700);
  t.frame(admitted("w2"), 800);
  assert.equal(t.state("w2")?.outcome === "unconfirmed" && t.state("w2")?.recorded, "refused_late");
});

test("tracker: an id-less error refuses the single unanswered write only, counting answered writes out", () => {
  const { t } = tracker();
  t.send("w1", 0);
  t.send("w2", 1);
  t.frame(admitted("w1"), 5);
  t.frame(refusedV1(null), 6);
  assert.equal(t.state("w1")?.outcome, "admitted");
  assert.equal(t.state("w2")?.outcome, "refused");
});

test("tracker: an id-less error never reaches a write that already ended unconfirmed", () => {
  const { t } = tracker();
  t.send("w1", 0);
  t.tick(500);
  t.frame(errorV0(), 501);
  assert.deepEqual(t.state("w1"), { outcome: "unconfirmed", reason: "window_elapsed" });
});

test("tracker: sessionEnd settles unanswered writes only, and an expired window keeps its own reason", () => {
  const { t } = tracker();
  t.send("w1", 0);
  t.send("w2", 0);
  t.send("w3", 400);
  t.frame(admitted("w2"), 10);
  t.sessionEnd(600);
  assert.deepEqual(t.state("w1"), { outcome: "unconfirmed", reason: "window_elapsed" });
  assert.equal(t.state("w2")?.outcome, "admitted");
  assert.deepEqual(t.state("w3"), { outcome: "unconfirmed", reason: "session_ended" });
});

test("tracker: re-sending a known id is the same write and does not restart its window", () => {
  const { t } = tracker();
  t.send("w1", 0);
  t.send("w1", 400);
  t.tick(500);
  assert.equal(t.state("w1")?.outcome, "unconfirmed");
});

test("tracker: a send past another write's window settles that write at once", () => {
  const { t, told } = tracker();
  t.send("w1", 0);
  t.send("w2", 600);
  assert.deepEqual(told, [["w1", { outcome: "unconfirmed", reason: "window_elapsed" }]]);
});

test("tracker: only receipt and error frames answer; any other frame naming the id settles nothing", () => {
  const { t } = tracker();
  t.send("w1", 0);
  t.frame({ schemaVersion: "cultnet.hello.v0", runtimeId: "r", runtimeKind: "k" }, 1);
  t.frame({ schemaVersion: "cultnet.document_put_raw.v0", messageId: "w1" } as unknown as CultNetMessage, 2);
  assert.equal(t.state("w1")?.outcome, "pending");
});

test("tracker: nextDeadlineMs reports the nearest unanswered window, none when all settled", () => {
  const { t } = tracker();
  assert.equal(t.nextDeadlineMs(0), undefined);
  t.send("w1", 0);
  t.send("w2", 100);
  assert.equal(t.nextDeadlineMs(50), 450);
  assert.equal(t.nextDeadlineMs(900), 0);
  t.frame(admitted("w1"), 60);
  assert.equal(t.nextDeadlineMs(150), 450);
  t.frame(admitted("w2"), 160);
  assert.equal(t.nextDeadlineMs(170), undefined);
});

test("the replay runner passes every vector against this runtime", () => {
  const script = join(__dirname, "..", "..", "tools", "replay-write-receipt-vectors.mjs");
  const run = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /^PASS scenario\/receipt_admits$/m);
  assert.doesNotMatch(run.stdout, /^FAIL/m);
});

// A runner that skips what it cannot read passes a file it did not replay. It must fail instead.
test("the replay runner fails on an unknown event key and on a vector count the file does not declare", () => {
  const script = join(__dirname, "..", "..", "tools", "replay-write-receipt-vectors.mjs");
  const vectorsPath = join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop", "write-receipt-vectors.json");
  const dir = mkdtempSync(join(tmpdir(), "receipt-vectors-"));
  const runWith = (edit: (vectors: any) => void) => {
    const vectors = JSON.parse(readFileSync(vectorsPath, "utf8"));
    edit(vectors);
    const file = join(dir, "vectors.json");
    writeFileSync(file, JSON.stringify(vectors));
    return spawnSync(process.execPath, [script, file], { encoding: "utf8" });
  };
  const typo = runWith((v) => { v.scenarios.cases[0].events[0] = { atMs: 0, sendfailed: "w1" }; });
  assert.equal(typo.status, 1, typo.stdout);
  assert.match(typo.stdout, /^FAIL scenario\/receipt_admits$/m);
  const dropped = runWith((v) => { v.scenarios.cases.pop(); });
  assert.equal(dropped.status, 1, dropped.stdout);
  assert.match(dropped.stdout, /^FAIL counts$/m);
  const undeclared = runWith((v) => { v.wire.push(v.wire[0]); });
  assert.equal(undeclared.status, 1, undeclared.stdout);
});

test("a write sent after the session ended settles unconfirmed session_ended at once, and the earlier receipt stands", () => {
  const settled: [string, WriteSettlement][] = [];
  const tracker = new WriteTracker({ windowMs: 1000, onSettle: (id, settlement) => settled.push([id, settlement]) });
  tracker.send("w1", 0);
  tracker.frame(admitted("w1"), 5);
  tracker.sessionEnd(40);
  tracker.send("w2", 50);
  assert.deepEqual(settled, [["w1", { outcome: "admitted" }], ["w2", { outcome: "unconfirmed", reason: "session_ended" }]]);
});

test("a failed local send settles only that write unconfirmed as local_send_failed, and an answer after it is recorded late", () => {
  const settled: [string, WriteSettlement][] = [];
  const tracker = new WriteTracker({ windowMs: 1000, onSettle: (id, settlement) => settled.push([id, settlement]) });
  tracker.send("w1", 0);
  tracker.send("w2", 0);
  tracker.sendFailed("w1", 10);
  assert.deepEqual(settled, [["w1", { outcome: "unconfirmed", reason: "local_send_failed" }]]);
  assert.equal(tracker.state("w2")?.outcome, "pending");
  tracker.frame(admitted("w1"), 20);
  assert.deepEqual(tracker.state("w1"), { outcome: "unconfirmed", reason: "local_send_failed", recorded: "admitted_late" });
  tracker.sendFailed("w2", 30);
  tracker.sendFailed("w2", 31);
  assert.equal(settled.length, 2, "a write settles once");
  tracker.sendFailed("unknown", 40);
  assert.equal(tracker.state("unknown"), undefined);
});

test("a failed local send advances the clock: other writes whose windows have run settle window_elapsed", () => {
  const tracker = new WriteTracker({ windowMs: 1000 });
  tracker.send("w1", 0);
  tracker.send("w2", 500);
  tracker.sendFailed("w2", 1200);
  assert.deepEqual(tracker.state("w1"), { outcome: "unconfirmed", reason: "window_elapsed" });
  assert.deepEqual(tracker.state("w2"), { outcome: "unconfirmed", reason: "local_send_failed" });
});

test("a write whose own send failed settles local_send_failed even when its window has also run", () => {
  const tracker = new WriteTracker({ windowMs: 1000 });
  tracker.send("w1", 0);
  tracker.sendFailed("w1", 1000);
  assert.deepEqual(tracker.state("w1"), { outcome: "unconfirmed", reason: "local_send_failed" });
});

// Blank is Unicode White_Space (write-receipt.md, rule 5), not what String.prototype.trim() trims: trim() takes U+FEFF and
// misses U+0085. Every frame decoder that rejects a blank text must agree with the servers that do.
test("a blank messageId, record key or tag is exactly Unicode White_Space, not trim()", () => {
  const put = (document: Record<string, unknown>, messageId = "p1") => ({
    schemaVersion: "cultnet.document_put_raw.v0", messageId,
    document: { schemaId: "note.v0", recordKey: "note:1", storedAt: "2026-11-08T00:00:00Z", payloadEncoding: "messagepack", payload: Uint8Array.of(0x81, 0xa1, 0x61, 0x01), ...document },
  });
  for (const blank of ["\u0085", "  ", "　"]) {
    assert.throws(() => parseCultNetMessage(put({}, blank)), `messageId ${JSON.stringify(blank)}`);
    assert.throws(() => parseCultNetMessage(put({ recordKey: blank })), `recordKey ${JSON.stringify(blank)}`);
    assert.throws(() => parseCultNetMessage(put({ tags: [blank] })), `tag ${JSON.stringify(blank)}`);
  }
  for (const named of ["﻿", "​", "\u001c", "᠎"]) {
    parseCultNetMessage(put({}, named));
    parseCultNetMessage(put({ recordKey: named }));
    parseCultNetMessage(put({ tags: [named] }));
  }
});

// `admittedMessageId` vectors: a write_admitted messageId needs a letter or number and no Default_Ignorable code point,
// compared raw (never trimmed). The D800 vector is the frame's bytes (wireHex), decoded by the msgpack library like the others.
test("write_admitted messageId: every admittedMessageId vector is refused or accepted raw", () => {
  const vectorsPath = join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop", "write-receipt-vectors.json");
  const vectors = JSON.parse(readFileSync(vectorsPath, "utf8"));
  assert.equal(vectors.counts.admittedMessageIdCases, vectors.admittedMessageId.cases.length);
  const failures: string[] = [];
  for (const vector of vectors.admittedMessageId.cases as { name: string; codePoints: string[]; repeat?: number; wireHex?: string; expect: "refuse" | "accept" }[]) {
    const id = vector.codePoints.map((cp) => String.fromCodePoint(parseInt(cp, 16))).join("").repeat(vector.repeat ?? 1);
    let decodedId: string | undefined;
    try {
      const frame = vector.wireHex ? decode(Uint8Array.from(Buffer.from(vector.wireHex, "hex"))) : admitted(id);
      decodedId = (parseCultNetMessage(frame) as CultNetWriteAdmittedMessage).messageId;
    } catch {
      decodedId = undefined;
    }
    if ((decodedId === undefined) !== (vector.expect === "refuse")) failures.push(`${vector.name}: expected ${vector.expect}`);
    else if (decodedId !== undefined && decodedId !== id) failures.push(`${vector.name}: id not echoed raw`);
  }
  assert.deepEqual(failures, []);
});

// The generated ranges file is the independent copy of the embedded tables: first, last and the points either side of every range.
test("write_admitted messageId: every range of the Unicode 16.0 tables is pinned at both ends", () => {
  const ranges = JSON.parse(readFileSync(join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop", "write-admitted-message-id-ranges.json"), "utf8"));
  assert.ok(ranges.letterOrNumber.length > 700);
  assert.equal(ranges.defaultIgnorable.length, 17);
  const accepted = (id: string) => { try { parseCultNetMessage(admitted(id)); return true; } catch { return false; } };
  const failures: string[] = [];
  for (const [low, high] of ranges.letterOrNumber as [number, number][]) {
    for (const [point, want] of [[low, true], [high, true], [low - 1, false], [high + 1, false]] as [number, boolean][]) {
      if (accepted(String.fromCodePoint(point)) !== want) failures.push(`U+${point.toString(16)} alone should be ${want ? "accepted" : "refused"}`);
    }
  }
  for (const [low, high] of ranges.defaultIgnorable as [number, number][]) {
    for (const [point, want] of [[low, false], [high, false], [low - 1, true], [high + 1, true]] as [number, boolean][]) {
      if (accepted("a" + String.fromCodePoint(point)) !== want) failures.push(`a + U+${point.toString(16)} should be ${want ? "accepted" : "refused"}`);
    }
  }
  assert.deepEqual(failures, []);
});

// TypeScript decodes the frame to an object, so it cannot see a duplicate key: it judges the last one (admittedMessageIdDuplicates).
// This pins @msgpack/msgpack's last-key behaviour plus the id check on the decoded object, not a first-key or duplicate path in
// cultnet-ts source: there is none, so a refuse-on-first mutation in src would not be killed.
test("write_admitted messageId: a repeated key follows the last-key-wins policy", () => {
  const vectors = JSON.parse(readFileSync(join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "interop", "write-receipt-vectors.json"), "utf8"));
  const section = vectors.admittedMessageIdDuplicates;
  assert.equal(vectors.counts.admittedMessageIdDuplicateCases, section.cases.length);
  for (const vector of section.cases as { name: string; wireHex: string; lastKeyWins: "accept" | "refuse"; lastKeyWinsEcho?: string }[]) {
    const frame = decode(Uint8Array.from(Buffer.from(vector.wireHex, "hex")));
    if (vector.lastKeyWins === "refuse") assert.throws(() => parseCultNetMessage(frame), /./s, vector.name);
    else assert.equal((parseCultNetMessage(frame) as CultNetWriteAdmittedMessage).messageId, vector.lastKeyWinsEcho, vector.name);
  }
});
