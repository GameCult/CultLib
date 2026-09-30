// A Connect's sequence says whether it repeats the one a session accepted, and
// the handshake alone seeds the watermark that orders delivery.
import assert from "node:assert/strict";
import test from "node:test";
import { CultNetRudpSession, randomInitialSequence, type CultNetRudpPacket } from "../src";

const connectionId = 0x10203080;
const enc = (text: string) => new TextEncoder().encode(text);
const dec = (bytes: Uint8Array) => new TextDecoder().decode(bytes);
const session = (initialSequence: number) => new CultNetRudpSession({ connectionId, initialSequence });
const send = (from: CultNetRudpSession, text: string) => from.send("schema", enc(text), { reliable: true, ordered: true });
const names = (result: { delivered: { payload: Uint8Array }[] }) => result.delivered.map((frame) => dec(frame.payload));

function handshake(client: CultNetRudpSession, server: CultNetRudpSession): { connect: CultNetRudpPacket; accept: CultNetRudpPacket } {
  const connect = client.createConnect(0);
  const accept = server.acceptConnect(connect, 0);
  client.receive(accept, 0);
  assert.ok(client.connected && server.connected);
  return { connect, accept };
}

test("a reconnect on the same session is not stranded by a write the ended session owed", () => {
  const client = session(1);
  const server = session(500);
  handshake(client, server);
  send(server, "lost");
  server.createDisconnect(enc("gone"));

  client.receive(server.acceptConnect(client.createConnect(1), 1), 1);
  assert.deepEqual(names(client.receive(send(server, "after"), 2)), ["after"]);
});

test("a server that accepts a new client forgets the old client's sequences", () => {
  const oldClient = session(50);
  const server = session(500);
  handshake(oldClient, server);
  for (const name of ["c1", "c2", "c3"]) {
    assert.deepEqual(names(server.receive(send(oldClient, name), 1)), [name]);
  }
  server.createDisconnect(enc("restart"));

  const newClient = session(2);
  newClient.receive(server.acceptConnect(newClient.createConnect(2), 2), 2);
  assert.deepEqual(names(server.receive(send(newClient, "fresh"), 3)), ["fresh"]);
});

test("a retransmitted Connect after data has flowed does not reset the session", () => {
  const client = session(1);
  const server = session(500);
  const { connect } = handshake(client, server);
  const frame = send(client, "once");
  assert.deepEqual(names(server.receive(frame, 1)), ["once"]);

  assert.equal(server.connectRepeats(connect), true);
  assert.equal(server.acceptConnect(connect, 2).packetType, "ack", "the Accept was already acknowledged");
  assert.equal(server.outstandingReliablePacketCount, 0, "a repeat queued something");
  assert.deepEqual(server.receive(frame, 3).delivered, [], "a retransmitted Connect made the server forget what it had delivered");
});

test("a Connect with another sequence is not a repeat whatever it carries", () => {
  const server = session(500);
  const connect = session(1).createConnect(0, enc("same payload"));
  server.acceptConnect(connect, 0);
  assert.equal(server.connectRepeats(connect), true);

  assert.equal(server.connectRepeats(session(9).createConnect(0, enc("same payload"))), false);
  assert.equal(server.connectRepeats({ ...connect, packetType: "ping" }), false);
  server.createDisconnect();
  assert.equal(server.connectRepeats(connect), false, "an ended session repeats nothing");
});

test("an Accept that does not name the pending Connect is ignored", () => {
  const client = session(10);
  const server = session(500);
  const connect = client.createConnect(0);
  const accept = server.acceptConnect(connect, 0);

  client.receive({ ...accept, ack: connect.sequence + 50, ackMask: 0 }, 1);
  assert.equal(client.connected, false, "a stale Accept connected the session");
  assert.deepEqual(client.pendingReliableSequences, [connect.sequence]);

  client.receive({ ...accept, ack: connect.sequence + 3, ackMask: 1 << 2 }, 2);
  assert.equal(client.connected, true, "the mask names the Connect");
});

test("an Accept after a local disconnect does not reconnect the session", () => {
  const client = session(1);
  const server = session(500);
  const { accept } = handshake(client, server);
  client.createDisconnect(enc("bye"));
  assert.equal(client.connected, false);

  client.receive(accept, 5);
  assert.equal(client.connected, false, "a late Accept revived an ended session");
});

