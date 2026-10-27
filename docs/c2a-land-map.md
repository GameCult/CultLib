# C2a land map

What C2a (`hands/variants-c2a`, a5c7954e) must still prove to merge to main, measured
on 2026-10-22 against main 7a8933b0 and the 2.0.0 release candidate
`hands/cultlib-unity-release-2.0.0` (5b5d8573, which carries f91e30ed). The land cut
is the merge plus the gate. Everything else is classified below. Governing ruling:
`cultlib-gaps:ruling:release-now-c2a-bumps-format`.

## Cut sequence to main (2026-10-27, branch head 7c7cc799)

Landed since the facts below: `land-c2a` (h1), `land-c2a-merge-api-docs` (h1-h6), the 2.0.1 and
cultcache-rs 0.3.1 merges (5baa02fa, f54472e5), `land-c2a-gate` r4 (h1), `store-gate-one-owner`
(h1-h7, Soul s6) and `restamp-on-write` (h1-h2, 8ee66714..7c7cc799, Soul s2 holds). One static `Owns`
decides ownership for cache and cacheless writers, and the High finding
`cut-land-c2a-gate.s1.reflist-record-read-only-at-head` is Fixed by 7c7cc799. origin/main is 73e684b1:
21 commits past the merge base 02cf04b5, all docs and research (15 files, +1879, none under src/,
tests/ or packages/, none on a path the branch changed). What stands between the branch and main, in
order:

1. **claimant-one-owner** (spec r1 at 7c7cc799, Hands then Soul). Restamp's Soul s2 left four Lows
   on the landing path. Which entry reads a persisted entry is chosen twice (`Claimant` at
   CultCache.cs:703-750, `ClaimantEntry` at :797-799), and so a cacheless writer handed registry {A,B}
   overwrites a record that a cache with {A,B} holds read-only (probe X4). The cut keeps one `Claimant`
   precedence over a by-id/by-name lookup that the registry or a single entry supplies. A cacheless
   writer with a registry then takes its claimant from that registry. The cut also pins the
   unpublished-id case and fixes the stale test comment. It closes `claimant-selection-duplicated`,
   `cacheless-inverts-registry-claimant`, `unpublished-id-guard-unpinned` and `stale-flag-copy-comment`.
   It is kept apart from the gate so that Soul can falsify it alone. Its files (CultCache.cs, the
   persistence contract, RestampOnWriteTests.cs) are disjoint from the gate's, so it can run before or
   beside step 2. The second cut to land rebases.
2. **land-c2a-gate** (spec r7 at 7c7cc799, Hands h2 then Soul). The ref-list gate does the same write
   at 2.0.1 and at head and compares schema ids. The committed vectors are pinned byte for byte under a
   fixed clock, and every stage counts its cases. r7 drops r6's head-only restamp assertions, which
   `RestampOnWriteTests.TheCommitted1060VectorsListRecordIsWrittenUnderTheHoldersOwnId` already pins.
   It also drops the red-at-193cf1c3 run, because restamp is measured. The cut closes
   `single-vector-hand-edit-passes`, `gate-green-with-no-refusal-probes` and follow-up
   `gate-reads-1x-written-store`.
