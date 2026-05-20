using System;
using System.Collections.Generic;
using System.Linq;

namespace GameCult.Mesh
{
    /// <summary>
    /// Grants one peer authority to perform roles for a Verse over a bounded interval.
    /// </summary>
    public sealed class CultMeshAuthorityLease
    {
        /// <summary>
        /// Creates an authority lease.
        /// </summary>
        public CultMeshAuthorityLease(
            string leaseId,
            string verseId,
            string peerId,
            IEnumerable<string> roles,
            IEnumerable<string>? shardIds,
            string issuerRuntimeId,
            DateTimeOffset validFrom,
            DateTimeOffset expiresAt,
            string? signature = null)
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
            if (expiresAt <= validFrom)
            {
                throw new ArgumentException("Lease expiry must be after its valid-from timestamp.", nameof(expiresAt));
            }
        }

        /// <summary>Gets the stable lease id.</summary>
        public string LeaseId { get; }
        /// <summary>Gets the Verse id governed by the lease.</summary>
        public string VerseId { get; }
        /// <summary>Gets the peer id receiving authority.</summary>
        public string PeerId { get; }
        /// <summary>Gets the roles granted by this lease.</summary>
        public IReadOnlyList<string> Roles { get; }
        /// <summary>Gets shard ids covered by this lease. Empty means every shard in the Verse.</summary>
        public IReadOnlyList<string> ShardIds { get; }
        /// <summary>Gets the runtime that issued the lease.</summary>
        public string IssuerRuntimeId { get; }
        /// <summary>Gets the first timestamp when the lease is valid.</summary>
        public DateTimeOffset ValidFrom { get; }
        /// <summary>Gets the timestamp when the lease expires.</summary>
        public DateTimeOffset ExpiresAt { get; }
        /// <summary>Gets an optional signature over the lease.</summary>
        public string? Signature { get; }

        /// <summary>
        /// Returns whether this lease is valid at the supplied time.
        /// </summary>
        public bool IsValidAt(DateTimeOffset at)
        {
            return at >= ValidFrom && at < ExpiresAt;
        }

        /// <summary>
        /// Returns whether this lease covers a peer card, role, and shard.
        /// </summary>
        public bool Covers(CultMeshPeerCard peer, string role, string? shardId = null, DateTimeOffset? at = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("Value must be non-empty.", nameof(role));
            var time = at ?? DateTimeOffset.UtcNow;
            return IsValidAt(time) &&
                   string.Equals(VerseId, peer.VerseId, StringComparison.Ordinal) &&
                   string.Equals(PeerId, peer.PeerId, StringComparison.Ordinal) &&
                   string.Equals(LeaseId, peer.AuthorityLeaseId, StringComparison.Ordinal) &&
                   Roles.Contains(role, StringComparer.Ordinal) &&
                   peer.Roles.Contains(role, StringComparer.Ordinal) &&
                   (string.IsNullOrWhiteSpace(shardId) ||
                    ShardIds.Count == 0 ||
                    ShardIds.Contains(shardId!, StringComparer.Ordinal));
        }

        private static string[] Clean(IEnumerable<string>? values)
        {
            return values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Local authority lease catalog.
    /// </summary>
    public sealed class CultMeshAuthorityLeaseCatalog
    {
        private readonly Dictionary<string, CultMeshAuthorityLease> _leases = new(StringComparer.Ordinal);

        /// <summary>Gets all known leases.</summary>
        public IReadOnlyList<CultMeshAuthorityLease> Leases => _leases.Values.OrderBy(lease => lease.LeaseId, StringComparer.Ordinal).ToArray();

        /// <summary>Adds or replaces an authority lease.</summary>
        public void Upsert(CultMeshAuthorityLease lease)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            _leases[lease.LeaseId] = lease;
        }

        /// <summary>Gets a lease by id, if known.</summary>
        public CultMeshAuthorityLease? Get(string leaseId)
        {
            if (string.IsNullOrWhiteSpace(leaseId)) throw new ArgumentException("Value must be non-empty.", nameof(leaseId));
            return _leases.TryGetValue(leaseId, out var lease) ? lease : null;
        }

        /// <summary>
        /// Returns whether a peer card is authorized for a role and optional shard.
        /// </summary>
        public bool IsAuthorized(CultMeshPeerCard peer, string role, string? shardId = null, DateTimeOffset? at = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (string.IsNullOrWhiteSpace(peer.AuthorityLeaseId))
            {
                return false;
            }

            return _leases.TryGetValue(peer.AuthorityLeaseId!, out var lease) &&
                   lease.Covers(peer, role, shardId, at);
        }
    }
}
