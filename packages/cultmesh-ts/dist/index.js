"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.CultMesh = exports.CultMeshAuthorityLeaseCatalog = exports.CultMeshPeerCatalog = exports.CultMeshVerseCatalog = exports.CultMeshNode = void 0;
const cultcache_ts_1 = require("cultcache-ts");
const cultnet_ts_1 = require("cultnet-ts");
class CultMeshNode {
    cache;
    store;
    documents;
    constructor(cache, store, documents) {
        this.cache = cache;
        this.store = store;
        this.documents = documents;
    }
    get(definition, key) {
        return this.cache.get(definition, key);
    }
    getRequired(definition, key) {
        return this.cache.getRequired(definition, key);
    }
    put(definition, key, value) {
        return this.cache.put(definition, key, value);
    }
    delete(definition, key) {
        return this.cache.delete(definition, key);
    }
    async flush(soft = false) {
        await this.store.pushAll?.(this.cache.snapshot(), { soft });
    }
}
exports.CultMeshNode = CultMeshNode;
class CultMeshVerseCatalog {
    #verses = new Map();
    get verses() {
        return [...this.#verses.values()];
    }
    upsert(verseId, verse) {
        requireNonEmpty(verseId, "verseId");
        this.#verses.set(verseId, verse);
    }
    get(verseId) {
        requireNonEmpty(verseId, "verseId");
        return this.#verses.get(verseId);
    }
}
exports.CultMeshVerseCatalog = CultMeshVerseCatalog;
class CultMeshPeerCatalog {
    #peers = new Map();
    get peers() {
        return [...this.#peers.values()].sort((left, right) => left.peerId.localeCompare(right.peerId));
    }
    upsert(peer) {
        requireNonEmpty(peer.peerId, "peer.peerId");
        requireNonEmpty(peer.verseId, "peer.verseId");
        this.#peers.set(peer.peerId, peer);
    }
    find(verseId, role) {
        requireNonEmpty(verseId, "verseId");
        return this.peers.filter((peer) => peer.verseId === verseId &&
            (!role || peer.roles?.includes(role) === true));
    }
}
exports.CultMeshPeerCatalog = CultMeshPeerCatalog;
class CultMeshAuthorityLeaseCatalog {
    #leases = new Map();
    upsert(lease) {
        requireNonEmpty(lease.leaseId, "lease.leaseId");
        requireNonEmpty(lease.verseId, "lease.verseId");
        requireNonEmpty(lease.peerId, "lease.peerId");
        if (lease.expiresAt <= lease.validFrom) {
            throw new Error("CultMesh authority lease expiry must be after validFrom.");
        }
        this.#leases.set(lease.leaseId, lease);
    }
    isAuthorized(peer, role, shardId, at = new Date()) {
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
exports.CultMeshAuthorityLeaseCatalog = CultMeshAuthorityLeaseCatalog;
class CultMesh {
    static async createNode(cachePath, options = {}) {
        requireNonEmpty(cachePath, "cachePath");
        const store = options.store ?? new cultcache_ts_1.SingleFileMessagePackBackingStore(cachePath);
        const cache = cultcache_ts_1.CultCache.builder().withGenericStore(store).build();
        const documents = new cultnet_ts_1.CultNetDocumentRegistry(options.bindings);
        for (const definition of options.documents ?? []) {
            cache.registerDocumentType(definition);
            if (!documents.get(definition.type)) {
                documents.register((0, cultnet_ts_1.defineCultNetDocumentBinding)({ definition }));
            }
        }
        if (options.pullOnStart !== false) {
            await cache.pullAllBackingStores();
        }
        return new CultMeshNode(cache, store, documents);
    }
    static startNode(cachePath, options = {}) {
        return CultMesh.createNode(cachePath, options);
    }
    static createVerseCatalog() {
        return new CultMeshVerseCatalog();
    }
    static createPeerCatalog() {
        return new CultMeshPeerCatalog();
    }
    static createAuthorityLeaseCatalog() {
        return new CultMeshAuthorityLeaseCatalog();
    }
}
exports.CultMesh = CultMesh;
function requireNonEmpty(value, name) {
    if (!value || value.trim().length === 0) {
        throw new Error(`${name} must be non-empty.`);
    }
}
