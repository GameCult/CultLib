// Client side of contracts/cultnet/interop/write-receipt.md in the one-shot writer: with `writeReceipts` the put
// completes only on a write_admitted carrying its id; a refusal, a silent window and a legacy server each end as the
// spec says. Each test serves the client a fake RUDP peer that acknowledges the put and answers as scripted.
import assert from "node:assert/strict";
import { createSocket, type Socket } from "node:dgram";
import test from "node:test";
import { z } from "zod";
import { defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS,
  CultNetRudpSession,
  decodeRudpPacket,
  defineCultNetDocumentBinding,
  encodeCultNetMessageForWire,
  encodeRudpPacket,
  type CultNetMessage,
} from "@gamecult/cultnet-ts";
import { decodeWireValue, encodeWireValue } from "@gamecult/cultnet-ts/wire";
import { CultMesh, CultMeshRudpWriteRefusedError, CultMeshRudpWriteUnconfirmedError } from "../src/index";

const note = defineDocumentType({
  type: "cultmesh.receipt_note",
  schemaId: "cultmesh.receipt_note.v0",
  schema: z.object({ noteId: z.string(), text: z.string() }),
  name: "noteId",
});
const binding = defineCultNetDocumentBinding({ definition: note });
let nextConnection = 0x10206000;

interface Script {
  /** Does the server's hello list both receipt frames? */
  advertise: boolean;
  /** What the server answers a put with, `delayMs` after it arrives. */
  answer: "admit" | "refuse" | "wrong-id" | "silence" | "goodbye";
  delayMs?: number;
  /** Acknowledge the reliable packets it receives? Default true. */
  ack?: boolean;
}

async function fakeServer(connectionId: number, script: Script) {
  const socket: Socket = createSocket("udp4");
  await new Promise<void>((resolve) => socket.bind(0, "127.0.0.1", resolve));
  const session = new CultNetRudpSession({ connectionId, resendDelayMs: 50 });
  const timers = new Set<NodeJS.Timeout>();
  const seen: string[] = [];
  let closed = false;
  const send = (packet: Parameters<typeof encodeRudpPacket>[0], port: number, address: string) => {
    if (!closed) socket.send(encodeRudpPacket(packet), port, address);
  };
  const say = (message: CultNetMessage, port: number, address: string) => {
    const packets = session.sendMany("schema", encodeWireValue(encodeCultNetMessageForWire(message)), { reliable: true, ordered: true, nowMs: Date.now() });
    packets.forEach((p) => send(p, port, address));
  };
  socket.on("message", (wire, remote) => {
    const packet = decodeRudpPacket(wire);
    const now = Date.now();
    if (packet.packetType === "connect") {
      send(session.acceptConnect(packet, now), remote.port, remote.address);
      return;
    }
    const result = session.receive(packet, now);
    if (result.reply) send(result.reply, remote.port, remote.address);
    for (const frame of result.delivered) {
      if (frame.channelId !== "schema") continue;
      const message = decodeWireValue(frame.payload) as { schemaVersion: string; messageId?: string };
      seen.push(message.schemaVersion);
      if (message.schemaVersion === "cultnet.hello.v0") {
        say({
          schemaVersion: "cultnet.hello.v0", runtimeId: "fake", runtimeKind: "fake",
          ...(script.advertise ? { supportedMessageVersions: [...CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS] } : {}),
        }, remote.port, remote.address);
        continue;
      }
      if (message.schemaVersion !== "cultnet.document_put_raw.v0" || script.answer === "silence") continue;
      const timer = setTimeout(() => {
        timers.delete(timer);
        if (script.answer === "goodbye") {
          send(session.endUnsendableSession(new Error("scripted goodbye")), remote.port, remote.address);
        } else if (script.answer === "refuse") {
          say({ schemaVersion: "cultnet.error.v1", error: "write_not_authorized", routingHint: null, code: "write_not_authorized", details: null, messageId: message.messageId! }, remote.port, remote.address);
        } else {
          const messageId = script.answer === "wrong-id" ? "someone-else" : message.messageId!;
          say({ schemaVersion: "cultnet.write_admitted.v0", messageId, operation: "put", schemaId: "cultmesh.receipt_note.v0", recordKey: "tests:receipt:a" }, remote.port, remote.address);
        }
      }, script.delayMs ?? 20);
      timers.add(timer);
    }
    if (packet.reliable && script.ack !== false) send(session.createAckForReceived(packet.sequence), remote.port, remote.address);
  });
  return { endpoint: `rudp://127.0.0.1:${socket.address().port}`, seen, close: () => { closed = true; timers.forEach(clearTimeout); socket.close(); } };
}

