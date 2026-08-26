# GameCult.Geometry

The project is known as **CultGeometry**. `GameCult.Geometry` is its clean .NET
package and namespace identity.

`GameCult.Geometry` owns reusable procedural geometry algorithms together with
the CultCache-native documents that carry their domains, build requests,
selected cuts, and mesh chunk artifacts.

It is the schema authority for the shared `CultGeometry*` records.
`GameCult.Geometry.Csg` owns CSG production behavior and proves wire parity
against those records; it does not independently evolve their identities.

Its first engine-neutral algorithm is `CultGeometryIsoSurface.Extract`, a
marching-tetrahedra extractor over regular scalar grids. It uses CultMath for
numeric primitives and emits `CultGeometryTriangleMesh`; Godot, Unity, native
workers, and other consumers adapt that neutral result into their own runtime
objects. See `ISOSURFACE-PROVENANCE.md` for the clean-port boundary.

The package is deliberately substrate-shaped:

- CultCache owns document identity, schema compatibility, references, and local
  persistence.
- CultNet can distribute these documents through the normal raw document lane.
- CultMesh can use them as worker-visible shared state for simulation and
  geometry pipelines.
- JSON is not a runtime transport here. It may publish schema descriptions at
  the xeno boundary, but the live machine eats and emits typed CultCache
  documents.

## Value Primitives

The small value layer is for cross-runtime query contracts, physics probes,
native slices, and UI surfaces:

- `CultVec2` and `CultVec3`: whole-vector values for positions, velocities,
  accelerations, and query inputs. SoA layouts may store them efficiently, but
  the semantic API does not force callers to split x/y/z into unrelated fields.
- `CultRect`: canonical XY viewport/query rectangle stored as min/max corners.
- `CultCircle`: 2D influence/query brush with rect intersection helpers.
- `CultSphere`: 3D query primitive with whole-vector center and XY projection.

Aetheria object viewports, gravity influence brushes, Ymir overlap/cast query
fixtures, and Unity native render views should use these shapes or generated
runtime equivalents instead of bespoke `{ minX, minY, maxX, maxY }` payloads.

## Runtime Path

CultGeometry CSG (`GameCult.Geometry.Csg`) emits domain documents, selected-cut
manifests, and chunk artifacts as MessagePack CultCache documents. A Godot
runtime, local worker, or remote geometry process receives the same records
through CultMesh/CultNet and reads them back as `CultGeometry*` types.

```csharp
using GameCult.Caching;
using GameCult.Geometry;
using GameCult.Mesh;
using GameCult.Networking;

var geometryDocuments = new CultNetDocumentRegistry(CultDocumentRegistry.Shared)
    .Register(CultNetDocumentBinding.ForDocument<CultGeometryDomainDocument>())
    .Register(CultNetDocumentBinding.ForDocument<CultGeometryBuildRequest>())
    .Register(CultNetDocumentBinding.ForDocument<CultGeometrySelectedCutManifest>())
    .Register(CultNetDocumentBinding.ForDocument<CultGeometryChunkArtifact>());

using var node = await CultMesh.CreateNodeAsync("ragnarok-geometry.ccmp", new CultMeshNodeOptions
{
    DatabaseOptions = new CultNetDatabaseOptions
    {
        RuntimeId = "godot-runtime",
        DocumentRegistry = geometryDocuments
    }
});

using var chunks = node.Database
    .Watch<CultGeometryChunkArtifact>()
    .Subscribe(change => ApplyChunk(change.Document));
```

The record key is part of the contract. Rust and C# must compute the same key
for the same geometry record, so stable fingerprints use the Rust canonical
field order and invariant float text. If this drifts, the same chunk becomes two
different chunks depending on which runtime touched it first. That is not a
cache miss; that is split-brain geometry.

## Documents

- `CultVec2`, `CultVec3`, `CultRect`, `CultCircle`, `CultSphere`: shared value
  primitives for query and view contracts.
- `CultGeometryDomainDocument`: one hierarchical domain tree, suitable for
  Ragnarok/Fensalir-style feature DSL output.
- `CultGeometryBuildRequest`: one LOD/frustum/budget request for workers.
- `CultGeometrySelectedCutManifest`: the selected domain cut, deferred children,
  fallback rows, and diagnostic contribution rows.
- `CultGeometryChunkArtifact`: render mesh, optional collider mesh, source
  domain/claim identities, build counters, and transition clipping seed.
