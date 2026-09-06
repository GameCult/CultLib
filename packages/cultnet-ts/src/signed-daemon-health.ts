// Signed daemon-health publication over CultNet RUDP.
//
// This is the transport half of daemon health publication: bind a socket, shake
// hands, sign the statement, put the document, wait for the ack. It is generic
// across services. The receiving service owns the connection id and the schema
// names, and supplies them as a contract; nothing service-specific is hardcoded
// here.
//
// The signature and identity domains are deliberately not parameterised. They
// are the GameCult provider-health domains, shared by every publisher and
// verifier, and a caller that could vary them could silently produce statements
// nobody verifies.

import dgram from "node:dgram";
import crypto from "node:crypto";
import fs from "node:fs";
import { encode } from "@msgpack/msgpack";

import { CultNetRudpSession, decodeRudpPacket, encodeRudpPacket } from "./rudp";
import type { CultNetRudpPacket } from "./rudp";
import { encodeCultNetMessageForWire } from "./contracts";

export const CULTNET_RUDP_PROTOCOL_ID = "cultnet.transport.rudp.v0";

const SIGNATURE_DOMAIN = Buffer.from("gamecult.provider-health.signature.v1\0", "utf8");
const ID_DOMAIN = Buffer.from("gamecult.provider-health.identity.v1\0", "utf8");

/** What the receiving service owns: where to connect and what to call the documents. */
export interface SignedDaemonHealthContract {
  /** RUDP connection id the receiving service listens on. */
  connectionId: number;
  /** Schema id for a signed statement. Also the signing purpose. */
  signedSchemaId: string;
  /** Schema id used when no signing key is configured. */
  unsignedSchemaId: string;
  /** Prefix for generated message ids. Defaults to "daemon-health". */
  messageIdPrefix?: string;
}

export interface SignedDaemonHealthPublisherOptions {
  /** host:port, or [ipv6]:port. */
  endpoint: string;
  daemonId: string;
  healthContract: string;
  sourceRuntimeId?: string;
  /** Omit to publish unsigned. */
  privateKeyPath?: string;
  contract: SignedDaemonHealthContract;
}

export interface DaemonHealth {
  state: string;
  detail?: string;
  observedAt?: string;
}

export interface SignedDaemonHealthPublisher {
  daemonId: string;
  endpoint: { host: string; port: number };
  healthContract: string;
  publisherIncarnationId: string;
  publisherSequence: number;
  sourceRuntimeId: string;
  contract: SignedDaemonHealthContract;
  privateKey?: crypto.KeyObject;
  publicKey?: Buffer;
  signerIdentityId?: string;
}

export function createSignedDaemonHealthPublisher(
  options: SignedDaemonHealthPublisherOptions | null | undefined,
): SignedDaemonHealthPublisher | null {
  if (!options) return null;
  if (!options.contract) {
    throw new Error("Signed daemon health publisher requires a contract naming the connection id and schema ids.");
  }

  const publisher: SignedDaemonHealthPublisher = {
    daemonId: options.daemonId,
    endpoint: parseEndpoint(options.endpoint),
    healthContract: options.healthContract,
    publisherIncarnationId: crypto.randomUUID(),
    publisherSequence: 0,
    sourceRuntimeId: options.sourceRuntimeId || "daemon-health-publisher",
    contract: options.contract,
  };

  if (options.privateKeyPath) {
    publisher.privateKey = crypto.createPrivateKey(fs.readFileSync(options.privateKeyPath));
    publisher.publicKey = rawEd25519PublicKey(publisher.privateKey);
    publisher.signerIdentityId = crypto
      .createHash("sha256")
      .update(ID_DOMAIN)
      .update(publisher.publicKey)
      .digest("hex");
  }
  return publisher;
}

