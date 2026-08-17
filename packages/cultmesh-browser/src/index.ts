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
} from "cultnet-ts/contracts";

export interface CultMeshBrowserIdentity {
  verseId: string;
  providerId: string;
}

export interface CultMeshBrowserRoute extends CultMeshBrowserIdentity {
  endpoint: string;
  generation?: string;
}

export interface CultMeshBrowserRendezvous {
  resolve(identity: CultMeshBrowserIdentity): Promise<CultMeshBrowserRoute>;
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

export interface CultMeshBrowserClientOptions extends CultMeshBrowserIdentity {
  runtimeId: string;
  rendezvous: CultMeshBrowserRendezvous;
  socketFactory?: (endpoint: string) => CultMeshBrowserSocket;
  reconnectDelayMs?: number;
  connectTimeoutMs?: number;
  requestTimeoutMs?: number;
  maxFrameBytes?: number;
  createId?: () => string;
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
  targetRuntimeId?: string;
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
    requireText(options.providerId, "providerId");
    requireText(options.runtimeId, "runtimeId");
    this.identity = { verseId: options.verseId, providerId: options.providerId };
    this.runtimeId = options.runtimeId;
    this.#options = {
      ...options,
      reconnectDelayMs: options.reconnectDelayMs ?? 250,
      connectTimeoutMs: options.connectTimeoutMs ?? 10_000,
      requestTimeoutMs: options.requestTimeoutMs ?? 10_000,
      maxFrameBytes: options.maxFrameBytes ?? 4 * 1024 * 1024,
      createId: options.createId ?? (() => crypto.randomUUID()),
      socketFactory: options.socketFactory ?? (endpoint => new WebSocket(endpoint)),
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
      ...(options.targetRuntimeId ? { targetRuntimeId: options.targetRuntimeId } : {}),
    };
    const response = new Promise<CultNetOperationResponseMessage>((resolve, reject) => {
      const timer = setTimeout(() => {
        if (!this.#pendingOperations.delete(messageId)) return;
        reject(new Error(`CultMesh operation '${messageId}' timed out.`));
      }, this.#options.requestTimeoutMs);
      this.#pendingOperations.set(messageId, { resolve, reject, timer });
    });
    try {
      this.send(request);
    } catch (error) {
      const pending = this.#pendingOperations.get(messageId);
      if (pending) clearTimeout(pending.timer);
      this.#pendingOperations.delete(messageId);
      throw error;
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
    if (route.verseId !== this.identity.verseId || route.providerId !== this.identity.providerId) {
      throw new Error("CultMesh rendezvous returned a route for the wrong stable identity.");
    }
    const endpoint = new URL(route.endpoint);
    if (endpoint.protocol !== "ws:" && endpoint.protocol !== "wss:") {
      throw new Error(`CultMesh browser route must use ws:// or wss://, got '${route.endpoint}'.`);
    }
    const socket = this.#options.socketFactory(route.endpoint);
    socket.binaryType = "arraybuffer";
    try {
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(
          () => reject(new Error(`CultMesh WebSocket open timed out for '${route.endpoint}'.`)),
          this.#options.connectTimeoutMs,
        );
        socket.onopen = () => { clearTimeout(timer); resolve(); };
        socket.onerror = () => { clearTimeout(timer); reject(new Error(`CultMesh WebSocket could not open '${route.endpoint}'.`)); };
        socket.onclose = () => { clearTimeout(timer); reject(new Error(`CultMesh WebSocket closed while opening '${route.endpoint}'.`)); };
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
      this.send({
        schemaVersion: "cultnet.hello.v0",
        runtimeId: this.runtimeId,
        runtimeKind: "browser",
        supportedMessageVersions: [
          "cultnet.database_subscribe.v0",
          "cultnet.database_unsubscribe.v0",
          "cultnet.database_change_raw.v0",
          "cultnet.operation_request.v0",
          "cultnet.operation_response.v0",
        ],
      });
      await Promise.all([...this.#leases.values()].map(lease => this.subscribe(lease)));
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
    for (const pending of this.#pendingOperations.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pendingSnapshots.clear();
    this.#pendingOperations.clear();
    this.setState("reconnecting");
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      void this.connectSocket().catch(() => this.handleClosed(this.#socketGeneration));
    }, this.#options.reconnectDelayMs);
  }

  private async receive(event: MessageEvent, generation: number): Promise<void> {
    if (this.#disposed || generation !== this.#socketGeneration) return;
    let payload: Uint8Array;
    if (event.data instanceof ArrayBuffer) {
      payload = new Uint8Array(event.data);
    } else if (ArrayBuffer.isView(event.data)) {
      payload = new Uint8Array(event.data.buffer, event.data.byteOffset, event.data.byteLength);
    } else if (event.data instanceof Blob) {
      payload = new Uint8Array(await event.data.arrayBuffer());
    } else {
      this.#socket?.close(1003, "CultNet requires binary WebSocket frames");
      return;
    }
    if (payload.byteLength > this.#options.maxFrameBytes) {
      this.#socket?.close(1009, "CultNet frame exceeds configured maximum");
      return;
    }
    const message = parseCultNetMessage(decode(payload));
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
        this.#pendingOperations.delete(message.messageId);
        clearTimeout(pending.timer);
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
      record.schemaId === pending.lease.schemaId && record.recordKey === pending.lease.recordKey);
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
      if (message.recordKey === lease.recordKey && message.schemaId === lease.schemaId) lease.apply(undefined);
      return;
    }
    const record = message.document;
    if (record?.recordKey === lease.recordKey && record.schemaId === lease.schemaId) lease.apply(record);
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

function requireText(value: string, field: string): void {
  if (!value || value.trim().length === 0) throw new Error(`CultMesh browser ${field} is required.`);
}
