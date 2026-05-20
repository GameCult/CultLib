# GameCult.Caching

`GameCult.Caching` is an attribute-driven typed cache with a canonical
single-file MessagePack store. Domain models stay clean. The cache owns record
keys, schema ids, timestamps, and persistence metadata instead of smearing
that sludge across every class.

## Authoring Model

Cacheable models are plain classes annotated with cache intent.

```csharp
using GameCult.Caching;
using MessagePack;

[CultDocument("gamecult.item_data", "gamecult.item_data.v1")]
public sealed class ItemData
{
    [Key(0)]
    [CultName]
    public string Name = string.Empty;

    [Key(1)]
    [CultIndex]
    public string Category = string.Empty;

    [Key(2)]
    public int Value;
}
```

Available cache attributes:

- `CultDocument(schemaName, schemaVersion)`
- `CultName`
- `CultIndex(alias?)`
- `CultGlobal`
- `CultReference(targetType?, many: false)`

If you want compile-time document metadata instead of runtime assembly
spelunking, add the `GameCult.Caching.MessagePack.Generator` analyzer to the
project that declares your `[CultDocument]` types. It emits a metadata provider
the cache registry can load directly, while reflection stays behind as fallback
for projects that have not taken the hint yet.

## Main Concepts

### Record keys and handles

`CultCache` gives each stored document a cache-owned `CultRecordKey`. You work
with typed handles instead of forcing storage identity into the domain object.

```csharp
var cache = new CultCache();
var handle = await cache.AddAsync(new ItemData
{
    Name = "Potion",
    Category = "Consumable",
    Value = 50
});

var potion = cache.Get<ItemData>(handle.Key);
```

### Name and index lookups

Name and index lookups are driven from attributes, not manual registration.

```csharp
var byName = cache.GetByName<ItemData>("Potion");
var byIndex = cache.GetByIndex<ItemData>("Category", "Consumable");
```

### Explicit references

References stay explicit in the domain. The cache will resolve them on demand,
but it will not quietly build an entire haunted object graph behind your back.

```csharp
[CultDocument("gamecult.quest_data", "gamecult.quest_data.v1")]
public sealed class QuestData
{
    [Key(0)] public string Title = string.Empty;
    [Key(1)] [CultReference] public CultRecordRef<ItemData> Reward;
}
```

## Persistence Shape

CultCache persistence is store-shaped, not entry-shaped:

1. store header
2. embedded schema catalog
3. records

Each persisted record carries:

- `key`
- `schemaId`
- `storedAt`
- `payload`

The domain payload stays free of storage metadata.

## Backing Store

CultCache now has one sanctioned persistence format:

- `SingleFileMessagePackBackingStore`

It persists the whole store as:

1. store format version
2. embedded schema catalog
3. persisted records

Each persisted record carries:

- `key`
- `schemaId`
- `storedAt`
- `payload`

## Happy Path

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;

var cache = await CultCacheMessagePack.OpenAsync("Data.msgpack");

var handle = await cache.UpsertAsync(new ItemData
{
    Name = "Potion",
    Category = "Consumable",
    Value = 50
});

await cache.FlushAsync();
```

If you want the explicit assembly rite, the lower-level `CultCache` +
`SingleFileMessagePackBackingStore` path is still there. The helper is just the
blessed front door.

## Persistence discipline

Persistence stays manual by default. Mutations mark the cache and attached
backing stores dirty; nothing silently flushes every object write just because
that felt convenient in the moment.

Useful surfaces:

- `CultCacheMessagePack.OpenAsync(path)`
- `cache.IsDirty`
- `store.IsDirty`
- `cache.UpsertAsync(document)`
- `cache.FlushAllBackingStores()`
- `cache.FlushAsync()`
- `cache.FlushBackingStore(store)`
- `cache.PrepareForReloadOrShutdown()`
- `cache.PrepareForReloadOrShutdownAsync()`
- `cache.FlushAttachedStoresOnDispose`
- `store.FlushOnDispose`

This is built for callers like Aquarium that want durable runtime state while
still choosing *when* to pay the write cost.

Single-file MessagePack remains a single-writer format. The atomic replace path
protects against partial writes, not competing writers trampling each other.

## Schema migration diagnostics

CultCache does not quietly fall back from exact `schemaId` resolution to
"close enough, probably." When a persisted schema drifts, the cache now emits a
typed migration report describing:

- exact vs compatible-drift resolution
- ignored extra slots
- defaulted missing slots
- warning codes/messages

Compatibility rules and canonical fixture receipts live in
`Contracts/cultcache-schema-compatibility.md`.