export async function publishSignedDaemonHealth(
  publisher: SignedDaemonHealthPublisher | null,
  health: DaemonHealth,
): Promise<void> {
  if (!publisher) return;
  const { contract } = publisher;

  const socket = dgram.createSocket(endpointFamily(publisher.endpoint.host));
  await bindSocket(socket, publisher.endpoint);
  const receiver = createPacketReceiver(socket);
  const session = new CultNetRudpSession({
    connectionId: contract.connectionId,
    initialSequence: 1,
    resendDelayMs: 100,
  });

  try {
    const connect = session.createConnect(Date.now(), new Uint8Array());
    await sendPacket(socket, publisher.endpoint, connect);
    await receiveUntil(receiver, session, publisher.endpoint, (packet) => packet.packetType === "accept", 5000, "accept");

    const observedAt = health.observedAt || new Date().toISOString();
    const signed = publisher.privateKey ? signedDaemonHealthPayload(publisher, health, observedAt) : null;
    const payload = signed?.payload || encode([
      publisher.daemonId,
      health.state,
      String(health.detail || "").slice(0, 512),
      observedAt,
      publisher.healthContract,
      "daemon-published",
      CULTNET_RUDP_PROTOCOL_ID,
    ]);

    const message = {
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: `${contract.messageIdPrefix || "daemon-health"}:${publisher.daemonId}:${observedAt.replace(/[:.]/g, "-")}`,
      document: {
        schemaId: signed ? contract.signedSchemaId : contract.unsignedSchemaId,
        recordKey: publisher.daemonId,
        storedAt: observedAt,
        payloadEncoding: "messagepack",
        payload,
        sourceRuntimeId: publisher.sourceRuntimeId,
        sourceRole: "daemon-health-publisher",
        tags: [CULTNET_RUDP_PROTOCOL_ID],
      },
    };

    const wirePayload = encode(encodeCultNetMessageForWire(message as never, "cultnet.schema.v0"));
    const dataPackets = session.sendMany("schema", wirePayload, { reliable: true, ordered: true, nowMs: Date.now() });
    const ack = receiveUntil(
      receiver,
      session,
      publisher.endpoint,
      (packet) => packet.packetType === "ack",
      500,
      "ack",
    ).catch(() => undefined);
    for (const packet of dataPackets) {
      await sendPacket(socket, publisher.endpoint, packet);
    }
    await ack;
  } finally {
    receiver.close();
    socket.close();
  }
}

export function signedDaemonHealthPayload(
  publisher: SignedDaemonHealthPublisher,
  health: DaemonHealth,
  observedAt: string,
): { payload: Uint8Array; statement: unknown[]; unsignedPayload: Uint8Array } {
  if (!publisher.privateKey) {
    throw new Error("Signed daemon health requires a configured private key.");
  }
  publisher.publisherSequence += 1;
  const observedAtUnixMillis = Date.parse(observedAt);
  if (!Number.isSafeInteger(observedAtUnixMillis) || observedAtUnixMillis <= 0) {
    throw new Error("Signed daemon health observation time is invalid.");
  }

  const unsigned: unknown[] = [
    publisher.contract.signedSchemaId,
    publisher.daemonId,
    publisher.healthContract,
    publisher.sourceRuntimeId,
    health.state,
    String(health.detail || "").slice(0, 512),
    publisher.signerIdentityId,
    publisher.publisherIncarnationId,
    publisher.publisherSequence,
    observedAtUnixMillis,
    null,
    null,
    null,
    null,
    "ed25519",
    new Uint8Array(),
    false,
  ];

  const unsignedPayload = encode(unsigned);
  const signature = crypto.sign(
    null,
    signedDaemonHealthSigningMessage(publisher.contract.signedSchemaId, unsignedPayload),
    publisher.privateKey,
  );
  const statement = unsigned.slice();
  statement[15] = new Uint8Array(signature);
  return { payload: encode(statement), statement, unsignedPayload };
}

/**
 * Domain-separated signing preimage. The signing purpose is the signed schema
 * id, so a statement signed for one service's schema cannot verify as another's.
 */
export function signedDaemonHealthSigningMessage(signedSchemaId: string, payload: Uint8Array): Buffer {
  const purpose = Buffer.from(signedSchemaId, "utf8");
  const purposeLength = Buffer.alloc(8);
  purposeLength.writeBigUInt64BE(BigInt(purpose.length));
  const payloadLength = Buffer.alloc(8);
  payloadLength.writeBigUInt64BE(BigInt(payload.length));
  return Buffer.concat([SIGNATURE_DOMAIN, purposeLength, purpose, payloadLength, Buffer.from(payload)]);
}

function rawEd25519PublicKey(privateKey: crypto.KeyObject): Buffer {
  const der = crypto.createPublicKey(privateKey).export({ type: "spki", format: "der" }) as Buffer;
  if (der.length < 32) throw new Error("Ed25519 public key export is too short.");
  return Buffer.from(der.subarray(der.length - 32));
}

async function bindSocket(socket: dgram.Socket, endpoint: { host: string }): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(0, endpoint.host.includes(":") ? "::" : "0.0.0.0", () => {
      socket.off("error", reject);
      resolve();
    });
  });
}

