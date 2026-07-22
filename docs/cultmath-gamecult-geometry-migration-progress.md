# CultMath and GameCult.Geometry Migration Progress

Plan: `docs/cultmath-gamecult-geometry-migration-plan.md` (locked)

Started: 2026-07-22

Status: Stages 0-1 complete; Stage 2 in progress

## Objective

Implement the locked migration so `GameCult.Geometry` becomes the sole owner of
geometry, planetary geometry, CSG, geometry schemas, and runtime projections,
while CultMath remains the HLSL-shaped numeric substrate and VibeGeometry loses
all live authority.

## Execution Authority Map

- Owner: `GameCult.Geometry` owns geometry decisions and geometry state.
- Inputs: CultMath numeric primitives, mined `vg-csg` implementation/evidence,
  current geometry v1 documents, and named downstream consumer contracts.
- Outputs: GameCult.Geometry C#/Rust/HLSL/Unity/web surfaces, v2 typed schemas,
  persisted artifacts, and CultMesh projections.
- Derived state: JSON inspection, UI/debug projections, caches, migration
  receipts, and compatibility reads are non-authoritative.
- Forbidden writers: CultMath planetary APIs, v1 geometry runtime writers,
  `CultVec*`, VibeGeometry/`vg-csg`, legacy shader entrypoints, and anonymous
  fixed-vector arrays.
- Shared paths: direct API calls, persisted reloads, imports, network envelopes,
  worker commands, Unity consumption, web tile reads, and Rust emitters must use
  the same current schema/layout/fingerprint rules.
- Deletion line: `CultVec2`, `CultVec3`, `CultMath.rect`, CultMath planetary and
  erosion ownership, v1 writers, VibeGeometry wiring/submodule authority, and
  stale package/docs/schema paths.

Demotions:

- Geometry v1 DTOs are no longer owners; when a durable corpus requires them,
  they are bounded read-only migration inputs to v2.
- JSON is no longer an owner; it is an inspection or xenos-boundary projection.
- VibeGeometry is no longer an owner; it is a provenance-bearing source quarry.
- CultMath planetary state is no longer an owner; it is source material pending
  transfer and deletion in the same unpublished migration cut.

## Worktree Safety

- CultLib began with unrelated modified `.voidbot`, CultMesh, CultCache TS,
  CultNet Rust, README, and test files plus scratch PID files. These changes are
  outside the migration unless an exact overlap is required and reviewed.
- CultMath began clean on `codex/rust-cultmath-core` at `cb481b4`.
- VibeGeometry began clean on `main` at `85ac60a`.
- The locked plan is not edited during implementation.

## Stage Ledger

| Stage | State | Evidence / next gate |
| --- | --- | --- |
| 0. Freeze and baseline | Complete | Source bodies, contracts, consumers, licenses, fixtures, and focused baselines mapped |
| 1. Destination/dependency/formatters/SoA | Complete | NuGet and dedicated UPM artifacts prove dependency direction and owned vector integration |
| 2. Primitive deletion and v2 schemas | In progress | Delete duplicate vectors first, then publish corrected v2 contracts |
| 3. Planetary transfer | Pending | Requires destination packaging and preserved parity corpus |
| 4. Mine `vg-csg` | Pending | Requires v1 fixture capture and target Rust package seam |
| 5. CultCache/CultNet/CultMesh integration | Pending | Requires current schemas and runtime packages |
| 6. Consumer cutover/release | Pending | Requires local candidate feed and downstream passes |
| 7. Delete obsolete authorities/audit | Pending | Requires every prior stage gate |

## Verification Receipts

Receipts will be appended here with command, repository, commit, result, and
relevant artifact/version. A passing narrow test is not accepted as evidence for
a broader stage gate.

### Stage 0 receipts

