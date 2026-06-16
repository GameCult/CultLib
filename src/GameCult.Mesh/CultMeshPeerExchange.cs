using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Networking;
using LiteNetLib;
using R3;

namespace GameCult.Mesh
{
    /// <summary>
    /// Well-known CultMesh peer roles.
    /// </summary>
    public static class CultMeshPeerRoles
    {
        /// <summary>Peer can answer discovery requests.</summary>
        public const string Discovery = "discovery";
        /// <summary>Peer can serve authoritative shard logs.</summary>
        public const string ShardPrimary = "shard-primary";
        /// <summary>Peer can serve replicated shard logs.</summary>
        public const string ShardReplica = "shard-replica";
        /// <summary>Peer can serve read traffic.</summary>
        public const string ReadReplica = "read-replica";
        /// <summary>Peer can observe simulation facts.</summary>
        public const string SimulationObserver = "simulation-observer";
    }

    /// <summary>
    /// Candidate peer contact information for a Verse.
    /// </summary>
    public sealed class CultMeshPeerCard
    {
        /// <summary>
        /// Creates a peer card.
        /// </summary>
        public CultMeshPeerCard(
            string peerId,
            string verseId,
            IEnumerable<string> endpoints,
            IEnumerable<string>? roles = null,
            IEnumerable<string>? shardIds = null,
            string? region = null,
            string? authorityLeaseId = null,
            string? expiresAt = null,
            string? signature = null)
        {
            PeerId = RequireNonEmpty(peerId, nameof(peerId));
            VerseId = RequireNonEmpty(verseId, nameof(verseId));
            Endpoints = Clean(endpoints);
            Roles = Clean(roles);
            ShardIds = Clean(shardIds);
            Region = region;
            AuthorityLeaseId = authorityLeaseId;
            ExpiresAt = expiresAt;
            Signature = signature;
        }

        /// <summary>Gets the stable peer id.</summary>
        public string PeerId { get; }
        /// <summary>Gets the Verse id this peer participates in.</summary>
        public string VerseId { get; }
        /// <summary>Gets reachable endpoints.</summary>
        public IReadOnlyList<string> Endpoints { get; }
        /// <summary>Gets advertised roles.</summary>
        public IReadOnlyList<string> Roles { get; }
        /// <summary>Gets shard ids this peer can serve or observe.</summary>
        public IReadOnlyList<string> ShardIds { get; }
        /// <summary>Gets an optional region/locality label.</summary>
        public string? Region { get; }
        /// <summary>Gets the authority lease id, when present.</summary>
        public string? AuthorityLeaseId { get; }
        /// <summary>Gets the expiry timestamp for this card.</summary>
        public string? ExpiresAt { get; }
        /// <summary>Gets an optional signature over the card.</summary>
        public string? Signature { get; }

