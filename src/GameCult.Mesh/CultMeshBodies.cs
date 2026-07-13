using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using MessagePack;

#nullable enable

namespace GameCult.Mesh
{
    public enum CultMeshBodyAccessMode
    {
        ReadOnly = 0,
        ReadWrite = 1
    }

    public enum CultMeshBodySynchronization
    {
        ImmutableSequence = 0,
        EpochSequence = 1,
        TripleBuffer = 2
    }

    public enum CultMeshBodyTransportKind
    {
        Network = 0,
        SharedFileMapping = 1,
        SharedMemory = 2
    }

    [MessagePackObject]
    public sealed class CultMeshBodyDescriptor
    {
        [Key(0)] public string BodyId { get; set; } = string.Empty;
        [Key(1)] public string SchemaId { get; set; } = string.Empty;
        [Key(2)] public int LayoutVersion { get; set; }
        [Key(3)] public long ByteSize { get; set; }
        [Key(4)] public int Capacity { get; set; }
        [Key(5)] public long ProducerEpoch { get; set; }
        [Key(6)] public long Sequence { get; set; }
        [Key(7)] public CultMeshBodyAccessMode AccessMode { get; set; } = CultMeshBodyAccessMode.ReadOnly;
        [Key(8)] public CultMeshBodySynchronization Synchronization { get; set; } = CultMeshBodySynchronization.ImmutableSequence;
        [Key(9)] public long LeaseExpiresAtUnixMs { get; set; }
        [Key(10)] public CultMeshBodyTransportKind TransportKind { get; set; } = CultMeshBodyTransportKind.Network;
        [Key(11)] public string CapabilityToken { get; set; } = string.Empty;
        [Key(12)] public string SemanticHash { get; set; } = string.Empty;
    }

    public sealed class CultMeshBodyValidationRequest
    {
        public string BodyId { get; set; } = string.Empty;
        public string SchemaId { get; set; } = string.Empty;
        public int LayoutVersion { get; set; }
        public long ProducerEpoch { get; set; }
        public long? Sequence { get; set; }
        public int? Capacity { get; set; }
        public CultMeshBodyAccessMode AccessMode { get; set; } = CultMeshBodyAccessMode.ReadOnly;
        public DateTimeOffset NowUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public static class CultMeshBodyDescriptorValidator
    {
        public static string ComputeSemanticHash(ReadOnlySpan<byte> body)
        {
            using var sha256 = SHA256.Create();
            Span<byte> digest = stackalloc byte[32];
            if (!sha256.TryComputeHash(body, digest, out var written) || written != digest.Length)
                throw new CryptographicException("Unable to compute the CultMesh body semantic digest.");
            return BitConverter.ToString(digest.ToArray())
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        public static void Validate(CultMeshBodyDescriptor descriptor, CultMeshBodyValidationRequest request)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(descriptor.BodyId) ||
                !string.Equals(descriptor.BodyId, request.BodyId, StringComparison.Ordinal))
                throw new InvalidOperationException("CultMesh body identity mismatch.");
            if (string.IsNullOrWhiteSpace(descriptor.SchemaId) ||
                !string.Equals(descriptor.SchemaId, request.SchemaId, StringComparison.Ordinal))
                throw new InvalidOperationException("CultMesh body schema mismatch.");
            if (descriptor.LayoutVersion != request.LayoutVersion)
                throw new InvalidOperationException("CultMesh body layout version mismatch.");
            if (descriptor.ProducerEpoch != request.ProducerEpoch)
                throw new InvalidOperationException("CultMesh body producer epoch mismatch.");
            if (request.Sequence.HasValue && descriptor.Sequence != request.Sequence.Value)
                throw new InvalidOperationException("CultMesh body sequence mismatch.");
            if (request.Capacity.HasValue && descriptor.Capacity != request.Capacity.Value)
                throw new InvalidOperationException("CultMesh body capacity mismatch.");
            if (descriptor.ByteSize < 0 || descriptor.Capacity < 0)
                throw new InvalidOperationException("CultMesh body size and logical capacity must be non-negative.");
            if (descriptor.Sequence < 0)
                throw new InvalidOperationException("CultMesh body sequence must be non-negative.");
            if (descriptor.LeaseExpiresAtUnixMs <= request.NowUtc.ToUnixTimeMilliseconds())
                throw new InvalidOperationException("CultMesh body lease has expired.");
            if (request.AccessMode != CultMeshBodyAccessMode.ReadOnly ||
                descriptor.AccessMode != CultMeshBodyAccessMode.ReadOnly)
                throw new UnauthorizedAccessException("CultMesh body consumers are read-only by default.");
        }
    }

