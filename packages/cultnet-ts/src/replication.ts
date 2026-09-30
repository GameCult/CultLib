import { decode, encode } from "@msgpack/msgpack";
import {
  type AnyCultCacheDocumentDefinition,
  type CultCache,
  type CultCacheDocumentDefinition,
  type CultCacheDocumentFormatter,
  type CultCacheDocumentValue,
  type CultCacheEnvelope,
  SchemaConflictError,
} from "@gamecult/cultcache-ts";

import {
  type CultNetDocumentDeleteMessage,
  type CultNetDocumentPutMessage,
  type CultNetDocumentPutRawMessage,
  type CultNetDocumentRecord,
  type CultNetRawDocumentRecord,
  type CultNetSnapshotRequestMessage,
  type CultNetSnapshotResponseMessage,
  type CultNetSnapshotResponseRawMessage,
} from "./contracts";

export interface CultNetDocumentBinding<
  TDefinition extends AnyCultCacheDocumentDefinition = AnyCultCacheDocumentDefinition,
> {
  definition: TDefinition;
  payloadSchemaVersion?:
    | string
    | ((value: CultCacheDocumentValue<TDefinition>) => string | undefined);
}

export function defineCultNetDocumentBinding<
  TDefinition extends AnyCultCacheDocumentDefinition,
>(
  binding: CultNetDocumentBinding<TDefinition>,
): CultNetDocumentBinding<TDefinition> {
  return Object.freeze({ ...binding });
}

export class CultNetDocumentRegistry {
  readonly #bindings = new Map<string, CultNetDocumentBinding>();
  readonly #schemaBindings = new Map<string, CultNetDocumentBinding>();

  constructor(bindings: Iterable<CultNetDocumentBinding> = []) {
    for (const binding of bindings) {
      this.register(binding);
    }
  }

  register(binding: CultNetDocumentBinding): this {
    // One definition owns a type, a schema id and a schema name. Another definition claiming any of them is
    // refused, so a schema never resolves to two types.
    const schemaId = schemaIdForBinding(binding);
    const schemaName = schemaNameForBinding(binding);
    for (const holder of this.#uniqueBindings()) {
      if (holder.definition === binding.definition) {
        continue;
      }
      const claimed =
        holder.definition.type === binding.definition.type ? `type "${binding.definition.type}"`
        : schemaIdForBinding(holder) === schemaId ? `schema id "${schemaId}"`
        : schemaNameForBinding(holder) === schemaName ? `schema name "${schemaName}"`
        : undefined;
      if (claimed) {
        throw new SchemaConflictError(schemaId, [schemaNameForBinding(holder), schemaName], "",
          `CultNet ${claimed} is already bound to type "${holder.definition.type}"; type "${binding.definition.type}" (schema id "${schemaId}") cannot also be bound.`);
      }
    }
    this.#bindings.set(binding.definition.type, binding);
    this.#schemaBindings.set(schemaIdForBinding(binding), binding);
    return this;
  }

  get(documentType: string): CultNetDocumentBinding | undefined {
    return this.#bindings.get(documentType);
  }

