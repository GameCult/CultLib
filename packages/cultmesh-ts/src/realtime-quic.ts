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

import {
  isCSharpWhiteSpace,
  verifyAuthorityRoute,
  type CultMeshAuthorityRouteCertificate,
  type CultMeshAuthorityTrustPolicy,
  type CultMeshP256PublicKey,
} from "cultnet-ts";
import type {
  CultMeshAuthorityRouteMessage,
  CultMeshVerseDescriptorMessage,
} from "cultnet-ts/contracts";

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
  CULTMESH_QUIC_EVENT_STREAM_FRAME,
  CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE,
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
  readonly authorityRoute?: CultMeshAuthorityRouteMessage;
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

class SimpleGate {
  private tail: Promise<void> = Promise.resolve();

  async run<T>(work: () => Promise<T>): Promise<T> {
    const previous = this.tail;
    let release!: () => void;
    this.tail = new Promise((resolve) => (release = resolve));
    await previous;
    try {
      return await work();
    } finally {
      release();
    }
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
      const timer = setTimeout(() => {
        if (settled) return;
        settled = true;
        runtime.offConnectionEvent(connectionId);
        runtime.connectionShutdown(connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
        void runtime.release();
        reject(new TimeoutError(`CultMesh QUIC handshake with '${candidate.endpoint}' timed out.`));
      }, handshakeTimeoutMs);
      const onAbort = (): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        runtime.offConnectionEvent(connectionId);
        runtime.connectionShutdown(connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
        void runtime.release();
        reject(new Error("CultMesh QUIC connect aborted."));
      };
      signal?.addEventListener("abort", onAbort, { once: true });

      const finish = (transport?: CultMeshRealtimeTransport, error?: Error): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
        if (error) {
          runtime.offConnectionEvent(connectionId);
          void runtime.release();
          reject(error);
          return;
        }
        resolve(transport!);
      };

      const transport = new CultMeshQuicRealtimeTransport(
        candidate.endpoint,
        runtime,
        connectionId,
        target,
        candidate.generation,
      );

      runtime.onConnectionEvent(connectionId, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED) {
          const der = event.payload;
          const accepted = validator
            ? validator(target, der)
            : certificateSha256 !== undefined &&
              certificateSha256Hex(der).toLowerCase() === certificateSha256.toLowerCase();
          try {
            runtime.connectionCertificateComplete(connectionId, accepted);
          } catch (error) {
            finish(undefined, error instanceof Error ? error : new Error(String(error)));
          }
          if (!accepted) {
            finish(undefined, new Error(`CultMesh QUIC provider certificate for '${candidate.endpoint}' was rejected.`));
          }
          return;
        }
        if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN && !settled) {
          const reason = Buffer.from(event.payload).toString("utf8");
          finish(undefined, new Error(`CultMesh QUIC connection closed during handshake: ${reason || event.code}`));
          return;
        }
        transport.handleConnectionEvent(event);
        if (!settled && event.type === CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED) {
          finish(transport);
        }
      });
    });
  }
}

