import dgram from "node:dgram";
import { decode, encode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, parseCultNetMessage } from "./contracts";
import type { IdunnRuntimeSigner } from "./idunn-runtime-authority";
import { CultNetRudpSession, decodeRudpPacket, encodeRudpPacket } from "./rudp";
import type { CultNetRudpPacket } from "./rudp";

const ODIN_CAPABILITY = "odin.verse-rendezvous";
const MAX_LEASE_WAIT_ROUNDS = 720;

export type IdunnRuntimePresencePublisher = {
  readonly runtimeId: string;
  readonly runtimeInstanceId: string;
  readonly requiresWriteLease: boolean;
  /** Publishes the signer's presence in the health the signer reports. */
  publish(detail: string): Promise<string>;
  /**
   * Publishes warming presence until Idunn's lease names one. Call it before
   * `reportHealth('active')`: once the signer reports a non-warming health,
   * `publish` inside this loop throws (it needs the lease) instead of polling.
   */
  waitForWriteLease(options?: { pollIntervalMs?: number; signal?: AbortSignal }): Promise<string>;
  assertWriteLease(): Promise<string>;
};

export type IdunnRuntimePresencePublisherOptions = {
  signer: IdunnRuntimeSigner;
  endpoint: string;
};

/**
 * Publishes the signer's presence to Odin over RUDP, for services that are
 * CultMesh-aware and declare `odin.verse-rendezvous` in Expected. The route
 * proof never passes through here: `signer.answerRouteObservation` answers
 * Idunn's challenge locally and needs no Odin. Capability shortfalls are not
 * judged here either; the correlation owner records a typed disagreement.
 */
export function createIdunnRuntimePresencePublisher(
  options: IdunnRuntimePresencePublisherOptions,
): IdunnRuntimePresencePublisher {
  const { signer } = options;
  const endpoint = parseRudpEndpoint(options.endpoint);
  const odinDependency = signer.authority.expected.dependencies.find((dependency) => dependency.kind === "shared-infrastructure"
    && dependency.capability === ODIN_CAPABILITY);
  if (!odinDependency || normalizeRudpEndpoint(odinDependency.providerEndpoint ?? "") !== normalizeRudpEndpoint(options.endpoint)) {
    throw new Error("Configured Odin endpoint does not match Idunn Expected dependency authority.");
  }
  const connectionId = 0x0d1d0002;
  let messageSequence = 0;

  const publish = async (detail: string) => {
    const signed = signer.sign(detail);
    messageSequence += 1;
    await publishDocument(endpoint, connectionId, {
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: `runtime-presence:${signer.authority.expected.target}:${signer.runtimeInstanceId}:${messageSequence}`,
      document: signed.document,
    });
    return signed.canonicalSha256;
  };

  return {
    runtimeId: signer.runtimeId,
    runtimeInstanceId: signer.runtimeInstanceId,
    requiresWriteLease: signer.requiresWriteLease,
    publish,
    async waitForWriteLease({ pollIntervalMs = 5000, signal } = {}) {
      if (!signer.requiresWriteLease) return "";
      let warmingSequence = 0;
      while (!signal?.aborted) {
        await publish("waiting-for-process-write-lease");
        warmingSequence += 1;
        try {
          return signer.assertWriteLease();
        } catch (error) {
          if (warmingSequence > MAX_LEASE_WAIT_ROUNDS) throw new Error("Timed out waiting for Idunn process write lease.", { cause: error });
        }
        await abortableDelay(pollIntervalMs, signal);
      }
      throw signal?.reason ?? new Error("Aborted while waiting for Idunn process write lease.");
    },
    async assertWriteLease() {
      return signer.assertWriteLease();
    },
  };
}

async function publishDocument(
  endpoint: { host: string; port: number },
  connectionId: number,
  message: Record<string, unknown>,
): Promise<void> {
  const socket = dgram.createSocket(endpoint.host.includes(":") ? "udp6" : "udp4");
  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(0, endpoint.host.includes(":") ? "::" : "0.0.0.0", () => {
      socket.off("error", reject);
      resolve();
    });
  });
  const receiver = receivePackets(socket);
  const session = new CultNetRudpSession({ connectionId, initialSequence: 1, resendDelayMs: 100 });
  try {
    await sendPacket(socket, endpoint, session.createConnect(Date.now(), new Uint8Array()));
    await receiveUntil(receiver, session, endpoint, (packet) => packet.packetType === "accept", 5000);
    const wirePayload = encode(encodeCultNetMessageForWire(message as never, "cultnet.schema.v0"));
    const packets = session.sendMany("schema", wirePayload, { reliable: true, ordered: true, nowMs: Date.now() });
    for (const packet of packets) await sendPacket(socket, endpoint, packet);
    await receiveUntil(
      receiver,
      session,
      endpoint,
      () => session.outstandingReliablePacketCount === 0,
      2000,
      (frame) => {
        if (frame.channelId !== "schema") return;
        const response = parseCultNetMessage(decode(frame.payload));
        if (response.schemaVersion === "cultnet.error.v0") {
          throw new Error(`Odin rejected runtime presence: ${response.error}`);
        }
      },
    );
  } finally {
    receiver.close();
    socket.close();
  }
}

