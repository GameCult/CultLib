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

    /// <summary>
    /// Options for a typed remote snapshot surface bound to one CultNet endpoint.
    /// </summary>
    public sealed class CultMeshSnapshotEndpointOptions
    {
        /// <summary>Gets or sets the Verse context used by handles from this endpoint.</summary>
        public CultMeshVerseContext? Context { get; set; }

        /// <summary>Gets or sets the document registry used for raw snapshot payload decoding.</summary>
        public CultNetDocumentRegistry? DocumentRegistry { get; set; }

        /// <summary>Gets or sets request options applied to each snapshot fetch.</summary>
        public CultMeshSnapshotRequestOptions? Request { get; set; }

        /// <summary>Gets or sets the source id advertised by diagnostics. Defaults to the endpoint.</summary>
        public string? SourceId { get; set; }

        /// <summary>Gets or sets the route hint for resulting handles.</summary>
        public CultMeshRouteHint? RouteHint { get; set; }

        /// <summary>Gets or sets the polling interval for watch fallback.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    }

    /// <summary>
    /// Result from syncing a remote snapshot into a local CultMesh node.
    /// </summary>
    public sealed class CultMeshSnapshotSyncResult
    {
        internal CultMeshSnapshotSyncResult(
            CultNetSnapshotResponseRawMessage snapshot,
            IReadOnlyList<object> appliedDocuments)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            AppliedDocuments = appliedDocuments ?? throw new ArgumentNullException(nameof(appliedDocuments));
        }

        /// <summary>Gets the raw snapshot that was fetched from the endpoint.</summary>
        public CultNetSnapshotResponseRawMessage Snapshot { get; }

        /// <summary>Gets the documents applied into the local node cache.</summary>
        public IReadOnlyList<object> AppliedDocuments { get; }

        /// <summary>Gets the number of documents applied into the local node cache.</summary>
        public int AppliedCount => AppliedDocuments.Count;

        /// <summary>Gets the source shard log sequence reported by the snapshot, when present.</summary>
        public long ShardLogSequence => Snapshot.ShardLogSequence ?? 0L;
    }

    /// <summary>
    /// Typed snapshot surface for one remote CultNet endpoint.
    /// </summary>
    public sealed class CultMeshSnapshotEndpoint
    {
        internal CultMeshSnapshotEndpoint(string endpoint, CultMeshSnapshotEndpointOptions? options)
        {
            Endpoint = string.IsNullOrWhiteSpace(endpoint)
                ? throw new ArgumentException("Value must be non-empty.", nameof(endpoint))
                : endpoint;

            var resolvedOptions = options ?? new CultMeshSnapshotEndpointOptions();
            Context = resolvedOptions.Context ?? CultMesh.Verse("remote", "cultmesh-snapshot-client").Context;
            DocumentRegistry = resolvedOptions.DocumentRegistry ?? new CultNetDocumentRegistry();
            Request = CloneSnapshotRequestOptions(resolvedOptions.Request);
            RouteHint = resolvedOptions.RouteHint ?? new CultMeshRouteHint(CultMeshLocalityKind.Network, Endpoint);
            SourceId = string.IsNullOrWhiteSpace(resolvedOptions.SourceId) ? Endpoint : resolvedOptions.SourceId!;
            PollInterval = resolvedOptions.PollInterval;
        }

        /// <summary>Gets the endpoint this typed surface reads from.</summary>
        public string Endpoint { get; }

        /// <summary>Gets the Verse context used by document handles from this endpoint.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the document registry used for raw snapshot payload decoding.</summary>
        public CultNetDocumentRegistry DocumentRegistry { get; }

        /// <summary>Gets request options applied to each snapshot fetch.</summary>
        public CultMeshSnapshotRequestOptions Request { get; }

        /// <summary>Gets the source id advertised by diagnostics.</summary>
        public string SourceId { get; }

        /// <summary>Gets the route hint for resulting handles.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets the polling interval for watch fallback.</summary>
        public TimeSpan PollInterval { get; }

        /// <summary>Fetches one raw snapshot with this endpoint's configured request policy.</summary>
        public Task<CultNetSnapshotResponseRawMessage> FetchSnapshotAsync(
            IReadOnlyList<string>? schemaIds = null,
            IReadOnlyList<string>? recordKeys = null)
        {
            return CultMesh.FetchSnapshotAsync(Endpoint, CreateRequest(schemaIds, recordKeys));
        }

        /// <summary>Fetches one snapshot and applies it into the node's cache.</summary>
        public async Task<CultMeshSnapshotSyncResult> SyncSnapshotAsync(
            CultMeshNode node,
            IReadOnlyList<string>? schemaIds = null,
            IReadOnlyList<string>? recordKeys = null,
            bool flush = false)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));

            var snapshot = await FetchSnapshotAsync(schemaIds, recordKeys).ConfigureAwait(false);
            var applied = await node.Database.Documents.ApplyRawSnapshotResponseAsync(node.Cache, snapshot)
                .ConfigureAwait(false);
            if (flush)
                await node.FlushAsync().ConfigureAwait(false);

            return new CultMeshSnapshotSyncResult(snapshot, applied);
        }

        /// <summary>Fetches and decodes documents assignable to the requested type or matching its schema.</summary>
        public Task<IReadOnlyList<TDocument>> FetchDocumentsAsync<TDocument>(
            IReadOnlyList<string>? recordKeys = null,
            IReadOnlyList<string>? schemaIds = null)
            where TDocument : class
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            return CultMesh.FetchSnapshotDocumentsAsync<TDocument>(
                Endpoint,
                CreateRequest(schemaIds ?? new[] { descriptor.SchemaId }, recordKeys),
                DocumentRegistry);
        }

        /// <summary>Fetches typed documents and syncs their raw snapshot into the node's cache.</summary>
        public async Task<IReadOnlyList<TDocument>> SyncDocumentsAsync<TDocument>(
            CultMeshNode node,
            IReadOnlyList<string>? recordKeys = null,
            IReadOnlyList<string>? schemaIds = null,
            bool flush = false)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var result = await SyncSnapshotAsync(
                    node,
                    schemaIds ?? new[] { descriptor.SchemaId },
                    recordKeys,
                    flush)
                .ConfigureAwait(false);
            return CultMesh.DecodeSnapshotDocuments<TDocument>(result.Snapshot, DocumentRegistry);
        }

        /// <summary>Fetches one typed document by record key.</summary>
        public async Task<TDocument> FetchDocumentAsync<TDocument>(string recordKey)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            var documents = await FetchDocumentsAsync<TDocument>(new[] { recordKey }).ConfigureAwait(false);
            return documents.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"CultNet snapshot endpoint '{Endpoint}' did not return {typeof(TDocument).FullName} record '{recordKey}'.");
        }

        /// <summary>Fetches one typed document by record key and syncs it into the node's cache.</summary>
        public async Task<TDocument> SyncDocumentAsync<TDocument>(
            CultMeshNode node,
            string recordKey,
            bool flush = false)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            var documents = await SyncDocumentsAsync<TDocument>(node, new[] { recordKey }, flush: flush)
                .ConfigureAwait(false);
            return documents.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"CultNet snapshot endpoint '{Endpoint}' did not return {typeof(TDocument).FullName} record '{recordKey}'.");
        }

        /// <summary>Creates a typed document handle over one remote endpoint record.</summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string recordKey,
            string? documentId = null)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var sources = new[]
            {
                CultMesh.ProjectionSource($"{SourceId}:{recordKey}", descriptor.SchemaId, "CultNet snapshot endpoint")
            };
            var watch = CultMesh.PollingQueryWatcher<CultMeshDocumentQueryParameters, TDocument>(
                async (_parameters, _context) => await FetchDocumentAsync<TDocument>(recordKey).ConfigureAwait(false),
                new CultMeshPollingWatchOptions<TDocument>(PollInterval));

            return CultMesh.Document<TDocument>(
                string.IsNullOrWhiteSpace(documentId) ? recordKey : documentId!,
                Context,
                _ => FetchDocumentAsync<TDocument>(recordKey),
                queryContext => watch(CultMeshDocumentQueryParameters.Empty, queryContext),
                sources,
                RouteHint);
        }

        /// <summary>Creates a schema-aware catalog over endpoint-backed document handles.</summary>
        public CultMeshDocumentCatalog Documents(params ICultMeshDocumentHandle[] documents)
        {
            return CultMesh.Documents(documents);
        }

        private CultMeshSnapshotRequestOptions CreateRequest(
            IReadOnlyList<string>? schemaIds,
            IReadOnlyList<string>? recordKeys)
        {
            var request = CloneSnapshotRequestOptions(Request);
            request.SchemaIds = schemaIds ?? request.SchemaIds;
            request.RecordKeys = recordKeys ?? request.RecordKeys;
            if (string.IsNullOrWhiteSpace(request.RudpRuntimeId))
                request.RudpRuntimeId = Context.RuntimeId;
            if (string.IsNullOrWhiteSpace(request.MessageIdPrefix))
                request.MessageIdPrefix = $"cultmesh:{Context.RuntimeId}:snapshot";
            return request;
        }

        private static CultMeshSnapshotRequestOptions CloneSnapshotRequestOptions(CultMeshSnapshotRequestOptions? source)
        {
            if (source == null)
                return new CultMeshSnapshotRequestOptions();

            return new CultMeshSnapshotRequestOptions
            {
                SchemaIds = source.SchemaIds,
                RecordKeys = source.RecordKeys,
                ShardId = source.ShardId,
                ShardEpoch = source.ShardEpoch,
                ResponseTimeout = source.ResponseTimeout,
                ConnectTimeout = source.ConnectTimeout,
                MessageIdPrefix = source.MessageIdPrefix,
                Security = source.Security,
                ConfigureClient = source.ConfigureClient,
                CreateClient = source.CreateClient,
                RudpRuntimeId = source.RudpRuntimeId,
                RudpConnectionId = source.RudpConnectionId,
                RudpConnectPayload = source.RudpConnectPayload,
                RudpMaxFragmentBytes = source.RudpMaxFragmentBytes,
                RudpResendDelayMs = source.RudpResendDelayMs
            };
        }
    }

    public static partial class CultMesh
    {
        /// <summary>
        /// Binds one CultNet endpoint as a typed snapshot surface.
        /// </summary>
        public static CultMeshSnapshotEndpoint SnapshotEndpoint(
            string endpoint,
            CultMeshSnapshotEndpointOptions? options = null)
        {
            return new CultMeshSnapshotEndpoint(endpoint, options);
        }

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

        internal static IReadOnlyList<TDocument> DecodeSnapshotDocuments<TDocument>(
            CultNetSnapshotResponseRawMessage snapshot,
            CultNetDocumentRegistry registry)
            where TDocument : class
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var documents = new List<TDocument>(snapshot.Documents.Length);
            foreach (var record in snapshot.Documents)
            {
                if (record == null)
                    continue;

                var binding = registry.GetBySchemaId(record.SchemaId);
                var canDeserializeWithBinding =
                    binding != null &&
                    typeof(TDocument).IsAssignableFrom(binding.DocumentType);
                var canDeserializeAsSchemaAlias =
                    string.Equals(record.SchemaId, descriptor.SchemaId, StringComparison.Ordinal);
                if (!canDeserializeWithBinding && !canDeserializeAsSchemaAlias)
                    continue;

                if (!string.Equals(record.PayloadEncoding, "messagepack", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"CultNet raw document payloadEncoding must be \"messagepack\", not \"{record.PayloadEncoding}\".");
                }

                documents.Add(canDeserializeWithBinding
                    ? (TDocument)binding!.PayloadDeserializer(record.Payload)
                    : CultDocumentMessagePackSerialization.Deserialize<TDocument>(record.Payload));
            }

            return documents;
        }
    }
}
