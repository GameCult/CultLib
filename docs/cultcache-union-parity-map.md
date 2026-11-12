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
- **U7. Rust at `aaf0d7f6`, before the union cuts.** Probe `rs` (`cultcache-rs` at `aaf0d7f6`, payloads built with `rmpv`, decoded with
  `rmp_serde::from_slice`): a serde enum's unknown variant is refused (`unknown variant 'Exploded'`); the C# arm
  shape `[0, [...]]` is refused (`expected variant identifier`); a non-nil value in a gap slot is skipped; a missing
  trailing slot is refused (`missing field 'added'`) unless the field is `#[cultcache(default)]`; an extra trailing
  slot is skipped (`cultcache-rs-derive/src/lib.rs`, the `IgnoredAny` loop after the slot loop). The derive
  accepts named structs only (`lib.rs:51-67`) and writes an empty member list into the catalog
  (`src/lib.rs:2706`).
- **U8. TypeScript at `aaf0d7f6`, before the union cuts.** No typed codec: with no `formatter` a document encodes as a MessagePack **map** of the
  zod-parsed object and decodes raw (`cult-cache.ts:679-692`; `@msgpack/msgpack` `encode({id:'d',version:7})` is
  `82 a2 69 64 ...`, a fixmap, where the reference writes an array); positional slots exist only in hand-written
  formatters (`test/cult-cache.test.ts:1263-1310`). P13 measured that a hand codec matches whichever encoding its
  author chose.
- **U9. Python at `aaf0d7f6`, before the union cuts.** `define_database_entry_type` (`packages/cultcache-py/src/cultcache_py/documents.py:185-276`)
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

## D. Payload depth bound (body at `c028c511`)

One bound, 256 nested MessagePack arrays and maps, on read and on write (ruling
`cultlib-gaps:ruling:payload-depth-limit-256`; prior art `docs/research/payload-depth-limit.md`). The C# reference
landed in `ead5260c`, Python in `5618d8dd`, Rust in `c028c511`, TypeScript in `b69d3057`. The one-value rule that rides the
same walkers (bytes after the first value) is section T1; the Python row below predates it, and T1 names its owner.

| Runtime | Owner of the decision (constant and walker) | Doors routed through it |
|---|---|---|
| C# | `CultPayloadDepth.Check` (`src/GameCult.Caching.MessagePack/CultPayloadDepth.cs`), limit `CultMessagePackSecurity.MaximumPayloadDepth`. MessagePack's own graph counter is set to the same value as a backstop only. | Write: `CultDocumentMessagePackSerialization.SerializeUntyped`. Read: `DeserializeUntyped`. The cache store (`SerializePayload`/`DeserializePayload`), the directory store, `CultMesh` and single-file documents all call those two; C# has no separate raw-envelope or prepared-batch door. |
| Rust | `check_record_payload` (private; depth, then no bytes after the first value) over `first_value_end`, and `MAX_PAYLOAD_DEPTH`; `cultcache_rs::check_payload_depth` is the depth-only probe for outside callers (`packages/cultcache-rs/src/lib.rs`). | Read: `decode_payload` (get, list, `validate_envelope`/`put_envelope`, `finish_prepared_entry`). Write: `put`/`prepare_entry`/`prepare_entry_named` reach it through `finish_prepared_entry`, which decodes what it just encoded. Raw envelope: `put_raw_envelope`, the door CultMesh network puts use (`cultmesh-rs` `lib.rs`, two call sites). Prepared batch: `put_prepared_batch` via `require_record_payload`. Load: `pull_all_backing_stores` and `load_soa` run `require_record_payload` on every record, so a store holding a payload past the bound (or with bytes after it) is refused at load, and the cache then refuses every write and removal (`CultCacheLoadRefused`) until a load succeeds. cultnet-rs: every free-form or skippable wire payload decodes through `cultcache_rs::decode_payload` (message decode in `contracts.rs`, `provider_session`, `media_stream_wire`, `replication`, `service_identity`). |
| Python | `_refuse_past_depth_limit` and `MAX_PAYLOAD_DEPTH` (`packages/cultcache-py/src/cultcache_py/documents.py`); a msgpack `StackError` on read maps to the same error. | `DocumentDefinition.encode_payload` (write) and `decode_payload` (read). `CultCache.put`, `put_envelope`, `put_envelopes` and load-time hydration go through them; there is no raw-envelope door. cultmesh-py round trips call the same pair. |
| TypeScript | `checkPayloadDepth` and `MAX_PAYLOAD_DEPTH` (`packages/cultcache-ts/src/payload-depth.ts`), iterative, returning where the first value ends. | `encodeDocumentPayload` (write, checks the bytes it just encoded) and `decodeDocumentPayloadMarked` (read, via `checkPayloadIsOneValue`) in `src/document.ts`, only for a definition that writes the library's own MessagePack (no `formatter`: a formatter's bytes are its own). The inspector (`cult-cache-inspector.ts`) checks a payload it previews with the same pair. |

