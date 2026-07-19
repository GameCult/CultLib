using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>Schema-v0 client over the length-prefixed TCP lane.</summary>
    public sealed class TcpFramedCultNetSchemaClient : ICultNetSchemaClient, ICultNetSchemaClientHealth
    {
        private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
        private readonly object _handlerGate = new();
        private readonly object _sendGate = new();
        private readonly TaskCompletionSource<Exception> _backgroundFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _shutdown = new();
        private TcpClient? _client;
        private TcpFramedTransportConnection? _transport;
        private bool _disposed;

        public bool Connected => !_disposed && _client?.Connected == true;
        public Task<Exception> BackgroundFailure => _backgroundFailure.Task;

        public void Connect(string host, int port)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpFramedCultNetSchemaClient));
            if (_client != null) throw new InvalidOperationException("TCP schema client is already connected.");
            var client = new TcpClient();
            try
            {
                client.Connect(host, port);
                _client = client;
                _transport = new TcpFramedTransportConnection(
                    client.GetStream(),
                    CultNetTransportProfiles.CreateTcpFramed("csharp-tcp-schema-client"));
                _ = ReceiveLoopAsync(_shutdown.Token);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpFramedCultNetSchemaClient));
            var transport = _transport ?? throw new InvalidOperationException("TCP schema client is not connected.");
            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            lock (_sendGate)
                transport.SendAsync("schema", payload, _shutdown.Token).GetAwaiter().GetResult();
        }

        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_handlerGate)
            {
                if (!_handlers.TryGetValue(typeof(T), out var handlers))
                    _handlers[typeof(T)] = handlers = new List<Delegate>();
                handlers.Add(callback);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _client?.Dispose();
            _shutdown.Dispose();
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var transport = _transport!;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    var message = CultNetSchemaMessageSerialization.Deserialize(frame.Payload);
                    Dispatch(message);
                }
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
    }

    /// <summary>Schema-v0 server over the length-prefixed TCP lane.</summary>
    public sealed class TcpFramedCultNetSchemaServer : ICultNetSchemaServer, ICultNetSchemaServerPeerLifecycle, IDisposable
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();
        private readonly ConcurrentDictionary<TcpPeer, byte> _peers = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly TaskCompletionSource<Exception> _backgroundFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public TcpFramedCultNetSchemaServer(TcpListener listener)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _listener.Start();
            _ = AcceptLoopAsync(_shutdown.Token);
        }

        public event Action<ICultNetSchemaServerPeer>? PeerDisconnected;
        /// <summary>Raised when one peer connection fails without stopping the listener.</summary>
        public event Action<EndPoint?, Exception>? PeerFailed;
        /// <summary>Gets the bound TCP endpoint.</summary>
        public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;
        /// <summary>Gets the number of active TCP peers.</summary>
        public int PeerCount => _peers.Count;
        /// <summary>Completes if the background accept loop fails.</summary>
        public Task<Exception> BackgroundFailure => _backgroundFailure.Task;

        public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _handlers[typeof(TMessage)] = callback;
        }

        public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (_handlers.TryGetValue(typeof(TMessage), out var existing) && existing == callback)
                _handlers.TryRemove(typeof(TMessage), out _);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _listener.Stop();
            foreach (var peer in _peers.Keys) peer.Dispose();
            _peers.Clear();
            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var peer = new TcpPeer(client);
                    _peers.TryAdd(peer, 0);
                    _ = ServePeerAsync(peer, cancellationToken);
                }
            }
            catch (Exception) when (_disposed || cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _backgroundFailure.TrySetResult(error);
            }
        }

        private async Task ServePeerAsync(TcpPeer peer, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await peer.Transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    var message = CultNetSchemaMessageSerialization.Deserialize(frame.Payload);
                    if (_handlers.TryGetValue(message.GetType(), out var handler))
                    {
                        var result = handler.DynamicInvoke(message, peer) as Task;
                        if (result != null) await result.ConfigureAwait(false);
                    }
                }
            }
            catch (Exception) when (_disposed || cancellationToken.IsCancellationRequested || !peer.Connected)
            {
            }
            catch (Exception error)
            {
                PeerFailed?.Invoke(peer.RemoteEndPoint, error);
            }
            finally
            {
                if (_peers.TryRemove(peer, out _)) PeerDisconnected?.Invoke(peer);
                peer.Dispose();
            }
        }

        private sealed class TcpPeer : ICultNetSchemaServerPeer, ICultNetSchemaServerPeerLocation, IDisposable
        {
            private readonly TcpClient _client;
            private readonly object _sendGate = new();
            private readonly EndPoint? _remoteEndPoint;

            public TcpPeer(TcpClient client)
            {
                _client = client;
                _remoteEndPoint = client.Client.RemoteEndPoint;
                Transport = new TcpFramedTransportConnection(
                    client.GetStream(),
                    CultNetTransportProfiles.CreateTcpFramed("csharp-tcp-schema-server"));
            }

            public bool Connected => _client.Connected;
            public EndPoint? RemoteEndPoint => _remoteEndPoint;
            public TcpFramedTransportConnection Transport { get; }

            public void SendCultNet<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage
            {
                var payload = CultNetSchemaMessageSerialization.Serialize(message);
                lock (_sendGate) Transport.SendAsync("schema", payload).GetAwaiter().GetResult();
            }

            public void Dispose() => _client.Dispose();
        }
    }
}
