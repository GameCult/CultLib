# Planetary Field Runtime

CultMath owns the deterministic, renderer-independent planetary field. The same
definition can be queried by a CPU simulation, baked into residual pages,
sampled through a map projection, or evaluated by HLSL. Render resources and
gameplay interpretation remain consumer responsibilities.

## Authority

- `PlanetaryFieldDefinition` identifies radius, seed, erosion parameters, and a
  version that includes the consumer-owned base-field version.
- `IPlanetaryBaseField` supplies deterministic application-authored base
  evidence without making CultMath depend on an application.
- `PlanetaryQueryScale` makes the physical footprint and accepted unresolved
  error explicit. There is deliberately no scale-free authoritative
  `HeightAt` call.
- `PlanetarySurfaceSample` carries displacement, tangent gradient, normal,
  slope, ridge/gully evidence, resolved wavelength, and unresolved bound.
- pages, residency, patches, and map tiles are disposable lowerings of that
  contract.

The C# surface targets .NET and Unity-compatible `netstandard2.1`. The matching
HLSL lives in `shaders/Planetary.hlsl` and is included by `CultMath.hlsl`.

## Point and batch queries

```csharp
var erosion = AdvancedErosionParameters.Default with
{
    Scale = radius * 0.075f,
    Strength = 0.12f,
    Octaves = 7,
};
var field = PlanetaryFieldDefinition.Create(
    baseFieldVersion: 17,
    radius,
    seed: 42,
    erosion);
var scale = PlanetaryQueryScale.AtFootprint(
    footprintMeters: 10,
    maximumUnresolvedHeight: 0.5f);

PlanetaryBaseFieldSample basis = source.Sample(direction);
PlanetarySurfaceSample sample = PlanetaryField.Sample(
    field, direction, basis, scale);
```

`PlanetaryField.SampleBatch` writes into a caller-owned span. `SamplePosition`
accepts `double3`, reduces the astronomical position to a stable unit direction,
and then evaluates the same float field contract.

Higher-level CPU queries include bounded ray intersection, segment clearance,
great-circle profiles, and region summaries. Their answers remain field queries;
only a consuming simulation can turn them into committed game consequences.

## Cube-sphere topology and pages

`PlanetaryTopology` owns the tangent/QSC cube-sphere mapping, inverse face
coordinates, tile selection, local page coordinates, and radial-graph surface
normal. `PlanetaryPageSampling` owns bordered directions, nominal spacing,
bilinear sampling, and summaries.

`PlanetaryPageBaker` evaluates a root page at its physical sample spacing. A
child stores `child filtered field - parent filtered field`. A
`PlanetaryPageSetSampling` query adds every containing ancestor contribution
and applies transition weights only to residuals.

`PlanetaryLodSelector` selects six roots plus one camera-facing ancestor chain
from the unresolved-height error. `PlanetaryResidualResidency` owns arrival and
departure presentation weights, content and presentation versions, and nothing
about field truth.

## Patches

`PlanetaryPatch.CreateFace` and `CreateCubeSphere` produce renderer-neutral
coarse meshes from the canonical topology. HLSL calls
`cultmath_planetary_face_direction` for procedural vertices and
`cultmath_planetary_radial_refinement_step` for bounded pixel refinement.

Renderers still own cameras, clip space, depth, materials, buffers, dispatch,
and draw submission.

`PlanetaryGpuPageBuilder` owns the renderer-neutral page input and metadata
payload. Host adapters may convert its `float4` values to native vector types,
but do not recalculate directions, spacing, addressing, or transition state.

The optional `CultMath.Unity` assembly converts a canonical face patch into a
Unity `Mesh` and converts the shared GPU payload into Unity-native structs. It
owns no field parameters, page selection, or simulation state. The package's
Planetary Field Viewer sample renders the shared HLSL field and reports matching
CPU queries. Unity render code remains responsible for allocating/binding
graphics buffers and dispatching application-specific base-field entry points.
The host Unity project must set `-langversion:latest` in `Assets/csc.rsp`.

## Maps

`PlanetaryProjection` supplies forward and inverse transforms for:

- equirectangular;
- Web Mercator with an explicit polar cutoff;
- Equal Earth;
- orthographic;
- azimuthal equidistant and equal-area;
- cube atlas;
- local tangent/gnomonic views.

`PlanetaryMapTileBaker` maps each valid tile sample through the inverse
projection and canonical field query. `PlanetaryMapTileKey` includes field,
projection, layer, layout, and query-scale identity. Palettes, contours, labels,
and strategic overlays do not enter CultMath.

`PlanetaryMapTileEncoding` writes the resulting evidence as a versioned
little-endian `CMPT` binary asset. `web/planetary-tile.ts` decodes that exact
format into browser-native values without defining another terrain generator.
Typed CultMesh documents should carry the tile key and asset reference; the
binary payload is derived CDN/cache data, not committed game state.

## Numeric contract

Integer hashes and published field versions are exact. Basic arithmetic follows
CultMath's matched C#/HLSL semantics. Hardware transcendental functions such as
`atan` and `tan` use measured tolerances rather than fictional bit identity.
Every filtered terrain query carries an unresolved-height bound so consumers can
reject insufficient fidelity explicitly.

Current D3D12 parity evidence covers the advanced erosion kernel, QSC topology,
and forward/inverse transforms for every supported map projection, including
center and scale parameters. Production page and patch shaders use the common
functions and retain their seam, residual, summary, and radial-hit integration
probes in Fensalir.
