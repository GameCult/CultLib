// Heimdall's invariant: no client may treat a reliable-transport ack as write admission. A put completes only
// on its result frame (write_not_authorized, or the success reply). Each test serves a client a peer that
// acknowledges the put and refuses it, the ack arriving before the refusal and after it.
import assert from "node:assert/strict";
import { createSocket, type Socket } from "node:dgram";
import test from "node:test";
import { z } from "zod";
import { defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  CultNetRudpSession,
  cultNetWriteNotAuthorizedError,
  decodeRudpPacket,
  defineCultNetDocumentBinding,
  encodeCultNetMessageForWire,
  encodeRudpPacket,
} from "@gamecult/cultnet-ts";
import { encodeWireValue } from "@gamecult/cultnet-ts/wire";
import { CultMesh, CultMeshGrantAllWrites, CultMeshRudpWriteRefusedError } from "../src/index";

const isTheRefusal = (error: unknown): boolean =>
  error instanceof CultMeshRudpWriteRefusedError && error.code === "write_not_authorized";

const note = defineDocumentType({
  type: "cultmesh.completion_note",
  schemaId: "cultmesh.completion_note.v0",
  schema: z.object({ noteId: z.string(), text: z.string() }),
  name: "noteId",
});
const binding = defineCultNetDocumentBinding({ definition: note });
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

type Order = "ack-then-refusal" | "refusal-then-ack";

/** A peer that acknowledges every reliable put and refuses it with write_not_authorized, in the given order. */
async function refusingPeer(connectionId: number, order: Order) {
  const socket: Socket = createSocket("udp4");
  await new Promise<void>((resolve) => socket.bind(0, "127.0.0.1", resolve));
  const session = new CultNetRudpSession({ connectionId, resendDelayMs: 50 });
  const timers = new Set<NodeJS.Timeout>();
  let closed = false;
  const send = (packet: Parameters<typeof encodeRudpPacket>[0], port: number, address: string) => {
    if (!closed) socket.send(encodeRudpPacket(packet), port, address);
  };
  const later = (action: () => void) => { const timer = setTimeout(() => { timers.delete(timer); action(); }, 120); timers.add(timer); };
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
      const ack = session.createAckForReceived(packet.sequence);
      const refusal = session.sendMany(
        "schema",
        encodeWireValue(encodeCultNetMessageForWire(cultNetWriteNotAuthorizedError("tests:completion:a"), "cultnet.schema.v0")),
        { reliable: true, ordered: true, nowMs: now },
      );
      if (order === "ack-then-refusal") {
        send(ack, remote.port, remote.address);
        later(() => refusal.forEach((p) => send(p, remote.port, remote.address)));
      } else {
        // The refusal carries no acknowledgement of its own; the ack follows it.
        refusal.forEach((p) => send({ ...p, ack: 0, ackMask: 0 }, remote.port, remote.address));
        later(() => send(ack, remote.port, remote.address));
      }
    }
    if (packet.reliable) send(session.createAckForReceived(packet.sequence), remote.port, remote.address);
  });
  return { endpoint: `rudp://127.0.0.1:${socket.address().port}`, close: () => { closed = true; timers.forEach(clearTimeout); socket.close(); } };
}

const orders: Order[] = ["ack-then-refusal", "refusal-then-ack"];
let nextConnection = 0x10205000;

for (const order of orders) {
  test(`publishRudpDocumentOnce does not complete on the ack of a refused put (${order})`, async () => {
    const peer = await refusingPeer(nextConnection, order);
    try {
      await assert.rejects(
        CultMesh.publishRudpDocumentOnce("completion-once", nextConnection++, peer.endpoint, binding, "tests:completion:a", { noteId: "a", text: "x" }, { resendDelayMs: 50, resendPollMs: 5, flushTimeoutMs: 2_000 }),
        isTheRefusal,
      );
    } finally { peer.close(); }
  });

  test(`publishRudpDocumentAndWaitForReceipt reports a refused put as refused, never as success (${order})`, async () => {
    const peer = await refusingPeer(nextConnection, order);
    try {
      await assert.rejects(
        CultMesh.publishRudpDocumentAndWaitForReceipt("completion-receipt", nextConnection++, peer.endpoint, binding, "tests:completion:a", { noteId: "a", text: "x" }, binding, { resendDelayMs: 50, resendPollMs: 5, receiptTimeoutMs: 800 }),
        isTheRefusal,
      );
    } finally { peer.close(); }
    await sleep(10);
  });
}

test("publishRudpDocumentOnce still publishes a put the server admitted", async () => {
  const connectionId = nextConnection++;
  const puts: string[] = [];
  const server = CultMesh.createRudpDocumentServer("completion-admit", connectionId, {
    documents: new CultNetDocumentRegistry([binding]),
    writeAuthority: new CultMeshGrantAllWrites(),
    bindHost: "127.0.0.1", bindPort: 0, resendDelayMs: 50, resendPollMs: 5,
    onDocumentPutRaw: (put) => { puts.push(put.recordKey); },
  });
  await server.start();
  try {
    await CultMesh.publishRudpDocumentOnce("completion-admit-client", connectionId, `rudp://127.0.0.1:${server.bind.port}`, binding, "tests:completion:a", { noteId: "a", text: "x" }, { resendDelayMs: 50, resendPollMs: 5, resultWindowMs: 200 });
    assert.deepEqual(puts, ["tests:completion:a"]);
  } finally { server.close(); }
});
