# Studio Record Grouping And Reference Drag-Drop Cut

First mapped 2026-09-17 (Imagination) against CultLib `122c217`. **Refreshed 2026-09-29
(Imagination) against CultLib `main` `feeced8` and Aetheria `codex/fire-control-12` `4b594e11`.**
Every `file:line` below is against those revisions unless marked. Hands re-reads an anchor
before editing it; `main` keeps moving.

**Rulings.** Q1 **B** (a class attribute on the model; argument order is nesting order;
2026-09-17). Q2 **A** (occupied values only). Q3 **A** (list only, not the ref picker).
Q4 **A** (no record search). Q5 **overtaken**: one release from `main`, together with
document-variants C3 (`docs/document-variants-cut.md`), taking whatever native-plugin drift
`main` carries. Q6 **A** (flat row by resolved value, C3's variant marker; 2026-09-30). Q7 **B** (legacy parity
minus manufacturer, plus `FactionProductData` by `Manufacturer` then `Design`, plus
`WeaponItemData` by `WeaponType`; 2026-09-30).

## Target

### Ends

1. **Declared record grouping.** `[CultInspectorGroupBy(nameof(A), nameof(B))]` on a document
   class makes Studio's record list for that type a foldout tree, nested in argument order. A
   declaration on a base type groups every derived type listed under it.
2. **Create in a group.** Every group node offers `Create`. The new record gets every grouped
   member on the path to that node set to that node's value, so it lands in that node.
3. **Reference drag and drop.** A record row dragged from Studio's list onto any
   `CultRecordRef<T>` value the inspector draws (member, list element, dictionary key or value)
   is accepted exactly when the ref's picker would offer that record.

Recovered from the legacy "Database Tools" window (Aetheria `d3db1730`,
`Assets/Scripts/CultCache/Editor/DatabaseView.cs`, `AetheriaDatabaseView.cs`,
`Inspectors/DatabaseLinkInspector.cs`). Selector lambdas are rejected (operator, 2026-09-17).

**The attribute is not `[CultIndex]`.** `[CultIndex]` means unique (operator, 2026-09-30;
`CultDocumentContracts.cs:34`). Grouping is `CultInspectorGroupByAttribute`, in
`CultInspectorAttributes.cs`, read only by `CultInspectorModel`. It changes no schema id,
catalog entry, index, or store byte. The index cut's deletion of grouping-only `[CultIndex]`
declarations (CultMesh, Geometry) adds no `[CultInspectorGroupBy]` in their place: those are
service stores, not Studio-authored catalogs.

### Invariants

- **I1. The model owns grouping.** `CultInspectorModel` alone decides which members group a
  listed type and in what order, which declarations are invalid (and the notice), the tree's
  partition, node identity, order and labels, and the object create-in-group makes. Studio
  lowers the tree; it never reads the attribute, calls `SetValue` for a preset, or computes a
  group label.
- **I2. The leaves partition the candidates.** Across all leaves the tree holds exactly
  `RecordCandidates(CultRecordRef<listed>, records)`, each record once, in candidate order
  inside a leaf. Grouping never hides a record, including one whose grouped value is unset,
  dangling, or an undefined enum value.
- **I3. The listed type owns the tree's shape.** Grouping comes from the type selected in the
  type pane, never from each record's runtime type.
- **I4. Node identity is value identity, not the label.** A ref groups by key, an enum by its
  underlying value, other kinds by the value's invariant string. Foldout state keys on node id.
- **I5. Create-in-group round-trips.** Upsert what `CreateInGroup(listed, node)` returns,
  regroup: the new record lies in a node with the same id.
- **I6. One candidate rule.** `IsRecordCandidate(refType, record)` is the only test for "this
  record may be this ref's value". `RecordCandidates` filters through it; drop acceptance calls it.
- **I7. One ref label.** The model owns `None` and `Missing <key>`; the picker and group labels
  both use it.
- **I8. Invalid declarations never break the window.** They give a flat list plus the model's
  notice, never a throw in `OnGUI`.
- **I9. A drop honours the picker's gates.** Refused in a disabled (read-only) scope, when the
  payload came from another model, and when the key equals the current key. A dictionary-key
  drop still passes `ReplaceKey` (`CultCacheStudioDrawers.cs:303-309`).
