using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    /// <summary>
    /// Schema versions for CultMesh distributed CDN documents.
    /// </summary>
    public static class CultMeshCdnSchemaVersions
    {
        /// <summary>Manifest document schema id.</summary>
        public const string ArtifactManifest = "gamecult.mesh.cdn_artifact_manifest.v1";
        /// <summary>Chunk document schema id.</summary>
        public const string ArtifactChunk = "gamecult.mesh.cdn_artifact_chunk.v1";
    }

    /// <summary>
    /// Well-known CultMesh CDN artifact kinds.
    /// </summary>
    public static class CultMeshCdnArtifactKinds
    {
        /// <summary>Game, UI, shader, texture, audio, or data asset payload.</summary>
        public const string Asset = "asset";
        /// <summary>Executable build or install/update payload.</summary>
        public const string Build = "build";
        /// <summary>Generic packaged payload.</summary>
        public const string Package = "package";
    }

    /// <summary>
    /// Options for packing bytes into CultMesh CDN documents.
    /// </summary>
    public sealed class CultMeshCdnPackOptions
    {
        /// <summary>Default CDN chunk size.</summary>
        public const int DefaultChunkSizeBytes = 256 * 1024;

        /// <summary>Maximum bytes per content chunk.</summary>
        public int ChunkSizeBytes { get; set; } = DefaultChunkSizeBytes;

        /// <summary>Artifact kind, such as asset or build.</summary>
        public string Kind { get; set; } = CultMeshCdnArtifactKinds.Asset;

        /// <summary>Caller-defined artifact version.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Artifact MIME type.</summary>
        public string MimeType { get; set; } = "application/octet-stream";

        /// <summary>Search and routing tags copied to the manifest.</summary>
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>Caller metadata copied to the manifest.</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>Override manifest creation timestamp in round-trip UTC format.</summary>
        public string? CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// Reference from an artifact manifest to one content-addressed chunk document.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshCdnChunkRef
    {
        /// <summary>SHA-256 hash of the referenced chunk payload.</summary>
        [Key(0)]
        public string ChunkHash { get; set; } = string.Empty;

        /// <summary>Byte offset of this chunk inside the materialized artifact.</summary>
        [Key(1)]
        public long Offset { get; set; }

        /// <summary>Number of payload bytes contributed by this chunk.</summary>
        [Key(2)]
        public int SizeBytes { get; set; }

        /// <summary>CultCache record key for the referenced chunk document.</summary>
        [Key(3)]
        public string RecordKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Content-addressed artifact chunk stored in CultCache and replicated through CultNet.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.mesh.cdn_artifact_chunk", CultMeshCdnSchemaVersions.ArtifactChunk)]
    public sealed class CultMeshCdnArtifactChunk
    {
        /// <summary>SHA-256 hash of the chunk payload.</summary>
        [Key(0)]
        [CultName]
        public string ChunkHash { get; set; } = string.Empty;

        /// <summary>Payload byte count.</summary>
        [Key(1)]
        public int SizeBytes { get; set; }

        /// <summary>Raw chunk payload bytes.</summary>
        [Key(2)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>Builds the deterministic content-addressed chunk record key.</summary>
        public static CultRecordKey CreateRecordKey(CultMeshCdnArtifactChunk chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            return CreateRecordKey(chunk.ChunkHash);
        }

        /// <summary>Builds the deterministic content-addressed chunk record key.</summary>
        public static CultRecordKey CreateRecordKey(string chunkHash)
        {
            return new CultRecordKey("mesh:cdn:chunk:" + CultMeshCdn.NormalizeHash(chunkHash, nameof(chunkHash)));
        }
    }

    /// <summary>
    /// Manifest for one versioned CDN artifact assembled from content-addressed chunks.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.mesh.cdn_artifact_manifest", CultMeshCdnSchemaVersions.ArtifactManifest)]
    public sealed class CultMeshCdnArtifactManifest
    {
        /// <summary>Stable artifact id, such as a logical asset or build path.</summary>
        [Key(0)]
        [CultName]
        public string ArtifactId { get; set; } = string.Empty;

        /// <summary>Artifact kind, such as asset, build, or package.</summary>
        [Key(1)]
        [CultIndex]
        public string Kind { get; set; } = CultMeshCdnArtifactKinds.Asset;

        /// <summary>Caller-defined artifact version.</summary>
        [Key(2)]
        [CultIndex]
        public string Version { get; set; } = string.Empty;

        /// <summary>SHA-256 hash of the full materialized artifact.</summary>
        [Key(3)]
        [CultIndex]
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>Full materialized artifact size.</summary>
        [Key(4)]
        public long SizeBytes { get; set; }

        /// <summary>Artifact MIME type.</summary>
        [Key(5)]
        public string MimeType { get; set; } = "application/octet-stream";

        /// <summary>Manifest creation timestamp in round-trip UTC format.</summary>
        [Key(6)]
        public string CreatedAtUtc { get; set; } = string.Empty;

        /// <summary>Ordered chunk references needed to materialize the artifact.</summary>
        [Key(7)]
        public CultMeshCdnChunkRef[] Chunks { get; set; } = Array.Empty<CultMeshCdnChunkRef>();

        /// <summary>Search and routing tags.</summary>
        [Key(8)]
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>Caller metadata, such as platform or texture channel layout.</summary>
        [Key(9)]
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>Builds the deterministic versioned artifact manifest record key.</summary>
        public static CultRecordKey CreateRecordKey(CultMeshCdnArtifactManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            return CreateRecordKey(manifest.ArtifactId, manifest.Version, manifest.ContentHash);
        }

        /// <summary>Builds the deterministic versioned artifact manifest record key.</summary>
        public static CultRecordKey CreateRecordKey(string artifactId, string version, string contentHash)
        {
            var artifactToken = CultMeshCdn.StableToken(artifactId, nameof(artifactId));
            var versionToken = string.IsNullOrWhiteSpace(version)
                ? "unversioned"
                : CultMeshCdn.StableToken(version, nameof(version));
            return new CultRecordKey(
                "mesh:cdn:artifact:" + artifactToken + ":" + versionToken + ":" + CultMeshCdn.NormalizeHash(contentHash, nameof(contentHash)));
        }
    }

    /// <summary>
    /// Packed CDN artifact ready to publish into a cache or database.
    /// </summary>
    public sealed class CultMeshCdnArtifact
    {
        /// <summary>Creates a packed artifact container.</summary>
        public CultMeshCdnArtifact(
            CultMeshCdnArtifactManifest manifest,
            IReadOnlyList<CultMeshCdnArtifactChunk> chunks)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        }

        /// <summary>Artifact manifest document.</summary>
        public CultMeshCdnArtifactManifest Manifest { get; }

        /// <summary>Content-addressed chunk documents.</summary>
        public IReadOnlyList<CultMeshCdnArtifactChunk> Chunks { get; }

        /// <summary>Deterministic manifest record key.</summary>
        public CultRecordKey ManifestKey => CultMeshCdnArtifactManifest.CreateRecordKey(Manifest);

        /// <summary>All unique record keys required to replicate this artifact.</summary>
        public IReadOnlyList<CultRecordKey> RecordKeys =>
            new[] { ManifestKey }.Concat(Chunks.Select(CultMeshCdnArtifactChunk.CreateRecordKey)).Distinct().ToArray();
    }

    /// <summary>
    /// Content-addressed byte artifact helpers for distributing assets and builds through CultMesh clients.
    /// </summary>
    public static class CultMeshCdn
    {
        /// <summary>
        /// Splits a payload into content-addressed chunk documents and a versioned manifest.
        /// </summary>
        public static CultMeshCdnArtifact PackArtifact(
            string artifactId,
            byte[] payload,
            CultMeshCdnPackOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(artifactId))
            {
                throw new ArgumentException("Artifact id must be non-empty.", nameof(artifactId));
            }

            if (payload == null) throw new ArgumentNullException(nameof(payload));
            options ??= new CultMeshCdnPackOptions();
            if (options.ChunkSizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "ChunkSizeBytes must be greater than zero.");
            }

            var chunks = new List<CultMeshCdnArtifactChunk>();
            var refs = new List<CultMeshCdnChunkRef>();
            for (var offset = 0; offset < payload.Length; offset += options.ChunkSizeBytes)
            {
                var count = Math.Min(options.ChunkSizeBytes, payload.Length - offset);
                var chunkPayload = new byte[count];
                Buffer.BlockCopy(payload, offset, chunkPayload, 0, count);
                var chunkHash = HashBytes(chunkPayload);
                var chunk = new CultMeshCdnArtifactChunk
                {
                    ChunkHash = chunkHash,
                    SizeBytes = count,
                    Payload = chunkPayload
                };
                chunks.Add(chunk);
                refs.Add(new CultMeshCdnChunkRef
                {
                    ChunkHash = chunkHash,
                    Offset = offset,
                    SizeBytes = count,
                    RecordKey = CultMeshCdnArtifactChunk.CreateRecordKey(chunk).Value
                });
            }

            if (payload.Length == 0)
            {
                var chunkHash = HashBytes(Array.Empty<byte>());
                var chunk = new CultMeshCdnArtifactChunk
                {
                    ChunkHash = chunkHash,
                    SizeBytes = 0,
                    Payload = Array.Empty<byte>()
                };
                chunks.Add(chunk);
                refs.Add(new CultMeshCdnChunkRef
                {
                    ChunkHash = chunkHash,
                    Offset = 0,
                    SizeBytes = 0,
                    RecordKey = CultMeshCdnArtifactChunk.CreateRecordKey(chunk).Value
                });
            }

            var manifest = new CultMeshCdnArtifactManifest
            {
                ArtifactId = artifactId,
                Kind = string.IsNullOrWhiteSpace(options.Kind) ? CultMeshCdnArtifactKinds.Asset : options.Kind,
                Version = options.Version ?? string.Empty,
                ContentHash = HashBytes(payload),
                SizeBytes = payload.LongLength,
                MimeType = string.IsNullOrWhiteSpace(options.MimeType) ? "application/octet-stream" : options.MimeType,
                CreatedAtUtc = options.CreatedAtUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Chunks = refs.ToArray(),
                Tags = options.Tags ?? Array.Empty<string>(),
                Metadata = options.Metadata ?? new Dictionary<string, string>()
            };

            return new CultMeshCdnArtifact(manifest, chunks);
        }

        /// <summary>
        /// Publishes all chunk documents and the manifest into a local CultCache.
        /// </summary>
        public static async Task<CultMeshCdnArtifactManifest> PublishAsync(
            CultCache cache,
            CultMeshCdnArtifact artifact)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (artifact == null) throw new ArgumentNullException(nameof(artifact));

            ValidateArtifact(artifact);
            foreach (var chunk in artifact.Chunks)
            {
                await cache.UpsertAsync(
                    chunk,
                    new CultRecordHandle<CultMeshCdnArtifactChunk>(
                        CultMeshCdnArtifactChunk.CreateRecordKey(chunk))).ConfigureAwait(false);
            }

            await cache.UpsertAsync(
                artifact.Manifest,
                new CultRecordHandle<CultMeshCdnArtifactManifest>(
                    CultMeshCdnArtifactManifest.CreateRecordKey(artifact.Manifest))).ConfigureAwait(false);

            return artifact.Manifest;
        }

        /// <summary>
        /// Publishes all chunk documents and the manifest into a distributed CultNet database.
        /// </summary>
        public static async Task<CultMeshCdnArtifactManifest> PublishAsync(
            CultNetDatabase database,
            CultMeshCdnArtifact artifact)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (artifact == null) throw new ArgumentNullException(nameof(artifact));

            ValidateArtifact(artifact);
            foreach (var chunk in artifact.Chunks)
            {
                await database.PutAsync(CultMeshCdnArtifactChunk.CreateRecordKey(chunk), chunk)
                    .ConfigureAwait(false);
            }

            await database.PutAsync(CultMeshCdnArtifactManifest.CreateRecordKey(artifact.Manifest), artifact.Manifest)
                .ConfigureAwait(false);
            return artifact.Manifest;
        }

        /// <summary>
        /// Reassembles and verifies an artifact from a local CultCache.
        /// </summary>
        public static byte[] ReadArtifact(CultCache cache, CultMeshCdnArtifactManifest manifest)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            ValidateManifestShape(manifest);
            var output = new byte[checked((int)manifest.SizeBytes)];
            foreach (var reference in manifest.Chunks.OrderBy(chunk => chunk.Offset))
            {
                var recordKey = string.IsNullOrWhiteSpace(reference.RecordKey)
                    ? CultMeshCdnArtifactChunk.CreateRecordKey(reference.ChunkHash)
                    : new CultRecordKey(reference.RecordKey);
                var chunk = cache.Get<CultMeshCdnArtifactChunk>(recordKey)
                    ?? throw new FileNotFoundException("CDN artifact chunk is missing from the cache.", recordKey.Value);
                ValidateChunk(reference, chunk);
                Buffer.BlockCopy(chunk.Payload, 0, output, checked((int)reference.Offset), reference.SizeBytes);
            }

            var actualHash = HashBytes(output);
            if (!string.Equals(NormalizeHash(manifest.ContentHash, nameof(manifest.ContentHash)), actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("CDN artifact content hash does not match its manifest.");
            }

            return output;
        }

        /// <summary>
        /// Reassembles and verifies an artifact from a distributed CultNet database.
        /// </summary>
        public static async Task<byte[]> ReadArtifactAsync(
            CultNetDatabase database,
            CultMeshCdnArtifactManifest manifest)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            ValidateManifestShape(manifest);
            var output = new byte[checked((int)manifest.SizeBytes)];
            foreach (var reference in manifest.Chunks.OrderBy(chunk => chunk.Offset))
            {
                var recordKey = string.IsNullOrWhiteSpace(reference.RecordKey)
                    ? CultMeshCdnArtifactChunk.CreateRecordKey(reference.ChunkHash)
                    : new CultRecordKey(reference.RecordKey);
                var chunk = await database.GetAsync<CultMeshCdnArtifactChunk>(recordKey).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("CDN artifact chunk is missing from the database.", recordKey.Value);
                ValidateChunk(reference, chunk);
                Buffer.BlockCopy(chunk.Payload, 0, output, checked((int)reference.Offset), reference.SizeBytes);
            }

            var actualHash = HashBytes(output);
            if (!string.Equals(NormalizeHash(manifest.ContentHash, nameof(manifest.ContentHash)), actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("CDN artifact content hash does not match its manifest.");
            }

            return output;
        }

        /// <summary>
        /// Creates a raw CultNet document registry for CDN manifests and chunks.
        /// </summary>
        public static CultNetDocumentRegistry CreateDocumentRegistry(CultDocumentRegistry? documents = null)
        {
            documents ??= CultDocumentRegistry.Shared;
            return new CultNetDocumentRegistry(documents)
                .Register(CultNetDocumentBinding.ForDocument<CultMeshCdnArtifactManifest>(documents))
                .Register(CultNetDocumentBinding.ForDocument<CultMeshCdnArtifactChunk>(documents));
        }

        internal static string HashBytes(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void ValidateArtifact(CultMeshCdnArtifact artifact)
        {
            ValidateManifestShape(artifact.Manifest);
            var expectedRefs = artifact.Chunks
                .Select(chunk => new
                {
                    Hash = HashBytes(chunk.Payload),
                    ChunkHash = NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash)),
                    chunk.SizeBytes,
                    Key = CultMeshCdnArtifactChunk.CreateRecordKey(chunk).Value
                })
                .ToArray();

            if (artifact.Manifest.Chunks.Length != expectedRefs.Length)
            {
                throw new InvalidDataException("CDN artifact manifest chunk count does not match chunk payloads.");
            }

            for (var i = 0; i < expectedRefs.Length; i++)
            {
                var reference = artifact.Manifest.Chunks[i];
                var expected = expectedRefs[i];
                if (!string.Equals(NormalizeHash(reference.ChunkHash, nameof(reference.ChunkHash)), expected.Hash, StringComparison.Ordinal) ||
                    !string.Equals(expected.ChunkHash, expected.Hash, StringComparison.Ordinal) ||
                    reference.SizeBytes != expected.SizeBytes ||
                    !string.Equals(reference.RecordKey, expected.Key, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("CDN artifact manifest chunk reference does not match its chunk payload.");
                }
            }

            var materialized = new byte[checked((int)artifact.Manifest.SizeBytes)];
            foreach (var reference in artifact.Manifest.Chunks.OrderBy(chunk => chunk.Offset))
            {
                var chunk = artifact.Chunks.First(candidate =>
                    string.Equals(
                        CultMeshCdnArtifactChunk.CreateRecordKey(candidate).Value,
                        reference.RecordKey,
                        StringComparison.Ordinal));
                Buffer.BlockCopy(chunk.Payload, 0, materialized, checked((int)reference.Offset), reference.SizeBytes);
            }

            if (!string.Equals(
                NormalizeHash(artifact.Manifest.ContentHash, nameof(artifact.Manifest.ContentHash)),
                HashBytes(materialized),
                StringComparison.Ordinal))
            {
                throw new InvalidDataException("CDN artifact content hash does not match its manifest.");
            }
        }

        private static void ValidateManifestShape(CultMeshCdnArtifactManifest manifest)
        {
            if (manifest.SizeBytes < 0)
            {
                throw new InvalidDataException("CDN artifact manifest has a negative size.");
            }

            if (manifest.SizeBytes > int.MaxValue)
            {
                throw new InvalidDataException("CDN artifact is too large to materialize into one byte array.");
            }

            var expectedOffset = 0L;
            foreach (var reference in manifest.Chunks.OrderBy(chunk => chunk.Offset))
            {
                if (reference.Offset != expectedOffset)
                {
                    throw new InvalidDataException("CDN artifact chunks are not contiguous.");
                }

                if (reference.SizeBytes < 0)
                {
                    throw new InvalidDataException("CDN artifact chunk has a negative size.");
                }

                expectedOffset += reference.SizeBytes;
            }

            if (expectedOffset != manifest.SizeBytes)
            {
                throw new InvalidDataException("CDN artifact chunk sizes do not sum to the manifest size.");
            }
        }

        private static void ValidateChunk(CultMeshCdnChunkRef reference, CultMeshCdnArtifactChunk chunk)
        {
            if (chunk.Payload.Length != reference.SizeBytes ||
                chunk.SizeBytes != reference.SizeBytes)
            {
                throw new InvalidDataException("CDN artifact chunk payload metadata does not match its manifest reference.");
            }

            var actualHash = HashBytes(chunk.Payload);
            if (!string.Equals(NormalizeHash(reference.ChunkHash, nameof(reference.ChunkHash)), actualHash, StringComparison.Ordinal) ||
                !string.Equals(NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash)), actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("CDN artifact chunk hash does not match its payload.");
            }
        }

        internal static string NormalizeHash(string hash, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                throw new ArgumentException("Hash must be non-empty.", parameterName);
            }

            var value = hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? hash.Substring("sha256:".Length)
                : hash;
            return value.Trim().ToLowerInvariant();
        }

        internal static string StableToken(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be non-empty.", parameterName);
            }

            var normalized = value.Trim().ToLowerInvariant();
            var builder = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-');
            }

            var token = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(token)
                ? HashBytes(Encoding.UTF8.GetBytes(value))
                : token;
        }
    }
}
