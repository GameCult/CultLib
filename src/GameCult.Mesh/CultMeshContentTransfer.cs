using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    public static class CultMeshContentTransferSchemaVersions
    {
        public const string State = "gamecult.mesh.content_transfer_state.v1";
    }

    [MessagePackObject]
    [CultDocument("gamecult.mesh.content_transfer_state", CultMeshContentTransferSchemaVersions.State)]
    public sealed class CultMeshContentTransferStateDocument
    {
        [Key(0), CultName] public string ContentHash { get; set; } = string.Empty;
        [Key(1)] public string ManifestFingerprint { get; set; } = string.Empty;
        [Key(2)] public long SizeBytes { get; set; }
        [Key(3)] public int[] VerifiedChunkIndexes { get; set; } = Array.Empty<int>();
        [Key(4)] public string UpdatedAtUtc { get; set; } = string.Empty;
    }

    public interface ICultMeshContentProvider
    {
        string ProviderId { get; }
        Task<CultMeshCdnArtifactChunk?> GetChunkAsync(CultMeshCdnChunkRef chunk, CancellationToken cancellationToken = default);
    }

    public sealed class CultMeshDatabaseContentProvider : ICultMeshContentProvider
    {
        private readonly CultNetDatabase _database;

        public CultMeshDatabaseContentProvider(string providerId, CultNetDatabase database)
        {
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? throw new ArgumentException("Provider id must be non-empty.", nameof(providerId)) : providerId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public string ProviderId { get; }

        public async Task<CultMeshCdnArtifactChunk?> GetChunkAsync(CultMeshCdnChunkRef chunk, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = string.IsNullOrWhiteSpace(chunk.RecordKey)
                ? CultMeshCdnArtifactChunk.CreateRecordKey(chunk.ChunkHash)
                : new CultRecordKey(chunk.RecordKey);
            var result = await _database.GetAsync<CultMeshCdnArtifactChunk>(key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    public sealed class CultMeshContentTransferOptions
    {
        public CultMeshContentTransferOptions(string cacheDirectory)
        {
            CacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
                ? throw new ArgumentException("Cache directory must be non-empty.", nameof(cacheDirectory))
                : Path.GetFullPath(cacheDirectory);
        }

        public string CacheDirectory { get; }
    }

    public sealed class CultMeshContentTransferService
    {
        private readonly CultCache _stateCache;
        private readonly ICultMeshContentProvider[] _providers;
        private readonly string _cacheDirectory;
        private readonly CultMeshVerifiedBodyMappingBroker? _mappedBodies;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _contentLocks = new(StringComparer.Ordinal);

        public CultMeshContentTransferService(
            CultCache stateCache,
            IEnumerable<ICultMeshContentProvider> providers,
            CultMeshContentTransferOptions options,
            CultMeshVerifiedBodyMappingBroker? mappedBodies = null)
        {
            _stateCache = stateCache ?? throw new ArgumentNullException(nameof(stateCache));
            _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
            if (_providers.Length == 0) throw new ArgumentException("At least one content provider is required.", nameof(providers));
            if (_providers.Any(provider => provider == null)) throw new ArgumentException("Content providers cannot contain null entries.", nameof(providers));
            _cacheDirectory = (options ?? throw new ArgumentNullException(nameof(options))).CacheDirectory;
            _mappedBodies = mappedBodies;
        }

        public async Task<CultMeshBodyDescriptor> FetchMappedBodyAsync(
            CultMeshCdnArtifactManifest manifest,
            CultMeshBodyDescriptor networkDescriptor,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            if (_mappedBodies == null)
                throw new InvalidOperationException("Verified body mapping is not configured for this transfer service.");
            if (networkDescriptor == null) throw new ArgumentNullException(nameof(networkDescriptor));
            if (networkDescriptor.TransportKind != CultMeshBodyTransportKind.Network)
                throw new ArgumentException("The fallback descriptor must use the network body transport.", nameof(networkDescriptor));
            if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            CultMeshCdn.ValidateManifest(manifest);
            if (networkDescriptor.ByteSize != manifest.SizeBytes)
                throw new InvalidDataException("CDN manifest size does not match its network body descriptor.");

            var path = await FetchAsync(manifest, cancellationToken).ConfigureAwait(false);
            var contentHash = CultMeshCdn.NormalizeHash(manifest.ContentHash, nameof(manifest.ContentHash));
            var requestedExpiry = nowUtc.Add(leaseDuration);
            var networkExpiry = DateTimeOffset.FromUnixTimeMilliseconds(networkDescriptor.LeaseExpiresAtUnixMs);
            var expiry = requestedExpiry <= networkExpiry ? requestedExpiry : networkExpiry;
            if (expiry <= nowUtc) throw new InvalidOperationException("Network body lease has expired.");
            return _mappedBodies.GrantVerified(contentHash, path, networkDescriptor, expiry);
        }

        public async Task<string> FetchAsync(
            CultMeshCdnArtifactManifest manifest,
            CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            CultMeshCdn.ValidateManifest(manifest);

            var contentHash = CultMeshCdn.NormalizeHash(manifest.ContentHash, nameof(manifest.ContentHash));
            var contentLock = _contentLocks.GetOrAdd(contentHash, _ => new SemaphoreSlim(1, 1));
            await contentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FetchOwnedAsync(manifest, contentHash, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                contentLock.Release();
            }
        }

        private async Task<string> FetchOwnedAsync(
            CultMeshCdnArtifactManifest manifest,
            string contentHash,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_cacheDirectory);
            var finalPath = Path.Combine(_cacheDirectory, contentHash + ".body");
            var partialPath = Path.Combine(_cacheDirectory, "." + contentHash + ".partial");
            var stateKey = StateKey(contentHash);

            if (File.Exists(finalPath))
            {
                if (await HashFileAsync(finalPath, cancellationToken).ConfigureAwait(false) == contentHash)
                    return finalPath;
                File.Delete(finalPath);
            }

            var fingerprint = ManifestFingerprint(manifest);
            var state = _stateCache.Get<CultMeshContentTransferStateDocument>(stateKey);
            var verified = RestoreVerifiedState(state, manifest, fingerprint, partialPath);

            using (var stream = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                stream.SetLength(manifest.SizeBytes);
                var ordered = manifest.Chunks
                    .Select((chunk, index) => new { Chunk = chunk, Index = index })
                    .OrderBy(item => item.Chunk.Offset)
                    .ToArray();

                foreach (var item in ordered.Where(item => !verified.Contains(item.Index)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = await FetchVerifiedChunkAsync(item.Chunk, cancellationToken).ConfigureAwait(false);
                    stream.Position = item.Chunk.Offset;
                    await stream.WriteAsync(chunk.Payload, 0, chunk.Payload.Length, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    verified.Add(item.Index);
                    await SaveStateAsync(contentHash, fingerprint, manifest.SizeBytes, verified, stateKey).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var actualHash = await HashFileAsync(partialPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, contentHash, StringComparison.Ordinal))
            {
                File.Delete(partialPath);
                _stateCache.Remove(stateKey);
                await _stateCache.FlushAsync().ConfigureAwait(false);
                throw new InvalidDataException("CDN artifact content hash does not match its manifest.");
            }

            PromoteAtomically(partialPath, finalPath);
            _stateCache.Remove(stateKey);
            await _stateCache.FlushAsync().ConfigureAwait(false);
            return finalPath;
        }

        private HashSet<int> RestoreVerifiedState(
            CultMeshContentTransferStateDocument? state,
            CultMeshCdnArtifactManifest manifest,
            string fingerprint,
            string partialPath)
        {
            if (state == null || !File.Exists(partialPath) ||
                state.SizeBytes != manifest.SizeBytes ||
                !string.Equals(state.ManifestFingerprint, fingerprint, StringComparison.Ordinal) ||
                new FileInfo(partialPath).Length != manifest.SizeBytes)
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
                return new HashSet<int>();
            }

            var verified = new HashSet<int>();
            using var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            foreach (var index in state.VerifiedChunkIndexes.Distinct().Where(index => index >= 0 && index < manifest.Chunks.Length))
            {
                var reference = manifest.Chunks[index];
                var bytes = new byte[reference.SizeBytes];
                stream.Position = reference.Offset;
                if (stream.Read(bytes, 0, bytes.Length) == bytes.Length &&
                    string.Equals(CultMeshCdn.HashBytes(bytes), CultMeshCdn.NormalizeHash(reference.ChunkHash, nameof(reference.ChunkHash)), StringComparison.Ordinal))
                {
                    verified.Add(index);
                }
            }

            return verified;
        }

        private async Task<CultMeshCdnArtifactChunk> FetchVerifiedChunkAsync(
            CultMeshCdnChunkRef reference,
            CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            foreach (var provider in _providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var chunk = await provider.GetChunkAsync(reference, cancellationToken).ConfigureAwait(false)
                        ?? throw new FileNotFoundException("CDN artifact chunk is missing from the provider.", reference.RecordKey);
                    CultMeshCdn.ValidateChunkPayload(reference, chunk);
                    return chunk;
                }
                catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    failures.Add(new InvalidDataException("Content provider '" + provider.ProviderId + "' could not supply a valid chunk.", error));
                }
            }

            throw new AggregateException("No content provider supplied a valid CDN chunk.", failures);
        }

        private async Task SaveStateAsync(
            string contentHash,
            string fingerprint,
            long sizeBytes,
            HashSet<int> verified,
            CultRecordKey stateKey)
        {
            var state = new CultMeshContentTransferStateDocument
            {
                ContentHash = contentHash,
                ManifestFingerprint = fingerprint,
                SizeBytes = sizeBytes,
                VerifiedChunkIndexes = verified.OrderBy(value => value).ToArray(),
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            await _stateCache.UpsertAsync(typeof(CultMeshContentTransferStateDocument), state, stateKey).ConfigureAwait(false);
            await _stateCache.FlushAsync().ConfigureAwait(false);
        }

        private static CultRecordKey StateKey(string contentHash) => new CultRecordKey("mesh:content-transfer:" + contentHash);

        private static string ManifestFingerprint(CultMeshCdnArtifactManifest manifest)
        {
            var shape = string.Join("|", manifest.Chunks.Select(chunk =>
                chunk.Offset.ToString(CultureInfo.InvariantCulture) + ":" +
                chunk.SizeBytes.ToString(CultureInfo.InvariantCulture) + ":" +
                CultMeshCdn.NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash))));
            return CultMeshCdn.HashBytes(System.Text.Encoding.UTF8.GetBytes(manifest.SizeBytes.ToString(CultureInfo.InvariantCulture) + "|" + shape));
        }

        private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return string.Concat(sha.Hash!.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void PromoteAtomically(string partialPath, string finalPath)
        {
            if (File.Exists(finalPath)) File.Replace(partialPath, finalPath, null);
            else File.Move(partialPath, finalPath);
        }
    }
}
