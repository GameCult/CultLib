import { createSocket, type RemoteInfo, type Socket } from "node:dgram";
import { decode, encode } from "@msgpack/msgpack";

import {
  encodeCultNetMessageForWire,
  parseCultNetMessage,
  type CultNetOperationRequestMessage,
  type CultNetOperationResponseMessage,
} from "./contracts";
import {
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
  decodeRudpPacket,
  encodeRudpPacket,
  isPermanentSendError,
  sendRudpDatagram,
  rudpClientBindHost,
  type CultNetRudpPacket,
} from "./rudp";
import { CultNetPeer } from "./peer";

const DEFAULT_CONNECTION_ID = 0x43554c54;
/** Matches the document server's default session cap. A Connect past it is dropped. */
const MAX_OPERATION_SESSIONS = 64;
/** Matches the document server's default `session_idle_timeout` (30 s). */
const DEFAULT_SESSION_IDLE_TIMEOUT_MS = 30_000;

export interface CultNetOperationServerOptions {
  runtimeId: string;
  host?: string;
  port?: number;
  connectionId?: number;
  maxFragmentBytes?: number;
  /** A session that has sent nothing for this long is dropped, freeing its slot. Default 30 000. */
  sessionIdleTimeoutMs?: number;
  handler: (request: CultNetOperationRequestMessage) =>
    CultNetOperationResponseMessage | Promise<CultNetOperationResponseMessage>;
}

export interface CultNetOperationServer {
  readonly endpoint: string;
  /** Datagrams read and discarded: malformed, unadmitted, refused by their session, or failed in handling. */
  readonly packetsDropped: number;
  /**
   * Datagrams that could not be sent to a peer and may yet be (no route, full buffers):
   * each is that peer's lost datagram, resent if it was reliable. One that can never be
   * sent as built ends that peer's session instead and is not counted here.
   */
  readonly sendFailures: number;
  close(): Promise<void>;
}

export interface CultNetOperationClientOptions {
  runtimeId: string;
  connectionId?: number;
  timeoutMs?: number;
  maxFragmentBytes?: number;
}

interface RemoteSession {
  session: CultNetRudpSession;
  remote: RemoteInfo;
  /** Handlers running for this session; a session serving a request is not idle. */
  handling: number;
  /** When the last handler finished: the response still needs its acknowledgement. */
  lastHandledAtMs: number;
  /** A packet it owed could never be sent, so the session ended. */
  unsendable: boolean;
}

type SendPacket = (key: string, peer: RemoteSession, packet: CultNetRudpPacket) => void;

export async function startCultNetOperationServer(
  options: CultNetOperationServerOptions,
): Promise<CultNetOperationServer> {
  if (!options.runtimeId) throw new Error("CultNet operation server requires runtimeId.");
  if (!options.handler) throw new Error("CultNet operation server requires a handler.");
  const socket = createSocket("udp4");
  const connectionId = options.connectionId ?? DEFAULT_CONNECTION_ID;
  const sessions = new Map<string, RemoteSession>();
  let packetsDropped = 0;
  let sendFailures = 0;
  const sendPacket: SendPacket = (key, peer, packet) => {
    sendRudpDatagram(socket, encodeRudpPacket(packet), peer.remote.port, peer.remote.address, (error) => {
      if (!isPermanentSendError(error)) {
        sendFailures += 1;
        return;
      }
      if (peer.unsendable) return;
      // The datagram can never be sent as built: the session that owes it ends, never
      // the server, and the peer is told.
      peer.unsendable = true;
      if (sessions.get(key) === peer) sessions.delete(key);
      const goodbye = encodeRudpPacket(peer.session.endUnsendableSession(error));
      sendRudpDatagram(socket, goodbye, peer.remote.port, peer.remote.address, () => {});
    });
  };
  socket.on("message", (wire, remote) => {
    // What a datagram carries is the sender's business: a rejection here must
    // drop and count the packet, never become an unhandled rejection that ends
    // the process.
    handleServerDatagram(sessions, connectionId, options, wire, remote, sendPacket).then(
      admitted => { if (!admitted) packetsDropped += 1; },
      () => { packetsDropped += 1; },
    );
  });
  await bindSocket(socket, options.port ?? 0, options.host ?? "127.0.0.1");
  const idleTimeoutMs = options.sessionIdleTimeoutMs ?? DEFAULT_SESSION_IDLE_TIMEOUT_MS;
  const resendTimer = setInterval(() => {
    const now = Date.now();
    for (const [key, peer] of sessions) {
      // An abandoned client must not hold a slot forever, or the cap locks
      // every new client out.
      if (peer.handling > 0 || now - peer.lastHandledAtMs < idleTimeoutMs) continue;
      if (peer.session.checkTimeout(now, idleTimeoutMs)) sessions.delete(key);
    }
    for (const [key, peer] of sessions) {
      for (const packet of peer.session.dueResends(Date.now())) sendPacket(key, peer, packet);
    }
  }, 25);
  resendTimer.unref?.();
  const address = socket.address();
  const endpoint = `rudp://${address.address}:${address.port}`;
  return {
    endpoint,
    get packetsDropped() { return packetsDropped; },
    get sendFailures() { return sendFailures; },
    close: async () => {
      clearInterval(resendTimer);
      await closeSocket(socket);
    },
  };
}

