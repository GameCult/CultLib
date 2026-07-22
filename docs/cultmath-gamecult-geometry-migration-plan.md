# CultMath and GameCult.Geometry Staged Migration Plan

Status: proposed for execution

Date: 2026-07-22

## Decision

`GameCult.Geometry` is the sole owner of geometric meaning and geometry
machinery. It builds on CultMath numeric primitives and participates in
CultCache, CultNet, and CultMesh through the normal CultLib integration paths.

CultMath owns the HLSL-shaped numeric substrate: scalar operations, vectors,
quaternions, and general numeric kernels that have no spatial domain authority.
It does not own regions, topology, projections, surfaces, planetary fields,
patches, geometry residency, meshes, or geometry workflow state.

VibeGeometry and `vg-csg` are sources to mine. They are not surviving owners or
dependencies.

Source transfer does not erase provenance. CultMath is MPL-2.0 and CultLib is
MIT-licensed; moved files require an explicit licensing decision and preserved
notices before they enter a differently licensed package.

## Objective

Produce one coherent geometry stack in which:

- consumers use `CultMath.float2`, `float3`, and `float4` as the canonical
  floating-point vector primitives;
- `GameCult.Geometry` owns reusable geometry, planetary geometry, CSG, mesh
  production, spatial queries, and the typed documents that coordinate those
  systems;
- CultMath no longer publishes planetary or other geometry-owned APIs;
- `CultVec2` and `CultVec3` no longer exist;
- VibeGeometry can be archived after its useful implementation, evidence, and
  doctrine have been absorbed;
- persisted geometry remains readable through an explicit migration path when
  a live persisted corpus requires one.

## Authority Map

| Organ | Owns | Does not own |
| --- | --- | --- |
| CultMath | `float2`, `float3`, `float4`, integer/double/bool vector primitives, quaternion algebra, scalar and component-wise intrinsics, coordinate-agnostic numeric kernels, CPU/HLSL parity for those primitives | Rectangles, spheres, topology, projections, planetary fields, geometry pages, meshes, CSG, persistence schemas, worker commands |
| GameCult.Geometry | Spatial value types built from CultMath primitives, spatial queries, topology and projection, planetary field and page machinery, mesh and CSG implementation, geometry schema documents, stable geometry identities, geometry-specific HLSL and cross-runtime fixtures | Competing vector types, generic storage/network ownership, application gameplay consequences |
| CultCache/CultNet/CultMesh | Typed persistence, schema resolution, transport, discovery, publication, commands, and interface projection | Geometry semantics or geometry algorithm ownership |
| VibeGeometry / `vg-csg` | Nothing after migration | Any live runtime, schema, package, or compatibility authority |

Authority sentence:

> CultMath owns how numeric primitives behave; GameCult.Geometry owns what those
> primitives mean in space.

## Current Mechanism

The live body is split across three places:

1. CultMath publishes the canonical HLSL-shaped vector types, but also contains
   `rect`, planetary topology/projection/field/page/residency systems, spherical
   erosion, planetary HLSL, Unity adapters, web fixtures, and planetary tools.
2. `GameCult.Geometry` contains duplicate `CultVec2` and `CultVec3` values,
   geometric primitives, and four CultCache document roots. Its document
   members still encode many vectors, bounds, transforms, vertices, normals,
   and UVs as anonymous `float[]` values.
3. VibeGeometry wraps the `vg-csg` Rust implementation for domain-tree intent,
   LOD-aware selected cuts, CSG lowering, and triangle/collider output.

CultCache's current managed SoA projection only admits primitive scalar members.
It neither preserves arbitrary unmanaged structs as columns nor recursively
splits them. A `float2` or `float3` member is therefore omitted from the SoA
until that storage seam is repaired.

## Invariants

The migration must preserve these statements at every published boundary:

1. There is one canonical vector vocabulary: CultMath's HLSL-shaped primitive
   types.
2. Geometry algorithms and geometry state have one owner: `GameCult.Geometry`.
3. CultMath never depends on CultLib, CultCache, MessagePack, or
   `GameCult.Geometry`.
4. `GameCult.Geometry` may depend on CultMath and CultLib.
5. Persistence decoration and formatters do not contaminate CultMath primitive
   definitions.
