// The Node QUIC realtime plane: a consumer transport and connector over the
// native v2 bridge, and the session manager that binds an advertised route to
// a connected, authority-verified transport.
//
// Provider and consumer semantics (which stream kind carries which delivery
// mode, the generation filter, the certificate pin decision) are pinned rule
// for rule against the C# reference, `src/GameCult.Mesh.Quic/CultMeshQuicRealtimeTransport.cs`
// (the connector) and `src/GameCult.Mesh/CultMeshSessions.cs` (the session
// manager's binding, ordering and racing).
//
// This module never touches `koffi` directly; every native call goes through
// `CultMeshQuicNativeRuntime` in `realtime-quic-native.ts`, which loads it
// lazily. No Odin WebSocket rendezvous lives here (Q7, ruled A): a lookup
// source is a port this module takes, not a transport it owns.

import { createHash } from "node:crypto";
import { createSecureContext } from "node:tls";

import {
  routeCertificateView,
  trimCSharp,
  verifyAuthorityRoute,
  type CultMeshAuthorityTrustPolicy,
} from "cultnet-ts";
import type { CultMeshVerseDescriptorMessage } from "cultnet-ts/contracts";

import {
  decodeRealtimeFrame,
  encodeRealtimeFrame,
  CULTMESH_REALTIME_CONNECTION_CLOSE_CODE,
  type CultMeshRealtimeFrame,
} from "./realtime-wire";
import {
  CultMeshQuicNativeRuntime,
  CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED,
  CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED,
  CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN,
  CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION,
  CULTMESH_QUIC_EVENT_STREAM_FRAME,
  CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE,
  CULTMESH_QUIC_EVENT_STREAM_SHUTDOWN,
  CULTMESH_QUIC_EVENT_STREAM_STARTED,
  CULTMESH_QUIC_SEND_CANCELED,
  CULTMESH_QUIC_STREAM_LATEST_ONLY,
  CULTMESH_QUIC_STREAM_RELIABLE,
  type CultMeshQuicNativeEvent,
} from "./realtime-quic-native";

/** `CultMeshProtocols.RealtimeState.Value` in the C# reference. */
export const CULTMESH_REALTIME_STATE_PROTOCOL_ID = "cultmesh.realtime_state.v1";

/** The QUIC realtime connector's discovery scheme; `CultMeshQuicRealtimeTransportConnector.Scheme`. */
export const CULTMESH_QUIC_REALTIME_SCHEME = "cultmesh-state+quic";

/** Stable provider identity inside one Verse; `CultMeshSessionTarget` in the C# reference. */
export interface CultMeshRealtimeTarget {
  readonly verseId: string;
  readonly authorityRuntimeId: string;
}

/** A physical route candidate; the TS analogue of `CultMeshTransportCandidate`. */
export interface CultMeshRealtimeCandidate {
  readonly endpoint: string;
  readonly authorityRuntimeId: string;
  readonly priority: number;
  readonly generation: string;
}

/** Transport-neutral realtime plane; the TS analogue of `ICultMeshRealtimeTransport`. */
export interface CultMeshRealtimeTransport {
  readonly transportId: string;
  readonly endpoint: string;
  sendFrame(frame: CultMeshRealtimeFrame): Promise<void>;
  receiveFrame(signal?: AbortSignal): Promise<CultMeshRealtimeFrame>;
  dispose(): void;
  isVerifiedFor(
    verseId: string,
    authorityRuntimeId: string,
    protocolId: string,
    routeGeneration: string,
  ): boolean;
  /**
   * Registers a handler invoked once this transport becomes unusable for any
   * reason (explicit `dispose()`, a peer-initiated shutdown, or an internal
   * fault). A session manager uses this to evict a dead cached session so the
   * next `connect()` redials, mirroring `CultMeshSessions.InvalidateRealtimeSession`.
   */
  onDisposed?(handler: () => void): void;
}

/** Creates realtime transports; the TS analogue of `ICultMeshRealtimeTransportConnector`. */
export interface CultMeshRealtimeTransportConnector {
  readonly connectorId: string;
  readonly priority: number;
  canConnect(candidate: CultMeshRealtimeCandidate): boolean;
  connect(
    candidate: CultMeshRealtimeCandidate,
    target: CultMeshRealtimeTarget,
    signal?: AbortSignal,
  ): Promise<CultMeshRealtimeTransport>;
}

/** A route source the session manager resolves against; Q7's lookup-source port. */
export interface ICultMeshRealtimeLookupSource {
  resolve(target: CultMeshRealtimeTarget): Promise<CultMeshVerseDescriptorMessage[]>;
}

/** `CultMeshQuicRealtimeTransportConnector.TryParseEndpoint` (`:98-109`). */
export function parseQuicRealtimeEndpoint(
  endpoint: string,
): { host: string; port: number; certificateSha256?: string } {
  let parsed: URL;
  try {
    parsed = new URL(endpoint);
  } catch {
    throw new Error(`QUIC realtime connector does not support '${endpoint}'.`);
  }
  if (parsed.protocol.toLowerCase() !== `${CULTMESH_QUIC_REALTIME_SCHEME}:`) {
    throw new Error(`QUIC realtime connector does not support '${endpoint}'.`);
  }
  if (!parsed.hostname) {
    throw new Error(`QUIC realtime connector does not support '${endpoint}'.`);
  }
  const port = parsed.port ? Number.parseInt(parsed.port, 10) : Number.NaN;
  if (!Number.isInteger(port) || port <= 0) {
    throw new Error(`QUIC realtime connector does not support '${endpoint}'.`);
  }
  const pin = parsed.searchParams.get("cert-sha256") ?? undefined;
  return { host: parsed.hostname, port, certificateSha256: pin ?? undefined };
}

function certificateSha256Hex(der: Uint8Array): string {
  return createHash("sha256").update(der).digest("hex");
}

/** Shared by `CultMeshQuicRealtimeTransport.sendFrame` and `CultMeshQuicRealtimeProvider.broadcast`: one message, not two copies that can drift. */
const CULTMESH_QUIC_UNRELIABLE_UNSUPPORTED_MESSAGE =
  "The native MsQuic connector exposes streams but not QUIC datagrams; unreliable delivery is not supported.";

interface PendingSend {
  resolve(): void;
  reject(error: Error): void;
}

interface LatestGeneration {
  producerEpoch: bigint;
  sequence: bigint;
}

function compareGeneration(a: LatestGeneration, b: LatestGeneration): number {
  if (a.producerEpoch !== b.producerEpoch) return a.producerEpoch < b.producerEpoch ? -1 : 1;
  if (a.sequence === b.sequence) return 0;
  return a.sequence < b.sequence ? -1 : 1;
}

