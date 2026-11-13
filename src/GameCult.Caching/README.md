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

[MessagePackObject]
[CultDocument("gamecult.item_data", "gamecult.item_data.v1")]
public sealed class ItemData
{
    [Key(0)]
    [CultName]
    public string Name = string.Empty;

    [Key(1)]
    [CultIndex]
    public string Sku = string.Empty;

    [Key(2)]
    public string Category = string.Empty;

    [Key(3)]
    public int Value;
}
```

Available cache attributes:

- `CultDocument(schemaName, schemaVersion)`
- `CultName`
- `CultIndex(alias?)`: a unique lookup. Within one document type, a non-blank value names one record; a commit that would leave two records holding one is refused, and a store holding such a pair refuses to load. Uniqueness is judged by a `CultCache` over the single-file store; a cacheless `WriteDocument`/`WriteRecord` of such a type is refused (also when the catalog declares an index alias the call cannot classify), and concurrent writers on a directory store are not covered (`unique-index-directory-store`). A value is culture-invariant, and a number is its canonical decimal (`1.0m` and `1.00m`, `0.0` and `-0.0` are one value).
- `CultRole(alias?)`: a selection and reference role. Many records may share a value; it is never a lookup.
- `CultGlobal`
- `CultReference(targetType?, many: false)`

`CultDocumentRegistry` builds every document descriptor by reflection over
these attributes and MessagePack's `[Key]`/`[IgnoreMember]`; it is the only
descriptor authority.

## Main Concepts

### Record keys and handles

`CultCache` gives each stored document a cache-owned `CultRecordKey`. You work
with typed handles instead of forcing storage identity into the domain object.

```csharp
var cache = new CultCache();
var handle = await cache.AddAsync(new ItemData
{
    Name = "Potion",
    Sku = "POT-1",
    Category = "Consumable",
    Value = 50
});

var potion = cache.Get<ItemData>(handle.Key);
```

### Name and index lookups

Name and index lookups are driven from attributes, not manual registration.

```csharp
var byName = cache.GetByName<ItemData>("Potion");
var byIndex = cache.GetByIndex<ItemData>("Sku", "POT-1");
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
var key = new CultRecordKey("potion");

// The golden path: read, change a private copy, commit only if the record is unchanged, re-run on a lost race.
// Durable when it returns; no flush.
var result = cache.Update<ItemData>(key, item =>
{
    item ??= new ItemData { Name = "Potion", Sku = "POT-1", Category = "Consumable" };
    item.Value += 10;
    return item;
});

// The blind opt-out: last writer wins per key. Name the key, or a new key claims the same unique Sku and is refused.
var handle = await cache.UpsertAsync(new ItemData
{
    Name = "Potion",
    Sku = "POT-1",
    Category = "Consumable",
    Value = 50
}, new CultRecordHandle<ItemData>(key));

await cache.FlushAsync();
```

`Update` may run your function more than once, so it must have no side effects. Returning null writes nothing, and a
lost race that outlasts `maxAttempts` comes back as `CultUpdateOutcome.LostRace`. `UpsertAsync` plus `FlushAsync` never
checks what it overwrites.

If you want the explicit assembly rite, the lower-level `CultCache` +
`SingleFileMessagePackBackingStore` path is still there. The helper is just the
blessed front door.

## Managed Documents And SoA Storage

Gameplay code keeps authoring normal typed documents. CultCache owns the record
identity, reactive presentation, and CPU-local structure-of-arrays table behind
that POCO surface.

```csharp
[CultDocument("game.player_transform", "game.player_transform.v1")]
public sealed class PlayerTransform
{
    [CultName] public string Name = string.Empty;
    public float PositionX;
    public float PositionY;
    public int Health;
}

var player = cache.Document<PlayerTransform>(new CultRecordKey("player:alice"));

await player.ReplaceAsync(new PlayerTransform
{
    Name = "Alice",
    PositionX = 4,
    PositionY = 8,
    Health = 100
});

using var subscription = player.Watch().Subscribe(current => Render(current));

player.Value!.Health = 90;
await player.CommitAsync();

var table = cache.Soa<PlayerTransform>();

Span<float> x = table.Column<float>(nameof(PlayerTransform.PositionX)).Span;
Span<float> y = table.Column<float>(nameof(PlayerTransform.PositionY)).Span;
Span<int> health = table.Column<int>(nameof(PlayerTransform.Health)).Span;

