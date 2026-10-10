# CultCache Union Parity: Map

Campaign `cultlib-gaps`, follow-up `cultlib-gaps:follow_up:union-parity-and-unknown-arms` (from
`thing2:follow_up:cultcache-union-parity`), under operator ruling `thing2:ruling:contract-decode-tolerance`
("CultCache refuses unknown union arms in every runtime, the C# reference included"). This file holds body facts,
the model page and rationale; the cut specs and the question are documents in the mind and cite this file by
section and fact number.

Pinned base: `origin/main` at `aaf0d7f6` (2026-10-10). Imagination pass `imagination-cl-union-parity`, session
`self-2026-10-10c-eureka`. Probes live under the session scratchpad, `imagination-cl-union-parity/` (`cs` payload
probe, `cs2` store probe, `rs` payload probe, `py` store walkers, `il` decompiled MessagePack-CSharp). None of it is
committed. Earlier evidence is not repeated here: Eve `docs/thing2-map.md` P12-P13 (the cross-runtime probe) and
`docs/research/cultcache-union-decode-tolerance-prior-art.md` (prior art).

## Cut order

1. `union-refusal-cs`: the C# reference refuses an unknown union arm on read and a subtype the union does not name
   on write. Ruled; nothing blocks it.
2. `union-golden`: the recursive-union golden and the arm refusal goldens in `tests/vectors/union-parity/`,
   written by the C# reference and held by its tests.
3. `union-derive-rs` and `union-codec-ts`, in either order or together: cultcache-rs and cultcache-ts gain a
   union (and, in TS, a slot record) codec that writes the reference shape and decodes the golden byte-identically.
   `union-codec-py` follows the same golden for cultcache-py.
4. `slot-tolerance` (C# reference), then `slot-readonly-parity` (Rust, TypeScript, Python): a record carrying a
   slot past its reader's declared slots, at any depth, is read-only (ruling
   `cultlib-gaps:ruling:extra-trailing-slots`; the refinement is settled by section S). Both change a slot rule the
   cuts in step 3 build, so they wait for 1-3 rather than reopening them.
5. `union-parity-release`: changelogs and tags, Breaking where `docs/semver-policy.md` says so.

Thing2's `cut-contract-rs` waits on step 3 (Rust) only: its first step pins cultcache-rs at the commit that lands
`union-derive-rs` and re-runs P12 or the `union-golden` vector both ways.

Why this order: the C# reference is the wire authority, so its refusal and its golden come first and the other
runtimes are held to bytes C# wrote, as `document-variants-c1` was (`docs/runtime-parity-scope.md`). Arms are
ruled and slots are not, so the slot rule is its own cut rather than a blocked step inside every runtime's cut.

## Model page (step 0b)

