# GameCult.Caching.MessagePack

`GameCult.Caching.MessagePack` provides MessagePack persistence for the
attribute-first `GameCult.Caching` stack.

## Included Types

- `SingleFileMessagePackBackingStore`
- `MultiFileMessagePackBackingStore`
- `CultDocumentMessagePackSerialization`
- `CultDocumentResolver`
- `CultRecordRefFormatter<T>`

## What It Does

- stores CultCache snapshots and records in MessagePack
- serializes plain attributed document payloads
- keeps explicit `CultRecordRef<T>` values compact on disk/wire
- preserves the cache/store split where metadata lives outside the domain model

## Example Model

```csharp
using GameCult.Caching;
using MessagePack;

[CultDocument("gamecult.item_data", "gamecult.item_data.v1")]
[MessagePackObject]
public sealed class ItemData
{
    [Key(0)] [CultName] public string Name = string.Empty;
    [Key(1)] public int Value;
}
```

## Typical Usage

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;

var cache = new CultCache();
var store = new MultiFileMessagePackBackingStore("Data");

cache.AddBackingStore(store);
await cache.PullAllBackingStoresAsync();
```

## Notes

- Payload bytes are MessagePack. Store metadata and schema catalogs are also
  persisted through MessagePack in this package.
- The generator package now targets attributed document types instead of
  `DatabaseEntry` subclasses.
- The raw CultNet document lane should treat these payload bytes as already
  blessed, not decode and re-encode them for sport.
