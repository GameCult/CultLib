# GameCult CultCache Unity Tools

`org.gamecult.caching.unity` is the Unity editor surface over CultCache: open a
store, browse its documents, edit them, save.

CultCache owns document identity, schema descriptors, persistence, dirty state
and flush. The Studio window owns selection, presentation and edit commands. It
references `GameCult.Caching` and `GameCult.Caching.MessagePack` at compile time
through the `org.gamecult.cultlib` package (assembly `GameCult.CultLib`) and
`CultMath` through `org.gamecult.cultmath`.

## Use

1. Add this package; it depends on `org.gamecult.cultlib` and
   `org.gamecult.cultmath`.
2. Open `GameCult/CultCache Studio`.
3. `Open` a `.cc` store or make one with `New`. `Directory` opens or creates a
   directory store (a manifest beside its `.records` folder; an existing one is
   detected without the toggle). `Read Only` opens the store read-only.
4. Pick a document type, then a record. Edits upsert into the cache as you make
   them; the toolbar shows `Dirty` until `Save` flushes the store.

Document types come from the cache's `CultDocumentRegistry`. A type lists its
own records and those of its subclasses, by `[CultName]` with the key as
fallback. `Add`, `Duplicate` and `Delete` act on the selected type and record.
A `[CultGlobal]` type with no record shows as absent with a `Create` action; the
Studio never creates one on its own.

Opening a store never writes it. Closing or reloading scripts drops unsaved
edits.

## Drawers

Built in: strings, integers, floats, bools, enums (flags included), the CultMath
values (`float2/3/4`, `double2/3`, `int2`, `bool2`, `quaternion`, `rect`,
`Color32`), `Vector2/3`, `Color`, Unity object
references, lists and arrays, dictionaries (keys and values drawn recursively),
`CultRecordRef<T>` (a picker of records assignable to `T`, plus the raw key),
abstract and interface members (a subtype picker limited to the member type's
MessagePack `[Union]` declarations), and nested `[MessagePackObject]` types by
their `[Key]` members. A member no drawer claims shows a red error row.

A project adds or overrides a drawer with an editor class:

```csharp
using System.Reflection;
using GameCult.Unity.Caching;
using GameCult.Unity.Caching.Editor;
using UnityEditor;
using UnityEngine;

[CultInspectorDrawer(typeof(CultMath.float3))]
public sealed class Float3ColorDrawer : ICultInspectorDrawer
{
    public object Draw(string label, object value, MemberInfo member)
    {
        var v = (CultMath.float3)value;
        var c = EditorGUILayout.ColorField(label, new Color(v.x, v.y, v.z));
        return new CultMath.float3(c.r, c.g, c.b);
    }
}
```

`MemberType` may be an open generic definition to claim every closed form. A
project drawer wins over the built-in one for its type.

## Inspector Annotations

```csharp
using GameCult.Caching;
using GameCult.Unity.Caching;
using MessagePack;

[MessagePackObject]
[CultDocument("game.item", "game.item.v1")]
public sealed class ItemData
{
    [Key(0)]
    [CultName]
    [CultInspectorLabel("Display Name")]
    public string Name = string.Empty;

    [Key(1)]
    [CultInspectorRange(0, 999)]
    public int Value;

    [Key(2)]
    [CultInspectorTextArea]
    public string Notes = string.Empty;

    [Key(3)]
    [CultInspectorAssetPath(typeof(UnityEngine.Texture2D))]
    public string IconPath = string.Empty;
}
```

- `CultInspectorLabel`
- `CultInspectorHidden`
- `CultInspectorReadOnly`
- `CultInspectorOrder`
- `CultInspectorTextArea`
- `CultInspectorRange`
- `CultInspectorAssetPath`
- `CultInspectorDrawer` (on drawer classes)

CultMesh collaboration should feed the same CultCache mutation surface rather
than becoming a second owner for document state.