/** A direct entry, or a coalesced `key` to resolve against `pending` at delivery time. */
type InboxToken<T> = { readonly value: T } | { readonly key: string };

/**
 * Mirrors the C# reference's `CultMeshRealtimeInbox`
 * (`CultMeshQuicRealtimeTransport.cs:598-644`): publishing under `key`
 * coalesces to at most one pending entry per key, keeping that key's
 * first-arrival queue position while a later publish overwrites its value;
 * publishing with no `key` always queues, unbounded. `receive` drains
 * whatever is queued before a `complete(error)` rejects further calls.
 * Shared by the transport's inbound queue, the provider's fan-in, and (via
 * a pump reading it instead of `receive` callers) each peer's outbox.
 */
class CultMeshRealtimeInbox<T> {
  private readonly pending = new Map<string, T>();
  private readonly ready: InboxToken<T>[] = [];
  private readonly waiters: { resolve(value: T): void; reject(error: Error): void }[] = [];
  private completed = false;
  private terminalError: Error | undefined;

  /** Entries queued but not yet delivered: one per ready key, or one per unkeyed entry. Test-visible. */
  get size(): number {
    return this.ready.length;
  }

  /** Publishes `value`, coalescing on `key` when given. Returns `false` once `complete()` has run. */
  publish(value: T, key?: string): boolean {
    if (this.completed) return false;
    if (key === undefined) {
      this.deliver({ value });
      return true;
    }
    const alreadyPending = this.pending.has(key);
    this.pending.set(key, value);
    if (!alreadyPending) this.deliver({ key });
    return true;
  }

  /** Resolves the oldest ready entry, or waits for one; rejects once `complete(error)` has drained the backlog. */
  async receive(signal?: AbortSignal): Promise<T> {
    if (this.ready.length > 0) return this.take(this.ready.shift()!);
    if (this.completed) throw this.terminalError ?? new Error("CultMesh realtime inbox is completed.");
    if (signal?.aborted) throw new Error("CultMesh realtime inbox receive aborted.");
    return await new Promise<T>((resolve, reject) => {
      const onAbort = (): void => {
        const index = this.waiters.indexOf(waiter);
        if (index >= 0) this.waiters.splice(index, 1);
        reject(new Error("CultMesh realtime inbox receive aborted."));
      };
      const waiter = {
        resolve: (value: T) => {
          signal?.removeEventListener("abort", onAbort);
          resolve(value);
        },
        reject: (error: Error) => {
          signal?.removeEventListener("abort", onAbort);
          reject(error);
        },
      };
      signal?.addEventListener("abort", onAbort, { once: true });
      this.waiters.push(waiter);
    });
  }

  /** Completes the inbox: entries already queued still drain via `receive`; every call after that rejects with `error`. */
  complete(error?: Error): void {
    if (this.completed) return;
    this.completed = true;
    this.terminalError = error;
    for (const waiter of this.waiters.splice(0, this.waiters.length)) {
      waiter.reject(error ?? new Error("CultMesh realtime inbox is completed."));
    }
  }

  private deliver(token: InboxToken<T>): void {
    const waiter = this.waiters.shift();
    if (waiter) {
      waiter.resolve(this.take(token));
      return;
    }
    this.ready.push(token);
  }

  private take(token: InboxToken<T>): T {
    if ("value" in token) return token.value;
    const value = this.pending.get(token.key)!;
    this.pending.delete(token.key);
    return value;
  }
}

/** Configures certificate acceptance for an outbound QUIC realtime connection. */
export interface CultMeshQuicRealtimeConnectorOptions {
  readonly handshakeTimeoutMs?: number;
  readonly validateProviderCertificate?: (
    target: CultMeshRealtimeTarget,
    der: Uint8Array,
  ) => boolean;
}

/** Creates Node-native MsQuic realtime connections. `ConnectorId` is `"msquic-realtime"`. */
export class CultMeshQuicRealtimeConnector implements CultMeshRealtimeTransportConnector {
  private readonly options: CultMeshQuicRealtimeConnectorOptions;

  constructor(options: CultMeshQuicRealtimeConnectorOptions = {}) {
    this.options = options;
  }

  readonly connectorId = "msquic-realtime";
  readonly priority = 0;

  canConnect(candidate: CultMeshRealtimeCandidate): boolean {
    try {
      parseQuicRealtimeEndpoint(candidate.endpoint);
      return true;
    } catch {
      return false;
    }
  }

  async connect(
    candidate: CultMeshRealtimeCandidate,
    target: CultMeshRealtimeTarget,
    signal?: AbortSignal,
  ): Promise<CultMeshRealtimeTransport> {
    if (signal?.aborted) {
      throw new Error("CultMesh QUIC connect aborted.");
    }
    const { host, port, certificateSha256 } = parseQuicRealtimeEndpoint(candidate.endpoint);
    const runtime = await CultMeshQuicNativeRuntime.open();
    let connectionId: bigint;
    try {
      connectionId = runtime.connectionOpen(host, port);
    } catch (error) {
      await runtime.release();
      throw error;
    }

    const handshakeTimeoutMs = this.options.handshakeTimeoutMs ?? 10_000;
    const validator = this.options.validateProviderCertificate;

    return await new Promise<CultMeshRealtimeTransport>((resolve, reject) => {
      let settled = false;

      // Every failure path below — an already-aborted signal, the handshake
      // timeout, a later abort, and any handshake-time error — settles here
      // exactly once: shut the native connection down, drop the runtime
      // reference, and reject.
      const fail = (error: Error): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
        runtime.offConnectionEvent(connectionId);
        runtime.connectionShutdown(connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
        void runtime.release();
        reject(error);
      };

      const succeed = (transport: CultMeshRealtimeTransport): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
        resolve(transport);
      };

      // Honour a signal already aborted by the time we reach here (it fired
      // before `addEventListener` could observe it, so it never would).
      if (signal?.aborted) {
        fail(new Error("CultMesh QUIC connect aborted."));
        return;
      }

      const timer = setTimeout(
        () => fail(new Error(`CultMesh QUIC handshake with '${candidate.endpoint}' timed out.`)),
        handshakeTimeoutMs,
      );
      const onAbort = (): void => fail(new Error("CultMesh QUIC connect aborted."));
      signal?.addEventListener("abort", onAbort, { once: true });

      // Told by the runtime when this connection faults mid-dispatch (a
      // throwing validator during the handshake, or a malformed frame /
      // stream-kind mismatch afterwards): reject the still-pending connect,
      // or hand the fault to the connected transport.
      const onFault = (error: Error): void => {
        if (!settled) {
          fail(error);
          return;
        }
        transport.fault(error);
      };

      const transport = new CultMeshQuicRealtimeTransport(
        candidate.endpoint,
        runtime,
        connectionId,
        target,
        candidate.generation,
      );

      runtime.onConnectionEvent(
        connectionId,
        (event) => {
          if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED) {
            const der = event.payload;
            const accepted = validator
              ? validator(target, der)
              : certificateSha256 !== undefined &&
                certificateSha256Hex(der).toLowerCase() === certificateSha256.toLowerCase();
            try {
              runtime.connectionCertificateComplete(connectionId, accepted);
            } catch (error) {
              fail(error instanceof Error ? error : new Error(String(error)));
              return;
            }
            if (!accepted) {
              fail(new Error(`CultMesh QUIC provider certificate for '${candidate.endpoint}' was rejected.`));
            }
            return;
          }
          if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN && !settled) {
            const reason = Buffer.from(event.payload).toString("utf8");
            fail(new Error(`CultMesh QUIC connection closed during handshake: ${reason || event.code}`));
            return;
          }
          transport.handleConnectionEvent(event);
          if (!settled && event.type === CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED) {
            succeed(transport);
          }
        },
        onFault,
      );
    });
  }
}

