// A datagram the document server cannot send to one peer is that peer's problem
// alone. It never escapes a timer or the receive handler, never stops the server
// serving the others, and only a datagram that can never be sent as built ends
// the session that owes it.
import assert from "node:assert/strict";
import { createSocket, type RemoteInfo, type Socket } from "node:dgram";
import test from "node:test";
import { encode } from "@msgpack/msgpack";
import {
  CultNetDocumentRegistry,
  CultNetRudpSession,
  decodeRudpPacket,
  encodeRudpPacket,
  type CultNetPeer,
} from "cultnet-ts";
import { CultMesh } from "../src/index";

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 3_000) {
      throw new Error(`Timed out waiting for ${description}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function watchUncaught(): { errors: unknown[]; stop: () => void } {
  const errors: unknown[] = [];
  const listener = (error: unknown) => errors.push(error);
  process.on("uncaughtException", listener);
  return { errors, stop: () => process.off("uncaughtException", listener) };
}

function put(recordKey: string) {
  return {
    schemaVersion: "cultnet.document_put_raw.v0" as const,
    messageId: `put-${recordKey}`,
    document: {
      schemaId: "cultmesh.note.v0",
      recordKey,
      storedAt: new Date().toISOString(),
      payloadEncoding: "messagepack" as const,
      payload: encode(recordKey),
    },
  };
}

interface Server {
  socket: Socket;
  server: ReturnType<typeof CultMesh.createRudpDocumentServer>;
  /** The record key of every put received, with the port it came from. */
  puts: Map<string, number>;
  closed: number[];
  errors: Error[];
  peer(name: string): Promise<CultNetPeer>;
}

async function startServer(connectionId: number, socket: Socket): Promise<Server> {
  const puts = new Map<string, number>();
  const closed: number[] = [];
  const errors: Error[] = [];
  const server = CultMesh.createRudpDocumentServer("send-failure-server", connectionId, {
    documents: new CultNetDocumentRegistry(),
    socket,
    bindHost: "127.0.0.1",
    bindPort: 0,
    resendDelayMs: 10,
    resendPollMs: 10,
    sessionTimeoutMs: 1_000,
    onDocumentPutRaw: (document) => {
      puts.set(document.recordKey, document.remote.port);
    },
    onSessionClosed: (session) => {
      closed.push(session.remote.port);
    },
    onError: (error) => errors.push(error),
  });
  await server.start();
  const peer = (name: string) =>
    CultMesh.createRudpPeer(name, connectionId, `rudp://127.0.0.1:${server.bind.port}`, {
      resendDelayMs: 10,
      resendPollMs: 5,
      connectTimeoutMs: 2_000,
    });
  return { socket, server, puts, closed, errors, peer };
}

test("a Connect whose Accept can never be sent starts no session, and the server keeps serving", async () => {
  const connectionId = 0x10203091;
  const uncaught = watchUncaught();
  const socket = createSocket("udp4");
  const served = await startServer(connectionId, socket);
  let peer: CultNetPeer | undefined;
  try {
    // A Connect as it arrives from UDP source port 0, which Node refuses to answer.
    const wire = Buffer.from(encodeRudpPacket(new CultNetRudpSession({ connectionId }).createConnect(Date.now())));
    const remote: RemoteInfo = { address: "127.0.0.1", family: "IPv4", port: 0, size: wire.length };
    socket.emit("message", wire, remote);
    // Past the idle timeout: a session that had started would have been closed by now.
    await sleep(1_300);

    peer = await served.peer("after-port-zero");
    peer.send(put("after"));
    await waitFor(() => served.puts.has("after"), "the real peer's put");
    assert.deepEqual(served.closed, [], "no session started for port 0");
    assert.deepEqual(served.errors.map((error) => error.message), []);
    assert.deepEqual(uncaught.errors, []);
  } finally {
    uncaught.stop();
    peer?.close();
    served.server.close();
  }
});

