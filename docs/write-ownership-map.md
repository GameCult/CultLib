# Write-ownership map

Imagination, 2026-10-27/28, session `self-2026-10-27c-eureka`. Base: CultLib `main` at `b7a68884` (C2a landed).
Serves `variants:follow_up:write-ownership-cut`. Spec: `variants:cut_spec:cut-write-ownership-cs.r1`. Question:
`variants:question:written-type-undeclared-drift`.

## The rulings: what decides and what illustrates

- `variants:ruling:cache-refuses-retype`. Her words decide: a stored record's schema id identifies its type, so a write of
  another type at that key is the wrong type and is refused, by a cache exactly as by a cacheless write. Retyping is an
  explicit remove plus add. The Household / `_pet` / rake analogy is an illustration, not a rule about object fields. Self's
  gloss (ownership becomes per staged key) is Self's reading, not hers.
- `variants:ruling:write-ownership-by-written-type`. Her words decide one thing: a record under the written type's own id is
  equivalent by construction, so which type owns that id cannot change the write. The full rule ("own id or one the written
  type declares it migrates from; anything else is refused; no registry, Shared included, and no owner/lister precedence
  enters a write's ownership decision") is Self's reading, which she accepted in the same exchange. Her remark that ids
  indicating structural equivalence beats demanding names match is context for the open
  `variants:question:store-schema-id-derivation`, not a ruling here.
- In force and touched: `variants:ruling:drift-record-write-ownership` (operator: a type that reads a record as compatible
  drift owns it and restamps it, as 2.0.1 does), `variants:ruling:mesh-writes-bound-by-declaration`,
  `variants:ruling:undeclared-read-only`, `variants:ruling:unheld-removal-refused-at-commit`,
  `variants:ruling:unheld-removal-multi-store-cost-ruled`.

The two Self readings disagree on one case: a record under an undeclared older id of the written schema name, read whole
(an undeclared version bump). Literal "anything else is refused" makes it read-only; `drift-record-write-ownership` makes it
owned. That is `variants:question:written-type-undeclared-drift`, and only the operator can settle it.

## Body facts (probed at b7a68884)

Probes: scratch tests in this worktree, removed. C# filtered runs on Starfire (one job each); TS and Rust on Yggdrasil
(`ygg-verify.sh`, scratch commit `78f604fd`, not pushed to the forge); Python locally. Types A (`tests.wo_a`) and B
(`tests.wo_b`) share one shape; N1/N2 are two versions of `tests.wo_n`, nothing declared.

| Probe | C# | TypeScript | Rust | Python |
|---|---|---|---|---|
| P1 cache {A,B}: B at k, then A at k | **C**: B's record replaced, k now A's id | C, and **both** records live at k | C, both live at k | C, both live at k |
| P2 cache {A} only, store holds B at k, write A at k | R `CultSchemaConflictException` (B is foreign, carried) | R at load: "No schema is registered for persisted schema id" | R at load, same | R at load: "Unknown persisted document type" |
| P3 cacheless `WriteDocument<A>` over B, registry null / {A} / {A,B} | R / R / R | no cacheless write | no cacheless write | no cacheless write |
| P4 cache {A,B}: remove k and upsert A at k in one batch | C (k is A's) | n/a | n/a | n/a |
| P5 cache {A,B}: remove, then add in a second commit | C | n/a | n/a | n/a |
| P6 N2 over an N1 record (undeclared name drift), cache {N2} / cacheless reg {N2} | C restamped / C restamped | not probed | not probed | C (cache) |

What this establishes:

- C#: one record per key. The only path that retypes is the cache arm (P1): `CacheBackingStore.RefuseUndeclared`
  (`CultCache.cs:3994-4009`) and the on-disk judge (`OwnsOnDisk`, `:4731-4763`) ask `Registry.Owns(schemaId, catalog)`
  (`:841-842`), which answers per schema id for the whole registry: any registered type declaring the id makes every
  registered type's write to that key owned. The cacheless arm already refuses the retype (P3) but decides it through
  `CatalogEntryLookup`'s registry half (`:596-634`) and the claimant precedence, which is what the ruling removes.
  The judge is also per batch, not per key: the cacheless predicate's `declared` is the union over every written entry
  (`:4752-4754`), and `UndeclaredOnDisk` memoises one answer per schema id (`:4784-4797`).
- Retype as remove plus add already works in the cache (P4, P5); the cut adds no new primitive.
- Directory store: its own judge (`DirectoryMessagePackBackingStore.cs:287-306`) asks `Registry.Owns` for every key,
  upserts included. It must take the same per-key rule.
- TypeScript, Rust, Python: records are keyed by `(type, key)` (Rust `entries: BTreeMap<(String, String), _>`,
  `lib.rs:2659`; TS `#entryId(definition.type, key)`, `cult-cache.ts:323`; Python sorts and stores by `(type, key)`).
  A write of A at k never meets B's record: both live at k (P1). None carries a foreign record; a store holding an
  unregistered type does not load (P2). So no leaf has a "write meets another type's record" path to fix. Bringing the
  ruling to the leaves first needs C#'s key identity (one record per key) and C#'s foreign carry, which are parity changes
  of the leaves' store model, not a write-ownership rule. Owned by the new follow-up `variants:follow_up:leaf-key-identity-parity`.
- Interop (unproven): a Python store holding A and B at k fails C# decode first on payload encoding (Python's
  lambda-defined document wrote JSON payloads), so what C# does with two records at one key was not reached.

## Model and authority table

| Value | Today (b7a68884) | After the cut |
|---|---|---|
| May an upsert of T replace the stored record at k? | cache: `Registry.Owns(id)` (any registered type); cacheless: union of written entries' declarations plus claimant over written entries and the lent registry | **Owner: `CultDocumentRegistry.WrittenOwns(T's entry, stored id, catalog)`**, per key, both arms, both store kinds |
| May a removal (or check) at k proceed? | cache: `Registry.Owns`; cacheless: the written-entries predicate | unchanged in kind: cache `Registry.Owns`; cacheless: owned when some entry the call writes `WrittenOwns` it (no registry) |
| Which type reads a record (claimant precedence) | `Claimant<T>` | unchanged; read machinery only, no write asks it except through `WrittenOwns`' one-entry lookup (option `own-entry-reads-whole`) |
| Lent registry in a cacheless write | encodes, decides copied-record v3 header, adds claimants | encodes and decides the copied-record header (`CopiedHoldIds`, `:4600-4630`); **adds no claimants** |
| `CatalogEntryLookup` registry half (`_others`, `Other`, `Others`) | live | **deleted** |
| `CultDocumentRegistry.Lookup` | live | **deleted** |
| `Owns<T>`'s `mayClaim` | live | **deleted** (the lookup holds only the written entry) |
| Foreign record at k | refused for every write | unchanged |

Authority map (also in the spec):

- Owner: `WrittenOwns(written, storedId, catalog)` decides whether an upsert of `written` may replace the record stored at
  its key. Inputs: the written catalog entry, the stored record's schema id, the catalog that publishes it. Nothing else.
- Derived: `Registry.Owns` is demoted to the removal/check rule and to load-time read-only marking; it no longer decides
  upserts. `Claimant` is read machinery.
- Forbidden writers: `Registry.Owns` on an upsert key; `CatalogEntryLookup` with a registry; the per-batch union `declared`;
  the per-schema-id memo across different written entries.
- Shared paths: cache staging (`Claim` -> `RefuseUndeclared`, `:3156`), single-file on-disk judge (`ApplyWriteSet` ->
  `RefuseUndeclaredOnDisk`, `:4565`, `:4736`), single-file `JudgeChecks` (`:4726`, checks only), directory store judge,
  `WriteRecord`, `WriteDocument`, unattached `PushAll`, Mesh's `WriteSingleFileDocument` and raw-payload write.

## Absorb or leave

- `variants:follow_up:drift-write-ownership-leaves`: **leave**, but it now waits on `leaf-key-identity-parity`. Its premise
  (give each leaf the C# Owns rule) assumes one record per key, which no leaf has (P1). The leaf write-ownership cuts in the
  sequence below close it together with this follow-up's leaf half.
- `variants:follow_up:public-store-writes-behind-cache`: **leave**. It asks who may stage behind an attached cache (API
  shape); this cut changes what any staging is judged by. No collision: direct `Push`/`CommitBatch` reach the same on-disk
  judge, so they get the per-key rule for free. Hands must not give direct staging a different predicate.
- `variants:follow_up:writer-dangling-link-tolerance`: **leave**. Rust/TS whole-store `push_all` reading a dangling link is
  a reader/writer tolerance question, not ownership; this cut touches no leaf.

## Deferred doc findings this cut fixes

The ownership-docs rewrite in part 1 is the fix for (each resolved `Deferred` to `write-ownership-cut`):
`variants:finding:cut-claimant-one-owner.s2.changelog-underdescribes-refusals`,
`variants:finding:cut-claimant-one-owner.s3.changelog-misstates-subset`,
`variants:finding:cut-claimant-one-owner.s5.refusal-list-ignores-declared-win`,
`variants:finding:cut-claimant-one-owner.s5.shared-read-stated-as-contract`.
Sites: `unity/org.gamecult.cultlib/CHANGELOG.md:16`, `src/GameCult.Caching/Contracts/cultcache-persistence-format.md:209-219`,
`CultCache.cs` `WriteRecord`/`WriteDocument` registry docs (`:4476-4519`). Self closes them `Fixed` after Soul holds.

## Cut sequence

1. `cut-write-ownership-cs` (spec r1, admitted, blocked on `variants:question:written-type-undeclared-drift`): C# reference,
   both arms, both store kinds, docs. Lands before the 3.0.0 release cut.
2. `leaf-key-identity` (one per leaf, TS, Rust, Python; follow-up `leaf-key-identity-parity`): one record per key and
   foreign carry at C# parity, with a cross-runtime vector. Not mapped.
3. `leaf-write-ownership` (one per leaf, after 2): `WrittenOwns` per leaf; closes `drift-write-ownership-leaves`. Not mapped.

Leaves ship on their own versions (cultcache-ts 0.14.2), not in the 3.0.0 Unity package, so 2 and 3 do not gate 3.0.0;
whether they must land first is Self's call.
