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
    public sealed class CultMeshContentServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly CultCache _content;
        private readonly Func<string, bool> _canServeHash;
        private readonly Func<CultMeshContentChunkRequestMessage, ICultNetSchemaServerPeer, Task> _handler;
        private bool _disposed;

        /// <summary>Attaches content serving to an existing schema host and provider cache.</summary>
        public CultMeshContentServer(
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

    /// <summary>Configures bounded response waiting for session-backed content requests.</summary>
    public sealed class CultMeshSessionContentProviderOptions
    {
        /// <summary>Gets or sets the injected reliability clock.</summary>
        public ICultMeshClock Clock { get; set; } = CultMeshSystemClock.Instance;
        /// <summary>Gets or sets the maximum time to wait for one chunk response.</summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Retrieves verified CDN chunks through one reusable identity-first CultMesh session.
    /// Transfer verification and final cache promotion remain owned by CultMeshContentTransferService.
    /// </summary>
    public sealed class CultMeshSessionContentProvider : ICultMeshContentProvider
    {
        private readonly CultMeshSessionManager _sessions;
        private readonly CultMeshEndpointId _endpointId;
        private readonly CultMeshSessionContentProviderOptions _options;

        /// <summary>Creates a content provider backed by a reusable identity-first session.</summary>
        public CultMeshSessionContentProvider(
            string providerId,
            CultMeshSessionManager sessions,
            CultMeshEndpointId endpointId,
            CultMeshSessionContentProviderOptions? options = null)
        {
            ProviderId = string.IsNullOrWhiteSpace(providerId)
                ? throw new ArgumentException("Provider identity is required.", nameof(providerId))
                : providerId;
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _endpointId = endpointId ?? throw new ArgumentNullException(nameof(endpointId));
            _options = options ?? new CultMeshSessionContentProviderOptions();
            if (_options.ResponseTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "Content response timeout must be positive.");
        }

        /// <inheritdoc />
        public string ProviderId { get; }

        /// <inheritdoc />
        public async Task<CultMeshCdnArtifactChunk?> GetChunkAsync(
            CultMeshCdnChunkRef chunk,
            CancellationToken cancellationToken = default)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            var hash = CultMeshCdn.NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash));
            var session = await _sessions.ConnectAsync(
                _endpointId,
                CultMeshProtocols.Content,
                cancellationToken).ConfigureAwait(false);
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultMeshContentChunkResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var responseSubscription = session.OnCultNet<CultMeshContentChunkResponseMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                    completion.TrySetResult(response);
            });
            using var errorSubscription = session.OnCultNet<CultNetErrorMessage>(error =>
                completion.TrySetException(new IOException(error.Error)));
            session.SendCultNet(new CultMeshContentChunkRequestMessage
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
                    "Content provider '" + ProviderId + "' rejected chunk '" + hash + "': " + response.Error,
                    chunk.RecordKey);
            var resolved = new CultMeshCdnArtifactChunk
            {
                ChunkHash = response.ChunkHash,
                SizeBytes = response.SizeBytes,
                Payload = response.Payload ?? Array.Empty<byte>()
            };
            CultMeshCdn.ValidateChunkPayload(chunk, resolved);
            return resolved;
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
            throw new TimeoutException(
                "Timed out fetching content from provider '" + ProviderId + "' through endpoint identity '" +
                _endpointId.Value + "'.");
        }
    }
}