- CultLib: `dotnet test tests/GameCult.Geometry.Tests/GameCult.Geometry.Tests.csproj --no-build --no-restore --disable-build-servers` passed 13/13. A full rebuild remains a Stage 1 gate; shared .NET build contention made attached build attempts exceed 60 seconds.
- CultMath: `dotnet test CultMath.slnx --no-restore` passed 72/72.
- CultMath native: `cargo test --locked` passed 1/1.
- CultMath web tile unit: `node --test web/planetary-tile.test.mjs` passed 1/1.
- CultMath pack and the combined web/C# cross-runtime script exceeded the
  baseline time window and remain required package-transfer gates.
- VibeGeometry: `cargo test` passed 56 unit and 9 integration tests; doc tests
  passed; `cargo clippy --all-targets --all-features` passed.
- Authoritative Rust quarry: VibeGeometry-pinned `vg-csg` submodule `a3197f4`.
  Standalone `E:\Projects\vg-csg` is stale and excluded.
- v1 witnesses: current C#/Rust slots and hash algorithms match; Rust expected
  record keys include domain `02c9...ed95` and chunk `b94d...21af5`. Exact
  checked-in MessagePack byte witnesses still need to be generated before v2.
- Persisted corpus: targeted file inventory covered CultLib, CultMath,
  VibeGeometry, Fensalir, and Aetheria. CultLib contains only the two checked-in
  `vg-csg-ragnarok` payload fixtures. Binary scanning of Aetheria `.cc`,
  `.msgpack`, and `.mpack` artifacts found no `gamecult.geometry.*.v1` catalog
  identity. Indexed-source retrieval found definitions/tests/docs but no durable
  geometry store. Execution therefore uses v2 plus retained test witnesses,
  with no live v1 runtime migrator.

### Stage 1 receipts

- CultCache MessagePack now resolves external member values through a
  document-owner assembly declaration. Resolver precedence is core CultCache,
  owner-declared resolvers, then MessagePack standard fallback; the options are
  cached per owner assembly and generated document codecs use that same path.
  CultCache does not reference Geometry or CultMath.
- CultCache SoA discovery now admits direct reference-free value types as one
  exact typed column. It rejects managed-reference and byref-like values and
  does not synthesize component columns from nested struct fields.
- `dotnet test tests/GameCult.Caching.Tests/GameCult.Caching.Tests.csproj
  --no-restore --filter
  "FullyQualifiedName~DocumentAssemblyResolverTests|FullyQualifiedName~SoaValueColumnTests"
  --verbosity minimal -m:1 -nodeReuse:false` passed 4/4 after a full rebuild.
- The initial SoA reflection probe used `Type.IsFunctionPointer`, which is not
  available on the library's `netstandard2.1` target. It was removed rather
  than papered over; structural managed-reference inspection is the owning
  eligibility rule.
- CultMath candidate `0.1.0-geometry-migration.1` was packed from commit
  `cb481b4c75444426a693781a0e5b35ade08a8938` with build servers disabled. The
  123,307-byte package SHA-256 is
  `07C0CE79453AF9BA29910809F8779916E7B8F92DCA7ACEB5E2B8F9D3EBED3E93`;
  a local-feed consumer restored, compiled, and executed `CultMath.float3`.
- GameCult.Geometry consumes that candidate as an explicit NuGet dependency;
  its sibling `.tools/local-feed` is only an optional restore source, not a
  project-reference authority. Geometry owns strict positional formatters for
  `float2`, `float3`, `float4`, and `quaternion`.
- `dotnet test tests/GameCult.Geometry.Tests/GameCult.Geometry.Tests.csproj
  --no-restore --filter FullyQualifiedName~CultMathValueIntegrationTests
  --verbosity minimal --disable-build-servers -p:UseSharedCompilation=false
  -m:1`
  passed 6/6. Evidence covers exact bytes, round trips, malformed-width
  rejection, actual `Column<float2>`/`Column<float3>` access, and absence of
  synthetic component columns.
- GameCult.Geometry candidate `0.1.0-geometry-migration.1` packed successfully;
  its nuspec declares exact candidate dependencies on CultMath, CultCache, and
  CultCache.MessagePack. The dedicated UPM artifact remains the open Stage 1
  gate.