6. A geometry schema has one active writer. Legacy readers or offline
   migrators may exist temporarily, but they cannot decide live behavior.
7. Record keys and stable fingerprints are identical across supported runtimes
   for the same semantic geometry value.
8. SoA storage preserves each authored unmanaged member as one column of its
   exact type. It never decomposes `float2`, `float3`, quaternion, or another
   value type by reflection.
9. CPU, HLSL, Unity, web, and Rust fixtures compare the same claimed layer of
   geometry behavior.
10. VibeGeometry is not retained as a wrapper, submodule, compatibility crate,
    or second source of truth.

## Target Package Shape

The exact directory names may follow existing CultLib conventions, but the
ownership should lower into these runtime surfaces:

```text
CultMath
  C# and Unity numeric primitives
  CultMath.hlsl numeric mirror
  coordinate-agnostic native numeric kernels

CultLib
  src/GameCult.Geometry/
    C# geometry API
    planetary geometry
    CultCache document contracts and formatters
    geometry-specific HLSL packaged as content
  packages/gamecult-geometry-rs/
    Rust geometry, CSG, and cross-runtime fixtures
  packages/gamecult-geometry-ts/       # only if the web runtime remains earned
    web projection/tile helpers and parity fixtures
  src/GameCult.Unity/Assets/Geometry/  # Unity-owned lowering/adapters
    mesh/page upload and presentation adapters
  Packages/org.gamecult.geometry/      # final location follows packaging audit
    Unity package depending on CultMath and CultLib
```

The runtime directories are projections of one geometry authority. They are not
independent products allowed to invent their own vector, topology, schema, or
fingerprint semantics.

Each stage is a commit/review boundary. Do not begin the next stage until the
current gate passes, and never publish a state in which both repositories claim
the same geometry authority.

## Source Classification

### Keep in CultMath

- HLSL-shaped primitive types such as `float2`, `float3`, and `float4`.
- Integer, double, and boolean vector primitives.
- Quaternion algebra.
- `math` scalar/component-wise operations and the core `CultMath.hlsl` mirror.
- Numeric batch kernels whose contracts are independent of regions, topology,
  surfaces, planets, meshes, CSG, rendering policy, and persistence.

### Move from CultMath to GameCult.Geometry

The initial move set is:

- `rect` and other spatial-region values;
- `PlanetaryTopology`;
- `PlanetaryProjection`;
- `PlanetaryQueries`;
- `PlanetaryPatch`;
- `PlanetaryPages`;
- `PlanetaryGpuPages`;
- `PlanetaryResidency`;
- `PlanetaryRadialRefinement`;
- `PlanetaryMapTileEncoding`;
- `PlanetaryField`;
- `AdvancedErosionFilter`;
- `ErosionFrequencyBands`;
- `SphericalErosion`;
- `AdvancedErosionFilter.hlsl`;
- `Planetary.hlsl`;
- spherical-erosion and planetary-radial-refinement shader bodies currently
  embedded in `CultMath.hlsl`;
- the versioned `CMPT` planetary tile format and its TypeScript decoder;
- planetary tests, docs, tools, Unity adapters, web fixtures, and samples.

`AdvancedErosionFilter` and `ErosionFrequencyBands` move with planetary
geometry. Their public contracts encode terrain-field policy, they are direct
dependencies of the planetary field definition, and their HLSL is coupled to
that field. Voronoi tone rendering and `native/cultmath-core` are outside this
migration: the native crate currently owns only that tone kernel and has no
planetary implementation. Audit those separately rather than dragging them
across this boundary because their names look spatial.

### Rebuild inside GameCult.Geometry

- Make `CultRect`, `CultCircle`, and `CultSphere` the GameCult.Geometry-owned
  spatial values, implemented over CultMath primitives, and delete
  `CultMath.rect` at cutover.
- Replace `CultVec2` and `CultVec3` with `float2` and `float3` at every typed
  boundary.
- Replace vector-shaped `float[]` document members with typed CultMath values or
  intentional packed buffers.
- Keep packed mesh buffers packed when they are genuinely bulk artifact storage;
  do not turn every vertex buffer into a forest of object wrappers.
