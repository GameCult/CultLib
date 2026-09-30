import { decode, encode } from "@msgpack/msgpack";
import {
  type AnyCultCacheDocumentDefinition,
  type CultCache,
  type CultCacheDocumentDefinition,
  type CultCacheDocumentFormatter,
  type CultCacheDocumentValue,
  type CultCacheEnvelope,
  SchemaConflictError,
  schemaIdentityOf,
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
  // The binding that owns each schema id, and every binding listing an id as compatible, in registration order.
  readonly #owners = new Map<string, CultNetDocumentBinding>();
  readonly #listers = new Map<string, CultNetDocumentBinding[]>();

  constructor(bindings: Iterable<CultNetDocumentBinding> = []) {
    for (const binding of bindings) {
      this.register(binding);
    }
  }

  register(binding: CultNetDocumentBinding): this {
    // One definition owns a type, a schema id and a schema name, derived as CultCache derives them. Another definition
    // claiming any of them is refused, so a schema never resolves to two types.
    const identity = schemaIdentityOf(binding.definition);
    for (const holder of this.#bindings.values()) {
      if (holder.definition === binding.definition) {
        continue;
      }
      const held = schemaIdentityOf(holder.definition);
      const claimed =
        holder.definition.type === binding.definition.type ? `type "${binding.definition.type}"`
        : held.schemaId === identity.schemaId ? `schema id "${identity.schemaId}"`
        : held.schemaName === identity.schemaName ? `schema name "${identity.schemaName}"`
        : undefined;
      if (claimed) {
        throw new SchemaConflictError(identity.schemaId, [held.schemaName, identity.schemaName], "",
          `CultNet ${claimed} is already bound to type "${holder.definition.type}"; type "${binding.definition.type}" (schema id "${identity.schemaId}") cannot also be bound.`);
      }
    }
    this.#bindings.set(binding.definition.type, binding);
    this.#owners.set(identity.schemaId, binding);
    for (const schemaId of identity.compatibleSchemaIds) {
      const listers = this.#listers.get(schemaId) ?? [];
      if (schemaId !== identity.schemaId && !listers.some(lister => lister.definition === binding.definition)) {
        this.#listers.set(schemaId, [...listers, binding]);
      }
    }
    return this;
  }

  get(documentType: string): CultNetDocumentBinding | undefined {
    return this.#bindings.get(documentType);
  }

  // The binding a record under this schema id belongs to: the id's owner, else the one binding that lists it as
  // compatible. An id nothing declares resolves to nothing. An id that several bindings list and none owns is refused,
  // as the cache refuses it, whatever order the bindings were registered in.
  getBySchemaId(schemaId: string): CultNetDocumentBinding | undefined {
    const owner = this.#owners.get(schemaId);
    if (owner) {
      return owner;
    }
    const listers = this.#listers.get(schemaId) ?? [];
    if (listers.length > 1) {
      const types = listers.map(lister => lister.definition.type).sort();
      throw new SchemaConflictError(schemaId, listers.map(lister => schemaIdentityOf(lister.definition).schemaName).sort(), "",
        `CultNet schema id "${schemaId}" is owned by no bound type and declared compatible by several (${types.join(", ")}), ` +
        "so it resolves to none. Bind the type that owns the id, or declare it on one type.");
    }
    return listers[0];
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

      if (requestedSchemaIds && !answersTo(schemaId, binding, requestedSchemaIds)) {
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

      if (requestedSchemaIds && !answersTo(schemaId, binding, requestedSchemaIds)) {
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
    const binding = this.#requireSchemaBinding(message.document.schemaId);
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
  return schemaIdentityOf(binding.definition).schemaId;
}

// A held record matches a snapshot filter when the filter names the id it carries or any id its binding answers to.
function answersTo(
  schemaId: string,
  binding: CultNetDocumentBinding,
  requestedSchemaIds: Set<string>,
): boolean {
  return requestedSchemaIds.has(schemaId)
    || schemaIdentityOf(binding.definition).compatibleSchemaIds.some(candidate => requestedSchemaIds.has(candidate));
}

function schemaIdForEnvelope(
  envelope: CultCacheEnvelope,
  binding: CultNetDocumentBinding,
): string {
  return envelope.schemaId ?? schemaIdForBinding(binding);
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
