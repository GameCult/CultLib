import {
  declaresRole,
  decodeDocumentPayloadMarked,
  documentPayloadBytes,
  documentPayloadFromBytes,
  encodeDocumentPayload,
  resolveDocumentMembers,
  schemaIdentityOf,
} from "./document";
import { resolveDocumentFormatter } from "./document-formatter";
import type { NotedSlots } from "./codec";
import type { DocumentPayload } from "./document";
import { SchemaConflictError, schemaIdForEnvelope, writtenOwns } from "./store-format";
import type {
  AnyCultCacheDocumentDefinition,
  CacheBackingStore,
  CultCacheDocumentAccessor,
  CultCacheDocumentDefinition,
  CultCacheDocumentRegistry,
  CultCacheDocumentValue,
  CultCacheEnvelope,
  CultCacheSchemaCatalogEntry,
  CultCacheStoreRegistration,
  CultCacheUnreadSlots,
  CultCacheUpdateOptions,
  CultCacheUpdateResult,
} from "./types";

type Accessor = (value: unknown) => string | undefined;

// A registered definition reads and writes its documents through the owner, on payloads the cache has cloned.
type RegisteredFormatter = {
  encode(value: unknown): DocumentPayload;
  decode(payload: DocumentPayload): { value: unknown; unreadSlots?: NotedSlots };
};

