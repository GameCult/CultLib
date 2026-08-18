using System.Collections.Concurrent;
using System.Net.WebSockets;
using GameCult.Networking;

namespace GameCult.Networking.WebSockets;

/// <summary>Schema-v0 client over a binary WebSocket route.</summary>
public sealed class CultNetWebSocketSchemaClient : ICultNetUriSchemaClient, ICultNetSchemaClientHealth
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _handlerGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<Exception> _backgroundFailure = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<ClientWebSocketOptions>? _configure;
    private readonly int _maxMessageBytes;
    private ClientWebSocket? _socket;
    private bool _disposed;

    /// <summary>Creates a binary WebSocket schema client.</summary>
    public CultNetWebSocketSchemaClient(
        Action<ClientWebSocketOptions>? configure = null,
        int maxMessageBytes = 4 * 1024 * 1024)
    {
        if (maxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _configure = configure;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <inheritdoc />
    public bool Connected => !_disposed && _socket?.State == WebSocketState.Open;

    /// <inheritdoc />
    public Task<Exception> BackgroundFailure => _backgroundFailure.Task;

    /// <inheritdoc />
    public void Connect(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ConnectAsync(new Uri($"ws://{host}:{port}/cultmesh"), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>Connects to an exact WebSocket route.</summary>
    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("CultNet WebSocket endpoint must use ws:// or wss://.", nameof(endpoint));
        ThrowIfDisposed();
        if (_socket != null) throw new InvalidOperationException("CultNet WebSocket client is already connected.");
        var socket = new ClientWebSocket();
        _configure?.Invoke(socket.Options);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _socket = socket;
            _ = ReceiveLoopAsync(socket, _shutdown.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
            throw new InvalidOperationException("CultNet WebSocket client is not connected.");
        var payload = CultNetSchemaMessageSerialization.Serialize(message);
        if (payload.Length > _maxMessageBytes)
            throw new InvalidOperationException(
                $"CultNet message exceeds the {_maxMessageBytes}-byte WebSocket limit.");
        _sendGate.Wait();
        try
        {
            CultNetWebSocketMessageIO.SendAsync(socket, payload, _shutdown.Token).GetAwaiter().GetResult();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
    {
        ArgumentNullException.ThrowIfNull(callback);
        ThrowIfDisposed();
        lock (_handlerGate)
        {
            if (!_handlers.TryGetValue(typeof(T), out var handlers))
                _handlers[typeof(T)] = handlers = new List<Delegate>();
            handlers.Add(callback);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        var socket = _socket;
        _socket = null;
        try
        {
            if (socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "CultNet client disposed", closeTimeout.Token)
                    .GetAwaiter().GetResult();
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            socket?.Abort();
        }
        socket?.Dispose();
        _sendGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!_disposed && socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payload = await CultNetWebSocketMessageIO.ReceiveAsync(socket, _maxMessageBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (payload == null) return;
                Dispatch(CultNetSchemaMessageSerialization.Deserialize(payload));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (!_disposed)
        {
            _backgroundFailure.TrySetResult(error);
        }
    }

    private void Dispatch(ICultNetSchemaMessage message)
    {
        Delegate[] handlers;
        lock (_handlerGate)
            handlers = _handlers.TryGetValue(message.GetType(), out var registered)
                ? registered.ToArray()
                : Array.Empty<Delegate>();
        foreach (var handler in handlers) handler.DynamicInvoke(message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
