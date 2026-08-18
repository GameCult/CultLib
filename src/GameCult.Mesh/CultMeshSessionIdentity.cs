using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly Func<CultMeshSessionOpenMessage, ICultNetSchemaServerPeer, Task> _handler;
        private bool _disposed;

        public CultMeshSessionIdentityServer(
            ICultNetSchemaServer server,
            string authorityRuntimeId,
            IEnumerable<string> verseIds,
            IEnumerable<string> protocolIds,
            IEnumerable<string> routeGenerations)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _authorityRuntimeId = Require(authorityRuntimeId, nameof(authorityRuntimeId));
            _verseIds = Clean(verseIds, nameof(verseIds));
            _protocolIds = Clean(protocolIds, nameof(protocolIds));
            _routeGenerations = Clean(routeGenerations, nameof(routeGenerations));
            _handler = HandleAsync;
            _server.OnCultNet(_handler);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultMeshSessionOpenMessage>(_handler);
        }

        private Task HandleAsync(CultMeshSessionOpenMessage request, ICultNetSchemaServerPeer peer)
        {
            var error = Validate(request);
            peer.SendCultNet(new CultMeshSessionAcceptedMessage
            {
                MessageId = request.MessageId ?? string.Empty,
                Accepted = error == null,
                VerseId = request.VerseId ?? string.Empty,
                AuthorityRuntimeId = _authorityRuntimeId,
                ProtocolId = request.ProtocolId ?? string.Empty,
                RouteGeneration = request.RouteGeneration ?? string.Empty,
                Error = error
            });
            return Task.CompletedTask;
        }

        private string? Validate(CultMeshSessionOpenMessage request)
        {
            if (string.IsNullOrWhiteSpace(request.MessageId)) return "session-message-id-required";
            if (string.IsNullOrWhiteSpace(request.SourceRuntimeId)) return "source-runtime-id-required";
            if (!string.Equals(request.AuthorityRuntimeId, _authorityRuntimeId, StringComparison.Ordinal))
                return "target-runtime-mismatch";
            if (!_verseIds.Contains(request.VerseId ?? string.Empty)) return "verse-not-served";
            if (!_protocolIds.Contains(request.ProtocolId ?? string.Empty)) return "protocol-not-served";
            if (!_routeGenerations.Contains(request.RouteGeneration ?? string.Empty)) return "route-generation-not-served";
            return null;
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
            string routeGeneration,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (client is ICultMeshVerifiedSchemaClient verified)
            {
                if (!verified.IsVerifiedFor(
                    target.VerseId,
                    target.AuthorityRuntimeId,
                    protocol.Value,
                    routeGeneration ?? string.Empty))
                {
                    throw new CultMeshSessionException(new CultMeshSessionFailure(
                        CultMeshSessionFailureReason.Authority,
                        $"Native transport identity did not match CultMesh target '{target}' for '{protocol}'."));
                }
                return;
            }
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultMeshSessionAcceptedMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultMeshSessionAcceptedMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                    completion.TrySetResult(response);
            });
            client.SendCultNet(new CultMeshSessionOpenMessage
            {
                MessageId = messageId,
                SourceRuntimeId = sourceRuntimeId,
                VerseId = target.VerseId,
                AuthorityRuntimeId = target.AuthorityRuntimeId,
                ProtocolId = protocol.Value,
                RouteGeneration = routeGeneration ?? string.Empty
            });

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
                !string.Equals(accepted.RouteGeneration, routeGeneration ?? string.Empty, StringComparison.Ordinal))
            {
                throw new CultMeshSessionException(new CultMeshSessionFailure(
                    CultMeshSessionFailureReason.Authority,
                    $"Route did not prove CultMesh authority '{target}' for protocol '{protocol}': " +
                    (accepted.Error ?? "identity-mismatch")));
            }
        }
    }
}
