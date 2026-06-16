import { EventEmitter } from "node:events";
import { type RemoteInfo, type Socket } from "node:dgram";

import type { CultNetTransportProfile } from "./contracts";
import {
  createCultNetReconnectPolicy,
  type CultNetReconnectPolicy,
  type CultNetTransportConnection,
  type CultNetTransportFrame,
  type CultNetTransportStats,
} from "./transport";

const RUDP_MAGIC = [0x43, 0x4e, 0x52, 0x30] as const; // CNR0
const RUDP_VERSION = 0;
const RUDP_FIXED_HEADER_BYTES = 36;
const MAX_CHANNEL_ID_BYTES = 255;

export type CultNetRudpPacketType =
  | "connect"
  | "accept"
  | "data"
  | "ack"
  | "ping"
  | "pong"
  | "disconnect";

export interface CultNetRudpPacket {
  packetType: CultNetRudpPacketType;
  connectionId: number;
  sequence: number;
  ack: number;
  ackMask: number;
  channelId: string;
  reliable?: boolean;
  ordered?: boolean;
  sequenced?: boolean;
  fragmentId?: number;
  fragmentIndex?: number;
  fragmentCount?: number;
  payload?: Uint8Array;
}

export interface CultNetRudpDeliveredFrame {
  channelId: string;
  payload: Uint8Array;
  sequence: number;
}

export interface CultNetRudpSessionOptions {
  connectionId: number;
  initialSequence?: number;
  resendDelayMs?: number;
  maxPendingReliablePackets?: number;
}

export interface CultNetRudpReceiveResult {
  delivered: CultNetRudpDeliveredFrame[];
  reply?: CultNetRudpPacket;
  pong?: boolean;
  pongPayload?: Uint8Array;
  disconnected?: boolean;
  disconnectReason?: Uint8Array;
}

type PendingReliablePacket = {
  packet: CultNetRudpPacket;
  lastSentAtMs: number;
};

type PendingOrderedFrame = {
  frame: CultNetRudpDeliveredFrame;
  nextSequence: number;
};

type FragmentBuffer = {
  channelId: string;
  reliable: boolean;
  ordered: boolean;
  sequenced: boolean;
  fragmentCount: number;
  payloads: Map<number, Uint8Array>;
  sequences: Map<number, number>;
};

export interface RudpTransportProfileOptions {
  transportId?: string;
  host?: string;
  port?: number;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
  maxPendingReliablePackets?: number;
  reconnectPolicy?: CultNetReconnectPolicy;
}

export interface CultNetRudpSocketTransportOptions {
  runtimeId: string;
  socket: Socket;
  mode: "client" | "server";
  remoteHost?: string;
  remotePort?: number;
  connectionId: number;
  initialSequence?: number;
  resendDelayMs?: number;
  resendPollMs?: number;
  transportId?: string;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
  maxPendingReliablePackets?: number;
  reconnectPolicy?: CultNetReconnectPolicy;
}

const packetTypeToCode: Record<CultNetRudpPacketType, number> = {
  connect: 1,
  accept: 2,
  data: 3,
  ack: 4,
  ping: 5,
  pong: 6,
  disconnect: 7,
};

const packetTypeFromCode = new Map<number, CultNetRudpPacketType>(
  Object.entries(packetTypeToCode).map(([name, code]) => [code, name as CultNetRudpPacketType]),
);

export class CultNetRudpSession {
  readonly connectionId: number;
  readonly resendDelayMs: number;
  #nextSequence: number;
  #nextFragmentId = 1;
  #connected = false;
  readonly #maxPendingReliablePackets: number | undefined;
  #lastReceivedAtMs: number | undefined;
  #highestReceivedSequence: number | undefined;
  readonly #receivedSequences = new Set<number>();
  readonly #pendingReliable = new Map<number, PendingReliablePacket>();
  readonly #orderedNextSequenceByChannel = new Map<string, number>();
  readonly #orderedBuffers = new Map<string, Map<number, PendingOrderedFrame>>();
  readonly #fragmentBuffers = new Map<string, FragmentBuffer>();

