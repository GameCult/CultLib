import type {
  CultNetShardCatalogRequestMessage,
  CultNetShardCatalogResponseMessage,
  CultNetShardDescriptor,
} from "./contracts";

export interface CultNetShardCatalogOptions {
  schemaIds?: readonly string[];
  recordKeys?: readonly string[];
}

export class CultNetShardCatalog {
  readonly #shards = new Map<string, CultNetShardDescriptor>();
  readonly #subscribers = new Set<(descriptor: CultNetShardDescriptor) => void>();

  get shards(): CultNetShardDescriptor[] {
    return Array.from(this.#shards.values())
      .sort((left, right) => left.shardId.localeCompare(right.shardId))
      .map(cloneShardDescriptor);
  }

  watch(callback: (descriptor: CultNetShardDescriptor) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  upsert(descriptor: CultNetShardDescriptor): CultNetShardDescriptor {
    if (!descriptor.shardId) {
      throw new Error("CultNet shard descriptor requires shardId.");
    }
    if (!descriptor.ownerRuntimeId) {
      throw new Error("CultNet shard descriptor requires ownerRuntimeId.");
    }

    const stored = cloneShardDescriptor(descriptor);
    this.#shards.set(stored.shardId, stored);
    for (const subscriber of Array.from(this.#subscribers)) {
      subscriber(cloneShardDescriptor(stored));
    }
    return cloneShardDescriptor(stored);
  }

  get(shardId: string): CultNetShardDescriptor | undefined {
    return this.#shards.has(shardId) ? cloneShardDescriptor(this.#shards.get(shardId)!) : undefined;
  }

  list(options: CultNetShardCatalogOptions = {}): CultNetShardDescriptor[] {
    const requestedSchemaIds = options.schemaIds ?? [];
    const requestedRecordKeys = options.recordKeys ?? [];

    return this.shards.filter((shard) => {
      if (requestedSchemaIds.length > 0 && !schemaIdsMatchAny(shard.schemaIds ?? [], requestedSchemaIds)) {
        return false;
      }
      if (requestedRecordKeys.length > 0 && !requestedRecordKeys.some((recordKey) => shardServes(shard, { recordKey }))) {
        return false;
      }
      return true;
    });
  }

  createCatalogResponse(
    request: CultNetShardCatalogRequestMessage,
  ): CultNetShardCatalogResponseMessage {
    return {
      schemaVersion: "cultnet.shard_catalog_response.v0",
      messageId: request.messageId,
      shards: this.list({
        schemaIds: request.schemaIds,
        recordKeys: request.recordKeys,
      }),
    };
  }

  applyResponse(response: CultNetShardCatalogResponseMessage): CultNetShardDescriptor[] {
    if (response.schemaVersion !== "cultnet.shard_catalog_response.v0") {
      throw new Error(`Expected cultnet.shard_catalog_response.v0, received ${response.schemaVersion}.`);
    }
    return response.shards.map((descriptor) => this.upsert(descriptor));
  }
}

export function shardServes(
  shard: CultNetShardDescriptor,
  options: { schemaId?: string; recordKey?: string } = {},
): boolean {
  if (
    options.schemaId !== undefined &&
    shard.schemaIds !== undefined &&
    shard.schemaIds.length > 0 &&
    !schemaIdsMatchAny(shard.schemaIds, [options.schemaId])
  ) {
    return false;
  }
  if (options.recordKey !== undefined && shard.keyPrefix !== undefined && !options.recordKey.startsWith(shard.keyPrefix)) {
    return false;
  }
  return true;
}

function schemaIdsMatchAny(
  advertisedSchemaIds: readonly string[],
  requestedSchemaIds: readonly string[],
): boolean {
  return advertisedSchemaIds.some(advertised =>
    requestedSchemaIds.some(requested => schemaIdsMatch(advertised, requested)),
  );
}

function schemaIdsMatch(left: string, right: string): boolean {
  if (left === right) {
    return true;
  }

  const leftName = inferSchemaName(left) ?? left;
  const rightName = inferSchemaName(right) ?? right;
  return leftName === rightName;
}

function inferSchemaName(schemaId: string): string | undefined {
  const marker = schemaId.lastIndexOf(".v");
  if (marker <= 0 || marker + 2 >= schemaId.length) {
    return undefined;
  }

  const version = schemaId.slice(marker + 2);
  return /^\d+$/u.test(version) ? schemaId.slice(0, marker) : undefined;
}

function cloneShardDescriptor(descriptor: CultNetShardDescriptor): CultNetShardDescriptor {
  return {
    shardId: descriptor.shardId,
    ownerRuntimeId: descriptor.ownerRuntimeId,
    epoch: descriptor.epoch,
    ...(descriptor.isPrimary !== undefined ? { isPrimary: descriptor.isPrimary } : {}),
    ...(descriptor.schemaIds !== undefined ? { schemaIds: [...descriptor.schemaIds] } : {}),
    ...(descriptor.keyPrefix !== undefined ? { keyPrefix: descriptor.keyPrefix } : {}),
    ...(descriptor.primaryEndpoints !== undefined ? { primaryEndpoints: [...descriptor.primaryEndpoints] } : {}),
    ...(descriptor.replicaEndpoints !== undefined ? { replicaEndpoints: [...descriptor.replicaEndpoints] } : {}),
    ...(descriptor.readReplicaEndpoints !== undefined ? { readReplicaEndpoints: [...descriptor.readReplicaEndpoints] } : {}),
    ...(descriptor.region !== undefined ? { region: descriptor.region } : {}),
    ...(descriptor.authorityLeaseId !== undefined ? { authorityLeaseId: descriptor.authorityLeaseId } : {}),
  };
}