| Kind | What names it | Over time | Who decides |
|---|---|---|---|
| Union arm | An integer key, `[Union(k, typeof(Arm))]` on the abstract base (C#), assigned by the union's author. The wire carries `[k, armSlots]`; the arm's type name never travels. `nil` is a null reference, not an arm. | **Added:** a key never used before. **Retired:** the attribute is removed or commented out and the key is never reused (Aetheria `Behaviors.cs:160,173`; Aetheria-legacy `Behaviors.cs:37-38`). **Renamed type:** free, the key is the identity. **Unknown to a reader** (a newer writer's arm, or a retired arm in an old store): refused, naming the union and the key (ruling). | The union's author assigns keys. The C# reference defines the shape; every runtime keeps parity with it (doctrine, `runtime-parity-scope.md`). |
| Arm subtype on write | The value's runtime type must be one the union names. | A subtype the union does not name is today written as `nil` (U4). After cut 1 it is refused. | The union's author, by naming the arm. |
| Arm slots, record slots | `[Key(n)]` integers, base-class keys included (an arm is a flat array). Gaps are written `nil`. | **Added:** appended at the next free key. **Retired:** kept as a gap, never reused (Aetheria `ItemInstance.cs:35`, `EntitySerializer.cs` key 5). **Missing in an older store:** the reader's default (C# field initializer; Rust and Python need a declared default). **Extra past the reader's last key, at any depth:** the record is read and is read-only; every write or removal of it is refused, and it is persisted from its original bytes (ruling, S). | The type's author. The slot rule is CultCache's (`src/GameCult.Caching/Contracts/cultcache-schema-compatibility.md`). |
| Serde-enum member (Rust) | serde's external tag: the variant **name** (`{"Name": fields}`, a unit variant the bare string). | Persisted already by Rust consumers (section C). They keep their bytes: the union derive is opt-in and serde enums are untouched. | The Rust consumer. Not a cross-runtime shape. |

## U. Mechanism facts (C# reference, Rust, TS)

- **U1. The miss branch is `Skip` then `null`, in both union formatters MessagePack-CSharp 3.1.7 can hand out.**
  `DynamicUnionResolver.BuildDeserialize` (decompiled from `MessagePack.dll` 3.1.7 net9.0 with ilspycmd): after
  `ReadArrayHeader() == 2` (else `InvalidOperationException "Invalid Union data was detected"`) and `ReadInt32`, a
  non-zero-start key set is mapped through `keyToJumpMap.TryGetValue`, a miss sets the index to `-1`, the `switch`
  default calls `reader.Skip()`, and the result local stays `null`. The source generator's `UnionTemplate`
  (`MessagePackAnalyzer` 3.1.7, `MessagePack.SourceGenerator.dll`) emits the same: `default: reader.Skip(); break;`
  with `result = null`. Its serializer writes `nil` when `typeToKeyAndJumpMap` has no entry for `value.GetType()`.
- **U2. The C# reference decodes an unknown arm to null and does not re-encode to its bytes.** Probe `cs`
  (payload built by hand with `MessagePackWriter`, decoded with `CultDocumentMessagePackSerialization.OptionsFor`):
  unknown key 9 in a union field gives `Root=NULL`, in a list `Items=[TextNode,NULL]`; re-encode equal: False in
  both. The formatter the reference composite hands out for the union is `MessagePack.Formatters.NodeFormatter1`
  (DynamicUnionResolver's emitted type; the probe assembly carries no generated resolver).
- **U3. Missing and extra trailing slots, C# payload level.** Same probe: a payload one slot short decodes with
  the field initializer (`Added=42`); a payload one slot long decodes with the extra slot skipped. Neither
  re-encodes to its bytes.
- **U4. The reference writes an unnamed subtype as nil.** Same probe: `Root = new RogueNode()` (a subclass the
  union does not list) serializes to `94 a2 64 32 c0 90 2a`: the root slot is `nil`, and it reads back `Root=NULL`.
  Silent loss on the write side, the mirror of U2.
- **U5. Refusal can live at the CultCache layer; no MessagePack-CSharp fork.** Same probe, `strict` options: a
  resolver placed ahead of `StandardResolver` returns, for any abstract or interface type with `[Union]`s, a
  formatter that peeks (`CreatePeekReader`) the `[key, ...]` header, throws
  `MessagePackSerializationException("union Node has no arm 9")` for a key the union does not list, refuses to
  serialize an unnamed subtype, and otherwise delegates to the formatter `StandardResolver` would have handed out.
  Baseline round-trips byte-identical; both unknown-arm cases and U4 are refused; slot behaviour is unchanged.
  Every C# decode path reaches the composite through `CultDocumentMessagePackSerialization.OptionsFor`
  (`CultCacheMessagePack.CreateCodec`, `DirectoryMessagePackBackingStore.cs:345`, `CultMesh.cs:2779,3493,3499`,
  `CultMeshPrimitives.cs:1158,1230,2357`), so one resolver covers the cache, the directory store and CultMesh.
  Consumer resolvers named by `[CultCacheFormatterResolver]` sit ahead of `CultDocumentResolver` today
  (`CultDocumentMessagePackSerialization.cs:58-62`); the guard goes first and delegates to the rest of the
  composite, so a consumer formatter still encodes the arm while the key check still runs.
- **U6. A top-level extra slot is dropped on write-back.** Probe `cs2`: a C#-written store, mutated as a newer
  writer would (catalog member slot 2 added, payload slot 2 `"newer writer data"`), is pulled, the record edited
  and flushed. The read succeeds, and the written payload is `['n1', 'edited by the older build']`: the newer
  writer's slot is gone. (The schema id was left unchanged, so the migration report said `Kind=Exact`; with a
  changed id the documented drift path reports `IgnoredExtraSlots`, `CultCache.cs:840-924`, but writes back the
  same way.)
- **U7. Rust today.** Probe `rs` (`cultcache-rs` at `aaf0d7f6`, payloads built with `rmpv`, decoded with
  `rmp_serde::from_slice`): a serde enum's unknown variant is refused (`unknown variant 'Exploded'`); the C# arm
  shape `[0, [...]]` is refused (`expected variant identifier`); a non-nil value in a gap slot is skipped; a missing
  trailing slot is refused (`missing field 'added'`) unless the field is `#[cultcache(default)]`; an extra trailing
  slot is skipped (`cultcache-rs-derive/src/lib.rs`, the `IgnoredAny` loop after the slot loop). The derive
  accepts named structs only (`lib.rs:51-67`) and writes an empty member list into the catalog
  (`src/lib.rs:2706`).
- **U8. TypeScript today.** No typed codec: with no `formatter` a document encodes as a MessagePack **map** of the
  zod-parsed object and decodes raw (`cult-cache.ts:679-692`; `@msgpack/msgpack` `encode({id:'d',version:7})` is
  `82 a2 69 64 ...`, a fixmap, where the reference writes an array); positional slots exist only in hand-written
  formatters (`test/cult-cache.test.ts:1263-1310`). P13 measured that a hand codec matches whichever encoding its
  author chose.
- **U9. Python today.** `define_database_entry_type` (`packages/cultcache-py/src/cultcache_py/documents.py:185-276`)
  is a slot codec: gaps written `nil`, extra slots ignored, a missing slot defaulted or refused. No union codec.
- **U10. The written contracts.** `cultcache-schema-compatibility.md:75-91` classifies "persisted slot is missing
  locally and can be ignored" as soft-migratable drift, reported as `IgnoredExtraSlots`;
  `cultcache-persistence-format.md:215-218` says a store reader "never skips a slot it does not understand" for
  record envelope slots, and every runtime refuses `extra-slot-full-payload.msgpack`
  (`tests/vectors/document-variants-c0`). `docs/persisted-record-encoding.md` says consumers verify canonicality
  by re-encoding and comparing bytes. The two contracts disagree about payload slots; the question below is
  about that disagreement.
- **U11. Fixture home.** `tests/vectors/<name>/`: committed bytes every runtime's tests read
  (`document-variants-c0` by Python `generate.py`, `document-variants-c1` written by C#), consumed by
  `tests/GameCult.Caching.Tests/PreCut2StoreFormatTests.cs`, `packages/cultcache-rs/src/lib.rs`,
  `packages/cultcache-ts/test/cult-cache.test.ts` and `packages/cultcache-py/tests/test_cultcache.py`. The CultNet
  precedent for a reference-written vector is `CULTNET_WRITE_VECTORS=1` (`docs/cultnet-error-contract-cut.md:170`).

## S. Slot read-only probe (any depth)

Imagination pass `imagination-cl-slot-readonly` (session `self-2026-10-10c-eureka`), under ruling
`cultlib-gaps:ruling:extra-trailing-slots` ("b, and probe the any-depth refinement"). Probes live under the session
scratchpad, `imagination-cl-slot-readonly/` (`cs` C# synthetic, `aeth` C# on Aetheria's game database, `rs`, `py`,
`ts`, `il` decompiled MessagePack-CSharp 3.1.7). None is committed. The ruling's test: every runtime's decoder
carries a "slot skipped" mark to the record cheaply, on the one decode path, with no second pass and no per-type
consumer code.

- **S1. Where C# skips, and why a resolver sees it.**
  `DynamicObjectTypeBuilder.BuildDeserializeInternalDeserializeLoopIntKey` emits the slot loop's `switch` default as
  a direct `MessagePackReader.Skip()` call; the source generator's `FormatterTemplate` writes
  `default: reader.Skip(); break;`. `MessagePackReader` is a struct, and neither path raises an event or reads an
  option, so the skip itself cannot be observed without a fork. Both fetch every member formatter from
  `options.Resolver` (the generated code's `formatterResolver = options.Resolver`), and the union formatters fetch arm
  formatters the same way (U1). A resolver ahead of the composite is therefore handed every nested object, list
  element and union arm, and can read each one's array header before delegating.
- **S2. The mark arrives in C#.** Probe `cs`: a guard resolver returns, for each concrete int-keyed
  `[MessagePackObject]` type, a formatter that reads the array count (from `NextCode` for a fixarray, a peek for
  array16 and array32), compares it with the highest `[Key]` plus one (base keys included, MessagePack's member
  visibility rule), notes a skip into a `[ThreadStatic]` scope the decode's caller opened, and delegates to the
  formatter the rest of the composite hands out. An extra slot at depth 0 (record), depth 1 (nested object), depth 2
  (object in an object), in a union arm and in a list element each arrives as one mark naming the type and both
  counts; the clean payload gives none. The guard needs `CompositeResolver.Create` around it so formatters are
  cached (uncached, it was 40 times slower).
- **S3. C# cost.** Aetheria `GameData/Aetheria.cc` (12.1 MB; 237 of 238 records decodable with Aetheria.Shared's
  types; decoded with `OptionsFor` that assembly; best of 9, three rounds): plain 165-209 ms, a second plain composite
  (A/A) 174-193 ms, guarded 179-196 ms. That is 3.7-8.9% over the best plain run, inside the A/A spread. Worst case,
  synthetic tiny objects (120,000 records, 23.1 MB, about 24 objects each): plain 618-637 ms with A/A within 4%,
  guarded 704-705 ms: 11-19%, about 30 ns per decoded object.
- **S4. A live case in Aetheria.** The same probe, run with the AetherDb release binaries (built 2026-09-30) against
  today's `Aetheria.cc`, marks 25 records: `WeaponItemData` whose `BehaviorData` arms (`AutoWeaponData`,
  `ChargedWeaponData` and others) carry 34 slots where that build declares 21 or 33. The extra slot is
  `WeaponData.Tracking` (key 33, Aetheria `8cd71ed6`, 2026-10-07): inside a union arm, inside a list. Today that tool
  reads these records and drops `Tracking` from all 25 at its next landing (S5). Refusing nested extra slots would
  stop it opening `Aetheria.cc` at all; read-only lets it open the store and refuses writes to those 25.
- **S5. A C# flush re-encodes every held record, not only the edited one.** `PushAll` and an unconditional
  `CommitBatch` write `ToPersistedRecord(entry, SerializePayload)` for every entry (`CultCache.cs:3191-3201`, `3355`,
  `3384`), under the local descriptor's schema id and catalog entry. So U6's loss reaches every held record with
  unread slots at the next flush of anything. Rust (holds payload bytes and decodes on `get`, `lib.rs:2280-2293`),
  Python (holds envelopes and decodes at load, `cache.py:257-285`) and TypeScript (holds payload bytes) carry an
  untouched record's bytes. In C#, read-only therefore also means persisted from the original record (bytes, schema
  id, catalog entry), never from the decoded document.
