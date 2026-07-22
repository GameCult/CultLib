using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using GameCult.Networking;
using CultMath;
using MessagePack;
using NUnit.Framework;
using R3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameCult.Geometry.Tests
{
    public sealed class GeometryDocumentTests
    {
        [Test]
        public void GeometryPrimitives_CanonicalizeAndIntersect()
        {
            var viewport = new CultRect(8f, 4f, -8f, -4f);

            viewport.Min.Should().Be(new float2(-8f, -4f));
            viewport.Max.Should().Be(new float2(8f, 4f));
            viewport.Center.Should().Be(float2.zero);
            viewport.Contains(new float2(2f, 3f)).Should().BeTrue();
            viewport.Intersects(new CultRect(7f, 3f, 10f, 5f)).Should().BeTrue();
            viewport.Intersects(new CultRect(9f, 5f, 10f, 6f)).Should().BeFalse();

            var gravityBrush = new CultCircle(new float2(10f, 0f), 3f);
            gravityBrush.Intersects(viewport).Should().BeTrue();
            gravityBrush.Contains(new float2(12f, 0f)).Should().BeTrue();
            gravityBrush.Contains(new float2(14f, 0f)).Should().BeFalse();
        }

        [Test]
        public void GeometryPrimitives_PreserveWholeVectorsForSoaAndPhysicsQueries()
        {
            var sphere = new CultSphere(new float3(1f, 2f, 3f), 5f);

            sphere.Contains(new float3(1f, 6f, 3f)).Should().BeTrue();
            sphere.Contains(new float3(1f, 8f, 3f)).Should().BeFalse();
            sphere.Center.xy.Should().Be(new float2(1f, 2f));
            sphere.Center.xz.Should().Be(new float2(1f, 3f));
            sphere.XyCircle.Bounds.Should().Be(new CultRect(-4f, -3f, 6f, 7f));
        }

        [Test]
        public void GeometryPrimitives_RoundTripThroughMessagePack()
        {
            var rect = new CultRect(new float2(5f, -2f), new float2(-1f, 7f));
            var options = CultDocumentMessagePackSerialization.OptionsFor(typeof(CultRect));
            var payload = MessagePackSerializer.Serialize(rect, options);
            var decoded = MessagePackSerializer.Deserialize<CultRect>(payload, options);

            decoded.Should().Be(rect);
            decoded.Min.Should().Be(new float2(-1f, -2f));
            decoded.Max.Should().Be(new float2(5f, 7f));
        }

        [Test]
        public void GeometryPrimitive_JsonInspectionProjection_UsesLowercaseComponents()
        {
            var value = new float3(1f, 2f, 3f);
            var json = JsonSerializer.Serialize(new { value.x, value.y, value.z });

            json.Should().Contain("\"x\":1");
            json.Should().Contain("\"y\":2");
            json.Should().Contain("\"z\":3");
            json.Should().NotContain("\"X\"");
            json.Should().NotContain("\"Y\"");
            json.Should().NotContain("\"Z\"");
        }

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

            cache.Registry.GetRequired<CultGeometryDomainDocument>().SchemaId
                .Should().Be("sha256:e3358436f8a07c84cc66b5196380cdfcd6920fa15f15d052d30b42b73762421d");
            cache.Registry.GetRequired<CultGeometryBuildRequest>().SchemaId
                .Should().Be("sha256:2de3162db128fc2c5cc2ea3dae46d0196110a9e5f969997002309e636a3721d3");
            cache.Registry.GetRequired<CultGeometrySelectedCutManifest>().SchemaId
                .Should().Be("sha256:864412d3cf0f6b079ceb0bbd3b36232f11380e63cdf0bfd64d505a710fb49ff8");
            cache.Registry.GetRequired<CultGeometryChunkArtifact>().SchemaId
                .Should().Be("sha256:2d28046b7c76244ced70ee6de09bcbb5c64d364aa578e5a4c903d397a7a56156");
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
                CameraPosition = new float3(36f, -42f, 30f),
                FrustumMin = new float3(-75f, -75f, -30f),
                FrustumMax = new float3(75f, 75f, 120f),
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
            var chunk = SampleChunk();

            var payload = CultDocumentMessagePackSerialization.Serialize(chunk);
            var decoded = CultDocumentMessagePackSerialization.Deserialize<CultGeometryChunkArtifact>(payload);

            decoded.ChunkId.Should().Be(chunk.ChunkId);
            decoded.RenderMesh.TriangleCount.Should().Be(1);
            decoded.BoundsMin.Should().Be(new float3(-1f, -1f, 0f));
            decoded.RenderMesh.Positions.Should().Equal(chunk.RenderMesh.Positions);
            decoded.RenderMesh.Normals.Should().HaveCount(decoded.RenderMesh.Positions.Length);
            decoded.RenderMesh.Uvs.Should().HaveCount(decoded.RenderMesh.Positions.Length);
            decoded.RenderMesh.Indices.Length.Should().Be(decoded.RenderMesh.TriangleMaterials.Length * 3);
            CultGeometryChunkArtifact.CreateRecordKey(decoded)
                .Should().Be(CultGeometryChunkArtifact.CreateRecordKey(chunk));
        }

        [Test]
        public void FeatureClaim_StableFingerprint_UsesRustCanonicalOrder()
        {
            var claim = SampleDomain().Root.Children[0].Claims[0];

            claim.StableFingerprint().Should().Be(string.Join('\u001e',
                "column-support-shell",
                "00000000,00000000,00000000",
                "00000000,00000000,00000000,3f800000",
                "00000000,00000000,42340000",
                "41900000,41900000,42c00000",
                "SupportShell",
                "10",
                "RenderAndCollider"));
        }

        [Test]
        public void TypedV2Domain_RoundTripsAndPreservesTheV1RecordKey()
        {
            var domain = SampleDomain();
            var payload = CultDocumentMessagePackSerialization.Serialize(domain);
            var decoded = CultDocumentMessagePackSerialization.Deserialize<CultGeometryDomainDocument>(payload);

            decoded.Root.Rotation.Should().Be(quaternion.identity);
            decoded.Root.Children[0].Claims[0].SupportCenter.Should().Be(new float3(0f, 0f, 45f));
            CultGeometryDomainDocument.CreateRecordKey(decoded).Value.Should().Be(
                "geometry:domain:175899ea97548da0599e12bcaccd07fa1d6009ed450fadb3de229d47bab04431");
        }

        [Test]
        public void V2Chunk_DoesNotAcceptTheLegacyFlatMeshPayload()
        {
            var payload = File.ReadAllBytes(FixturePath("ragnarok-first-chunk.msgpack"));

            FluentActions.Invoking(() =>
                    CultDocumentMessagePackSerialization.Deserialize<CultGeometryChunkArtifact>(payload))
                .Should().Throw<MessagePackSerializationException>();
        }

        [Test]
        public async Task ChunkArtifact_Replicates_ThroughCultNetRawDocumentLane()
        {
            var sourceCache = new CultCache();
            var targetCache = new CultCache();
            var schemaId = sourceCache.Registry.GetRequired<CultGeometryChunkArtifact>().SchemaId;
            var registry = new CultNetDocumentRegistry(sourceCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<CultGeometryChunkArtifact>(sourceCache.Registry));
            var chunk = SampleChunk();
            var handle = new CultRecordHandle<CultGeometryChunkArtifact>(
                CultGeometryChunkArtifact.CreateRecordKey(chunk));

            await sourceCache.AddAsync(chunk, handle);

            var put = registry.CreateRawDocumentPutMessage(
                "geometry-chunk-put",
                handle,
                chunk,
                new CultNetDocumentMessageOptions
                {
                    SourceRuntimeId = "vg-csg",
                    SourceRole = "geometry-worker",
                    Tags = ["ragnarok", "chunk"]
                });

            put.Document.SchemaId.Should().Be(schemaId);
            put.Document.PayloadEncoding.Should().Be("messagepack");
            put.Document.Payload.Should().NotBeEmpty();
            put.Document.RecordKey.Should().Be(handle.Key.Value);

            var applied = await registry.ApplyRawDocumentPutMessageAsync<CultGeometryChunkArtifact>(targetCache, put);
            var replicated = targetCache.Get<CultGeometryChunkArtifact>(handle.Key);

            applied.ChunkId.Should().Be(chunk.ChunkId);
            replicated.Should().NotBeNull();
            replicated!.RenderMesh.TriangleCount.Should().Be(1);
            CultGeometryChunkArtifact.CreateRecordKey(replicated)
                .Should().Be(handle.Key);
        }

        [Test]
        public async Task ChunkArtifact_Replicates_ThroughCultNetRawSnapshot()
        {
            var sourceCache = new CultCache();
            var targetCache = new CultCache();
            var schemaId = sourceCache.Registry.GetRequired<CultGeometryChunkArtifact>().SchemaId;
            var registry = new CultNetDocumentRegistry(sourceCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<CultGeometryChunkArtifact>(sourceCache.Registry));
            var chunk = SampleChunk();
            var handle = new CultRecordHandle<CultGeometryChunkArtifact>(
                CultGeometryChunkArtifact.CreateRecordKey(chunk));

            await sourceCache.AddAsync(chunk, handle);

            var request = registry.CreateSnapshotRequest(
                "geometry-snapshot-request",
                schemaIds: [schemaId],
                recordKeys: [handle.Key.Value]);
            var response = registry.CreateRawSnapshotResponse(sourceCache, "geometry-snapshot", request);

            response.Documents.Should().ContainSingle();
            response.Documents[0].SchemaId.Should().Be(schemaId);
            response.Documents[0].RecordKey.Should().Be(handle.Key.Value);

            var applied = await registry.ApplyRawSnapshotResponseAsync<CultGeometryChunkArtifact>(targetCache, response);
            var replicated = targetCache.Get<CultGeometryChunkArtifact>(handle.Key);

            applied.Should().ContainSingle();
            replicated.Should().NotBeNull();
            replicated!.ChunkId.Should().Be(chunk.ChunkId);
            replicated.RenderMesh.Indices.Should().Equal(chunk.RenderMesh.Indices);
        }

        [Test]
        public async Task ChunkArtifact_IsTypedSharedState_InLocalCultMeshNode()
        {
            var cachePath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "geometry-node-" + Guid.NewGuid().ToString("N") + ".ccmp");
            var registry = new CultNetDocumentRegistry(CultDocumentRegistry.Shared)
                .Register(CultNetDocumentBinding.ForDocument<CultGeometryChunkArtifact>(CultDocumentRegistry.Shared));
            var chunk = SampleChunk();
            var key = CultGeometryChunkArtifact.CreateRecordKey(chunk);
            var changes = new List<CultNetDatabaseChange<CultGeometryChunkArtifact>>();

            try
            {
                using (var node = await CultMesh.CreateNodeAsync(cachePath, new CultMeshNodeOptions
                {
                    StartServer = false,
                    DatabaseOptions = new CultNetDatabaseOptions
                    {
                        RuntimeId = "geometry-runtime",
                        DocumentRegistry = registry
                    }
                }))
                {
                    using var subscription = node.Database
                        .WatchRecord<CultGeometryChunkArtifact>(key)
                        .Subscribe(change => changes.Add(change));

                    await node.Database.PutAsync(key, chunk);
                    await node.FlushAsync();
                }

                using (var reopened = await CultMesh.CreateNodeAsync(cachePath, new CultMeshNodeOptions
                {
                    StartServer = false,
                    DatabaseOptions = new CultNetDatabaseOptions
                    {
                        RuntimeId = "unity-runtime",
                        DocumentRegistry = registry
                    }
                }))
                {
                    var persisted = await reopened.Database.GetAsync<CultGeometryChunkArtifact>(key);

                    persisted.Should().NotBeNull();
                    persisted!.ChunkId.Should().Be(chunk.ChunkId);
                    persisted.RenderMesh.Positions.Should().Equal(chunk.RenderMesh.Positions);
                }
            }
            finally
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }

            changes.Should().ContainSingle();
            changes[0].Kind.Should().Be(CultNetDatabaseChangeKind.Added);
            changes[0].Document.Should().NotBeNull();
            changes[0].Document!.ChunkId.Should().Be(chunk.ChunkId);
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
                    Translation = float3.zero,
                    Rotation = quaternion.identity,
                    Seed = 0x5EED,
                    Claims = [],
                    Children =
                    [
                        new CultGeometryDomainNode
                        {
                            Name = "stellarator-column-00",
                            Kind = "Column",
                            Translation = float3.zero,
                            Rotation = quaternion.identity,
                            Seed = 0xC0110000,
                            Claims =
                            [
                                new CultGeometryFeatureClaim
                                {
                                    Name = "column-support-shell",
                                    Translation = float3.zero,
                                    Rotation = quaternion.identity,
                                    SupportCenter = new float3(0f, 0f, 45f),
                                    SupportSize = new float3(18f, 18f, 96f),
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

        private static CultGeometryChunkArtifact SampleChunk()
        {
            return new CultGeometryChunkArtifact
            {
                ChunkId = "chunk/ragnarok-column/column-00",
                CutKey = "geometry:cut:test",
                SelectedCutId = "cut-test",
                BoundsMin = new float3(-1f, -1f, 0f),
                BoundsMax = new float3(1f, 1f, 1f),
                SourceDomainKeys = ["ragnarok-column/stellarator-column-00"],
                SourceClaimKeys = ["ragnarok-column/stellarator-column-00/claim/support"],
                RenderMesh = new CultGeometryTriangleMesh
                {
                    Positions = [new float3(0f, 0f, 0f), new float3(1f, 0f, 0f), new float3(0f, 1f, 0f)],
                    Normals = [new float3(0f, 0f, 1f), new float3(0f, 0f, 1f), new float3(0f, 0f, 1f)],
                    Uvs = [new float2(0f, 0f), new float2(1f, 0f), new float2(0f, 1f)],
                    Indices = [0u, 1u, 2u],
                    TriangleMaterials = [20u]
                },
                ColliderMesh = null,
                InputBrushes = 1,
                StableClipSeed = 1234,
                SupportsParentChildCoexistence = true
            };
        }

        private static string FixturePath(string fileName)
        {
            return Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "vg-csg-ragnarok",
                fileName);
        }
    }
}
