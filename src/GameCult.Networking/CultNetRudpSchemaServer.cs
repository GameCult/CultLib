using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// Options for hosting schema-v0 messages over the native CultNet RUDP listener.
    /// </summary>
    public sealed class RudpCultNetSchemaServerOptions
    {
        /// <summary>
        /// Gets or sets the runtime id advertised by the RUDP listener.
        /// </summary>
        public string RuntimeId { get; set; } = "csharp-rudp-schema-server";
        /// <summary>
        /// Gets or sets the UDP socket used by the listener. When omitted, a loopback socket is created.
        /// </summary>
        public Socket? Socket { get; set; }
        /// <summary>
        /// Gets or sets the accepted RUDP connection id.
        /// </summary>
        public uint ConnectionId { get; set; } = 0x43554c54;
        /// <summary>
        /// Gets or sets the first local packet sequence for each accepted peer.
        /// </summary>
        public uint InitialSequence { get; set; } = 100;
        /// <summary>
        /// Gets or sets the resend delay in milliseconds.
        /// </summary>
        public long ResendDelayMs { get; set; } = 25;
        /// <summary>
        /// Gets or sets the advertised transport id.
        /// </summary>
        public string TransportId { get; set; } = "schema-rudp";
        /// <summary>
        /// Gets or sets the maximum fragment size for schema-channel payloads.
        /// </summary>
        public int? MaxFragmentBytes { get; set; } = 1024;
        /// <summary>
        /// Gets or sets the maximum pending reliable packet count.
        /// </summary>
        public int? MaxPendingReliablePackets { get; set; }
        /// <summary>
        /// Gets or sets the payload sent with accept packets.
        /// </summary>
        public byte[]? AcceptPayload { get; set; }
    }

    /// <summary>
    /// Peer context for a schema-v0 RUDP server.
    /// </summary>
    public sealed class RudpCultNetSchemaServerPeer : ICultNetSchemaServerPeer
    {
        private readonly RudpCultNetSchemaServer _server;

        internal RudpCultNetSchemaServerPeer(
            RudpCultNetSchemaServer server,
            CultNetRudpSocketServerPeer transportPeer)
        {
            _server = server;
            TransportPeer = transportPeer;
        }

        /// <summary>
        /// Gets the underlying RUDP transport peer.
        /// </summary>
        public CultNetRudpSocketServerPeer TransportPeer { get; }
        /// <summary>
        /// Gets the UDP endpoint for this peer.
        /// </summary>
        public EndPoint RemoteEndPoint => TransportPeer.RemoteEndPoint;

        /// <summary>
        /// Sends a schema-v0 response to this peer.
        /// </summary>
        public void SendCultNet<TMessage>(TMessage message)
            where TMessage : ICultNetSchemaMessage
        {
            _server.SendCultNet(TransportPeer, message);
        }
    }

    /// <summary>
    /// Multi-peer schema-v0 host over the native CultNet RUDP transport.
    /// </summary>
    public sealed class RudpCultNetSchemaServer : ICultNetSchemaServer, IDisposable
    {
        private readonly Socket _socket;
        private readonly CultNetRudpSocketTransportServer _transport;
        private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();
        private readonly ConcurrentDictionary<Delegate, Delegate> _schemaPeerAdapters = new();
        private bool _disposed;

        /// <summary>
        /// Creates a schema-v0 RUDP server.
        /// </summary>
        public RudpCultNetSchemaServer(RudpCultNetSchemaServerOptions? options = null)
        {
            options ??= new RudpCultNetSchemaServerOptions();
            var socket = options.Socket;
            if (socket == null)
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }

            _socket = socket;
            _transport = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = options.RuntimeId,
                Socket = socket,
                ConnectionId = options.ConnectionId,
                InitialSequence = options.InitialSequence,
                ResendDelayMs = options.ResendDelayMs,
                TransportId = options.TransportId,
                MaxFragmentBytes = options.MaxFragmentBytes,
                MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                AcceptPayload = options.AcceptPayload
            });
        }

        /// <summary>
        /// Gets the underlying RUDP listener profile.
        /// </summary>
        public CultNetTransportProfile Profile => _transport.Profile;

        /// <summary>
        /// Gets the local UDP endpoint.
        /// </summary>
        public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

        /// <summary>
        /// Gets a snapshot of the current transfer counters.
        /// </summary>
        public CultNetTransportStats Stats => _transport.Stats;

        /// <summary>
        /// Registers a schema-v0 handler.
        /// </summary>
        public void OnCultNet<TMessage>(Func<TMessage, RudpCultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _handlers.AddOrUpdate(
                typeof(TMessage),
                _ => callback,
                (type, current) =>
                {
                    var combined = Delegate.Combine(current, callback) as Func<TMessage, RudpCultNetSchemaServerPeer, Task>;
                    return combined ?? throw new InvalidOperationException($"Failed to combine delegates for {type.Name}.");
                });
        }

        /// <summary>
        /// Registers a schema-v0 handler.
        /// </summary>
        public void OnCultNet<TMessage>(Action<TMessage, RudpCultNetSchemaServerPeer> callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            OnCultNet<TMessage>((message, peer) =>
            {
                callback(message, peer);
                return Task.CompletedTask;
            });
        }

        /// <inheritdoc />
        public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            Func<TMessage, RudpCultNetSchemaServerPeer, Task> adapter = (message, peer) => callback(message, peer);
            _schemaPeerAdapters[callback] = adapter;
            OnCultNet(adapter);
        }

        /// <inheritdoc />
        public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (_schemaPeerAdapters.TryRemove(callback, out var adapter))
            {
                callback = adapter;
            }

            if (!_handlers.TryGetValue(typeof(TMessage), out var current))
            {
                return;
            }

            var next = Delegate.Remove(current, callback);
            if (next == null)
            {
                _handlers.TryRemove(typeof(TMessage), out _);
            }
            else
            {
                _handlers[typeof(TMessage)] = next;
            }
        }

        /// <summary>
        /// Sends a schema-v0 message to a transport peer.
        /// </summary>
        public void SendCultNet<TMessage>(CultNetRudpSocketServerPeer peer, TMessage message)
            where TMessage : ICultNetSchemaMessage
        {
            _transport.SendSchemaMessage(peer, message);
        }

        /// <summary>
        /// Polls one packet/frame and dispatches at most one schema-v0 message.
        /// </summary>
        public async Task<bool> PollOnceAsync()
        {
            var delivered = _transport.ReceiveOnce();
            _transport.PollResends();
            if (delivered == null || !string.Equals(delivered.Frame.ChannelId, "schema", StringComparison.Ordinal))
            {
                return false;
            }

            var message = CultNetSchemaMessageSerialization.Deserialize(delivered.Frame.Payload);
            if (!_handlers.TryGetValue(message.GetType(), out var handler) || handler == null)
            {
                return false;
            }

            var result = handler.DynamicInvoke(message, new RudpCultNetSchemaServerPeer(this, delivered.Peer));
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>
        /// Sends any reliable packets whose resend timers are due.
        /// </summary>
        public void PollResends()
        {
            _transport.PollResends();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _transport.Dispose();
        }
    }
}