type PacketReceiver = {
  socket: dgram.Socket;
  next(timeoutMs: number): Promise<CultNetRudpPacket>;
  close(): void;
};

function receivePackets(socket: dgram.Socket): PacketReceiver {
  const packets: CultNetRudpPacket[] = [];
  const waiters: Array<{ resolve: (packet: CultNetRudpPacket) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }> = [];
  const errors: Error[] = [];
  const drain = () => {
    while (waiters.length && (packets.length || errors.length)) {
      const waiter = waiters.shift()!;
      clearTimeout(waiter.timer);
      if (errors.length) waiter.reject(errors.shift()!);
      else waiter.resolve(packets.shift()!);
    }
  };
  const onMessage = (wire: Buffer) => {
    try { packets.push(decodeRudpPacket(wire)); } catch (error) { errors.push(error as Error); }
    drain();
  };
  const onError = (error: Error) => { errors.push(error); drain(); };
  socket.on("message", onMessage);
  socket.on("error", onError);
  return {
    socket,
    next(timeoutMs) {
      if (packets.length) return Promise.resolve(packets.shift()!);
      if (errors.length) return Promise.reject(errors.shift()!);
      return new Promise((resolve, reject) => {
        const waiter = {
          resolve,
          reject,
          timer: setTimeout(() => {
            const index = waiters.indexOf(waiter);
            if (index >= 0) waiters.splice(index, 1);
            const error = new Error("Timed out waiting for CultNet RUDP packet.") as NodeJS.ErrnoException;
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
      for (const waiter of waiters.splice(0)) {
        clearTimeout(waiter.timer);
        waiter.reject(new Error("CultNet RUDP publisher closed."));
      }
    },
  };
}

async function receiveUntil(
  receiver: PacketReceiver,
  session: CultNetRudpSession,
  endpoint: { host: string; port: number },
  predicate: (packet: CultNetRudpPacket) => boolean,
  timeoutMs: number,
  onDelivered?: (frame: { channelId: string; payload: Uint8Array; sequence: number }) => void,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const packet = await receiver.next(Math.min(100, deadline - Date.now()));
      const received = session.receive(packet, Date.now());
      if (received.reply) throw new Error("Runtime presence received an unexpected reply-required packet.");
      for (const frame of received.delivered) onDelivered?.(frame);
      if (predicate(packet)) return;
    } catch (error) {
      if ((error as NodeJS.ErrnoException)?.code !== "ETIMEDOUT") throw error;
    }
    for (const packet of session.dueResends(Date.now())) await sendPacket(receiver.socket, endpoint, packet);
  }
  throw new Error(`Timed out waiting for CultNet RUDP response after ${timeoutMs} ms.`);
}

async function sendPacket(socket: dgram.Socket, endpoint: { host: string; port: number }, packet: CultNetRudpPacket): Promise<void> {
  const wire = encodeRudpPacket(packet);
  await new Promise<void>((resolve, reject) => {
    socket.send(wire, endpoint.port, endpoint.host, (error) => error ? reject(error) : resolve());
  });
}

function parseRudpEndpoint(value: string): { host: string; port: number } {
  const text = value.replace(/^rudp:\/\//i, "");
  const match = text.match(/^\[([^\]]+)\]:(\d+)$/) ?? text.match(/^([^:]+):(\d+)$/);
  if (!match) throw new Error(`Invalid Odin RUDP endpoint: ${value}`);
  const port = Number(match[2]);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`Invalid Odin RUDP port: ${match[2]}`);
  return { host: match[1]!, port };
}

function normalizeRudpEndpoint(value: string): string {
  const normalized = value.trim().toLowerCase().replace(/^rudp:\/\//, "");
  const parsed = parseRudpEndpoint(normalized);
  return `${parsed.host.toLowerCase()}:${parsed.port}`;
}

function abortableDelay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        signal?.removeEventListener("abort", onAbort);
        resolve();
      }, milliseconds);
      const onAbort = () => {
        clearTimeout(timer);
        reject(signal?.reason ?? new Error("Aborted."));
      };
      signal?.addEventListener("abort", onAbort, { once: true });
  });
}
