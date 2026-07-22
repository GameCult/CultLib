# CultMath and GameCult.Geometry Migration Progress

Plan: `docs/cultmath-gamecult-geometry-migration-plan.md` (locked)

Started: 2026-07-22

Status: Stages 0-7 complete for source ownership, immutable Git/UPM delivery,
consumer cutover, and verification. NuGet and Cargo registry mirrors remain an
explicit credential-bound publication follow-up; release artifacts are public.

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
| 2. Primitive deletion and v2 schemas | Complete | C# and Geometry-owned Rust emit identical v2 bytes and record keys for all four roots |
| 3. Planetary transfer | Complete | Managed, HLSL, CMPT, Unity, web, tool, docs, tests, and package authority moved; CultMath owners deleted |
| 4. Mine `vg-csg` | Complete | Geometry-owned Rust kernel passes correctness, parity, performance, formatting, and warning-denied lint gates |
| 5. CultCache/CultNet/CultMesh integration | Complete | One provider commit path persists, watches, replicates, and probes typed geometry state |
| 6. Consumer cutover/release | Complete | Fensalir and Aetheria green; immutable Git/UPM tags and public release artifacts verified |
| 7. Delete obsolete authorities/audit | Complete | VibeGeometry workspace authority archived; tracked negative audit is clean apart from retained provenance/witnesses |

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
- `packages/gamecult-geometry-rs` is the Geometry-owned Rust v2 destination.
  It has no VibeGeometry, `vg-csg`, `bevy_math`, CultCache, or CultNet
  dependency and preserves the MIT provenance of the mined `vg-csg` contract.
- C# and Rust pin identical exact MessagePack bytes and record keys for domain,
  build request, selected cut, and chunk roots. C# decodes the Rust witnesses
  and re-emits the same bytes. The Geometry suite passes 24/24; Rust passes 4/4,
  doc tests, rustfmt, and warning-denied clippy.
- The parity pass exposed an old split identity: C# request scalar floats used
  round-trip decimal text while `vg-csg` used IEEE-bit hex. v2 deliberately
  standardizes every float on IEEE bits. The request key therefore changes at
  the v2 boundary; signed zero, a chosen NaN payload, and infinity are pinned
  across MessagePack so this cannot quietly diverge again.

### Stage 3 receipts

- Thirteen planetary/erosion C# sources and thirteen xUnit suites now live
  under GameCult.Geometry; `CultMath.rect`, its factories, and all original
  planetary sources/tests are deleted. Destination planetary tests pass 41/41;
  remaining CultMath tests pass 28/28 for both target frameworks.
- Geometry owns five HLSL files behind `GameCult.Geometry.hlsl`; public geometry
  symbols use `gamecult_geometry_*`. CultMath's umbrella and NuGet candidate
  contain only numeric `CultMath.hlsl`. A DXC SPIR-V smoke compiled the joined
  umbrella successfully to a 1,428-byte module.
- The CMPT v1 tool and TypeScript decoder moved intact. The end-to-end fixture
  is exactly 1,221 bytes with SHA-256
  `8b2bc46e4123b8ac8936b43f8d08a34e3f2e74f38d0bb3f101cf3728cd201962`;
  the C# emitter and TypeScript decoder pass together.
- Unity adapters retain their original `.meta` GUIDs; the Planetary Viewer uses
  the Geometry umbrella and owned shader symbols. The staged UPM artifact has
  one owned DLL, both adapters, the sample, all five shaders, and explicit
  MIT/MPL license and provenance documents.
- Stale candidate cache was made impossible by rotating CultMath and Geometry
  to `0.1.0-geometry-migration.2`. CultMath's candidate contains only its DLLs,
  README, and numeric shader. Geometry's candidate declares the exact CultMath
  `.2` dependency and contains all Geometry shaders/docs/notices under a
  mixed-license file declaration rather than the inherited blanket MIT claim.
- Full Geometry tests pass 27/27 after the joined shader/Unity integration;
  planetary tests pass 41/41. Negative searches find obsolete CultMath geometry
  names only inside tests that assert their absence or ignored generated output.

### Stage 4 receipts

- `packages/gamecult-geometry-rs` now owns the mined convex splitting, ordered
  brush CSG, tree lowering, dirty frontiers, prefix checkpoints, domain/LOD
  selection, feature lowering, and triangle/collider assembly kernels. No build
  or runtime dependency points at VibeGeometry or `vg-csg`.
- The public `GeometryBrushAssembler` facade accepts Geometry-owned
  `Float3` values and returns `GeometryTriangleMesh`; `bevy_math` remains a
  private implementation dependency and is not re-exported.
- The duplicate quarry persistence authority (`DomainSpecDocument`,
  `DomainNodeDocument`, `FeatureClaimDocument`, and its local schema version)
  was deleted. Private specs are in-memory builders; the v2 `Geometry*`
  documents remain the sole wire authority.
- Nine RealtimeCSG behavioral fixtures and a bounded 512-distant-cutter
  performance fixture moved under Geometry ownership. `cargo test --locked`
  passes 67/67 plus doc tests; rustfmt and warning-denied Clippy pass.
- The authority transfer commits are CultLib `756f5a33`, CultMath `6a16260`,
  and the post-transfer label cleanup `99c893bd`. The Rust absorption commit is
  CultLib `6b44f969`.

### Stage 5 receipts

- `CultGeometryWorkerProvider` owns the build invocation and output commit
  sequence. Direct calls and the `gamecult.geometry.worker.build` CultMesh
  operation share `BuildAsync`; compute is injected through the persistence-free
  `ICultGeometryBuildPipeline` port.
