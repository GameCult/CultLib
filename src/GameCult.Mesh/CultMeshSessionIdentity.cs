using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>
    /// Optional transport proof surfaced by connectors that already authenticate
    /// the peer during their native handshake. Other schema clients must complete
    /// the portable CultMesh handshake before use.
    /// </summary>
    public interface ICultMeshVerifiedSchemaClient
    {
        bool IsVerifiedFor(
            string verseId,
            string authorityRuntimeId,
            string protocolId,
            string routeGeneration);
    }

    /// <summary>
    /// Proof exposed by content and realtime transports after their native or
    /// portable handshake has bound the physical channel to an Odin route.
    /// </summary>
    public interface ICultMeshVerifiedTransport
    {
        bool IsVerifiedFor(
            string verseId,
            string authorityRuntimeId,
            string protocolId,
            string routeGeneration);
    }

    internal static class CultMeshTransportIdentity
    {
        public static void RequireVerified(
            object transport,
            CultMeshSessionTarget target,
            CultMeshProtocolId protocol,
            CultMeshTransportCandidate candidate)
        {
            if (transport is ICultMeshVerifiedTransport verified &&
                verified.IsVerifiedFor(
                    target.VerseId,
                    target.AuthorityRuntimeId,
                    protocol.Value,
                    candidate.Generation))
                return;
            throw new CultMeshSessionException(new CultMeshSessionFailure(
                CultMeshSessionFailureReason.Authority,
                $"Transport did not prove CultMesh authority '{target}' for protocol '{protocol}'.",
                candidate.Endpoint));
        }
    }

    /// <summary>
    /// Owns the server side of the transport-neutral CultMesh session identity
    /// handshake. Application messages are not authority proof; providers attach
    /// this gate to every schema transport they advertise.
    /// </summary>
    public sealed class CultMeshSessionIdentityServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly string _authorityRuntimeId;
        private readonly HashSet<string> _verseIds;
        private readonly HashSet<string> _protocolIds;
        private readonly HashSet<string> _routeGenerations;
        private readonly Dictionary<string, CultMeshSessionProofSigner> _proofSigners;
        private readonly ConcurrentDictionary<ICultNetSchemaServerPeer, string> _acceptedPeers = new();
        private readonly Func<CultMeshSessionOpenMessage, ICultNetSchemaServerPeer, Task> _handler;
        private readonly ICultNetSchemaServerPeerLifecycle? _peerLifecycle;
        private bool _disposed;

        public CultMeshSessionIdentityServer(
            ICultNetSchemaServer server,
            string authorityRuntimeId,
            IEnumerable<string> verseIds,
            IEnumerable<string> protocolIds,
            IEnumerable<string> routeGenerations,
            IEnumerable<CultMeshSessionProofSigner>? proofSigners = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _authorityRuntimeId = Require(authorityRuntimeId, nameof(authorityRuntimeId));
            _verseIds = Clean(verseIds, nameof(verseIds));
            _protocolIds = Clean(protocolIds, nameof(protocolIds));
            _routeGenerations = Clean(routeGenerations, nameof(routeGenerations));
            _proofSigners = (proofSigners ?? Array.Empty<CultMeshSessionProofSigner>())
                .ToDictionary(signer => signer.Route.Generation, StringComparer.Ordinal);
            _handler = HandleAsync;
            _server.OnCultNet(_handler);
            _peerLifecycle = server as ICultNetSchemaServerPeerLifecycle;
            if (_peerLifecycle != null)
                _peerLifecycle.PeerDisconnected += ForgetPeer;
        }

        /// <summary>
        /// Resolves the runtime identity established by this peer's accepted session-open handshake.
        /// Application ingress must use this transport-bound value instead of trusting payload identity fields.
        /// </summary>
        public bool TryGetSourceRuntimeId(ICultNetSchemaServerPeer peer, out string sourceRuntimeId) =>
            _acceptedPeers.TryGetValue(peer, out sourceRuntimeId!);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultMeshSessionOpenMessage>(_handler);
            if (_peerLifecycle != null)
                _peerLifecycle.PeerDisconnected -= ForgetPeer;
            _acceptedPeers.Clear();
        }

        private Task HandleAsync(CultMeshSessionOpenMessage request, ICultNetSchemaServerPeer peer)
        {
            var error = Validate(request);
            if (error == null)
                error = BindPeer(peer, request.SourceRuntimeId);
            _proofSigners.TryGetValue(request.RouteGeneration ?? string.Empty, out var signer);
            peer.SendCultNet(new CultMeshSessionAcceptedMessage
            {
                MessageId = request.MessageId ?? string.Empty,
                Accepted = error == null,
                VerseId = request.VerseId ?? string.Empty,
                AuthorityRuntimeId = _authorityRuntimeId,
                ProtocolId = request.ProtocolId ?? string.Empty,
                RouteGeneration = request.RouteGeneration ?? string.Empty,
                ClientNonce = request.ClientNonce ?? string.Empty,
                ProviderKeyId = error == null ? signer?.ProviderKeyId ?? string.Empty : string.Empty,
                ProviderSignature = error == null && signer != null ? signer.Sign(request) : string.Empty,
                Error = error
            });
            return Task.CompletedTask;
        }

        private void ForgetPeer(ICultNetSchemaServerPeer peer) => _acceptedPeers.TryRemove(peer, out _);

        private string? BindPeer(ICultNetSchemaServerPeer peer, string sourceRuntimeId)
        {
            if (_acceptedPeers.TryGetValue(peer, out var established))
                return string.Equals(established, sourceRuntimeId, StringComparison.Ordinal)
                    ? null
                    : "session-source-runtime-rebind-forbidden";
            return _acceptedPeers.TryAdd(peer, sourceRuntimeId)
                ? null
                : BindPeer(peer, sourceRuntimeId);
        }

        private string? Validate(CultMeshSessionOpenMessage request)
        {
            if (string.IsNullOrWhiteSpace(request.MessageId)) return "session-message-id-required";
            if (string.IsNullOrWhiteSpace(request.SourceRuntimeId)) return "source-runtime-id-required";
            if (!TryNonce(request.ClientNonce)) return "client-nonce-invalid";
            if (!string.Equals(request.AuthorityRuntimeId, _authorityRuntimeId, StringComparison.Ordinal))
                return "target-runtime-mismatch";
            if (!_verseIds.Contains(request.VerseId ?? string.Empty)) return "verse-not-served";
            if (!_protocolIds.Contains(request.ProtocolId ?? string.Empty)) return "protocol-not-served";
            if (!_routeGenerations.Contains(request.RouteGeneration ?? string.Empty)) return "route-generation-not-served";
            return null;
        }

        private static bool TryNonce(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try { return Convert.FromBase64String(value).Length == 32; }
            catch (FormatException) { return false; }
        }

        private static HashSet<string> Clean(IEnumerable<string> values, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            var result = new HashSet<string>(
                values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()),
                StringComparer.Ordinal);
            if (result.Count == 0) throw new ArgumentException("At least one identity is required.", name);
            return result;
        }

        private static string Require(string value, string name) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", name)
                : value.Trim();
    }

    internal static class CultMeshSessionIdentityClient
    {
        public static async Task VerifyAsync(
            ICultNetSchemaClient client,
            string sourceRuntimeId,
            CultMeshSessionTarget target,
            CultMeshProtocolId protocol,
            CultMeshTransportCandidate candidate,
            CultMeshAuthorityTrustPolicy? trust,
            DateTimeOffset now,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            var route = candidate.AuthorityRoute ?? new CultMeshAuthorityRoute(
                target.AuthorityRuntimeId,
                candidate.Endpoint,
                new[] { protocol.Value },
                candidate.Priority,
                candidate.Generation);
            var verifiedRoute = candidate.VerifiedAuthority ??
                (trust ?? throw new CultMeshSessionException(new CultMeshSessionFailure(
                    CultMeshSessionFailureReason.Authentication,
                    "No consumer trust policy verified this CultMesh route.",
                    candidate.Endpoint))).Verify(target.VerseId, route, now);
            if (client is ICultMeshVerifiedSchemaClient verified)
            {
                if (!verified.IsVerifiedFor(
                    target.VerseId,
                    target.AuthorityRuntimeId,
                    protocol.Value,
                    candidate.Generation))
                {
                    throw new CultMeshSessionException(new CultMeshSessionFailure(
                        CultMeshSessionFailureReason.Authority,
                        $"Native transport identity did not match CultMesh target '{target}' for '{protocol}'."));
                }
                // Native identity is sufficient only for an explicitly selected
                // loopback development route. Remote schema sessions still prove
                // possession of the provider key certified by Odin.
                if (verifiedRoute.IsLocalDevelopment) return;
            }
            var messageId = Guid.NewGuid().ToString("N");
            var nonce = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
            var completion = new TaskCompletionSource<CultMeshSessionAcceptedMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultMeshSessionAcceptedMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                    completion.TrySetResult(response);
            });
            var request = new CultMeshSessionOpenMessage
            {
                MessageId = messageId,
                SourceRuntimeId = sourceRuntimeId,
                VerseId = target.VerseId,
                AuthorityRuntimeId = target.AuthorityRuntimeId,
                ProtocolId = protocol.Value,
                RouteGeneration = candidate.Generation,
                ClientNonce = Convert.ToBase64String(nonce)
            };
            client.SendCultNet(request);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (deadline.Token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(completion.Task, cancelled.Task).ConfigureAwait(false) != completion.Task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new CultMeshSessionException(new CultMeshSessionFailure(
                        CultMeshSessionFailureReason.Timeout,
                        $"Timed out verifying authority runtime '{target.AuthorityRuntimeId}'."));
                }
            }

            var accepted = await completion.Task.ConfigureAwait(false);
            if (!accepted.Accepted ||
                !string.Equals(accepted.VerseId, target.VerseId, StringComparison.Ordinal) ||
                !string.Equals(accepted.AuthorityRuntimeId, target.AuthorityRuntimeId, StringComparison.Ordinal) ||
                !string.Equals(accepted.ProtocolId, protocol.Value, StringComparison.Ordinal) ||
                !string.Equals(accepted.RouteGeneration, candidate.Generation, StringComparison.Ordinal) ||
                !CultMeshAuthorityProof.VerifySession(request, accepted, verifiedRoute))
            {
                throw new CultMeshSessionException(new CultMeshSessionFailure(
                    CultMeshSessionFailureReason.Authority,
                    $"Route did not prove CultMesh authority '{target}' for protocol '{protocol}': " +
                    (accepted.Error ?? "identity-mismatch")));
            }
        }
    }
}
