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
| Single-file store file | the path; one MessagePack array `[header, catalog, records]` | replaced whole and atomically; refused when unreadable; see "Store authorities" | each runtime's one reader decides what is a store; the writer derives the header from it |
| Store lock and staging files | `<file>.lock`; a unique temp beside the store | the lock is taken by C# and Rust writers (CultMesh included, through `ReplaceDurable`); staging is removed on a failed write | the store, never CultMesh or a consumer |
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

**C2a Soul pass 3 (`9ed61dd2`), 2026-09-30: fix first.**
- A flattened record's `HoldsIds` goes stale when its base changes in the same batch.
- A conditional commit onto disk ignores content.
- Rust `push_all` clobbers headers it cannot read.
- TS keeps a stale header after the file goes away.
- Self's claim that "rollout order makes the raw-writer hole benign" was wrong. The reader that strips ids
  from a v1 store is a post-C0, pre-C2a build. That is exactly the reader the v3 marker targets, and rollout
  order does not keep it away.

**Ruling, 2026-09-30 (operator): C0 is released only together with C2a.** The first CultLib release that
refuses v3 also contains C2a, so no released reader strips ids. C0 and C1 are never released on their own.
Raw and cross-runtime writers that cannot see ids stay sticky-only. Fix batch 4 is in Hands.

