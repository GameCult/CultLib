using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshEntityPrefabTests
{
    [Test]
    public async Task EntityPrefabPackage_PublishesBrokkrAuthoredGraph_WithCdnAssetRefs()
    {
        var cache = new CultCache();
        var meshArtifact = CultMesh.PackCdnArtifact(
            "aetheria/prefabs/ships/scout/mesh/body",
            Encoding.UTF8.GetBytes("portable-mesh-bytes"),
            new CultMeshCdnPackOptions
            {
                Version = "authoring-1",
                MimeType = "model/gltf-binary",
                Tags = ["brokkr", "mesh"],
            });
        var textureArtifact = CultMesh.PackCdnArtifact(
            "aetheria/prefabs/ships/scout/texture/albedo",
            Encoding.UTF8.GetBytes("texture-bytes"),
            new CultMeshCdnPackOptions
            {
                Version = "authoring-1",
                MimeType = "image/png",
                Tags = ["brokkr", "texture"],
            });
        await CultMesh.PublishCdnArtifactAsync(cache, meshArtifact);
        await CultMesh.PublishCdnArtifactAsync(cache, textureArtifact);

        var package = CultMesh.FinalizeEntityPrefabPackage(new CultMeshEntityPrefabPackage
        {
            PrefabId = "aetheria/entity/ship/scout",
            Version = "authoring-1",
            SourceTool = "brokkr.blender",
            SourceScene = "ships.blend",
            SourceCollection = "prefab.ship.scout",
            Tags = ["aetheria", "ship", "brokkr"],
            Assets =
            [
                AssetRef("mesh.body", CultMeshEntityPrefabAssetRoles.Mesh, meshArtifact.Manifest),
                AssetRef("texture.albedo", CultMeshEntityPrefabAssetRoles.Texture, textureArtifact.Manifest)
            ],
            Nodes =
            [
                new CultMeshEntityPrefabNode
                {
                    NodeId = "root",
                    Name = "Scout",
                    Kind = CultMeshEntityPrefabNodeKinds.Empty,
                    Tags = ["entity-root"],
                    Components =
                    [
                        new CultMeshEntityPrefabComponent
                        {
                            Type = "aetheria.ship",
                            Properties = new Dictionary<string, string>
                            {
                                ["hullClass"] = "scout"
                            }
                        }
                    ]
                },
                new CultMeshEntityPrefabNode
                {
                    NodeId = "body",
                    Name = "Body",
                    ParentNodeId = "root",
                    Kind = CultMeshEntityPrefabNodeKinds.Mesh,
                    MeshAssetId = "mesh.body",
                    MaterialAssetIds = ["texture.albedo"],
                    Transform = new CultMeshEntityPrefabTransform
                    {
                        Position = [0f, 0f, 0f],
                        Rotation = [0f, 0f, 0f, 1f],
                        Scale = [1f, 1f, 1f]
                    }
                }
            ],
            Metadata = new Dictionary<string, string>
            {
                ["authoringAxis"] = "blender",
                ["runtimeLowering"] = "unity,ts"
            }
        });

        await CultMesh.PublishEntityPrefabPackageAsync(cache, package);

        var key = CultMeshEntityPrefabPackage.CreateRecordKey(package);
        var stored = cache.Get<CultMeshEntityPrefabPackage>(key);
        stored.Should().NotBeNull();
        stored!.SourceTool.Should().Be("brokkr.blender");
        stored.SourceCollection.Should().Be("prefab.ship.scout");
        stored.Nodes.Should().Contain(node => node.Kind == CultMeshEntityPrefabNodeKinds.Mesh);
        stored.Assets.Select(asset => asset.CdnManifestRecordKey)
            .Should()
            .Contain(meshArtifact.ManifestKey.Value)
            .And.Contain(textureArtifact.ManifestKey.Value);
    }

    [Test]
    public async Task EntityPrefabPackage_ReplicatesWithReferencedCdnArtifacts_ThroughRawSnapshot()
    {
        var sourceCache = new CultCache();
        var targetCache = new CultCache();
        var registry = CultMesh.CreateAssetPipelineDocumentRegistry(sourceCache.Registry);
        var meshArtifact = CultMesh.PackCdnArtifact(
            "aetheria/prefabs/projectiles/bolt/mesh",
            Encoding.UTF8.GetBytes("bolt-mesh"),
            new CultMeshCdnPackOptions { Version = "v1", MimeType = "model/gltf-binary" });
        await CultMesh.PublishCdnArtifactAsync(sourceCache, meshArtifact);

        var package = CultMesh.FinalizeEntityPrefabPackage(new CultMeshEntityPrefabPackage
        {
            PrefabId = "aetheria/entity/projectile/bolt",
            Version = "v1",
            SourceTool = "brokkr.blender",
            SourceScene = "projectiles.blend",
            SourceCollection = "prefab.projectile.bolt",
            Assets = [AssetRef("mesh", CultMeshEntityPrefabAssetRoles.Mesh, meshArtifact.Manifest)],
            Nodes =
            [
                new CultMeshEntityPrefabNode
                {
                    NodeId = "bolt",
                    Name = "Bolt",
                    Kind = CultMeshEntityPrefabNodeKinds.Mesh,
                    MeshAssetId = "mesh",
                    Components =
                    [
                        new CultMeshEntityPrefabComponent
                        {
                            Type = "aetheria.projectile",
                            Properties = new Dictionary<string, string>
                            {
                                ["damage"] = "10"
                            }
                        }
                    ]
                }
            ]
        });
        await CultMesh.PublishEntityPrefabPackageAsync(sourceCache, package);

        var packageSchema = sourceCache.Registry.GetRequired<CultMeshEntityPrefabPackage>().SchemaId;
        var manifestSchema = sourceCache.Registry.GetRequired<CultMeshCdnArtifactManifest>().SchemaId;
        var chunkSchema = sourceCache.Registry.GetRequired<CultMeshCdnArtifactChunk>().SchemaId;
        var recordKeys = new[] { CultMeshEntityPrefabPackage.CreateRecordKey(package) }
            .Concat(meshArtifact.RecordKeys)
            .Select(key => key.Value)
            .ToArray();

        var request = registry.CreateSnapshotRequest(
            "entity-prefab-request",
            schemaIds: [packageSchema, manifestSchema, chunkSchema],
            recordKeys: recordKeys);
        var response = registry.CreateRawSnapshotResponse(sourceCache, "entity-prefab-response", request);

        response.Documents.Should().HaveCount(meshArtifact.Chunks.Count + 2);
        await registry.ApplyRawSnapshotResponseAsync(targetCache, response);

        var replicatedPackage = targetCache.Get<CultMeshEntityPrefabPackage>(
            CultMeshEntityPrefabPackage.CreateRecordKey(package));
        replicatedPackage.Should().NotBeNull();
        replicatedPackage!.Nodes.Single().Components.Single().Type.Should().Be("aetheria.projectile");

        var meshManifest = targetCache.Get<CultMeshCdnArtifactManifest>(meshArtifact.ManifestKey);
        meshManifest.Should().NotBeNull();
        CultMesh.ReadCdnArtifact(targetCache, meshManifest!).Should().Equal(Encoding.UTF8.GetBytes("bolt-mesh"));
    }

    private static CultMeshEntityPrefabAssetRef AssetRef(
        string assetId,
        string role,
        CultMeshCdnArtifactManifest manifest)
    {
        return new CultMeshEntityPrefabAssetRef
        {
            AssetId = assetId,
            Role = role,
            CdnManifestRecordKey = CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value,
            ContentHash = manifest.ContentHash,
            MimeType = manifest.MimeType,
            Metadata = new Dictionary<string, string>
            {
                ["sourceArtifactId"] = manifest.ArtifactId
            }
        };
    }
}
