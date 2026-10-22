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