    public sealed class CultMeshBodyNegotiationResult
    {
        internal CultMeshBodyNegotiationResult(
            ICultMeshBodyReadLease lease,
            CultMeshBodyTransportKind preferredTransport,
            Exception? preferredFailure)
        {
            Lease = lease;
            PreferredTransport = preferredTransport;
            PreferredFailure = preferredFailure;
        }

        public ICultMeshBodyReadLease Lease { get; }
        public CultMeshBodyTransportKind PreferredTransport { get; }
        public CultMeshBodyTransportKind SelectedTransport => Lease.TransportKind;
        public Exception? PreferredFailure { get; }
        public bool UsedFallback => SelectedTransport != PreferredTransport;
    }

    public interface ICultMeshBodyReadLease : IDisposable
    {
        CultMeshBodyDescriptor Descriptor { get; }
        CultMeshBodyTransportKind TransportKind { get; }
        byte ReadByte(long offset);
        int ReadInt32(long offset);
        long ReadInt64(long offset);
        float ReadSingle(long offset);
        double ReadDouble(long offset);
        int CopyTo(long offset, byte[] destination, int destinationOffset, int count);
    }

    public interface ICultMeshBodyTransportAdapter
    {
        CultMeshBodyTransportKind TransportKind { get; }
        bool CanOpen(CultMeshBodyDescriptor descriptor);
        ICultMeshBodyReadLease OpenReadOnly(
            CultMeshBodyDescriptor descriptor,
            CultMeshBodyValidationRequest request);
    }

    public sealed class CultMeshBodyTransportService
    {
        private readonly IReadOnlyDictionary<CultMeshBodyTransportKind, ICultMeshBodyTransportAdapter> _adapters;
        private readonly Func<CultMeshBodyDescriptor, bool> _authorizeProducer;