- **S6. Rust.** Probe `rs`: the derive's slot loop, with the trailing `IgnoredAny` loop counting into a
  `thread_local`, marks depth 1, depth 2 and a list element. Depth 0 is the `DatabaseEntry` derive's own trailing
  loop (`cultcache-rs-derive/src/lib.rs:258`), the same edit. A plain serde struct nested in a record already refuses
  an extra element (`array had incorrect length, expected 2`), so only derived slot types skip. Cost (120,000
  records, 21.4 MB): inside the A/A spread (A/A from -3% to +9%; marked against plain from -14% to +13% over six
  runs). The added code is one branch per object, never taken on a clean payload. Rust decodes on `get`, so whether
  a held record is writable is answered by decoding its held bytes through the same derive, on the write path only.
- **S7. Python and TypeScript.** Probes `py` and `ts`: a slot codec tree passing a context argument (the shape
  `union-codec-py` and `union-codec-ts` build) marks all five positions. Python (40,000 records, 11.1 MB, msgpack
  unpack included): +14% against an A/A spread of 8%. TypeScript (120,000 decoded records, codec walk only; no
  msgpack in the scratch copy): +20% against an A/A spread of 18%. The check is one length comparison per object.
- **S8. Kotlin has no library slot codec.** `CultDocumentCodec<T>.decode` is written per type by the consumer
  (`packages/cultmesh-kotlin/src/main/kotlin/org/gamecult/cultmesh/CultMesh.kt:31-36`), and the package's own Eve
  decoders skip extras by hand at 8 sites (`repeat((count - N).coerceAtLeast(0)) { reader.skip() }` in
  `eve/EveDocuments.kt`). No slot rule, refusal or read-only, can be held by the library there until it owns slot
  decoding. That is a follow-up, not a per-runtime rule.
