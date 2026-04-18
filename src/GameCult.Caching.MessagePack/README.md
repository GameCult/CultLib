# GameCult.Caching.MessagePack

`GameCult.Caching.MessagePack` provides MessagePack-backed persistence for `GameCult.Caching`.

## Scope

The package focuses on:

- MessagePack storage for `DatabaseEntry` models
- single-file and multi-file store implementations
- support for generated formatters for concrete cache-entry types
- formatter support for `DatabaseLink<T>`

## Included Types

- `SingleFileMessagePackBackingStore`
- `MultiFileMessagePackBackingStore`
- `DatabaseEntryResolver`
- `DatabaseLinkFormatter<T>`

## Why The Generator Matters

`DatabaseEntry` is abstract. Concrete subclasses need MessagePack formatters to be serializable without hand-writing formatters for each model.

That is the role of:

- `GameCult.Caching.MessagePack.Generator`
- `GameCult.Caching.MessagePack.Analyzers`

In practice:

- this package gives you the stores and resolver support
- the generator package produces the actual concrete formatters

## Typical Usage

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;

var cache = new CultCache();
var store = new MultiFileMessagePackBackingStore("Data");

cache.AddBackingStore(store);
await cache.PullAllBackingStoresAsync();
```

## Single-File vs Multi-File

### `SingleFileMessagePackBackingStore`

Use this when you want one snapshot file containing all entries.

Use cases:

- compact settings data
- simple local persistence
- write-all-at-once workflows

```csharp
var store = new SingleFileMessagePackBackingStore("cache.msgpack");
cache.AddBackingStore(store);
```

### `MultiFileMessagePackBackingStore`

Use this when you want one file per entry.

Use cases:

- large datasets
- debugging persisted entries individually
- change observation through the multi-file base store

```csharp
var store = new MultiFileMessagePackBackingStore("Data");
cache.AddBackingStore(store, typeof(PlayerData));
```

## `DatabaseLink<T>`

`DatabaseLink<T>` is a lightweight reference type used by the cache layer. The MessagePack formatter serializes only the linked `Guid`, not the fully loaded target object.

That means:

- links stay compact on disk
- linked objects are resolved through the active `CultCache`
- deserialized links depend on `DatabaseLinkBase.Cache` pointing at the active cache

## Example Model

```csharp
using GameCult.Caching;
using MessagePack;

[MessagePackObject]
public class ItemData : DatabaseEntry, INamedEntry
{
    [Key(1)] public string Name = string.Empty;
    [Key(2)] public int Value;

    [IgnoreMember]
    public string EntryName
    {
        get => Name;
        set => Name = value;
    }
}
```

## Practical Notes

- For polymorphic `DatabaseEntry` serialization, use this package with the generator/analyzer package.
- The backing stores build on the semantics of `CultCache`, including primary-store routing and type-specific store routing.
- `MultiFileMessagePackBackingStore` inherits the rename-safe and atomic-write behavior of the hardened `MultiFileBackingStore` base class.
