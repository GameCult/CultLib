# Surface nets extraction provenance

`CultGeometrySurfaceNets` is a clean implementation of surface nets over a
regular scalar grid. It is implemented fresh from the published algorithm
description; no code or lookup tables were copied from any source. `CultGeometryQuadNormals`
is likewise a fresh implementation of face-weighted vertex normal averaging.

Cited literature:

- S. F. F. Gibson, "Constrained Elastic Surface Nets: Generating Smooth
  Surfaces from Binary Segmented Data," MICCAI 1998 (MERL Technical Report
  TR99-24).
- M. Lysenko, "Smooth Voxel Terrain (Part 2)," 0fps.net, 2012.
- N. Max, "Weights for Computing Vertex Normals from Facet Normals," Journal
  of Graphics Tools 4(2), 1999.

Deliberate behavior:

- a scalar sample less than or equal to the isovalue is inside, matching
  `CultGeometryIsoSurface`'s sign convention;
- a cell's vertex is the mean of the linearly interpolated crossings of its
  own twelve edges;
- Gibson's elastic relaxation of the vertex toward its neighbors is omitted;
  the vertex stays at the unrelaxed dual-cell mean;
- one quad is emitted per crossing interior grid edge, from the (up to) four
  cells that share it; an edge whose four surrounding cells do not all exist
  emits no quad, leaving the mesh open at the field boundary;
- quad winding is oriented outward: the diagonal cross product of each quad
  must have a positive dot product with the direction from the edge's inside
  endpoint to its outside endpoint;
- vertices are welded: one vertex per active cell, shared by every quad
  touching that cell;
- face-weighted normals sum each quad's diagonal cross product (unnormalized,
  so a bigger quad outweighs a sliver) per touched vertex, then normalize; a
  vertex touched only by zero-area quads gets a zero normal rather than NaN,
  matching `CultGeometryIsoSurface`'s degenerate-normal rule.
