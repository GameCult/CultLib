# GameCult.Caching

`GameCult.Caching` is an attribute-driven typed cache with pluggable stores.
Domain models stay clean. The cache owns record keys, schema ids, timestamps,
and persistence metadata instead of smearing that sludge across every class.

## Authoring Model

Cacheable models are plain classes annotated with cache intent.

```csharp
using GameCult.Caching;
using MessagePack;

[CultDocument("gamecult.item_data", "gamecult.item_data.v1")]
[MessagePackObject]
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
[MessagePackObject]
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

## Backing Stores

Base abstractions:

- `CacheBackingStore`
- `SingleFileBackingStore`
- `MultiFileBackingStore`

Included implementations in sibling packages:

- `SingleFileMessagePackBackingStore`
- `MultiFileMessagePackBackingStore`
- `SingleFileNewtonsoftJsonBackingStore`
- `MultiFileNewtonsoftJsonBackingStore`

`MultiFileBackingStore` derives filenames from the `CultName` member when one
exists, so renames replace stale files instead of leaving little corpses
behind.

## Typical Startup

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;

var cache = new CultCache();
var store = new MultiFileMessagePackBackingStore("Data");

cache.AddBackingStore(store);
await cache.PullAllBackingStoresAsync();
```