type RegisteredDefinition = {
  readonly definition: AnyCultCacheDocumentDefinition;
  readonly global: boolean;
  readonly formatter: RegisteredFormatter;
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

// What a conditional write requires of the record at its key: the content version its store reported (null: no record), or for a
// cache with no store the entry it held. Every write installs a new entry object, so the identity catches an A-B-A.
type WriteCondition = { readonly version: string | null } | { readonly held: HydratedEntry | undefined };

type HydratedEntry = CultCacheEnvelope & {
  catalogEntry: NonNullable<CultCacheEnvelope["catalogEntry"]>;
  value: unknown;
  lookups: EntryLookups;
  // What the payload decode read past: set when the record was admitted, and it makes the record read-only unless explained.
  unreadSlots?: CultCacheUnreadSlots;
  // The schema id the record arrived under, before it was admitted under its reader's: what an unread-slots refusal names.
  storedSchemaId?: string;
};

// Everything a pull replaces. Built whole, then swapped in with one assignment.
type HydratedState = {
  readonly entries: Map<string, HydratedEntry>;
  readonly typeEntryIds: Map<string, Set<string>>;
  readonly nameToKeyMaps: Map<string, Map<string, string>>;
  readonly indexToKeyMaps: Map<string, Map<string, string>>;
  readonly globalKeys: Map<string, string>;
  // Load compatibility shim: a global persisted under a key other than GLOBAL_KEY is held as that
  // type's global under GLOBAL_KEY, and its persisted envelope is kept here so the first write or
  // delete of that global removes it from the home store. Remove once no store holds a global under
  // a key other than GLOBAL_KEY.
  readonly legacyGlobals: Map<string, CultCacheEnvelope>;
  // The records the last pull found that no registered type claims. Derived from the pull and replaced with the state; it
  // decides nothing but the refusal of a write or a removal at its keys.
  readonly foreign: CultCacheForeignRecord[];
};

/** A record a store carries that no registered type claims: the cache does not serve it, keeps its bytes, and refuses a write or a removal at its key. */
export type CultCacheForeignRecord = {
  readonly key: string;
  readonly schemaId: string;
  readonly schemaName: string;
  readonly storedAt: string;
};

// Lookups for every held record of one type under candidate accessors, derived before anything is installed.
type DerivedTypeLookups = {
  readonly entryLookups: Array<[HydratedEntry, EntryLookups]>;
  readonly nameMap?: Map<string, string>;
  readonly indexMaps: Map<string, Map<string, string>>;
};

// The consent a compatible-id declaration gives (cultlib-gaps:ruling:slot-mark-consent): a mark is explained only when the record
// arrived under an id other than the reader's own that the reader lists compatible, it is at
// depth 0 and the schema comparison of the catalog entry the record arrived with against the reader's own reported the slots it
// carries as ignored, so its slot count is at most the highest ignored slot plus one. A record written or put through the cache
// arrives under the reader's own entry, which ignores nothing, so no mark of it is explained.
function explainedBy(noted: NotedSlots, reader: CultCacheSchemaCatalogEntry, arrivedId: string, arrived: CultCacheSchemaCatalogEntry | undefined): boolean {
  if (arrivedId === reader.schemaId || !(reader.compatibleSchemaIds ?? []).includes(arrivedId)) {
    return false;
  }
  const declared = new Set((reader.members ?? []).map((member) => member.slot));
  const ignored = (arrived?.members ?? []).map((member) => member.slot).filter((slot) => !declared.has(slot));
  return noted.depth0Only && noted.slotCount <= Math.max(-1, ...ignored) + 1;
}

function emptyHydratedState(): HydratedState {
  return {
    entries: new Map(),
    typeEntryIds: new Map(),
    nameToKeyMaps: new Map(),
    indexToKeyMaps: new Map(),
    globalKeys: new Map(),
    legacyGlobals: new Map(),
    foreign: [],
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
  // The definition that owns each schema id, and every definition listing an id as compatible, in registration order.
  readonly #schemaIdDefinitions = new Map<string, RegisteredDefinition>();
  readonly #listingDefinitions = new Map<string, RegisteredDefinition[]>();
  readonly #schemaNameDefinitions = new Map<string, RegisteredDefinition>();
  #hydrated = emptyHydratedState();
  readonly #stores: CultCacheStoreRegistration[] = [];
  #tail: Promise<void> = Promise.resolve();

  static builder(): CultCacheBuilder {
    return new CultCacheBuilder();
  }

  // Attach, pull, registration and every write and delete run one at a time through this chain,
  // so validation, the store call and applying to the cache never interleave with another
  // mutation of this cache. Reads are not queued. Observer delivery is not scheduled here.
  // The chain advances on its own promise; the caller gets the operation's promise, untouched, so
  // an unawaited failure is still an unhandled rejection. A call made from inside an operation
  // queues behind it; a store call that waits for that call waits on itself and never finishes.
  #serial<T>(operation: () => T | Promise<T>): Promise<T> {
    let advance!: () => void;
    const previous = this.#tail;
    this.#tail = new Promise<void>((resolve) => {
      advance = resolve;
    });
    return previous.then(async () => {
      try {
        return await operation();
      } finally {
        advance();
      }
    });
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
      const derived = this.#deriveTypeLookups(definition.type, registered.catalogEntry.schemaName, nameAccessor, registered.indexAccessors);
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

      if (declaresRole(definition, indexName)) {
        throw new Error(`Index "${indexName}" of ${definition.type} is declared as a role; a role is never a unique index.`);
      }

      const registered = this.#requireDefinition(definition);
      const indexAccessors = new Map(registered.indexAccessors);
      indexAccessors.set(indexName, this.#compileAccessor(definition.type, `index "${indexName}"`, accessor));
      const derived = this.#deriveTypeLookups(definition.type, registered.catalogEntry.schemaName, registered.nameAccessor, indexAccessors);
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
    return this.#serial(() => this.#pullNow());
  }

  // The pull itself, for a caller already inside a serial operation.
  async #pullNow(): Promise<void> {
    const next = emptyHydratedState();
    const persistedGlobalKeys = new Map<string, string>();

    for (const registration of this.#stores) {
      const entries = await registration.store.pullAll();

      for (const entry of entries) {
        const registered = this.#resolveDefinitionForEnvelope(entry);
        if (!registered) {
          // A store may hold what this cache has no type for; it is carried, not served, and not a reason to refuse the load.
          next.foreign.push({
            key: entry.key,
            schemaId: schemaIdForEnvelope(entry),
            schemaName: entry.catalogEntry?.schemaName ?? entry.type,
            storedAt: entry.storedAt,
          });
          continue;
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
        const { value, unreadSlots } = registered.formatter.decode(payload);
        let admitted: CultCacheEnvelope = { ...entry, type, payload };
        if (registered.global) {
          const persistedKey = persistedGlobalKeys.get(type);
          if (persistedKey !== undefined) {
            throw new Error(
              `CultCache global document type "${type}" has multiple persisted entries: "${persistedKey}" and "${entry.key}".`,
            );
          }
          persistedGlobalKeys.set(type, entry.key);
          if (entry.key !== CultCache.GLOBAL_KEY) {
            next.legacyGlobals.set(type, admitted);
            admitted = { ...admitted, key: CultCache.GLOBAL_KEY };
          }
        }
        const hydrated = this.#admitEntry(next, registered, admitted, value, unreadSlots);
        const other = this.#heldUnderOtherType(next, type, hydrated.key);
        if (other !== undefined) {
          throw new Error(`Record ${hydrated.key} is held by two stores, as type "${other.type}" and as type "${type}".`);
        }
        this.#installEntry(next, registered, hydrated);
      }
    }

    const foreignByKey = new Map<string, string>();
    for (const record of next.foreign) {
      const first = foreignByKey.get(record.key);
      if (first !== undefined) {
        throw new Error(`Record ${record.key} is held by two stores, as schema "${first}" and as schema "${record.schemaName}".`);
      }
      foreignByKey.set(record.key, record.schemaName);
      const other = [...this.#definitions.keys()].map((type) => next.entries.get(this.#entryId(type, record.key))).find((held) => held !== undefined);
      if (other !== undefined) {
        throw new Error(`Record ${record.key} is held by two stores, as type "${other.type}" and as schema "${record.schemaName}".`);
      }
    }

    // Judged whole, once: the refusal names the least violation of the state this load would leave, whatever the
    // order of the stores and of the entries in them.
    refuseLeastIndexSharing(function* () {
      for (const entry of next.entries.values()) {
        for (const [alias, value] of entry.lookups.indexes) {
          yield { type: entry.type, schemaName: entry.catalogEntry.schemaName, alias, value, key: entry.key };
        }
      }
    }());

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

  // A unique index: one key per non-blank value. A roles alias is never indexed and answers undefined.
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

  /**
   * The blind write: the record at the key becomes `value`, whatever it held, and the last writer wins. `update` is the
   * conditional path, which changes a record from what the store holds now and re-runs on a lost race.
   */
  put<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(async () => (await this.#putNow(definition, key, value)).value);
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

      const payload = this.#cloneBytes(envelope.payload);
      const { value: parsed, unreadSlots } = registered.formatter.decode(payload) as { value: CultCacheDocumentValue<TDefinition>; unreadSlots?: NotedSlots };
      const entry: CultCacheEnvelope = {
        key: envelope.key,
        type: envelope.type,
        payload,
        storedAt: envelope.storedAt,
        // The record is stamped with its registered schema's id, whatever id the envelope arrived under, so the
        // catalog entry the store writes is the one its schema id names.
        schemaId: registered.catalogEntry.schemaId,
        catalogEntry: registered.catalogEntry,
      };
      await this.#writeNow(registered, entry, parsed, unreadSlots);
      return parsed;
    });
  }

  putGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.#serial(async () => {
      this.#requireGlobal(definition);
      return (await this.#putNow(definition, CultCache.GLOBAL_KEY, value)).value;
    });
  }

  /**
   * Changes the record at the key from what the store holds now. `apply` may run up to `maxAttempts` times (default 5): each
   * attempt gets a fresh copy of the record as the store holds it (undefined when absent), which it may change and return, and
   * the result is written only if the record is still that version; a lost race runs `apply` again on the newer record. It must
   * not write this cache or have side effects. Returning undefined writes nothing (`declined`). A lost race that outlasts
   * `maxAttempts` returns `lostRace` and does not throw; a refusal, or a throw from `apply`, rejects and is not retried. The
   * write is durable when the promise resolves. `document` is the cache's record after the outcome. `put` is the blind write.
   */
  async update<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    apply: (
      current: CultCacheDocumentValue<TDefinition> | undefined,
    ) => CultCacheDocumentValue<TDefinition> | undefined | Promise<CultCacheDocumentValue<TDefinition> | undefined>,
    options: CultCacheUpdateOptions = {},
  ): Promise<CultCacheUpdateResult<CultCacheDocumentValue<TDefinition>>> {
    if (typeof apply !== "function") {
      throw new TypeError("CultCache update needs a function to apply.");
    }
    // undefined is the default; anything else must be an integer of at least 1, so null is refused.
    const maxAttempts = options.maxAttempts === undefined ? 5 : options.maxAttempts;
    if (!Number.isInteger(maxAttempts) || maxAttempts < 1) {
      throw new RangeError(`CultCache update maxAttempts must be an integer of at least 1, not ${String(maxAttempts)}.`);
    }

    for (let attempt = 1; ; attempt++) {
      const { expected, copy } = await this.#serial(() => this.#readForUpdate(definition, key));
      // Outside the serial queue and the store's lock: apply may await, and may write this cache through other keys.
      const next = await apply(copy as CultCacheDocumentValue<TDefinition> | undefined);
      if (next === undefined || next === null) {
        return { outcome: "declined", document: this.get(definition, key) };
      }
      const written = await this.#serial(() => this.#putNow(definition, key, next, expected));
      if (written.outcome === "committed") {
        return { outcome: "updated", document: this.get(definition, key) as CultCacheDocumentValue<TDefinition> };
      }
      if (attempt === maxAttempts) {
        // Display only. A cache with no store has nothing to pull, and a pull would replace what it holds with nothing.
        if (this.#stores.length > 0) {
          await this.#serial(() => this.#pullNow());
        }
        return { outcome: "lostRace", document: this.get(definition, key) };
      }
    }
  }

  /** `update` of the type's global record. */
  async updateGlobal<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    apply: (
      current: CultCacheDocumentValue<TDefinition> | undefined,
    ) => CultCacheDocumentValue<TDefinition> | undefined | Promise<CultCacheDocumentValue<TDefinition> | undefined>,
    options: CultCacheUpdateOptions = {},
  ): Promise<CultCacheUpdateResult<CultCacheDocumentValue<TDefinition>>> {
    this.#requireGlobal(definition);
    return this.update(definition, CultCache.GLOBAL_KEY, apply, options);
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

  /** What the record's payload carried past this build's declared slots, or undefined: the held record is read-only unless `explained`. */
  unreadSlots(definition: AnyCultCacheDocumentDefinition, key: string): CultCacheUnreadSlots | undefined {
    return this.#hydrated.entries.get(this.#entryId(this.#requireDefinition(definition).definition.type, key))?.unreadSlots;
  }

  /**
   * Whether a write of the record's own type at the key would be let through: it asks the judge #writeNow asks, of the record the
   * cache holds (a key a store carries foreign, a record of another type the write does not own or cannot replace, a global at
   * another key, a record carrying unread slots nothing consents to). It answers the WRITE form. A removal asks the judge's
   * removal form and differs in two states: an owner's `delete` of a record held in another store than its own home succeeds
   * where this is false (a write would push to the wrong store), and `delete` of a global type at a key other than __global__ is
   * the plain false of an unheld key where this is false. It does not look at store paths: a link at a store path is not predicted, and
   * the write refuses it with `StoreLinkedError`. Live and advisory: the write or removal still decides.
   */
  isWritable(definition: AnyCultCacheDocumentDefinition, key: string): boolean {
    const registered = this.#requireDefinition(definition);
    try {
      this.#judge(registered, key, this.#probe(registered, key));
      return true;
    } catch {
      return false;
    }
  }

  // What the last pull found that no registered type claims, by key ordinal. A later registration claims a record at the next pull.
  foreignRecords(): readonly CultCacheForeignRecord[] {
    return [...this.#hydrated.foreign].sort((left, right) => compareOrdinal(left.key, right.key));
  }

  snapshot(): CultCacheEnvelope[] {
    return [...this.#hydrated.entries.values()].map((entry) => this.#toEnvelope(entry));
  }

  // The image as the stores hold it, for persisting it whole (a flush): a global loaded from a legacy key is handed out under
  // that key until its first write retires it, so a flush cannot leave the store holding it under two keys. `snapshot` is the
  // logical image and always names a global by CultCache.GLOBAL_KEY.
  storedSnapshot(): CultCacheEnvelope[] {
    return [...this.#hydrated.entries.values()].map((entry) => this.#hydrated.legacyGlobals.get(entry.type) ?? this.#toEnvelope(entry));
  }

  #registerNow(definition: AnyCultCacheDocumentDefinition): void {
    // Every registration refusal is a SchemaConflictError naming the new definition's schema id and both schema names.
    const refuse = (holder: RegisteredDefinition, claimed: CultCacheSchemaCatalogEntry, what: string) =>
      new SchemaConflictError(claimed.schemaId, [holder.catalogEntry.schemaName, claimed.schemaName], "",
        `CultCache ${what} is already registered for type "${holder.definition.type}" and cannot also be claimed by type "${definition.type}".`);
    const existing = this.#definitions.get(definition.type);
    if (existing && existing.definition !== definition) {
      throw refuse(existing, this.#createCatalogEntry(definition), `type "${definition.type}"`);
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

    // One definition owns each schema id. Others may list it as compatible beside its owner: a record under the id resolves to
    // its owner, and to a lister only when nothing owns it. Neither depends on the order definitions were registered in.
    const ownId = registered.catalogEntry.schemaId;
    const listed = (registered.catalogEntry.compatibleSchemaIds ?? []).filter((schemaId) => schemaId !== ownId);
    const owner = this.#schemaIdDefinitions.get(ownId);
    if (owner && owner !== registered) {
      throw refuse(owner, registered.catalogEntry, `schema id "${ownId}"`);
    }
    const existingBySchemaName = this.#schemaNameDefinitions.get(registered.catalogEntry.schemaName);
    if (existingBySchemaName && existingBySchemaName !== registered) {
      throw refuse(existingBySchemaName, registered.catalogEntry, `schema name "${registered.catalogEntry.schemaName}"`);
    }
    const derived = this.#deriveTypeLookups(definition.type, registered.catalogEntry.schemaName, nameAccessor, indexAccessors);

    // Every check and accessor has run; install.
    this.#definitions.set(definition.type, registered);
    this.#schemaIdDefinitions.set(ownId, registered);
    for (const schemaId of listed) {
      const listers = this.#listingDefinitions.get(schemaId) ?? [];
      if (!listers.includes(registered)) {
        this.#listingDefinitions.set(schemaId, [...listers, registered]);
      }
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

  // One attempt's read for update: the record at the key as its home holds it now, the condition a commit must meet, and a copy of
  // the record for apply to change. A store-backed cache asks its store, which reports the record and its version from one read;
  // a cache with no store has only the held entry, whose identity is the condition and whose admitted payload is the copy.
  async #readForUpdate(
    definition: AnyCultCacheDocumentDefinition,
    key: string,
  ): Promise<{ expected: WriteCondition; copy: unknown }> {
    const registered = this.#requireDefinition(definition);
    this.#requireGlobalKey(registered, key);
    const type = definition.type;
    this.#refuseLegacyConditional(type);
    const home = this.#homeStore(type);
    if (home === undefined) {
      const held = this.#hydrated.entries.get(this.#entryId(type, key));
      if (held === undefined && this.#heldUnderOtherType(this.#hydrated, type, key) !== undefined) {
        throw new Error(`Record ${key} is not a "${type}"; update cannot copy it.`);
      }
      return { expected: { held }, copy: held ? registered.formatter.decode(this.#cloneBytes(held.payload)).value : undefined };
    }
    if (home.read === undefined || home.commitIf === undefined) {
      throw new Error(`CultCache backing store for type "${type}" cannot write conditionally; update needs a store with read and commitIf.`);
    }
    const found = await home.read(key);
    if (found === undefined) {
      return { expected: { version: null }, copy: undefined };
    }
    if (this.#resolveDefinitionForEnvelope(found.envelope) !== registered) {
      throw new Error(`Record ${key} is not a "${type}"; update cannot copy it.`);
    }
    return { expected: { version: found.version }, copy: registered.formatter.decode(this.#cloneBytes(found.envelope.payload)).value };
  }

  #putNow<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<{ outcome: "committed"; value: CultCacheDocumentValue<TDefinition> }>;
  #putNow<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
    expected: WriteCondition,
  ): Promise<{ outcome: "committed"; value: CultCacheDocumentValue<TDefinition> } | { outcome: "mismatch" }>;
  async #putNow<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
    expected?: WriteCondition,
  ): Promise<{ outcome: "committed"; value: CultCacheDocumentValue<TDefinition> } | { outcome: "mismatch" }> {
    const registered = this.#requireDefinition(definition);
    const payload = registered.formatter.encode(value);
    const { value: parsed, unreadSlots } = registered.formatter.decode(payload) as { value: CultCacheDocumentValue<TDefinition>; unreadSlots?: NotedSlots };
    const entry: CultCacheEnvelope = {
      key,
      type: definition.type,
      payload: this.#cloneBytes(payload),
      storedAt: new Date().toISOString(),
      schemaId: registered.catalogEntry.schemaId,
      catalogEntry: registered.catalogEntry,
    };
    const outcome = await this.#writeNow(registered, entry, parsed, unreadSlots, expected);
    return outcome === "committed" ? { outcome, value: parsed } : { outcome };
  }

  // Validates the whole write (home, global singleton, name and index accessors) before the store
  // is touched; once the store accepts, applying to the cache has no fallible step.
  // With a condition, the write lands only if the record at the key is still the one the caller read: the store's version
  // (commitIf), or for a cache with no store the held entry itself. Otherwise nothing is written or installed and the answer is
  // "mismatch". Without one the write is blind, as put's.
  async #writeNow(
    registered: RegisteredDefinition,
    entry: CultCacheEnvelope,
    value: unknown,
    unreadSlots?: NotedSlots,
    expected?: WriteCondition,
  ): Promise<"committed" | "mismatch"> {
    const home = this.#homeStore(entry.type);
    const retyped = this.#judge(registered, entry.key, entry);
    const hydrated = this.#admitEntry(this.#hydrated, registered, entry, value, unreadSlots);
    this.#refuseIndexSharing(this.#hydrated, entry.type, registered.catalogEntry.schemaName, entry.key, hydrated.lookups);
    const legacy = this.#hydrated.legacyGlobals.get(entry.type);
    // A conditional write over a legacy global is refused here too (a pull while apply awaited can have loaded one): the condition
    // was read from the global's own key, the record sits under its legacy key, and the retire below would delete it unseen.
    if (expected !== undefined) {
      this.#refuseLegacyConditional(entry.type);
    }
    if (expected !== undefined && "held" in expected) {
      // The condition was fixed at read for a cache with no store. A store attached since has its own record at the key that
      // this read never saw, so the held identity no longer judges the write: lose the race and read again.
      if (home !== undefined || this.#hydrated.entries.get(this.#entryId(entry.type, entry.key)) !== expected.held) {
        return "mismatch";
      }
    }
    if (expected !== undefined && "version" in expected) {
      if (home?.commitIf === undefined) {
        throw new Error(`CultCache backing store for type "${entry.type}" cannot write conditionally; update needs a store with read and commitIf.`);
      }
      const committed = await home.commitIf(entry, expected.version);
      if (committed.outcome === "mismatch") {
        return "mismatch";
      }
      hydrated.storedAt = committed.storedAt;
    } else {
      // A store that re-stamps a write (storedAt later than the record it replaces) reports the stamp; the cache holds that one.
      const pushed = await home?.push(entry);
      if (typeof pushed === "object" && pushed !== null && typeof pushed.storedAt === "string" && !Number.isNaN(Date.parse(pushed.storedAt))) {
        hydrated.storedAt = pushed.storedAt;
      }
    }
    if (home && legacy) {
      await this.#retireLegacyGlobal(home, legacy, entry);
    }
    this.#hydrated.legacyGlobals.delete(entry.type);
    if (retyped !== undefined) {
      // The record the write restamped (it owns the held id) is the same record: one key, one held type.
      this.#removeEntry(this.#hydrated, this.#definitions.get(retyped.type)!, retyped);
    }
    this.#installEntry(this.#hydrated, registered, hydrated);
    return "committed";
  }

  // The one rule that a conditional write (update, whichever entry reaches it) never touches a global loaded from a legacy key:
  // only putGlobal retires that key. Asked before apply runs and again at the write.
  #refuseLegacyConditional(type: string): void {
    if (this.#hydrated.legacyGlobals.has(type)) {
      throw new Error(
        `CultCache global "${type}" was loaded from a legacy key; write it once with putGlobal to retire its legacy key.`,
      );
    }
  }

  // An envelope of the definition's type at the key, carrying nothing: what the judge asks about a write or a removal by that type.
  #probe(registered: RegisteredDefinition, key: string): CultCacheEnvelope {
    return {
      key, type: registered.definition.type, payload: documentPayloadFromBytes(new Uint8Array(0)), storedAt: "",
      schemaId: registered.catalogEntry.schemaId, catalogEntry: registered.catalogEntry,
    };
  }

  // The one judge of whether a key may be written (a written envelope) or removed (none), before any store is touched: it throws the
  // refusal, and #writeNow, #deleteNow and isWritable all ask it, so the query cannot disagree with the write. For a write it
  // returns the held record of another type that the write owns and so restamps, if any. A refusal names keys and schemas,
  // never a payload or a slot value.
  #judge(registered: RegisteredDefinition, key: string, written?: CultCacheEnvelope): HydratedEntry | undefined {
    const type = registered.definition.type;
    const held = this.#hydrated.entries.get(this.#entryId(type, key));
    if (written === undefined) {
      // A removal is by key: the record it removes is the one held at the key, of this type or of another type whose schema id
      // this type owns (the record a write of this type would replace). A held record of a type it does not own is refused.
      this.#refuseForeignKey(key, type, "removed");
      this.#refuseUnread(held);
      if (held !== undefined) {
        return undefined;
      }
      const other = this.#heldUnderOtherType(this.#hydrated, type, key);
      if (other === undefined) {
        return undefined;
      }
      if (!writtenOwns(this.#probe(registered, key), schemaIdForEnvelope(other))) {
        throw new SchemaConflictError(
          registered.catalogEntry.schemaId,
          [other.type, type],
          key,
          `CultCache holds record "${key}" as type "${other.type}", so type "${type}" cannot remove it.`,
        );
      }
      this.#refuseUnread(other);
      return other;
    }
    this.#requireGlobalKey(registered, key);
    const retyped = this.#refuseRetype(written);
    this.#refuseUnread(held);
    this.#refuseUnread(retyped);
    if (retyped !== undefined && this.#homeStore(retyped.type) !== this.#homeStore(type)) {
      // The write owns the held record's id and would replace it, but the held record is in another store; the push
      // reaches only this type's home, so the old record would stay in its own file and load as a second holder.
      throw new SchemaConflictError(
        schemaIdForEnvelope(written),
        [retyped.type, type],
        key,
        `CultCache holds record "${key}" as type "${retyped.type}" in another store than the home of type "${type}", so the rename cannot replace it; remove the record first.`,
      );
    }
    return retyped;
  }

  // The one refusal of a record held read-only for the slots its payload carries past this build's: a write or a removal of
  // its key. It names the type and the counts, never a slot value. A mark that is explained (see #admitEntry) is consented to.
  #unreadRefusal(held: HydratedEntry | undefined): SchemaConflictError | undefined {
    const unread = held?.unreadSlots;
    if (held === undefined || unread === undefined || unread.explained) {
      return undefined;
    }
    return new SchemaConflictError(
      held.storedSchemaId ?? held.schemaId ?? held.type,
      [held.catalogEntry.schemaName],
      held.key,
      `${held.catalogEntry.schemaName} record ${held.key} is read-only: ${unread.typeName} was read with ${unread.slotCount} slots where this build declares ${unread.declaredCount} ` +
        `(${unread.occurrences} object(s) in the record). Writing it would drop data a newer writer stored; read it with types that declare those slots, or write it from that build.`,
      unread,
    );
  }

  #refuseUnread(held: HydratedEntry | undefined): void {
    const refusal = this.#unreadRefusal(held);
    if (refusal !== undefined) {
      throw refusal;
    }
  }

  // The one key-identity judge of the cache: may this write land at its key, given what the cache holds there under
  // every registered type and the keys its stores carry foreign. The store judges the file with the same predicate
  // (writtenOwns); this judges the cache, which a store-less cache has no other judge for. Returns the held record of
  // another type the write owns and so replaces, if any. A refusal names the key and schemas, never a payload.
  #refuseRetype(entry: CultCacheEnvelope): HydratedEntry | undefined {
    this.#refuseForeignKey(entry.key, entry.type, "written");
    const held = this.#heldUnderOtherType(this.#hydrated, entry.type, entry.key);
    if (held === undefined || writtenOwns(entry, schemaIdForEnvelope(held))) {
      return held;
    }
    throw new SchemaConflictError(
      schemaIdForEnvelope(entry),
      [held.type, entry.type],
      entry.key,
      `CultCache holds record "${entry.key}" as type "${held.type}", so type "${entry.type}" cannot be written under the same key; remove the record first.`,
    );
  }

  #refuseForeignKey(key: string, type: string, what: "written" | "removed"): void {
    const foreign = this.#hydrated.foreign.find((record) => record.key === key);
    if (foreign !== undefined) {
      throw new SchemaConflictError(
        foreign.schemaId,
        [foreign.schemaName],
        key,
        `CultCache store carries record "${key}" as schema "${foreign.schemaName}" (${foreign.schemaId}), which no registered type claims, so type "${type}" cannot be ${what} at that key.`,
      );
    }
  }

  // The record another registered type holds at the key, if any.
  #heldUnderOtherType(state: HydratedState, type: string, key: string): HydratedEntry | undefined {
    for (const other of this.#definitions.keys()) {
      const held = other === type ? undefined : state.entries.get(this.#entryId(other, key));
      if (held !== undefined) {
        return held;
      }
    }
    return undefined;
  }

  // The first write of a global loaded from a legacy key removes that record in the same write. If the
  // removal fails, the new record is taken back out, so the store keeps only the legacy record.
  async #retireLegacyGlobal(home: CacheBackingStore, legacy: CultCacheEnvelope, written: CultCacheEnvelope): Promise<void> {
    try {
      await home.delete(legacy);
    } catch (error) {
      await home.delete(written).catch(() => undefined);
      throw error;
    }
  }

  async #deleteNow<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    const registered = this.#requireDefinition(definition);
    // variants:ruling:unheld-removal-refused-at-commit: a key a store carries foreign is refused, a key held by nobody is the
    // existing false; a key held as another type is removed by an owner of its id and refused to any other type.
    // Removal is by key, in one store call: the record held at the key of this type, else the one of another type this type owns.
    const owned = this.#judge(registered, key);
    const entry = this.#hydrated.entries.get(this.#entryId(definition.type, key)) ?? owned;
    if (!entry) {
      return false;
    }

    // The cache drops the record only once the store has removed it.
    const home = this.#homeStore(entry.type);
    await home?.delete(this.#hydrated.legacyGlobals.get(entry.type) ?? this.#toEnvelope(entry));
    this.#hydrated.legacyGlobals.delete(entry.type);
    this.#removeEntry(this.#hydrated, this.#definitions.get(entry.type)!, entry);
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

  #createFormatter(definition: AnyCultCacheDocumentDefinition): RegisteredFormatter {
    // A definition that declares both a codec and a formatter is refused here, where it is registered.
    resolveDocumentFormatter(definition);

    return {
      encode: (value: unknown) => this.#cloneBytes(encodeDocumentPayload(definition, value)),
      decode: (payload: DocumentPayload) => decodeDocumentPayloadMarked(definition, this.#cloneBytes(payload)),
    };
  }

  #createCatalogEntry(definition: AnyCultCacheDocumentDefinition): CultCacheSchemaCatalogEntry {
    const { schemaId, schemaName, schemaVersion, compatibleSchemaIds } = schemaIdentityOf(definition);
    const canonicalSchemaJson = definition.canonicalSchemaJson ?? JSON.stringify({
      schemaName,
      schemaVersion,
      members: [...resolveDocumentMembers(definition)]
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

    return {
      schemaId,
      schemaName,
      schemaVersion,
      contentHash: definition.contentHash ?? schemaId,
      canonicalSchemaJson,
      compatibleSchemaIds: [...compatibleSchemaIds],
      members: [...resolveDocumentMembers(definition)]
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
        return isBlankIndexText(value) ? undefined : value;
      case "boolean":
        return value ? "True" : "False";
      case "bigint":
        return value.toString();
      case "number":
        return canonicalNumberText(value);
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
      const listers = this.#listingDefinitions.get(entry.schemaId) ?? [];
      const owner = this.#schemaIdDefinitions.get(entry.schemaId);
      if (!owner && listers.length > 1) {
        const types = listers.map((lister) => lister.definition.type).sort();
        throw new SchemaConflictError(entry.schemaId, listers.map((lister) => lister.catalogEntry.schemaName).sort(), entry.key,
          `CultCache schema id "${entry.schemaId}" of record "${entry.key}" is owned by no registered type and declared compatible by ` +
          `several (${types.join(", ")}), so it resolves to none. Register the type that owns the id, or declare it on one type.`);
      }
      return owner
        ?? listers[0]
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
    noted?: NotedSlots,
  ): HydratedEntry {
    if (registered.global) {
      const currentGlobalKey = state.globalKeys.get(entry.type);
      if (currentGlobalKey !== undefined && currentGlobalKey !== entry.key) {
        throw new Error(
          `CultCache global document type "${entry.type}" has multiple persisted entries: "${currentGlobalKey}" and "${entry.key}".`,
        );
      }
    }

    const lookups = this.#lookupsFor(registered.nameAccessor, registered.indexAccessors, value);
    return {
      ...entry,
      payload: this.#cloneBytes(entry.payload),
      // Admitted under the registered schema's id, as a put stamps it: a record loaded under a compatible id is
      // written back under the id its catalog entry carries.
      schemaId: registered.catalogEntry.schemaId,
      catalogEntry: registered.catalogEntry,
      value,
      lookups,
      storedSchemaId: entry.schemaId ?? entry.type,
      ...(noted ? { unreadSlots: { ...noted, explained: explainedBy(noted, registered.catalogEntry, entry.schemaId ?? entry.type, entry.catalogEntry) } } : {}),
    };
  }

  // The one index rule (variants:ruling:unique-indexes): within one type and one unique alias, no two records hold one
  // non-blank value. The held key at the landing record's own key is its previous self and keeps its value. The message
  // names both keys, ordinal, and the alias, never the value.
  #refuseIndexSharing(state: HydratedState, type: string, schemaName: string, key: string, lookups: EntryLookups): void {
    for (const alias of [...lookups.indexes.keys()].sort(compareOrdinal)) {
      const held = state.indexToKeyMaps.get(this.#indexId(type, alias))?.get(lookups.indexes.get(alias)!);
      if (held !== undefined && held !== key) {
        throw indexSharingRefusal(held, key, schemaName, alias);
      }
    }
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
    schemaName: string,
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
        const map = indexMaps.get(indexName)!;
        if (!map.has(indexValue)) map.set(indexValue, entry.key);
      }
    }

    // Judged whole, by the same judge as a load: the least violation of the state the registration would leave.
    refuseLeastIndexSharing(function* () {
      for (const [entry, lookups] of entryLookups) {
        for (const [alias, value] of lookups.indexes) yield { type, schemaName, alias, value, key: entry.key };
      }
    }());

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

  #cloneBytes(payload: DocumentPayload): DocumentPayload {
    return documentPayloadFromBytes(new Uint8Array(documentPayloadBytes(payload)));
  }
}

