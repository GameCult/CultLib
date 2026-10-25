# C2a land map

What C2a (`hands/variants-c2a`, a5c7954e) must still prove to merge to main, measured
on 2026-10-22 against main 7a8933b0 and the 2.0.0 release candidate
`hands/cultlib-unity-release-2.0.0` (5b5d8573, which carries f91e30ed). The land cut
is the merge plus the gate. Everything else is classified below. Governing ruling:
`cultlib-gaps:ruling:release-now-c2a-bumps-format`.

## Facts

**1. The merge.** A trial `git merge origin/main` on a5c7954e gives 12 conflicted files and
33 hunks of about 950 marker lines (CultCache.cs 17 hunks/196; cult-cache.test.ts 337;
single-file-messagepack-backing-store.ts 4/106; rs lib.rs 2/51; VariantTests 51; three
CHANGELOG add/adds; store-composition contract 32; Directory store 14; InteropPeer 21).
Main moved 4.8k lines in the cache runtimes since base 1a12bc2d. Three semantic collisions
are not textual:

- *Durable record identity has two owners.* Main added `CultStoredDocument.PersistedSchemaId`
  and `CacheBackingStore.MarkWritten` (73d438db, afd799ba, 3f6dda8b). Conditional commit
  compares the persisted schema id plus storedAt. C2a already owns this as
  `StoredSchemaId` + `StoredVersion` (SHA-256 of the stored bytes, which names the schema
  id and storedAt) set by `CultCache.Wrote`. After the merge, 9 references in
  ConditionalCommitTests and the contract name main's owner. The version is the owner.
  PersistedSchemaId and MarkWritten are deleted, and main's +309 test lines are ported as
  pins against StoredVersion. 2.0.0 ships MarkWritten as protected API, and C2a also
  removes `SerializeSnapshot`/`DeserializeSnapshot` overrides and changes `ConditionsHold`.
  C2a is therefore a major release (3.0.0), which semver forces.
- *TS store commit moved.* Main's `store-file.ts` (0e6452c9, 0010ee92; 391 lines) now owns
  commit, replace and the `<path>.lock`. C2a's v3 read and keep-v3-on-rewrite
  (`STORE_FORMAT_ELEMENT_IDS`) lives in the old store file and must move into store-file.ts.
- *Rust decode door.* Main's cultcache-rs 0.3.0 grown decoder (`decode_stack`, fc630d3b)
  must sit under C2a's `readable_store_format` and v3 `decode_store_file`.

**2. The gate already holds; it needs a test, not a format change.** C2a stamps
`cultcache.store.v3` (single file) or `...v5.directory-content-addressed-pages` exactly when
a record holds an id (`HeaderFor`, CultCache.cs:3543). It also marks when a copied record
decodes holding one (`CopiedHoldIds`, :4285). The 2.0 C# reader accepts only v1/v2
(CultDocumentMessagePackSerialization.cs:229) and directory v4 (Directory store :452).
Main's TS, Rust and Python readers accept only v1 (store-format.ts `isV1Snapshot`, lib.rs:1274,
stores.py:192). Probe on Yggdrasil at 5b5d8573 plus C2a's `v3-base.msgpack` (job
`gate1.log`): 4/4 pass. The single file is refused naming v3, and a later flush leaves it
byte-identical. A v5-stamped directory store is refused naming v5, and its 4 files are
byte-identical after a later add and flush. **The pin:** C2a already ships
`tests/vectors/document-variants-c2a/old-reader-refusal.sh`, which stages an old revision
plus probes on a scratch ref and runs them per runtime. The land cut sets its
`OLD_READER_REV` default to the tag `cultlib-unity-v2.0.0`. It also adds the never-rewritten
assertion and the v5 directory case to the C# probe, and runs all four runtimes at the
tag. If the tag is absent, the land waits for it. The tag, not a package, is the pin: the tag's
tree holds every runtime's released reader.

*Outside C2a's reach (release session):* in 2.0, `CultMesh.WriteSingleFileDocument`/
`WriteSingleFileDocumentPayload` (CultMeshSingleFileDocuments.cs:211-245 at 7a8933b0) build
a fresh one-record v1 snapshot and replace the file without reading it. A 2.0 Mesh write
to a C2a v3 path therefore discards it whole, ids and other records included, and no header
gate can stop a writer that never reads. C2a's version reads under the lock and refuses.
Fix it in 2.0.0 before tagging, or record it.

**5. Consumers.** Aetheria pins a CultLib revision (`VerifyCultLibRevision`) and Unity tags.
Ghostlight, Eve's Rust crates, Huginn and the Brokkr daemon pin cultcache-rs by git rev.
None of them moves when main moves. Two C# consumers build against an unpinned sibling
`..\CultLib` checkout: Eve `GameCult.Eve.Surface.csproj:16` and Brokkr's three csprojs.
Eve.Surface has `[MessagePackObject]` element arrays with no `[CultElementId]`
(`EveAssetCatalogEntry[]`, `EveEntitySoaBuffer[]`, `EveInputActionDocument[]`,
`IReadOnlyList<EveCommandTemplate>`, 0 ids in the repo), so C2a registration refuses its
documents as soon as the checkout pulls the land. Grep finds no object lists in Brokkr's
`[CultDocument]` files (not build-verified). Removed C2a API (`Options`, `AsSchemaAlias`) is
unused by Eve and Brokkr. Fork: `variants:question:unpinned-consumers-at-land`.