  constructor(options: CultNetRudpSessionOptions) {
    this.connectionId = toUint32(options.connectionId, "connectionId");
    this.#nextSequence = toUint32(options.initialSequence ?? 1, "initialSequence");
    this.resendDelayMs = options.resendDelayMs ?? 250;
    if (options.maxPendingReliablePackets !== undefined && options.maxPendingReliablePackets <= 0) {
      throw new Error("RUDP maxPendingReliablePackets must be greater than zero.");
    }
    this.#maxPendingReliablePackets = options.maxPendingReliablePackets;
  }

  get connected(): boolean {
    return this.#connected;
  }

  get pendingReliableSequences(): number[] {
    return [...this.#pendingReliable.keys()].sort((left, right) => left - right);
  }

  get lastReceivedAtMs(): number | undefined {
    return this.#lastReceivedAtMs;
  }

  createConnect(nowMs = 0, payload = new Uint8Array()): CultNetRudpPacket {
    this.#ensureReliableCapacity(1);
    const packet = this.#createPacket({
      packetType: "connect",
      channelId: "control",
      reliable: true,
      ordered: true,
      payload,
    });
    this.#trackReliable(packet, nowMs);
    return packet;
  }

  acceptConnect(packet: CultNetRudpPacket, nowMs = 0, payload = new Uint8Array()): CultNetRudpPacket {
    this.#requireConnection(packet);
    if (packet.packetType !== "connect") {
      throw new Error(`Expected RUDP connect packet, got ${packet.packetType}.`);
    }

    this.#ensureReliableCapacity(1);
    this.#rememberReceived(packet.sequence);
    this.#connected = true;
    const response = this.#createPacket({
      packetType: "accept",
      channelId: "control",
      reliable: true,
      ordered: true,
      payload,
    });
    this.#trackReliable(response, nowMs);
    return response;
  }

  send(
    channelId: string,
    payload: Uint8Array,
    options: { reliable?: boolean; ordered?: boolean; sequenced?: boolean; nowMs?: number } = {},
  ): CultNetRudpPacket {
    return this.sendMany(channelId, payload, options)[0]!;
  }

  sendMany(
    channelId: string,
    payload: Uint8Array,
    options: {
      reliable?: boolean;
      ordered?: boolean;
      sequenced?: boolean;
      nowMs?: number;
      maxFragmentBytes?: number;
    } = {},
  ): CultNetRudpPacket[] {
    if (!this.#connected) {
      throw new Error("Cannot send RUDP data before the session is connected.");
    }

    const maxFragmentBytes = options.maxFragmentBytes;
    if (maxFragmentBytes !== undefined && maxFragmentBytes <= 0) {
      throw new Error("RUDP maxFragmentBytes must be greater than zero.");
    }

    if (maxFragmentBytes === undefined || payload.byteLength <= maxFragmentBytes) {
      this.#ensureReliableCapacity(options.reliable ? 1 : 0);
      const packet = this.#createPacket({
        packetType: "data",
        channelId,
        payload,
        reliable: options.reliable,
        ordered: options.ordered,
        sequenced: options.sequenced,
      });
      if (packet.reliable) {
        this.#trackReliable(packet, options.nowMs ?? 0);
      }
      return [packet];
    }

    const fragmentCount = Math.ceil(payload.byteLength / maxFragmentBytes);
    if (fragmentCount > 0xffff) {
      throw new Error("RUDP payload requires more than 65535 fragments.");
    }
    this.#ensureReliableCapacity(options.reliable ? fragmentCount : 0);

    const fragmentId = this.#allocateFragmentId();
    const packets: CultNetRudpPacket[] = [];
    for (let index = 0; index < fragmentCount; index += 1) {
      const start = index * maxFragmentBytes;
      const packet = this.#createPacket({
        packetType: "data",
        channelId,
        payload: payload.slice(start, Math.min(start + maxFragmentBytes, payload.byteLength)),
        reliable: options.reliable,
        ordered: options.ordered,
        sequenced: options.sequenced,
        fragmentId,
        fragmentIndex: index,
        fragmentCount,
      });
      if (packet.reliable) {
        this.#trackReliable(packet, options.nowMs ?? 0);
      }
      packets.push(packet);
    }
    return packets;
  }

  receive(packet: CultNetRudpPacket, nowMs = 0): CultNetRudpReceiveResult {
    this.#requireConnection(packet);
    this.#applyAcknowledgements(packet);
    this.#lastReceivedAtMs = nowMs;
    const expectedSequenceIfUninitialized = this.#highestReceivedSequence === undefined
      ? packet.sequence
      : this.#highestReceivedSequence + 1;

    if (packet.packetType === "accept") {
      this.#rememberReceived(packet.sequence);
      this.#connected = true;
      return { delivered: [] };
    }

    if (packet.packetType === "ping") {
      this.#rememberReceived(packet.sequence);
      return {
        delivered: [],
        reply: this.#createPacket({
          packetType: "pong",
          channelId: "control",
          payload: packet.payload ?? new Uint8Array(),
        }),
      };
    }

    if (packet.packetType === "ack" || packet.packetType === "pong") {
      this.#rememberReceived(packet.sequence);
      return {
        delivered: [],
        pong: packet.packetType === "pong",
        pongPayload: packet.packetType === "pong" ? packet.payload ?? new Uint8Array() : undefined,
      };
    }

    if (packet.packetType === "disconnect") {
      this.#rememberReceived(packet.sequence);
      this.#connected = false;
      return {
        delivered: [],
        disconnected: true,
        disconnectReason: packet.payload ?? new Uint8Array(),
      };
    }

    if (packet.packetType !== "data") {
      return { delivered: [] };
    }

    const isDuplicate = this.#receivedSequences.has(packet.sequence);
    this.#rememberReceived(packet.sequence);
    if (isDuplicate) {
      return { delivered: [] };
    }

    const reassembled = this.#reassemble(packet);
    if (!reassembled) {
      return { delivered: [] };
    }

    if (!reassembled.ordered) {
      return { delivered: [reassembled.frame] };
    }

    return { delivered: this.#deliverOrdered(reassembled.frame, reassembled.nextSequence, expectedSequenceIfUninitialized) };
  }

  createAck(): CultNetRudpPacket {
    return this.#createPacket({
      packetType: "ack",
      channelId: "control",
    });
  }

  createPing(payload = new Uint8Array()): CultNetRudpPacket {
    return this.#createPacket({
      packetType: "ping",
      channelId: "control",
      payload,
    });
  }

  createDisconnect(reason = new Uint8Array()): CultNetRudpPacket {
    this.#connected = false;
    return this.#createPacket({
      packetType: "disconnect",
      channelId: "control",
      payload: reason,
    });
  }

  checkTimeout(nowMs: number, timeoutMs: number): boolean {
    if (!this.#connected || this.#lastReceivedAtMs === undefined) {
      return false;
    }
    if (nowMs - this.#lastReceivedAtMs <= timeoutMs) {
      return false;
    }
    this.#connected = false;
    return true;
  }

  dueResends(nowMs: number): CultNetRudpPacket[] {
    const due: CultNetRudpPacket[] = [];
    for (const pending of this.#pendingReliable.values()) {
      if (nowMs - pending.lastSentAtMs >= this.resendDelayMs) {
        pending.lastSentAtMs = nowMs;
        due.push({ ...pending.packet });
      }
    }
    return due.sort((left, right) => left.sequence - right.sequence);
  }

  #createPacket(packet: {
    packetType: CultNetRudpPacketType;
    channelId: string;
    payload?: Uint8Array;
    reliable?: boolean;
    ordered?: boolean;
    sequenced?: boolean;
    fragmentId?: number;
    fragmentIndex?: number;
    fragmentCount?: number;
  }): CultNetRudpPacket {
    const sequence = this.#nextSequence;
    this.#nextSequence = toUint32(this.#nextSequence + 1, "sequence");
    const { ack, ackMask } = this.#ackState();
    return {
      packetType: packet.packetType,
      connectionId: this.connectionId,
      sequence,
      ack,
      ackMask,
      channelId: packet.channelId,
      reliable: packet.reliable ?? false,
      ordered: packet.ordered ?? false,
      sequenced: packet.sequenced ?? false,
      fragmentId: packet.fragmentId ?? 0,
      fragmentIndex: packet.fragmentIndex ?? 0,
      fragmentCount: packet.fragmentCount ?? 0,
      payload: packet.payload ?? new Uint8Array(),
    };
  }

  #trackReliable(packet: CultNetRudpPacket, nowMs: number): void {
    this.#pendingReliable.set(packet.sequence, {
      packet: { ...packet, payload: packet.payload ? new Uint8Array(packet.payload) : new Uint8Array() },
      lastSentAtMs: nowMs,
    });
  }

  #ensureReliableCapacity(packetCount: number): void {
    if (packetCount === 0 || this.#maxPendingReliablePackets === undefined) {
      return;
    }
    if (this.#pendingReliable.size + packetCount > this.#maxPendingReliablePackets) {
      throw new Error("RUDP reliable send queue is full.");
    }
  }

  #applyAcknowledgements(packet: CultNetRudpPacket): void {
    this.#pendingReliable.delete(packet.ack);
    for (let bit = 0; bit < 32; bit += 1) {
      if ((packet.ackMask & (1 << bit)) !== 0) {
        this.#pendingReliable.delete(packet.ack - bit - 1);
      }
    }
  }

  #rememberReceived(sequence: number): void {
    this.#receivedSequences.add(sequence);
    if (this.#highestReceivedSequence === undefined || sequence > this.#highestReceivedSequence) {
      this.#highestReceivedSequence = sequence;
    }
  }

  #ackState(): { ack: number; ackMask: number } {
    const ack = this.#highestReceivedSequence ?? 0;
    let ackMask = 0;
    for (let bit = 0; bit < 32; bit += 1) {
      if (ack > bit && this.#receivedSequences.has(ack - bit - 1)) {
        ackMask |= 1 << bit;
      }
    }
    return { ack, ackMask: ackMask >>> 0 };
  }

  #reassemble(packet: CultNetRudpPacket): { frame: CultNetRudpDeliveredFrame; ordered: boolean; nextSequence: number } | undefined {
    const payload = packet.payload ?? new Uint8Array();
    const fragmentCount = packet.fragmentCount ?? 0;
    if (fragmentCount === 0) {
      return {
        frame: {
          channelId: packet.channelId,
          payload,
          sequence: packet.sequence,
        },
        ordered: packet.ordered ?? false,
        nextSequence: packet.sequence + 1,
      };
    }

    const fragmentIndex = packet.fragmentIndex ?? 0;
    const fragmentId = packet.fragmentId ?? 0;
    if (fragmentId === 0) {
      throw new Error("RUDP fragmented packet must have a non-zero fragment id.");
    }
    if (fragmentIndex >= fragmentCount) {
      throw new Error("RUDP fragment index must be lower than fragment count.");
    }

    const key = `${packet.channelId}\0${fragmentId}`;
    let buffer = this.#fragmentBuffers.get(key);
    if (!buffer) {
      buffer = {
        channelId: packet.channelId,
        reliable: packet.reliable ?? false,
        ordered: packet.ordered ?? false,
        sequenced: packet.sequenced ?? false,
        fragmentCount,
        payloads: new Map(),
        sequences: new Map(),
      };
      this.#fragmentBuffers.set(key, buffer);
    }
    if (buffer.fragmentCount !== fragmentCount || buffer.ordered !== (packet.ordered ?? false)) {
      throw new Error("RUDP fragment metadata changed within a fragment set.");
    }

    buffer.payloads.set(fragmentIndex, payload);
    buffer.sequences.set(fragmentIndex, packet.sequence);
    if (buffer.payloads.size < fragmentCount) {
      return undefined;
    }

    const chunks: Uint8Array[] = [];
    const sequences: number[] = [];
    let totalBytes = 0;
    for (let index = 0; index < fragmentCount; index += 1) {
      const chunk = buffer.payloads.get(index);
      const sequence = buffer.sequences.get(index);
      if (!chunk || sequence === undefined) {
        return undefined;
      }
      chunks.push(chunk);
      sequences.push(sequence);
      totalBytes += chunk.byteLength;
    }

    const merged = new Uint8Array(totalBytes);
    let offset = 0;
    for (const chunk of chunks) {
      merged.set(chunk, offset);
      offset += chunk.byteLength;
    }
    this.#fragmentBuffers.delete(key);
    return {
      frame: {
        channelId: buffer.channelId,
        payload: merged,
        sequence: Math.min(...sequences),
      },
      ordered: buffer.ordered,
      nextSequence: Math.max(...sequences) + 1,
    };
  }

  #deliverOrdered(
    frame: CultNetRudpDeliveredFrame,
    nextSequence: number,
    expectedSequenceIfUninitialized: number,
  ): CultNetRudpDeliveredFrame[] {
    let next = this.#orderedNextSequenceByChannel.get(frame.channelId);
    if (next === undefined) {
      next = Math.min(expectedSequenceIfUninitialized, frame.sequence);
      this.#orderedNextSequenceByChannel.set(frame.channelId, next);
    }

    if (frame.sequence < next) {
      return [];
    }

    if (frame.sequence > next) {
      let buffer = this.#orderedBuffers.get(frame.channelId);
      if (!buffer) {
        buffer = new Map();
        this.#orderedBuffers.set(frame.channelId, buffer);
      }
      buffer.set(frame.sequence, { frame, nextSequence });
      return [];
    }

    this.#orderedNextSequenceByChannel.set(frame.channelId, nextSequence);
    return [
      frame,
      ...this.#drainOrdered(frame.channelId),
    ];
  }

  #drainOrdered(channelId: string): CultNetRudpDeliveredFrame[] {
    const delivered: CultNetRudpDeliveredFrame[] = [];
    const buffer = this.#orderedBuffers.get(channelId);
    if (!buffer) {
      return delivered;
    }

    let next = this.#orderedNextSequenceByChannel.get(channelId);
    while (next !== undefined && buffer.has(next)) {
      const pending = buffer.get(next)!;
      buffer.delete(next);
      delivered.push(pending.frame);
      next = pending.nextSequence;
      this.#orderedNextSequenceByChannel.set(channelId, next);
    }
    return delivered;
  }

  #allocateFragmentId(): number {
    const fragmentId = this.#nextFragmentId;
    this.#nextFragmentId += 1;
    if (this.#nextFragmentId > 0xffff) {
      this.#nextFragmentId = 1;
    }
    return fragmentId;
  }

  #requireConnection(packet: CultNetRudpPacket): void {
    if (packet.connectionId !== this.connectionId) {
      throw new Error(`RUDP packet connection id ${packet.connectionId} does not match ${this.connectionId}.`);
    }
  }
}

