// The presence publisher is send-only (write-receipt.md): Odin answers no write with a receipt yet, so no frame, whatever
// id it carries, is admission. Each test serves it a fake Odin that acknowledges the put and answers as scripted.
import assert from "node:assert/strict";
import { createSocket, type Socket } from "node:dgram";
import test from "node:test";
import { encode, decode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, type CultNetMessage } from "../src/contracts";
import { IdunnPresenceRefusedError, sendPresenceDocument } from "../src/idunn-odin-presence-publisher";
import { CultNetRudpSession, decodeRudpPacket, encodeRudpPacket } from "../src/rudp";

interface Script {
  answer: "silence" | "echo" | "admitted" | "refuse-v0" | "refuse-v1-other";
  /** Acknowledge the put? Default true. */
  ack?: boolean;
}

async function fakeOdin(connectionId: number, script: Script) {
  const socket: Socket = createSocket("udp4");
  await new Promise<void>((resolve) => socket.bind(0, "127.0.0.1", resolve));
  const session = new CultNetRudpSession({ connectionId, resendDelayMs: 50 });
  const send = (packet: Parameters<typeof encodeRudpPacket>[0], port: number, address: string) => {
    try { socket.send(encodeRudpPacket(packet), port, address); } catch { /* closed */ }
  };
  const say = (message: CultNetMessage, port: number, address: string) => {
    const packets = session.sendMany("schema", encode(encodeCultNetMessageForWire(message, "cultnet.schema.v0")), { reliable: true, ordered: true, nowMs: Date.now() });
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
      const put = decode(frame.payload) as { messageId: string; document: unknown };
      if (script.answer === "echo") say({ schemaVersion: "cultnet.document_put_raw.v0", messageId: put.messageId, document: put.document } as unknown as CultNetMessage, remote.port, remote.address);
      else if (script.answer === "admitted") say({ schemaVersion: "cultnet.write_admitted.v0", messageId: put.messageId, operation: "put", schemaId: "s", recordKey: "k" }, remote.port, remote.address);
      else if (script.answer === "refuse-v0") say({ schemaVersion: "cultnet.error.v0", error: "no", routingHint: null, code: "write_not_authorized", details: null }, remote.port, remote.address);
      else if (script.answer === "refuse-v1-other") say({ schemaVersion: "cultnet.error.v1", error: "no", routingHint: null, code: "write_not_authorized", details: null, messageId: "someone-else" }, remote.port, remote.address);
    }
    if (packet.reliable && script.ack !== false) send(session.createAckForReceived(packet.sequence), remote.port, remote.address);
  });
  return { port: socket.address().port, close: () => socket.close() };
}

let nextConnection = 0x0d1d1000;
const put = (id: string) => ({ schemaVersion: "cultnet.document_put_raw.v0", messageId: id, document: { schemaId: "s", recordKey: "k", storedAt: "t", payloadEncoding: "messagepack", payload: new Uint8Array([1]) } });
const sendTo = (server: { port: number }, id: string, connectionId: number) =>
  sendPresenceDocument({ host: "127.0.0.1", port: server.port }, connectionId, put(id));

for (const answer of ["silence", "echo", "admitted", "refuse-v1-other"] as const) {
  test(`a ${answer} Odin yields sent, never admitted: no frame carrying the put's id is success`, async () => {
    const connectionId = nextConnection++;
    const server = await fakeOdin(connectionId, { answer });
    try {
      const outcome = await sendTo(server, "presence-1", connectionId);
      assert.equal(outcome, "sent");
      assert.notEqual(outcome as string, "admitted");
    } finally { server.close(); }
  });
}

test("a refusal that arrives by the transport ack rejects the put", async () => {
  const connectionId = nextConnection++;
  const server = await fakeOdin(connectionId, { answer: "refuse-v0" });
  try {
    await assert.rejects(sendTo(server, "presence-1", connectionId), IdunnPresenceRefusedError);
  } finally { server.close(); }
});

test("a put the transport never acknowledges is an error, not sent", async () => {
  const connectionId = nextConnection++;
  const server = await fakeOdin(connectionId, { answer: "silence", ack: false });
  try {
    await assert.rejects(sendTo(server, "presence-1", connectionId), /Timed out/);
  } finally { server.close(); }
});
