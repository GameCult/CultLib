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
            CultMeshEndpointId endpointId,
            CancellationToken cancellationToken = default)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (endpointId == null) throw new ArgumentNullException(nameof(endpointId));
            if (!TryParseEndpoint(candidate.Endpoint, out var host, out var port))
                throw new NotSupportedException($"TCP content connector does not support '{candidate.Endpoint}'.");

            var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            try
            {
                await CultMeshTcpContentWire.AwaitAsync(client.ConnectAsync(host, port), timeout.Token)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _connectCount);
                return new CultMeshTcpContentTransport(candidate.Endpoint, client);
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

    internal sealed class CultMeshTcpContentTransport : ICultMeshContentTransport
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, PendingCopy> _pending = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _receiveLoop;
        private bool _disposed;

        public CultMeshTcpContentTransport(string endpoint, TcpClient client)
        {
            Endpoint = endpoint;
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _stream = client.GetStream();
            _receiveLoop = ReceiveLoopAsync(_shutdown.Token);
        }

        public string TransportId => "tcp-content";
        public string Endpoint { get; }

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
        private readonly CultCache _content;
        private readonly Func<string, bool> _canServeHash;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private readonly Task _acceptLoop;
        private long _chunkRequestsServed;
        private bool _disposed;

        public CultMeshTcpContentServer(
            TcpListener listener,
            CultCache content,
            Func<string, bool>? canServeHash = null)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _content = content ?? throw new ArgumentNullException(nameof(content));
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
                chunk = _content.Get<CultMeshCdnArtifactChunk>(key)
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