- **Doors deliberately not routed.** cultnet-rs `idunn_lifecycle_brake`, `provider_health_contracts` and
  `runtime_authority_contracts` decode records whose positional array length is checked exactly before decoding. A
  fixed shape has no undeclared slot to skip and no free-form content, so the derive's depth is its shape (commit
  `84bb1e28`). The five media records have no length check and are held by the test instead. C# `CultCacheMessagePack.CreateCodec`
  serialises member values and deserialises member types without the document guard, by design (codec comment); only
  the document type goes through the guard.
- **Forbidden writers.** No second walker and no second constant: a runtime's decoders, stores and wire layers call
  the owner above, never re-count depth or restate 256. An added payload door routes through the owner or names
  itself in the exact-length list.
- **Shared path.** `tests/vectors/union-parity/` (`bounds-outcomes.msgpack`, `depth-*.msgpack`; table in its
  `README.md`), written by the C# reference and held by `tests/GameCult.Caching.Tests/UnionParityVectorTests.cs` and
  `PayloadDepthTests.cs`, `packages/cultcache-rs/tests/union_parity.rs`, `packages/cultcache-py/tests/test_union_parity.py`.
- **Cut line.** `packages/cultnet-rs/tests/payload_depth.rs` and the `service_identity`/`cultmesh-rs` tests pin the
  Rust wire doors. cultnet-py and cultmesh-py message and replication decoders call `msgpack.unpackb` directly and
  are outside the Python cut; they hold only msgpack's own stack limit.
- **Verification layer.** Vector held by each runtime's own suite (limit 256 accepted, 257 refused, skipped content
  counted, non-recursive walk); Rust wire doors by the cultnet-rs test above.

## T. Ownership after the trailing-bytes, slot-tolerance and load-gate cuts (body at `66bea107`, 2026-11-12)

Body facts for the cuts that moved who decides these invariants. Typed state is cited by id, not restated:
rulings `cultlib-gaps:ruling:trailing-bytes-refuse`, `cultlib-gaps:ruling:extra-trailing-slots`,
`cultlib-gaps:ruling:slot-mark-consent`, `cultlib-gaps:ruling:slot-unmeasurable-type`,
`cultlib-gaps:ruling:leaf-acceptance-follows-reference`; specs `cultlib-gaps:cut_spec:cut-bytes-after-{cs,py,rs,ts}.r1`,
`cut-slot-tolerance.r4`, `cut-slot-readonly-{py,ts}` and `-rs`, `cut-cache-pull-before-write-rs.r1`. S1-S10 above are
probes at the pre-cut base; where they say "today" or "dropped on write-back" (U6, U10) the behaviour they describe is
gone, and T is the live owner.

### T1. A record payload is exactly one MessagePack value