**C2a F2 ruled (operator, 2026-09-30, accepting Self's recommendation B): records nobody claims are carried
untouched.** A store may hold records under a schema id that no local type owns or lists as compatible, for
example after a rename that declared nothing.
- Both store kinds carry those records byte for byte under their own id. The catalog lists them as foreign
  records.
- Writes to other records proceed.
- A write that would overwrite or relabel an unclaimed record is refused with a typed schema conflict.
- The single-file store's whole-view write stops relabelling such records onto local types. The directory
  store stops refusing every commit while it holds one.
- Why: batches 8-9 showed that the danger is rewriting a record under a new identity (F1, F6), not an
  undeclared old id. Batch 10's owner-beats-lister rule makes declaring an old id cheap anyway.
- Self's first lean, "refuse loudly" (A), is superseded.

**C2a batches 9-10 (Soul pass on `fdb8a60a`, 2026-09-30): hold.**
- **F6, A-B-A lost update on the single-file store.** Ruled by Self: any write that changes a record's bytes or
  schema id mints a later `storedAt`.
- **F8, batch 9 over-refused.** An owner beats a lister, so v1 and v2 coexist.
- **F9.** Compatible ids compare as a set.
- **F10.** Typed refusal errors in TS and Python.
- Batch 10 is in Hands.
- **F7 ruled (operator, 2026-09-30): retire `AsSchemaAlias`.** "Schema aliasing sounds like a really bad idea, I
  support retiring it."
  - C# refuses a second type for one schema, as TS, Python and Rust already do. There is one type per schema in
    every runtime.
  - The API, its registry path (`IsExactWireAlias`) and the tests that exercised it (7 Mesh, 2 Caching) are
    deleted or rewritten.
  - No consumer called it.

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

## Schema identity across runtimes (Imagination, 2026-09-30)

**RULINGS (operator, 2026-09-30).**
- **C1: agreed** ("Agreed on schema identity"). C# puts the declared version string on the wire as the schema id.
  F-ID.1 follows Self's recommendation: the id is `<name>.v<N>`, not the bare schemaName.
- **F-ID.2: AetheriaEve is not a consumer.** Operator: "AetheriaEve is purely archaeological, I would recommend
  archiving it purely as evidence of what happens when you try to build a game with hyperfocused agents and with
  CultLib under construction. We might try again at some point, but it won't look anything like AetheriaEve." C1
  deletes the fallbacks that exist only for AetheriaEve's readers. It does not migrate those readers.
- **F-ID.3: stored C# hash ids.** Operator: "I have no idea". Self's default is to defer C2. C1 changes nothing on
  disk. C# keeps reading its own `sha256` ids. F2 already makes other runtimes keep C# records byte-for-byte. C2
  gets mapped only when a cross-runtime store read of a C# store is actually needed.
- C1 is mapped (section "C1 cut: one schema id on the wire", below). **Operator, 2026-09-30: "Yes to both, defaults
  are fine".** The per-binding wire-id override is deleted. A declared compatible id resolves on the wire, and the
  owner beats a lister. All six defaults stand. Stonks is fixed in Stonks before C1 merges. Order: F2 Soul, then the
  put-serve merge, then c2a merges main, then C1.0-C1.5.

Probed at CultLib `hands/variants-c2a` 92d9e138. The probe ran on Yggdrasil in `ack1d-interop:2` from a scratch branch, which has since been deleted. Sources: `scratchpad/schema-identity-probe/`. Full output: `scratchpad/probe2.log` (store round trip, section 1), plus a wire rerun shown inline below. Every probe used one declared schema: the interop note, name `cultcache.interop-note`, version `cultcache.interop_note.v1`, with six members.

### 1. How each runtime derives a schema id (probed)

| Runtime | Id it stamps | Derived from | Carries name / version / content hash? |
|---|---|---|---|
| C# | `sha256:eafe3e…b249` | `sha256("name\|version\|slot:Member:ClrType:value/ref:target:one/many\|…")`. Recomputing it by hand gives the same id. | Store catalog: name, version, `contentHash = sha256(canonical JSON)`, canonical JSON, members. Wire: `schemaName`, `schemaVersion`, `schemaContentHash` are all filled. |
| TS | `cultcache.interop-note` | `definition.schemaId ?? schemaName ?? type`. There is no hash. With no declared version, the version defaults to `<name>.v1`. | contentHash is whatever the author declares. The interop test declares the id string itself. |
| Python | `cultcache.interop-note` | `schema_id or type`. There is no hash. contentHash defaults to the id. | Same as TS. |
| Rust | `cultcache.interop-note` | `#[cultcache(type=…)]`. There is no hash. `schema = "CultCacheInteropNote"` never reaches the catalog. | The catalog entry it writes is fabricated: version `"<type>.v1"` (`cultcache.interop-note.v1`, not the declared `…_note.v1`), `members: []`, `contentHash = type`. |
| Kotlin (CultMesh only; it has no store) | `cultcache.interop_note.v1` | `codec.schemaVersion` | Nothing else. |

On the C# fingerprint:
- **The id stays the same** when only the CLR class or namespace changes (V1).
- **The id changes** when a member is renamed (V2), a slot is appended (V3), `string[]` becomes `List<string>` (V4), the version string changes (V5), or a slot is removed (V6).
- **V2 and V4 produce identical wire bytes.** MessagePack slot arrays carry neither member names nor CLR collection types. So the fingerprint witnesses the C# declaration, not the wire layout.

The C# registry already refuses two CLR types for one `(name, version)` in a process. The probe crashed on exactly that when V6 and the base type shared an assembly. Inside C#, `(name, version)` is already the ownership key, and the hash adds nothing to ownership.

### 2. Wire parity for identity does not hold (probed)

No two runtimes agree, and C# is the only one that hashes. The CultNet interop harness is the parity witness in CI. It never exercises the C# default: `tests/GameCult.Networking.InteropPeer/Program.cs:77,201,1183-1196` binds every type with `ForDocument(schemaId: <readable JSON-schema URL>)`.

What reads what today:

- **Stores (`.cc`). C#, Rust, TS and Python all read each other's notes.** It works only because each reader, when the id is unknown, falls back to the embedded catalog's **schemaName**: C# `CultCache.cs:653` `BySchemaName`, TS `cult-cache.ts:827`, Rust `resolve_registered_type` `lib.rs:2754`. Round trip, C# → X → C#:
  - Each writer stamps its own record with its own id.
  - **A C# rewrite restamps every other runtime's record to `sha256:…`** and drops their catalog entries.
  - **A Rust write destroys the C# catalog entry for the id it read.** The entry becomes name=`cultcache.interop-note`, version=`sha256:…eafe….v1`, contentHash=`sha256:…eafe…`, canonical JSON with `schemaName: "sha256:…"`, `members: []`. The C# record bytes survive, but their description is lost. That breaks F2 ("never relabels…") at the catalog level, and it is a defect whatever this fork decides.
  - TS and Python kept the C# catalog entry intact.
- **Wire (CultNet raw record carrying a C# `sha256` id):**

| Reader | `sha256:…` | `cultcache.interop-note` (name) | `cultcache.interop_note.v1` (version) |
|---|---|---|---|
| TS `CultNetDocumentRegistry.applyRawDocumentPut` (probed) | refused | ok | refused |
| Kotlin `CultCache.putRaw` (probed) | refused | ok (documentType) | ok |
| C# `CultNetDocumentRegistry.DeserializeRawDocument` (probed) | ok | ok | ok, **and `gamecult.unknown.v1` → `Note` too** |
| Python `cultnet_py` `resolve_document_for_raw_record` (read only) | only if declared | ok | ok, plus payload sniffing |
| Rust `pull_rudp_catalog_snapshot` (read only, per the Eyes sweep) | dropped silently | — | strips `.vN` and matches the type |

C# accepts any id because `TryResolveDescriptorBySchemaAlias` and `TryResolveDescriptorByPayloadSchema` (`CultNetDocumentRegistry.cs:755-830`) read slot 0 of the payload as a schema version. Python does the same (`replication.py:160-196`). **Shape routing and aliasing are still live in C# and Python CultNet on c2a**, and so is C# request matching `CultNetSchemaAliasMatching` (`CultNetDatabase.cs:52-89`). F7 has not reached them.

- **The C# wire id is readable only when the binding is overridden.** The probe showed `ForDocument<Note>(schemaId: "cultcache.interop-note")` emits that id with the same payload, name, version and contentHash. `CultMesh.CreateCultNetDocumentRegistry` (`src/GameCult.Mesh/CultMeshDocumentRegistries.cs:69`) never overrides it, so every C# CultMesh host publishes `sha256` ids.

### 3. Live cross-runtime paths that depend on the fallback

This section comes from the Eyes sweep. I spot-checked the Hermodr lines, the Odin rule and `CultMeshDocumentRegistries.cs`; the rest I only read. Every one of these paths is **TS reader → C# host (the Aetheria daemon in `F:\Projects\AetheriaEve`)**.

| # | Consumer | Asks for | Works via | Under C1 |
|---|---|---|---|---|
| 1 | `EveElectron/src/cultmesh-provider-client.mjs:129-143`, started by `AetheriaEve/Aetheria.Rts.Web/Electron/main.ts:65` | `gamecult.eve.provider_advertisement.v1`, `…surface.v1`, `gamecult.fields.*.v1`, `…command_receipt.v1`, `gamecult.cultmesh.cdn.asset_blob.v1` | cultmesh-ts HELD `firstRecordAtKey` | Exact match. The exception is `cdn.asset_blob.v1`: **no writer anywhere declares it**, so that read needs a declaration. |
| 1b | same file, lines 94-108 (command put) | `gamecult.eve.command_invocation.v1` | C# inbound alias (g), probably | Exact match |
| 2 | `AetheriaEve/Aetheria.Rts.Web/Electron/aetheria-cultmesh.ts:138-147, 345-406` (only the verify-stage7* scripts use it) | `gamecult.aetheria.*.v1`, `gamecult.fields.*.v1` | first-record fallback | Exact match |
| 3 | `Hermodr/src/hermodr-daemon.cjs:1335-1363` | provider state, surfaces, CDN | Its own `|| candidates[0]` / `|| documents[0]`, plus C# request alias (f) | Exact match. Delete both fallbacks. |
| 4 | `AetheriaEve/scripts/aetheria-browser-provider-witness.ts:49-60` (a witness, not product code) | eve schemas | cultmesh-browser `recordMatchesSchema` name/version arms | Exact match |
| 5 | `aetheria-cultmesh.ts:408-411`, local `.cc` read | same ids | none: probably already broken (unverified) | still a store-path question |

**Paths that already work without a fallback, because everyone hand-stamps `<name>.vN`:**
- The Aetheria daemon and Gjallar hand-stamp `.v1` puts to Odin: `Program.cs:4574-4830` and `Gjallar/Program.cs:3040-3080`.
- Odin **refuses any id without `.vN`**: `Odin/crates/odin-daemon/src/main.rs:1310-1326`.
- Rust hosts bind `.vN` through `cultmesh_documents!`: Ghostlight, Epiphany, Muninn.
- Kotlin uses its schemaVersion.
- The Eve C# declaration itself reads `[CultDocument("gamecult.eve.provider_advertisement", SchemaId)]` with `const SchemaId = "gamecult.eve.provider_advertisement.v1"`. Authors already treat the version string as the id.

Scope flag: every broken consumer lives in `AetheriaEve`, which the CultCache campaign ruled "taxidermy". The operator should confirm those paths are live consumers of this campaign before any cut is shaped for them.

Stored data:
- `Aetheria/GameData/Aetheria.cc`, `run.cc` and `player.cc` hold 21 C# schemas under `sha256` ids. The Eyes sweep found only C# readers.
- The versions there are the bare string `"1"`: `[CultDocument("aetheria.faction", "1")]`. So a version string is not globally unique across the estate.
- TS stores (Bifrost, VoidBot, weksa, Stonks) are TS-only and hold readable ids.
- Odin's Rust store holds `.vN` ids.

### 4. What the C# fingerprint protects

A C#-to-C# store read. A record whose declaration drifted misses the exact id and falls to the persisted-catalog comparison: `CompatibleDrift`, or refused as incompatible (`CultCache.cs:900-936`).

Across runtimes it protects nothing today:
- The other runtimes do not compute it.
- Their content hashes are placeholder strings.
- The C# wire receiver accepts any id through payload sniffing (probed).
- The hash reacts to renames and CLR collection spellings, which do not change wire bytes (probed V2, V4).

### Fork F-ID: what names a schema across runtimes

**(A) Every runtime computes the C# semantic fingerprint; readers name schemas by definition.**
- Every non-C# runtime has to emit C# type spellings: `System.String[]` versus `System.Collections.Generic.List<System.String>`.
- Rust has to start declaring members and versions at all.
- String-named readers can no longer name a schema. Eve Electron and Hermodr read ids from provider advertisements.
- Odin refuses the ids outright (`.vN` rule).
- Every TS, Python and Rust store holds readable ids. Under no-relabel those records become permanently held unless each runtime lists its old ids.
- Gain: a layout witness that is still over-sensitive (V2 and V4 change the id with no wire change).
- Stored data: Aetheria is untouched; every non-C# store is orphaned.
- Not recommended.

**(B) Readers match the declared `(schemaName, schemaVersion)` pair the envelope carries.**
- The C# wire already carries both. Store catalogs carry both.
- Two identity strings stay in play: the id for storage, the pair for lookup. That is a split owner.
- `sha256` ids still reach Odin, which refuses them.
- The version alone is ambiguous (Aetheria's `"1"`), so every reader API has to take a pair instead of the one string readers pass today.
- Rust writes a fabricated version, so pair matching against a Rust host fails until Rust declares versions.
- Layout protection is only as good as a contentHash comparison, and no runtime but C# computes a real one.
- Stored data: unchanged, and nothing is relabelled. Resolution becomes pair matching against the catalog, which is a formalised form of today's schemaName fallback.

**(C) C# puts the readable id on the wire, and the id is the declared version string `<name>.v<N>`.**

This is what Odin enforces, Rust and Kotlin use, and Eve and Gjallar already hand-stamp.
- C1, wire only:
  - `ForDocument` and `CultMesh.CreateCultNetDocumentRegistry` default the binding id to `descriptor.SchemaVersion`.
  - Registering a wire binding whose version lacks `.vN` is refused. Aetheria's `"1"` types are not on the wire today.
  - Then delete the HELD first-record fallback (cultmesh-ts), Hermodr's two fallbacks, the name and version arms of cultmesh-browser `recordMatchesSchema`, C# `TryResolveDescriptorBySchemaAlias`, payload sniffing and `CultNetSchemaAliasMatching`, and the Python CultNet alias map and sniffing. That finishes F7 on the wire.
- C2, the store as well, as a later cut: C# stamps new writes with the version id and resolves an old `sha256` record by the type's own **computed** former fingerprint. That is a derivation, not an alias. Where a version string changes (Aetheria's `"1"` → `aetheria.faction.v1`), the old id goes into the existing `CompatibleSchemaIds`.
- The fingerprint is demoted to `schemaContentHash`, which the wire already carries. It stays a C#-to-C# drift witness and does not decide identity.
- Costs:
  - C#: the default in two files, plus deleting the alias and sniffing paths.
  - TS, Hermodr and Python: deletions only.
  - Rust and Kotlin: none.
  - Mixed-version C# peers: an old host sends `sha256` and a new receiver without sniffing refuses it, so C# peers upgrade together.
  - Eve Electron `cdn.asset_blob.v1`: needs a declaring writer.
- Stored data: C1 changes nothing on disk. Under C2, Aetheria's catalogs are read unchanged and records are restamped only when C# rewrites records it understands, which F2 allows. TS, Rust and Odin stores are untouched.

**Recommendation: C1 now; decide C2 separately.** C1 is the only option where one string names a schema on every runtime and at Odin, where every reader already asks for that string, and where the work is almost entirely deletion. It makes the F7 retirement possible on the wire, because readers no longer need a fallback to find their records. Aetheria's `.cc` files are untouched. B keeps the split and A breaks every readable-id store. Separately from F-ID, the Rust catalog-destruction defect (§2) and the live C# and Python CultNet aliasing should be admitted as findings or follow-ups whatever is ruled.

**Sub-questions for the operator:**
- F-ID.1: Is the id the version string (`<name>.vN`, as Odin, Rust, Kotlin and Eve do), or `schemaName`, which CultLib's own TS and Python tests use and which is also TS's default?
- F-ID.2: Are the AetheriaEve consumers live for this campaign?
- F-ID.3: Should C2 (store identity) be mapped now, or deferred until a non-C# reader of a C# store exists?

### Probed versus read

- **Probed:**
  - All C# ids and variants, and the hand-recomputed fingerprint.
  - The TS and Python default ids.
  - Rust's id and catalog entry, from store bytes.
  - Kotlin's id and its accept/refuse behaviour.
  - Store round trips C#→Rust/Python/TS→C#, with bytes, including the C# restamp and the Rust catalog destruction.
  - C# wire record bytes, default and overridden.
  - C# CultNet accepting any id.
  - TS CultNet exact-id behaviour.
- **Read only:**
  - Python CultNet aliasing and sniffing, and Rust `pull_rudp_catalog_snapshot` dropping unknown records.
  - C# request alias matching (f).
  - Every consumer path in §3, from the Eyes sweep. I spot-checked Hermodr 1335-1363, Odin 1310-1326, `CultMeshDocumentRegistries.cs:69` and the Eve declaration.
  - Path 1b/2b's dependence on (g).
  - Path 5 being broken.
  - The Aetheria `.cc` catalogs were decoded from the files, with no git.

## C1 cut: one schema id on the wire

Imagination, 2026-09-30. This maps the operator's C1 ruling. On the wire, a document's schema id is its declared
version string, `<name>.v<N>`. Every runtime stamps it and every runtime resolves it exactly. A wire binding whose
version lacks `.v<digits>` is refused. The AetheriaEve readers are archaeology, and C2 (store identity) is deferred.

**Anchors.** Line numbers are against CultLib `hands/variants-c2a` at `976b57f1`. That commit is F2 in progress, the
Rust catalog lay-back. I read it from a `git archive` snapshot and did not touch the worktree. Where main
(`4a7fa1d6`, code as of `daedfdd6`) or `hands/cultmesh-put-serve-bound` (`fa34b685`) differs in a file C1 edits, it is
said at the spot. Each anchor also names its symbol, so it can be found again after c2a merges main (see Sequencing).

### 0. Probe facts

These are new and were run, not inferred. The sources are in `scratchpad/c1-probe/`. They ran on Yggdrasil in
`ack1d-interop:2` from the scratch ref `refs/scratch/c1probe`, a detached commit on `976b57f1` that is deleted after
this pass. Output is in `scratchpad/c1-probe3.log`. The run was commit `43677b97ac`.

- **P-VER: a C# version change does not orphan stored records.** The probe used two builds of one type,
  `probe.faction`, declared first with version `"1"` and then with `"probe.faction.v1"`, plus a sibling
  `probe.ship "1"`. The same members.
  - The id changes: `sha256:64eb…` becomes `sha256:0603…`.
  - The new build reads the old store through the catalog's schema name (`Claimant`, `CultCache.cs:697`) and reports
    `CompatibleDrift` with warnings `compatible_schema_id_drift` and `content_hash_drift`. The record decodes.
  - **Opening and flushing without edits leaves the file byte-identical** (1529 bytes, old id).
  - **Upserting restamps** that record and its catalog entry to the new id. The untouched sibling keeps its own.
  - The old build then reads the rewritten store the same way.
  - So a version-string change is a restamp on the next write, not an orphan. This holds only while one local type owns
    the schema name. Two local versions of one name make the fallback ambiguous and it throws (`CultCache.cs:697-705`).
  - C1 does not need this. It is recorded for C2.
- **P-WIRE: what C1 changes on the C# wire.**
  - Binding both types by their version string: with `"1"` twice, c2a's `Register` refuses the second binding
    (`CultSchemaConflictException … schema id "1"`). With `probe.faction.v1` and `"1"`, it accepts both.
  - The default wire id is still sha256 in both builds.
  - With an unbound registry, `DeserializeRawDocument` returned `Faction` for `"1"`, `probe.faction` (the name) and
    `probe.faction.v7` (a version nobody declared). Only `some.other.v1` was refused.
  - So today a C# receiver resolves a version it has never seen by stripping `.vN` and matching the name. That is the
    path C1.0 deletes, and the negative check in §8 pins its death.

Earlier facts this map rests on, from the §1-§4 probes above: C# hashes the version into the id (V5). The C# wire
id is sha256 unless the binding overrides it. C# CultNet accepts any id through sniffing. TS refuses a version id.
Kotlin stamps its version.

### 1. What the consumer sweep settles

This comes from three Eyes-class sweeps over F:\Projects. I read the rows marked (R); the rest were confirmed by the
sweeping agent reading the files.

- **Aetheria's `"1"` versions never reach the wire.** Aetheria has no CultNet or CultMesh code. `Aetheria.Shared.csproj`
  references only the caching assemblies. So C1's refusal never fires for its 24 declarations, and **nothing in
  Aetheria changes, on disk or in source.**
- The other C# `"1"` declarations are in StreamPixels `apps/overlay-unity` (24). They are client-only and never bound:
  frame ids map to types by hand (`OverlayFrameDocuments.cs:98-116`).
- Every C# type that is bound in any repo already declares `.vN`: AetheriaEve 61, AquaSynth 28, Mimir 37, Brokkr,
  Eve, Gjallar, Ymir, Delvehold. **No repo outside CultLib passes `ForDocument(schemaId:)`.**
- **Live C# hosts with default (sha256) bindings:**
  - AquaSynth, `AquaSynthCultNetDaemon.cs:431-449`, 19 bindings. Its peers are C# (`AquaDaemonClient.cs`).
  - Mimir: `EveDashboard/Program.cs:877-886`, read by Eve Android, which ignores schema ids
    (`MainActivity.kt:618-625`); and `CultMeshMedia/Program.cs:277`, C#.
  - Delvehold: no server.
  - All of these are C#-to-C# or id-blind. They change id together when they rebuild on C1, so no shim is needed.
- **The C# publishers to Odin bypass bindings.** AetheriaEve `Program.cs:4574-4831` and Gjallar `Program.cs:3045,3069`
  build their ids by hand and serialize payloads directly. C1 does not change them. Odin's rule is at
  `Odin/crates/odin-daemon/src/main.rs:1311-1326` (R).
- **Every Rust wire binding in every repo already binds `.vN`** through `cultmesh_documents!` or
  `for_entry_with_schema_id` with `.vN` constants: Odin, Muninn, Ghostlight, Epiphany, Ratatoskr, Huginn. No consumer
  uses bare `for_entry::<T>()`.
- **The AetheriaEve readers are archaeology:**
  - EveElectron's only code consumer is AetheriaEve (`Aetheria.Rts.Web/package.json:30`).
  - Hermodr was parked on 2026-09-30: `gamecult-ops/inventory.md:631-634`, `runbooks/odin-yggdrasil.md:35-48`, and
    `scripts/idunn/idunn-deployment-targets.ps1:36-46` (status "archived").
  - cultmesh-browser has two consumers. One is the AetheriaEve witness. The other is CultLib's own sample
    `samples/eve-browser-network/browser/main.ts:8`.
  - **So C1 edits neither Hermodr nor EveElectron.** Both are archived, so C1 deletes the CultLib fallbacks that exist
    only for them and does not migrate them.
- **Non-C# consumers that change id under C1:**
  - **Stonks is the only one that breaks.** `F:\Projects\Stonks\src\stonks-daemon.cjs:152-195` declares
    `schemaVersion: "v1"` on six definitions, one of them `provider_advertisement` published to Odin at `:303`. Under
    C1 its TS registration is refused. Stonks loads `../CultLib` from disk, so it breaks on the first restart after C1
    reaches that checkout. The fix is in Stonks: set each `schemaVersion` to its current `schemaId`. That is six lines,
    and they land before C1 is merged to main.
  - Bifrost `tools/agent-transport.mjs:62-66`: the id is hyphenated (`…update-request.v0`) and the version has
    underscores (`…update_request.v0`). The wire id changes to the version. The snapshot and apply sides are the same
    tool on one pinned CultLib (`f67f5122`), so they change together. Its store keeps the hyphenated id.
  - Brokkr Python `surfaces/blender/brokkr_bridge/blender_target.py:630-637` declares eight type-only definitions whose
    type already ends in `.v0`. The Python default version would make the wire id `….v0.v1`. The fix is in Brokkr:
    declare `schema_version=<the type>`. No live reader was found, so this is hygiene, not a break.
  - repixelizer: `verse_state.py:139-230` declares version ≠ id. Its wire id moves to the `.v0` version, which Odin
    accepts.
  - Everything else already has id == version: VoidBot, Vili, weksa, Heimdall, StreamPixels, Bifrost's other tools,
    Hermodr.
  - VoidBot's persona stores use a vendored cultcache-ts and are untouched.
- **Raw health puts are out of C1's reach.** Vili, Stonks, weksa, Bifrost and repixelizer send hand-built
  `idunn.daemon_health` with no version, and Bifrost sends `idunn.signed_daemon_health`. These never register a
  binding. No receiving binding was found in Idunn or Odin main. That is Soul's consumer check, not a C1 edit.

**Q3, settled:** no declaration in any repo has to change to satisfy the refusal on a binding that actually exists.
Aetheria and the StreamPixels overlay are the `"1"` repos, and neither binds. Stonks's `"v1"` is TS; it is fixed in
Stonks and touches no store. Stonks's store keeps the ids it holds today, because TS stores under `schemaId`. So
**no declaration change touches disk.** Whether a future change of Aetheria's version string would orphan its records
is the P-VER probe in §0. It is recorded for C2 and not needed by C1.

**Q4, settled:** C1 changes no store record in any runtime. The store id and the wire id become two derivations:
- C#: sha256 on disk, version on the wire.
- TS: `schemaId ?? schemaName ?? type` on disk, version on the wire.
- Python: `schema_id or type` on disk, version on the wire.
- Rust: the store id and apply-path stamping are unchanged (see C1.4).
- Kotlin: version for both, as today.

Four places where one variable serves both today must be split. Each has its cut below:
- TS `replication.ts:363-368`, `schemaIdForEnvelope`, which serves the stored id.
- Python `cultmesh_py/node.py:767` and `interop_peer.py:1085`.
- The C# durable shard log, `CultNetShardMutationLogStorage.cs:75-88`. It persists wire-form entries, and so it is the
  one C# store that holds wire ids.
- Rust `pull_rudp_catalog_snapshot`, `cultmesh-rs/src/lib.rs:742`.

### 2. Authority map

- **Owner.** The declaration's version string is the only wire identity:
  - C# `CultDocumentAttribute.SchemaVersion` → `CultDocumentDescriptor.SchemaVersion`;
  - TS `definition.schemaVersion`, defaulting to `${schemaName}.v1`, through `schemaIdentityOf(...).schemaVersion`;
  - Python `schema_version`, defaulting to `f"{schema_name}.v1"`;
  - Rust: the version given to `cultmesh_documents!` or `for_entry_with_schema_id`;
  - Kotlin `codec.schemaVersion`.
- **One derivation per runtime:**
  - C#: `CultNetDocumentRegistry.WireSchemaId(descriptor)` returns `descriptor.SchemaVersion`. It is the single place
    `.vN` is checked, and it is also called eagerly from `Register`.
  - TS: `wireSchemaIdOf(definition)` in cultnet-ts.
  - Python: `wire_schema_id(definition)` in cultnet_py.
  - Rust: `CultNetDocumentBinding::schema_id`, which is already the version.
  - Kotlin: `codec.schemaVersion`.
- **Inputs:** the declaration only. Never a payload, a schema name, an inferred name, a record's position in a
  response, or a stored record's id.
- **Outputs:** every schema id that crosses a process boundary:
  - raw records, puts and deletes;
  - snapshot and selection responses, headers, edges and cursors;
  - database change messages and shard-log entries;
  - snapshot, subscribe and selection filters, and the shard catalog;
  - the document-payload schema catalog;
  - CultMesh projection-source and state-binding records.
- **Resolution.** A wire id resolves to the binding that owns it (exact). Otherwise it resolves to the one binding that
  *declares* it compatible (`CompatibleSchemaIds`, owner beats lister), which is the rule stores already use. **OQ-C1.2
  decides whether declared compatibility applies on the wire.** Anything else is refused.
- **Derived state after C1:**
  - C# `descriptor.SchemaId` (sha256) is **store-only**. It keys CultCache, `_predictedDocuments`,
    `_lastWriteSequence`, and in-process dictionaries.
  - TS and Python `schemaId` are **store-only**.
  - `CultNetDocumentBinding.SchemaId` is **derived** (the version). It is no longer an owner.
  - `schemaName` is **metadata only**, never matched.
  - Payload slot 0 or `schemaVersion` is **data only**, never read for routing.
  - `schemaContentHash` is a **C#-to-C# drift witness**, carried and never matched.
- **Forbidden writers, all deleted:**
  - C#: `CultNetSchemaAliasMatching`, `TryResolveDescriptorBySchemaAlias`, `TryResolveDescriptorByPayloadSchema`,
    `PayloadMatchesSchema`, `TryReadSchemaVersion`, both `InferSchemaName`, `ResolvePayloadSchemaCandidates`,
    `ExpandSchemaBindingAliases`, the `ForDocument(schemaId:)` override (OQ-C1.1), the CultMesh `aliased` and
    `byPayload` branches, and the descriptor-name and version keys in the C# and TS mesh catalogs;
  - TS: `firstRecordAtKey`, cultmesh-browser's name and version arms, `shard-catalog.ts` `inferSchemaName`, and
    `inferCultMeshSchemaName` / `bySchemaNameVersion`;
  - Python: the name and version keys in `schema_document_map`, `_infer_schema_version_from_payload`, and every
    `_infer_schema_name`;
  - Rust: `strip_schema_version_suffix` and `selection::schema_alias`;
  - Kotlin: `codecs[schemaId]`, the documentType fallback.
- **Shared paths.** Put, delete, snapshot (v0, v1, raw), selection, subscription, database change, shard log (live and
  replayed from disk), shard catalog, schema catalog, and CultMesh peer reads all take the id from the one derivation.
  A served stored record is stamped by the derivation, never by its stored id.

### 3. Deletes first (C1.0, subtraction)

C1.0 deletes every inference path and makes each comparison exact against the existing wire id: the binding id, or
sha256 for an unbound type. C#-to-C# peers keep working. Only readers that relied on a fallback stop resolving, and
those are the AetheriaEve readers, which are archaeology. C1.0 adds no behaviour; C1.1 flips the id.

**C#, `src/GameCult.Networking`:**
- `CultNetDatabase.cs:47-104`: delete the whole `CultNetSchemaAliasMatching` class (58 lines, public).
  - Replace every caller with an exact `string.Equals` against the registry's wire id for the descriptor.
  - Callers: `:143, :150` (`ClientAuthorityScope.Matches`); `:252, :260` (`Shard.Matches`); `:1757` (shard-catalog
    filter).
  - `Shard.Matches(string)` and `ClientAuthorityScope.Matches(string)` compare a configured id with the wire id.
  - `EnsureClientAuthority(string)` at `:1640-1652` has no callers; delete it.
- `CultNetDocumentRegistry.cs`:
  - Delete `:779-799` `TryResolveDescriptorBySchemaAlias`, `:813-830` `TryResolveDescriptorByPayloadSchema` (public),
    `:832-842` `ResolvePayloadSchemaCandidates`, `:852-854` `PayloadMatchesSchema` (public), `:865-898`
    `TryReadSchemaVersion`, `:900-910` `InferSchemaName`, and `:538-578` `ExpandSchemaBindingAliases` with its comment.
    About 175 lines in all, doc comments included.
  - `ResolveDescriptorForRawDocument` (`:755-765`) and `ResolveDescriptorForSchemaId` (`:767-777`) collapse into one
    exact lookup. It uses `_bindingsBySchemaId`, or, for a registry with no bindings, the one descriptor whose wire id
    equals the id. A miss throws `InvalidOperationException` naming the id.
  - Call sites stay on the one method: `CultNetDatabase.cs:717, 947, 998, 1234, 1455`,
    `CultNetDatabaseSubscriptionClient.cs:254`, and `CultNetDocumentRegistry.cs:691, 713`.
- `CultNetDocumentRegistry.cs:407, 507`: drop the `ExpandSchemaBindingAliases` calls. `CultNetDatabaseServer.cs:566-567`
  and `CultNetDatabaseSubscriptionServer.cs:559-560` pass the selection unchanged.
- `CultNetSelection.cs:393, 453` and `CultNetSelectionEvaluator.cs:88, 422`: exact comparison against the wire id.
  - The evaluator reads only descriptors, so C1.0 gives it the registry's `WireSchemaId` as a `Func<CultDocumentDescriptor,
    string>`.
  - After C1.1 the id is `descriptor.SchemaVersion`, and the parameter collapses to that (see C1.1).
  - `CultNetSelection.cs:178` doc: "schema ids", not "or aliases".

**C#, `src/GameCult.Mesh`:**
- `CultMesh.cs:2724-2774` `ReadDocumentFromSnapshotResponse`: delete the `aliased` and `byPayload` branches
  (`:2744-2745, 2754-2757`). Pick `exact` or throw. Rewrite the comment block at `:2724-2733`.
- `CultMesh.cs:3441-3464` `IsSameCultDocumentSchema`: the id is local (a `CultNetDatabaseChange` sha256). Replace with
  `schemaId == descriptor.SchemaId`, or `storedDescriptor.SchemaId == descriptor.SchemaId`. Delete the comment
  claiming "every … decision goes through" the matcher.
- `CultMeshSnapshots.cs:982-985` `DecodeSnapshotDocuments`: keep only `canDeserializeWithBinding`, or
  `record.SchemaId == wire id of TDocument`.
- `CultMeshDocumentRegistries.cs:64-71`: the `GroupBy(SchemaId)…Reverse()` loop is left over from aliasing. With c2a's
  `Register` refusal, collapse it to one `Register` per distinct type.
- `CultMeshPrimitives.cs` ~`1806-1860` and ~`1996-2050`, the C# `CultMeshDocumentCatalog` and
  `CultMeshCollectionCatalog`: drop the schema-version and schema-name keys. A lookup by schema answers by the handle's
  type only. **A lookup that more than one handle answers is refused, naming their document ids.** This is the "typed
  catalog lookup returns the last handle added" defect, and TS has the same fix below.

**TS:**
- `cultmesh-ts/src/index.ts:6297-6328` `requestCultNetRawSnapshotDocument`: delete the HELD comment, the
  `firstRecordAtKey` option, and `?? atKey[0]`. Delete the caller's `firstRecordAtKey:` at `:2691`.
- `cultmesh-ts/src/index.ts:6175-6191` `normalizeCultMeshDocumentSchema`: drop the `inferCultMeshSchemaName` arm and the
  version-from-id arm, and delete `inferCultMeshSchemaName` at `:6228-6238`.
- `cultmesh-ts` catalogs: `CultMeshDocumentCatalog` `:1462-1519` and `CultMeshCollectionCatalog` `:1688-1731`.
  - Delete `#bySchemaNameVersion`, `cultMeshSchemaNameVersionKey` (`:6220-6226`), and its arm in
    `cultMeshRefuseSecondCatalogType` (`:6197-6218`).
  - `tryDocument` and `tryCollection` answer by type or by wire id. When `#byType` holds several handles for one type
    (`add` at `:1482` overwrites, so today the last one wins), the lookup is refused with the handles' document ids.
    That needs `#byType: Map<string, handle[]>`.
- `cultmesh-browser/src/index.ts:770-774` `recordMatchesSchema`: exact `record.schemaId === requestedSchema`. Callers
  are `:704, :721`. After C1.2, CultLib's `samples/eve-browser-network` asks by version.
- `cultnet-ts/src/shard-catalog.ts:103-130`: `schemaIdsMatch` becomes `===`. Delete `inferSchemaName`.

**Python:**
- `cultnet_py/replication.py:21-30` `schema_document_map`: key by wire id and compatible ids only; drop `schema_name` and
  `schema_version` keys that differ from the wire id.
- `replication.py:158-194`: `resolve_document_for_raw_record` becomes `dict` lookup or `KeyError`. Delete
  `_infer_schema_version_from_payload` and `_infer_schema_name`.
- `cultnet_py/shard_catalog.py:143-154`: exact.
- `cultmesh_py/node.py:1311-1343`: `_schema_matches_request` and `_document_matches_schema_id` become exact; delete
  `_infer_schema_name`.
- `cultmesh_py/server.py:687-721`: the map copy and request filter become exact; delete `_infer_schema_name`.
- Keep `_documents_alias` / `_resolve_document_alias` (`node.py:1188-1243`). They compare local definitions, not wire
  ids (follow-up F-C1-PYALIAS).

**Rust:**
- `cultmesh-rs/src/lib.rs:750-773`: delete `strip_schema_version_suffix`. `registered_document_type_for_schema` becomes
  `binding_by_schema_id(..).map(|b| b.document_type.clone())` and nothing else. Delete the `documents.binding(schema_id)`
  type-name arm.
- `cultnet-rs/src/selection.rs:709-851`: delete `schema_alias`, tests included. Its uses at `:1432, 1487, 1651, 2112`
  become exact comparisons against the row's binding id.
- `cultnet-rs/src/snapshot_query.rs:160-181` `RawSnapshotRow`: stop defaulting `schema_name` to the id; it reaches
  `schema_alias` from `serve_read_only_raw_snapshot` (`:129`).

**Kotlin:** `cultmesh-kotlin/.../CultMesh.kt:154-155` `codecForSchema`: drop `?: codecs[schemaId]`. That also fixes
`deleteBySchema` at `:170-171`.

**Not edited, archaeology:** Hermodr `hermodr-daemon.cjs:1335-1363`, EveElectron, and AetheriaEve's own readers.

**Tests deleted or inverted in C1.0.** Each rule that dies keeps a test that proves it is dead.
- C#:
  - `NetworkingTests.cs:3177` (a selection by version matches through the alias): invert to not-found before C1.1.
  - `NetworkingTests.cs:4022` (foreign id by sniffing): invert to refused.
  - `NetworkingTests.cs:4109, 4140, 4276, 4317, 4366` (shard and scope configured by version): configure by the wire id.
  - `CultNetSelectionFixBatchTests.cs:121, 158` (name alias): invert.
  - `CultMeshStreamingTests.cs:2638-2708, 3062, 3105` (payload fallback): invert to refused or skipped.
- TS:
  - `cultmesh.test.ts:3061-3074, 3244-3249` (HELD): invert to not-found.
  - `cultmesh.test.ts:1170-1196, 1241-1286`: catalog by version against a sha256 handle.
  - `cultnet.test.ts:1064-1068` (shard by name).
  - `cultmesh-browser.test.ts:139, 452, 530, 536, 692-697`.
- Python:
  - `test_cultnet.py:1685-1726, 2059-2063`.
  - `test_cultmesh.py:291-339, 343-379` (drop the name case).
  - `test_cultmesh.py:4150-4302` (sniffing and deletes by version against a sha256 id).
- Rust: `tests/selection.rs:2134, 2151, 2179, 2193, 2216-2219`.

### 4. C1.1: the C# wire id is the version

This is a behaviour cut on `src/GameCult.Networking` and `src/GameCult.Mesh`, plus the shared selection fixture with
Rust.

**The store id and the wire id stay separate.** `descriptor.SchemaId` (sha256) keeps keying CultCache and every
in-process dictionary. The wire id is `descriptor.SchemaVersion`. Once the override is deleted (OQ-C1.1), the wire id
derives from the descriptor alone, with no registry. That is what lets the selection evaluator, the mesh and the
database compute it without a binding.

**How a C# reader maps `<name>.vN` to its descriptor:**
- `_bindingsBySchemaId` is keyed by `binding.SchemaId`, which now equals `SchemaVersion`.
- An unbound registry looks the version up among its descriptors.
- C# already refuses two types for one `(name, version)` in a registry (`CultCache.cs` `RegisterDescriptor`, "One type
  owns a schema"), and c2a's `Register` refuses two types for one binding id. So an exact version lookup has at most one
  answer.
- Two types sharing version `"1"` cannot both be bound, because `"1"` is refused first.

File changes:
- `CultNetDocumentRegistry.cs:76-135` `ForDocument<T>` and `ForDocument(Type…)`: delete the `schemaId` parameter
  (OQ-C1.1). The binding id is `descriptor.SchemaVersion`.
- `CultNetDocumentBinding`: the internal constructor at `:44` takes the id from the descriptor.
- `CultNetDocumentRegistry.cs:173` `Register`: before c2a's conflict check, refuse a binding whose id fails
  `^.+\.v[0-9]+$`. Throw `CultSchemaConflictException` or `ArgumentException`, naming the type, the declared version,
  and the rule. The refusal happens at registration, never at first send.
- `CultNetDocumentRegistry.cs:675-676` `WireSchemaId`: becomes `descriptor.SchemaVersion`, checked by the same rule, so
  an unbound `"1"` type is refused when it is first emitted. Every emitter below calls it.
- `CultNetDocumentRegistry.cs:616-645` `ToRawRecord`: the synthesized binding at `:621-626` uses `WireSchemaId`, not
  `descriptor.SchemaId`.
- `CultNetDocumentRegistry.cs:217-228` `CreateDocumentDeleteMessage(string schemaId…)`: add
  `CreateDocumentDeleteMessage<T>(messageId, CultRecordHandle<T>)`, which derives the wire id. Keep the string overload,
  documented as "the wire id".
- `CultNetDatabase.cs` emitters: publish the wire id wherever a `CultNetDatabaseChange.SchemaId` leaves the process.
  - `CultNetDatabaseServer.cs:555/581` (removal change) sends `WireSchemaId(descriptor)`, not `change.SchemaId`. The
    change record keeps the sha256 locally.
  - `CultNetDatabase.cs:1554` `ToLogEntryMessage` `Delete.SchemaId` becomes the wire id.
  - `CultNetDatabase.cs:1512` `ApplyCommittedDelete` resolves the incoming delete id through the one exact lookup, not
    `GetRequiredBySchemaId`.
  - Error text at `:1664, :1725` names the wire id.
- **Durable shard log** (`CultNetShardMutationLogStorage.cs:60-88`, `CultNetDatabase.cs:586-591, 1385-1409`):
  - It persists store ids. `Append` rewrites each entry's `schemaId` (put document and delete) to
    `descriptor.SchemaId`.
  - `GetMutationLogMessages` restamps with `WireSchemaId` on the way out.
  - `KeyOf` (`:1399-1409`, the restart rebuild of `_lastWriteSequence`) reads store ids and so agrees with the live
    writes at `:1294, 1486, 1531`. That also fixes the rebuild disagreement that exists today when a binding overrides
    the id.
  - **Existing log files hold sha256 for default bindings, which is the store id, so nothing on disk changes.** Only
    logs written through an override hold a foreign id, and only the interop peer used one. No repo outside CultLib uses
    `CultNetFileShardMutationLogStore` (grep over F:\Projects).
- Request filters that send the sha256 today switch to `WireSchemaId`:
  - `CultNetDatabaseSubscriptionClient.cs:70-71`;
  - `CultMeshClient.cs:602, 843, 944`;
  - `CultMesh.cs:2696` (`RequestPeerSnapshotAsync`) and `:2038`;
  - `CultMeshSnapshots.cs:874` `ResolveDefaultSelection`.
- **Shard catalog and config:** `CultNetShardDescriptor.SchemaIds` and `CultNetClientAuthorityScope.SchemaIds` are wire
  ids. They are documented so, and matched exactly (C1.0).
- CultMesh projection sources and binding records (`CultMesh.cs:1521, 1602, 1972, 2032, 2273, 2300, 2331, 2357, 2384,
  2413`; `CultMeshSnapshots.cs:530, 555`) cross the wire as `CultMeshStateBindingRecord [Key(3)]`
  (`CultMeshPrimitives.cs:3334`). Stamp them with the version.
  - The collection and feed ids built from them (`CultMesh.cs:2298, 2329, 2382, 2411, 3266`) move to the version too.
  - The cultmesh-ts type of the same shape then agrees.
- **Selection order and cursor** (`CultNetSelectionEvaluator.cs:213-214, 318, 345, 586, 593`): tie-break and cursor on
  the wire id, as Rust does on its binding id.
  - This changes a wire-visible order: the parity vector R-AJ flips (`leaf_a.v1 < leaf_b.v1`, where the sha256 order
    was `b1f2… < ff35…`).
  - Cursors minted before the upgrade stop resuming. That is acceptable, because cursors are per session.
- **Shared fixture, landed in this cut with the Rust side:**
  - `contracts/cultnet/interop/selection-vectors.fixture.json`: `schemaId` becomes the version. Delete `nameAlias` and
    `hashAlias`.
  - Regenerate `selection-vectors.cs-written.json` and `selection-vectors.rs-written.json`.
  - `SelectionParityVectorTests.cs:112-117, 156-159` assert the fixture ids equal the declared versions. Delete the
    alias cases at `:235, :238, :329`; keep the refusal of an unknown id at `:325`.
  - Rust `cultnet-rs/tests/selection.rs:1561-1690, 1849` (vector writer): drop the alias rows; order by the version.
- **Left alone:** `global:{descriptor.SchemaId}` record keys (`CultCache.cs:2473`, `CultNetDatabase.cs:1120`). They are
  a record key, not a schema id, and changing them changes store keys. See F-C1-GLOBALKEY.
- **Schema catalog:** `CultNetSchemaRegistry` holds JSON-Schema contracts keyed by `$id` URL, and no production C# host
  answers a catalog request (`Server.cs:565` is an allowlist only). The only document-payload registrations are the
  interop peer's, which C1.5 changes. Nothing to cut here.

### 5. C1.2: TypeScript

Runtime packages `cultcache-ts`, `cultnet-ts`, `cultmesh-ts` and `cultmesh-browser`.

- `cultcache-ts/src/document.ts:82-91` `schemaIdentityOf` stays the store identity, unchanged.
- **cultnet-ts owns the wire:** `export function wireSchemaIdOf(definition)` returns
  `schemaIdentityOf(definition).schemaVersion`. The store keeps a definition with no declared version valid; only a
  wire registration refuses it.
- `cultnet-ts/src/replication.ts:52-80` `register`:
  - refuse a version failing `/\.v\d+$/` with `SchemaConflictError` or `TypeError`, naming the type;
  - key `#owners` by `wireSchemaIdOf`;
  - `#listers` keep the declared compatible ids (OQ-C1.2).
  - The conflict check at `:59-71` also compares wire ids.
- Outgoing ids: `schemaIdForBinding` (`:349-351`) splits in two.
  - Outgoing messages (`:123, :178, :287` put echo) use `wireSchemaIdOf`.
  - `applyRawDocumentPutMessage` (`:280-290`) keeps storing under the store id, `schemaIdentityOf(...).schemaId`.
    That is the leak point: today the same function feeds both.
- **Serving** (`:196-260`, `:363-368` `schemaIdForEnvelope`, `:336-345` `#createRawDocumentRecord`):
  - A served record carries `wireSchemaIdOf(binding)`, never `envelope.schemaId`.
  - A record stored under a compatible id holds that version's bytes. It is decoded and re-encoded through the owning
    definition before it is served under the owner's id. This matches C#, which serializes from the decoded document
    (`CultNetDocumentRegistry.cs:640`).
  - `answersTo` (`:354-361`) matches the requested id against the binding's wire id (and its compatible ids if
    OQ-C1.2 is yes).
  - Tests `cultnet.test.ts:1627-1664` (the wire id equals the store id), `:1703-1708` (served under the stored id) and
    `:1713-1744` (a version id does not resolve) are inverted.
- `cultnet-ts/src/shard-catalog.ts`: exact after C1.0; shard ids are wire ids.
- **cultmesh-ts:**
  - `cultMeshSchemaFromDefinition` (`index.ts:6168-6173`): `schemaId` for peer reads is `wireSchemaIdOf`.
  - `syncDocumentFromPeerSnapshot` (`:3995-4019`) and `cultMeshDocumentFromPeerSnapshot` (`:2668-2710`) ask by wire id.
  - `resolveCultMeshStoreDocumentRecord` / `cultMeshAcceptedSchemaIds` (`:6243-6256`) are store-local and stay on
    `schemaIdentityOf`.
  - The receipt filter at `:5976` compares `receiptBinding.definition.schemaId`, which is `undefined` when not
    declared. Compare with `wireSchemaIdOf`.
- Put-serve overlap (not on c2a): `hands/cultmesh-put-serve-bound` adds `#rawPutEnvelope` and
  `createRawSnapshotResponseForPut` to this file, on main's shape resolution (`preserveIncomingSchemaId`). After the
  merge, the put-sizing response must be built by the same serving path, so it carries the wire id.
- `cultmesh-browser`: exact after C1.0. `samples/eve-browser-network` declares or asks by version.
- **Consumer action, not in CultLib:** Stonks `stonks-daemon.cjs:152-195` sets `schemaVersion` to the full id. It
  lands in Stonks before C1 merges to main.

### 6. C1.3: Python

- `cultcache_py/documents.py:105-143` `catalog_entry` stays the store identity. Add `wire_schema_id(definition)` in
  cultnet_py, returning `schema_version`.
- Registration in `cultnet_py` and `cultmesh_py` refuses a version failing `\.v\d+$`.
- Outgoing ids use `wire_schema_id`:
  - `cultmesh_py/node.py:577` (put), `:641` (delete), `:824-827` (sync);
  - `client.py:430` (subscribe);
  - `node.py:767, 776` (snapshot, never the stored id; a compatible-id record is re-encoded as in TS);
  - `server.py:617-633` (catalog).
  - `server.py:416, 444` echo a record that has already been resolved exactly, so it carries a wire id.
- Applied records keep being stored under the local store id: `replication.py:42, 84`, `node.py:664-670`.
- Inverted tests: `test_cultmesh.py:4116-4148` (a version request is served under the sha256 id) and `:4215-4258`
  (echo of a foreign id).
- `interop_peer.py:1085` serves the stored id; that moves in C1.5.

### 7. C1.4: Rust mesh and Kotlin

- **Rust.** The wire id is already the version on every consumer binding.
  - `for_entry::<T>()` (`cultnet-rs/src/replication.rs:44-47`) has no consumer. It binds `T::TYPE`, which fails the
    refusal unless it ends in `.vN`. Refuse it at `register` like any other id without a version. Keep the constructor,
    so a type whose TYPE is `x.v1` still works.
  - `for_entry_with_schema_id(id, version)` (`:50-64`): refuse `id != version` or a missing `.vN`. That is one id, and
    `cultmesh_documents!` already passes the same string twice.
  - `register` (`replication.rs`) applies the rule.
  - The apply path keeps stamping `binding.schema_id` (`:400`). That is what Odin, Muninn and the rest store today, so
    nothing on disk changes.
  - `pull_rudp_catalog_snapshot` (`cultmesh-rs/src/lib.rs:714-747`) stamps the resolved binding's id. It is exact after
    C1.0, so no foreign id is written.
  - Rust's mixed store ids (typed put stamps `T::TYPE` at `cultcache-rs/src/lib.rs:2362`; apply stamps the version) are
    left as they are. That is C2's question, F-C1-RUSTSTOREID.
- **Kotlin.**
  - `register` (`CultMesh.kt:79-93`) refuses a `schemaVersion` failing `\.v\d+$`.
  - The interop codecs at `CultMesh.kt:3872-3880, 4317-4322, 4351-4354, 3779-3789` put the contract URL in
    `schemaVersion`. They become `cultnet.interop_note.v0` and the capability `*.v0` versions. Kotlin's own suite
    (`CultMesh.kt` self-checks) moves with them.

### 7b. C1.5: the interop harness tests the default binding

The parity witness hid the mismatch because every peer names the note by its JSON-Schema `$id` URL. C1.5 removes the
overrides so each runtime's default binding meets the others.

- C# `tests/GameCult.Networking.InteropPeer/Program.cs`:
  - `:77-80, :201-204`: `ForDocument<CultNetInteropNote>(payloadSerializer:…, payloadDeserializer:…)` with no id. The
    id is `cultnet.interop_note.v0`.
  - `:1183-1196`: four capability bindings with no id; their versions are `*.v0` (`:1560-1572`).
  - Delete the `*SchemaId` URL constants.
  - `LoadSchemaRegistration` (`:1116-1137`): the catalog registration's `SchemaId` becomes `InteropSchemaVersion`. The
    JSON `$id` stays inside `SchemaJson`.
  - The catalog server at `:682-714` stops copying `SchemaId` into `SchemaVersion`.
- TS `packages/cultnet-ts/test/interop/cultnet-interop-shared.ts:10-19, 289-306` and `cultnet-interop-peer.ts:123,
  135, 896-930`: definitions declare `schemaVersion` and no `schemaId` override. The note id constant is at
  `cultnet-interop.test.ts:43`.
- Python `cultnet_py/interop_peer.py:59-68, 869-884, 936-981, 1085, 1149`.
- Rust `cultnet-rs/examples/cultnet_interop_peer.rs:56-67, 1227-1261`: `for_entry_with_schema_id` with the version
  only.
- Kotlin: C1.4.
- The Python cultmesh scripts inside `cultnet-interop.test.ts:3872-4060` declare `schema_version` only.
- **The added negative lane** is one scenario in `cultnet-interop.test.ts`. A C# host serves a note from a
  **CultMesh-created registry** (`CultMesh.CreateCultNetDocumentRegistry`, the production path). A TS reader:
  - asks by `cultnet.interop_note.v0` and gets it;
  - asks by `cultnet.interop-note` (the name) and gets not-found;
  - asks by the sha256 id and gets not-found.
- CI: `.github/workflows/cultnet-interop.yml:141-163` already runs every pair lane. The new scenario joins the
  `TypeScript and C#` pattern.

### 8. Verification

Each rule gets a behavioural test that fails when the rule breaks, plus the ecosystem mutation tool on the cut's
diff: Stryker.NET, StrykerJS, mutmut, cargo-mutants. Kotlin has no tool, so its rules are defended by scenario.

**Positive, across runtimes, with default bindings** (C1.5 lanes, run on Yggdrasil in `ack1d-interop:2`):
- A C# host with a CultMesh-created registry publishes a note, and TS, Rust, Python and Kotlin each read it. Each asks
  only by `cultnet.interop_note.v0`.
- Then each non-C# host publishes, and C# reads.
- The wire bytes carry `schemaId = cultnet.interop_note.v0`. Assert the decoded `CultNetRawDocumentRecord`, not a log
  line.

**Negative checks:**
- **No fallback.** In each runtime, a record at the requested key under another id is not returned (TS, C#, browser),
  and a payload whose slot 0 names a registered version but whose id is foreign is refused (C#, Python).
  - Mutant: restore `?? atKey[0]`, or restore the `byPayload` or `aliased` branch. It must die.
- **Unknown id refused.**
  - C# `DeserializeRawDocument` with `gamecult.unknown.v1` throws. Today it returns `Note` (probed).
  - TS, Python, Rust and Kotlin `applyRawDocumentPut` with an unknown id throw.
- **No name match.** Asking by `schemaName` finds nothing, in every runtime and in the selection evaluator (C# and
  Rust). Mutant: add `|| candidate == descriptor.SchemaName`.
- **Refusal.**
  - Registering a C# binding for a `[CultDocument("x", "1")]` type throws at `Register` and names the type.
  - An unbound `"1"` type in a registry with no bindings throws at first emission.
  - TS, Python, Rust and Kotlin refuse `"v1"` and `"1"`, and accept `x.v0` and `x.v12`.
  - Mutants that must die: `\.v\d*$` (accepts `x.v`), `.v` anywhere, and `\d+$` alone (accepts `"1"`).
- **The wire id is derived from the declaration, not a constant.** Two types with different versions each emit their
  own. A mutant that returns `SchemaName + ".v1"` must die, so use a fixture whose version is not `<name>.v1`: the
  interop note's name is `cultnet.interop-note` and its version is `cultnet.interop_note.v0`.
- **The store id is untouched.**
  - A C# store written before C1 is read after it. `dump.py` shows record ids unchanged after a C1 host reads and
    serves.
  - A TS store with `schemaId: "p1.shared"` keeps that id after a raw put arrives under `p1.shared.v1`.
  - Mutant: store under the wire id. It must die.
- **The served id is not the stored id.** A TS or Python record stored under a compatible id is served under the
  owner's wire id, with bytes that decode as the owner. Mutant: serve `envelope.schemaId`.
- **Durable log.**
  - A C# database with `CultNetFileShardMutationLogStore` writes, restarts, and replays to a replica. The replayed
    entries carry the version, and `LastWriteSequence` after the restart equals the value before it.
  - A log file written before C1 (sha256 entries) replays under the version.
- **Selection.** The parity vectors agree C#↔Rust with version ids. The tie-break vector R-AJ orders by version.
- **Catalog lookup.** A catalog with two handles of one type refuses a by-schema lookup and names both document ids
  (C# and TS). Mutant: return the first or last handle.

**Builds.** These are the focused set, with no workspace-wide build:
- `dotnet test` on `GameCult.Networking.Tests` and `GameCult.Mesh.Tests`;
- `npm test` in `cultnet-ts`, `cultmesh-ts` and `cultmesh-browser`;
- `pytest` in `cultnet-py` and `cultmesh-py`;
- `cargo test -p cultnet-rs -p cultmesh-rs`;
- the Kotlin self-check;
- the interop lanes.
All of them run on Yggdrasil through `ygg-verify.sh`.

**Negative greps**, which must return nothing in `src/` and `packages/*/src`:
- `CultNetSchemaAliasMatching|InferSchemaName|inferSchemaName|_infer_schema_name|infer_schema_name`
- `strip_schema_version_suffix|schema_alias|PayloadMatchesSchema|TryReadSchemaVersion|firstRecordAtKey`
- `bySchemaNameVersion|ExpandSchemaBindingAliases`
- in `CultNetDocumentRegistry.cs`: `schemaId:` as a `ForDocument` parameter.

**Live consumer checks**, after release and before any consumer bumps:
- **Stonks** has its six versions fixed. `idunn` shows Odin holding Stonks' `provider_advertisement` after a Stonks
  restart on C1.
- **AquaSynth** daemon and clients rebuilt together; the Dings host round-trips.
- **Mimir** EveDashboard is visible in Eve Android.
- **Bifrost** agent-transport snapshot and apply round-trip.
- **Odin** still ingests from Heimdall, VoidBot, weksa and StreamPixels. Those ids do not change.
- **Grep** Idunn and Odin for a receiving binding of `idunn.daemon_health` with no version. If one exists, it is
  refused at startup under C1.4. It was not found in main.

### 9. Sequencing

1. **F2 finishes first.** C1 and F2 touch different layers:
   - F2 is store-side: `CultCache.cs` `Claimant`, the single-file and directory stores, and the Rust catalog lay-back.
   - C1 is wire-side.
   - They overlap in three places:
     - `cultnet-ts/src/replication.ts`. F2 and batch 10 built owner-beats-lister and the `#listers` refusal (F9, F10)
       there, and C1.2 re-keys `#owners` by wire id. The lister rule is kept, with OQ-C1.2 deciding its wire reach.
     - `cultnet-ts/test/cultnet.test.ts:1346-1394, 1627-1744`. C1.2 inverts tests F2's batches wrote.
     - `cultnet_py/replication.py` `schema_document_map`.
   - C1 does not relabel any stored record and adds no store write, so F2's rule ("never relabels … a write over it is
     refused") is not in play. F2's test `976b57f1` ("each runtime's writes leave another runtime's records and catalog
     entries unchanged") must stay green through C1.
   - Hands on F2 should not be interrupted. C1 starts after F2's Soul pass holds.
2. **Then put-serve merges to main** (`hands/cultmesh-put-serve-bound`, based on `b04d03b6`).
   - It edits `cultnet-ts/src/replication.ts` on main's resolution. Main still has `preserveIncomingSchemaId` and
     payload-shape resolution, which c2a deleted in `0ee98e8c`.
   - It also edits `cultmesh-ts/src/index.ts`, `cultmesh_py/server.py` and `node.py`, and `cultmesh-rs`.
3. **Then c2a merges main.** This is the conflict to resolve before C1, whatever C1 is:
   - c2a's exact resolution plus put-serve's `createRawSnapshotResponseForPut`;
   - main's R1 changes to `CultNetDocumentRegistry.cs` (`rowFilter`) against c2a's `Register` refusal.
   - C1 re-anchors on that merge commit. Every anchor above names its symbol for that reason.
4. **C1 cuts, in order, on the variants branch** (or `hands/variants-c1id` cut from it):
   - C1.0 C# subtraction;
   - C1.0 TS, Python, Rust and Kotlin subtraction, one cut per runtime or together, since they are deletions only;
   - C1.1 C# wire id with the shared selection fixture and Rust selection order;
   - C1.2 TS;
   - C1.3 Python;
   - C1.4 Rust and Kotlin;
   - C1.5 harness.
   - The interop lane is red between C1.1 and C1.5 on the branch. Soul measures at C1.5.
   - Stonks' fix lands before the variants branch merges to main.
5. **Release.** C1 is wire-breaking between C# peers of different releases, because an old host sends sha256 and a new
   receiver refuses it. It rides the C0/C2a release that the 2026-09-30 ruling already gates. The release note names:
   C# peers upgrade together, the refusal of versions without `.vN`, and Stonks.

### 10. Ledger estimate

| Cut | Deleted (src) | Added (src) | Tests | Notes |
|---|---|---|---|---|
| C1.0 C# | ~290: alias class 58; registry block ~175; mesh branches ~35; expand ~40 | ~40 exact lookups | ~15 tests inverted, ~6 deleted | public API removed: `CultNetSchemaAliasMatching`, `TryResolveDescriptorByPayloadSchema`, `PayloadMatchesSchema` |
| C1.0 TS/Py/Rust/Kt | ~230: selection `schema_alias` ~140 with tests; Python infer/sniff ~45; TS infer/HELD/catalog keys ~45 | ~20 | ~25 tests deleted or inverted | |
| C1.1 C# | ~30 (override parameter, sha256 fallbacks) | ~80 (refusal, log restamp, emitters) | ~20 changed; fixture and vectors regenerated | wire-visible order change |
| C1.2 TS | ~15 | ~50 (`wireSchemaIdOf`, refusal, serve re-encode) | ~10 | |
| C1.3 Py | ~10 | ~40 | ~8 | |
| C1.4 Rs/Kt | ~5 | ~25 | ~6 | |
| C1.5 harness | ~60 URL constants and overrides | ~40 negative lane | — | |
| **Net** | **~-640 / +295 src**, about **-345** | | | Nothing on disk changes. Two public C# methods and one public C# class are gone. |

### 11. Operator questions

**OQ-C1.1: delete the per-binding wire-id override?**
- The overrides are C# `ForDocument(schemaId:)`, Rust's free first argument of `for_entry_with_schema_id`, and the
  implicit TS and Python `schemaId`-on-the-wire.
- A. Delete. The declared version is the one wire id, and it derives from the descriptor alone, so the selection
  evaluator, the durable log and the mesh need no registry to compute it. No repo outside CultLib passes the override;
  only CultLib's interop peer and two conflict tests do.
- B. Keep it, but refuse ids without `.vN`. A second declaration of identity survives beside the version, and every
  descriptor-only site (evaluator order, cursor, log, projections) needs the registry again. That is today's
  `WireSchemaId` split.
- **Recommendation: A.** It removes a public parameter, which the CultLib consumer rule asks to be put to you rather
  than cut silently. What a reasonable consumer loses is the ability to put an id on the wire that its declaration
  does not state. That is the thing C1 rules out.
- C1.1's shape depends on the answer.

**OQ-C1.2: does declared compatibility (`CompatibleSchemaIds`) resolve on the wire?**
- A. Yes, with the same rule the stores use: owner beats lister, and two listers with no owner are refused. A v2 type
  that declares `x.v1` compatible accepts a v1 peer's records, so mixed-version peers work when an author declares it.
  It is declared, never inferred. c2a's TS and Python registries already do this. Served records always go out under
  the owner's wire id, re-encoded (§5).
- B. No, wire resolution is the owner only. Compatible ids become store-only. It is simpler, and a version bump is a
  hard break between peers.
- **Recommendation: A.** It is the rule the operator ruled for stores (owner beats lister), applied to one more layer.
  It is not aliasing, because nothing is guessed.
- C1.0's Python map, C1.2's `#listers`, and the C# lookup depend on the answer.

**Defaulted, not asked. Veto if wrong:**
- TS and Python keep their default version `${name}.v1` when none is declared.
- A served record stored under a compatible id is re-encoded through its owner, as C# does.
- `global:{sha256}` record keys stay.
- Rust's apply path keeps stamping the binding id.
- A catalog lookup by schema that more than one handle answers is refused.
- Stonks is fixed in Stonks.

### 12. Follow-ups (no cut owns them)

- **F-C1-GLOBALKEY:** `global:{descriptor.SchemaId}` keys (`CultCache.cs:2473`, `CultNetDatabase.cs:1120`) carry the
  sha256 across the wire as a record key. This is for C2.
- **F-C1-RUSTSTOREID:** Rust stores hold `T::TYPE` for typed puts and the version for replicated puts. Its catalog
  fabricates version `<TYPE>.v1` (`cultcache-rs/src/lib.rs:2694-2708`) even where `cultmesh_documents!` declares another
  one. The derive cannot declare a version (`cultcache-rs-derive/src/lib.rs:24-33`), which is a gap beside the other
  runtimes. This is for C2.
- **F-C1-PYALIAS:** Python cultmesh `_documents_alias` compares two local definitions by name and version. It is
  redundant under one-type-per-schema, but local.
- **F-C1-HEALTHID:** Bifrost sends `idunn.signed_daemon_health` without `.v1`
  (`persona-feedback-idunn-health.cjs:41`), where every other sender uses `.v1`. It is pre-existing and unrelated to
  C1.
- **F-C1-AEVE-CONST:** the AetheriaEve daemon compares wire ids with version constants (`Program.cs:1591`) and so never
  matched under sha256. It is archaeology; record only.

## F2 forks (operator, 2026-10-01)

These came from the C2a F2 Hands at `a771e71b`.

- **Fork A: RULED, forbid slot reuse.** Operator: "Forbid slot reuse. This is expected behavior for any MessagePack or
  protobuf veterans."
  - A whole-view write keeps laying back records the type does not own.
  - Registration refuses a type that puts a different member in a slot the stored catalog shows was used before.
    This follows protobuf's reserved field numbers.
  - `VariantTests.AFlushShedsAnOverrideOfADroppedSlotSoALaterTypeReusingTheSlotDoesNotResurrectIt` is rewritten to pin
    the refusal instead.
- **Fork B: DEFERRED, foreign records in TS, Python and Rust.** Operator: "Foreign records in other runtimes can wait,
  I suppose, though I don't love leaving parity on the table."
  - Today those runtimes refuse to open a store that holds a record they cannot claim. That is safe, but it is not
  parity with C#.
  - Carrying foreign records needs their whole-store writers taught first: TS `pushAll` via CultMesh `flush`, and Rust
    `put_prepared_batch` → `push_all`.
  - Recorded as the next parity cut, not dropped.

## Soul: cultcache-ts push identity (`hands/cultcache-ts-push-identity` `1d6e1364..a00bb01f`, 2026-10-01)

Verdict: **hold, not merged.** Recorded under drain mode; no fix dispatched.

- **Held:** the Stonks duplicate is fixed; the suites on a merge with main are green (cultcache-ts 54/54 with four-runtime interop, cultnet-ts 198/198, cultmesh-ts 128 passing).
- **F1 (CONFIRMED, high):** `isSameRecord` (`single-file-messagepack-backing-store.ts:284-286`) still matches on the stored label. When one definition's type equals another's schemaName, a put or delete for one destroys the other's record at the same key, globals included. The branch's own test "an envelope with no catalog entry replaces a record stored under another schema id that carries its label" pins this hole. The coherent cut: the cache resolver hands the store the persisted identity (key plus schemaId), and the store compares only that.
- **F2 (CONFIRMED, high):** Python (`stores.py:54-67, 105-119`) has the same duplicate bug. A second `put_global` makes the store unloadable.
- **F3 (CONFIRMED, medium):** Rust `entry_id` is `(type, key)`, which duplicates records written by another runtime. Rust also writes its TYPE into the catalog schemaName slot, a parity defect.
- **F4:** mutant MA (legacy global labelled by schema name) survives; the rule needs a committed test.
- **F5 (PLAUSIBLE, high after c2a):** c2a's compatible-owned ids plus clause 1 let a non-owner's write delete the owner's record. c2a's `encodeSnapshot` `${type}::${key}` is a second identity authority.
- Probes survive as unreachable commit `1c7744e2` (ts, rs, py); recover them before gc.

## Soul: C2a F2 keep unknown records (`hands/variants-c2a` `92d9e138..a771e71b`, 2026-10-01)

Verdict: **hold, not merged.** Recorded under drain mode; no fix dispatched. The core promise holds: a C# write neither destroys nor relabels a record it doesn't claim. Checked on the real `Aetheria.cc` (206 records): 0 record or entry differences after a commit and a flush.

- **F1 (CONFIRMED, medium):** C# decodes and re-encodes laid-back catalog entries (`CultDocumentMessagePackSerialization.cs:501,567`, `CultCache.cs:3847-3851`). Unknown entry and member fields are dropped, and nil and int widths are normalized. Fix: carry the raw entry bytes.
- **F2 (CONFIRMED, medium):** laying back a loaded foreign record reverts a newer write by its owner and moves storedAt backwards (`CultCache.cs:3831-3836`, breaks F6). Fix: lay back only while the file still holds the loaded `(id, storedAt)`.
- **F3 (CONFIRMED, medium):** one refused flush blocks every later write to other records. The directory store stays stuck until restart (`CultCache.cs:3827-3831`, `DirectoryMessagePackBackingStore.cs:249-253`). Breaks "writes to other records proceed".
- **F4 (low):** a laid-back record marks the v3 header without holding ids (`CultCache.cs:3731`).
- **F5 (plausible, low):** an in-place change to a laid-back document is dropped silently.
- **F6:** mutants H2 (`CultCache.cs:3850`) and R2 (`lib.rs:2663`) survive.
- **The main merge is its own Hands cut.**
  - 8 files conflict, including about 74 KB in `lib.rs`.
  - Main refuses a zero-byte store; c2a reads it as an empty v1 store.
  - Main's `CultMeshStreamingTests.cs:919` uses `MeshNoteAliasDocument`, which c2a deleted.
  - The rough merge fails in Caching, Mesh (compile), TS and Python.
- `ADeterministicIdIsTheSameUnderEveryCulture…` fails at the base too: the image is globalization-invariant.

## Store authorities (Modeling, body at `hands/variants-c2a` `705ab669`, 2026-10-03)

Source: cuts `merge-r1` (closed, reports h2-h6) and `mesh-single-file-reader` (closed, report h1). Every `file:line`
is at `705ab669`; re-find by symbol when the branch moves. This section supersedes the Body facts in section 1
about raw readers (the Mesh legacy snapshot decoder and `ReadLegacyPersistedRecord` no longer exist) and the
merge notes in the C2a Soul sections below. C#, Rust, TypeScript and Python each have one single-file reader, and
CultMesh reaches CultCache's reader, lock and replace through one internal entry.

Abbreviations: **CS** `src/GameCult.Caching/CultCache.cs` (`SingleFileBackingStore` :3627-4140), **CSM**
`src/GameCult.Caching.MessagePack/CultDocumentMessagePackSerialization.cs`, **MESH**
`src/GameCult.Mesh/CultMeshSingleFileDocuments.cs`, **RS** `packages/cultcache-rs/src/lib.rs`, **TS**
`packages/cultcache-ts/src/single-file-messagepack-backing-store.ts`, **PY**
`packages/cultcache-py/src/cultcache_py/stores.py`, **AUTH** `packages/cultnet-ts/src/idunn-runtime-authority.ts`.

The one door per runtime, and who walks through it:

| Runtime | The reader | Callers that must use it |
| --- | --- | --- |
| C# | `SingleFileBackingStore.ReadSnapshot` CS:3997 over `ReadDisk` CS:4026 | `PullAllCore` CS:3665, `PushAll` CS:3768, `CommitBatchCore` CS:3799, `ReadDurable` CS:4014, `ReplaceDurable` CS:4016; MESH:164, :195, :245 |
| Rust | `read_store_unlocked` RS:935 (header by `store_header` RS:389) | `read_all_unlocked` RS:924 (`pull_all` RS:1243 and the read-only snapshot), `write_all_unlocked` RS:983, soft `push_all` RS:1268 |
| TypeScript | `readSingleFileStore` TS:153 (bytes by `readStoreBytes` TS:174), exported from `packages/cultcache-ts/src/index.ts:4` | the store's `#readDisk` TS:108 (`pullAll`, `push`, `delete`, `pushAll`); AUTH:321 `readAuthorityRecord` |
| Python | `pull_all` decode path PY:123-140 (`_read_store` PY:265, `_decode_snapshot` PY:296) | `push`, `push_all`, `delete` call `pull_all` (PY:142-152) |

### A. Header decision (what the file is, and what header a write carries)

- **Owner.** Read side: the reader in the table above. C#: `CSM.DeserializeSnapshot` :188 (whole-array proof :245-269), then `RequireSingleFileFormat` :273, reached through `SingleFileMessagePackBackingStore.DeserializeSnapshot` CSM:592. Rust: `store_header` RS:389 is the only classifier of leading bytes (it returns `first` and the whole-array proof separately), then `readable_store_format` RS:2890 and `decode_store_file` RS:2901. TS: `decodeStoreFile` TS:423. Python: `_decode_snapshot` PY:296. Write side: the header is derived from what the reader just saw. C#: `HeaderFor` CS:3439, fed by `disk?.FormatVersion` in `PushAll` and `CommitBatchCore`, and by `existing?.FormatVersion` from MESH:247. Rust: `encode_store_snapshot` RS:2793, fed by the `DiskStore` read in `write_all_unlocked` RS:992. TS: `#format`, set by the pre-write read (TS:48-52, :86-100) and used by `encodeSnapshot` TS:136. Python: `_format`, set in `pull_all`, used by `_encode_snapshot` PY:245.
- **Inputs.** The bytes at the path. **Outputs.** The decoded store with header and catalog; or a typed refusal (`CultStoreUnreadableException` in C#; `CultCacheStoreUnreadable{path, kind}` in Rust, kind `Undecodable` or `UnsupportedFormat`; `StoreUnreadableError` in TS and Python). A refusal names a header only as `cultcache.store.v<digits>`.
- **Derived state.** The write header is never decided by the writer: it is the reader's verdict (header read, whole-store flag, whether ids or variants are held). A rewrite of a file that cannot be read cannot happen, because the reader throws first.
- **Forbidden writers.** A second header classifier. Deleted: Rust `leading_header`, `read_array_header`, `read_string`; cultnet-ts's own decode of the store header; the TS async `readStore`; Rust `StoreUnreadableError` (negative grep `StoreUnreadableError` in `packages/cultcache-rs`: no match); Python `_decode_v1_snapshot`.
- **Shared paths.** cultnet-ts reaches the header decision only through `readSingleFileStore` (AUTH:4, :321) and checks only record count, schema id and type. cultnet-ts therefore declares cultcache-ts `^0.15.0`, the first release that exports it.
- **Cut line.** `merge-r1` h3 (Rust) and h4 (TS, cultnet-ts); spec `cut-merge-r1.r3` authority map.
- **Pinned by.** `tests/vectors/document-variants-c2a/readability/manifest.txt`, walked by `StoreReadabilityTests.OpenReadsExactlyTheFilesTheVectorsSayItReads` (C#), `CultMeshSingleFileReadabilityTests.AWriteReplacesAFileExactlyWhenItReads` (Mesh), `a_file_is_replaced_exactly_when_it_reads` (RS:5132), "SingleFileMessagePackBackingStore replaces a file exactly when it reads" (TS test :2321) and `test_single_file_replaces_a_file_exactly_when_it_reads` (Python). AUTH cases: `packages/cultnet-ts/test/idunn-runtime-authority.test.ts`. Declared divergences in the manifest: `legacy-envelopes.bin` (C# refuses; the others read and replace it) and `variant-slot-v3.bin` (C# reads; the others refuse).
- **Known gaps.** `variants:follow_up:c0-bad-canonical-in-manifest` (`current-catalog-bad-canonical.msgpack` is not in the manifest; TS reads it, Rust refuses). `variants:follow_up:legacy-vectors-all-runtimes` (the three `legacy-catalog-*` c0 vectors have a verdict only in C# tests; all four runtimes refuse them, probed). `variants:follow_up:range-check-in-ci` (`npm run test:pack:ts` builds every admitted tag and checks exports, but `.github/workflows/publish-packages.yml` does not run it).

### B. Existence and links (what "nothing at the path" means)

- **Owner.** C#: `ReadDisk` CS:4026 decides absence from the path's attributes (`-1`), with `PathIsALink` CS:4057. Rust: `is_absent` RS:3049, inside `read_store_unlocked` RS:936-945. TS: `readStoreBytes` TS:174 (the read and the lstat must both find nothing; the lstat is a test seam). Python: `_read_store` PY:265.
- **Inputs.** The path's attributes and the read result. **Outputs.** Absent (an empty, unmarked store), or bytes, or an I/O error. Only nothing at the path is absent. A link whose target is gone is an I/O error in all four runtimes (C#: `IOException` for a file link and for a Windows directory junction, CS:4040-4048; Rust: `io::Error`; TS: an error with code `ENOENT`; Python: `FileNotFoundError` re-raised).
- **Derived state.** Mesh's `FileNotFoundException` is derived from the store reader's `null` (MESH:164, :195); Mesh no longer owns absence.
- **Forbidden writers.** `File.Exists` or any existence gate in MESH (negative grep `File\.(Exists|ReadAllBytes|Delete|Move|WriteAllBytes)|HasOlderCatalogLayout|ReadLegacySchemaCatalogEntry|WriteFileAtomically|ReadSingleFileSnapshot` over MESH: no match); a zero-length-means-absent shortcut; a writer's own read.
- **Shared paths.** Open, flush, commit, `ReadDurable`, `ReplaceDurable`. Rust `pull_all` RS:1243 also checks `is_absent` on the store and on the lock path before it takes the shared lock; that skips lock creation only, and the verdict stays with the reader.
- **Cut line.** `merge-r1` h2 (C#) and h4/h6 (TS `readStoreBytes`); `mesh-single-file-reader` h1.
- **Pinned by.** C#: `StoreReadabilityTests` `ADanglingFileLinkIsAnIoErrorOnOpenNotAnEmptyStore` :208, `ADanglingDirectoryLinkIsAnIoErrorOnOpenNotAnEmptyStore` :185, and the commit variants :196, :219. Mesh: `ADanglingLinkIsAnIoErrorOnTypedWriteRawWriteAndReadNeverAMissingFile` :107 and `OnlyNothingAtThePathIsAMissingFile` :134 (`CultMeshSingleFileStoreTests.cs`). Rust: `a_dangling_symlink_at_the_store_path_is_an_error_and_nothing_is_written` RS:5832. TS: "readStoreBytes reports nothing at the path only when the lstat finds nothing too".
- **Known gaps.** The writer side still tolerates a dangling link: Rust `write_all_unlocked` RS:992-996 and TS `pushAll` TS:94-100 replace it on a whole-store write (`variants:follow_up:writer-dangling-link-tolerance`, owner `c2a-write-set`). What a replace does to a live link is `variants:follow_up:store-replace-cut` item 2. Windows junction behavior was proven at `42f05f66` and `b1ff2dcb`, not at the final head.

### C. Zero-byte file

- **Owner.** None separate: a zero-byte file is bytes the one reader cannot decode. C#: `ReadDisk` returns the bytes and `CSM.DeserializeSnapshot` refuses them (the `Length: > 0` shortcut is deleted). Rust: the `store_header` whole-array proof fails, `Undecodable`. TS: `readSingleFileStore` TS:158 wraps the decode error in `StoreUnreadableError`. Python: the `unpackb` failure is wrapped at PY:139-140.
- **Inputs/outputs.** Zero bytes in; a typed unreadable refusal out; every writer leaves the file as it was.
- **Derived state.** None. **Forbidden writers.** Any `length === 0`, `is_empty` or `Length > 0` branch before decode (negative greps in spec `cut-merge-r1.r3`: `Length: > 0 } bytes` in CS, `data.length === 0` in TS).
- **Shared paths.** Open, flush, commit, soft flush, Mesh typed and raw write, AUTH.
- **Cut line.** `merge-r1` h1-h3; ruling `variants:ruling:merge-r1-precedence`.
- **Pinned by.** Manifest row `zero-byte.bin` (refuses x4) in every walk above; Mesh `AReadOfAZeroByteFileIsRefusedAsUnreadableNotAsAMissingRecord` and `AZeroByteFileIsRefusedOnTypedAndRawWriteAndLeftAlone` (`CultMeshSingleFileStoreTests.cs:175`); the AUTH zero-byte case.
- **Known gaps.** None open.

### D. Directory at the path

- **Owner.** C#: the explicit clause in `ReadDisk` CS:4034-4035 (a directory that is not a link is an `IOException`; a directory link falls to the catch at CS:4045). Rust: `fs::read` fails with a non-`NotFound` error, kept as `io::Error` (RS:940-944). TS: `readFileSync` throws `EISDIR`. Python: `Path.read_bytes` raises an `OSError`.
- **Outputs.** An I/O error, never an empty store; the directory is untouched and the refusal text does not echo the path.
- **Forbidden writers.** Treating a directory as absent, or creating a store over it.
- **Shared paths.** Open, commit, Mesh read and write.
- **Cut line.** `mesh-single-file-reader` h1 (the C# clause). Rust, TS and Python were correct by construction of their reads.
- **Pinned by.** C#: `ADirectoryAtTheStorePathIsAnIoErrorOnOpenAndOnCommitNotAnEmptyStore` (`StoreReadabilityTests.cs:236`). Mesh: `ADirectoryAtThePathIsAnIoErrorOnReadAndWriteAndIsLeftAlone` (:149). Rust: `a_write_over_a_directory_stops_at_the_read_and_leaves_no_staging_file` RS:5726.
- **Known gaps.** TS and Python have no test for a directory at the store path. No follow-up is registered; the natural home is `write-set-vectors` with the other readability pins.

### E. Lock

- **Owner.** C#: `AcquireLock` CS:4082 (`<file>.lock`, `FileShare.None`, 30 s wait), taken by `PushAll` CS:3766, `CommitBatchCore` CS:3795 and `ReplaceDurable` CS:4018, so Mesh writes wait for it. Rust: `with_exclusive_lock` RS:1038 and `with_shared_lock` RS:1031 (`fs2` flock on `<file>.lock`, `open_lock_file` RS:1162), taken by `pull_all` and `push_all`. TS: no cross-process lock; a per-store promise queue (`#enqueue`) only. Python: `threading.RLock` PY:119, in-process only.
- **Inputs/outputs.** A read-modify-write request; the lock is held across the read and the replace for every writer that merges.
- **Derived state.** A writer's header and merge base come from a read taken inside the lock (C# `PushAll`, `CommitBatchCore`, `ReplaceDurable`; Rust `write_all_unlocked`). TS and Python take them from a read outside any cross-process lock.
- **Forbidden writers.** Any Mesh write outside `AcquireLock` (the former `File.Delete` plus `File.Move` writer and its fixed `path + ".tmp"` are deleted). C# readers on open take no file lock (`ReadSnapshot`), and Rust's read-only snapshot takes none by design.
- **Shared paths.** The C# store and the Mesh single-file helpers; the Rust store.
- **Cut line.** `mesh-single-file-reader` h1.
- **Pinned by.** Mesh `AMeshWriteWaitsForTheStoreLock` (`CultMeshSingleFileStoreTests.cs:187`, deterministic) and `ConcurrentMeshWritersNeverErrorAndLeaveAReadableFile` (:261); mutant m3 (`ReplaceDurable` without `AcquireLock`) killed.
- **Known gaps.** TS and Python have no cross-process lock; the mesh spec cites ruling `variants:ruling:write-lock-python-now-ts-later`, and no follow-up in this record carries the remaining TS work. `variants:follow_up:store-replace-cut` item 3: no test that `ReplaceDurable` reads under the lock. Prior art: `docs/research/cross-process-file-locking-prior-art.md`.

### F. Replace (staging and atomic swap)

- **Owner.** C#: `WriteSnapshotAtomically` CS:4104 (unique temp `.{name}.{guid}.tmp`, `WriteThrough`, then `File.Replace`, or `File.Move` when nothing is there), called from `WriteSnapshot` CS:4062 and `ReplaceDurable` CS:4016. Rust: `replace_unlocked` RS:1010, `stage_and_replace` RS:3011, `replace_file_atomically` (unix `rename` RS:3095; Windows `MoveFileExW` with retry RS:3106), then `sync_parent_directory` RS:3198; outcomes `Rejected`, `NotReplaced`, `ReplacedNotDurable`. TS: `#writeAll` TS:124-141 and `renameWithRetry` TS:198 (unique temp, no fsync). Python: `_replace_all` PY:154-159 (fixed `<path>.tmp`, `Path.replace`, no fsync).
- **Inputs/outputs.** Encoded bytes in; the destination holds the old bytes or the new bytes, and a failed write removes its staging file (Rust pins the rename-failure path).
- **Derived state.** None.
- **Forbidden writers.** A second replace in a runtime. MESH's `WriteFileAtomically` is deleted; Mesh reaches the replace only through `ReplaceDurable`.
- **Shared paths.** C# cache flush, commit and Mesh writes; Rust `write_all_unlocked`.
- **Cut line.** `mesh-single-file-reader` h1 (C#). Rust's replace predates the campaign; `merge-r1` h3 added its rename fault seam.
- **Pinned by.** Rust `a_write_whose_rename_fails_is_not_replaced_and_leaves_no_staging_file` (seam `WriteStep::Rename` RS:3212). Mesh `ACacheOpeningDuringMeshWritesNeverSeesAnEmptyStore` (:212, Linux only; it skips Windows).
- **Known gaps.** `variants:follow_up:store-replace-cut`: (1) the Windows primitive, decided by open question `variants:question:windows-store-replace-primitive` (probe: `File.Replace` gave 1,722 empty opens in 17,313 under Mesh writers; `MoveFileExW` and POSIX rename gave none; `docs/research/windows-store-replace-prior-art.md`); (2) what a replace does to a live symlink (Mesh pins parity with a cache commit, `AMeshWriteOverALiveLinkLeavesWhatACacheCommitLeaves` :287); (3) a read-under-lock test; (4) a live directory link misreported as unreachable. Python's fixed temp name lets two processes share one staging file (research doc above). Rust's Windows replace loop and its rename fault line are unmutated (h3, undone).

### G. Soft write decision (a soft flush writes only where nothing is)

- **Owner.** The one reader decides; the writer does not. TS: `pushAll` TS:79-104 (absent writes; a readable store is left byte for byte; an unreadable one is refused; a dangling link is refused when soft). Rust: the soft branch of `push_all` RS:1268-1280 (`!read_store_unlocked()?.absent` returns). C#: none; `CultMesh`'s `FlushAsync(bool soft)` accepts the flag and drops it (`src/GameCult.Mesh/CultMesh.cs:433-436`). Python: no soft flush. cultmesh-ts passes the flag (`packages/cultmesh-ts/src/index.ts:4073`).
- **Inputs/outputs.** A flush with `soft`; no write for a readable store, a write for an absent one, the reader's refusal otherwise.
- **Derived state.** None. **Forbidden writers.** A soft check that reads the file its own way (TS `readStore` once returned zero bytes as present; deleted in h4).
- **Shared paths.** TS `pushAll` and Rust `push_all` only.
- **Cut line.** `merge-r1` h4 (TS) and h6 (Rust).
- **Pinned by.** The manifest walks run a soft flush over every vector in TS (the `softPushAll` operation, TS test :2336) and Rust (`soft_push_all`, RS:5149), each with an absent and a dangling-link case; `a_soft_push_all_writes_where_nothing_is_and_refuses_a_dangling_link` RS:6019.
- **Known gaps.** C# and Python have no soft decision, and the C# Mesh flag is dead; no follow-up is registered (h6 `undone` names it). `writer-dangling-link-tolerance` applies to the non-soft branch.

### What no cut owns yet

`c2a-write-set` replaces `ReplaceDurable` with `ApplyWriteSet` (comment at CS:4012-4013) and decides `writer-dangling-link-tolerance`. `write-set-vectors` owns `c0-bad-canonical-in-manifest` and `legacy-vectors-all-runtimes`. `merge-main` owns `range-check-in-ci`. Imagination owns `store-replace-cut`.
