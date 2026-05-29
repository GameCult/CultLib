using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using GameCult.Networking;
using NUnit.Framework;
using R3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

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
            var chunk = SampleChunk();

            var payload = CultDocumentMessagePackSerialization.Serialize(chunk);
            var decoded = CultDocumentMessagePackSerialization.Deserialize<CultGeometryChunkArtifact>(payload);

            decoded.ChunkId.Should().Be(chunk.ChunkId);
            decoded.RenderMesh.TriangleCount.Should().Be(1);
            CultGeometryChunkArtifact.CreateRecordKey(decoded)
                .Should().Be(CultGeometryChunkArtifact.CreateRecordKey(chunk));
        }

        [Test]
        public void FeatureClaim_StableFingerprint_UsesRustCanonicalOrder()
        {
            var claim = SampleDomain().Root.Children[0].Claims[0];

            claim.StableFingerprint().Should().Be(string.Join('\u001e',
                "column-support-shell",
                "0,0,0",
                "0,0,0,1",
                "0,0,45",
                "18,18,96",
                "SupportShell",
                "10",
                "RenderAndCollider"));
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

        private static CultGeometryChunkArtifact SampleChunk()
        {
            return new CultGeometryChunkArtifact
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
        }
    }
}
