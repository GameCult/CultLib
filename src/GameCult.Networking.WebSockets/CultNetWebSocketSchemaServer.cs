using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using GameCult.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameCult.Networking.WebSockets;

/// <summary>Controls the bounded CultNet schema-v0 WebSocket endpoint.</summary>
public sealed class CultNetWebSocketEndpointOptions
{
    /// <summary>Maximum accepted binary CultNet message size.</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Authorizes the HTTP upgrade before it can become a CultNet peer. Production hosts
    /// should bind this to their authenticated session middleware.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>>? AuthorizeAsync { get; init; }

    /// <summary>Allows an unauthenticated endpoint for explicit local development only.</summary>
    public bool AllowAnonymousDevelopment { get; init; }
}

/// <summary>
/// Adapts one binary WebSocket endpoint to the transport-neutral CultNet schema server port.
/// The host owns HTTP, TLS, and authentication; this adapter owns bounded binary message
/// assembly, schema validation, handler dispatch, peer lifetime, and serialized sends.
/// </summary>
public sealed class CultNetWebSocketSchemaServer :
    ICultNetSchemaServer,
    ICultNetSchemaServerPeerLifecycle,
    IAsyncDisposable
{
    private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();
    private readonly ConcurrentDictionary<WebSocketPeer, byte> _peers = new();
    private bool _disposed;

    /// <inheritdoc />
    public event Action<ICultNetSchemaServerPeer>? PeerDisconnected;

    /// <summary>Gets the active upgraded peer count.</summary>
    public int PeerCount => _peers.Count;

    /// <inheritdoc />
    public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
        where TMessage : ICultNetSchemaMessage
    {
        ArgumentNullException.ThrowIfNull(callback);
        ThrowIfDisposed();
        _handlers[typeof(TMessage)] = callback;
    }

    /// <inheritdoc />
    public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
        where TMessage : ICultNetSchemaMessage
    {
        if (_handlers.TryGetValue(typeof(TMessage), out var existing) && existing == callback)
            _handlers.TryRemove(typeof(TMessage), out _);
    }

    /// <summary>Serves one already-authorized WebSocket until it closes.</summary>
    public async Task AcceptAsync(
        WebSocket socket,
        EndPoint? remoteEndPoint = null,
        int maxMessageBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ThrowIfDisposed();
        if (maxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        var peer = new WebSocketPeer(socket, remoteEndPoint, maxMessageBytes);
        if (!_peers.TryAdd(peer, 0)) throw new InvalidOperationException("CultNet WebSocket peer already exists.");
        try
        {
            while (!_disposed && socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payload = await CultNetWebSocketMessageIO.ReceiveAsync(socket, maxMessageBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (payload == null) break;
                var message = CultNetSchemaMessageSerialization.Deserialize(payload);
                if (!_handlers.TryGetValue(message.GetType(), out var handler)) continue;
                if (handler.DynamicInvoke(message, peer) is Task task) await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (_peers.TryRemove(peer, out _)) PeerDisconnected?.Invoke(peer);
            await peer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var peers = _peers.Keys.ToArray();
        _peers.Clear();
        foreach (var peer in peers)
        {
            PeerDisconnected?.Invoke(peer);
            await peer.DisposeAsync().ConfigureAwait(false);
        }
        _handlers.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class WebSocketPeer :
        ICultNetSchemaServerPeer,
        ICultNetSchemaServerPeerLocation,
        IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly int _maxMessageBytes;
        private bool _disposed;

        public WebSocketPeer(WebSocket socket, EndPoint? remoteEndPoint, int maxMessageBytes)
        {
            _socket = socket;
            RemoteEndPoint = remoteEndPoint;
            _maxMessageBytes = maxMessageBytes;
        }

        public EndPoint? RemoteEndPoint { get; }

        public void SendCultNet<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage
        {
            ArgumentNullException.ThrowIfNull(message);
            if (_disposed || _socket.State != WebSocketState.Open)
                throw new InvalidOperationException("CultNet WebSocket peer is not open.");
            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            if (payload.Length > _maxMessageBytes)
                throw new InvalidOperationException(
                    $"CultNet message exceeds the {_maxMessageBytes}-byte WebSocket limit.");
            _sendGate.Wait();
            try
            {
                CultNetWebSocketMessageIO.SendAsync(_socket, payload, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "CultNet peer closed", closeTimeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
                _socket.Abort();
            }
            _socket.Dispose();
            _sendGate.Dispose();
        }
    }
}

internal static class CultNetWebSocketMessageIO
{
    public static Task SendAsync(WebSocket socket, byte[] payload, CancellationToken cancellationToken) =>
        socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);

    public static async Task<byte[]?> ReceiveAsync(
        WebSocket socket,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var segment = new byte[Math.Min(16 * 1024, maxMessageBytes)];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.InvalidMessageType,
                    "CultNet requires binary MessagePack WebSocket messages.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (message.Length + result.Count > maxMessageBytes)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "CultNet message exceeds the configured maximum.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            message.Write(segment, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }
}

/// <summary>ASP.NET Core endpoint mapping for the CultNet WebSocket schema server.</summary>
public static class CultNetWebSocketEndpointRouteBuilderExtensions
{
    /// <summary>Maps one authenticated binary CultNet WebSocket endpoint.</summary>
    public static IEndpointConventionBuilder MapCultNetWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        CultNetWebSocketSchemaServer server,
        CultNetWebSocketEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxMessageBytes));
        if (options.AuthorizeAsync == null && !options.AllowAnonymousDevelopment)
            throw new InvalidOperationException(
                "CultNet WebSocket endpoints require AuthorizeAsync unless AllowAnonymousDevelopment is explicitly enabled.");

        return endpoints.Map(pattern, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }
            if (options.AuthorizeAsync != null && !await options.AuthorizeAsync(context).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await server.AcceptAsync(
                socket,
                context.Connection.RemoteIpAddress == null
                    ? null
                    : new IPEndPoint(context.Connection.RemoteIpAddress, context.Connection.RemotePort),
                options.MaxMessageBytes,
                context.RequestAborted).ConfigureAwait(false);
        });
    }
}
