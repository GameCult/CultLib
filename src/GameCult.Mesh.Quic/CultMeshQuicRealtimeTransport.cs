using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace GameCult.Mesh.Quic;

/// <summary>Configures identity-bound CultMesh QUIC client connections.</summary>
public sealed class CultMeshQuicRealtimeConnectorOptions
{
    /// <summary>Application protocol negotiated during the TLS handshake.</summary>
    public SslApplicationProtocol ApplicationProtocol { get; set; } = CultMeshQuicRealtimeProtocol.ApplicationProtocol;

    /// <summary>Maximum time permitted for connection establishment.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Optional provider-aware certificate validator. Standard TLS validation is used when omitted.
    /// </summary>
    public Func<CultMeshEndpointId, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>?
        ValidateProviderCertificate { get; set; }
}

/// <summary>Creates MsQuic-backed realtime state connections for .NET runtimes.</summary>
public sealed class CultMeshQuicRealtimeTransportConnector : ICultMeshRealtimeTransportConnector
{
    /// <summary>Discovery scheme owned by this connector.</summary>
    public const string Scheme = "cultmesh-state+quic";

    private readonly CultMeshQuicRealtimeConnectorOptions _options;

    /// <summary>Creates a QUIC connector.</summary>
    public CultMeshQuicRealtimeTransportConnector(CultMeshQuicRealtimeConnectorOptions? options = null)
    {
        _options = options ?? new CultMeshQuicRealtimeConnectorOptions();
        if (_options.HandshakeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "QUIC handshake timeout must be positive.");
    }

    /// <inheritdoc />
    public string ConnectorId => "msquic-realtime";

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public bool CanConnect(CultMeshTransportCandidate candidate) =>
        candidate != null && TryParseEndpoint(candidate.Endpoint, out _, out _);

    /// <inheritdoc />
    public async Task<ICultMeshRealtimeTransport> ConnectAsync(
        CultMeshTransportCandidate candidate,
        CultMeshEndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        if (endpointId == null) throw new ArgumentNullException(nameof(endpointId));
        if (!QuicConnection.IsSupported)
            throw new PlatformNotSupportedException("QUIC is unavailable. Install a supported MsQuic runtime.");
        if (!TryParseEndpoint(candidate.Endpoint, out var host, out var port))
            throw new NotSupportedException($"QUIC realtime connector does not support '{candidate.Endpoint}'.");

        var validator = _options.ValidateProviderCertificate;
        EndPoint remoteEndPoint = IPAddress.TryParse(host, out var address)
            ? new IPEndPoint(address, port)
            : new DnsEndPoint(host, port);
        var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = remoteEndPoint,
            DefaultCloseErrorCode = CultMeshQuicRealtimeProtocol.ConnectionCloseCode,
            DefaultStreamErrorCode = CultMeshQuicRealtimeProtocol.StreamAbortCode,
            MaxInboundUnidirectionalStreams = 1024,
            HandshakeTimeout = _options.HandshakeTimeout,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { _options.ApplicationProtocol },
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    var providerCertificate = certificate as X509Certificate2 ??
                        (certificate == null ? null : new X509Certificate2(certificate));
                    return validator?.Invoke(endpointId, providerCertificate, chain, errors) ??
                        errors == SslPolicyErrors.None ||
                        MatchesAdvertisedCertificatePin(candidate.Endpoint, providerCertificate);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return new CultMeshQuicRealtimeTransport(candidate.Endpoint, connection);
    }

    private static bool TryParseEndpoint(string? endpoint, out string host, out int port)
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

