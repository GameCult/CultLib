import { decode, encode } from "@msgpack/msgpack";

import { codecFormatter } from "./codec";
import type {
  AnyCultCacheDocumentDefinition,
  CultCacheDocumentFormatter,
  CultCacheSchemaCatalogMember,
  CultCacheDocumentDefinition,
  CultCacheDocumentRegistry,
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

// The one place that decides how a document is encoded: its own formatter, or its codec's, or a plain map. A codec and a
// formatter are two answers to one question, so a definition that carries both is refused wherever it enters (the
// builder resolves through here as well).
export function resolveDocumentFormatter(definition: AnyCultCacheDocumentDefinition): CultCacheDocumentFormatter<unknown> {
  if (definition.codec && definition.formatter) {
    throw new Error(`CultCache document type "${definition.type}" declares both a codec and a formatter.`);
  }
  return definition.formatter ?? (definition.codec ? codecFormatter(definition.codec) : {
    encode: (value: unknown) => encode(value),
    decode: (payload: Uint8Array) => decode(payload),
  });
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
    isName: definition.name === codec.names[index],
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