- **I10 (new). Grouping reads what readers read.** A variant record groups, labels, and is a
  drop candidate by its resolved `Document` (`CultCache.cs:369-371`), never its delta.

### Non-goals

No sibling-runtime port (TS `cult-cache-inspector.ts` is a byte dumper; Rust and Python have
no inspector). No runtime CultUI panel. No search (Q4), no picker grouping (Q3), no
drop-to-append on list headers, no legacy `New <Type>` naming.

## Body findings

Kept from 2026-09-17, re-anchored:

- **F3.** Legacy grouping matched the exact type; grouping derived records under a base
  declaration is new.
- **F4.** Legacy create-in-group was dead (`DatabaseView.cs:70-75` throws); legacy foldouts
  keyed on the label hash.
- **F5.** `member.GetCustomAttributes(true)` ignores `inherit` for a `PropertyInfo`, so a base
  `[M] abstract P` is not seen on the override. Sites: `CultInspectorModel.cs:36` (now the only
  metadata site: `MetadataOf` caches it per member, `:341-351`) and `:209`
  (`CultInspectorDrawerClaims.Resolve`). Fix: `Attribute.GetCustomAttributes(member, true)`.
- **F6.** Studio `Add` creates only the listed type (`CultCacheStudioWindow.cs:340-346`,
  `CreateElement(type, type)`), disabled when the model cannot create it (`:141`). So
  create-in-group never makes a derived type, and an abstract listed type offers no `Create`.
- **F7.** Studio search filters the type pane only (`:112-120`).
- **F8.** Foldouts are in-memory (`CultCacheStudioDrawers.cs:33`); `OnDisable` closes the store
  (`:48-51`), domain reload included. The only `EditorPrefs` key is `LastPathKey` (`:13`).
- **F9.** No drawer claims `CultRecordRef<>`, so every ref reaches `DrawRecordRef`
  (`CultCacheStudioDrawers.cs:254-280`).
- **F10.** The Studio editor sources compile outside Unity (netstandard2.1, C# 9, against
  Unity 6000.3.24f1 `Editor\Data\Managed\UnityEngine\*.dll` without the `UnityEditor.dll`
  facade, plus the built `GameCult.Caching*` and tracked `MessagePack*.dll`). The 09-17
  scratch project is gone; Hands rebuilds it in its own scratch from this recipe.
- **F11.** The record row reads its click in `GUILayout.Toggle` (`:184`); a drag start must be
  handled on a reserved rect before the toggle draws (IMGUI convention, not probed).

New since the map:

- **F12. Variants (C1, `6ffe17a`).** `CultInspectorModel(registry, CultCodec)`
  (`CultInspectorModel.cs:302-306`); Studio still calls `CultCacheMessagePack.CreateInspectorModel(registry)`
  (`CultCacheMessagePack.cs:33-34`, unchanged). `CultStoredDocument.Variant` is the delta
  (`CultCache.cs:374`); `Document` is the resolved view. A top-level override is a
  `Set` whose path is one step at the member's slot (`CultVariants.cs:72-73`), so "is this
  grouped member overridden on this variant" is computable from the member's `Slot`.
- **F13. Every Studio commit to a variant key is refused until C3.** `CultInspectorEdit.Commit`
  is a plain `UpsertAsync` (`CultInspectorModel.cs:266-282`); Q1a refuses a plain write at a
  variant key (`CultCache.cs:2494-2496`). A drop into a variant's inspector therefore reports
  that refusal until C3 lands. The release waits for C3, so no shipped Studio shows it, but
  the operator click-through must run after C3.
- **F14. Studio `Duplicate` of a variant makes a flattened plain copy**
  (`CultCacheStudioWindow.cs:349-353` clones the resolved `Document`). Not this campaign's
  defect; C3's `CreateVariant` sits beside it.
- **F15. Element ids (C2a, `hands/variants-c2a`, WIP `df74f8c`).** No grouping interaction:
  grouped members are top-level scalars and refs; ids live on list elements. Drops write a ref
  into the edit document; ids are untouched and minted on write for new elements; C3's diff
  keys overrides on them. Two consequences:
  - After C2a, registration refuses a document type whose object-list element lacks a
    `[CultElementId]` (`CultCache.cs` descriptor build, WIP). Cut 1's new fixtures carry no
    object lists.
  - C2a does not hide or lock the id member, so Studio shows it as an editable string.
    Hand-editing one retargets overrides. That belongs to C3 (make `[CultElementId]` members
    read-only in the model), not here; flagged for Self.
