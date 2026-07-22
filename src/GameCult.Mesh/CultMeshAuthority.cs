using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GameCult.Mesh
{
    public sealed class CultMeshAuthorityLease
    {
        public const string CurrentSchemaVersion = "gamecult.mesh.authority_lease.v1";
        public const string LegacySchemaVersion = "gamecult.mesh.authority_lease.v0";

        public CultMeshAuthorityLease(
            string leaseId,
            string verseId,
            string peerId,
            IEnumerable<string> roles,
            IEnumerable<string>? shardIds,
            string issuerRuntimeId,
            DateTimeOffset validFrom,
            DateTimeOffset expiresAt,
            string? signature = null,
            long? authorityEpoch = null,
            IEnumerable<string>? resourceScopes = null,
            string? schemaVersion = null)
        {
            LeaseId = RequireNonEmpty(leaseId, nameof(leaseId));
            VerseId = RequireNonEmpty(verseId, nameof(verseId));
            PeerId = RequireNonEmpty(peerId, nameof(peerId));
            Roles = Clean(roles);
            ShardIds = Clean(shardIds);
            IssuerRuntimeId = RequireNonEmpty(issuerRuntimeId, nameof(issuerRuntimeId));
            ValidFrom = validFrom;
            ExpiresAt = expiresAt;
            Signature = signature;
            AuthorityEpoch = authorityEpoch;
            ResourceScopes = Clean(resourceScopes);
            SchemaVersion = schemaVersion ?? (authorityEpoch.HasValue ? CurrentSchemaVersion : LegacySchemaVersion);
            if (expiresAt <= validFrom) throw new ArgumentException("Lease expiry must be after its valid-from timestamp.", nameof(expiresAt));
            if (authorityEpoch < 0) throw new ArgumentOutOfRangeException(nameof(authorityEpoch));
        }

        public string LeaseId { get; }
        public string VerseId { get; }
        public string PeerId { get; }
        public IReadOnlyList<string> Roles { get; }
        public IReadOnlyList<string> ShardIds { get; }
        public string IssuerRuntimeId { get; }
        public DateTimeOffset ValidFrom { get; }
        public DateTimeOffset ExpiresAt { get; }
        public string? Signature { get; }
        public long? AuthorityEpoch { get; }
        public IReadOnlyList<string> ResourceScopes { get; }
        public string SchemaVersion { get; }

        public bool IsValidAt(DateTimeOffset at) => at >= ValidFrom && at < ExpiresAt;

        [Obsolete("Authority decisions belong to CultMeshAuthorityResolver. This method denies leases that lack v1 signed authority evidence.")]
        public bool Covers(CultMeshPeerCard peer, string role, string? shardId = null, DateTimeOffset? at = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var resolver = CultMeshAuthorityResolver.CreateDenyByDefault(
                new SingleLeaseSource(this),
                at.HasValue ? new FixedClock(at.Value) : CultMeshSystemClock.Instance);
            return resolver.Resolve(new CultMeshAuthorityRequest(peer, role, shardId, AuthorityEpoch ?? -1)).IsAuthorized;
        }

        private static string[] Clean(IEnumerable<string>? values) =>
            values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();

        private static string RequireNonEmpty(string value, string paramName) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must be non-empty.", paramName) : value;

        private sealed class SingleLeaseSource : ICultMeshAuthorityLeaseSource
        {
            private readonly CultMeshAuthorityLease _lease;
            public SingleLeaseSource(CultMeshAuthorityLease lease) => _lease = lease;
            public CultMeshAuthorityLease? Get(string leaseId) => string.Equals(leaseId, _lease.LeaseId, StringComparison.Ordinal) ? _lease : null;
        }

        private sealed class FixedClock : ICultMeshClock
        {
            public FixedClock(DateTimeOffset now) => UtcNow = now;
            public DateTimeOffset UtcNow { get; }
            public System.Threading.Tasks.Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.CompletedTask;
        }
    }

    public interface ICultMeshAuthorityLeaseSource
    {
        CultMeshAuthorityLease? Get(string leaseId);
    }

    public interface ICultMeshAuthoritySignatureVerifier
    {
        bool Verify(CultMeshAuthorityLease lease);
    }

    public interface ICultMeshAuthorityRevocationSource
    {
        bool IsRevoked(string leaseId, long authorityEpoch);
    }

    public enum CultMeshAuthorityDenialReason
    {
        None,
        MissingLeaseReference,
        LeaseNotFound,
        UnsupportedLeaseVersion,
        MissingSignature,
        InvalidSignature,
        NotYetValid,
        Expired,
        Revoked,
        VerseMismatch,
        PeerMismatch,
        LeaseReferenceMismatch,
        RoleNotGranted,
        RoleNotAdvertised,
        ShardNotGranted,
        ShardNotAdvertised,
        EpochMismatch,
        ResourceScopeNotGranted
    }

    public sealed class CultMeshAuthorityRequest
    {
        public CultMeshAuthorityRequest(CultMeshPeerCard peer, string role, string? shardId, long authorityEpoch, string? resourceScope = null)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            Role = string.IsNullOrWhiteSpace(role) ? throw new ArgumentException("Value must be non-empty.", nameof(role)) : role;
            if (authorityEpoch < 0) throw new ArgumentOutOfRangeException(nameof(authorityEpoch));
            ShardId = shardId;
            AuthorityEpoch = authorityEpoch;
            ResourceScope = resourceScope;
        }

        public CultMeshPeerCard Peer { get; }
        public string Role { get; }
        public string? ShardId { get; }
        public long AuthorityEpoch { get; }
        public string? ResourceScope { get; }
    }

    public sealed class CultMeshAuthorityDecision
    {
        internal CultMeshAuthorityDecision(CultMeshAuthorityRequest request, CultMeshAuthorityLease? lease, CultMeshAuthorityDenialReason reason)
        {
            Request = request;
            Lease = lease;
            DenialReason = reason;
        }

        public CultMeshAuthorityRequest Request { get; }
        public CultMeshAuthorityLease? Lease { get; }
        public bool IsAuthorized => DenialReason == CultMeshAuthorityDenialReason.None;
        public CultMeshAuthorityDenialReason DenialReason { get; }
        public string? LeaseId => Lease?.LeaseId ?? Request.Peer.AuthorityLeaseId;
        public string? IssuerRuntimeId => Lease?.IssuerRuntimeId;
    }

    public sealed class CultMeshAuthorityResolver
    {
        private readonly ICultMeshAuthorityLeaseSource _leases;
        private readonly ICultMeshAuthoritySignatureVerifier _signatures;
        private readonly ICultMeshAuthorityRevocationSource _revocations;
        private readonly ICultMeshClock _clock;
        private readonly ICultMeshDiagnosticSink _diagnostics;
        private long _diagnosticSequence;

        public CultMeshAuthorityResolver(
            ICultMeshAuthorityLeaseSource leases,
            ICultMeshAuthoritySignatureVerifier signatures,
            ICultMeshAuthorityRevocationSource revocations,
            ICultMeshClock clock,
            ICultMeshDiagnosticSink? diagnostics = null)
        {
            _leases = leases ?? throw new ArgumentNullException(nameof(leases));
            _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
            _revocations = revocations ?? throw new ArgumentNullException(nameof(revocations));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _diagnostics = diagnostics ?? CultMeshNullDiagnosticSink.Instance;
        }

        public CultMeshAuthorityDecision Resolve(CultMeshAuthorityRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var leaseId = request.Peer.AuthorityLeaseId;
            if (string.IsNullOrWhiteSpace(leaseId)) return Decide(request, null, CultMeshAuthorityDenialReason.MissingLeaseReference);
            var lease = _leases.Get(leaseId!);
            if (lease == null) return Decide(request, null, CultMeshAuthorityDenialReason.LeaseNotFound);
            if (!string.Equals(lease.SchemaVersion, CultMeshAuthorityLease.CurrentSchemaVersion, StringComparison.Ordinal) || !lease.AuthorityEpoch.HasValue)
                return Decide(request, lease, CultMeshAuthorityDenialReason.UnsupportedLeaseVersion);
            if (string.IsNullOrWhiteSpace(lease.Signature)) return Decide(request, lease, CultMeshAuthorityDenialReason.MissingSignature);
            if (!_signatures.Verify(lease)) return Decide(request, lease, CultMeshAuthorityDenialReason.InvalidSignature);
            if (_clock.UtcNow < lease.ValidFrom) return Decide(request, lease, CultMeshAuthorityDenialReason.NotYetValid);
            if (_clock.UtcNow >= lease.ExpiresAt) return Decide(request, lease, CultMeshAuthorityDenialReason.Expired);
            if (_revocations.IsRevoked(lease.LeaseId, lease.AuthorityEpoch.Value)) return Decide(request, lease, CultMeshAuthorityDenialReason.Revoked);
            if (!string.Equals(lease.VerseId, request.Peer.VerseId, StringComparison.Ordinal)) return Decide(request, lease, CultMeshAuthorityDenialReason.VerseMismatch);
            if (!string.Equals(lease.PeerId, request.Peer.PeerId, StringComparison.Ordinal)) return Decide(request, lease, CultMeshAuthorityDenialReason.PeerMismatch);
            if (!string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)) return Decide(request, lease, CultMeshAuthorityDenialReason.LeaseReferenceMismatch);
            if (!lease.Roles.Contains(request.Role, StringComparer.Ordinal)) return Decide(request, lease, CultMeshAuthorityDenialReason.RoleNotGranted);
            if (!request.Peer.Roles.Contains(request.Role, StringComparer.Ordinal)) return Decide(request, lease, CultMeshAuthorityDenialReason.RoleNotAdvertised);
            if (!string.IsNullOrWhiteSpace(request.ShardId) && lease.ShardIds.Count != 0 && !lease.ShardIds.Contains(request.ShardId!, StringComparer.Ordinal))
                return Decide(request, lease, CultMeshAuthorityDenialReason.ShardNotGranted);
            if (!string.IsNullOrWhiteSpace(request.ShardId) && request.Peer.ShardIds.Count != 0 && !request.Peer.ShardIds.Contains(request.ShardId!, StringComparer.Ordinal))
                return Decide(request, lease, CultMeshAuthorityDenialReason.ShardNotAdvertised);
            if (lease.AuthorityEpoch.Value != request.AuthorityEpoch) return Decide(request, lease, CultMeshAuthorityDenialReason.EpochMismatch);
            if (!string.IsNullOrWhiteSpace(request.ResourceScope) && !lease.ResourceScopes.Contains(request.ResourceScope!, StringComparer.Ordinal))
                return Decide(request, lease, CultMeshAuthorityDenialReason.ResourceScopeNotGranted);
            return Decide(request, lease, CultMeshAuthorityDenialReason.None);
        }

        internal static CultMeshAuthorityResolver CreateDenyByDefault(ICultMeshAuthorityLeaseSource leases, ICultMeshClock? clock = null) =>
            new CultMeshAuthorityResolver(leases, DenyAllSignatures.Instance, NoRevocations.Instance, clock ?? CultMeshSystemClock.Instance);

        private CultMeshAuthorityDecision Decide(CultMeshAuthorityRequest request, CultMeshAuthorityLease? lease, CultMeshAuthorityDenialReason reason)
        {
            var decision = new CultMeshAuthorityDecision(request, lease, reason);
            _diagnostics.Emit(new CultMeshDiagnosticEvent(
                Interlocked.Increment(ref _diagnosticSequence), _clock.UtcNow, CultMeshReliabilityOrgan.Authority,
                CultMeshDiagnosticKind.AuthorityDecision, lease?.LeaseId ?? request.Peer.AuthorityLeaseId ?? "lease:missing",
                request.Peer.PeerId, decision.IsAuthorized ? "authorized" : "denied", reason.ToString(), lease?.IssuerRuntimeId ?? "",
                schemaVersion: "gamecult.mesh.authority_decision.v1"));
            return decision;
        }

        private sealed class DenyAllSignatures : ICultMeshAuthoritySignatureVerifier
        {
            public static DenyAllSignatures Instance { get; } = new DenyAllSignatures();
            public bool Verify(CultMeshAuthorityLease lease) => false;
        }

        private sealed class NoRevocations : ICultMeshAuthorityRevocationSource
        {
            public static NoRevocations Instance { get; } = new NoRevocations();
            public bool IsRevoked(string leaseId, long authorityEpoch) => false;
        }
    }

    public sealed class CultMeshAuthorityLeaseCatalog : ICultMeshAuthorityLeaseSource
    {
        private readonly Dictionary<string, CultMeshAuthorityLease> _leases = new(StringComparer.Ordinal);
        public IReadOnlyList<CultMeshAuthorityLease> Leases => _leases.Values.OrderBy(lease => lease.LeaseId, StringComparer.Ordinal).ToArray();
        public void Upsert(CultMeshAuthorityLease lease) { if (lease == null) throw new ArgumentNullException(nameof(lease)); _leases[lease.LeaseId] = lease; }
        public CultMeshAuthorityLease? Get(string leaseId)
        {
            if (string.IsNullOrWhiteSpace(leaseId)) throw new ArgumentException("Value must be non-empty.", nameof(leaseId));
            return _leases.TryGetValue(leaseId, out var lease) ? lease : null;
        }

        [Obsolete("Authority decisions belong to CultMeshAuthorityResolver. This compatibility path denies unsigned and unverifiable leases.")]
        public bool IsAuthorized(CultMeshPeerCard peer, string role, string? shardId = null, DateTimeOffset? at = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var lease = string.IsNullOrWhiteSpace(peer.AuthorityLeaseId) ? null : Get(peer.AuthorityLeaseId!);
            var epoch = lease?.AuthorityEpoch ?? -1;
            if (epoch < 0) return false;
            var resolver = CultMeshAuthorityResolver.CreateDenyByDefault(this, at.HasValue ? new CatalogFixedClock(at.Value) : null);
            return resolver.Resolve(new CultMeshAuthorityRequest(peer, role, shardId, epoch)).IsAuthorized;
        }

        private sealed class CatalogFixedClock : ICultMeshClock
        {
            public CatalogFixedClock(DateTimeOffset now) => UtcNow = now;
            public DateTimeOffset UtcNow { get; }
            public System.Threading.Tasks.Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
