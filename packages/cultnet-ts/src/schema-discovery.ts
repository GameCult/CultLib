import { createHash } from "node:crypto";

import {
  cultNetSchemas,
  type CultNetSchemaCatalogRequestMessage,
  type CultNetSchemaCatalogResponseMessage,
  type CultNetSchemaDescriptor,
  type CultNetSchemaKind,
  type CultNetWireContract,
} from "./contracts";

export interface CultNetSchemaRegistration {
  schemaId: string;
  kind: CultNetSchemaKind;
  schema: object;
  wireContracts: readonly CultNetWireContract[];
  schemaVersion?: string;
  documentType?: string;
  title?: string;
}

export interface CultNetSchemaCatalogOptions {
  includeSchemaJson?: boolean;
  schemaIds?: readonly string[];
  kinds?: readonly CultNetSchemaKind[];
}

export class CultNetSchemaCatalog {
  readonly #descriptors = new Map<string, CultNetSchemaDescriptor>();
  readonly #subscribers = new Set<(descriptor: CultNetSchemaDescriptor) => void>();

  get schemas(): CultNetSchemaDescriptor[] {
    return Array.from(this.#descriptors.values())
      .sort((left, right) => left.schemaId.localeCompare(right.schemaId))
      .map((descriptor) => cloneSchemaDescriptor(descriptor, true));
  }

  watch(callback: (descriptor: CultNetSchemaDescriptor) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  upsert(descriptor: CultNetSchemaDescriptor): CultNetSchemaDescriptor {
    if (!descriptor.schemaId) {
      throw new Error("CultNet schema descriptor requires schemaId.");
    }
    if (!descriptor.kind) {
      throw new Error("CultNet schema descriptor requires kind.");
    }
    if (!descriptor.wireContracts.length) {
      throw new Error("CultNet schema descriptor requires at least one wire contract.");
    }

    const stored = cloneSchemaDescriptor(descriptor, true);
    this.#descriptors.set(stored.schemaId, stored);
    for (const subscriber of Array.from(this.#subscribers)) {
      subscriber(cloneSchemaDescriptor(stored, true));
    }
    return cloneSchemaDescriptor(stored, true);
  }

  get(schemaId: string, options: CultNetSchemaCatalogOptions = {}): CultNetSchemaDescriptor | undefined {
    const descriptor = this.#descriptors.get(schemaId);
    if (!descriptor) {
      return undefined;
    }
    return cloneSchemaDescriptor(descriptor, options.includeSchemaJson === true);
  }

  list(options: CultNetSchemaCatalogOptions = {}): CultNetSchemaDescriptor[] {
    const requestedSchemaIds = options.schemaIds ? new Set(options.schemaIds) : null;
    const requestedKinds = options.kinds ? new Set(options.kinds) : null;

    return this.schemas
      .filter((descriptor) => {
        if (requestedSchemaIds && !requestedSchemaIds.has(descriptor.schemaId)) {
          return false;
        }
        if (requestedKinds && !requestedKinds.has(descriptor.kind)) {
          return false;
        }
        return true;
      })
      .map((descriptor) => cloneSchemaDescriptor(descriptor, options.includeSchemaJson === true));
  }

  createCatalogResponse(
    request: CultNetSchemaCatalogRequestMessage,
  ): CultNetSchemaCatalogResponseMessage {
    return {
      schemaVersion: "cultnet.schema_catalog_response.v0",
      messageId: request.messageId,
      schemas: this.list({
        includeSchemaJson: request.includeSchemaJson,
        schemaIds: request.schemaIds,
        kinds: request.kinds,
      }),
    };
  }

  applyResponse(response: CultNetSchemaCatalogResponseMessage): CultNetSchemaDescriptor[] {
    if (response.schemaVersion !== "cultnet.schema_catalog_response.v0") {
      throw new Error(`Expected cultnet.schema_catalog_response.v0, received ${response.schemaVersion}.`);
    }
    return response.schemas.map((descriptor) => this.upsert(descriptor));
  }
}

interface CultNetRegisteredSchemaRecord extends CultNetSchemaRegistration {
  canonicalSchemaJson: string;
  contentHash: string;
}

export class CultNetSchemaRegistry {
  readonly #entries = new Map<string, CultNetRegisteredSchemaRecord>();

  constructor(entries: Iterable<CultNetSchemaRegistration> = []) {
    for (const entry of entries) {
      this.register(entry);
    }
  }

  register(entry: CultNetSchemaRegistration): this {
    const canonicalSchemaJson = stableStringify(entry.schema);
    const contentHash = createHash("sha256").update(canonicalSchemaJson).digest("hex");

    this.#entries.set(entry.schemaId, {
      ...entry,
      canonicalSchemaJson,
      contentHash,
    });

    return this;
  }

  get(schemaId: string, options: CultNetSchemaCatalogOptions = {}): CultNetSchemaDescriptor | undefined {
    const entry = this.#entries.get(schemaId);

    if (!entry) {
      return undefined;
    }

    return toSchemaDescriptor(entry, options.includeSchemaJson === true);
  }

  list(options: CultNetSchemaCatalogOptions = {}): CultNetSchemaDescriptor[] {
    const requestedSchemaIds = options.schemaIds ? new Set(options.schemaIds) : null;
    const requestedKinds = options.kinds ? new Set(options.kinds) : null;
    const includeSchemaJson = options.includeSchemaJson === true;

    return Array.from(this.#entries.values())
      .filter((entry) => {
        if (requestedSchemaIds && !requestedSchemaIds.has(entry.schemaId)) {
          return false;
        }

        if (requestedKinds && !requestedKinds.has(entry.kind)) {
          return false;
        }

        return true;
      })
      .map((entry) => toSchemaDescriptor(entry, includeSchemaJson));
  }

  createCatalogResponse(
    request: CultNetSchemaCatalogRequestMessage,
  ): CultNetSchemaCatalogResponseMessage {
    return {
      schemaVersion: "cultnet.schema_catalog_response.v0",
      messageId: request.messageId,
      schemas: this.list({
        includeSchemaJson: request.includeSchemaJson,
        schemaIds: request.schemaIds,
        kinds: request.kinds,
      }),
    };
  }
}

function cloneSchemaDescriptor(
  descriptor: CultNetSchemaDescriptor,
  includeSchemaJson: boolean,
): CultNetSchemaDescriptor {
  const clone: CultNetSchemaDescriptor = {
    schemaId: descriptor.schemaId,
    kind: descriptor.kind,
    wireContracts: [...descriptor.wireContracts],
    contentHash: descriptor.contentHash,
  };
  if (descriptor.schemaVersion !== undefined) {
    clone.schemaVersion = descriptor.schemaVersion;
  }
  if (descriptor.documentType !== undefined) {
    clone.documentType = descriptor.documentType;
  }
  if (descriptor.title !== undefined) {
    clone.title = descriptor.title;
  }
  if (includeSchemaJson && descriptor.schemaJson !== undefined) {
    clone.schemaJson = descriptor.schemaJson;
  }
  return clone;
}

function toSchemaDescriptor(
  entry: CultNetRegisteredSchemaRecord,
  includeSchemaJson: boolean,
): CultNetSchemaDescriptor {
  const descriptor: CultNetSchemaDescriptor = {
    schemaId: entry.schemaId,
    kind: entry.kind,
    wireContracts: [...entry.wireContracts],
    contentHash: entry.contentHash,
  };
  if (entry.schemaVersion !== undefined) {
    descriptor.schemaVersion = entry.schemaVersion;
  }
  if (entry.documentType !== undefined) {
    descriptor.documentType = entry.documentType;
  }
  if (entry.title !== undefined) {
    descriptor.title = entry.title;
  }
  if (includeSchemaJson) {
    descriptor.schemaJson = entry.canonicalSchemaJson;
  }

  return descriptor;
}

function stableStringify(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.map((item) => stableStringify(item)).join(",")}]`;
  }

  if (value && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, child]) => `${JSON.stringify(key)}:${stableStringify(child)}`);
    return `{${entries.join(",")}}`;
  }

  return JSON.stringify(value);
}

export const cultNetBuiltinSchemaRegistry = new CultNetSchemaRegistry([
  {
    schemaId: cultNetSchemas.helloSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.helloSchema,
    schemaVersion: "cultnet.hello.v0",
    title: cultNetSchemas.helloSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.transportProfileSchema.$id,
    kind: "shared_contract",
    schema: cultNetSchemas.transportProfileSchema,
    schemaVersion: "cultnet.transport_profile.v0",
    title: cultNetSchemas.transportProfileSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.loginSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.loginSchema,
    schemaVersion: "cultnet.login.v0",
    title: cultNetSchemas.loginSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.registerSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.registerSchema,
    schemaVersion: "cultnet.register.v0",
    title: cultNetSchemas.registerSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.verifySchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.verifySchema,
    schemaVersion: "cultnet.verify.v0",
    title: cultNetSchemas.verifySchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.loginSuccessSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.loginSuccessSchema,
    schemaVersion: "cultnet.login_success.v0",
    title: cultNetSchemas.loginSuccessSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.errorSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.errorSchema,
    schemaVersion: "cultnet.error.v0",
    title: cultNetSchemas.errorSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.sampleChangeNameSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.sampleChangeNameSchema,
    schemaVersion: "cultnet.sample.change_name.v0",
    title: cultNetSchemas.sampleChangeNameSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.sampleChatSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.sampleChatSchema,
    schemaVersion: "cultnet.sample.chat.v0",
    title: cultNetSchemas.sampleChatSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.documentPutSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.documentPutSchema,
    schemaVersion: "cultnet.document_put.v0",
    title: cultNetSchemas.documentPutSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.documentDeleteSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.documentDeleteSchema,
    schemaVersion: "cultnet.document_delete.v0",
    title: cultNetSchemas.documentDeleteSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.rawDocumentRecordSchema.$id,
    kind: "shared_contract",
    schema: cultNetSchemas.rawDocumentRecordSchema,
    title: cultNetSchemas.rawDocumentRecordSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.documentPutRawSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.documentPutRawSchema,
    schemaVersion: "cultnet.document_put_raw.v0",
    title: cultNetSchemas.documentPutRawSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.snapshotRequestSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.snapshotRequestSchema,
    schemaVersion: "cultnet.snapshot_request.v0",
    title: cultNetSchemas.snapshotRequestSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.snapshotResponseSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.snapshotResponseSchema,
    schemaVersion: "cultnet.snapshot_response.v0",
    title: cultNetSchemas.snapshotResponseSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.snapshotResponseRawSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.snapshotResponseRawSchema,
    schemaVersion: "cultnet.snapshot_response_raw.v0",
    title: cultNetSchemas.snapshotResponseRawSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.schemaCatalogRequestSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.schemaCatalogRequestSchema,
    schemaVersion: "cultnet.schema_catalog_request.v0",
    title: cultNetSchemas.schemaCatalogRequestSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.schemaCatalogResponseSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.schemaCatalogResponseSchema,
    schemaVersion: "cultnet.schema_catalog_response.v0",
    title: cultNetSchemas.schemaCatalogResponseSchema.title,
    wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
  },
  {
    schemaId: cultNetSchemas.shardCatalogRequestSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.shardCatalogRequestSchema,
    schemaVersion: "cultnet.shard_catalog_request.v0",
    title: "CultNet Shard Catalog Request Message",
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.shardCatalogResponseSchema.$id,
    kind: "wire_message",
    schema: cultNetSchemas.shardCatalogResponseSchema,
    schemaVersion: "cultnet.shard_catalog_response.v0",
    title: "CultNet Shard Catalog Response Message",
    wireContracts: ["cultnet.schema.v0"],
  },
  {
    schemaId: cultNetSchemas.ghostlightAgentStateSchema.$id,
    kind: "document_payload",
    schema: cultNetSchemas.ghostlightAgentStateSchema,
    schemaVersion: "ghostlight.agent_state.v0",
    documentType: "ghostlight.agent-state",
    title: cultNetSchemas.ghostlightAgentStateSchema.title,
    wireContracts: ["cultnet.schema.v0"],
  },
]);
