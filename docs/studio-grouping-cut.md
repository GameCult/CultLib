# Studio Record Grouping And Reference Drag-Drop Cut

Date: 2026-09-17 (Imagination pass)

Status: cut map, forks open. The operator rules on forks Q1-Q5 before Hands
starts. Where a line depends on a fork, it names the fork and follows the
recommendation. Line references are against CultLib `122c217` and Aetheria
`515859cf` unless marked otherwise.

CultLib `main` moved during this pass, from `122c217` to `1845301`. The three
new commits touch only `native/GameCult.Mesh.Quic.Native`,
`scripts/mutate-cultmesh.mjs` and a QUIC doc. They were live uncommitted edits
in the main checkout when the pass began. The Q5 audit extends to them
unchanged. Another agent is actively working on `main`, so Hands works in its
own worktree.

## Target

### Ends

1. **Declared record grouping.** An attribute on a document type's model
   declares that the type's records group by one or more member values. Studio's
   record list becomes a foldout tree for that type. Tree levels nest in the
   declared order. A declaration on a base type groups the records of every
   derived type listed under it.
2. **Create in a group.** Every group node offers `Create`. The new record gets
   every grouped member on the path to that node set to that node's value, so
   it appears in that node.
3. **Reference drag and drop.** You can drag a record row from Studio's record
   list onto any `CultRecordRef<T>` value the inspector draws (a member, a list
   element, a dictionary key or value). The drop is accepted exactly when the
   record is one the ref's picker would offer.

Recovered from the legacy "Database Tools" window (Aetheria `d3db1730`,
`Assets/Scripts/CultCache/Editor/DatabaseView.cs`, `AetheriaDatabaseView.cs`,
`Inspectors/DatabaseLinkInspector.cs`). Declaring grouping by selector lambdas
is rejected by operator ruling (2026-09-17): use attributes.

### Invariants

- **I1. Model owns grouping.** `CultInspectorModel` alone decides:
  - which members group a listed type, and in what order;
  - which declarations are invalid, and the notice for each;
  - the tree's partition, node identity, node order and node labels;
  - the object that create-in-group makes.
  The Studio lowers the tree and never reads the attribute, calls
  `SetValue` for a preset, or computes a label.
- **I2. The leaves partition the candidates.** Across all leaves, the tree's
  records are exactly `RecordCandidates(CultRecordRef<listed>, records)`. Each
  record appears once, and records keep candidate order inside a leaf. Grouping
  never hides a record, including one whose grouped value is unset, dangling,
  or an undefined enum value.
- **I3. The listed type owns the tree's shape.** Grouping comes from the type
  selected in the type pane, never from each record's runtime type. A
  `WeaponItemData` listed under `GearData` is grouped by `GearData`'s
  declaration.
- **I4. Node identity is value identity, not the label.**
  - A `CultRecordRef` groups by key.
  - An enum groups by its underlying value.
  - Other kinds group by the invariant string of the value.
  So two factions with the same label are two nodes, and foldout state keys on
  the node id.
- **I5. Create-in-group round-trips.** Upsert the object that
  `CreateInGroup(listed, node)` returns, then regroup: the new record lies in a
  node with the same id.
- **I6. One candidate rule.** `IsRecordCandidate(refType, record)` is the only
  test for "this record may be this ref's value". `RecordCandidates` filters
  through it, and drop acceptance calls it. No `IsInstanceOfType` appears in
  `src/GameCult.Unity`.
- **I7. One ref label.** The model owns the strings `None` and `Missing <key>`.
  The picker (`DrawRecordRef`) and group labels both use it.
- **I8. Invalid declarations never break the window.** An invalid declaration
  gives a flat list plus the model's notice. It never throws in `OnGUI`.
- **I9. A drop honours the same gates as the picker.** A drop is refused:
  - in a disabled (read-only) scope;
  - when the payload came from another store's model;
  - when the key equals the current key.
  A dictionary key drop still goes through `ReplaceKey` refusal, because
  `DrawDictionary` already routes every key change there
  (`CultCacheStudioDrawers.cs:243-251`).

### Non-consumers and non-goals

- **Sibling runtimes.** There is no port of the inspector model or the
  inspector attributes.
  - `packages/cultcache-ts/src/cult-cache-inspector.ts` is a raw
    `.cc` byte and catalog dumper (`inspectCultCacheBytes`). It has no members,
    metadata, candidates or grouping.
  - Rust and Python have neither.
  No parity work is needed.
- **Wire and catalog.** Attributes are metadata only. They change no schema id,
  catalog entry or store byte.
- **Runtime CultUI panel.** This cut adds none. The model's API is shaped so one
  could lower it.
- **Not in scope:** record search (Q4), grouping in the ref picker popup (Q3),
  drop-to-append on list headers (the legacy `DatabaseLinkListInspector`), and
  the legacy "entry name `New <Type>`" on create.

## Body findings that shape the cut

Established by reading and by probes in scratch (`probe/`, `studio-compile/`):