        /// <summary>
        /// Returns whether the card advertises a role.
        /// </summary>
        public bool HasRole(string role)
        {
            return Roles.Contains(role, StringComparer.Ordinal);
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
    /// Conversion helpers for CultMesh peer exchange messages.
    /// </summary>
    public static class CultMeshPeerMessages
    {
        /// <summary>
        /// Converts a peer card to its schema-v0 wire shape.
        /// </summary>
        public static CultMeshPeerCardMessage ToMessage(this CultMeshPeerCard card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            return new CultMeshPeerCardMessage
            {
                PeerId = card.PeerId,
                VerseId = card.VerseId,
                Endpoints = card.Endpoints.ToArray(),
                Roles = card.Roles.ToArray(),
                ShardIds = card.ShardIds.ToArray(),
                Region = card.Region,
                AuthorityLeaseId = card.AuthorityLeaseId,
                ExpiresAt = card.ExpiresAt,
                Signature = card.Signature
            };
        }

        /// <summary>
        /// Converts a wire peer card to the local public API shape.
        /// </summary>
        public static CultMeshPeerCard ToPeerCard(this CultMeshPeerCardMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return new CultMeshPeerCard(
                message.PeerId,
                message.VerseId,
                message.Endpoints,
                message.Roles,
                message.ShardIds,
                message.Region,
                message.AuthorityLeaseId,
                message.ExpiresAt,
                message.Signature);
        }
    }

    /// <summary>
    /// Reactive local catalog for discovered CultMesh peers.
    /// </summary>
    public sealed class CultMeshPeerCatalog : IDisposable
    {
        private readonly Dictionary<string, CultMeshPeerCard> _peers = new(StringComparer.Ordinal);
        private readonly Subject<CultMeshPeerCard> _updates = new();
        private bool _disposed;

        /// <summary>Gets all known peers.</summary>
        public IReadOnlyList<CultMeshPeerCard> Peers => _peers.Values.OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();

        /// <summary>Watches peer card updates.</summary>
        public Observable<CultMeshPeerCard> Watch()
        {
            ThrowIfDisposed();
            return _updates;
        }

        /// <summary>Adds or replaces a peer card.</summary>
        public void Upsert(CultMeshPeerCard card)
        {
            ThrowIfDisposed();
            if (card == null) throw new ArgumentNullException(nameof(card));
            _peers[card.PeerId] = card;
            _updates.OnNext(card);
        }

        /// <summary>Adds or replaces peer cards from a wire response.</summary>
        public void Upsert(CultMeshPeerExchangeResponseMessage response)
        {
            ThrowIfDisposed();
            if (response == null) throw new ArgumentNullException(nameof(response));
            foreach (var peer in response.Peers)
            {
                Upsert(peer.ToPeerCard());
            }
        }

        /// <summary>Finds peers by Verse and optional role.</summary>
        public IReadOnlyList<CultMeshPeerCard> Find(string verseId, string? role = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(verseId)) throw new ArgumentException("Value must be non-empty.", nameof(verseId));
            return _peers.Values
                .Where(peer => string.Equals(peer.VerseId, verseId, StringComparison.Ordinal))
                .Where(peer => string.IsNullOrWhiteSpace(role) || peer.HasRole(role!))
                .OrderBy(peer => peer.PeerId, StringComparer.Ordinal)
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
            _peers.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultMeshPeerCatalog));
            }
        }
    }

    /// <summary>
    /// Answers CultMesh peer exchange requests from a local peer catalog.
    /// </summary>
    public sealed class CultMeshPeerExchangeServer : IDisposable
    {
        private readonly Server _server;
        private readonly CultMeshPeerCatalog _catalog;
        private readonly Func<CultMeshPeerExchangeRequestMessage, NetPeer, Task> _requestHandler;
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a peer exchange bridge.
        /// </summary>
        public CultMeshPeerExchangeServer(Server server, CultMeshPeerCatalog catalog)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _requestHandler = HandleRequestAsync;
            _server.OnCultNet(_requestHandler);
        }

        /// <summary>
        /// Creates a peer exchange response for a request.
        /// </summary>
        public CultMeshPeerExchangeResponseMessage CreateResponse(CultMeshPeerExchangeRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.VerseId))
            {
                throw new ArgumentException("Peer exchange requires a verseId.", nameof(request));
            }

            var known = request.KnownPeerIds == null || request.KnownPeerIds.Length == 0
                ? null
                : new HashSet<string>(request.KnownPeerIds, StringComparer.Ordinal);
            var roles = request.Roles == null || request.Roles.Length == 0
                ? null
                : new HashSet<string>(request.Roles, StringComparer.Ordinal);
            var peers = _catalog.Find(request.VerseId)
                .Where(peer => known == null || !known.Contains(peer.PeerId))
                .Where(peer => roles == null || peer.Roles.Any(role => roles.Contains(role)))
                .Take(request.Limit ?? int.MaxValue)
                .Select(peer => peer.ToMessage())
                .ToArray();

            return new CultMeshPeerExchangeResponseMessage
            {
                MessageId = string.IsNullOrWhiteSpace(request.MessageId)
                    ? Guid.NewGuid().ToString("N")
                    : request.MessageId,
                Peers = peers
            };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _server.RemoveCultNetMessageListener<CultMeshPeerExchangeRequestMessage>(_requestHandler);
        }

        private Task HandleRequestAsync(CultMeshPeerExchangeRequestMessage request, NetPeer peer)
        {
            return HandleRequestAsync(request, _server.GetPeerContext(peer));
        }

        private Task HandleRequestAsync(CultMeshPeerExchangeRequestMessage request, CultNetServerPeer peer)
        {
            peer.SendCultNet(CreateResponse(request));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Options for schema-v0 CultMesh peer exchange clients.
    /// </summary>
    public sealed class CultMeshPeerExchangeClientOptions
    {
        /// <summary>
        /// Gets or sets client security options used to connect to peer exchange endpoints.
        /// </summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets how long to wait for a connection before failing.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets how long to wait for a peer exchange response before failing.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets a callback used to customize each ephemeral exchange client.
        /// </summary>
        public Action<Client>? ConfigureClient { get; set; }
    }

    /// <summary>
    /// Fetches peer cards from CultMesh peers.
    /// </summary>
    public sealed class CultMeshPeerExchangeClient
    {
        private readonly CultMeshPeerExchangeClientOptions _options;

        /// <summary>
        /// Creates a peer exchange client.
        /// </summary>
        public CultMeshPeerExchangeClient(CultMeshPeerExchangeClientOptions? options = null)
        {
            _options = options ?? new CultMeshPeerExchangeClientOptions();
        }

        /// <summary>
        /// Fetches peer cards from one endpoint.
        /// </summary>
        public async Task<CultMeshPeerExchangeResponseMessage> FetchAsync(
            string endpoint,
            CultMeshPeerExchangeRequestMessage request)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.VerseId)) throw new ArgumentException("Peer exchange requires a verseId.", nameof(request));

            var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(endpoint);
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultMeshPeerExchangeResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var client = new Client(_options.Security ?? ClientSecurityOptions.Development())
            {
                AllowUnverifiedCultNetMessages = true
            };
            _options.ConfigureClient?.Invoke(client);
            client.OnCultNet<CultMeshPeerExchangeResponseMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                {
                    completion.TrySetResult(response);
                }
            });
            client.OnCultNet<CultNetErrorMessage>(error =>
                completion.TrySetException(new InvalidOperationException(error.Error)));

            client.Connect(host, port);
            await WaitForConnectionAsync(client, endpoint).ConfigureAwait(false);
            client.SendCultNet(new CultMeshPeerExchangeRequestMessage
            {
                MessageId = messageId,
                VerseId = request.VerseId,
                Roles = request.Roles,
                KnownPeerIds = request.KnownPeerIds,
                Limit = request.Limit
            });

            return await WaitForResponseAsync(completion.Task, endpoint).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches peer cards from endpoints and upserts every response into a local peer catalog.
        /// </summary>
        public async Task<int> DiscoverAsync(
            CultMeshPeerCatalog catalog,
            IEnumerable<string> endpoints,
            string verseId,
            IEnumerable<string>? roles = null,
            int? limit = null)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));
            if (string.IsNullOrWhiteSpace(verseId)) throw new ArgumentException("Value must be non-empty.", nameof(verseId));

            var count = 0;
            foreach (var endpoint in endpoints.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                var response = await FetchAsync(endpoint, new CultMeshPeerExchangeRequestMessage
                {
                    VerseId = verseId,
                    Roles = roles?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray(),
                    KnownPeerIds = catalog.Find(verseId).Select(peer => peer.PeerId).ToArray(),
                    Limit = limit
                }).ConfigureAwait(false);
                catalog.Upsert(response);
                count += response.Peers.Length;
            }

            return count;
        }

        private async Task WaitForConnectionAsync(Client client, string endpoint)
        {
            var deadline = DateTimeOffset.UtcNow + _options.ConnectTimeout;
            while (!client.Connected)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out connecting to peer exchange endpoint {endpoint}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        private async Task<CultMeshPeerExchangeResponseMessage> WaitForResponseAsync(
            Task<CultMeshPeerExchangeResponseMessage> responseTask,
            string endpoint)
        {
            var timeoutTask = Task.Delay(_options.ResponseTimeout);
            var completed = await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false);
            if (completed != responseTask)
            {
                throw new TimeoutException($"Timed out waiting for peer exchange response from {endpoint}.");
            }

            return await responseTask.ConfigureAwait(false);
        }
    }
}