export async function invokeCultNetOperation(
  endpoint: string,
  request: CultNetOperationRequestMessage,
  options: CultNetOperationClientOptions,
): Promise<CultNetOperationResponseMessage> {
  const target = parseRudpEndpoint(endpoint);
  const bindHost = await rudpClientBindHost(target.host);
  const socket = createSocket(bindHost.includes(":") ? "udp6" : "udp4");
  await bindSocket(socket, 0, bindHost);
  const transport = new CultNetRudpSocketTransportConnection({
    mode: "client",
    runtimeId: options.runtimeId,
    transportId: `${options.runtimeId}.operations`,
    socket,
    remoteHost: target.host,
    remotePort: target.port,
    connectionId: options.connectionId ?? DEFAULT_CONNECTION_ID,
    maxFragmentBytes: options.maxFragmentBytes ?? 2048,
  });
  const peer = new CultNetPeer(transport, { wireContract: "cultnet.schema.v0" });
  const timeoutMs = options.timeoutMs ?? 10_000;
  try {
    transport.connect();
    await waitUntil(() => transport.connected, timeoutMs, "CultNet operation connection timed out.");
    return await new Promise<CultNetOperationResponseMessage>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("CultNet operation response timed out.")), timeoutMs);
      const onMessage = (message: unknown): void => {
        const candidate = message as CultNetOperationResponseMessage;
        if (candidate.schemaVersion !== "cultnet.operation_response.v0" || candidate.messageId !== request.messageId) return;
        clearTimeout(timer);
        peer.off("message", onMessage);
        resolve(candidate);
      };
      peer.on("message", onMessage);
      peer.send(request);
    });
  } finally {
    transport.close();
  }
}

