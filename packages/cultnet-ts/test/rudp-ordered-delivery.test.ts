// Ordered delivery has one owner: the contiguous received-through watermark. A
// frame is held for exactly one reason, a reliable sequence below it has not
// arrived, and it is delivered in the call that fills the last such gap,
// whichever channel the gap belonged to.
import assert from "node:assert/strict";
import test from "node:test";
import { CultNetRudpSession } from "../src";

const enc = (text: string) => new TextEncoder().encode(text);
const dec = (bytes: Uint8Array) => new TextDecoder().decode(bytes);

function handshake(): { client: CultNetRudpSession; server: CultNetRudpSession } {
  const client = new CultNetRudpSession({ connectionId: 410, initialSequence: 1 });
  const server = new CultNetRudpSession({ connectionId: 410, initialSequence: 500 });
  const accept = server.acceptConnect(client.createConnect(0), 0);
  client.receive(accept, 0);
  return { client, server };
}

const names = (result: { delivered: { payload: Uint8Array }[] }) => result.delivered.map((frame) => dec(frame.payload));
const send = (session: CultNetRudpSession, channel: string, payload: string, ordered: boolean) =>
  session.send(channel, enc(payload), { reliable: true, ordered });

test("an ordered frame waits for a gap filled by another channel", () => {
  const { client: sender, server: receiver } = handshake();
  const o1 = send(sender, "schema", "o1", true);
  const u = send(sender, "rel", "u", false);
  const o2 = send(sender, "schema", "o2", true);
  const o3 = send(sender, "schema", "o3", true);

  assert.deepEqual(names(receiver.receive(o1, 1)), ["o1"]);
  assert.deepEqual(names(receiver.receive(o2, 2)), [], "u is missing");
  assert.deepEqual(names(receiver.receive(o3, 3)), [], "u is missing");
  assert.deepEqual(names(receiver.receive(u, 4)), ["u", "o2", "o3"]);
});

test("two ordered channels release each other in sequence order", () => {
  const { client: sender, server: receiver } = handshake();
  const a1 = send(sender, "schema", "A1", true);
  const b1 = send(sender, "other", "B1", true);
  const a2 = send(sender, "schema", "A2", true);
  const a3 = send(sender, "schema", "A3", true);

  const delivered = [
    ...names(receiver.receive(a1, 1)),
    ...names(receiver.receive(a2, 2)),
    ...names(receiver.receive(a3, 3)),
  ];
  assert.deepEqual(delivered, ["A1"], "B1 is missing, so A2 and A3 wait");
  assert.deepEqual(names(receiver.receive(b1, 4)), ["B1", "A2", "A3"]);
});

test("a channel first used after other traffic loses nothing", () => {
  const { client: sender, server: receiver } = handshake();
  const c1 = send(sender, "late", "C1", true);
  const c2 = send(sender, "late", "C2", true);
  const x = send(sender, "schema", "X", true);

  assert.deepEqual(names(receiver.receive(x, 1)), [], "C1 is missing");
  assert.deepEqual(names(receiver.receive(c2, 2)), [], "C1 is missing");
  assert.deepEqual(names(receiver.receive(c1, 3)), ["C1", "C2", "X"]);
});

test("Accept seeds the watermark of the connecting side", () => {
  const { client, server } = handshake();
  const s1 = send(server, "schema", "s1", true);
  const s2 = send(server, "schema", "s2", true);

  assert.deepEqual(names(client.receive(s2, 1)), []);
  assert.deepEqual(names(client.receive(s1, 2)), ["s1", "s2"]);
});

test("Connect seeds the watermark of the accepting side", () => {
  const { client, server } = handshake();
  const first = send(client, "schema", "first", true);
  const second = send(client, "schema", "second", true);

  assert.deepEqual(names(server.receive(second, 1)), []);
  assert.deepEqual(names(server.receive(first, 2)), ["first", "second"]);
});

test("a duplicate of a held frame is not delivered twice", () => {
  const { client: sender, server: receiver } = handshake();
  const s1 = send(sender, "schema", "s1", true);
  const s2 = send(sender, "schema", "s2", true);

  assert.deepEqual(names(receiver.receive(s2, 1)), []);
  assert.deepEqual(names(receiver.receive(s2, 2)), []);
  assert.deepEqual(names(receiver.receive(s1, 3)), ["s1", "s2"]);
  assert.deepEqual(names(receiver.receive(s2, 4)), []);
});

test("resetting peer state forgets held frames and the watermark", () => {
  const { client: sender, server: receiver } = handshake();
  const s1 = send(sender, "schema", "s1", true);
  const s2 = send(sender, "schema", "s2", true);
  assert.deepEqual(names(receiver.receive(s2, 1)), []);

  receiver.resetPeerState();
  receiver.receive({ packetType: "accept", connectionId: 410, sequence: 1, ack: 0, ackMask: 0, channelId: "control" }, 2);

  assert.deepEqual(names(receiver.receive(s1, 3)), ["s1"], "the held s2 died with the reset");
});
