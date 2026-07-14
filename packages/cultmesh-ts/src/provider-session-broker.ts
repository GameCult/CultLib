import { encode } from "@msgpack/msgpack";
import type {
  CultNetDocumentPutRawMessage,
  CultNetOperationRequestMessage,
  CultNetOperationResponseMessage,
  CultNetRawDocumentRecord,
} from "cultnet-ts";

import type { CultMeshRudpServerSession } from "./index";
import {
  CULTMESH_PROVIDER_SESSION_SERVICE_ID,
  cultMeshProviderSessionOperations,
  cultMeshProviderSessionSchemas,
  decodeProviderSessionPayload,
  encodeProviderSessionPayload,
  type CultMeshProviderCommandReceiptWire,
  type CultMeshProviderCommandWire,
  type CultMeshProviderLeaseRenewalWire,
  type CultMeshProviderLeaseWire,
  type CultMeshProviderOperationStatus,
  type CultMeshProviderPublicationDeleteWire,
  type CultMeshProviderPublicationPutWire,
  type CultMeshProviderReceiptPutWire,
  type CultMeshProviderRegistrationWire,
  type CultMeshProviderWithdrawalWire,
} from "./provider-session-wire";

export interface CultMeshProviderBrokerClock {
  now(): Date;
}

export interface CultMeshProviderSessionBrokerOptions {
  readonly runtimeId: string;
  readonly clock?: CultMeshProviderBrokerClock;
  readonly createLeaseId?: () => string;
  readonly maximumLeaseDurationMs?: number;
  readonly expiryPollMs?: number;
  readonly onError?: (error: Error) => void;
  readonly authorizeRegistration: (
    identity: CultMeshProviderRegistrationWire,
    session: CultMeshRudpServerSession,
  ) => boolean | Promise<boolean>;
  readonly acceptPublication: (
    identity: CultMeshProviderRegistrationWire,
    publicationId: string,
    document: CultNetRawDocumentRecord,
  ) => void | Promise<void>;
  readonly deletePublications: (
    identity: CultMeshProviderRegistrationWire,
    publications: readonly {
      publicationId: string;
      document: Pick<CultNetRawDocumentRecord, "schemaId" | "recordKey">;
    }[],
  ) => void | Promise<void>;
  readonly acceptReceipt: (
    identity: CultMeshProviderRegistrationWire,
    receipt: CultMeshProviderCommandReceiptWire,
  ) => void | Promise<void>;
}

interface LeaseRecord {
  identity: CultMeshProviderRegistrationWire;
  lease: CultMeshProviderLeaseWire;
  session?: CultMeshRudpServerSession;
  publications: Map<string, CultNetRawDocumentRecord>;
}

export class CultMeshProviderSessionBroker {
  readonly #options: CultMeshProviderSessionBrokerOptions;
  readonly #clock: CultMeshProviderBrokerClock;
  readonly #leases = new Map<string, LeaseRecord>();
  readonly #identityLeases = new Map<string, string>();
  readonly #sessionLeases = new Map<string, Set<string>>();
  readonly #commands = new Map<string, CultMeshProviderCommandWire>();
  readonly #receipts = new Map<string, CultMeshProviderCommandReceiptWire>();
  readonly #publicationOwners = new Map<string, { identityKey: string; publicationId: string }>();
  readonly #expiryTimer: NodeJS.Timeout;
  #work: Promise<void> = Promise.resolve();
  #leaseSequence = 0;

