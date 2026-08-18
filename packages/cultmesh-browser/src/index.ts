import { decode, encode } from "@msgpack/msgpack";
import {
  encodeCultNetMessageForWire,
  parseCultNetMessage,
  type CultNetDatabaseChangeRawMessage,
  type CultNetDatabaseSubscribeMessage,
  type CultNetDatabaseUnsubscribeMessage,
  type CultNetMessage,
  type CultNetOperationRequestMessage,
  type CultNetOperationResponseMessage,
  type CultNetRawDocumentRecord,
  type CultNetSnapshotResponseRawMessage,
  type CultMeshVerseCatalogRequestMessage,
  type CultMeshVerseCatalogResponseMessage,
  type CultMeshSessionAcceptedMessage,
  type CultMeshSessionOpenMessage,
} from "cultnet-ts/contracts";

export interface CultMeshBrowserIdentity {
  verseId: string;
  authorityRuntimeId: string;
}

export interface CultMeshBrowserRoute extends CultMeshBrowserIdentity {
  endpoint: string;
  protocolId?: string;
  protocolIds?: readonly string[];
  priority?: number;
  generation: string;
  certificate?: CultMeshBrowserRouteCertificate;
}

export interface CultMeshBrowserP256PublicKey {
  keyId: string;
  x: string;
  y: string;
}

export interface CultMeshBrowserRouteCertificate {
  providerKey: CultMeshBrowserP256PublicKey;
  odinKeyId: string;
  issuedAtUnixMilliseconds: number;
  expiresAtUnixMilliseconds: number;
  signature: string;
}

export type CultMeshBrowserAuthorityTrustMode = "authenticated-remote" | "local-development";

export interface CultMeshBrowserAuthorityTrustPolicy {
  mode: CultMeshBrowserAuthorityTrustMode;
  odinRoots?: readonly CultMeshBrowserP256PublicKey[];
  now?: () => number;
}

export interface CultMeshBrowserRendezvous {
  resolve(identity: CultMeshBrowserIdentity): Promise<CultMeshBrowserRoute>;
}

export interface CultMeshBrowserOdinRendezvousOptions {
  endpoints: readonly string[];
  runtimeId: string;
  transportVersion?: string;
  timeoutMs?: number;
  maxFrameBytes?: number;
  createId?: () => string;
  socketFactory?: (endpoint: string) => CultMeshBrowserSocket;
}

export interface CultMeshBrowserSocket {
  binaryType: BinaryType;
  readonly readyState: number;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  send(data: ArrayBuffer | ArrayBufferView): void;
  close(code?: number, reason?: string): void;
}

