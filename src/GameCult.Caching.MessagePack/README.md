# GameCult.Caching.MessagePack

`GameCult.Caching.MessagePack` provides the canonical CultCache persistence
format for the attribute-first `GameCult.Caching` stack.

## Included Types

- `SingleFileMessagePackBackingStore`
- `CultDocumentMessagePackSerialization`
- `CultDocumentResolver`
- `CultRecordRefFormatter<T>`
- `CultMathResolver`: the canonical component-array encoding of every public CultMath value type, part of the default resolver chain

Cult document payloads serialize through `MessagePackSerializer` with the
options of the document's assembly (`CultDocumentMessagePackSerialization.OptionsFor`),
after the reflective `CultDocumentRegistry` has accepted the document shape. The
store snapshot format is written by hand against
`MessagePackWriter`/`MessagePackReader`.

## What It Does

- stores whole CultCache snapshots in MessagePack
- serializes attributed document payloads as MessagePack `[Key(n)]` arrays
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
- The raw CultNet document lane should treat these payload bytes as already
  blessed, not decode and re-encode them for sport.

## CultMath Value Shapes

Every public CultMath value type has one MessagePack shape (`CultMathResolver`,
in the default chain) and one JSON shape (`CultMathJson`, added to a consumer's
options with `new JsonSerializerOptions().AddCultMathConverters()`; CultLib owns
no JSON options of its own). MessagePack decoding skips extra elements, leaves
missing ones at zero (`Color32` alpha at 255) and refuses nil. JSON reading
skips unknown properties (`UnmappedMemberHandling.Disallow` refuses them), leaves
missing ones at zero, honours `PropertyNameCaseInsensitive`, lets a repeated
property replace the whole earlier value (refused when `AllowDuplicateProperties`
is false), and throws `JsonException` for a wrong token or an out-of-range number.
Numbers follow `JsonSerializerOptions.NumberHandling` as System.Text.Json's own
float does: `"NaN"`, `"Infinity"` and `"-Infinity"` are written under
`AllowNamedFloatingPointLiterals` or `WriteAsString` and read under
`AllowNamedFloatingPointLiterals` or `AllowReadingFromString`; quoted numbers read
under `AllowReadingFromString` with no surrounding whitespace.
Every type is also a dictionary key: its flattened components joined by commas,
invariant culture (`"1,2"`, `"true,false"`, matrices row by row, `rect` as
`"minX,minY,maxX,maxY"`), and only that exact spelling reads back. Two intended
divergences from System.Text.Json: a bare number beyond a component's range
(`1e40` as a float) is refused where System.Text.Json reads infinity, and a
refusal on write is a `JsonException` where System.Text.Json throws
`ArgumentException`. A consumer resolver or converter
registered ahead of these wins.

| Type | MessagePack | JSON |
| --- | --- | --- |
| `float2`, `double2`, `int2`, `bool2` | `[x, y]` | `{"x":..,"y":..}` |
| `float3`, `double3`, `int3`, `bool3` | `[x, y, z]` | `{"x":..,"y":..,"z":..}` |
| `float4`, `int4`, `bool4`, `quaternion` | `[x, y, z, w]` | `{"x":..,"y":..,"z":..,"w":..}` |
| `Color32` | `[r, g, b, a]` as uint8 | `{"r":..,"g":..,"b":..,"a":..}` |
| `Random` | `[state]` | `{"state":..}` |
| `float2x2`, `float3x3` | array of row vectors (row-major) | array of row objects |
| `rect` | `[min, max]` of `float2` | `{"min":{..},"max":{..}}` |
| `CultCellular` | `[nearest, edge, id]` | `{"nearest":{..},"edge":{..},"id":..}` |
| `CultPhasor` | `[cos, sin]` of `float4` | `{"cos":{..},"sin":{..}}` |

Floats are MessagePack float32 (`0xCA`), doubles float64 (`0xCB`), integers the
shortest MessagePack integer.
