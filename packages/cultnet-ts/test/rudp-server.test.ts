// One server socket serves many peers, each with its own reliable session.
import assert from "node:assert/strict";
import test from "node:test";
import dgram, { type Socket } from "node:dgram";
import {
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
  CultNetRudpSocketTransportServer,
  decodeRudpPacket,
  encodeRudpPacket,
  type CultNetRudpSocketServerPeer,
} from "../src";

const connectionId = 0x10203091;
const enc = (text: string) => new TextEncoder().encode(text);
const dec = (bytes: Uint8Array) => new TextDecoder().decode(bytes);
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function bind(): Promise<Socket> {
  const socket = dgram.createSocket("udp4");
  await new Promise<void>((resolve) => socket.bind(0, "127.0.0.1", resolve));
  return socket;
}

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 3_000) throw new Error(`Timed out waiting for ${description}.`);
    await sleep(5);
  }
}

type Client = { connection: CultNetRudpSocketTransportConnection; socket: Socket; received: string[]; closed: () => boolean };

async function startServer(extra: Record<string, unknown> = {}) {
  const socket = await bind();
  const server = new CultNetRudpSocketTransportServer({
    runtimeId: "many-peer-server", socket, connectionId, resendDelayMs: 10, resendPollMs: 5, ...extra,
  });
  const requests: { peer: CultNetRudpSocketServerPeer; text: string }[] = [];
  const connected: CultNetRudpSocketServerPeer[] = [];
  const left: { peer: CultNetRudpSocketServerPeer; reason: string }[] = [];
  server.on("peerConnected", (peer: CultNetRudpSocketServerPeer) => connected.push(peer));
  server.on("peerDisconnected", ({ peer, reason }: { peer: CultNetRudpSocketServerPeer; reason: Uint8Array }) => left.push({ peer, reason: dec(reason) }));
  // Every request is answered on the peer it arrived from.
  server.on("frame", ({ peer, payload }: { peer: CultNetRudpSocketServerPeer; payload: Uint8Array }) => {
    requests.push({ peer, text: dec(payload) });
    server.send(peer, "schema", enc(`re:${dec(payload)}`));
  });
  const port = (socket.address() as { port: number }).port;
  return { server, port, requests, connected, left };
}

async function startClient(port: number, connectPayload = new Uint8Array()): Promise<Client> {
  const socket = await bind();
  const connection = new CultNetRudpSocketTransportConnection({
    runtimeId: "client", socket, mode: "client", remoteHost: "127.0.0.1", remotePort: port,
    connectionId, resendDelayMs: 10, resendPollMs: 5,
  });
  const received: string[] = [];
  let closed = false;
  connection.on("frame", ({ payload }: { payload: Uint8Array }) => received.push(dec(payload)));
  connection.on("close", () => { closed = true; });
  connection.connect(connectPayload);
  await waitFor(() => connection.connected, "client handshake");
  return { connection, socket, received, closed: () => closed };
}

/** A bare session on a socket, so a goodbye can be sent and the socket released only after it left. */
async function rawClient(port: number) {
  const socket = await bind();
  const session = new CultNetRudpSession({ connectionId });
  const transmit = (packet: ReturnType<CultNetRudpSession["createConnect"]>) =>
    new Promise<void>((resolve) => socket.send(encodeRudpPacket(packet), port, "127.0.0.1", () => resolve()));
  socket.on("message", (wire) => { session.receive(decodeRudpPacket(wire), Date.now()); });
  await transmit(session.createConnect(Date.now()));
  await waitFor(() => session.connected, "raw client handshake");
  return { async leave() { await transmit(session.createDisconnect()); socket.close(); } };
}

test("two clients on different ports talk through one server socket and neither resets the other", async () => {
  const { server, port, requests } = await startServer();
  const a = await startClient(port, enc("alpha"));
  const b = await startClient(port, enc("beta"));
  try {
    assert.equal(server.peers.length, 2);
    assert.deepEqual(server.peers.map((peer) => dec(peer.connectPayload)).sort(), ["alpha", "beta"]);
    for (let round = 0; round < 5; round += 1) {
      a.connection.send("schema", enc(`a${round}`));
      b.connection.send("schema", enc(`b${round}`));
    }
    await waitFor(() => a.received.length === 5 && b.received.length === 5, "both clients' replies");
    assert.deepEqual(a.received, [0, 1, 2, 3, 4].map((n) => `re:a${n}`));
    assert.deepEqual(b.received, [0, 1, 2, 3, 4].map((n) => `re:b${n}`));
    assert.equal(requests.length, 10);
    assert.equal(a.connection.connected && b.connection.connected, true);
    assert.equal(a.closed() || b.closed(), false);
    assert.equal(server.peers.length, 2);
  } finally {
    a.connection.close(); b.connection.close(); await server.close();
  }
});