export class CultNetRudpSocketTransportConnection extends EventEmitter implements CultNetTransportConnection {
  readonly profile: CultNetTransportProfile;
  readonly #socket: Socket;
  readonly #session: CultNetRudpSession;
  readonly #mode: "client" | "server";
  readonly #resendTimer: NodeJS.Timeout;
  readonly #maxFragmentBytes: number | undefined;
  #remoteHost: string | undefined;
  #remotePort: number | undefined;
  #closed = false;
  readonly #stats: CultNetTransportStats = {
    bytesReceived: 0,
    bytesSent: 0,
    framesReceived: 0,
    framesSent: 0,
  };

  constructor(options: CultNetRudpSocketTransportOptions) {
    super();
    this.#socket = options.socket;
    this.#mode = options.mode;
    this.#remoteHost = options.remoteHost;
    this.#remotePort = options.remotePort;
    this.#maxFragmentBytes = options.maxFragmentBytes;
    this.#session = new CultNetRudpSession({
      connectionId: options.connectionId,
      initialSequence: options.initialSequence,
      resendDelayMs: options.resendDelayMs,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
    });
    const address = this.#socket.address();
    const localPort = typeof address === "string" ? undefined : address.port;
    const localHost = typeof address === "string" ? undefined : address.address;
    this.profile = createRudpTransportProfile(options.runtimeId, {
      transportId: options.transportId,
      host: localHost,
      port: localPort,
      maxPayloadBytes: options.maxPayloadBytes,
      maxFragmentBytes: options.maxFragmentBytes,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
      reconnectPolicy: options.reconnectPolicy,
    });

    this.#socket.on("message", (wire, remote) => this.#receiveDatagram(wire, remote));
    this.#socket.on("close", () => {
      this.#closed = true;
      clearInterval(this.#resendTimer);
      this.emit("close");
    });
    this.#socket.on("error", (error) => this.emit("error", error instanceof Error ? error : new Error(String(error))));
    this.#resendTimer = setInterval(() => this.#sendDueResends(), options.resendPollMs ?? 25);
    this.#resendTimer.unref?.();
  }

  get connected(): boolean {
    return this.#session.connected;
  }

  get stats(): CultNetTransportStats {
    return { ...this.#stats };
  }

  connect(payload = new Uint8Array()): void {
    if (this.#mode !== "client") {
      throw new Error("Only a client RUDP socket transport can initiate connect.");
    }
    this.#sendPacket(this.#session.createConnect(Date.now(), payload));
  }

  send(channelId: string, payload: Uint8Array): void {
    const packets = this.#session.sendMany(channelId, payload, {
      ...channelOptions(channelId),
      nowMs: Date.now(),
      maxFragmentBytes: this.#maxFragmentBytes,
    });
    for (const packet of packets) {
      this.#sendPacket(packet);
    }
    this.#stats.framesSent += 1;
  }

  ping(payload = new Uint8Array()): void {
    this.#sendPacket(this.#session.createPing(payload));
  }

  checkTimeout(timeoutMs: number, nowMs = Date.now()): boolean {
    const timedOut = this.#session.checkTimeout(nowMs, timeoutMs);
    if (timedOut) {
      this.emit("timeout");
      this.emit("close");
    }
    return timedOut;
  }

  close(): void {
    clearInterval(this.#resendTimer);
    if (!this.#closed) {
      this.#closed = true;
      this.#socket.close();
    }
  }

  #receiveDatagram(wire: Buffer, remote: RemoteInfo): void {
    this.#stats.bytesReceived += wire.length;
    let packet: CultNetRudpPacket;
    try {
      packet = decodeRudpPacket(wire);
    } catch (error) {
      this.emit("error", error instanceof Error ? error : new Error(String(error)));
      return;
    }

    if (!this.#remoteHost || this.#remotePort === undefined) {
      this.#remoteHost = remote.address;
      this.#remotePort = remote.port;
    } else if (remote.address !== this.#remoteHost || remote.port !== this.#remotePort) {
      return;
    }

    try {
      if (this.#mode === "server" && packet.packetType === "connect") {
        this.#sendPacket(this.#session.acceptConnect(packet, Date.now()));
        return;
      }

      const result = this.#session.receive(packet, Date.now());
      if (result.reply) {
        this.#sendPacket(result.reply);
      }
      if (result.pong) {
        this.emit("pong", { payload: result.pongPayload ?? new Uint8Array() });
      }
      for (const frame of result.delivered) {
        this.#stats.framesReceived += 1;
        this.emit("frame", {
          channelId: frame.channelId,
          payload: frame.payload,
        } satisfies CultNetTransportFrame);
      }
      if (result.disconnected) {
        this.emit("disconnect", { reason: result.disconnectReason ?? new Uint8Array() });
        this.emit("close");
        return;
      }
      if (packet.packetType === "accept" || result.delivered.length > 0) {
        this.#sendPacket(this.#session.createAck());
      }
    } catch (error) {
      this.emit("error", error instanceof Error ? error : new Error(String(error)));
    }
  }

  #sendDueResends(): void {
    for (const packet of this.#session.dueResends(Date.now())) {
      this.#sendPacket(packet);
    }
  }

  #sendPacket(packet: CultNetRudpPacket): void {
    if (!this.#remoteHost || this.#remotePort === undefined) {
      throw new Error("RUDP socket transport does not have a remote endpoint.");
    }
    const wire = encodeRudpPacket(packet);
    this.#stats.bytesSent += wire.length;
    this.#socket.send(wire, this.#remotePort, this.#remoteHost);
  }
}