- **F16. The 1.0.60/1.4.0 release shipped** (`0155ba8`, tags on `45c2f40`) and
  `codex/asset-guid-attribute` is merged. It already carried the 302,080-byte native bridge,
  so the 09-17 Q5 drift is spent. Native source has moved since (`git diff --stat 45c2f40 main
  -- native/`: 5 files, +840/-118), so this release rebuilds the bridge again. The v1 exports
  the managed side imports (`src/GameCult.Mesh.Quic.Native/*.cs:263-276`) remain at
  `cultmesh_quic_native.cpp:1405-1465`.
- **F17. Aetheria's schema moved.** `ItemData.Manufacturer` is gone (slot 3 retired,
  `ItemData.cs:281-282`). The manufacturer relation now lives on `FactionProductData`
  (`FactionProduct.cs:27`, `CultRecordRef<Faction> Manufacturer`; `:24`,
  `CultRecordRef<CraftedItemData> Design`). Aetheria pins `cultlib-unity-v1.0.60` /
  `caching-unity-v1.4.0` / `cultmath-unity-v0.2.4` (`Packages/manifest.json:54-56`),
  `CultLibRevision` `45c2f40` and a separate `CultMathRevision` `6d5e209`
  (`Directory.Build.props:6,10`).
- **F18. After C2a, Aetheria cannot take the new CultLib without its element-id sweep**
  (`List<ItemRole>`, `List<BehaviorData>`, `List<AudioStat>`, `List<HardpointData>`,
  `List<ProductRole>`, `List<LoadoutSlot>`, among others). So the Aetheria re-pin belongs to
  variants C4, and this campaign's Cut 4 shrinks to annotations and tests on top of it.

## Identity and authority

| Thing | Owner | Lifecycle | Persisted |
|---|---|---|---|
| `[CultInspectorGroupBy]` | consumer source | compile time; nearest declaration wins; no arguments opts out | source only |
| `CultInspectorGrouping` (resolved) | model | cached per listed type like `_shapes` (`CultInspectorModel.cs:296`) | no |
| `CultInspectorRecordGroup` tree, node ids, labels | model | rebuilt each `OnGUI`, as `RecordCandidates` is (`CultCacheStudioWindow.cs:167`) | no |
| Group foldouts (node id → bool) | window | window memory; cleared by `CloseStore` (`:318-327`) | no |
| Drag payload `{ Model, Key }` under `"GameCult.CultCacheStudio.Record"` | Studio list (source) | one drag | no |
| Drop acceptance | `IsRecordCandidate` plus I9 gates | per `DragUpdated`/`DragPerform` | no |
| Created-in-group object | model makes; `CultCache` admits via `UpsertAsync`, as `Add` | new plain record | store, on `Save` |

## Forks

Q1-Q5 are ruled (header). Two remain.

### Q6. RULED A (2026-09-30). How does the grouped list show a variant? (blocks nothing in Cuts 1-2; shapes C3's row lowering)

Mechanical defaults, not asked: a variant groups by its resolved values (I10), so a variant
that inherits its base's `HullType` sits in the base's node and moves when the base changes;
create-in-group always makes a plain record; C3's `CreateVariant` needs no preset because the
variant inherits its base's group; a variant is a valid drag payload.

- **A. Flat by resolved value.** A variant is an ordinary row in its resolved group. C3 adds a
  row marker ("variant of Longinus"). Which of its grouped values are overridden shows in the
  inspector through C3's per-member marker, not in the tree.
- **B. Nested under the base.** Inside a leaf, a variant row sits indented under its base's row
  when both share the leaf; otherwise it stands alone with the marker.
- **C. A's rows plus an inherited/overridden glyph on the row** for the grouped members.
- **Recommendation: A.** I2 stays one rule, the tree has one shape, and the base link already
  lives in C3's inspector. B makes the tree's shape depend on two relations, and splits
  whenever a variant overrides a grouped value. C duplicates C3's inspector marker.