const transport = { resendDelayMs: 50, resendPollMs: 5, flushTimeoutMs: 2_000 };
const publish = (server: { endpoint: string }, options: Record<string, unknown>) =>
  CultMesh.publishRudpDocumentOnce("receipt-client", nextConnection++, server.endpoint, binding, "tests:receipt:a", { noteId: "a", text: "x" }, { ...transport, ...options });

test("a receipt-capable server's write_admitted completes the put, past the legacy 500 ms window", async () => {
  const server = await fakeServer(nextConnection, { advertise: true, answer: "admit", delayMs: 700 });
  try {
    await publish(server, { writeReceipts: true });
    assert.deepEqual(server.seen.slice(0, 2), ["cultnet.hello.v0", "cultnet.document_put_raw.v0"]);
  } finally { server.close(); }
});

test("a receipt-capable server's error.v1 refuses the put with its code", async () => {
  const server = await fakeServer(nextConnection, { advertise: true, answer: "refuse" });
  try {
    await assert.rejects(publish(server, { writeReceipts: true }), (error: unknown) =>
      error instanceof CultMeshRudpWriteRefusedError && error.code === "write_not_authorized" && error.response.schemaVersion === "cultnet.error.v1");
  } finally { server.close(); }
});

test("a receipt-capable server that stays silent leaves the put unconfirmed, never published", async () => {
  const server = await fakeServer(nextConnection, { advertise: true, answer: "silence" });
  try {
    await assert.rejects(publish(server, { writeReceipts: true, resultWindowMs: 250 }), (error: unknown) =>
      error instanceof CultMeshRudpWriteUnconfirmedError && error.reason === "window_elapsed");
  } finally { server.close(); }
});

test("a receipt for another id settles nothing", async () => {
  const server = await fakeServer(nextConnection, { advertise: true, answer: "wrong-id" });
  try {
    await assert.rejects(publish(server, { writeReceipts: true, resultWindowMs: 250 }), CultMeshRudpWriteUnconfirmedError);
  } finally { server.close(); }
});

test("a server whose hello does not advertise leaves a silent put unconfirmed after the 500 ms window, never published", async () => {
  const server = await fakeServer(nextConnection, { advertise: false, answer: "silence" });
  const started = Date.now();
  try {
    await assert.rejects(publish(server, { writeReceipts: true }), (error: unknown) =>
      error instanceof CultMeshRudpWriteUnconfirmedError && error.reason === "window_elapsed");
    assert.ok(Date.now() - started >= 500, "the legacy window ran from the send");
  } finally { server.close(); }
});

test("a session that ends before any result leaves the put unconfirmed (session_ended), against a legacy server too", async () => {
  for (const advertise of [false, true]) {
    const server = await fakeServer(nextConnection, { advertise, answer: "goodbye" });
    try {
      await assert.rejects(publish(server, { writeReceipts: true, resultWindowMs: 3_000 }), (error: unknown) =>
        error instanceof CultMeshRudpWriteUnconfirmedError && error.reason === "session_ended");
    } finally { server.close(); }
  }
});

test("a put whose send is never acknowledged is unconfirmed as local_send_failed, not an untyped error", async () => {
  const server = await fakeServer(nextConnection, { advertise: true, answer: "silence", ack: false });
  try {
    await assert.rejects(publish(server, { writeReceipts: true, flushTimeoutMs: 300 }), (error: unknown) =>
      error instanceof CultMeshRudpWriteUnconfirmedError && error.reason === "local_send_failed");
  } finally { server.close(); }
});

test("without writeReceipts no hello is sent and the receipt frames are never asked for", async () => {
  const server = await fakeServer(nextConnection, { advertise: true, answer: "silence" });
  try {
    await assert.rejects(publish(server, { resultWindowMs: 100 }), CultMeshRudpWriteUnconfirmedError);
    assert.deepEqual(server.seen, ["cultnet.document_put_raw.v0"]);
  } finally { server.close(); }
});
