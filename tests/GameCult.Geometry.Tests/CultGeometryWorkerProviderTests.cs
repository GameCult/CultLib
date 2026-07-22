using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CultMath;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Mesh;
using GameCult.Networking;
using NUnit.Framework;
using R3;

namespace GameCult.Geometry.Tests
{
    public sealed class CultGeometryWorkerProviderTests
    {
        [Test]
        public async Task BuildOperation_UsesTheDirectCommitPath_AndPublishesTypedState()
        {
            using var database = CreateDatabase("geometry-worker");
            var (domain, request) = await SeedAsync(database);
            var provider = new CultGeometryWorkerProvider("worker-1", database, new FakePipeline());
            var artifactChanges = new List<CultNetDatabaseChange<CultGeometryChunkArtifact>>();
            using var subscription = database.Watch<CultGeometryChunkArtifact>()
                .Subscribe(change => artifactChanges.Add(change));

            var receipt = await provider.BuildOperation.InvokeAsync(
                new CultGeometryBuildCommand { RequestKey = CultGeometryBuildRequest.CreateRecordKey(request).Value },
                "test-runtime");

            receipt.RequestKey.Should().Be(CultGeometryBuildRequest.CreateRecordKey(request).Value);
            receipt.ArtifactKeys.Should().ContainSingle();
            receipt.ContentHashes.Should().ContainSingle();
            artifactChanges.Should().ContainSingle(change => change.Key.Value == receipt.ArtifactKeys[0]);
            (await database.GetAsync<CultGeometrySelectedCutManifest>(new CultRecordKey(receipt.SelectedCutKey)))
                .Should().NotBeNull();
            (await database.GetAsync<CultGeometryChunkArtifact>(new CultRecordKey(receipt.ArtifactKeys[0])))
                .Should().NotBeNull();

            var state = await database.GetAsync<CultGeometryWorkerState>(provider.WorkerStateKey);
            state.Should().NotBeNull();
            state!.ActiveRequestKey.Should().Be(receipt.RequestKey);
            state.LastSelectedCutKey.Should().Be(receipt.SelectedCutKey);
            domain.DomainId.Should().Be("domain-1");
        }

        [Test]
        public async Task DirectAndOperationBuilds_DeriveTheSameCanonicalOutputKeys()
        {
            using var database = CreateDatabase("geometry-direct-operation");
            var (_, request) = await SeedAsync(database);
            var provider = new CultGeometryWorkerProvider("worker-1", database, new FakePipeline());
            var command = new CultGeometryBuildCommand
            {
                RequestKey = CultGeometryBuildRequest.CreateRecordKey(request).Value
            };

            var direct = await provider.BuildAsync(command);
            var operation = await provider.BuildOperation.InvokeAsync(command, "test-runtime");

            operation.SelectedCutKey.Should().Be(direct.SelectedCutKey);
            operation.ArtifactKeys.Should().Equal(direct.ArtifactKeys);
            operation.ContentHashes.Should().Equal(direct.ContentHashes);
        }