### Q7. RULED B (2026-09-30). Which Aetheria types group, and by what?

Candidates on `codex/fire-control-12` (enums unless noted; `[Flags]` enums excluded):

| Type | Member | Anchor | Note |
|---|---|---|---|
| `SimpleCommodityData` | `Category` (`SimpleCommodityCategory`) | `ItemData.cs:310` | legacy parity |
| `CompoundCommodityData` | `Category` (`CompoundCommodityCategory`) | `ItemData.cs:338` | legacy parity |
| `GearData` | `Hardpoint` (`HardpointType`) | `ItemData.cs:454` | legacy parity; `WeaponItemData` inherits when listed under `GearData` |
| `WeaponItemData` | `WeaponType`; or `WeaponCaliber`, `WeaponRange` | `ItemData.cs:485`, `:482`, `:479` | re-declaration, applies when listing weapons |
| `HullData` | `HullType` | `ItemData.cs:519` | legacy parity |
| `FactionProductData` | `Manufacturer` (ref `Faction`), then `Design` (ref `CraftedItemData`) | `FactionProduct.cs:27`, `:24` | where "by manufacturer" now lives |
| `Loadout` | `Hull` (ref `HullData`) | `Loadout.cs:17` | optional |

`ConsumableItemData`, `CargoBayData`, `DockingBayData`, `Faction`, `PersonalityAttribute`
have no groupable member worth a level.

- **A. Legacy parity minus manufacturer:** commodities by `Category`, `GearData` by
  `Hardpoint`, `HullData` by `HullType`.
- **B. A, plus `FactionProductData` by `Manufacturer` then `Design`, plus `WeaponItemData` by
  `WeaponType`.**
- **C. B with `FactionProductData` by `Design` then `Manufacturer`** (the market-segment view:
  who makes this design).
- **D. B plus `Loadout` by `Hull`.**
- **Recommendation: B.** It recovers everything the legacy window grouped, puts the
  manufacturer view where the manufacturer now is, and gives the weapon list its own axis.
  C is the better view if the operator authors products per design rather than per faction.
  Product record counts are unprobed; Cut 4's Hands reports them.

## Defaults (recorded, not asked)

- **Labels:** enum `Enum.GetName`, else the number; ref `RecordLabel` of the candidate with that
  key (for `Faction`, `Name`, not legacy `ShortName`); unset ref `None`; dangling `Missing <key>`;
  string its value or `(empty)`; integer invariant string; bool `False`/`True`.
- **Order within a level:** enums by underlying value; refs `None` first, then label
  (`OrdinalIgnoreCase`, then key), `Missing` last; strings `OrdinalIgnoreCase` then ordinal;
  integers numeric; `false` before `true`.
- **Groupable kinds:** String, Integer, Bool, Enum (not `[Flags]`), RecordRef. Refused with a
  notice and a flat list: any other kind (Float included), a `[Flags]` enum, `Hidden`,
  `IsReadOnly`, a name that is not an inspector member of the listed type, a name given twice.
- **Global types are never grouped.**
- **Foldouts:** window memory, collapsed by default, cleared by `CloseStore`. Create-in-group,
  `Add` and `Duplicate` expand the path to the new selection. No `EditorPrefs`/`SessionState`.
- **Create into a `Missing <key>` node** presets the dangling key (keeps I5).
- **A drop of the current key is rejected** (no no-op commit).
- **Variants:** see Q6's defaults and I10.

## Sequence with document variants

| Cut | Files | Overlaps grouping? |
|---|---|---|
| Variants C2a (element ids, in flight) | `CultCache.cs`, new `CultElementIds.cs`, Mesh/Geometry documents, `ElementIdTests.cs` | no |
| Index cut (`[CultIndex]` means unique; queued behind C2a) | `CultCache.cs`, Mesh/Geometry | no |
| Variants C2b (nested paths; inferred from the brief, not yet written in the variants map) | `CultCache.cs`, `CultVariants.cs` | no |
| **Grouping Cuts 1-2** | `CultInspectorModel.cs`, `CultInspectorAttributes.cs`, `CultInspectorModelTests.cs`, `CultCacheStudioWindow.cs`, `CultCacheStudioDrawers.cs`, Studio README | — |
| Variants C3 (Studio edits the delta) | `CultInspectorModel.cs` (edit, commit, diff, `CreateVariant`, `Rebase`), window (variant controls beside `Duplicate`, row marker), drawers (per-member marker and revert), `CultInspectorModelTests.cs` | **yes: all five grouping files** |

