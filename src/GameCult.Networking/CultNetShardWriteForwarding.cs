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
        /// Forwards a raw document put to the shard owner. Completes only when the owner answered that it admitted the write;
        /// throws <see cref="CultNetWriteRefusedException"/> when it refused, <see cref="CultNetWriteNotSentException"/> when
        /// nothing could have reached it (the only failure after which the id may be forwarded again), and any other exception
        /// when the frame may have gone out and was not answered (unconfirmed, including <see cref="CultNetWriteUnconfirmedException"/>). The server relays to the original peer exactly what the owner answered.
        /// </summary>
        Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message);

        /// <summary>
        /// Forwards a raw document delete to the shard owner; completes, refuses and goes unconfirmed as
        /// <see cref="ForwardPutAsync"/> does.
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

        /// <summary>
        /// Gets or sets the authority asked for every peer put and delete. None configured refuses them all: a connection
        /// key admits a peer to the transport, never to write. A write forwarded to a primary is judged here for the
        /// original peer and again at the primary for the forwarder's connection.
        /// </summary>
        public ICultNetRemoteWriteAuthority? RemoteWriteAuthority { get; set; }
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

        /// <summary>
        /// Gets or sets the schema-v0 client factory. Defaults to an endpoint-aware adapter:
        /// rudp:// endpoints use the C# RUDP schema client and cultnet:// endpoints use LiteNetLib.
        /// </summary>
        public Func<ICultNetSchemaClient>? CreateClient { get; set; }

        /// <summary>
        /// Gets or sets how long the primary has to answer a forwarded write (from the send). Its receipt completes the forward,
        /// its refusal fails it with <see cref="CultNetWriteRefusedException"/>, and silence fails it with
        /// <see cref="CultNetWriteUnconfirmedException"/>. Defaults to <see cref="CultNetWriteCompletion.ReceiptResultWindow"/>; a
        /// primary that predates write receipts never answers, so forwarding to one is unconfirmed after this window.
        /// </summary>
        public TimeSpan ResultWindow { get; set; } = CultNetWriteCompletion.ReceiptResultWindow;
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

        /// <summary>
        /// Forwards a put under its original message id and completes on the primary's result frame: a receipt returns, a refusal
        /// throws <see cref="CultNetWriteRefusedException"/> and silence throws <see cref="CultNetWriteUnconfirmedException"/>.
        /// </summary>
        public async Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            message.ShardId ??= shard.ShardId;
            message.ShardEpoch ??= shard.Epoch;
            await SendAsync(shard, message, message.MessageId).ConfigureAwait(false);
        }

        /// <summary>
        /// Forwards a delete under its original message id; completes, refuses and goes unconfirmed as
        /// <see cref="ForwardPutAsync"/> does.
        /// </summary>
        public async Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            message.ShardId ??= shard.ShardId;
            message.ShardEpoch ??= shard.Epoch;
            await SendAsync(shard, message, message.MessageId).ConfigureAwait(false);
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
                !IsSupportedSchemaEndpointScheme(uri.Scheme) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new FormatException(
                    $"CultNet endpoint '{endpoint}' must use cultnet+tcp://host:port, cultnet://host:port, or rudp://host:port.");
            }

            return (uri.Host, uri.IsDefaultPort ? 3075 : uri.Port);
        }

        // The write frame has not been handed to the transport until this returns: any failure here is CultNetWriteNotSentException,
        // the one failure the caller may retry under the same id.
        private async Task<ICultNetSchemaClient> ConnectBeforeSendAsync(CultNetShardDescriptor shard, string messageId)
        {
            ICultNetSchemaClient? client = null;
            try
            {
                var endpoint = ResolvePrimaryEndpoint(shard);
                var (host, port) = ParseEndpoint(endpoint);
                client = CreateClient(endpoint);
                client.Connect(host, port);
                await WaitForConnectionAsync(client, endpoint).ConfigureAwait(false);
                // The hello comes first on the ordered schema channel: it opts this connection in to the primary's receipts.
                client.SendCultNet(new CultNetHelloMessage
                {
                    RuntimeId = "gamecult-networking-shard-forwarder",
                    RuntimeKind = "dotnet",
                    SupportedMessageVersions = [CultNetSchemaVersions.WriteAdmitted, CultNetSchemaVersions.ErrorV1]
                });
                return client;
            }
            catch (Exception failure)
            {
                client?.Dispose();
                if (failure is CultNetWriteNotSentException) throw;
                throw new CultNetWriteNotSentException(messageId, failure);
            }
        }

        private async Task SendAsync<T>(CultNetShardDescriptor shard, T message, string messageId) where T : ICultNetSchemaMessage
        {
            using var client = await ConnectBeforeSendAsync(shard, messageId).ConfigureAwait(false);
            // From the write's send on, every failure (timeout, reset, silence, an exception from the send itself) propagates as it
            // is: the frame may have gone out, so the caller treats it as unconfirmed.
            await CultNetWriteCompletion.SendAndAwaitReceiptAsync(
                (admitted, refuse, refuseV1) =>
                {
                    client.OnCultNet(admitted);
                    client.OnCultNet(refuse);
                    client.OnCultNet(refuseV1);
                    return null;
                },
                () => client.SendCultNet(message),
                messageId,
                _options.ResultWindow).ConfigureAwait(false);
        }

        private ICultNetSchemaClient CreateClient(string endpoint)
        {
            return _options.CreateClient?.Invoke()
                   ?? CultNetSchemaClients.CreateForEndpoint(endpoint, _options.Security, _options.ConfigureClient);
        }

        private static bool IsSupportedSchemaEndpointScheme(string scheme)
        {
            return string.Equals(scheme, "cultnet+tcp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, "cultnet", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, "rudp", StringComparison.OrdinalIgnoreCase);
        }

        private async Task WaitForConnectionAsync(ICultNetSchemaClient client, string endpoint)
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