        public CultMeshBodyTransportService(
            IEnumerable<ICultMeshBodyTransportAdapter> adapters,
            Func<CultMeshBodyDescriptor, bool> authorizeProducer)
        {
            _adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters)))
                .ToDictionary(adapter => adapter.TransportKind);
            _authorizeProducer = authorizeProducer ?? throw new ArgumentNullException(nameof(authorizeProducer));
        }

        public ICultMeshBodyReadLease OpenReadOnly(
            CultMeshBodyDescriptor preferred,
            CultMeshBodyDescriptor networkFallback,
            CultMeshBodyValidationRequest request,
            out Exception? localFailure)
        {
            var result = NegotiateReadOnly(preferred, networkFallback, request);
            localFailure = result.PreferredFailure;
            return result.Lease;
        }

        public CultMeshBodyNegotiationResult NegotiateReadOnly(
            CultMeshBodyDescriptor preferred,
            CultMeshBodyDescriptor networkFallback,
            CultMeshBodyValidationRequest request)
        {
            if (preferred == null) throw new ArgumentNullException(nameof(preferred));
            if (networkFallback == null) throw new ArgumentNullException(nameof(networkFallback));
            if (!SameLogicalGeneration(preferred, networkFallback))
                throw new InvalidOperationException("CultMesh local and network representations describe different logical body generations.");
            CultMeshBodyDescriptorValidator.Validate(preferred, request);
            CultMeshBodyDescriptorValidator.Validate(networkFallback, request);
            if (!_authorizeProducer(preferred))
                throw new UnauthorizedAccessException("CultMesh body producer is not authorized for this logical body.");

            Exception? preferredFailure = null;
            try
            {
                if (!_adapters.TryGetValue(preferred.TransportKind, out var local))
                    throw new NotSupportedException($"No CultMesh {preferred.TransportKind} body adapter is available.");
                if (!local.CanOpen(preferred))
                    throw new NotSupportedException($"The CultMesh {preferred.TransportKind} body adapter declined the preferred descriptor.");
                var lease = local.OpenReadOnly(preferred, request);
                return new CultMeshBodyNegotiationResult(lease, preferred.TransportKind, null);
            }
            catch (Exception error) when (!(error is UnauthorizedAccessException))
            {
                preferredFailure = error;
            }

            if (!_adapters.TryGetValue(CultMeshBodyTransportKind.Network, out var network) || !network.CanOpen(networkFallback))
                throw new NotSupportedException("No CultMesh network body fallback is available.");
            var fallback = network.OpenReadOnly(networkFallback, request);
            return new CultMeshBodyNegotiationResult(fallback, preferred.TransportKind, preferredFailure);
        }

        private static bool SameLogicalGeneration(CultMeshBodyDescriptor left, CultMeshBodyDescriptor right) =>
            string.Equals(left.BodyId, right.BodyId, StringComparison.Ordinal) &&
            string.Equals(left.SchemaId, right.SchemaId, StringComparison.Ordinal) &&
            left.LayoutVersion == right.LayoutVersion &&
            left.ProducerEpoch == right.ProducerEpoch &&
            left.Sequence == right.Sequence &&
            left.ByteSize == right.ByteSize &&
            left.Capacity == right.Capacity &&
            left.AccessMode == right.AccessMode &&
            left.Synchronization == right.Synchronization &&
            string.Equals(left.SemanticHash, right.SemanticHash, StringComparison.Ordinal);
    }

    public sealed class CultMeshMappedBodyPublisher
    {
        private readonly string _rootPath;
        private readonly TimeSpan _leaseDuration;

        public CultMeshMappedBodyPublisher(string rootPath, TimeSpan? leaseDuration = null)
        {
            _rootPath = string.IsNullOrWhiteSpace(rootPath)
                ? throw new ArgumentException("Mapped body root is required.", nameof(rootPath))
                : Path.GetFullPath(rootPath);
            _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(5);
            if (_leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            Directory.CreateDirectory(_rootPath);
        }

        public CultMeshBodyDescriptor Publish(
            string bodyId,
            string schemaId,
            int layoutVersion,
            int logicalCapacity,
            long producerEpoch,
            long sequence,
            ReadOnlySpan<byte> body,
            DateTimeOffset nowUtc)
        {
            if (string.IsNullOrWhiteSpace(bodyId)) throw new ArgumentException("Body identity is required.", nameof(bodyId));
            if (string.IsNullOrWhiteSpace(schemaId)) throw new ArgumentException("Schema identity is required.", nameof(schemaId));
            if (layoutVersion < 0) throw new ArgumentOutOfRangeException(nameof(layoutVersion));
            if (logicalCapacity < 0) throw new ArgumentOutOfRangeException(nameof(logicalCapacity));
            if (producerEpoch < 0) throw new ArgumentOutOfRangeException(nameof(producerEpoch));
            if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));

            var token = CreateCapabilityToken();
            var finalPath = ResolveTokenPath(token);
            var temporaryPath = finalPath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(body);
            File.Move(temporaryPath, finalPath);
            return new CultMeshBodyDescriptor
            {
                BodyId = bodyId,
                SchemaId = schemaId,
                LayoutVersion = layoutVersion,
                ByteSize = body.Length,
                Capacity = logicalCapacity,
                ProducerEpoch = producerEpoch,
                Sequence = sequence,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                Synchronization = CultMeshBodySynchronization.ImmutableSequence,
                LeaseExpiresAtUnixMs = nowUtc.Add(_leaseDuration).ToUnixTimeMilliseconds(),
                TransportKind = CultMeshBodyTransportKind.SharedFileMapping,
                CapabilityToken = token,
                SemanticHash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(body)
            };
        }

        public void Revoke(CultMeshBodyDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.CapabilityToken)) return;
            var path = ResolveTokenPath(descriptor.CapabilityToken);
            if (File.Exists(path)) File.Delete(path);
        }

        private string ResolveTokenPath(string token)
        {
            if (token.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || token.Contains(".."))
                throw new InvalidOperationException("Invalid CultMesh body capability token.");
            var path = Path.GetFullPath(Path.Combine(_rootPath, token + ".body"));
            if (!path.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CultMesh body token escaped the broker root.");
            return path;
        }

        private static string CreateCapabilityToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    public sealed class CultMeshMappedBodyAdapter : ICultMeshBodyTransportAdapter
    {
        private readonly string? _rootPath;
        private readonly CultMeshVerifiedBodyMappingBroker? _verifiedBodies;

        public CultMeshMappedBodyAdapter(string rootPath) =>
            _rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath)
                ? throw new ArgumentException("Mapped body root is required.", nameof(rootPath))
                : rootPath);

        public CultMeshMappedBodyAdapter(CultMeshVerifiedBodyMappingBroker verifiedBodies) =>
            _verifiedBodies = verifiedBodies ?? throw new ArgumentNullException(nameof(verifiedBodies));

        public CultMeshBodyTransportKind TransportKind => CultMeshBodyTransportKind.SharedFileMapping;

        public bool CanOpen(CultMeshBodyDescriptor descriptor) =>
            descriptor != null && descriptor.TransportKind == TransportKind;

        public ICultMeshBodyReadLease OpenReadOnly(
            CultMeshBodyDescriptor descriptor,
            CultMeshBodyValidationRequest request)
        {
            CultMeshBodyDescriptorValidator.Validate(descriptor, request);
            if (descriptor.TransportKind != CultMeshBodyTransportKind.SharedFileMapping)
                throw new NotSupportedException($"CultMesh body transport '{descriptor.TransportKind}' is not a file mapping.");
            if (string.IsNullOrWhiteSpace(descriptor.CapabilityToken))
                throw new InvalidOperationException("Mapped CultMesh body descriptor has no transport capability token.");
            var path = _verifiedBodies != null
                ? _verifiedBodies.Resolve(descriptor.CapabilityToken, request.NowUtc)
                : ResolvePublisherPath(descriptor.CapabilityToken);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != descriptor.ByteSize)
                throw new InvalidDataException("Mapped CultMesh body size does not match its descriptor.");
            return new CultMeshMappedBodyLease(descriptor, path);
        }

        private string ResolvePublisherPath(string token)
        {
            if (token.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || token.Contains(".."))
                throw new UnauthorizedAccessException("Invalid CultMesh body capability token.");
            var path = Path.GetFullPath(Path.Combine(_rootPath!, token + ".body"));
            if (!path.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("CultMesh body token escaped the broker root.");
            return path;
        }
    }

    public sealed class CultMeshVerifiedBodyMappingBroker
    {
        private sealed class Grant
        {
            public string Path { get; set; } = string.Empty;
            public long ExpiresAtUnixMs { get; set; }
        }

        private readonly string _cacheDirectory;
        private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);

        public CultMeshVerifiedBodyMappingBroker(string cacheDirectory) =>
            _cacheDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(cacheDirectory)
                ? throw new ArgumentException("Verified body cache directory is required.", nameof(cacheDirectory))
                : cacheDirectory);

        internal CultMeshBodyDescriptor GrantVerified(
            string contentHash,
            string verifiedPath,
            CultMeshBodyDescriptor networkDescriptor,
            DateTimeOffset expiresAtUtc)
        {
            if (networkDescriptor == null) throw new ArgumentNullException(nameof(networkDescriptor));
            var expectedPath = Path.Combine(_cacheDirectory, contentHash + ".body");
            if (!string.Equals(Path.GetFullPath(verifiedPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the transfer-owned verified body may be mapped.");
            var info = new FileInfo(expectedPath);
            if (!info.Exists || info.Length != networkDescriptor.ByteSize)
                throw new InvalidDataException("Verified CultMesh body size does not match its network descriptor.");

            var token = CreateCapabilityToken();
            var expiresAtUnixMs = expiresAtUtc.ToUnixTimeMilliseconds();
            _grants[token] = new Grant { Path = expectedPath, ExpiresAtUnixMs = expiresAtUnixMs };
            return new CultMeshBodyDescriptor
            {
                BodyId = networkDescriptor.BodyId,
                SchemaId = networkDescriptor.SchemaId,
                LayoutVersion = networkDescriptor.LayoutVersion,
                ByteSize = networkDescriptor.ByteSize,
                Capacity = networkDescriptor.Capacity,
                ProducerEpoch = networkDescriptor.ProducerEpoch,
                Sequence = networkDescriptor.Sequence,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                Synchronization = networkDescriptor.Synchronization,
                LeaseExpiresAtUnixMs = expiresAtUnixMs,
                TransportKind = CultMeshBodyTransportKind.SharedFileMapping,
                CapabilityToken = token,
                SemanticHash = networkDescriptor.SemanticHash
            };
        }

        internal string Resolve(string token, DateTimeOffset nowUtc)
        {
            if (!_grants.TryGetValue(token, out var grant))
                throw new UnauthorizedAccessException("Unknown verified body capability token.");
            if (grant.ExpiresAtUnixMs <= nowUtc.ToUnixTimeMilliseconds())
            {
                _grants.TryRemove(token, out _);
                throw new InvalidOperationException("Verified body capability has expired.");
            }
            return grant.Path;
        }

        private static string CreateCapabilityToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    public sealed class CultMeshSharedMemoryBodyAdapter : ICultMeshBodyTransportAdapter
    {
        public CultMeshBodyTransportKind TransportKind => CultMeshBodyTransportKind.SharedMemory;
        public bool CanOpen(CultMeshBodyDescriptor descriptor) =>
            descriptor != null && descriptor.TransportKind == TransportKind;

        public ICultMeshBodyReadLease OpenReadOnly(
            CultMeshBodyDescriptor descriptor,
            CultMeshBodyValidationRequest request)
        {
            CultMeshBodyDescriptorValidator.Validate(descriptor, request);
            if (string.IsNullOrWhiteSpace(descriptor.CapabilityToken))
                throw new InvalidOperationException("Shared-memory CultMesh body descriptor has no transport capability token.");
            if (CultMeshFrameBodyPublisher.IsFrameCapability(descriptor.CapabilityToken))
                return CultMeshFrameBodyPublisher.OpenReadOnly(descriptor);
            MemoryMappedFile mapping;
            try
            {
                mapping = MemoryMappedFile.OpenExisting(descriptor.CapabilityToken, MemoryMappedFileRights.Read);
            }
            catch (FileNotFoundException error)
            {
                throw new InvalidDataException("CultMesh shared-memory capability is unavailable or revoked.", error);
            }
            try
            {
                return new CultMeshMappedBodyLease(descriptor, mapping);
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }
    }

    public sealed class CultMeshMappedBodyLease : ICultMeshBodyReadLease
    {
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private bool _disposed;

        internal CultMeshMappedBodyLease(CultMeshBodyDescriptor descriptor, string path)
            : this(descriptor, MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        {
        }

        internal CultMeshMappedBodyLease(
            CultMeshBodyDescriptor descriptor,
            MemoryMappedFile mapping,
            long offset = 0,
            Action? release = null)
        {
            Descriptor = descriptor;
            _mapping = mapping;
            _release = release;
            _view = _mapping.CreateViewAccessor(offset, descriptor.ByteSize, MemoryMappedFileAccess.Read);
        }

        private readonly Action? _release;

        public CultMeshBodyDescriptor Descriptor { get; }
        public CultMeshBodyTransportKind TransportKind => Descriptor.TransportKind;
        public byte ReadByte(long offset) { ValidateRange(offset, 1); return _view.ReadByte(offset); }
        public int ReadInt32(long offset) { ValidateRange(offset, sizeof(int)); return _view.ReadInt32(offset); }
        public long ReadInt64(long offset) { ValidateRange(offset, sizeof(long)); return _view.ReadInt64(offset); }
        public float ReadSingle(long offset) { ValidateRange(offset, sizeof(float)); return _view.ReadSingle(offset); }
        public double ReadDouble(long offset) { ValidateRange(offset, sizeof(double)); return _view.ReadDouble(offset); }
        public int CopyTo(long offset, byte[] destination, int destinationOffset, int count)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            ValidateRange(offset, count);
            return _view.ReadArray(offset, destination, destinationOffset, count);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view.Dispose();
            _mapping.Dispose();
            _release?.Invoke();
        }

        private void ValidateRange(long offset, long length)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshMappedBodyLease));
            if (offset < 0 || length < 0 || offset > Descriptor.ByteSize - length)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    public sealed class CultMeshNetworkBodyAdapter : ICultMeshBodyTransportAdapter
    {
        private readonly Func<CultMeshBodyDescriptor, byte[]> _fetch;

        public CultMeshNetworkBodyAdapter(Func<CultMeshBodyDescriptor, byte[]> fetch) =>
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));

        public CultMeshBodyTransportKind TransportKind => CultMeshBodyTransportKind.Network;
        public bool CanOpen(CultMeshBodyDescriptor descriptor) =>
            descriptor != null && descriptor.TransportKind == TransportKind;

        public ICultMeshBodyReadLease OpenReadOnly(
            CultMeshBodyDescriptor descriptor,
            CultMeshBodyValidationRequest request)
        {
            CultMeshBodyDescriptorValidator.Validate(descriptor, request);
            var bytes = _fetch(descriptor) ?? throw new InvalidDataException("CultMesh network body returned no bytes.");
            if (bytes.LongLength != descriptor.ByteSize)
                throw new InvalidDataException("CultMesh network body size does not match its descriptor.");
            if (string.IsNullOrWhiteSpace(descriptor.SemanticHash))
                throw new InvalidDataException("CultMesh network body descriptor has no semantic digest.");
            var actualHash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(bytes);
            if (!string.Equals(actualHash, descriptor.SemanticHash, StringComparison.Ordinal))
                throw new InvalidDataException("CultMesh network body semantic digest does not match its descriptor.");
            return new CultMeshBufferedBodyLease(descriptor, bytes);
        }
    }

    internal sealed class CultMeshBufferedBodyLease : ICultMeshBodyReadLease
    {
        private byte[] _body;
        public CultMeshBufferedBodyLease(CultMeshBodyDescriptor descriptor, byte[] body)
        {
            Descriptor = descriptor;
            _body = body;
        }
        public CultMeshBodyDescriptor Descriptor { get; }
        public CultMeshBodyTransportKind TransportKind => CultMeshBodyTransportKind.Network;
        public byte ReadByte(long offset) { Validate(offset, 1); return _body[(int)offset]; }
        public int ReadInt32(long offset) { Validate(offset, 4); return BitConverter.ToInt32(_body, (int)offset); }
        public long ReadInt64(long offset) { Validate(offset, 8); return BitConverter.ToInt64(_body, (int)offset); }
        public float ReadSingle(long offset) { Validate(offset, 4); return BitConverter.ToSingle(_body, (int)offset); }
        public double ReadDouble(long offset) { Validate(offset, 8); return BitConverter.ToDouble(_body, (int)offset); }
        public int CopyTo(long offset, byte[] destination, int destinationOffset, int count)
        {
            Validate(offset, count);
            Buffer.BlockCopy(_body, (int)offset, destination, destinationOffset, count);
            return count;
        }
        public void Dispose() { _body = Array.Empty<byte>(); }
        private void Validate(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > _body.LongLength - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