/** Resolves a stable Verse/authority-runtime target through Odin's canonical CultNet Verse catalog. */
export class CultMeshBrowserOdinRendezvous implements CultMeshBrowserRendezvous {
  #options: Required<Omit<CultMeshBrowserOdinRendezvousOptions, "transportVersion" | "socketFactory">> & {
    transportVersion?: string;
    socketFactory: (endpoint: string) => CultMeshBrowserSocket;
  };

  constructor(options: CultMeshBrowserOdinRendezvousOptions) {
    requireText(options.runtimeId, "runtimeId");
    const endpoints = [...new Set(options.endpoints.map(value => value.trim()).filter(Boolean))];
    if (endpoints.length === 0) throw new Error("At least one Odin WebSocket endpoint is required.");
    for (const endpoint of endpoints) requireWebSocketEndpoint(endpoint, "Odin rendezvous endpoint");
    this.#options = {
      ...options,
      endpoints,
      timeoutMs: options.timeoutMs ?? 10_000,
      maxFrameBytes: options.maxFrameBytes ?? 4 * 1024 * 1024,
      createId: options.createId ?? (() => crypto.randomUUID()),
      socketFactory: options.socketFactory ?? (endpoint => new WebSocket(endpoint)),
    };
    if (this.#options.timeoutMs <= 0) throw new Error("Odin rendezvous timeoutMs must be positive.");
    if (this.#options.maxFrameBytes <= 0) throw new Error("Odin rendezvous maxFrameBytes must be positive.");
  }

  async resolve(identity: CultMeshBrowserIdentity): Promise<CultMeshBrowserRoute> {
    requireText(identity.verseId, "verseId");
    requireText(identity.authorityRuntimeId, "authorityRuntimeId");
    const failures: Error[] = [];
    for (const endpoint of this.#options.endpoints) {
      try {
        return await this.resolveFrom(endpoint, identity);
      } catch (error) {
        failures.push(error instanceof Error ? error : new Error(String(error)));
      }
    }
    throw new AggregateError(
      failures,
      `Odin could not resolve Verse '${identity.verseId}' authority runtime '${identity.authorityRuntimeId}'.`,
    );
  }

  private resolveFrom(endpoint: string, identity: CultMeshBrowserIdentity): Promise<CultMeshBrowserRoute> {
    const socket = this.#options.socketFactory(endpoint);
    socket.binaryType = "arraybuffer";
    const messageId = this.#options.createId();
    const request: CultMeshVerseCatalogRequestMessage = {
      schemaVersion: "cultmesh.verse_catalog_request.v0",
      messageId,
      verseIds: [identity.verseId],
      ...(this.#options.transportVersion ? { transportVersion: this.#options.transportVersion } : {}),
    };
    return new Promise<CultMeshBrowserRoute>((resolve, reject) => {
      let settled = false;
      const finish = (error?: Error, route?: CultMeshBrowserRoute) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        socket.onopen = null;
        socket.onmessage = null;
        socket.onerror = null;
        socket.onclose = null;
        socket.close(1000, error ? "Odin resolution failed" : "Odin resolution complete");
        if (error) reject(error);
        else resolve(route!);
      };
      const timer = setTimeout(
        () => finish(new Error(`Odin resolution timed out at '${endpoint}'.`)),
        this.#options.timeoutMs,
      );
      socket.onopen = () => {
        try {
          sendSocketMessage(socket, {
            schemaVersion: "cultnet.hello.v0",
            runtimeId: this.#options.runtimeId,
            runtimeKind: "browser",
            supportedMessageVersions: [
              "cultmesh.verse_catalog_request.v0",
              "cultmesh.verse_catalog_response.v0",
            ],
          }, this.#options.maxFrameBytes);
          sendSocketMessage(socket, request, this.#options.maxFrameBytes);
        } catch (error) {
          finish(error instanceof Error ? error : new Error(String(error)));
        }
      };
      socket.onmessage = event => {
        void decodeSocketMessage(event, this.#options.maxFrameBytes).then(message => {
          if (message.schemaVersion === "cultnet.error.v0") {
            finish(new Error(message.error));
            return;
          }
          if (message.schemaVersion !== "cultmesh.verse_catalog_response.v0" || message.messageId !== messageId) return;
          finish(undefined, selectOdinRoute(message, identity));
        }).catch(error => finish(error instanceof Error ? error : new Error(String(error))));
      };
      socket.onerror = () => finish(new Error(`Odin WebSocket could not open '${endpoint}'.`));
      socket.onclose = () => finish(new Error(`Odin WebSocket closed before resolving '${identity.verseId}'.`));
    });
  }
}

export interface CultMeshBrowserClientOptions extends CultMeshBrowserIdentity {
  runtimeId: string;
  rendezvous: CultMeshBrowserRendezvous;
  socketFactory?: (endpoint: string) => CultMeshBrowserSocket;
  reconnectDelayMs?: number;
  connectTimeoutMs?: number;
  requestTimeoutMs?: number;
  maxFrameBytes?: number;
  createId?: () => string;
  /** Consumer-owned authority roots. Remote sessions fail closed when omitted. */
  trust?: CultMeshBrowserAuthorityTrustPolicy;
}

export interface CultMeshRawDocumentLeaseOptions {
  schemaId: string;
  recordKey: string;
  subscriptionId?: string;
}

export interface CultMeshOperationOptions {
  serviceId: string;
  operation: string;
  payloadSchema: string;
  payload: unknown;
  idempotencyKey?: string;
  /** @deprecated The connected authority is the operation target. A different value is rejected. */
  targetRuntimeId?: string;
}

/** Portable schema returned when routing or envelope validation fails before a domain reply exists. */
export const cultNetOperationFailureSchema = "gamecult.cultnet.operation_failure.v1";

export interface CultNetOperationFailure {
  code: string;
  message: string;
}

/** Correlated framework-level failure returned by a CultMesh operation provider. */
export class CultMeshBrowserOperationError extends Error {
  readonly status: string;
  readonly code: string;
  readonly diagnostics: readonly string[];

  constructor(response: CultNetOperationResponseMessage, failure: CultNetOperationFailure) {
    super(failure.message || "CultMesh operation failed.");
    this.name = "CultMeshBrowserOperationError";
    this.status = response.status || "error";
    this.code = failure.code || "operation-error";
    this.diagnostics = response.diagnostics ?? [];
  }
}

export type CultMeshConnectionState = "connecting" | "connected" | "reconnecting" | "disposed";

type DocumentWatcher = (record: CultNetRawDocumentRecord | undefined) => void;

interface PendingSnapshot {
  lease: CultMeshRawDocumentLease;
  resolve: () => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

interface PendingOperation {
  request: CultNetOperationRequestMessage;
  resolve: (response: CultNetOperationResponseMessage) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class CultMeshRawDocumentLease implements Disposable {
  readonly schemaId: string;
  readonly recordKey: string;
  readonly subscriptionId: string;
  #client: CultMeshBrowserClient | undefined;
  #current: CultNetRawDocumentRecord | undefined;
  #watchers = new Set<DocumentWatcher>();

  constructor(
    client: CultMeshBrowserClient,
    options: Required<CultMeshRawDocumentLeaseOptions>,
  ) {
    this.#client = client;
    this.schemaId = options.schemaId;
    this.recordKey = options.recordKey;
    this.subscriptionId = options.subscriptionId;
  }

  get current(): CultNetRawDocumentRecord | undefined {
    return this.#current;
  }

  watch(watcher: DocumentWatcher): () => void {
    if (!this.#client) throw new Error("CultMesh document lease is disposed.");
    this.#watchers.add(watcher);
    watcher(this.#current);
    return () => this.#watchers.delete(watcher);
  }

  dispose(): void {
    this.#client?.releaseLease(this);
    this.#client = undefined;
    this.#watchers.clear();
  }

  [Symbol.dispose](): void {
    this.dispose();
  }

  apply(record: CultNetRawDocumentRecord | undefined): void {
    this.#current = record;
    for (const watcher of this.#watchers) watcher(record);
  }
}

export class CultMeshBrowserClient implements AsyncDisposable {
  readonly identity: CultMeshBrowserIdentity;
  readonly runtimeId: string;
  #options: Required<Omit<CultMeshBrowserClientOptions, "socketFactory">> & {
    socketFactory: (endpoint: string) => CultMeshBrowserSocket;
  };
  #socket: CultMeshBrowserSocket | undefined;
  #socketGeneration = 0;
  #state: CultMeshConnectionState = "connecting";
  #disposed = false;
  #reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  #connectPromise: Promise<void> | undefined;
  #leases = new Map<string, CultMeshRawDocumentLease>();
  #openingLeases = new Map<string, CultMeshRawDocumentLease>();
  #pendingSnapshots = new Map<string, PendingSnapshot>();
  #pendingOperations = new Map<string, PendingOperation>();
  #stateWatchers = new Set<(state: CultMeshConnectionState) => void>();

  private constructor(options: CultMeshBrowserClientOptions) {
    requireText(options.verseId, "verseId");
    requireText(options.authorityRuntimeId, "authorityRuntimeId");
    requireText(options.runtimeId, "runtimeId");
    this.identity = { verseId: options.verseId, authorityRuntimeId: options.authorityRuntimeId };
    this.runtimeId = options.runtimeId;
    this.#options = {
      ...options,
      reconnectDelayMs: options.reconnectDelayMs ?? 250,
      connectTimeoutMs: options.connectTimeoutMs ?? 10_000,
      requestTimeoutMs: options.requestTimeoutMs ?? 10_000,
      maxFrameBytes: options.maxFrameBytes ?? 4 * 1024 * 1024,
      createId: options.createId ?? (() => crypto.randomUUID()),
      socketFactory: options.socketFactory ?? (endpoint => new WebSocket(endpoint)),
      trust: options.trust ?? { mode: "authenticated-remote", odinRoots: [] },
    };
  }

  static async connect(options: CultMeshBrowserClientOptions): Promise<CultMeshBrowserClient> {
    const client = new CultMeshBrowserClient(options);
    await client.connectSocket();
    return client;
  }

  get state(): CultMeshConnectionState {
    return this.#state;
  }

  watchState(watcher: (state: CultMeshConnectionState) => void): () => void {
    this.#stateWatchers.add(watcher);
    watcher(this.#state);
    return () => this.#stateWatchers.delete(watcher);
  }

  async leaseRawDocument(options: CultMeshRawDocumentLeaseOptions): Promise<CultMeshRawDocumentLease> {
    this.throwIfDisposed();
    requireText(options.schemaId, "schemaId");
    requireText(options.recordKey, "recordKey");
    const subscriptionId = options.subscriptionId ?? `lease:${this.#options.createId()}`;
    if (this.#leases.has(subscriptionId) || this.#openingLeases.has(subscriptionId)) {
      throw new Error(`CultMesh subscription '${subscriptionId}' is already leased.`);
    }
    const lease = new CultMeshRawDocumentLease(this, { ...options, subscriptionId });
    this.#openingLeases.set(subscriptionId, lease);
    try {
      await this.subscribe(lease);
      this.#openingLeases.delete(subscriptionId);
      this.#leases.set(subscriptionId, lease);
      return lease;
    } catch (error) {
      this.#openingLeases.delete(subscriptionId);
      throw error;
    }
  }

  async invoke(options: CultMeshOperationOptions): Promise<CultNetOperationResponseMessage> {
    this.throwIfDisposed();
    requireText(options.serviceId, "serviceId");
    requireText(options.operation, "operation");
    requireText(options.payloadSchema, "payloadSchema");
    if (options.targetRuntimeId && options.targetRuntimeId !== this.identity.authorityRuntimeId) {
      throw new Error(
        `CultMesh operation target '${options.targetRuntimeId}' does not match connected authority '${this.identity.authorityRuntimeId}'.`,
      );
    }
    await this.ensureConnected();
    const messageId = options.idempotencyKey ?? this.#options.createId();
    requireText(messageId, "operation idempotencyKey");
    if (this.#pendingOperations.has(messageId)) {
      throw new Error(`CultMesh operation '${messageId}' is already in flight.`);
    }
    const request: CultNetOperationRequestMessage = {
      schemaVersion: "cultnet.operation_request.v0",
      messageId,
      serviceId: options.serviceId,
      operation: options.operation,
      payloadSchema: options.payloadSchema,
      payloadEncoding: "messagepack-base64",
      payload: bytesToBase64(encode(options.payload)),
      sourceRuntimeId: this.runtimeId,
      targetRuntimeId: this.identity.authorityRuntimeId,
    };
    const response = new Promise<CultNetOperationResponseMessage>((resolve, reject) => {
      const timer = setTimeout(() => {
        if (!this.#pendingOperations.delete(messageId)) return;
        reject(new Error(`CultMesh operation '${messageId}' timed out.`));
      }, this.#options.requestTimeoutMs);
      this.#pendingOperations.set(messageId, { request, resolve, reject, timer });
    });
    try {
      this.send(request);
    } catch (error) {
      this.scheduleReconnect();
    }
    return response;
  }

  async refreshRoute(): Promise<void> {
    this.throwIfDisposed();
    this.#socketGeneration++;
    const socket = this.#socket;
    this.#socket = undefined;
    socket?.close(1012, "CultMesh route refresh");
    await this.connectSocket();
  }

  releaseLease(lease: CultMeshRawDocumentLease): void {
    if (this.#leases.get(lease.subscriptionId) !== lease) return;
    this.#leases.delete(lease.subscriptionId);
    for (const [messageId, pending] of this.#pendingSnapshots) {
      if (pending.lease !== lease) continue;
      this.#pendingSnapshots.delete(messageId);
      clearTimeout(pending.timer);
      pending.reject(new Error("CultMesh document lease was disposed before its snapshot arrived."));
    }
    if (this.#state === "connected") {
      const message: CultNetDatabaseUnsubscribeMessage = {
        schemaVersion: "cultnet.database_unsubscribe.v0",
        messageId: this.#options.createId(),
        subscriptionId: lease.subscriptionId,
      };
      this.send(message);
    }
  }

  async dispose(): Promise<void> {
    if (this.#disposed) return;
    this.#disposed = true;
    this.setState("disposed");
    if (this.#reconnectTimer) clearTimeout(this.#reconnectTimer);
    this.#reconnectTimer = undefined;
    const error = new Error("CultMesh browser client was disposed.");
    for (const pending of this.#pendingSnapshots.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    for (const pending of this.#pendingOperations.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pendingSnapshots.clear();
    this.#pendingOperations.clear();
    this.#leases.clear();
    this.#openingLeases.clear();
    const socket = this.#socket;
    this.#socket = undefined;
    socket?.close(1000, "CultMesh client disposed");
  }

  async [Symbol.asyncDispose](): Promise<void> {
    await this.dispose();
  }

  private async subscribe(lease: CultMeshRawDocumentLease): Promise<void> {
    await this.ensureConnected();
    const messageId = this.#options.createId();
    const ready = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        if (!this.#pendingSnapshots.delete(messageId)) return;
        reject(new Error(`CultMesh subscription '${lease.subscriptionId}' timed out.`));
      }, this.#options.requestTimeoutMs);
      this.#pendingSnapshots.set(messageId, { lease, resolve, reject, timer });
    });
    const request: CultNetDatabaseSubscribeMessage = {
      schemaVersion: "cultnet.database_subscribe.v0",
      messageId,
      subscriptionId: lease.subscriptionId,
      schemaIds: [lease.schemaId],
      recordKeys: [lease.recordKey],
      includeSnapshot: true,
      consumerRuntimeId: this.runtimeId,
    };
    try {
      this.send(request);
    } catch (error) {
      const pending = this.#pendingSnapshots.get(messageId);
      if (pending) clearTimeout(pending.timer);
      this.#pendingSnapshots.delete(messageId);
      throw error;
    }
    return ready;
  }

  private ensureConnected(): Promise<void> {
    if (this.#state === "connected" && this.#socket?.readyState === 1) return Promise.resolve();
    return this.connectSocket();
  }

  private connectSocket(): Promise<void> {
    this.throwIfDisposed();
    if (this.#connectPromise) return this.#connectPromise;
    const generation = ++this.#socketGeneration;
    this.setState(this.#socket ? "reconnecting" : "connecting");
    this.#connectPromise = this.openSocket(generation).finally(() => {
      this.#connectPromise = undefined;
    });
    return this.#connectPromise;
  }

  private async openSocket(generation: number): Promise<void> {
    const route = await this.#options.rendezvous.resolve(this.identity);
    if (route.verseId !== this.identity.verseId ||
        route.authorityRuntimeId !== this.identity.authorityRuntimeId) {
      throw new Error("CultMesh rendezvous returned a route for the wrong stable identity.");
    }
    await verifyAuthorityRoute(route, this.#options.trust);
    const endpoint = new URL(route.endpoint);
    if (endpoint.protocol !== "ws:" && endpoint.protocol !== "wss:") {
      throw new Error(`CultMesh browser route must use ws:// or wss://, got '${route.endpoint}'.`);
    }
    const socket = this.#options.socketFactory(route.endpoint);
    socket.binaryType = "arraybuffer";
    try {
      await new Promise<void>((resolve, reject) => {
        let opened = false;
        let settled = false;
        const handshakeId = this.#options.createId();
        const clientNonce = randomNonce();
        const sessionRequest: CultMeshSessionOpenMessage = {
          schemaVersion: "cultmesh.session_open.v2",
          messageId: handshakeId,
          sourceRuntimeId: this.runtimeId,
          verseId: this.identity.verseId,
          authorityRuntimeId: this.identity.authorityRuntimeId,
          protocolId: route.protocolId ?? "cultmesh.documents.v1",
          routeGeneration: route.generation,
          clientNonce,
        };
        const finish = (error?: Error) => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          if (error) reject(error);
          else resolve();
        };
        const timer = setTimeout(
          () => finish(new Error(
            opened
              ? `CultMesh authority handshake timed out for '${route.endpoint}'.`
              : `CultMesh WebSocket open timed out for '${route.endpoint}'.`,
          )),
          this.#options.connectTimeoutMs,
        );
        socket.onopen = () => {
          opened = true;
          try {
            sendSocketMessage(socket, {
              schemaVersion: "cultnet.hello.v0",
              runtimeId: this.runtimeId,
              runtimeKind: "browser",
              supportedMessageVersions: [
                "cultmesh.session_open.v2",
                "cultmesh.session_accepted.v2",
                "cultnet.database_subscribe.v0",
                "cultnet.database_unsubscribe.v0",
                "cultnet.database_change_raw.v0",
                "cultnet.operation_request.v0",
                "cultnet.operation_response.v0",
              ],
            }, this.#options.maxFrameBytes);
            sendSocketMessage(socket, sessionRequest, this.#options.maxFrameBytes);
          } catch (error) {
            finish(error instanceof Error ? error : new Error(String(error)));
          }
        };
        socket.onmessage = event => {
          void decodeSocketMessage(event, this.#options.maxFrameBytes).then(message => {
            if (message.schemaVersion === "cultnet.error.v0") {
              finish(new Error(message.error));
              return;
            }
            if (message.schemaVersion !== "cultmesh.session_accepted.v2" || message.messageId !== handshakeId) return;
            void validateSessionAcceptance(
              message,
              sessionRequest,
              route,
              this.#options.trust,
            ).then(finish).catch(error => finish(error instanceof Error ? error : new Error(String(error))));
          }).catch(error => finish(error instanceof Error ? error : new Error(String(error))));
        };
        socket.onerror = () => finish(new Error(`CultMesh WebSocket could not open '${route.endpoint}'.`));
        socket.onclose = () => finish(new Error(`CultMesh WebSocket closed before proving authority at '${route.endpoint}'.`));
      });
    } catch (error) {
      socket.close(1000, "CultMesh route open failed");
      throw error;
    }
    if (this.#disposed || generation !== this.#socketGeneration) {
      socket.close(1000, "Stale CultMesh route");
      return;
    }
    this.#socket = socket;
    socket.onmessage = event => { void this.receive(event, generation); };
    socket.onerror = () => undefined;
    socket.onclose = () => this.handleClosed(generation);
    this.setState("connected");
    try {
      await Promise.all([...this.#leases.values()].map(lease => this.subscribe(lease)));
      for (const pending of this.#pendingOperations.values()) this.send(pending.request);
    } catch (error) {
      if (this.#socket === socket) this.#socket = undefined;
      socket.onclose = null;
      socket.close(1000, "CultMesh session initialization failed");
      throw error;
    }
  }

  private handleClosed(generation: number): void {
    if (this.#disposed || generation !== this.#socketGeneration) return;
    this.#socket = undefined;
    const error = new Error("CultMesh route closed before the provider response arrived.");
    for (const pending of this.#pendingSnapshots.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pendingSnapshots.clear();
    this.setState("reconnecting");
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      void this.connectSocket().catch(() => this.handleClosed(this.#socketGeneration));
    }, this.#options.reconnectDelayMs);
  }

  private async receive(event: MessageEvent, generation: number): Promise<void> {
    if (this.#disposed || generation !== this.#socketGeneration) return;
    let message: CultNetMessage;
    try {
      message = await decodeSocketMessage(event, this.#options.maxFrameBytes);
    } catch (error) {
      const oversized = error instanceof Error && error.message.includes("exceeds");
      this.#socket?.close(
        oversized ? 1009 : 1003,
        oversized ? "CultNet frame exceeds configured maximum" : "CultNet requires binary WebSocket frames",
      );
      return;
    }
    switch (message.schemaVersion) {
      case "cultnet.snapshot_response_raw.v0":
        this.applySnapshot(message);
        return;
      case "cultnet.database_change_raw.v0":
        this.applyChange(message);
        return;
      case "cultnet.operation_response.v0": {
        const pending = this.#pendingOperations.get(message.messageId);
        if (!pending) return;
        if (message.sourceRuntimeId !== this.identity.authorityRuntimeId) {
          this.#pendingOperations.delete(message.messageId);
          clearTimeout(pending.timer);
          pending.reject(new Error(
            `CultMesh operation response came from '${message.sourceRuntimeId ?? "unknown"}', expected '${this.identity.authorityRuntimeId}'.`,
          ));
          this.#socket?.close(1008, "CultMesh authority identity changed");
          return;
        }
        this.#pendingOperations.delete(message.messageId);
        clearTimeout(pending.timer);
        if (message.payloadSchema === cultNetOperationFailureSchema) {
          try {
            pending.reject(new CultMeshBrowserOperationError(
              message,
              decodeCultNetOperationPayload<CultNetOperationFailure>(message),
            ));
          } catch (error) {
            pending.reject(error instanceof Error
              ? error
              : new Error("CultMesh operation failure payload was malformed."));
          }
          return;
        }
        pending.resolve(message);
        return;
      }
      case "cultnet.error.v0":
        this.rejectCorrelated(message.error);
        return;
      default:
        return;
    }
  }

  private applySnapshot(message: CultNetSnapshotResponseRawMessage): void {
    const pending = this.#pendingSnapshots.get(message.messageId);
    if (!pending) return;
    this.#pendingSnapshots.delete(message.messageId);
    clearTimeout(pending.timer);
    const matches = message.documents.filter(record =>
      record.recordKey === pending.lease.recordKey && recordMatchesSchema(record, pending.lease.schemaId));
    if (matches.length > 1) {
      pending.reject(new Error(`CultMesh snapshot returned duplicate record '${pending.lease.recordKey}'.`));
      return;
    }
    pending.lease.apply(matches[0]);
    pending.resolve();
  }

  private applyChange(message: CultNetDatabaseChangeRawMessage): void {
    const lease = this.#leases.get(message.subscriptionId) ?? this.#openingLeases.get(message.subscriptionId);
    if (!lease) return;
    if (message.changeKind === "removed") {
      if (message.recordKey === lease.recordKey) lease.apply(undefined);
      return;
    }
    const record = message.document;
    if (record?.recordKey === lease.recordKey && recordMatchesSchema(record, lease.schemaId)) lease.apply(record);
  }

  private rejectCorrelated(reason: string): void {
    if (this.#pendingOperations.size === 1) {
      const [messageId, pending] = this.#pendingOperations.entries().next().value!;
      this.#pendingOperations.delete(messageId);
      clearTimeout(pending.timer);
      pending.reject(new Error(reason));
    }
  }

  private send(message: CultNetMessage): void {
    const socket = this.#socket;
    if (!socket || socket.readyState !== 1) throw new Error("CultMesh browser route is not connected.");
    const payload = encode(encodeCultNetMessageForWire(message));
    if (payload.byteLength > this.#options.maxFrameBytes) {
      throw new Error(`CultMesh message exceeds the ${this.#options.maxFrameBytes}-byte limit.`);
    }
    socket.send(payload);
  }

  private setState(state: CultMeshConnectionState): void {
    if (this.#state === state) return;
    this.#state = state;
    for (const watcher of this.#stateWatchers) watcher(state);
  }

  private scheduleReconnect(): void {
    if (this.#disposed || this.#reconnectTimer) return;
    this.setState("reconnecting");
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      void this.connectSocket().catch(() => this.scheduleReconnect());
    }, this.#options.reconnectDelayMs);
  }

  private throwIfDisposed(): void {
    if (this.#disposed) throw new Error("CultMesh browser client is disposed.");
  }
}

export function decodeCultNetPayload<T>(record: CultNetRawDocumentRecord): T {
  if (record.payloadEncoding !== "messagepack") {
    throw new Error(`Unsupported CultNet document payload '${record.payloadEncoding}'.`);
  }
  return decode(record.payload) as T;
}

function recordMatchesSchema(record: CultNetRawDocumentRecord, requestedSchema: string): boolean {
  return record.schemaId === requestedSchema ||
    record.schemaName === requestedSchema ||
    record.schemaVersion === requestedSchema;
}

export function decodeCultNetOperationPayload<T>(response: CultNetOperationResponseMessage): T {
  if (response.payloadEncoding !== "messagepack-base64") {
    throw new Error(`Unsupported CultNet operation payload '${response.payloadEncoding ?? "unspecified"}'.`);
  }
  return decode(base64ToBytes(response.payload)) as T;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

function base64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}

function requireWebSocketEndpoint(value: string, field: string): URL {
  let endpoint: URL;
  try {
    endpoint = new URL(value);
  } catch {
    throw new Error(`${field} '${value}' is not a valid URL.`);
  }
  if (endpoint.protocol !== "ws:" && endpoint.protocol !== "wss:") {
    throw new Error(`${field} must use ws:// or wss://, got '${value}'.`);
  }
  return endpoint;
}

function sendSocketMessage(socket: CultMeshBrowserSocket, message: CultNetMessage, maxFrameBytes: number): void {
  const payload = encode(encodeCultNetMessageForWire(message));
  if (payload.byteLength > maxFrameBytes) {
    throw new Error(`CultNet message exceeds the ${maxFrameBytes}-byte limit.`);
  }
  socket.send(payload);
}

async function decodeSocketMessage(event: MessageEvent, maxFrameBytes: number): Promise<CultNetMessage> {
  let payload: Uint8Array;
  if (event.data instanceof ArrayBuffer) {
    payload = new Uint8Array(event.data);
  } else if (ArrayBuffer.isView(event.data)) {
    payload = new Uint8Array(event.data.buffer, event.data.byteOffset, event.data.byteLength);
  } else if (event.data instanceof Blob) {
    payload = new Uint8Array(await event.data.arrayBuffer());
  } else {
    throw new Error("CultNet requires binary WebSocket frames.");
  }
  if (payload.byteLength > maxFrameBytes) {
    throw new Error(`CultNet frame exceeds the ${maxFrameBytes}-byte limit.`);
  }
  return parseCultNetMessage(decode(payload));
}

function selectOdinRoute(
  response: CultMeshVerseCatalogResponseMessage,
  identity: CultMeshBrowserIdentity,
): CultMeshBrowserRoute {
  const matchingVerses = response.verses.filter(verse => verse.verseId === identity.verseId);
  if (matchingVerses.length !== 1) {
    throw new Error(
      `Odin returned ${matchingVerses.length} routes for Verse '${identity.verseId}' authority runtime '${identity.authorityRuntimeId}'.`,
    );
  }
  const verse = matchingVerses[0];
  if (verse.authorityRoutes == null && verse.authorityRuntimeIds.length !== 1) {
    throw new Error(
      `Odin returned ambiguous legacy routes for Verse '${identity.verseId}'. Explicit authority route bindings are required.`,
    );
  }
  if (verse.authorityRoutes == null && verse.authorityRuntimeIds[0] !== identity.authorityRuntimeId) {
    throw new Error(
      `Odin advertised no route for authority runtime '${identity.authorityRuntimeId}'.`,
    );
  }
  const boundRoutes = verse.authorityRoutes ?? verse.discoveryEndpoints.map(endpoint => ({
    authorityRuntimeId: verse.authorityRuntimeIds[0],
    endpoint,
    protocolIds: ["cultmesh.documents.v1"],
    priority: 0,
    generation: [verse.compatibility.transportVersion, verse.compatibility.rulesHash, endpoint].join(":"),
    certificate: undefined,
  }));
  const routes = boundRoutes.filter(route =>
    route.authorityRuntimeId === identity.authorityRuntimeId &&
    route.protocolIds.includes("cultmesh.documents.v1") &&
    (() => {
    try {
      requireWebSocketEndpoint(route.endpoint, "Odin provider route");
      return true;
    } catch {
      return false;
    }
  })()).sort((left, right) => left.priority - right.priority || left.endpoint.localeCompare(right.endpoint));
  if (routes.length === 0) {
    throw new Error(`Odin advertised no browser-compatible route for authority runtime '${identity.authorityRuntimeId}'.`);
  }
  const route = routes[0];
  return {
    ...identity,
    endpoint: route.endpoint,
    protocolId: "cultmesh.documents.v1",
    protocolIds: route.protocolIds,
    priority: route.priority,
    generation: route.generation,
    ...(route.certificate ? { certificate: {
      providerKey: {
        keyId: route.certificate.providerKeyId,
        x: route.certificate.providerPublicKeyX,
        y: route.certificate.providerPublicKeyY,
      },
      odinKeyId: route.certificate.odinKeyId,
      issuedAtUnixMilliseconds: route.certificate.issuedAtUnixMilliseconds,
      expiresAtUnixMilliseconds: route.certificate.expiresAtUnixMilliseconds,
      signature: route.certificate.signature,
    } } : {}),
  };
}

async function validateSessionAcceptance(
  message: CultMeshSessionAcceptedMessage,
  request: CultMeshSessionOpenMessage,
  route: CultMeshBrowserRoute,
  trust: CultMeshBrowserAuthorityTrustPolicy,
): Promise<Error | undefined> {
  if (!message.accepted) return new Error(message.error ?? "CultMesh authority rejected the session.");
  const protocolId = route.protocolId ?? "cultmesh.documents.v1";
  if (message.verseId !== request.verseId ||
      message.authorityRuntimeId !== request.authorityRuntimeId ||
      message.protocolId !== protocolId ||
      message.routeGeneration !== route.generation ||
      message.clientNonce !== request.clientNonce) {
    return new Error(
      `CultMesh route proved '${message.verseId}/${message.authorityRuntimeId}/${message.protocolId}/${message.routeGeneration}', ` +
      `expected '${request.verseId}/${request.authorityRuntimeId}/${protocolId}/${route.generation}'.`,
    );
  }
  if (trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint) && !route.certificate) return undefined;
  const providerKey = route.certificate?.providerKey;
  if (!providerKey || message.providerKeyId !== providerKey.keyId || !message.providerSignature) {
    return new Error("CultMesh authority did not prove possession of the Odin-certified provider key.");
  }
  if (!await verifyP256(providerKey, canonicalSession(request, route.endpoint), message.providerSignature)) {
    return new Error("CultMesh provider session proof is invalid.");
  }
  return undefined;
}

async function verifyAuthorityRoute(
  route: CultMeshBrowserRoute,
  trust: CultMeshBrowserAuthorityTrustPolicy,
): Promise<void> {
  const certificate = route.certificate;
  if (!certificate) {
    if (trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint)) return;
    throw new Error("Remote CultMesh routes require an Odin-signed authority certificate.");
  }
  const endpoint = new URL(route.endpoint);
  if (endpoint.protocol !== "wss:" && !(trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint))) {
    throw new Error("Authenticated remote CultMesh browser routes require wss:// channel protection.");
  }
  const now = trust.now?.() ?? Date.now();
  if (now < certificate.issuedAtUnixMilliseconds || now >= certificate.expiresAtUnixMilliseconds) {
    throw new Error("The Odin route certificate is not currently valid.");
  }
  const root = trust.odinRoots?.find(candidate => candidate.keyId === certificate.odinKeyId);
  if (!root) throw new Error(`Odin key '${certificate.odinKeyId}' is not trusted by this consumer.`);
  if (!await verifyP256(root, canonicalRoute(route), certificate.signature)) {
    throw new Error("The Odin route certificate signature is invalid.");
  }
}

function canonicalRoute(route: CultMeshBrowserRoute): Uint8Array {
  const certificate = route.certificate!;
  return canonicalFields(
    "gamecult.cultmesh.route-certificate.v1",
    route.verseId,
    route.authorityRuntimeId,
    route.endpoint,
    [...(route.protocolIds ?? [route.protocolId ?? "cultmesh.documents.v1"])].sort().join("\u001f"),
    String(route.priority ?? 0),
    route.generation,
    certificate.providerKey.keyId,
    certificate.providerKey.x,
    certificate.providerKey.y,
    certificate.odinKeyId,
    String(certificate.issuedAtUnixMilliseconds),
    String(certificate.expiresAtUnixMilliseconds),
  );
}

function canonicalSession(request: CultMeshSessionOpenMessage, endpoint: string): Uint8Array {
  return canonicalFields(
    "gamecult.cultmesh.session-proof.v1",
    request.clientNonce,
    request.messageId,
    request.sourceRuntimeId,
    request.verseId,
    request.authorityRuntimeId,
    request.protocolId,
    endpoint,
    request.routeGeneration,
  );
}

function canonicalFields(...values: string[]): Uint8Array {
  const encoder = new TextEncoder();
  const encoded = values.map(value => encoder.encode(value));
  const total = encoded.reduce((sum, value) => sum + 4 + value.byteLength, 0);
  const result = new Uint8Array(total);
  const view = new DataView(result.buffer);
  let offset = 0;
  for (const value of encoded) {
    view.setUint32(offset, value.byteLength, false);
    offset += 4;
    result.set(value, offset);
    offset += value.byteLength;
  }
  return result;
}

async function verifyP256(
  key: CultMeshBrowserP256PublicKey,
  payload: Uint8Array,
  signatureBase64: string,
): Promise<boolean> {
  try {
    const x = base64ToBytes(key.x);
    const y = base64ToBytes(key.y);
    const signature = base64ToBytes(signatureBase64);
    if (x.byteLength !== 32 || y.byteLength !== 32 || signature.byteLength !== 64) return false;
    const raw = new Uint8Array(65);
    raw[0] = 4;
    raw.set(x, 1);
    raw.set(y, 33);
    const publicKey = await crypto.subtle.importKey(
      "raw",
      raw.slice().buffer as ArrayBuffer,
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      publicKey,
      signature.slice().buffer as ArrayBuffer,
      payload.slice().buffer as ArrayBuffer,
    );
  } catch {
    return false;
  }
}

function randomNonce(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return bytesToBase64(bytes);
}

function isLoopbackEndpoint(value: string): boolean {
  try {
    const host = new URL(value).hostname.replace(/^\[|\]$/g, "").toLowerCase();
    return host === "localhost" || host === "127.0.0.1" || host === "::1";
  } catch {
    return false;
  }
}

function requireText(value: string, field: string): void {
  if (!value || value.trim().length === 0) throw new Error(`CultMesh browser ${field} is required.`);
}
