import {
  CultCache,
  SingleFileMessagePackBackingStore,
  type AnyCultCacheDocumentDefinition,
  type CacheBackingStore,
  type CultCacheDocumentValue,
} from "cultcache-ts";
import {
  CultNetDocumentRegistry,
  defineCultNetDocumentBinding,
  type CultNetDocumentBinding,
} from "cultnet-ts";

export interface CultMeshNodeOptions {
  documents?: Iterable<AnyCultCacheDocumentDefinition>;
  bindings?: Iterable<CultNetDocumentBinding>;
  store?: CacheBackingStore;
  pullOnStart?: boolean;
}

export class CultMeshNode {
  readonly cache: CultCache;
  readonly store: CacheBackingStore;
  readonly documents: CultNetDocumentRegistry;

  constructor(
    cache: CultCache,
    store: CacheBackingStore,
    documents: CultNetDocumentRegistry,
  ) {
    this.cache = cache;
    this.store = store;
    this.documents = documents;
  }

  get<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    return this.cache.get(definition, key);
  }

  getRequired<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> {
    return this.cache.getRequired(definition, key);
  }

  put<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.cache.put(definition, key, value);
  }

  delete<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    return this.cache.delete(definition, key);
  }

  async flush(soft = false): Promise<void> {
    await this.store.pushAll?.(this.cache.snapshot(), { soft });
  }
}

export class CultMeshVerseCatalog<TVerse = unknown> {
  readonly #verses = new Map<string, TVerse>();

  get verses(): readonly TVerse[] {
    return [...this.#verses.values()];
  }

  upsert(verseId: string, verse: TVerse): void {
    requireNonEmpty(verseId, "verseId");
    this.#verses.set(verseId, verse);
  }

  get(verseId: string): TVerse | undefined {
    requireNonEmpty(verseId, "verseId");
    return this.#verses.get(verseId);
  }
}

export interface CultMeshPeerCard {
  peerId: string;
  verseId: string;
  endpoints: readonly string[];
  roles?: readonly string[];
  shardIds?: readonly string[];
  authorityLeaseId?: string;
}

export class CultMeshPeerCatalog {
  readonly #peers = new Map<string, CultMeshPeerCard>();

  get peers(): readonly CultMeshPeerCard[] {
    return [...this.#peers.values()].sort((left, right) =>
      left.peerId.localeCompare(right.peerId),
    );
  }

  upsert(peer: CultMeshPeerCard): void {
    requireNonEmpty(peer.peerId, "peer.peerId");
    requireNonEmpty(peer.verseId, "peer.verseId");
    this.#peers.set(peer.peerId, peer);
  }

  find(verseId: string, role?: string): readonly CultMeshPeerCard[] {
    requireNonEmpty(verseId, "verseId");
    return this.peers.filter((peer) =>
      peer.verseId === verseId &&
      (!role || peer.roles?.includes(role) === true),
    );
  }
}

export interface CultMeshAuthorityLease {
  leaseId: string;
  verseId: string;
  peerId: string;
  roles: readonly string[];
  shardIds?: readonly string[];
  validFrom: Date;
  expiresAt: Date;
}

export class CultMeshAuthorityLeaseCatalog {
  readonly #leases = new Map<string, CultMeshAuthorityLease>();

  upsert(lease: CultMeshAuthorityLease): void {
    requireNonEmpty(lease.leaseId, "lease.leaseId");
    requireNonEmpty(lease.verseId, "lease.verseId");
    requireNonEmpty(lease.peerId, "lease.peerId");
    if (lease.expiresAt <= lease.validFrom) {
      throw new Error("CultMesh authority lease expiry must be after validFrom.");
    }

    this.#leases.set(lease.leaseId, lease);
  }

  isAuthorized(
    peer: CultMeshPeerCard,
    role: string,
    shardId?: string,
    at = new Date(),
  ): boolean {
    requireNonEmpty(role, "role");
    if (!peer.authorityLeaseId) {
      return false;
    }

    const lease = this.#leases.get(peer.authorityLeaseId);
    if (!lease) {
      return false;
    }

    return at >= lease.validFrom &&
      at < lease.expiresAt &&
      lease.verseId === peer.verseId &&
      lease.peerId === peer.peerId &&
      lease.roles.includes(role) &&
      peer.roles?.includes(role) === true &&
      (!shardId || !lease.shardIds || lease.shardIds.length === 0 || lease.shardIds.includes(shardId));
  }
}

export class CultMesh {
  static async createNode(
    cachePath: string,
    options: CultMeshNodeOptions = {},
  ): Promise<CultMeshNode> {
    requireNonEmpty(cachePath, "cachePath");

    const store = options.store ?? new SingleFileMessagePackBackingStore(cachePath);
    const cache = CultCache.builder().withGenericStore(store).build();
    const documents = new CultNetDocumentRegistry(options.bindings);

    for (const definition of options.documents ?? []) {
      cache.registerDocumentType(definition);
      if (!documents.get(definition.type)) {
        documents.register(defineCultNetDocumentBinding({ definition }));
      }
    }

    if (options.pullOnStart !== false) {
      await cache.pullAllBackingStores();
    }

    return new CultMeshNode(cache, store, documents);
  }

  static startNode(
    cachePath: string,
    options: CultMeshNodeOptions = {},
  ): Promise<CultMeshNode> {
    return CultMesh.createNode(cachePath, options);
  }

  static createVerseCatalog<TVerse = unknown>(): CultMeshVerseCatalog<TVerse> {
    return new CultMeshVerseCatalog<TVerse>();
  }

  static createPeerCatalog(): CultMeshPeerCatalog {
    return new CultMeshPeerCatalog();
  }

  static createAuthorityLeaseCatalog(): CultMeshAuthorityLeaseCatalog {
    return new CultMeshAuthorityLeaseCatalog();
  }
}

function requireNonEmpty(value: string, name: string): void {
  if (!value || value.trim().length === 0) {
    throw new Error(`${name} must be non-empty.`);
  }
}
