import { randomInt } from "node:crypto";
import { EventEmitter } from "node:events";
import { type RemoteInfo, type Socket } from "node:dgram";

import type { CultNetTransportProfile } from "./contracts";
import {
  CultNetReconnectController,
  createCultNetReconnectPolicy,
  type CultNetReconnectDecision,
  type CultNetReconnectPolicy,
  type CultNetTransportConnection,
  type CultNetTransportFrame,
  type CultNetTransportStats,
} from "./transport";

const RUDP_MAGIC = [0x43, 0x4e, 0x52, 0x30] as const; // CNR0
const RUDP_VERSION = 0;
const RUDP_FIXED_HEADER_BYTES = 36;
const MAX_CHANNEL_ID_BYTES = 255;
export const CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS = 32;
const RUDP_RECEIVED_SEQUENCE_WINDOW = 4_096;
/// How long a client keeps retransmitting a Connect nobody answered before it
/// abandons that attempt for a fresh one. A Connect the server answers with an
/// Ack, never an Accept, is one the server judged stale (see `acceptConnect`);
/// retransmitting the same sequence would never change its mind.
const RUDP_CONNECT_ATTEMPT_MS = 3_000;

/// A sequence drawn from a secure source in [floor, 2^31). The Connect's
/// sequence is what tells a peer whether a Connect repeats one it already
/// accepted or starts a new session, so two sessions must not share one by
/// default. A floor above the range draws nothing new: the caller keeps its own
/// sequence.
function drawSequence(floor: number): number {
  const low = Math.max(1, floor);
  return low >= 2 ** 31 ? low : randomInt(low, 2 ** 31);
}

/// Whether `sequence` is at or before `mark` in serial order, within the
/// receive window. The compare is modular, so it holds across the wrap of the
/// 32-bit space; a sequence further back than the window is the duplicate
/// test's below-window clause, and one ahead of the mark is never before it.
function atOrBefore(sequence: number, mark: number): boolean {
  return ((mark - sequence) >>> 0) < RUDP_RECEIVED_SEQUENCE_WINDOW;
}

function packetAcknowledges(packet: CultNetRudpPacket, sequence: number): boolean {
  if (packet.ack === sequence) return true;
  for (let bit = 0; bit < 32; bit += 1) {
    if ((packet.ackMask & (1 << bit)) !== 0 && packet.ack - bit - 1 === sequence) return true;
  }
  return false;
}

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
  readyToSend?: CultNetRudpPacket[];
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

export interface CultNetRudpReconnectTransport extends EventEmitter {
  connect(payload?: Uint8Array): void;
  close(): void;
}

export interface CultNetRudpReconnectLoopOptions {
  reconnectPolicy?: CultNetReconnectPolicy;
  createTransport: () => CultNetRudpReconnectTransport;
  connectPayload?: Uint8Array;
  nowMs?: () => number;
  jitterMs?: () => number;
  setTimer?: (callback: () => void, delayMs: number) => unknown;
  clearTimer?: (handle: unknown) => void;
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
  readonly #nextSequencedByChannel = new Map<string, number>();
  #nextFragmentId = 1;
  #connected = false;
  /// Advances every time a generation ends. Everything issued in a generation
  /// (writes, flushes) belongs to it and dies with it.
  #generation = 0;
  /// True from the end of a generation until the next Connect or Accept begins
  /// one. A flush started in that interval has no live generation to wait on.
  #ended = false;
  /// The sequence of the Connect that started the current generation: sent by
  /// this side, or accepted from the peer. A Connect repeats exactly when the
  /// session is connected and the Connect carries this sequence.
  #connectSequence: number | undefined;
  /// A Connect this side sent is unanswered. Only then is an Accept honoured.
  #awaitingAccept = false;
  /// When the unanswered Connect first went out; `dueResends` abandons it for a
  /// fresh attempt once `RUDP_CONNECT_ATTEMPT_MS` has passed.
  #connectStartedAtMs = 0;
  readonly #maxPendingReliablePackets: number | undefined;
  #lastReceivedAtMs: number | undefined;
  #highestReceivedSequence: number | undefined;
  readonly #receivedSequences = new Set<number>();
  readonly #latestSequencedByChannel = new Map<string, number>();
  readonly #pendingReliable = new Map<number, PendingReliablePacket>();
  readonly #queuedReliable: CultNetRudpPacket[] = [];
  /// Every reliable sequence up to and including this one has been received
  /// since the peer state was last reset. Only the handshake seeds it: the
  /// peer's Connect on the accepting side, the peer's Accept on the connecting
  /// side. Reliable data that arrives before that is refused.
  #receivedThrough: number | undefined;
  /// Ordered frames received but not yet deliverable, keyed by first sequence.
  /// A frame is held for exactly one reason: a reliable sequence below it has
  /// not arrived.
  readonly #orderedHeld = new Map<number, CultNetRudpDeliveredFrame>();
  readonly #fragmentBuffers = new Map<string, FragmentBuffer>();
  /// Matches cultnet-rs's `max_pending_fragment_sets`. A fragment set is only
  /// removed on successful reassembly, so a set that loses one fragment is
  /// stranded for the life of the session. Without a bound the map grows
  /// forever under any loss on a fragmenting channel.
  #maxPendingFragmentSets = 64;
  #fragmentSetsEvicted = 0;

