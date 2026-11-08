import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { join } from "node:path";

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
