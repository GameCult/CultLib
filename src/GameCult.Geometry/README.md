# GameCult.Geometry

`GameCult.Geometry` defines CultCache-native documents for procedural geometry
domains, LOD build requests, selected-cut manifests, and mesh chunk artifacts.

The package is deliberately substrate-shaped:

- CultCache owns document identity, schema compatibility, references, and local
  persistence.
- CultNet can distribute these documents through the normal raw document lane.
- CultMesh can use them as worker-visible shared state for simulation and
  geometry pipelines.
- JSON is not a runtime transport here. It may publish schema descriptions at
  the xeno boundary, but the live machine eats and emits typed CultCache
  documents.