- **Invariant.** Bytes after a payload's first value are refused, by count, on read in every runtime, and on write
  where bytes the library did not encode itself are accepted. A payload nested past 256 is refused as depth even when
  a tail follows: depth wins, and a tail is never walked or quoted. A payload that ends early is the decoder's
  refusal, not "bytes after". Streams of values (CultNet frames, a store's record array) are other contracts.
- **One owner per runtime** (the same walker that owns depth, D):

| Runtime | Owner | Inputs | Doors that must reach it |
|---|---|---|---|
| C# | `CultPayloadDepth.Check(bytes, type, direction)` via `CultDocumentMessagePackSerialization` `SerializeUntyped` (write) and `DeserializeUntyped` / `DeserializeMarked` (read) | payload bytes | every cache, directory, single-file and `CultMesh` document read or write; no raw-envelope door |
| Python | `read_payload_tree(type_name, payload, decoder=None)` in `documents.py`, exported; `DocumentDefinition.decode_payload` calls it. `_refuse_past_depth_limit` stays the depth owner (write calls it directly) | payload bytes | `decode_payload`, load-time hydration, `put_envelope(s)`; cultnet-py's witness bundle decode (`decode_witness_artifact_bundle_payload`) |
| Rust | `check_record_payload` (private; depth, then one value) over `first_value_end`; `require_record_payload(&CultCacheEnvelope)` adds type and key to the error | payload bytes | `decode_payload` and `decode_payload_marked`, `put_raw_envelope`, `put_prepared_batch`, `stage_pull` (load), `load_soa`; `check_payload_depth` stays the depth-only public probe |
| TypeScript | `checkPayloadIsOneValue(bytes, subject)` in `payload-depth.ts` over `checkPayloadDepth` (returns where the first value ends) | payload bytes | `decodeDocumentPayloadMarked` (so `decodeDocumentPayload`) and the inspector; only for a definition with no `formatter` |

- **Outputs.** A refusal naming the type and the count (`<type> payload has N bytes after its first MessagePack value
  (refused on read|write).`; C# `MessagePackSerializationException`, Python `ValueError`, TS error, Rust `anyhow`),
  never the tail. Load and open refuse the whole store: Rust `pull_all_backing_stores` now refuses depth and tail for
  every record, not only some doors (`stage_pull` and `load_soa` call `require_record_payload`).
- **Derived state.** None. The count is computed per call; no runtime stores a "had a tail" flag.
- **Forbidden writers.** A second walker, a second "is this one value" test, or a reader that ignores the tail
  (`msgpack.unpackb` raising `ExtraData` and being caught as success; a deserializer left to stop at the first value).
  A new payload door calls the owner or names itself exempt (D "Doors deliberately not routed"; a formatter's own
  bytes in C# and TS).
- **Shared path.** `tests/vectors/union-parity/bytes-after-*.msgpack` (`nil`, `never-used`, `deep-tail`,
  `second-document`) and `bytes-after-outcomes.msgpack`, written by C# only; table in that directory's `README.md`
  ("Bytes after the value"). CDN manifests follow the same rule through
  `contracts/cultmesh/content-acceptance-vectors.json` (`trailing_nil`, `trailing_reserved_code`,
  `trailing_truncated_array`).
- **Cut line.** Inside each owner. cultnet-py and cultmesh-py wire messages still call `msgpack.unpackb` directly
  (D, cut line); only the witness bundle payload crosses the owner.
- **Verification layer.** Vector, each runtime's own suite: C# `PayloadDepthTests.OnlyOneValueIsAPayload` (owner)
  and `UnionParityVectorTests`; Rust `union_parity.rs` `bytes_after_vectors`, `depth_beats_a_tail`,
  `a_truncated_payload_is_the_decoders_not_bytes_after`, and `slot_readonly.rs`
  `a_tail_on_a_slot_payload_is_refused_before_any_mark` and
  `a_store_with_marked_records_and_a_tail_is_refused_and_its_cache_writes_nothing`; Python `test_union_parity.py`
  `test_every_bytes_after_vector_is_refused`, `test_a_single_file_store_holding_a_tail_as_a_record_payload_refuses_on_load`,
  `test_depth_wins_over_a_tail_whatever_the_unpackers_own_stack_limit`,
  `test_a_payload_that_ends_early_is_the_decoders_not_bytes_after`; TypeScript `union-parity.test.ts` "every
  bytes-after vector is refused by the one owner, by count, on the codec, plain and store paths" and
  `payload-depth.test.ts` "the inspector refuses bytes after a payload's first value ...". Contract text:
  `src/GameCult.Caching/Contracts/cultcache-persistence-format.md` ("A record's payload is exactly one MessagePack value").

### T2. A record with unread slots is held read-only

- **Invariant.** A record whose payload carries a slot past its reader's declared slots, at any depth (record, nested
  record, list element, union arm, unit arm), is read, served, and held read-only in all four runtimes. Every write or
  removal of its key is refused before any store is touched, with the runtime's schema-conflict refusal carrying the
  mark (type, slots carried, slots declared, occurrences; never a slot value). An untouched marked record is
  persisted from its original bytes, schema id, stored-at and catalog entry, never re-encoded from the decoded
  document (S5). Missing trailing slots default and mark nothing; a non-nil value in a retired slot below the highest
  key does not mark. Exempt types (custom formatter, unmeasurable): `cultlib-gaps:ruling:leaf-acceptance-follows-reference`,
  `cultlib-gaps:ruling:slot-unmeasurable-type`.
- **Consent.** Per `tests/vectors/union-parity/README.md` ("Consent"): a mark is explained only at depth 0, when the
  record's stored id is one the reader lists compatible, that id's catalog entry has more members, and the mark's slot
  count is at most the highest reported slot plus one. Declaring a compatible id consents to an explained mark only;
  a nested mark or a mark under the reader's own id stays read-only whatever is declared
  (`cultlib-gaps:ruling:slot-mark-consent`).
- **One shared write-refusal judge per runtime.** The write entries and the writability query ask the same function,
  so the query cannot disagree with the write:

| Runtime | Judge | Query | Writers that must ask it |
|---|---|---|---|
| C# | `CultCache.WriteRefusal(stored, upsert, batch, out home)` (`CultCache.cs`); `Claim` throws its result, `FlattenRefusal` extends it over a variant's base chain | `CultCache.IsWritable(key)` | every upsert, variant write, flatten, removal and batch |
| TypeScript | `#judge(registered, key, written?)` in `cult-cache.ts` (`#refuseForeignKey`, `#refuseRetype`, `#refuseUnread`) | `isWritable(definition, key)` | `#writeNow`, `#deleteNow` |
| Python | `CultCache._refuse_at(document, key)` (`_refuse_unread`, then `_refuse_retype`; global-key rule first) | `is_writable(document, key)` | `put`, `put_global`, `update`, `update_global`, `put_envelope(s)`, `delete`, `delete_global` |
| Rust | `refuse_unread_slots(type_id, key)` over `held_finding` (decodes the held bytes with the registered type's mark; `Free`, `Marked`, `Unreadable`) | `CultCache::is_writable(type_id, key)` (also asks `refuse_retype`, so a foreign or other-typed key is false) | `put`, `put_envelope`, `put_raw_envelope`, `put_prepared_batch`, `delete`, and `load_soa` (which keeps a held read-only record) |

- **Derived state.** The mark (`UnreadSlots`, C# `CultStoredDocument.UnreadSlots`, Python `_state.unread` by key, Rust
  derived on demand from the held bytes, TS `NotedSlots`) is derived from the payload at decode and never persisted. Rust
  and TS hold payload bytes and carry them untouched (S5).
- **Forbidden writers.** A per-entry check that re-derives the rule; a consumer deciding writability from its own
  slot count; a decoder that drops unread slots and lets the next flush shed them (the old U6 loss).
- **Shared path.** `tests/vectors/union-parity/extra-slot-*.msgpack`, `extra-slot-store.cc`,
  `extra-slot-consent.cc`, `slot-outcomes.msgpack`, `outcomes.msgpack` (nine rows: four `read-only`; the fifth
  read-only payload is `depth-skipped-at-limit.msgpack` in `bounds-outcomes.msgpack`); C# is the only writer.
- **Cut line.** The decode path that carries the mark to the record (S10) and the judge; consumers only read
  `isWritable`. Kotlin has no library slot codec (S8) and holds no rule. A Rust or Python document with a custom decode
  and no slot codec never marks.
- **Verification layer.** Vector plus per-runtime suites: C# `SlotReadOnlyTests` (`AMarkAtAnyDepthHoldsTheRecordReadOnly`,
  `EverySlotOutcomeRowHoldsAndARemovalIsRefusedExactlyWhereTheRecordIsNotWritable`, `ConsentStopsAtTheSlotsTheCatalogReports`,
  `AMarkedRecordIsCarriedByteForByteWhenAnotherRecordIsWritten`, `IsWritableIs*`); Rust `slot_readonly.rs`
  (`every_read_only_vector_decodes_marked_and_is_not_writable`, `a_held_payload_the_type_cannot_read_is_read_only_and_refused_by_name`);
  Python `test_slot_readonly.py` (`test_every_row_is_writable_exactly_where_the_vector_says_and_refuses_with_its_mark`,
  `test_is_writable_agrees_with_the_write_for_a_key_a_store_carries_foreign`); TypeScript `slot-readonly.test.ts`
  ("every slot-outcomes row holds: the mark, IsWritable, and the removal refused ..."). Contract text:
  `cultcache-schema-compatibility.md` "Extra slots are read-only".

### T3. Rust load gate (PR #132 `hands/cache-pull-before-write` at `7da20731`: pending, not on `origin/main`)

Describe as pending until `git log origin/main` shows "must pull". Main today:

- **Owner.** `CultCache::require_loaded` over `load_refused: Option<String>`. It is set when a `pull_all_backing_stores`
  is refused (a tail, a payload past the bound, a duplicate unique-index value, a retype conflict, a record from the
  wrong store) and cleared by a successful pull. `CultCacheLoadRefused` is a struct `{ reason }`. Five write
  entries (`put`, `put_prepared_batch`, `put_envelope`, `put_raw_envelope`, `delete`) call `require_loaded` first.
- **Hole on main.** A cache that attaches a populated store and never pulls has no `load_refused`, so its
  `put_prepared_batch` replaces the populated store with only the new key (`cultlib-gaps:follow_up:cache-must-pull-before-write`).
- **After #132.** `CultCacheLoadRefused` is an enum `{ NotLoaded, Failed { reason } }`; a private
  `LoadState { Loaded, NotLoaded, Refused(String) }` replaces `load_refused`; `require_loaded` stays the only gate over
  it. Attach rule: attaching a populated (or unreadable) store sets `NotLoaded`, which only a successful pull clears;
  an empty store or no store stays `Loaded`. Costs one read of the store at attach. A store that gains records after
  attach is not seen by the gate (also true after a pull). cultmesh-rs with `pull_on_start=false` over a populated store
  refuses writes until the caller pulls. Breaking, cultcache-rs Unreleased. Spec `cut-cache-pull-before-write-rs.r1`,
  verdict `cultlib-gaps:verdict:cut-cache-pull-before-write-rs.s1`.
- **Forbidden writers.** A write entry that inspects `load` or `load_refused` itself; a second gate; a consumer
  pulling to dodge the refusal without handling `Failed`.
- **Not covered.** C#, Python and TypeScript caches have no such hole (research `unpulled-write.*`). TS
  `SingleFileMessagePackBackingStore.pushAll` still replaces the file at the store level (follow-up
  `ts-pushall-replaces-store`). `is_writable` does not ask `require_loaded` on main and after #132 (it answers
  slot and retype questions only).
- **Verification layer.** Main: `union_parity.rs` (load refusals downcast `CultCacheLoadRefused`, near line 665) and
  `slot_readonly.rs` (near 514-535). #132 adds red-first tests in `union_parity.rs` (single-file and redb).
- **Docs to touch when #132 merges.** The Rust row of D already names `CultCacheLoadRefused::Failed` / `::NotLoaded` in
  #132's own diff; `packages/cultcache-rs/CHANGELOG.md` gains the Breaking line.

### T4. cultcache-py 0.4.0 and the dependency ranges

- **Owner.** `packages/cultcache-py/pyproject.toml` `version = "0.4.0"` (the minor the Unreleased Breaking entries imply
  at 0.x: slot read-only, one record per key, unique indexes, one value). The CHANGELOG heading is still
  `## Unreleased`; it becomes `## [0.4.0]` at the tag, and no `cultcache-py-v0.4.0` tag exists (latest `v0.3.0`).
- **Consumers.** `cultnet-py` (`dependencies = ["cultcache-py>=0.4.0", "msgpack>=1.0"]`, version 0.1.0) and
  `cultmesh-py` (`"cultcache-py>=0.4.0"`, `"cultnet-py>=0.1.0"`, version 0.1.0). The bound exists because cultnet-py
  imports `read_payload_tree` at module load (finding `cultlib-gaps:finding:cut-bytes-after-py.s2.pin-admits-reader-less-cultcache`),
  and 0.3.x lacks it.
- **Verification layer.** `packages/cultnet-py/tests/test_declared_dependency_ranges.py` (the Python form of
  `scripts/test-declared-dependency-ranges.mjs`): for each tagged `cultcache-py-vX.Y.Z` that the `>=` bound of
  cultnet-py or cultmesh-py admits, every name those packages import from `cultcache_py` must exist in that release's
  source. It needs git with release tags and skips without them; only `>=` bounds are read, star imports and
  attribute access are unchecked. Run by the portability workflow (`.github/workflows/cultmesh-portability.yml`).
  With `v0.3.0` as the newest tag the bound admits no older release, so the test is vacuous until a post-0.4.0 tag
  exists; it starts to bite the first time a consumer imports a name newer than its bound.
- **Forbidden writers.** A consumer importing a new `cultcache_py` name without raising its `>=` bound; the workspace
  editable install is not evidence (it satisfies any range).
- **Cut line.** Publishing: tagging `cultcache-py-v0.4.0` and the changelog heading. cultnet-py has a changelog
  (Unreleased, breaking, next minor) but its manifest stays 0.1.0; `packages/cultmesh-py` has no CHANGELOG.

### T5. Stale docs and state found

- `docs/cultcache-union-parity-map.md`, D header: said TypeScript depth was not landed (spec `cut-union-bounds-ts.r2`,
  question `ts-payload-opaque-type`); `b69d3057` landed it. Header and TS row corrected here. The Python row in D
  still lists only `encode_payload` / `decode_payload`; T1 names `read_payload_tree` as the read owner.
- D, Rust row: names `CultCacheLoadRefused` without a variant. #132 edits that row; this branch leaves it alone to
  avoid a conflict. When #132 merges, no further edit is needed to that row.
- Cut order, step 4, and the Model page ("Extra past the reader's last key ... read-only"): still correct.
  U3, U6, U7-U10, A3 and the Rationale "extra slots" bullets describe pre-cut behaviour (silent drop on write-back,
  "two written contracts disagree") and are probe history; U10's disagreement is resolved by
  `cultcache-schema-compatibility.md` "Extra slots are read-only". They are not edited.
- `docs/research/decode-stack-safety.md` lines 8, 9, 66, 80 cite `check_payload_depth` at `lib.rs` ~526 / 598-604 and
  `decode_payload` as `check_payload_depth(payload)?; rmp_serde::from_slice`. Rust now routes `decode_payload` through
  `check_record_payload`; the function is at `lib.rs:605`. The prose is research as of its date; line numbers are stale.
- `docs/cultlib-gaps-map.md` line ~612 (`check_payload_depth` is iterative) is correct for the Rust probe; the file
  is the decode-stack-safety map and holds no bytes-after or slot-readonly facts. Its pinned base (`6bdc23c9`) predates all of T.
- READMEs and docs searched for the deleted names `require_payload_depth` and `DeserializeBoundedUntyped`: none remain
  in tracked docs or READMEs (the live names are `require_record_payload` / `check_record_payload` and
  `DeserializeUntyped`).
- `tests/vectors/union-parity/README.md` counts: the "Extra trailing slot" table's last row (the unit arm) and the
  `outcomes.msgpack` "nine rows" statement are consistent with `slot-outcomes.msgpack`; the `bytes-after-*` section
  does not list `outcomes.msgpack`'s relation to `bytes-after-outcomes.msgpack` beyond the row shape (no defect, noted).
- `packages/cultmesh-py` has no CHANGELOG, so its `>=0.4.0` pin bump is recorded only in the manifest and in the
  cultnet-py entry.

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