- **S9. CultMesh decode paths** (`CultMesh.cs:2779,3493,3499`, `CultMeshPrimitives.cs:1158,1230,2357`) go through
  `OptionsFor`, so through the guard, with no scope open: unchanged. Whether a Mesh replica that re-publishes a
  decoded document drops slots is a follow-up.
- **S10. The refinement is in.** Every runtime with a library slot decoder (C#, Rust, Python, and the TypeScript
  codec being built) carries the mark to the record on its one decode path, with no second pass and no consumer
  code. The cost on Aetheria's real store is inside the A/A noise (S3); the worst synthetic case is 11-19% in C#.
  A nested extra slot makes the record read-only; it is not refused.

## A. Aetheria (C#, the only consumer persisting `[Union]`s)

Aetheria consumes the reference through Unity packages pinned by tag (`Packages/manifest.json:55-57`:
`caching-unity-v1.4.0`, `cultlib-unity-v1.0.60`), so a CultLib change reaches it only when it bumps the pin.
Its only consumer resolver is `MathResolver` (`AssemblyInfo.cs:4`), which supplies no union formatter.

- **A1. Unions inside persisted documents** (Aetheria `aac2cf91`, `Assets/Scripts/ServerShared`):
  `BehaviorData` (`Behaviors/Behaviors.cs:147-191`, keys 0-11, 15, 16, 18, 20-24, 28, 31-40; 12 and 26 retired,
  14, 17, 19, 25, 27, 29, 30 commented out) in `List<BehaviorData> Behaviors` of the item data documents
  (`ItemData.cs:346,368`); `WeaponData` (`Behaviors/Weapon.cs:15-18`); `EntityPack` (`EntitySerializer.cs:172-173`)
  in `aetheria.savedzone` contents and its own `Children`; `ItemInstance` and `CraftedItemInstance`
  (`ItemInstance.cs:18-31`, a union whose arms are also a union) in entity cargo and docking bays
  (`EntitySerializer.cs:186-187`); `Provenance` (`Provenance.cs:96-98`) in `aetheria.provenanceledger`
  (`Provenance.cs:11-15`, `Lot.Origin`); `SavedActionBarBinding` (`SavedGame.cs:221-223`) in `aetheria.savedgame`;
  the input layout rows and keys (`InputLayout.cs:29-50`) in `aetheria.inputlayout`. `Brush`
  (`Environment.cs:73`) was not traced to a persisted document.