**Order:** grouping Cuts 1-2 now, in parallel with C2a, the index cut and C2b (no shared
files). Grouping merges to `main` before C3's Hands starts; C3 needs C2b anyway. C3 then
rebases onto grouping and lowers its row marker inside the grouped row drawing that Cut 2
writes. Then **one release (Cut 3)**, then one Aetheria pass (variants C4 plus Cut 4).

Where they touch: `DrawRecords` rows (`CultCacheStudioWindow.cs:180-185`; grouping rewrites,
C3 marks); the toolbar (`:142-157`; C3 adds variant buttons beside `Duplicate`, grouping
leaves it alone); `DrawRecordRef` (grouping adds the drop target; C3 marks at the member
level in `DrawValue`, not inside it); `CultInspectorModel.cs` (disjoint methods);
`CultInspectorModelTests.cs` (disjoint fixtures; name grouping fixtures `Group*`).

**One release commit:** Cut 3 below is the single release for both campaigns. The variants
map's C3 line "Then tag `caching-unity-v1.5.0`" should point here instead of owning a second
release step (proposal for Self).

## Cuts

Hands for Cuts 1-2: branch `hands/studio-grouping` in a fresh worktree off `origin/main`
(Self picks the path, e.g. `C:/wsNN-grouping`). `F:\Projects\CultLib-studio-grouping` holds the
superseded docs-only `codex/studio-grouping`; do not reuse it. Do not touch the main checkout.
Mutation testing is Stryker.NET on the cut's diff; there are no hand-applied mutations.

### Cut 1. CultLib model: declaration, tree, candidate and label authority (~150k tokens)

- **Delete and collapse first:**
  - `CultInspectorModel.cs:519`: the inline `target.IsInstanceOfType(record.Document) &&
    record.Key.Value.Length > 0` in `RecordCandidates` (`:514-522`) moves into
    `IsRecordCandidate`; `RecordCandidates` filters through it (I6).
  - `:36` and `:209`: `member.GetCustomAttributes(true)` → `Attribute.GetCustomAttributes(member, true)` (F5).
- **Add in `CultInspectorAttributes.cs`**, before `CultInspectorDrawerAttribute` (`:81-86`):
  `CultInspectorGroupByAttribute(params string[] members)`, `AttributeUsage(Class,
  Inherited = true, AllowMultiple = false)`, property `Members`, with a comment in the file's
  style stating nesting, nearest-wins and opt-out.
- **Add in `CultInspectorModel.cs`** (or a sibling `CultInspectorGrouping.cs`, same namespace;
  one file while it stays under ~800 lines; it is 645 now):
  - `CultInspectorGrouping { Type ListedType; IReadOnlyList<CultInspectorMember> Members; string? Notice }`
    (members empty when `Notice` is set).
  - `CultInspectorRecordGroup { string Id; string Label; int Depth; IReadOnlyList<object?> Values;
    IReadOnlyList<CultInspectorRecordGroup> Children; IReadOnlyList<CultStoredDocument> Records; int Count }`.
  - `GroupingOf(Type listed)`, cached like `ShapeOf` (`:324-333`): names resolve against
    `MembersOf(listed)` (`:335-339`); validation per Defaults.
  - `GroupRecords(Type listed, IEnumerable<CultStoredDocument> records)`: partitions
    `RecordCandidates(CultRecordRef<listed>)` by value identity, reading each record's
    `Document` (resolved, I10).
  - `CreateInGroup(Type listed, CultInspectorRecordGroup group, out string? notice)`:
    `CreateElement(listed, listed, out notice)` (`:387-398`), then `SetValue` each level; refs
    via `CreateRecordRef` (`:524-529`). `ArgumentException` for a node not of `listed`'s current
    grouping.
  - `IsRecordCandidate(Type refType, CultStoredDocument record)`.
  - `RecordRefLabel(Type refType, object? value, IEnumerable<CultStoredDocument> records)`: `None`,
    `RecordLabel` (`:315-319`), or `Missing <key>`.
