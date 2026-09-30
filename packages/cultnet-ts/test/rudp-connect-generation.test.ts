// A Connect's sequence says whether it repeats the one a session accepted, and
// the handshake alone seeds the watermark that orders delivery.
import assert from "node:assert/strict";
import test from "node:test";
import { createSocket, type Socket } from "node:dgram";
import { once } from "node:events";
import {
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
  decodeRudpPacket,
  encodeRudpPacket,
  type CultNetRudpPacket,
} from "../src";

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

test("a full queue owed to a vanished peer does not refuse a new generation", () => {
  const bounded = (initialSequence: number) => new CultNetRudpSession({ connectionId, initialSequence, maxPendingReliablePackets: 1 });
  const server = bounded(500);
  server.acceptConnect(session(1).createConnect(0), 0);
  assert.equal(server.outstandingReliablePacketCount, 1, "the Accept is owed and never acknowledged");
  const next = session(9).createConnect(1);
  server.acceptConnect(next, 1);
  assert.equal(server.connectRepeats(next), true);

  const client = bounded(1);
  client.createConnect(0);
  client.createConnect(1);
  assert.equal(client.outstandingReliablePacketCount, 1, "only the new Connect is owed");
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

test("sessions from one options object each draw their own initial sequence from one to two to the thirty first", () => {
  const options = { connectionId };
  const firsts = new Set(Array.from({ length: 64 }, () => new CultNetRudpSession(options).createConnect(0).sequence));
  assert.ok(firsts.size > 32, `a default session does not draw its own sequence: ${[...firsts]}`);
  assert.ok([...firsts].every((value) => value >= 1 && value < 2 ** 31), `left [1, 2^31): ${[...firsts]}`);
});

// Ack Cut 1d: a frame from an earlier generation is a duplicate, a delayed
// Connect from an earlier attempt is stale, a Connect from a new endpoint is a
// new client.

const namesSequence = (ack: CultNetRudpPacket, sequence: number) =>
  ack.ack === sequence
  || Array.from({ length: 32 }, (_, bit) => bit).some(
    (bit) => (ack.ackMask & (1 << bit)) !== 0 && ack.ack - bit - 1 === sequence,
  );

test("a frame from the client's earlier generation is acknowledged, not delivered again", () => {
  const client = session(10);
  const server = session(500);
  handshake(client, server);
  const old = send(client, "old");
  assert.deepEqual(names(server.receive(old, 1)), ["old"]);

  const connect = client.createConnect(2);
  client.receive(server.acceptConnect(connect, 2), 2);
  assert.ok(old.sequence < connect.sequence);
  assert.deepEqual(server.receive(old, 3).delivered, [], "an earlier generation's frame was delivered again");
  assert.ok(namesSequence(server.createAckForReceived(old.sequence), old.sequence), "the duplicate was not acknowledged");
  assert.deepEqual(names(server.receive(send(client, "fresh"), 4)), ["fresh"]);
});

test("a frame from the server's earlier generation is acknowledged, not delivered again", () => {
  const client = session(10);
  const server = session(500);
  handshake(client, server);
  const old = send(server, "old");
  assert.deepEqual(names(client.receive(old, 1)), ["old"]);

  client.receive(server.acceptConnect(client.createConnect(2), 2), 2);
  assert.deepEqual(client.receive(old, 3).delivered, [], "an earlier generation's frame was delivered again");
  assert.ok(namesSequence(client.createAckForReceived(old.sequence), old.sequence));
  assert.deepEqual(names(client.receive(send(server, "fresh"), 4)), ["fresh"]);
});

test("a duplicate below the receive window is acknowledged by name", () => {
  const client = session(10);
  const server = session(500);
  handshake(client, server);
  const first = send(client, "first");
  assert.deepEqual(names(server.receive(first, 1)), ["first"]);
  for (let index = 0; index < 4_200; index += 1) {
    server.receive(send(client, "x"), 1);
    client.receive(server.createAck(), 1);
  }
  assert.deepEqual(server.receive(first, 2).delivered, []);
  assert.equal(server.createAckForReceived(first.sequence).ack, first.sequence, "a duplicate below the window was not acknowledged by name");
});

test("a delayed Connect from an earlier attempt does not strand the client", () => {
  const client = session(10);
  const server = session(500);
  const earlier = client.createConnect(0);
  const retried = client.createConnect(300);
  const accept = server.acceptConnect(retried, 301);
  client.receive(accept, 302);
  server.receive(client.createAckForReceived(accept.sequence), 302);
  assert.ok(client.connected);
  const a = send(client, "a");
  assert.deepEqual(names(server.receive(a, 303)), ["a"]);
  client.receive(server.createAckForReceived(a.sequence), 303);

  assert.equal(server.connectRepeats(earlier), true, "the delayed Connect starts nothing");
  const reply = server.acceptConnect(earlier, 304);
  assert.equal(reply.packetType, "ack");
  client.receive(reply, 305);
  assert.ok(client.connected);

  const b = send(client, "b");
  assert.deepEqual(names(server.receive(b, 306)), ["b"], "the delayed Connect reset the server");
  client.receive(server.createAckForReceived(b.sequence), 307);
  assert.ok(!client.pendingReliableSequences.includes(b.sequence));
});

test("a repeated Connect refreshes liveness and a stale one does not", () => {
  const client = session(10);
  const server = session(500);
  const earlier = client.createConnect(0);
  const current = client.createConnect(1);
  server.acceptConnect(current, 0);

  server.acceptConnect(current, 900);
  assert.equal(server.checkTimeout(1_000, 500), false, "a repeated Connect did not refresh liveness");

  server.acceptConnect(earlier, 1_400);
  assert.equal(server.checkTimeout(1_600, 500), true, "a stale Connect refreshed liveness");
});

test("stale Connects are recognised by serial arithmetic", () => {
  const server = session(500);
  server.acceptConnect(session(3).createConnect(0), 0);
  const connect = (sequence: number) => session(sequence).createConnect(0);
  assert.equal(server.connectRepeats(connect(2 ** 32 - 2)), true, "just before the wrap");
  assert.equal(server.connectRepeats(connect(4)), false, "just after");
  assert.equal(server.connectRepeats(connect(3 + 4_096)), false, "far after");
  assert.equal(server.connectRepeats(connect(2 ** 32 - 4_092)), true, "the window's edge");
  assert.equal(server.connectRepeats(connect(2 ** 32 - 4_093)), false, "past the window");
});

test("a restarted client whose first sequence is stale connects after redrawing", () => {
  const server = session(500);
  const old = session(1_000);
  const oldAccept = server.acceptConnect(old.createConnect(0), 0);
  old.receive(oldAccept, 0);

  const restarted = session(900);
  const first = restarted.createConnect(0, enc("join"));
  // The old client has not acknowledged its Accept yet, so that is the reply:
  // it names the old Connect, not this one.
  const early = server.acceptConnect(first, 1);
  assert.equal(early.packetType, "accept");
  restarted.receive(early, 1);
  assert.equal(restarted.connected, false, "an Accept for another Connect connected the client");
  server.receive(old.createAckForReceived(oldAccept.sequence), 1);
  const reply = server.acceptConnect(first, 2);
  assert.equal(reply.packetType, "ack");
  restarted.receive(reply, 2);
  assert.equal(restarted.connected, false);

  const retransmitted = restarted.dueResends(1_000);
  assert.equal(retransmitted.length, 1);
  assert.equal(retransmitted[0]!.sequence, first.sequence, "the attempt is still young");

  const fresh = restarted.dueResends(3_500);
  assert.equal(fresh.length, 1);
  assert.equal(fresh[0]!.packetType, "connect");
  assert.notEqual(fresh[0]!.sequence, first.sequence, "the same Connect was retransmitted for ever");
  assert.equal(dec(fresh[0]!.payload ?? new Uint8Array()), "join", "the fresh attempt lost the Connect payload");

  const accept = server.acceptConnect(fresh[0]!, 3_500);
  assert.equal(accept.packetType, "accept");
  restarted.receive(accept, 3_500);
  assert.ok(restarted.connected);
  assert.deepEqual(names(server.receive(send(restarted, "hello"), 3_501)), ["hello"]);
});

test("an answered Connect is never replaced by the attempt timeout", () => {
  const client = session(10);
  const server = session(500);
  const { connect } = handshake(client, server);
  const resent = client.dueResends(60_000);
  assert.ok(resent.every((packet) => packet.packetType !== "connect" || packet.sequence === connect.sequence), "a connected client started a new attempt");
  assert.ok(client.connected);
});

async function bind(): Promise<Socket> {
  const socket = createSocket("udp4");
  socket.bind(0, "127.0.0.1");
  await once(socket, "listening");
  return socket;
}
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test("server mode admits a pinned client that restarts on a new port", async () => {
  const serverSocket = await bind();
  const a = await bind();
  const b = await bind();
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "srv", socket: serverSocket, mode: "server", connectionId, resendPollMs: 20, resendDelayMs: 20,
  });
  const frames: string[] = [];
  server.on("frame", (frame: { payload: Uint8Array }) => frames.push(dec(frame.payload)));
  const port = (serverSocket.address() as { port: number }).port;
  const atA: CultNetRudpPacket[] = [];
  const atB: CultNetRudpPacket[] = [];
  a.on("message", (wire) => atA.push(decodeRudpPacket(wire)));
  b.on("message", (wire) => atB.push(decodeRudpPacket(wire)));
  try {
    const first = session(1);
    a.send(encodeRudpPacket(first.createConnect(0, enc("join"))), port, "127.0.0.1");
    await sleep(100);
    const accept = atA.find((packet) => packet.packetType === "accept");
    assert.ok(accept);
    first.receive(accept, 0);
    a.send(encodeRudpPacket(first.createAckForReceived(accept.sequence)), port, "127.0.0.1");
    a.send(encodeRudpPacket(send(first, "hello")), port, "127.0.0.1");
    await sleep(100);
    assert.deepEqual(frames, ["hello"]);

    const second = session(1); // the process restarted on a new port, pinned
    b.send(encodeRudpPacket(second.createConnect(0, enc("join"))), port, "127.0.0.1");
    await sleep(100);
    const acceptB = atB.find((packet) => packet.packetType === "accept");
    assert.ok(acceptB, `the restarted client on a new port was not admitted: ${JSON.stringify(atB.map((packet) => packet.packetType))}`);
    second.receive(acceptB, 1);
    assert.ok(second.connected);
    b.send(encodeRudpPacket(send(second, "after")), port, "127.0.0.1");
    await sleep(100);
    assert.deepEqual(frames, ["hello", "after"]);
  } finally {
    server.close();
    a.close();
    b.close();
  }
});