- **A2. `GameData/Aetheria.cc` carries no unknown arm.** Walker `py/store_unions.py` over the git-tracked game
  database (12.7 MB, `cultcache.store.v1`, 238 records, 13 catalog entries): `Behaviors` arm keys
  {0:3, 1:2, 2:10, 3:5, 4:2, 5:2, 6:3, 8:7, 9:23, 11:2, 16:4, 23:7, 24:5, 31:1, 32:1, 34:1, 35:2, 37:4, 38:10, 39:2,
  40:1}; none retired or commented out; no `nil` element.
- **A3. The run save carries no unknown arm, and does carry missing trailing slots.** Walker `py/run_unions.py`
  over `GameData/run.cc` (2026-09-30): `EntityPack` arms 0 and 1 (3 each), `ItemInstance` arm 2 (12),
  `Provenance` arm 0 (64), `SavedActionBarBinding` 9 `nil` (empty bar slots). The `EntityPack` arms carry 21 slots
  (orbital) and 20 (ship) against today's 22: the `Boss` slot (key 21, added 2026-10-07 in `db4617d8`) is missing,
  so today's Aetheria reads this save only because a missing trailing slot defaults. `py/gaps.py`: no non-nil
  retired slot (ItemInstance 2, 9, 10; EntityPack 5). `GameData/stale-2026-09-22/run.cc` is the same shape;
  `player.cc` holds only `aetheria.playersettings`. `py/slots.py`: every top-level record matches its own store's
  catalog exactly.