  constructor(options: CultNetRudpSessionOptions) {
    this.connectionId = toUint32(options.connectionId, "connectionId");
    this.#nextSequence = toUint32(options.initialSequence ?? drawSequence(1), "initialSequence");
    if (this.#nextSequence === 0xffff_ffff) {
      throw new Error("RUDP initialSequence must leave room for a reliable packet.");
    }
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

  get queuedReliablePacketCount(): number {
    return this.#queuedReliable.length;
  }

  get outstandingReliablePacketCount(): number {
    return this.#pendingReliable.size + this.#queuedReliable.length;
  }

  /// Incomplete fragment sets dropped to stay within the pending bound.
  /// Non-zero means the peer is losing fragments: the receiver is still
  /// serving, and something upstream is not.
  get fragmentSetsEvicted(): number {
    return this.#fragmentSetsEvicted;
  }

  get pendingFragmentSetCount(): number {
    return this.#fragmentBuffers.size;
  }

  get lastReceivedAtMs(): number | undefined {
    return this.#lastReceivedAtMs;
  }

  get generation(): number {
    return this.#generation;
  }

  get ended(): boolean {
    return this.#ended;
  }

  /// The one way a session generation ends. The session stops being connected
  /// and what it still owed the peer dies with it: a write not yet acknowledged
  /// is dropped, so no later session retransmits it or credits an ack to it.
  /// What was learned from the peer is not touched: the peer may not know the
  /// session ended, and forgetting what it sent would let its retransmits be
  /// delivered twice.
  #endSession(): void {
    this.#connected = false;
    this.#ended = true;
    this.#awaitingAccept = false;
    this.#generation += 1;
    this.#pendingReliable.clear();
    this.#queuedReliable.splice(0);
  }

  /// Ends the current generation and forgets everything learned from the peer;
  /// sequence numbers already issued stay issued.
  resetPeerState(): void {
    this.#endSession();
    this.#lastReceivedAtMs = undefined;
    this.#highestReceivedSequence = undefined;
    this.#receivedSequences.clear();
    this.#nextSequencedByChannel.clear();
    this.#latestSequencedByChannel.clear();
    this.#receivedThrough = undefined;
    this.#orderedHeld.clear();
    this.#fragmentBuffers.clear();
    this.#fragmentSetsEvicted = 0;
  }

