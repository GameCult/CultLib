# Isosurface extraction provenance

`CultGeometryIsoSurface` is a clean implementation of marching tetrahedra over
a regular scalar grid. The implementation follows the tetrahedral case shape
described by Peter Shirley and Allan Tuchman, “A Polygonal Approximation to
Direct Scalar Volume Rendering,” Computer Graphics 24(5), 1990,
doi:10.1145/99308.99322.

The DELVE/HOLD port was behaviorally motivated by these unversioned local Unity
sources:

- `D:\WIP4\Projects\VoxelTerrain\Assets\Scripts\Procedural\GenerateTerrain.cs`
- `D:\WIP4\Projects\VoxelTerrain\Assets\Plugins\MarchingCubes.cs`

The source directory is not a Git checkout and the plugin carries no license or
author notice. Its code and lookup tables were therefore not copied. The
CultGeometry implementation uses a fresh six-tetrahedra decomposition, explicit
inside/outside classification, interpolation, winding, and flat-normal output.

Deliberate behavior retained:

- scalar samples less than or equal to the isovalue are inside;
- intersections are linearly interpolated along cell edges;
- vertices are emitted per triangle rather than welded;
- output is engine-neutral positions, normals, indices, and material slots.

Deliberate changes:

- no Unity mesh, physics, coroutine, navigation, or random dependency;
- no mutable global target, mode, or winding state;
- outward winding is derived from inside-to-outside direction;
- invalid fields and cell sizes fail at the public boundary.