- **A4. What changes for Aetheria.** After it adopts a CultLib with cut 1, its current stores load unchanged
  (A2, A3). A store written later by a build that has added an arm is refused by a build that lacks it, naming the
  union and key, where today the arm silently becomes `null` (a gear item quietly loses a behaviour, a lot loses
  its provenance) and the next save writes the loss back. Builds already pinned to 1.0.60 keep the null path; a
  shipped binary cannot change. Aetheria-legacy does not use CultCache and has no store.
- **A5. Other C# stores.** `%LOCALAPPDATA%Low/GameCult/Aetheria/aetheria-unity.cc` (2026-08-08, 1.2 KB) is a
  `cultcache.store.v2.directory-indexed` header from an older layout, not a union carrier.

## C. Rust, TypeScript and other consumers

Eyes audit for this pass (session `self-2026-10-10c-eureka`), `git grep` at each repo's `origin/HEAD`, Rust reach
by a lexical script (it can over-count; it does not under-count named fields). Aetheria and CultLib's tests excluded.

- **C1. Rust: no `DatabaseEntry` record crosses runtimes as a union, and none uses the reference arm shape.**
  Records that reach serde enums, each keeping its bytes because the derive is opt-in:
  Huginn `epiphany.pipeline.resolution.v2` (`ResolutionOutcome`, external tag, `eureka-pipeline/src/lib.rs:746`,
  live on Yggdrasil); Idunn `idunn.deployment_transaction` and `idunn.admitted_generation` (v4 and legacy forms;
  internally tagged `#[serde(tag = ..., deny_unknown_fields)]` enums in `control_plane.rs`, `deployment_plan.rs`,
  and two `#[serde(untagged)]` enums kept so older transactions decode, `drivers.rs:259,387`; live at
  `/var/lib/gamecult/idunn/control.cc`); CodexConnector `gamecult.codex.replay_record.v1` (`CodexReplayState`,
  `lib.rs:1017`); Epiphany's Atlas, model-request, persona and receipt records (internal, adjacent, external and
  untagged representations; `.cc` files under `F:\Projects\.epiphany-runtime\` and on Yggdrasil). Unit-only serde
  enums (a bare string) are widespread in Idunn, Huginn (`Faculty`, the pipeline's unit enums), CodexConnector and
  Epiphany. No enum reaches any record in Odin, Muninn, Ghostlight, Huginn `mind-body`, or cultnet-rs and
  cultmesh-rs production records. None of these is read by another runtime as a union today, so none is affected
  by the arm refusal; the derive changes nothing for them.
- **C2. TypeScript: no consumer encodes a tagged union.** The only hand formatters are StreamPixels
  `apps/service/src/realtime/overlay-state-documents.ts:190,232,277` (positional arrays, no union). Bifrost,
  Heimdall, Hermodr, Odin scripts, Stonks, Vili, VoidBot (vendored `cultcache-ts`) and weksa have none.
- **C3. C# outside Aetheria: StreamPixels' overlay declares unions; its tracked store holds none of them.**
  `apps/overlay-unity/Assets/Scripts/Assembly-CSharp/` (`8d40541`): `ActivationCondition` (10 arms),
  `BattleEffect` (2), `StatusEffect` (4), `ItemInstance` (3), `EquippableItemInstance` (2), reachable from
  `streampixels.status`, `streampixels.action_item`, `streampixels.skill` and gear documents. Walker
  `py/sp_catalog.py` over `apps/overlay-unity/Data/catalog.cc` (`cultcache.store.v1`, 20 records): only
  `outfit`, `skin_color`, `hair_style`, `emote` and `hair_color` records, and no `[int, array]` pair at any depth.
  Its only consumer resolver is `MathResolver` (`Catalog/CatalogAssembly.cs:6`). CultLib's own
  `GameCult.Networking` `Message` union (`Messages.cs:10`) is a CultNet wire message, decoded with
  `MessageSerialization.Options` (`MessageSerialization.cs:7`, `DefaultOptions`), outside `OptionsFor`: the arm
  guard does not reach it (follow-up).
- **C4. Python and Kotlin: no union-shaped record.** repixelizer uses `define_database_entry_type` with flat
  slots; `cultmesh-kotlin` documents are `data class`es with no sealed type.

So the stores the arm refusal can affect today are Aetheria's (A2-A4, none carrying an unknown arm) and
StreamPixels' overlay catalog (C3, carrying no arm). Every other runtime's persisted enums are serde shapes that
the opt-in derive leaves alone.

## Rationale

- **Why a guard formatter, not a fork or a re-implementation.** U5 shows a guard that only checks the key and the
  subtype and delegates the encoding is enough. It keeps MessagePack-CSharp's union encoding as the one encoder,
  costs one small file, and covers every path that already goes through `OptionsFor`. Forking the generated
  template or vendoring `DynamicUnionResolver` would put a second encoder in CultLib.
- **Why the Rust derive is opt-in.** Serde enums already persisted by Rust consumers keep their bytes
  (`docs/persisted-record-encoding.md`); a changed default would make every such store unreadable or
  non-canonical. A new derive with explicit arm keys is how a Rust type says "this crosses runtimes".
- **Why arms use newtype variants over slot structs.** A C# arm is a subclass whose slots include the base class's
  keys. A Rust newtype variant over a struct with keyed slots is the same thing, and it needs the slot code the
  `DatabaseEntry` derive already has, factored so both derives share one slot generator.
- **Why the slot rule is a separate, asked cut.** The ruling settles arms and asks for extra trailing slots to be
  "settled against the same re-encode rule". Two written contracts disagree (U10), and the current behaviour drops
  data silently (U6), so the choice is the operator's and it should change every runtime at once.
- **The recommendation on extra slots was refusal; the ruling chose read-only, and S settles its depth.** An extra trailing slot is the slot-level twin of an unknown
  arm: data from a newer writer that the reader's types do not describe. The operator ruled refusal for arms
  knowing it costs an older reader the newer record; the same reasoning gives refusal for slots, and record
  envelope slots are already refused in every runtime (U10). The alternative with precedent is
  `variants:ruling:undeclared-read-only` (a record a type can read but does not declare loads read-only): it would
  let an older reader see a newer record without overwriting it, but only where a catalog shows the extra slot
  (top level), while most evolution here is nested (A3's `Boss` slot sits inside a union arm).
- **Missing trailing slots stay tolerated.** The reader knows a missing slot and its default, so it carries no
  bytes it does not understand, and Aetheria's live run save depends on it (A3). A store whose guard compares
  re-encoded bytes (Idunn's) refuses such a record by its own stricter rule, which is that guard's purpose.