## Classification

| Item | Class | Why |
|---|---|---|
| c2a-format-version-gate | **a** | The gate test above. No new header: v3/v5 already refuse. |
| slot-reuse-contract-doc | **a** | The ruled consequence of the write set C2a lands. One paragraph in cultcache-schema-compatibility.md, absent at a5c7954e (grep). |
| foreign-ids-under-v1-header | **b** | Obsoleted by fb065f0d (write-time `CopiedHoldIds`, after the finding) for every C# writer with the type. The residual (non-C# or opaque writers) is what `non-csharp-writers-mark-ids-2` ruled to carry, and the contract states it (persistence-format.md:434-439). The gate changes neither side, so no fork remains. Resolve as Superseded by that ruling. |
| custom-resolver-element-ids | **b** | Fixed by b091d21f (19:52Z, after the 18:27Z follow-up): `ListElementsClaimedByAResolverAreRefused`, `AResolverClaimingTheListTypeOfAnObjectListIsRefused`. s6 holds on the claim loop. |
| codec-door-proven-mismatch | **b** | Done in h6 57fdf70c (s6 `only-proven-mismatch` Holds; Geometry 76/0). |
| consumer-element-id-sweep-r2 | **c** | Each repo's sweep is its own work (q3). Only the unpinned trackers matter at land, and that is the question below. Aetheria bumps a pin on purpose (C4). |
| schema-discovery-holder-recogniser | **c** | `TryGetDictionaryRefTypes` is identical on main (CultCache.cs:1288). It describes record-ref dictionaries and is not on the id path. Changing it moves schema hashes, which is its own epoch event. |
| copy-check-memo | **c** | Performance only (about 3x flush on unmarked stores of 20k records). Correct as is. |
| string-key-field-and-shadow-pins | **c** | No wrong behaviour at 64ec14f9. These are test pins and may ride the land cut if Hands touches the file. |
| declared-range-gate-own-version, range-check-in-ci | **c** | The script exists only on C2a and runs in no CI. It guards a TS publish, not a merge. Fix both before the first TS release from main after the land. |
| s6 Unproven: Unity/IL2CPP identity, other MessagePack versions | **c** | The merge reaches no Unity build. Unity sees C2a only at the 3.0.0 tag, so measure it in the Unity release cut (operator Unity check, C3). |
| multi-store-commit-atomicity, store-replace-cut, reader-refuses-duplicate-keys, soft-flush-all-runtimes, writer-dangling-link-tolerance, unique-index-cut, py-shared-write-lock, rust-lock-open-waits, ts-lock-test-hardening, c0/legacy/directory vectors, culture-test, wire-id-cuts, remaining-variant-cuts, golden-path, paged store | **c** | None says "before land". Each is pre-existing on main or C2a-independent. None lets a released reader strip ids or corrupt a C2a store. |

## The land cut's shape (transcribe once the question is ruled)

- **Base.** Merge main at or after the `cultlib-unity-v2.0.0` tag into `hands/variants-c2a`.
  Versions stay main's. CHANGELOGs get Unreleased entries, and the Unity entry says major.
- **Deletes and collapses.** Delete `PersistedSchemaId`, `PersistedIdentity`,
  `CultStoredDocument.MarkWritten` and `CacheBackingStore.MarkWritten` (main, about 60 lines),
  and the store-composition contract's MarkWritten paragraph. C2a's `StoredVersion`/`Wrote`
  is the one owner. In TS, delete C2a's header handling in
  single-file-messagepack-backing-store.ts and move it into store-file.ts. In Rust, C2a's v3
  decode goes through the grown decoder.
- **Adds.** Retarget old-reader-refusal.sh to the tag. Add the C# probe's never-rewritten
  assertion and the v5 directory case (as in gate1.log). Add the slot-reuse paragraph.
  Port main's ConditionalCommitTests to the version identity.
- **Verification.** Full suites: Caching, Mesh, Networking, Geometry, WebSockets,
  ConsumerBuild, InteropPeer, cultcache-ts/-rs/-py, cultnet-ts and cultmesh-ts, all on
  Yggdrasil. Run old-reader-refusal.sh for all four runtimes at the tag, and it must refuse.
  Negative greps over the merged tree: `PersistedSchemaId|MarkWritten`, conflict markers.
  Mutation: drop the v3/v5 stamp in `HeaderFor` and the gate probe must fail.
- **Estimate.** About 950 conflict lines resolved. Net -60 to -150 from the identity
  collapse and TS de-duplication, +80 to +120 tests and docs. One Hands pass of 100-150
  calls, then one Soul pass.
- **Precondition (question).** Eve.Surface, and Brokkr if confirmed, are pinned or swept
  before the merge reaches main.

## Store gate: attachment is the attach's hold (store-gate-one-owner r2)

