# GameCult.Caching.MessagePack

`GameCult.Caching.MessagePack` provides the canonical CultCache persistence
format for the attribute-first `GameCult.Caching` stack.

## Included Types

- `SingleFileMessagePackBackingStore`
- `CultDocumentMessagePackSerialization`
- `CultDocumentResolver`
- `CultRecordRefFormatter<T>`

When a project declares `[CultDocument]` models, wire in the
`GameCult.Caching.MessagePack.Generator` analyzer there as well. It emits
assembly-local metadata providers for the cache registry. Cult documents then
serialize through explicit generated slot codecs, while the store snapshot
format is written by hand against `MessagePackWriter`/`MessagePackReader`.

## What It Does

- stores whole CultCache snapshots in MessagePack
- serializes plain attributed document payloads through explicit generated slot codecs
- keeps explicit `CultRecordRef<T>` values compact on disk/wire
- preserves the cache/store split where metadata lives outside the domain model

## Example Model

```csharp
using GameCult.Caching;
using MessagePack;

[CultDocument("gamecult.item_data", "gamecult.item_data.v1")]
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
var store = new SingleFileMessagePackBackingStore("Data.msgpack");

cache.AddBackingStore(store);
await cache.PullAllBackingStoresAsync();
```

## Notes

- Payload bytes are MessagePack. Store metadata and schema catalogs are also
  persisted through hand-written MessagePack array layouts in this package.
- The generator package now targets attributed document types instead of
  `DatabaseEntry` subclasses.
- The raw CultNet document lane should treat these payload bytes as already
  blessed, not decode and re-encode them for sport.
