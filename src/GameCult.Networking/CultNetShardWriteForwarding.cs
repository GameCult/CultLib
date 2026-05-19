using System;
using System.Linq;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// Forwards shard writes to an authoritative owner.
    /// </summary>
    public interface ICultNetShardWriteForwarder
    {
        /// <summary>
        /// Forwards a raw document put to the shard owner.
        /// </summary>
        Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message);

        /// <summary>
        /// Forwards a raw document delete to the shard owner.
        /// </summary>
        Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message);
    }

    /// <summary>
    /// Options controlling shard write forwarding from a database server bridge.
    /// </summary>
    public sealed class CultNetDatabaseServerOptions
    {
        /// <summary>
        /// Gets or sets whether non-primary writes may be forwarded to shard owners.
        /// </summary>
        public bool ForwardNonPrimaryWrites { get; set; }

        /// <summary>
        /// Gets or sets the forwarder used when non-primary write forwarding is enabled.
        /// </summary>
        public ICultNetShardWriteForwarder? WriteForwarder { get; set; }
    }

    /// <summary>
    /// Options for the schema-v0 client-based shard write forwarder.
    /// </summary>
    public sealed class CultNetSchemaWriteForwarderOptions
    {
        /// <summary>
        /// Gets or sets client security options used to connect to primary endpoints.
        /// </summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets how long to wait for a connection before failing the forwarded write.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets a callback used to customize each ephemeral forwarding client.
        /// </summary>
        public Action<Client>? ConfigureClient { get; set; }
    }

    /// <summary>
    /// Forwards shard writes over the CultNet schema-v0 client transport.
    /// </summary>
    public sealed class CultNetSchemaWriteForwarder : ICultNetShardWriteForwarder
    {
        private readonly CultNetSchemaWriteForwarderOptions _options;

        /// <summary>
        /// Creates a schema-v0 write forwarder.
        /// </summary>
        public CultNetSchemaWriteForwarder(CultNetSchemaWriteForwarderOptions? options = null)
        {
            _options = options ?? new CultNetSchemaWriteForwarderOptions();
        }

        /// <inheritdoc />
        public async Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var endpoint = ResolvePrimaryEndpoint(shard);
            message.ShardId ??= shard.ShardId;
            message.ShardEpoch ??= shard.Epoch;
            await SendAsync(endpoint, message).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var endpoint = ResolvePrimaryEndpoint(shard);
            message.ShardId ??= shard.ShardId;
            message.ShardEpoch ??= shard.Epoch;
            await SendAsync(endpoint, message).ConfigureAwait(false);
        }

        /// <summary>
        /// Parses a CultNet endpoint URI into host and port.
        /// </summary>
        public static (string Host, int Port) ParseEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint must be non-empty.", nameof(endpoint));
            }

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "cultnet", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new FormatException($"CultNet endpoint '{endpoint}' must use cultnet://host:port.");
            }

            return (uri.Host, uri.IsDefaultPort ? 3075 : uri.Port);
        }

        private async Task SendAsync<T>(string endpoint, T message) where T : ICultNetSchemaMessage
        {
            var (host, port) = ParseEndpoint(endpoint);
            using var client = new Client(_options.Security ?? ClientSecurityOptions.Development())
            {
                AllowUnverifiedCultNetMessages = true
            };
            _options.ConfigureClient?.Invoke(client);
            client.Connect(host, port);
            await WaitForConnectionAsync(client, endpoint).ConfigureAwait(false);
            client.SendCultNet(message);
        }

        private async Task WaitForConnectionAsync(Client client, string endpoint)
        {
            var deadline = DateTimeOffset.UtcNow + _options.ConnectTimeout;
            while (!client.Connected)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out connecting to shard primary endpoint {endpoint}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        private static string ResolvePrimaryEndpoint(CultNetShardDescriptor shard)
        {
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            return shard.PrimaryEndpoints.FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       $"Shard '{shard.ShardId}' does not advertise a primary endpoint.");
        }
    }
}