test("an accepted peer that goes silent times out", () => {
  const server = session(500);
  server.acceptConnect(session(10).createConnect(0), 0);
  assert.equal(server.checkTimeout(100_000, 1_000), true);
});

// Ack Cut 1d, batch 2: the connect-attempt timeout.

test("an Ack naming the pending Connect does not retire it", () => {
  const server = session(500);
  const old = session(1);
  const oldAccept = server.acceptConnect(old.createConnect(0), 0);
  old.receive(oldAccept, 0);
  server.receive(old.createAckForReceived(oldAccept.sequence), 0);

  const restarted = session(1);
  const first = restarted.createConnect(0, enc("join"));
  const reply = server.acceptConnect(first, 1);
  assert.equal(reply.packetType, "ack");
  assert.ok(namesSequence(reply, first.sequence), "the Ack must name the pending Connect for this test to bite");
  restarted.receive(reply, 1);
  assert.equal(restarted.connected, false);
  assert.deepEqual(restarted.pendingReliableSequences, [first.sequence], "an Ack retired the Connect");

  const fresh = restarted.dueResends(3_000);
  assert.equal(fresh.length, 1, "the client waits for ever with nothing to resend");
  assert.equal(dec(fresh[0]!.payload ?? new Uint8Array()), "join");
  const accept = server.acceptConnect(fresh[0]!, 3_000);
  assert.equal(accept.packetType, "accept");
  restarted.receive(accept, 3_000);
  assert.ok(restarted.connected);
});

