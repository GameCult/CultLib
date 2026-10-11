import { resolveDocumentFormatter } from "./document-formatter";
import type {
  AnyCultCacheDocumentDefinition,
  CultCacheSchemaCatalogMember,
  CultCacheDocumentDefinition,
  CultCacheDocumentRegistry,
  CultCacheDocumentValue,
  CultCacheSchema,
} from "./types";

export function defineDocumentType<TSchema extends CultCacheSchema>(
  definition: CultCacheDocumentDefinition<TSchema>,
): CultCacheDocumentDefinition<TSchema> {
  if (!definition.type || definition.type.trim().length === 0) {
    throw new Error("CultCache document types must declare a non-empty type.");
  }

  for (const [fieldName, fieldValue] of [
    ["schemaId", definition.schemaId],
    ["schemaName", definition.schemaName],
    ["schemaVersion", definition.schemaVersion],
    ["contentHash", definition.contentHash],
    ["canonicalSchemaJson", definition.canonicalSchemaJson],
  ] as const) {
    if (fieldValue !== undefined && fieldValue.trim().length === 0) {
      throw new Error(`CultCache document type "${definition.type}" declares an empty ${fieldName}.`);
    }
  }

  if (definition.name !== undefined && typeof definition.name !== "string" && typeof definition.name !== "function") {
    throw new Error(`CultCache document type "${definition.type}" declares an invalid name accessor.`);
  }

  if (Array.isArray(definition.indexes)) {
    const seen = new Set<string>();
    for (const index of definition.indexes) {
      if (!index.name || index.name.trim().length === 0) {
        throw new Error(`CultCache document type "${definition.type}" declares an index with an empty name.`);
      }
      if (seen.has(index.name)) {
        throw new Error(`CultCache document type "${definition.type}" declares duplicate index "${index.name}".`);
      }
      seen.add(index.name);
    }
  } else if (definition.indexes) {
    for (const name of Object.keys(definition.indexes)) {
      if (!name || name.trim().length === 0) {
        throw new Error(`CultCache document type "${definition.type}" declares an index with an empty name.`);
      }
    }
  }

  if (definition.members) {
    const seenSlots = new Set<number>();
    for (const member of definition.members) {
      if (!Number.isInteger(member.slot) || member.slot < 0) {
        throw new Error(`CultCache document type "${definition.type}" declares an invalid schema member slot.`);
      }
      if (seenSlots.has(member.slot)) {
        throw new Error(`CultCache document type "${definition.type}" declares duplicate schema member slot ${member.slot}.`);
      }
      seenSlots.add(member.slot);
      if (!member.memberName || member.memberName.trim().length === 0) {
        throw new Error(`CultCache document type "${definition.type}" declares a schema member with an empty name.`);
      }
      if (!member.typeName || member.typeName.trim().length === 0) {
        throw new Error(`CultCache document type "${definition.type}" declares schema member "${member.memberName}" with an empty type.`);
      }
    }
  }

  resolveDocumentFormatter(definition);
  return Object.freeze(definition.codec ? { ...definition, members: resolveDocumentMembers(definition) } : { ...definition });
}

/**
 * The bytes of one document, as they travel in a store envelope, a CultNet record or a realtime frame.
 *
 * At run time this is the same `Uint8Array`, so frames encode as they always did. At compile time it is opaque: it is not
 * assignable to or from `Uint8Array` or `ArrayLike<number>`, so no byte decoder accepts it. The only reader is
 * `decodeDocumentPayload(definition, payload)`, which needs the definition that says how the bytes are laid out, and the
 * only writer is `encodeDocumentPayload(definition, value)`. The two escapes are named: `documentPayloadFromBytes` where
 * bytes enter (a validated record, a store read, tests, a writer of pre-encoded or signed bytes) and
 * `documentPayloadBytes` where they leave (a store write, a frame encode, hashing or signing).
 *
 * The key is a plain string, not a unique symbol, so two installed copies of this package stay compatible.
 *
 * What the type guarantees: a payload cannot be read or assigned directly (a decoder call, `const b: Uint8Array = p`,
 * `new Uint8Array(p)` fail to compile). What it does not: the runtime value is a `Uint8Array`, so runtime narrowing
 * (`p instanceof Uint8Array`) or a cast gets the raw bytes back, and a codec or formatter document read that way is not
 * the document. The escape for bytes is `documentPayloadBytes`; the reader is `decodeDocumentPayload`.
 */
export interface DocumentPayload {
  readonly "cultcache.documentPayload": true;
  readonly byteLength: number;
}

export function documentPayloadFromBytes(bytes: Uint8Array): DocumentPayload {
  return bytes as unknown as DocumentPayload;
}

export function documentPayloadBytes(payload: DocumentPayload): Uint8Array {
  return payload as unknown as Uint8Array;
}

// The one reader of a document payload: through the definition's formatter, codec or plain map, then its schema. A
// reader holds the definition or it has no business reading the bytes, so there is no definition-less form.
export function decodeDocumentPayload<TDefinition extends AnyCultCacheDocumentDefinition>(
  definition: TDefinition,
  payload: DocumentPayload,
): CultCacheDocumentValue<TDefinition> {
  const decoded = resolveDocumentFormatter(definition).decode(documentPayloadBytes(payload));
  return definition.schema.parse(decoded) as CultCacheDocumentValue<TDefinition>;
}

// The one writer: the definition's schema first, then its formatter, codec or plain map.
export function encodeDocumentPayload(definition: AnyCultCacheDocumentDefinition, value: unknown): DocumentPayload {
  return documentPayloadFromBytes(resolveDocumentFormatter(definition).encode(definition.schema.parse(value)));
}

// The schema members of a document: the ones it declares, else those its codec derives, else none.
export function resolveDocumentMembers(definition: AnyCultCacheDocumentDefinition): readonly CultCacheSchemaCatalogMember[] {
  if (definition.members || !definition.codec) {
    return definition.members ?? [];
  }
  const codec = definition.codec;
  const indexAlias = (slotName: string) =>
    !Array.isArray(definition.indexes) && definition.indexes
      ? Object.entries(definition.indexes).find(([, accessor]) => accessor === slotName)?.[0] ?? null
      : null;
  return codec.members.map((member, index) => ({
    ...member,
    isName: (definition.name as unknown) === codec.names[index],
    indexAlias: indexAlias(codec.names[index]),
  }));
}

export function defineDocumentRegistry<
  TDefinitions extends readonly AnyCultCacheDocumentDefinition[],
>(...definitions: TDefinitions): CultCacheDocumentRegistry<TDefinitions> {
  return Object.freeze({
    definitions: Object.freeze([...definitions]) as TDefinitions,
  });
}