class TimeoutError extends Error {}

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
  private readonly reliableGate = new SimpleGate();
  private readonly pendingSends = new Map<bigint, PendingSend>();
  private readonly streamKinds = new Map<bigint, number>();
  private readonly latestGenerations = new Map<string, LatestGeneration>();
  private readonly receiveQueue: CultMeshRealtimeFrame[] = [];
  private readonly receiveWaiters: Array<(frame: CultMeshRealtimeFrame) => void> = [];
  private reliableOutboundStreamId: bigint | undefined;
  private disposed = false;

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

  async sendFrame(frame: CultMeshRealtimeFrame): Promise<void> {
    if (this.disposed) throw new Error("CultMesh QUIC realtime transport is disposed.");
    if (frame.delivery === "unreliable") {
      throw new Error(
        "The native MsQuic connector exposes streams but not QUIC datagrams; unreliable delivery is not supported.",
      );
    }
    const encoded = encodeRealtimeFrame(frame);
    if (frame.delivery === "reliable-ordered") {
      await this.reliableGate.run(async () => {
        if (this.reliableOutboundStreamId === undefined) {
          this.reliableOutboundStreamId = this.runtime.streamOpen(
            this.connectionId,
            CULTMESH_QUIC_STREAM_RELIABLE,
          );
          this.streamKinds.set(this.reliableOutboundStreamId, CULTMESH_QUIC_STREAM_RELIABLE);
        }
        await this.sendOnStream(this.reliableOutboundStreamId, encoded, false);
      });
      return;
    }
    // latest-only: a fresh stream per send, mirroring the C# reference.
    const streamId = this.runtime.streamOpen(this.connectionId, CULTMESH_QUIC_STREAM_LATEST_ONLY);
    this.streamKinds.set(streamId, CULTMESH_QUIC_STREAM_LATEST_ONLY);
    await this.sendOnStream(streamId, encoded, true);
  }

  private sendOnStream(streamId: bigint, encoded: Uint8Array, fin: boolean): Promise<void> {
    return new Promise<void>((resolve, reject) => {
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
    if (this.receiveQueue.length > 0) return this.receiveQueue.shift()!;
    if (this.disposed) throw new Error("CultMesh QUIC realtime transport is disposed.");
    return await new Promise<CultMeshRealtimeFrame>((resolve, reject) => {
      const onAbort = (): void => {
        const index = this.receiveWaiters.indexOf(waiter);
        if (index >= 0) this.receiveWaiters.splice(index, 1);
        reject(new Error("CultMesh QUIC receiveFrame aborted."));
      };
      const waiter = (frame: CultMeshRealtimeFrame): void => {
        signal?.removeEventListener("abort", onAbort);
        resolve(frame);
      };
      signal?.addEventListener("abort", onAbort, { once: true });
      this.receiveWaiters.push(waiter);
    });
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.runtime.offConnectionEvent(this.connectionId);
    for (const [, pending] of this.pendingSends) pending.reject(new Error("CultMesh QUIC realtime transport is disposed."));
    this.pendingSends.clear();
    try {
      this.runtime.connectionShutdown(this.connectionId, CULTMESH_REALTIME_CONNECTION_CLOSE_CODE);
    } catch {
      // Best-effort: the connection may already be gone.
    }
    void this.runtime.release();
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
        this.disposed = true;
        return;
      default:
        return;
    }
  }

  /** `ReadStreamAsync`'s stream-kind/delivery consistency check (`:519-524`). */
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

  /** `PublishReceived`'s per-`(channel, body)` generation filter (`:538-560`). */
  private publishReceived(frame: CultMeshRealtimeFrame): void {
    if (frame.delivery === "latest-only") {
      const key = frame.channelId + "\u001f" + frame.bodyId;
      const candidate: LatestGeneration = { producerEpoch: frame.producerEpoch, sequence: frame.sequence };
      const current = this.latestGenerations.get(key);
      if (current && compareGeneration(candidate, current) <= 0) return;
      this.latestGenerations.set(key, candidate);
    }
    const waiter = this.receiveWaiters.shift();
    if (waiter) {
      waiter(frame);
    } else {
      this.receiveQueue.push(frame);
    }
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

function routeCertificateView(
  certificate: CultMeshAuthorityRouteMessage["certificate"],
): CultMeshAuthorityRouteCertificate | undefined {
  if (!certificate) return undefined;
  const providerKey: CultMeshP256PublicKey = {
    keyId: certificate.providerKeyId,
    x: certificate.providerPublicKeyX,
    y: certificate.providerPublicKeyY,
  };
  return {
    providerKey,
    odinKeyId: certificate.odinKeyId,
    issuedAtUnixMilliseconds: certificate.issuedAtUnixMilliseconds,
    expiresAtUnixMilliseconds: certificate.expiresAtUnixMilliseconds,
    signature: certificate.signature,
  };
}

function trimmedOrEmpty(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && isCSharpWhiteSpace(value[start]!)) start += 1;
  while (end > start && isCSharpWhiteSpace(value[end - 1]!)) end -= 1;
  return value.slice(start, end);
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

  /** Resolves, verifies, races and connects; disposes losing candidates. */
  async connect(target: CultMeshRealtimeTarget): Promise<CultMeshRealtimeTransport> {
    const key = this.sessionKey(target);
    const existing = this.sessions.get(key);
    if (existing) return existing;

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
        generation: trimmedOrEmpty(route.generation),
        authorityRoute: route,
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
      const entries = [...(tiers.get(tier) ?? [])]
        .sort((a, b) => a.candidate.priority - b.candidate.priority)
        .slice(0, maxRaced);
      const attempts = entries.map(async (entry) => {
        const transport = await entry.connector.connect(entry.candidate, target);
        if (!transport.isVerifiedFor(target.verseId, target.authorityRuntimeId, CULTMESH_REALTIME_STATE_PROTOCOL_ID, entry.candidate.generation)) {
          transport.dispose();
          throw new Error(`Transport did not prove CultMesh authority for '${key}'.`);
        }
        return transport;
      });
      const settled = await Promise.allSettled(attempts);
      const winner = settled.find((result): result is PromiseFulfilledResult<CultMeshRealtimeTransport> => result.status === "fulfilled");
      for (const result of settled) {
        if (result === winner) continue;
        if (result.status === "fulfilled") result.value.dispose();
        else failures.push(result.reason instanceof Error ? result.reason : new Error(String(result.reason)));
      }
      if (winner) {
        this.sessions.set(key, winner.value);
        return winner.value;
      }
    }
    throw new Error(
      `No realtime state path connected for '${key}': ${failures.map((error) => error.message).join("; ")}`,
    );
  }

  /** Marks the session for `target` offline, disposing its transport. */
  disconnect(target: CultMeshRealtimeTarget): void {
    const key = this.sessionKey(target);
    const transport = this.sessions.get(key);
    if (!transport) return;
    this.sessions.delete(key);
    transport.dispose();
  }

  dispose(): void {
    for (const transport of this.sessions.values()) transport.dispose();
    this.sessions.clear();
  }
}