- **F1. Legacy grouping by gear hardpoint read a derived property.**
  - Legacy `GearData` grouping read `HardpointType` and wrote `Hardpoint`.
  - `EquippableItemData.HardpointType` (`ItemData.cs:401-402`) is an abstract
    `[IgnoreMember]` property. `GearData.HardpointType => Hardpoint`
    (`:455`), `CargoBayData` returns `Tool` (`:464`) and `HullData` returns
    `Hull` (`:533`).
  - The stored member is `GearData.Hardpoint` (slot 23, `:453`).
  - The model's member list for `GearData` and `WeaponItemData` includes
    `Hardpoint#23@GearData`, confirmed by probe. `HardpointType` is not an
    inspector member.
  - The declaration names `Hardpoint`. The overriding types have no stored
    hardpoint, so they have nothing to group.
- **F2. `Manufacturer` is declared on the abstract `ItemData`** (`ItemData.cs:281`),
  not on `GearData`. Probe of `GameData/Aetheria.cc` (a copy):

  | Listed type | Records | Manufacturer set | Manufacturer unset |
  |---|---|---|---|
  | `GearData` (includes `WeaponItemData`) | 43 | 29 | 14 |
  | `HullData` | 3 | 3 | 0 |
  | `CargoBayData` (includes `DockingBayData`) | 5 | 5 | 0 |
  | `SimpleCommodityData` | 13 | 0 | 13 |
  | `CompoundCommodityData` | 51 | 0 | 51 |

  `GearData` has 11 distinct manufacturers. A member attribute on
  `ItemData.Manufacturer` would therefore also wrap all 64 commodity records in
  one "None" level. Legacy grouped only `GearData` by manufacturer. This drives
  Q1.
- **F3. Legacy grouping matched the exact type** (`CanGroup: typeof(T) == type`),
  and its tables listed exact types. So it never grouped `WeaponItemData`
  records. Grouping derived records under a base declaration is new behaviour,
  as the brief asks.
- **F4. Legacy create-in-group was dead.** `DatabaseEntryGroup<T,K>.Activate`
  (`DatabaseView.cs:70-75`) throws after invoking. Legacy groups also existed
  only when occupied (`GroupBy`), and foldouts keyed on the label's hash, so
  groups with the same label collapsed together.
- **F5. Existing metadata misses inherited attributes on overriding properties.**
  - `CultInspectorMetadata` reads `member.GetCustomAttributes(true)`
    (`CultInspectorModel.cs:36`), and `CultInspectorDrawerClaims.Resolve` reads
    it too (`:209`).
  - For a `PropertyInfo`, that overload ignores `inherit`. Probe: a base
    `[M] abstract int P` is not seen on the override.
    `Attribute.GetCustomAttributes(member, true)` does see it.
  - Fields are unaffected. This is a latent defect in every `Inherited = true`
    inspector attribute.
- **F6. Studio `Add` creates only the listed type**
  (`CultCacheStudioWindow.cs:340-347`: `CreateElement(type, type)`), and is
  disabled when the model cannot create it (`:141`).
  - Create-in-group therefore never makes a derived type, and every grouped
    member exists on the created object.
  - "A preset value is invalid for the created derived type" cannot arise, so it
    is recorded as a default, not a fork.
  - An abstract listed type offers no `Create`.
- **F7. Studio search filters the type pane only** (`:112-120`). No record
  search exists.
- **F8. Studio foldouts are not persisted.** The inspector keeps foldouts in an
  in-memory dictionary (`CultCacheStudioDrawers.cs:33`). The window closes its
  store on `OnDisable` (`:48-51`), which includes a domain reload. The only
  `EditorPrefs` key is `LastPathKey` (`:13`).
- **F9. No drawer claims `CultRecordRef<>`.** This was checked in CultLib Studio
  and in Aetheria `HEAD:Assets/Scripts/Editor/CultCacheDrawers.cs`. So every
  ref value reaches `DrawRecordRef` (`:195-221`), and the drop target added
  there covers all of them. A future claimed ref drawer would bypass it, which
  is acceptable: a claim owns its widget.
- **F10. The Studio editor sources compile outside Unity.**
  - Probe: a netstandard2.1 / C# 9 project compiles
    `src/GameCult.Unity/Assets/Caching/Editor/*.cs` with 0 errors against:
    - `Editor/Data/Managed/UnityEngine/*.dll` from Unity 6000.3.24f1 (the
      `UnityEditor.dll` facade must be left out, because it duplicates the
      module types);
    - the tracked `GameCult.Caching*.dll` and `MessagePack*.dll`.
  - This gives a pre-tag compile gate. The last migration only had
    post-release Aetheria batchmode.
- **F11. The Studio source row reads its click in `GUILayout.Toggle`**
  (`:184`). IMGUI button-like controls take `MouseDrag` while they are the hot
  control, so the drag start must be handled on a reserved rect *before* the
  toggle draws. This comes from Unity's IMGUI convention and was not probed
  here; the operator click-through proves it.

