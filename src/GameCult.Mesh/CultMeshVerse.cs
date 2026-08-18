using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameCult.Networking;
using R3;

namespace GameCult.Mesh
{
    /// <summary>
    /// Describes how a CultMesh Verse makes authority decisions.
    /// </summary>
    public enum CultMeshVerseAuthorityModel
    {
        /// <summary>
        /// A known operator cluster is authoritative for committed world state.
        /// </summary>
        OperatorCluster,
        /// <summary>
        /// Regional operators share authority for lower latency and availability.
        /// </summary>
        FederatedCluster,
        /// <summary>
        /// Peers participate directly in authority decisions.
        /// </summary>
        PeerToPeer,
        /// <summary>
        /// The Verse follows another Verse and applies declared overlays.
        /// </summary>
        SubscribedOverlay
    }

    /// <summary>
    /// Declares transport and rules compatibility for a Verse.
    /// </summary>
    public sealed class CultMeshVerseCompatibility
    {
        /// <summary>
        /// Creates compatibility metadata.
        /// </summary>
        public CultMeshVerseCompatibility(
            string transportVersion,
            string rulesHash,
            IEnumerable<string>? compatibleVerseIds = null,
            IEnumerable<string>? requiredPluginIds = null,
            IEnumerable<string>? optionalPluginIds = null)
        {
            TransportVersion = RequireNonEmpty(transportVersion, nameof(transportVersion));
            RulesHash = RequireNonEmpty(rulesHash, nameof(rulesHash));
            CompatibleVerseIds = Clean(compatibleVerseIds);
            RequiredPluginIds = Clean(requiredPluginIds);
            OptionalPluginIds = Clean(optionalPluginIds);
        }

        /// <summary>
        /// Gets the CultMesh/CultNet transport compatibility version.
        /// </summary>
        public string TransportVersion { get; }
        /// <summary>
        /// Gets the stable rules hash.
        /// </summary>
        public string RulesHash { get; }
        /// <summary>
        /// Gets Verse ids this Verse can transfer from or subscribe to.
        /// </summary>
        public IReadOnlyList<string> CompatibleVerseIds { get; }
        /// <summary>
        /// Gets plugin ids required to enter this Verse.
        /// </summary>
        public IReadOnlyList<string> RequiredPluginIds { get; }
        /// <summary>
        /// Gets plugin ids supported but not required by this Verse.
        /// </summary>
        public IReadOnlyList<string> OptionalPluginIds { get; }

        private static string[] Clean(IEnumerable<string>? values)
        {
            return values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be non-empty.", paramName);
            }

