using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    /// <summary>
    /// Schema versions for portable entity prefab packages.
    /// </summary>
    public static class CultMeshEntityPrefabSchemaVersions
    {
        /// <summary>Portable prefab package schema id.</summary>
        public const string Package = "gamecult.mesh.entity_prefab_package.v1";
    }

    /// <summary>
    /// Well-known portable prefab node kinds.
    /// </summary>
    public static class CultMeshEntityPrefabNodeKinds
    {
        /// <summary>Transform-only node.</summary>
        public const string Empty = "empty";
        /// <summary>Renderable mesh node.</summary>
        public const string Mesh = "mesh";
        /// <summary>Collider node.</summary>
        public const string Collider = "collider";
        /// <summary>Particle or projectile effect node.</summary>
        public const string Effect = "effect";
        /// <summary>Camera or view anchor node.</summary>
        public const string Camera = "camera";
        /// <summary>Runtime attachment point.</summary>
        public const string Socket = "socket";
    }

    /// <summary>
    /// Well-known prefab asset roles.
    /// </summary>
    public static class CultMeshEntityPrefabAssetRoles
    {
        /// <summary>Mesh geometry payload.</summary>
        public const string Mesh = "mesh";
        /// <summary>Texture image payload.</summary>
        public const string Texture = "texture";
        /// <summary>Material definition payload.</summary>
        public const string Material = "material";
        /// <summary>Animation clip payload.</summary>
        public const string Animation = "animation";
        /// <summary>Audio payload.</summary>
        public const string Audio = "audio";
    }

    /// <summary>
    /// Portable transform using Blender/CultMesh source coordinates before runtime lowering.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshEntityPrefabTransform
    {
        /// <summary>Local position vector.</summary>
        [Key(0)]
        public float[] Position { get; set; } = [0f, 0f, 0f];

        /// <summary>Local rotation quaternion.</summary>
        [Key(1)]
        public float[] Rotation { get; set; } = [0f, 0f, 0f, 1f];

        /// <summary>Local scale vector.</summary>
        [Key(2)]
        public float[] Scale { get; set; } = [1f, 1f, 1f];
    }

    /// <summary>
    /// Runtime component intent attached to a portable prefab node.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshEntityPrefabComponent
    {
        /// <summary>Portable component kind or runtime adapter type.</summary>
        [Key(0)]
        public string Type { get; set; } = string.Empty;

        /// <summary>String-valued component properties for runtime-specific lowering.</summary>
        [Key(1)]
        public Dictionary<string, string> Properties { get; set; } = new();
    }

    /// <summary>
    /// Reference from a prefab package to a CDN artifact manifest.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshEntityPrefabAssetRef
    {
        /// <summary>Stable source asset id inside the prefab package.</summary>
        [Key(0)]
        public string AssetId { get; set; } = string.Empty;

        /// <summary>Semantic role, such as mesh, texture, material, or animation.</summary>
        [Key(1)]
        public string Role { get; set; } = string.Empty;

        /// <summary>CultCache record key for the referenced CDN artifact manifest.</summary>
        [Key(2)]
        public string CdnManifestRecordKey { get; set; } = string.Empty;

        /// <summary>Full artifact content hash copied from the referenced CDN manifest.</summary>
        [Key(3)]
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>Artifact MIME type copied from the referenced CDN manifest.</summary>
        [Key(4)]
        public string MimeType { get; set; } = string.Empty;

        /// <summary>Runtime and source metadata for the asset reference.</summary>
        [Key(5)]
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// One node in a portable entity prefab graph.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshEntityPrefabNode
    {
        /// <summary>Stable node id inside the package.</summary>
        [Key(0)]
        public string NodeId { get; set; } = string.Empty;

        /// <summary>Source object name.</summary>
        [Key(1)]
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional parent node id.</summary>
        [Key(2)]
        public string ParentNodeId { get; set; } = string.Empty;

        /// <summary>Node kind.</summary>
        [Key(3)]
        public string Kind { get; set; } = CultMeshEntityPrefabNodeKinds.Empty;

        /// <summary>Local transform.</summary>
        [Key(4)]
        public CultMeshEntityPrefabTransform Transform { get; set; } = new();

        /// <summary>Optional mesh asset id used by this node.</summary>
        [Key(5)]
        public string MeshAssetId { get; set; } = string.Empty;

        /// <summary>Material asset ids used by this node.</summary>
        [Key(6)]
        public string[] MaterialAssetIds { get; set; } = Array.Empty<string>();

        /// <summary>Runtime component intents attached to this node.</summary>
        [Key(7)]
        public CultMeshEntityPrefabComponent[] Components { get; set; } = Array.Empty<CultMeshEntityPrefabComponent>();

        /// <summary>Runtime tags copied from source metadata.</summary>
        [Key(8)]
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>Additional source metadata.</summary>
        [Key(9)]
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Portable entity prefab package authored from a Blender collection and lowerable into multiple runtimes.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.mesh.entity_prefab_package", CultMeshEntityPrefabSchemaVersions.Package)]
    public sealed class CultMeshEntityPrefabPackage
    {
        /// <summary>Stable logical prefab id.</summary>
        [Key(0)]
        [CultName]
        public string PrefabId { get; set; } = string.Empty;

        /// <summary>Caller-defined package version.</summary>
        [Key(1)]
        [CultIndex]
        public string Version { get; set; } = string.Empty;

        /// <summary>Tool that authored the package, such as Brokkr Blender.</summary>
        [Key(2)]
        [CultIndex]
        public string SourceTool { get; set; } = string.Empty;

        /// <summary>Source scene name or path.</summary>
        [Key(3)]
        public string SourceScene { get; set; } = string.Empty;

        /// <summary>Source Blender collection name.</summary>
        [Key(4)]
        [CultIndex]
        public string SourceCollection { get; set; } = string.Empty;

        /// <summary>Stable hash of graph structure and referenced assets.</summary>
        [Key(5)]
        [CultIndex]
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>Package creation timestamp in round-trip UTC format.</summary>
        [Key(6)]
        public string CreatedAtUtc { get; set; } = string.Empty;

        /// <summary>Referenced CDN assets.</summary>
        [Key(7)]
        public CultMeshEntityPrefabAssetRef[] Assets { get; set; } = Array.Empty<CultMeshEntityPrefabAssetRef>();

        /// <summary>Prefab hierarchy nodes.</summary>
        [Key(8)]
        public CultMeshEntityPrefabNode[] Nodes { get; set; } = Array.Empty<CultMeshEntityPrefabNode>();

        /// <summary>Search and routing tags.</summary>
        [Key(9)]
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>Package-level metadata.</summary>
        [Key(10)]
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>Builds the deterministic package record key.</summary>
        public static CultRecordKey CreateRecordKey(CultMeshEntityPrefabPackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            return CreateRecordKey(package.PrefabId, package.Version, package.ContentHash);
        }

        /// <summary>Builds the deterministic package record key.</summary>
        public static CultRecordKey CreateRecordKey(string prefabId, string version, string contentHash)
        {
            var prefabToken = CultMeshCdn.StableToken(prefabId, nameof(prefabId));
            var versionToken = string.IsNullOrWhiteSpace(version)
                ? "unversioned"
                : CultMeshCdn.StableToken(version, nameof(version));
            return new CultRecordKey(
                "mesh:entity-prefab:" + prefabToken + ":" + versionToken + ":" + CultMeshCdn.NormalizeHash(contentHash, nameof(contentHash)));
        }
    }

    /// <summary>
    /// Helpers for publishing and validating portable entity prefab packages.
    /// </summary>
    public static class CultMeshEntityPrefabs
    {
        /// <summary>
        /// Computes a stable package content hash from graph structure and referenced asset hashes.
        /// </summary>
        public static string ComputeContentHash(CultMeshEntityPrefabPackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            var builder = new StringBuilder();
            builder.Append(package.PrefabId).Append('\u001F')
                .Append(package.Version).Append('\u001F')
                .Append(package.SourceTool).Append('\u001F')
                .Append(package.SourceScene).Append('\u001F')
                .Append(package.SourceCollection).Append('\u001F');

            foreach (var asset in package.Assets.OrderBy(asset => asset.AssetId, StringComparer.Ordinal))
            {
                builder.Append("asset").Append('\u001E')
                    .Append(asset.AssetId).Append('\u001E')
                    .Append(asset.Role).Append('\u001E')
                    .Append(asset.CdnManifestRecordKey).Append('\u001E')
                    .Append(CultMeshCdn.NormalizeHash(asset.ContentHash, nameof(asset.ContentHash))).Append('\u001E')
                    .Append(asset.MimeType).Append('\u001E')
                    .Append(StableMap(asset.Metadata)).Append('\u001F');
            }

            foreach (var node in package.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
            {
                builder.Append("node").Append('\u001E')
                    .Append(node.NodeId).Append('\u001E')
                    .Append(node.Name).Append('\u001E')
                    .Append(node.ParentNodeId).Append('\u001E')
                    .Append(node.Kind).Append('\u001E')
                    .Append(StableFloats(node.Transform.Position)).Append('\u001E')
                    .Append(StableFloats(node.Transform.Rotation)).Append('\u001E')
                    .Append(StableFloats(node.Transform.Scale)).Append('\u001E')
                    .Append(node.MeshAssetId).Append('\u001E')
                    .Append(string.Join(",", node.MaterialAssetIds.OrderBy(value => value, StringComparer.Ordinal))).Append('\u001E')
                    .Append(StableComponents(node.Components)).Append('\u001E')
                    .Append(string.Join(",", node.Tags.OrderBy(value => value, StringComparer.Ordinal))).Append('\u001E')
                    .Append(StableMap(node.Metadata)).Append('\u001F');
            }

            builder.Append(string.Join(",", package.Tags.OrderBy(value => value, StringComparer.Ordinal))).Append('\u001F')
                .Append(StableMap(package.Metadata));

            return CultMeshCdn.HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        /// <summary>
        /// Stamps the current content hash and creation timestamp on a package.
        /// </summary>
        public static CultMeshEntityPrefabPackage FinalizePackage(CultMeshEntityPrefabPackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            package.CreatedAtUtc = string.IsNullOrWhiteSpace(package.CreatedAtUtc)
                ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : package.CreatedAtUtc;
            package.ContentHash = ComputeContentHash(package);
            return package;
        }

        /// <summary>
        /// Publishes a finalized portable entity prefab package into a local cache.
        /// </summary>
        public static async Task<CultMeshEntityPrefabPackage> PublishAsync(
            CultCache cache,
            CultMeshEntityPrefabPackage package)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (package == null) throw new ArgumentNullException(nameof(package));
            ValidatePackage(package);
            await cache.UpsertAsync(
                package,
                new CultRecordHandle<CultMeshEntityPrefabPackage>(
                    CultMeshEntityPrefabPackage.CreateRecordKey(package))).ConfigureAwait(false);
            return package;
        }

        /// <summary>
        /// Publishes a finalized portable entity prefab package into a distributed CultNet database.
        /// </summary>
        public static async Task<CultMeshEntityPrefabPackage> PublishAsync(
            CultNetDatabase database,
            CultMeshEntityPrefabPackage package)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (package == null) throw new ArgumentNullException(nameof(package));
            ValidatePackage(package);
            await database.PutAsync(CultMeshEntityPrefabPackage.CreateRecordKey(package), package)
                .ConfigureAwait(false);
            return package;
        }

        /// <summary>
        /// Creates a raw CultNet document registry for entity prefab packages.
        /// </summary>
        public static CultNetDocumentRegistry CreateDocumentRegistry(CultDocumentRegistry? documents = null)
        {
            documents ??= CultDocumentRegistry.Shared;
            return new CultNetDocumentRegistry(documents)
                .Register(CultNetDocumentBinding.ForDocument<CultMeshEntityPrefabPackage>(documents));
        }

        /// <summary>
        /// Creates a raw CultNet document registry for CDN assets and portable entity prefab packages.
        /// </summary>
        public static CultNetDocumentRegistry CreateAssetPipelineDocumentRegistry(CultDocumentRegistry? documents = null)
        {
            documents ??= CultDocumentRegistry.Shared;
            return CultMeshCdn.CreateDocumentRegistry(documents)
                .Register(CultNetDocumentBinding.ForDocument<CultMeshEntityPrefabPackage>(documents));
        }

        private static void ValidatePackage(CultMeshEntityPrefabPackage package)
        {
            if (string.IsNullOrWhiteSpace(package.PrefabId))
            {
                throw new ArgumentException("Prefab id must be non-empty.", nameof(package));
            }

            var expectedHash = ComputeContentHash(package);
            if (!string.Equals(CultMeshCdn.NormalizeHash(package.ContentHash, nameof(package.ContentHash)), expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Entity prefab package content hash does not match its graph.");
            }
        }

        private static string StableComponents(IEnumerable<CultMeshEntityPrefabComponent> components)
        {
            return string.Join("|", components
                .OrderBy(component => component.Type, StringComparer.Ordinal)
                .Select(component => component.Type + "=" + StableMap(component.Properties)));
        }

        private static string StableFloats(IEnumerable<float> values)
        {
            return string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static string StableMap(Dictionary<string, string> values)
        {
            return string.Join("|", values
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value));
        }
    }
}