/** Resolves false when the datagram was dropped without an error. */
async function handleServerDatagram(
  sessions: Map<string, RemoteSession>,
  connectionId: number,
  options: CultNetOperationServerOptions,
  wire: Buffer,
  remote: RemoteInfo,
  sendPacket: SendPacket,
): Promise<boolean> {
  let packet: CultNetRudpPacket;
  try {
    packet = decodeRudpPacket(wire);
  } catch {
    return false;
  }
  if (packet.connectionId !== connectionId) return false;
  const key = `${remote.address}:${remote.port}`;
  let peer = sessions.get(key);
  if (packet.packetType === "connect") {
    if (peer) {
      // A Connect from an admitted peer repeats: the session answers with the
      // Accept already owed and queues nothing.
      sendPacket(key, peer, peer.session.acceptConnect(packet, Date.now(), encode("cultnet-operation-service")));
      return true;
    }
    if (sessions.size >= MAX_OPERATION_SESSIONS) return false;
    const candidate: RemoteSession = {
      session: new CultNetRudpSession({ connectionId, resendDelayMs: 25 }),
      remote,
      handling: 0,
      lastHandledAtMs: 0,
      unsendable: false,
    };
    sendPacket(key, candidate, candidate.session.acceptConnect(packet, Date.now(), encode("cultnet-operation-service")));
    // A peer whose Accept can never be sent cannot be answered, so no session starts.
    if (candidate.unsendable) return false;
    sessions.set(key, candidate);
    return true;
  }
  if (!peer) return false;
  let result: ReturnType<CultNetRudpSession["receive"]>;
  try {
    result = peer.session.receive(packet, Date.now());
  } catch {
    endSession(sessions, key, peer, sendPacket);
    return false;
  }
  if (result.reply) sendPacket(key, peer, result.reply);
  for (const ready of result.readyToSend ?? []) sendPacket(key, peer, ready);
  if (result.disconnected) {
    sessions.delete(key);
    return true;
  }
  // A request belongs to the session generation it arrived on. If that generation ends while a
  // handler runs (a goodbye, a refusal, a timeout, a new Connect from the same endpoint), what
  // the handler returns or throws belongs to no live session, and nothing of it may reach the
  // endpoint: a client that connected again there owns it now.
  const generation = peer.session.generation;
  for (const frame of result.delivered) {
    if (frame.channelId !== "schema") continue;
    try {
      const message = parseCultNetMessage(decode(frame.payload));
      if (message.schemaVersion !== "cultnet.operation_request.v0") continue;
      peer.handling += 1;
      let response: CultNetOperationResponseMessage;
      try {
        response = await options.handler(message);
      } finally {
        peer.handling -= 1;
        peer.lastHandledAtMs = Date.now();
      }
      if (peer.session.generation !== generation) return true;
      const payload = encode(encodeCultNetMessageForWire(response, "cultnet.schema.v0"));
      for (const responsePacket of peer.session.sendMany("schema", payload, {
        reliable: true,
        ordered: true,
        nowMs: Date.now(),
        maxFragmentBytes: options.maxFragmentBytes ?? 2048,
      })) sendPacket(key, peer, responsePacket);
    } catch {
      if (peer.session.generation !== generation) return true;
      // The session recorded this request's sequence, so keeping it would
      // acknowledge the retransmit of a request that was never handled.
      endSession(sessions, key, peer, sendPacket);
      return false;
    }
  }
  if (packet.packetType === "data" || result.delivered.length > 0) {
    sendPacket(key, peer, peer.session.createAckForReceived(packet.sequence));
  }
  return true;
}

/**
 * Ends a session that took a packet it could not serve, and tells the peer. The
 * reset comes first, or the goodbye's ack field would acknowledge the very
 * packet that was refused. A session already replaced under the same key is left.
 */
function endSession(
  sessions: Map<string, RemoteSession>,
  key: string,
  peer: RemoteSession,
  sendPacket: SendPacket,
): void {
  if (sessions.get(key) === peer) sessions.delete(key);
  peer.session.resetPeerState();
  sendPacket(key, peer, peer.session.createDisconnect(Buffer.from("session refused a packet", "utf8")));
}

function parseRudpEndpoint(endpoint: string): { host: string; port: number } {
  const url = new URL(endpoint);
  if (url.protocol !== "rudp:") throw new Error(`CultNet operation endpoint must use rudp://: ${endpoint}`);
  const port = Number(url.port);
  if (!url.hostname || !Number.isInteger(port) || port <= 0) throw new Error(`Invalid CultNet operation endpoint: ${endpoint}`);
  return { host: url.hostname, port };
}

function bindSocket(socket: Socket, port: number, host: string): Promise<void> {
  return new Promise((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(port, host, () => {
      socket.off("error", reject);
      resolve();
    });
  });
}

function closeSocket(socket: Socket): Promise<void> {
  return new Promise(resolve => {
    if (!socket.address()) return resolve();
    socket.close(() => resolve());
  });
}

async function waitUntil(check: () => boolean, timeoutMs: number, message: string): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!check()) {
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}
