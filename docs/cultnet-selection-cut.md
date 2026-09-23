# CultNet typed selection: cut map

Date: 2026-09-17. Imagination output. Intended path: `F:\Projects\CultLib\docs\cultnet-selection-cut.md`.
No code in this document has been written; nothing here is committed by
Imagination. This is its own campaign with its own map; it does not touch
`docs/typescript-quic-realtime-cut.md` or any code in that tree.

**Progress, 2026-09-22.** Cut 1 is running (Hands, Sonnet) on the branch
`cultnet/selection-cut1`. It will not merge to `main` until Soul has passed it.

- **Commit 0, `e420410`:** the cache's public read surface. Twelve tests, six
  mutation entries, all killed.
- **Commit 1, `17d10e0`:** `CultNetSelection` and its evaluator, the three v1
  messages, and the three §1 selector engines deleted. v0 now lowers into a
  selection. Test results: Networking 162/162, Mesh 252/253 (one skip that was
  already there), Caching 188/188. Nine networking mutation entries, all killed.
- **Mutation harness:** `scripts/mutate-dotnet.ps1` is a shared dotnet/NUnit
  harness and works for any NUnit project in this repository.
- **Not run through mutations yet:** S1, S2, S6, S9–S15, S17, S20 and S23. A
  test pins each one, but none has a mutation entry.

Two things were found that this map did not name:

- **`CultNetDocumentBinding` can carry a schema id that differs from its
  descriptor's.** The deleted engines checked for this; the evaluator reads
  only the descriptor and cannot see it. The fix is at the call sites
  (`WithBindingSchemaAlias`, `ExpandSchemaBindingAliases`), so the evaluator
  stays independent of the registry.
- **A fourth copy of schema matching, at `CultNetDatabase.cs:1707`**, which
  serves `CreateShardSnapshotResponse`. §1 counted three engines; there are four.
  **Self's ruling, 2026-09-22: it collapses into the evaluator in commit 2.**
  Leaving it would make the claim of one evaluator false. It is a deletion and
  takes a mutation entry like the others.

**Q-J was not implemented, and the map is the reason.** Commits 0 and 1
shipped `number` as `double?` (`CultNetSelection.cs:71`), `CompareNumber` as
IEEE comparison (`CultNetSelectionEvaluator.cs:103-113`), a JSON schema that
says `"type": "number"`, and a cache `NumberOf` that returns `double`. The
operator had already ruled decimal strings, and §16 recorded it, but §2 and
D8 still specified float64, and nothing specified the canonical form or the
comparison that the ruling explicitly required. Hands built what §2 said. The
Rust Hands caught the conflict before writing a line and stopped, correctly.
**Self swept §2, D8 and the §7 table on 2026-09-22**, and §2 "Numbers" now
specifies the form. The Q-J fix lands on the branch before the Rust runtime,
as its own commits. Commit 1 is not wire-final until it does.

**Commit 2 landed** (Sonnet) in five parts:
- `0787608` deletes `_projectRecord`. Its two tests now drive a real change of
  identity instead of a projection. It also collapses the fourth matcher.
- `c5b03cb` collapses the CultMesh alias checks.
- `4167cc4` adds the fourth matcher's mutation entry.
- `5ba48f0` finishes the Mesh collapse. `CultMeshSnapshotRequestOptions` and
  `CultMeshPeerSnapshotDocumentOptions` become records that carry one
  `Selection`, and their clones collapse to `with`. `CleanSnapshotFilter` and
  the triple rule give way to two owners, `ResolveDefaultSelection` and
  `OverlaySelection`. `CultMeshHotBodySubscription` carries one `Selection`.
  The four-tier read fallback becomes one read anchored on the record key,
  with the two tiers that could return the wrong key's record deleted.
- `4c33a62` adds the Mesh entries: three reverts killed and two loosenings
  **survived**. The survivals are fixture gaps, since no test passes an empty
  non-null `recordKeys` or a record key with different casing. They go to the
  fix batch.

Mesh is net **−83** against §11's estimate of about −326. The shortfall has
four parts:
- about −41 is the payload-sniff fallback, which was kept;
- `IsSameCultDocumentSchema` stays, because it already delegates entirely and
  inlining it would duplicate the resolution at four sites;
- `CultMeshClient`'s three subscription sites still call the v0
  `SubscribeAsync`, which this cut does not retype. They move with Cut 2,
  the watch cut;
- the read keeps two tiers, because a test enforces that.

This is an honest miss.

**Two corrections to Hands' report.** First, the payload-sniff fallback was
**not "ruled kept by the operator"**. Hands kept it in `c5b03cb` as a
discrepancy, and Soul is judging whether it is a second selection decision or
a decode capability. Second, Hands reports that one Networking test crashes the
test host on a disposed-socket race in `CultNetRudpSchemaServer`/`CultNetTransport`
and calls it pre-existing. Networking was 162/162 at `17d10e0`, so that claim
must be checked against `17d10e0` and `main` before anyone believes it. It goes
to Soul by name.

