# GameCult.Caching

`GameCult.Caching` provides a typed in-memory cache for `DatabaseEntry` models and a persistence abstraction for backing stores.

## Scope

The library is designed for applications that need:

- fast in-memory lookup by `Guid`
- optional lookup by human-readable name
- optional lookup by indexed fields or properties
- persistence through one or more pluggable stores
- a common model base type for runtime and storage code

It is a domain cache with persistence adapters.

## Main Concepts

### `DatabaseEntry`

All cacheable models inherit from `DatabaseEntry`.

```csharp
using GameCult.Caching;

public class ItemData : DatabaseEntry, INamedEntry
{
    public string Name = string.Empty;
    public string Category = string.Empty;
    public int Value;

    public string EntryName
    {
        get => Name;
        set => Name = value;
    }
}
```

### `INamedEntry`

If a type implements `INamedEntry`, `CultCache` maintains a name-to-id lookup for it. That enables:

- `GetIdByName<T>(...)`
- `GetByName<T>(...)`

### Registered Indexes

Indexes are opt-in. Register an index once for a field or property, and the cache maintains a value-to-id lookup map for that member.

```csharp
var cache = new CultCache();
cache.RegisterIndex<ItemData>("Category");
```

After registration:

```csharp
var consumable = cache.GetByIndex<ItemData>("Category", "Consumable");
```

### Global Entries

If a `DatabaseEntry` type is marked with `GlobalSettingsAttribute`, the cache treats it as a singleton-style global entry and exposes it through `GetGlobal<T>()`.

## Core Features

- ID-based lookup
- optional name-based lookup
- optional field/property indexes
- global singleton-style entries
- pluggable backing stores
- event propagation from backing stores into the cache
- support for both single-file and multi-file persistence strategies

## Basic Usage

```csharp
using GameCult.Caching;

var cache = new CultCache();
cache.RegisterIndex<ItemData>("Category");

await cache.AddAsync(new ItemData
{
    ID = Guid.NewGuid(),
    Name = "Potion",
    Category = "Consumable",
    Value = 50
});

var potion = cache.GetByName<ItemData>("Potion");
var samePotion = cache.GetByIndex<ItemData>("Category", "Consumable");
```

## Important Concept: Backing Store Routing

`CultCache` can have:

- generic backing stores
- type/domain-specific backing stores

### Domain-Specific Stores

If you register a store with one or more domain types:

```csharp
cache.AddBackingStore(playerStore, typeof(PlayerData));
cache.AddBackingStore(settingsStore, typeof(AppSettings));
```

then writes for those types are routed directly to those stores.

### Generic Stores

If you register stores without domains:

```csharp
cache.AddBackingStore(primaryStore);
cache.AddBackingStore(mirrorStore);
```

the first generic store becomes the primary write target for entries that do not have a type-specific store.

The later generic stores subscribe to earlier ones and mirror their changes.

That means:

- generic store registration order matters
- the first generic store is effectively the source of truth for generic writes
- later generic stores behave like downstream replicas

### How Multiple Backing Stores Behave

With this setup:

```csharp
cache.AddBackingStore(primaryStore);
cache.AddBackingStore(reportingMirror);
cache.AddBackingStore(playerStore, typeof(PlayerData));
```

the behavior is:

- `PlayerData` writes go to `playerStore`
- other entry types go to `primaryStore`
- `reportingMirror` mirrors change events from `primaryStore`
- `PullAllBackingStoresAsync()` pulls from all registered stores

This is not a multi-master design. If you need symmetric writes to multiple stores, implement that outside `CultCache`.

## Persistence Semantics

`AddAsync` persists to the appropriate backing store before the cache treats the write as committed. If persistence fails, the call throws instead of updating only the in-memory state.

Effects:

- memory and disk are less likely to diverge silently
- callers can react to persistence failures explicitly

## Backing Store Base Types

### `CacheBackingStore`

Abstract base for persistence implementations. It exposes:

- `PullAll()`
- `Push(DatabaseEntry entry)`
- `Delete(DatabaseEntry entry)`
- `PushAll(bool soft = false)`

### `SingleFileBackingStore`

Stores the entire set of entries in one file.

Use cases:

- small datasets
- settings-style data
- atomic snapshot persistence

### `MultiFileBackingStore`

Stores one file per entry in type-specific directories.

Use cases:

- large sets of independent entries
- change observation with `FileSystemWatcher`
- workflows where per-entry files are useful

## Rename Behavior In `MultiFileBackingStore`

For `INamedEntry` types, the filename is derived from the entry name. The backing store removes the stale old file before committing the new one.

## Example: Custom Backing Store

```csharp
using System.IO;
using System.Text;
using GameCult.Caching;

public sealed class TextItemStore : MultiFileBackingStore
{
    public TextItemStore(string path) : base(path)
    {
    }

    public override byte[] Serialize(DatabaseEntry entry)
    {
        var item = (ItemData)entry;
        return Encoding.UTF8.GetBytes($"{item.ID}|{item.Name}|{item.Category}|{item.Value}");
    }

    public override DatabaseEntry Deserialize(byte[] data)
    {
        var parts = Encoding.UTF8.GetString(data).Split('|');
        return new ItemData
        {
            ID = Guid.Parse(parts[0]),
            Name = parts[1],
            Category = parts[2],
            Value = int.Parse(parts[3])
        };
    }

    public override string Extension => "item";
}
```

## Typical Startup Sequence

```csharp
var cache = new CultCache();
cache.RegisterIndex<PlayerData>("Email");

var store = new TextItemStore("Data");
cache.AddBackingStore(store, typeof(ItemData));

await cache.PullAllBackingStoresAsync();
```
