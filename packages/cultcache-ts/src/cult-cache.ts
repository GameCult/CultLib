import { decode, encode } from "@msgpack/msgpack";

import type {
  AnyCultCacheDocumentDefinition,
  CacheBackingStore,
  CultCacheDocumentAccessor,
  CultCacheDocumentDefinition,
  CultCacheDocumentFormatter,
  CultCacheDocumentRegistry,
  CultCacheDocumentValue,
  CultCacheEnvelope,
  CultCacheSchemaCatalogEntry,
  CultCacheStoreRegistration,
} from "./types";

type RegisteredDefinition = {
  readonly definition: AnyCultCacheDocumentDefinition;
  readonly global: boolean;
  readonly formatter: CultCacheDocumentFormatter<unknown>;
  readonly catalogEntry: CultCacheSchemaCatalogEntry;
  nameAccessor?: (value: unknown) => string | undefined;
  readonly indexAccessors: Map<string, (value: unknown) => string | undefined>;
};

type HydratedEntry = CultCacheEnvelope & {
  value: unknown;
};

// Everything a pull replaces. Built whole, then swapped in with one assignment.
type HydratedState = {
  readonly entries: Map<string, HydratedEntry>;
  readonly typeEntryIds: Map<string, Set<string>>;
  readonly nameToKeyMaps: Map<string, Map<string, string>>;
  readonly indexToKeyMaps: Map<string, Map<string, string>>;
  readonly globalKeys: Map<string, string>;
};

function emptyHydratedState(): HydratedState {
  return {
    entries: new Map(),
    typeEntryIds: new Map(),
    nameToKeyMaps: new Map(),
    indexToKeyMaps: new Map(),
    globalKeys: new Map(),
  };
}

function getOrCreate<K, V>(map: Map<K, V>, key: K, create: () => V): V {
  let value = map.get(key);
  if (value === undefined) {
    value = create();
    map.set(key, value);
  }
  return value;
}

type BackingStoreTypeReference = string | AnyCultCacheDocumentDefinition;
type AccessorInput = string | ((value: any) => unknown);

export class CultCacheBuilder {
  readonly #cache = new CultCache();

  withDocumentType<TDefinition extends AnyCultCacheDocumentDefinition>(definition: TDefinition): this {
    this.#cache.registerDocumentType(definition);
    return this;
  }

  withRegistry(
    registry:
      | CultCacheDocumentRegistry
      | Iterable<AnyCultCacheDocumentDefinition>,
  ): this {
    this.#cache.registerRegistry(registry);
    return this;
  }

  withBackingStore(
    store: CacheBackingStore,
    ...types: BackingStoreTypeReference[]
  ): this {
    this.#cache.addBackingStore(store, ...types);
    return this;
  }

  withGenericStore(store: CacheBackingStore): this {
    this.#cache.addGenericBackingStore(store);
    return this;
  }

  build(): CultCache {
    return this.#cache;
  }
}

export class CultCache {
  static readonly GLOBAL_KEY = "__global__";

  readonly #definitions = new Map<string, RegisteredDefinition>();
  readonly #schemaIdDefinitions = new Map<string, RegisteredDefinition>();
  readonly #schemaNameDefinitions = new Map<string, RegisteredDefinition>();
  #hydrated = emptyHydratedState();
  readonly #stores: CultCacheStoreRegistration[] = [];

  static builder(): CultCacheBuilder {
    return new CultCacheBuilder();
  }