- Put MessagePack/CultCache formatters for CultMath values in the integration
  package or generated formatter output, not on the CultMath types.

### Mine from VibeGeometry and `vg-csg`

Evaluate and extract, in dependency order:

- convex plane splitting and brush operations;
- CSG tree and ordered operation semantics;
- domain-tree DSL and lowering;
- dirty-frontier and prefix-checkpoint work;
- selected-cut and LOD planning;
- triangle/collider assembly;
- performance fixtures and parity evidence;
- useful RealtimeCSG-derived behavioral fixtures;
- runtime doctrine that still describes the adopted machine.

Rewrite extracted code under GameCult.Geometry ownership. Preserve third-party
license notices and source provenance. Do not retain a `vg-csg` forwarding
crate or a VibeGeometry submodule after consumers have moved.

## Staged Migration

### Stage 0: Freeze and Baseline

Purpose: establish the evidence needed to move authority without guessing.

Work:

- Freeze new planetary and geometry API additions in CultMath, VibeGeometry,
  and the current duplicate primitive surface.
- Do not publish an intermediate release containing duplicate live planetary
  implementations. A coordinated working branch may hold source temporarily
  for comparison, but only one owner may enter a package feed.
- Inventory public symbols, package versions, HLSL entrypoints, Unity samples,
  web fixtures, native exports, persisted schema IDs, record-key algorithms,
  and known consumers.
- Inventory exact contract constants: `FieldVersion`, enum ordinals, tile stable
  keys, `CMPT` versions and bytes, MessagePack slots, GPU buffer layouts,
  integer hashes, topology face/seam/corner rules, and shader include paths.
- Search deployed and checked-in `.cc` stores for the four
  `gamecult.geometry.*.v1` schemas.
- Record whether any external consumer relies on CultMath planetary namespaces,
  `CultVec*`, `CultMath.rect`, `vg-csg`, or the current HLSL function names.
- Capture passing baselines for CultMath tests, GameCult.Geometry tests,
  `vg-csg` tests/clippy, cross-runtime planetary fixtures, and package builds.
- Add representative fixture hashes for projection, topology, field sampling,
  patch selection, page encoding, CSG output, and geometry record keys.
- Resolve licensing and provenance for MPL-2.0 CultMath sources and any
  third-party-derived `vg-csg` behavior before copying source into CultLib.

Gate:

- Every published surface has a named consumer or is marked for deletion.
- The persisted v1 corpus is known rather than assumed.
- Baseline fixtures can be run without booting an entire daemon.

### Stage 1: Establish the Destination and Dependency Direction

Purpose: make `GameCult.Geometry` capable of receiving geometry without growing
a second primitive vocabulary.

Work:

- Add the explicit `GameCult.Geometry -> CultMath` dependency.
- Define the target namespaces and package content layout for C#, Rust, HLSL,
  Unity, and any retained web surface.
- Establish distribution before source transfer: an explicit NuGet dependency
  and a dedicated `org.gamecult.geometry` Unity package depending on
  `org.gamecult.cultmath` and `org.gamecult.cultlib`, unless package inspection
  proves an existing surface already owns that role.
- Retain `net8.0` and `netstandard2.1` targets and Unity 2021.3 compatibility.
  The geometry asmdef references CultMath; the Unity lowering assembly
  references both. CultMath's asmdef remains reference-free with
  `noEngineReferences: true`.
- Add GameCult.Geometry-owned MessagePack formatters for `float2`, `float3`,
  `float4`, and quaternion with fixed positional layouts.
- Add cross-runtime byte fixtures for those value encodings.
- Repair CultCache's SoA member discovery so any direct unmanaged member is one
  exact typed column. Add `Column<float2>` and `Column<float3>` tests and a
  negative test proving no scalar decomposition occurs.
- Ensure generated schema metadata describes the authored vector member rather
  than synthetic x/y/z members.

Gate:

- CultMath has no new dependency.
- `GameCult.Geometry` can serialize, deserialize, index, and expose intact
  CultMath vectors.
- CultCache hot-path access returns intact vector spans.
- Built NuGet and UPM artifacts contain the expected dependency metadata,
  formatters, shaders, and owned runtime assets.

