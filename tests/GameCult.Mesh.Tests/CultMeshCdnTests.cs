using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshCdnTests
{
    [Test]
    public async Task Artifact_PacksPublishesAndReassembles_FromCultCache()
    {
        var cache = new CultCache();
        var payload = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz");
        var artifact = CultMesh.PackCdnArtifact(
            "aetheria/ui/icons/minimap-pack",
            payload,
            new CultMeshCdnPackOptions
            {
                ChunkSizeBytes = 8,
                Version = "2026.06.27",
                MimeType = "application/octet-stream",
                Tags = ["aetheria", "ui"],
                Metadata = new Dictionary<string, string>
                {
                    ["channel"] = "dev"
                }
            });

        artifact.Manifest.Kind.Should().Be(CultMeshCdnArtifactKinds.Asset);
        artifact.Manifest.SizeBytes.Should().Be(payload.Length);
        artifact.Manifest.Chunks.Should().HaveCount(4);
        artifact.Manifest.Chunks.Select(chunk => chunk.Offset).Should().Equal(0, 8, 16, 24);
        artifact.Manifest.Chunks.Select(chunk => chunk.SizeBytes).Should().Equal(8, 8, 8, 2);

        var manifest = await CultMesh.PublishCdnArtifactAsync(cache, artifact);
        var manifestKey = CultMeshCdnArtifactManifest.CreateRecordKey(manifest);
        cache.Get<CultMeshCdnArtifactManifest>(manifestKey).Should().NotBeNull();

        var roundTrip = CultMesh.ReadCdnArtifact(cache, manifest);
        roundTrip.Should().Equal(payload);
    }

    [Test]
    public async Task BuildArtifact_Replicates_ThroughCultNetRawSnapshot()
    {
        var sourceCache = new CultCache();
        var targetCache = new CultCache();
        var registry = CultMesh.CreateCdnDocumentRegistry(sourceCache.Registry);
        var payload = Encoding.UTF8.GetBytes("starbridge-build-update");
        var artifact = CultMesh.PackCdnArtifact(
            "starbridge/windows/client",
            payload,
            new CultMeshCdnPackOptions
            {
                ChunkSizeBytes = 7,
                Kind = CultMeshCdnArtifactKinds.Build,
                Version = "0.4.0-preview",
                Tags = ["starbridge", "build"],
                Metadata = new Dictionary<string, string>
                {
                    ["platform"] = "windows-x64"
                }
            });
        var manifest = await CultMesh.PublishCdnArtifactAsync(sourceCache, artifact);
        var manifestSchemaId = sourceCache.Registry.GetRequired<CultMeshCdnArtifactManifest>().SchemaId;
        var chunkSchemaId = sourceCache.Registry.GetRequired<CultMeshCdnArtifactChunk>().SchemaId;
        var recordKeys = artifact.RecordKeys.Select(key => key.Value).ToArray();

        var request = registry.CreateSnapshotRequest(
            "cdn-snapshot-request",
            schemaIds: [manifestSchemaId, chunkSchemaId],
            recordKeys: recordKeys);
        var response = registry.CreateRawSnapshotResponse(sourceCache, "cdn-snapshot", request);

        response.Documents.Should().HaveCount(artifact.Chunks.Count + 1);
        response.Documents.Select(document => document.RecordKey).Should().BeEquivalentTo(recordKeys);

        await registry.ApplyRawSnapshotResponseAsync(targetCache, response);
        var replicatedManifest = targetCache.Get<CultMeshCdnArtifactManifest>(
            CultMeshCdnArtifactManifest.CreateRecordKey(manifest));
        replicatedManifest.Should().NotBeNull();
        replicatedManifest!.Kind.Should().Be(CultMeshCdnArtifactKinds.Build);
        replicatedManifest.Metadata["platform"].Should().Be("windows-x64");

        var roundTrip = CultMesh.ReadCdnArtifact(targetCache, replicatedManifest);
        roundTrip.Should().Equal(payload);
    }

    [Test]
    public async Task ArtifactRead_RejectsTamperedChunkPayload()
    {
        var cache = new CultCache();
        var artifact = CultMesh.PackCdnArtifact(
            "aetheria/gravity/layer0",
            [1, 1, 2, 3, 5, 8],
            new CultMeshCdnPackOptions
            {
                ChunkSizeBytes = 3,
                Metadata = new Dictionary<string, string>
                {
                    ["channels"] = "r32"
                }
            });
        var manifest = await CultMesh.PublishCdnArtifactAsync(cache, artifact);
        var firstChunk = artifact.Chunks[0];
        firstChunk.Payload[0] = 99;
        await cache.UpsertAsync(
            firstChunk,
            new CultRecordHandle<CultMeshCdnArtifactChunk>(
                CultMeshCdnArtifactChunk.CreateRecordKey(firstChunk)));

        Action read = () => CultMesh.ReadCdnArtifact(cache, manifest);
        read.Should().Throw<InvalidDataException>()
            .WithMessage("*chunk hash*");
    }
}