test("a late copy of an abandoned Connect is stale once its replacement is accepted", () => {
  const server = session(500);
  const client = session(10);
  const abandoned = client.createConnect(0);
  const fresh = client.dueResends(3_000);
  assert.equal(fresh.length, 1);
  assert.equal(fresh[0]!.sequence, abandoned.sequence + 4_095);
  const accept = server.acceptConnect(fresh[0]!, 3_001);
  client.receive(accept, 3_002);
  server.receive(client.createAckForReceived(accept.sequence), 3_002);
  assert.ok(client.connected);
  const a = send(client, "a");
  assert.deepEqual(names(server.receive(a, 3_003)), ["a"]);
  client.receive(server.createAckForReceived(a.sequence), 3_003);

  assert.equal(server.connectRepeats(abandoned), true, "the late copy would restart the server");
  const reply = server.acceptConnect(abandoned, 3_004);
  assert.equal(reply.packetType, "ack");
  client.receive(reply, 3_005);
  assert.ok(client.connected);
  assert.deepEqual(names(server.receive(send(client, "b"), 3_006)), ["b"]);
});

test("a server sitting at the jump is left by the next attempt", () => {
  const server = session(500);
  const old = session(5_095);
  const oldAccept = server.acceptConnect(old.createConnect(0), 0);
  old.receive(oldAccept, 0);
  server.receive(old.createAckForReceived(oldAccept.sequence), 0);

  const restarted = session(1_000);
  const first = restarted.createConnect(0, enc("join"));
  restarted.receive(server.acceptConnect(first, 1), 1);
  assert.equal(restarted.connected, false);

  const second = restarted.dueResends(3_000)[0]!;
  assert.equal(second.sequence, 5_095, "the jump lands on the server's generation");
  const reply = server.acceptConnect(second, 3_001);
  assert.equal(reply.packetType, "ack");
  restarted.receive(reply, 3_001);
  assert.equal(restarted.connected, false);

  const third = restarted.dueResends(6_000)[0]!;
  assert.equal(third.sequence, 5_095 + 4_095);
  const accept = server.acceptConnect(third, 6_001);
  assert.equal(accept.packetType, "accept");
  restarted.receive(accept, 6_001);
  assert.ok(restarted.connected);
});

