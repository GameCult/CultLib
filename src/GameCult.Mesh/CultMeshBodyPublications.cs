using System;
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
    public static class CultMeshBodyPublicationSchemaVersions
    {
        public const string Publication = "gamecult.mesh.body_publication.v1";
        public const string NetworkBody = "gamecult.mesh.network_body.v1";
    }

    public sealed class CultMeshBodyGeneration
    {
        public string BodyId { get; set; } = string.Empty;
        public string ProducerId { get; set; } = string.Empty;
        public string SchemaId { get; set; } = string.Empty;
        public int LayoutVersion { get; set; }
        public int Capacity { get; set; }
        public long ProducerEpoch { get; set; }
        public long Sequence { get; set; }
        public CultMeshBodySynchronization Synchronization { get; set; } = CultMeshBodySynchronization.ImmutableSequence;
        public long LeaseExpiresAtUnixMs { get; set; }
    }

    [MessagePackObject]
    [CultDocument("gamecult.mesh.network_body", CultMeshBodyPublicationSchemaVersions.NetworkBody)]
    public sealed class CultMeshNetworkBodyDocument
    {
        [Key(0), CultName] public string CapabilityToken { get; set; } = string.Empty;
        [Key(1), CultIndex] public string BodyId { get; set; } = string.Empty;
        [Key(2), CultIndex] public string ProducerId { get; set; } = string.Empty;
        [Key(3)] public string SchemaId { get; set; } = string.Empty;
        [Key(4)] public int LayoutVersion { get; set; }
        [Key(5)] public long ByteSize { get; set; }
        [Key(6)] public int Capacity { get; set; }
        [Key(7)] public long ProducerEpoch { get; set; }
        [Key(8)] public long Sequence { get; set; }
        [Key(9)] public CultMeshBodySynchronization Synchronization { get; set; }
        [Key(10)] public long LeaseExpiresAtUnixMs { get; set; }
        [Key(11)] public string SemanticHash { get; set; } = string.Empty;
        [Key(12)] public string ManifestRecordKey { get; set; } = string.Empty;

        public static CultRecordKey CreateRecordKey(string capabilityToken)
        {
            if (string.IsNullOrWhiteSpace(capabilityToken))
                throw new ArgumentException("Network body capability token is required.", nameof(capabilityToken));
            return new CultRecordKey("mesh:network-body:" + capabilityToken);
        }

        [IgnoreMember]
        public CultRecordKey RecordKey => CreateRecordKey(CapabilityToken);
    }

    public sealed class CultMeshNetworkBodyPublisher
    {
        private readonly CultCache _cache;
        private readonly Func<CultMeshBodyGeneration, bool> _authorizePublication;
        private readonly int _chunkSizeBytes;

        public CultMeshNetworkBodyPublisher(
            CultCache cache,
            Func<CultMeshBodyGeneration, bool> authorizePublication,
            int chunkSizeBytes = CultMeshCdnPackOptions.DefaultChunkSizeBytes)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _authorizePublication = authorizePublication ?? throw new ArgumentNullException(nameof(authorizePublication));
            if (chunkSizeBytes <= 0 || chunkSizeBytes > CultMeshCdnPackOptions.DefaultChunkSizeBytes)
                throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Network body chunks must be positive and no larger than the CultMesh CDN default bound.");
            _chunkSizeBytes = chunkSizeBytes;
        }

        public async Task<CultMeshBodyDescriptor> PublishAsync(CultMeshBodyGeneration generation, byte[] body)
        {
            ValidateGeneration(generation);
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (!_authorizePublication(generation))
                throw new UnauthorizedAccessException("CultMesh body producer is not authorized to publish this logical body.");

            var semanticHash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(body);
            var capabilityToken = CreateCapabilityToken(generation);
            var recordKey = CultMeshNetworkBodyDocument.CreateRecordKey(capabilityToken);
            var existing = _cache.Get<CultMeshNetworkBodyDocument>(recordKey);
            if (existing != null)
            {
                ValidateImmutableGeneration(existing, generation, body.LongLength, semanticHash);
                return CreateDescriptor(existing);
            }

            var artifact = CultMeshCdn.PackArtifact(
                generation.BodyId,
                body,
                new CultMeshCdnPackOptions
                {
                    ChunkSizeBytes = _chunkSizeBytes,
                    Kind = CultMeshCdnArtifactKinds.Package,
                    Version = generation.ProducerEpoch.ToString(CultureInfo.InvariantCulture) + "." + generation.Sequence.ToString(CultureInfo.InvariantCulture)
                });
            await CultMeshCdn.PublishAsync(_cache, artifact).ConfigureAwait(false);

            var document = new CultMeshNetworkBodyDocument
            {
                CapabilityToken = capabilityToken,
                BodyId = generation.BodyId,
                ProducerId = generation.ProducerId,
                SchemaId = generation.SchemaId,
                LayoutVersion = generation.LayoutVersion,
                ByteSize = body.LongLength,
                Capacity = generation.Capacity,
                ProducerEpoch = generation.ProducerEpoch,
                Sequence = generation.Sequence,
                Synchronization = generation.Synchronization,
                LeaseExpiresAtUnixMs = generation.LeaseExpiresAtUnixMs,
                SemanticHash = semanticHash,
                ManifestRecordKey = artifact.ManifestKey.Value
            };
            await _cache.UpsertAsync(document, new CultRecordHandle<CultMeshNetworkBodyDocument>(recordKey)).ConfigureAwait(false);
            return CreateDescriptor(document);
        }

        private static void ValidateGeneration(CultMeshBodyGeneration generation)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (string.IsNullOrWhiteSpace(generation.BodyId)) throw new ArgumentException("Body identity is required.", nameof(generation));
            if (string.IsNullOrWhiteSpace(generation.ProducerId)) throw new ArgumentException("Producer identity is required.", nameof(generation));
            if (string.IsNullOrWhiteSpace(generation.SchemaId)) throw new ArgumentException("Schema identity is required.", nameof(generation));
            if (generation.LayoutVersion < 0 || generation.Capacity < 0 || generation.ProducerEpoch < 0 || generation.Sequence < 0)
                throw new ArgumentOutOfRangeException(nameof(generation), "Body generation values must be non-negative.");
            if (generation.LeaseExpiresAtUnixMs <= 0) throw new ArgumentOutOfRangeException(nameof(generation), "Body generation requires a positive lease expiry.");
        }

        private static string CreateCapabilityToken(CultMeshBodyGeneration generation)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(generation.BodyId);
                writer.Write(generation.ProducerEpoch);
                writer.Write(generation.Sequence);
            }
            using var sha256 = SHA256.Create();
            return string.Concat(sha256.ComputeHash(stream.ToArray()).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void ValidateImmutableGeneration(
            CultMeshNetworkBodyDocument existing,
            CultMeshBodyGeneration generation,
            long byteSize,
            string semanticHash)
        {
            if (!string.Equals(existing.BodyId, generation.BodyId, StringComparison.Ordinal) ||
                !string.Equals(existing.ProducerId, generation.ProducerId, StringComparison.Ordinal) ||
                !string.Equals(existing.SchemaId, generation.SchemaId, StringComparison.Ordinal) ||
                existing.LayoutVersion != generation.LayoutVersion || existing.ByteSize != byteSize ||
                existing.Capacity != generation.Capacity || existing.ProducerEpoch != generation.ProducerEpoch ||
                existing.Sequence != generation.Sequence || existing.Synchronization != generation.Synchronization ||
                existing.LeaseExpiresAtUnixMs != generation.LeaseExpiresAtUnixMs ||
                !string.Equals(existing.SemanticHash, semanticHash, StringComparison.Ordinal))
                throw new InvalidOperationException("CultMesh body generation is immutable and was already published with different content or metadata.");
        }

        private static CultMeshBodyDescriptor CreateDescriptor(CultMeshNetworkBodyDocument document) => new()
        {
            BodyId = document.BodyId,
            SchemaId = document.SchemaId,
            LayoutVersion = document.LayoutVersion,
            ByteSize = document.ByteSize,
            Capacity = document.Capacity,
            ProducerEpoch = document.ProducerEpoch,
            Sequence = document.Sequence,
            AccessMode = CultMeshBodyAccessMode.ReadOnly,
            Synchronization = document.Synchronization,
            LeaseExpiresAtUnixMs = document.LeaseExpiresAtUnixMs,
            TransportKind = CultMeshBodyTransportKind.Network,
            CapabilityToken = document.CapabilityToken,
            SemanticHash = document.SemanticHash
        };
    }

    public sealed class CultMeshNetworkBodyResolver
    {
        private readonly CultCache _cache;

        public CultMeshNetworkBodyResolver(CultCache cache) =>
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        public Func<CultMeshBodyDescriptor, byte[]> CreateFetchDelegate() => Fetch;

        public byte[] Fetch(CultMeshBodyDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (descriptor.TransportKind != CultMeshBodyTransportKind.Network)
                throw new NotSupportedException("CultMesh network body resolver requires a network descriptor.");
            var document = _cache.Get<CultMeshNetworkBodyDocument>(
                CultMeshNetworkBodyDocument.CreateRecordKey(descriptor.CapabilityToken))
                ?? throw new FileNotFoundException("CultMesh network body capability is missing.", descriptor.CapabilityToken);
            ValidateDescriptor(document, descriptor);
            var manifestKey = new CultRecordKey(document.ManifestRecordKey);
            var manifest = _cache.Get<CultMeshCdnArtifactManifest>(manifestKey)
                ?? throw new FileNotFoundException("CultMesh network body manifest is missing.", manifestKey.Value);
            if (!string.Equals(CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value, manifestKey.Value, StringComparison.Ordinal))
                throw new InvalidDataException("CultMesh network body manifest identity does not match its capability binding.");
            if (manifest.SizeBytes != document.ByteSize ||
                !string.Equals(CultMeshCdn.NormalizeHash(manifest.ContentHash, nameof(manifest.ContentHash)), document.SemanticHash, StringComparison.Ordinal))
                throw new InvalidDataException("CultMesh network body manifest disagrees with its capability binding.");
            return CultMeshCdn.ReadArtifact(_cache, manifest);
        }

        private static void ValidateDescriptor(CultMeshNetworkBodyDocument document, CultMeshBodyDescriptor descriptor)
        {
            if (!string.Equals(document.CapabilityToken, descriptor.CapabilityToken, StringComparison.Ordinal) ||
                !string.Equals(document.BodyId, descriptor.BodyId, StringComparison.Ordinal) ||
                !string.Equals(document.SchemaId, descriptor.SchemaId, StringComparison.Ordinal) ||
                document.LayoutVersion != descriptor.LayoutVersion || document.ByteSize != descriptor.ByteSize ||
                document.Capacity != descriptor.Capacity || document.ProducerEpoch != descriptor.ProducerEpoch ||
                document.Sequence != descriptor.Sequence || document.Synchronization != descriptor.Synchronization ||
                document.LeaseExpiresAtUnixMs != descriptor.LeaseExpiresAtUnixMs ||
                !string.Equals(document.SemanticHash, descriptor.SemanticHash, StringComparison.Ordinal))
                throw new InvalidDataException("CultMesh network body descriptor disagrees with its capability binding.");
        }
    }

    [MessagePackObject]
    [CultDocument("gamecult.mesh.body_publication", CultMeshBodyPublicationSchemaVersions.Publication)]
    public sealed class CultMeshBodyPublicationDocument
    {
        [Key(0), CultName] public string BodyId { get; set; } = string.Empty;
        [Key(1), CultIndex] public string ProducerId { get; set; } = string.Empty;
        [Key(2)] public string SchemaId { get; set; } = string.Empty;
        [Key(3)] public int LayoutVersion { get; set; }
        [Key(4)] public long ByteSize { get; set; }
        [Key(5)] public int Capacity { get; set; }
        [Key(6)] public long ProducerEpoch { get; set; }
        [Key(7)] public long Sequence { get; set; }
        [Key(8)] public CultMeshBodySynchronization Synchronization { get; set; }
        [Key(9)] public long LivenessExpiresAtUnixMs { get; set; }
        [Key(10)] public CultMeshBodyDescriptor PreferredLocal { get; set; } = new();
        [Key(11)] public CultMeshBodyDescriptor NetworkFallback { get; set; } = new();

        public static CultRecordKey CreateRecordKey(string bodyId, long producerEpoch, long sequence)
        {
            if (string.IsNullOrWhiteSpace(bodyId))
                throw new ArgumentException("Logical body identity is required.", nameof(bodyId));
            if (producerEpoch < 0) throw new ArgumentOutOfRangeException(nameof(producerEpoch));
            if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(bodyId);
                writer.Write(producerEpoch);
                writer.Write(sequence);
            }
            using var sha256 = SHA256.Create();
            var generationId = string.Concat(sha256.ComputeHash(stream.ToArray())
                .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            return new CultRecordKey("mesh:body-generation:" + generationId);
        }

        public static CultRecordKey CreateLatestRecordKey(string bodyId)
        {
            if (string.IsNullOrWhiteSpace(bodyId))
                throw new ArgumentException("Logical body identity is required.", nameof(bodyId));
            return new CultRecordKey("mesh:body:" + bodyId);
        }

        [IgnoreMember]
        public CultRecordKey RecordKey => CreateRecordKey(BodyId, ProducerEpoch, Sequence);
    }

    public sealed class CultMeshBodyPublicationHandle
    {
        public CultMeshBodyPublicationHandle(string bodyId, long producerEpoch, long sequence)
        {
            BodyId = string.IsNullOrWhiteSpace(bodyId)
                ? throw new ArgumentException("Logical body identity is required.", nameof(bodyId))
                : bodyId;
            ProducerEpoch = producerEpoch >= 0
                ? producerEpoch
                : throw new ArgumentOutOfRangeException(nameof(producerEpoch));
            Sequence = sequence >= 0
                ? sequence
                : throw new ArgumentOutOfRangeException(nameof(sequence));
            RecordKey = CultMeshBodyPublicationDocument.CreateRecordKey(bodyId, producerEpoch, sequence);
        }

        public string BodyId { get; }
        public long ProducerEpoch { get; }
        public long Sequence { get; }
        public CultRecordKey RecordKey { get; }

        public void Validate(CultMeshBodyPublicationDocument publication) =>
            CultMeshBodyPublicationValidator.Validate(publication, BodyId, ProducerEpoch, Sequence, RecordKey);
    }

    public static class CultMeshBodyPublicationValidator
    {
        public static void Validate(
            CultMeshBodyPublicationDocument publication,
            string? expectedBodyId = null,
            long? expectedProducerEpoch = null,
            long? expectedSequence = null,
            CultRecordKey? expectedRecordKey = null)
        {
            if (publication == null) throw new ArgumentNullException(nameof(publication));
            if (string.IsNullOrWhiteSpace(publication.BodyId))
                throw new InvalidOperationException("CultMesh body publication has no logical body identity.");
            if (expectedBodyId != null && !string.Equals(publication.BodyId, expectedBodyId, StringComparison.Ordinal))
                throw new InvalidOperationException("CultMesh body publication handle identity mismatch.");
            if (expectedProducerEpoch.HasValue && publication.ProducerEpoch != expectedProducerEpoch.Value)
                throw new InvalidOperationException("CultMesh body publication handle producer epoch mismatch.");
            if (expectedSequence.HasValue && publication.Sequence != expectedSequence.Value)
                throw new InvalidOperationException("CultMesh body publication handle sequence mismatch.");
            if (string.IsNullOrWhiteSpace(publication.ProducerId))
                throw new InvalidOperationException("CultMesh body publication has no producer identity.");
            if (string.IsNullOrWhiteSpace(publication.SchemaId))
                throw new InvalidOperationException("CultMesh body publication has no schema identity.");
            if (publication.LayoutVersion < 0 || publication.ByteSize < 0 || publication.Capacity < 0 ||
                publication.ProducerEpoch < 0 || publication.Sequence < 0)
                throw new InvalidOperationException("CultMesh body publication generation values must be non-negative.");
            if (expectedRecordKey.HasValue && !publication.RecordKey.Equals(expectedRecordKey.Value))
                throw new InvalidOperationException("CultMesh body publication record key disagrees with its generation envelope.");
            ValidateDescriptor(publication, publication.PreferredLocal, "preferred local");
            ValidateDescriptor(publication, publication.NetworkFallback, "network fallback");
            if (publication.PreferredLocal.TransportKind == CultMeshBodyTransportKind.Network)
                throw new InvalidOperationException("CultMesh preferred local body descriptor must use a local transport.");
            if (publication.NetworkFallback.TransportKind != CultMeshBodyTransportKind.Network)
                throw new InvalidOperationException("CultMesh network fallback body descriptor must use network transport.");
        }

        private static void ValidateDescriptor(
            CultMeshBodyPublicationDocument publication,
            CultMeshBodyDescriptor descriptor,
            string label)
        {
            if (descriptor == null)
                throw new InvalidOperationException($"CultMesh body publication has no {label} descriptor.");
            if (!string.Equals(descriptor.BodyId, publication.BodyId, StringComparison.Ordinal) ||
                !string.Equals(descriptor.SchemaId, publication.SchemaId, StringComparison.Ordinal) ||
                descriptor.LayoutVersion != publication.LayoutVersion ||
                descriptor.ByteSize != publication.ByteSize ||
                descriptor.Capacity != publication.Capacity ||
                descriptor.ProducerEpoch != publication.ProducerEpoch ||
                descriptor.Sequence != publication.Sequence ||
                descriptor.Synchronization != publication.Synchronization ||
                descriptor.LeaseExpiresAtUnixMs != publication.LivenessExpiresAtUnixMs)
                throw new InvalidOperationException($"CultMesh {label} descriptor disagrees with its publication envelope.");
        }
    }

    public sealed class CultMeshBodyPublicationResolver
    {
        private readonly CultMeshBodyTransportService _transport;

        public CultMeshBodyPublicationResolver(CultMeshBodyTransportService transport) =>
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

        public ICultMeshBodyReadLease ResolveReadOnly(
            CultMeshBodyPublicationDocument publication,
            CultMeshBodyValidationRequest request) =>
            NegotiateReadOnly(publication, request).Lease;

        public CultMeshBodyNegotiationResult NegotiateReadOnly(
            CultMeshBodyPublicationDocument publication,
            CultMeshBodyValidationRequest request)
        {
            CultMeshBodyPublicationValidator.Validate(publication);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (publication.LivenessExpiresAtUnixMs <= request.NowUtc.ToUnixTimeMilliseconds())
                throw new InvalidOperationException("CultMesh body publication is no longer live.");
            return _transport.NegotiateReadOnly(
                publication.ProducerId,
                publication.PreferredLocal,
                publication.NetworkFallback,
                request);
        }
    }

    public static partial class CultMesh
    {
        public static CultNetDocumentRegistry CreateBodyPublicationDocumentRegistry(
            CultDocumentRegistry? documents = null)
        {
            documents ??= CultDocumentRegistry.Shared;
            return new CultNetDocumentRegistry(documents)
                .Register(CultNetDocumentBinding.ForDocument<CultMeshBodyPublicationDocument>(documents))
                .Register(CultNetDocumentBinding.ForDocument<CultMeshNetworkBodyDocument>(documents))
                .Register(CultNetDocumentBinding.ForDocument<CultMeshCdnArtifactManifest>(documents))
                .Register(CultNetDocumentBinding.ForDocument<CultMeshCdnArtifactChunk>(documents));
        }
    }
}