    private static bool MatchesAdvertisedCertificatePin(string endpoint, X509Certificate2? certificate)
    {
        if (certificate == null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;
        var advertised = ParseQueryValue(uri.Query, "cert-sha256");
        if (string.IsNullOrWhiteSpace(advertised)) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return string.Equals(advertised, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ParseQueryValue(string query, string key)
    {
        foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var candidateKey = separator < 0 ? component : component[..separator];
            if (!string.Equals(Uri.UnescapeDataString(candidateKey), key, StringComparison.OrdinalIgnoreCase))
                continue;
            return separator < 0 ? string.Empty : Uri.UnescapeDataString(component[(separator + 1)..]);
        }
        return null;
    }
}

/// <summary>Configures an authenticated CultMesh QUIC realtime listener.</summary>
public sealed class CultMeshQuicRealtimeServerOptions
{
    /// <summary>Local endpoint to bind.</summary>
    public IPEndPoint ListenEndPoint { get; set; } = new(IPAddress.Loopback, 0);

    /// <summary>Server certificate used by QUIC TLS 1.3.</summary>
    public X509Certificate2 ServerCertificate { get; set; } = null!;

    /// <summary>Application protocol negotiated during the TLS handshake.</summary>
    public SslApplicationProtocol ApplicationProtocol { get; set; } = CultMeshQuicRealtimeProtocol.ApplicationProtocol;

    /// <summary>Maximum time permitted for connection establishment.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Accepts .NET QUIC realtime sessions. It owns physical connections only; provider
/// authorization and the meaning of frames remain outside this adapter.
/// </summary>
public sealed class CultMeshQuicRealtimeServer : IAsyncDisposable
{
    private readonly QuicListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<CultMeshQuicRealtimeTransport, byte> _clients = new();
    private readonly CultMeshRealtimeInbox _received = new();
    private readonly TaskCompletionSource<Exception> _backgroundFailure = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;
    private bool _disposed;

    private CultMeshQuicRealtimeServer(QuicListener listener)
    {
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    /// <summary>Bound listener endpoint.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndPoint;

    /// <summary>Number of active physical client connections.</summary>
    public int ConnectionCount => _clients.Count;

    /// <summary>Completes if the listener's background accept loop fails.</summary>
    public Task<Exception> BackgroundFailure => _backgroundFailure.Task;

    /// <summary>Creates and starts an authenticated QUIC listener.</summary>
    public static async Task<CultMeshQuicRealtimeServer> ListenAsync(
        CultMeshQuicRealtimeServerOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.ServerCertificate == null)
            throw new ArgumentException("A QUIC server certificate is required.", nameof(options));
        if (options.HandshakeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "QUIC handshake timeout must be positive.");
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is unavailable. Install a supported MsQuic runtime.");

        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = options.ListenEndPoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { options.ApplicationProtocol },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = CultMeshQuicRealtimeProtocol.ConnectionCloseCode,
                DefaultStreamErrorCode = CultMeshQuicRealtimeProtocol.StreamAbortCode,
                MaxInboundUnidirectionalStreams = 1024,
                HandshakeTimeout = options.HandshakeTimeout,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { options.ApplicationProtocol },
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ServerCertificate = options.ServerCertificate
                }
            })
        }, cancellationToken).ConfigureAwait(false);
        return new CultMeshQuicRealtimeServer(listener);
    }

    /// <summary>Broadcasts one state frame to the currently connected clients.</summary>
    public async Task BroadcastAsync(
        CultMeshRealtimeFrame frame,
        CancellationToken cancellationToken = default)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        cancellationToken.ThrowIfCancellationRequested();

        if (frame.Delivery == CultMeshRealtimeDelivery.LatestOnly)
        {
            foreach (var client in _clients.Keys)
            {
                if (client.TryPublishLatest(frame)) continue;
                if (_clients.TryRemove(client, out _)) client.Dispose();
            }
            return;
        }

        var sends = _clients.Keys.Select(client => SendToConnectedClientAsync(client, frame, cancellationToken)).ToArray();
        if (sends.Length > 0) await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task SendToConnectedClientAsync(
        CultMeshQuicRealtimeTransport client,
        CultMeshRealtimeFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (_clients.TryRemove(client, out _))
                client.Dispose();
        }
    }

    /// <summary>Receives the next client-originated state frame.</summary>
    public Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _received.ReceiveAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var client in _clients.Keys) client.Dispose();
        _clients.Clear();
        _received.Complete();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await _listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
                var transport = new CultMeshQuicRealtimeTransport(
                    connection.RemoteEndPoint?.ToString() ?? "quic-peer",
                    connection,
                    frame => _received.Publish(frame));
                _clients.TryAdd(transport, 0);
                _ = transport.Completion.ContinueWith(
                    completed =>
                    {
                        _clients.TryRemove(transport, out _);
                        transport.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            _backgroundFailure.TrySetResult(error);
        }
    }
}