- **Tests** in `tests/GameCult.Caching.Tests/CultInspectorModelTests.cs` (476 lines; fixtures
  `:299-476`). New fixtures `GroupBase` (abstract) / `GroupLeaf : GroupBase` `[CultDocument]`,
  a re-declaring type and an opting-out type; members: enum declared out of alphabetical
  order, `CultRecordRef<InspectOther>`, string, float, `[Flags]` enum, hidden int, read-only int.
  No object lists (F15). Behaviours, one test each:
  1. levels nest in declared order, not slot order;
  2. a `GroupLeaf` listed under `GroupBase` groups by the base declaration (I3);
  3. nearest declaration wins; an empty declaration opts out;
  4. leaves partition the candidates over unset refs, dangling refs and undefined enum values (I2);
  5. two refs sharing one `[CultName]` give two nodes; `None` first; `Missing k` last (I4, I7);
  6. enum nodes order by value;
  7. create at depth 2, upsert into a temp single-file `CultCache`, regroup: same node id (I5);
  8. each invalid declaration (unknown, float, `[Flags]`, hidden, read-only, duplicate) gives a
     notice and a flat tree, no throw (I8);
  9. `IsRecordCandidate` accepts a subtype, refuses a sibling and an empty key, and
     `RecordCandidates` equals the records it accepts (I6);
  10. a base abstract property's `[CultInspectorLabel]` reaches the override's metadata (F5);
  11. **a variant groups by its resolved value**: base with `Enum=A`, variant overriding it to
      `B` via `UpsertVariantAsync` and `Override`, regroup: the variant is in `B`; change the
      base to `C` with the variant not overriding it, regroup: it follows to `C` (I10).
- **Verify (Yggdrasil):**
  `ygg-verify.sh /f/Projects/CultLib <rev> dotnet 'dotnet test tests/GameCult.Caching.Tests -c Release'`,
  then
  `ygg-verify.sh /f/Projects/CultLib <rev> dotnet 'dotnet tool install -g dotnet-stryker && export PATH="$PATH:$HOME/.dotnet/tools" && cd tests/GameCult.Caching.Tests && dotnet stryker --project GameCult.Caching.csproj --since:<base-sha> > /tmp/s.log 2>&1; rc=$?; tail -60 /tmp/s.log; exit $rc'`.
  Survivors triaged by name in the report.
- **Ledger:** model ~+170, attributes ~+15, tests ~+230; one predicate collapsed.

### Cut 2. CultLib Studio: lower the tree, create in group, drag source, drop target (~150k tokens)

- **Delete first:**
  - `CultCacheStudioDrawers.cs:266` inline `"Missing " + key : "None"` →
    `Model.RecordRefLabel(type, value, Records)` for `names[0]` when `index == 0`.
  - `CultCacheStudioWindow.cs:180-185` flat `foreach` → tree drawing.
- **Window** (`CultCacheStudioWindow.cs`):
  - fields (`:13-30`): `Dictionary<string,bool> _groupFoldouts` (ordinal), `const string RecordDragKey`;
    `CloseStore` (`:318-327`) clears them.
  - `DrawRecords` (`:135-189`): `GroupingOf(_selectedType)`; a `Notice` shows a warning
    `HelpBox`. Draw `GroupRecords(_selectedType, _records)` recursively: foldout `Label (Count)`
    indented by `Depth`; an expanded node draws children, leaf rows, then a `Create` mini
    button under `Add`'s gate (`:141-146`). The root is not a foldout; `Add` stays root create.
  - row: reserve the rect with `GUILayoutUtility.GetRect`, handle `MouseDrag` there first
    (`PrepareStartDrag`, `SetGenericData(RecordDragKey, new CultCacheStudioRecordDrag(_model, key))`,
    empty `objectReferences`, `StartDrag(label)`, `Use()`), then `GUI.Toggle(rect, ...)` (F11).
    Keep the existing `<Type>` suffix logic (`:182-183`) in one place for C3 to extend.
  - `AddInGroup(node)` mirrors `Add` (`:340-346`) through `Run` (`:369`); selects the key and
    expands the node's id prefixes. `Add`/`Duplicate` reveal the new record's path (a
    `_revealKey` resolved next frame is acceptable).