test("a datagram that may yet pass is reported; one that never can ends only that peer's session", async () => {
  const connectionId = 0x10203092;
  const socket = createSocket("udp4");
  // Sends to a failing port report the failure the way Node reports a system one:
  // later, through the send's callback.
  const failing = new Map<number, string>();
  const send = socket.send.bind(socket) as (...args: unknown[]) => void;
  Object.assign(socket, {
    send: (message: Uint8Array, port: number, address: string, callback?: (error: Error | null) => void) => {
      const code = failing.get(port);
      if (code === undefined) {
        send(message, port, address, callback);
        return;
      }
      process.nextTick(() => callback?.(Object.assign(new Error(`send failed: ${code}`), { code })));
    },
  });
  const served = await startServer(connectionId, socket);
  let x: CultNetPeer | undefined;
  let y: CultNetPeer | undefined;
  try {
    x = await served.peer("x");
    y = await served.peer("y");
    x.send(put("x-1"));
    await waitFor(() => served.puts.has("x-1"), "X's first put");
    const xPort = served.puts.get("x-1")!;

    // No route: X's acknowledgements are lost datagrams, reported, and end nothing.
    failing.set(xPort, "EHOSTUNREACH");
    x.send(put("x-2"));
    await waitFor(() => served.errors.some((error) => (error as NodeJS.ErrnoException).code === "EHOSTUNREACH"), "the reported loss");
    y.send(put("y-1"));
    await waitFor(() => served.puts.has("y-1"), "Y's put");
    assert.deepEqual(served.closed, [], "a loss that may pass ends no session");

    // X's retransmitted put is acknowledged again, and that datagram can never be sent.
    failing.set(xPort, "EMSGSIZE");
    await waitFor(() => served.closed.length > 0, "X's session to end");
    assert.deepEqual(served.closed, [xPort]);
    assert.equal(
      served.errors.some((error) => (error as NodeJS.ErrnoException).code === "EMSGSIZE"),
      false,
      "a datagram that can never be sent ends the session instead of being reported",
    );

    y.send(put("y-2"));
    await waitFor(() => served.puts.has("y-2"), "Y's put after X ended");
    assert.deepEqual(served.closed, [xPort]);
  } finally {
    x?.close();
    y?.close();
    served.server.close();
  }
});

test("a packet the session refuses ends only that peer's session, and the peer is told", async () => {
  const connectionId = 0x10203093;
  const socket = createSocket("udp4");
  const served = await startServer(connectionId, socket);
  const raw = createSocket("udp4");
  await new Promise<void>((resolve) => raw.bind(0, "127.0.0.1", resolve));
  const arrived: ReturnType<typeof decodeRudpPacket>[] = [];
  raw.on("message", (wire) => arrived.push(decodeRudpPacket(wire)));
  let y: CultNetPeer | undefined;
  try {
    const session = new CultNetRudpSession({ connectionId });
    raw.send(encodeRudpPacket(session.createConnect(Date.now())), served.server.bind.port, "127.0.0.1");
    await waitFor(() => arrived.some((packet) => packet.packetType === "accept"), "the Accept");
    session.receive(arrived.find((packet) => packet.packetType === "accept")!, Date.now());
    y = await served.peer("y");

    // A fragment whose metadata the session refuses: fragment id 0 names no set.
    const [fragment] = session.sendMany("schema", new Uint8Array([1]), { reliable: false });
    raw.send(
      encodeRudpPacket({ ...fragment!, fragmentId: 0, fragmentIndex: 0, fragmentCount: 2 }),
      served.server.bind.port,
      "127.0.0.1",
    );
    await waitFor(() => arrived.some((packet) => packet.packetType === "disconnect"), "the goodbye");
    const goodbye = arrived.find((packet) => packet.packetType === "disconnect")!;
    assert.equal(Buffer.from(goodbye.payload ?? []).toString("utf8"), "session refused a packet");
    assert.deepEqual(served.closed, [(raw.address() as { port: number }).port]);

    y.send(put("y-after"));
    await waitFor(() => served.puts.has("y-after"), "Y's put");
    assert.deepEqual(served.errors.map((error) => error.message), []);
  } finally {
    y?.close();
    raw.close();
    served.server.close();
  }
});