## Identity, lifecycle and authority

| Thing | Identity | Lifecycle | Owner (decides) | Readers | Persisted? |
|---|---|---|---|---|---|
| Grouping declaration | Attribute on a document class (Q1-B) or member (Q1-A), in the consumer's engine-free assembly | Compile time; inherited per `AttributeUsage` | Consumer source (Aetheria `ServerShared`) | `CultInspectorModel` only | Source code; no store bytes |
| Resolved grouping (`CultInspectorGrouping`) | Listed `Type` | Cached per model instance, like `_shapes` (`CultInspectorModel.cs:298-299`); dies with the model at store close | `CultInspectorModel` | Studio window | No |
| Group tree (`CultInspectorRecordGroup`) | Root per (listed type, records snapshot) | Rebuilt each `OnGUI`, as `RecordCandidates` already is (`CultCacheStudioWindow.cs:167`) | `CultInspectorModel` | Studio window | No |
| Node id | `<listed schema name>` + `/` + per-level value identity (I4) | Stable while the values exist | `CultInspectorModel` | Studio foldout map | No |
| Node label | Enum name, `RecordLabel`, `None`, `Missing <key>`, invariant string, `(empty)` | Recomputed with the tree | `CultInspectorModel` | Studio | No |
| Group foldout state | Node id → bool | Window instance memory; cleared by `CloseStore`; lost on domain reload, which already closes the store (F8) | `CultCacheStudioWindow` | Same | No (default; see Defaults) |
| Invalid-declaration notice | Listed `Type` | With the resolved grouping | `CultInspectorModel` | Studio shows a warning HelpBox above the list | No |
| Created-in-group object | New document | Made by the model, upserted by the window through `_cache.UpsertAsync` exactly as `Add` does | Model makes it; `CultCache` admits it | Window selects the key and expands its path | Store, on `Save` |
| Drag payload | `CultCacheStudioRecordDrag { CultInspectorModel Model; string Key }` under generic data key `"GameCult.CultCacheStudio.Record"` | One drag gesture (Unity `DragAndDrop`) | Studio record list (source) | `CultInspector.DrawRecordRef` (target) | No |
| Drop acceptance | (ref type, record) | Per `DragUpdated` / `DragPerform` | `CultInspectorModel.IsRecordCandidate` plus the I9 gates | Studio | No |

No persistent state is introduced. No cell is empty.

## Forks

### Q1. Where does the grouping declaration live, and how is nesting ordered? (blocks Cuts 1, 4)

- **A. Member attribute** `[CultInspectorGroup(int order = 0)]` on a field or
  property.
  - Levels are ordered by `order`, then by the member's display order
    (`Metadata.Order ?? Slot`).
  - Inheritance comes free.
  - The member can't be scoped: on `ItemData.Manufacturer`, it groups every item
    type by manufacturer, and all 64 commodity records sit under one `None`
    level (F2).
  - Ordering spreads across files: `Hardpoint` on `GearData` with `order: 0`,
    `Manufacturer` on `ItemData` with `order: 1`.
- **B. Type attribute** `[CultInspectorGroupBy(nameof(Hardpoint), nameof(Manufacturer))]`
  on a document class. `AttributeUsage(Class, Inherited = true,
  AllowMultiple = false)`.
  - The argument order is the nesting order.
  - The nearest declaration wins, so a derived type re-declares, and
    `[CultInspectorGroupBy]` with no arguments opts out.
  - Names resolve against the listed type's inspector members, so inherited
    members work.
  - `nameof` keeps it refactor-safe, and it names data rather than code.
  - It scopes `Manufacturer` to `GearData` exactly as legacy did.
- **C. Both forms.** Twice the surface, two ordering rules.
- **Recommendation: B.**
  - It is the only form that recovers the legacy grouping without the
    commodity regression.
  - Its nesting order sits in one place, readable.
  - It is still an attribute, not a selector.
  - It departs from the brief's "member attribute" working name; that is why
    this is asked.
- **Depends on it:**
  - Cut 1's attribute type and its resolution tests.
  - Cut 4's annotations: under B, four class attributes; under A, four member
    attributes plus accepting F2.

### Q2. Which group nodes exist? (blocks Cut 1)

- **A. Occupied values only** (legacy parity, F4).
  - Create into an empty category means creating at the root and then editing
    the member.
- **B. Enums show every defined value, including empty nodes**, so
  create-in-group reaches empty categories. Other kinds show occupied values
  only.
  - Refs cannot enumerate every candidate record: 11+ factions per level times
    the enum levels is too many.
- **Recommendation: A.**
  - The trees stay proportional to the data.
  - The rule is the same for every kind.
  - B is a small later addition.
- **Depends on it:** the tree builder and one test.

### Q3. Does grouping also apply to the `CultRecordRef` picker popup? (blocks Cut 2 scope)