- **Drawers** (`CultCacheStudioDrawers.cs`): `internal sealed class CultCacheStudioRecordDrag
  { Model; Key }`. In `DrawRecordRef` (`:254-280`), capture the `HorizontalScope` rect; on
  `DragUpdated`/`DragPerform` inside it with our payload: `Copy` when `GUI.enabled`, same
  `Model`, key differs, record in `Records`, and `IsRecordCandidate`; else `Rejected`. Leave
  foreign drags alone. On perform: `AcceptDrag`, `next = key`, `GUI.changed = true`, `Use()`;
  return through the existing `:279`.
- **README** (`src/GameCult.Unity/Assets/Caching/README.md`): grouping, create in group and
  drag-to-reference after the paragraph at `:30-34`; the grouping API, `IsRecordCandidate`,
  `RecordRefLabel` under `## Inspection Model` (`:39`).
- **Verify:**
  - Cut 1's Yggdrasil test command still passes.
  - Negative greps (each empty):
    `rg -n "IsInstanceOfType" src/GameCult.Unity/Assets/Caching/Editor | rg -v "assetType.IsInstanceOfType"` (I6; `:215` is the asset-GUID sub-asset check, not candidacy);
    `rg -n "\"Missing \"" src/GameCult.Unity/Assets/Caching/Editor` (I7; the union drawer's
    `"None"` at `:393` is not a ref label, so grep `Missing` only);
    `rg -n "SetValue|GetCustomAttribute|CultInspectorGroupBy" src/GameCult.Unity/Assets/Caching/Editor/CultCacheStudioWindow.cs` (I1);
    `rg -n "EditorPrefs" src/GameCult.Unity/Assets/Caching/Editor` shows only `LastPathKey`.
  - **Starfire, one job, editor closed:** the F10 compile gate, `dotnet build` of a scratch
    netstandard2.1 project over the worktree's `Assets/Caching/Editor/*.cs`. 0 errors.
- **Ledger:** window ~+80, drawers ~+30, README ~+20; two inline decisions deleted.

### Cut 3. Joint release: cultlib Unity 1.0.61, Studio 1.5.0 (after grouping and variants C3 merge)

- **Base:** `main` with grouping Cuts 1-2 and variants C1-C3 merged; a clean worktree.
- **Files:** `unity/org.gamecult.cultlib/package.json:4` `1.0.60` → `1.0.61`;
  `src/GameCult.Unity/Assets/Caching/package.json:4` `1.4.0` → `1.5.0` and `:13` dependency
  `1.0.61`; `CHANGELOG.md` entries in both packages (grouping, create in group, drag to
  reference; variant editing from C3); plugins via
  `powershell -File scripts\build-unity-package.ps1 -UpdateTemplate` (Windows: MSVC and the
  pinned MsQuic digests; its version check is at `:168-170`).
- **Tags:** `cultlib-unity-v1.0.61`, `caching-unity-v1.5.0`; no `cultmath-unity` tag.
- **Verify (Starfire, one job at a time):**
  - byte check: rerun the script; `git status --porcelain unity/` is empty;
  - expected diff: `GameCult.Caching.dll/.pdb` changes, and `gamecult_mesh_quic_native.dll`
    changes (F16); `msquic.dll` should not (same pinned digest). Anything else stops the release;
  - API present: reflect over the tracked `GameCult.Caching.dll` for
    `CultInspectorGroupByAttribute`, `GroupRecords`, `CreateInGroup`, `IsRecordCandidate`,
    `RecordRefLabel`, and C3's `CreateVariant`;
  - native ABI: `dumpbin /exports` lists `cultmesh_quic_open`, `_state`, `_poll`, `_error`, `_close`;
  - `git ls-remote --tags origin` lists both tags.

### Cut 4. Aetheria: grouping annotations (inside variants C4's pass; blocked on Q7)

