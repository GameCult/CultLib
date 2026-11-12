import type { SlotsCodec } from "./codec";
import type { DocumentPayload } from "./document";

export type CultCacheIndexScalar = string | number | boolean | bigint;

export interface CultCacheSchema<TValue = unknown> {
  parse(input: unknown): TValue;
}

export type InferCultCacheSchemaValue<TSchema extends CultCacheSchema> =
  TSchema extends CultCacheSchema<infer TValue> ? TValue : never;

export type CultCacheDocumentFieldName<TValue> = TValue extends object ? Extract<keyof TValue, string> : never;

export type CultCacheDocumentAccessor<TValue> =
  | CultCacheDocumentFieldName<TValue>
  | ((value: TValue) => CultCacheIndexScalar | null | undefined);

export interface CultCacheDocumentFormatter<TValue> {
  encode(value: TValue): Uint8Array;
  decode(payload: Uint8Array): TValue;
}

export interface CultCacheSchemaCatalogMember {
  slot: number;
  memberName: string;
  typeName: string;
  isReference?: boolean;
  isMany?: boolean;
  targetSchemaName?: string | null;
  isName?: boolean;
  indexAlias?: string | null;
}

export interface CultCacheSchemaCatalogEntry {
  schemaId: string;
  schemaName: string;
  schemaVersion: string;
  contentHash: string;
  canonicalSchemaJson: string;
  compatibleSchemaIds?: readonly string[];
  members?: readonly CultCacheSchemaCatalogMember[];
}

export interface CultCacheDocumentIndexDefinition<TValue> {
  name: string;
  accessor: CultCacheDocumentAccessor<TValue>;
}

export interface CultCacheDocumentDefinition<TSchema extends CultCacheSchema = CultCacheSchema> {
  type: string;
  schema: TSchema;
  schemaId?: string;
  schemaName?: string;
  schemaVersion?: string;
  contentHash?: string;
  canonicalSchemaJson?: string;
  compatibleSchemaIds?: readonly string[];
  members?: readonly CultCacheSchemaCatalogMember[];
  global?: boolean;
  name?: CultCacheDocumentAccessor<InferCultCacheSchemaValue<TSchema>>;
  /**
   * Unique lookup aliases. A non-blank value names one record of the type: a write that would leave two records holding
   * one value, and a store that already holds two, is refused naming both keys and the alias. Blank values (absent,
   * empty, or only whitespace) are never indexed. `getByIndex` and `getKeyByIndex` answer from these.
   */
  indexes?:
    | Readonly<Record<string, CultCacheDocumentAccessor<InferCultCacheSchemaValue<TSchema>>>>
    | readonly CultCacheDocumentIndexDefinition<InferCultCacheSchemaValue<TSchema>>[];
  /**
   * Selection and reference aliases: the member's catalog `indexAlias`. A role implies nothing about uniqueness, many
   * records may share a value, and it is never indexed for lookup. An alias is declared in `indexes` or `roles`, not both.
   */
  roles?:
    | Readonly<Record<string, CultCacheDocumentAccessor<InferCultCacheSchemaValue<TSchema>>>>
    | readonly CultCacheDocumentIndexDefinition<InferCultCacheSchemaValue<TSchema>>[];
  formatter?: CultCacheDocumentFormatter<InferCultCacheSchemaValue<TSchema>>;
  codec?: SlotsCodec<InferCultCacheSchemaValue<TSchema> & object>;
}

export type CultCacheDocumentValue<TDefinition extends CultCacheDocumentDefinition> = InferCultCacheSchemaValue<
  TDefinition["schema"]
>;

export type AnyCultCacheDocumentDefinition = CultCacheDocumentDefinition<any>;

export interface CultCacheDocumentRegistry<
  TDefinitions extends readonly AnyCultCacheDocumentDefinition[] = readonly AnyCultCacheDocumentDefinition[],
> {
  readonly definitions: TDefinitions;
}

export interface CultCacheEnvelope {
  key: string;
  type: string;
  payload: DocumentPayload;
  storedAt: string;
  schemaId?: string;
  catalogEntry?: CultCacheSchemaCatalogEntry;
}

/**
 * What a payload decode found past the slots this build declares, at any depth: the first type read with extra slots, how many
 * slots it carried against how many it declares, and how many objects in the record did. It holds no slot value, so a refusal
 * can name it. A record carrying it is read-only unless `explained`.
 */
export interface CultCacheUnreadSlots {
  /** The first type read with extra slots (the unit arm's arm name for a unit arm). */
  readonly typeName: string;
  readonly slotCount: number;
  readonly declaredCount: number;
  readonly occurrences: number;
  /** True when every mark sat in the document's own object. */
  readonly depth0Only: boolean;
  /**
   * True only when every mark is at depth 0 and the schema comparison of the id the record was stored under reported every slot
   * the mark carries (slotCount is at most the highest reported slot plus one): the loss a compatible-id declaration consents to.
   */
  readonly explained: boolean;
}

export interface PushAllOptions {
  soft?: boolean;
}

export interface CacheBackingStore {
  pullAll(): Promise<CultCacheEnvelope[]>;
  push(entry: CultCacheEnvelope): Promise<void>;
  delete(entry: CultCacheEnvelope): Promise<void>;
  pushAll?(entries: CultCacheEnvelope[], options?: PushAllOptions): Promise<void>;
}

export interface CultCacheStoreRegistration {
  store: CacheBackingStore;
  types: string[];
}
