using System;

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
    }
}
