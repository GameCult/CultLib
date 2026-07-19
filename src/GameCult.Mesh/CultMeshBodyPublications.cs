using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    public static class CultMeshBodyPublicationSchemaVersions
    {
        public const string Publication = "gamecult.mesh.body_publication.v2";
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
        [Key(10)] public CultMeshBodyDescriptor[] Representations { get; set; } = Array.Empty<CultMeshBodyDescriptor>();

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
            var representations = publication.Representations ?? Array.Empty<CultMeshBodyDescriptor>();
            if (representations.Length == 0)
                throw new InvalidOperationException("CultMesh body publication has no transport representations.");
            if (representations.Select(value => value?.TransportKind).Distinct().Count() != representations.Length)
                throw new InvalidOperationException("CultMesh body publication advertises the same transport representation more than once.");
            foreach (var representation in representations)
                ValidateDescriptor(publication, representation, representation?.TransportKind.ToString() ?? "missing");
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

        /// <summary>Gets the body planes this resolver can actually open.</summary>
        public IReadOnlyList<CultMeshBodyTransportKind> SupportedTransports => _transport.SupportedTransports;

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
                publication.Representations,
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
                .Register(CultNetDocumentBinding.ForDocument<CultMeshCdnArtifactManifest>(documents))
                .Register(CultNetDocumentBinding.ForDocument<CultMeshCdnArtifactChunk>(documents));
        }
    }
}