**Q-J fix landed** at `e5348d3` (commit 2f, C#). The Rust runtime is next.

**Soul on commit 2, 2026-09-22** (Opus, pinned at `4c33a62`). **Commit 2 does
not pass.**

- **S2-1, medium, predates the range.** The crash is real, and Hands was right
  that it is older than this commit. It is a disposed-socket race: the ack
  `SendTo` at `CultNetTransport.cs:2549/2593` runs on a test's background poll
  thread. About 18 tests start `IsBackground` poll threads, and only one of
  them (`:1885`) joins its thread before the server disposes. Crash rates:

  | Checkout | Suite | Crashes |
  |---|---|---|
  | `17d10e0` | full | 1 of 6 |
  | `17d10e0` | subscription subset | 5 of 12 |
  | `main` | subscription subset | 1 of 12 |

  "162/162" was luck. A second defect sits in the harness:
  `mutate-dotnet.ps1:43/61`, under `$ErrorActionPreference='Stop'` on
  PowerShell 5.1, throws the whole run away when a host crash writes to
  stderr, where it should record the mutant as NO VERDICT.
- **S2-2, high: the rewritten reconcile tests lost what the old ones proved.**
  With `_projectRecord` gone, a source key always equals its record key, and
  the delete-then-put now arrives through the live `Watch` path, so
  `Reconcile()` has nothing left to do.
  - Two mutants that `17d10e0` killed now survive: dropping Reconcile's
    identity branch (`:261`), and returning early from Reconcile.
  - Dropping the add loop (`:274`) and dropping the live identity branch
    (`:340`) survive both before and after the rewrite.
  - The `RecordKey` comparisons at `:261` and `:340`, and the throw at
    `:311`, are now **dead code**.
- **S2-3, high: the fourth matcher was collapsed into the alias matcher, not
  into the evaluator.** `CreateShardSnapshotResponse`
  (`CultNetDatabase.cs:633-670`) still loops on its own.
  - A probe sent `{schemas:[], keys:[]}` to both paths: the shard path
    returned 0 rows, `CreateRawSnapshotResponse` returned 2.
  - The binding-alias expansion does not run on the shard path either. That
    part is plausible, not probed.
  - §2 says an empty list is **refused at the door**. The evaluator reads `[]`
    as "no filter" (`CultNetSelectionEvaluator.cs:84`) and the shard path
    reads it as "nothing". Neither follows the map.
- **S2-4, medium-high: the Mesh one-pass read returns the wrong schema's
  record.** At `CultMesh.cs:2730-2750`, the first candidate at the key that
  passes the exact match, the alias match or a trial decode wins. With
  `[MeshOther, MeshNote]` at one key, a MeshNote read returned MeshOther.
  Before `5ba48f0`, the exact schema was searched first.
- **S2-5, medium: Mesh has two decode rules that disagree.** The read accepts
  a record by trial decode. `DecodeSnapshotDocuments` rejects the same record
  by payload sniff. `RawSnapshotPayloadMatchesSchema` is a real decode
  capability, since both of its tests are keys-only. **It is not a second
  selection decision.** It is, though, a second copy of the registry's
  resolver (`CultNetDocumentRegistry.TryResolveDescriptorByPayloadSchema` /
  `TryReadSchemaVersion` / `InferSchemaName`, `:596-670`).
- **S2-6, high: the public `CultMeshSnapshotRequestOptions.Selection` honours
  only `Schemas` and `Keys`.** Mesh sends v0 only. A request with a limit, a
  field predicate and a header projection went out as `schemas=null
  keys=null`, and two full rows came back. A caller's `Selection` also skips
  the old cleaning, so `Keys=[""]` now returns nothing where it used to return
  everything.
- **S2-7, low, predates the range: selection meaning is decided in two more
  places.** `ResolveDefaultSelection` (`:835`) checks the raw `Count` before
  cleaning, so `[" "]` selects everything. `ResolveDefaultSelection` reads
  null as "no filter", and `OverlaySelection` (`:840`) reads it as "inherit".
- **S2-8, low.** Both options types changed from `class` to `record`, which
  gives them value equality and is acceptable.
  `CultMeshHotBodySubscription.Selection` exposes mutable arrays on a contract
  that was immutable. The prebuilt Unity `GameCult.Mesh.dll` carries the old
  API: a release-time follow-up, rebuilt at the next release cut.
- **S2-9 and S2-10:** a miscount in the entries header. For the two Mesh
  survivors, Soul's fixtures kill both.
- **Held:** Mesh 3 killed and 2 survived, as reported. Networking 10 of 10
  killed. Every restore verified by hash. The alias-matcher routing is right.
  `IsSameCultDocumentSchema` delegates.

**Self's rulings, 2026-09-22, for the commit 2 fix batch:**

- **Empty lists and blank keys are refused at the one door, as §2 already
  says.** `CultNetSelection.Validate` refuses `keys: []`, `schemas: []`, and
  any key or schema that is empty or whitespace, with `selection_invalid`.
  `null` means no filter, everywhere. The evaluator never sees `[]`. v0
  lowering carries over v0's old cleaning: an empty v0 list, or one made only
  of blanks, lowers to `null`. That is a v0 compatibility rule the lowering
  owns, not a meaning the evaluator carries. `ResolveDefaultSelection` and
  `OverlaySelection` stop deciding what null means. They build a
  `CultNetSelection` and let the door decide. Overlay means "the caller's
  selection replaces the default wholesale".
- **The shard snapshot answers through the evaluator**, binding aliases
  included, with no loop of its own.
- **Mesh keeps sending v0 in this cut.** §9 says v0-only peers refuse v1, and
  FU-v0 retires v0 later. So `CultMeshSnapshotRequestOptions` refuses, loudly
  and typed at construction or send, any `Selection` term v0 cannot carry:
  `fields`, `cites`, `cited`, `limit`, `cursor`, `descending`, `projection`
  other than the default. **Refused, never silently dropped.** Mesh sending v1
  is a follow-up, **FU-Mesh-v1**, triggered by the first Mesh caller that
  needs a term beyond schemas and keys, most likely the Huginn consumer or
  Cut 2.
- **The Mesh read searches the exact schema first,** then the alias match,
  then a foreign id decoded by payload. It uses **one** decode rule shared
  with `DecodeSnapshotDocuments`, and that rule is the registry's resolver.
  The Mesh copy in `CultMeshSnapshots.cs` is deleted in favour of
  `CultNetDocumentRegistry`'s.
- **The dead identity code is deleted, not tested around.** `:261`, `:340`
  and `:311` can no longer fire. Reconcile gets a test for what it still owns
  after the deletion: flipping `authorizeRecord` per key while the request
  stays authorized, and the add loop. Both surviving mutants must die.
- **Test hygiene belongs to this batch, because a flaky M0 is not a
  control.** Every test that starts a background poll thread joins it before
  its server is disposed. `mutate-dotnet.ps1` records a host crash as NO
  VERDICT and carries on.
- **Also owed by Cut 1 and not yet done: R-2.** Python replies
  `unsupported_schema_version` to v1 (§9, `interop_peer.py:360-387`,
  `cultmesh_py/server.py:341`), with one test per site.

**Rust commit 3 landed** at `b090ca9`. Its parity vectors wait on the fix
batch below.

**The commit 2 fix batch landed** (Sonnet), `568e8e3` and `021e3f1`:

- **Door and lowering.** The door refuses blank and whitespace entries, and
  `CultNetV0SelectionLowering` owns the v0 cleaning.
- **Shard snapshot.** It answers through the evaluator
  (`NET-D4-ShardMembership-Revert`), with a fixture for exclusion outside the
  shard's prefix.
- **Mesh v0 terms.** `CultMeshSnapshotRequestOptions` refuses any term v0 cannot
  carry, and `HotBodySubscription.Selection` returns a copy.
- **Mesh read order.** The read goes exact, then alias, then payload, under one
  decode rule, and the Mesh copy of the resolver is deleted. The shared rule
  is `CultNetDocumentRegistry.TryReadSchemaVersion`, now public, matched against
  the caller's own descriptor. `TryResolveDescriptorByPayloadSchema` is
  ambiguous when two CLR types alias one schema id, which the foreign-id
  fixture does on purpose.
- **Dead code and test hygiene.** The dead identity code in Reconcile is
  deleted, and the poll threads are joined.
- **Harness.** `mutate-dotnet.ps1` records a host crash as NO VERDICT.
- **R-2.** The Python v0 peer refuses v1.

Tests: Networking 195/195 over ten consecutive runs, with no crash. Mesh
253/254 with one skip that was already there. Caching 192/192. cultnet-py
54/54. Mutation entries 22/22 killed across the fix-batch, Mesh and Networking
files, and both Mesh survivors now die. Delta: Mesh −3, Networking +62.

**Open for Soul:**

- It is unclear whether the door now refuses `[]`, as ruled, or only blank
  entries. The report names blank entries only.
- Seven `cultmesh-py` daemon tests fail on `ModuleNotFoundError: cultcache_py`
  in a spawned child process. Hands calls this pre-existing; `main` must
  confirm it.
- The anchor-matching fragility of `mutate-dotnet.ps1` (separator spelling) is
  recorded, not fixed.

**Commit 4 landed** (Sonnet) at `00f4c02`, completing Cut 1's execution.
- **Parity vectors (S12):** `contracts/cultnet/interop/selection-vectors.{cs,rs}-written.json`,
  8 vectors each, read in both directions. Six are evaluated selections. Two
  are door refusals: `schemas: []`, and a blank key.
- **Door:** `keys: []` and `schemas: []` **are** refused (`CultNetSelection.cs:326/328`),
  as ruled.
- **Tests:** cultnet-rs 111 → 174, cultcache-rs 64 (unchanged), Networking
  196 + 1 skip, Caching 192, Mesh 252/253.
- **Mutation entries**, all killed:
  - Rust 23/23;
  - Caching 10/10, including 4 Q-J;
  - Networking 15/15, including 5 Q-J.

  S6 first survived. Its fixture now carries two references to the same
  target under different roles. S9–S15 and S17 do not apply to Rust
  (Rust has no subscription client).
- **Open for Soul, highest first:**
  - **No `cites` vectors cross the runtimes.** Hands says C#'s
    `MatchesCitation` compares `Citation.Target.SchemaId` against the real
    SHA-256 schema id with no alias fallback, so "this fixture pair cannot
    agree". That is either a fixture limit or **a wire-parity gap in the
    hop**, and parity is this cut's invariant. Soul settles which.
  - **Size:** `selection.rs` is 1,407 lines and `contracts.rs` +595, about
    2,000 in all against §14's roughly 430.
  - Hands calls S20 "structurally vacuous" and S23 without a surface in Rust.
    Both are claims, not yet reached, and need checking.
- **A flaky test, recorded as a follow-up.**
  `reactive_document_coalesces_direct_same_schema_alias_member_writes`
  (`packages/cultnet-rs/tests/cultnet.rs`) fails under the harness's
  invocation of the full binary and passes in isolation. The v0-lowering entry's
  command was narrowed around it. It is not this cut's code. Its owner is the
  reactive-document path.

**Two pre-merge additions from the Huginn read-side map, 2026-09-22.** They
join the next selection fix batch, whatever the final Soul pass adds.

- **P-1:** Rust's `Evaluation` gains `matched`. Rust cannot fill the page's
  `matched` today.
- **P-2:** derive `Eq` on the selection wire types, because Huginn's request
  derives it.

**Load-bearing for Huginn:** `Cursor::parse` is public and exposes `as_of`.
The organ answers a cursor's page at the cursor's own `asOf`. If a finding
makes `parse` private, expose `cursor_as_of` instead.

**Soul's whole-cut pass, 2026-09-22** (Opus, at `00f4c02`). **Cut 1 does not
merge.** The notes are in the session scratchpad at `soul-ss4-notes.md`.

- **High, confirmed:**
  - **The C# reference never answers v1.** No v1 listener is registered
    (`CultNetDatabaseServer.cs:49-55`, `Server.cs:562-571`).
    `CreateSelectionResponse` and `LastWriteSequence` have no callers. §7's
    server handling, D6's subscription v1 and S9 were never built. Hands
    reported none of it missing.
  - **`cited` returns the wrong edges in both runtimes.** Edges are filtered by
    whether the *citer* is on the page, but under `cited` the page rows are the
    *cited* rows. `exists:true` gives no edges. `exists:false` gives edges into
    rows that are not on the page. The committed vector pins the wrong answer.
  - **The C# door accepts `"5\n"`.** .NET's `$` matches before a final
    newline. Rust and the schema refuse it.
- **Parity broken on caller inputs:**
  - **Float rendering at ties.** 394 `f32` and 48 `f64` values out of 200k
    render differently, because .NET rounds half to even and Rust does not.
    A shortest form is a stand-in for the value in any case: an `f32` of 3e20
    is stored as 300000006012263202816, bits `0x61821ab1`. (Corrected
    2026-09-22. Soul's report and this map first gave 300000002010536247296.
    The Rust Hands and an independent Python `Decimal` check both give
    ...6012263202816.)
  - **`schemas`.** C# matches through the alias matcher and Rust matches exact
    ids. `["leaf_a"]` gives 8 rows against 0.
  - **Tiebreak.** C# compares UTF-16 code units and Rust compares UTF-8
    bytes, so astral keys sort in opposite orders.
  - **`cited` edge order.** It comes from a Rust `HashMap` and is
    nondeterministic.
  - **`any_of` on a numeric alias.** C# goes through a culture-dependent
    `ToString()`.
- **The door is advisory.** Both `select` implementations are public and never
  validate. Rust's `matches` only `debug_assert`s the hop, so a release build
  ignores `cites` and `cited`. Mesh `EnsureV0Compatible` lets `Keys=[]` and
  `[""]` through, and v0 lowering turns them into "every key".
- **Not what the map specifies:**
  - `Matched` counts the page, which breaks C5.
  - v0 and shard snapshots re-evaluate the whole cache on every page, which is
    O(N²/200), and cannot detect a write between pages.
  - Cursor digests collide: `["a|b"]` digests the same as `["a","b"]`.
  - S20 has no C# test. Two S20 mutants survive 196/196. Rust `select`
    ignores `projection`, so the rule has no owner there. Nothing tests
    `SelectPage` or `CreateSelectionResponse`.
- **The Networking host still crashes**, 2 in 11 runs. The flaky `HasConsumers`
  assertion (`NetworkingTests.cs:1720`) predates this cut. Joins sit outside
  `finally` in about 18 tests, so a failed assertion becomes a host crash. The
  "10 runs, no crash" claim did not reproduce.
- **Low:**
  - The JSON schema disagrees with the runtimes on `limit` bounds and on
    `minItems`.
  - Python R-2 refuses only v1, so v2 and later are still dropped.
  - `TryReadSchemaVersion` is a public sniff heuristic, and two Mesh sites
    repeat the same line around it.
  - Rust v0 dedups before `reject_duplicates`, so that function is dead.
  - `mutate-dotnet.ps1` throws on `\` against `/` in paths.
- **Deletions Soul named:**
  - `contracts.rs:1206-1541`, about 335 lines of hand codecs that the serde
    derives already round-trip, verified both ways;
  - `expand_scientific_notation`, about 46 lines, dead: 400k renders produced
    no exponent;
  - `reject_duplicates`;
  - `RawDocumentHeader::from_document`, which has no callers;
  - C#'s `WithBindingSchemaAlias`/`ExpandSchemaBindingAliases` duplicate,
    about 60 lines, and a shared lower-and-page loop, about 20 lines.
- **Settled:** `cites` is a fixture limit, not a parity gap. With real SHA-256
  ids, 40 selections agree byte for byte. The seven `cultmesh-py` failures
  are environmental: with `PYTHONPATH` set, 78/78 pass. The flaky Rust test
  waits 30×5 ms for a flusher, which is too short under contention, and it is
  not this cut's.
- **Held:**
  - Every mutation suite killed everything.
  - Door refusals are byte-identical apart from `"5\n"`.
  - The Q-J comparator agrees with Python `Decimal`.
  - No float sits on either comparison path.
  - Negation, limit clamping and v0 dedup are at parity.
  - Mesh reads in the order exact, then alias, then payload.
  - Mesh is actually 253/254.

**Self's rulings for the Cut 1 fix batch, 2026-09-22:**

- **R-A. Serving v1 is part of this cut.** Both C# servers register v1
  listeners. The snapshot answers through `CreateSelectionResponse` in
  last-write order, and the subscription server answers v1 as D6 and §7 say,
  with S9 tested. A v1 request that is not served is a defect, not a
  follow-up.
- **R-B. Hop edges follow the hop's direction.** Under `cites`, the edges are
  the ones *from* page rows. Under `cited`, they are the ones *into* page
  rows. Every edge touches a page row. The order is deterministic: the
  page-row order, then `(from, role, to)` in code-point order. Regenerate the
  vectors.
- **R-C. Every string comparison in the vocabulary uses Unicode code-point
  order.** In Rust that is UTF-8 byte order. C# compares by code point, not
  by UTF-16 unit. This covers the tiebreak, edge order and anything else
  ordered.
- **R-D. A floating-point member renders as its exact decimal expansion,
  in canonical form, and not as a shortest form.** Every finite `f32` and
  `f64` is an exact dyadic rational and has a finite decimal expansion. That
  expansion is the stored value, it is the same in both runtimes by
  construction, and it matches the reason for Q-J. Implement it from
  mantissa and exponent, with `BigInteger` in C# and a small bignum in Rust.
  Add no new dependency unless the expansion cannot be written in about 60
  lines. `decimal` and the integers are unchanged. **Flagged for the
  operator's review:** this is Self's reading of Q-J, and it changes what
  `ge "3e20"` means for an `f32` of 3e20. **Accepted by the operator,
  2026-09-22** ("I accept your recommendations").
- **R-E. There is one schema-identity rule in both runtimes.** Rust gets the
  alias matcher (the owner is `cultnet-rs`, ported from
  `CultNetSchemaAliasMatching`). `schemas`, `cites.target.schemaId` and the
  binding id emitted on records all go through it. A `cites` target that
  matches no schema is refused at the door, never answered with an empty page.
  `ToRawRecord` and `ToEdge` emit the same id.
- **R-F. The door is inside `select`.** `select` validates first, in both
  runtimes, and returns the typed refusal. A public entry point that skips
  validation does not exist. Rust's hop handling is not a `debug_assert`.
  Mesh's `EnsureV0Compatible` runs the door first.
- **R-G. `matched` is the total number of matches, not the page count** (C5).
  Rust's `Evaluation` carries it (P-1). A single evaluation answers v0 and
  shard paging, taken as one snapshot.
- **R-H. Cursor digests use length-prefixed encoding** for every string and
  list, in both runtimes.
- **R-I. The projection has an owner in both runtimes.** `cultnet-rs` gets
  `select_page`, which produces `SelectionPage` under `projection`, with
  `matched` and edges. C# `SelectPage` and `CreateSelectionResponse` get
  tests, and so does S20. Page bytes are compared across the runtimes in the
  parity vectors.
- **R-J. `any_of` on a numeric alias** compares the canonical rendering.
- **R-K. Test hygiene is finished, not claimed.** Every poll-thread join
  moves into `finally`, and the `HasConsumers` race gets a deterministic
  wait. The fix is proven by 20 consecutive full runs.
- **R-L. The contract follows the runtimes.** The schema gains
  `minItems: 1` on `schemas` and `keys`, and `limit` is documented as
  clamped. Python refuses every `cultnet.*.vN` with N > 0.
  `TryReadSchemaVersion` goes back to internal, behind a registry method that
  takes the descriptor. `mutate-dotnet.ps1` normalises path separators.
- **R-M. Soul's named deletions all land.** P-2 (`Eq`) lands too.

**The C# half of the fix batch landed** (Sonnet), `7903853`..`3d32c67`.

What changed:
- **R-A:** v1 is served by both servers, and D6 reconcile routing is in place.
- **R-C:** `CultNetCodePointComparer` gives code-point order.
- **R-D:** floats render as their exact decimal expansion, computed with
  `BigInteger`.
- **R-E:** cites go through the alias matcher, and the wire id is one
  `WireSchemaId`.
- **R-F:** the door is unconditional inside `Select`.
- **R-G:** `Matched` is the total, computed once through `EvaluateAll` and
  `SelectAll`.
- **R-H:** the cursor digest is length-prefixed.
- **R-J:** numeric `any_of` compares the canonical form.
- **R-M:** `WithBindingSchemaAlias` and the duplicated paging loop are deleted.
- **R-I:** tests kill both S20 mutants. One real bug was fixed in the
  process: `ToEdge` serialised a plain-typed payload as a document.
- **R-K:** every join is in `finally`. `HasConsumers` now waits for the
  condition.
- **R-L:** the schema carries `minItems`. Python refuses every vN>0 (only
  one site exists). The harness normalises path separators.
- **Vectors:** `cs-written.json` is regenerated and gains the float-tie,
  cites-by-alias, astral-order and projection cases.

Tests: Networking 201/202, where the one failure is the stale Rust vector
until the Rust half lands. Caching 192. Mesh 253/254. Python 54, 78 and 34. In
20 full runs there was no crash, but one run had an unexplained failure.

**Not done, sent to a second Hands:** mutation entries for the fix-batch
rules (there are only regression tests), S9, and a diagnosis of the 1-in-20
failure over 30 runs.

**The Rust half of the fix batch landed** (Sonnet), `8b114ed`..`a088435`:
- 36/36 entries killed.
- Three real bugs were found while it ran, among them a v0 fast path that
  had inherited the door's "empty matches all".
- The `contracts.rs` hand codecs are gone (−362/+35).
- The Rust Hands corrected `f32` 3e20 to `300000006012263202816`.

**Shared parity fixture** (Sonnet), `3317ddd` and `defb9a1`. Both runtimes now
build their vectors from
`contracts/cultnet/interop/selection-vectors.fixture.json`, which uses real
SHA-256 ids. A test bug is fixed: both readers had compared the page size with
`matched`. Of 15 vectors in each direction, the following agree byte for byte:
exact ids, float tie, astral order, header and document projection,
`cites`/`cited`, and the refusals. **Two real parity defects:**
- **Two incompatible alias rules.** C#'s `CultNetSchemaAliasMatching.Matches`
  (`CultNetDatabase.cs:74-87`) strips `.vN` and compares against the
  descriptor's `SchemaName`. Rust's `schema_alias::matches`
  (`selection.rs:452-459`) strips `.vN` and compares against the *hash*. No
  alias resolves in both.
- **C# does not refuse an unresolved `cites.target`** (`CultNetSelection.cs:361-391`),
  which R-E requires. Rust does refuse it.

**Self's ruling, 2026-09-22: the C# reference's alias rule is the rule.**
CultLib keeps wire parity against the C# reference. An alias is a schema
*name* with a `.vN` suffix, resolved against the descriptor's name. A
hash-shaped alias is not a wire form. Rust ports the name rule, which means
its `RowSet` must expose each schema's name, and it deletes the hash-stripping
matcher. C# refuses an unresolved `cites.target` at the door. Each vector that
fails today becomes a vector both runtimes must pass.

**The Rust alias port landed** (Sonnet). Rust's `schema_alias::matches` is
now `(candidate, schema_id, schema_name)`, and it matches the C# reference row
for row: exact id, bare name, `.vN` on the name, case-sensitive, the last
`.v` marker wins, and `.v` with no digits is taken literally. The C# table was
produced by running the reference source. The hash-stripping overload is
deleted. `SchemaVersion` and `CompatibleSchemaIds` are not ported, because no
Rust call site needs them; that is a stated reduction. Rust entries are 40/40
killed. The whole suite had never run under `-Repo` before, and its commands
now carry `--manifest-path`.

| Reader | Vectors from | Pass |
|---|---|---|
| Rust | `cs-written` | 15/15 |
| C# | `rs-written` | 14/16 |

The two C# failures both come from the pending C# door refusal of an
unmatched `cites.target`, which sits with the matrix Hands.

**The early Soul pass (commits 0–1, pinned at `17d10e0`) reported late, on
2026-09-22.** It was interrupted three times by API errors. Much of what it
found is already covered by R-A to R-M. These findings are **new, and still
open against the current branch:**

- **F2, high: typed refusals never reach the wire.** `CultNetErrorMessage`
  (`CultNetSchemaMessages.cs:~498-512`) has no `code` and no `details`. Nothing
  maps the selection or cursor exceptions to an error. This means
  `selection_invalid`, `cursor_stale`, `cursor_invalid` and
  `reference_outside_target` exist only in-process.
- **F6, high: the cursor can be forged.** The digest is an unkeyed SHA-256
  that anyone can recompute, so any caller can mint a cursor. A record key
  containing the U+241F delimiter makes a server's own cursor fail to parse.
- **F7, high: authorization sees two different ids.** On the snapshot path,
  the authorizer receives the binding's wire id. On the live path it receives
  the descriptor id (`CultNetDatabaseSubscriptionServer.cs:~309` against
  `:~451`). A row delivered by the snapshot can therefore be refused on its
  first live change, or leak through a denylist. No test pins the live id.
- **F8, medium: `LastWriteSequence` is not the order a snapshot reflects.**
  - Replica apply paths (`CultNetDatabase.cs:~1416`, `:~1459`) never set it.
  - Nothing rebuilds it after a restart.
  - Sequences are per shard, but `asOf` is a single `ulong`.
- **F11, medium: v0 lowering changed what v0 answers.** A snapshot with
  `recordKeys: []` used to answer empty, and now answers every row. v0 rows
  now come back in schema-and-key order instead of the requested key order.
- **F12, medium: the committed schemas do not describe the bytes C# writes.**
  - Headers `$ref` a record schema that requires `payload`.
  - MessagePack-CSharp writes both `headers` and `documents`, one of them
    nil, so every C# page fails the `oneOf`.
  - `fieldPredicate` does not forbid `values` and `number` together.
- **F14, low, now reachable because v1 is served: field predicates on a
  Removed change throw.** `TryGetIndexValue(null)`, at
  `CultNetDatabaseServer.cs:~375`.
- **F15, low: `cites` skips the out-of-target check** for edges that do not
  point at the requested key. `EnsureWithinDeclaredTarget` also returns early
  when `TargetType` is null.
- **Surviving mutants:**
  - S1-Loose (reachable becomes all descriptors);
  - S2-AllOf;
  - S17-FirstLeaf;
  - Door-NoValidate, since fixed by R-F;
  - S5-DigestNoFields;
  - Auth-LiveSchemaId, which is F7;
  - QL-OutOfTargetCites, which is F15.

  S4 has no entry. A clone at `C:\ss1` was left behind: the Soul pass could
  not remove it.

**Self's rulings for fix batch 3, 2026-09-22.** Batch 3 goes to Hands once the
C# matrix Hands and the Rust alias port have landed, so that no two C# Hands
edit `src/` at the same time.

- **R-N, F2: refusals are wire messages.** `cultnet.error.v0` gains `code` and
  `details`. That is an additive field, so the version is unchanged, and v0
  peers ignore it. Every selection and cursor refusal, and
  `reference_outside_target`, maps to its code in both servers and in Rust.
  Tests decode the bytes a peer actually receives.
- **R-O, F6: cursors are keyed.** The digest becomes an HMAC under a key the
  answering server holds. The key is random per process, so a cursor does not
  survive a restart and answers `cursor_invalid`. That is acceptable, because
  cursors are short-lived and §2 already calls them "minted by the answering
  server". The cursor body uses length-prefixed fields, not a delimiter. Rust
  does the same, since Huginn mints cursors.
- **R-P, F7: one id reaches the authorizer, and it is the wire id**, on the
  snapshot, live and reconcile paths alike. A test pins the id each path
  passes.
- **R-Q, F8:** every path that commits a row, whether local or a replica
  apply, sets the last-write sequence. It is rebuilt from the mutation log at
  startup. A multi-shard selection's `asOf` is defined, or refused: a
  selection that spans shards is answered only when every row comes from one
  shard's log. Otherwise it is refused with `cursor_stale`/`selection_invalid`
  as appropriate, and the choice is documented in §2. A single shard is the
  case today.
- **R-R, F11: v0 lowering preserves v0's pre-cut answers exactly.** v0 is a
  frozen contract for the TS, Python and Kotlin peers. **This corrects the
  earlier ruling in the commit 2 fix batch**, which lowered an empty v0 list
  to "no filter". Where the pre-cut v0 server answered empty for
  `recordKeys: []`, it still answers empty. Where pre-cut v0 kept the
  requested key order, it still does. The pre-cut behaviour comes from the
  code at `b3d9cf7`, and tests prove it by running the same requests against
  both.
- **R-S, F12: the schemas describe the bytes.** Add a header schema, make the
  page shape match what MessagePack-CSharp writes (nil keys included), and
  make `fieldPredicate` exclusive. A test decodes real C# bytes against the
  schemas.
- **R-T, F14 and F15:** removed changes are evaluated without reading a
  document. A removal matches through the selection's keys and schemas
  alone, and field predicates count as unknown. `cites` checks every edge
  against its declared target.
- **R-U: every surviving mutant from the early pass becomes an entry, and it
  must die.** That covers S1-Loose, S2-AllOf, S17-FirstLeaf,
  S5-DigestNoFields, Auth-LiveSchemaId, QL-OutOfTargetCites and an S4 entry.

**Fix batch 3 is landing in pieces, 2026-09-22.**

- **The harness is retired** (operator ruling). The branch's `mutate-dotnet.ps1`,
  the four `*.entries.ps1` and the Rust entries file are deleted. Tests pin
  behaviour, and Soul runs Stryker.NET and cargo-mutants on the diff.
- **C#, first pass** (`8d7bee9..f366791`): R-R (v0 lowering answers exactly
  what pre-cut v0 answered, key order included) and the cites-refusal vectors.
  C# now reads 16 of 16 Rust vectors. It stopped there, so R-N, R-O, R-P, R-Q,
  R-S, R-T and R-U were left.
  - **R-R meets R-F:** the raw v0 wire answers an explicit empty list with an
    empty page, while Mesh's `EnsureV0Compatible` refuses that list before
    sending. Both layers keep their own ruling, which is right.
- **Rust** (`5cf31d1..c606eb3`): R-O (keyed cursor), R-T, R-R, and the entries
  file deleted.
  - **The cursor API for Huginn:** `Cursor::parse` stays public and key-free
    and still exposes `as_of`. Only digest verification needs the process key,
    so no second helper was needed.
  - Rust reads 17 of 17 C# vectors.
  - **R-N is pending in Rust by design.** An earlier guess at the wire shape
    was backed out; Rust matches C#'s field names byte for byte once they land.
- **C# pass A is in Hands:** R-N, R-P, R-T. **Pass B follows:** R-O, R-Q, R-S,
  R-U.
- **C# pass A landed** (`66b5e60`, `e8a3dec`): R-N, R-P and R-T.
  - **R-N's wire shape:** `CultNetErrorMessage` gains `code` and `details`.
    `CultNetErrorDetails` carries `field`, `value`, `asOf` and `current`. The
    codes are `selection_invalid`, `cursor_stale`, `cursor_invalid` and
    `reference_outside_target`. The schema gains both fields additively.
  - Two catch sites that previously let the exception escape now answer the
    peer.
  - **R-P:** the live fast path authorized on the descriptor id and now uses
    the wire id, so all three paths agree.
  - **R-T:** a removal skips field predicates instead of dereferencing a null
    document. `cites` no longer early-returns on a null target, and **the root
    cause was in CultCache**: a many-reference declared without an explicit
    attribute left its target type null, even though the element type was
    statically known, so the out-of-target check silently skipped.
  - Networking 223 pass and 2 skip; Mesh 255.
  - **The Caching claim was wrong.** There is one failure, not three, and it
    is identical at the branch head, at `b3d9cf7` and on `main`. It expects a
    write to a read-only directory to fail, which cannot happen in a container
    running as root. **Environmental.**
- **Rust R-N landed** (`eff62e5`): 213 tests pass. Parity is pinned by
  **captured C# bytes** for all four codes, taken from a probe run on the
  dotnet image.
  - **An encoding trap the vectors would not have caught:** MessagePack C#
    always writes all five declared keys in declaration order, nil included,
    while Rust's generic path sorts keys alphabetically. Rust now builds that
    map by hand. **A key added or reordered in either error type must be
    mirrored there.**
  - **A defect in C#, sent to pass B:** `ForReferenceOutsideTarget` prefixes a
    message that already carries its own prefix, so a peer reads
    `reference_outside_target: reference_outside_target: …`.
  - The parity-vector machinery carries no concept of an error message, so
    R-N's parity lives in its own byte-level test. That is the right home.
- **C# pass B landed** (`a4cf54f`..`52082ec`): R-O, R-Q, R-S and R-U, plus the
  doubled `reference_outside_target` prefix.
  - Networking 238 pass and 2 skip; Mesh 255.
  - **Hand probes: six of seven killed.** S1-Loose, S2-AllOf, S17, S5-DigestNoFields,
    Auth-LiveSchemaId and S4 all die.
  - **QL-OutOfTargetCites is a genuine gap.** Since pass A fixed
    `CultCache.ResolveReferenceTarget`, the fixture's many-reference gets an
    inferred target type, so the guard never fires for it. `TargetType` is now
    null only for a scalar reference typed as the bare `ICultRecordRef`, and
    no fixture reaches that shape. **Open for Soul:** is that shape
    registerable at all? If it is, pin it. If it is not, the null branch is
    dead and goes.
  - **Two fixes found while writing the tests:**
    - the cursor's length prefix counted UTF-16 chars, so an astral key did
      not round-trip;
    - `MiniJsonSchemaValidator` had no `not`, so the exclusivity test found
      nothing.
  - **`cultnet.raw-document-record.schema.json` was missing `schemaName`,
    `schemaVersion` and `schemaContentHash` entirely.** Any real C# page would
    have failed it on `additionalProperties: false` before R-S's own bugs even
    mattered.
  - **Self's correction: cursors need no cross-runtime byte parity.** The
    brief for pass B said Rust and C# must encode a cursor identically, on the
    premise that one runtime parses another's cursor. **That premise is
    wrong.** A cursor is opaque, minted by the answering server and echoed
    back by the caller, and the digests cannot verify across processes because
    each server holds its own key. Both encodings are length-prefixed
    five-field bodies with an HMAC-SHA256 digest, and they differ in base64
    flavour and separators. That is correct and stays. Rust's own doc said so
    first.
  - **Settled by Soul:** three Caching failures, identical at `b3d9cf7` and
    `52082ec`. All three expect a write to a read-only directory to fail, and
    the container is root. **Environmental.** Pass B's count was right.

**Soul's whole-cut pass, 2026-09-22** (Opus, at `52082ec`). **Cut 1 may not
merge.**

- **Held:** Networking 238 and 2 skips, Mesh 255, cultnet-rs 213. R-Q holds
  (both replica applies, the startup rebuild, and a shard-spanning refusal).
  R-O holds against a stranger and across a restart, and the astral key
  round-trips. R-H's digest covers every selection term. R-R holds and is
  honest. R-A holds. R-F's door is inside `EvaluateAll`, which every path
  reaches. The canonical-number door is anchored `\A..\z`.
- **S-1, high: the hop indexes rows by record key alone.** `byKey`
  (`CultNetSelectionEvaluator.cs:176-178`) and Rust's `HashMap<&str, &R>`
  (`selection.rs:1457`) both keep the last row at a key. CultCache keys are
  unique **per schema**, and the Mesh tests already put two schemas at one
  key. With rows in one order the selection refuses valid data as
  `reference_outside_target`; in the other order it succeeds. Under `cited`
  the index is a set of bare keys, so a row of one schema counts as cited
  because something cites another schema at the same key.
- **S-2, high: R-T's `cites` half did not land in C#.** C# filters edges by
  the citation's key (`:355`) **before** `EnsureWithinDeclaredTarget` (`:359`);
  Rust checks first. A many-reference declared to one leaf but holding an edge
  into another is refused by Rust and accepted by C#.
- **S-3, high: a selection with both hops loses its `cites` edges in Rust.**
  `selection.rs:1541` computes one direction for every edge; C# anchors each
  edge by its own hop. No vector covers both hops.
- **S-4, high: the error message fails its own schema.** Nothing decodes real
  `CultNetErrorMessage` bytes against `cultnet.error.schema.json`.
  `routingHint` is undeclared under `additionalProperties: false`, and nil
  `details.asOf`, `details.current`, `code` and `details` all fail their
  declared types. The header schema shows the nullable pattern was understood
  and simply not applied here.
- **S-5, medium: Stryker leaves 85 survivors** over the cut's two core files:
  396 mutants, 310 killed, 85 survived, **75 with no coverage at all**, score
  66%. The old "every entry killed" was an artifact of a hand-picked list.
- **S-6, medium: the cursor is malleable in its own position.** The digest
  covers the selection but not the body's `asOf`, `ordinal`, `schemaId` or
  `recordKey`. A caller holding one valid cursor can rewrite its position and
  reuse the digest.
- **S-7, medium: R-I's claim is false.** The vectors compare ids, `matched`,
  `hasNext` and edges. They never compare page bytes, `asOf`, `next`, or a
  refusal's code, and `rs-written.json` carries no projection vector at all.
- **S-8, low: the out-of-target null branch is dead.** D11 refuses a bare
  `ICultRecordRef` in both scalar and many form, so every surviving shape has
  a non-null target.
- **S-9, low: `asOf` is the maximum across all shards**, so a single-shard
  page advertises another shard's number, and an unrelated write invalidates
  a live cursor.
- **S-10, low: `MiniJsonSchemaValidator` silently ignores** `minimum`,
  `maximum`, `maxLength`, `maxItems`, `anyOf`, `allOf`, `if/then/else`,
  `uniqueItems` and `format`. The committed schemas use `minimum` 34 times,
  including `limit`'s bound. Its `pattern` also runs through .NET `Regex`,
  whose `$` accepts a trailing newline.
- **S-11, low:** the v1 subscribe refuses a hop before running the door and
  always names field `cites`.
- **S-12, low:** the fixture's prose still calls the alias vectors a
  cross-runtime defect, which the alias port fixed. Rust's port also drops
  `SchemaVersion` and `CompatibleSchemaIds`, so R-E is two rules agreeing on a
  subset.
- **Where the evidence lives.** The pass ran against `52082ec`. Its nine hand
  probes — eight C# and the Rust both-hops probe that proved S-3 on a matched
  pair — are on branch `soul-probe` in the scratch clone `C:\ss5`
  (`e0fd672..107ff5f`), **unpushed**. Nothing reached origin and the pinned
  checkout at `F:\Projects\CultLib` was never touched. Notes are at
  `scratchpad/soul-sel-final-notes.md`. Probes are scratch by doctrine and are
  meant to be thrown away; this pointer exists because S-1 through S-3 were
  proven there and fix batch 4's tests have to reproduce them independently. If
  `C:\ss5` is gone, the probes are gone with it, and that is acceptable — the
  findings are recorded here, which is the durable copy.
- **Soul's own corrections, same pass.** It first reported a wedged
  `cargo mutants` job on Yggdrasil and asked for it to be killed. Checking the
  box rather than its log, the work directory was already gone: what it had
  lost was its detached ssh's output. Self had by then killed the process,
  which was Soul's own run, so nothing else was affected. **The shape is worth
  keeping: a log is not the machine, and Self acted before confirming the
  process was actually stalled.**
- **The Stryker triage is owed, after four attempts, none of them Stryker's
  fault.** The `dotnet/sdk:10.0` image has no `python3`; the JSON reporter threw
  on `CreateDirectory`; two runs with a different output path ran about fifty
  minutes without finishing, against three minutes for the mutation phase that
  did complete. The completed run stands, and the command is in Soul's notes:
  `dotnet-stryker --project GameCult.Networking.csproj --mutate "**/CultNetSelection*.cs" --reporter json --reporter progress --concurrency 4`.
  **Read the HTML report Stryker writes beside the JSON**, rather than parsing
  JSON in the container.
- **The triage landed on the fifth attempt, 2026-09-22, and it sharpens S-5
  rather than softening it.** Most of the 85 are message-string mutations and
  are not worth chasing; roughly a dozen are real, and they sit on the rules
  this cut exists to enforce:
  - `CultNetSelection.cs:439`, `Any()` → `All()` survived. §2's "a comparison
    on an alias any reachable schema declares non-numeric is refused" degrades
    to "refused only if every reachable schema declares it non-numeric". No
    fixture has an alias numeric on one reachable leaf and a string on
    another — the exact case the rule exists for.
  - `CultNetSelection.cs:387`, `||` → `&&` survived. A `cites.target` with a
    schemaId and a blank recordKey, or the reverse, stops being refused.
  - `member.IndexAlias ?? member.MemberName` → `member.MemberName` survived at
    all four sites (`CultNetSelectionEvaluator.cs:352,384,404` and
    `CultNetSelection.cs:445`). §2's role-naming rule for the hop is untested
    everywhere it is implemented.
  - `CultNetSelectionEvaluator.cs:203,204`, `ThenBy` ↔ `ThenByDescending`
    survived on both paths: the `(ordinal, schemaId, recordKey)` tiebreak is
    pinned by nothing.
  - `CultNetSelectionEvaluator.cs:290-297`, five order flips in `EdgesFor`, all
    survived. **R-B's "page-row order, then `(from, role, to)` in code-point
    order" has no test pinning a single component**, though R-B exists because
    that ordering was wrong once already.
  - `CultNetSelectionEvaluator.cs:590-605` has no coverage at all:
    `ComputeDigest`'s `cites` and `cited` branches, plus the number-presence
    flag at `:584-585`. R-H's collision-freedom rests on a reading, not a test.
  - `:673,678` survived: `Cursor.Parse`'s length-prefix bounds checks, on
    attacker-supplied base64. `:307,322` survived: cursor positioning under
    `descending`, and `ComparePosition`'s schema-vs-key tiebreak. `:710,715,729`
    survived: the code-point comparator's surrogate path, despite R-C having a
    vector for it.
  - **Equivalent, not to be chased:** `Selection.cs:103` `<=`, `:118` `>=`,
    `:121` `dot <= 0` are guarded upstream and unreachable for canonical input;
    `:103,107` turn `sign * x` into `sign / x` with `x` in {-1,1}; `:109`
    Max→Min is a no-op because `PadRight` to a shorter length does nothing;
    `:80` is a `RegexOptions` flag; `Evaluator:272` is equivalent because
    `full.Edges` is empty without a hop.
- **Soul withdrew a finding of its own in the same pass.** It had reported that
  C#'s cursor digest does not sort its lists while Rust's does, offered as a
  low-severity intra-runtime difference. `CultNetSelectionEvaluator.cs:637`
  sorts by code point, same as Rust. The remark belongs in no fix list.
- **cargo-mutants cannot run on `cultnet-rs` at all.** `schema_discovery.rs`
  uses `include_str!("../../../contracts/…")`, which escapes the package, so
  the tool's copied tree fails its baseline build. **There is no mutation
  evidence for the Rust half**, and the package's own source shape is why.

**Self's rulings for fix batch 4, 2026-09-22:**

- **R-V (S-1). A row's identity is `(schemaId, recordKey)`, never the key
  alone.** Both runtimes index rows and incoming edges by the pair. `RecordRef`
  already carries both. Tests: two schemas at one key, in both row orders,
  give the same answer; `cited` does not match a row of another schema at the
  same key.
- **R-W (S-2 and S-3). The hop behaves identically in both runtimes.** The
  target check runs **before** any key filtering, and each edge is anchored by
  its own hop, so a selection carrying both `cites` and `cited` returns both
  sets. C# adopts Rust's ordering for the first; Rust adopts C#'s per-edge
  anchoring for the second. Vectors cover both.
- **R-X (S-4 and S-10). A message that fails its own schema is a defect.**
  `cultnet.error.schema.json` declares `routingHint` and every nullable field
  the way the header schema does. A test decodes real bytes for every code.
  `MiniJsonSchemaValidator` **refuses a schema containing a keyword it does
  not implement**, rather than ignoring it, and implements `minimum`,
  `maximum`, `maxLength` and `maxItems`. Its `pattern` anchors so a trailing
  newline cannot pass.
- **R-Y (S-6).** The cursor's HMAC covers the body as well as the selection,
  so the position cannot be rewritten. Test: a rewritten ordinal or key is
  refused.
- **R-Z (S-7).** The vectors compare **page bytes**, plus `asOf`, `next` and a
  refusal's code, and carry a header-projection and a document-projection
  vector on both sides. Where a field is legitimately per-server, such as a
  cursor, the vector states that and compares the rest.
- **R-AA (S-5).** Triage all 85 survivors by name. The 75 uncovered mutants
  mean whole regions have no test; write behavioural tests for them. A
  survivor that is genuinely equivalent gets a one-line reason in the report.
  **The triage half is done** (see the list under S-5, 2026-09-22). What is
  owed is a behavioural test for each named live survivor: the alias
  disagreement across reachable schemas, the half-blank `cites.target`, the
  four index-alias role sites, the row tiebreak, `EdgesFor`'s ordering,
  `ComputeDigest`'s hop branches, `Cursor.Parse`'s bounds, descending cursor
  positioning, and the surrogate comparator. The equivalent mutants are
  recorded and are not to be chased.
- **R-AB. Make `cultnet-rs` self-contained enough to mutate.** Replace the
  escaping `include_str!` with a build script that stages the contracts into
  `OUT_DIR`, or another route that keeps every source path inside the package.
  Prove it by running cargo-mutants on the crate.
- **S-8:** delete the dead branch.
- **S-9:** `asOf` is the watermark of the shards the selection actually
  covers.
- **S-11:** run the door first, and name the field that is actually set.
- **S-12:** correct the fixture's prose, and state Rust's alias reduction
  where R-E is described.

**Fix batch 4, C# half, landed** (Sonnet) on `cultnet/selection-cut1`,
`52082ec..b7d77b7`:

- `e99bf46` — R-V, R-W, S-8, R-Y: row identity is `(schemaId, recordKey)`; the
  cursor's HMAC covers its own body.
- `b3a8f6d` — S-9, S-11: `asOf` is scoped to the shards the selection covers;
  the v1 subscribe door runs before the hop refusal.
- `b7d77b7` — R-X, S-4, S-10: the error schema matches the wire; the validator
  refuses keywords it does not implement.

Counts on Yggdrasil: Networking 247 pass / 2 skip (was 238, so +9 tests), Mesh
255 pass. Caching's three failures are the read-only-directory tests that
cannot fail under the stopgap's root container — unproven, not passing.

**R-V proven in both row orders.** Before the fix, a narrow reference declared
to `SelLeafA` resolving a key shared with a `SelLeafB` row depended on
insertion order: one order refused a valid reference as `reference_outside_target`,
the other silently resolved to the wrong schema's row for `cited` membership.
After it, both orders resolve through the leaf set and `cited` never matches
the other schema's row at that key.

**What R-X's new refusal exposed.** Twelve uses of unimplemented keywords
across three committed schemas, none reached by any existing test:
`cultnet.database-change-raw.schema.json` uses `allOf` and `if`/`then`/`else`;
both `cultmesh.verse-catalog-*.schema.json` use `uniqueItems` six times and
`anyOf` once. **Those three schemas are now formally unvalidatable** until
either the keywords are implemented or the schemas are rewritten. Nothing
broke, because nothing ran them through the validator — which is the finding.

**Two gaps Hands named rather than papered over:**

- **S-11 has no dedicated regression test.** `HandleSubscribeV1Async` is
  private and reachable only through a live LiteNetLib round trip. Fixed by
  inspection, covered only by the suite passing. Owed.
- The three unvalidatable schemas above need a decision: implement the
  keywords, or rewrite them.

**Ledger correction.** Commits 0 and 1 came in at about twice the §14
estimate:

| Package | Actual (net) | §14 estimate |
|---|---|---|
| Caching | +297 | about +150 |
| Networking | +851 | about +400 |
| contracts | 163 | about 200 |

The two new Networking files alone are 714 lines, against about 360. The hop's
incoming index, the cursor's digest and mint, and the per-field door messages
all cost more than the prose assumed. The binding-alias fix was not in the
ledger at all. The estimate was wrong; the scope did not grow. The deletions
in commits 2 to 4 (Mesh collapse, `_projectRecord`, the Rust
`snapshot_query.rs`) are still to be counted against the total.

Status: cut map for two cuts. **Cut 1: the selection vocabulary in the C#
reference runtime and the Rust runtime** (sections 1-17), with the
follow-on runtime cuts and the Huginn consumer cut named and not mapped.
**Cut 2: watching a selection** (section 18), mapped to the same standard,
landing after Cut 1 and depending on it. Cut 1 goes to Hands without
waiting on anything in Cut 2.

**Rulings this map is written under (operator, 2026-09-17):**

- The vocabulary lands in CultLib at the CultNet layer, not in Huginn. It
  fills a hole: the query side of the substrate was never dropped by
  decision, only never asked for. `contracts/cultnet/cultnet-distributed-database.md:10-11`
  names RethinkDB changefeeds with "point, table/schema, and **filtered
  watches**" as prior art to keep; schema and key allowlists were built,
  filtered selection never was.
- The first cut covers the C# reference (`src\GameCult.Networking`) and
  Rust (`packages\cultnet-rs`, what Huginn consumes). TypeScript, Python and
  Kotlin follow when they have a caller.
- Deletes first: the two untyped stand-ins are replaced, not sat beside.
- The CultMesh subtraction audit is a required part of the map, with real
  line counts, and an honest miss beats a padded ledger.
- Nothing on the wire is serde-untagged (rule of this cut; evidence below).

**What survives unchanged from the Huginn-targeted specification**
(`scratchpad/cut10b-read-side-spec.md`, D1-D4): the five capabilities, the
chosen representation and the rejected ones with their reasons, the order and
snapshot cursor, the role-typed hop, the one negation, and the bound. What
changes: owner, repo, the value model of a field predicate (strings, because
the cache's indexes are strings), the parity obligation, and the subtraction.

## Pins

| Repo | Branch | HEAD | State |
|---|---|---|---|
| CultLib | `main` | `51fb449` | dirty in `native/GameCult.Mesh.Quic.Native/*` and `scripts/mutate-cultmesh.mjs` from the live QUIC campaign; **not touched**. Every `file:line` below is against `51fb449`. |
| Huginn | `eureka/memory-organ` | `25a841d` | the consumer; its cut is named in section 12 and mapped in the Epiphany campaign map. |
| Epiphany leaf | `5cda0886` | — | untouched. |

Baselines: `src/GameCult.Networking` 14,848 lines; `src/GameCult.Mesh`
21,720 lines; `tests/GameCult.Networking.Tests/NetworkingTests.cs` 141
`[Test]`s; `packages/cultnet-rs` 111 `#[test]`s; `cultnet-rs` depends on
`serde`, `rmp-serde 1.3`, `rmpv`, `serde_json`, `cultcache-rs` and no
`schemars` (`packages/cultnet-rs/Cargo.toml:14-31`).

## 1. Body facts, each by source read

**The reference's read side today.**

- `CultNetDatabase` (`src/GameCult.Networking/CultNetDatabase.cs`): change
  kinds `:15-45`; `Watch<T>` `:1052`, `WatchAllChanges` `:1063`,
  `WatchRecord<T>` `:1072`; `GetAll<T>` `:776`; per-shard mutation log with
  sequence (`:465-466`, `:528-590`, `:631` a snapshot carries
  `ShardLogSequence`). `CultNetSchemaAliasMatching` `:47-97` is the one
  schema-alias matcher, `internal static`.
- **Three separate selector engines evaluate the same two allowlists:**
  `CultNetDocumentRegistry.CreateRawSnapshotResponse` `:263-349` (snapshot,
  over `cache.AllEntries` by reflection at `:325-342`) with its own
  `MatchesRequestedSchema` `:505-524`; `CultNetDatabaseServer.CreateChangeMessage`
  `:347-384` with `Matches`/`MatchesSchema`/`InferSchemaName` `:401-468`
  (the LiteNetLib path, the one production construction site,
  `CultNetLocal.cs:154`); `CultNetDatabaseSubscriptionServer.CreateChangeCore`
  `:442-485` with `MatchesRequestedSchema` `:493-500` (the RUDP path). Two
  of them carry an identical reflection copy of `CreateRawRecord`
  (`CultNetDatabaseServer.cs:386-399`, `CultNetDatabaseSubscriptionServer.cs:502-515`).
- **The two stand-ins.** `CultNetDatabaseSubscribeMessage`
  (`CultNetSchemaMessages.cs:788-826`): `schemaIds` `:805`, `recordKeys`
  `:809`, `includeSnapshot` `:813`, then `consumerRuntimeId`, `bodyIds`,
  `supportedBodyTransports` (body-plane demand, not selection). The same
  pair on `CultNetSnapshotRequestMessage` `:737-741`,
  `CultNetShardCatalogRequestMessage` `:949-953` (shard routing, stays), and
  `CultNetSchemaCatalogRequestMessage` `:1591` (catalog, stays).
  `_projectRecord` on the subscription server (`:27`, `:44`, `:50`,
  `:239-240`, `:314`): **no production caller constructs the subscription
  server at all** — `grep "new CultNetDatabaseSubscriptionServer"` over
  `src/` is empty; it is built only in tests (`NetworkingTests.cs:1596-1605`
  passes `projectRecord`). Projection is authority that exists in the
  reference and reaches no wire and no caller.
- **The cache already owns addressing.** `CultIndexAttribute`
  (`src/GameCult.Caching/CultDocumentContracts.cs:34`, `Alias` `:41`) names
  a member as an index; the cache's index values are **strings by
  construction** (`CultCache.cs:1084` `Getter = document => getValue(document)?.ToString()`;
  `GetByIndex<T>(string alias, string value)` `:1503-1509`).
  `CultReferenceAttribute` (`:45`, `TargetType` `:53`, `Many` `:55`) names a
  member as a reference; the descriptor records `IsReference`, `IsMany`,
  `TargetSchemaName` (`CultCache.cs:1069-1080`). So a field predicate's key
  is an index alias and a hop's role is a reference member, both declared
  by the document's owner.
- **The declarations are public; the values are not.** `ToCatalogEntry()`
  (`CultCache.cs:151-176`) is public and carries every member's `Slot`,
  `MemberName`, `TypeName`, `IsReference`, `IsMany`, `TargetSchemaName`,
  `IsName`, `IndexAlias`. But `CultDocumentDescriptor.Members` (`:149`) and
  `IndexAccessors` (`:148`) are **`internal`**, and `GameCult.Caching`
  declares no `InternalsVisibleTo` for `GameCult.Networking` (the only
  `InternalsVisibleTo` in `src/` outside the vendored Unity tree is
  `GameCult.Caching.MessagePack` → `GameCult.Caching.Tests`). That is why
  `CreateRawSnapshotResponse` reaches for reflection at
  `CultNetDocumentRegistry.cs:325-342`. **An evaluator outside the cache
  assembly cannot read a declared index's value today**, by any means the
  cache offers. The earlier claim in this map that no cache change is
  needed for addressing was wrong: addressing is declared publicly and
  *readable* only inside the cache. Section 6 carries the read surface as
  an add; sections 7 and 14 carry its cost.
- **Schemas are hand-written JSON** in `contracts/cultnet/*.schema.json`,
  listed by file name in `CultNetSchemaRegistry.cs:360-385`; TypeScript
  compiles them with Ajv and refuses unknown fields
  (`cultnet.database-subscribe.schema.json` has `additionalProperties: false`);
  Python lists required fields per version by hand
  (`packages/cultnet-py/src/cultnet_py/schema_catalog.py:283`, `:407`); Rust
  types are a serde enum tagged on `schemaVersion`
  (`packages/cultnet-rs/src/contracts.rs:252`) with hand validation
  (`require_optional_string_vec`, `:548-594`). C# encodes with
  MessagePack-CSharp string keys (`[Key("name")]`), i.e. named maps; Rust
  encodes with `rmp_serde::to_vec_named`.
- **Runtime coverage of the subscribe message today.** C# server ×2, C#
  client (`CultNetDatabaseSubscriptionClient.cs:138-151`); TypeScript
  contract only (`packages/cultnet-ts/src/contracts.ts:237-247`, no server
  fanout, interop tests send it at `test/interop/cultnet-interop.test.ts:719,798`);
  Python server ×2 (`cultnet_py/interop_peer.py:390-401` with
  `DatabaseSubscription.matches` `:110-125`, and
  `cultmesh_py/server.py:315-331` with `_subscription_matches_document`
  `:449-470`, `_schema_matches_request` `:699-716`, and a third copy in
  `cultmesh_py/node.py:1322-1345`); **Rust: none** — `cultnet-rs` has
  `SnapshotRequest` with the two allowlists (`contracts.rs:314-321`) and no
  `DatabaseSubscribe` variant; Kotlin: none (a snapshot-request builder,
  `CultMesh.kt:2111`, and a `require(schemaVersion == "…snapshot_request.v0")`
  at `:2195`).
- **How each runtime treats a message it does not know**, which is the
  wire-parity fact section 9 rests on: C# throws
  (`CultNetSchemaMessageSerialization.cs:85` `_ => throw`); TypeScript
  `parseCultNetMessage` (`contracts.ts:728-744`) looks the version up in its
  Ajv validators and throws when absent; Kotlin `require`s the exact version
  (`CultMesh.kt:2195-2196`); Rust's tagged enum fails to deserialize on an
  unknown tag; **Python returns `[]`** (`interop_peer.py:360-387` falls off
  the chain; `cultmesh_py/server.py:341` delegates to `handle_message`) — a
  silent drop, no reply, and for a *known* version with unknown fields
  Python reads only the fields it expects (`message.get(...)`), so it would
  answer a selector it did not understand with the wrong rows.
- **Every committed write has a shard-log sequence** — assumed from
  `CultNetDatabase.cs:465` (`_nextLogSequences`) and the invariant "raw
  snapshot/put/delete messages must pass through `CultNetDatabase`"
  (`cultnet-distributed-database.md:115-116`); **Hands confirms** that a
  local `PutAsync` outside any shard also takes a sequence, because the
  order in section 3 rests on it. If it does not, the row's ordinal is its
  `storedAt` and the cursor refuses on any advance (section 3 says what
  that costs).

**The Aetheria catalog as a hostile example, verified at
`F:\Projects\Aetheria\Assets\Scripts\ServerShared`:**

- **Inheritance.** `ItemData.cs:272` `abstract class ItemData` (`Name`
  `:275`, `Mass` `:284`), `:313` `abstract class CraftedItemData : ItemData`
  (which re-declares `Name` at `:327`, hiding the base's), and only the
  leaves carry `[CultDocument]`: `SimpleCommodityData` `:300`,
  `CompoundCommodityData` `:330`, `GearData` `:449`, and more. The cache's
  `DiscoverMembers` (`CultCache.cs:838-875`) walks `current.BaseType`, so a
  leaf's descriptor carries every inherited member with its attributes
  (`GetCustomAttribute` reads the member's own attribute wherever it is
  declared). Inherited addressing therefore already resolves per leaf.
  **Corrected on a second read:** duplicate *slots* are refused
  (`:931-935`), and a **hiding member is already refused too** — `:937-943`
  groups keyed members by `Member.Name` and rejects any name carried at two
  different slots (`HiddenMemberMessage`), while a hiding member re-using
  one slot is caught by the duplicate-slot rule. So `CraftedItemData`'s
  `Name` at `ItemData.cs:327` hiding `:275` is **not registrable in
  CultCache as it stands**, however the aliases fall; that is a finding for
  the Aetheria campaign, not a hole here. What is *not* refused by name is
  two members with **different names** carrying the **same index alias**
  (base `[CultIndex("mass")] Mass`, leaf `[CultIndex("mass")] Weight`).
  That does not collide silently either: the alias map is built by
  `ToDictionary` at `:462-468`, which throws `ArgumentException("An item
  with the same key has already been added")` — a real refusal, from the
  wrong owner, naming neither schema nor member nor alias. D10 is therefore
  a **renaming of an existing refusal**, not a new rule, and is smaller
  than it first looked.
- **A reference to an abstract target.** `FactionProduct.cs:24`
  `CultRecordRef<CraftedItemData> Design`. `PersistedMember.FromMember`
  (`CultCache.cs:1069-1071`) takes the target type and reads
  `CultDocumentAttribute` from it — an abstract base has none, so
  **`TargetSchemaName` is `null` today for exactly this reference**. The
  registry can compute the leaf set by assignability (it already does for
  index lookups, `:1506`, and store routing, `:1828`) but records nothing.
- **A dictionary reference with a value.** `ItemData.cs:333-334`
  `[CultReference(typeof(PersonalityAttribute), many: true)] Dictionary<CultRecordRef<PersonalityAttribute>, float> DemandProfile`.
  The descriptor records `IsMany` (`:1080`) and nothing else: **no code in
  the cache enumerates a many-reference's targets**, and the only accessor
  is `Getter = document => getValue(document)?.ToString()` (`:1082`), which
  on a dictionary yields the CLR type name. The edge and its payload are
  declared and unreadable.
- **Numbers.** `PersistedMember.MemberType` (`:1053`) knows the member is a
  `float`, and the catalog persists it as `TypeName` (`:38`, `:192`,
  compared by `CompareSchemaShapes` `:605-629`), so **a member's numeric
  type is already declared and persisted**; what is missing is a typed
  accessor beside the string one. Adding an accessor is a runtime change
  with no catalog-shape or hash change, because `TypeName` is already in
  the canonical shape.

**The cache's index maps are unique indexes, and the evaluator must not use
them.** `Index(stored)` (`CultCache.cs:1849-1862`) writes
`MapOf(_indexes, (type, alias))[value] = key` — **one key per value, last
writer wins**. Two rows of one type sharing an index value do not collide
loudly; the second silently displaces the first in the map. `GetByIndex<T>`
(`:1502-1508`) reads that map and `Single<T>` (`:1838-1848`) throws only
when *several types* assignable to `T` each contribute a key, never for two
rows of one type. So `GetByIndex` answers "the row that most recently
claimed this value", not "the rows with this value".

The consequence for this cut is a rule, not a caveat: **the evaluator
evaluates every predicate per row, through the accessor, and never through
`_indexes` or `GetByIndex`.** The declared alias is an *addressing* name —
the cache's own lookup map is a different, lossier thing that happens to be
built from the same declaration. A "use the index, it is already there"
optimisation would turn `kind any_of [weapon]` into a one-row answer. S23
pins it, `WatchByIndex` already has the bug (section 18), and the
unique-index behaviour itself is left exactly as it is: it is the cache's
own contract for a single-record lookup and this cut does not renegotiate
it.

**A fourth selector engine, in Rust, that this map's first draft missed.**
`packages/cultnet-rs/src/snapshot_query.rs` (339 lines, `pub use` at
`lib.rs:29`) already serves and asks snapshot requests:

- `serve_read_only_raw_snapshot` `:64-130` is an engine — it walks the
  source's records, refuses a duplicate identity and an unregistered
  schema, applies a `CultNetReadOnlySnapshotPolicy` allowlist of
  `(schema_id, record_key)` pairs, then filters by the request's
  `schema_ids` and `record_keys` (`:112-125`). The policy is
  **authorization** and stays; `:112-125` is the fifth copy of the same two
  allowlists and goes, as the C# three do.
- `CultNetRawSnapshotQuery` `:158-285` is the client side, and it exposes
  the sharpest limit of the two-allowlist model: `request()` `:195-217`
  turns a set of exact `(schema, key)` pairs into the **cross product** of
  their schemas and their keys, and `accept_response` `:219-284` then
  *errors* on any record the cross product dragged in
  (`"snapshot response contains unexpected record"`). Asking for `(A,x)`
  and `(B,y)` asks for four and refuses two. **v1 does not fix this**:
  `schemas × keys` is still a cross product (section 13, Q-M). What v1 does
  change is that `accept_response`'s check is a *declared expectation*, not
  a re-filter of a page the server got right, and section 8's forbidden
  writer is worded to keep that distinction honest.

**The CultMesh audit** (Explore pass over `src/GameCult.Mesh` and
`CultNetDocumentRegistry.cs`, 92 sites classified) is in section 11 with its
numbers.

## 2. The vocabulary, exactly

One shape, `cultnet.selection.v1`, carried by two request messages at a new
version and answered by one page message. Field names are the wire
spelling; C# and Rust type names follow each runtime's convention.

```
Selection {
  schemas:     string[]?              // kinds: schema ids or aliases, matched by CultNetSchemaAliasMatching; absent = every schema
  keys:        string[]?              // record-key allowlist; absent = every key
  fields:      FieldPredicate[]?      // conjunction; each is any-of over one declared index alias
  cites:       Citation?              // rows whose declared reference `role` (or any reference) names `target`
  cited:       Incoming?              // rows that some row does / does not name in `role`
  projection:  "header" | "document"  // default "header"
  descending:  bool                   // default false; the one order, reversed
  limit:       uint32?                // clamped 1..=200
  cursor:      string?                // opaque; minted by the answering server
}
FieldPredicate {                                           // one operator per predicate, named by `op`; no union, no untagged
  index:   string                                          // a declared index alias, reachable on some schema the selection can reach, else refused
  op:      "any_of" | "lt" | "le" | "ge" | "gt"
  values:  string[]?                                       // present iff op = any_of; non-empty
  number:  string?                                         // present iff op is a comparison; a canonical decimal (Q-J, below); the alias must be declared numeric on every reachable schema that declares it
}
Citation       { target: RecordRef, role: string? }        // target validated as a reference the row owner recognises
Incoming       { role: string, exists: bool }
RecordRef      { schemaId: string, recordKey: string }

SelectionPage {                                            // cultnet.snapshot_response_raw.v1 carries this
  matched:  uint32
  asOf:     uint64                                          // the snapshot the page is exact for
  next:     string?                                         // absent on the last page
  headers:  RawDocumentHeader[]?  |  documents: RawDocumentRecord[]?   // exactly one present, by projection
  edges:    Edge[]?                                         // present iff the selection has `cites` or `cited`: the edges the hop traversed
  shardId?, shardEpoch?, shardLogSequence?                   // as v0 carries them
}
Edge {
  from:     RecordRef                                       // the citing row
  role:     string
  to:       RecordRef                                       // the cited row, its schema resolved to the leaf actually stored
  payloadEncoding: "messagepack"?, payload: bytes?          // the value attached to the edge (a dictionary's value), typed by the member's declared value type; present under `document`, absent under `header`
}
RawDocumentHeader = RawDocumentRecord minus payload, payloadEncoding
```

Messages: `cultnet.snapshot_request.v1 { messageId, selection, shardId?, shardEpoch? }`;
`cultnet.database_subscribe.v1 { messageId, subscriptionId, selection, includeSnapshot, consumerRuntimeId?, bodyIds?, supportedBodyTransports? }`;
`cultnet.snapshot_response_raw.v1` as above. `database_change_raw.v0` and
`database_unsubscribe.v0` are unchanged: a change is a change.

**Rules, each one owner:**

- **Order** is `(ordinal, schemaId, recordKey)` ascending, or the reverse
  under `descending`. `ordinal` is the sequence of the commit that last
  wrote the row (the shard-log sequence in the reference; the receipt
  sequence in Huginn). The row owner supplies it; the vocabulary never reads
  a clock.
- **Snapshot.** A page is exact as of `asOf`. A cursor carries `asOf`, the
  last `(ordinal, schemaId, recordKey)`, and a digest of the selection with
  `cursor` and `limit` cleared. A server that can present rows as of the
  cursor's `asOf` answers the next page over that snapshot; **a server that
  cannot refuses** `cultnet.error.v0 { code: "cursor_stale", details: { asOf, current } }`.
  The reference refuses when the shard log has advanced (rows mutate, and a
  replay is not this cut's); Huginn never refuses (append-only, one
  writer). One contract, two honest servers. A cursor that does not decode,
  or whose digest is not this selection's, is `cursor_invalid`.
- **Fields** are predicates over one declared index each, conjoined across
  predicates: `any_of` over string values, or one of four comparisons
  (`lt`, `le`, `ge`, `gt`) against one number over a declared numeric
  member. A row of a schema that does not declare the index never matches
  it. An `index` no reachable schema declares, an empty `values`, a
  comparison on an alias any reachable schema declares non-numeric, an
  empty `keys` or `schemas` list, and a `role` no schema declares are
  refused typed at the door (`selection_invalid { field, value }`), never
  answered as an empty page. `any_of` values are strings because the
  cache's indexes are strings (`CultCache.cs:1084`). Comparison numbers are
  **canonical decimal strings** under the operator's Q-J ruling. The rule is
  specified in full in the **Numbers** item below. No range operator (two
  predicates on one alias make a range), no `ne`, and no nesting.
- **Numbers (Q-J; Self's specification, 2026-09-22, which the ruling asked
  for and this map had not yet written).**
  - *Canonical form.* A number on the wire matches
    `^-?(0|[1-9][0-9]*)(\.[0-9]*[1-9])?$`, and `-0` is excluded. That fixes
    one spelling per value. It forbids leading zeros, trailing fractional
    zeros, a bare trailing point, an explicit `+`, exponent notation,
    negative zero, whitespace, and culture separators.
  - *Non-canonical spellings are refused.* Any other spelling is refused at
    the door with `selection_invalid { field: "number", value }`. It is never
    normalised. The reasons: the cursor digest and the parity vectors need
    one byte form per value, and a lenient parser differs between runtimes
    at exactly these edges. The ruling says "a non-canonical spelling of an
    equal value must not change an answer". That holds because such a
    spelling never reaches the evaluator. It gets a typed refusal, not a
    different page.
  - *Row side.* The cache renders a numeric member as its exact canonical
    decimal:
    - An integer type renders as its exact decimal. There is no 2^53 limit.
    - `decimal` renders with its trailing zeros stripped.
    - `float` and `double` render in the runtime's established shortest
      round-trip form (`"R"` in .NET, the standard shortest form in Rust),
      rewritten from exponent notation to positional digits. Parity pins
      this.
    - A NaN or infinite member matches no comparison.
  - *Comparison.* Both operands are canonical, so they are compared
    numerically by sign, then by the integer part's length, then by the
    integer digits, then by the fraction digits padded on the right. It is
    one small pure function per runtime, with no numeric parse and no
    precision limit.
  - *Mutants that must die:*
    - a lexicographic comparison ("10" < "9");
    - a comparison that ignores sign;
    - a comparison that ignores the integer part's length;
    - a door that accepts `+1`, `1.0`, `01`, `1e3` or `-0`;
    - a row rendering through float64 (a `long` member of 2^53+1 must
      compare greater than `"9007199254740992"`).
  - Refusing a non-canonical number at the door, rather than normalising it,
    was Self's reading of the ruling. **The operator accepted it on
    2026-09-22.**
- **Inheritance.** A schema is a leaf: only a type with `[CultDocument]`
  has a schema name, and `schemas` names leaves. A predicate on an alias
  applies to every reachable leaf whose descriptor carries a member with
  that alias, inherited or own — the cache already discovers inherited
  members per leaf, so "gear or commodities with `mass` over 10" is
  `schemas: [geardata, simplecommoditydata], fields: [{index: mass, op: gt, number: 10}]`
  with `mass` declared once on the abstract base. Two leaves that each
  declare a member with the same alias independently are two members; the
  predicate reads each leaf's own. One leaf that resolves the same alias to
  two members (a base member and a hiding re-declaration both carrying the
  alias) is refused at registration (D10). A numeric comparison on an alias
  that is numeric on one reachable leaf and a string on another is refused
  at the door, because the row owner cannot compare a string.
- **The hop** reads edges the row owner declares: in the reference, members
  with `CultReferenceAttribute` (role = the member's index alias or name);
  a one-reference yields one edge, a many-reference yields one edge per
  element, and a dictionary keyed by references yields one edge per key
  **with the value as the edge's payload**. In Rust, `Row::references()`
  yields `(role, target, payload)`. **A reference's target is a set of
  leaves**: the declared target type's own schema if it is a leaf, else
  every registered leaf assignable to it (`CraftedItemData` →
  `compoundcommoditydata`, `consumableitemdata`, …). The hop follows an
  edge into any row whose schema is in that set. **A stored edge naming a
  row whose schema is outside the set refuses the selection**
  (`reference_outside_target { from, role, to }`), not skips it: an edge the
  declaration forbids is corrupt data and a read does not paper over it.
  The citer's status, fields and authorization are not consulted by the
  hop; one hop, no second.
- **Edges in the answer.** A selection with `cites` or `cited` returns, beside
  the rows, the edges the hop traversed, each with its payload under
  `document` projection and without it under `header`. A weighted reference
  and a bill of materials are the same shape: follow the edge, return the
  payload, filter the rows. **Filtering on the payload as part of the
  traversal is not expressible** (section 13): that is a join condition.
- **Projection.** `header` is the record without its payload; `document` is
  the record. The consumer decides what a header *means* for typed rows
  (Huginn's typed summary is its header; the substrate carries whichever
  type the page was instantiated with).
- **Not on the wire:** the admission window (`admittedAfter/Before`) of
  Huginn's Cut 9 — `descending` plus the cursor answer "the latest"; a date
  window is a range, and ranges are out (R-3).

**The bound, carried forward in force, with one widening the operator
ruled.** Closed key set (declared aliases), a closed operator set (any-of
and four comparisons, one per predicate, no nesting), no path language, no
expression terms, no OR, one negation, one hop with its edges returned, no
depth field, no sort field, no text, no predicate on an edge's payload in
the traversal. Section 13 lists what is deliberately impossible. The
capability set is now: **C1** typed predicates over declared members,
string any-of and numeric comparison; **C2** projection; **C3** order and
cursor; **C4** one hop, both directions, returning typed edge payloads;
**C5** existence and count without bodies.

**Nothing on the wire is serde-untagged — rule, with evidence.** Probe
`scratchpad/untagged-probe/` (result in `untagged-probe-result.txt`), run
2026-09-17 against `rmp-serde 1.3.1` / `rmp 0.8.15` / `serde 1.0.229` (the
versions Huginn's lockfile resolves; `cultnet-rs` pins `rmp-serde 1.3`):
every `#[serde(untagged)]` enum with struct variants **fails to round-trip
under positional MessagePack** (`data did not match any variant of untagged
enum`) and passes only under named maps; externally tagged shapes pass under
both. The reference encodes named maps and so does Rust today, so an
untagged shape would work by accident of an undocumented encoding choice.
`grep untagged packages/cultnet-rs/src packages/cultcache-rs/src` is empty
at `51fb449`; this cut keeps it empty and pins it (section 10).

## 3. Decisions, with the reasons

**D1. Strings for field values, not typed enums.** In the Huginn-targeted
design a predicate was `Option<Vec<FindingSeverity>>`, closed at compile
time. The substrate cannot know a consumer's enums, and the cache's own index
model is string-valued; so the wire carries strings and the row owner
refuses a value outside its enum at the door. The loss is compile-time
closure; what is kept is runtime closure with a typed refusal, which is the
same guarantee to a caller.

**D2. Declared aliases, not a field-path language.** `index` and `role` are
names the document's owner declared with `CultIndexAttribute` and
`CultReferenceAttribute`; an undeclared name is refused. A dot-path over the
payload would be the string language the bound excludes, and the cache
already has the declaration mechanism.

**D3. A new message version, not new fields on v0.** Section 1's fact:
Python reads only the fields it knows and would answer a v0 selector it
did not understand with the wrong rows. A v1 version string makes every
runtime not in this cut refuse or drop by its own dispatch rather than
mis-answer. The cost is that v0 stays answerable by the reference until the
follow-ups land (R-1, a scheduled deletion with a named trigger).

**D4. One evaluator in the reference, three engines deleted.** `Select`
over `(descriptor, key, document)` rows from the cache replaces
`CreateRawSnapshotResponse`'s loop, `CultNetDatabaseServer.CreateChangeMessage`'s
`Matches`, and `CultNetDatabaseSubscriptionServer.CreateChangeCore`.
`CultNetSchemaAliasMatching` (`CultNetDatabase.cs:47-97`) becomes the one
alias matcher and goes `public`; the four Mesh copies and two Networking
copies go.

**D5. Projection becomes a wire value; `_projectRecord` dies.** The
delegate ran after authorization and reached no caller. `projection` on the
selection is the same authority as a value; `_authorizeRequest` and
`_authorizeRecord` stay, because authorization is not selection.

**D6. Subscriptions evaluate the selection on the snapshot and on every
change, and hop-bearing selections reconcile.** `cites`/`cited` are
set-dependent: a change to row B can change whether row A matches. The
subscription server already owns a diff-against-delivered reconcile
(`Reconcile` `:251-292`, `DeliveredBySourceRecordKey`); a hop-bearing
subscription runs it on every change instead of the per-change fast path.
That is the mechanism by which "watch a question" falls out, and this cut
does **not** add a local `CultNetDatabase.Watch(Selection)`; section 11
records what that later cut would collapse; it is Cut 2, section 18 (R-5).

**D7. The Rust runtime carries the evaluator, not only the types.** Huginn
evaluates selections over its own rows; if `select` lived only in C#, Rust
consumers would each write one. `packages/cultnet-rs/src/selection.rs`
holds the types, the cursor, and `select` over a `Row` trait, with no
subscription client in this cut (Huginn does not subscribe).

**D8. Numbers are addressable through the type the cache already declares;
the cache gains accessors, not attributes.** A comparison needs a numeric
value the string getter cannot give. `CultCache.cs:1053` holds the CLR
`MemberType` and the catalog already persists `TypeName` in the canonical
shape, so numeric-ness is declared; what is added is runtime: on each
member, `ValueOf(document) -> object?` and `NumberOf(document) -> string?` (the exact canonical decimal, Q-J; was `double?` before 2026-09-22)
(non-null exactly for the closed set of CLR numeric types), and the public
member entry exposes `IsNumeric` derived from `TypeName`. No new attribute,
no catalog-shape change, no schema hash moves, no migration. Rust mirrors
it as `Row::number(index)` and `RowSet::is_numeric(schema, index)`; the
consumer decides from its own types.

**Verified by source read, because everything downstream rests on it:**
`CultDocumentMemberDescriptor.TypeName` is set at `CultCache.cs:474` from
`CultSchemaTypeNames.FromType(member.MemberType)`, which renders a CLR full
name (`System.Single`, `System.Nullable<System.Single>`,
`System.Collections.Generic.Dictionary<GameCult.Caching.CultRecordRef<…>,
System.Single>`); `ToCatalogEntry()` `:168` copies it into
`CultSchemaMemberCatalogEntry.TypeName` `:42`; `CompareSchemaShapes`
compares it inside `CanonicalSchemaMember` `:605-629`. Numeric-ness is
declared and persisted today, and the claim holds.

**Two corrections to the estimate.** First, `IsNumeric` has two possible
authorities — the CLR `MemberType` in hand and the `TypeName` string read
back from a stored catalog. They must agree, or a schema validates one way
locally and another way against a persisted catalog. **The CLR set is the
authority** (a closed set: the eight integer types, `float`, `double`,
`decimal`, and `Nullable<T>` over any of them); the string set is generated
from it by `CultSchemaTypeNames.FromType`, never hand-written. S22 pins
that.

Second, and larger: the accessor is not the only thing missing. Per section
1, `IndexAccessors` and `Members` are `internal` and `GameCult.Networking`
is not a friend assembly, so the evaluator cannot read *any* declared
value — string or number — without the reflection the map is deleting. The
cache add is therefore a **public read surface over declared members**, of
which the numeric accessor is one method:
`TryGetIndexValue(object document, string alias, out string? value)`,
`TryGetIndexNumber(object document, string alias, out string canonicalDecimal)`
(**revised 2026-09-22 under Q-J**: `NumberOf` returns the member's exact
canonical decimal string, never `double`; see §2 "Numbers"; commit 0 shipped
`double` and is corrected in the Q-J fix), `ReferencesOf` (D11), and a public member view carrying `IndexAlias`,
`IsReference`, `IsMany`, `IsNumeric` and the target **`Type`** (D9 needs
the type, not the name). Section 14 carries the revised cost.

**D9. A reference's target set is resolved by the registry at evaluation,
not persisted.** Persisting `TargetSchemaNames` would change the canonical
member shape and every stored catalog's hash for such schemas. Resolving at
evaluation by assignability over registered leaves (`:1506`, `:1828` do
this today) costs nothing and is correct as of the registry in hand. The
catalog's `TargetSchemaName` stays what it is (`null` for an abstract
target) and is not read by the hop.

The resolution needs a **`Type`**, and `CultDocumentMemberDescriptor`
carries only `TargetSchemaName`, a string that is `null` in exactly the
case that matters. So the public member view exposes the target `Type`
(held already on `PersistedMember.MemberType` / the attribute's
`TargetType`, `CultCache.cs:1069-1070`), and the leaf set is
`AllDescriptors` (public, `:254-255`) filtered by
`target.IsAssignableFrom(d.DocumentType)` — the same predicate
`GetByIndex<T>` uses at `:1506` and store routing at `:1828`. It must be
computed **against the registry at evaluation, not at descriptor build**:
`BuildDescriptor` runs per type as it is registered, and a leaf registered
later would be missing from a set frozen early. A cached set keyed by
target type is legitimate only if it is invalidated by `Refresh()`
(`:257-265`) and by `RegisterDescriptor` (`:381-410`); simplest is not to
cache it.

**D10. The duplicate-alias refusal is renamed, not invented.** The Aetheria
hiding case turned out to be refused already (section 1: `HiddenMemberMessage`
at `CultCache.cs:937-943`), and two differently-named members sharing one
alias already throw — from `ToDictionary` at `:462-468`, as an
`ArgumentException` that names neither the schema, nor the members, nor the
alias. D10 is to replace that with a rejection in `DiscoverMembers`'
`rejections` list, in the shape of its neighbours: one grouping over
`IndexAlias` beside the two at `:931-943`, one message beside
`DuplicateSlotMessage` / `HiddenMemberMessage`. The behaviour barely
changes; what changes is that the refusal can be read. Nothing in the
selection depends on it — a colliding catalog never registers either way —
so it is in this cut only because the cut is already in this file and the
message is four lines.

**D11. Many-references are enumerated by the cache, with payloads.** On each
reference member, `ReferencesOf(document) -> IEnumerable<(CultRecordKey target, object? payload)>`:
a `CultRecordRef<T>` yields one; an `IEnumerable<CultRecordRef<T>>` yields
each with no payload; an `IDictionary<CultRecordRef<T>, V>` yields each key
with its value. Anything else declared `many` is refused at registration
(`unsupported_reference_shape`), so a declaration the cache cannot walk is
never silently walked as nothing.

The enumeration reads `ICultRecordRef.Key` (`CultDocumentContracts.cs:92-95`),
which every `CultRecordRef<T>` implements (`:97-99`), so it needs no
generic dispatch. Two shape rules fall out of `ResolveReferenceTarget`
(`CultCache.cs:1100-1113`) and belong in the same refusal: a member
declared `many` whose element type is not a `CultRecordRef<>` and whose
attribute names no `TargetType` has **no resolvable target at all**
(`IsReference` is true only because the attribute exists, and
`TargetSchemaName` is `null` for a reason that is not inheritance); and a
`many` member that is neither `IEnumerable<CultRecordRef<T>>` nor
`IDictionary<CultRecordRef<T>, V>` cannot be walked. Both are
`unsupported_reference_shape` at registration. A `many: false` member whose
type is not `CultRecordRef<T>` is the same fault and takes the same
refusal.

**Agreement with our own abandoned prior art** (`ThreadEpiphanyGraphQuery`,
Epiphany `5c62a650`, deleted with its host `5f6f2441`): direction and edge
kind as data, schema derived — agreed; differs as before (predicate not
subgraph read; roles are declared names not free strings; one negation
added; no depth field).

**Rejected, unchanged in reason:** Qdrant's `Filter` (open dot-path keys,
polymorphic values, OR/threshold/recursion advertised then refused, the
untagged hazard; Cut 11 lowers `FieldPredicate` to `must`/`match.any` in ~30
lines on the daemon side); ReQL term trees; Firestore `CompositeFilter`;
Mongo/ES magic-key documents; every string-parsed evaluator surveyed.

## 4. Deletes first

| Path:lines (at `51fb449`) | Lines | What |
|---|---:|---|
| `src/GameCult.Networking/CultNetDatabaseServer.cs:347-468` | 122 | `CreateChangeMessage`, `CreateRawDocumentRecord`, `Matches`, `MatchesSchema`, `InferSchemaName` — the LiteNetLib selector engine; the handler calls the shared evaluator and the shared raw-record projection |
| `src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs:424-515` | 92 | `CreateAuthorizedChange`, `CreateChange`, `CreateChangeCore`, `ResolveWireSchemaId`, `MatchesRequestedSchema`, `CreateRawRecord` — the RUDP selector engine |
| `CultNetDatabaseSubscriptionServer.cs:27, :44, :50, :239-240, :314` | 6 | `_projectRecord`, its parameter and its two call sites |
| `CultNetDatabaseSubscriptionServer.cs:294-322` | 29 | `CreateProjectedSnapshot` body: becomes evaluate + authorize + project (~12 lines stay) |
| `src/GameCult.Networking/CultNetDocumentRegistry.cs:263-349` | 87 | `CreateRawSnapshotResponse`: the loop and its reflection over `cache.AllEntries` (`:325-342`); the raw-record construction (`:297-311`) survives as `ToRawRecord` |
| `CultNetDocumentRegistry.cs:505-524` | 20 | `MatchesRequestedSchema`, a copy of `CultNetSchemaAliasMatching.Matches(candidate, descriptor)` |
| `CultNetDocumentRegistry.cs:247-258` | 12 | `CreateSnapshotRequest` (v0 builder); v1 takes a `Selection` |
| **Networking total** | **368** | |
| `src/GameCult.Mesh/CultMeshSnapshots.cs:638-661, 750-773, 889-911` | 71 | three verbatim clones of the selector-carrying options; one `with` copy over a record type remains |
| `src/GameCult.Mesh/CultMesh.cs:2721-2748, 1941-1969` | 57 | the fourth and fifth copies (`ToSnapshotRequestOptions`, `WithPublicationBindingOptions`) |
| `CultMeshSnapshots.cs:973-983` | 11 | `CleanSnapshotFilter`: normalisation is the selection's own validation |
| `CultMeshSnapshots.cs:1000-1009, 1033-1047, 1078-1088` | 36 | the client-side re-filter of a snapshot by schema and `InferSchemaName`; the server's v1 answer is exact, decode stays |
| `CultMesh.cs:2750-2800` | 51 | `ReadDocumentFromSnapshotResponse`'s four-tier fallback (which returns a record with the wrong key when only the schema matches) and `TryDecodeSnapshotDocument`; replaced by one exact read (~10 stay) |
| `CultMesh.cs:3474-3497` | 24 | `IsSameCultDocumentSchema`: `CultNetSchemaAliasMatching`, now public |
| `CultMeshSnapshots.cs:625-636` and the rule at `:198`, `CultMesh.cs:2716` | ~15 | the "no schema filter when keys are given" rule, three times; one place |
| `CultMeshClient.cs:574-604, 830-860, 932-962` selector construction | ~25 net | `{recordKeys, schemaIds}` literals become `Selection` literals; the retry loops stay |
| `CultMeshBodyDemand.cs:177-221` | ~15 net | `CultMeshHotBodySubscription` builds two allowlists; builds one `Selection` |
| `CultMesh.cs:2701-2719, 2838-2858, 2875-2915` | ~20 net | selector construction for peer snapshots and body subscriptions |
| `CultMeshSnapshots.cs:139-201, 434-516, 611-623` overlay logic | ~40 net | overlaying caller `schemaIds`/`recordKeys` onto defaults, four times |
| **Mesh total** | **≈ 365 gross, ≈ 331 net** | see section 11 for what does not go |
| `packages/cultnet-rs/src/snapshot_query.rs:100-130` | ~31 | the fifth selector engine: the `requested_schemas` / `requested_keys` sets and the two `is_some_and` filters inside `serve_read_only_raw_snapshot` become one `select` over a lowered v0 selection. The duplicate-identity and unregistered-schema refusals (`:90-110`) and the whole `CultNetReadOnlySnapshotPolicy` (`:132-157`, authorization) stay |
| `src/GameCult.Caching/CultCache.cs` | **0** | the cache deletes nothing. The string `Getter`/`GetterNullable` pair (`:1081-1082`) stays: it is what `any_of` compares and what the index maps are built from (`:1858`). The numeric accessor is beside it, not instead of it |
| `tests/GameCult.Networking.Tests/NetworkingTests.cs`: `CultNetDatabaseServer_Creates_Filtered_SnapshotResponse` `:3124`, `…_ForCompatibleSchemaAlias` `:3158`, `CultNetDatabaseServer_Creates_Filtered_SubscriptionChange` `:5547`, `…_ForSchemaAlias` `:5601`, `DatabaseSubscription_FiltersLiveChangesByWireSchemaBinding` `:2256`, `…ByRequestedSchemaAlias` `:2310`, and the `projectRecord` lambda in `…AuthorizesRequestAndFiltersSnapshotAndLiveRecords` `:1601-1605` | ~250 | rewritten over v1 selections against the one evaluator (section 10); the rules they pin survive |

**Not deleted in this cut, by ruling (R-1), and scheduled for deletion on FU-v0's trigger:** the v0 message classes and
their three schema files, the v0 arms in `CultNetSchemaMessageSerialization`,
and the v0 handling in the two servers — kept as a **lowering**: a v0
request lowers to `Selection { schemas: schemaIds, keys: recordKeys, projection: document }`
and is answered through the same evaluator, so v0 has no engine of its own.
They die with the last runtime follow-up.

## 5. Keeps and moves

- `CultNetSchemaAliasMatching` (`CultNetDatabase.cs:47-97`), `internal` →
  `public`, the one alias matcher for every runtime-side match.
- `_authorizeRequest`, `_authorizeRecord`, `Reconcile`, `Watch`,
  `ApplyProjectedChange`, `PublishDemand`, `DemandChanged` and the body-plane
  fields of the subscribe message: authorization, reconciliation and demand
  are not selection.
- `CultNetDocumentRegistry`'s raw-record construction (`:297-311`) as
  `ToRawRecord(descriptor, binding, key, document, options)`; the payload
  schema resolution (`:452-503`) untouched.
- The v0 messages, schemas and arms as a lowering (above).
- `CultMeshSnapshotRequestOptions` as a record `{ Selection, ShardId, ShardEpoch, …transport knobs }`.
- Every (B)-class site of the audit (section 11): transport racing,
  authority, freshness, body routing, provider-session validation.
- The interop lanes and CI witness (`.github/workflows/cultnet-interop.yml`)
  green throughout, because v0 still answers.

## 6. Adds

| Add | Owner | Live consumer | Protected invariant | What it replaces |
|---|---|---|---|---|
| `src/GameCult.Caching/CultCache.cs`: a **public read surface over declared members** — `CultDocumentDescriptor.DeclaredMembers` (a public view: `MemberName`, `Slot`, `IndexAlias`, `IsName`, `IsReference`, `IsMany`, `IsNumeric`, `TargetType`), `TryGetIndexValue(document, alias, out string?)`, `TryGetIndexNumber(document, alias, out string canonicalDecimal)` (Q-J, revised 2026-09-22), `ReferencesOf(document, member)` (D11), `ResolveTargetLeaves(targetType)` over `AllDescriptors` (D9) | `GameCult.Caching` | the evaluator; `CultNetDocumentRegistry`; later any runtime-side reader | **the cache owns addressing and its values**; a reader outside the assembly reads declarations and values through one surface instead of reflecting over documents | the reflection loop at `CultNetDocumentRegistry.cs:325-342`, and the absence that forced it |
| `CultCache.cs` registration refusals: one alias-collision rejection beside `:931-943` (D10), one `unsupported_reference_shape` rejection for a `many`/reference member the cache cannot walk (D11) | `GameCult.Caching` | every registering catalog | a declaration the cache cannot read is refused when it is declared, not answered as nothing when it is read | an `ArgumentException` from `ToDictionary` `:462-468` that names nothing; a silent unwalkable reference |
| `contracts/cultnet/cultnet.selection.schema.json` (`$id`, referenced by `$ref`), `cultnet.snapshot-request.v1.schema.json`, `cultnet.database-subscribe.v1.schema.json`, `cultnet.snapshot-response-raw.v1.schema.json`; registry entries in `CultNetSchemaRegistry.cs` | CultLib contracts | C#, Rust; TS/Py/Kotlin follow-ups | one published shape of a selection, hand-written like its siblings, pinned by vectors in both runtimes | `schemaIds`/`recordKeys`/`includeSnapshot` as the wire's only selection |
| `src/GameCult.Networking/CultNetSelection.cs`: `CultNetSelection`, `CultNetFieldPredicate`, `CultNetCitation`, `CultNetIncoming`, `CultNetSelectionProjection`, `CultNetRecordRef`, `CultNetRawDocumentHeader`, `CultNetSelectionPage`, **`CultNetEdge`** (`From`, `Role`, `To`, `PayloadEncoding?`, `Payload?`), **`CultNetSelectionOperator`** (`AnyOf`, `Lt`, `Le`, `Ge`, `Gt`, serialised as the `op` string, never as a union); `Validate(IReadOnlyList<CultDocumentDescriptor>)` — declared aliases and roles, non-empty lists, exactly one of `values`/`number` per `op`, and every reachable leaf declaring a compared alias declares it numeric | `GameCult.Networking` | the three message classes, the evaluator, Mesh | a selection is typed data with one validation | `_projectRecord`, the allowlist pair |
| `src/GameCult.Networking/CultNetSelectionEvaluator.cs`: `Select(cache, descriptors, selection, ordinals, asOf)` → ordered, hopped, cursored, projected page; `Matches(selection, descriptor, key, document)` for a single change; the cursor mint/parse with `cursor_stale` / `cursor_invalid` | `GameCult.Networking` | `CultNetDocumentRegistry` (snapshot), both servers (changes), `Reconcile` | one evaluator; order, snapshot and hop have one owner | the three engines (section 4) |
| `CultNetDatabase.LastWriteSequence(schemaId, key)` (a per-key ordinal kept on apply, ~20 lines) | `CultNetDatabase` | the evaluator's order and cursor | a row's ordinal is its last commit's sequence | `storedAt` as the only time |
| v1 message classes beside the v0 ones; the v0→`Selection` lowering (one function) | `GameCult.Networking` | both servers | v0 has no engine of its own | — |
| `packages/cultnet-rs/src/selection.rs`: the types (`Serialize`, `Deserialize`, no `untagged`; `SelectionOperator` an **externally tagged / string** enum, `Edge { from, role, to, payload_encoding, payload }`), `Row` trait (`id`, `ordinal`, `values(index) -> &[String]`, **`number(index) -> Option<f64>`**, **`references() -> Vec<(role, RecordRef, Option<Vec<u8>>)>`**), `RowSet` trait (`declared_indexes`, `declared_roles`, **`is_numeric(schema, index)`**, **`target_leaves(role)`**), `Malformed`, `select`, cursor mint/parse, `LIMIT_MAX` | `cultnet-rs` | Huginn (section 12), later Rust services | the same evaluator semantics as the reference, pinned by vectors both ways | Huginn's own `select.rs` from the earlier design |
| `snapshot_query.rs`: `serve_read_only_raw_snapshot` calls `select` over a lowered v0 selection; the policy check stays where it is | `cultnet-rs` | its existing callers and tests | the fifth engine has no rule of its own | the filter at `:112-125` |
| `contracts.rs`: `SnapshotRequestV1`, `DatabaseSubscribeV1`, `SnapshotResponseRawV1` variants | `cultnet-rs` | Huginn's daemon | the wire spelling equals the reference's | — |
| `contracts/cultnet/selection-vectors.cs-written.json` and `selection-vectors.rs-written.json`: a fixture row set (`cultnet.interop-selection-row` document type; declared indexes `kind`, `severity`, `tags[]` and **`mass` (numeric, declared on the abstract middle type, not on the leaves)**; references `parent`, `related[]` and **`components` (a dictionary keyed by reference with a float payload)** and **`design` (declared target = the abstract middle, resolving to two leaves)**; **two leaf schemas under one abstract middle**, so inheritance, the abstract target, the dictionary edge and the comparison are all exercised by the fixture rather than argued about in prose), **twenty-eight** selections, and each selection's expected page as ordered ids + `matched` + `next` presence + **the expected `edges` with their payload bytes**. The fixture must also carry, because tests depend on it and a loosening that cannot fail is the fixture's defect: a row whose `mass` **equals** a compared number exactly (S16), **three** rows sharing one index value (S23), one edge pointing outside its declared target (S18), two dictionary entries with **different** payload bytes (S19), and a nullable numeric member and a `decimal` one (S22). **Expected to grow** (R-4): it is chosen for coverage now, not pinned as final, and adding rows or selections to it is a normal change | tests, both runtimes | `NetworkingTests`, `cultnet-rs/tests/selection.rs` | **vectors written by the reference decode and evaluate identically in Rust, and vectors written by Rust decode and evaluate identically in the reference** | a within-runtime round trip, which pins nothing about parity |
| `contracts/cultnet/interop/cultnet.interop-selection-row.schema.json` | contracts | the vectors | — | — |
| `docs/cultnet-selection-cut.md` (this file), a paragraph in `contracts/cultnet/cultnet-distributed-database.md` under "Implemented", and the runtime table row in `docs/runtime-parity-scope.md` | docs | — | describe the live system | — |

No dependency in either runtime (`schemars` is **not** added to
`cultnet-rs`: the schema is hand-written JSON like every CultNet schema, and
Huginn's derived schema for its own request embeds `Selection` through a
`#[schemars(schema_with)]` field attribute in Huginn. The attribute returns a
`$ref` to this cut's published selection schema `$id`. It does not go through
the substrate. (Corrected 2026-09-22: a hand-written `impl JsonSchema` for a
foreign type fails to compile with `E0117`, the orphan rule. The read-side
Imagination probed it.) —
see section 12). No new package, binary, transport, or store format.
**`packages/cultcache-rs` is untouched**: the C# cache change buys the
evaluator a read surface it does not have, and in Rust the row owner
already supplies exactly that through `Row`/`RowSet`. The asymmetry is
real and intended — the reference reads documents the cache owns, Rust
reads rows the consumer owns — and it is the reason the parity obligation
is on the *vectors* and not on the two implementations' shapes.

## 7. Per-file changes (code that exists, anchored; new code by name and rule)

**`src/GameCult.Networking/CultNetSchemaMessages.cs`**: after `:826`, the
three v1 classes; `CultNetSchemaVersions` (`:77`, `:93` and the
`SnapshotResponseRaw` constant) gains `SnapshotRequestV1`,
`DatabaseSubscribeV1`, `SnapshotResponseRawV1`. v0 classes untouched.

**`CultNetSchemaMessageSerialization.cs:9-85`**: three arms before the
`_ => throw`.

**`CultNetSchemaRegistry.cs:360-385`**: four `SchemaResourceSpec` entries.

**`CultNetDatabase.cs`**: `:47` `internal static class` → `public static
class`; a `Dictionary<(string, string), long>` maintained where a mutation
is appended to the shard log (Hands anchors on the append site; section 1's
assumption is confirmed there) and `LastWriteSequence`; `:1052-1075` untouched.

**`CultNetDocumentRegistry.cs`**: `:247-258` → `CreateSnapshotRequest(messageId, CultNetSelection)`;
`:263-349` → `CreateRawSnapshotResponse(cache, messageId, selection, ordinals, asOf, options)`
calling the evaluator and `ToRawRecord`; `:505-524` deleted.

**`CultNetDatabaseServer.cs`**: `:276-316` handles v0 (lowered) and v1;
`:332-345` `CreateSubscription` calls `evaluator.Matches(selection, change)`
then `ToRawRecord`; `:347-468` deleted.

**`CultNetDatabaseSubscriptionServer.cs`**: `:27, :44, :50` `_projectRecord`
deleted; `:112-167` handles v0 (lowered) and v1, and answers a
`snapshot_response_raw.v1` page with `matched`/`asOf`/`next`; `:218-249`
`Watch`: a selection with `cites`/`cited` runs `Reconcile` on every change,
otherwise `Matches` on the changed row; `:294-322` evaluate + authorize +
project; `:424-515` deleted. Module doc `:14-18` rewritten: projection is a
value on the request.

**`src/GameCult.Caching/CultCache.cs`** (the only cache file this cut
touches):

- `:112-150` `CultDocumentDescriptor`'s constructor and fields: `Members`
  `:149` and `IndexAccessors` `:148` stay `internal`; a **public**
  `DeclaredMembers` view and the three value accessors are added beside
  them. Do not widen `Members` itself — `CultDocumentMemberDescriptor` is a
  mutable internal DTO whose shape `ToCatalogEntry` `:151-176` depends on,
  and publishing a mutable descriptor list makes the catalog's shape a
  public contract by accident. The public view is a readonly struct or
  sealed record built from it.
- `:445-490` `BuildDescriptor`: the alias `ToDictionary` at `:462-468` is
  the site of the D10 refusal's *symptom*; the refusal itself belongs in
  `DiscoverMembers` so it lands in `rejections` with its neighbours.
- `:838-946` `DiscoverMembers`: two new groupings beside the two at
  `:931-943` — one over `IndexAlias` (D10), one over reference shape
  (D11) — and two message helpers beside `DuplicateSlotMessage` /
  `HiddenMemberMessage`. Nothing in the existing walk changes.
- `:1050-1114` `PersistedMember`: `Getter`/`GetterNullable` `:1081-1082`
  untouched; `NumberOf` (non-null exactly for the closed CLR numeric set,
  `Nullable<T>` unwrapped) and `ReferencesOf` added beside them;
  `MemberType` `:1052` and the attribute's `TargetType` surface through the
  public view as the target `Type`.
- `:254-255` `AllDescriptors` is the input to `ResolveTargetLeaves`; the
  set is computed per call, not cached, because `Refresh()` `:257-265` and
  `RegisterDescriptor` `:381-410` can both add a leaf after any given
  descriptor was built.
- `CultSchemaTypeNames.FromType` (`CultSchemaTypeNames.cs:9-31`) is
  `internal` and generates the `TypeName` strings; the `IsNumeric` string
  set is generated from the CLR set through it, never written by hand
  (S22).

**`packages/cultnet-rs/src/snapshot_query.rs`**: `:100-130` the two
allowlist sets and their filters become one `select` over the lowered
selection; `:64-99` and `:132-157` untouched; `:158-285`
`CultNetRawSnapshotQuery` untouched in this cut and named in Q-M.

**`CultNetSelection.cs`**, **`CultNetSelectionEvaluator.cs`** (new): as
section 6. Rules that must die under their own mutation are in section 10.

**`src/GameCult.Mesh/CultMeshSnapshots.cs`**: `:16-62` the options record
carries `Selection`; `:139-201`, `:434-516`, `:611-636` overlay through one
`WithSelection`; `:638-661`, `:750-773`, `:889-911` deleted; `:973-983`,
`:1000-1009`, `:1033-1047`, `:1078-1088` deleted.
**`CultMesh.cs`**: `:1941-1969`, `:2721-2748`, `:2750-2800` (one exact read
remains), `:3474-3497` deleted; `:2701-2719`, `:2838-2858`, `:2875-2915`
build a `Selection`.
**`CultMeshClient.cs:574-604, 830-860, 932-962`**, **`CultMeshBodyDemand.cs:177-221`**:
`Selection` literals.

**`packages/cultnet-rs/src/selection.rs`** (new), **`contracts.rs:314-331`**
region gains the three v1 variants, **`lib.rs`** re-exports. **`tests/selection.rs`**
(new): the vectors both directions, the evaluator, the cursor, the
refusals, the untagged grep.

**`tests/GameCult.Networking.Tests/NetworkingTests.cs`**: the seven tests in
section 4 rewritten; new tests in section 10; the vector writer under
`CULTNET_WRITE_VECTORS=1` following `scripts/write-cultmesh-realtime-frame-vectors.mjs`
and the C#-written pattern the QUIC campaign established (`docs/typescript-quic-realtime-cut.md:15-21`).

## 8. Authority map

- **Owner.** CultNet's database organ, `src/GameCult.Networking`, as the
  reference: `CultNetSelection` owns the shape and its validation;
  `CultNetSelectionEvaluator` owns matching, the hop, the order, the
  snapshot and the cursor; `CultNetSchemaAliasMatching` owns schema
  identity; `CultNetDatabase` owns a row's ordinal. **The cache owns
  addressing and every value of it**: which names are indexes, which
  members are references, whether a member is numeric, what leaves a
  reference may target, what a row's value at an alias is, and what edges
  and payloads a row carries — through `CultIndexAttribute` and
  `CultReferenceAttribute`, the persisted `TypeName`, and the public read
  surface of section 6. The vocabulary reads those declarations and adds
  none: no new attribute, no persisted field, no schema-hash movement, and
  no opinion in the evaluator about what a CLR type means. **The evaluator
  never reflects over a document**; if it needs something the cache does
  not expose, the cache gains a method, not the evaluator a `GetProperty`. `packages/cultnet-rs`
  is the Rust spelling of the same owner, at wire parity, with the row
  owner supplying what the cache supplies in C# (`Row`, `RowSet`).
- **Inputs.** The cache's rows and descriptors, the shard log's sequences,
  the selection, the peer's authorization answers. Not inputs: a clock,
  a payload path, a transport.
- **Outputs.** Pages (`headers` or `documents`, `matched`, `asOf`, `next`),
  per-change match decisions, typed refusals (`selection_invalid`,
  `cursor_stale`, `cursor_invalid`) as `cultnet.error.v0` with a code, the
  form the reference already uses for routing errors.
- **Derived state.** The page, the order, the incoming-edge index per
  evaluation, the cursor. **Demotions:** `schemaIds`/`recordKeys` are no
  longer a selection language; they are a lowering of v0 into one.
  `_projectRecord` is no longer an authority; projection is a request
  value. `CreateRawSnapshotResponse`, `CreateChangeMessage` and
  `CreateChangeCore` are no longer engines; they call one.
  `IsSameCultDocumentSchema`, the Mesh `InferSchemaName` and the registry's
  `MatchesRequestedSchema` are no longer owners of schema identity.
- **Forbidden writers.** Neither server may match a record by a rule of its
  own; **`serve_read_only_raw_snapshot` may not filter by schema or key**
  (its policy is authorization and stays; its allowlist filter goes); Mesh
  may not re-filter a page the server answered — a caller *may* verify a
  page against expectations it declared before the request
  (`CultNetRawSnapshotQuery::accept_response`, which errors rather than
  quietly dropping, and so cannot hide a server that answered wrongly), but
  no caller may narrow a page and call the narrowed result the answer;
  nothing may mint a
  cursor a caller could construct; nothing may answer a page over a
  different `asOf` than its cursor's; the evaluator may not read a payload
  by path, only declared indexes and references, **and may not reflect over
  a document at all**; **the evaluator may not read the cache's `_indexes`
  map or call `GetByIndex`** — those answer a unique-index lookup, not a
  predicate (section 1, S23); no rule may consult an edge's payload while
  traversing (section 13); nothing outside `GameCult.Caching` may decide
  whether a member is numeric or what leaves a target resolves to; no type
  on the wire is `untagged`; the Rust `select` may not diverge from the
  reference on any vector.
- **Shared paths.** The evaluator under snapshot, change, and reconcile;
  `CultNetSchemaAliasMatching` under every schema match in both assemblies;
  `ToRawRecord` under snapshot and change; the v0 lowering under both
  servers.
- **Deletion line.** Commit 0: the cache's public read surface and its two
  registration refusals, with S17-S22 green and `GameCult.Networking`
  untouched — the cache change is independently true and lands first, so
  that if the rest of the cut is cut, the substrate is not left with half a
  surface. Commit 1: the shape, the evaluator and the v1
  messages beside v0, with the three engines rewired and deleted and the
  seven tests rewritten — green. Commit 2: `_projectRecord` and the
  Mesh collapse. Commit 3: Rust. Commit 4: vectors both ways and entries.
  Nothing lands with two engines alive.
- **Where the line to CultMesh falls.** Mesh is a client that builds
  selections and reads pages; it evaluates nothing. Where Mesh watches a
  cache locally by name or index (audit sites #2, #4-9, #16-21), it keeps
  its own loops until `CultNetDatabase.Watch(Selection)` exists, which is Cut 2 (section 18).

## 9. Parity: what this cut covers, and what the other runtimes do

**In this cut:** the C# reference (both servers, the client, the registry)
and Rust (`cultnet-rs`: types, evaluator, cursor; no subscription client,
because no Rust caller subscribes). Parity is pinned by vectors in both
directions (section 10), not by a round trip inside one runtime.

**Runtimes not in this cut, stated answer per runtime, by source read:**

| Runtime | On v0 (unchanged) | On a v1 request it receives | On a v1 page it receives |
|---|---|---|---|
| TypeScript | unaffected; the reference still answers v0 | **refuses**: `parseCultNetMessage` finds no validator for the version and throws (`contracts.ts:728-744`) | refuses the same way |
| Kotlin | unaffected (client only) | **refuses**: `require(schemaVersion == …v0)` throws (`CultMesh.kt:2195`) | n/a as a server; a Kotlin client sends v0 and gets v0 |
| Python | unaffected | **silently drops, no reply**: `handle_server_message` returns `[]` for an unknown version (`interop_peer.py:387`), so a caller times out rather than reading a refusal; **it cannot answer with wrong rows**, because v1 is a version it does not dispatch, which is why D3 chose a version over new fields | as above |
| Rust | in this cut | in this cut | in this cut |

The one runtime that mis-answers today is none: nothing sends v1 to a
runtime that does not speak it until its follow-up lands, and a stray v1 is
refused (TS, Kotlin, C#) or dropped (Python). **Python's drop is fixed in
this cut, not in its follow-up** (R-2): after this cut the table's Python
row reads "refuses with `unsupported_schema_version`", and every runtime on
the wire answers an unknown version with something a caller can read. The
build budget gains one Python test run for it.

**Follow-ups, recorded with triggers:**

- **FU-TS.** `cultnet-ts`: the three v1 contracts, Ajv registration, a
  `Selection` type; `cultmesh-ts` builders. Trigger: the first TypeScript
  caller that needs a filtered snapshot or subscription (the browser Eve
  runtime, when it reads pipeline state). Until then TS refuses v1 loudly.
- **FU-Py.** Its first line is **not** a follow-up: by R-2, `cultnet-py`
  and `cultmesh-py` reply `cultnet.error.v0
  { code: "unsupported_schema_version" }` for an unknown version **in this
  cut** (five lines at `interop_peer.py:360-387` and the delegating path at
  `cultmesh_py/server.py:341`, with one test per site that a v1 message
  produces a refusal and not an empty list). The rest of FU-Py is the v1
  contracts, and the **three** copies of the selector match
  (`interop_peer.py:110-125`, `cultmesh_py/server.py:449-470`,
  `cultmesh_py/node.py:1322-1345`) collapsed into one evaluator. Trigger:
  the first Python peer asked a v1 question, or the interop lane gaining a
  v1 case.
- **FU-Kt.** `cultmesh-kotlin`: v1 snapshot request builder and page
  reader. Trigger: an Android/JVM client reading filtered state.
- **FU-v0.** Retire v0's classes, schemas and lowering once FU-TS, FU-Py,
  FU-Kt have landed and the interop lanes send v1. Trigger: the last of the
  three.
- **Not a follow-up any more:** `CultNetDatabase.Watch(Selection)` is
  **Cut 2**, mapped in section 18 with its own deletes, authority map,
  verification and numbers. It lands after this cut. The audit's ≈ 240
  local-watch lines are counted there, and the real figure turned out
  larger than the audit's estimate (≈ −378 in Mesh, section 18.3), because
  the audit did not count the nine `Collection*` overloads as selection.

`docs/runtime-parity-scope.md`'s table gains, per runtime, "typed selection
v1: C# and Rust claimed; TS, Python, Kotlin not claimed (refuse / refuse /
drop)". The claim is written where the parity claims live, not here alone.

## 10. Verification

**Builds.** `dotnet build` of `GameCult.Caching`, `GameCult.Networking`,
`GameCult.Mesh` and their test projects — the cache is now in the build
budget, and it is the assembly the rest of CultLib depends on, so
`dotnet test tests/GameCult.Caching.Tests` is the gate on commit 0 and runs
on every later commit; `cargo test -p cultnet-rs`; the TypeScript workspace
untouched but `npm run test --workspace packages/cultnet-ts` run once to
prove the interop lane still passes on v0. Host = target = workstation for
C# and Rust; no native code. Nothing cleaned.

**Tests, each named for the rule it pins, with the mutation that kills it —
a loosening where one exists.**

| # | Test (C# unless marked) | Pins | Revert kills | Loosening kills |
|---|---|---|---|---|
| S1 | `Selection_RefusesUndeclaredIndexRoleAndEmptyLists` | D2, door refusals typed | validation dropped (empty page) | `index` checked against *any* schema's aliases rather than the reachable ones (a predicate on an alias only an excluded schema declares matches nothing silently) |
| S2 | `Evaluator_ConjoinsAnyOfPredicatesOverDeclaredIndexes` | fields | an arm dropped | any-of treated as all-of |
| S3 | `Evaluator_OrdersByLastWriteSequenceThenIdentityAndReverses` | order, `descending` | sort by `storedAt` | `descending` ignored |
| S4 | `Evaluator_PagesExactlyOnceAndTheLastPageSaysSo` | cursor walk | `next` always present | position `>=` (last item repeats) |
| S5 | `Evaluator_RefusesAStaleCursorAfterTheShardLogAdvances` | `cursor_stale` typed | advance ignored (a page over a moved set) | digest check dropped (a cursor accepted under another selection) |
| S6 | `Evaluator_HopsOneEdgeByDeclaredReferenceAndRole` | hop, roles from `CultReferenceAttribute`, `Many` | role ignored | a second hop followed |
| S7 | `Evaluator_NegatedIncomingEdgeIsTheOnlyNegation` | `cited { exists: false }` | `exists` ignored | citer's status consulted (an unauthorized citer's edge dropped) |
| S8 | `Evaluator_HeaderProjectionCarriesNoPayload` | projection | payload carried | `payloadEncoding` carried without payload |
| S9 | `Subscription_EvaluatesSelectionOnSnapshotAndEveryChange_AndReconcilesHopBearingOnes` (rewrite of `:1587`) | D6 | a change matched by v0 allowlists | a hop-bearing subscription taking the per-change fast path (a change to the citer not reconciled) |
| S10 | `V0Request_LowersToASelectionAndHasNoEngineOfItsOwn` (rewrites of `:3124`, `:3158`, `:5547`, `:5601`, `:2256`, `:2310`) | D3/D4 | v0 matched by its own loop | alias matching inlined again in one server |
| S11 | `SchemaAliasMatching_IsTheOneMatcherInBothAssemblies` (grep-shaped, negative) | D4 | — | — |
| S12 | `SelectionVectors_WrittenByTheReference_DecodeAndEvaluateIdenticallyInRust` (Rust reads `selection-vectors.cs-written.json`) and `SelectionVectors_WrittenByRust_DecodeAndEvaluateIdenticallyInTheReference` (C# reads `selection-vectors.rs-written.json`) | **parity** | any semantic drift on either side | a vector whose expected ids are compared as a set (order lost) — the vectors carry ordered ids and `next` presence |
| S13 (Rust) | `selection_is_never_untagged` (grep over `packages/cultnet-rs/src` for `untagged`, empty) and `selection_round_trips_named_and_positional` (both encodings) | the encoding rule | an `untagged` shape added | — |
| S14 (Rust) | `select_over_a_toy_row_set_matches_orders_hops_pages_and_refuses` | D7, no CultCache knowledge in the evaluator | as S1-S8 | as S1-S8 |
| S15 | `Mesh_ReadsASnapshotPageWithoutReFilteringIt` (Mesh tests) | the client trusts the page | the four-tier fallback restored | schema re-filter restored |
| S16 | `Evaluator_ComparesNumbersAtTheBoundaryForEachOfTheFourOperators` | D8's operators; one operator per predicate; two predicates make a range | the comparison ignored (every row matches) | `le` evaluated as `lt` (and `ge` as `gt`) — **kills only if the fixture carries a row whose `mass` equals the compared number exactly**; the fixture must, and if it does not, that is the fixture's defect, not licence to mutate something easier |
| S17 | `Evaluator_AppliesAnInheritedAliasToEveryReachableLeaf` | the inheritance rule; the cache's `BaseType` walk reaching the evaluator | the walk stopped at the leaf (`CultCache.cs:847`) — kills S17 and S12 together | "reachable" narrowed from *every* leaf matching `schemas` to the first descriptor consulted, so a two-leaf selection on an alias declared only on the abstract middle answers with one leaf's rows and no refusal — **requires the fixture to declare `mass` only on the middle and to carry rows of both leaves** |
| S18 | `Evaluator_ResolvesAnAbstractTargetToItsLeavesAndRefusesAnEdgeOutsideThem` | D9; the typed `reference_outside_target` | the target set taken as `{TargetSchemaName}`, which is `null` for an abstract target, so the hop follows nothing | the out-of-target edge **skipped** instead of refused — the option section 3 rejected, and the mutant that proves the rejection is load-bearing; **requires the fixture to carry one edge pointing at a schema outside the declared target** |
| S19 | `Cache_EnumeratesADictionaryReferenceAsEdgesCarryingItsValues` (cache tests) | D11 | `ReferencesOf` yields nothing for a dictionary — today's behaviour, restored | the keys enumerated and the **values dropped** (`payload` null), which is the shape a careless `IEnumerable<ICultRecordRef>` implementation would have; **requires two dictionary entries with different payload bytes**, or the assertion passes on shape alone |
| S20 | `Evaluator_ReturnsTraversedEdgesOnlyForHoppingSelections_AndNoPayloadUnderHeader` | the `edges` rule | `edges` never emitted | payload carried under `header`; and, separately, `edges` emitted for a selection with neither `cites` nor `cited` — a widening rather than a loosening, and killed by the same test |
| S21 | `Cache_RefusesADuplicateIndexAliasAndAnUnwalkableReferenceByName` (cache tests) | D10, D11's shape rule | the rejections removed — **kills only if the test asserts the message names the schema, the two members and the alias**, since without them `ToDictionary` still throws an `ArgumentException` and a shallow `Assert.Throws` passes either way. That is the entire content of D10 | the alias grouping keyed by `MemberName` instead of `IndexAlias`, which refuses nothing the existing `HiddenMemberMessage` (`:937-943`) did not already refuse |
| S23 | `Evaluator_MatchesEveryRowSharingAnIndexValue_NotTheCacheIndexWinner` | the evaluator reads values per row, never `_indexes` / `GetByIndex` | the predicate answered from `GetByIndex`, so an `any_of` over a value **three** fixture rows share answers with one | the per-row read kept but short-circuited after the first match when `matched` is all the caller asked for — a plausible optimisation that makes `matched` a boolean in disguise; **requires three rows sharing one index value**, which the fixture must carry |
| S22 | `Cache_DerivesIsNumericFromTheClrSetAndTheTypeNameSetAgrees` (cache tests) | D8's two authorities agreeing | `IsNumeric` true for every member, so a comparison on a string alias validates and compares `ToString()` lexically | the `TypeName`-string set hand-written and missing `System.Nullable<System.Single>` or `System.Decimal`, so a member is numeric against the live descriptor and non-numeric against the same schema read back from a persisted catalog — **requires the fixture to declare a nullable numeric member and a `decimal` one** |

**On the parity vectors and the four findings.** S12 is not a new test but
a widened one: the fixture of section 6 carries the abstract middle, the
two leaves, the inherited numeric alias, the dictionary reference with
float payloads and the abstract-target reference, and the vector set
includes at least one selection per finding — a comparison on the
inherited alias, a hop into the abstract target, a hop over the dictionary
returning payloads, and the same hop under `header`. Both directions:
**vectors written by the reference are evaluated in Rust, and vectors
written by Rust are evaluated by the reference**. A round trip inside one
runtime pins nothing and is not counted. The Rust side's fixture rows
implement `Row` over the same declared values, so the vectors test the
*semantics* both ways while each runtime keeps its own source of rows.

**Negative greps:** `rg -n "GetProperty|GetField|GetCustomAttribute" src/GameCult.Networking`
empty (the evaluator reflects over nothing);
`rg -n "untagged" packages/cultnet-rs/src packages/cultcache-rs/src`
empty; `rg -n "InferSchemaName" src/GameCult.Mesh` empty and
`src/GameCult.Networking` only in `CultNetSchemaAliasMatching` and the
registry's payload-schema resolution; `rg -n "MatchesRequestedSchema|MatchesSchema\(" src`
empty; `rg -n "_projectRecord|projectRecord" src` empty;
`rg -n "SchemaIds = .*RecordKeys = " src/GameCult.Mesh` empty (no
allowlist pair assembled by hand); `rg -n "CloneSnapshot(Facade)?RequestOptions" src` at most one.

**Mutations.** `tools`-style entries in `scripts/mutate-cultmesh.mjs`'s
format with three new targets, `caching` (killer `dotnet test
tests/GameCult.Caching.Tests`, carrying S19, S21 and S22), `networking`
(killer `dotnet test tests/GameCult.Networking.Tests`) and `rust` (killer
`cargo test -p cultnet-rs`),
one entry per rule above with revert and loosening; the runner's
byte-rewrite/verified-restore discipline applies unchanged. The runner is
the QUIC campaign's file and is dirty in the tree; **this campaign adds its
entries in its own file** (`scripts/mutate-cultnet-selection.mjs` or an
entries module the runner imports) and does not edit the QUIC campaign's
targets — Self decides the exact split at landing.

## 11. The CultMesh subtraction audit, with real numbers

Method: an Explore pass classified 92 sites in `src/GameCult.Mesh` and
`CultNetDocumentRegistry.cs` as (A) mechanical selection/projection over
schema ids, keys or fields, or (B) a domain rule (authority, freshness,
transport, session). (A) totalled **1,158 lines**, (B) **429**. **(A) is not
a reduction figure**; it is what *could* be expressed as a selection. What
collapses is what has a replacement in this cut.

| Category | Sites (audit #) | Lines now | After | Net |
|---|---|---:|---:|---:|
| Selector-carrying options cloned five times | #11, #3, #74, #75, #77 | 128 | ~30 (`with` on a record) | **−98** |
| "No schema filter when keys are given" rule, three times | #65, #73, #10 | ~16 | ~4 | −12 |
| `CleanSnapshotFilter` | #79 | 11 | 0 | −11 |
| Client-side re-filter of a snapshot by schema, plus `InferSchemaName` | #80 (part), #81, #82 | ~51 | ~10 (decode only) | −41 |
| Four-tier read fallback + decode gate | #12, #13 | 50 | ~10 | −40 |
| `IsSameCultDocumentSchema` | #22 | 24 | 0 (public matcher) | −24 |
| Selector literals in client bindings | #32, #34, #35 | 93 | ~68 | −25 |
| Hot-body subscription allowlists | #25 | 45 | ~30 | −15 |
| Peer snapshot / body subscribe selector construction | #10, #14, #15 | 81 | ~61 | −20 |
| Snapshot facade overlay logic | #64, #66-70, #72 | ~110 | ~70 | −40 |
| **Collapses in this cut** | | | | **≈ −326** |
| Local cache/database watches by name and index and their predicates | #2, #4-9, #16-21 | ≈ 240 | ≈ 240 | 0 in this cut; **≈ −378 in Cut 2** (section 18.3 — larger than this audit estimated, because the nine `Collection*` overloads at `CultMesh.cs:2278-2480` were not classified as selection and are) |
| Handle catalogs by type/schema, surface catalog, operation payload | #41-49 | 233 | 233 | 0 — these index *handles*, not records |
| Verse discovery selection and reshaping | #36, #38 (part), #39, #40, #83, #85 | ~110 | ~110 | 0 — over Verse observations, not documents |
| Single-file catalog membership | #58-62 | 81 | 81 | 0 — file-format membership |
| Provider session wire decode gate | #50 | 12 | 12 | 0 |
| (B) sites | 24 sites | 429 | 429 | 0 by definition |

**Plain statement.** `src/GameCult.Mesh` shrinks by about **326 lines of
21,720 (1.5%)** in this cut, and a further **≈ 378 in Cut 2** — the two
together are about 3.2% of the assembly, and Cut 2 is where the audit's
expectation of a real reduction is met. About 590 of the
audit's (A) lines stay because they select handles, Verse observations or
file-catalog entries, not database records. The operator's expectation of a
net reduction in CultMesh holds, modestly; it does not hold for CultLib as a
whole in this cut, because the Rust runtime is new capability (section 14).
If the expectation was a large reduction, this audit says it is not there
without the watch cut, and it is not there at all for the catalogs and
discovery.

## 12. Huginn: the consumer cut, and what moved

**Moves to CultLib (this map):** `Selection`, `Citation`, `Incoming`,
`Projection`, `Page`, the cursor, `select`, the `Row`/`RowSet` traits, the
untagged rule and its probe, the cap, the order rule and the snapshot
contract. Huginn's earlier `select.rs` is never written.

**Stays with the organ (a smaller, later cut in the Epiphany campaign map,
against a `cultnet-rs` dependency Huginn already has):**

- `impl Row for PipelineDocumentView`: `ordinal = admission.sequence`
  (Q-B A, still recommended; under Q-B B Huginn refuses `cursor_stale` on
  advance exactly as the reference does, which is now a legitimate second
  option), `values(index)` for the declared aliases `campaign`, `repo`,
  `cut`, `kinds`→(schema), `in_force`, `faculty`, `severity`, `confidence`,
  `origin`, `authority`, `claim_outcome`, `outcome`; `references()` from
  `docs::citations` with the fifteen roles; `RowSet::declared_*` as the
  organ's closed lists, so an unknown alias or a value outside an enum
  refuses typed.
- `docs::citations`, `CitationRole`, `ResolutionOutcomeKind`, the summary
  as the organ's header, the `as_of` restriction of the image to receipts,
  the semantic refusal, the ref validation of `cites.target` through the
  leaf, and the deletion of `open_items`/`history` with the relation table
  unchanged (`history(Subject)` = `kinds: [resolution], cites { target, role: subject }`;
  the open lists = five selections).
- **Deleted from the organ's wire (R-3, ruled, not proposed):**
  `PipelineQuery`; **`admitted_after` and `admitted_before`**, which existed
  only to approximate an order and are replaced by `descending` plus the
  cursor — they are a delete in the Huginn cut's own deletes-first table,
  with their query-builder call sites, their schema fields and the tests
  that pin them, not a deprecation; and `QUERY_LIMIT_MAX`. Huginn's `Query { instance, selection }` carries
  the substrate's type; its published request schema embeds `Selection`
  through a `#[schemars(schema_with)]` attribute that returns a `$ref` to the
  published selection schema. A hand-written impl fails with `E0117`
  (corrected 2026-09-22), and the substrate publishes JSON, not `schemars`. It
  is pinned by the
  existing byte-for-byte test.
- Cut 11's lowering to Qdrant and the after-index application of the hop,
  as before.

The Huginn cut is ordered **after** this cut's commit 3 (Rust) and before
Cut 11 and Cut 13, as 10b was.

## 13. Deliberately not possible, and why

OR across predicates (ask twice); negation beyond `cited { exists: false }`;
a predicate on the far end of the hop; a second hop; ranges, dates, text,
paths, sort fields, counts beyond `matched`; a page not tied to `asOf`;
selection across shards or minds; a cursor the caller constructs. Each for
the reason the earlier specification gave, unchanged.

Named individually, because each has an obvious-looking one-line
implementation and a caller who will ask for it:

| Not expressible | The obvious ask | Why it is the door |
|---|---|---|
| **A predicate on an edge's payload during traversal** — "components whose quantity is over 4", "demand profile entries weighted above 0.5" | one `if` inside the hop loop, over a payload the evaluator is already holding | **It is a join condition.** The moment a predicate can read a value that belongs to neither the row nor the selection, the evaluator is combining two sources under a condition, and everything that follows is a query planner: which side to drive from, whether to build the incoming-edge index before or after filtering, how `matched` counts a row whose only surviving edge was filtered, what the cursor means when the page is a join result, and whether the payload's type is comparable at all when its declared type differs per leaf. None of that is answerable in a bounded vocabulary. **The caller follows the edge, receives the payload in `edges`, and filters the result** — one extra step for the caller, an entire category of machinery this organ never grows |
| **A set of exact `(schema, key)` pairs** | `keys` already exists; let an entry name its schema | `schemas` × `keys` is a cross product and stays one. `CultNetRawSnapshotQuery` (section 1) shows the cost honestly: it over-requests and verifies. The fix is either a second list shape or a pair type on the wire, and both are the beginning of a term language. A caller who needs exact pairs asks once per schema, or accepts the cross product and verifies against what it declared. Recorded as Q-M, recommendation A: state the limit |
| **A predicate that depends on how many edges a row has** — "hulls with more than three components" | `cited` already counts to one | `exists: true/false` is a boolean, and a threshold is a range over a derived aggregate. Aggregation is a second evaluator with its own ordering and paging semantics |
| **`edges` without rows, or rows the hop excluded** | a `projection: "edges"` | the page's rows are the answer and the edges explain it; an edge-only page is a different question (a graph read) and would need its own order, cursor and bound |

## 14. Subtraction ledger (estimate)

| | Removed | Added |
|---|---:|---:|
| `src/GameCult.Caching` | 0 | **~150**: the public member view ~40, `TryGetIndexValue` / `TryGetIndexNumber` + the CLR numeric set ~35, `ReferencesOf` over the three shapes ~40, `ResolveTargetLeaves` ~10, two rejections with their messages ~20 |
| `src/GameCult.Networking` | 368 | ~400 (`CultNetSelection` + `CultNetEdge` + the operator 170, evaluator + cursor + the incoming-edge index 190, ordinals 20, v1 classes and lowering ~40 minus shared) |
| `src/GameCult.Mesh` | ≈ 365 gross | ≈ 40 (one `WithSelection`, one exact read) |
| `packages/cultnet-rs/src` | ~31 (`snapshot_query.rs`'s filter) | ~430 (`selection.rs` 390 with edges and numbers, `contracts.rs` 40) |
| `packages/cultnet-py`, `cultmesh-py` | 0 | ~5 (the `unsupported_schema_version` reply, ruled into this cut) |
| `contracts/cultnet` | 0 | 4 schema files (~200 lines JSON), 1 interop row schema, 2 vector files (generated) |
| tests, C# | ~250 rewritten | ~600 (S1-S12, S15-S18, S20 in Networking; S19, S21, S22 in Caching; the vector writer) |
| tests, Rust | 0 | ~340 (S12-S14 with the edge and number vectors) |
| entries | 0 | ~38 entries across three targets |
| docs | — | this map; two paragraphs |
| **net source outside tests** | | **≈ +32 in C# Networking, +150 in C# Caching, +399 in Rust; ≈ +585 across the repo** |

**The estimate moved, and in the wrong direction.** The committed version of
this ledger said ≈ +370 and ≈ −18 in C#. Both were wrong, for one reason:
the map assumed the cache needed no change, and the cache needs about 150
lines of new public surface before the evaluator can read a single declared
value from outside the assembly (section 1). The honest figure is ≈ +585.
What the repo buys for it is unchanged in kind and larger in size: one
evaluator where **five** stood (three in C#, one in Mesh's re-filter, one
in Rust), a read surface where the substrate's own consumers were
reflecting over documents, edges and their payloads where a declared
reference was unreadable, numeric comparison over a type the catalog has
persisted all along, and a Rust runtime with the capability at all. Nothing
here is a reason to shrink the cut; it is a reason not to claim the cut
shrinks the repo.

The repo grows by about 370 source lines for: a typed selection with a
derived-by-vector schema where two allowlists were the only language;
projection as a value where it was a delegate no caller reached; an exact
snapshot cursor where paging did not exist; one evaluator where three
engines and six schema matchers stood; a Rust runtime that did not have the
capability at all. CultMesh proper shrinks ≈ 326. The liability retired is
not in the line count: five copies of one options record, four spellings
of schema identity, a client that re-filtered what the server answered and
a read that could return the wrong key.

## 15. Build budget

C#: **three** assemblies and three test projects (`GameCult.Caching` joins
the budget; it is a dependency of the other two, so touching it rebuilds
them), debug, workstation. Rust:
`cultnet-rs` lib + tests, warm target dir, +0 to +60 paths. Nothing cleaned.
No native, no TypeScript build beyond one interop-lane run.

## 16. Operator questions, one batch

**Ruled 2026-09-17 and no longer questions.** Four of the batch are
settled; they are recorded here as rulings and carried in the sections
they govern.

- **R-1 (was Q-F). v0 retires on a named trigger, not "eventually."** The
  reference keeps answering v0 by lowering it into a `Selection`, with no
  engine of its own. That lowering is a **scheduled deletion**: it is
  deleted when FU-TS, FU-Py and FU-Kt have all landed and the interop lanes
  send v1, and that is FU-v0's trigger in section 9. A shim with no
  deletion date is a permanent shim; this one has a date written as a
  condition, and the condition is checkable by grep over the three
  runtimes' contract files. Section 4's "not deleted in this cut" row and
  section 5's keeps both point here.
- **R-2 (was the second half of Q-F). Python's silent drop is fixed in this
  cut.** Every other runtime refuses a message it does not understand;
  Python returns `[]` (`interop_peer.py:387`) and the caller reads a hang
  rather than a rejection. The five lines that reply `cultnet.error.v0
  { code: "unsupported_schema_version" }` land here, ahead of FU-Py's own
  trigger, because a silence is a worse failure than a missing feature.
  Carried in section 9's FU-Py entry and section 14's ledger.
- **R-3 (was Q-G). `descending` is the order; the time windows go.**
  `admittedAfter` / `admittedBefore` existed to approximate an order the
  vocabulary now states outright, and two filters standing in for an order
  are worse than the order. They are deleted from Huginn's wire in the
  consumer cut (section 12), not kept as an organ-side alias any-of. "The
  latest N" is `descending, limit: N`.
- **R-4 (was Q-I). The fixture is chosen for coverage and expected to
  grow.** The vector row type is the dedicated
  `cultnet.interop-selection-row` family of section 6, shaped to *exercise*
  the four hostile findings rather than describe them: shared fields on an
  abstract middle with two leaves under it, a reference whose declared
  target is that middle, and a many-reference carrying a payload. It is
  **not pinned as final** — the fixture is expected to gain rows and
  selections as the vocabulary meets more catalogs, and a change to it is a
  normal change, not a contract break. What is pinned is the parity
  obligation over whatever the fixture holds: both directions, ordered ids,
  `matched`, `next` presence, and the edges with their payloads.
- **R-5 (was Q-H). Watching a selection is its own cut, mapped in section
  18 of this document**, after this one and depending on it. Nothing of it
  is smuggled into the selection cut and nothing here is designed around it
  speculatively; the seam already drawn — one selector type serving both a
  query and a subscription — is all this cut owes it.

**Still open:**

- **Q-A. Leaf `Short` titles for question and ruling** (Huginn/Epiphany).
  Unchanged: **A, not now** (recommended); B, a leaf follow-up before Cut 13;
  C, a text projection (Cut 11's question). Depends: the organ's header row
  for those two kinds.
- **Q-B. Huginn's receipt ordinal.** **A, record it** (recommended; exact
  order, no `cursor_stale` on an append-only mind); **B, do not**: Huginn
  refuses `cursor_stale` on advance like the reference does — now a
  legitimate option under one contract, costing a re-walk on a busy mind
  and id-order ties within a second. Depends: Huginn's consumer cut only.
- **Q-C. Delete `open_items` and `history`.** **A, delete** (recommended);
  B, thin presets in Huginn; C, wire operations (refused). Depends: Cut 13's
  tool list.
- **Q-D. The representation.** **A, declared-alias any-of over strings in a
  new v1 selection** (recommended; D1, D2); B, Qdrant's `Filter` (rejected,
  section 3); C, typed enums on the wire (impossible in the substrate: it
  cannot know a consumer's enums; the organ keeps the typed refusal at its
  door). Depends: everything in sections 2-10.
- **Q-E.** Withdrawn: the seam question is answered by the placement ruling.
- **Q-J. Number encoding on the wire. RULED B, decimal strings,
  2026-09-17.** The operator: floats are fragile, and strings are the most
  reliable way to store a number. **Self's recommendation of A was argued
  backwards and the map said so wrongly.** B was framed as "a second value
  encoding beside `any_of`'s strings"; it is the opposite. Every value in this
  vocabulary is already a string, so decimal strings keep the wire to **one**
  value encoding, and it is float64 that would add a second. What B really
  costs is a comparison rule, and that is where the danger sits: a decimal
  string compared lexicographically answers that ten is less than nine. So
  the cut must specify a canonical decimal form and a numeric comparison,
  and pin both with mutants — a lexicographic comparison must die, and a
  non-canonical spelling of an equal value must not change an answer. Name
  the edge cases the canonical form settles: leading and trailing zeros, an
  explicit plus, exponent notation, and negative zero. No exactness limit
  needs stating, because there is no longer one.
  - *Superseded, kept as history:* **A, IEEE float64, with the 2^53
  limit stated in the schema and in this map** (recommended: one encoding,
  no parse rule per runtime, and no catalog in hand declares an integer
  member wide enough to lose). **B**, decimal strings — exact, but a second
  value encoding beside `any_of`'s strings and a per-runtime parse rule,
  and two ways to spell a number is how a vocabulary starts growing terms.
  **C**, add `integer: int64` beside `number` now, sharing `op` — additive
  and untagged-free, and can be added later without breaking A, which is
  why adding it now is premature. Depends: the schema files and both
  runtimes' predicate types.
- **Q-K. Family names for abstract bases on the wire. PARKED, 2026-09-17,
  and Self's reason was wrong.** The operator: this was possible when
  CultCache was a single file, so it is odd that it is hard now; not
  load bearing either way. That is correct. In-process a family is one
  assignability check, because the runtime holds the hierarchy. Over the
  wire there is no type system, only schema names, and an abstract type
  carries no `[CultDocument]` so it never gets one. **The hardness is not
  modelling, it is that nothing publishes the hierarchy where a remote
  caller can see it.** The honest cost is therefore a catalog addition,
  each leaf's entry naming its base chain, after which a family is the
  leaves whose chain contains the name asked for. Small work — but it moves
  the catalog's shape, and this cut's whole claim to being cheap is that it
  moves neither the catalog's shape nor its hashes. So it is **deferred to a
  cut that is already touching the catalog**, not deferred because there is
  nothing to name.

  **Trigger named, 2026-09-17: the source-generator campaign needs it first.**
  The operator's position is that any project using CultNet wants the types it
  touches native, and that where the hierarchy is not being declared in
  parallel by hand, a generator should build it. That generator runs against
  **published schemas**, with C# authoring and the schema as the contract,
  which is what the reference-runtime rule already says.

  The obstacle is the same missing fact. `CultCache.cs:847,887,966` walks the
  base types while collecting members and then discards the hierarchy, and
  `ToCatalogEntry` (`:152-177`) publishes a schema id, the canonical schema,
  the compatible ids and a flat member list carrying slot, name, type,
  reference-ness, many-ness, target and alias. **Nothing records what a type
  inherits from.** So a generator fed published schemas alone emits one flat
  class per leaf with the inherited members copied into each — exactly the
  hierarchy the operator wants built for them, and unbuildable from what is
  published today.

  So the base chain in the catalog is a **prerequisite of the generator
  campaign**, and family names fall out of it for free once it exists. Until
  then a family expands at the call site into its leaf schema names, which the
  generator can emit as a constant, costing the protocol nothing. The honest
  limit of call-site expansion, to be stated wherever it lands: it is
  closed-world per compilation, so a leaf declared where the caller does not
  compile leaves the list silently short, and an open-world caller must ask the
  catalog at run time instead.

  The generator is **its own campaign, mapped after this cut lands** (operator,
  2026-09-17: "definitely later"), because its value is emitting against a
  shape that is settled and this cut is what settles it. It is also where the
  query sugar comes from: typed per-kind builders make filtering a document by
  another kind's field a compile error rather than an empty page. **No source
  generator exists in CultLib today** — no Roslyn generator anywhere in the
  repository, and `src/GameCult.Caching/emitted/` is empty.

  Superseded reasoning, kept as history:
  an abstract type has no `[CultDocument]`, no schema name
  and no catalog entry, so there is nothing to name; a caller who wants
  "every crafted item" enumerates the leaves from the schema catalog it
  already reads. **B**, a `[CultDocumentFamily]` declaration plus a catalog
  concept plus a matching rule in every runtime, in a later cut. Note this
  is only about the `schemas` list: the *hop* already resolves an abstract
  target to its leaves (D9), so the hostile example works under A.
- **Q-L. Refusing an out-of-target reference at write time. RULED A,
  2026-09-17**, with the follow-up upgraded from optional to intended. The
  operator: notice on read for now, but on write eventually; today this is
  left to consumers to enforce, as Aetheria's Database Tools did and as
  CultCache Studio is expected to, and it is easy to insert garbage at the
  source level which a cheap check would prevent.

  **Two things make the follow-up worth more than "nice to have."** First,
  enforcement living in consumers is duplicated authority by construction:
  every tool that writes records re-implements the same check, and a tool
  that forgets is indistinguishable from one that does not, until a read
  refuses. Second, the check really is cheap — the declaration already names
  the target (`TargetSchemaName` on the member catalog entry), and the write
  path already has the record in hand.

  **What Self could establish about the consumer story, 2026-09-17:** it
  rests on less than it sounds. There is no CultCache Studio repository
  under `F:\Projects`, and no project file by that name anywhere; every
  reference to it is forward-looking, in Aetheria's migration documents,
  where it is described as absorbing that project's Database Tools. Aetheria's
  actual data tooling today is the `tools/AetherDb` console project. So the
  enforcement being relied on is one tool, in one project, for one catalog,
  and there is no second implementation to be inconsistent with yet — which is
  the cheapest moment to move the rule to its owner rather than the most
  expensive.

  Original options: **A, follow-up (FU-Ref)** (recommended): this cut refuses it at read
  (`reference_outside_target`, S18), which is where the corruption is
  noticed; refusing at write touches the cache's put path and every
  runtime's apply path and is a change of a different size. **B**, in this
  cut. Under A, the read-side refusal is the only thing standing between a
  corrupt edge and a wrong answer, which is why it refuses instead of
  skipping.
- **Q-M. The `(schema, key)` cross product. RULED A, 2026-09-17**, in the
  operator's words: once per schema. The cross product stays, the limit is
  stated, and a caller needing exact pairs asks once per schema. No third
  addressing list and no rule for combining three of them.
  Original options: **A, state the limit**
  (recommended): `schemas` × `keys` stays a cross product; a caller needing
  exact pairs asks once per schema or verifies what it declared, as
  `CultNetRawSnapshotQuery` already does. **B**, allow a `refs:
  RecordRef[]` list beside `keys` — expressible without a term language and
  genuinely useful, at the cost of a third addressing list and a rule for
  how three lists combine. **C**, let a `keys` entry carry a schema — a
  path language in miniature, refused. Depends: section 13's table and
  `snapshot_query.rs`'s client.

**Q-A, Q-B and Q-C are no longer open.** The operator ruled all three on
2026-09-17 and they are recorded in the Huginn campaign's map,
`F:\Projects\Epiphany\notes\eureka-pipeline-state-cut.md`: receipt ordinals
yes; `open_items` and `history` deleted rather than kept as presets; and the
leaf gains titles on `question` and `ruling`, riding the same store version
bump as the ordinal so there is one migration rather than two. They are named
below only because work here still depends on them.

What depends on each: S1-S8 on Q-D A; S5 on the shard-log assumption
(section 1) and on Q-B for Huginn; S9 on D6; S16 and the schema files on
Q-J; S18 on Q-L A; the Huginn cut on Q-B and Q-C. Settled by R-1 to R-5:
S10, S12, the fixture, FU-v0's trigger, FU-Py's five lines, Huginn's
window deletion, and the watch cut's existence.

## 17. What was probed, read, and not settled

**Probed:** the untagged round trip (section 2). **Read:** every line
anchored above; the CultMesh audit's 92 sites by an Explore pass whose
classification I checked at the sites that carry the ledger (the five
clones, the four matchers, the fallback, the two engines). **Not run:**
`dotnet` or `cargo` on CultLib — the tree is dirty from the QUIC campaign
and no claim here depends on a build; Hands takes the baselines named in
Pins. **Not settled:** whether every committed write has a shard-log
sequence (section 1, Hands confirms at the append site); the exact split of
mutation entries against the QUIC campaign's runner (section 10, Self at
landing); whether MessagePack-CSharp needs a union attribute for the
page's `headers | documents` (it does not if both are optional arrays with
exactly one present, which is the spelling chosen so no union is needed —
Hands confirms the schema says "exactly one" with `oneOf` over `required`).

**Read on the second pass, and what it changed.** Each of these was read at
the source, and three of them contradicted something this map or its
predecessor's notes asserted:

| Read | Held | Changed |
|---|---|---|
| `CultCache.cs:474`, `:151-176`, `:598-629`, `CultSchemaTypeNames.cs:9-31` | **Yes.** A member's type name is derived from the CLR type, persisted in the catalog entry and compared inside the canonical shape. Numeric addressing needs no new declaration, no hash movement and no migration — the load-bearing claim of this whole widening | — |
| `CultCache.cs:112-150` | — | **No.** `Members` and `IndexAccessors` are `internal` with no friend assembly, so the evaluator cannot read a declared value from outside `GameCult.Caching` at all. "No cache change is needed for addressing" was false; the cache add is ~150 lines, not ~60, and the ledger moved from ≈ +370 to ≈ +585 |
| `CultCache.cs:931-943`, `:462-468` | — | **No.** A hiding member is already refused (`HiddenMemberMessage`), and a duplicate alias already throws from `ToDictionary`. D10 shrank from "a missing refusal" to "a refusal that names nothing", and its test only means anything if it asserts the message |
| `packages/cultnet-rs/src/snapshot_query.rs` | — | **No.** Rust has a fifth selector engine (`serve_read_only_raw_snapshot:112-125`) that "Rust: none" concealed, and its client (`:195-217`) shows the cross-product limit that v1 does not fix (Q-M) |
| `CultDocumentContracts.cs:92-99`, `CultCache.cs:1100-1113`, `:1495-1509`, `:1818-1838`, `:254-265` | **Yes.** `ICultRecordRef` is the enumerable handle D11 needs; assignability is already how the cache resolves a base type to concrete documents (D9); `AllDescriptors` is public | — |

**Still not settled, and who settles it:**

- Whether every committed write takes a shard-log sequence (unchanged;
  Hands, at the append site).
- Whether `CultDocumentDescriptor`'s public member view can be a readonly
  struct without forcing an allocation per row per predicate in the
  evaluator's inner loop. Nothing here depends on the answer being fast,
  but a `Select`-per-row over a fresh list would be a silly cost to build
  in; Hands measures if a fixture of a few thousand rows is slow, and not
  before.
- The exact split of mutation entries against the QUIC campaign's runner
  (unchanged; Self at landing).
- Whether `serve_read_only_raw_snapshot`'s existing tests survive the
  lowering unchanged; they should, since the behaviour is identical, but
  they were not read line by line.
- Q-J, Q-K, Q-L, Q-M: the operator.

**Not probed, and deliberately:** nothing was built or run against CultLib.
The tree is hosting a live QUIC campaign, and no claim in this map needs a
build to be true — every one of them is a source read, and the three that
were wrong were wrong because the first pass did not read far enough, not
because a build would have caught them.

---

# Cut 2: watching a selection

Ruled 2026-09-17 (R-5): the operator wants this and wants it soon, so it is
mapped here rather than left as a follow-up trigger. It is **its own cut**,
landing after Cut 1 and depending on it. Nothing below is smuggled into Cut
1, and Cut 1 is not shaped around it: the seam Cut 1 draws — one selector
type serving a query and a subscription alike — is the whole of what Cut 1
owes this one. Cut 1 can go to Hands without waiting for a ruling on
anything here.

Same discipline as Cut 1: facts by source read, deletes first, an authority
map, verification with a mutation per rule including a loosening, and a
subtraction estimate with real numbers.

## 18.1 Body facts

**The local watch surface today** (`src/GameCult.Networking/CultNetDatabase.cs`):

- `Watch<T>()` `:1051-1057` filters the change stream by CLR type;
  `WatchAllChanges()` `:1063-1067`; `WatchRecord<T>(key)` `:1072-1075`;
  `WatchGlobal<T>()` `:1080-1083`; `WatchByName<T>(name)` `:1088-1094`;
  `WatchByIndex<T>(alias, value)` `:1100-1107`.
- **`WatchByName` and `WatchByIndex` do not do what they are named.** Both
  read the cache's *current single winner* for the lookup and compare it to
  the change's document by `ReferenceEquals`. Combined with section 1's
  finding that the cache's index maps hold **one key per value**, this
  means `WatchByIndex<Weapon>("kind", "cannon")` observes changes to
  whichever single `Weapon` most recently claimed `cannon`, not to the
  weapons whose kind is `cannon`. It also re-runs the lookup *at change
  time*, so a change that clears the value is evaluated against the state
  after the clear. The same two lines exist again in CultMesh as
  `MatchesName` / `MatchesIndex` (`CultMesh.cs:3312-3333`).
- The rule is set-dependent even without a hop, and nothing tracks
  membership: there is no "was in the set, now is not" anywhere in the
  local path. `Reconcile` (`CultNetDatabaseSubscriptionServer.cs:251-292`)
  is the only membership tracker in the reference and it belongs to a
  server, over a delivered set, per peer.

**The CultMesh collection surface** (`src/GameCult.Mesh/CultMesh.cs`): nine
`Collection*` overloads at `:2278-2480` — three shapes (all / by name / by
index) by three sources (`CultCache` `:2278`, `:2302`, `:2331`;
`CultNetDatabase` `:2362`, `:2386`, `:2413`; `CultMeshNode` `:2442`,
`:2456`, `:2471`, which delegate to the database trio in three lines each).
`CollectionByIndex` over a cache resolves its contents as
`Optional(cache.GetByIndex<TDocument>(alias, value))` — a "collection" of
**at most one row**, whose live feed then filters by `MatchesIndex`. Three
shapes exist because the shape *is* the query language; a selection
replaces the shape axis entirely and leaves the source axis alone.

**What Cut 1 leaves in place for this cut:** `CultNetSelection` and its
validation, `CultNetSelectionEvaluator.Matches(selection, descriptor, key,
document)` for a single row, the cache's public read surface, and D6's rule
that a hop-bearing selection is set-dependent and must reconcile rather
than decide per change.

## 18.2 The shape

```
CultNetDatabase.Watch(CultNetSelection selection)
  -> Observable<CultNetSelectionChange>

CultNetSelectionChange {
  kind:      Entered | Left | Updated     // membership, not storage
  schemaId, recordKey
  document:  object?                      // present on Entered/Updated under `document` projection
  previous:  object?                      // present on Left/Updated
  cause:     Self | Edge                  // this row changed, or a row it cites / is cited by did
}
```

**Rules, each one owner:**

- **Membership, not mutation.** The stream reports a row entering the
  selection, leaving it, or changing while inside it. A write to a row that
  was outside and stays outside emits nothing. A write that changes a value
  the selection filters on emits `Entered` or `Left`, **not** `Updated` —
  which is precisely what `WatchByIndex` cannot express today.
- **`cause: Edge`** is how a hop-bearing selection stays honest: when a
  change to row B moves row A in or out, the subscriber is told about A,
  with the cause marked, and never about B unless B is itself in the set.
- **No order, no cursor, no `asOf`, no `limit`.** A live feed is not a
  page. A selection carrying `limit`, `cursor` or `descending` is **refused
  at the door** (`selection_invalid { field }`) rather than silently
  ignored: "the latest ten, live" is a different question with a different
  answer (it needs an eviction rule), and this cut does not answer it.
  `projection` is honoured; `schemas`, `keys`, `fields`, `cites` and
  `cited` are honoured.
- **The initial set is not a snapshot message.** `Watch` is local and
  in-process; it emits `Entered` for every row already matching, at
  subscribe time, under the cache's lock, before any live change. There is
  no `asOf` because there is no wire and no second reader.
- **A hop-bearing selection recomputes; a plain one decides per row.**
  Without `cites`/`cited`, membership is `Matches(selection, row)` and
  costs one row's read. With a hop, a change anywhere can move anything, so
  the watch re-evaluates the selection over the cache and diffs against the
  membership set it holds. That is O(rows) per change and it is stated
  plainly rather than hidden (Q-N).

## 18.3 Deletes first

| Path:lines | Lines | What |
|---|---:|---|
| `CultNetDatabase.cs:1080-1107` | 28 | `WatchGlobal`, `WatchByName`, `WatchByIndex` — the three that encode a query in a method name, two of them wrongly. `WatchGlobal` lowers to `keys: ["global:{schemaId}"]`, the other two to a `fields` predicate |
| `CultMesh.cs:3312-3333` | 22 | `MatchesName`, `MatchesIndex` — the same `ReferenceEquals`-against-the-winner rule, second copy |
| `CultMesh.cs:2278-2480` | 203 | the nine `Collection*` overloads; **after: three**, one per source (`CultCache`, `CultNetDatabase`, `CultMeshNode`), each taking a `CultNetSelection`, about 75 lines |
| `CultMeshSnapshots.cs` and `CultMesh.cs` local-watch predicates, audit sites #2, #4-9, #16-21 | ~240 | the remaining local watch loops and their inline predicates, about 40 after |
| **Total** | **~493** | **~115 after; net about −378** |

`Watch<T>()`, `WatchAllChanges()` and `WatchRecord<T>(key)` **stay**, as
Cut 1's v0 lowering stays: a CLR-type filter and a single-key watch are
conveniences that lower to a selection and have no rule of their own. If
they grow a rule again, W6 catches it.

## 18.4 Adds and authority

| Add | Owner | Live consumer | Protected invariant | Replaces |
|---|---|---|---|---|
| `CultNetSelectionWatch` in `GameCult.Networking` (~130 lines): the membership set, the per-change decision, the hop recompute, `CultNetSelectionChange` | `CultNetDatabase` | Mesh's three `Collection` overloads; any local consumer that watches a question | membership is tracked in one place, and a row that leaves the set is reported leaving | `WatchByName` / `WatchByIndex` / `MatchesName` / `MatchesIndex`, four spellings of one wrong rule |
| `CultNetDatabase.Watch(CultNetSelection)` (~15 lines) | `CultNetDatabase` | the above | one entry point | three named-query methods |
| Rust: **nothing in this cut** | — | — | — | no Rust caller watches locally; when one does, it gets the same shape over `Row`/`RowSet` |

- **Owner.** `CultNetSelectionWatch` owns membership and only membership.
  `CultNetSelectionEvaluator.Matches` owns whether a row is in the set —
  the watch never re-implements a predicate. `CultNetDatabase` owns the
  change stream. The cache owns values, as in Cut 1.
- **Inputs.** The change stream, the cache's rows through the public read
  surface, the validated selection. Not inputs: a clock, a transport, the
  shard log's sequences (there is no order here), a peer's authorization.
- **Outputs.** `Entered` / `Left` / `Updated` with a cause, and one typed
  refusal for a selection carrying page-shaped fields.
- **Derived state.** The membership set, and the incoming-edge index when
  the selection hops. **Demotions:** `Watch<T>` and `WatchRecord<T>` are no
  longer queries, they are lowerings; Mesh's collection *shape* is no
  longer a language, it is a `Selection`; `MatchesIndex` is not an owner of
  anything.
- **Forbidden writers.** Nothing outside `CultNetSelectionWatch` may decide
  membership; no watch path may call `GetByIndex` or `GetByName` to decide
  whether a change is interesting (that is the bug being deleted, in both
  its copies); Mesh may not filter a feed the watch answered; the watch may
  not emit `Updated` for a row that crossed the predicate boundary; the
  watch may not accept `limit`, `cursor` or `descending`.
- **Shared paths.** `Matches` under both the query and the watch — a row
  that a page would return and a row the watch reports entering are decided
  by the same function, or the two truths drift. The subscription server's
  `Reconcile` keeps its own per-peer delivered set (it answers a different
  question: what this peer has been told) and **does not** become the local
  membership tracker.
- **Deletion line.** Commit 1: `CultNetSelectionWatch` and
  `Watch(Selection)`, with the three named-query methods deleted and their
  tests rewritten. Commit 2: Mesh's nine overloads collapsed to three, both
  matchers deleted. Nothing lands with `WatchByIndex` alive beside it.

## 18.5 Verification

| # | Test | Pins | Revert kills | Loosening kills |
|---|---|---|---|---|
| W1 | `Watch_ReportsARowLeavingWhenItsFilteredValueChanges` | membership, not mutation — the bug being deleted | `Left` never emitted (today's behaviour restored: the change is simply not delivered) | `Updated` emitted instead of `Left`, so a subscriber holding a list keeps a row that no longer matches |
| W2 | `Watch_ReportsEveryRowSharingAnIndexValue_NotTheCacheIndexWinner` | the deletion of the `ReferenceEquals`-against-the-winner rule | `MatchesIndex` restored | membership decided by the winner *plus* the changed row, which passes a two-row fixture and fails a three-row one — **the fixture must carry three rows sharing the value** |
| W3 | `Watch_EmitsTheInitialSetBeforeAnyLiveChange` | the subscribe-time set | no initial emission | the initial set emitted *after* the subscription goes live, so a write racing the subscribe is delivered twice or not at all — needs a test that writes between subscribe and first emission |
| W4 | `Watch_ReportsAnEdgeCausedEntryWhenTheCiterChanges` | `cause: Edge`, D6 locally | a hop-bearing watch decided per changed row (the citee never moves) | the citee reported with `cause: Self`, which loses the subscriber's ability to tell a row's own change from a set change |
| W5 | `Watch_RefusesAPageShapedSelection` | no order, cursor or limit on a feed | the refusal dropped and the fields ignored | `limit` refused but `descending` ignored — one field's worth of silent ignoring, which is how a "harmless" ignored field becomes a bug report |
| W6 | `Watch_AndSelect_AgreeOnMembershipForTheSameSelection` (property-shaped: for each fixture selection, the set the watch holds equals the ids a page returns) | the shared `Matches` | the watch given its own predicate | the watch's copy agreeing on the plain selections and differing on the hop-bearing ones — kills only if the fixture's selections include hops, which section 6's fixture does |
| W7 | `Mesh_CollectionTakesASelectionAndFiltersNothingItself` (Mesh tests) | the collapse | an overload restored | the three surviving overloads re-filtering the feed after the watch answered |

**Negative greps:** `rg -n "WatchByIndex|WatchByName|MatchesIndex|MatchesName" src`
empty; `rg -n "GetByIndex|GetByName" src/GameCult.Mesh src/GameCult.Networking`
empty; `rg -n "CollectionByIndex|CollectionByName" src` empty.

**Mutations.** Entries in this campaign's own file, target `networking`
(killer `dotnet test tests/GameCult.Networking.Tests`) plus the Mesh tests
for W7, one per rule above with its revert and its loosening.

## 18.6 Subtraction estimate

| | Removed | Added |
|---|---:|---:|
| `src/GameCult.Networking` | 28 | ~145 |
| `src/GameCult.Mesh` | ~465 | ~115 |
| tests | ~80 rewritten | ~260 (W1-W7) |
| **net source outside tests** | | **about −233** |

This is the cut where CultMesh actually shrinks: about −350 in
`CultMesh.cs` and the snapshot facade against about +117 in the database
organ. It is also the cut that deletes a wrong answer rather than a
duplicate one — four spellings of "watch the row that currently wins a
unique-index lookup", replaced by one membership tracker over the selection
the caller actually asked about. Cut 1 pays about +585 for the vocabulary;
Cut 2 spends it.

## 18.7 Operator questions for this cut

- **Q-N. The hop-bearing watch's recompute.** **A, recompute and diff on
  every change** (recommended: obviously correct, O(rows) per change, and
  the local caches this runs over hold thousands of rows, not millions).
  **B**, maintain an incremental incoming-edge index invalidated per write
  — faster, and a second piece of derived state that can disagree with the
  cache. Take B only with a measured reason.
- **Q-O. Does `WatchRecord<T>` survive?** **A, yes, as a lowering**
  (recommended; it is one key and no rule). **B**, delete it too and make
  every caller say `keys: [k]` — cleaner, noisier at its call sites.
- **Q-P. Ordering on a feed, later.** Out of this cut by the rules above.
  If "the latest N, live" is wanted, it is a third cut with an eviction
  rule, not a field added here.

**R-AA landed, 2026-09-22** (Sonnet), `b7d77b7..99e366a` on
`cultnet/selection-cut1`. One new test file, 27 tests, 707 lines. **No
production code touched.** Score **66% → 77.62%**: killed 345→371, survived
85→57, no-coverage 75→50, over the same 428-mutant set both passes. Suite
green on Yggdrasil, 271 passed / 2 skipped.

Every one of the nine named survivor groups is killed, with two corrections to
the triage that matter more than the score.

- **`EdgesFor`: three of five killed, and two are genuinely equivalent.**
  Within one hop direction, `(From, Role)` already determines a unique
  declared edge value, so no legal selection can make two edges share anchor,
  From and Role while `To` differs. Hands could not construct one, and
  Stryker agreed across two independent runs. **Soul's triage misclassified
  `To.SchemaId` and `To.Key` as live.** They are recorded here as equivalent
  so nobody pays for them a third time.
- **A sixth mutant in the same method that nobody had named**:
  `OrderBy(pageRowIndex)`, which is R-B's *primary* key, ahead of
  `(from, role, to)`. It survived the first pass and is now killed by a test
  that forces page order and `(from, role, to)` order to disagree. The rule
  R-B exists to protect had its first component unguarded, and the triage had
  not caught that either.
- **The digest needed a golden value, not a comparison.** Beyond the named
  branch mutants, about nineteen finer-grained ones survived — deleting
  individual `AppendString` calls. A same-versus-different digest test
  structurally cannot catch these: a mutant that swaps which case gets which
  flag still produces two *different* digests, merely mislabelled. Hands
  reconstructed a golden HMAC independently from the method's doc comments
  rather than from its private helpers, which is the right way to build one,
  and it killed all but three residual coincidences.

**Line drift: every rule had moved 30–90 lines** from Soul's `52082ec`
numbers, none to a different method. Locate by name, not by number, and do not
trust the numbers in this section either.

**Four things found along the way, flagged rather than silently fixed.** Two
are test gaps; two look like real defects:

- `ReadLengthPrefixedFields`, `length <= 0`: a legitimately zero-length field
  would be wrongly refused.
- `ReferenceMembers`'s `!member.IsReference` guard: a data member could be
  treated as citeable.
- `FindCursorPosition`'s loop bound: an out-of-bounds read when paging past
  the last row with a cursor exactly at the end.
- `ComputeDigest`'s `else`-branch appends survive only because the golden
  test has both `Cites` and `Cited` non-null; a second golden with both absent
  closes them.

**Self's rulings, 2026-09-22:**

- **R-AC. The two `EdgesFor` `To` mutants are equivalent.** Recorded, not to
  be chased. A survivor correctly identified beats a test written to move a
  number.
- **R-AD. The three suspected defects above get behaviour tests first**, since
  a test that fails today is the only thing that proves they are defects
  rather than readings. The out-of-bounds read in `FindCursorPosition` is the
  one to check first: it is reachable from an attacker-supplied cursor.
- **R-AE. A second golden digest with both hops absent**, closing the three
  residual mutants rather than leaving them as folklore.

**Fix batch 4, Rust half, landed** (Sonnet), `cultnet/selection-cut1` now at
`d7f73af`, rebased onto the C# side's `99e366a`. Six commits: `c91e937`
(R-V and Rust's half of R-W), `97627dd` (R-Y), `4bfd989` and `94c32ac`
(R-AB), `8005586` (S-12, prose), `d7f73af` (a vendored-schema resync).

**217 Rust tests green** on Yggdrasil, and re-confirmed locally with
`contracts/` entirely hidden, which simulates cargo-mutants' isolated copy.
**Mutants scoped to the diff: 23 tested, 23 caught, 0 missed.** Nothing to
triage.

**S-2 is C#-only** — Rust already checked the declared target before filtering
by key, so the divergence was one-sided and the C# commits close it. No Rust
change was needed and none was made. S-1 and S-3 are closed by R-V and R-W.

**R-AB worked, and it took two fixes rather than one.** The named escape was
`schema_discovery.rs`'s `include_str!("../../../contracts/…")`. Vendoring
byte-identical copies under `packages/cultnet-rs/contracts/` fixed that and
left the crate still unmutable: `tests/selection.rs`'s fixture loader read the
same canonical tree three levels up, and 37 of 45 tests panicked under the
isolated copy. The first full-crate run failed its unmutated baseline for
exactly that reason. Vendoring the fixture too, plus a skip-when-absent guard
on the test that reads C#'s own `cs-written.json` output — which cannot be
vendored, being the other runtime's product rather than a checked-in input —
made the crate mutable for the first time.

**The vendoring is duplicate authority, and it drifted within hours.** The
rebase onto the C# side's landed R-X changed `cultnet.error.schema.json`, and
the vendored copy did not see it. **Hands' own drift test caught it** and the
copy was re-synced at `d7f73af`.

That is worth stating plainly rather than filing as a win. Twenty-two schemas
now exist twice, and the guard against the copies diverging is a test rather
than a structure. The cleaner shape — a build script staging the canonical
contracts into `OUT_DIR` — **fails for the same reason the `include_str!`
did**: cargo-mutants copies the package tree, so a `build.rs` reaching three
levels up is just as unreachable. Publishing the contracts as their own crate
would fix it properly and is out of this cut's scope. **So the duplicate is
accepted for now, on the record, with the drift test as its named guard.** It
goes in the follow-ups, not the win column.

**Hands burned an unscoped full-crate mutants run** — 2,329 mutants and a
timeout cascade across files the cut never touched — before rescoping to the
diff. It reported that unprompted and discarded the output. The ruling did say
"the cut's diff"; the run cost most of a night on a shared host.