export function createRudpTransportProfile(
  runtimeId: string,
  options: RudpTransportProfileOptions = {},
): CultNetTransportProfile {
  const channel = (
    channelId: string,
    delivery: "reliable" | "unreliable",
    ordering: "ordered" | "unordered" | "sequenced",
  ): CultNetTransportProfile["transports"][number]["channels"][number] => {
    const value: CultNetTransportProfile["transports"][number]["channels"][number] = {
      channelId,
      delivery,
      ordering,
    };
    if (options.maxPayloadBytes !== undefined) {
      value.maxPayloadBytes = options.maxPayloadBytes;
    }
    if (options.maxFragmentBytes !== undefined) {
      value.maxFragmentBytes = options.maxFragmentBytes;
    }
    if (options.maxPendingReliablePackets !== undefined) {
      value.maxPendingReliablePackets = options.maxPendingReliablePackets;
    }
    return value;
  };

  const transport: CultNetTransportProfile["transports"][number] = {
    transportId: options.transportId ?? "rudp",
    protocol: "rudp",
    wireContracts: ["cultnet.schema.v0"],
    reconnectPolicy: options.reconnectPolicy ?? createCultNetReconnectPolicy(),
    channels: [
      channel("schema", "reliable", "ordered"),
      channel("latest", "unreliable", "sequenced"),
      channel("realtime", "unreliable", "unordered"),
    ],
  };
  if (options.host !== undefined) {
    transport.host = options.host;
  }
  if (options.port !== undefined) {
    transport.port = options.port;
  }

  return {
    schemaVersion: "cultnet.transport_profile.v0",
    runtimeId,
    transports: [transport],
  };
}

