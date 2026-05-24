import { CultCache, type AnyCultCacheDocumentDefinition, type CacheBackingStore, type CultCacheDocumentValue } from "cultcache-ts";
import { CultNetDocumentRegistry, type CultNetDocumentBinding } from "cultnet-ts";
export interface CultMeshNodeOptions {
    documents?: Iterable<AnyCultCacheDocumentDefinition>;
    bindings?: Iterable<CultNetDocumentBinding>;
    store?: CacheBackingStore;
    pullOnStart?: boolean;
}
export declare class CultMeshNode {
    readonly cache: CultCache;
    readonly store: CacheBackingStore;
    readonly documents: CultNetDocumentRegistry;
    constructor(cache: CultCache, store: CacheBackingStore, documents: CultNetDocumentRegistry);
    get<TDefinition extends AnyCultCacheDocumentDefinition>(definition: TDefinition, key: string): CultCacheDocumentValue<TDefinition> | undefined;
    getRequired<TDefinition extends AnyCultCacheDocumentDefinition>(definition: TDefinition, key: string): CultCacheDocumentValue<TDefinition>;
    put<TDefinition extends AnyCultCacheDocumentDefinition>(definition: TDefinition, key: string, value: CultCacheDocumentValue<TDefinition>): Promise<CultCacheDocumentValue<TDefinition>>;
    delete<TDefinition extends AnyCultCacheDocumentDefinition>(definition: TDefinition, key: string): Promise<boolean>;
    flush(soft?: boolean): Promise<void>;
}
export declare class CultMeshVerseCatalog<TVerse = unknown> {
    #private;
    get verses(): readonly TVerse[];
    upsert(verseId: string, verse: TVerse): void;
    get(verseId: string): TVerse | undefined;
}
export interface CultMeshPeerCard {
    peerId: string;
    verseId: string;
    endpoints: readonly string[];
    roles?: readonly string[];
    shardIds?: readonly string[];
    authorityLeaseId?: string;
}
export declare class CultMeshPeerCatalog {
    #private;
    get peers(): readonly CultMeshPeerCard[];
    upsert(peer: CultMeshPeerCard): void;
    find(verseId: string, role?: string): readonly CultMeshPeerCard[];
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
export declare class CultMeshAuthorityLeaseCatalog {
    #private;
    upsert(lease: CultMeshAuthorityLease): void;
    isAuthorized(peer: CultMeshPeerCard, role: string, shardId?: string, at?: Date): boolean;
}
export declare class CultMesh {
    static createNode(cachePath: string, options?: CultMeshNodeOptions): Promise<CultMeshNode>;
    static startNode(cachePath: string, options?: CultMeshNodeOptions): Promise<CultMeshNode>;
    static createVerseCatalog<TVerse = unknown>(): CultMeshVerseCatalog<TVerse>;
    static createPeerCatalog(): CultMeshPeerCatalog;
    static createAuthorityLeaseCatalog(): CultMeshAuthorityLeaseCatalog;
}
//# sourceMappingURL=index.d.ts.map