3. **Ledger** (Self, no Hands; can run now, beside steps 1 and 2). Each of these is in force though
   its fix has landed:
   - The seven `cut-land-c2a.s1.*` findings. `land-c2a-merge-api-docs` carried their pins (source reads
     2026-10-27: GameCult.Caching grants InternalsVisibleTo only to `.Tests` and `.MessagePack`, the
     residual being follow-up `caching-messagepack-ivt`; PreCut2StoreFormatTests pins "with a suffix
     of"). Close each one against its commit, or defer it. `stale-unity-plugin-dlls` defers to the
     Unity 3.0.0 release cut (the plugins are 2.0.1 builds, 1eeb1a01).
   - `cut-c2a-write-set.s1.untouched-dropped-slot-resurrects` and follow-up `slot-reuse-contract-doc`.
     Ruling `slot-reuse-documented` decided the behaviour, and the paragraph is in
     cultcache-schema-compatibility.md:64-66.
   - `cut-restamp-on-write.s1.websockets-tests-unreported`, Fixed by h2 (s2 promise
     `websockets-tests-reported` holds).
   - Follow-up `gate-bound-test-tighten`, carried by restamp r2 (50362447, a768b821). Close it if
     restamp's Soul passes measured it; otherwise route it.
   - After steps 1 and 2 hold: the four s2 Lows, the two remaining `cut-land-c2a-gate.s1` Lows and
     `gate-reads-1x-written-store`.
4. **Consumer pins** (ruling `unpinned-consumers-at-land-3`, follow-up `consumer-pins-before-land`).
   Brokkr, Aquarium, Ymir, AquaSynth and Mimir have no `CultLibRevision`/`VerifyCultLibRevision` guard,
   and each builds against the sibling `..\CultLib` checkout. In its own repo, each adopts Aetheria's
   guard at the 2.0.0 tag, with one Hands per repo, before the merge reaches main. The pins touch no
   CultLib file, so they can start now and run in parallel with steps 1-3. Eve and Gjallar get no pin
   (ruled). The step is done when each repo's guard fails a build against a CultLib checkout at
   another revision and passes at the tag, and the follow-up is resolved.
5. **Main into the branch, then full verification.** After steps 1, 2 and 4, `git merge origin/main`
   into `hands/variants-c2a`. It should be clean, because main's commits since 02cf04b5 are docs and
   research only. Re-check that before merging (`git diff --stat 02cf04b5 origin/main -- src tests
   packages unity`). Then run every suite at the merged head on Yggdrasil: Caching, Mesh, Networking,
   Geometry, WebSockets, ConsumerBuild, InteropPeer, cultcache-ts/-rs/-py, cultnet-ts and cultmesh-ts.
   Run `old-reader-refusal.sh gate` for csharp, typescript, python and rust. If main gained code by then,
   a Soul pass covers the merge.
6. **Land.** The land is `git merge --no-ff`, never a squash, so the SHAs that every report and verdict cites stay reachable from main (skip the branch's non-building commits with `git bisect --first-parent`). Steps 1 and 2 closed at 723b7996 (claimant-one-owner, land-c2a-gate); step 5 closed at the merge commit 3157d4b3. Run `git merge --no-ff hands/variants-c2a` onto main, with an empty diff against the
   verified head. There is no tag: tags, the Unity plugin rebuild and `declared-range-gate-own-version`
   belong to the 3.0.0 release cut. Open follow-ups `c2a-load-slower` (load 2 to 2.3 times slower than
   main on a synthetic Aetheria catalog) and `drift-write-ownership-leaves` do not say "before land".
   `c2a-load-slower` still needs a reason or a fix before the 3.0.0 release, under
   `cultlib-gaps:ruling:database-shaped-built-for-load`.

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

## Store gate: Cache is the one attachment fact (store-gate-one-owner r3)

Spec `variants:cut_spec:cut-store-gate-one-owner.r3` (base 0fa836cb), superseding r2. It
closes Soul s4's `attach-window-held-store-writes` (Medium), `cacheless-refusal-order-unpinned`
(Low) and `interop-consumer-suite-unrun` (Low). This is the third time the attach window has
beaten the rule (s3 from the cacheless side, s4 from the held side), so r3 moves the
decision instead of patching the next entry point.

**Facts: every entry point that reads or decides attachment, at 0fa836cb.** Source reads of
CultCache.cs, DirectoryMessagePackBackingStore.cs and CultMeshSingleFileDocuments.cs. The
window column is a journal on the attaching thread, through a real attach of a store holding
`a`. Probe: scratch clone `imagination-store-gate-r3/clone`, commit b2330b6d (test R3Probes),
Yggdrasil `probe-base.log`, 2026-10-25 around 23:00 CEST. "Disagree" means that after the
attach, a commit of `later` and a flush, the attaching cache holds `a,later` and a fresh cache
over the same file reads something else.

| Entry point (line) | Fact it reads | In the attach window at 0fa836cb |
| --- | --- | --- |
| Single-file `PullAll` (4156) | `Gate`: `Cache` guessed unlocked, then re-read under the re-entered store gate | Runs as detached; its `Loaded` re-enters the cache and is refused as a journal write. Agrees. |
| Single-file `Push` (4212) | same | Runs as detached and stages `b`. Disagree: fresh reads `a,b,later`. |
| Single-file `Delete` (4223) | same | Runs as detached and stages a removal the cache never made (dirty after the attach). |
| Single-file `PushAll` (4239), and `Dispose` (3722) through it | same, then in-body `Cache == null` (4531, 4543, 4588, 4603, 4611, 4661) | Runs as a cacheless writer. Push then PushAll writes `b` to the file inside the attach (disagree). Delete then PushAll is refused with the cacheless writer's text. On a variant store it is refused as "a writer with no CultCache" (Soul s4). |
| Single-file `CommitBatch` (4256) | same | Runs as detached and commits `b`. Disagree. |
| Single-file `JudgeChecks` (4568), `RefuseRemovalOfUndeclared` and `StageChecks` (4558, 3915) | same | Run as detached. Agree in the probe, but under the wrong gate's rules. |
| Single-file `ReadStoreSnapshot` (4687) | same | Runs as detached and reads the file. |
| `WriteRecord` (4342), `WriteDocument` (4366) | `InStoreHold`'s per-thread hold (cacheless re-entry), before `Cache` | Refused with the re-entry text (h5). Agree. |
| Directory `PullAll` (48) | `Gate`, as above | Refused as a journal write. Agree. |
| Directory `Push` (147), `Delete` (160), `CommitBatch` (172) | same | Run as detached. Disagree: fresh reads `a,b,later` after Push or CommitBatch, and `later` after Delete. |
| Directory `PushAll` (235) | same | Runs as detached. Push then PushAll and Delete then PushAll disagree. |
| Directory `JudgeChecks` (269), `RefuseRemovalOfUndeclared` (259) | same | Run as detached. Agree. |
| In-body reads: `StoredAtOver` clock (4028), refusal text and `DurableAt` (4531, 4543), declares and cacheless refusal (4588, 4603, 4611), `Wrote` (4661) | `Cache`, under the gate `Gate` chose | On the attaching thread they read null while the thread holds the cache's gate, so a held call runs as a cacheless writer. |
| `AddBackingStore` (2259-2314) | writes `Cache` under both gates, after the pull | The window's author: the store is attached for the pull (registry lent, `Loaded` set, in `_stores`) while `Cache` is null. |
| Mesh `CultMeshSingleFileDocuments` writes and reads (99, 143, 174, 226) | none of the attached store's | `StoreAt` (239) builds a new store per call, so they never touch an attached store object. A file it shares with an attached store is the file lock's and the write set's business. |
| `IsDirty`, `HasStagedChecks`, `ForeignRecords`, `LastSchemaMigrationReports`, `HeaderFor`, `HeaderForWrite` | none | Ungated values or static decisions. They read no attachment fact. |

So during an attach, cacheless entry points decide attachment from the hold, and every held
entry point, plus every in-body read, decides it from `Cache`, which is still null. These are
two facts.

**Probe of the chosen shape** (commit 4695390c on b2330b6d; `proto-full.log`). `AddBackingStore`
publishes `Cache` before the pull and takes it back in the same hold if the pull fails. `Gate`
becomes straight-line code: guess, then the store's gate, where `Cache` is settled, then the
settled cache's gate. Results:

- All 21 probe cases are refused. The cacheless writes get the re-entry text and every other
  case gets "A journal entered the cache it journals". After the flush, the attaching cache
  and a fresh cache both read `a,later`.
- Caching: the only failures are the two tests that pin the late publish:
  `AnAttachLendsTheRegistryAndPublishesTheCacheAfterThePull` (asserts no cache during the
  pull) and `ACallWhoseGateChangesTwiceUnderItThrowsInsteadOfLooping` (asserts the old message
  text; the tampering reader still throws). The churn, queued-reader and failed-attach tests
  pass unchanged.
- Mesh and Networking are green.
- The cultcache-ts suite, which builds and drives the C# InteropPeer, passes on this shape:
  175 tests, 171 pass, 0 fail, 4 skipped. The C#, Rust and Python interop tests ran, the C#
  edit-in-place one included (`proto-ts2.log`, commit f5f02053). Recipe `clone/r3ts.sh`:
  image node:24-bookworm, apt python3-msgpack, dotnet-install for 10.0, rustup minimal.

**Shapes weighed (rationale).**

- (a) Publish `Cache` before the pull, as a616431e did. Chosen. What s2 found was not that an
  early publish is unbounded. It was that `Gate` re-read `Cache` *unlocked* after a stale
  guess, so a churn of failing attaches could hand it a new stale guess every pass. The fix is
  where `Gate` reads on its second step. Under the store's gate, `Cache` is settled: an attach
  publishes and takes it back inside one hold of that gate, and no other thread can hold that
  gate during the attach. A settled cache is permanent, because there is no detach. So `Gate`
  needs at most three acquisitions, with no loop and no counter: the guess's gate, the store's
  gate, and then the settled cache's gate, which cannot miss. This deletes a fact rather than
  reconciling two. During the attach the store *is* attached, so every entry point, cacheless
  or held, and every in-body read sees one answer. A journal's call is then refused by the rule
  journals already have (AddJournal: "any hold on this cache while a journal runs throws"),
  exactly as it is after the attach. Prior art: `ConcurrentHashMap.computeIfAbsent` installs a
  ReservationNode in the bin *before* running the mapping function, so a recursive update on
  the same thread finds the owner and throws, and on failure the reservation is removed (see
  `docs/research/2026-10-23-store-lock-user-code-prior-art.md` item 4 on main: the re-check
  after locking, and the ReservationNode on the empty-bin path). The validate-after-acquire in
  `Gate` is the same item.
- (b) Keep the late publish and make the attach's hold the one fact for every entry point.
  Rejected. `InStoreHold` knows that this thread holds the store's gate, not *whose* hold it is.
  Refusing every same-store re-entry kills `UserCodeInACachelessCallMayCallItsOwnStore` (r2's
  R5), so (b) needs an "attaching" marker beside `Cache`. That is the second fact that s3 and
  s4 rule out, plus every in-body `Cache == null` read would need to consult it as well.
- (c) Run no user code on the attaching thread under the store's hold during a pull: defer the
  pull's journals to after the hold (open calls). Rejected as insufficient on its own. Getters
  and constructors still run during the pull's decode, so the window stays open to any user
  code that is not a journal. It also moves journal timing, which is a documented contract
  ("right after an admission is applied").
- Scar check: `git log -S` over CultCache.cs finds `store.Cache = null` and `MaxStaleGuesses`
  only in ba658965 and 1468a83a. The early publish was not removed for a correctness reason of
  its own: 1468a83a removed it to bound `Gate` and to lend the codec, and the codec loan is gone
  (`pulled-variant-codec`). No settled read under the store's gate was built before. The
  precedent recipe (no root filter) finds no same decision.
- Not a product fork. A journal's store call during an attach now throws, as it already does
  after the attach. Cacheless writes on the attaching thread stay refused, with the same text.
  Getters and constructors during the pull see an attached store, as they do during any held
  call. It changes the late-publish clause of Defaulted ruling `variants:ruling:pulled-variant-codec`,
  so Self defaults a superseding ruling (recommended label `cache-published-for-the-pull`)
  before dispatch.
- Residual, not in r3. `Push`, `Delete`, `PushAll` and `CommitBatch` are public, so user code
  that is not a journal (another thread, or a getter during any held call) can stage records
  behind an attached store's cache. Soul s4 shows this with `other-thread-push`: file `a,b`,
  cache `a`. That is true before, during and after an attach, so it is not the window's
  two-fact problem. Closing it changes public API (a store's writes taken only from its
  cache), so it is a follow-up and an operator question for Self, not part of this cut.

## Body facts: claimant-one-owner r2 (Imagination, 2026-10-27, base fc74cf6f)

Why a revision: Soul beat the same rule three times (s1 claimant choice, s2 compatible ids, s3 shape). Each time a
cacheless write lent a registry let the registry's entry for the written schema id decide ownership beside the entry
the caller passed. Two descriptions of one type reached `Owns`; each fix (h2 schema-id compare, h3 subset of compatible
ids) compared one more facet of the two and left the rest open. r2 removes the second description from the lookup.

### Where the two descriptions met (source read, fc74cf6f)

- `OwnsOnDisk` cacheless arm (`CultCache.cs:4725-4740`): with a lent registry it called
  `lent.Owns(id, catalog, declared, written)`, whose lookup is the registry's `_indexes` and whose `entryOf` is
  `descriptor.ToCatalogEntry()` (`:824-825`). The claimant for the written schema id was the registry's descriptor,
  so `ReadsWhole(persisted, claimantEntry)` (`:848`) judged shape against the registry's entry, and the claimant
  precedence (`:756-806`) walked the registry's compatible ids. `written` (`:4734-4736`) then compared the two entries
  by schema id plus a subset of compatible ids.
- `WriteDocument` (`:4495-4524`) takes its entry from the registry (`resolvedRegistry.GetRequired<TDocument>().ToCatalogEntry()`),
  so its two descriptions are always identical. Only `WriteRecord(record, catalogEntry, holdsIds, registry)` (`:4471`) can
  pass an entry that disagrees with the registry's entry for the same id.
- `CopiedHoldIds` (`:4600-4630`) also reads `AttachedRegistry`, to decide the v3 header from copied records. It is a
  positive-only header decision, not ownership, and out of this cut.

### Consumer audit (2026-10-27)

- `WriteRecord` and `WriteDocument` are new in the Unreleased 3.0.0 entry; `main` has neither (`git show main:src/GameCult.Caching/CultCache.cs`).
- Per-repo `rg` over every git repo directly under `F:\Projects` for `.WriteRecord(`, `.WriteDocument(`, `WriteSingleFileDocument`
  (Library, obj, bin, PackageCache excluded): hits only in CultLib and in vendored CultLib copies under Heimdall and StreamPixels
  (`vendor\CultLib`, older pins without these methods). No consumer outside CultLib calls either method.
- Inside CultLib at fc74cf6f: Mesh `WriteSingleFileDocument` calls `WriteDocument(key, document, storedAt, registry)`;
  Mesh's raw-payload path calls `WriteRecord(..., registry: null)` (`CultMeshSingleFileDocuments.cs:129, 226`). The only
  `WriteRecord` calls with a registry are tests: `CachelessSingleFileTests.cs:453, 660` (a registry `{IdDeck}` that does
  not hold the written entry `One` at all, used for the copied-record header), `RestampOnWriteTests.cs:595, 599`, and
  Soul's probes. Packages: no TS, Python or Rust counterpart takes a registry (cultnet-rs `write_record` is an example helper).
- Consequence: the registry's documented role in `WriteRecord` is to describe the other records in the store. A shape
  that derives the written entry from the registry (or refuses an entry the registry does not hold) breaks
  `CachelessSingleFileTests.cs:660` and removes that role.

### Candidate shapes weighed

| Shape | One description? | Public API | Kills s2/s3 | Cost |
|---|---|---|---|---|
| A. Lent registry's entry decides; refuse a passed entry that disagrees | yes, after a door check | signature kept; new refusal | yes | an equality check over a redundant parameter; breaks the registry-describes-others use (`:660`) when the registry lacks the written type unless it falls back to the passed entry, which is two descriptions again |
| B. Passed entry is the only description of its schema id; the registry supplies other types only | yes, by construction | signature kept; doc only | yes (probed) | one lookup class gains the registry; one registry overload deleted |
| C. Overloads: `WriteRecord(record, catalogEntry, holdsIds)` and `WriteRecord(record, registry, holdsIds)` | yes, by type | changes a 3.0.0 signature | yes | loses the registry-describes-others use |
| D. Delete `WriteRecord`'s registry parameter | yes | changes a 3.0.0 signature | yes | loses the copied-record header decision for raw writes |

Picked B. It is the only shape that keeps every documented use of the registry, needs no public signature change,
and deletes the comparison instead of extending it. It is ordinary scoping (an inner binding shadows the outer one
for its name), applied to schema ids.

### Probes (Starfire, local filtered runs, 2026-10-27)

Scratch clone of fc74cf6f at `<scratchpad>/imagination-claimant-r2/clone`, Soul's `SoulClaimantProbe.cs`,
`SoulClaimant2Probe.cs`, `SoulClaimant3Probe.cs` copied into `tests/GameCult.Caching.Tests`.

Base fc74cf6f (`probe-base.log`):
- P7 (registry {N lists own, A, B}; passed entry N with the list varied; record k under O, named like N): lent R / no
  registry C for rows b, b2, e, g, h, k, l, l2; lent C for a, c, d, f, i, j. Confirms `subset-refuses-own-drift`.
- P9 (registry {NW: Name, Value, Colour}; passed NW minus Colour): lent C (restamped, Colour lost), no registry R.
  Confirms `lent-shape-widens-passed-entry`.
- P6 (bare X1 entry, registry {X1} listing OX, record under OX): lent R, no registry R, cache C. h3 closed the id axis.
- P4: 12 states x 8 registries, violations=0. P1d: a cache {N,L} commits N over L's listed old id (`cache-replaces-other-types-record`).

Prototype B (scratch commit 349ea3f3 on the clone, not for landing; `probe-protoB2.log`): `CatalogEntryLookup` takes
the written entries and an optional registry and drops every registry descriptor whose schema id a written entry has;
the registry's `Owns(schemaId, catalog, declared, mayClaim)` overload is deleted; the cacheless arm always asks
`CultDocumentRegistry.Owns(id, catalog, declared, new CatalogEntryLookup(writes, AttachedRegistry), entry => entry,
claimant => writes has claimant's schema id)`.
- P7: lent == no registry on all 14 rows (all C).
- P9: lent R, no registry R, wide entry C.
- P6: unchanged (R, R, C). P4: identical table to base, violations=0. P1a-f, P2a-d: identical to base.
- `RestampOnWriteTests`, `CachelessSingleFileTests` and the probes: 139/139 passed locally.
- Yggdrasil, 349ea3f3, `ygg-protoB-full.log`: GameCult.Caching.Tests 1180 passed / 0 failed / 2 skipped;
  GameCult.Mesh.Tests 342 / 0 / 4.

Negative greps, each tested at fc74cf6f (each matches there, and none may match after the cut, src and tests):
`claimant.CompatibleSchemaIds.All(` (1, the subset), `lent.Owns(` (1), `Func<CultSchemaCatalogEntry, bool> mayClaim) =>`
(1, the deleted overload), `CatalogEntryLookup(writes)` (3, one-argument construction). CHANGELOG and docs:
`never widens what the passed entry declares` (1), `with no compatible id the passed entry does not list` (1).

### Not settled by r2

- `cache-replaces-other-types-record` (s2, PreExisting, cache arm): r2 touches only the cacheless lookup, so it does
  not settle whether a cache may replace another registered type's record at a shared key.
- Whether a registry-less `WriteDocument` should consult `CultDocumentRegistry.Shared` for claimants at all (s1
  `shared-decides-registryless-write`, closed Fixed by documentation in h2). r2 keeps today's answer.
- `variants:finding:cut-restamp-on-write.s2.cacheless-inverts-registry-claimant` is still in force although r1 was
  mapped to close it; Self should close or re-triage it after r2's Soul pass.
