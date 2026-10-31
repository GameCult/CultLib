import { EventEmitter } from "node:events";
import { type RemoteInfo, type Socket } from "node:dgram";

import type { CultNetTransportProfile } from "./contracts";
import {
  CultNetRudpSession,
  MAX_UDP4_DATAGRAM_BYTES,
  MAX_UDP6_DATAGRAM_BYTES,
  RUDP_FIXED_HEADER_BYTES,
  channelOptions,
  createRudpTransportProfile,
  decodeRudpPacket,
  encodeRudpPacket,
  isPermanentSendError,
  sendRudpDatagram,
  type CultNetRudpPacket,
} from "./rudp";
import type { CultNetReconnectPolicy, CultNetTransportFrame, CultNetTransportStats } from "./transport";

export interface CultNetRudpSocketServerOptions {
  runtimeId: string;
  /** A bound UDP socket. The server owns it from here: `close()` closes it. */
  socket: Socket;
  connectionId: number;
  /** The first local sequence of every peer session. Unset, each session draws its own. */
  initialSequence?: number;
  resendDelayMs?: number;
  resendPollMs?: number;
  transportId?: string;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
  maxPendingReliablePackets?: number;
  reconnectPolicy?: CultNetReconnectPolicy;
  /** A Connect from an endpoint beyond this many peers is dropped. Default 256. */
  maxPeers?: number;
  /**
   * A peer that has sent nothing for this long is ended and told so. Default 60 000;
   * `Infinity` keeps idle peers until they leave.
   */
  peerTimeoutMs?: number;
}

/** One remote endpoint's session. A peer that reconnects from the same endpoint is a new object. */
export interface CultNetRudpSocketServerPeer {
  readonly remoteHost: string;
  readonly remotePort: number;
  /** The exact bytes of the Connect that opened this peer's session. */
  readonly connectPayload: Uint8Array;
  readonly connected: boolean;
  readonly outstandingReliablePacketCount: number;
}

export interface CultNetRudpSocketServerFrame extends CultNetTransportFrame {
  peer: CultNetRudpSocketServerPeer;
}

type PeerEntry = {
  peer: CultNetRudpSocketServerPeer;
  session: CultNetRudpSession;
  key: string;
};

/** The goodbye for a session that refused a packet; built after the reset, or its ack would acknowledge the refused frame. */
function refusedGoodbye(session: CultNetRudpSession): CultNetRudpPacket {
  session.resetPeerState();
  return session.createDisconnect(utf8("session refused a packet"));
}

const utf8 = (text: string) => new TextEncoder().encode(text);

/**
 * One UDP socket serving many peers. Every remote address and port gets its own reliable
 * session, opened by its Connect: a second client never takes the first one's session, and
 * a client that reconnects from the same endpoint replaces only its own. The wire format is
 * the single-peer transport's; the shape matches the Rust `CultNetRudpServerHub` and the C#
 * `CultNetRudpSocketTransportServer`.
 *
 * Reply to a peer with `send(peer, ...)` on the peer a frame arrived from.
 *
 * Events: `peerConnected` (peer), `frame` ({ peer, channelId, payload }), `pong` ({ peer, payload }),
 * `peerDisconnected` ({ peer, reason }), `close`, `error`.
 */