test("server mode admits a pinned client that restarts on the same address", async () => {
  const serverSocket = await bind();
  const peer = await bind();
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "srv", socket: serverSocket, mode: "server", connectionId, resendPollMs: 20, resendDelayMs: 20,
  });
  const port = (serverSocket.address() as { port: number }).port;
  const received: CultNetRudpPacket[] = [];
  peer.on("message", (wire) => received.push(decodeRudpPacket(wire)));
  const take = async (type: string) => {
    for (let index = 0; index < 50; index += 1) {
      const found = received.findIndex((packet) => packet.packetType === type);
      if (found >= 0) return received.splice(found, 1)[0]!;
      await sleep(10);
    }
    throw new Error(`no ${type} arrived`);
  };
  try {
    const first = session(1);
    peer.send(encodeRudpPacket(first.createConnect(0, enc("join"))), port, "127.0.0.1");
    const accept = await take("accept");
    first.receive(accept, 0);
    peer.send(encodeRudpPacket(first.createAckForReceived(accept.sequence)), port, "127.0.0.1");
    await sleep(50);
    received.length = 0;

    const restarted = session(1);
    peer.send(encodeRudpPacket(restarted.createConnect(0, enc("join"))), port, "127.0.0.1");
    restarted.receive(await take("ack"), 1);
    assert.equal(restarted.connected, false);

    peer.send(encodeRudpPacket(restarted.dueResends(3_000)[0]!), port, "127.0.0.1");
    restarted.receive(await take("accept"), 3_001);
    assert.ok(restarted.connected, "the restarted pinned client was never admitted");
  } finally {
    server.close();
    peer.close();
  }
});
