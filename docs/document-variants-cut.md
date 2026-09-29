# CultCache document variants: cut map (draft)

Date 2026-09-29. Imagination output for `docs/document-variants-target.md` (branch
`codex/document-variants`). Body read at `origin/main` `268e0ef`. This map owns means only;
the target owns ends and rulings 1-7. Nothing here is decided that the batch in section 4
asks the operator to decide.

## 1. Probe facts (run, not inferred)

Probe: scratch clone `scratchpad/variant-probe` (commit `7b6de8e`, probe files only), run on
Yggdrasil via `ygg-verify.sh` with images `dotnet`, `rust`, `python:3.12-slim`, `node:22`.
Each runtime's current reader wrote a normal store, the bytes were altered four ways, and
the store was reopened. Output in `scratchpad/probe-out/*.txt`.

| Altered store | C# | Python | TypeScript | Rust |
| --- | --- | --- | --- | --- |
| A. v1 header, record gains slot 4, payload empty | refuses, "read past end of stream" | refuses, "incomplete input" | refuses, "Offset outside DataView" | refuses (strict tuple), misleading legacy-envelope message |
| B. v1, slot 4 plus a full payload | **loads the payload silently** | **loads silently** | **loads silently** | refuses |
| C. header `cultcache.store.v2` | **ignores the header**; refused only by the empty payload | refuses, misleading "legacy envelopes" message | refuses | refuses |
| D. payload holds the delta as a sparse array | **loads `Name=''`, `Max=99`** | **loads `name=None`** | refuses | loads; `get` of that key fails |

Consequences:
- The **empty payload is the only tripwire every shipped reader trips**, pinned builds
  included (Aetheria pins `caching-unity-v1.4.0`). A variant record's payload must be empty,
  and the delta must live outside it.
- A payload-as-delta encoding is ruled out: C# and Python misread it silently.
- C# never checks `formatVersion` (`CultDocumentMessagePackSerialization.DeserializeSnapshot`
  defaults a missing one to v1). C#, Python and TypeScript skip extra record slots silently.
  These are latent defects today and become misreads when the format grows.
- Every refusal is loud but none of them names the record. The target requires named refusals,
  so current readers need a small fix (C0).

Other Body facts that shape the map:
- **CultNet has no member-path grammar.** `docs/cultnet-selection-cut.md` D2: "Declared
  aliases, not a field-path language". It reaches top-level declared `index`/`role` aliases
  only (`CultDocumentMemberView`). Nothing exists to reuse. See section 3.
- **The schema catalog describes top-level members only.** A nested type (`BehaviorData`,
  `PerformanceStat`, `HardpointData`) appears only as a CLR type name. Soft drift and hard
  rejection cannot see below slot level.
- **Resolution needs a codec, and the core has none.** `GameCult.Caching` holds objects. The
  MessagePack codec lives in `GameCult.Caching.MessagePack`. `CultInspectorModel` already
  takes the codec as two injected functions (`CultCacheMessagePack.CreateInspectorModel`).
- **Same concrete type implies same home store.** `Home(type)` routes by type, so ruling 3
  ("same store") falls out of ruling 6. No separate check is needed.
- **The name index shadows silently.** `Index()` does `MapOf(_names, type)[name] = key`, so a
  second record with the same name overwrites the entry. A variant that inherits its base's
  name would make `GetByName` return one of the two, depending on apply order.
- **Studio edits by whole-document upsert.** `CultInspectorEdit.Commit` calls
  `UpsertAsync(type, copy, key)`, and `Duplicate` clones through the codec.
- **Raw readers bypass the cache.** In CultLib: `CultMesh.ReadSingleFileDocument`
  (`CultMeshSingleFileDocuments.cs:112`), plus its legacy snapshot decoder. In consumers:
  AetheriaEve `AetheriaRuntimeCatalogStore` (directory-store pages).
- **Kotlin reads no store files.** It sees CultNet raw snapshots only.

## 2. Model page (step 0b)