        [Test]
        public async Task Artifact_RawEnvelopeReplicates_AndWorkerStateCannotOverrideProbeTruth()
        {
            using var source = CreateDatabase("geometry-source");
            var (_, request) = await SeedAsync(source);
            var provider = new CultGeometryWorkerProvider("worker-1", source, new FakePipeline());
            var receipt = await provider.BuildAsync(new CultGeometryBuildCommand
            {
                RequestKey = CultGeometryBuildRequest.CreateRecordKey(request).Value
            });
            var artifactKey = new CultRecordKey(receipt.ArtifactKeys[0]);
            var artifact = await source.GetAsync<CultGeometryChunkArtifact>(artifactKey);
            artifact.Should().NotBeNull();

            var put = source.Documents.CreateRawDocumentPutMessage(
                "geometry-artifact",
                new CultRecordHandle<CultGeometryChunkArtifact>(artifactKey),
                artifact!,
                new CultNetDocumentMessageOptions { SourceRuntimeId = "geometry-source", SourceRole = "geometry-worker" });
            put.Document.PayloadEncoding.Should().Be("messagepack");

            var targetCache = new CultCache();
            var replicated = await source.Documents.ApplyRawDocumentPutMessageAsync<CultGeometryChunkArtifact>(targetCache, put);
            CultGeometryChunkArtifact.CreateRecordKey(replicated).Should().Be(artifactKey);

            await source.PutAsync(provider.WorkerStateKey, new CultGeometryWorkerState
            {
                WorkerId = "worker-1",
                Phase = "lying-status",
                ActiveRequestKey = receipt.RequestKey,
                LastSelectedCutKey = receipt.SelectedCutKey,
                LastArtifactKeys = receipt.ArtifactKeys,
                ServedPackageVersion = "false-version"
            });
            var probe = await provider.ProbeAsync();

            probe.Owner.Should().Be(CultGeometryWorkerProvider.Owner);
            probe.SchemaVersion.Should().Be(CultGeometrySchemaVersions.SelectedCut);
            probe.SelectedCutKey.Should().Be(receipt.SelectedCutKey);
            probe.ContentHashes.Should().Equal(receipt.ContentHashes);
            probe.ServedPackageVersion.Should().NotBe("false-version");
            (await source.GetAsync<CultGeometryChunkArtifact>(artifactKey))!.ChunkId.Should().Be("chunk-1");
        }

        [Test]
        public void WorkerState_RegistersThroughTheGeneratedDocumentRegistry()
        {
            var descriptor = new CultCache().Registry.GetRequired<CultGeometryWorkerState>();
            descriptor.SchemaVersion.Should().Be(CultGeometryWorkerSchemaVersions.State);
            descriptor.SchemaName.Should().Be("gamecult.geometry.worker_state");
        }

        private static CultNetDatabase CreateDatabase(string runtimeId)
        {
            var cache = new CultCache();
            return new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = runtimeId,
                DocumentRegistry = new CultNetDocumentRegistry(cache.Registry)
            });
        }

        private static async Task<(CultGeometryDomainDocument Domain, CultGeometryBuildRequest Request)> SeedAsync(
            CultNetDatabase database)
        {
            var domain = new CultGeometryDomainDocument
            {
                DomainId = "domain-1",
                RootKey = "root",
                SourceRuntime = "test",
                Root = new CultGeometryDomainNode { Name = "root", Kind = "root" }
            };
            var domainKey = CultGeometryDomainDocument.CreateRecordKey(domain);
            var request = new CultGeometryBuildRequest
            {
                RequestId = "request-1",
                DomainKey = domainKey.Value,
                WorkerGroup = "test",
                ViewportHeightPixels = 1080,
                VerticalFovRadians = 1,
                TargetError = 0.01f,
                TriangleBudget = 100,
                ColliderBudget = 100
            };
            await database.PutAsync(domainKey, domain);
            await database.PutAsync(CultGeometryBuildRequest.CreateRecordKey(request), request);
            return (domain, request);
        }

        private sealed class FakePipeline : ICultGeometryBuildPipeline
        {
            public Task<CultGeometryBuildOutput> BuildAsync(
                CultGeometryDomainDocument domain,
                CultGeometryBuildRequest request)
            {
                return Task.FromResult(new CultGeometryBuildOutput
                {
                    SelectedCut = new CultGeometrySelectedCutManifest
                    {
                        CutId = "cut-1",
                        SelectedNodes = [domain.Root.Name]
                    },
                    Artifacts =
                    [
                        new CultGeometryChunkArtifact
                        {
                            ChunkId = "chunk-1",
                            BoundsMin = float3.zero,
                            BoundsMax = new float3(1, 1, 1),
                            SourceDomainKeys = [request.DomainKey],
                            RenderMesh = new CultGeometryTriangleMesh()
                        }
                    ]
                });
            }
        }
    }
}
