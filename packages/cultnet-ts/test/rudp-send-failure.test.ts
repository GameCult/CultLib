// A datagram that cannot be sent to one peer is that peer's problem alone. Node throws
// for a send it refuses outright (a peer at UDP port 0, which any spoofer can claim)
// and reports a system failure later through the callback; neither may escape a
// timer or a receive handler, and only a datagram that can never be sent as built
// ends the session that owes it.
import assert from "node:assert/strict";
import dgram, { type RemoteInfo, type Socket } from "node:dgram";
import test from "node:test";
import {
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
  encodeRudpPacket,
  invokeCultNetOperation,
  isPermanentSendError,
  startCultNetOperationServer,
  type CultNetOperationResponseMessage,
} from "../src";

async function bindUdpSocket(): Promise<Socket> {
  const socket = dgram.createSocket("udp4");
  await new Promise<void>((resolve) => socket.bind(0, "127.0.0.1", resolve));
  return socket;
}

function udpPort(socket: Socket): number {
  const address = socket.address();
  assert.notEqual(typeof address, "string");
  return address.port;
}

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 2_000) {
      throw new Error(`Timed out waiting for ${description}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** A Connect as it arrives from UDP source port 0: Node refuses to send anything back. */
function connectFromPortZero(socket: Socket, connectionId: number): void {
  const wire = Buffer.from(encodeRudpPacket(new CultNetRudpSession({ connectionId }).createConnect(Date.now())));
  const remote: RemoteInfo = { address: "127.0.0.1", family: "IPv4", port: 0, size: wire.length };
  socket.emit("message", wire, remote);
}

/** Records every exception that would otherwise end the process. */
function watchUncaught(): { errors: unknown[]; stop: () => void } {
  const errors: unknown[] = [];
  const listener = (error: unknown) => errors.push(error);
  process.on("uncaughtException", listener);
  return { errors, stop: () => process.off("uncaughtException", listener) };
}

test("only a datagram that can never be sent as built is a permanent send failure", () => {
  const failure = (code: string) => Object.assign(new Error("send failed"), { code });
  for (const code of ["EMSGSIZE", "EINVAL", "EAFNOSUPPORT", "ERR_SOCKET_BAD_PORT"]) {
    assert.equal(isPermanentSendError(failure(code)), true, code);
  }
  for (const code of ["EHOSTUNREACH", "ENETUNREACH", "ECONNREFUSED", "ENOBUFS", "EAGAIN", "ERR_SOCKET_DGRAM_NOT_RUNNING"]) {
    assert.equal(isPermanentSendError(failure(code)), false, code);
  }
  assert.equal(isPermanentSendError(new Error("no code")), false);
});

test("a server transport answering a Connect from port 0 ends that session and keeps serving", async () => {
  const serverSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const connectionId = 0x10203090;
  const uncaught = watchUncaught();
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendDelayMs: 10,
    resendPollMs: 5,
  });
  const errors: Error[] = [];
  const reasons: string[] = [];
  server.on("error", (error: Error) => errors.push(error));
  server.on("disconnect", ({ reason }: { reason: Uint8Array }) => reasons.push(Buffer.from(reason).toString("utf8")));
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(serverSocket),
    connectionId,
    resendDelayMs: 10,
    resendPollMs: 5,
  });
  try {
    connectFromPortZero(serverSocket, connectionId);
    // Many resend ticks: nothing the port-0 session owed is ever retried.
    await sleep(100);
    // The goodbye names the error by its code, never by a message quoting the address.
    assert.deepEqual(reasons, ["packet could not be sent: ERR_SOCKET_BAD_PORT"]);

    const frames: string[] = [];
    server.on("frame", (frame: { payload: Uint8Array }) => frames.push(Buffer.from(frame.payload).toString("utf8")));
    client.connect();
    await waitFor(() => client.connected && server.connected, "a real client's handshake");
    client.send("schema", Buffer.from("after", "utf8"));
    await waitFor(() => frames.length === 1, "the real client's frame");
    assert.deepEqual(frames, ["after"]);
    assert.deepEqual(errors, []);
    assert.deepEqual(uncaught.errors, []);
  } finally {
    uncaught.stop();
    client.close();
    server.close();
  }
});

test("an operation server answering a Connect from port 0 starts no session and keeps serving", async () => {
  const connectionId = 0x43554c54;
  const uncaught = watchUncaught();
  // The server owns its socket; this captures it so a datagram can arrive from port 0.
  const createSocket = dgram.createSocket;
  let serverSocket: Socket | undefined;
  Object.assign(dgram, {
    createSocket: (...args: Parameters<typeof dgram.createSocket>) => {
      serverSocket = createSocket(...args);
      return serverSocket;
    },
  });
  const server = await startCultNetOperationServer({
    runtimeId: "operations",
    handler: (request): CultNetOperationResponseMessage => ({
      schemaVersion: "cultnet.operation_response.v0",
      messageId: request.messageId,
      serviceId: request.serviceId,
      operation: request.operation,
      status: "ok",
      payloadSchema: request.payloadSchema,
      payloadEncoding: request.payloadEncoding,
      payload: request.payload,
      diagnostics: [],
      sourceRuntimeId: "operations",
    }),
  }).finally(() => Object.assign(dgram, { createSocket }));
  const raw = await bindUdpSocket();
  const port = Number(new URL(server.endpoint).port);
  const accepts: number[] = [];
  raw.on("message", () => accepts.push(Date.now()));
  try {
    connectFromPortZero(serverSocket!, connectionId);
    // The resend timer runs every 25 ms; a session that had started would owe its Accept.
    await sleep(150);
    assert.equal(server.packetsDropped, 1, "the Connect started no session");
    assert.equal(server.sendFailures, 0);

    // A real peer is still answered.
    raw.send(encodeRudpPacket(new CultNetRudpSession({ connectionId }).createConnect(0)), port, "127.0.0.1");
    await waitFor(() => accepts.length > 0, "the real peer's Accept");
    assert.deepEqual(uncaught.errors, []);
  } finally {
    uncaught.stop();
    raw.close();
    await server.close();
  }
});

test("a server transport whose resend can never be sent ends the session and sends nothing more of it", async () => {
  const serverSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const connectionId = 0x10203094;
  const uncaught = watchUncaught();
  const clientPort = udpPort(clientSocket);
  // Sends to the client fail as the test says: later through the callback, or at once.
  let mode: "healthy" | "lost" | "never" = "healthy";
  const send = serverSocket.send.bind(serverSocket) as (...args: unknown[]) => void;
  Object.assign(serverSocket, {
    send: (message: Uint8Array, port: number, address: string, callback?: (error: Error | null) => void) => {
      if (port !== clientPort || mode === "healthy") {
        send(message, port, address, callback);
      } else if (mode === "lost") {
        process.nextTick(() => callback?.(Object.assign(new Error("lost"), { code: "EHOSTUNREACH" })));
      } else {
        throw Object.assign(new Error("never"), { code: "EMSGSIZE" });
      }
    },
  });
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendDelayMs: 10,
    resendPollMs: 5,
  });
  const errors: string[] = [];
  const reasons: string[] = [];
  server.on("error", (error: NodeJS.ErrnoException) => errors.push(error.code ?? ""));
  server.on("disconnect", ({ reason }: { reason: Uint8Array }) => reasons.push(Buffer.from(reason).toString("utf8")));
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(serverSocket),
    connectionId,
    resendDelayMs: 10,
    resendPollMs: 5,
  });
  try {
    client.connect();
    await waitFor(() => client.connected && server.connected, "the handshake");
    mode = "lost";
    server.send("schema", Buffer.from("one", "utf8"));
    server.send("schema", Buffer.from("two", "utf8"));
    await waitFor(() => server.stats.sendFailures >= 4, "both writes lost, and lost again when resent");
    assert.deepEqual(errors, [], "a loss that may pass is counted, never emitted");
    assert.deepEqual(reasons, []);

    // Both writes are due again together; the first can never be sent.
    mode = "never";
    await waitFor(() => reasons.length > 0, "the session to end");
    await sleep(50);
    assert.deepEqual(reasons, ["packet could not be sent: EMSGSIZE"]);
    assert.equal(server.connected, false);
    assert.deepEqual(uncaught.errors, []);
  } finally {
    uncaught.stop();
    client.close();
    server.close();
  }
});

// The kernel refuses a datagram to the limited broadcast address from a socket that has
// not asked for broadcast (EACCES): a real failure that may pass once the socket or the
// route changes, so it is a lost datagram. Node reports it only to the send's callback,
// never on the socket's "error" event, so nothing may turn it into an "error" either.
const UNREACHABLE = { host: "255.255.255.255", port: 9 };

test("a client transport with no error listener counts a send the network refuses and does not end the process", async () => {
  const socket = await new Promise<Socket>((resolve) => {
    const created = dgram.createSocket("udp4");
    created.bind(0, "0.0.0.0", () => resolve(created));
  });
  const uncaught = watchUncaught();
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-client",
    socket,
    mode: "client",
    remoteHost: UNREACHABLE.host,
    remotePort: UNREACHABLE.port,
    connectionId: 0x10203095,
    resendDelayMs: 10,
    resendPollMs: 5,
  });
  try {
    client.connect();
    // The Connect and its resends, each refused by the kernel.
    await waitFor(() => client.stats.sendFailures >= 5, "the refused Connect and its resends");
    assert.equal(client.connected, false);
    assert.deepEqual(uncaught.errors, []);
  } finally {
    uncaught.stop();
    client.close();
  }
});

test("an operation call the network refuses rejects with a timeout and does not end the process", async () => {
  const uncaught = watchUncaught();
  try {
    await assert.rejects(
      invokeCultNetOperation(
        `rudp://${UNREACHABLE.host}:${UNREACHABLE.port}`,
        {
          schemaVersion: "cultnet.operation_request.v0",
          messageId: "refused",
          serviceId: "service",
          operation: "operation",
          payloadSchema: "payload",
          payloadEncoding: "messagepack",
          payload: new Uint8Array(),
          diagnostics: [],
          sourceRuntimeId: "caller",
        },
        { runtimeId: "caller", timeoutMs: 400 },
      ),
      /CultNet operation connection timed out/,
    );
    assert.deepEqual(uncaught.errors, []);
  } finally {
    uncaught.stop();
  }
});
