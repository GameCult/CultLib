import { createSocket, type Socket } from "node:dgram";

import {
  CultCache,
  SingleFileMessagePackBackingStore,
  type AnyCultCacheDocumentDefinition,
  type CacheBackingStore,
  type CultCacheDocumentValue,
} from "cultcache-ts";
import {
  CultNetDocumentRegistry,
  CultNetRudpSocketTransportConnection,
  CultNetSchemaCatalog,
  CultNetShardCatalog,
  cultNetBuiltinSchemaRegistry,
  defineCultNetDocumentBinding,
  type CultNetDocumentBinding,
  type CultNetReconnectPolicy,
  type CultNetSchemaCatalogOptions,
} from "cultnet-ts";

export interface CultMeshNodeOptions {
  documents?: Iterable<AnyCultCacheDocumentDefinition>;
  bindings?: Iterable<CultNetDocumentBinding>;
  store?: CacheBackingStore;
  pullOnStart?: boolean;
}

export class CultMeshNode {
  public constructor(
    public readonly cache: CultCache,
    public readonly store: CacheBackingStore,
    public readonly documents: CultNetDocumentRegistry,
  ) {}

  public get<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    return this.cache.get(definition, key);
  }

  public getRequired<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> {
    return this.cache.getRequired(definition, key);
  }

  public put<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.cache.put(definition, key, value);
  }

  public delete<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    return this.cache.delete(definition, key);
  }

  public async flush(soft = false): Promise<void> {
    await this.store.pushAll?.(this.cache.snapshot(), { soft });
  }
}

export class CultMeshVerseCatalog<TVerse = unknown> {
  readonly #verses = new Map<string, TVerse>();
  readonly #subscribers = new Set<(verse: TVerse) => void>();

  public get verses(): readonly TVerse[] {
    return [...this.#verses.entries()]
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([, verse]) => verse);
  }