export function parseEndpoint(value: string): { host: string; port: number } {
  const text = String(value || "").trim();
  const ipv6 = text.match(/^\[([^\]]+)\]:(\d+)$/);
  if (ipv6) return { host: ipv6[1], port: parsePort(ipv6[2]) };
  const index = text.lastIndexOf(":");
  if (index <= 0) {
    throw new Error(`Daemon health RUDP endpoint must be host:port, got "${value}".`);
  }
  return { host: text.slice(0, index), port: parsePort(text.slice(index + 1)) };
}

function parsePort(value: string): number {
  const port = Number(value);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new Error(`Daemon health RUDP endpoint port is invalid: ${value}`);
  }
  return port;
}

function endpointFamily(host: string): "udp4" | "udp6" {
  return host.includes(":") ? "udp6" : "udp4";
}

interface PacketReceiver {
  socket: dgram.Socket;
  next(timeoutMs: number, label?: string): Promise<CultNetRudpPacket>;
  close(): void;
}

async function receiveUntil(
  receiver: PacketReceiver,
  session: CultNetRudpSession,
  endpoint: { host: string; port: number },
  predicate: (packet: CultNetRudpPacket) => boolean,
  timeoutMs: number,
  label: string,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const packet = await receiver.next(Math.min(100, deadline - Date.now()), label);
      const result = session.receive(packet, Date.now());
      if (result.reply) {
        throw new Error("Daemon health publisher received an unexpected reply-required packet.");
      }
      if (predicate(packet)) return;
    } catch (error) {
      if ((error as NodeJS.ErrnoException)?.code !== "ETIMEDOUT") throw error;
    }
    for (const packet of session.dueResends(Date.now())) {
      await sendPacket(receiver.socket, endpoint, packet);
    }
  }
  throw new Error(`timed out waiting for daemon health RUDP ${label} response after ${timeoutMs}ms`);
}

function createPacketReceiver(socket: dgram.Socket): PacketReceiver {
  const packets: CultNetRudpPacket[] = [];
  const waiters: Array<{
    resolve: (packet: CultNetRudpPacket) => void;
    reject: (error: Error) => void;
    timer: NodeJS.Timeout;
  }> = [];
  const errors: Error[] = [];

  const resolveNext = () => {
    while (waiters.length > 0 && (packets.length > 0 || errors.length > 0)) {
      const waiter = waiters.shift()!;
      clearTimeout(waiter.timer);
      if (errors.length > 0) waiter.reject(errors.shift()!);
      else waiter.resolve(packets.shift()!);
    }
  };
  const onMessage = (wire: Buffer) => {
    try {
      packets.push(decodeRudpPacket(wire));
    } catch (error) {
      errors.push(error as Error);
    }
    resolveNext();
  };
  const onError = (error: Error) => {
    errors.push(error);
    resolveNext();
  };

  socket.on("message", onMessage);
  socket.on("error", onError);

  return {
    socket,
    next(timeoutMs: number, label = "packet") {
      if (packets.length > 0) return Promise.resolve(packets.shift()!);
      if (errors.length > 0) return Promise.reject(errors.shift()!);
      return new Promise<CultNetRudpPacket>((resolve, reject) => {
        const waiter = {
          resolve,
          reject,
          timer: setTimeout(() => {
            const index = waiters.indexOf(waiter);
            if (index >= 0) waiters.splice(index, 1);
            const error = new Error(`timed out waiting for daemon health RUDP ${label}`) as NodeJS.ErrnoException;
            error.code = "ETIMEDOUT";
            reject(error);
          }, Math.max(1, timeoutMs)),
        };
        waiters.push(waiter);
      });
    },
    close() {
      socket.off("message", onMessage);
      socket.off("error", onError);
      while (waiters.length > 0) {
        const waiter = waiters.shift()!;
        clearTimeout(waiter.timer);
        const error = new Error("Daemon health publisher closed.") as NodeJS.ErrnoException;
        error.code = "ECLOSED";
        waiter.reject(error);
      }
    },
  };
}

async function sendPacket(
  socket: dgram.Socket,
  endpoint: { host: string; port: number },
  packet: CultNetRudpPacket,
): Promise<void> {
  const wire = encodeRudpPacket(packet);
  await new Promise<void>((resolve, reject) => {
    socket.send(wire, endpoint.port, endpoint.host, (error) => {
      if (error) reject(error);
      else resolve();
    });
  });
}
