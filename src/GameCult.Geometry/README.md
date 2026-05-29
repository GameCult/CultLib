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

## Runtime Path

`vg-csg` emits domain documents, selected-cut manifests, and chunk artifacts as
MessagePack CultCache documents. A Unity runtime, local worker, or remote
geometry process should receive the same records through CultMesh/CultNet and
read them back as `CultGeometry*` types.

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
        RuntimeId = "unity-runtime",
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

- `CultGeometryDomainDocument`: one hierarchical domain tree, suitable for
  Ragnarok/Fensalir-style feature DSL output.
- `CultGeometryBuildRequest`: one LOD/frustum/budget request for workers.
- `CultGeometrySelectedCutManifest`: the selected domain cut, deferred children,
  fallback rows, and diagnostic contribution rows.
- `CultGeometryChunkArtifact`: render mesh, optional collider mesh, source
  domain/claim identities, build counters, and transition clipping seed.