/**
 * `CultMeshQuicRealtimeTransport`, over the native v2 bridge. `TransportId`
 * is `"msquic-realtime"`.
 */
export class CultMeshQuicRealtimeTransport implements CultMeshRealtimeTransport {
  readonly transportId = "msquic-realtime";
  readonly endpoint: string;

  private readonly runtime: CultMeshQuicNativeRuntime;
  private readonly connectionId: bigint;
  private readonly target: CultMeshRealtimeTarget;
  private readonly routeGeneration: string;
  /** Serializes writes to the reliable-ordered outbound stream: each send chains onto this tail. */
  private reliableSendTail: Promise<void> = Promise.resolve();
  private readonly pendingSends = new Map<bigint, PendingSend>();
  /** Peer-opened stream id to its declared kind, pruned on `STREAM_SHUTDOWN`; see `trackedStreamCount`. */
  private readonly streamKinds = new Map<bigint, number>();
  /** Generation staleness filter: the newest `(channel, body)` generation delivered so far. */
  private readonly latestGenerations = new Map<string, LatestGeneration>();
  /** Inbound frame queue: bounded to one pending latest-only frame per key. */
  private readonly inbox = new CultMeshRealtimeInbox<CultMeshRealtimeFrame>();
  private reliableOutboundStreamId: bigint | undefined;
  private disposed = false;
  private terminalError: Error | undefined;
  private readonly disposalHandlers = new Set<() => void>();

  constructor(
    endpoint: string,
    runtime: CultMeshQuicNativeRuntime,
    connectionId: bigint,
    target: CultMeshRealtimeTarget,
    routeGeneration: string,
  ) {
    this.endpoint = endpoint;
    this.runtime = runtime;
    this.connectionId = connectionId;
    this.target = target;
    this.routeGeneration = routeGeneration ?? "";
  }

  isVerifiedFor(
    verseId: string,
    authorityRuntimeId: string,
    protocolId: string,
    routeGeneration: string,
  ): boolean {
    return (
      this.target.verseId === verseId &&
      this.target.authorityRuntimeId === authorityRuntimeId &&
      CULTMESH_REALTIME_STATE_PROTOCOL_ID === protocolId &&
      this.routeGeneration === routeGeneration
    );
  }

  /** Number of peer-opened streams still tracked for their kind. Test-visible: proves `STREAM_SHUTDOWN` prunes `streamKinds` instead of leaking one entry per stream for the life of the connection. */
  get trackedStreamCount(): number {
    return this.streamKinds.size;
  }

  async sendFrame(frame: CultMeshRealtimeFrame): Promise<void> {
    if (this.disposed) throw new Error("CultMesh QUIC realtime transport is disposed.");
    if (frame.delivery === "unreliable") {
      throw new Error(CULTMESH_QUIC_UNRELIABLE_UNSUPPORTED_MESSAGE);
    }
    return this.sendEncodedFrame(frame.delivery, encodeRealtimeFrame(frame));
  }

  /**
   * @internal The primitive `sendFrame` itself calls, after encoding. Exposed
   * so a caller that already holds the encoded bytes for many peers at once
   * — `CultMeshQuicRealtimeProvider.broadcast` fanning a frame out to every
   * peer, and each peer's `CultMeshQuicRealtimeProviderOutbox` pump — sends
   * them without re-encoding (and re-validating) per peer.
   */
  async sendEncodedFrame(delivery: "reliable-ordered" | "latest-only", encoded: Uint8Array): Promise<void> {
    if (this.disposed) throw new Error("CultMesh QUIC realtime transport is disposed.");
    if (delivery === "reliable-ordered") {
      const send = this.reliableSendTail.then(async () => {
        if (this.reliableOutboundStreamId === undefined) {
          this.reliableOutboundStreamId = this.runtime.streamOpen(
            this.connectionId,
            CULTMESH_QUIC_STREAM_RELIABLE,
          );
        }
        await this.sendOnStream(this.reliableOutboundStreamId, encoded, false);
      });
      // The tail must advance even if this send rejects, so the next queued
      // send is not blocked forever on a settled promise; the caller still
      // observes the rejection through `send` itself.
      this.reliableSendTail = send.catch(() => {});
      return send;
    }
    // latest-only: a fresh stream per send, mirroring the C# reference.
    const streamId = this.runtime.streamOpen(this.connectionId, CULTMESH_QUIC_STREAM_LATEST_ONLY);
    await this.sendOnStream(streamId, encoded, true);
  }

