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
  type CultNetRudpPacket,
} from "./rudp";
import { CultNetPeer } from "./peer";

const DEFAULT_CONNECTION_ID = 0x43554c54;
/** Matches the document server's default session cap. A Connect past it is dropped. */
const MAX_OPERATION_SESSIONS = 64;

export interface CultNetOperationServerOptions {
  runtimeId: string;
  host?: string;
  port?: number;
  connectionId?: number;
  maxFragmentBytes?: number;
  handler: (request: CultNetOperationRequestMessage) =>
    CultNetOperationResponseMessage | Promise<CultNetOperationResponseMessage>;
}

export interface CultNetOperationServer {
  readonly endpoint: string;
  /** Datagrams read and discarded: malformed, unadmitted, refused by their session, or failed in handling. */
  readonly packetsDropped: number;
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
}

export async function startCultNetOperationServer(
  options: CultNetOperationServerOptions,
): Promise<CultNetOperationServer> {
  if (!options.runtimeId) throw new Error("CultNet operation server requires runtimeId.");
  if (!options.handler) throw new Error("CultNet operation server requires a handler.");
  const socket = createSocket("udp4");
  const connectionId = options.connectionId ?? DEFAULT_CONNECTION_ID;
  const sessions = new Map<string, RemoteSession>();
  const sendPacket = (remote: RemoteInfo, packet: CultNetRudpPacket): void => {
    const wire = encodeRudpPacket(packet);
    socket.send(wire, remote.port, remote.address);
  };
  let packetsDropped = 0;
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
  const resendTimer = setInterval(() => {
    for (const peer of sessions.values()) {
      for (const packet of peer.session.dueResends(Date.now())) sendPacket(peer.remote, packet);
    }
  }, 25);
  resendTimer.unref?.();
  const address = socket.address();
  const endpoint = `rudp://${address.address}:${address.port}`;
  return {
    endpoint,
    get packetsDropped() { return packetsDropped; },
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
  const socket = createSocket("udp4");
  await bindSocket(socket, 0, "127.0.0.1");
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
  sendPacket: (remote: RemoteInfo, packet: CultNetRudpPacket) => void,
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
    if (!peer && sessions.size >= MAX_OPERATION_SESSIONS) return false;
    peer = { session: new CultNetRudpSession({ connectionId, resendDelayMs: 25 }), remote };
    sessions.set(key, peer);
    sendPacket(remote, peer.session.acceptConnect(packet, Date.now(), encode("cultnet-operation-service")));
    return true;
  }
  if (!peer) return false;
  let result: ReturnType<CultNetRudpSession["receive"]>;
  try {
    result = peer.session.receive(packet, Date.now());
  } catch {
    // receive() has already recorded the packet's reliable sequence, so the
    // session cannot be kept: end it and tell the peer. The reset comes first,
    // or the goodbye's ack field would acknowledge the refused frame.
    sessions.delete(key);
    peer.session.resetPeerState();
    sendPacket(remote, peer.session.createDisconnect(Buffer.from("session refused a packet", "utf8")));
    return false;
  }
  if (result.reply) sendPacket(remote, result.reply);
  for (const ready of result.readyToSend ?? []) sendPacket(remote, ready);
  if (result.disconnected) {
    sessions.delete(key);
    return true;
  }
  for (const frame of result.delivered) {
    if (frame.channelId !== "schema") continue;
    const message = parseCultNetMessage(decode(frame.payload));
    if (message.schemaVersion !== "cultnet.operation_request.v0") continue;
    const response = await options.handler(message);
    const payload = encode(encodeCultNetMessageForWire(response, "cultnet.schema.v0"));
    for (const responsePacket of peer.session.sendMany("schema", payload, {
      reliable: true,
      ordered: true,
      nowMs: Date.now(),
      maxFragmentBytes: options.maxFragmentBytes ?? 2048,
    })) sendPacket(remote, responsePacket);
  }
  if (packet.packetType === "data" || result.delivered.length > 0) {
    sendPacket(remote, peer.session.createAckForReceived(packet.sequence));
  }
  return true;
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