  getBySchemaId(schemaId: string): CultNetDocumentBinding | undefined {
    const exact = this.#schemaBindings.get(schemaId);
    if (exact) {
      return exact;
    }

    const requestedSchemaIds = new Set([schemaId]);
    return this.#uniqueBindings().find(binding =>
      schemaMatchesBinding(schemaIdForBinding(binding), binding, requestedSchemaIds),
    );
  }

  createDocumentPutMessage<TDefinition extends AnyCultCacheDocumentDefinition>(
    binding: CultNetDocumentBinding<TDefinition>,
    messageId: string,
    documentKey: string,
    value: CultCacheDocumentValue<TDefinition>,
    options: {
      storedAt?: string;
      sourceRuntimeId?: string;
      sourceAgentId?: string;
      sourceRole?: string;
      tags?: string[];
    } = {},
  ): CultNetDocumentPutMessage<CultCacheDocumentValue<TDefinition>> {
    const parsed = binding.definition.schema.parse(value);
    return {
      schemaVersion: "cultnet.document_put.v0",
      messageId,
      document: {
        schemaId: schemaIdForBinding(binding),
        recordKey: documentKey,
        storedAt: options.storedAt ?? new Date().toISOString(),
        payload: parsed,
        sourceRuntimeId: options.sourceRuntimeId,
        sourceAgentId: options.sourceAgentId,
        sourceRole: options.sourceRole,
        tags: options.tags,
      },
    };
  }

  createDocumentDeleteMessage(
    messageId: string,
    schemaId: string,
    recordKey: string,
  ): CultNetDocumentDeleteMessage {
    return {
      schemaVersion: "cultnet.document_delete.v0",
      messageId,
      schemaId,
      recordKey,
    };
  }

  createRawDocumentPutMessageFromEnvelope(
    messageId: string,
    envelope: CultCacheEnvelope,
  ): CultNetDocumentPutRawMessage {
    return {
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId,
      document: this.#createRawDocumentRecord(envelope),
    };
  }

  createRawDocumentPutMessage<TDefinition extends AnyCultCacheDocumentDefinition>(
    binding: CultNetDocumentBinding<TDefinition>,
    messageId: string,
    documentKey: string,
    value: CultCacheDocumentValue<TDefinition>,
    options: {
      storedAt?: string;
      sourceRuntimeId?: string;
      sourceAgentId?: string;
      sourceRole?: string;
      tags?: string[];
    } = {},
  ): CultNetDocumentPutRawMessage {
    const parsed = binding.definition.schema.parse(value);
    const formatter = formatterFor(binding.definition);
    return {
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId,
      document: {
        schemaId: schemaIdForBinding(binding),
        recordKey: documentKey,
        storedAt: options.storedAt ?? new Date().toISOString(),
        payloadEncoding: "messagepack",
        payload: formatter.encode(parsed),
        sourceRuntimeId: options.sourceRuntimeId,
        sourceAgentId: options.sourceAgentId,
        sourceRole: options.sourceRole,
        tags: options.tags,
      },
    };
  }

  createSnapshotResponse(
    cache: CultCache,
    messageId: string,
    filter?: CultNetSnapshotRequestMessage,
  ): CultNetSnapshotResponseMessage {
    const requestedSchemaIds = filter?.schemaIds ? new Set(filter.schemaIds) : undefined;
    const requestedKeys = filter?.recordKeys ? new Set(filter.recordKeys) : undefined;
    const documents: CultNetDocumentRecord[] = [];

    for (const envelope of cache.snapshot()) {
      const binding = this.#requireBinding(envelope.type);
      const schemaId = schemaIdForEnvelope(envelope, binding);

      if (requestedSchemaIds && !schemaMatchesBinding(schemaId, binding, requestedSchemaIds)) {
        continue;
      }

      if (requestedKeys && !requestedKeys.has(envelope.key)) {
        continue;
      }

      const payload = decodeDocumentValue(binding.definition, envelope.payload);
      documents.push({
        schemaId,
        recordKey: envelope.key,
        storedAt: envelope.storedAt,
        payload,
      });
    }

    return {
      schemaVersion: "cultnet.snapshot_response.v0",
      messageId,
      documents,
    };
  }

  createRawSnapshotResponse(
    cache: CultCache,
    messageId: string,
    filter?: CultNetSnapshotRequestMessage,
  ): CultNetSnapshotResponseRawMessage {
    const requestedSchemaIds = filter?.schemaIds ? new Set(filter.schemaIds) : undefined;
    const requestedKeys = filter?.recordKeys ? new Set(filter.recordKeys) : undefined;
    const documents: CultNetRawDocumentRecord[] = [];

    for (const envelope of cache.snapshot()) {
      const binding = this.#requireBinding(envelope.type);
      const schemaId = schemaIdForEnvelope(envelope, binding);

      if (requestedSchemaIds && !schemaMatchesBinding(schemaId, binding, requestedSchemaIds)) {
        continue;
      }

      if (requestedKeys && !requestedKeys.has(envelope.key)) {
        continue;
      }

      documents.push(this.#createRawDocumentRecord(envelope));
    }

    return {
      schemaVersion: "cultnet.snapshot_response_raw.v0",
      messageId,
      documents,
    };
  }

  async applyDocumentPutMessage(
    cache: CultCache,
    message: CultNetDocumentPutMessage,
  ): Promise<unknown> {
    const binding = this.#requireSchemaBinding(message.document.schemaId);
    return cache.put(
      binding.definition,
      message.document.recordKey,
      binding.definition.schema.parse(message.document.payload),
    );
  }

  async applyDocumentDeleteMessage(
    cache: CultCache,
    message: CultNetDocumentDeleteMessage,
  ): Promise<boolean> {
    const binding = this.#requireSchemaBinding(message.schemaId);
    return cache.delete(binding.definition, message.recordKey);
  }

  async applyRawDocumentPutMessage(
    cache: CultCache,
    message: CultNetDocumentPutRawMessage,
  ): Promise<unknown> {
    const binding = this.#resolveRawDocumentBinding(message.document);
    return cache.putEnvelope(binding.definition, {
      key: message.document.recordKey,
      type: binding.definition.type,
      schemaId: schemaIdForBinding(binding),
      payload: new Uint8Array(message.document.payload),
      storedAt: message.document.storedAt,
    });
  }

  async applySnapshotResponse(
    cache: CultCache,
    response: CultNetSnapshotResponseMessage,
  ): Promise<void> {
    for (const document of response.documents) {
      await this.applyDocumentPutMessage(cache, {
        schemaVersion: "cultnet.document_put.v0",
        messageId: response.messageId,
        document,
      });
    }
  }

  async applyRawSnapshotResponse(
    cache: CultCache,
    response: CultNetSnapshotResponseRawMessage,
  ): Promise<void> {
    for (const document of response.documents) {
      await this.applyRawDocumentPutMessage(cache, {
        schemaVersion: "cultnet.document_put_raw.v0",
        messageId: response.messageId,
        document,
      });
    }
  }

  #requireBinding(documentType: string): CultNetDocumentBinding {
    const binding = this.get(documentType);
    if (!binding) {
      throw new Error(`No CultNet document binding is registered for "${documentType}".`);
    }

    return binding;
  }

  #requireSchemaBinding(schemaId: string): CultNetDocumentBinding {
    const binding = this.getBySchemaId(schemaId);
    if (!binding) {
      throw new Error(`No CultNet document binding is registered for schema "${schemaId}".`);
    }

    return binding;
  }

  // routes a replicated raw record to a local binding; the store always persists the local schema id; this is not store-reader recovery.
  #resolveRawDocumentBinding(document: CultNetRawDocumentRecord): CultNetDocumentBinding {
    const exact = this.getBySchemaId(document.schemaId);
    if (exact) {
      return exact;
    }

    const payload = new Uint8Array(document.payload);
    for (const binding of this.#uniqueBindings()) {
      try {
        decodeDocumentValue(binding.definition, payload);
        return binding;
      } catch {
        // Keep looking; a foreign schema id is acceptable only when the payload validates locally.
      }
    }

    throw new Error(`No CultNet document binding is registered for schema "${document.schemaId}".`);
  }

  #uniqueBindings(): CultNetDocumentBinding[] {
    return [...new Set([
      ...this.#schemaBindings.values(),
      ...this.#bindings.values(),
    ])];
  }

  #createRawDocumentRecord(envelope: CultCacheEnvelope): CultNetRawDocumentRecord {
    const binding = this.#requireBinding(envelope.type);
    return {
      schemaId: schemaIdForEnvelope(envelope, binding),
      recordKey: envelope.key,
      storedAt: envelope.storedAt,
      payloadEncoding: "messagepack",
      payload: new Uint8Array(envelope.payload),
    };
  }
}