| Kind | Named by | Over time | Decided by |
| --- | --- | --- | --- |
| Variant record | its own `CultRecordKey`, minted like any record; same `schemaId` as its type | created, re-overridden, rebased, removed; never converted to or from a plain record implicitly (Q1) | author through Studio or the variant write API; the cache validates |
| Base reference | the base's key, in the record's variant slot | changed only by an explicit rebase; a base cannot be deleted while it has variants, unless the same batch rebases or removes them | author; the cache refuses a cycle, a missing base, a different concrete type, a global type |
| Override entry | `(op, path, id, value)`; op is Set, Insert or Remove | added by editing; removed by clearing; survives base edits while its path resolves | author; the cache refuses entries a base edit strands (Q2); an entry whose member left the type soft-drifts with a warning (Q4 c) |
| Member path | a sequence of `(slot, elementId)` steps; `slot` is the MessagePack `[Key]`; `elementId` is `""` unless the step enters an object-list element | a type change can invalidate it | the type's owner (slot authority); ids by the element's creator |
| Element id | a `[CultElementId]` string member on an object-list element type; `""` means unset | minted by the cache on write when unset; copied unchanged into variants; a moved element is a new element | cache mints it; it is never retargeted |
| Resolved view | `CultStoredDocument.Document` for a variant key | recomputed inside the hold that admits the variant, or any base on its chain | the cache only; never persisted, never written back (Q1) |
| Index and name entries | as today, from the resolved view | a dependent's entries are dropped and re-added in the same `Apply` as its base change | the cache; a variant must override the `[CultName]` member (rule R6) |
| Watches | as today; each re-resolved dependent emits `Updated` with its own `Sequence` | published in the same hold as the base change | the cache |
| Compare-exchange | `(schemaId, storedAt)` of the stored delta (ruling 2) | a base change does not stamp its variants | the cache; the resolved view is not an identity |
| Persisted format version | store header `formatVersion` | `cultcache.store.v2` when the store holds at least one variant; `v1` otherwise (Q5) | the writing store, from content |
| Variant slot on the wire (CultNet) | **empty cell**: no CultNet message carries a delta today | C5 | ruling 4 decides deltas; the message shape is C5's design |
| Dictionary entries, scalar lists, arrays | **empty cell**: no stable identity exists | whole-member Set only (Q7) | Q7 |
| Template (non-item) bases | **empty cell**: parked by ruling 6 | none | not in these cuts |

## 3. Where the Body contradicts the target

1. **"A member path in CultNet's grammar"** (ruling 1 and fork 1 text) has nothing to reuse.
   CultNet selection deliberately has no path language. This map specifies a minimal slot
   path (section 2) owned by `GameCult.Caching`. If CultNet ever needs paths, it can adopt
   this one. Nothing is shared today, and nothing is invented twice.
2. **"Overrides are subject to the same soft drift rules as full records"** conflicts with
   the ends list: "Loading refuses an override naming a member that does not exist on the
   type". Under soft drift, a persisted slot missing locally is ignored with a warning, so
   the two rules disagree for a dropped member. Below top level, the catalog has no shape to
   drift against at all. See Q4.
3. **"Deletes first"** has little to delete: the feature is additive. The honest deletions
   are the silent extra-slot skips and C#'s ignored header. C0 puts them first.

## 4. Operator questions (one batch)

**Operator rulings, 2026-09-29, each asked on its own:**

- **Q1: (a)**, a plain `Upsert` at a variant's key is refused loudly.
- **Q2: (a)**, a base edit removing an overridden element is refused unless the same batch
  resolves it. Operator's reading: "the very existence of that element in the child collection
  must become the new override state... you can't change the parent without also changing the
  child... that's the resolve part of (a)."
