using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>
    /// Serves bounded, content-addressed CDN chunks over the CultMesh content protocol.
    /// Manifests and descriptors remain typed document state; payload bytes do not.
    /// </summary>
    public sealed class CultMeshLegacyRudpContentServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly CultCache _content;
        private readonly Func<string, bool> _canServeHash;
        private readonly Func<CultMeshContentChunkRequestMessage, ICultNetSchemaServerPeer, Task> _handler;
        private bool _disposed;

        /// <summary>Attaches content serving to an existing schema host and provider cache.</summary>
        public CultMeshLegacyRudpContentServer(
            ICultNetSchemaServer server,
            CultCache content,
            Func<string, bool>? canServeHash = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _canServeHash = canServeHash ?? (_ => true);
            _handler = HandleAsync;
            _server.OnCultNet(_handler);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultMeshContentChunkRequestMessage>(_handler);
        }

        private Task HandleAsync(
            CultMeshContentChunkRequestMessage request,
            ICultNetSchemaServerPeer peer)
        {
            var response = new CultMeshContentChunkResponseMessage
            {
                MessageId = request?.MessageId ?? string.Empty,
                ChunkHash = request?.ChunkHash ?? string.Empty
            };
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.MessageId))
                    throw new InvalidDataException("Content chunk request requires a message identity.");
                var hash = CultMeshCdn.NormalizeHash(request.ChunkHash, nameof(request.ChunkHash));
                if (!_canServeHash(hash))
                    throw new UnauthorizedAccessException("Content access policy rejected the requested hash.");
                var canonicalKey = CultMeshCdnArtifactChunk.CreateRecordKey(hash);
                if (!string.IsNullOrWhiteSpace(request.RecordKey) &&
                    !string.Equals(request.RecordKey, canonicalKey.Value, StringComparison.Ordinal))
                    throw new InvalidDataException("Content chunk record key disagrees with its content hash.");
                var chunk = _content.Get<CultMeshCdnArtifactChunk>(canonicalKey)
                    ?? throw new FileNotFoundException("Content chunk is not available.", canonicalKey.Value);
                var reference = new CultMeshCdnChunkRef
                {
                    ChunkHash = hash,
                    RecordKey = canonicalKey.Value,
                    SizeBytes = request.ExpectedSizeBytes
                };
                CultMeshCdn.ValidateChunkPayload(reference, chunk);
                response.Found = true;
                response.ChunkHash = hash;
                response.SizeBytes = chunk.SizeBytes;
                response.Payload = chunk.Payload;
            }
            catch (Exception error)
            {
                response.Found = false;
                response.Error = error.GetType().Name + ": " + error.Message;
                response.Payload = Array.Empty<byte>();
            }
            peer.SendCultNet(response);
            return Task.CompletedTask;
        }
    }

    /// <summary>Configures the explicit compatibility path for content over schema-message RUDP.</summary>
    public sealed class CultMeshLegacyRudpContentTransportOptions
    {
        /// <summary>Gets or sets the injected reliability clock.</summary>
        public ICultMeshClock Clock { get; set; } = CultMeshSystemClock.Instance;
        /// <summary>Gets or sets the maximum time to wait for one chunk response.</summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Explicit low-priority compatibility connector for the retired content-over-RUDP path.
    /// It is never installed by default.
    /// </summary>
    public sealed class CultMeshLegacyRudpContentTransportConnector : ICultMeshContentTransportConnector
    {
        public const int LegacyPriority = 10_000;
        private readonly CultMeshSchemaTransportConnector _schemaConnector;
        private readonly CultMeshLegacyRudpContentTransportOptions _options;

        public CultMeshLegacyRudpContentTransportConnector(
            CultMeshLegacyRudpContentTransportOptions? options = null,
            Func<string, ICultNetSchemaClient>? createClient = null)
        {
            _options = options ?? new CultMeshLegacyRudpContentTransportOptions();
            if (_options.ResponseTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "Content response timeout must be positive.");
            _schemaConnector = new CultMeshSchemaTransportConnector(createClient, _options.Clock, _options.ResponseTimeout);
        }

        public string ConnectorId => "legacy-rudp-content";
        public int Priority => LegacyPriority;
        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase);

        public async Task<ICultMeshContentTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshSessionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (!CanConnect(candidate))
                throw new NotSupportedException($"Legacy RUDP content connector does not support '{candidate.Endpoint}'.");
            var client = await _schemaConnector.ConnectAsync(candidate, CultMeshProtocols.Content, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await CultMeshSessionIdentityClient.VerifyAsync(
                    client,
                    "cultmesh-content-client",
                    target,
                    CultMeshProtocols.Content,
                    candidate.Generation,
                    _options.ResponseTimeout,
                    cancellationToken).ConfigureAwait(false);
                return new LegacyRudpContentTransport(
                    candidate.Endpoint, client, _options, target, candidate.Generation);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private sealed class LegacyRudpContentTransport : ICultMeshContentTransport, ICultMeshVerifiedTransport
        {
            private readonly ICultNetSchemaClient _client;
            private readonly CultMeshLegacyRudpContentTransportOptions _options;
            private readonly string _verseId;
            private readonly string _authorityRuntimeId;
            private readonly string _routeGeneration;
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<CultMeshContentChunkResponseMessage>> _pending = new(StringComparer.Ordinal);
            private bool _disposed;

            public LegacyRudpContentTransport(
                string endpoint,
                ICultNetSchemaClient client,
                CultMeshLegacyRudpContentTransportOptions options,
                CultMeshSessionTarget target,
                string routeGeneration)
            {
                Endpoint = endpoint;
                _client = client;
                _options = options;
                _verseId = target.VerseId;
                _authorityRuntimeId = target.AuthorityRuntimeId;
                _routeGeneration = routeGeneration ?? string.Empty;
                _client.OnCultNet<CultMeshContentChunkResponseMessage>(OnResponse);
                _client.OnCultNet<CultNetErrorMessage>(OnError);
            }

            public string TransportId => "legacy-rudp-content";
            public string Endpoint { get; }

            public bool IsVerifiedFor(
                string verseId,
                string authorityRuntimeId,
                string protocolId,
                string routeGeneration) =>
                string.Equals(_verseId, verseId, StringComparison.Ordinal) &&
                string.Equals(_authorityRuntimeId, authorityRuntimeId, StringComparison.Ordinal) &&
                string.Equals(CultMeshProtocols.Content.Value, protocolId, StringComparison.Ordinal) &&
                string.Equals(_routeGeneration, routeGeneration, StringComparison.Ordinal);

            public async Task CopyChunkToAsync(
                CultMeshCdnChunkRef chunk,
                Stream destination,
                CancellationToken cancellationToken = default)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LegacyRudpContentTransport));
                if (chunk == null) throw new ArgumentNullException(nameof(chunk));
                if (destination == null) throw new ArgumentNullException(nameof(destination));
                var hash = CultMeshCdn.NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash));
                var messageId = Guid.NewGuid().ToString("N");
                var completion = new TaskCompletionSource<CultMeshContentChunkResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pending.TryAdd(messageId, completion))
                    throw new InvalidOperationException("Duplicate legacy content request identity.");
                try
                {
                    _client.SendCultNet(new CultMeshContentChunkRequestMessage
                    {
                        MessageId = messageId,
                        ChunkHash = hash,
                        RecordKey = string.IsNullOrWhiteSpace(chunk.RecordKey)
                            ? CultMeshCdnArtifactChunk.CreateRecordKey(hash).Value
                            : chunk.RecordKey,
                        ExpectedSizeBytes = chunk.SizeBytes
                    });
                    var response = await WaitForResponseAsync(completion.Task, cancellationToken).ConfigureAwait(false);
                    if (!response.Found)
                        throw new FileNotFoundException(
                            "Legacy RUDP content provider rejected chunk '" + hash + "': " + response.Error,
                            chunk.RecordKey);
                    var resolved = new CultMeshCdnArtifactChunk
                    {
                        ChunkHash = response.ChunkHash,
                        SizeBytes = response.SizeBytes,
                        Payload = response.Payload ?? Array.Empty<byte>()
                    };
                    CultMeshCdn.ValidateChunkPayload(chunk, resolved);
                    await destination.WriteAsync(resolved.Payload, 0, resolved.Payload.Length, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _pending.TryRemove(messageId, out _);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var completion in _pending.Values)
                    completion.TrySetException(new ObjectDisposedException(nameof(LegacyRudpContentTransport)));
                _pending.Clear();
                _client.Dispose();
            }

            private void OnResponse(CultMeshContentChunkResponseMessage response)
            {
                if (_pending.TryGetValue(response.MessageId ?? string.Empty, out var completion))
                    completion.TrySetResult(response);
            }

            private void OnError(CultNetErrorMessage error)
            {
                var exception = new IOException(error.Error);
                foreach (var completion in _pending.Values) completion.TrySetException(exception);
            }

            private async Task<CultMeshContentChunkResponseMessage> WaitForResponseAsync(
                Task<CultMeshContentChunkResponseMessage> response,
                CancellationToken cancellationToken)
            {
                using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var deadline = _options.Clock.DelayAsync(_options.ResponseTimeout, deadlineCancellation.Token);
                var completed = await Task.WhenAny(response, deadline).ConfigureAwait(false);
                if (completed == response)
                {
                    deadlineCancellation.Cancel();
                    return await response.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out fetching content through the legacy RUDP connector.");
            }
        }
    }
}
