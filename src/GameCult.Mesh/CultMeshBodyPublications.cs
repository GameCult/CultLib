using System;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    public static class CultMeshBodyPublicationSchemaVersions
    {
        public const string Publication = "gamecult.mesh.body_publication.v1";
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

        public static CultRecordKey CreateRecordKey(string bodyId)
        {
            if (string.IsNullOrWhiteSpace(bodyId))
                throw new ArgumentException("Logical body identity is required.", nameof(bodyId));
            return new CultRecordKey("mesh:body:" + bodyId);
        }

        [IgnoreMember]
        public CultRecordKey RecordKey => CreateRecordKey(BodyId);
    }

    public sealed class CultMeshBodyPublicationHandle
    {
        public CultMeshBodyPublicationHandle(string bodyId)
        {
            BodyId = string.IsNullOrWhiteSpace(bodyId)
                ? throw new ArgumentException("Logical body identity is required.", nameof(bodyId))
                : bodyId;
            RecordKey = CultMeshBodyPublicationDocument.CreateRecordKey(bodyId);
        }

        public string BodyId { get; }
        public CultRecordKey RecordKey { get; }

        public void Validate(CultMeshBodyPublicationDocument publication) =>
            CultMeshBodyPublicationValidator.Validate(publication, BodyId);
    }

    public static class CultMeshBodyPublicationValidator
    {
        public static void Validate(CultMeshBodyPublicationDocument publication, string? expectedBodyId = null)
        {
            if (publication == null) throw new ArgumentNullException(nameof(publication));
            if (string.IsNullOrWhiteSpace(publication.BodyId))
                throw new InvalidOperationException("CultMesh body publication has no logical body identity.");
            if (expectedBodyId != null && !string.Equals(publication.BodyId, expectedBodyId, StringComparison.Ordinal))
                throw new InvalidOperationException("CultMesh body publication handle identity mismatch.");
            if (string.IsNullOrWhiteSpace(publication.ProducerId))
                throw new InvalidOperationException("CultMesh body publication has no producer identity.");
            if (string.IsNullOrWhiteSpace(publication.SchemaId))
                throw new InvalidOperationException("CultMesh body publication has no schema identity.");
            if (publication.LayoutVersion < 0 || publication.ByteSize < 0 || publication.Capacity < 0 ||
                publication.ProducerEpoch < 0 || publication.Sequence < 0)
                throw new InvalidOperationException("CultMesh body publication generation values must be non-negative.");
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
                .Register(CultNetDocumentBinding.ForDocument<CultMeshBodyPublicationDocument>(documents));
        }
    }
}