### Stage 2: Replace Duplicate Primitives and Repair Geometry Schemas

Purpose: remove the duplicate vocabulary before moving larger geometry systems.

Work:

- Rewrite `CultRect`, `CultCircle`, and `CultSphere` with CultMath primitives.
- Delete `CultVec2` and `CultVec3`; do not add aliases or conversion wrappers.
- Replace typed references and opaque native-slice metadata using the obsolete
  names.
- Classify every vector-shaped `float[]` in `CultGeometryDocuments.cs`:
  - `Translation`, `SupportCenter`, `SupportSize`, `CameraPosition`,
    `FrustumMin`, `FrustumMax`, `BoundsMin`, and `BoundsMax` become `float3`;
  - `RotationXyzw` becomes quaternion;
  - mesh `Positions` and `Normals` become `float3[]`;
  - mesh `Uvs` becomes `float2[]`;
  - scalar and index buffers remain packed arrays;
  - use a named geometry value when bounds/topology carries an invariant.
- Define v2 schemas for every document whose field types change. CultCache
  includes CLR type identity in the canonical schema and must reject an in-place
  slot type mutation even when no durable v1 corpus is found.
- If live v1 documents exist, add bounded v1 reader DTOs and an offline
  v1-to-v2 migrator. Stop all v1 writers at cutover. If no live persisted corpus
  exists, omit the migrator and delete v1 runtime support, but still publish the
  corrected contract under v2 identity.
- Reject malformed legacy vectors: fixed vectors must have the exact expected
  component count, while packed mesh streams must be divisible by their element
  width before conversion.
- Recompute and cross-check stable fingerprints and record keys against Rust
  fixtures before accepting the new schemas. Preserve an old record key only
  when the typed representation reproduces the prior IEEE-bit canonicalization
  exactly; otherwise version the identity deliberately.
- Use CultMath's lowercase component names in v2 JSON inspection projections.
  JSON is not the runtime transport, and uppercase `X/Y/Z` aliases must not keep
  the deleted vector vocabulary alive. Any xenos-facing alternate casing is an
  explicit boundary projection.

Gate:

- Repository-wide searches find no `CultVec2` or `CultVec3` outside historical
  evidence.
- Repository and package searches find no `CultMath.rect`.
- No live writer emits vector-shaped anonymous arrays for semantic single
  values.
- The old schema cannot overwrite, repair, or become a second owner of v2
  state.

### Stage 3: Transfer Planetary Geometry out of CultMath

Purpose: move planetary authority as one coherent vertical slice.

Work:

- Move the classified planetary C# implementation and tests into
  `GameCult.Geometry`.
- Move `Planetary.hlsl` and rename its include ownership and public entrypoints
  to GameCult.Geometry. It may include CultMath numeric HLSL; CultMath must no
  longer include planetary geometry. Remove the current implicit planetary
  include from `CultMath.hlsl` only after consumers include the new owner.
- Move `AdvancedErosionFilter.hlsl` plus the spherical-erosion and
  planetary-radial-refinement bodies embedded in `CultMath.hlsl`. Publish one
  geometry-owned umbrella include that imports CultMath first and geometry
  organs afterward.
- Move planetary documentation, fixture tools, web tile code, Unity page/mesh
  adapters, and the planetary viewer sample to their consumer-owned runtime
  locations.
- Move ownership of the `CMPT` little-endian tile format and TypeScript decoder.
  Preserve v1 bytes exactly or introduce a new format version; never reuse a
  wire version after changing its bytes.
- Rename public namespaces and symbols to their GameCult.Geometry authority.
  Prefer a coordinated breaking release over permanent forwarding types.
- Move spherical/terrain erosion components classified as geometry. Keep only
  genuinely coordinate-agnostic numeric kernels in CultMath.
- Run CPU/HLSL and cross-runtime fixtures from the new owner before deleting
  the old files.
- Preserve exact fixtures for field versions, enum ordinals, integer hashes,
  tile keys/bytes, GPU buffer packing, MessagePack slots, topology seams and
  corners, and record hashes. Use explicit tolerances only for floating
  CPU/HLSL comparisons.