  /**
   * A pending send registered after `cleanup()` already ran (a `sendFrame`
   * queued behind `reliableSendTail` whose microtask lands after `dispose()`
   * or a fault settles this transport) would otherwise sit in `pendingSends`
   * forever: `cleanup()`'s own reject sweep already ran once and is guarded
   * against running again, and `offConnectionEvent` means no later native
   * event can resolve it either. Checking `this.disposed` here closes that
   * window: any send that loses the race against disposal rejects
   * immediately instead of registering an entry nothing will ever settle.
   */
  private sendOnStream(streamId: bigint, encoded: Uint8Array, fin: boolean): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      if (this.disposed) {
        reject(this.terminalError ?? new Error("CultMesh QUIC realtime transport is disposed."));
        return;
      }
      this.pendingSends.set(streamId, { resolve, reject });
      try {
        this.runtime.streamSendFrame(streamId, encoded, fin);
      } catch (error) {
        this.pendingSends.delete(streamId);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  async receiveFrame(signal?: AbortSignal): Promise<CultMeshRealtimeFrame> {
    return this.inbox.receive(signal);
  }

  /** Registers a handler invoked once, when this transport becomes unusable. */
  onDisposed(handler: () => void): void {
    if (this.disposed) {
      handler();
      return;
    }
    this.disposalHandlers.add(handler);
  }

  dispose(): void {
    if (this.disposed) return;
    try {
      this.runtime.connectionShutdown(this.connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
    } catch {
      // Best-effort: the connection may already be gone.
    }
    this.cleanup();
  }

  /**
   * @internal Called by the connector when the native runtime faults this
   * connection mid-dispatch (a malformed frame, a stream-kind mismatch, or a
   * throwing caller callback). Mirrors the C# reference's
   * `_received.Complete(error)` / `_shutdown.Cancel()`: pending and future
   * `receiveFrame` calls reject with `error`, and the connection is torn
   * down. The native side has already shut the connection down and removed
   * its listener by the time this runs.
   */
  fault(error: Error): void {
    this.cleanup(error);
  }

  /**
   * Runs exactly once, however the transport becomes unusable: explicit
   * `dispose()`, a peer-initiated `CONNECTION_SHUTDOWN`, or `fault()`.
   * Releases the runtime reference before running any `onDisposed` handler,
   * so a handler that throws (or blocks) can never delay or skip the
   * release; every handler runs in its own try/catch, so one throwing
   * handler cannot stop the rest from running. Also rejects every pending
   * and future receive/send, so none of those paths can leak the reference
   * or hang a waiter forever.
   */
  private cleanup(error?: Error): void {
    if (this.disposed) return;
    this.disposed = true;
    this.terminalError = error;
    this.runtime.offConnectionEvent(this.connectionId);
    const rejection = error ?? new Error("CultMesh QUIC realtime transport is disposed.");
    for (const [, pending] of this.pendingSends) pending.reject(rejection);
    this.pendingSends.clear();
    this.inbox.complete(rejection);
    void this.runtime.release();
    const handlers = [...this.disposalHandlers];
    this.disposalHandlers.clear();
    for (const handler of handlers) {
      try {
        handler();
      } catch (handlerError) {
        // eslint-disable-next-line no-console
        console.error("CultMesh QUIC realtime transport onDisposed handler threw:", handlerError);
      }
    }
  }

  /** @internal Routed here by the connector's connection-event registration. */
  handleConnectionEvent(event: CultMeshQuicNativeEvent): void {
    switch (event.type) {
      case CULTMESH_QUIC_EVENT_STREAM_STARTED:
        this.streamKinds.set(event.streamId, event.streamKind);
        return;
      case CULTMESH_QUIC_EVENT_STREAM_FRAME:
        this.onStreamFrame(event);
        return;
      case CULTMESH_QUIC_EVENT_STREAM_SHUTDOWN:
        this.streamKinds.delete(event.streamId);
        return;
      case CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE: {
        const pending = this.pendingSends.get(event.streamId);
        if (!pending) return;
        this.pendingSends.delete(event.streamId);
        if (event.code === BigInt(CULTMESH_QUIC_SEND_CANCELED)) {
          pending.reject(new Error("CultMesh QUIC send was canceled."));
        } else {
          pending.resolve();
        }
        return;
      }
      case CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN:
        this.cleanup(new Error("CultMesh QUIC connection was closed by the remote peer."));
        return;
      default:
        return;
    }
  }

  /**
   * `ReadStreamAsync`'s stream-kind/delivery consistency check (`:519-524`).
   * Throwing here is intentional: the native runtime's dispatch loop (fix 1)
   * catches it, faults only this connection, and keeps the pump and every
   * other connection running.
   */
  private onStreamFrame(event: CultMeshQuicNativeEvent): void {
    const kind = this.streamKinds.get(event.streamId);
    const frame = decodeRealtimeFrame(event.payload);
    if (kind === CULTMESH_QUIC_STREAM_RELIABLE && frame.delivery !== "reliable-ordered") {
      throw new Error("Reliable QUIC stream carried incompatible delivery semantics.");
    }
    if (kind === CULTMESH_QUIC_STREAM_LATEST_ONLY && frame.delivery !== "latest-only") {
      throw new Error("Latest-only QUIC stream carried incompatible delivery semantics.");
    }
    this.publishReceived(frame);
  }

  /**
   * `PublishReceived`'s per-`(channel, body)` generation filter (`:538-560`),
   * plus the inbox's own coalescing: a latest-only frame overwrites the
   * still-pending frame for its key rather than queuing beside it, so an
   * unread backlog holds at most one frame per `(channel, body)`.
   * Reliable-ordered frames queue directly, unbounded, exactly as the C#
   * reference's unbounded channel does.
   */
  private publishReceived(frame: CultMeshRealtimeFrame): void {
    if (frame.delivery === "latest-only") {
      const key = frame.channelId + "\u001f" + frame.bodyId;
      const candidate: LatestGeneration = { producerEpoch: frame.producerEpoch, sequence: frame.sequence };
      const current = this.latestGenerations.get(key);
      if (current && compareGeneration(candidate, current) <= 0) return;
      this.latestGenerations.set(key, candidate);
      this.inbox.publish(frame, key);
      return;
    }
    this.inbox.publish(frame);
  }
}

/**
 * `TryPublishLatest` plus `SendPublishedFramesAsync` (`:363-367`, `:421-439`),
 * over a coalescing outbox (`:572-618`). Owns one peer's outbound
 * `latest-only` traffic: `publish` never blocks (it just replaces whatever is
 * still pending for the frame's `(channel, body)` key and, if a pump is not
 * already draining, starts one), and the pump opens a fresh kind-2 stream per
 * frame through the peer's own `sendFrame` (unchanged from the consumer path
 * above), sending with `fin` and waiting for `send_complete` before it takes
 * the next ready key. A stalled peer therefore accumulates at most one
 * pending frame per key and never blocks a caller's `broadcast`. A send
 * failure evicts the peer once, the same way a reliable-ordered send failure
 * does in `CultMeshQuicRealtimeProvider.broadcast`.
 */
interface OutboxEntry {
  readonly frame: CultMeshRealtimeFrame;
  readonly encoded: Uint8Array;
}

class CultMeshQuicRealtimeProviderOutbox {
  private readonly inbox = new CultMeshRealtimeInbox<OutboxEntry>();
  private readonly transport: CultMeshQuicRealtimeTransport;
  private readonly onSendFailure: (error: Error) => void;
  private pumping = false;
  private disposed = false;

  constructor(transport: CultMeshQuicRealtimeTransport, onSendFailure: (error: Error) => void) {
    this.transport = transport;
    this.onSendFailure = onSendFailure;
  }

  /**
   * Enqueues an already-encoded `frame`, coalescing on `(channelId,
   * bodyId)`. Never blocks. Takes the encoded bytes rather than encoding its
   * own copy so every peer's outbox reuses the one encode `broadcast` (or a
   * retained-frame seed) already did for this frame.
   */
  publish(frame: CultMeshRealtimeFrame, encoded: Uint8Array): void {
    if (this.disposed) return;
    const key = frame.channelId + "\u001f" + frame.bodyId;
    this.inbox.publish({ frame, encoded }, key);
    if (!this.pumping) void this.pump();
  }

  dispose(): void {
    this.disposed = true;
    this.inbox.complete();
  }

  /** One in flight per peer: the next ready key is taken only after `send_complete`, mirroring `SendPublishedFramesAsync`. */
  private async pump(): Promise<void> {
    this.pumping = true;
    try {
      for (;;) {
        if (this.disposed) return;
        let entry: OutboxEntry;
        try {
          entry = await this.inbox.receive();
        } catch {
          return;
        }
        try {
          await this.transport.sendEncodedFrame("latest-only", entry.encoded);
        } catch (error) {
          this.dispose();
          this.onSendFailure(error instanceof Error ? error : new Error(String(error)));
          return;
        }
      }
    } finally {
      this.pumping = false;
    }
  }
}

/** Stable placeholder target for a provider's accepted peer connections: `isVerifiedFor` is a consumer-side concept the provider never calls. */
const CULTMESH_PROVIDER_PEER_TARGET: CultMeshRealtimeTarget = { verseId: "", authorityRuntimeId: "" };

/** Configures a `CultMeshQuicRealtimeProvider` listener. */
export interface CultMeshQuicRealtimeProviderOptions {
  readonly host: string;
  readonly port: number;
  readonly serverCertificate: { readonly pkcs12: Uint8Array; readonly password?: string };
  readonly handshakeTimeoutMs?: number;
  /**
   * The host advertised in `advertisedEndpoint`, when it differs from the
   * bind address `host` — the usual case for a public service: `host` is
   * typically `0.0.0.0`, an empty string, or an IPv6 wildcard (`::`), none of
   * which describe a route a client can dial, and the native listener (which
   * `host` also configures) only accepts IP addresses in the first place, not
   * a DNS name. Defaults to `host`.
   */
  readonly advertisedHost?: string;
  /**
   * When `false` (the default: every existing caller, test, and interop
   * peer calls `receive()`), the provider fans client-originated streams
   * into `receive()` as before. When `true`, the provider is publish-only:
   * any stream a peer opens faults that connection at `STREAM_STARTED`,
   * before a frame can be decoded or queued, so no inbox, generation-map,
   * or fan-in entry is ever created for client data; `receive()` itself
   * rejects. StreamPixels sets this explicitly — it closes the unbounded
   * per-client memory growth Soul's probe found.
   */
  readonly acceptClientFrames?: boolean;
  /**
   * Refuses a new connection at accept once the number of connections this
   * provider is tracking (including mid-handshake ones) has reached this
   * many: shut down immediately, before any transport, outbox, or
   * retained-frame seeding is allocated for it. Existing peers are
   * unaffected. Defaults to 1024 — far more than any single StreamPixels
   * fan-out needs today, while still bounding per-connection overhead
   * against a public UDP port whose pin authenticates only the server, so
   * any internet client can dial in. StreamPixels sets its own value.
   */
  readonly maxConnections?: number;
}

/**
 * Brackets an IPv6 literal for use in a URL authority (`[::1]:443`), the way
 * `URL` itself renders one; a hostname or IPv4 literal passes through
 * unchanged, and an already-bracketed literal is left alone.
 */
function formatEndpointHost(host: string): string {
  if (host.startsWith("[")) return host;
  return host.includes(":") ? `[${host}]` : host;
}

/**
 * Recovers the leaf certificate's DER bytes from a PKCS12 credential, to
 * compute the pin a provider advertises. Node has no public PKCS12 decoder;
 * `tls.createSecureContext` loads the PKCS12 through OpenSSL, and its
 * internal `context.getCertificate()` is the only way to read back the DER
 * bytes OpenSSL parsed out of it. Probed against the pinned Node 24 build and
 * this package's own test fixture pair (`test/fixtures/quic-test.p12` and
 * `quic-test-cert.der`, generated independently by `openssl pkcs12 -export`):
 * the returned buffer is byte-identical to the `.der` file and its SHA-256
 * matches the pin the fixture's tests already assert.
 */
function extractLeafCertificateDer(serverCertificate: CultMeshQuicRealtimeProviderOptions["serverCertificate"]): Uint8Array {
  const context = createSecureContext({
    pfx: Buffer.from(serverCertificate.pkcs12),
    passphrase: serverCertificate.password ?? "",
  }) as unknown as { context: { getCertificate(): Buffer | undefined } };
  const der = context.context.getCertificate();
  if (!der || der.length === 0) {
    throw new Error("CultMesh QUIC provider certificate has no parseable leaf certificate.");
  }
  return new Uint8Array(der);
}

/**
 * The StreamPixels role: a TypeScript listener that broadcasts with an
 * explicit delivery mode and cannot be backpressured by a slow consumer.
 * Rule for rule, this is `CultMeshQuicRealtimeServer` (`CultMeshQuicRealtimeTransport.cs:151-310`):
 * `listen` opens a listener and computes the advertised endpoint;
 * `broadcast` mirrors `BroadcastAsync` (`:216-235`); `receive` mirrors
 * `ReceiveAsync` fanned in from every accepted peer's inbox
 * (`CultMeshRealtimeInbox.ReceiveAsync`, `:597-609`); peers are evicted on
 * send failure or shutdown, and there is no reconnect logic here, matching
 * the C# reference's `AcceptLoopAsync` (`:276-309`).
 */
export class CultMeshQuicRealtimeProvider {
  /** `cultmesh-state+quic://<host>:<boundPort>?cert-sha256=<HEX>`, uppercase — the Unity connector requires the pin. */
  readonly advertisedEndpoint: string;

  private readonly runtime: CultMeshQuicNativeRuntime;
  private readonly listenerId: bigint;
  private readonly handshakeTimeoutMs: number;
  private readonly peers = new Set<CultMeshQuicRealtimeTransport>();
  private readonly outboxes = new Map<CultMeshQuicRealtimeTransport, CultMeshQuicRealtimeProviderOutbox>();
  /** The newest `latest-only` frame broadcast so far per `(channel, body)` key, already
   * encoded, seeded into every peer attached after that broadcast. Reliable-ordered frames
   * are events, not state, and are never retained. */
  private readonly retained = new Map<string, { frame: CultMeshRealtimeFrame; encoded: Uint8Array; generation: LatestGeneration }>();
  /** Client-to-provider fan-in, bounded the same way as the transport's inbound queue. */
  private readonly received = new CultMeshRealtimeInbox<CultMeshRealtimeFrame>();
  /**
   * Every transport `acceptConnection` has created for a still-live native
   * connection, whether or not its handshake has reached CONNECTED and
   * joined `peers` yet. A connection whose handshake never reaches CONNECTED
   * (a rejected certificate, a stalled client) would otherwise sit on its
   * own handshake timer — and hold a runtime reference — for up to
   * `handshakeTimeoutMs` after the provider itself was disposed, keeping the
   * process alive over a connection nothing can still reach; `dispose()`
   * sweeps this set instead of only `peers` to close that gap.
   */
  private readonly accepted = new Set<CultMeshQuicRealtimeTransport>();
  private readonly acceptClientFrames: boolean;
  private readonly maxConnections: number;
  private disposed = false;

  private constructor(
    runtime: CultMeshQuicNativeRuntime,
    listenerId: bigint,
    advertisedEndpoint: string,
    handshakeTimeoutMs: number,
    acceptClientFrames: boolean,
    maxConnections: number,
  ) {
    this.runtime = runtime;
    this.listenerId = listenerId;
    this.advertisedEndpoint = advertisedEndpoint;
    this.handshakeTimeoutMs = handshakeTimeoutMs;
    this.acceptClientFrames = acceptClientFrames;
    this.maxConnections = maxConnections;
  }

  /** Number of currently accepted peer connections. */
  get connectionCount(): number {
    return this.peers.size;
  }

  /** Frames fanned in from peers but not yet drained by `receive()`. Test-visible: proves the fan-in queue is bounded. */
  get receiveQueueSize(): number {
    return this.received.size;
  }

  static async listen(options: CultMeshQuicRealtimeProviderOptions): Promise<CultMeshQuicRealtimeProvider> {
    const handshakeTimeoutMs = options.handshakeTimeoutMs ?? 10_000;
    const der = extractLeafCertificateDer(options.serverCertificate);
    const pinHex = certificateSha256Hex(der).toUpperCase();
    const bindHost = options.host && options.host.length > 0 ? options.host : "127.0.0.1";
    const advertisedHost = options.advertisedHost && options.advertisedHost.length > 0 ? options.advertisedHost : bindHost;

    const runtime = await CultMeshQuicNativeRuntime.open();
    let listenerId: bigint;
    let boundPort: number;
    try {
      ({ listenerId, boundPort } = runtime.listenerOpen(
        options.host || null,
        options.port,
        options.serverCertificate.pkcs12,
        options.serverCertificate.password ?? null,
      ));
    } catch (error) {
      await runtime.release();
      throw error;
    }

    const advertisedEndpoint = `${CULTMESH_QUIC_REALTIME_SCHEME}://${formatEndpointHost(advertisedHost)}:${boundPort}?cert-sha256=${pinHex}`;
    const provider = new CultMeshQuicRealtimeProvider(
      runtime,
      listenerId,
      advertisedEndpoint,
      handshakeTimeoutMs,
      options.acceptClientFrames ?? true,
      options.maxConnections ?? 1024,
    );
    runtime.onListenerEvent(listenerId, (event) => {
      if (event.type === CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION) provider.acceptConnection(event.connectionId);
    });
    return provider;
  }

  /**
   * `BroadcastAsync` (`:216-235`). `latest-only` enqueues into each peer's
   * outbox and returns as soon as every peer has accepted the frame into its
   * outbox — never once any peer's I/O. `reliable-ordered` awaits every
   * peer's `send_complete` (or eviction) before resolving, matching
   * `Task.WhenAll` over `SendToConnectedClientAsync` (`:233-255`).
   */
  async broadcast(frame: CultMeshRealtimeFrame): Promise<void> {
    if (this.disposed) throw new Error("CultMesh QUIC realtime provider is disposed.");
    if (frame.delivery === "unreliable") {
      throw new Error(CULTMESH_QUIC_UNRELIABLE_UNSUPPORTED_MESSAGE);
    }
    // Encode (and validate) exactly once, up front: an invalid frame throws
    // here, to the caller, before touching any peer. The old per-peer encode
    // inside `sendFrame` meant an invalid frame failed identically for every
    // peer in turn, evicting all of them while `broadcast` itself still
    // reported success.
    const encoded = encodeRealtimeFrame(frame);
    if (frame.delivery === "latest-only") {
      const key = frame.channelId + "\u001f" + frame.bodyId;
      const current = this.retained.get(key);
      const candidate: LatestGeneration = { producerEpoch: frame.producerEpoch, sequence: frame.sequence };
      if (!current || compareGeneration(candidate, current.generation) > 0) {
        this.retained.set(key, { frame, encoded, generation: candidate });
      }
      for (const outbox of this.outboxes.values()) outbox.publish(frame, encoded);
      return;
    }
    const sends = [...this.peers].map(async (transport) => {
      try {
        await transport.sendEncodedFrame("reliable-ordered", encoded);
      } catch {
        transport.dispose();
      }
    });
    if (sends.length > 0) await Promise.all(sends);
  }

  /** The next client-originated frame, fanned in from every accepted peer. */
  async receive(signal?: AbortSignal): Promise<CultMeshRealtimeFrame> {
    if (!this.acceptClientFrames) {
      throw new Error(
        "CultMesh QUIC realtime provider is publish-only (acceptClientFrames: false); it never accepts client frames.",
      );
    }
    return this.received.receive(signal);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    try {
      this.runtime.listenerClose(this.listenerId);
    } catch {
      // Best-effort: the listener may already be gone.
    }
    // `accepted` is every transport this provider ever created for a native
    // connection, from the moment `acceptConnection` synchronously builds
    // it — whether or not its handshake ever reached CONNECTED and joined
    // `peers`. A still-handshaking connection has no other owner to sweep it.
    for (const transport of [...this.accepted]) transport.dispose();
    this.received.complete(new Error("CultMesh QUIC realtime provider is disposed."));
    void this.runtime.release();
  }

  /** Coalesces client-sent `latest-only` frames by `(channel, body)`, so a publish-only `receive()` caller (StreamPixels) backs up to at most one pending frame per key. */
  private async pumpReceivedFrom(transport: CultMeshQuicRealtimeTransport): Promise<void> {
    for (;;) {
      let frame: CultMeshRealtimeFrame;
      try {
        frame = await transport.receiveFrame();
      } catch {
        return; // The transport is disposed/faulted; nothing more to read.
      }
      if (frame.delivery === "latest-only") {
        this.received.publish(frame, frame.channelId + "\u001f" + frame.bodyId);
      } else {
        this.received.publish(frame);
      }
    }
  }

  /**
   * Handles `LISTENER_NEW_CONNECTION`, fully synchronously: one handler owns
   * this connection from this point on, so there is no async window in which
   * a NEW_CONNECTION-then-CONNECTED pair (the bridge's own event order,
   * `native/GameCult.Mesh.Quic.Native/cultmesh_quic_native.cpp:731-757,627`)
   * can be split across a temporary buffering handler and a later real one.
   * `this.runtime.retain()` takes a reference on the provider's own
   * already-open runtime instead of `await`ing a fresh
   * `CultMeshQuicNativeRuntime.open()`, which is what used to force that
   * split: the previous async gap left `CONNECTION_CONNECTED` dispatched
   * straight to `transport.handleConnectionEvent` (which has no case for
   * it) with nothing to clear the handshake timer, so a healthy peer was
   * evicted at `handshakeTimeoutMs` regardless of how well-behaved the
   * client was.
   */
  private acceptConnection(connectionId: bigint): void {
    if (this.disposed) {
      this.runtime.connectionShutdown(connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
      return;
    }
    if (this.accepted.size >= this.maxConnections) {
      // Refused before any transport, timer, or outbox exists for it:
      // existing accepted connections are untouched.
      this.runtime.connectionShutdown(connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
      return;
    }

    const transport = new CultMeshQuicRealtimeTransport(
      `quic-peer-${connectionId}`,
      this.runtime.retain(),
      connectionId,
      CULTMESH_PROVIDER_PEER_TARGET,
      "",
    );
    this.accepted.add(transport);

    const handshakeTimer = setTimeout(() => transport.dispose(), this.handshakeTimeoutMs);
    transport.onDisposed(() => {
      clearTimeout(handshakeTimer);
      this.accepted.delete(transport);
      this.peers.delete(transport);
      this.outboxes.get(transport)?.dispose();
      this.outboxes.delete(transport);
    });

    // CONNECTED fires at most once per connection; `attached` guards against
    // ever running the join-`peers` bookkeeping twice.
    let attached = false;
    this.runtime.onConnectionEvent(
      connectionId,
      (event) => {
        // Publish-only: a peer-opened stream faults this connection alone,
        // before `handleConnectionEvent` can record its stream kind or
        // decode a frame off it. Throwing here drives the dispatch loop's
        // own fault path (`CultMeshQuicNativeRuntime.dispatch`), which
        // shuts the connection down and evicts it.
        if (!this.acceptClientFrames && event.type === CULTMESH_QUIC_EVENT_STREAM_STARTED) {
          throw new Error(
            "CultMesh QUIC provider is publish-only (acceptClientFrames: false); it does not accept inbound streams from peers.",
          );
        }
        transport.handleConnectionEvent(event);
        if (attached || event.type !== CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED) return;
        attached = true;
        clearTimeout(handshakeTimer);
        // Cut C's retained-frame seeding happens atomically here, before the
        // peer joins `peers`: nothing can observe this transport as an
        // attached peer with a not-yet-seeded outbox.
        const outbox = new CultMeshQuicRealtimeProviderOutbox(transport, () => transport.dispose());
        for (const { frame, encoded } of this.retained.values()) outbox.publish(frame, encoded);
        this.outboxes.set(transport, outbox);
        this.peers.add(transport);
        if (this.acceptClientFrames) void this.pumpReceivedFrom(transport);
      },
      (error) => transport.fault(error),
    );
  }
}

/** `CultMeshStaticRealtimeLookupSource`: an array of pre-resolved Verse descriptors. */
export class CultMeshStaticRealtimeLookupSource implements ICultMeshRealtimeLookupSource {
  private readonly verses: readonly CultMeshVerseDescriptorMessage[];

  constructor(verses: readonly CultMeshVerseDescriptorMessage[]) {
    this.verses = verses;
  }

  async resolve(target: CultMeshRealtimeTarget): Promise<CultMeshVerseDescriptorMessage[]> {
    return this.verses.filter((verse) => verse.verseId === target.verseId);
  }
}

/** Options for `CultMeshQuicRealtimeSessionManager`. */
export interface CultMeshQuicRealtimeSessionManagerOptions {
  readonly lookupSource: ICultMeshRealtimeLookupSource;
  readonly trust: CultMeshAuthorityTrustPolicy;
  readonly connectors: readonly CultMeshRealtimeTransportConnector[];
  readonly maxRacedCandidates?: number;
}

/**
 * Binds an advertised route to a connected, authority-verified realtime
 * transport. Rule for rule, this is `CultMeshSessionManager`'s realtime path
 * in the C# reference (`ConnectRealtimeAsync`, `:833-896`).
 */
export class CultMeshQuicRealtimeSessionManager {
  private readonly options: CultMeshQuicRealtimeSessionManagerOptions;
  private readonly sessions = new Map<string, CultMeshRealtimeTransport>();
  /** Single-flight: concurrent `connect()` calls for the same key share one dial. */
  private readonly connecting = new Map<string, Promise<CultMeshRealtimeTransport>>();
  /** One controller per in-flight dial, so `dispose()` and `disconnect()` can cancel it. */
  private readonly dialAborts = new Map<string, AbortController>();
  private disposed = false;

  constructor(options: CultMeshQuicRealtimeSessionManagerOptions) {
    if (options.connectors.length === 0) {
      throw new Error("CultMeshQuicRealtimeSessionManager requires at least one realtime connector.");
    }
    if ((options.maxRacedCandidates ?? 2) <= 0) {
      throw new Error("maxRacedCandidates must be positive.");
    }
    this.options = options;
  }

  private sessionKey(target: CultMeshRealtimeTarget): string {
    return target.verseId + "\u001f" + target.authorityRuntimeId;
  }

  /**
   * Resolves, verifies, races and connects; disposes losing candidates.
   * Returns the cached session for `target` when one is live. Concurrent
   * calls for the same target share one dial (mirrors C#'s `Lazy`); a
   * transport that dies later (peer shutdown, an internal fault, or explicit
   * disposal) evicts itself from the cache so the next call redials, mirroring
   * `CultMeshSessions.InvalidateRealtimeSession` (`:898-913`).
   */
  async connect(target: CultMeshRealtimeTarget): Promise<CultMeshRealtimeTransport> {
    if (this.disposed) throw new Error("CultMeshQuicRealtimeSessionManager is disposed.");
    const key = this.sessionKey(target);
    const existing = this.sessions.get(key);
    if (existing) return existing;

    const inFlight = this.connecting.get(key);
    if (inFlight) return inFlight;

    const controller = new AbortController();
    this.dialAborts.set(key, controller);
    const attempt = this.connectOwnedAsync(key, target, controller.signal).finally(() => {
      this.connecting.delete(key);
      if (this.dialAborts.get(key) === controller) this.dialAborts.delete(key);
    });
    this.connecting.set(key, attempt);
    return attempt;
  }

  private async connectOwnedAsync(
    key: string,
    target: CultMeshRealtimeTarget,
    signal: AbortSignal,
  ): Promise<CultMeshRealtimeTransport> {
    const transport = await this.dialAsync(key, target, signal);
    if (this.disposed || signal.aborted) {
      transport.dispose();
      throw new Error(
        this.disposed
          ? "CultMeshQuicRealtimeSessionManager was disposed while connecting."
          : `Dial for '${key}' was cancelled while connecting.`,
      );
    }
    this.sessions.set(key, transport);
    transport.onDisposed?.(() => {
      if (this.sessions.get(key) === transport) this.sessions.delete(key);
    });
    return transport;
  }

  private async dialAsync(
    key: string,
    target: CultMeshRealtimeTarget,
    signal: AbortSignal,
  ): Promise<CultMeshRealtimeTransport> {
    const verses = await this.options.lookupSource.resolve(target);
    const routes = verses
      .flatMap((verse) => verse.authorityRoutes ?? [])
      .filter((route) => route.authorityRuntimeId === target.authorityRuntimeId)
      .filter((route) => route.protocolIds.includes(CULTMESH_REALTIME_STATE_PROTOCOL_ID));
    if (routes.length === 0) {
      throw new Error(`No realtime state route was advertised for '${key}'.`);
    }

    const verifiedCandidates: CultMeshRealtimeCandidate[] = [];
    const verificationFailures: Error[] = [];
    for (const route of routes) {
      const candidate: CultMeshRealtimeCandidate = {
        endpoint: route.endpoint,
        authorityRuntimeId: route.authorityRuntimeId,
        priority: route.priority,
        generation: trimCSharp(route.generation),
      };
      try {
        await verifyAuthorityRoute(
          {
            verseId: target.verseId,
            authorityRuntimeId: route.authorityRuntimeId,
            endpoint: route.endpoint,
            protocolIds: route.protocolIds,
            priority: route.priority,
            generation: route.generation,
            certificate: routeCertificateView(route.certificate),
          },
          this.options.trust,
        );
        verifiedCandidates.push(candidate);
      } catch (error) {
        verificationFailures.push(error instanceof Error ? error : new Error(String(error)));
      }
    }
    if (verifiedCandidates.length === 0) {
      throw new Error(
        `No trusted realtime route remained for '${key}': ${verificationFailures.map((error) => error.message).join("; ")}`,
      );
    }

    const tiers = new Map<number, Array<{ candidate: CultMeshRealtimeCandidate; connector: CultMeshRealtimeTransportConnector }>>();
    for (const candidate of verifiedCandidates) {
      for (const connector of this.options.connectors) {
        if (!connector.canConnect(candidate)) continue;
        const tier = connector.priority;
        const list = tiers.get(tier) ?? [];
        list.push({ candidate, connector });
        tiers.set(tier, list);
      }
    }
    if (tiers.size === 0) {
      throw new Error(`No realtime connector supports an advertised route for '${key}'.`);
    }

    const maxRaced = this.options.maxRacedCandidates ?? 2;
    const failures: Error[] = [];
    for (const tier of [...tiers.keys()].sort((a, b) => a - b)) {
      if (signal.aborted) break;
      const entries = [...(tiers.get(tier) ?? [])]
        .sort((a, b) => a.candidate.priority - b.candidate.priority)
        .slice(0, maxRaced);
      const winner = await this.raceFirstSuccessAsync(key, target, entries, failures, signal);
      if (winner) return winner;
    }
    throw new Error(
      `No realtime state path connected for '${key}': ${failures.map((error) => error.message).join("; ")}`,
    );
  }

  /**
   * Races one tier's candidates and returns the first verified success,
   * mirroring C#'s `Task.WhenAny` loop (`:851-866`): as soon as one candidate
   * both connects and proves `isVerifiedFor`, the rest are aborted (an
   * `AbortSignal` is threaded into `connector.connect`) and disposed once
   * they settle, without blocking the return. `Promise.allSettled` here would
   * instead wait out every candidate, including a dead route stalled on its
   * own handshake timeout.
   */
  private async raceFirstSuccessAsync(
    key: string,
    target: CultMeshRealtimeTarget,
    entries: ReadonlyArray<{ candidate: CultMeshRealtimeCandidate; connector: CultMeshRealtimeTransportConnector }>,
    failures: Error[],
    outerSignal: AbortSignal,
  ): Promise<CultMeshRealtimeTransport | undefined> {
    const controller = new AbortController();
    if (outerSignal.aborted) controller.abort();
    else outerSignal.addEventListener("abort", () => controller.abort(), { once: true });
    const pending = new Set(
      entries.map((entry) => this.connectCandidateAsync(key, target, entry, controller.signal)),
    );
    let winner: CultMeshRealtimeTransport | undefined;
    while (pending.size > 0 && !winner) {
      const settled = await Promise.race(
        [...pending].map((attempt) =>
          attempt.then(
            (value) => ({ attempt, ok: true as const, value }),
            (error) => ({ attempt, ok: false as const, error }),
          ),
        ),
      );
      pending.delete(settled.attempt);
      if (settled.ok) {
        winner = settled.value;
      } else {
        failures.push(settled.error instanceof Error ? settled.error : new Error(String(settled.error)));
      }
    }
    if (!winner) return undefined;

    controller.abort();
    // The remaining candidates may already be in flight past the point an
    // abort signal helps; dispose whatever they eventually produce, without
    // blocking the winner's return.
    for (const loser of pending) {
      loser.then((transport) => transport.dispose()).catch(() => {});
    }
    return winner;
  }

  private async connectCandidateAsync(
    key: string,
    target: CultMeshRealtimeTarget,
    entry: { candidate: CultMeshRealtimeCandidate; connector: CultMeshRealtimeTransportConnector },
    signal: AbortSignal,
  ): Promise<CultMeshRealtimeTransport> {
    const transport = await entry.connector.connect(entry.candidate, target, signal);
    if (!transport.isVerifiedFor(target.verseId, target.authorityRuntimeId, CULTMESH_REALTIME_STATE_PROTOCOL_ID, entry.candidate.generation)) {
      transport.dispose();
      throw new Error(`Transport did not prove CultMesh authority for '${key}'.`);
    }
    return transport;
  }

  /**
   * Marks the session for `target` offline, disposing its transport. Also
   * cancels a dial still in flight for `target`, so a connect racing this
   * call cannot cache a transport this call meant to discard.
   */
  disconnect(target: CultMeshRealtimeTarget): void {
    const key = this.sessionKey(target);
    const transport = this.sessions.get(key);
    if (transport) {
      this.sessions.delete(key);
      transport.dispose();
    }
    this.dialAborts.get(key)?.abort();
  }

  dispose(): void {
    this.disposed = true;
    for (const controller of this.dialAborts.values()) controller.abort();
    this.dialAborts.clear();
    for (const transport of this.sessions.values()) transport.dispose();
    this.sessions.clear();
  }
}