  public constructor(options: CultMeshProviderSessionBrokerOptions) {
    if (!options.runtimeId) throw new Error("CultMesh provider-session broker requires runtimeId.");
    if (!options.authorizeRegistration) throw new Error("CultMesh provider-session broker requires an explicit registration authority.");
    this.#options = options;
    this.#clock = options.clock ?? { now: () => new Date() };
    this.#expiryTimer = setInterval(() => {
      void this.expireLeases().catch(error => {
        const normalized = error instanceof Error ? error : new Error(String(error));
        if (this.#options.onError) this.#options.onError(normalized);
        else console.error(`CultMesh provider-session expiry failed: ${normalized.message}`);
      });
    }, Math.max(10, options.expiryPollMs ?? 1_000));
    this.#expiryTimer.unref?.();
  }

  public close(): void {
    clearInterval(this.#expiryTimer);
  }

  public async handle(
    request: CultNetOperationRequestMessage,
    session: CultMeshRudpServerSession,
  ): Promise<CultNetOperationResponseMessage> {
    const work = this.#work.then(() => this.#handle(request, session));
    this.#work = work.then(() => undefined, () => undefined);
    return await work;
  }

  async #handle(
    request: CultNetOperationRequestMessage,
    session: CultMeshRudpServerSession,
  ): Promise<CultNetOperationResponseMessage> {
    if (request.serviceId !== CULTMESH_PROVIDER_SESSION_SERVICE_ID) {
      return this.#response(request, "denied", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        `Unsupported service ${request.serviceId}.`,
      ]);
    }
    if (request.payloadEncoding !== "messagepack-base64") {
      return this.#response(request, "invalid", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        "Provider-session operation payloads must use messagepack-base64.",
      ]);
    }
    try {
      switch (request.operation) {
        case cultMeshProviderSessionOperations.register:
          return await this.#register(request, session);
        case cultMeshProviderSessionOperations.renew:
          return await this.#renew(request, session);
        case cultMeshProviderSessionOperations.publicationPut:
          return await this.#publicationPut(request, session);
        case cultMeshProviderSessionOperations.publicationDelete:
          return await this.#publicationDelete(request, session);
        case cultMeshProviderSessionOperations.receiptPut:
          return await this.#receiptPut(request, session);
        case cultMeshProviderSessionOperations.withdraw:
          return await this.#withdraw(request, session);
        default:
          return this.#response(request, "invalid", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
            `Unsupported provider-session operation ${request.operation}.`,
          ]);
      }
    } catch (error) {
      if (error instanceof BrokerInfrastructureError) throw error.cause;
      return this.#response(request, error instanceof BrokerStatusError ? error.status : "invalid", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        error instanceof Error ? error.message : String(error),
      ]);
    }
  }

  public sessionClosed(session: CultMeshRudpServerSession): void {
    for (const leaseId of this.#sessionLeases.get(session.sessionId) ?? []) {
      const record = this.#leases.get(leaseId);
      if (record?.session?.sessionId === session.sessionId) record.session = undefined;
    }
    this.#sessionLeases.delete(session.sessionId);
  }

  public enqueueCommand(command: CultMeshProviderCommandWire): void {
    requireText(command.commandId, "commandId");
    requireText(command.providerId, "providerId");
    requireText(command.serviceInstanceId, "serviceInstanceId");
    const completed = this.#receipts.get(command.commandId);
    if (completed) {
      if (completed.commandKind !== command.commandKind || completed.providerId !== command.providerId || completed.serviceInstanceId !== command.serviceInstanceId) {
        throw new Error(`Command id ${command.commandId} is already completed for another command transaction.`);
      }
      return;
    }
    const existing = this.#commands.get(command.commandId);
    if (existing && !wireEquivalent(existing, command)) {
      throw new Error(`Command id ${command.commandId} is already retained for another command transaction.`);
    }
    this.#commands.set(command.commandId, existing ?? command);
    for (const record of this.#leases.values()) {
      if (!this.#isActive(record)) continue;
      if (record.identity.providerId !== command.providerId || record.identity.serviceInstanceId !== command.serviceInstanceId) continue;
      this.#sendCommand(record, command);
    }
  }

  public get activeLeaseCount(): number {
    return [...this.#leases.values()].filter(record => this.#isActive(record)).length;
  }

  public async expireLeases(): Promise<void> {
    const work = this.#work.then(() => this.#expireLeases());
    this.#work = work.then(() => undefined, () => undefined);
    await work;
  }

  async #expireLeases(): Promise<void> {
    const now = this.#clock.now().getTime();
    for (const [leaseId, record] of [...this.#leases]) {
      if (Date.parse(record.lease.expiresAtUtc) > now) continue;
      await this.#options.deletePublications(record.identity, publicationEntries(record));
      this.#releasePublicationOwners(record);
      this.#removeLease(leaseId, record);
    }
  }

  async #register(request: CultNetOperationRequestMessage, session: CultMeshRudpServerSession): Promise<CultNetOperationResponseMessage> {
    expectSchema(request, cultMeshProviderSessionSchemas.registration);
    const identity = decodeProviderSessionPayload<CultMeshProviderRegistrationWire>(request.payload);
    validateRegistration(identity);
    if (!await infrastructure(() => this.#options.authorizeRegistration(identity, session))) {
      return this.#response(request, "denied", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        "The CultNet session does not authorize this provider identity.",
      ]);
    }
    const identityKey = identityTuple(identity);
    const previousLeaseId = this.#identityLeases.get(identityKey);
    let publications = new Map<string, CultNetRawDocumentRecord>();
    if (previousLeaseId) {
      const previous = this.#leases.get(previousLeaseId);
      if (previous) {
        publications = previous.publications;
        this.#removeLease(previousLeaseId, previous);
      }
    }
    const lease = this.#createLease(identity, identity.requestedLeaseDurationMs);
    const record: LeaseRecord = { identity, lease, session, publications };
    this.#leases.set(lease.leaseId, record);
    this.#identityLeases.set(identityKey, lease.leaseId);
    this.#attachSession(session, lease.leaseId);
    this.#replayCommands(record);
    return this.#response(request, "ok", cultMeshProviderSessionSchemas.lease, lease);
  }

  async #renew(request: CultNetOperationRequestMessage, session: CultMeshRudpServerSession): Promise<CultNetOperationResponseMessage> {
    expectSchema(request, cultMeshProviderSessionSchemas.leaseRenewal);
    const renewal = decodeProviderSessionPayload<CultMeshProviderLeaseRenewalWire>(request.payload);
    const current = this.#requireLease(renewal.leaseId, session);
    const lease = this.#createLease(current.identity, renewal.requestedLeaseDurationMs);
    const replacement: LeaseRecord = { ...current, lease, session };
    this.#removeLease(renewal.leaseId, current);
    this.#leases.set(lease.leaseId, replacement);
    this.#identityLeases.set(identityTuple(current.identity), lease.leaseId);
    this.#attachSession(session, lease.leaseId);
    return this.#response(request, "ok", cultMeshProviderSessionSchemas.lease, lease);
  }

  async #publicationPut(request: CultNetOperationRequestMessage, session: CultMeshRudpServerSession): Promise<CultNetOperationResponseMessage> {
    expectSchema(request, cultMeshProviderSessionSchemas.publicationPut);
    const mutation = decodeProviderSessionPayload<CultMeshProviderPublicationPutWire>(request.payload);
    const record = this.#requireLease(mutation.leaseId, session);
    requireText(mutation.publicationId, "publicationId");
    validateDocument(mutation.document);
    const previous = record.publications.get(mutation.publicationId);
    if (previous && (previous.schemaId !== mutation.document.schemaId || previous.recordKey !== mutation.document.recordKey)) {
      return this.#response(request, "conflict", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        `Publication ${mutation.publicationId} already owns ${previous.schemaId}:${previous.recordKey}. Delete it before changing its tuple.`,
      ]);
    }
    const tuple = documentTuple(mutation.document);
    const owner = this.#publicationOwners.get(tuple);
    const ownerIdentity = identityTuple(record.identity);
    if (owner && (owner.identityKey !== ownerIdentity || owner.publicationId !== mutation.publicationId)) {
      return this.#response(request, "conflict", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        `Publication tuple ${mutation.document.schemaId}:${mutation.document.recordKey} is already owned.`,
      ]);
    }
    const normalizedDocument: CultNetRawDocumentRecord = {
      ...mutation.document,
      sourceRuntimeId: record.identity.serviceInstanceId,
      sourceAgentId: record.identity.providerId,
      sourceRole: "provider",
    };
    await infrastructure(() => this.#options.acceptPublication(record.identity, mutation.publicationId, normalizedDocument));
    record.publications.set(mutation.publicationId, normalizedDocument);
    this.#publicationOwners.set(tuple, { identityKey: ownerIdentity, publicationId: mutation.publicationId });
    return this.#accepted(request, record.lease.leaseId, { publicationId: mutation.publicationId });
  }

  async #publicationDelete(request: CultNetOperationRequestMessage, session: CultMeshRudpServerSession): Promise<CultNetOperationResponseMessage> {
    expectSchema(request, cultMeshProviderSessionSchemas.publicationDelete);
    const mutation = decodeProviderSessionPayload<CultMeshProviderPublicationDeleteWire>(request.payload);
    const record = this.#requireLease(mutation.leaseId, session);
    const owned = record.publications.get(mutation.publicationId);
    if (!owned || owned.schemaId !== mutation.schemaId || owned.recordKey !== mutation.recordKey) {
      return this.#response(request, "denied", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        `Publication ${mutation.publicationId} is not owned by this lease with the supplied schema and key.`,
      ]);
    }
    await infrastructure(() => this.#options.deletePublications(record.identity, [{ publicationId: mutation.publicationId, document: owned }]));
    record.publications.delete(mutation.publicationId);
    this.#publicationOwners.delete(documentTuple(owned));
    return this.#accepted(request, record.lease.leaseId, { publicationId: mutation.publicationId });
  }

  async #receiptPut(request: CultNetOperationRequestMessage, session: CultMeshRudpServerSession): Promise<CultNetOperationResponseMessage> {
    expectSchema(request, cultMeshProviderSessionSchemas.receiptPut);
    const mutation = decodeProviderSessionPayload<CultMeshProviderReceiptPutWire>(request.payload);
    const record = this.#requireLease(mutation.leaseId, session);
    const receipt = mutation.receipt;
    validateReceipt(receipt);
    if (receipt.providerId !== record.identity.providerId || receipt.serviceInstanceId !== record.identity.serviceInstanceId) {
      return this.#response(request, "denied", cultMeshProviderSessionSchemas.mutationAcceptance, {}, ["Receipt identity does not match its lease."]);
    }
    const previousReceipt = this.#receipts.get(receipt.commandId);
    if (previousReceipt) {
      if (!wireEquivalent(previousReceipt, receipt)) {
        return this.#response(request, "conflict", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
          `Command ${receipt.commandId} already has a different accepted receipt.`,
        ]);
      }
      return this.#accepted(request, record.lease.leaseId, { commandId: receipt.commandId, receiptId: receipt.receiptId });
    }
    const command = this.#commands.get(receipt.commandId);
    if (!command || command.commandKind !== receipt.commandKind || command.providerId !== receipt.providerId || command.serviceInstanceId !== receipt.serviceInstanceId) {
      return this.#response(request, "denied", cultMeshProviderSessionSchemas.mutationAcceptance, {}, [
        `Receipt ${receipt.receiptId} does not match a retained command for this provider generation.`,
      ]);
    }
    await infrastructure(() => this.#options.acceptReceipt(record.identity, receipt));
    this.#receipts.set(receipt.commandId, receipt);
    this.#commands.delete(receipt.commandId);
    return this.#accepted(request, record.lease.leaseId, { commandId: receipt.commandId, receiptId: receipt.receiptId });
  }

  async #withdraw(request: CultNetOperationRequestMessage, session: CultMeshRudpServerSession): Promise<CultNetOperationResponseMessage> {
    expectSchema(request, cultMeshProviderSessionSchemas.withdrawal);
    const mutation = decodeProviderSessionPayload<CultMeshProviderWithdrawalWire>(request.payload);
    const record = this.#requireLease(mutation.leaseId, session);
    await infrastructure(() => this.#options.deletePublications(record.identity, publicationEntries(record)));
    this.#releasePublicationOwners(record);
    this.#removeLease(mutation.leaseId, record);
    return this.#accepted(request, mutation.leaseId);
  }

  #requireLease(leaseId: string, session: CultMeshRudpServerSession): LeaseRecord {
    requireText(leaseId, "leaseId");
    const record = this.#leases.get(leaseId);
    if (!record || !this.#isActive(record)) throw new BrokerStatusError("expired", `Provider lease ${leaseId} is expired or fenced.`);
    if (record.session?.sessionId !== session.sessionId) throw new BrokerStatusError("denied", `Provider lease ${leaseId} belongs to another physical session.`);
    return record;
  }

  #isActive(record: LeaseRecord): boolean {
    return Date.parse(record.lease.expiresAtUtc) > this.#clock.now().getTime();
  }

  #createLease(identity: CultMeshProviderRegistrationWire, requestedMs: number): CultMeshProviderLeaseWire {
    if (!Number.isFinite(requestedMs) || requestedMs <= 0) throw new Error("requestedLeaseDurationMs must be positive.");
    const durationMs = Math.min(requestedMs, this.#options.maximumLeaseDurationMs ?? 120_000);
    const now = this.#clock.now();
    return {
      providerId: identity.providerId,
      serviceInstanceId: identity.serviceInstanceId,
      endpointId: identity.endpointId,
      verseId: identity.verseId,
      leaseId: this.#options.createLeaseId?.() ?? `${this.#options.runtimeId}:lease:${++this.#leaseSequence}`,
      validFromUtc: now.toISOString(),
      expiresAtUtc: new Date(now.getTime() + durationMs).toISOString(),
    };
  }

  #attachSession(session: CultMeshRudpServerSession, leaseId: string): void {
    let leases = this.#sessionLeases.get(session.sessionId);
    if (!leases) this.#sessionLeases.set(session.sessionId, leases = new Set());
    leases.add(leaseId);
  }

  #removeLease(leaseId: string, record: LeaseRecord): void {
    this.#leases.delete(leaseId);
    if (this.#identityLeases.get(identityTuple(record.identity)) === leaseId) this.#identityLeases.delete(identityTuple(record.identity));
    this.#sessionLeases.get(record.session?.sessionId ?? "")?.delete(leaseId);
  }

  #releasePublicationOwners(record: LeaseRecord): void {
    const ownerIdentity = identityTuple(record.identity);
    for (const [publicationId, document] of record.publications) {
      const tuple = documentTuple(document);
      const owner = this.#publicationOwners.get(tuple);
      if (owner?.identityKey === ownerIdentity && owner.publicationId === publicationId) this.#publicationOwners.delete(tuple);
    }
  }

  #replayCommands(record: LeaseRecord): void {
    for (const command of this.#commands.values()) {
      if (command.providerId === record.identity.providerId && command.serviceInstanceId === record.identity.serviceInstanceId) this.#sendCommand(record, command);
    }
  }

  #sendCommand(record: LeaseRecord, command: CultMeshProviderCommandWire): void {
    if (!record.session) return;
    const message: CultNetDocumentPutRawMessage = {
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: `${this.#options.runtimeId}:command:${command.commandId}`,
      document: {
        schemaId: cultMeshProviderSessionSchemas.command,
        recordKey: `${command.providerId}/${command.serviceInstanceId}/${command.commandId}`,
        storedAt: this.#clock.now().toISOString(),
        payloadEncoding: "messagepack",
        payload: encode(command),
        sourceRuntimeId: this.#options.runtimeId,
        sourceRole: "provider-session-broker",
      },
    };
    record.session.send(message);
  }

  #accepted(request: CultNetOperationRequestMessage, leaseId: string, extra: Record<string, string> = {}): CultNetOperationResponseMessage {
    return this.#response(request, "ok", cultMeshProviderSessionSchemas.mutationAcceptance, {
      acceptedAtUtc: this.#clock.now().toISOString(),
      leaseId,
      ...extra,
    });
  }

  #response(
    request: CultNetOperationRequestMessage,
    status: CultMeshProviderOperationStatus,
    payloadSchema: string,
    payload: unknown,
    diagnostics?: string[],
  ): CultNetOperationResponseMessage {
    const response: CultNetOperationResponseMessage = {
      schemaVersion: "cultnet.operation_response.v0",
      messageId: request.messageId,
      serviceId: request.serviceId,
      operation: request.operation,
      status,
      payloadSchema,
      payloadEncoding: "messagepack-base64",
      payload: encodeProviderSessionPayload(payload),
      sourceRuntimeId: this.#options.runtimeId,
    };
    if (diagnostics) response.diagnostics = diagnostics;
    return response;
  }
}