function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

// The whole-state judge (variants:question:unique-index-refusal-pair-rule): over every (type, alias, value) held at two
// or more distinct keys, throws for the one least by (alias, least key, second-least key, schema name), each compared by
// UTF-16 code unit. A function of the state, never of the order the rows arrive in.
function refuseLeastIndexSharing(rows: Iterable<{ type: string; schemaName: string; alias: string; value: string; key: string }>): void {
  const groups = new Map<string, { schemaName: string; alias: string; keys: string[] }>();
  for (const { type, schemaName, alias, value, key } of rows) {
    const id = JSON.stringify([type, alias, value]);
    const group = groups.get(id);
    if (group) group.keys.push(key);
    else groups.set(id, { schemaName, alias, keys: [key] });
  }
  let least: { schemaName: string; alias: string; first: string; second: string } | undefined;
  for (const { schemaName, alias, keys } of groups.values()) {
    if (keys.length < 2) continue;
    keys.sort(compareOrdinal);
    const candidate = { schemaName, alias, first: keys[0], second: keys[1] };
    if (!least || compareLeast(candidate, least) < 0) least = candidate;
  }
  if (least) throw indexSharingRefusal(least.first, least.second, least.schemaName, least.alias);
}

function compareLeast(
  a: { schemaName: string; alias: string; first: string; second: string },
  b: { schemaName: string; alias: string; first: string; second: string },
): number {
  return compareOrdinal(a.alias, b.alias) || compareOrdinal(a.first, b.first) || compareOrdinal(a.second, b.second) || compareOrdinal(a.schemaName, b.schemaName);
}