for (var row = 0; row < table.Count; row++)
{
    x[row] += 0.016f;
}
```

`Document<T>()` is the standard presentation. `Soa<T>()` exposes the
cache-owned physical table for hot loops and diagnostics; it is not a second
domain model callers have to maintain.

## Element ids

A document type that holds a list of objects gives every object type in that list
(union arms included) a string member marked `[CultElementId]`, next to its
`[Key]`. The id is what lets a variant override one list element instead of the
whole list. Lists of strings, numbers and other non-object values need none.

The rule covers the shapes the id walk owns: arrays, `IEnumerable<T>` classes and
interfaces, dictionary values, and integer-keyed `[MessagePackObject]` and `[Union]`
types. Any other holder is one whole value: `ImmutableArray<T>`, `ArraySegment<T>`,
`Memory<T>`, `KeyValuePair`, tuples, `Lazy<T>`, a class without `[MessagePackObject]`,
a `[DataContract]` or string-keyed type, and a type or member declaring
`[MessagePackFormatter]`. Its elements carry no element ids: a `[CultElementId]`
member inside it is a plain string, never minted or checked, and a variant can only
replace the holder whole.

```csharp
[MessagePackObject]
public sealed class Line
{
    [Key(0)] [CultElementId] public string Id = string.Empty;
    [Key(1)] public string Text = string.Empty;
}
```

Leave `Id` empty and the cache mints a random id when the record is written. An
id you set yourself must be exactly 12 lowercase hex characters
(`0123456789ab`); anything else is refused with a `CultElementIdException`.

For readable ids, derive them: `[CultElementId(nameof(Name))]` makes an unset id
the text of that member (a string or number on the same type, unique within its
list), and a derived id may be any text. A readable name set by hand on a
non-derived list id is refused; the same member outside a list is unconstrained.

### A type that already has a readable id

If the element type already has a readable string id (`Name`, `Slug`, a catalog
key), leave it as it is. Add a trailing keyed string member marked
`[CultElementId(nameof(ExistingId))]`; the element id is derived from the
existing member on load or first write, and then persisted:

```csharp
[MessagePackObject]
public sealed class Recipe
{
    [Key(0)] public string Name = string.Empty;        // existing readable id
    [Key(1)] public int Yield;
    [Key(2)] [CultElementId(nameof(Name))] public string ElementId = string.Empty; // new, last
}
```

Marking `Name` itself `[CultElementId]` is refused (`Name` is not 12 lowercase
hex), and that is deliberate:

- Minted and hand-written ids share one grammar, 12 lowercase hex, so they can
  never collide; a derived id is the one readable form and is checked for
  uniqueness within its list.
- An id the consumer can edit is not a stable override address. Variants address
  an element by its id, so a field that is renamed in place would silently move
  the overrides to a different element, or to none.
- A derived id is persisted once. Renaming `Name` later does not change
  `ElementId`, so the element and every override that addresses it stay put.
  `ElementId` is the address; `Name` goes back to being ordinary data.

Adopting the recipe on data that already exists: the existing member becomes an
id source, so it must be non-empty and unique within its list. A record with a
duplicate value and an empty one both refuse to load
(`CultElementIdException`), so clean such data first.

Take the next free `[Key]` slot, after the highest key on the type (and above
the subclasses' keys on a union base).

## Store format and rollback

A store that holds an element id is written as `cultcache.store.v3` (a directory
store as `cultcache.store.v5.directory-content-addressed-pages`, `v4` otherwise).
A single-file store that holds a variant but no id is `v2`, and a store with
neither stays `v1`. The mark follows content: a whole-store write that leaves no
element holding an id drops it.

Refusal by name depends on the store kind and the pin. A **directory** store (`v5`) is refused by every Unity pin from `cultlib-unity-v1.0.57` and `caching-unity-v1.2.0` on (the earliest tags of each line whose `DirectoryMessagePackBackingStore` reads only the `v4` stamp: "only ... is readable"). A **single-file** store (`v3`) is refused only from `cultlib-unity-v2.0.0` and `caching-unity-v2.0.0`: `cultlib-unity-v1.0.61` and `caching-unity-v1.4.0` read the header string and never compare it. The NuGet line (`cultlib-v1.0.32`, the last `cultlib-v` tag) refuses neither: its directory store writes `cultcache.store.v1.directory` and never checks the stamp on read. Rolling back a single-file store to any of those pins reads it silently, drops the ids, and can rewrite the elements without them. That is the dangerous case.

Rolling a consumer back after it has written a v3 or v5 store is therefore
one-way. The only carry-back is narrow and lossy: a whole-store write that leaves
no id anywhere in the store (every record rewritten without list elements) writes
the store unmarked again, and the ids are gone. Otherwise restore from a copy
taken before upgrading. Upgrade every reader before any writer.

## Persistence discipline

Persistence stays manual by default. Mutations mark the cache and attached
backing stores dirty; nothing silently flushes every object write just because
that felt convenient in the moment.

Useful surfaces:

- `CultCacheMessagePack.OpenAsync(path)`
- `cache.IsDirty`
- `store.IsDirty`
- `cache.Document<T>(key)`
- `cache.Update<T>(key, apply)`
- `cache.UpsertAsync(document)` (blind write)
- `cache.FlushAllBackingStores()`
- `cache.FlushAsync()`
- `cache.FlushAttachedStoresOnDispose`
- `store.FlushOnDispose`

This is built for callers like Aquarium that want durable runtime state while
still choosing *when* to pay the write cost.

A single-file store admits any number of writers, in one process or many, one at
a time. Every write (flush, `PushAll`, `CommitBatch`, cacheless `WriteRecord`)
takes an exclusive advisory lock on the sidecar `<path>.lock`, reads the file as
it is then, applies its own staged keys onto it and replaces the file by a
write-to-temp-then-rename. Two writers never interleave bytes, and a record
another writer put there survives; a key both staged holds the last write
(`Contracts/cultcache-store-composition.md`, "Locking"). What the lock does not
give is cross-key or cross-store atomicity, a lock that binds a program that
ignores it, or protection on a filesystem without working `flock`. A second
writer waits (30 seconds, then `IOException`; `TryCommit` reports `Contended`
instead of waiting); readers take no lock and always see one complete file. The
operator procedures for a leftover temp file and for backing up two stores are
in the same contract document, "Operating a single-file store".

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
