using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>
    /// Options for one scoped CultNet snapshot request.
    /// </summary>
    public sealed class CultMeshSnapshotRequestOptions
    {
        /// <summary>Gets or sets schema ids to request. Empty means no schema filter.</summary>
        public IReadOnlyList<string>? SchemaIds { get; set; }

        /// <summary>Gets or sets record keys to request. Empty means no record-key filter.</summary>
        public IReadOnlyList<string>? RecordKeys { get; set; }

        /// <summary>Gets or sets the target shard id, when the endpoint is shard-aware.</summary>
        public string? ShardId { get; set; }

        /// <summary>Gets or sets the target shard epoch, when the endpoint is shard-aware.</summary>
        public long? ShardEpoch { get; set; }

        /// <summary>Gets or sets the response timeout.</summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets the connection timeout.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets the message id prefix used for snapshot requests.</summary>
        public string? MessageIdPrefix { get; set; }

        /// <summary>Gets or sets client security options for endpoint-created LiteNetLib clients.</summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>Gets or sets a callback used to configure endpoint-created LiteNetLib clients.</summary>
        public Action<Client>? ConfigureClient { get; set; }

        /// <summary>Gets or sets a custom schema client factory.</summary>
        public Func<ICultNetSchemaClient>? CreateClient { get; set; }

        /// <summary>Gets or sets the runtime id used by endpoint-created RUDP clients.</summary>
        public string? RudpRuntimeId { get; set; }

        /// <summary>Gets or sets the connection id used by endpoint-created RUDP clients.</summary>
        public uint RudpConnectionId { get; set; } = 0x43554c54;

        /// <summary>Gets or sets the connect payload used by endpoint-created RUDP clients.</summary>
        public string RudpConnectPayload { get; set; } = "cultnet-schema-rudp";

        /// <summary>Gets or sets the maximum fragment size used by endpoint-created RUDP clients.</summary>
        public int RudpMaxFragmentBytes { get; set; } = 1024;

        /// <summary>Gets or sets the resend delay used by endpoint-created RUDP clients.</summary>
        public long RudpResendDelayMs { get; set; } = 25;
    }

    public static partial class CultMesh
    {
        /// <summary>
        /// Fetches one scoped raw CultNet snapshot from an endpoint.
        /// </summary>
        public static Task<CultNetSnapshotResponseRawMessage> FetchSnapshotAsync(
            string endpoint,
            CultMeshSnapshotRequestOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            var resolvedOptions = options ?? new CultMeshSnapshotRequestOptions();
            return FetchSnapshotAsync(CreateSnapshotClient(endpoint, resolvedOptions), endpoint, resolvedOptions);
        }

        /// <summary>
        /// Fetches one scoped raw CultNet snapshot through a caller-provided schema client factory.
        /// </summary>
        public static async Task<CultNetSnapshotResponseRawMessage> FetchSnapshotAsync(
            Func<ICultNetSchemaClient> createClient,
            string endpoint,
            CultMeshSnapshotRequestOptions? options = null)
        {
            if (createClient == null) throw new ArgumentNullException(nameof(createClient));
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));

            var resolvedOptions = options ?? new CultMeshSnapshotRequestOptions();
            var messageId = CreateSnapshotMessageId(resolvedOptions);
            var completion = new TaskCompletionSource<CultNetSnapshotResponseRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = createClient();
            client.OnCultNet<CultNetSnapshotResponseRawMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                    completion.TrySetResult(response);
            });
            client.OnCultNet<CultNetErrorMessage>(error =>
                completion.TrySetException(new InvalidOperationException(error.Error)));

            var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(endpoint);
            client.Connect(host, port);
            await WaitForSnapshotClientConnectionAsync(client, endpoint, resolvedOptions.ConnectTimeout)
                .ConfigureAwait(false);
            client.SendCultNet(new CultNetSnapshotRequestMessage
            {
                MessageId = messageId,
                SchemaIds = CleanSnapshotFilter(resolvedOptions.SchemaIds),
                RecordKeys = CleanSnapshotFilter(resolvedOptions.RecordKeys),
                ShardId = string.IsNullOrWhiteSpace(resolvedOptions.ShardId) ? null : resolvedOptions.ShardId,
                ShardEpoch = resolvedOptions.ShardEpoch
            });

            return await WaitForSnapshotResponseAsync(
                    completion.Task,
                    endpoint,
                    resolvedOptions,
                    messageId)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches a scoped raw CultNet snapshot and applies it into the node's cache.
        /// </summary>
        public static async Task<IReadOnlyList<object>> ApplySnapshotAsync(
            CultMeshNode node,
            string endpoint,
            CultMeshSnapshotRequestOptions? options = null)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var snapshot = await FetchSnapshotAsync(endpoint, options).ConfigureAwait(false);
            return await node.Database.Documents.ApplyRawSnapshotResponseAsync(node.Cache, snapshot).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches a scoped raw CultNet snapshot and decodes documents assignable to the requested type.
        /// </summary>
        public static async Task<IReadOnlyList<TDocument>> FetchSnapshotDocumentsAsync<TDocument>(
            string endpoint,
            CultMeshSnapshotRequestOptions? options = null,
            CultNetDocumentRegistry? registry = null)
            where TDocument : class
        {
            var snapshot = await FetchSnapshotAsync(endpoint, options).ConfigureAwait(false);
            return DecodeSnapshotDocuments<TDocument>(snapshot, registry ?? new CultNetDocumentRegistry());
        }

        private static Func<ICultNetSchemaClient> CreateSnapshotClient(
            string endpoint,
            CultMeshSnapshotRequestOptions options)
        {
            if (options.CreateClient != null)
                return options.CreateClient;

            return () =>
            {
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                    string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase))
                {
                    return CultNetSchemaClients.CreateRudp(
                        string.IsNullOrWhiteSpace(options.RudpRuntimeId)
                            ? "cultmesh-snapshot-client"
                            : options.RudpRuntimeId!,
                        options.RudpConnectionId,
                        options.RudpConnectPayload,
                        options.RudpMaxFragmentBytes,
                        options.RudpResendDelayMs);
                }

                return CultNetSchemaClients.CreateForEndpoint(endpoint, options.Security, options.ConfigureClient);
            };
        }

        private static string CreateSnapshotMessageId(CultMeshSnapshotRequestOptions options)
        {
            var prefix = string.IsNullOrWhiteSpace(options.MessageIdPrefix)
                ? "cultmesh:snapshot"
                : options.MessageIdPrefix!;
            return $"{prefix}:{Guid.NewGuid():N}";
        }

        private static async Task WaitForSnapshotClientConnectionAsync(
            ICultNetSchemaClient client,
            string endpoint,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!client.Connected)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException($"Timed out connecting to CultNet snapshot endpoint {endpoint}.");

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        private static async Task<CultNetSnapshotResponseRawMessage> WaitForSnapshotResponseAsync(
            Task<CultNetSnapshotResponseRawMessage> responseTask,
            string endpoint,
            CultMeshSnapshotRequestOptions options,
            string messageId)
        {
            var timeoutTask = Task.Delay(options.ResponseTimeout);
            var completed = await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false);
            if (completed != responseTask)
            {
                throw new TimeoutException(
                    $"Timed out waiting for CultNet snapshot response '{messageId}' from {endpoint} " +
                    $"for schemas [{string.Join(", ", CleanSnapshotFilter(options.SchemaIds) ?? Array.Empty<string>())}] " +
                    $"and records [{string.Join(", ", CleanSnapshotFilter(options.RecordKeys) ?? Array.Empty<string>())}].");
            }

            return await responseTask.ConfigureAwait(false);
        }

        private static string[]? CleanSnapshotFilter(IReadOnlyList<string>? values)
        {
            if (values is not { Count: > 0 })
                return null;

            var filtered = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return filtered.Length == 0 ? null : filtered;
        }

        private static IReadOnlyList<TDocument> DecodeSnapshotDocuments<TDocument>(
            CultNetSnapshotResponseRawMessage snapshot,
            CultNetDocumentRegistry registry)
            where TDocument : class
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var documents = new List<TDocument>(snapshot.Documents.Length);
            foreach (var record in snapshot.Documents)
            {
                if (record == null)
                    continue;

                var binding = registry.GetBySchemaId(record.SchemaId);
                if (binding == null || !typeof(TDocument).IsAssignableFrom(binding.DocumentType))
                    continue;

                if (!string.Equals(record.PayloadEncoding, "messagepack", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"CultNet raw document payloadEncoding must be \"messagepack\", not \"{record.PayloadEncoding}\".");
                }

                documents.Add((TDocument)binding.PayloadDeserializer(record.Payload));
            }

            return documents;
        }
    }
}