function schemaIdForBinding(binding: CultNetDocumentBinding): string {
  return binding.definition.schemaId ?? binding.definition.type;
}

function schemaNameForBinding(binding: CultNetDocumentBinding): string {
  return binding.definition.schemaName ?? binding.definition.type;
}

function schemaMatchesBinding(
  schemaId: string,
  binding: CultNetDocumentBinding,
  requestedSchemaIds: Set<string>,
): boolean {
  if (requestedSchemaIds.has(schemaId)) {
    return true;
  }

  const definition = binding.definition;
  if (definition.schemaId && requestedSchemaIds.has(definition.schemaId)) {
    return true;
  }
  if (definition.schemaName && requestedSchemaIds.has(definition.schemaName)) {
    return true;
  }
  if (definition.schemaVersion && requestedSchemaIds.has(definition.schemaVersion)) {
    return true;
  }
  if (definition.compatibleSchemaIds?.some(candidate => requestedSchemaIds.has(candidate))) {
    return true;
  }

  return Array.from(requestedSchemaIds)
    .some(candidate => inferSchemaName(candidate) === definition.schemaName);
}

function inferSchemaName(schemaId: string): string | undefined {
  const marker = schemaId.lastIndexOf(".v");
  if (marker <= 0 || marker + 2 >= schemaId.length) {
    return undefined;
  }
  const version = schemaId.slice(marker + 2);
  return /^\d+$/u.test(version) ? schemaId.slice(0, marker) : undefined;
}

function schemaIdForEnvelope(
  envelope: CultCacheEnvelope,
  binding: CultNetDocumentBinding,
): string {
  return envelope.schemaId ?? schemaIdForBinding(binding);
}

function resolvePayloadSchemaVersion<TDefinition extends AnyCultCacheDocumentDefinition>(
  binding: CultNetDocumentBinding<TDefinition>,
  value: CultCacheDocumentValue<TDefinition>,
): string | undefined {
  if (typeof binding.payloadSchemaVersion === "function") {
    return binding.payloadSchemaVersion(value);
  }

  return binding.payloadSchemaVersion;
}

function decodeDocumentValue<TDefinition extends CultCacheDocumentDefinition>(
  definition: TDefinition,
  payload: Uint8Array,
): CultCacheDocumentValue<TDefinition> {
  const formatter = formatterFor(definition);
  const decoded = formatter.decode(payload);
  return definition.schema.parse(decoded) as CultCacheDocumentValue<TDefinition>;
}

function formatterFor<TDefinition extends CultCacheDocumentDefinition>(
  definition: TDefinition,
): CultCacheDocumentFormatter<CultCacheDocumentValue<TDefinition>> {
  return (definition.formatter as CultCacheDocumentFormatter<
    CultCacheDocumentValue<TDefinition>
  > | undefined) ?? {
    encode: (value: CultCacheDocumentValue<TDefinition>) => encode(value),
    decode: (bytes: Uint8Array) => decode(bytes) as CultCacheDocumentValue<TDefinition>,
  };
}