test("a reconnect forgets what the old server sent", () => {
  const client = session(1);
  const oldServer = session(500);
  handshake(client, oldServer);
  for (const name of ["d1", "d2", "d3"]) {
    assert.deepEqual(names(client.receive(send(oldServer, name), 1)), [name]);
  }

  const newServer = session(501);
  client.receive(newServer.acceptConnect(client.createConnect(2), 2), 2);
  assert.deepEqual(names(client.receive(send(newServer, "fresh"), 3)), ["fresh"]);
});

test("an Accept for an abandoned Connect does not connect the session", () => {
  const client = session(1);
  const server = session(500);
  const accept = server.acceptConnect(client.createConnect(0), 0);
  client.createDisconnect(enc("never mind"));

  client.receive(accept, 1);
  assert.equal(client.connected, false, "an Accept revived an abandoned Connect");
});

test("a duplicate Accept does not reseed the watermark", () => {
  const client = session(1);
  const server = session(500);
  const { accept } = handshake(client, server);
  const s1 = send(server, "s1");
  const s2 = send(server, "s2");
  client.receive({ ...accept, sequence: s2.sequence }, 1);
  assert.deepEqual(client.receive(s2, 2).delivered, [], "s1 is still missing");
  assert.deepEqual(names(client.receive(s1, 3)), ["s1", "s2"]);
});

test("reliable data before the Accept is neither delivered nor acknowledged", () => {
  const client = session(1);
  const server = session(500);
  const accept = server.acceptConnect(client.createConnect(0), 0);
  const a = send(server, "A");
  const b = send(server, "B");

  for (const early of [b, a]) {
    assert.deepEqual(client.receive(early, 1).delivered, []);
    const ack = client.createAckForReceived(early.sequence);
    assert.deepEqual([ack.ack, ack.ackMask], [0, 0], "an unhandled packet was acknowledged");
  }
  client.receive(accept, 2);
  assert.ok(client.connected);
  assert.equal(server.outstandingReliablePacketCount, 3, "nothing was acknowledged");

  assert.deepEqual(names(client.receive(a, 3)), ["A"]);
  assert.deepEqual(names(client.receive(b, 3)), ["B"]);
});

test("ordered frames after the Accept are delivered in order whatever arrives first", () => {
  const client = session(1);
  const server = session(500);
  handshake(client, server);
  const a = send(server, "A");
  const b = send(server, "B");
  assert.deepEqual(client.receive(b, 1).delivered, []);
  assert.deepEqual(names(client.receive(a, 2)), ["A", "B"]);
});

test("an unordered reliable frame is delivered on receipt, never held behind a gap", () => {
  const client = session(1);
  const server = session(500);
  handshake(client, server);
  send(server, "lost");
  const unordered = server.send("media", enc("now"), { reliable: true, ordered: false });
  const ordered = send(server, "later");
  assert.deepEqual(names(client.receive(unordered, 1)), ["now"]);
  assert.deepEqual(client.receive(ordered, 2).delivered, [], "the ordered frame waits for the gap");
});

test("a peer Disconnect does not forget what the peer sent", () => {
  const client = session(1);
  const server = session(500);
  handshake(client, server);
  const s1 = send(client, "s1");
  const s2 = send(client, "s2");
  assert.deepEqual(server.receive(s2, 1).delivered, [], "s1 is missing, so s2 is held");

  server.receive(client.createDisconnect(enc("bye")), 2);
  assert.deepEqual(names(server.receive(s1, 3)), ["s1", "s2"], "the Disconnect forgot the held frame");
  assert.deepEqual(server.receive(s2, 4).delivered, [], "the Disconnect forgot what was received");
});

test("default initial sequences are drawn at random from one to two to the thirty first", () => {
  const draws = new Set(Array.from({ length: 64 }, () => randomInitialSequence()));
  assert.ok(draws.size > 32, `does not draw at random: ${[...draws]}`);
  assert.ok([...draws].every((value) => value >= 1 && value < 2 ** 31), `left [1, 2^31): ${[...draws]}`);
  const firsts = new Set(Array.from({ length: 64 }, () => new CultNetRudpSession({ connectionId }).createConnect(0).sequence));
  assert.ok(firsts.size > 32, `a default session does not draw its own sequence: ${[...firsts]}`);
});
