# GameCult CultCache Unity Tools

`org.gamecult.caching.unity` is the Unity editor surface over CultCache: open a
store, browse its documents, edit them, save.

CultCache owns document identity, schema descriptors, persistence, dirty state
and flush. `CultInspectorModel` (in `GameCult.Caching`) owns what an inspector
shows and which edits it may make. The Studio window owns selection, the
toolbar and IMGUI widgets. It references `GameCult.Caching` and
`GameCult.Caching.MessagePack` at compile time through the
`org.gamecult.cultlib` package (assembly `GameCult.CultLib`).

## Use

1. Add this package; it depends on `org.gamecult.cultlib`.
2. Open `GameCult/CultCache Studio`.
3. `Open` a `.cc` store. `Directory` opens a directory store (a manifest beside
   its `.records` folder; an existing one is detected without the toggle).
   `Read Only` opens the store read-only. `New` creates an empty writable
   single-file store; it is disabled while `Read Only` or `Directory` is on and
   refuses a path that already exists.
4. Pick a document type, then a record. Edits upsert into the cache as you make
   them; the toolbar shows `Dirty` until `Save` flushes the store.

Game data stores do not live under the project's `Assets` folder: Unity would
import and manage them, and CultCache exists so Unity does not manage game
data. `New` refuses a path under `Assets`, `Open` warns about one, and the file
dialogs start in the last store's folder or the project root.

Document types come from the cache's `CultDocumentRegistry`. A type lists its
own records and those of its subclasses, by `[CultName]` with the key as
fallback. `Add`, `Duplicate` and `Delete` act on the selected type and record.
A `[CultGlobal]` type with no record shows as absent with a `Create` action; the
Studio never creates one on its own.

Opening a store never writes it, directory stores included. Closing or
reloading scripts drops unsaved edits.

## Inspection Model

`CultInspectorModel` is engine-free and lives beside the inspector attributes
in `GameCult.Caching`. Given a registry and the store's document codec it gives
the member list of a type (registry catalog slots, `CultInspector*` metadata,
whether a member is assignable), the shape of any value (string, integer,
float, bool, enum, composite value rebuilt through its constructor,
`CultRecordRef<T>` with its candidate records, rank-1 list or array,
dictionary, `[Union]` with only its declared subtypes, nested keyed object, or
unsupported with a reason), drawer claim resolution
(`CultInspectorDrawerClaims`), dictionary key identity and refusal (null keys,
empty record references and duplicates), and edits that work on a copy
(`CultInspectorEdit`): a refused upsert leaves the cached object unchanged. The
Studio is one lowering of that model and owns only widgets and layout; a
runtime CultUI panel needs only its own renderer and drawers to show and edit
the same documents under the same rules.

## Drawers

Built in: every model shape above. Composite values of scalars (`float2/3/4`,
`int2`, `double2/3`, `bool2`, `quaternion`, `Color32`) draw as one row;
`rect` folds out to its corners. Unity object references draw as object
fields. A value no drawer claims and no shape covers shows a red error row, as
does a multi-dimensional array.

A project adds or overrides a drawer with an editor class. `Claimed` is either
a value type (an open generic definition claims every closed form) or an
attribute type, which claims every member carrying it. Attribute claims win
over type claims, which win over built-in drawing. A claim two drawers make is
used by neither: both are logged and the member shows the error row. A drawer
that throws draws an error row and leaves the value unchanged.

```csharp
using System;
using System.Reflection;
using GameCult.Caching;
using GameCult.Unity.Caching.Editor;
using UnityEditor;
using UnityEngine;

public sealed class ColorAttribute : Attribute
{
}

[CultInspectorDrawer(typeof(ColorAttribute))]
public sealed class ColorDrawer : ICultInspectorDrawer
{
    public object Draw(CultInspector inspector, string label, Type type, object value, MemberInfo member)
    {
        if (type != typeof(CultMath.float3))
            return inspector.DrawDefault(label, type, value, member);
        var v = (CultMath.float3)value;
        var c = EditorGUILayout.ColorField(label, new Color(v.x, v.y, v.z));
        return new CultMath.float3(c.r, c.g, c.b);
    }
}
```

`Draw` receives the value's declared type and the member it belongs to, also
for list elements and dictionary keys and values, so an attribute drawer
checks the type it is handed. `inspector.DrawDefault` hands a value back to
built-in drawing; `inspector.DrawValue` draws a sub-value with claims applied.

## Inspector Annotations

The annotations live in `GameCult.Caching`, so headless shared models carry
them without referencing Unity.

```csharp
using GameCult.Caching;
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
    [CultInspectorAssetPath]
    public string IconPath = string.Empty;
}
```

- `CultInspectorLabel`
- `CultInspectorHidden`
- `CultInspectorReadOnly`
- `CultInspectorOrder`
- `CultInspectorTextArea`
- `CultInspectorRange`
- `CultInspectorAssetPath` (optional engine asset `Type`, named by the
  consumer; the Studio uses it when it is a `UnityEngine.Object` type)
- `CultInspectorDrawer` (on drawer classes)

CultMesh collaboration should feed the same CultCache mutation surface rather
than becoming a second owner for document state.