test("a client reconnecting from the same endpoint replaces only its own session", async () => {
  const { server, port, connected, left } = await startServer();
  const a = await startClient(port);
  const b = await startClient(port);
  try {
    const oldPeer = connected[0]!;
    a.connection.connect();
    await waitFor(() => connected.length === 3, "the reconnect to be accepted");
    assert.equal(left.length, 1);
    assert.equal(left[0]!.peer, oldPeer);
    assert.equal(left[0]!.reason, "replaced by a new Connect generation");
    assert.throws(() => server.send(oldPeer, "schema", enc("stale")), /not connected/);
    assert.equal(server.peers.length, 2);

    a.connection.send("schema", enc("after"));
    b.connection.send("schema", enc("still"));
    await waitFor(() => a.received.includes("re:after") && b.received.includes("re:still"), "replies after the reconnect");
    assert.equal(b.closed(), false);
  } finally {
    a.connection.close(); b.connection.close(); await server.close();
  }
});

test("a peer that leaves, or goes silent, is dropped and the others keep their sessions", async () => {
  const { server, port, left } = await startServer({ peerTimeoutMs: 150 });
  const leaver = await rawClient(port);
  const silent = await startClient(port);
  const steady = await startClient(port);
  try {
    await leaver.leave();
    await waitFor(() => server.peers.length === 2, "the leaver to be dropped");
    assert.deepEqual(left.map((entry) => entry.reason), [""]);

    // Only the steady peer speaks; the silent one is reaped and told so.
    const keepAlive = setInterval(() => steady.connection.ping(), 20);
    try {
      await waitFor(() => server.peers.length === 1, "the silent peer to be reaped");
    } finally {
      clearInterval(keepAlive);
    }
    assert.ok(left.some((entry) => entry.reason === "session timed out"));
    await waitFor(() => silent.closed(), "the reaped client to hear the goodbye");
    assert.equal(steady.closed(), false);
    steady.connection.send("schema", enc("alive"));
    await waitFor(() => steady.received.includes("re:alive"), "the surviving peer's reply");
  } finally {
    silent.connection.close(); steady.connection.close(); await server.close();
  }
});

test("Connects beyond maxPeers and datagrams from unadmitted endpoints are dropped", async () => {
  const { server, port } = await startServer({ maxPeers: 1 });
  const first = await startClient(port);
  const stranger = await bind();
  try {
    const wire = Buffer.from(encodeRudpPacket(new CultNetRudpSession({ connectionId }).createConnect(Date.now())));
    stranger.send(wire, port, "127.0.0.1");
    const data = new CultNetRudpSession({ connectionId, initialSequence: 9 });
    data.createConnect(0);
    stranger.send(Buffer.from(encodeRudpPacket(data.createPing())), port, "127.0.0.1");
    await waitFor(() => server.stats.packetsDropped >= 1, "the extra Connect to be dropped");
    await sleep(30);
    assert.equal(server.peers.length, 1);
    first.connection.send("schema", enc("ok"));
    await waitFor(() => first.received.includes("re:ok"), "the admitted peer's reply");
  } finally {
    stranger.close(); first.connection.close(); await server.close();
  }
});

test("close() delivers a Disconnect to every peer before the socket is released", async () => {
  const { server, port } = await startServer();
  const peers = await Promise.all([0, 1, 2].map(async () => {
    const socket = await bind();
    const session = new CultNetRudpSession({ connectionId });
    const types: string[] = [];
    socket.on("message", (wire) => {
      const packet = decodeRudpPacket(wire);
      types.push(packet.packetType);
      session.receive(packet, Date.now());
    });
    socket.send(encodeRudpPacket(session.createConnect(Date.now())), port, "127.0.0.1");
    await waitFor(() => session.connected, "raw handshake");
    return { socket, types };
  }));
  try {
    assert.equal(server.peers.length, 3);
    await server.close();
    await waitFor(() => peers.every((peer) => peer.types.includes("disconnect")), "every peer to observe the Disconnect");
  } finally {
    peers.forEach((peer) => peer.socket.close());
  }
});