Spec `variants:cut_spec:cut-store-gate-one-owner.r2` (base ec94ab98), question
`variants:question:pulled-variant-codec`. It closes Soul s3's
`attach-window-cacheless-write` (Medium) and `codec-loan-pinned-by-reflection` (Low).

**Facts (source reads at ec94ab98 and a probe, 2026-10-25).**

- The window. AddBackingStore holds the cache's gate, then the store's gate through
  `InStoreHold`, lends the registry and codec, sets `Loaded`, adds the store to `_stores`,
  pulls, and only then sets `store.Cache` (CultCache.cs 2268-2312). `InStoreHold` lets any
  call on the same store's gate re-enter it (2825-2831). So a journal the pull's Admit runs
  on the attaching thread can call `store.WriteDocument`: `Gate(cacheless)` guesses null,
  re-enters the store's gate, finds `Cache == null` and runs the write, and its `finally`
  calls `UseRegistry(null, null)` (Soul s3 P1, full1.log lines 49-53). Other threads are not
  in the window: they queue on the store's gate and see either the published cache or a
  store that the failed attach put back.
- The codec loan has no reader on the attach path. A pulled variant's codec is read only
  by `CultStoredDocument.HoldsIds` (543-557). Header decisions read `HoldsIds` only on
  staged entries (single-file 4318, 4447 and 4472; directory store 362), and a staged entry
  is always the document the cache pushed (`Push` sets `Entries[key] = entry`, 4211-4215).
  A single-file `PullAll` returns without loading while the store is dirty (4157-4159), so
  a pulled copy is never staged. A refused write unstages its keys before it adopts the
  reload. The directory store refuses variants (ruling `q6-directory-and-cultnet-refuse`;
  DirectoryMessagePackBackingStore.cs 139-144 and 642-643). Admit re-resolves every pulled
  variant with the cache's codec (CultCache.cs 3259). Soul's N4 mutant (the attach lends
  no codec) was caught only by the reflection test, and h4's M2 only by a bare-store test
  that sets `Loaded` and calls `UseRegistry` by hand.
- Probe (scratch clone `imagination-store-gate-r2/clone`, commits 4663934e and c099b9e8 on
  ec94ab98; Yggdrasil t1.log, t2.log and t3.log; 2026-10-25 around 20:50 CEST). The
  probe did three things. `InStoreHold` refuses a cacheless call that re-enters the same
  store's gate. The `_codec` field and the two-argument `UseRegistry` are deleted. The pull
  builds variants with a null codec. Results:
  - Mesh is 342/0/4.
  - In the Caching suite, the only failures were two probe tests, both from authoring
    errors: a key `AddAsync` minted, and an element id that was not 12 hex characters.
    No existing test failed, `UserCodeInACachelessCallMayCallItsOwnStore` included.
  - Soul's P1 as a test: the journal's write is refused with the new message. The attach
    keeps its registry and the file holds only `a`. After the attach, a cache write, a
    flush and `store.PullAll()` all succeed.
  - A cacheless write nested in another on the same store is refused, the outer write
    fails, and no file is written.
  - Through a real attach, a store holding a name-override variant flushes an unrelated
    commit with a `cultcache.store.v2` header. With no codec loan, the pulled copy does not
    mark the store.
  - Mutant `return cacheless` changed to `return false`, which is ec94ab98's behaviour,
    fails both new gate tests. In that mutant the nested write succeeds and the file exists.

**Rationale.**

- Attachment has one fact again. While the attach runs, the store's gate is the attach's
  hold, and `Cache` is published when that hold ends. A cacheless write is a write the
  store owns outright, so it must be the outermost hold of its store's gate. One more
  argument to the existing owner of per-thread gate facts (`InStoreHold`) refuses it. No
  field mirrors `Cache`, and `Loaded` and `_registry` are not read. Precedent:
  `ConcurrentHashMap.computeIfAbsent` refuses a mapping function that updates the same
  map with `IllegalStateException("Recursive update")` since Java 9. That is from
  memory, not fetched this pass; the 2026-10-23 prior-art note covers the documented
  restriction in its counter-positions. Non-cacheless store calls from user code still
  re-enter: `UserCodeInACachelessCallMayCallItsOwnStore` stays green.
- The late publish stays. It is what bounds `Gate` at two passes (s2's finding). Publishing
  early would bring back the failure-rate-bounded retry.
- The codec loan goes: it is a value nothing on the attach path reads. A pulled variant
  carries no codec and counts as holding ids (the documented conservative answer), and
  the cache's resolved copy is what any header decision sees. This reverses the codec
  half of Defaulted ruling `late-publish-needs-attach-codec`, so it goes to Self as
  `variants:question:pulled-variant-codec`. The spec is written for the recommended option.
- Scar check: `git log -S` over CultCache.cs for `reentrant`, `outermost` and `_attaching`
  finds nothing. `Cache?.Codec` appears in 1468a83a (h4) and 619e089f only. No earlier
  re-entry refusal was built and deleted.
- Behaviour change, stated in the CHANGELOG: a cacheless write made from user code that
  another cacheless write of the same store runs used to succeed, and its `finally` took
  the outer call's registry. It is now refused.
