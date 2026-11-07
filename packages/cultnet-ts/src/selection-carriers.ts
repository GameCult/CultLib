// The two shape conversions the selection/v1 carriers need between the reference's wire and TypeScript:
// the reference writes every declared key, nil included, in declaration order (MessagePack-CSharp map
// mode; schema notes R-S/R-X), so a received message arrives with explicit nulls that are normalised
// to absent keys, and a sent message is rebuilt with every key in the reference's order.

import type {
  CultNetDatabaseSubscribeV1Message,
  CultNetRawDocumentRecord,
  CultNetSnapshotRequestV1Message,
  CultNetSnapshotResponseRawV1Message,
} from "./contracts";
import type { CultNetEdge, CultNetRawDocumentHeader, CultNetRecordRef, CultNetSelection } from "./selection";

export type CultNetSelectionCarrier =
  | CultNetSnapshotRequestV1Message
  | CultNetDatabaseSubscribeV1Message
  | CultNetSnapshotResponseRawV1Message;

type Bag = Record<string, unknown>;

const isBag = (value: unknown): value is Bag => !!value && typeof value === "object" && !Array.isArray(value);

function stripNulls(candidate: unknown, keys: readonly string[]): void {
  if (!isBag(candidate)) return;
  for (const key of keys) {
    if (candidate[key] === null) delete candidate[key];
  }
}

function eachBag(list: unknown, visit: (item: Bag) => void): void {
  if (!Array.isArray(list)) return;
  for (const item of list) {
    if (isBag(item)) visit(item);
  }
}

/** Drops the nil an absent selection member travels as, at every depth the selection carries one. */
export function normalizeSelectionNulls(selection: unknown): void {
  stripNulls(selection, ["schemas", "keys", "fields", "cites", "cited", "limit", "cursor"]);
  if (!isBag(selection)) return;
  eachBag(selection.fields, field => stripNulls(field, ["values", "number"]));
  stripNulls(selection.cites, ["role"]);
}

/** Drops the nil an absent member travels as, for one selection/v1 carrier message. */
export function normalizeSelectionCarrierNulls(
  schemaVersion: CultNetSelectionCarrier["schemaVersion"],
  candidate: Bag,
): void {
  switch (schemaVersion) {
    case "cultnet.snapshot_request.v1":
      stripNulls(candidate, ["shardId", "shardEpoch"]);
      normalizeSelectionNulls(candidate.selection);
      return;
    case "cultnet.database_subscribe.v1":
      stripNulls(candidate, ["consumerRuntimeId", "bodyIds", "supportedBodyTransports"]);
      normalizeSelectionNulls(candidate.selection);
      return;
    case "cultnet.snapshot_response_raw.v1":
      stripNulls(candidate, ["next", "headers", "documents", "edges", "shardId", "shardEpoch", "shardLogSequence"]);
      eachBag(candidate.headers, header => stripNulls(header, HEADER_OPTIONAL));
      eachBag(candidate.documents, document => stripNulls(document, HEADER_OPTIONAL));
      eachBag(candidate.edges, edge => stripNulls(edge, ["payloadEncoding", "payload"]));
      return;
  }
}

const HEADER_OPTIONAL = ["schemaName", "schemaVersion", "schemaContentHash", "sourceRuntimeId", "sourceAgentId", "sourceRole", "tags"] as const;

const orNull = <T>(value: T | undefined | null): T | null => value ?? null;

function recordRefWire(ref: CultNetRecordRef) {
  return { schemaId: ref.schemaId, recordKey: ref.recordKey };
}

function selectionWire(selection: CultNetSelection) {
  return {
    schemas: orNull(selection.schemas),
    keys: orNull(selection.keys),
    fields: selection.fields
      ? selection.fields.map(field => ({
          index: field.index,
          op: field.op,
          values: orNull(field.values),
          number: orNull(field.number),
        }))
      : null,
    cites: selection.cites
      ? { target: recordRefWire(selection.cites.target), role: orNull(selection.cites.role) }
      : null,
    cited: selection.cited ? { role: selection.cited.role, exists: selection.cited.exists } : null,
    projection: selection.projection ?? "header",
    descending: selection.descending ?? false,
    limit: orNull(selection.limit),
    cursor: orNull(selection.cursor),
  };
}

function headerWire(header: CultNetRawDocumentHeader) {
  return {
    schemaId: header.schemaId,
    schemaName: orNull(header.schemaName),
    schemaVersion: orNull(header.schemaVersion),
    schemaContentHash: orNull(header.schemaContentHash),
    recordKey: header.recordKey,
    storedAt: header.storedAt,
    sourceRuntimeId: orNull(header.sourceRuntimeId),
    sourceAgentId: orNull(header.sourceAgentId),
    sourceRole: orNull(header.sourceRole),
    tags: orNull(header.tags),
  };
}

function documentWire(document: CultNetRawDocumentRecord) {
  return {
    schemaId: document.schemaId,
    schemaName: orNull(document.schemaName),
    schemaVersion: orNull(document.schemaVersion),
    schemaContentHash: orNull(document.schemaContentHash),
    recordKey: document.recordKey,
    storedAt: document.storedAt,
    payloadEncoding: document.payloadEncoding,
    payload: document.payload,
    sourceRuntimeId: orNull(document.sourceRuntimeId),
    sourceAgentId: orNull(document.sourceAgentId),
    sourceRole: orNull(document.sourceRole),
    tags: orNull(document.tags),
  };
}

function edgeWire(edge: CultNetEdge) {
  return {
    from: recordRefWire(edge.from),
    role: edge.role,
    to: recordRefWire(edge.to),
    payloadEncoding: orNull(edge.payloadEncoding),
    payload: orNull(edge.payload),
  };
}

/** The reference's nil-keyed, declaration-ordered shape for one selection/v1 carrier message. */
export function encodeSelectionCarrierForWire(message: CultNetSelectionCarrier): unknown {
  switch (message.schemaVersion) {
    case "cultnet.snapshot_request.v1":
      return {
        schemaVersion: message.schemaVersion,
        messageId: message.messageId,
        selection: selectionWire(message.selection),
        shardId: orNull(message.shardId),
        shardEpoch: orNull(message.shardEpoch),
      };
    case "cultnet.database_subscribe.v1":
      return {
        schemaVersion: message.schemaVersion,
        messageId: message.messageId,
        subscriptionId: message.subscriptionId,
        selection: selectionWire(message.selection),
        includeSnapshot: message.includeSnapshot ?? true,
        consumerRuntimeId: orNull(message.consumerRuntimeId),
        bodyIds: orNull(message.bodyIds),
        supportedBodyTransports: orNull(message.supportedBodyTransports),
      };
    case "cultnet.snapshot_response_raw.v1":
      return {
        schemaVersion: message.schemaVersion,
        messageId: message.messageId,
        matched: message.matched,
        asOf: message.asOf,
        next: orNull(message.next),
        headers: message.headers ? message.headers.map(headerWire) : null,
        documents: message.documents ? message.documents.map(documentWire) : null,
        edges: message.edges ? message.edges.map(edgeWire) : null,
        shardId: orNull(message.shardId),
        shardEpoch: orNull(message.shardEpoch),
        shardLogSequence: orNull(message.shardLogSequence),
      };
  }
}
