import { randomUUID } from "node:crypto";
import { createSocket, type Socket } from "node:dgram";
import { encode } from "@msgpack/msgpack";
import {
  CultNetPeer,
  CultNetRudpSocketTransportConnection,
  type CultNetMessage,
  type CultNetOperationRequestMessage,
  type CultNetOperationResponseMessage,
  type CultNetRawDocumentRecord,
} from "cultnet-ts";

import type {
  CultMeshProviderCommand,
  CultMeshProviderCommandListener,
  CultMeshProviderCommandReceipt,
  CultMeshProviderConnection,
  CultMeshProviderIdentity,
  CultMeshProviderLease,
  CultMeshProviderPublication,
  CultMeshProviderRegistration,
  CultMeshProviderTransport,
  CultMeshProviderUnsubscribe,
  CultMeshProviderWithdrawal,
} from "./provider-session";
import {
  CULTMESH_PROVIDER_SESSION_SERVICE_ID,
  assertMutationAcceptance,
  assertProviderLease,
  cultMeshProviderSessionOperations,
  cultMeshProviderSessionSchemas,
  decodeProviderCommandDocument,
  encodeProviderConnectEvidence,
  decodeProviderSessionPayload,
  encodeProviderSessionPayload,
  type CultMeshProviderCommandReceiptWire,
  type CultMeshProviderLeaseWire,
  type CultMeshProviderMutationAcceptanceWire,
} from "./provider-session-wire";

export interface CultMeshProviderRudpTransportOptions {
  readonly endpoint: string;
  readonly runtimeId: string;
  readonly connectionId: number;
  readonly bindHost?: string;
  readonly connectTimeoutMs?: number;
  readonly operationTimeoutMs?: number;
  readonly maxFragmentBytes?: number;
  readonly sessionToken?: string;
  readonly socketFactory?: () => Socket;
}

/**
 * Private-development provider transport. Public deployment additionally needs
 * an authenticated CultNet session whose claims authorize the provider identity.
 */
export class CultMeshProviderRudpTransport implements CultMeshProviderTransport {
  readonly #options: CultMeshProviderRudpTransportOptions;

  public constructor(options: CultMeshProviderRudpTransportOptions) {
    if (!options.endpoint) throw new Error("CultMesh provider RUDP transport requires endpoint.");
    if (!options.runtimeId) throw new Error("CultMesh provider RUDP transport requires runtimeId.");
    if (!Number.isInteger(options.connectionId)) throw new Error("CultMesh provider RUDP transport requires integer connectionId.");
    this.#options = options;
  }