            return value;
        }
    }

    /// <summary>
    /// Binds one authoritative runtime to one physical route. This relationship is
    /// the routing authority; the legacy endpoint/runtime lists are projections only.
    /// </summary>
    public sealed class CultMeshAuthorityRoute
    {
        public CultMeshAuthorityRoute(
            string authorityRuntimeId,
            string endpoint,
            IEnumerable<string>? protocolIds = null,
            int priority = 0,
            string? generation = null)
        {
            AuthorityRuntimeId = RequireNonEmpty(authorityRuntimeId, nameof(authorityRuntimeId));
            Endpoint = RequireNonEmpty(endpoint, nameof(endpoint));
            ProtocolIds = Clean(protocolIds);
            if (priority < 0) throw new ArgumentOutOfRangeException(nameof(priority));
            Priority = priority;
            Generation = string.IsNullOrWhiteSpace(generation)
                ? AuthorityRuntimeId + "@" + Endpoint
                : generation!.Trim();
        }

        public string AuthorityRuntimeId { get; }
        public string Endpoint { get; }
        public IReadOnlyList<string> ProtocolIds { get; }
        public int Priority { get; }
        public string Generation { get; }

        public bool Supports(CultMeshProtocolId protocol) =>
            protocol != null &&
            (ProtocolIds.Count == 0 || ProtocolIds.Contains(protocol.Value, StringComparer.Ordinal));

        private static string[] Clean(IEnumerable<string>? values) =>
            values?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
            ?? Array.Empty<string>();

        private static string RequireNonEmpty(string value, string paramName) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value.Trim();
    }

    /// <summary>
    /// Describes one CultMesh Verse: a rule-bearing consensus graph.
    /// </summary>
    public sealed class CultMeshVerseDescriptor
    {
        /// <summary>
        /// Creates a Verse descriptor.
        /// </summary>
        public CultMeshVerseDescriptor(
            string verseId,
            string displayName,
            CultMeshVerseAuthorityModel authorityModel,
            CultMeshVerseCompatibility compatibility,
            IEnumerable<string>? discoveryEndpoints = null,
            IEnumerable<string>? authorityRuntimeIds = null,
            string? parentVerseId = null,
            string? description = null,
            IEnumerable<CultMeshAuthorityRoute>? authorityRoutes = null)
        {
            VerseId = RequireNonEmpty(verseId, nameof(verseId));
            DisplayName = RequireNonEmpty(displayName, nameof(displayName));
            AuthorityModel = authorityModel;
            Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
            var legacyEndpoints = Clean(discoveryEndpoints);
            var legacyRuntimeIds = Clean(authorityRuntimeIds);
            var suppliedRoutes = authorityRoutes?.ToArray();
            if (suppliedRoutes?.Any(route => route == null) == true)
                throw new ArgumentException("Authority routes cannot contain null.", nameof(authorityRoutes));
            if (suppliedRoutes != null && (legacyEndpoints.Length > 0 || legacyRuntimeIds.Length > 0))
                throw new ArgumentException(
                    "Supply bound authority routes or legacy endpoint/runtime lists, never both.",
                    nameof(authorityRoutes));
            if (suppliedRoutes == null && legacyEndpoints.Length > 0 && legacyRuntimeIds.Length > 1)
                throw new InvalidOperationException(
                    "Legacy Verse routing with multiple authority runtimes is ambiguous. " +
                    "Publish explicit CultMeshAuthorityRoute bindings.");

            AuthorityRoutes = NormalizeRoutes(suppliedRoutes ??
                (legacyRuntimeIds.Length == 1
                    ? legacyEndpoints.Select(endpoint => new CultMeshAuthorityRoute(legacyRuntimeIds[0], endpoint))
                    : Array.Empty<CultMeshAuthorityRoute>()));
            DiscoveryEndpoints = AuthorityRoutes.Count > 0
                ? AuthorityRoutes.Select(route => route.Endpoint).Distinct(StringComparer.Ordinal).ToArray()
                : legacyEndpoints;
            AuthorityRuntimeIds = AuthorityRoutes.Select(route => route.AuthorityRuntimeId)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            ParentVerseId = parentVerseId;
            Description = description;
        }

        /// <summary>
        /// Gets the stable Verse id.
        /// </summary>
        public string VerseId { get; }
        /// <summary>
        /// Gets the human-facing Verse name.
        /// </summary>
        public string DisplayName { get; }
        /// <summary>
        /// Gets the authority model used by this Verse.
        /// </summary>
        public CultMeshVerseAuthorityModel AuthorityModel { get; }
        /// <summary>
        /// Gets compatibility metadata for transport, rules, and plugins.
        /// </summary>
        public CultMeshVerseCompatibility Compatibility { get; }
        /// <summary>
        /// Gets discovery endpoints for nodes serving this Verse.
        /// </summary>
        public IReadOnlyList<string> DiscoveryEndpoints { get; }
        /// <summary>
        /// Gets known authoritative runtime ids, when authority is cluster-shaped.
        /// </summary>
        public IReadOnlyList<string> AuthorityRuntimeIds { get; }
        /// <summary>Gets exact authority-to-route bindings used for identity-sensitive resolution.</summary>
        public IReadOnlyList<CultMeshAuthorityRoute> AuthorityRoutes { get; }
        /// <summary>
        /// Gets the parent Verse id for subscribed overlays or branches.
        /// </summary>
        public string? ParentVerseId { get; }
        /// <summary>
        /// Gets optional public description.
        /// </summary>
        public string? Description { get; }

        /// <summary>
        /// Returns whether this Verse can accept transfer from another Verse.
        /// </summary>
        public bool CanTransferFrom(CultMeshVerseDescriptor source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return string.Equals(Compatibility.TransportVersion, source.Compatibility.TransportVersion, StringComparison.Ordinal) &&
                   (string.Equals(Compatibility.RulesHash, source.Compatibility.RulesHash, StringComparison.Ordinal) ||
                    Compatibility.CompatibleVerseIds.Contains(source.VerseId, StringComparer.Ordinal));
        }

        /// <summary>
        /// Computes a stable rules hash from ordered rules/plugin parts.
        /// </summary>
        public static string ComputeRulesHash(params string[] parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            var canonical = string.Join("\u001F", parts.Select(part => part ?? string.Empty));
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return string.Concat(bytes.Select(value => value.ToString("x2")));
        }

        private static string[] Clean(IEnumerable<string>? values)
        {
            return values?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? Array.Empty<string>();
        }

        private static CultMeshAuthorityRoute[] NormalizeRoutes(IEnumerable<CultMeshAuthorityRoute> routes)
        {
            return routes
                .GroupBy(route => new
                {
                    route.AuthorityRuntimeId,
                    route.Endpoint,
                    Protocols = string.Join("\u001f", route.ProtocolIds)
                })
                .Select(group => group.OrderBy(route => route.Priority).First())
                .OrderBy(route => route.Priority)
                .ThenBy(route => route.AuthorityRuntimeId, StringComparer.Ordinal)
                .ThenBy(route => route.Endpoint, StringComparer.Ordinal)
                .ToArray();
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be non-empty.", paramName);
            }

            return value;
        }
    }

    /// <summary>
    /// Reactive local catalog for discovered CultMesh Verses.
    /// </summary>
    public sealed class CultMeshVerseCatalog : IDisposable
    {
        private readonly Dictionary<string, CultMeshVerseDescriptor> _verses = new(StringComparer.Ordinal);
        private readonly Subject<CultMeshVerseDescriptor> _updates = new();
        private bool _disposed;

        /// <summary>
        /// Gets all known Verses.
        /// </summary>
        public IReadOnlyList<CultMeshVerseDescriptor> Verses => _verses.Values.OrderBy(verse => verse.VerseId, StringComparer.Ordinal).ToArray();

        /// <summary>
        /// Watches Verse discovery updates.
        /// </summary>
        public Observable<CultMeshVerseDescriptor> Watch()
        {
            ThrowIfDisposed();
            return _updates;
        }

        /// <summary>
        /// Adds or replaces a discovered Verse.
        /// </summary>
        public void Upsert(CultMeshVerseDescriptor verse)
        {
            ThrowIfDisposed();
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            _verses[verse.VerseId] = verse;
            _updates.OnNext(verse);
        }

        /// <summary>
        /// Adds or replaces Verses from a wire catalog response.
        /// </summary>
        public void Upsert(CultMeshVerseCatalogResponseMessage response)
        {
            ThrowIfDisposed();
            if (response == null) throw new ArgumentNullException(nameof(response));
            foreach (var verse in response.Verses)
            {
                Upsert(verse.ToVerseDescriptor());
            }
        }

        /// <summary>
        /// Gets a Verse by id, if known.
        /// </summary>
        public CultMeshVerseDescriptor? Get(string verseId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(verseId)) throw new ArgumentException("Value must be non-empty.", nameof(verseId));
            return _verses.TryGetValue(verseId, out var verse) ? verse : null;
        }

        /// <summary>
        /// Gets known Verses that can accept transfer from the supplied source Verse.
        /// </summary>
        public IReadOnlyList<CultMeshVerseDescriptor> FindTransferTargets(CultMeshVerseDescriptor source)
        {
            ThrowIfDisposed();
            if (source == null) throw new ArgumentNullException(nameof(source));
            return _verses.Values
                .Where(verse => !string.Equals(verse.VerseId, source.VerseId, StringComparison.Ordinal) &&
                                verse.CanTransferFrom(source))
                .OrderBy(verse => verse.VerseId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _updates.Dispose();
            _verses.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultMeshVerseCatalog));
            }
        }
    }
}
