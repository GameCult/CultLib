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
in `GameCult.Caching`; `CultCacheMessagePack.CreateInspectorModel` builds it
over a `.cc` store's codec. It gives the member list of a type (registry
catalog slots, `CultInspector*` metadata, whether a member is assignable), the
shape of any value (string, integer, float, bool, enum, `CultRecordRef<T>` with
its candidate records, rank-1 list or array, dictionary, `[Union]` with only
its declared subtypes, nested keyed object, struct edited in place through its
public fields, or its settable properties when it has none, or unsupported with a reason), drawer claim resolution
(`CultInspectorDrawerClaims`), and the edit decisions: the key a dictionary
entry keeps and the notice when a key is refused (null, an empty record
reference, a duplicate: serialized the same as another key under the owning
document's serializer or equal by the rebuilt dictionary type's default
comparer, or a key that document cannot serialize), the fresh key an added entry gets, what a new list
element, dictionary value or union pick may be, and how an integer edit clamps
to its type. Edits work on a copy (`CultInspectorEdit`): a refused upsert
leaves the cached object unchanged. The Studio is one lowering of that model
and owns only widgets and layout; a runtime CultUI panel needs only its own
renderer and drawers to show and edit the same documents under the same rules.

## Drawers

Built in: every model shape above. A struct without `[Key]` members is edited
in place. With public fields it folds out to those fields only (readonly ones
read-only), so aliasing properties such as `Quaternion.eulerAngles` or
`Rect.min` add no rows; without public fields it folds out to its public
properties with a setter, `init` ones read-only (record structs and CultMath
matrices work as written). Get-only properties are never shown. Unity object
references draw as object fields. A value no drawer claims and no shape covers
shows a red error row with the model's reason, as does a multi-dimensional
array.

A project adds or overrides a drawer with an editor class. `Claimed` is either
a value type (an open generic definition claims every closed form) or an
attribute type, which claims every member carrying it. Attribute claims win
over type claims, which win over built-in drawing. A claim two drawers make is
used by neither: both are logged and the member shows the error row.

A drawer returns the new value and lets its IMGUI controls set `GUI.changed`;
only a frame that reports a change is saved. Mutating the value in place
without reporting a change is not saved reliably. A drawer that throws shows
an error row and discards that frame's edit copy, so nothing edited in that
frame is saved.

```csharp
using System;
using System.Reflection;
using GameCult.Caching;
using GameCult.Unity.Caching.Editor;
using UnityEditor;
using UnityEngine;

// Lives beside the documents; an attribute needs no Unity reference.
public sealed class ColorAttribute : Attribute
{
}

// Draws a "#RRGGBBAA" string member as a color field.
[CultInspectorDrawer(typeof(ColorAttribute))]
public sealed class ColorDrawer : ICultInspectorDrawer
{
    public object Draw(CultInspector inspector, string label, Type type, object value, MemberInfo member)
    {
        if (type != typeof(string))
            return inspector.DrawDefault(label, type, value, member);
        ColorUtility.TryParseHtmlString(value as string ?? string.Empty, out var color);
        var next = EditorGUILayout.ColorField(label, color);
        return next == color ? value : "#" + ColorUtility.ToHtmlStringRGBA(next);
    }
}
```

`Draw` receives the value's declared type and the member it belongs to, also
for list elements and dictionary keys and values, so an attribute drawer
checks the type it is handed. `inspector.DrawDefault` hands a value back to
built-in drawing; `inspector.DrawValue` draws a sub-value with claims applied.

`inspector.Record` is the whole document the value sits in, for drawers that
read sibling members (a texture path, a hull's tints). It is read-only by
contract: the model's `CultInspectorEdit.Record`, a second copy of the stored
document as the edit began, made on first read. It is neither the cached
object nor the edit copy the Studio saves, so writing to it saves nothing; an
edit reaches the record only through the value `Draw` returns. Within one frame
it does not show edits other drawers made earlier in that frame; after a save
the next frame's edit starts from the saved record.

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