  public async connect(
    identity: CultMeshProviderIdentity,
    signal: AbortSignal,
  ): Promise<CultMeshProviderConnection> {
    if (signal.aborted) throw abortError();
    const endpoint = parseEndpoint(this.#options.endpoint);
    const socket = this.#options.socketFactory?.() ?? createSocket(endpoint.host.includes(":") ? "udp6" : "udp4");
    await bindSocket(socket, this.#options.bindHost ?? (endpoint.host.includes(":") ? "::" : "0.0.0.0"), signal);
    const transport = new CultNetRudpSocketTransportConnection({
      mode: "client",
      runtimeId: this.#options.runtimeId,
      transportId: `${this.#options.runtimeId}.cultmesh-provider-session`,
      socket,
      remoteHost: endpoint.host,
      remotePort: endpoint.port,
      connectionId: this.#options.connectionId,
      maxFragmentBytes: this.#options.maxFragmentBytes ?? 2_048,
    });
    const peer = new CultNetPeer(transport, { wireContract: "cultnet.schema.v0" });
    transport.connect(Buffer.from(encodeProviderConnectEvidence({
      clientSessionId: randomUUID(),
      sessionToken: this.#options.sessionToken ?? null,
    })));
    try {
      await waitUntilConnected(transport, signal, this.#options.connectTimeoutMs ?? 2_000);
      return new RudpProviderConnection(identity, peer, transport, this.#options.operationTimeoutMs ?? 5_000);
    } catch (error) {
      transport.close();
      throw error;
    }
  }
}

class RudpProviderConnection implements CultMeshProviderConnection {
  readonly #identity: CultMeshProviderIdentity;
  readonly #peer: CultNetPeer;
  readonly #transport: CultNetRudpSocketTransportConnection;
  readonly #timeoutMs: number;
  readonly #listeners = new Set<CultMeshProviderCommandListener>();
  readonly #pending = new Map<string, {
    operation: string;
    payloadSchema: string;
    resolve: (response: CultNetOperationResponseMessage) => void;
    reject: (error: Error) => void;
    timer: NodeJS.Timeout;
  }>();
  readonly #publications = new Map<string, CultMeshProviderPublication>();
  readonly #bufferedCommands = new Map<string, CultMeshProviderCommand>();
  #closed = false;
  #requestedLeaseDurationMs = 30_000;

  public constructor(
    identity: CultMeshProviderIdentity,
    peer: CultNetPeer,
    transport: CultNetRudpSocketTransportConnection,
    timeoutMs: number,
  ) {
    this.#identity = identity;
    this.#peer = peer;
    this.#transport = transport;
    this.#timeoutMs = timeoutMs;
    peer.on("message", message => this.#onMessage(message));
    peer.on("invalidMessage", error => this.#failPending(error));
    peer.on("close", () => this.#failPending(new Error("CultMesh provider RUDP connection closed.")));
    peer.on("error", error => this.#failPending(error));
  }

  public async register(
    registration: CultMeshProviderRegistration,
    signal: AbortSignal,
  ): Promise<CultMeshProviderLease> {
    this.#requestedLeaseDurationMs = registration.requestedLeaseDurationMs;
    const response = await this.#operation(
      cultMeshProviderSessionOperations.register,
      cultMeshProviderSessionSchemas.registration,
      cultMeshProviderSessionSchemas.lease,
      { ...registration.identity, requestedLeaseDurationMs: registration.requestedLeaseDurationMs },
      signal,
    );
    const wire = decodeProviderSessionPayload<CultMeshProviderLeaseWire>(response.payload);
    assertProviderLease(wire);
    assertIdentity(this.#identity, wire);
    return { leaseId: wire.leaseId, expiresAt: new Date(wire.expiresAtUtc) };
  }

  public async renew(lease: CultMeshProviderLease): Promise<CultMeshProviderLease> {
    const response = await this.#operation(
      cultMeshProviderSessionOperations.renew,
      cultMeshProviderSessionSchemas.leaseRenewal,
      cultMeshProviderSessionSchemas.lease,
      { leaseId: lease.leaseId, requestedLeaseDurationMs: this.#requestedLeaseDurationMs },
    );
    const wire = decodeProviderSessionPayload<CultMeshProviderLeaseWire>(response.payload);
    assertProviderLease(wire);
    assertIdentity(this.#identity, wire);
    return { leaseId: wire.leaseId, expiresAt: new Date(wire.expiresAtUtc) };
  }

  public async publish(
    publication: CultMeshProviderPublication,
    lease: CultMeshProviderLease,
  ): Promise<void> {
    const document: CultNetRawDocumentRecord = {
      schemaId: publication.schemaId,
      recordKey: publication.recordKey,
      storedAt: new Date().toISOString(),
      payloadEncoding: "messagepack",
      payload: encode(publication.value),
      sourceRuntimeId: this.#identity.serviceInstanceId,
      sourceAgentId: this.#identity.providerId,
      sourceRole: "provider",
    };
    await this.#accepted(cultMeshProviderSessionOperations.publicationPut, cultMeshProviderSessionSchemas.publicationPut, {
      leaseId: lease.leaseId,
      publicationId: publication.publicationId,
      document,
    });
    this.#publications.set(publication.publicationId, publication);
  }

  public async withdrawPublication(publicationId: string, lease: CultMeshProviderLease): Promise<void> {
    const publication = this.#publications.get(publicationId);
    if (!publication) throw new Error(`CultMesh provider publication ${publicationId} is not owned by this connection.`);
    await this.#accepted(cultMeshProviderSessionOperations.publicationDelete, cultMeshProviderSessionSchemas.publicationDelete, {
      leaseId: lease.leaseId,
      publicationId,
      schemaId: publication.schemaId,
      recordKey: publication.recordKey,
    });
    this.#publications.delete(publicationId);
  }

  public watchCommands(listener: CultMeshProviderCommandListener): CultMeshProviderUnsubscribe {
    this.#listeners.add(listener);
    for (const [commandId, command] of this.#bufferedCommands) {
      this.#bufferedCommands.delete(commandId);
      void Promise.resolve(listener(command)).catch(() => undefined);
    }
    return () => {
      this.#listeners.delete(listener);
    };
  }

  public async publishReceipt(
    receipt: CultMeshProviderCommandReceipt,
    lease: CultMeshProviderLease,
  ): Promise<void> {
    const wire: CultMeshProviderCommandReceiptWire = {
      receiptId: receipt.receiptId,
      commandId: receipt.commandId,
      commandKind: receipt.commandKind,
      providerId: receipt.providerId,
      serviceInstanceId: receipt.serviceInstanceId,
      state: receipt.state,
      completedAtUtc: receipt.completedAt.toISOString(),
      result: receipt.result,
      error: receipt.error,
    };
    await this.#accepted(cultMeshProviderSessionOperations.receiptPut, cultMeshProviderSessionSchemas.receiptPut, {
      leaseId: lease.leaseId,
      receipt: wire,
    });
  }

  public async withdraw(withdrawal: CultMeshProviderWithdrawal): Promise<void> {
    await this.#accepted(cultMeshProviderSessionOperations.withdraw, cultMeshProviderSessionSchemas.withdrawal, {
      leaseId: withdrawal.leaseId,
    });
    this.#publications.clear();
  }

