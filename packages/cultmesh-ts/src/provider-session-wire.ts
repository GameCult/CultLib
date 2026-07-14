import { decode, encode } from "@msgpack/msgpack";
import type { CultNetRawDocumentRecord } from "cultnet-ts";

export const CULTMESH_PROVIDER_SESSION_SERVICE_ID = "gamecult.mesh.provider_session";

export const cultMeshProviderSessionOperations = {
  register: "provider.register",
  renew: "provider.renew",
  publicationPut: "provider.publication.put",
  publicationDelete: "provider.publication.delete",
  receiptPut: "provider.receipt.put",
  withdraw: "provider.withdraw",
} as const;

export const cultMeshProviderSessionSchemas = {
  registration: "gamecult.mesh.provider_registration.v1",
  lease: "gamecult.mesh.provider_lease.v1",
  leaseRenewal: "gamecult.mesh.provider_lease_renewal.v1",
  publicationPut: "gamecult.mesh.provider_publication_put.v1",
  publicationDelete: "gamecult.mesh.provider_publication_delete.v1",
  command: "gamecult.mesh.provider_command.v1",
  receiptPut: "gamecult.mesh.provider_receipt_put.v1",
  withdrawal: "gamecult.mesh.provider_withdrawal.v1",
  mutationAcceptance: "gamecult.mesh.provider_mutation_acceptance.v1",
} as const;

export type CultMeshProviderOperationStatus =
  | "ok"
  | "conflict"
  | "expired"
  | "denied"
  | "invalid";

export interface CultMeshProviderRegistrationWire {
  providerId: string;
  serviceInstanceId: string;
  endpointId: string;
  verseId: string;
  requestedLeaseDurationMs: number;
  authorityLeaseId?: string;
}

export interface CultMeshProviderLeaseWire {
  providerId: string;
  serviceInstanceId: string;
  endpointId: string;
  verseId: string;
  leaseId: string;
  validFromUtc: string;
  expiresAtUtc: string;
}

export interface CultMeshProviderLeaseRenewalWire {
  leaseId: string;
  requestedLeaseDurationMs: number;
}

export interface CultMeshProviderPublicationPutWire {
  leaseId: string;
  publicationId: string;
  document: CultNetRawDocumentRecord;
}

export interface CultMeshProviderPublicationDeleteWire {
  leaseId: string;
  publicationId: string;
  schemaId: string;
  recordKey: string;
}

export interface CultMeshProviderCommandWire {
  commandId: string;
  commandKind: string;
  providerId: string;
  serviceInstanceId: string;
  payload: unknown;
}

export type CultMeshProviderReceiptStateWire = "applied" | "rejected" | "failed";

export interface CultMeshProviderCommandReceiptWire {
  receiptId: string;
  commandId: string;
  commandKind: string;
  providerId: string;
  serviceInstanceId: string;
  state: CultMeshProviderReceiptStateWire;
  completedAtUtc: string;
  result?: unknown;
  error?: string;
}

export interface CultMeshProviderReceiptPutWire {
  leaseId: string;
  receipt: CultMeshProviderCommandReceiptWire;
}

export interface CultMeshProviderWithdrawalWire {
  leaseId: string;
}

export interface CultMeshProviderMutationAcceptanceWire {
  acceptedAtUtc: string;
  leaseId?: string;
  publicationId?: string;
  commandId?: string;
  receiptId?: string;
}

export function encodeProviderSessionPayload(value: unknown): string {
  return Buffer.from(encode(value)).toString("base64");
}

export function decodeProviderSessionPayload<T>(payload: string): T {
  if (typeof payload !== "string" || payload.length === 0) {
    throw new Error("CultMesh provider-session payload must be non-empty MessagePack base64.");
  }
  return decode(Buffer.from(payload, "base64")) as T;
}

export function decodeProviderCommandDocument(document: CultNetRawDocumentRecord): CultMeshProviderCommandWire {
  if (document.schemaId !== cultMeshProviderSessionSchemas.command) {
    throw new Error(`Expected ${cultMeshProviderSessionSchemas.command}, received ${document.schemaId}.`);
  }
  if (document.payloadEncoding !== "messagepack") {
    throw new Error(`Provider command ${document.recordKey} must use MessagePack.`);
  }
  const command = decode(document.payload) as Partial<CultMeshProviderCommandWire>;
  requireText(command.commandId, "commandId");
  requireText(command.commandKind, "commandKind");
  requireText(command.providerId, "providerId");
  requireText(command.serviceInstanceId, "serviceInstanceId");
  return command as CultMeshProviderCommandWire;
}

export function assertProviderLease(value: unknown): asserts value is CultMeshProviderLeaseWire {
  const lease = requireObject(value, "provider lease");
  requireText(lease.providerId, "providerId");
  requireText(lease.serviceInstanceId, "serviceInstanceId");
  requireText(lease.endpointId, "endpointId");
  requireText(lease.verseId, "verseId");
  requireText(lease.leaseId, "leaseId");
  requireTimestamp(lease.validFromUtc, "validFromUtc");
  requireTimestamp(lease.expiresAtUtc, "expiresAtUtc");
}

export function assertMutationAcceptance(value: unknown): asserts value is CultMeshProviderMutationAcceptanceWire {
  const acceptance = requireObject(value, "provider mutation acceptance");
  requireTimestamp(acceptance.acceptedAtUtc, "acceptedAtUtc");
}

function requireObject(value: unknown, name: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`CultMesh ${name} must be a MessagePack map.`);
  }
  return value as Record<string, unknown>;
}

function requireText(value: unknown, name: string): asserts value is string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`CultMesh provider-session ${name} must be non-empty.`);
  }
}

function requireTimestamp(value: unknown, name: string): asserts value is string {
  requireText(value, name);
  if (!Number.isFinite(Date.parse(value))) {
    throw new Error(`CultMesh provider-session ${name} must be an RFC3339 timestamp.`);
  }
}