  createConnect(nowMs = 0, payload = new Uint8Array()): CultNetRudpPacket {
    // A session that has had a peer starts a new generation: nothing it learned
    // from that peer describes the one this Connect reaches, and what it still
    // owed that peer no longer takes room in the queue.
    if (this.#connectSequence !== undefined) {
      this.resetPeerState();
    }
    this.#ensureReliableCapacity(1);
    this.#ended = false;
    const packet = this.#createPacket({
      packetType: "connect",
      channelId: "control",
      reliable: true,
      ordered: true,
      payload,
    });
    this.#connectSequence = packet.sequence;
    this.#awaitingAccept = true;
    this.#connectStartedAtMs = nowMs;
    this.#trackReliable(packet, nowMs);
    return packet;
  }

  /// Whether `packet` is a Connect this session's current generation already
  /// owns: a retransmit of the Connect that started it (the session is
  /// connected and the sequence is that Connect's), or a stale copy of an
  /// earlier attempt by the same client (a sequence before it, within the
  /// receive window). Servers that keep one session per peer ask this to tell a
  /// Connect that starts a new session from one that does not; anything else
  /// that reaches `acceptConnect` starts a new generation.
  connectRepeats(packet: CultNetRudpPacket): boolean {
    return packet.packetType === "connect"
      && this.#connected
      && this.#connectSequence !== undefined
      && (this.#connectSequence === packet.sequence
        || this.#connectIsStale(packet.sequence, this.#connectSequence));
  }

  /// A Connect that precedes the current generation's within the receive window
  /// is the client's earlier attempt, delayed in the network: a client that
  /// retried never sends a lower sequence again. Restarting on it would strand
  /// the client, which honours only the Accept for its newest Connect. The rule
  /// is TCP's answer to a delayed SYN (RFC 5961's challenge ACK): keep the
  /// connection and answer with an Ack. A restarted client whose random initial
  /// sequence lands in this window is answered the same way and abandons the
  /// attempt for a fresh draw (`RUDP_CONNECT_ATTEMPT_MS`).
  #connectIsStale(sequence: number, current: number): boolean {
    return sequence !== current && atOrBefore(sequence, current);
  }

  /// Answers a Connect. A repeat of the accepted one queues nothing, so a
  /// Connect storm cannot grow the reliable queue: the reply is the Accept
  /// still awaiting acknowledgement, or an Ack once it was acknowledged. A
  /// stale copy of an earlier attempt gets the same reply and changes nothing
  /// else: it is not evidence the peer is alive. Any other Connect ends the
  /// current generation, forgets the peer and accepts a new one.
  acceptConnect(packet: CultNetRudpPacket, nowMs = 0, payload = new Uint8Array()): CultNetRudpPacket {
    this.#requireConnection(packet);
    if (packet.packetType !== "connect") {
      throw new Error(`Expected RUDP connect packet, got ${packet.packetType}.`);
    }

    if (this.connectRepeats(packet)) {
      if (this.#connectSequence === packet.sequence) {
        this.#applyAcknowledgements(packet);
        this.#rememberReceived(packet.sequence);
        this.#lastReceivedAtMs = nowMs;
      }
      return this.#pendingAcceptForResend(nowMs) ?? this.createAck();
    }
    this.resetPeerState();
    this.#ensureReliableCapacity(1);
    this.#seedReceived(packet.sequence);
    this.#lastReceivedAtMs = nowMs;
    this.#connectSequence = packet.sequence;
    this.#connected = true;
    this.#ended = false;
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

  /// The Accept still awaiting acknowledgement, resent.
  #pendingAcceptForResend(nowMs: number): CultNetRudpPacket | undefined {
    const pendingAccept = [...this.#pendingReliable.values()]
      .find(pending => pending.packet.packetType === "accept");
    if (!pendingAccept) {
      return undefined;
    }
    pendingAccept.lastSentAtMs = nowMs;
    return {
      ...pendingAccept.packet,
      payload: new Uint8Array(pendingAccept.packet.payload ?? new Uint8Array()),
    };
  }

  send(
    channelId: string,
    payload: Uint8Array,
    options: { reliable?: boolean; ordered?: boolean; sequenced?: boolean; nowMs?: number } = {},
  ): CultNetRudpPacket {
    if (options.reliable && this.#pendingReliable.size >= CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS) {
      throw new Error("RUDP reliable send window is full; receive acknowledgements before sending.");
    }
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
    if (options.ordered && !options.reliable) {
      throw new Error("RUDP ordered delivery requires reliability.");
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
      return packet.reliable
        ? this.#admitReliablePackets([packet], options.nowMs ?? 0)
        : [packet];
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
      packets.push(packet);
    }
    return options.reliable
      ? this.#admitReliablePackets(packets, options.nowMs ?? 0)
      : packets;
  }

  receive(packet: CultNetRudpPacket, nowMs = 0): CultNetRudpReceiveResult {
    this.#requireConnection(packet);
    // An Accept counts only while this side's Connect is unanswered and the
    // Accept names it. A late or duplicate one, or one from an earlier
    // generation, must not seed the watermark or revive an ended session.
    const honoursAccept = packet.packetType === "accept"
      && this.#awaitingAccept
      && this.#connectSequence !== undefined
      && packetAcknowledges(packet, this.#connectSequence);
    if (packet.packetType === "accept" && !honoursAccept) {
      return { delivered: [], readyToSend: [] };
    }
    this.#applyAcknowledgements(packet);
    const readyToSend = this.#promoteQueuedReliable(nowMs);
    this.#lastReceivedAtMs = nowMs;

    if (honoursAccept) {
      this.#awaitingAccept = false;
      this.#seedReceived(packet.sequence);
      this.#connected = true;
      return { delivered: [], readyToSend };
    }

    if (packet.packetType === "ping") {
      return {
        delivered: [],
        readyToSend,
        reply: this.#createPacket({
          packetType: "pong",
          channelId: "control",
          payload: packet.payload ?? new Uint8Array(),
        }),
      };
    }

    if (packet.packetType === "ack" || packet.packetType === "pong") {
      return {
        delivered: [],
        readyToSend,
        pong: packet.packetType === "pong",
        pongPayload: packet.packetType === "pong" ? packet.payload ?? new Uint8Array() : undefined,
      };
    }

    if (packet.packetType === "disconnect") {
      this.#endSession();
      return {
        delivered: [],
        readyToSend,
        disconnected: true,
        disconnectReason: packet.payload ?? new Uint8Array(),
      };
    }

    if (packet.packetType !== "data") {
      return { delivered: [], readyToSend };
    }

    // Reliable data before the handshake has seeded the watermark has no place
    // in the order: refuse it unremembered, so it is not acknowledged and the
    // sender retransmits it once the handshake is done.
    if ((packet.reliable ?? false) && this.#receivedThrough === undefined) {
      return { delivered: [], readyToSend };
    }

    const isDuplicate = (packet.reliable ?? false) && this.#wasReceived(packet.sequence);
    if (packet.reliable ?? false) this.#rememberReceived(packet.sequence);
    if (isDuplicate) {
      return { delivered: [], readyToSend };
    }

    const delivered: CultNetRudpDeliveredFrame[] = [];
    const reassembled = this.#reassemble(packet);
    if (reassembled) {
      if (reassembled.ordered) {
        this.#orderedHeld.set(reassembled.frame.sequence, reassembled.frame);
      } else if (!(packet.sequenced ?? false)) {
        delivered.push(reassembled.frame);
      } else {
        const newestSequence = reassembled.nextSequence - 1;
        const latestSequence = this.#latestSequencedByChannel.get(reassembled.frame.channelId);
        if (latestSequence === undefined || newestSequence > latestSequence) {
          this.#latestSequencedByChannel.set(reassembled.frame.channelId, newestSequence);
          delivered.push(reassembled.frame);
        }
      }
    }

    // Any reliable packet may have advanced the watermark, a fragment or an
    // unordered frame as much as an ordered one, so the drain runs after all of
    // them.
    delivered.push(...this.#drainOrdered());
    return { delivered, readyToSend };
  }

  createAck(): CultNetRudpPacket {
    const { ack, ackMask } = this.#ackState();
    return {
      packetType: "ack",
      connectionId: this.connectionId,
      sequence: 0,
      ack,
      ackMask,
      channelId: "control",
      reliable: false,
      ordered: false,
      sequenced: false,
      fragmentId: 0,
      fragmentIndex: 0,
      fragmentCount: 0,
      payload: new Uint8Array(),
    };
  }

  createAckFor(sequence: number): CultNetRudpPacket {
    return {
      packetType: "ack",
      connectionId: this.connectionId,
      sequence: 0,
      ack: toUint32(sequence, "ack sequence"),
      ackMask: 0,
      channelId: "control",
      reliable: false,
      ordered: false,
      sequenced: false,
      fragmentId: 0,
      fragmentIndex: 0,
      fragmentCount: 0,
      payload: new Uint8Array(),
    };
  }

  createAckForReceived(sequence: number): CultNetRudpPacket {
    // What `receive` refused is not acknowledged by name (the ack carries only
    // what was received), so its sender retransmits it.
    const receivedSequence = toUint32(sequence, "received sequence");
    if (!this.#wasReceived(receivedSequence)) {
      return this.createAck();
    }
    const { ack } = this.#ackState();
    return ack >= receivedSequence && ack - receivedSequence <= 32
      ? this.createAck()
      : this.createAckFor(receivedSequence);
  }

  createPing(payload = new Uint8Array()): CultNetRudpPacket {
    return this.#createPacket({
      packetType: "ping",
      channelId: "control",
      payload,
    });
  }

  createDisconnect(reason = new Uint8Array()): CultNetRudpPacket {
    this.#endSession();
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
    this.#endSession();
    return true;
  }

  dueResends(nowMs: number): CultNetRudpPacket[] {
    const fresh = this.#abandonUnansweredConnect(nowMs);
    if (fresh) {
      return [fresh];
    }
    const due: CultNetRudpPacket[] = [];
    for (const pending of this.#pendingReliable.values()) {
      if (nowMs - pending.lastSentAtMs >= this.resendDelayMs) {
        pending.lastSentAtMs = nowMs;
        due.push({ ...pending.packet });
      }
    }
    return due.sort((left, right) => left.sequence - right.sequence);
  }

  /// A Connect unanswered for `RUDP_CONNECT_ATTEMPT_MS` is replaced, not
  /// retransmitted further, by a Connect with a newly drawn sequence and the
  /// same payload. Nothing was ever sent in an unanswered generation, so no
  /// sequence issued so far is owed; the draw stays above them, so a frame the
  /// peer still remembers from an earlier generation of this session stays at or
  /// before the new Connect.
  #abandonUnansweredConnect(nowMs: number): CultNetRudpPacket | undefined {
    if (!this.#awaitingAccept || nowMs - this.#connectStartedAtMs < RUDP_CONNECT_ATTEMPT_MS) {
      return undefined;
    }
    const pending = this.#connectSequence === undefined
      ? undefined
      : this.#pendingReliable.get(this.#connectSequence);
    if (!pending) {
      return undefined;
    }
    const payload = new Uint8Array(pending.packet.payload ?? []);
    this.#nextSequence = drawSequence(this.#nextSequence);
    return this.createConnect(nowMs, payload);
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
    let sequence = 0;
    if (packet.reliable ?? false) {
      sequence = this.#nextSequence;
      this.#nextSequence = toUint32(this.#nextSequence + 1, "reliable sequence");
    } else if (packet.sequenced ?? false) {
      sequence = this.#nextSequencedByChannel.get(packet.channelId) ?? 1;
      this.#nextSequencedByChannel.set(
        packet.channelId,
        toUint32(sequence + 1, "sequenced channel sequence"),
      );
    }
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

  #admitReliablePackets(packets: CultNetRudpPacket[], nowMs: number): CultNetRudpPacket[] {
    const available = Math.max(0, CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS - this.#pendingReliable.size);
    const ready = packets.slice(0, available);
    for (const packet of ready) this.#trackReliable(packet, nowMs);
    this.#queuedReliable.push(...packets.slice(available));
    return ready;
  }

  #promoteQueuedReliable(nowMs: number): CultNetRudpPacket[] {
    const available = Math.max(0, CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS - this.#pendingReliable.size);
    const ready = this.#queuedReliable.splice(0, available);
    for (const packet of ready) this.#trackReliable(packet, nowMs);
    return ready;
  }

  #ensureReliableCapacity(packetCount: number): void {
    if (packetCount === 0 || this.#maxPendingReliablePackets === undefined) {
      return;
    }
    if (this.outstandingReliablePacketCount + packetCount > this.#maxPendingReliablePackets) {
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

  /// The handshake's one act on the watermark: the peer's Connect or Accept is
  /// the first sequence of the session, whatever else it has sent.
  #seedReceived(sequence: number): void {
    this.#receivedThrough = sequence;
    this.#rememberReceived(sequence);
  }

  /// The one duplicate test: true for a sequence in the window that was
  /// received, for one below the window, and for one at or before the
  /// watermark. The watermark starts at the handshake's seed, so a frame the
  /// peer sent in an earlier generation (every sequence it issued is below the
  /// Connect that began this one) is a duplicate of something already delivered
  /// and is acknowledged, never delivered again.
  #wasReceived(sequence: number): boolean {
    return this.#receivedSequences.has(sequence)
      || (this.#highestReceivedSequence !== undefined
        && sequence < this.#highestReceivedSequence
        && this.#highestReceivedSequence - sequence >= RUDP_RECEIVED_SEQUENCE_WINDOW)
      || (this.#receivedThrough !== undefined && atOrBefore(sequence, this.#receivedThrough));
  }

  #rememberReceived(sequence: number): void {
    this.#receivedSequences.add(sequence);
    if (this.#receivedThrough !== undefined) {
      let through = this.#receivedThrough;
      while (this.#receivedSequences.has(through + 1)) through += 1;
      this.#receivedThrough = through;
    }
    if (this.#highestReceivedSequence === undefined || sequence > this.#highestReceivedSequence) {
      this.#highestReceivedSequence = sequence;
    }
    if (this.#receivedSequences.size > RUDP_RECEIVED_SEQUENCE_WINDOW) {
      const keepFrom = Math.max(0, (this.#highestReceivedSequence ?? sequence) - RUDP_RECEIVED_SEQUENCE_WINDOW + 1);
      for (const received of this.#receivedSequences) {
        if (received < keepFrom) this.#receivedSequences.delete(received);
      }
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
      // Evict the oldest stranded set rather than refusing the payload. A
      // receiver that stops accepting fragmented traffic after a bounded
      // number of losses is a denial of service delivered by the network;
      // dropping the least recent incomplete set costs one payload that was
      // already incomplete. Map iteration is insertion-ordered, so the first
      // key is the oldest.
      while (this.#fragmentBuffers.size >= this.#maxPendingFragmentSets) {
        const oldest = this.#fragmentBuffers.keys().next();
        if (oldest.done) {
          break;
        }
        this.#fragmentBuffers.delete(oldest.value);
        this.#fragmentSetsEvicted += 1;
      }
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

  /// Delivers, in sequence order, every held ordered frame whose first sequence
  /// is at most one past the watermark. Ordered delivery has one owner: the
  /// watermark. A frame this call does not deliver waits for the sequence below
  /// it, and nothing else releases it.
  #drainOrdered(): CultNetRudpDeliveredFrame[] {
    if (this.#receivedThrough === undefined) return [];
    const releasable = [...this.#orderedHeld.keys()]
      .filter((sequence) => sequence <= this.#receivedThrough! + 1)
      .sort((left, right) => left - right);
    return releasable.map((sequence) => {
      const frame = this.#orderedHeld.get(sequence)!;
      this.#orderedHeld.delete(sequence);
      return frame;
    });
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
  // Why the last generation ended. A report only: the session owns whether it
  // ended.
  #endedReason: Uint8Array | undefined;
  readonly #stats: CultNetTransportStats = {
    bytesReceived: 0,
    bytesSent: 0,
    framesReceived: 0,
    framesSent: 0,
    packetsDropped: 0,
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

  get outstandingReliablePacketCount(): number {
    return this.#session.outstandingReliablePacketCount;
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

  async flush(timeoutMs = 30_000): Promise<void> {
    const deadline = Date.now() + Math.max(0, timeoutMs);
    // A flush belongs to the generation it started in: whatever ends that
    // generation, and whatever begins after it (a reconnect from a disconnect
    // listener included), the writes being waited on are gone.
    const generation = this.#session.generation;
    for (;;) {
      // An ended session forgot its unacknowledged writes; reporting them
      // flushed would be a lie.
      if (this.#session.ended || this.#session.generation !== generation) {
        throw new Error(
          `RUDP session ended before its reliable writes were acknowledged: ${Buffer.from(this.#endedReason ?? new Uint8Array()).toString("utf8")}`,
        );
      }
      if (this.#session.outstandingReliablePacketCount === 0) {
        return;
      }
      if (this.#closed) {
        throw new Error("RUDP transport closed before reliable packets were acknowledged.");
      }
      if (Date.now() >= deadline) {
        throw new Error(
          `RUDP reliable flush timed out with ${this.#session.outstandingReliablePacketCount} packets outstanding.`,
        );
      }
      await new Promise(resolve => setTimeout(resolve, 5));
    }
  }

  ping(payload = new Uint8Array()): void {
    this.#sendPacket(this.#session.createPing(payload));
  }

  checkTimeout(timeoutMs: number, nowMs = Date.now()): boolean {
    const timedOut = this.#session.checkTimeout(nowMs, timeoutMs);
    if (timedOut) {
      this.#endedReason = Buffer.from("session timed out", "utf8");
      this.emit("timeout");
      this.emit("close");
    }
    return timedOut;
  }

  close(): void {
    clearInterval(this.#resendTimer);
    if (!this.#closed) {
      if (this.#session.connected && this.#remoteHost && this.#remotePort !== undefined) {
        try {
          this.#sendPacket(this.#session.createDisconnect());
        } catch {
          // Best-effort shutdown: close must still release the UDP socket.
        }
      }
      this.#closed = true;
      this.#socket.close();
    }
  }

  #endRefusedSession(): void {
    const reason = Buffer.from("session refused a packet", "utf8");
    // The goodbye is built after the reset, or its ack field would acknowledge
    // the very frame the session refused.
    this.#session.resetPeerState();
    this.#endedReason = reason;
    try {
      this.#sendPacket(this.#session.createDisconnect(reason));
    } catch {
      // Best-effort: the session ends whether or not the peer hears it.
    }
    if (this.#mode === "server") {
      // Only a Connect can claim the endpoint again.
      this.#remoteHost = undefined;
      this.#remotePort = undefined;
    }
    this.emit("disconnect", { reason });
    this.emit("close");
  }

  #receiveDatagram(wire: Buffer, remote: RemoteInfo): void {
    this.#stats.bytesReceived += wire.length;
    // What a datagram carries is the sender's business, not a fault of this
    // process: a malformed frame, another session's connection id and a packet
    // the session refuses are dropped and counted, never emitted as "error"
    // (an "error" with no listener throws and ends the process). The id is
    // checked before the peer endpoint or session state is touched, so a stray
    // Connect can neither move the endpoint nor reset the session.
    let packet: CultNetRudpPacket;
    try {
      packet = decodeRudpPacket(wire);
    } catch {
      this.#stats.packetsDropped += 1;
      return;
    }
    if (packet.connectionId !== this.#session.connectionId) {
      this.#stats.packetsDropped += 1;
      return;
    }

    // A Connect from another endpoint is a new client, whatever sequence it
    // carries: the endpoint moves and the Connect starts a new generation. Only
    // a Connect from the peer's own endpoint can be its repeat.
    let connectFromNewEndpoint = false;
    if (!this.#remoteHost || this.#remotePort === undefined) {
      if (this.#mode === "server" && packet.packetType !== "connect") {
        this.#stats.packetsDropped += 1;
        return;
      }
      this.#remoteHost = remote.address;
      this.#remotePort = remote.port;
    } else if (remote.address !== this.#remoteHost || remote.port !== this.#remotePort) {
      if (this.#mode === "server" && packet.packetType === "connect") {
        this.#remoteHost = remote.address;
        this.#remotePort = remote.port;
        connectFromNewEndpoint = true;
      } else {
        this.#stats.packetsDropped += 1;
        return;
      }
    }

    if (this.#mode === "server" && packet.packetType === "connect") {
      // The session decides whether this Connect repeats the one it accepted; a
      // repeat is answered with the Accept owed, anything else replaces the
      // peer and the old session's writes die with it. A timed-out session
      // already ended its generation; any other predecessor ends here.
      if ((connectFromNewEndpoint || !this.#session.connectRepeats(packet)) && !this.#session.ended) {
        this.#endedReason = Buffer.from("replaced by a new Connect", "utf8");
      }
      if (connectFromNewEndpoint) {
        this.#session.resetPeerState();
      }
      let accept: CultNetRudpPacket;
      try {
        accept = this.#session.acceptConnect(packet, Date.now());
      } catch {
        this.#stats.packetsDropped += 1;
        return;
      }
      this.#sendPacket(accept);
      return;
    }

    let result: ReturnType<CultNetRudpSession["receive"]>;
    try {
      result = this.#session.receive(packet, Date.now());
    } catch {
      // receive() has already recorded the packet's reliable sequence, so the
      // session cannot be kept: a retransmit would be acknowledged and the
      // frame silently lost. End it and tell the peer.
      this.#stats.packetsDropped += 1;
      this.#endRefusedSession();
      return;
    }
    try {
      if (result.reply) {
        this.#sendPacket(result.reply);
      }
      for (const ready of result.readyToSend ?? []) {
        this.#sendPacket(ready);
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
        this.#endedReason = result.disconnectReason ?? new Uint8Array();
        this.emit("disconnect", { reason: result.disconnectReason ?? new Uint8Array() });
        this.emit("close");
        return;
      }
      if (packet.packetType === "accept" || packet.packetType === "data" || result.delivered.length > 0) {
        this.#sendPacket(this.#session.createAckForReceived(packet.sequence));
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

export class CultNetRudpReconnectLoop extends EventEmitter {
  readonly reconnectController: CultNetReconnectController;
  readonly #createTransport: () => CultNetRudpReconnectTransport;
  readonly #connectPayload: Uint8Array;
  readonly #nowMs: () => number;
  readonly #jitterMs: () => number;
  readonly #setTimer: (callback: () => void, delayMs: number) => unknown;
  readonly #clearTimer: (handle: unknown) => void;
  #transport: CultNetRudpReconnectTransport | undefined;
  #timer: unknown;
  #stopped = true;

  constructor(options: CultNetRudpReconnectLoopOptions) {
    super();
    this.reconnectController = new CultNetReconnectController(options.reconnectPolicy ?? createCultNetReconnectPolicy());
    this.#createTransport = options.createTransport;
    this.#connectPayload = options.connectPayload ?? new Uint8Array();
    this.#nowMs = options.nowMs ?? Date.now;
    this.#jitterMs = options.jitterMs ?? (() => 0);
    this.#setTimer = options.setTimer ?? ((callback, delayMs) => setTimeout(callback, delayMs));
    this.#clearTimer = options.clearTimer ?? ((handle) => clearTimeout(handle as NodeJS.Timeout));
  }

  get transport(): CultNetRudpReconnectTransport | undefined {
    return this.#transport;
  }

  start(): CultNetRudpReconnectTransport {
    this.#stopped = false;
    this.reconnectController.reset();
    return this.#openTransport();
  }

  stop(): void {
    this.#stopped = true;
    if (this.#timer !== undefined) {
      this.#clearTimer(this.#timer);
      this.#timer = undefined;
    }
    const transport = this.#transport;
    this.#transport = undefined;
    transport?.close();
    this.reconnectController.reset();
  }

  markConnected(): void {
    this.reconnectController.reset();
  }

  #openTransport(): CultNetRudpReconnectTransport {
    const transport = this.#createTransport();
    this.#transport = transport;
    transport.once("close", () => {
      if (this.#transport === transport) {
        this.#transport = undefined;
      }
      this.#scheduleReconnect();
    });
    transport.on("error", (error) => this.emit("error", error));
    this.emit("transport", transport);
    try {
      transport.connect(this.#connectPayload);
    } catch (error) {
      this.emit("error", error instanceof Error ? error : new Error(String(error)));
      this.#scheduleReconnect();
    }
    return transport;
  }

  #scheduleReconnect(): void {
    if (this.#stopped || this.#timer !== undefined) {
      return;
    }
    const decision = this.reconnectController.recordFailure(this.#nowMs(), this.#jitterMs());
    this.emit("reconnectScheduled", decision satisfies CultNetReconnectDecision);
    if (!decision.shouldRetry) {
      this.emit("reconnectExhausted", decision);
      return;
    }
    this.#timer = this.#setTimer(() => {
      this.#timer = undefined;
      if (this.#stopped || !this.reconnectController.canAttempt(this.#nowMs())) {
        return;
      }
      this.emit("reconnecting", decision);
      this.#openTransport();
    }, decision.delayMs);
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