  public close(): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#failPending(new Error("CultMesh provider RUDP connection closed."));
    this.#listeners.clear();
    this.#transport.close();
  }

  async #accepted(operation: string, payloadSchema: string, payload: unknown): Promise<CultMeshProviderMutationAcceptanceWire> {
    const response = await this.#operation(operation, payloadSchema, cultMeshProviderSessionSchemas.mutationAcceptance, payload);
    const acceptance = decodeProviderSessionPayload<CultMeshProviderMutationAcceptanceWire>(response.payload);
    assertMutationAcceptance(acceptance);
    return acceptance;
  }

  async #operation(
    operation: string,
    payloadSchema: string,
    responsePayloadSchema: string,
    payload: unknown,
    signal?: AbortSignal,
  ): Promise<CultNetOperationResponseMessage> {
    if (this.#closed) throw new Error("CultMesh provider RUDP connection is closed.");
    if (signal?.aborted) throw abortError();
    const messageId = randomUUID();
    const request: CultNetOperationRequestMessage = {
      schemaVersion: "cultnet.operation_request.v0",
      messageId,
      serviceId: CULTMESH_PROVIDER_SESSION_SERVICE_ID,
      operation,
      payloadSchema,
      payloadEncoding: "messagepack-base64",
      payload: encodeProviderSessionPayload(payload),
      sourceRuntimeId: this.#identity.serviceInstanceId,
    };
    return await new Promise<CultNetOperationResponseMessage>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(messageId);
        reject(new Error(`CultMesh provider operation ${operation} timed out.`));
      }, this.#timeoutMs);
      const abort = (): void => {
        clearTimeout(timer);
        this.#pending.delete(messageId);
        reject(abortError());
      };
      signal?.addEventListener("abort", abort, { once: true });
      this.#pending.set(messageId, {
        operation,
        payloadSchema: responsePayloadSchema,
        timer,
        resolve: response => {
          signal?.removeEventListener("abort", abort);
          if (response.serviceId !== CULTMESH_PROVIDER_SESSION_SERVICE_ID || response.operation !== operation) {
            reject(new Error(`CultMesh provider response correlation does not match ${operation}.`));
            return;
          }
          if (response.payloadEncoding !== "messagepack-base64") {
            reject(new Error(`CultMesh provider response for ${operation} has incompatible payload encoding.`));
            return;
          }
          if (response.status !== "ok") {
            reject(new Error(`CultMesh provider operation ${operation} was ${response.status}: ${(response.diagnostics ?? []).join("; ")}`));
            return;
          }
          if (response.payloadSchema !== responsePayloadSchema) {
            reject(new Error(`CultMesh provider response for ${operation} has incompatible payload schema.`));
            return;
          }
          resolve(response);
        },
        reject: error => {
          signal?.removeEventListener("abort", abort);
          reject(error);
        },
      });
      this.#peer.send(request);
    });
  }

  #onMessage(message: CultNetMessage): void {
    if (message.schemaVersion === "cultnet.operation_response.v0") {
      const pending = this.#pending.get(message.messageId);
      if (!pending) return;
      this.#pending.delete(message.messageId);
      clearTimeout(pending.timer);
      pending.resolve(message);
      return;
    }
    if (message.schemaVersion !== "cultnet.document_put_raw.v0") return;
    if (message.document.schemaId !== cultMeshProviderSessionSchemas.command) return;
    const wire = decodeProviderCommandDocument(message.document);
    if (wire.providerId !== this.#identity.providerId || wire.serviceInstanceId !== this.#identity.serviceInstanceId) return;
    const command: CultMeshProviderCommand = { ...wire };
    if (this.#listeners.size === 0) {
      this.#bufferedCommands.set(command.commandId, command);
      return;
    }
    for (const listener of [...this.#listeners]) void Promise.resolve(listener(command)).catch(() => undefined);
  }

  #failPending(error: Error): void {
    for (const [messageId, pending] of this.#pending) {
      this.#pending.delete(messageId);
      clearTimeout(pending.timer);
      pending.reject(error);
    }
  }
}