- **Where:** the Aetheria branch and worktree Self opens for variants C4. Never
  `F:\Projects\Aetheria`'s working tree. C4 owns the pin bump (`Directory.Build.props:6`, and
  `Packages/manifest.json:54-55` to `#caching-unity-v1.5.0` / `#cultlib-unity-v1.0.61`;
  `:56` cultmath unchanged; `packages-lock.json` follows) and the element-id sweep (F18). This
  cut adds only:
  - Q7's `[CultInspectorGroupBy(...)]` class attributes (recommendation B: five declarations);
  - `tests/Aetheria.Shared.Tests/StudioGroupingTests.cs` (xUnit): `GroupingOf` over
    `CultCacheMessagePack.CreateInspectorModel(registry)` names exactly the declared members
    per annotated type with no notice; `GroupingOf(typeof(WeaponItemData))` follows Q7; every
    registered document type's `GroupingOf` has a null notice.
- **Verify:**
  - headless (Starfire, one job; the revision check needs `CultLibRoot` clean at the release
    SHA and `CultMathRoot` clean at `6d5e209`):
    `dotnet test tests\Aetheria.Shared.Tests -p:CultLibRoot=<clean worktree at release SHA> -p:CultMathRoot=<clean worktree at 6d5e209>`;
  - Unity 6000.3.24f1 batchmode compile, editor closed:
    `-batchmode -nographics -quit -projectPath <Aetheria worktree> -logFile <scratch>`; 0 errors,
    both new tags resolved.
- **Operator only, in Aetheria's Studio against a copy of `GameData/Aetheria.cc`:**
  - each annotated type shows the declared levels; `GearData` includes `<WeaponItemData>` rows;
  - create in a node: the record lands there and is selected;
  - drag a `Faction` onto a `FactionProductData.Manufacturer`: accepted; drag a gear record onto
    it: rejected;
  - drag a `PersonalityAttribute` onto a `DemandProfile` key that already exists: refused
    through the notice;
  - read-only open: every drop rejected;
  - a variant (once C4 authors one, e.g. `LonginusX`) sits in its resolved `HullType` node,
    and a drop into its inspector writes an override (C3), not a refusal;
  - close without edits: store bytes unchanged.

## Subtraction ledger

Added: one attribute, two model types, five model methods, ~11 CultLib and ~3 Aetheria tests,
~110 Studio lines, five annotations. Collapsed: the candidate predicate (now one method, reused
by drop), the ref label strings, the property-inheritance miss (F5). Not added: selectors,
registries, `EditorPrefs` keys, runtime ports, packages, targets, daemons, a second release.
Build footprint: `GameCult.Caching` and its tests (Yggdrasil); the F10 compile gate, the
package script (all tracked plugins including the native bridge) and Aetheria's headless and
batchmode checks (Starfire; Windows is the target for the editor plugin and win-x64 bridge).

## What the 2026-09-17 map had wrong as of `feeced8`

- Cut 4 grouped `GearData` by `Manufacturer`; `ItemData.Manufacturer` no longer exists (F17).
- Q5's native-plugin drift was spent by the 1.0.60 release (F16); the Cut 3 "rebase onto
  `codex/asset-guid-attribute`" note is obsolete (merged).
- Cut 4 re-pinned Aetheria on its own; after C2a it cannot, without the id sweep (F18).
- Aetheria branch `codex/cultcache-cutover`, props `:5`, manifest `:53-55`, cultmath `v0.2.3`: now
  `codex/fire-control-12`, `:6`, `:54-56`, `v0.2.4` with a separate CultMath pin.
- Negative greps for `IsInstanceOfType` and `"None"` would fail on unrelated lines (`:215`, `:393`).
- Hand-applied mutations per test are retired; Stryker.NET on the diff replaces them.
- Moved anchors: `RecordCandidates` `:512-520`→`:514-522`; `_shapes` `:298`→`:296`; `RecordLabel`
  `:313-317`→`:315-319`; `CreateElement` `:385-396`→`:387-398`; drawer attribute `:80`→`:81-86`;
  `DrawRecordRef` `:195-221`→`:254-280`, label `:207`→`:266`; `ReplaceKey` routing
  `:243-251`→`:303-309`; test fixtures `:380-459`→`:299-476`; README `:36-39`→`:30-39`; build-script
  version check `:150-155`→`:168-170`.
- Worktree `F:\Projects\CultLib-studio-grouping` is now the superseded docs branch; Hands uses a
  fresh one.