function channelOptions(channelId: string): { reliable: boolean; ordered: boolean; sequenced: boolean } {
  if (channelId === "schema") {
    return { reliable: true, ordered: true, sequenced: false };
  }
  if (channelId === "latest") {
    return { reliable: false, ordered: false, sequenced: true };
  }
  return { reliable: false, ordered: false, sequenced: false };
}

export function encodeRudpPacket(packet: CultNetRudpPacket): Uint8Array {
  const channelId = new TextEncoder().encode(packet.channelId);
  if (channelId.length > MAX_CHANNEL_ID_BYTES) {
    throw new Error("CultNet RUDP channel id cannot exceed 255 UTF-8 bytes.");
  }

  const payload = packet.payload ?? new Uint8Array();
  const headerBytes = RUDP_FIXED_HEADER_BYTES + channelId.length;
  const wire = new Uint8Array(headerBytes + payload.length);
  const view = new DataView(wire.buffer, wire.byteOffset, wire.byteLength);
  wire.set(RUDP_MAGIC, 0);
  view.setUint8(4, RUDP_VERSION);
  view.setUint8(5, packetTypeToCode[packet.packetType]);
  view.setUint8(6, encodeFlags(packet));
  view.setUint8(7, headerBytes);
  view.setUint32(8, toUint32(packet.connectionId, "connectionId"), false);
  view.setUint32(12, toUint32(packet.sequence, "sequence"), false);
  view.setUint32(16, toUint32(packet.ack, "ack"), false);
  view.setUint32(20, toUint32(packet.ackMask, "ackMask"), false);
  view.setUint16(24, toUint16(packet.fragmentId ?? 0, "fragmentId"), false);
  view.setUint16(26, toUint16(packet.fragmentIndex ?? 0, "fragmentIndex"), false);
  view.setUint16(28, toUint16(packet.fragmentCount ?? 0, "fragmentCount"), false);
  view.setUint32(30, toUint32(payload.length, "payload length"), false);
  view.setUint8(34, channelId.length);
  view.setUint8(35, 0);
  wire.set(channelId, RUDP_FIXED_HEADER_BYTES);
  wire.set(payload, headerBytes);
  return wire;
}