- Authoritative `vg-csg` commit `8f070f4` now pins exact v1 MessagePack bytes
  for representative domain/chunk tuples plus complete domain, request,
  selected-cut, and chunk record keys. Focused Rust tests passed 4/4; formatting
  and clippy passed. VibeGeometry commit `82b56db` advances its submodule to
  that witness commit. Both migration branches are pushed.
- `scripts/build-geometry-unity-package.ps1` staged
  `org.gamecult.geometry@0.1.0` successfully. Its manifest depends explicitly
  on `org.gamecult.cultmath@0.1.0` and `org.gamecult.cultlib@1.0.7`; the package
  contains only its owned `GameCult.Geometry.dll`/symbols plus its asmdef and
  documentation. It neither duplicates CultMath nor buries Geometry inside the
  CultLib Unity package.

## Decisions and Deviations

- Licensing: no relicensing is assumed. Moved CultMath files retain MPL-2.0
  notices and provenance; the GameCult.Geometry package will carry explicit
  mixed-license package documentation rather than falsely stamping those files
  MIT. `AdvancedErosionFilter` source and shader notices remain verbatim.
- Serialization: Geometry-owned external value formatters require a declarative
  per-document-assembly resolver seam in `GameCult.Caching.MessagePack`.
  CultMath remains annotation-free and core caching does not reference Geometry.
- v1 persistence: no durable corpus was found in the named repositories and
  artifacts. v1 remains fixture evidence only; it will not survive as a runtime
  writer or general compatibility model.
- No deviations from the locked target architecture.
- Stage 2 versions all four root geometry documents as one v2 cohort. Domain's
  nested CLR layout changes are invisible to the current root-only schema
  fingerprint, and leaving SelectedCut as the lone live v1 writer would retain
  a split cohort even though its direct fields are unchanged.
- Typed vectors preserve existing record keys by flattening components in the
  same authored order into the existing IEEE-754 bit canonicalization. Wire
  payloads still change—especially typed mesh arrays—so byte fixtures and v2
  identities remain mandatory.

### Stage 2 receipts

- `CultVec2` and `CultVec3` definitions were deleted; `CultRect`, `CultCircle`,
  and `CultSphere` now expose CultMath `float2`/`float3` directly. No alias or
  conversion owner remains.
- `dotnet test tests/GameCult.Geometry.Tests/GameCult.Geometry.Tests.csproj
  --no-restore --verbosity minimal --disable-build-servers
  -p:UseSharedCompilation=false -m:1` passed 19/19 for the primitive cut.
- CultMesh native-slice metadata fixtures now name `CultMath.float2` and
  `CultMath.float3` while retaining 8/12-byte element widths. Only those three
  overlapping lines were staged from the already-dirty TypeScript test file.
- All four root geometry schemas now form a v2 cohort with their original slot
  numbers. Semantic single vectors use `float3`/`quaternion`; mesh vertices,
  normals, and UVs use `float3[]`/`float2[]`. The runtime contains no v1 DTO,
  reader, writer, or conversion authority.
- Live v2 semantic SchemaIds are pinned as domain `sha256:e335...421d`, build
  request `sha256:2de3...21d3`, selected cut `sha256:8644...9ff8`, and chunk
  artifact `sha256:2d28...6156`. Full values live in the executable tests.
- Typed stable fingerprints flatten components into the prior ordered IEEE-bit
  stream. Domain/chunk round trips preserve record identity, while the checked-
  in flat-mesh v1 payload is retained as negative evidence and rejected by the
  v2 decoder. The Geometry suite passes 19/19 after the cohort cut.

## Next Actions

1. Rewrite Geometry primitives over CultMath and delete `CultVec2`/`CultVec3`
   without aliases or forwarding types.
2. Publish v2 geometry documents with typed vectors/quaternion and deliberate
   stable-key fixtures; keep v1 only as test evidence.
3. Run repository/package negative searches before beginning planetary transfer.
