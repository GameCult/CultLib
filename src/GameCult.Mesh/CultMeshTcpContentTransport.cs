using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>Identity served by one dedicated content listener.</summary>
    public sealed class CultMeshTcpContentServerOptions
    {
        public string VerseId { get; set; } = string.Empty;
        public string AuthorityRuntimeId { get; set; } = string.Empty;
        public string RouteGeneration { get; set; } = string.Empty;

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(VerseId)) throw new InvalidOperationException("Content Verse identity is required.");
            if (string.IsNullOrWhiteSpace(AuthorityRuntimeId)) throw new InvalidOperationException("Content authority runtime identity is required.");
            if (string.IsNullOrWhiteSpace(RouteGeneration)) throw new InvalidOperationException("Content route generation is required.");
        }
    }

    /// <summary>Creates dedicated TCP streams for immutable CultMesh content.</summary>
    public sealed class CultMeshTcpContentTransportConnector : ICultMeshContentTransportConnector
    {
        public const string Scheme = "cultmesh-content+tcp";
        private readonly TimeSpan _connectTimeout;
        private int _connectCount;

        public CultMeshTcpContentTransportConnector(TimeSpan? connectTimeout = null)
        {
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
            if (_connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        public string ConnectorId => "tcp-content";
        public int Priority => 0;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            TryParseEndpoint(candidate?.Endpoint, out _, out _);

        public async Task<ICultMeshContentTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshSessionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!TryParseEndpoint(candidate.Endpoint, out var host, out var port))
                throw new NotSupportedException($"TCP content connector does not support '{candidate.Endpoint}'.");

            var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            try
            {
                await CultMeshTcpContentWire.AwaitAsync(client.ConnectAsync(host, port), timeout.Token)
                    .ConfigureAwait(false);
                await CultMeshTcpContentWire.VerifyAuthorityAsync(
                    client.GetStream(), target, candidate, timeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _connectCount);
                return new CultMeshTcpContentTransport(candidate.Endpoint, client, target, candidate.Generation);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        internal static bool TryParseEndpoint(string? endpoint, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
                return false;
            host = uri.Host;
            port = uri.Port;
            return true;
        }
    }

    internal sealed class CultMeshTcpContentTransport : ICultMeshContentTransport, ICultMeshVerifiedTransport
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, PendingCopy> _pending = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _receiveLoop;
        private readonly string _verseId;
        private readonly string _authorityRuntimeId;
        private readonly string _routeGeneration;
        private bool _disposed;

        public CultMeshTcpContentTransport(
            string endpoint,
            TcpClient client,
            CultMeshSessionTarget target,
            string routeGeneration)
        {
            Endpoint = endpoint;
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _verseId = target?.VerseId ?? throw new ArgumentNullException(nameof(target));
            _authorityRuntimeId = target.AuthorityRuntimeId;
            _routeGeneration = routeGeneration ?? string.Empty;
            _stream = client.GetStream();
            _receiveLoop = ReceiveLoopAsync(_shutdown.Token);
        }

        public string TransportId => "tcp-content";
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
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshTcpContentTransport));
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new ArgumentException("Content destination must be writable.", nameof(destination));

            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingCopy(destination, completion, cancellationToken);
            if (!_pending.TryAdd(messageId, pending))
                throw new InvalidOperationException("Duplicate TCP content request identity.");
            try
            {
                var request = new CultMeshContentChunkRequestMessage
                {
                    MessageId = messageId,
                    ChunkHash = CultMeshCdn.NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash)),
                    RecordKey = string.IsNullOrWhiteSpace(chunk.RecordKey)
                        ? CultMeshCdnArtifactChunk.CreateRecordKey(chunk.ChunkHash).Value
                        : chunk.RecordKey,
                    ExpectedSizeBytes = chunk.SizeBytes
                };
                var payload = CultNetSchemaMessageSerialization.Serialize(request);
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await CultMeshTcpContentWire.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
                await CultMeshTcpContentWire.AwaitAsync(completion.Task, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(messageId, out _);
                pending.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _client.Dispose();
            var error = new ObjectDisposedException(nameof(CultMeshTcpContentTransport));
            foreach (var pending in _pending.Values) pending.Completion.TrySetException(error);
            _pending.Clear();
            _sendLock.Dispose();
            _shutdown.Dispose();
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var headerBytes = await CultMeshTcpContentWire.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
                    var response = CultNetSchemaMessageSerialization.Deserialize(headerBytes) as CultMeshContentChunkResponseMessage
                        ?? throw new InvalidDataException("TCP content response used an unexpected schema.");
                    if (!_pending.TryGetValue(response.MessageId ?? string.Empty, out var pending))
                        throw new InvalidDataException("TCP content response did not match an active request.");
                    if (!response.Found)
                    {
                        pending.Completion.TrySetException(new FileNotFoundException(response.Error, response.ChunkHash));
                        continue;
                    }
                    await CultMeshTcpContentWire.CopyExactlyAsync(
                        _stream,
                        pending.Destination,
                        response.SizeBytes,
                        pending.CancellationToken).ConfigureAwait(false);
                    pending.Completion.TrySetResult(true);
                }
            }
            catch (Exception error) when (!_disposed)
            {
                foreach (var pending in _pending.Values) pending.Completion.TrySetException(error);
            }
        }

        private sealed class PendingCopy : IDisposable
        {
            private readonly CancellationTokenRegistration _registration;

            public PendingCopy(
                Stream destination,
                TaskCompletionSource<bool> completion,
                CancellationToken cancellationToken)
            {
                Destination = destination;
                Completion = completion;
                CancellationToken = cancellationToken;
                _registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            }

            public Stream Destination { get; }
            public TaskCompletionSource<bool> Completion { get; }
            public CancellationToken CancellationToken { get; }
            public void Dispose() => _registration.Dispose();
        }
    }

    /// <summary>
    /// Serves content-addressed chunks over a dedicated TCP byte stream. Typed headers
    /// remain CultNet schema messages; chunk bytes are streamed outside those messages.
    /// </summary>
    public sealed class CultMeshTcpContentServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, CultMeshCdnArtifactChunk?> _resolveChunk;
        private readonly Func<string, bool> _canServeHash;
        private readonly CultMeshTcpContentServerOptions _options;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private readonly Task _acceptLoop;
        private long _chunkRequestsServed;
        private bool _disposed;

        public CultMeshTcpContentServer(
            TcpListener listener,
            CultCache content,
            CultMeshTcpContentServerOptions options,
            Func<string, bool>? canServeHash = null)
            : this(listener, CreateCacheResolver(content), options, canServeHash)
        {
        }

        /// <summary>
        /// Starts a dedicated TCP content server over provider-owned immutable chunks.
        /// The resolver receives a normalized SHA-256 hash and may return <see langword="null"/>
        /// when that chunk is unavailable.
        /// </summary>
        public CultMeshTcpContentServer(
            TcpListener listener,
            Func<string, CultMeshCdnArtifactChunk?> resolveChunk,
            CultMeshTcpContentServerOptions options,
            Func<string, bool>? canServeHash = null)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _resolveChunk = resolveChunk ?? throw new ArgumentNullException(nameof(resolveChunk));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _canServeHash = canServeHash ?? (_ => true);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        }

        public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;
        public long ChunkRequestsServed => Interlocked.Read(ref _chunkRequestsServed);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Keys) client.Dispose();
            _clients.Clear();
            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _clients.TryAdd(client, 0);
                    _ = ServeClientAsync(client, cancellationToken);
                }
            }
            catch (Exception) when (_disposed || cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using var stream = client.GetStream();
                if (!await AcceptAuthorityAsync(stream, cancellationToken).ConfigureAwait(false)) return;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var requestBytes = await CultMeshTcpContentWire.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                    var request = CultNetSchemaMessageSerialization.Deserialize(requestBytes) as CultMeshContentChunkRequestMessage
                        ?? throw new InvalidDataException("TCP content request used an unexpected schema.");
                    await SendChunkAsync(stream, request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception) when (_disposed || cancellationToken.IsCancellationRequested || !client.Connected)
            {
            }
            finally
            {
                _clients.TryRemove(client, out _);
                client.Dispose();
            }
        }

        private async Task<bool> AcceptAuthorityAsync(Stream stream, CancellationToken cancellationToken)
        {
            var requestBytes = await CultMeshTcpContentWire.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            var request = CultNetSchemaMessageSerialization.Deserialize(requestBytes) as CultMeshSessionOpenMessage
                ?? throw new InvalidDataException("TCP content session did not begin with an authority handshake.");
            var accepted = string.Equals(request.VerseId, _options.VerseId, StringComparison.Ordinal) &&
                string.Equals(request.AuthorityRuntimeId, _options.AuthorityRuntimeId, StringComparison.Ordinal) &&
                string.Equals(request.ProtocolId, CultMeshProtocols.Content.Value, StringComparison.Ordinal) &&
                string.Equals(request.RouteGeneration, _options.RouteGeneration, StringComparison.Ordinal);
            var response = new CultMeshSessionAcceptedMessage
            {
                MessageId = request.MessageId,
                Accepted = accepted,
                VerseId = _options.VerseId,
                AuthorityRuntimeId = _options.AuthorityRuntimeId,
                ProtocolId = CultMeshProtocols.Content.Value,
                RouteGeneration = _options.RouteGeneration,
                Error = accepted ? null : "Content session target does not match this authority route."
            };
            await CultMeshTcpContentWire.WriteFrameAsync(
                stream,
                CultNetSchemaMessageSerialization.Serialize(response),
                cancellationToken).ConfigureAwait(false);
            return accepted;
        }

        private async Task SendChunkAsync(
            Stream stream,
            CultMeshContentChunkRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new CultMeshContentChunkResponseMessage
            {
                MessageId = request.MessageId,
                ChunkHash = request.ChunkHash,
                Payload = Array.Empty<byte>()
            };
            CultMeshCdnArtifactChunk? chunk = null;
            try
            {
                if (string.IsNullOrWhiteSpace(request.MessageId))
                    throw new InvalidDataException("Content chunk request requires a message identity.");
                var hash = CultMeshCdn.NormalizeHash(request.ChunkHash, nameof(request.ChunkHash));
                if (!_canServeHash(hash))
                    throw new UnauthorizedAccessException("Content access policy rejected the requested hash.");
                var key = CultMeshCdnArtifactChunk.CreateRecordKey(hash);
                if (!string.IsNullOrWhiteSpace(request.RecordKey) &&
                    !string.Equals(request.RecordKey, key.Value, StringComparison.Ordinal))
                    throw new InvalidDataException("Content chunk record key disagrees with its content hash.");
                chunk = _resolveChunk(hash)
                    ?? throw new FileNotFoundException("Content chunk is not available.", key.Value);
                CultMeshCdn.ValidateChunkPayload(new CultMeshCdnChunkRef
                {
                    ChunkHash = hash,
                    RecordKey = key.Value,
                    SizeBytes = request.ExpectedSizeBytes
                }, chunk);
                response.Found = true;
                response.ChunkHash = hash;
                response.SizeBytes = chunk.SizeBytes;
                Interlocked.Increment(ref _chunkRequestsServed);
            }
            catch (Exception error)
            {
                response.Found = false;
                response.Error = error.GetType().Name + ": " + error.Message;
            }

            await CultMeshTcpContentWire.WriteFrameAsync(
                stream,
                CultNetSchemaMessageSerialization.Serialize(response),
                cancellationToken).ConfigureAwait(false);
            if (chunk != null)
            {
                await stream.WriteAsync(chunk.Payload, 0, chunk.Payload.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static Func<string, CultMeshCdnArtifactChunk?> CreateCacheResolver(CultCache content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            return hash => content.Get<CultMeshCdnArtifactChunk>(CultMeshCdnArtifactChunk.CreateRecordKey(hash));
        }
    }

    internal static class CultMeshTcpContentWire
    {
        private const int HeaderBytes = 4;
        private const int MaximumHeaderBytes = 1024 * 1024;

        public static async Task AwaitAsync(Task task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(cancellationToken);
            }
            await task.ConfigureAwait(false);
        }

        public static async Task WriteFrameAsync(
            Stream stream,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            if (payload.Length > MaximumHeaderBytes)
                throw new InvalidDataException("TCP content control frame exceeds its size bound.");
            var header = new byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public static async Task VerifyAuthorityAsync(
            Stream stream,
            CultMeshSessionTarget target,
            CultMeshTransportCandidate candidate,
            CancellationToken cancellationToken)
        {
            var request = new CultMeshSessionOpenMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SourceRuntimeId = "cultmesh-content-client",
                VerseId = target.VerseId,
                AuthorityRuntimeId = target.AuthorityRuntimeId,
                ProtocolId = CultMeshProtocols.Content.Value,
                RouteGeneration = candidate.Generation
            };
            await WriteFrameAsync(stream, CultNetSchemaMessageSerialization.Serialize(request), cancellationToken)
                .ConfigureAwait(false);
            var responseBytes = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            var response = CultNetSchemaMessageSerialization.Deserialize(responseBytes) as CultMeshSessionAcceptedMessage
                ?? throw new InvalidDataException("TCP content authority handshake returned an unexpected schema.");
            if (!string.Equals(response.MessageId, request.MessageId, StringComparison.Ordinal) ||
                !response.Accepted ||
                !string.Equals(response.VerseId, target.VerseId, StringComparison.Ordinal) ||
                !string.Equals(response.AuthorityRuntimeId, target.AuthorityRuntimeId, StringComparison.Ordinal) ||
                !string.Equals(response.ProtocolId, CultMeshProtocols.Content.Value, StringComparison.Ordinal) ||
                !string.Equals(response.RouteGeneration, candidate.Generation, StringComparison.Ordinal))
                throw new CultMeshSessionException(new CultMeshSessionFailure(
                    CultMeshSessionFailureReason.Authority,
                    response.Error ?? "TCP content endpoint did not prove the selected authority route.",
                    candidate.Endpoint));
        }

        public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[HeaderBytes];
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
            if (size <= 0 || size > MaximumHeaderBytes)
                throw new InvalidDataException("TCP content control frame has an invalid size.");
            var payload = new byte[size];
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            return payload;
        }

        public static async Task CopyExactlyAsync(
            Stream source,
            Stream destination,
            long bytes,
            CancellationToken cancellationToken)
        {
            if (bytes < 0) throw new InvalidDataException("TCP content body size cannot be negative.");
            var buffer = new byte[81920];
            var remaining = bytes;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer, 0, requested, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}