export function decodeRudpPacket(wire: Uint8Array): CultNetRudpPacket {
  if (wire.length < RUDP_FIXED_HEADER_BYTES) {
    throw new Error("CultNet RUDP packet is shorter than the fixed header.");
  }

  const view = new DataView(wire.buffer, wire.byteOffset, wire.byteLength);
  for (let index = 0; index < RUDP_MAGIC.length; index += 1) {
    if (view.getUint8(index) !== RUDP_MAGIC[index]) {
      throw new Error("CultNet RUDP packet has the wrong magic.");
    }
  }

  const version = view.getUint8(4);
  if (version !== RUDP_VERSION) {
    throw new Error(`Unsupported CultNet RUDP packet version ${version}.`);
  }

  const packetType = packetTypeFromCode.get(view.getUint8(5));
  if (!packetType) {
    throw new Error(`Unsupported CultNet RUDP packet type ${view.getUint8(5)}.`);
  }

  const headerBytes = view.getUint8(7);
  const channelIdLength = view.getUint8(34);
  if (headerBytes !== RUDP_FIXED_HEADER_BYTES + channelIdLength) {
    throw new Error("CultNet RUDP packet header length does not match the channel id length.");
  }

  const payloadLength = view.getUint32(30, false);
  if (wire.length !== headerBytes + payloadLength) {
    throw new Error("CultNet RUDP packet payload length does not match the packet size.");
  }

  const flags = view.getUint8(6);
  return {
    packetType,
    connectionId: view.getUint32(8, false),
    sequence: view.getUint32(12, false),
    ack: view.getUint32(16, false),
    ackMask: view.getUint32(20, false),
    fragmentId: view.getUint16(24, false),
    fragmentIndex: view.getUint16(26, false),
    fragmentCount: view.getUint16(28, false),
    channelId: new TextDecoder().decode(wire.subarray(RUDP_FIXED_HEADER_BYTES, headerBytes)),
    reliable: (flags & 0b0000_0001) !== 0,
    ordered: (flags & 0b0000_0010) !== 0,
    sequenced: (flags & 0b0000_0100) !== 0,
    payload: wire.subarray(headerBytes),
  };
}

function encodeFlags(packet: CultNetRudpPacket): number {
  return (
    (packet.reliable ? 0b0000_0001 : 0) |
    (packet.ordered ? 0b0000_0010 : 0) |
    (packet.sequenced ? 0b0000_0100 : 0) |
    ((packet.fragmentCount ?? 0) > 0 ? 0b0000_1000 : 0)
  );
}

function toUint32(value: number, fieldName: string): number {
  if (!Number.isInteger(value) || value < 0 || value > 0xffffffff) {
    throw new Error(`CultNet RUDP ${fieldName} must fit in uint32.`);
  }

  return value;
}

function toUint16(value: number, fieldName: string): number {
  if (!Number.isInteger(value) || value < 0 || value > 0xffff) {
    throw new Error(`CultNet RUDP ${fieldName} must fit in uint16.`);
  }

  return value;
}