- **A. List only.**
- **B. Also the popup**, as `EditorGUILayout.Popup` submenus using `/` paths
  built from the ref target type's grouping.
  - A label containing `/` would split wrongly unless it is escaped.
  - A target type such as `EquippableItemData` has no declaration, so it still
    shows flat.
- **Recommendation: A.**
  - With drag-drop from the grouped list, the long-popup pain is gone.
  - B can reuse the model tree later without new model surface.

### Q4. Record search? (blocks nothing if A)

- **A. None in this cut.** The Studio has no record search today (F7).
- **B. A record search field** that filters tree leaves and auto-expands
  matching paths.
- **Recommendation: A.** It is a separate feature, and grouping does not need it.

### Q5. Release base and re-pin scope (blocks Cuts 3, 4)

`git log a0813c6..122c217` (68 commits) touches nothing under:

- `src/GameCult.Caching`
- `src/GameCult.Caching.MessagePack`
- `src/GameCult.Unity`
- `unity/`
- `packages/cultmath`

It touches:

- the native QUIC bridge, rewritten to a C ABI v2 (`6a091ef`). The v1 exports
  `cultmesh_quic_open/state/poll` remain at `cultmesh_quic_native.cpp:1360-1392`.
- OpenSSL MsQuic (`c729dff`).
- TS CultMesh/CultNet, the QUIC native tests, and docs and CI.
- `scripts/build-unity-package.ps1`, one message line.

The build script rebuilds and, with `-UpdateTemplate`, replaces
`unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/gamecult_mesh_quic_native.dll`
and `msquic.dll`. `19d8d5c` records that the committed native plugin is the
older 40,448-byte Schannel build and that current source builds to 302,080
bytes. So a release from main ships a new native bridge and msquic into
Aetheria's Unity package. Aetheria calls no QUIC (grep `Quic` in `Assets/Scripts`
hits only a KDTree file).

- **A. Release from main** after the feature merges, and accept the native
  plugin replacement.
  - Verify the rebuilt DLL still exports the three v1 entry points the managed
    `GameCult.Mesh.Quic.Native.dll` imports
    (`src/GameCult.Mesh.Quic.Native/*.cs:261-275`).
  - The build host needs MSVC and the pinned MsQuic digests. If the native
    build fails, stop and return to the operator.
- **B. Release from a branch off `a0813c6`** carrying only this feature. Tag it
  there and merge back to main.
  - The tags sit on a commit whose native plugin stays old.
  - The next main release takes the drift anyway, so B only defers it.
- **C. Release from main, but hand-restore the old native plugin** in the
  release commit.
  - The byte check then fails by construction. Rejected unless the operator
    wants it.
- **Recommendation: A.** The drift is committed, reviewed work that only the
  release step has held back. Aetheria does not load the bridge, and B leaves
  the committed plugin already known to be stale (`19d8d5c`).
- **Depends on it:** Cut 3's base, its verification list, and Cut 4's
  `CultLibRevision`.

## Defaults (recorded, not asked)