function assertIdentity(identity: CultMeshProviderIdentity, lease: CultMeshProviderLeaseWire): void {
  for (const key of ["providerId", "serviceInstanceId", "endpointId", "verseId"] as const) {
    if (identity[key] !== lease[key]) throw new Error(`CultMesh provider lease ${key} does not match the registered identity.`);
  }
}

function parseEndpoint(endpoint: string): { host: string; port: number } {
  const url = new URL(endpoint);
  const port = Number(url.port);
  if (url.protocol !== "rudp:" || !url.hostname || !Number.isInteger(port) || port <= 0 || port > 65_535) {
    throw new Error(`Invalid CultMesh provider RUDP endpoint ${endpoint}.`);
  }
  return { host: url.hostname, port };
}

function bindSocket(socket: Socket, host: string, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const abort = (): void => {
      socket.close();
      reject(abortError());
    };
    signal.addEventListener("abort", abort, { once: true });
    socket.once("error", reject);
    socket.bind(0, host, () => {
      signal.removeEventListener("abort", abort);
      socket.removeListener("error", reject);
      resolve();
    });
  });
}

async function waitUntilConnected(
  transport: CultNetRudpSocketTransportConnection,
  signal: AbortSignal,
  timeoutMs: number,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!transport.connected) {
    if (signal.aborted) throw abortError();
    if (Date.now() >= deadline) throw new Error("CultMesh provider RUDP connection timed out.");
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}

function abortError(): Error {
  const error = new Error("CultMesh provider RUDP operation aborted.");
  error.name = "AbortError";
  return error;
}