- Delete the corresponding CultMath sources, package entries, and implicit
  includes in the same authority-transfer stage after the destination passes.
  Do not defer the actual ownership cut to consumer cleanup.

Gate:

- GameCult.Geometry passes the captured planetary baseline and owns the fixture
  commands.
- CultMath packages no `Planetary.hlsl`, planetary sample, planetary tool, or
  planetary public namespace.
- No source or shader include can select an old CultMath planetary path.
- Package inspection finds no planetary API, Unity adapter, sample, tile
  decoder, documentation claim, or implicit shader include in CultMath.

### Stage 4: Mine and Absorb `vg-csg`

Purpose: preserve useful CSG machinery without preserving the defunct owner.

Work:

- Import the earned Rust implementation into the GameCult.Geometry runtime
  surface with source provenance and applicable license notices.
- Replace `bevy_math`-shaped public contracts where they conflict with the
  canonical cross-runtime primitive/layout contract. Internal use may remain
  where it protects a measured invariant and does not leak a second public
  vocabulary.
- Connect domain trees, selected cuts, build requests, and emitted artifacts to
  the GameCult.Geometry schema definitions.
- Port useful fixtures before refactoring algorithms so observable behavior has
  a witness.
- Preserve the existing Rust v1 tuple MessagePack and stable-hash fixtures as
  migration witnesses, then make the GameCult.Geometry-owned Rust package emit
  the v2 contract.
- Re-run performance fixtures and retain thresholds only where the fixture
  represents a real workload.
- Move surviving doctrine into GameCult.Geometry docs and discard historical
  instructions that would recreate VibeGeometry ownership.

Gate:

- No production build or test depends on the VibeGeometry repository or
  `vg-csg` submodule.
- CSG output satisfies the adopted correctness and performance fixtures from
  its new package.
- The Rust runtime speaks the same schema IDs, vector layouts, fingerprints,
  and record keys as C#.

### Stage 5: Integrate Geometry Through CultCache, CultNet, and CultMesh

Purpose: prove the new owner through the real typed-state path.

Work:

- Register the adopted geometry schemas and formatters through the generated
  registry path.
- Prove raw envelope interchange for geometry documents across C# and Rust,
  adding TypeScript only if an actual web consumer remains.
- Publish geometry worker state and commands through the provider-owned
  CultMesh surface.
- Ensure geometry build requests, selected cuts, chunk artifacts, and planetary
  page state share one commit/derivation path for direct and programmatic use.
- Add a development probe exposing schema version, owner, source record key,
  selected cut/page, content hash, and served package version.

Gate:

- A pipeline smoke proves typed handoff from domain input through selected cut
  or planetary page selection to a persisted and republished artifact.
- The visible/debug projection and persisted state identify the same owner and
  version.
- No ad hoc JSON or parallel status model becomes load-bearing geometry truth.

### Stage 6: Consumer Cutover and Coordinated Release

Purpose: switch consumers without maintaining two live authorities.

Release order:

1. Build an unpublished CultMath candidate that contains the stable primitive
   contract and removes geometry-owned APIs.
2. Build an unpublished GameCult.Geometry candidate against that exact CultMath
   version, containing adopted schemas, planetary geometry, and runtime packages.
3. Update and test GameCult services, Unity projects, web consumers, and tools
   against a local candidate feed.
4. Publish the CultMath and GameCult.Geometry candidates in one coordinated
   release window, with the primitives-only CultMath dependency entering the
   feed first.
5. Publish consumer updates after both owning packages are available.
6. Convert persisted v1 geometry stores, if any, and remove migration tooling
   after the retained-corpus policy is satisfied.

Work:

- Pin compatible package versions across the coordinated release.
- Update examples and developer-facing entrypoints to use
  `GameCult.Geometry` plus CultMath primitives directly.
- Cut Fensalir's Engine/Contracts public planetary types and shader includes to
  the new assembly before removing CultMath geometry. Its CPU/GPU parity tests
  are release gates.
- Add the geometry package to Aetheria/Unity, move its directly compiled
  planetary sources to the new assembly, preserve moved Unity `.meta` GUIDs,
  and regenerate Unity project files.