- **Group labels:**
  - An enum shows `Enum.GetName`. An undefined value shows its number.
  - A `CultRecordRef<T>` shows `RecordLabel` of the candidate with that key
    (the target's `[CultName]`), so for Faction that is `Name`, not the
    legacy `ShortName`.
  - An unset ref shows `None`, like the picker (`CultCacheStudioDrawers.cs:207`),
    replacing the legacy `Default`.
  - A key with no candidate shows `Missing <key>`, as the picker does.
  - A string shows its value, or `(empty)`.
  - An integer shows its invariant string.
  - A bool shows `False`/`True`.
- **Group order within a level:**
  - Enums sort by underlying value.
  - Refs sort `None` first, then by label (`OrdinalIgnoreCase`, then key),
    with `Missing` last.
  - Strings sort `OrdinalIgnoreCase`, then ordinal.
  - Integers sort numerically; bools sort false before true.
- **Groupable kinds:** String, Integer, Bool, Enum, RecordRef. A grouped member
  is refused with a notice and a flat list (I8) when:
  - it is of any other kind, including Float, whose bit-equality makes
    meaningless groups;
  - it is `Hidden`;
  - it is read-only (`IsReadOnly`, so a preset cannot assign it and an edit
    cannot move the record);
  - it is named but is not an inspector member of the listed type;
  - it is named twice.
- **Global types are never grouped.** They have one record.
- **Foldouts:**
  - Window memory, collapsed by default.
  - `CloseStore` clears them.
  - Create-in-group, `Add`, and `Duplicate` expand the path to the new
    selection, so the selection is always visible.
  - No `EditorPrefs` or `SessionState` (F8: a domain reload already closes the
    store).
- **Create-in-group into a `Missing <key>` node** presets the dangling key, which
  keeps I5. The record joins the node it was created in.
- **A drop of the current key is rejected** (legacy parity) so that no no-op
  commit occurs.
- **F5 is fixed in Cut 1.** The new attribute reads through `MetadataOf`, so
  that path must honour inheritance on properties; the fix is one call site
  in each of two places.

## Cuts

### Cut 1. CultLib model: declaration, grouping tree, candidate and label authority

- **Repo and branch:** CultLib `claude/studio-grouping`, in a new worktree
  `F:\Projects\CultLib-studio-grouping` from `origin/main`. The main checkout
  is in active use by the QUIC bridge work; do not use or touch it or the other
  worktrees.
- **Deletes and collapses first:**
  - `CultInspectorModel.cs:512-520` `RecordCandidates`: its inline predicate
    `target.IsInstanceOfType(record.Document) && record.Key.Value.Length > 0`
    moves into `IsRecordCandidate`, and `RecordCandidates` filters through it
    (I6).
  - `CultCacheStudioDrawers.cs:207` builds its `"Missing " + key` / `"None"`
    label inline. The label moves to the model (I7), and the Studio call site
    changes in Cut 2.
  - `CultInspectorModel.cs:36` and `:209` switch from `member.GetCustomAttributes(true)`
    to `Attribute.GetCustomAttributes(member, true)` (F5).
- **Adds in `CultInspectorAttributes.cs`:**
  - Q1-B: `CultInspectorGroupByAttribute(params string[] members)`, with
    `Class`, `Inherited = true`, `AllowMultiple = false`, and property
    `Members`.
  - Q1-A instead: `CultInspectorGroupAttribute(int order = 0)`, with
    `Field|Property`, `Inherited = true`.
  - Place it before `CultInspectorDrawerAttribute` (`:80`), with a comment in the
    file's style stating the nesting and inheritance rules.
- **Adds in `CultInspectorModel.cs`** (or a sibling `CultInspectorGrouping.cs`
  in the same namespace; Hands picks, and one file is preferred while it
  stays under ~800 lines):
  - `public sealed class CultInspectorGrouping`: `Type ListedType`,
    `IReadOnlyList<CultInspectorMember> Members` (outermost first), and
    `string? Notice`. When `Notice` is set, `Members` is empty.
  - `public sealed class CultInspectorRecordGroup`: `string Id`, `string Label`
    (empty at the root), `int Depth`, `IReadOnlyList<object?> Values` (root to
    here), `IReadOnlyList<CultInspectorRecordGroup> Children`,
    `IReadOnlyList<CultStoredDocument> Records` (non-empty only at leaves), and
    `int Count` (records beneath).
  - `CultInspectorModel.GroupingOf(Type listedType)`, cached like `ShapeOf`:
    - Global types, and types with no declaration, give empty members and no
      notice.
    - Q1-B: resolve names against `MembersOf(listedType)`.
    - Q1-A: take members carrying the attribute, ordered by (order, display
      order).
    - Validate the groupable kinds, hidden, read-only and duplicates (Defaults).
  - `CultInspectorModel.GroupRecords(Type listedType, IEnumerable<CultStoredDocument> records)`:
    - The input is `RecordCandidates(CultRecordRef<listedType>)`, partitioned
      level by level by value identity (I4).
    - Nodes follow the Defaults order and labels. Records stay in candidate
      order (I2).
    - With no grouping members, the root holds every record as one leaf.
  - `CultInspectorModel.CreateInGroup(Type listedType, CultInspectorRecordGroup group, out string? notice)`:
    - Call `CreateElement(listedType, listedType, out notice)`. On null, return
      null with that notice.
    - Otherwise `SetValue` each grouping member with the node's value. A ref is
      rebuilt with `CreateRecordRef(member type, key)`.
    - Refuse (`ArgumentException`) a node whose depth or id prefix does not
      belong to `listedType`'s current grouping.
  - `public bool IsRecordCandidate(Type recordRefType, CultStoredDocument record)`.
  - `public string RecordRefLabel(Type recordRefType, object? value, IEnumerable<CultStoredDocument> records)`:
    - `None` when unset.
    - Otherwise `RecordLabel` of the candidate with that key.
    - Otherwise `Missing <key>`.
- **Keeps:** `RecordLabel` (`:313-317`), `CreateElement` (`:385-396`), and every other API unchanged.
- **Tests** go in `tests/GameCult.Caching.Tests/CultInspectorModelTests.cs`,
  with new fixtures near `:380-459`. Fixtures:
  - an abstract base document class, and a derived `[CultDocument]` pair
    `GroupBase` / `GroupLeaf : GroupBase`;
  - an enum member, a `CultRecordRef<InspectOther>` member, a string, a float,
    a hidden int and a read-only int;
  - a type re-declaring (Q1-B) and a type opting out.

  Each test names the mutation that must turn it red:

  1. `GroupsNestInDeclaredOrder`. The level order equals the declaration, not
     slot order. *Mutation:* order members by slot, and the test fails.
  2. `BaseDeclarationGroupsDerivedRecords`. A `GroupLeaf` record listed under
     `GroupBase` is grouped by the base declaration (I3). *Mutation:* look up the
     attribute with `inherit: false`, or take the grouping from
     `record.Descriptor.DocumentType`.
  3. `NearestDeclarationWinsAndEmptyOptsOut` (Q1-B only). *Mutation:* merge base
     and derived name lists.
  4. `LeavesPartitionTheCandidates`. Over records with unset refs, dangling
     refs and undefined enum values, the concatenated leaves equal
     `RecordCandidates` as a multiset in order (I2). *Mutation:* skip records
     whose value is unset, or dedupe by label.
  5. `RefNodesAreKeyedByKeyAndLabelledByTheModel`. Two referenced records share
     one `[CultName]` and give two nodes. Unset gives `None`, first. A dangling
     key gives `Missing k`, last (I4, I7). *Mutation:* group by label.
  6. `EnumNodesOrderByValue`. Declare the enum out of alphabetical order.
     *Mutation:* order nodes by label.
  7. `CreateInGroupPresetsEveryLevelAndRegroupsInPlace`. Create at depth 2,
     upsert into a `CultCache` over a temp single-file store, and regroup: the
     new record's leaf id equals the node id (I5). *Mutation:* preset only the
     deepest level.
  8. `InvalidDeclarationsGiveANoticeAndAFlatTree`. Cover an unknown name, a
     float, a hidden member, a read-only member and a duplicate. Each gives a
     notice, and `GroupRecords` does not throw (I8). *Mutation:* remove any one
     validation branch.
  9. `IsRecordCandidateIsTheRecordCandidatesRule`. A subtype record is
     accepted; a sibling type and an empty key are refused; `RecordCandidates`
     equals the records filtered by it (I6). *Mutation:* use an exact-type
     comparison.
  10. `InheritedMetadataReachesOverridingProperties`. A base abstract property
      with `[CultInspectorLabel]` is seen on the override's member metadata
      (F5). *Mutation:* revert `:36`.
- **Verification:**
  - Run `dotnet test tests\GameCult.Caching.Tests\GameCult.Caching.Tests.csproj -c Release`
    in the worktree. All pass, and the count grows by the tests above.
  - For each mutation listed, apply it in the worktree, confirm its test fails,
    then revert. Record the mutation table in the landing commit message.
- **Ledger:**
  - Model: about +150 to 190 lines.
  - Attributes: about +15 lines.
  - Tests: about +200 lines.
  - Two predicate and label duplicates deleted (one moved, one
    collapsed in Cut 2).

### Cut 2. CultLib Studio: lower the tree, create in group, drag source, drop target

- **Repo and branch:** CultLib `claude/studio-grouping` (same worktree), after
  Cut 1.
- **Deletes first:**
  - `CultCacheStudioDrawers.cs:207`: the inline `"Missing " + key : "None"`
    becomes `Model.RecordRefLabel(type, value, Records)` for `names[0]` when
    `index == 0`.
  - `CultCacheStudioWindow.cs:180-185`: the flat `foreach` over records is
    replaced by tree drawing.
- **Changes in `CultCacheStudioWindow.cs`:**
  - Fields (`:13-30`): add `Dictionary<string,bool> _groupFoldouts`
    (`StringComparer.Ordinal`) and `const string RecordDragKey`.
    `CloseStore` (`:318-327`) clears the foldouts.
  - `DrawRecords` (`:135-190`):
    - Take `_model.GroupingOf(_selectedType)`. When its `Notice` is set, show a
      `HelpBox` Warning above the list.
    - Draw `_model.GroupRecords(_selectedType, _records)` recursively. A node
      is a foldout labelled `Label (Count)` and indented by `Depth`.
    - An expanded node draws its children, then its leaf records, then a
      `Create` mini button. The button is enabled under the same
      `cannotAdd || IsGlobal` gate as `Add` (`:141-147`) and calls
      `CreateInGroup`.
    - The root (depth 0) is not drawn as a foldout. `Add` stays the root create.
  - Record row:
    - Reserve its rect with `GUILayoutUtility.GetRect` before
      `GUI.Toggle(rect, ...)` (F11).
    - On `MouseDrag` inside the rect with a button held, set
      `DragAndDrop.PrepareStartDrag()`, then `SetGenericData(RecordDragKey,
      new CultCacheStudioRecordDrag(_model, key))`, set `objectReferences` to
      empty, call `StartDrag(label)`, and use the event.
  - `AddInGroup(node)`: mirrors `Add` (`:340-347`) through `Run`. It upserts
    `CreateInGroup`'s object (null means throw its notice), selects the key, and
    expands the node's id prefixes. `Add` and `Duplicate` expand the new
    record's path by looking up its leaf in the next tree; Hands may do that
    on the next frame through a `_revealKey` field.
- **Changes in `CultCacheStudioDrawers.cs`:**
  - Add `internal sealed class CultCacheStudioRecordDrag { Model; Key }`, or
    put it in the window file (Hands picks; one type, no interface).
  - `DrawRecordRef` (`:195-221`): capture the `HorizontalScope` rect.
    - On `DragUpdated` or `DragPerform` inside it, when `GUI.enabled` is true,
      the payload's `Model == Model`, the payload key is not the current key,
      the key's record is in `Records`, and `Model.IsRecordCandidate(type,
      record)` holds, set the visual mode to `Copy`. Otherwise set it to
      `Rejected` for our payload only (leave foreign drags untouched).
    - On perform: `AcceptDrag`, `next = key`, `GUI.changed = true`, and use
      the event.
    - Return through the existing `next == key ? value : CreateRecordRef`
      (`:220`).
- **README** (`src/GameCult.Unity/Assets/Caching/README.md`, after `:36-39`):
  - Grouping: the attribute, nesting, inheritance, labels, create in group,
    and the notice.
  - Drag a record onto a reference field.
  - Under "Inspection Model", the grouping API and `IsRecordCandidate` /
    `RecordRefLabel`.
- **Verification:**
  - Pre-tag compile gate (F10). Copy the scratch `studio.csproj` pattern:
    netstandard2.1, `LangVersion 9.0`, compiling the worktree's
    `Assets/Caching/Editor/*.cs`. Reference Unity 6000.3.24f1
    `Editor\Data\Managed\UnityEngine\*.dll` (not `UnityEditor.dll`) and the
    worktree's freshly built `src\GameCult.Caching` and
    `GameCult.Caching.MessagePack` outputs (`dotnet build -c Release`), plus
    the tracked `MessagePack*.dll`. Expect 0 errors. Keep the project in
    scratch, not the repo.
  - Negative greps in the worktree, each empty:
    - `rg -n "IsInstanceOfType" src/GameCult.Unity/Assets/Caching` (I6)
    - `rg -n "\"Missing \"|\"None\"" src/GameCult.Unity/Assets/Caching/Editor` (I7)
    - `rg -n "SetValue|GetCustomAttribute|CultInspectorGroup" src/GameCult.Unity/Assets/Caching/Editor/CultCacheStudioWindow.cs` (I1)
    - `rg -n "EditorPrefs" src/GameCult.Unity/Assets/Caching/Editor` shows only
      `LastPathKey`
  - Cut 1's tests still pass.
  - Operator click-through happens after Cut 4, in Aetheria against the tagged
    package, as in the migration's Cut 6 precedent. See Cut 4.
- **Ledger:** Window about +70 to 90 lines, drawers about +25 lines, README
  about +20 lines. Two inline decisions deleted.

### Cut 3. CultLib release: cultlib Unity 1.0.60, Studio 1.4.0

- **Repo and branch:** CultLib `main`. Merge `claude/studio-grouping`
  (fast-forward or merge commit), then create the release commit in the same
  clean worktree. Q5-A is assumed.
- **Files:**
  - `unity/org.gamecult.cultlib/package.json:4`: `1.0.59` → `1.0.60`.
  - `src/GameCult.Unity/Assets/Caching/package.json:4`: `1.3.1` → `1.4.0`,
    because this is new user-facing capability. `:13`: `"org.gamecult.cultlib": "1.0.60"`.
  - `unity/org.gamecult.cultlib/Runtime/Plugins/*.dll|pdb` and
    `x86_64/*.dll`, via
    `powershell -File scripts\build-unity-package.ps1 -UpdateTemplate`. The
    script derives the assembly version from the package.json and checks it
    (`:150-153`).
- **Tags on the release commit:** `cultlib-unity-v1.0.60` and
  `caching-unity-v1.4.0`. That is two tags in one push, under GitHub's
  three-tag workflow limit (migration cut map header). No `cultmath-unity` tag,
  because CultMath is unchanged.
- **Verification:**
  - **Byte check.** Run the build script again and check that
    `git status --porcelain unity/` is empty (a reproducible build). This is
    manual, as recorded in the migration's follow-ups.
  - **Expected diff.** `git diff --stat HEAD~1 -- unity/` lists
    `GameCult.Caching.dll/.pdb` changed. Under Q5-A,
    `x86_64/gamecult_mesh_quic_native.dll` (about 302 KB) and `msquic.dll` also
    change. An unexpected changed assembly stops the release for review.
  - **API present.** A throwaway scratch script loads the tracked
    `GameCult.Caching.dll` by reflection and confirms that
    `CultInspectorGroupByAttribute`, `CultInspectorModel.GroupRecords`,
    `CreateInGroup`, `IsRecordCandidate` and `RecordRefLabel` exist.
  - **Native ABI (Q5-A).** Run `dumpbin /exports` on the tracked
    `gamecult_mesh_quic_native.dll`. It lists `cultmesh_quic_open`, `_state`,
    `_poll`, `_error` and `_close`.
  - **Tags pushed.** `git ls-remote --tags origin` lists both tags.
- **Ledger:** Versions and binaries only.

### Cut 4. Aetheria: re-pin CultLib and annotate grouping

- **Repo and branch:** Aetheria `codex/cultcache-cutover`, the current branch.
  Stage by path only: `Assets/Scripts/Editor/CultCacheDrawers.cs` carries an
  unrelated uncommitted edit that must stay out of the commit and untouched.
- **Files:**
  - `Directory.Build.props:5` `CultLibRevision`: set it to the Cut 3 release
    commit's full SHA. Note that it lives in props, not targets; the check is in
    `Directory.Build.targets:2-22`.
  - `Packages/manifest.json:53`: `#caching-unity-v1.3.1` → `#caching-unity-v1.4.0`.
    `:54`: `#cultlib-unity-v1.0.59` → `#cultlib-unity-v1.0.60`. `:55`
    `cultmath-unity-v0.2.3` is unchanged. Unity rewrites
    `Packages/packages-lock.json`; commit that too.
  - `Assets/Scripts/ServerShared/ItemData.cs`, under Q1-B:
    - `SimpleCommodityData` (`:300`): `[CultInspectorGroupBy(nameof(Category))]`
    - `CompoundCommodityData` (`:330`): `[CultInspectorGroupBy(nameof(Category))]`
    - `GearData` (`:449`): `[CultInspectorGroupBy(nameof(Hardpoint), nameof(Manufacturer))]`.
      `WeaponItemData` inherits it.
    - `HullData` (`:493`): `[CultInspectorGroupBy(nameof(HullType))]`
  - Under Q1-A instead: `[CultInspectorGroup]` on `SimpleCommodityData.Category`
    (`:309`), `CompoundCommodityData.Category` (`:337`),
    `GearData.Hardpoint` (`:453`), `ItemData.Manufacturer` (`:281`, order 1)
    and `HullData.HullType` (`:503`). Commodity lists then gain the `None`
    manufacturer level (F2).
- **Tests** (`tests/Aetheria.Shared.Tests`, new `StudioGroupingTests.cs`, xUnit):
  - `AnnotatedCatalogTypesGroupAsDeclared`:
    - Build `CultCacheMessagePack.CreateInspectorModel` over the Aetheria
      registry, as the probe did with the `[CultDocument]` types of
      `typeof(GearData).Assembly`.
    - `GroupingOf` names exactly `Category`, `Category`,
      `Hardpoint, Manufacturer` and `HullType` for the four types, each with no
      notice.
    - `GroupingOf(typeof(WeaponItemData))` equals `GearData`'s.
    - *Mutation:* remove one annotation, or reorder `GearData`'s names.
  - `NoGroupedMemberIsRefused`. Every registered document type's `GroupingOf`
    has a null notice. This guards against a future annotation on a float or
    hidden member.
- **Verification:**
  - **Headless.** Run
    `dotnet test tests\Aetheria.Shared.Tests -p:CultLibRoot=F:\Projects\CultLib-studio-grouping`,
    with that worktree checked out clean at the release SHA. The revision check
    requires a clean root at exactly that SHA, and the main CultLib checkout is
    shared with live work. All pass.
  - **Unity batchmode compile.** With the editor closed, run
    `-batchmode -nographics -quit -projectPath F:\Projects\Aetheria -logFile <scratch>`
    on Unity 6000.3.24f1. Expect 0 compile errors, and the package cache
    resolving both new tags.
  - **Operator click-through** in Aetheria's Studio against
    `GameData/Aetheria.cc`:
    - `GearData` shows hardpoint then manufacturer. It includes the
      `WeaponItemData` records marked `<WeaponItemData>`, and a `None` node
      holds the 14 unset.
    - Commodities show categories only. `HullData` shows hull types.
    - Create in a gear node, then check that the record lands there and is
      selected.
    - Drag a faction onto a gear `Manufacturer` and see it accepted. Drag a
      gear record onto it and see it rejected.
    - Drag onto a `DemandProfile` dictionary key and see a duplicate refused
      through the notice.
    - In a read-only open, every drop is rejected.
    - Close without edits and confirm that the store bytes are unchanged.
  - **Negative grep.** `rg -n "HardpointType\)" Assets/Scripts/ServerShared/ItemData.cs`
    shows no grouping declaration names the derived property.
- **Ledger:** 4 attribute lines (Q1-B), the pin and manifest lines, and about
  +40 test lines.

## Subtraction ledger (whole change)

- **Added:**
  - One attribute type.
  - Two model types and five model methods.
  - About 10 CultLib tests and 2 Aetheria tests.
  - About 100 Studio lines.
  - 4 annotations.
- **Removed or collapsed:**
  - The candidate predicate duplicated at each future call site. It was
    inline once; now it is one method, reused by drop.
  - The ref label strings, duplicated between picker and grouping.
  - The latent attribute-inheritance miss on overriding properties (F5).
- **Not added:** selectors, registries, `EditorPrefs` keys, sibling-runtime
  ports, or new packages, targets or daemons.
- **Build footprint:**
  - `GameCult.Caching` and its tests.
  - The Unity package script, which rebuilds every tracked plugin including the
    native bridge (Q5).
  - Aetheria's headless shared build and one batchmode compile.
  - The build host is the Windows workstation, which is also the target
    platform for both the Unity editor plugin and the win-x64 native bridge.
