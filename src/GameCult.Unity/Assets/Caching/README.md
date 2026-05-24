# GameCult CultCache Unity Tools

`org.gamecult.caching.unity` provides Unity editor tooling for opening,
inspecting, editing, and saving CultCache `.cc` stores.

The package is intentionally a Unity editor surface over `GameCult.Caching`.
CultCache owns document identity, schema descriptors, persistence, dirty state,
and flush behavior. The editor window owns selection, presentation, and edit
commands.

## Use

1. Reference `GameCult.Caching`, `GameCult.Caching.MessagePack`, and this UPM
   package from the Unity project that declares your `[CultDocument]` classes.
2. Open Unity's `GameCult/CultCache Studio` menu.
3. Open or create a `.cc` file.
4. Select a document type, select a record, edit fields, then save explicitly.

The editor discovers registered CultCache document descriptors from the same
registry used by runtime code. It does not require document classes to inherit
from Unity types or storage base classes.

## Inspector Annotations

Domain classes can opt into nicer Unity editor rendering with attributes from
`GameCult.Unity.Caching`:

```csharp
using GameCult.Caching;
using GameCult.Unity.Caching;
using MessagePack;

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

Available annotations:

- `CultInspectorLabel`
- `CultInspectorHidden`
- `CultInspectorReadOnly`
- `CultInspectorOrder`
- `CultInspectorTextArea`
- `CultInspectorRange`
- `CultInspectorAssetPath`

## Current Scope

The first studio pass supports local `.cc` files, explicit save/reload, adding
records with parameterless constructors, deleting records, primitive fields,
enums, strings, lists/arrays, nested objects, Unity object references, asset
path pickers, and `CultRecordRef<T>` key editing.

CultMesh collaboration should feed the same CultCache mutation surface rather
than becoming a second owner for document state.
