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

type Accessor = (value: unknown) => string | undefined;

type RegisteredDefinition = {
  readonly definition: AnyCultCacheDocumentDefinition;
  readonly global: boolean;
  readonly formatter: CultCacheDocumentFormatter<unknown>;
  readonly catalogEntry: CultCacheSchemaCatalogEntry;
  nameAccessor?: Accessor;
  indexAccessors: ReadonlyMap<string, Accessor>;
};

// The name and index values a record produced when it was admitted. Removing the record
// reads these and runs no accessor, so removal has no fallible step.
type EntryLookups = {
  readonly name?: string;
  readonly indexes: ReadonlyMap<string, string>;
};

type HydratedEntry = CultCacheEnvelope & {
  value: unknown;
  lookups: EntryLookups;
};

// Everything a pull replaces. Built whole, then swapped in with one assignment.
type HydratedState = {
  readonly entries: Map<string, HydratedEntry>;
  readonly typeEntryIds: Map<string, Set<string>>;
  readonly nameToKeyMaps: Map<string, Map<string, string>>;
  readonly indexToKeyMaps: Map<string, Map<string, string>>;
  readonly globalKeys: Map<string, string>;
};

// Lookups for every held record of one type under candidate accessors, derived before anything is installed.
type DerivedTypeLookups = {
  readonly entryLookups: Array<[HydratedEntry, EntryLookups]>;
  readonly nameMap?: Map<string, string>;
  readonly indexMaps: Map<string, Map<string, string>>;
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

// Assigned in CultCache's static block. The builder's cache is unreachable until build(), so no
// queued operation can be in flight and registration and attach may run synchronously.
let registerNow!: (cache: CultCache, definition: AnyCultCacheDocumentDefinition) => void;
let attachNow!: (cache: CultCache, store: CacheBackingStore, types: BackingStoreTypeReference[]) => void;

export class CultCacheBuilder {
  readonly #cache = new CultCache();
  #built = false;

  withDocumentType<TDefinition extends AnyCultCacheDocumentDefinition>(definition: TDefinition): this {
    registerNow(this.#unbuilt(), definition);
    return this;
  }

  withRegistry(
    registry:
      | CultCacheDocumentRegistry
      | Iterable<AnyCultCacheDocumentDefinition>,
  ): this {
    const cache = this.#unbuilt();
    for (const definition of registryDefinitions(registry)) {
      registerNow(cache, definition);
    }
    return this;
  }

  withBackingStore(
    store: CacheBackingStore,
    ...types: BackingStoreTypeReference[]
  ): this {
    attachNow(this.#unbuilt(), store, types);
    return this;
  }

  withGenericStore(store: CacheBackingStore): this {
    attachNow(this.#unbuilt(), store, []);
    return this;
  }

  build(): CultCache {
    this.#built = true;
    return this.#cache;
  }

  #unbuilt(): CultCache {
    if (this.#built) {
      throw new Error("CultCacheBuilder has already built its cache; configure the cache through its own methods.");
    }
    return this.#cache;
  }
}

function registryDefinitions(
  registry: CultCacheDocumentRegistry | Iterable<AnyCultCacheDocumentDefinition>,
): Iterable<AnyCultCacheDocumentDefinition> {
  return Symbol.iterator in Object(registry) && !("definitions" in Object(registry))
    ? registry as Iterable<AnyCultCacheDocumentDefinition>
    : (registry as CultCacheDocumentRegistry).definitions;
}

export class CultCache {
  static readonly GLOBAL_KEY = "__global__";

  static {
    registerNow = (cache, definition) => cache.#registerNow(definition);
    attachNow = (cache, store, types) => cache.#attachNow(store, types);
  }

  readonly #definitions = new Map<string, RegisteredDefinition>();
  readonly #schemaIdDefinitions = new Map<string, RegisteredDefinition>();
  readonly #schemaNameDefinitions = new Map<string, RegisteredDefinition>();
  #hydrated = emptyHydratedState();
  readonly #stores: CultCacheStoreRegistration[] = [];
  #tail: Promise<unknown> = Promise.resolve();

  static builder(): CultCacheBuilder {
    return new CultCacheBuilder();
  }

  // Attach, pull, registration and every write and delete run one at a time through this chain,
  // so validation, the store call and applying to the cache never interleave with another
  // mutation of this cache. Reads are not queued. Observer delivery is not scheduled here.
  #serial<T>(operation: () => T | Promise<T>): Promise<T> {
    const run = this.#tail.then(operation);
    this.#tail = run.catch(() => undefined);
    return run;
  }

  registerDocumentType<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): Promise<TDefinition> {
    return this.#serial(() => {
      this.#registerNow(definition);
      return definition;
    });
  }

  registerRegistry(
    registry:
      | CultCacheDocumentRegistry
      | Iterable<AnyCultCacheDocumentDefinition>,
  ): Promise<void> {
    return this.#serial(() => {
      for (const definition of registryDefinitions(registry)) {
        this.#registerNow(definition);
      }
    });
  }

  registerNameLookup<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    accessor: CultCacheDocumentAccessor<CultCacheDocumentValue<TDefinition>>,
  ): Promise<TDefinition> {
    return this.#serial(() => {
      const registered = this.#requireDefinition(definition);
      const nameAccessor = this.#compileAccessor(definition.type, "name", accessor);
      const derived = this.#deriveTypeLookups(definition.type, nameAccessor, registered.indexAccessors);
      this.#installAccessors(registered, nameAccessor, registered.indexAccessors, derived);
      return definition;
    });
  }

  registerIndex<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    indexName: string,
    accessor: CultCacheDocumentAccessor<CultCacheDocumentValue<TDefinition>>,
  ): Promise<TDefinition> {
    return this.#serial(() => {
      if (!indexName || indexName.trim().length === 0) {
        throw new Error(`CultCache index names for type "${definition.type}" must be non-empty.`);
      }

      const registered = this.#requireDefinition(definition);
      const indexAccessors = new Map(registered.indexAccessors);
      indexAccessors.set(indexName, this.#compileAccessor(definition.type, `index "${indexName}"`, accessor));
      const derived = this.#deriveTypeLookups(definition.type, registered.nameAccessor, indexAccessors);
      this.#installAccessors(registered, registered.nameAccessor, indexAccessors, derived);
      return definition;
    });
  }

  addBackingStore(
    store: CacheBackingStore,
    ...types: BackingStoreTypeReference[]
  ): Promise<void> {
    return this.#serial(() => this.#attachNow(store, types));
  }

  addGenericBackingStore(store: CacheBackingStore): Promise<void> {
    return this.addBackingStore(store);
  }

  // The complete next state (entries, globals, name and index lookups) is built into a fresh
  // HydratedState, running every check and every user accessor, and replaces the cache's state
  // in one assignment only once the build finishes. A refused load admits nothing.
  pullAllBackingStores(): Promise<void> {
    return this.#serial(async () => {
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
          const hydrated = this.#admitEntry(next, registered, { ...entry, type, payload }, value);
          this.#installEntry(next, registered, hydrated);
        }
      }

      this.#hydrated = next;
    });
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
    this.#requireGlobal(definition);
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
    this.#requireGlobal(definition);
    const globalKey = this.#hydrated.globalKeys.get(definition.type);
    return globalKey ? this.getEnvelope(definition, globalKey) : undefined;
  }

  getAll<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): CultCacheDocumentValue<TDefinition>[] {
    this.#requireDefinition(definition);
    return this.#entriesForType(definition.type).map((entry) => entry.value as CultCacheDocumentValue<TDefinition>);
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

  put<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(() => this.#putNow(definition, key, value));
  }

  putEnvelope<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    envelope: CultCacheEnvelope,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(async () => {
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
      this.#requireGlobalKey(registered, envelope.key);

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
      await this.#writeNow(registered, entry, parsed);
      return parsed;
    });
  }

  putGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(() => {
      this.#requireGlobal(definition);
      return this.#putNow(definition, CultCache.GLOBAL_KEY, value);
    });
  }

  update<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    updater: (
      current: CultCacheDocumentValue<TDefinition> | undefined,
    ) => CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(() => this.#putNow(definition, key, updater(this.get(definition, key))));
  }

  updateGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    updater: (
      current: CultCacheDocumentValue<TDefinition> | undefined,
    ) => CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(() =>
      this.#putNow(definition, CultCache.GLOBAL_KEY, updater(this.getGlobal(definition))),
    );
  }

  delete<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    return this.#serial(() => this.#deleteNow(definition, key));
  }

  deleteGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
  ): Promise<boolean> {
    return this.#serial(async () => {
      this.#requireGlobal(definition);
      const globalKey = this.#hydrated.globalKeys.get(definition.type);
      return globalKey ? this.#deleteNow(definition, globalKey) : false;
    });
  }

  snapshot(): CultCacheEnvelope[] {
    return [...this.#hydrated.entries.values()].map((entry) => this.#toEnvelope(entry));
  }

  #registerNow(definition: AnyCultCacheDocumentDefinition): void {
    const existing = this.#definitions.get(definition.type);
    if (existing && existing.definition !== definition) {
      throw new Error(`CultCache already has a different definition registered for type "${definition.type}".`);
    }

    const registered: RegisteredDefinition = existing ?? {
      definition,
      global: definition.global === true,
      formatter: this.#createFormatter(definition),
      catalogEntry: this.#createCatalogEntry(definition),
      nameAccessor: undefined,
      indexAccessors: new Map<string, Accessor>(),
    };

    const nameAccessor = definition.name
      ? this.#compileAccessor(definition.type, "name", definition.name)
      : undefined;
    const indexAccessors = new Map<string, Accessor>();
    for (const [indexName, accessor] of this.#enumerateDefinitionIndexes(definition)) {
      indexAccessors.set(indexName, this.#compileAccessor(definition.type, `index "${indexName}"`, accessor));
    }

    const schemaIds = registered.catalogEntry.compatibleSchemaIds ?? [registered.catalogEntry.schemaId];
    for (const schemaId of schemaIds) {
      const existingBySchema = this.#schemaIdDefinitions.get(schemaId);
      if (existingBySchema && existingBySchema !== registered) {
        throw new Error(`CultCache schema id "${schemaId}" is already registered for type "${existingBySchema.definition.type}".`);
      }
    }
    const existingBySchemaName = this.#schemaNameDefinitions.get(registered.catalogEntry.schemaName);
    if (existingBySchemaName && existingBySchemaName !== registered) {
      throw new Error(
        `CultCache schema name "${registered.catalogEntry.schemaName}" is already registered for type "${existingBySchemaName.definition.type}".`,
      );
    }
    const derived = this.#deriveTypeLookups(definition.type, nameAccessor, indexAccessors);

    // Every check and accessor has run; install.
    this.#definitions.set(definition.type, registered);
    for (const schemaId of schemaIds) {
      this.#schemaIdDefinitions.set(schemaId, registered);
    }
    this.#schemaNameDefinitions.set(registered.catalogEntry.schemaName, registered);
    this.#installAccessors(registered, nameAccessor, indexAccessors, derived);
  }

  #attachNow(store: CacheBackingStore, types: BackingStoreTypeReference[]): void {
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

  async #putNow<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    const registered = this.#requireDefinition(definition);
    this.#requireGlobalKey(registered, key);
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
    await this.#writeNow(registered, entry, parsed);
    return parsed;
  }

  // Validates the whole write (home, global singleton, name and index accessors) before the store
  // is touched; once the store accepts, applying to the cache has no fallible step.
  async #writeNow(registered: RegisteredDefinition, entry: CultCacheEnvelope, value: unknown): Promise<void> {
    const home = this.#homeStore(entry.type);
    const hydrated = this.#admitEntry(this.#hydrated, registered, entry, value);
    await home?.push(entry);
    this.#installEntry(this.#hydrated, registered, hydrated);
  }

  async #deleteNow<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    const registered = this.#requireDefinition(definition);
    const entry = this.#hydrated.entries.get(this.#entryId(definition.type, key));
    if (!entry) {
      return false;
    }

    const home = this.#homeStore(definition.type);
    await home?.delete(this.#toEnvelope(entry));
    this.#removeEntry(this.#hydrated, registered, entry);
    return true;
  }

  #requireGlobal(definition: AnyCultCacheDocumentDefinition): RegisteredDefinition {
    const registered = this.#requireDefinition(definition);
    if (!registered.global) {
      throw new Error(`CultCache document type "${definition.type}" is not marked as global.`);
    }
    return registered;
  }

  // A global is only ever stored under GLOBAL_KEY; any other key is refused before a store is touched.
  #requireGlobalKey(registered: RegisteredDefinition, key: string): void {
    if (registered.global && key !== CultCache.GLOBAL_KEY) {
      throw new Error(
        `CultCache global document type "${registered.definition.type}" must use key "${CultCache.GLOBAL_KEY}", not "${key}".`,
      );
    }
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
  ): Accessor {
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

  #lookupsFor(
    nameAccessor: Accessor | undefined,
    indexAccessors: ReadonlyMap<string, Accessor>,
    value: unknown,
  ): EntryLookups {
    const name = nameAccessor?.(value);
    const indexes = new Map<string, string>();
    for (const [indexName, accessor] of indexAccessors) {
      const indexValue = accessor(value);
      if (indexValue !== undefined) {
        indexes.set(indexName, indexValue);
      }
    }
    return { name, indexes };
  }

  // Fallible half of admission: the global singleton check and every user accessor. Touches no state.
  #admitEntry(
    state: HydratedState,
    registered: RegisteredDefinition,
    entry: CultCacheEnvelope,
    value: unknown,
  ): HydratedEntry {
    if (registered.global) {
      const currentGlobalKey = state.globalKeys.get(entry.type);
      if (currentGlobalKey !== undefined && currentGlobalKey !== entry.key) {
        throw new Error(
          `CultCache global document type "${entry.type}" has multiple persisted entries: "${currentGlobalKey}" and "${entry.key}".`,
        );
      }
    }

    return {
      ...entry,
      payload: this.#cloneBytes(entry.payload),
      schemaId: entry.schemaId ?? registered.catalogEntry.schemaId,
      catalogEntry: entry.catalogEntry ?? registered.catalogEntry,
      value,
      lookups: this.#lookupsFor(registered.nameAccessor, registered.indexAccessors, value),
    };
  }

  // Infallible half of admission: replaces any held record at the same key and installs the admitted one.
  #installEntry(state: HydratedState, registered: RegisteredDefinition, hydrated: HydratedEntry): void {
    const entryId = this.#entryId(hydrated.type, hydrated.key);
    const existing = state.entries.get(entryId);
    if (existing) {
      this.#removeEntry(state, registered, existing);
    }

    state.entries.set(entryId, hydrated);
    getOrCreate(state.typeEntryIds, hydrated.type, () => new Set<string>()).add(entryId);
    if (registered.global) {
      state.globalKeys.set(hydrated.type, hydrated.key);
    }
    if (hydrated.lookups.name !== undefined) {
      getOrCreate(state.nameToKeyMaps, hydrated.type, () => new Map<string, string>()).set(hydrated.lookups.name, hydrated.key);
    }
    for (const [indexName, indexValue] of hydrated.lookups.indexes) {
      getOrCreate(state.indexToKeyMaps, this.#indexId(hydrated.type, indexName), () => new Map<string, string>())
        .set(indexValue, hydrated.key);
    }
  }

  // Reads the lookups stored at admission; runs no accessor.
  #removeEntry(
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

    if (entry.lookups.name !== undefined) {
      const names = state.nameToKeyMaps.get(entry.type);
      if (names?.get(entry.lookups.name) === entry.key) {
        names.delete(entry.lookups.name);
      }
    }
    for (const [indexName, indexValue] of entry.lookups.indexes) {
      const index = state.indexToKeyMaps.get(this.#indexId(entry.type, indexName));
      if (index?.get(indexValue) === entry.key) {
        index.delete(indexValue);
      }
    }
  }

  // Runs the candidate accessors over every held record of the type into local maps.
  #deriveTypeLookups(
    type: string,
    nameAccessor: Accessor | undefined,
    indexAccessors: ReadonlyMap<string, Accessor>,
  ): DerivedTypeLookups {
    const entryLookups: Array<[HydratedEntry, EntryLookups]> = [];
    const nameMap = nameAccessor ? new Map<string, string>() : undefined;
    const indexMaps = new Map<string, Map<string, string>>();
    for (const indexName of indexAccessors.keys()) {
      indexMaps.set(indexName, new Map());
    }

    for (const entry of this.#entriesForType(type)) {
      const lookups = this.#lookupsFor(nameAccessor, indexAccessors, entry.value);
      entryLookups.push([entry, lookups]);
      if (lookups.name !== undefined) {
        nameMap?.set(lookups.name, entry.key);
      }
      for (const [indexName, indexValue] of lookups.indexes) {
        indexMaps.get(indexName)?.set(indexValue, entry.key);
      }
    }

    return { entryLookups, nameMap, indexMaps };
  }

  // Installs accessors and their derived lookups together. Nothing here can throw.
  #installAccessors(
    registered: RegisteredDefinition,
    nameAccessor: Accessor | undefined,
    indexAccessors: ReadonlyMap<string, Accessor>,
    derived: DerivedTypeLookups,
  ): void {
    const type = registered.definition.type;
    for (const indexName of registered.indexAccessors.keys()) {
      this.#hydrated.indexToKeyMaps.delete(this.#indexId(type, indexName));
    }

    registered.nameAccessor = nameAccessor;
    registered.indexAccessors = indexAccessors;
    for (const [entry, lookups] of derived.entryLookups) {
      entry.lookups = lookups;
    }
    if (derived.nameMap) {
      this.#hydrated.nameToKeyMaps.set(type, derived.nameMap);
    } else {
      this.#hydrated.nameToKeyMaps.delete(type);
    }
    for (const [indexName, map] of derived.indexMaps) {
      this.#hydrated.indexToKeyMaps.set(this.#indexId(type, indexName), map);
    }
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