- **Q3: ids everywhere, not opt-in.** Operator: "Opt-in sounds nice but consider the UX. We're
  planting a footgun if we don't require ids everywhere." Rollout: **required, minted on load**.
  The C# registry requires an id member on object-list element types. A store written before ids
  existed loads, and ids are minted once on its first write (C2's rewrite). Other runtimes carry
  the id as an ordinary member and enforce the rule when they gain variants. Each consumer's type
  edit is a sweep in its own repo after C2.
- **Q4: (c) soft drift everywhere. This reverses the recommendation and supersedes the target's
  end "loading refuses ... an override naming a member that does not exist on the type".**
  Operator: "Soft drift everywhere aligns with the vision... the rationale for adopting
  migrationless changes is still valid for variants. Any member dropped in a type change is a
  design change, the reason for it to apply to the parent also applies to the variant." A
  dangling override, at any depth, is ignored with a warning naming the variant and the member,
  and the variant inherits the base value. The other broken-variant refusals (a base cycle, a
  missing base, an incompatible base type) stand.
- **Q5: (a)**, the v2 header is written only when the store holds a variant.
- **Q6: yes**, the directory store and CultNet refuse variants loudly until their own cuts.
- **Q7: yes**, object-list elements are addressable by id, and dictionaries, scalar lists and
  arrays are replaced whole. The operator asked first whether design roles and quality levels
  use dictionaries. They do not: `CraftedItemData.Roles` is a `List<ItemRole>` and
  `FactionProduct.Roles` is a `List<ProductRole>` (Aetheria `ItemData.cs:316`,
  `FactionProduct.cs:30`). A market-segment product becomes a variant overriding one
  `ProductRole` by id.

*The questions as asked:*

**Q1. A plain `Upsert` of a whole document at a variant's key.** It happens when Studio's
current edit commits, when AetherDb migrations do `batch.Upsert(type, doc, key)`, and on a
CultNet remote write.
- (a) Refuse it loudly. A variant changes only through the variant API. Flattening is an
  explicit, separate operation.
- (b) Diff the document against the base and store the difference. Ids make the diff
  deterministic, but a value set equal to the base stops being an override without anyone
  saying so.
- (c) Flatten silently (today's behaviour by default).
- **Recommend (a).** Affects C1's write path, C3's edit commit, and every AetherDb migration
  (each skips variants or uses the variant API).

**Q2. A base edit that removes an element some variant overrides.**
- (a) Refuse the commit unless the same batch clears or retargets those overrides. Studio
  offers keep, retarget or clear at edit time, and "keep" means the base edit is not
  committed. The store never holds a conflict, and loading refuses one.
- (b) Persist the conflict. The override goes inert, readers get the resolved document
  without it, and Studio lists it.
- **Recommend (a).** Option (b) makes a reader's resolved document silently differ from what
  the author wrote. Affects C2's validation, C3's rebase UI, and migrations that remove
  behaviours.

**Q3. Scope of "ids everywhere".**
- (a) Every object-list element in every registered document type, in all 28 consumers. The
  registry refuses a type whose element types lack an id.
- (b) Only document types that opt in with `[CultVariants]`. For those, the registry requires
  `[CultElementId]` on every object element type reachable through their lists, including
  union bases.
- **Recommend (b).** Option (a) breaks 28 registries for a feature that one consumer uses.
  Affects C2 and Aetheria's adoption cut. A `BehaviorData` id needs a base slot above the
  subclasses' highest key (32), which adds about 30 nil bytes per behaviour unless Aetheria
  renumbers.

**Q4. Schema drift under overrides** (contradiction 2).
- (a) Refuse loading when an override names a member missing at any depth, or holds a value
  that will not decode into the member's current type. Record this as a stated exception to
  soft drift in `cultcache-schema-compatibility.md`.
- (b) Top-level overrides follow catalog soft drift (ignore with a warning); nested ones are
  refused.
- **Recommend (a).** It follows the target's ends and is one rule. The cost: after a type
  change drops an overridden member, the catalog must be rewritten before the new build opens
  it.

**Q5. Format version.**
- (a) Write `cultcache.store.v2` only when the store holds a variant. Stores without variants
  stay byte-identical `v1` for every consumer.
- (b) Always write v2 after the upgrade.
- **Recommend (a).** The empty payload remains the tripwire for pinned old C#, which ignores
  the header. Affects C0 and C1.

**Q6. Slice scope.** Should the directory store and CultNet refuse variants loudly until
their own cuts? The directory store's only consumer is AetheriaEve's raw reader. CultNet
must carry deltas (ruling 4), and no message can yet.
- **Recommend yes.** It is two refusals. Otherwise C1 must design CultNet messages first.
  Affects C1's size and the order of C5.

**Q7. What is addressable below a member.** The proposal: object-list elements by id only.
Dictionary entries, scalar lists and arrays are replaced whole.
- **Recommend yes.** Aetheria's cases (`Hardpoints` whole; `Behaviors[id]...Max`) fit. Keyed
  dictionary addressing can be added later without a format change. Affects C2.

## 5. Cuts, in order

All cuts are in `F:\Projects\CultLib`. Rebase `codex/document-variants` (docs only) onto
`origin/main`. Each cut is one Hands pass on that branch and merges to main after its Soul
pass. Probes show that a C1 writer can merge safely before other runtimes change, because
every reader refuses it. Verification uses `ygg-verify.sh /f/Projects/CultLib <rev> <image> '<cmd>'`.
The command strings below are the `<cmd>`. Keep the job's exit status last.

Overlap: BP-1 (`cultmesh/rust-content-plane`) edits `docs/runtime-parity-scope.md`, which
C0 also edits. Rebase after BP-1 merges. `codex/fix-node24-ajv-esm` has no diff against main.

### C0. Tripwires: every reader refuses what C1 will write, and names the record

Deletes:
- The silent extra-slot skip in the C# record decoder
  (`CultDocumentMessagePackSerialization.ReadPersistedRecord`'s trailing `Skip` loop) and
  its copy in `CultMeshSingleFileDocuments.ReadLegacyPersistedRecord`.
- The `?? "cultcache.store.v1"` defaulting of the header.
- Python `raw_record[:4]` slicing.
- The TypeScript and Python fallthrough to the legacy envelope decoder when the header is a
  `cultcache.store.*` string they do not know.

Adds, one rule in each runtime: a snapshot whose header is not `v1`, or whose record has more
than 4 slots, is refused with a message that names the version or the record key, and says
the store needs a runtime that resolves variants. C# accepts `v1` only, until C1 adds `v2`.

Keeps: every v1 byte and every v1 test.

Verification (rule: "unknown header or extra record slot is refused by name"). Each runtime
gets one test, and each test's negative is fixture B from section 1 (full payload plus slot
4) loading:
- `dotnet`: `dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~PreCut2StoreFormat`
- `node:22`: `npm install --no-audit --no-fund && cd packages/cultcache-ts && npm test`
- `python:3.12-slim`: `pip install -q msgpack -e packages/cultcache-py && python -m unittest discover packages/cultcache-py/tests`
- `rust`: `cd packages/cultcache-rs && cargo test` (message only; Rust already refuses)

Subtraction: about −20 lines of skip and fallthrough; +4 small refusals and 4 tests. Build:
the 4 cache packages only.

### C1. C# core: variant records, top-level Set overrides, resolution in the admitting hold

Adds:
- **`CultPersistedRecord.Variant`**, record slot 4: `[baseKey, overrides[]]`. Payload slot 3
  is empty for a variant. An override is `[op, path[], id, value]`.
  - Owner: `GameCult.Caching`.
  - Consumer: both file stores and Studio.
  - Invariant: an old reader refuses rather than misreads (section 1).
  - Why no existing owner serves: the payload cannot hold the delta (probe D).
- **`CultStoredDocument.Variant`** (base key and overrides; null for plain records).
  `Document` stays the resolved view.
- **A codec port on `CultCache`**: the `(serialize, deserialize)` pair `CultInspectorModel`
  already takes, lifted.
  - Owner: `GameCult.Caching.MessagePack`, supplied by `CultCacheMessagePack.Create`.
  - Consumers: resolution and the inspector model.
  - Invariant: one codec per cache; clones and override values use the store's encoding.
  - `CultInspectorModel`'s constructor takes the port instead of its own pair, so there is one
    pair, not two.
  - A cache without a codec refuses variant writes.
- **Write API** on `CultCacheBatch`, with single-call equivalents: `UpsertVariant(key, baseKey,
  overrides)`, `Flatten(key)` (Q1a), `Remove` unchanged. Rebase is `UpsertVariant` with a new
  base.
- **`Validate` resolves** every admitted variant and every dependent of an admitted or
  evicted base, in base-first order, against the post-batch set. It refuses, naming keys:
  a cycle; a missing base; a different concrete type; a global type; an evicted base with
  surviving variants; a variant whose resolved `[CultName]` equals its base's (R6); a plain
  `Upsert` at a variant key (Q1a). **Not refused (Q4 c, operator 2026-09-29):** an override
  naming a slot the type no longer has, or holding a value that no longer decodes into the
  member's type, soft-drifts exactly as the same member would on a plain record under
  `cultcache-schema-compatibility.md`. It is ignored with a warning naming the variant and
  the member, and the variant inherits the base value. One rule, the existing soft-drift
  path, never a variant-specific one.
- **`Apply` re-resolves dependents** in the same hold. Each emits `Updated` and is re-indexed.
  A base change stamps no dependent `storedAt`.
- **Header**: the store writes `v2` iff it holds a variant (Q5a) and reads `v1` and `v2`.
- **Refusals** (Q6): `DirectoryMessagePackBackingStore` push and commit of a variant;
  `CultNetDocumentRegistry` row building for a variant; `CultMesh.ReadSingleFileDocument` of
  a variant key.
- **Contracts**: the variant record section in `cultcache-persistence-format.md`; one
  sentence in `cultcache-schema-compatibility.md` saying overrides soft-drift like members
  (Q4 c, no exception); a "variants: C# resolves; others refuse" row in
  `runtime-parity-scope.md`.

Authority map:
- Owner: `CultCache.Validate`/`Apply` owns the resolved view.
- Inputs: stored deltas, base records, the codec.
- Outputs: the resolved `Document`, index entries, changes.
- Demoted: the resolved `Document` is no longer a writable owner for a variant key (Q1a).
  `storedAt` is the delta's identity only.
- Forbidden writers: store `Loaded` callbacks never resolve; Studio never writes a resolved
  document to a variant key; CultNet never emits a variant row.
- Shared paths: a load (`Admit` with a source), a single write, a batch and a remove all pass
  the same `Validate` and `Apply`. There is no second resolution path.

Verification. New `VariantTests.cs`; each rule gets a positive and a named negative:
- a load with a variant resolves;
- a base edit updates the variant's `Get`, `GetByName` and `GetByIndex`, and `Watch` sees both
  changes inside one publication, with no gap in `Sequence` between them. Negative: a
  subscriber observing the base change must already read the new variant.
- `Expect` on the variant survives a base change;
- each refusal case, and the refusal message names both keys;
- the header is `v1` without variants and `v2` with them;
- a read-only store resolves;
- a variant round-trips byte-identically through flush.

Cross-runtime vectors: the Caching interop peer gains `write-variant --file`. A test in
`packages/cultcache-ts/test/cult-cache.test.ts` asserts that TypeScript refuses that file by
name; Python and Rust tests read the same fixture from `contracts/cultcache/variant-store.cc`.

Command, `dotnet`:
`dotnet test tests/GameCult.Caching.Tests && dotnet test tests/GameCult.Networking.Tests --filter FullyQualifiedName~Registry`,
then the C0 commands for `node:22`, `python` and `rust` against the new fixture.

Subtraction: the inspector's own codec pair collapses into the port; otherwise net additive,
estimated +350 to 450 lines of core and +400 of tests. Build: the Caching and MessagePack
projects, Caching.Tests, the Networking test filter, the interop peer.

**C1 status, 2026-09-29 (Self).** C1 landed on `hands/variants-c1` (`136adce`). Soul (Opus) found it
not closeable, and a fix batch is in Hands:
- **F1:** CultNet's live-change paths leak variants as plain records, which breaks Q6.
- **F3:** an undecodable override soft-drifted where a plain record refuses to load.
- **F4:** invalid overrides were accepted at write.
- **F2:** an inherited unique index value shadows the base, and the winner depends on order.

**Self's readings and defaults, not new operator rulings:**
- **Q4 (c) read literally:** an override follows the plain-record outcome. A missing slot
  soft-drifts; a value that no longer decodes refuses loudly, as a plain record does.
- **Invalid overrides are refused at write.** Drift covers type changes after the fact, not
  bad writes.
- **F2 extends R6 to indexes.** A commit that leaves a variant sharing an indexed value
  with another record is refused.
- **One override per slot.**

**Open for the operator:** plain records with duplicate unique-index values are silently
overwritten today. That predates variants. Fixing it could refuse existing stores.

**C2a status, 2026-09-30.** Hands pushed `hands/variants-c2a` at `640ad9a`.
- Element ids are 12 hex characters. Writes mint them at random. Load mints them deterministically.
  Derived ids use `[CultElementId(nameof(Member))]`.
- The CDN chunk ref's id is its offset. The witness types carry ids, and the ids cross C#, TS, Python and
  Rust.

Soul held it. Fixes are in Hands:
- ids are never minted inside variant overrides;
- interface unions are never minted;
- a null derived source leaves an empty id;
- load minting is culture-dependent;
- the minted-on-load list goes stale;
- a refused batch leaves minted ids behind;
- the id format is unchecked;
- late union-slot reuse passes registration.

**C2a rulings (operator, 2026-09-30):**
- **Old readers are tripwired.** A store that holds element ids carries a marker that every pre-C2a reader
  refuses under C0's unknown-header rule. An old C# reader can therefore never silently strip nested ids.
  MessagePack's generated formatters skip unknown slots inside elements.
- **Duplicate element ids refuse at load,** naming the record, the list and the id. This matches the
  duplicate-index ruling.
- **C2b precondition (Soul F7).** Whether a pre-id element keeps its load-time id depends on the write path.
  C2b must refuse an override that targets a record whose ids exist only in memory, or mint on open.

**C2a Soul pass 2 (`e85b201b`), 2026-09-30: hold.** The tripwire only works against unreleased readers.
Every published release (`cultlib-unity-v1.0.60`, `caching-unity-v1.4.0`, `cultcache-ts-v0.14.0`,
`cultcache-py-v0.3.0`) predates C0. The 1.0.60 C# reader loads a v3 store, and its next write is v1 with the ids
stripped (probe). Other findings:
- F2: the same element object twice in one list commits, then the store refuses to load.
- F3: CultMesh single-file writes bypass the marker.
- F4: TS `pushAll` writes the header without reading it first.

**C2a rulings, 2026-09-30 (operator):**
- **Rollout order closes the released-reader hole.** Ship a CultLib release that refuses v3, move every C#
  reader of a store onto it, then let a C2a writer touch that store. There is no format change for this.
  This supersedes the claim above that old readers are tripwired: that claim holds only for post-C0 readers.
- **Mark by content.** A store is written as v3 (v5 for directory stores) only when a record actually holds an
  element id. An existing v3 header on disk stays.
- Fix batch 3 in Hands: F2, marking by content in every runtime, F3 routed through the cache's header
  decision and load checks, F4.

**Operator rulings, 2026-09-30:**
- **Duplicate unique-index values do not load** ("Duplicate indices should not load"). This covers
  plain records too, so R6-for-indexes becomes one general rule:
  - a commit that would leave two records holding one indexed value is refused;
  - a store holding duplicates refuses to load, and the refusal names both keys and the index.
  - The variant-specific rule `RefuseVariantIndexSharing` collapses into this rule.
  - Before it ships, every real store is scanned, so no store stops loading by surprise.
  - **Scan result (Eyes, 2026-09-30):** `[CultIndex]` had been recycled as a grouping and
    selection-filter declaration: `Category`, `Kind`, `HardpointType`, `ShardId`, and others in
    CultMesh, Geometry and docs. AetheriaEve's `aetheria-world.cc` (taxidermy) is the only store
    holding duplicates. The only single-result lookups in real code are unique:
    `PlayerData.PlayerId` and `Email` in `Server.cs`. The operator: "Pretty sure indexes are
    supposed to be unique, it was not my decision to recycle that mechanism for grouping
    purposes" and "Grouping was a desirable feature from my old DatabaseListView that didn't
    quite make it into the initial CultCache Studio port".
  - **Resolution (Self, from those answers):** `[CultIndex]` means unique. Grouping belongs to
    the Studio record-grouping campaign's own attribute (`docs/studio-grouping-cut.md` on
    `codex/studio-grouping`, attribute-based per the 2026-09-17 ruling). CultNet selection
    filters any declared member by its catalog name, so no index is needed for filtering. The
    grouping-only `[CultIndex]` declarations are deleted. This is one CultLib cut, queued
    behind C2a because both edit `CultCache.cs`.
  - The TS/Python last-writer-wins parity gap closes by the same rule when those runtimes
    adopt it.
- **CultNet invisibility is a defect** ("looks like a defect to me"): `cache.Commit` and
  `cache.UpsertAsync` on a database's cache must publish to subscribers and to the mutation
  log (`CultCache.Publish`'s `if (!loaded) continue;`).
- **Hop subscriptions keep refusing variants** until CultNet carries deltas (C5): "keep refusing,
  I guess, CultNet will have deltas by the time we're done here anyway".

### C2. C# element identity and nested paths (Set, Insert, Remove)

Adds:
- **`[CultElementId]`** (a string member). **Ids everywhere, not opt-in (Q3, operator
  2026-09-29): there is no `[CultVariants]` attribute.** The C# registry requires an id member
  on every object element type reachable through a list in any registered document type,
  union bases included, and refuses a type that lacks one.
- **Minting**: on write, the cache mints ids for unset ids in every type, and refuses a
  duplicate id within one list. A store written before ids existed loads, and its ids are
  minted on its first write (minted on load, per the operator's rollout ruling).
- **Path steps below top level.**
- **Insert** anchored after an id, where `""` means the head. An inserted run sits
  immediately after its anchor and before base elements added there later.
- **Remove** by id.
- **Canonical order**: removes, then inserts in list order, then sets by path.
- **Q2a refusal**: a base commit that orphans an override is refused.
- **Q7**: whole-member Set for dictionaries, scalar lists and arrays.
- **`CultCache.MintElementIds()`**: the one-shot rewrite for existing stores (ruling 1). It
  upserts every record that has an unset id through the normal path.
- **The consumer sweep (Q3 rollout).** About 28 consumers carry list-element types, and each
  needs an id member before it can take the CultLib that requires one. C2 ships the
  requirement. Self schedules the per-repo sweep, one small type edit per consumer, before
  those consumers bump. C2 lists the element types it finds per consumer, as data for that
  sweep.

Keeps: the C1 format unchanged. C2 fills path shapes that C1 refused.

Verification:
- Two look-alike elements (two of one union subtype) get different overrides, and a base
  insert before them retargets nothing.
- Rebase re-applies overrides by id. A missing id is refused, and the message names the
  variant, path and id.
- A Set on a removed element is refused.
- Minting is idempotent.
- A registered type with an id-less list element type is refused, naming the type and member.
- A store written before ids loads, and its ids are minted exactly once on first write
  (idempotent).
- A C#-written nested-override fixture joins `contracts/cultcache/`.

Command: `dotnet test tests/GameCult.Caching.Tests`.

Estimate +300 lines and +350 of tests. Build: as C1, without Networking.

### C3. Studio: edit the delta

Adds, in `CultInspectorModel` (engine-free; the lowering stays a renderer):
- For a variant edit, each member path is marked inherited or overridden.
- `Commit` writes `UpsertVariant` with overrides equal to the prior overrides plus a
  structural diff by id. It is deterministic because ids exist.
- An explicit "override" pins a value equal to its base's.
- `ClearOverride(path)`.
- `CreateVariant(record)` next to `Duplicate`; it prompts for a name (R6).
- `Rebase(record, base)` returns a conflict list for Q2a before commit.

Lowering, in `CultCacheStudioWindow`/`Drawers`: an overridden marker and a revert control per
member, a base link, and variant creation and rebase controls.

Verification:
- `CultInspectorModelTests`: the diff produces exactly the touched paths; clear removes one
  entry; the pin survives an equal value; rebase conflicts are listed.
- Negative: a Studio commit never calls a plain `Upsert` at a variant key.

Only the operator can check these in Unity against a copy of `Aetheria.cc`:
- the markers read correctly;
- clearing reverts the value live;
- a hull variant replaces `Hardpoints`;
- a laser variant changes one behaviour's `PerformanceStat.Max`;
- a base edit shows in the open variant.

Then tag `caching-unity-v1.5.0`. **This is the point where Aetheria is unblocked.**

Estimate +250 model, +150 lowering, +250 tests. Build: Caching.Tests. The Unity package is
checked by the operator in the editor.

### C4. Aetheria adoption (Aetheria repo, its own cut, after the release)

- Bump the package to `v1.5.0`.
- Add `[CultElementId]` to every list-element type Aetheria's documents carry (ids everywhere, Q3). There is no per-type opt-in.
- Add `[CultElementId]` to `BehaviorData` (base) and `HardpointData`.
- Run `MintElementIds` once over `GameData/Aetheria.cc`.
- AetherDb migrations skip variants or use the variant API (Q1a).
- Re-author the families listed in section 6.

### C5. CultNet deltas (C# reference)

Adds a new raw-record message version that carries the variant slot, following selection D3:
a v1 message string, so peers not in this cut refuse through their own dispatch. The C1
refusal in `CultNetDocumentRegistry` is deleted. Scheduled when a CultNet consumer needs
variants; none does today.

### Parked: TypeScript, Python and Rust resolution

Each runtime gets its own cut when a consumer that reads a variant-bearing store exists
(ruling 5). None does. `Aetheria.cc` is read by C# only.

## 6. First payoff in `Aetheria.cc`

Scan of the live file (206 records, `v1`): records of one schema whose leaves differ by at
most 20 percent, name excluded. This is a heuristic; the operator confirms the real families.

| Type | Records | Families that collapse | Records becoming variants |
| --- | --- | --- | --- |
| geardata | 32 | Refinery / Assembly Line / Deep Ore / Surface Ore / Shipyard (1 to 3 leaves differ); Small / Medium / Large Drive; RevvITup 2.0 / deep space burnout / Talaria; Skiron / Iapyx; Arctica / cold like my heart | 10 |
| weaponitemdata | 18 | ChargeBlast SG / ChargeBlast+- (14 of 253 leaves differ); pswarm / scorched void policy; Earp / 6k Shooter; Spectra / ColdFire | 4 |
| hulldata | 5 | Longinus / LonginusX (only `Behaviors`, `Hardpoints` and two cosmetic members differ) | 1 |

That is 15 of 55 equipment records. In most of these families the difference lies inside
`Behaviors`, so element-level overrides (C2) are what pay. Top-level overrides alone would
still copy each behaviour list.

## 7. Consumers

There are 28 projects: C# 13, Rust 9, TypeScript 10, Python 1, Kotlin 0 (Explore survey;
dependency-level for most, read in detail for Aetheria and AetheriaEve).
- **See variants:** Aetheria through C# `Get`, `GetByName` and `CultRecordRef` (resolved
  documents only), CultCache Studio (deltas), and AetherDb tools (must honour Q1).
- **Must stay oblivious or refuse:** every other consumer. No other project reads
  `Aetheria.cc`. Raw readers refuse through the empty payload plus C0/C1: `CultMesh`
  single-file reads, AetheriaEve's directory-page reader (the directory store refuses
  variants, Q6), and Mimir (writer only).
