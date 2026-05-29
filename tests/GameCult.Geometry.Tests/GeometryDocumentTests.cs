using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Geometry.Tests
{
    public sealed class GeometryDocumentTests
    {
        [Test]
        public void GeometryDocuments_Register_AsCultCacheSchemas()
        {
            var cache = new CultCache();

            cache.Registry.GetRequired<CultGeometryDomainDocument>().SchemaVersion
                .Should().Be(CultGeometrySchemaVersions.Domain);
            cache.Registry.GetRequired<CultGeometryBuildRequest>().SchemaVersion
                .Should().Be(CultGeometrySchemaVersions.BuildRequest);
            cache.Registry.GetRequired<CultGeometrySelectedCutManifest>().SchemaVersion
                .Should().Be(CultGeometrySchemaVersions.SelectedCut);
            cache.Registry.GetRequired<CultGeometryChunkArtifact>().SchemaVersion
                .Should().Be(CultGeometrySchemaVersions.ChunkArtifact);
        }

        [Test]
        public void GeometryBuildRequest_UsesStableCultCacheKey()
        {
            var domain = SampleDomain();
            var domainKey = CultGeometryDomainDocument.CreateRecordKey(domain);
            var request = new CultGeometryBuildRequest
            {
                DomainKey = domainKey.Value,
                WorkerGroup = "ragnarok-column-workers",
                CameraPosition = [36f, -42f, 30f],
                FrustumMin = [-75f, -75f, -30f],
                FrustumMax = [75f, 75f, 120f],
                ViewportHeightPixels = 1080f,
                VerticalFovRadians = 1.0471976f,
                TargetError = 0.01f,
                TriangleBudget = 10_000,
                ColliderBudget = 10_000,
                SemanticFilter = [],
                RequestedChunkKeys = [],
                DirtyDomainKeys = []
            };

            var first = CultGeometryBuildRequest.CreateRecordKey(request);
            var second = CultGeometryBuildRequest.CreateRecordKey(request);
            request.TriangleBudget++;
            var changed = CultGeometryBuildRequest.CreateRecordKey(request);

            second.Should().Be(first);
            changed.Should().NotBe(first);
            first.Value.Should().StartWith("geometry:request:");
        }

        [Test]
        public void ChunkArtifact_RoundTrips_ThroughCultCacheMessagePack()
        {
            var chunk = new CultGeometryChunkArtifact
            {
                ChunkId = "chunk/ragnarok-column/column-00",
                CutKey = "geometry:cut:test",
                SelectedCutId = "cut-test",
                BoundsMin = [-1f, -1f, 0f],
                BoundsMax = [1f, 1f, 1f],
                SourceDomainKeys = ["ragnarok-column/stellarator-column-00"],
                SourceClaimKeys = ["ragnarok-column/stellarator-column-00/claim/support"],
                RenderMesh = new CultGeometryTriangleMesh
                {
                    Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
                    Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                    Uvs = [0f, 0f, 1f, 0f, 0f, 1f],
                    Indices = [0u, 1u, 2u],
                    TriangleMaterials = [20u]
                },
                ColliderMesh = null,
                InputBrushes = 1,
                StableClipSeed = 1234,
                SupportsParentChildCoexistence = true
            };

            var payload = CultDocumentMessagePackSerialization.Serialize(chunk);
            var decoded = CultDocumentMessagePackSerialization.Deserialize<CultGeometryChunkArtifact>(payload);

            decoded.ChunkId.Should().Be(chunk.ChunkId);
            decoded.RenderMesh.TriangleCount.Should().Be(1);
            CultGeometryChunkArtifact.CreateRecordKey(decoded)
                .Should().Be(CultGeometryChunkArtifact.CreateRecordKey(chunk));
        }

        private static CultGeometryDomainDocument SampleDomain()
        {
            return new CultGeometryDomainDocument
            {
                DomainId = "ragnarok-column",
                RootKey = "ragnarok-column",
                SourceRuntime = "vg-csg",
                CreatedAt = "2026-05-29T00:00:00.0000000Z",
                Root = new CultGeometryDomainNode
                {
                    Name = "ragnarok-column",
                    Kind = "Root",
                    Translation = [0f, 0f, 0f],
                    RotationXyzw = [0f, 0f, 0f, 1f],
                    Seed = 0x5EED,
                    Claims = [],
                    Children =
                    [
                        new CultGeometryDomainNode
                        {
                            Name = "stellarator-column-00",
                            Kind = "Column",
                            Translation = [0f, 0f, 0f],
                            RotationXyzw = [0f, 0f, 0f, 1f],
                            Seed = 0xC0110000,
                            Claims =
                            [
                                new CultGeometryFeatureClaim
                                {
                                    Name = "column-support-shell",
                                    Translation = [0f, 0f, 0f],
                                    RotationXyzw = [0f, 0f, 0f, 1f],
                                    SupportCenter = [0f, 0f, 45f],
                                    SupportSize = [18f, 18f, 96f],
                                    Kind = "SupportShell",
                                    Material = 10,
                                    LoweringPolicy = "RenderAndCollider"
                                }
                            ],
                            Children = []
                        }
                    ]
                }
            };
        }
    }
}