function indexSharingRefusal(keyA: string, keyB: string, schemaName: string, alias: string): Error {
  const [first, second] = compareOrdinal(keyA, keyB) <= 0 ? [keyA, keyB] : [keyB, keyA];
  return new Error(`Records ${first} and ${second} (${schemaName}) both hold unique index '${alias}'; an index value names one record.`);
}

// Blank is no value: empty or only C# White_Space characters. Not JavaScript's \s, which counts U+FEFF and omits U+0085.
const BLANK_INDEX_TEXT = /^[\t-\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$/;

function isBlankIndexText(value: string): boolean {
  return BLANK_INDEX_TEXT.test(value);
}

// The canonical text of a number, as C# RenderCanonicalNumber: integers as their digits (-0 is 0), a non-integral finite
// double as its exact decimal expansion, NaN and infinities as no value.
function canonicalNumberText(value: number): string | undefined {
  if (!Number.isFinite(value)) {
    return undefined;
  }
  if (Number.isInteger(value)) {
    return BigInt(value).toString();
  }

  // value = mantissa * 2^exponent with exponent < 0, so value = mantissa * 5^k / 10^k for k = -exponent.
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, value);
  const bits = view.getBigUint64(0);
  const biasedExponent = Number((bits >> 52n) & 0x7ffn);
  const fraction = bits & 0xfffffffffffffn;
  const mantissa = biasedExponent === 0 ? fraction : fraction | (1n << 52n);
  const k = -((biasedExponent === 0 ? 1 : biasedExponent) - 1075);
  const digits = (mantissa * 5n ** BigInt(k)).toString().padStart(k + 1, "0");
  const text = `${digits.slice(0, digits.length - k)}.${digits.slice(digits.length - k)}`.replace(/0+$/, "").replace(/\.$/, "");
  return value < 0 ? `-${text}` : text;
}
