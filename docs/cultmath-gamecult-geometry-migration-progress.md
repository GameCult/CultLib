# CultMath and GameCult.Geometry Migration Progress

Plan: `docs/cultmath-gamecult-geometry-migration-plan.md` (locked)

Started: 2026-07-22

Status: Stage 0 complete; Stage 1 in progress

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
| 1. Destination/dependency/formatters/SoA | In progress | Resolver ownership and exact unmanaged-column support are the first cuts |
| 2. Primitive deletion and v2 schemas | Pending | Requires intact CultMath vector serialization and SoA |
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

## Next Actions

1. Implement the owner-assembly MessagePack resolver seam and its isolation tests.
2. Establish the Geometry -> CultMath dependency and exact vector formatter
   fixtures.
3. Cut the scalar-only SoA whitelist so intact unmanaged vectors can exist as
   columns before v2 document fields adopt them.