  registerDocumentType<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): TDefinition {
    const existing = this.#definitions.get(definition.type);
    if (existing && existing.definition !== definition) {
      throw new Error(`CultCache already has a different definition registered for type "${definition.type}".`);
    }

    const registered = existing ?? {
      definition,
      global: definition.global === true,
      formatter: this.#createFormatter(definition),
      catalogEntry: this.#createCatalogEntry(definition),
      nameAccessor: undefined,
      indexAccessors: new Map<string, (value: unknown) => string | undefined>(),
    };

    registered.nameAccessor = definition.name
      ? this.#compileAccessor(definition.type, "name", definition.name)
      : undefined;
    registered.indexAccessors.clear();
    for (const [indexName, accessor] of this.#enumerateDefinitionIndexes(definition)) {
      registered.indexAccessors.set(
        indexName,
        this.#compileAccessor(definition.type, `index "${indexName}"`, accessor),
      );
    }

    this.#definitions.set(definition.type, registered);
    for (const schemaId of registered.catalogEntry.compatibleSchemaIds ?? [registered.catalogEntry.schemaId]) {
      const existingBySchema = this.#schemaIdDefinitions.get(schemaId);
      if (existingBySchema && existingBySchema !== registered) {
        throw new Error(`CultCache schema id "${schemaId}" is already registered for type "${existingBySchema.definition.type}".`);
      }
      this.#schemaIdDefinitions.set(schemaId, registered);
    }
    const existingBySchemaName = this.#schemaNameDefinitions.get(registered.catalogEntry.schemaName);
    if (existingBySchemaName && existingBySchemaName !== registered) {
      throw new Error(
        `CultCache schema name "${registered.catalogEntry.schemaName}" is already registered for type "${existingBySchemaName.definition.type}".`,
      );
    }
    this.#schemaNameDefinitions.set(registered.catalogEntry.schemaName, registered);
    this.#rebuildDefinitionLookups(registered);
    return definition;
  }

  registerRegistry(
    registry:
      | CultCacheDocumentRegistry
      | Iterable<AnyCultCacheDocumentDefinition>,
  ): this {
    const definitions = Symbol.iterator in Object(registry) && !("definitions" in Object(registry))
      ? registry as Iterable<AnyCultCacheDocumentDefinition>
      : (registry as CultCacheDocumentRegistry).definitions;

    for (const definition of definitions) {
      this.registerDocumentType(definition);
    }

    return this;
  }

  registerNameLookup<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    accessor: CultCacheDocumentAccessor<CultCacheDocumentValue<TDefinition>>,
  ): TDefinition {
    const registered = this.#requireDefinition(definition);
    registered.nameAccessor = this.#compileAccessor(definition.type, "name", accessor);
    this.#rebuildNameLookup(registered);
    return definition;
  }

  registerIndex<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    indexName: string,
    accessor: CultCacheDocumentAccessor<CultCacheDocumentValue<TDefinition>>,
  ): TDefinition {
    if (!indexName || indexName.trim().length === 0) {
      throw new Error(`CultCache index names for type "${definition.type}" must be non-empty.`);
    }

    const registered = this.#requireDefinition(definition);
    registered.indexAccessors.set(
      indexName,
      this.#compileAccessor(definition.type, `index "${indexName}"`, accessor),
    );
    this.#rebuildIndexLookup(registered, indexName);
    return definition;
  }

  addBackingStore(
    store: CacheBackingStore,
    ...types: BackingStoreTypeReference[]
  ): void {
    const claimed = [...new Set(types.map((value) => (typeof value === "string" ? value : value.type)))];
    if (claimed.length === 0 && this.#stores.some((registration) => registration.types.length === 0)) {
      throw new Error("Backing store would be a second generic store; name the types it is home to.");
    }
    for (const type of claimed) {
      if (this.#stores.some((registration) => registration.types.includes(type))) {
        throw new Error(`CultCache type "${type}" is already routed to another backing store; it cannot also route to this one.`);
      }
    }
    const candidate: CultCacheStoreRegistration = { store, types: claimed };
    for (const type of this.#hydrated.typeEntryIds.keys()) {
      const before = this.#resolveRegistration(type);
      this.#stores.push(candidate);
      const after = this.#resolveRegistration(type);
      this.#stores.pop();
      if (before !== after) {
        throw new Error(
          `Attaching this backing store would move "${type}" from ${this.#describe(before)} to ${this.#describe(after)}; ` +
            "attach routed stores before the generic store.",
        );
      }
    }
    this.#stores.push(candidate);
  }

  addGenericBackingStore(store: CacheBackingStore): void {
    this.addBackingStore(store);
  }

  // The complete next state (entries, globals, name and index lookups) is built into a fresh
  // HydratedState, running every check and every user accessor, and replaces the cache's state
  // in one assignment only once the build finishes. A refused load admits nothing.
  async pullAllBackingStores(): Promise<void> {
    const next = emptyHydratedState();

    for (const registration of this.#stores) {
      const entries = await registration.store.pullAll();

      for (const entry of entries) {
        const registered = this.#resolveDefinitionForEnvelope(entry);
        if (!registered) {
          throw new Error(
            entry.schemaId
              ? `No schema is registered for persisted schema id "${entry.schemaId}".`
              : `No schema is registered for persisted document type "${entry.type}".`,
          );
        }

        const type = registered.definition.type;
        const home = this.#resolveRegistration(type);
        if (home?.store !== registration.store) {
          throw new Error(
            `CultCache "${type}" record "${entry.key}" was loaded from ${this.#describe(registration)}, ` +
              `but its home is ${this.#describe(home)}.`,
          );
        }

        const payload = this.#cloneBytes(entry.payload);
        const value = registered.formatter.decode(payload);
        this.#applyHydratedEntry(next, registered, { ...entry, type, payload }, value);
      }
    }

    this.#hydrated = next;
  }

  get<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    this.#requireDefinition(definition);
    const entry = this.#hydrated.entries.get(this.#entryId(definition.type, key));
    return entry ? (entry.value as CultCacheDocumentValue<TDefinition>) : undefined;
  }

  getRequired<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> {
    const value = this.get(definition, key);
    if (value === undefined) {
      throw new Error(`CultCache has no "${definition.type}" document at key "${key}".`);
    }

    return value;
  }

  getEnvelope<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheEnvelope | undefined {
    this.#requireDefinition(definition);
    const entry = this.#hydrated.entries.get(this.#entryId(definition.type, key));
    return entry ? this.#toEnvelope(entry) : undefined;
  }

  getRequiredEnvelope<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheEnvelope {
    const entry = this.getEnvelope(definition, key);
    if (!entry) {
      throw new Error(`CultCache has no "${definition.type}" envelope at key "${key}".`);
    }

    return entry;
  }

  getGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    const registered = this.#requireDefinition(definition);
    if (!registered.global) {
      throw new Error(`CultCache document type "${definition.type}" is not marked as global.`);
    }

    const globalKey = this.#hydrated.globalKeys.get(definition.type);
    return globalKey ? this.get(definition, globalKey) : undefined;
  }

  getRequiredGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): CultCacheDocumentValue<TDefinition> {
    const value = this.getGlobal(definition);
    if (value === undefined) {
      throw new Error(`CultCache has no global "${definition.type}" document.`);
    }

    return value;
  }

  getGlobalEnvelope<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): CultCacheEnvelope | undefined {
    const registered = this.#requireDefinition(definition);
    if (!registered.global) {
      throw new Error(`CultCache document type "${definition.type}" is not marked as global.`);
    }

    const globalKey = this.#hydrated.globalKeys.get(definition.type);
    return globalKey ? this.getEnvelope(definition, globalKey) : undefined;
  }

  getAll<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): CultCacheDocumentValue<TDefinition>[] {
    this.#requireDefinition(definition);
    const values: CultCacheDocumentValue<TDefinition>[] = [];
    const entryIds = this.#hydrated.typeEntryIds.get(definition.type);

    if (!entryIds) {
      return values;
    }

    for (const entryId of entryIds) {
      const entry = this.#hydrated.entries.get(entryId);
      if (entry) {
        values.push(entry.value as CultCacheDocumentValue<TDefinition>);
      }
    }

    return values;
  }

  getKeyByName<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    name: string,
  ): string | undefined {
    const registered = this.#requireDefinition(definition);
    if (!registered.nameAccessor) {
      return undefined;
    }

    return this.#hydrated.nameToKeyMaps.get(definition.type)?.get(name);
  }

  getIdByName<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    name: string,
  ): string | undefined {
    return this.getKeyByName(definition, name);
  }

  getByName<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    name: string,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    const key = this.getKeyByName(definition, name);
    return key ? this.get(definition, key) : undefined;
  }

  getKeyByIndex<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    indexName: string,
    value: string,
  ): string | undefined {
    this.#requireDefinition(definition);
    return this.#hydrated.indexToKeyMaps.get(this.#indexId(definition.type, indexName))?.get(value);
  }

  getIdByIndex<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    indexName: string,
    value: string,
  ): string | undefined {
    return this.getKeyByIndex(definition, indexName, value);
  }

  getByIndex<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    indexName: string,
    value: string,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    const key = this.getKeyByIndex(definition, indexName, value);
    return key ? this.get(definition, key) : undefined;
  }

  async put<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    const registered = this.#requireDefinition(definition);
    const payload = registered.formatter.encode(value);
    const parsed = registered.formatter.decode(payload) as CultCacheDocumentValue<TDefinition>;
    const entry: CultCacheEnvelope = {
      key,
      type: definition.type,
      payload: this.#cloneBytes(payload),
      storedAt: new Date().toISOString(),
      schemaId: registered.catalogEntry.schemaId,
      catalogEntry: registered.catalogEntry,
    };

    const home = this.#homeStore(definition.type);

    if (registered.global) {
      const existingGlobalKey = this.#hydrated.globalKeys.get(definition.type);
      if (existingGlobalKey && existingGlobalKey !== key) {
        await this.delete(definition, existingGlobalKey);
      }
    }

    await home?.push(entry);
    this.#applyHydratedEntry(this.#hydrated, registered, entry, parsed);
    return parsed;
  }

  async putEnvelope<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    envelope: CultCacheEnvelope,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    const registered = this.#requireDefinition(definition);
    if (envelope.type !== definition.type) {
      throw new Error(
        `CultCache envelope type "${envelope.type}" does not match definition "${definition.type}".`,
      );
    }
    if (!envelope.key || envelope.key.trim().length === 0) {
      throw new Error(`CultCache envelope key for type "${definition.type}" must be non-empty.`);
    }
    if (!envelope.storedAt || envelope.storedAt.trim().length === 0) {
      throw new Error(`CultCache envelope storedAt for type "${definition.type}" must be non-empty.`);
    }

    const payload = this.#cloneBytes(envelope.payload);
    const parsed = registered.formatter.decode(payload) as CultCacheDocumentValue<TDefinition>;
    const entry: CultCacheEnvelope = {
      key: envelope.key,
      type: envelope.type,
      payload,
      storedAt: envelope.storedAt,
      schemaId: envelope.schemaId ?? registered.catalogEntry.schemaId,
      catalogEntry: envelope.catalogEntry ?? registered.catalogEntry,
    };

    const home = this.#homeStore(definition.type);

    if (registered.global) {
      const existingGlobalKey = this.#hydrated.globalKeys.get(definition.type);
      if (existingGlobalKey && existingGlobalKey !== entry.key) {
        await this.delete(definition, existingGlobalKey);
      }
    }

    await home?.push(entry);
    this.#applyHydratedEntry(this.#hydrated, registered, entry, parsed);
    return parsed;
  }

  async putGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    const registered = this.#requireDefinition(definition);
    if (!registered.global) {
      throw new Error(`CultCache document type "${definition.type}" is not marked as global.`);
    }

    return this.put(definition, CultCache.GLOBAL_KEY, value);
  }

  async update<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    updater: (
      current: CultCacheDocumentValue<TDefinition> | undefined,
    ) => CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    const current = this.get(definition, key);
    return this.put(definition, key, updater(current));
  }

  async updateGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    updater: (
      current: CultCacheDocumentValue<TDefinition> | undefined,
    ) => CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    const current = this.getGlobal(definition);
    return this.putGlobal(definition, updater(current));
  }

  async delete<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    const registered = this.#requireDefinition(definition);
    const entry = this.#hydrated.entries.get(this.#entryId(definition.type, key));
    if (!entry) {
      return false;
    }

    const home = this.#homeStore(definition.type);

    const envelope = this.#toEnvelope(entry);
    await home?.delete(envelope);
    this.#removeHydratedEntry(this.#hydrated, registered, entry);
    return true;
  }

  async deleteGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): Promise<boolean> {
    const registered = this.#requireDefinition(definition);
    if (!registered.global) {
      throw new Error(`CultCache document type "${definition.type}" is not marked as global.`);
    }

    const globalKey = this.#hydrated.globalKeys.get(definition.type);
    return globalKey ? this.delete(definition, globalKey) : false;
  }

  snapshot(): CultCacheEnvelope[] {
    return [...this.#hydrated.entries.values()].map((entry) => this.#toEnvelope(entry));
  }

  #createFormatter(definition: AnyCultCacheDocumentDefinition): CultCacheDocumentFormatter<unknown> {
    const userFormatter = definition.formatter ?? {
      encode: (value: unknown) => encode(value),
      decode: (payload: Uint8Array) => decode(payload),
    };

    return {
      encode: (value: unknown) => {
        const parsed = definition.schema.parse(value);
        return this.#cloneBytes(userFormatter.encode(parsed));
      },
      decode: (payload: Uint8Array) => {
        const decoded = userFormatter.decode(this.#cloneBytes(payload));
        return definition.schema.parse(decoded);
      },
    };
  }

  #createCatalogEntry(definition: AnyCultCacheDocumentDefinition): CultCacheSchemaCatalogEntry {
    const schemaName = definition.schemaName ?? definition.type;
    const schemaVersion = definition.schemaVersion ?? `${schemaName}.v1`;
    const canonicalSchemaJson = definition.canonicalSchemaJson ?? JSON.stringify({
      schemaName,
      schemaVersion,
      members: [...(definition.members ?? [])]
        .sort((left, right) => left.slot - right.slot)
        .map((member) => ({
          slot: member.slot,
          name: member.memberName,
          type: member.typeName,
          isReference: member.isReference === true,
          many: member.isMany === true,
          targetSchemaName: member.targetSchemaName ?? null,
          indexAlias: member.indexAlias ?? null,
          isName: member.isName === true,
        })),
    });
    const schemaId = definition.schemaId ?? schemaName;
    const compatibleSchemaIds = [
      ...new Set([schemaId, ...(definition.compatibleSchemaIds ?? [])]),
    ];

    return {
      schemaId,
      schemaName,
      schemaVersion,
      contentHash: definition.contentHash ?? schemaId,
      canonicalSchemaJson,
      compatibleSchemaIds,
      members: [...(definition.members ?? [])]
        .sort((left, right) => left.slot - right.slot)
        .map((member) => ({
          slot: member.slot,
          memberName: member.memberName,
          typeName: member.typeName,
          isReference: member.isReference === true,
          isMany: member.isMany === true,
          targetSchemaName: member.targetSchemaName ?? null,
          isName: member.isName === true,
          indexAlias: member.indexAlias ?? null,
        })),
    };
  }

  #enumerateDefinitionIndexes(
    definition: AnyCultCacheDocumentDefinition,
  ): Array<[string, AccessorInput]> {
    if (!definition.indexes) {
      return [];
    }

    if (Array.isArray(definition.indexes)) {
      return definition.indexes.map((index) => [index.name, index.accessor as AccessorInput]);
    }

    return Object.entries(definition.indexes) as Array<[string, AccessorInput]>;
  }

  #compileAccessor(
    type: string,
    label: string,
    accessor: AccessorInput,
  ): (value: unknown) => string | undefined {
    if (typeof accessor === "function") {
      return (value: unknown) => this.#normalizeIndexValue(type, label, accessor(value));
    }

    return (value: unknown) => {
      if (value === null || typeof value !== "object") {
        return undefined;
      }

      const fieldValue = (value as Record<string, unknown>)[accessor];
      return this.#normalizeIndexValue(type, `${label} field "${accessor}"`, fieldValue);
    };
  }

  #normalizeIndexValue(
    type: string,
    label: string,
    value: unknown,
  ): string | undefined {
    if (value === undefined || value === null) {
      return undefined;
    }

    switch (typeof value) {
      case "string":
        return value;
      case "number":
      case "boolean":
      case "bigint":
        return String(value);
      default:
        throw new Error(`CultCache ${label} accessor for type "${type}" produced a non-scalar value.`);
    }
  }

  #requireDefinition<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): RegisteredDefinition {
    const registered = this.#definitions.get(definition.type);
    if (!registered || registered.definition !== definition) {
      throw new Error(`CultCache document type "${definition.type}" is not registered on this cache instance.`);
    }

    return registered;
  }

  #resolveDefinitionForEnvelope(entry: CultCacheEnvelope): RegisteredDefinition | undefined {
    if (entry.schemaId) {
      return this.#schemaIdDefinitions.get(entry.schemaId)
        ?? this.#schemaNameDefinitions.get(entry.type)
        ?? this.#definitions.get(entry.type);
    }

    return this.#schemaNameDefinitions.get(entry.type) ?? this.#definitions.get(entry.type);
  }

  // The registration that claims the type, else the generic one. Registration guarantees at most one of each.
  #resolveRegistration(type: string): CultCacheStoreRegistration | undefined {
    return this.#stores.find((registration) => registration.types.includes(type))
      ?? this.#stores.find((registration) => registration.types.length === 0);
  }

  // A cache with no stores is in memory: undefined. Once any store is attached, a type with no home is an error.
  #homeStore(type: string): CacheBackingStore | undefined {
    if (this.#stores.length === 0) {
      return undefined;
    }
    const home = this.#resolveRegistration(type);
    if (!home) {
      throw new Error(`No backing store is registered for document type "${type}".`);
    }
    return home.store;
  }

  #describe(registration: CultCacheStoreRegistration | undefined): string {
    if (!registration) {
      return "memory";
    }
    return registration.types.length === 0
      ? "the generic store"
      : `the store routed to ${registration.types.join(", ")}`;
  }

  #applyHydratedEntry(
    state: HydratedState,
    registered: RegisteredDefinition,
    entry: CultCacheEnvelope,
    value: unknown,
  ): void {
    const entryId = this.#entryId(entry.type, entry.key);
    const existing = state.entries.get(entryId);
    if (existing) {
      this.#removeHydratedEntry(state, registered, existing);
    }

    if (registered.global) {
      const currentGlobalKey = state.globalKeys.get(entry.type);
      if (currentGlobalKey !== undefined && currentGlobalKey !== entry.key) {
        throw new Error(
          `CultCache global document type "${entry.type}" has multiple persisted entries: "${currentGlobalKey}" and "${entry.key}".`,
        );
      }
    }

    const hydrated: HydratedEntry = {
      ...entry,
      payload: this.#cloneBytes(entry.payload),
      schemaId: entry.schemaId ?? registered.catalogEntry.schemaId,
      catalogEntry: entry.catalogEntry ?? registered.catalogEntry,
      value,
    };
    state.entries.set(entryId, hydrated);
    getOrCreate(state.typeEntryIds, entry.type, () => new Set<string>()).add(entryId);

    if (registered.global) {
      state.globalKeys.set(entry.type, entry.key);
    }

    if (registered.nameAccessor) {
      const name = registered.nameAccessor(value);
      if (name !== undefined) {
        getOrCreate(state.nameToKeyMaps, entry.type, () => new Map<string, string>()).set(name, entry.key);
      }
    }

    for (const [indexName, accessor] of registered.indexAccessors) {
      const indexValue = accessor(value);
      if (indexValue !== undefined) {
        getOrCreate(state.indexToKeyMaps, this.#indexId(entry.type, indexName), () => new Map<string, string>())
          .set(indexValue, entry.key);
      }
    }
  }

  #removeHydratedEntry(
    state: HydratedState,
    registered: RegisteredDefinition,
    entry: HydratedEntry,
  ): void {
    const entryId = this.#entryId(entry.type, entry.key);
    state.entries.delete(entryId);

    const typeEntryIds = state.typeEntryIds.get(entry.type);
    if (typeEntryIds) {
      typeEntryIds.delete(entryId);
      if (typeEntryIds.size === 0) {
        state.typeEntryIds.delete(entry.type);
      }
    }

    if (registered.global && state.globalKeys.get(entry.type) === entry.key) {
      state.globalKeys.delete(entry.type);
    }

    if (registered.nameAccessor) {
      const name = registered.nameAccessor(entry.value);
      if (name !== undefined) {
        state.nameToKeyMaps.get(entry.type)?.delete(name);
      }
    }

    for (const [indexName, accessor] of registered.indexAccessors) {
      const value = accessor(entry.value);
      if (value !== undefined) {
        state.indexToKeyMaps.get(this.#indexId(entry.type, indexName))?.delete(value);
      }
    }
  }

  #rebuildDefinitionLookups(registered: RegisteredDefinition): void {
    this.#rebuildNameLookup(registered);

    for (const indexName of registered.indexAccessors.keys()) {
      this.#rebuildIndexLookup(registered, indexName);
    }
  }

  // Lookups are built in a local map and assigned only after every accessor has run.
  #rebuildNameLookup(registered: RegisteredDefinition): void {
    const type = registered.definition.type;
    if (!registered.nameAccessor) {
      this.#hydrated.nameToKeyMaps.delete(type);
      return;
    }

    const map = new Map<string, string>();
    for (const entry of this.#entriesForType(type)) {
      const name = registered.nameAccessor(entry.value);
      if (name !== undefined) {
        map.set(name, entry.key);
      }
    }
    this.#hydrated.nameToKeyMaps.set(type, map);
  }

  #rebuildIndexLookup(registered: RegisteredDefinition, indexName: string): void {
    const lookupId = this.#indexId(registered.definition.type, indexName);
    const accessor = registered.indexAccessors.get(indexName);
    if (!accessor) {
      this.#hydrated.indexToKeyMaps.delete(lookupId);
      return;
    }

    const map = new Map<string, string>();
    for (const entry of this.#entriesForType(registered.definition.type)) {
      const value = accessor(entry.value);
      if (value !== undefined) {
        map.set(value, entry.key);
      }
    }
    this.#hydrated.indexToKeyMaps.set(lookupId, map);
  }

  #entriesForType(type: string): HydratedEntry[] {
    const entryIds = this.#hydrated.typeEntryIds.get(type);
    if (!entryIds) {
      return [];
    }

    const entries: HydratedEntry[] = [];
    for (const entryId of entryIds) {
      const entry = this.#hydrated.entries.get(entryId);
      if (entry) {
        entries.push(entry);
      }
    }

    return entries;
  }

  #toEnvelope(entry: HydratedEntry): CultCacheEnvelope {
    return {
      key: entry.key,
      type: entry.type,
      payload: this.#cloneBytes(entry.payload),
      storedAt: entry.storedAt,
      schemaId: entry.schemaId,
      catalogEntry: entry.catalogEntry,
    };
  }

  #entryId(type: string, key: string): string {
    return `${type}::${key}`;
  }

  #indexId(type: string, indexName: string): string {
    return `${type}::${indexName}`;
  }

  #cloneBytes(payload: Uint8Array): Uint8Array {
    return new Uint8Array(payload);
  }
}