- Verify served package, shader, schema, and migration versions in at least one
  real consumer for each retained runtime.
- Run the preserved parity corpus through known consumers including Fensalir,
  Zyphos, Aetheria/Unity, the web tile decoder when retained, C#, Rust, and HLSL.
- Refuse mixed old/new geometry writers rather than reconciling them later.

Gate:

- Every known consumer runs on the new owner.
- No published package graph has `CultMath -> CultLib` or
  `CultMath -> GameCult.Geometry`.
- Old and new writers cannot target the same state surface.

### Stage 7: Delete Obsolete Authorities

Purpose: make the old ownership structurally unable to return.

Work:

- Confirm deletion of all CultMath planetary/geometry sources, tests, shaders,
  samples, docs, tools, web surfaces, package content entries, and Unity
  adapters; remove any residue found after consumer cutover.
- Delete obsolete GameCult.Geometry duplicate primitive code and stale schema
  paths.
- Delete VibeGeometry workspace wiring and the `vg-csg` submodule dependency;
  archive the repositories after provenance links and release tags are
  recorded.
- Remove compatibility aliases, forwarding namespaces, dual registries, and
  temporary migration flags.
- Update source indexes and repository memory so future agents retrieve the new
  authority first.

Gate:

- Negative searches and package inspection prove the old paths are absent.
- Building CultMath alone cannot access planetary or geometry APIs.
- Building GameCult.Geometry cannot access `CultVec*` or VibeGeometry.
- The old persisted schema path cannot write or override current geometry.

## Schema Migration Policy

Do not create a permanent compatibility model pre-emptively.

- If no durable v1 corpus exists, change the schema once, update fixtures, and
  publish the corrected contract as v2, then delete the old form without
  building a migrator.
- If a durable v1 corpus exists, create an offline migrator with explicit input
  and output schema IDs, content hashes, counts, and a dry-run report.
- A v1 reader may exist only inside migration/inspection tooling.
- The runtime writes only the current schema after cutover.
- Migration completes only when retained stores have receipts and no active
  service advertises or writes v1.

## Verification Matrix

| Layer | Required evidence |
| --- | --- |
| CultMath primitives | C#/HLSL parity, unmanaged layout/size checks, package inspection |
| Geometry values | Exact `float2`/`float3` field ownership, intersection/query unit tests, MessagePack byte fixtures |
| CultCache SoA | Intact vector columns, no recursive flattening, add/update/remove/snapshot behavior |
| Geometry schemas | Catalog IDs and slots, v1 inventory or migration receipts, stable record-key parity |
| Planetary geometry | Projection round trips, topology adjacency, field/normal samples, radial refinement, page encoding/residency, CPU/HLSL parity |
| CSG | Boolean/convex fixtures, dirty rebuild behavior, selected-cut correctness, triangle/collider receipts, performance fixtures |
| Cross-runtime | C#/Rust payload and fingerprint parity; web parity only for retained web consumers |
| Packaging | CultMath contains no geometry assets; GameCult.Geometry includes its owned HLSL/docs/runtime assets |
| End to end | Domain or planetary input to persisted artifact through CultMesh with visible owner/version evidence |

## Negative Completion Checks

The migration is not complete until all are true:

- `CultVec2` and `CultVec3` cannot appear in newly generated schemas or native
  slice metadata.
- CultMath cannot publish or execute planetary geometry through an old entrypoint.
- VibeGeometry and `vg-csg` cannot influence a build, runtime, fixture, or
  generated artifact.
- VibeGeometry is absent from project references, package manifests,
  submodules, CI, runtime identifiers, and authoritative documentation.
- Legacy v1 state cannot override or repair current geometry after the fact.
- CultCache cannot decompose a vector into independently owned scalar columns.
- Manual, programmatic, imported, persisted, and network-received geometry use
  the same schema and commit paths.
- Passing unit tests are accompanied by package inspection and cross-runtime
  evidence at the layer users actually consume.

## Definition of Done

The work is complete when `GameCult.Geometry` is the only geometry authority,
CultMath is a clean numeric substrate, all retained runtime projections agree on
typed layouts and stable identities, VibeGeometry has no live dependency path,
and the old owners are structurally unable to emit current geometry state.