class BrokerStatusError extends Error {
  public constructor(public readonly status: CultMeshProviderOperationStatus, message: string) {
    super(message);
  }
}

class BrokerInfrastructureError extends Error {
  public constructor(public readonly cause: unknown) {
    super(cause instanceof Error ? cause.message : String(cause));
  }
}

async function infrastructure<T>(action: () => T | Promise<T>): Promise<T> {
  try {
    return await action();
  } catch (error) {
    throw new BrokerInfrastructureError(error);
  }
}

function expectSchema(request: CultNetOperationRequestMessage, schema: string): void {
  if (request.payloadSchema !== schema) throw new Error(`Operation ${request.operation} requires payload schema ${schema}.`);
}

function validateRegistration(value: CultMeshProviderRegistrationWire): void {
  for (const field of ["providerId", "serviceInstanceId", "endpointId", "verseId"] as const) requireText(value[field], field);
  if (!Number.isFinite(value.requestedLeaseDurationMs) || value.requestedLeaseDurationMs <= 0) throw new Error("requestedLeaseDurationMs must be positive.");
}

function validateDocument(document: CultNetRawDocumentRecord): void {
  requireText(document?.schemaId, "document.schemaId");
  requireText(document?.recordKey, "document.recordKey");
  if (document.payloadEncoding !== "messagepack" || !(document.payload instanceof Uint8Array)) throw new Error("Provider publications must carry a raw MessagePack document.");
}

