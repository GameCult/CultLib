using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GameCult.Networking
{
    /// <summary>
    /// Minimal client port for request/response services that speak CultNet schema-v0 messages.
    /// </summary>
    public interface ICultNetSchemaClient : IDisposable
    {
        /// <summary>
        /// Gets whether the underlying transport reports an established connection.
        /// </summary>
        bool Connected { get; }

        /// <summary>
        /// Connects the transport to a remote endpoint.
        /// </summary>
        void Connect(string host, int port);

        /// <summary>
        /// Sends one schema-v0 message through the transport.
        /// </summary>
        void SendCultNet<T>(T message)
            where T : ICultNetSchemaMessage;

        /// <summary>
        /// Adds a callback for one schema-v0 response type.
        /// </summary>
        void OnCultNet<T>(Action<T> callback)
            where T : ICultNetSchemaMessage;
    }

    /// <summary>
    /// LiteNetLib-backed implementation of the CultNet schema client port.
    /// </summary>
    public sealed class LiteNetLibCultNetSchemaClient : ICultNetSchemaClient
    {
        private readonly Client _client;

        /// <summary>
        /// Creates a LiteNetLib schema client adapter.
        /// </summary>
        public LiteNetLibCultNetSchemaClient(
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            _client = new Client(security ?? ClientSecurityOptions.Development())
            {
                AllowUnverifiedCultNetMessages = true
            };
            configureClient?.Invoke(_client);
        }

        /// <inheritdoc />
        public bool Connected => _client.Connected;

        /// <inheritdoc />
        public void Connect(string host, int port)
        {
            _client.Connect(host, port);
        }

        /// <inheritdoc />
        public void SendCultNet<T>(T message)
            where T : ICultNetSchemaMessage
        {
            _client.SendCultNet(message);
        }

        /// <inheritdoc />
        public void OnCultNet<T>(Action<T> callback)
            where T : ICultNetSchemaMessage
        {
            _client.OnCultNet(callback);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _client.Dispose();
        }
    }

    /// <summary>
    /// RUDP-backed implementation of the CultNet schema client port.
    /// </summary>
    public sealed class RudpCultNetSchemaClient : ICultNetSchemaClient
    {
        private readonly string _runtimeId;
        private readonly uint _connectionId;
        private readonly string _connectPayload;
        private readonly int _maxFragmentBytes;
        private readonly long _resendDelayMs;
        private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
        private readonly object _handlerLock = new();
        private CultNetRudpSocketTransportConnection? _transport;
        private Thread? _pumpThread;
        private volatile bool _disposed;

        /// <summary>
        /// Creates a RUDP schema client adapter.
        /// </summary>
        public RudpCultNetSchemaClient(
            string runtimeId = "csharp-rudp-schema-client",
            uint connectionId = 0x43554c54,
            string connectPayload = "cultnet-schema-rudp",
            int maxFragmentBytes = 1024,
            long resendDelayMs = 25)
        {
            if (string.IsNullOrWhiteSpace(runtimeId)) throw new ArgumentException("Runtime id is required.", nameof(runtimeId));
            _runtimeId = runtimeId;
            _connectionId = connectionId;
            _connectPayload = connectPayload;
            _maxFragmentBytes = maxFragmentBytes;
            _resendDelayMs = resendDelayMs;
        }

        /// <inheritdoc />
        public bool Connected => _transport?.Connected == true;

        /// <inheritdoc />
        public void Connect(string host, int port)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RudpCultNetSchemaClient));
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            var remoteAddress = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : Dns.GetHostAddresses(host)[0];
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.ReceiveTimeout = 20;
            _transport = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = _runtimeId,
                Mode = CultNetRudpSocketMode.Client,
                Socket = socket,
                RemoteEndPoint = new IPEndPoint(remoteAddress, port),
                ConnectionId = _connectionId,
                TransportId = "schema-rudp",
                MaxFragmentBytes = _maxFragmentBytes,
                ResendDelayMs = _resendDelayMs
            });
            _transport.Connect(Encoding.UTF8.GetBytes(_connectPayload));
            _pumpThread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "CultNet RUDP schema client pump"
            };
            _pumpThread.Start();
        }

        /// <inheritdoc />
        public void SendCultNet<T>(T message)
            where T : ICultNetSchemaMessage
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var transport = _transport ?? throw new InvalidOperationException("RUDP schema client is not connected.");
            transport.SendSchemaMessage(message);
        }

        /// <inheritdoc />
        public void OnCultNet<T>(Action<T> callback)
            where T : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_handlerLock)
            {
                var handlers = _handlers.GetOrAdd(typeof(T), _ => new List<Delegate>());
                handlers.Add(callback);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _disposed = true;
            _transport?.Dispose();
        }

        private void Pump()
        {
            while (!_disposed)
            {
                var transport = _transport;
                if (transport == null)
                {
                    return;
                }

                ICultNetSchemaMessage? message;
                try
                {
                    message = transport.ReceiveSchemaMessageOnce();
                    transport.PollResends();
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    return;
                }
                catch (SocketException) when (_disposed)
                {
                    return;
                }

                if (message != null)
                {
                    Dispatch(message);
                }

                Thread.Sleep(5);
            }
        }

        private void Dispatch(ICultNetSchemaMessage message)
        {
            List<Delegate>? handlers;
            lock (_handlerLock)
            {
                if (!_handlers.TryGetValue(message.GetType(), out var registered))
                {
                    return;
                }
                handlers = registered.ToList();
            }

            foreach (var handler in handlers)
            {
                handler.DynamicInvoke(message);
            }
        }
    }

    /// <summary>
    /// Factory helpers for schema client adapters.
    /// </summary>
    public static class CultNetSchemaClients
    {
        /// <summary>
        /// Creates the default C# LiteNetLib schema client adapter.
        /// </summary>
        public static ICultNetSchemaClient CreateLiteNetLib(
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            return new LiteNetLibCultNetSchemaClient(security, configureClient);
        }

        /// <summary>
        /// Creates the C# RUDP schema client adapter.
        /// </summary>
        public static ICultNetSchemaClient CreateRudp(
            string runtimeId = "csharp-rudp-schema-client",
            uint connectionId = 0x43554c54,
            string connectPayload = "cultnet-schema-rudp",
            int maxFragmentBytes = 1024,
            long resendDelayMs = 25)
        {
            return new RudpCultNetSchemaClient(runtimeId, connectionId, connectPayload, maxFragmentBytes, resendDelayMs);
        }

        /// <summary>
        /// Creates the default schema client adapter for an advertised endpoint URI.
        /// </summary>
        public static ICultNetSchemaClient CreateForEndpoint(
            string endpoint,
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint must be non-empty.", nameof(endpoint));
            }

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                throw new FormatException($"CultNet endpoint '{endpoint}' must be an absolute URI.");
            }

            if (string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase))
            {
                return CreateRudp();
            }

            if (string.Equals(uri.Scheme, "cultnet", StringComparison.OrdinalIgnoreCase))
            {
                return CreateLiteNetLib(security, configureClient);
            }

            throw new FormatException($"CultNet endpoint '{endpoint}' must use cultnet://host:port or rudp://host:port.");
        }
    }
}