export class CultNetRudpSocketTransportServer extends EventEmitter {
  readonly profile: CultNetTransportProfile;
  readonly #socket: Socket;
  readonly #options: CultNetRudpSocketServerOptions;
  readonly #entries = new Map<string, PeerEntry>();
  readonly #timer: NodeJS.Timeout;
  readonly #maxDatagramBytes: number;
  readonly #maxPeers: number;
  readonly #peerTimeoutMs: number;
  #closed = false;
  readonly #stats: CultNetTransportStats = {
    bytesReceived: 0,
    bytesSent: 0,
    framesReceived: 0,
    framesSent: 0,
    packetsDropped: 0,
    sendFailures: 0,
  };

  constructor(options: CultNetRudpSocketServerOptions) {
    super();
    this.#options = options;
    this.#socket = options.socket;
    this.#maxPeers = options.maxPeers ?? 256;
    this.#peerTimeoutMs = options.peerTimeoutMs ?? 60_000;
    if (!Number.isInteger(this.#maxPeers) || this.#maxPeers < 1) {
      throw new Error("RUDP server maxPeers must be a positive integer.");
    }
    const address = this.#socket.address();
    this.#maxDatagramBytes =
      typeof address !== "string" && String(address.family).endsWith("6") ? MAX_UDP6_DATAGRAM_BYTES : MAX_UDP4_DATAGRAM_BYTES;
    this.profile = createRudpTransportProfile(options.runtimeId, {
      transportId: options.transportId,
      host: typeof address === "string" ? undefined : address.address,
      port: typeof address === "string" ? undefined : address.port,
      maxPayloadBytes: options.maxPayloadBytes,
      maxFragmentBytes: options.maxFragmentBytes,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
      reconnectPolicy: options.reconnectPolicy,
    });

    this.#socket.on("message", (wire, remote) => this.#receiveDatagram(wire, remote));
    this.#socket.on("close", () => {
      if (this.#closed) return;
      this.#closed = true;
      clearInterval(this.#timer);
      this.#entries.clear();
      this.emit("close");
    });
    this.#socket.on("error", (error) => this.emit("error", error instanceof Error ? error : new Error(String(error))));
    this.#timer = setInterval(() => this.#tick(), options.resendPollMs ?? 25);
    this.#timer.unref?.();
  }

  /** The peers with a live session. */
  get peers(): CultNetRudpSocketServerPeer[] {
    return [...this.#entries.values()].map((entry) => entry.peer);
  }

  get stats(): CultNetTransportStats {
    return { ...this.#stats };
  }

  /** Sends to one peer. Throws when that peer's session has ended or was replaced. */
  send(peer: CultNetRudpSocketServerPeer, channelId: string, payload: Uint8Array): void {
    const entry = this.#liveEntry(peer);
    const largestPayload =
      this.#options.maxFragmentBytes === undefined ? payload.byteLength : Math.min(payload.byteLength, this.#options.maxFragmentBytes);
    if (RUDP_FIXED_HEADER_BYTES + Buffer.byteLength(channelId, "utf8") + largestPayload > this.#maxDatagramBytes) {
      throw Object.assign(new Error("RUDP datagram is larger than UDP can carry."), { code: "EMSGSIZE" });
    }
    const packets = entry.session.sendMany(channelId, payload, {
      ...channelOptions(channelId),
      nowMs: Date.now(),
      maxFragmentBytes: this.#options.maxFragmentBytes,
    });
    for (const packet of packets) {
      const thrown = this.#transmit(entry, packet);
      if (thrown) throw thrown;
    }
    this.#stats.framesSent += 1;
  }

  /** Ends one peer's session, telling it why. */
  disconnect(peer: CultNetRudpSocketServerPeer, reason: Uint8Array = new Uint8Array()): void {
    const entry = this.#liveEntry(peer);
    this.#endPeer(entry, entry.session.createDisconnect(Uint8Array.from(reason)));
  }

  /** Ends every peer and releases the socket. */
  close(): void {
    if (this.#closed) return;
    clearInterval(this.#timer);
    for (const entry of [...this.#entries.values()]) {
      this.#endPeer(entry, entry.session.createDisconnect(utf8("server closed")));
    }
    this.#closed = true;
    this.#socket.close();
  }

  #liveEntry(peer: CultNetRudpSocketServerPeer): PeerEntry {
    const entry = this.#entries.get(`${peer.remoteHost}:${peer.remotePort}`);
    if (!entry || entry.peer !== peer) {
      throw new Error("RUDP peer is not connected.");
    }
    return entry;
  }

  /** Removes the peer, tells the caller, and says goodbye on the wire. Idempotent per entry. */
  #endPeer(entry: PeerEntry, goodbye: CultNetRudpPacket): void {
    if (this.#entries.get(entry.key) !== entry) return;
    this.#entries.delete(entry.key);
    this.emit("peerDisconnected", { peer: entry.peer, reason: goodbye.payload });
    // Best-effort: the session is over whether or not the peer hears it.
    const wire = encodeRudpPacket(goodbye);
    this.#stats.bytesSent += wire.length;
    sendRudpDatagram(this.#socket, wire, entry.peer.remotePort, entry.peer.remoteHost, () => {});
  }

  #receiveDatagram(wire: Buffer, remote: RemoteInfo): void {
    this.#stats.bytesReceived += wire.length;
    // What a datagram carries is the sender's business: a malformed frame, another
    // session's connection id and a sender no Connect admitted are dropped and counted.
    let packet: CultNetRudpPacket;
    try {
      packet = decodeRudpPacket(wire);
    } catch {
      this.#stats.packetsDropped += 1;
      return;
    }
    if (packet.connectionId !== this.#options.connectionId) {
      this.#stats.packetsDropped += 1;
      return;
    }
    const key = `${remote.address}:${remote.port}`;
    const existing = this.#entries.get(key);

    if (packet.packetType === "connect") {
      this.#receiveConnect(packet, remote, key, existing);
      return;
    }
    if (!existing) {
      this.#stats.packetsDropped += 1;
      return;
    }
    this.#receiveFromPeer(existing, packet);
  }

  #receiveConnect(packet: CultNetRudpPacket, remote: RemoteInfo, key: string, existing: PeerEntry | undefined): void {
    if (existing?.session.connectRepeats(packet)) {
      // A repeat of the Connect it accepted: the Accept owed, nothing restarted.
      try {
        this.#sendInPoll(existing, existing.session.acceptConnect(packet, Date.now()));
      } catch {
        this.#stats.packetsDropped += 1;
        this.#endPeer(existing, refusedGoodbye(existing.session));
      }
      return;
    }
    if (!existing && this.#entries.size >= this.#maxPeers) {
      this.#stats.packetsDropped += 1;
      return;
    }
    const session = new CultNetRudpSession({
      connectionId: this.#options.connectionId,
      initialSequence: this.#options.initialSequence,
      resendDelayMs: this.#options.resendDelayMs,
      maxPendingReliablePackets: this.#options.maxPendingReliablePackets,
    });
    let accept: CultNetRudpPacket;
    try {
      accept = session.acceptConnect(packet, Date.now());
    } catch {
      this.#stats.packetsDropped += 1;
      return;
    }
    const entry: PeerEntry = {
      key,
      session,
      peer: {
        remoteHost: remote.address,
        remotePort: remote.port,
        connectPayload: Uint8Array.from(packet.payload ?? []),
        get connected() { return session.connected; },
        get outstandingReliablePacketCount() { return session.outstandingReliablePacketCount; },
      },
    };
    if (this.#transmit(entry, accept)) {
      // The peer cannot be answered, so no session starts.
      this.#stats.packetsDropped += 1;
      return;
    }
    if (existing) {
      // Same endpoint, new Connect generation: only this peer's old session ends.
      this.#entries.delete(key);
      this.emit("peerDisconnected", { peer: existing.peer, reason: utf8("replaced by a new Connect generation") });
    }
    this.#entries.set(key, entry);
    this.emit("peerConnected", entry.peer);
  }

  #receiveFromPeer(entry: PeerEntry, packet: CultNetRudpPacket): void {
    const session = entry.session;
    let result: ReturnType<CultNetRudpSession["receive"]>;
    try {
      result = session.receive(packet, Date.now());
    } catch {
      // receive() has already recorded the packet's reliable sequence, so the session
      // cannot be kept: a retransmit would be acknowledged and the frame silently lost.
      this.#stats.packetsDropped += 1;
      this.#endPeer(entry, refusedGoodbye(session));
      return;
    }
    try {
      if (result.reply) this.#sendInPoll(entry, result.reply);
      for (const ready of result.readyToSend ?? []) this.#sendInPoll(entry, ready);
      if (result.pong) {
        this.emit("pong", { peer: entry.peer, payload: result.pongPayload ?? new Uint8Array() });
      }
      for (const frame of result.delivered) {
        this.#stats.framesReceived += 1;
        this.emit("frame", { peer: entry.peer, channelId: frame.channelId, payload: frame.payload });
      }
      if (result.disconnected) {
        if (this.#entries.get(entry.key) === entry) {
          this.#entries.delete(entry.key);
          this.emit("peerDisconnected", { peer: entry.peer, reason: result.disconnectReason ?? new Uint8Array(0) });
        }
        return;
      }
      if (this.#entries.get(entry.key) === entry && (packet.packetType === "data" || result.delivered.length > 0)) {
        this.#sendInPoll(entry, session.createAckForReceived(packet.sequence));
      }
    } catch (error) {
      this.emit("error", error instanceof Error ? error : new Error(String(error)));
    }
  }

  #tick(): void {
    const now = Date.now();
    for (const entry of [...this.#entries.values()]) {
      if (this.#entries.get(entry.key) !== entry) continue;
      if (entry.session.checkTimeout(now, this.#peerTimeoutMs)) {
        this.#endPeer(entry, entry.session.createDisconnect(utf8("session timed out")));
        continue;
      }
      const generation = entry.session.generation;
      for (const packet of entry.session.dueResends(now)) {
        this.#sendInPoll(entry, packet);
        // A resend that can never be sent ended the session and what it owed.
        if (entry.session.generation !== generation || this.#entries.get(entry.key) !== entry) break;
      }
    }
  }

  /** A send with no caller to hand a failure to: it ends that peer, never the poll. */
  #sendInPoll(entry: PeerEntry, packet: CultNetRudpPacket): void {
    const thrown = this.#transmit(entry, packet);
    if (thrown) this.#sendFailed(entry, thrown);
  }

  /** Sends one packet to the peer and returns the failure the send threw, if any. */
  #transmit(entry: PeerEntry, packet: CultNetRudpPacket): Error | undefined {
    const wire = encodeRudpPacket(packet);
    this.#stats.bytesSent += wire.length;
    let thrown: Error | undefined;
    let sending = true;
    const generation = entry.session.generation;
    sendRudpDatagram(this.#socket, wire, entry.peer.remotePort, entry.peer.remoteHost, (error) => {
      if (sending) {
        thrown = error;
      } else if (entry.session.generation === generation) {
        this.#sendFailed(entry, error);
      }
    });
    sending = false;
    return thrown;
  }

  /** A datagram that can never be sent ends that peer; any other failure is a lost datagram. */
  #sendFailed(entry: PeerEntry, error: Error): void {
    if (isPermanentSendError(error)) {
      this.#endPeer(entry, entry.session.endUnsendableSession(error));
      return;
    }
    this.#stats.sendFailures += 1;
  }
}