function validateReceipt(receipt: CultMeshProviderCommandReceiptWire): void {
  requireText(receipt?.receiptId, "receipt.receiptId");
  requireText(receipt?.commandId, "receipt.commandId");
  requireText(receipt?.commandKind, "receipt.commandKind");
  requireText(receipt?.providerId, "receipt.providerId");
  requireText(receipt?.serviceInstanceId, "receipt.serviceInstanceId");
  if (receipt.state !== "applied" && receipt.state !== "rejected" && receipt.state !== "failed") throw new Error("CultMesh provider-session receipt.state is invalid.");
  if (!Number.isFinite(Date.parse(receipt.completedAtUtc))) throw new Error("CultMesh provider-session receipt.completedAtUtc must be RFC3339.");
}

function identityTuple(identity: CultMeshProviderRegistrationWire): string {
  return `${identity.verseId}\u0000${identity.providerId}\u0000${identity.serviceInstanceId}\u0000${identity.endpointId}`;
}

function documentTuple(document: Pick<CultNetRawDocumentRecord, "schemaId" | "recordKey">): string {
  return `${document.schemaId}\u0000${document.recordKey}`;
}

function wireEquivalent(left: unknown, right: unknown): boolean {
  return Buffer.from(encode(left)).equals(Buffer.from(encode(right)));
}

function publicationEntries(record: LeaseRecord): readonly {
  publicationId: string;
  document: Pick<CultNetRawDocumentRecord, "schemaId" | "recordKey">;
}[] {
  return [...record.publications].map(([publicationId, document]) => ({ publicationId, document }));
}

function requireText(value: unknown, name: string): asserts value is string {
  if (typeof value !== "string" || value.trim().length === 0) throw new Error(`CultMesh provider-session ${name} must be non-empty.`);
}
