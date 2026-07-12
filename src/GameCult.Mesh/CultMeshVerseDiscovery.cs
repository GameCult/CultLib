using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>
    /// Conversion helpers for CultMesh Verse wire messages.
    /// </summary>
    public static class CultMeshVerseMessages
    {
        /// <summary>
        /// Converts a local Verse descriptor to its schema-v0 wire shape.
        /// </summary>
        public static CultMeshVerseDescriptorMessage ToMessage(this CultMeshVerseDescriptor verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return new CultMeshVerseDescriptorMessage
            {
                VerseId = verse.VerseId,
                DisplayName = verse.DisplayName,
                AuthorityModel = verse.AuthorityModel.ToString(),
                Compatibility = new CultMeshVerseCompatibilityMessage
                {
                    TransportVersion = verse.Compatibility.TransportVersion,
                    RulesHash = verse.Compatibility.RulesHash,
                    CompatibleVerseIds = verse.Compatibility.CompatibleVerseIds.ToArray(),
                    RequiredPluginIds = verse.Compatibility.RequiredPluginIds.ToArray(),
                    OptionalPluginIds = verse.Compatibility.OptionalPluginIds.ToArray()
                },
                DiscoveryEndpoints = verse.DiscoveryEndpoints.ToArray(),
                AuthorityRuntimeIds = verse.AuthorityRuntimeIds.ToArray(),
                ParentVerseId = verse.ParentVerseId,
                Description = verse.Description
            };
        }

        /// <summary>
        /// Converts a schema-v0 Verse descriptor to the local public API shape.
        /// </summary>
        public static CultMeshVerseDescriptor ToVerseDescriptor(this CultMeshVerseDescriptorMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var authorityModel = Enum.TryParse<CultMeshVerseAuthorityModel>(
                message.AuthorityModel,
                ignoreCase: true,
                out var parsed)
                ? parsed
                : CultMeshVerseAuthorityModel.SubscribedOverlay;
            return new CultMeshVerseDescriptor(
                message.VerseId,
                message.DisplayName,
                authorityModel,
                new CultMeshVerseCompatibility(
                    message.Compatibility.TransportVersion,
                    message.Compatibility.RulesHash,
                    message.Compatibility.CompatibleVerseIds,
                    message.Compatibility.RequiredPluginIds,
                    message.Compatibility.OptionalPluginIds),
                message.DiscoveryEndpoints,
                message.AuthorityRuntimeIds,
                message.ParentVerseId,
                message.Description);
        }
    }

    /// <summary>
    /// Answers CultMesh Verse discovery requests from a local Verse catalog.
    /// </summary>
    public sealed class CultMeshVerseDiscoveryServer : IDisposable
    {
        private readonly Server _server;
        private readonly CultMeshVerseCatalog _catalog;
        private readonly Func<CultMeshVerseCatalogRequestMessage, CultNetServerPeer, Task> _requestHandler;
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a Verse discovery bridge.
        /// </summary>
        public CultMeshVerseDiscoveryServer(Server server, CultMeshVerseCatalog catalog)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _requestHandler = HandleRequestAsync;
            _server.OnCultNet(_requestHandler);
        }

        /// <summary>
        /// Creates a Verse catalog response for a request.
        /// </summary>
        public CultMeshVerseCatalogResponseMessage CreateResponse(CultMeshVerseCatalogRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var verseIds = request.VerseIds == null || request.VerseIds.Length == 0
                ? null
                : new HashSet<string>(request.VerseIds, StringComparer.Ordinal);
            var verses = _catalog.Verses
                .Where(verse => verseIds == null || verseIds.Contains(verse.VerseId))
                .Where(verse => string.IsNullOrWhiteSpace(request.TransportVersion) ||
                                string.Equals(verse.Compatibility.TransportVersion, request.TransportVersion, StringComparison.Ordinal))
                .Select(verse => verse.ToMessage())
                .ToArray();

            return new CultMeshVerseCatalogResponseMessage
            {
                MessageId = string.IsNullOrWhiteSpace(request.MessageId)
                    ? Guid.NewGuid().ToString("N")
                    : request.MessageId,
                Verses = verses
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
            _server.RemoveCultNetMessageListener<CultMeshVerseCatalogRequestMessage>(_requestHandler);
        }

        private Task HandleRequestAsync(CultMeshVerseCatalogRequestMessage request, CultNetServerPeer peer)
        {
            peer.SendCultNet(CreateResponse(request));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Options for schema-v0 CultMesh Verse discovery clients.
    /// </summary>
    public sealed class CultMeshVerseDiscoveryClientOptions
    {
        /// <summary>
        /// Gets or sets client security options used to connect to discovery endpoints.
        /// </summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets how long to wait for a connection before failing.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets how long to wait for a discovery response before failing.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets a callback used to customize each ephemeral discovery client.
        /// </summary>
        public Action<Client>? ConfigureClient { get; set; }

        /// <summary>
        /// Gets or sets the schema-v0 client factory.
        /// Defaults to endpoint selection: rudp:// uses RUDP and cultnet:// uses LiteNetLib.
        /// </summary>
        public Func<ICultNetSchemaClient>? CreateClient { get; set; }

        /// <summary>Gets or sets the clock that owns discovery deadlines.</summary>
        public ICultMeshClock Clock { get; set; } = CultMeshSystemClock.Instance;

        /// <summary>Gets or sets the bounded operational diagnostic sink.</summary>
        public ICultMeshDiagnosticSink Diagnostics { get; set; } = CultMeshNullDiagnosticSink.Instance;

        /// <summary>Gets or sets the identity of this bootstrap lookup source.</summary>
        public string SourceId { get; set; } = "configured-bootstrap";
    }

    /// <summary>
    /// Fetches CultMesh Verse catalogs from discovery endpoints.
    /// </summary>
    public sealed class CultMeshVerseDiscoveryClient
    {
        private readonly CultMeshVerseDiscoveryClientOptions _options;
        private long _diagnosticSequence;

        /// <summary>
        /// Creates a Verse discovery client.
        /// </summary>
        public CultMeshVerseDiscoveryClient(CultMeshVerseDiscoveryClientOptions? options = null)
        {
            _options = options ?? new CultMeshVerseDiscoveryClientOptions();
        }

        /// <summary>
        /// Fetches a Verse catalog response from one discovery endpoint.
        /// </summary>
        public async Task<CultMeshVerseCatalogResponseMessage> FetchAsync(
            string endpoint,
            CultMeshVerseCatalogRequestMessage? request = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(endpoint);
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultMeshVerseCatalogResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var client = CreateClient(endpoint);
            client.OnCultNet<CultMeshVerseCatalogResponseMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                {
                    completion.TrySetResult(response);
                }
            });
            client.OnCultNet<CultNetErrorMessage>(error =>
                completion.TrySetException(new InvalidOperationException(error.Error)));

            Emit(CultMeshDiagnosticKind.ConnectionAttempt, messageId, endpoint, "connecting");
            try
            {
                client.Connect(host, port);
                var backgroundFailure = (client as ICultNetSchemaClientHealth)?.BackgroundFailure;
                await WaitForConnectionAsync(client, endpoint, backgroundFailure).ConfigureAwait(false);
                client.SendCultNet(new CultMeshVerseCatalogRequestMessage
                {
                    MessageId = messageId,
                    TransportVersion = request?.TransportVersion,
                    VerseIds = request?.VerseIds
                });

                var response = await WaitForResponseAsync(completion.Task, endpoint, backgroundFailure).ConfigureAwait(false);
                Emit(
                    CultMeshDiagnosticKind.DiscoveryObservation,
                    messageId,
                    endpoint,
                    "fresh",
                    schemaVersion: response.SchemaVersion);
                return response;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Emit(
                    CultMeshDiagnosticKind.CandidateRejected,
                    messageId,
                    endpoint,
                    "unavailable",
                    ReasonCode(error));
                throw;
            }
        }

        /// <summary>
        /// Fetches Verse catalogs from endpoints and upserts every response into a local catalog.
        /// </summary>
        public async Task<int> DiscoverAsync(
            CultMeshVerseCatalog catalog,
            IEnumerable<string> endpoints,
            string? transportVersion = null)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (endpoints == null) throw new ArgumentNullException(nameof(endpoints));

            var count = 0;
            foreach (var endpoint in endpoints.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                var response = await FetchAsync(endpoint, new CultMeshVerseCatalogRequestMessage
                {
                    TransportVersion = transportVersion
                }).ConfigureAwait(false);
                catalog.Upsert(response);
                count += response.Verses.Length;
            }

            return count;
        }

        private ICultNetSchemaClient CreateClient(string endpoint)
        {
            return _options.CreateClient?.Invoke()
                   ?? CultNetSchemaClients.CreateForEndpoint(endpoint, _options.Security, _options.ConfigureClient);
        }

        private async Task WaitForConnectionAsync(
            ICultNetSchemaClient client,
            string endpoint,
            Task<Exception>? backgroundFailure)
        {
            var deadline = _options.Clock.UtcNow + _options.ConnectTimeout;
            while (!client.Connected)
            {
                if (backgroundFailure?.IsCompleted == true)
                {
                    var error = await backgroundFailure.ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Verse discovery client failed while connecting to {endpoint}.",
                        error);
                }
                if (_options.Clock.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out connecting to Verse discovery endpoint {endpoint}.");
                }

                await _options.Clock.DelayAsync(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        private async Task<CultMeshVerseCatalogResponseMessage> WaitForResponseAsync(
            Task<CultMeshVerseCatalogResponseMessage> responseTask,
            string endpoint,
            Task<Exception>? backgroundFailure)
        {
            var timeoutTask = _options.Clock.DelayAsync(_options.ResponseTimeout);
            var completed = backgroundFailure == null
                ? await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false)
                : await Task.WhenAny(responseTask, timeoutTask, backgroundFailure).ConfigureAwait(false);
            if (completed == backgroundFailure)
            {
                var error = await backgroundFailure.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Verse discovery client failed while waiting for a response from {endpoint}.",
                    error);
            }
            if (completed != responseTask)
            {
                throw new TimeoutException($"Timed out waiting for Verse discovery response from {endpoint}.");
            }

            return await responseTask.ConfigureAwait(false);
        }

        private void Emit(
            CultMeshDiagnosticKind kind,
            string operationId,
            string endpoint,
            string state,
            string reasonCode = "",
            string schemaVersion = "")
        {
            _options.Diagnostics.Emit(new CultMeshDiagnosticEvent(
                Interlocked.Increment(ref _diagnosticSequence),
                _options.Clock.UtcNow,
                CultMeshReliabilityOrgan.Discovery,
                kind,
                operationId,
                endpoint,
                state,
                reasonCode,
                _options.SourceId,
                endpoint,
                schemaVersion));
        }

        private static string ReasonCode(Exception error)
        {
            if (error is TimeoutException) return "timeout";
            if (error is InvalidOperationException) return "transport_failure";
            return "lookup_failure";
        }
    }
}