internal sealed class CultMeshQuicRealtimeTransport : ICultMeshRealtimeTransport
{
    private readonly QuicConnection _connection;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CultMeshRealtimeInbox _received = new();
    private readonly ConcurrentDictionary<string, CultMeshGeneration> _latestGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> _streamReaders = new();
    private readonly SemaphoreSlim _reliableSendGate = new(1, 1);
    private readonly CultMeshRealtimeInbox _outbound = new();
    private readonly Action<CultMeshRealtimeFrame>? _observer;
    private readonly Task _acceptLoop;
    private readonly Task _outboundLoop;
    private readonly Task _completion;
    private QuicStream? _reliableOutbound;
    private int _nextReaderId;
    private bool _disposed;

    public CultMeshQuicRealtimeTransport(
        string endpoint,
        QuicConnection connection,
        Action<CultMeshRealtimeFrame>? observer = null)
    {
        Endpoint = endpoint;
        _connection = connection;
        _observer = observer;
        _acceptLoop = AcceptStreamsAsync(_shutdown.Token);
        _outboundLoop = SendPublishedFramesAsync(_shutdown.Token);
        _completion = Task.WhenAll(_acceptLoop, _outboundLoop);
    }

    public string TransportId => "msquic-realtime";
    public string Endpoint { get; }
    public Task Completion => _completion;

    internal bool TryPublishLatest(CultMeshRealtimeFrame frame)
    {
        if (_disposed || frame.Delivery != CultMeshRealtimeDelivery.LatestOnly) return false;
        return _outbound.Publish(frame);
    }

    public async Task SendAsync(CultMeshRealtimeFrame frame, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CultMeshQuicRealtimeTransport));
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (frame.Delivery == CultMeshRealtimeDelivery.Unreliable)
            throw new NotSupportedException(
                "System.Net.Quic exposes streams but not QUIC datagrams; unreliable delivery requires a native MsQuic connector.");

        var payload = CultMeshQuicRealtimeProtocol.EncodeFrame(frame);
        if (frame.Delivery == CultMeshRealtimeDelivery.ReliableOrdered)
        {
            await _reliableSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _reliableOutbound ??= await OpenStreamAsync(
                    CultMeshQuicRealtimeProtocol.ReliableStream,
                    cancellationToken).ConfigureAwait(false);
                await CultMeshQuicRealtimeProtocol.WriteFramedAsync(
                    _reliableOutbound,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _reliableSendGate.Release();
            }
            return;
        }

        await using var stream = await OpenStreamAsync(
            CultMeshQuicRealtimeProtocol.LatestOnlyStream,
            cancellationToken).ConfigureAwait(false);
        await CultMeshQuicRealtimeProtocol.WriteFramedAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        stream.CompleteWrites();
    }

    public Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _received.ReceiveAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _outbound.Complete();
        _shutdown.Cancel();
        _reliableOutbound?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _received.Complete();
        _reliableSendGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task SendPublishedFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _outbound.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                await SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            _shutdown.Cancel();
            throw;
        }
    }

    private async Task<QuicStream> OpenStreamAsync(byte kind, CancellationToken cancellationToken)
    {
        var stream = await _connection.OpenOutboundStreamAsync(
            QuicStreamType.Unidirectional,
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new[] { kind }, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    private async Task AcceptStreamsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = await _connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
                var readerId = Interlocked.Increment(ref _nextReaderId);
                var reader = ReadStreamOwnedAsync(readerId, stream, cancellationToken);
                _streamReaders.TryAdd(readerId, reader);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (QuicException) when (_disposed)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(_streamReaders.Values).ConfigureAwait(false);
                _received.Complete();
            }
            catch (Exception error)
            {
                _received.Complete(error);
                throw;
            }
        }
    }

    private async Task ReadStreamOwnedAsync(
        int readerId,
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReadStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!_disposed && error is not OperationCanceledException)
        {
            _received.Complete(error);
            _shutdown.Cancel();
        }
        finally
        {
            _streamReaders.TryRemove(readerId, out _);
        }
    }

    private async Task ReadStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var kind = await CultMeshQuicRealtimeProtocol.ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                if (kind != CultMeshQuicRealtimeProtocol.ReliableStream &&
                    kind != CultMeshQuicRealtimeProtocol.LatestOnlyStream)
                    throw new InvalidDataException($"Unknown CultMesh QUIC stream kind {kind}.");

                do
                {
                    var payload = await CultMeshQuicRealtimeProtocol.ReadFramedAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (payload == null) break;
                    var frame = CultMeshQuicRealtimeProtocol.DecodeFrame(payload);
                    if (kind == CultMeshQuicRealtimeProtocol.ReliableStream &&
                        frame.Delivery != CultMeshRealtimeDelivery.ReliableOrdered)
                        throw new InvalidDataException("Reliable QUIC stream carried incompatible delivery semantics.");
                    if (kind == CultMeshQuicRealtimeProtocol.LatestOnlyStream &&
                        frame.Delivery != CultMeshRealtimeDelivery.LatestOnly)
                        throw new InvalidDataException("Latest-only QUIC stream carried incompatible delivery semantics.");
                    PublishReceived(frame);
                }
                while (kind == CultMeshQuicRealtimeProtocol.ReliableStream);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (QuicException) when (_disposed)
            {
            }
        }
    }

    private void PublishReceived(CultMeshRealtimeFrame frame)
    {
        if (frame.Delivery == CultMeshRealtimeDelivery.LatestOnly)
        {
            var key = frame.ChannelId + "\u001f" + frame.BodyId;
            var generation = new CultMeshGeneration(frame.ProducerEpoch, frame.Sequence);
            while (true)
            {
                if (_latestGenerations.TryGetValue(key, out var current))
                {
                    if (generation.CompareTo(current) <= 0) return;
                    if (!_latestGenerations.TryUpdate(key, generation, current)) continue;
                }
                else if (!_latestGenerations.TryAdd(key, generation))
                {
                    continue;
                }
                break;
            }
        }
        _observer?.Invoke(frame);
        _received.Publish(frame);
    }

    private readonly record struct CultMeshGeneration(long ProducerEpoch, long Sequence) : IComparable<CultMeshGeneration>
    {
        public int CompareTo(CultMeshGeneration other)
        {
            var epochOrder = ProducerEpoch.CompareTo(other.ProducerEpoch);
            return epochOrder != 0 ? epochOrder : Sequence.CompareTo(other.Sequence);
        }
    }
}