  public watch(callback: (verse: TVerse) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  public upsert(verseId: string, verse: TVerse): void {
    requireNonEmpty(verseId, "verseId");
    this.#verses.set(verseId, verse);
    for (const subscriber of [...this.#subscribers]) {
      subscriber(verse);
    }
  }

  public get(verseId: string): TVerse | undefined {
    requireNonEmpty(verseId, "verseId");
    return this.#verses.get(verseId);
  }
}

export interface CultMeshPeerCard {
  peerId: string;
  verseId: string;
  endpoints: readonly string[];
  roles?: readonly string[];
  shardIds?: readonly string[];
  authorityLeaseId?: string;
}

export interface CultMeshRudpEndpoint {
  host: string;
  port: number;
  uri: string;
}

export interface CultMeshRudpSocketOptions {
  bindHost?: string;
  bindPort?: number;
  socket?: Socket;
  initialSequence?: number;
  resendDelayMs?: number;
  resendPollMs?: number;
  transportId?: string;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
  maxPendingReliablePackets?: number;
  reconnectPolicy?: CultNetReconnectPolicy;
}

export class CultMeshPeerCatalog {
  readonly #peers = new Map<string, CultMeshPeerCard>();
  readonly #subscribers = new Set<(peer: CultMeshPeerCard) => void>();

  public get peers(): readonly CultMeshPeerCard[] {
    return [...this.#peers.values()].sort((left, right) =>
      left.peerId.localeCompare(right.peerId),
    );
  }

  public watch(callback: (peer: CultMeshPeerCard) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  public upsert(peer: CultMeshPeerCard): void {
    requireNonEmpty(peer.peerId, "peer.peerId");
    requireNonEmpty(peer.verseId, "peer.verseId");
    this.#peers.set(peer.peerId, peer);
    for (const subscriber of [...this.#subscribers]) {
      subscriber(peer);
    }
  }

  public get(peerId: string): CultMeshPeerCard | undefined {
    requireNonEmpty(peerId, "peerId");
    return this.#peers.get(peerId);
  }

  public find(verseId: string, role?: string): readonly CultMeshPeerCard[] {
    requireNonEmpty(verseId, "verseId");
    return this.peers.filter(
      (peer) =>
        peer.verseId === verseId &&
        (!role || peer.roles?.includes(role) === true),
    );
  }
}

export interface CultMeshAuthorityLease {
  leaseId: string;
  verseId: string;
  peerId: string;
  roles: readonly string[];
  shardIds?: readonly string[];
  validFrom: Date;
  expiresAt: Date;
}

export class CultMeshAuthorityLeaseCatalog {
  readonly #leases = new Map<string, CultMeshAuthorityLease>();
  readonly #subscribers = new Set<(lease: CultMeshAuthorityLease) => void>();

  public get leases(): readonly CultMeshAuthorityLease[] {
    return [...this.#leases.values()].sort((left, right) =>
      left.leaseId.localeCompare(right.leaseId),
    );
  }

  public watch(callback: (lease: CultMeshAuthorityLease) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  public upsert(lease: CultMeshAuthorityLease): void {
    requireNonEmpty(lease.leaseId, "lease.leaseId");
    requireNonEmpty(lease.verseId, "lease.verseId");
    requireNonEmpty(lease.peerId, "lease.peerId");
    if (lease.expiresAt <= lease.validFrom) {
      throw new Error("CultMesh authority lease expiry must be after validFrom.");
    }

    this.#leases.set(lease.leaseId, lease);
    for (const subscriber of [...this.#subscribers]) {
      subscriber(lease);
    }
  }

  public get(leaseId: string): CultMeshAuthorityLease | undefined {
    requireNonEmpty(leaseId, "leaseId");
    return this.#leases.get(leaseId);
  }

  public isAuthorized(
    peer: CultMeshPeerCard,
    role: string,
    shardId?: string,
    at = new Date(),
  ): boolean {
    requireNonEmpty(role, "role");
    if (!peer.authorityLeaseId) {
      return false;
    }

    const lease = this.#leases.get(peer.authorityLeaseId);
    if (!lease) {
      return false;
    }

    return (
      at >= lease.validFrom &&
      at < lease.expiresAt &&
      lease.verseId === peer.verseId &&
      lease.peerId === peer.peerId &&
      lease.roles.includes(role) &&
      peer.roles?.includes(role) === true &&
      (!shardId ||
        !lease.shardIds ||
        lease.shardIds.length === 0 ||
        lease.shardIds.includes(shardId))
    );
  }
}

export type CultMeshStreamKind = "audio" | "video" | "tensor" | "bytes";

export type CultMeshStreamBodyTransport =
  | "shared-memory"
  | "shared-d3d12-texture"
  | "shared-d3d11-texture"
  | "dma-buf"
  | "iosurface"
  | "ahardwarebuffer"
  | "cultcache-page"
  | "inline-bytes";

export type CultMeshStreamAccess = "read" | "write" | "read-write";

export interface CultMeshStreamClock {
  clockDomainId: string;
  sourceId?: string;
  sampleRate?: number;
  offsetToVerseTimeNs?: number;
  confidence?: number;
  evidenceKind?: string;
}

export interface CultMeshAudioStreamFormat {
  sampleRate: number;
  channels: number;
  sampleFormat: "float32" | "int16" | "int24" | "int32";
  framesPerPacket?: number;
}

export interface CultMeshVideoStreamFormat {
  width: number;
  height: number;
  pixelFormat: string;
  framesPerSecond?: number;
  planeCount?: number;
}

export interface CultMeshStreamDescriptor {
  streamId: string;
  verseId: string;
  ownerPeerId: string;
  kind: CultMeshStreamKind;
  label?: string;
  clock: CultMeshStreamClock;
  audio?: CultMeshAudioStreamFormat;
  video?: CultMeshVideoStreamFormat;
  preferredTransports: readonly CultMeshStreamBodyTransport[];
  requiredAccess?: CultMeshStreamAccess;
  maxInFlightFrames?: number;
  metadataSchemaId?: string;
}

export interface CultMeshStreamConsumerProfile {
  peerId: string;
  verseId: string;
  supportedTransports: readonly CultMeshStreamBodyTransport[];
  acceptedKinds?: readonly CultMeshStreamKind[];
  canImportGpuHandles?: boolean;
  canMapSharedMemory?: boolean;
  maxInFlightFrames?: number;
}

export interface CultMeshStreamNegotiation {
  streamId: string;
  producerPeerId: string;
  consumerPeerId: string;
  transport: CultMeshStreamBodyTransport;
  access: CultMeshStreamAccess;
  maxInFlightFrames: number;
  copyBudget: "zero-copy-target" | "one-copy-fallback" | "opaque-runtime";
}

export interface CultMeshStreamFrameHandle {
  streamId: string;
  sequence: bigint;
  timestampNs: bigint;
  durationNs?: bigint;
  transport: CultMeshStreamBodyTransport;
  byteLength?: number;
  nativeHandle?: string;
  resourceKey?: string;
  pageRef?: string;
  fenceHandle?: string;
  fenceValue?: bigint;
  unavoidableCopyCount?: number;
  metadata?: Record<string, unknown>;
}

export class CultMeshStreamCatalog {
  readonly #streams = new Map<string, CultMeshStreamDescriptor>();
  readonly #latestFrames = new Map<string, CultMeshStreamFrameHandle>();
  readonly #streamSubscribers = new Set<(stream: CultMeshStreamDescriptor) => void>();
  readonly #frameSubscribers = new Set<(frame: CultMeshStreamFrameHandle) => void>();

  public get streams(): readonly CultMeshStreamDescriptor[] {
    return [...this.#streams.values()].sort((left, right) =>
      left.streamId.localeCompare(right.streamId),
    );
  }

  public watch(callback: (stream: CultMeshStreamDescriptor) => void): () => void {
    this.#streamSubscribers.add(callback);
    return () => {
      this.#streamSubscribers.delete(callback);
    };
  }

  public watchFrames(callback: (frame: CultMeshStreamFrameHandle) => void): () => void {
    this.#frameSubscribers.add(callback);
    return () => {
      this.#frameSubscribers.delete(callback);
    };
  }

  public declare(stream: CultMeshStreamDescriptor): CultMeshStreamDescriptor {
    requireNonEmpty(stream.streamId, "stream.streamId");
    requireNonEmpty(stream.verseId, "stream.verseId");
    requireNonEmpty(stream.ownerPeerId, "stream.ownerPeerId");
    requireNonEmpty(stream.clock.clockDomainId, "stream.clock.clockDomainId");
    if (stream.preferredTransports.length === 0) {
      throw new Error("stream.preferredTransports must not be empty.");
    }

    this.#streams.set(stream.streamId, stream);
    for (const subscriber of [...this.#streamSubscribers]) {
      subscriber(stream);
    }
    return stream;
  }

  public get(streamId: string): CultMeshStreamDescriptor | undefined {
    requireNonEmpty(streamId, "streamId");
    return this.#streams.get(streamId);
  }

  public find(
    verseId: string,
    kind?: CultMeshStreamKind,
  ): readonly CultMeshStreamDescriptor[] {
    requireNonEmpty(verseId, "verseId");
    return this.streams.filter(
      (stream) => stream.verseId === verseId && (!kind || stream.kind === kind),
    );
  }

  public negotiate(
    streamId: string,
    consumer: CultMeshStreamConsumerProfile,
  ): CultMeshStreamNegotiation {
    const stream = this.get(streamId);
    if (!stream) {
      throw new Error(`Unknown CultMesh stream '${streamId}'.`);
    }

    if (consumer.verseId !== stream.verseId) {
      throw new Error("stream and consumer must belong to the same Verse.");
    }

    if (
      consumer.acceptedKinds &&
      !consumer.acceptedKinds.includes(stream.kind)
    ) {
      throw new Error(`consumer does not accept ${stream.kind} streams.`);
    }

    const transport = stream.preferredTransports.find((candidate) =>
      consumer.supportedTransports.includes(candidate),
    );
    if (!transport) {
      throw new Error("stream and consumer have no compatible body transport.");
    }

    return {
      streamId: stream.streamId,
      producerPeerId: stream.ownerPeerId,
      consumerPeerId: consumer.peerId,
      transport,
      access: stream.requiredAccess ?? "read",
      maxInFlightFrames: Math.min(
        stream.maxInFlightFrames ?? Number.MAX_SAFE_INTEGER,
        consumer.maxInFlightFrames ?? Number.MAX_SAFE_INTEGER,
      ),
      copyBudget: copyBudgetFor(transport),
    };
  }

  public publishFrame(frame: CultMeshStreamFrameHandle): CultMeshStreamFrameHandle {
    requireNonEmpty(frame.streamId, "frame.streamId");
    if (!this.#streams.has(frame.streamId)) {
      throw new Error(`Unknown CultMesh stream '${frame.streamId}'.`);
    }

    this.#latestFrames.set(frame.streamId, frame);
    for (const subscriber of [...this.#frameSubscribers]) {
      subscriber(frame);
    }
    return frame;
  }

  public latestFrame(streamId: string): CultMeshStreamFrameHandle | undefined {
    requireNonEmpty(streamId, "streamId");
    return this.#latestFrames.get(streamId);
  }
}

export class CultMesh {
  public static async createNode(
    cachePath: string,
    options: CultMeshNodeOptions = {},
  ): Promise<CultMeshNode> {
    requireNonEmpty(cachePath, "cachePath");
    const store =
      options.store ?? new SingleFileMessagePackBackingStore(cachePath);
    const cache = CultCache.builder().withGenericStore(store).build();
    const documents = new CultNetDocumentRegistry(options.bindings);

    for (const definition of options.documents ?? []) {
      cache.registerDocumentType(definition);
      if (!documents.get(definition.type)) {
        documents.register(defineCultNetDocumentBinding({ definition }));
      }
    }

    if (options.pullOnStart !== false) {
      await cache.pullAllBackingStores();
    }

    return new CultMeshNode(cache, store, documents);
  }

  public static startNode(
    cachePath: string,
    options: CultMeshNodeOptions = {},
  ): Promise<CultMeshNode> {
    return CultMesh.createNode(cachePath, options);
  }

  public static createVerseCatalog<TVerse = unknown>(): CultMeshVerseCatalog<TVerse> {
    return new CultMeshVerseCatalog<TVerse>();
  }

  public static createPeerCatalog(): CultMeshPeerCatalog {
    return new CultMeshPeerCatalog();
  }

  public static createAuthorityLeaseCatalog(): CultMeshAuthorityLeaseCatalog {
    return new CultMeshAuthorityLeaseCatalog();
  }

  public static createStreamCatalog(): CultMeshStreamCatalog {
    return new CultMeshStreamCatalog();
  }

  public static createSchemaCatalog(): CultNetSchemaCatalog {
    return new CultNetSchemaCatalog();
  }

  public static createBuiltInSchemaCatalog(
    options: CultNetSchemaCatalogOptions = {},
  ): CultNetSchemaCatalog {
    const catalog = new CultNetSchemaCatalog();
    catalog.applyResponse(cultNetBuiltinSchemaRegistry.createCatalogResponse({
      schemaVersion: "cultnet.schema_catalog_request.v0",
      messageId: "cultmesh-ts-builtins",
      includeSchemaJson: options.includeSchemaJson,
      schemaIds: options.schemaIds ? [...options.schemaIds] : undefined,
      kinds: options.kinds ? [...options.kinds] : undefined,
    }));
    return catalog;
  }

  public static createShardCatalog(): CultNetShardCatalog {
    return new CultNetShardCatalog();
  }

  public static parseRudpEndpoint(endpoint: string): CultMeshRudpEndpoint {
    requireNonEmpty(endpoint, "endpoint");
    const parsed = new URL(endpoint);
    if (parsed.protocol.toLowerCase() !== "rudp:") {
      throw new Error("RUDP endpoint must use the rudp:// scheme.");
    }
    if (!parsed.hostname || !parsed.port) {
      throw new Error("RUDP endpoint must include a host and port.");
    }
    const port = Number.parseInt(parsed.port, 10);
    if (!Number.isInteger(port) || port <= 0 || port > 65535) {
      throw new Error("RUDP endpoint port must be between 1 and 65535.");
    }
    const host = parsed.hostname;
    const uriHost =
      host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
    return { host, port, uri: `rudp://${uriHost}:${port}` };
  }

  public static async createRudpServer(
    runtimeId: string,
    connectionId: number,
    options: CultMeshRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    requireNonEmpty(runtimeId, "runtimeId");
    const socket = options.socket ?? (await bindRudpSocket(options));
    return new CultNetRudpSocketTransportConnection({
      runtimeId,
      socket,
      mode: "server",
      connectionId,
      initialSequence: options.initialSequence,
      resendDelayMs: options.resendDelayMs,
      resendPollMs: options.resendPollMs,
      transportId: options.transportId,
      maxPayloadBytes: options.maxPayloadBytes,
      maxFragmentBytes: options.maxFragmentBytes,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
      reconnectPolicy: options.reconnectPolicy,
    });
  }

  public static async createRudpClient(
    runtimeId: string,
    connectionId: number,
    endpoint: string | CultMeshRudpEndpoint,
    options: CultMeshRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    requireNonEmpty(runtimeId, "runtimeId");
    const parsedEndpoint =
      typeof endpoint === "string" ? CultMesh.parseRudpEndpoint(endpoint) : endpoint;
    const socket = options.socket ?? (await bindRudpSocket(options));
    return new CultNetRudpSocketTransportConnection({
      runtimeId,
      socket,
      mode: "client",
      remoteHost: parsedEndpoint.host,
      remotePort: parsedEndpoint.port,
      connectionId,
      initialSequence: options.initialSequence,
      resendDelayMs: options.resendDelayMs,
      resendPollMs: options.resendPollMs,
      transportId: options.transportId,
      maxPayloadBytes: options.maxPayloadBytes,
      maxFragmentBytes: options.maxFragmentBytes,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
      reconnectPolicy: options.reconnectPolicy,
    });
  }

  public static async createRudpClientForPeer(
    runtimeId: string,
    connectionId: number,
    peer: CultMeshPeerCard,
    options: CultMeshRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    const endpoint = peer.endpoints.find((value) =>
      value.toLowerCase().startsWith("rudp://"),
    );
    if (!endpoint) {
      throw new Error(`Peer ${peer.peerId} does not advertise a RUDP endpoint.`);
    }
    return CultMesh.createRudpClient(runtimeId, connectionId, endpoint, options);
  }
}

function requireNonEmpty(value: string, name: string): void {
  if (!value || value.trim().length === 0) {
    throw new Error(`${name} must be non-empty.`);
  }
}

async function bindRudpSocket(options: CultMeshRudpSocketOptions): Promise<Socket> {
  const socket = createSocket("udp4");
  const host = options.bindHost ?? "127.0.0.1";
  const port = options.bindPort ?? 0;
  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(port, host, () => {
      socket.off("error", reject);
      resolve();
    });
  });
  return socket;
}

function copyBudgetFor(
  transport: CultMeshStreamBodyTransport,
): CultMeshStreamNegotiation["copyBudget"] {
  switch (transport) {
    case "shared-memory":
    case "shared-d3d12-texture":
    case "shared-d3d11-texture":
    case "dma-buf":
    case "iosurface":
    case "ahardwarebuffer":
      return "zero-copy-target";
    case "cultcache-page":
      return "one-copy-fallback";
    case "inline-bytes":
      return "opaque-runtime";
  }
}