- Domain/request reads and selected-cut, artifact, and observational worker-state
  writes use `CultNetDatabase` typed APIs. Watches and raw MessagePack envelopes
  are consequences of that commit path rather than parallel publishers.
- The development probe derives owner, v2 schema, request/cut keys, selected
  nodes, artifact identities, content hashes, and served assembly version from
  persisted documents. Mutating worker display state cannot overwrite geometry
  outputs or forge the served version.
- Worker state registers through the generated CultDocument registry. Focused
  provider tests pass 4/4 and the full GameCult.Geometry suite passes 31/31.
- Rust raw-envelope duplication was deliberately omitted: CultNet owns that
  protocol. Existing C#/Rust exact v2 document bytes and record keys remain the
  cross-runtime geometry witness.

### Stage 6 receipts

- Coordinated local candidates use
  `scripts/build-geometry-candidate.ps1`. CultMath `.2` is primitives-only;
  Geometry `.2` contains its managed assembly, notices/docs, and all five owned
  shaders with exact support-package and CultMath candidate dependencies. A
  clean net10 consumer restored and built; the Unity package staged one owned
  assembly plus adapters, sample, and shaders. The Rust crate passed an
  independent `cargo package --allow-dirty --no-verify` dry run.
- Fensalir cutover commit `42778c1` adds direct Geometry dependencies, migrates
  planetary C# consumers, compiler include roots, shaders, and parity harnesses
  while retaining CultMath for numeric primitives. The solution builds with no
  warnings/errors; fractal tests pass 170/170, erosion parity 1/1, topology
  parity 19/19, and planetary page tests 16/16.
- Aetheria's coordinated cutover ends at commit `3ec6222f`. It replaces
  `CultVec*` fixtures with CultMath vectors under an exact legacy MessagePack
  byte witness, moves spatial rectangles to `GameCult.Geometry.CultRect`, and
  pins CultMath, CultLib, and Geometry to immutable Git/UPM tags. Geometry is an
  explicit Unity testable dependency. The Freeze build and State verifier pass;
  the verifier fingerprint is
  `664afd7e...48b`, and the legacy float2/float3 bytes remain exact.
- A disposable Aetheria import using only the published tags passed the owned
  EditMode suite 3/3: primitive ownership, CultMath visibility, and planetary
  Unity mesh projection. The resolved commits are CultMath `b43cf49`, Geometry
  and CultLib `b65619ff`, and Eve `e5556c3`. No compatibility compiler errors
  remain. Normal Unity project generation confirmed the core packages are
  precompiled and require no generated CultMath or Geometry core project.
  Aetheria commit `9c983a5e` deletes the stale generated `CultMath.csproj` and
  its solution/project references; generated project metadata contains no
  deleted `rect.cs` or planetary source paths.
- Legacy D3DCompiler exposed that macro-expanded HLSL includes were not
  portable. CultLib commit `cdeea474` gives the Geometry umbrella direct default
  and Unity include branches; Geometry shader ownership tests pass 3/3 and the
  downstream D3D12 corpus passes.
- The coordinated release body is CultLib commit `b65619ff`, merging Geometry
  ownership with the CultMesh reliability line. Caching passes 36/36, Mesh
  210/210, and Geometry 31/31. Its Unity package contains 24 managed assemblies,
  owned binaries at file version `1.0.44.0`, the resolver declaration and body
  read-lease API, and the native QUIC payload.
- CultMath `0.1.0` and Geometry `0.1.0` are published as immutable Git/UPM tags.
  Public GitHub releases carry the NuGet and Cargo artifacts at
  `https://github.com/GameCult/CultMath/releases/tag/cultmath-v0.1.0` and
  `https://github.com/GameCult/CultLib/releases/tag/gamecult-geometry-v0.1.0`.
  The CultMath NuGet SHA-256 is `90dc30c0...d070`; Geometry NuGet is
  `fdce63a3...6835`; the Rust crates are `a346dca1...d68` and
  `ac0c45cb...9e0e` respectively.
- NuGet and Cargo registry mirroring was not attempted: the authenticated GitHub
  token lacks package scopes, no NuGet API key or Cargo registry token exists,
  and GitHub Packages is not a Cargo registry. This is a distribution mirror,
  not surviving source or package authority; the exact public artifacts and
  immutable consumer paths above are the completed release contract.

### Stage 7 receipts

- Tracked negative audits across CultLib, CultMath, Fensalir, Aetheria, and
  VibeGeometry find no live `CultVec*`, `CultMath.rect`, CultMath planetary
  implementation, old planetary shader vocabulary, geometry v1 writer, or
  VibeGeometry/`vg-csg` build dependency. Frozen fixtures, source attribution,
  and negative ownership tests remain deliberately.
- VibeGeometry commit `bc9604a` removes its tracked Cargo workspace, lockfile,
  submodule declaration/gitlink, scratch/build wiring, and live mission claims.
  Its remaining documents point to source witness `8f070f4f` and Geometry
  absorption commit `6b44f969`; repository history preserves provenance.
- CultLib commits `8a7eee71` and `b9f20a64` remove manual-registry guidance and
  update the root package map. Generated CultDocument metadata remains the only
  registration owner.

## Publication Follow-up

- Mirror the already-published NuGet artifacts to the chosen package feed when
  a credential with package write scope is available.
- Mirror the two Rust crates to crates.io or another real Cargo registry when a
  registry token and registry choice are available.
- These mirrors must publish the recorded bytes. Rebuilding them would create a
  different release and requires a new version.