internal sealed class CultMeshRealtimeInbox
{
    private readonly object _gate = new();
    private readonly Channel<InboxToken> _ready = Channel.CreateUnbounded<InboxToken>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly Dictionary<string, CultMeshRealtimeFrame> _latest = new(StringComparer.Ordinal);
    private bool _completed;

    public bool Publish(CultMeshRealtimeFrame frame)
    {
        if (frame.Delivery != CultMeshRealtimeDelivery.LatestOnly)
        {
            return _ready.Writer.TryWrite(new InboxToken(frame, null));
        }

        var key = frame.ChannelId + "\u001f" + frame.BodyId;
        lock (_gate)
        {
            if (_completed) return false;
            var alreadyPending = _latest.ContainsKey(key);
            _latest[key] = frame;
            return alreadyPending || _ready.Writer.TryWrite(new InboxToken(null, key));
        }
    }

    public async Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var token = await _ready.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (token.Frame != null) return token.Frame;
            lock (_gate)
            {
                if (token.LatestKey != null && _latest.Remove(token.LatestKey, out var latest))
                    return latest;
            }
        }
    }

    public void Complete(Exception? error = null)
    {
        lock (_gate) _completed = true;
        _ready.Writer.TryComplete(error);
    }

    private sealed record InboxToken(CultMeshRealtimeFrame? Frame, string? LatestKey);
}

internal static class CultMeshQuicRealtimeProtocol
{
    public static readonly SslApplicationProtocol ApplicationProtocol =
        new(CultMeshRealtimeWireProtocol.ApplicationProtocolName);
    public const long ConnectionCloseCode = CultMeshRealtimeWireProtocol.ConnectionCloseCode;
    public const long StreamAbortCode = CultMeshRealtimeWireProtocol.StreamAbortCode;
    public const byte ReliableStream = CultMeshRealtimeWireProtocol.ReliableStream;
    public const byte LatestOnlyStream = CultMeshRealtimeWireProtocol.LatestOnlyStream;

    public static byte[] EncodeFrame(CultMeshRealtimeFrame frame) =>
        CultMeshRealtimeWireProtocol.EncodeFrame(frame);

    public static CultMeshRealtimeFrame DecodeFrame(byte[] bytes) =>
        CultMeshRealtimeWireProtocol.DecodeFrame(bytes);

    public static async Task WriteFramedAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadFramedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > CultMeshRealtimeWireProtocol.MaximumEncodedFrameBytes)
            throw new InvalidDataException("Realtime QUIC frame length is invalid.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await ReadExactlyAsync(stream, value, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("QUIC stream ended before its frame was complete.");
            offset += read;
        }
    }
}
