using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;
using MessagePack;

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
    /// Reuses one connected CultNet schema client for an ordered sequence of snapshot requests.
    /// </summary>
    public sealed class CultMeshSnapshotSession : IDisposable
    {
        private readonly string _endpoint;
        private readonly CultMeshSnapshotRequestOptions _defaults;
        private readonly ICultNetSchemaClient _client;
        private readonly CultMeshSession? _sharedSession;
        private readonly IDisposable? _responseSubscription;
        private readonly IDisposable? _errorSubscription;
        private readonly CultNetDocumentRegistry _registry;
        private readonly SemaphoreSlim _requests = new(1, 1);
        private readonly object _completionLock = new();
        private TaskCompletionSource<CultNetSnapshotResponseRawMessage>? _completion;
        private string? _messageId;
        private bool _disposed;

        internal CultMeshSnapshotSession(
            string endpoint,
            CultMeshSnapshotRequestOptions options,
            CultNetDocumentRegistry? registry)
        {
            _endpoint = string.IsNullOrWhiteSpace(endpoint)
                ? throw new ArgumentException("Value must be non-empty.", nameof(endpoint))
                : endpoint;
            _defaults = CultMesh.CloneSnapshotRequestOptions(options);
            _registry = registry ?? new CultNetDocumentRegistry();
            _client = CultMesh.CreateSnapshotClient(endpoint, _defaults)();
            _client.OnCultNet<CultNetSnapshotResponseRawMessage>(OnResponse);
            _client.OnCultNet<CultNetErrorMessage>(OnError);
            var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(endpoint);
            _client.Connect(host, port);
        }

        private CultMeshSnapshotSession(
            CultMeshSession session,
            CultMeshSnapshotRequestOptions options,
            CultNetDocumentRegistry? registry)
        {
            _sharedSession = session ?? throw new ArgumentNullException(nameof(session));
            _endpoint = session.State.Path?.Endpoint ?? session.Target.AuthorityRuntimeId;
            _defaults = CultMesh.CloneSnapshotRequestOptions(options);
            _registry = registry ?? new CultNetDocumentRegistry();
            _client = session.Channel;
            _responseSubscription = session.OnCultNet<CultNetSnapshotResponseRawMessage>(OnResponse);
            _errorSubscription = session.OnCultNet<CultNetErrorMessage>(OnError);
        }

        public static async Task<CultMeshSnapshotSession> ConnectAsync(
            CultMeshSessionManager sessions,
            CultMeshSessionTarget target,
            CultMeshSnapshotRequestOptions options,
            CultNetDocumentRegistry? registry = null,
            CancellationToken cancellationToken = default)
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            var session = await sessions.ConnectAsync(target, CultMeshProtocols.Documents, cancellationToken).ConfigureAwait(false);
            return new CultMeshSnapshotSession(session, options ?? new CultMeshSnapshotRequestOptions(), registry);
        }

        public static async Task<CultMeshSnapshotSession> ConnectAsync(
            CultMeshSessionManager sessions,
            CultMeshSessionTarget target,
            CultMeshProtocolId protocol,
            CultMeshSnapshotRequestOptions options,
            CultNetDocumentRegistry? registry = null,
            CancellationToken cancellationToken = default)
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            var session = await sessions.ConnectAsync(target, protocol, cancellationToken).ConfigureAwait(false);
            return new CultMeshSnapshotSession(session, options ?? new CultMeshSnapshotRequestOptions(), registry);
        }

        /// <summary>Fetches one raw snapshot while retaining the underlying endpoint connection.</summary>
        public async Task<CultNetSnapshotResponseRawMessage> FetchSnapshotAsync(
            IReadOnlyList<string>? schemaIds = null,
            IReadOnlyList<string>? recordKeys = null)
        {
            ThrowIfDisposed();
            await _requests.WaitAsync().ConfigureAwait(false);
            try
            {
                await CultMesh.WaitForSnapshotClientConnectionAsync(
                        _client,
                        _endpoint,
                        _defaults.ConnectTimeout,
                        (_client as ICultNetSchemaClientHealth)?.BackgroundFailure)
                    .ConfigureAwait(false);
                var options = CultMesh.CloneSnapshotRequestOptions(_defaults);
                options.SchemaIds = schemaIds ?? options.SchemaIds;
                options.RecordKeys = recordKeys ?? options.RecordKeys;
                var messageId = CultMesh.CreateSnapshotMessageId(options);
                var completion = new TaskCompletionSource<CultNetSnapshotResponseRawMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_completionLock)
                {
                    _messageId = messageId;
                    _completion = completion;
                }
                _client.SendCultNet(new CultNetSnapshotRequestMessage
                {
                    MessageId = messageId,
                    SchemaIds = CultMesh.CleanSnapshotFilter(options.SchemaIds),
                    RecordKeys = CultMesh.CleanSnapshotFilter(options.RecordKeys),
                    ShardId = string.IsNullOrWhiteSpace(options.ShardId) ? null : options.ShardId,
                    ShardEpoch = options.ShardEpoch
                });
                return await CultMesh.WaitForSnapshotResponseAsync(
                        completion.Task,
                        _endpoint,
                        options,
                        messageId,
                        (_client as ICultNetSchemaClientHealth)?.BackgroundFailure)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_completionLock)
                {
                    _messageId = null;
                    _completion = null;
                }
                _requests.Release();
            }
        }

        /// <summary>Fetches and decodes typed documents over the retained endpoint connection.</summary>
        public async Task<IReadOnlyList<TDocument>> FetchDocumentsAsync<TDocument>(
            IReadOnlyList<string>? recordKeys = null,
            IReadOnlyList<string>? schemaIds = null)
            where TDocument : class
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var resolvedSchemas = schemaIds ?? (recordKeys is { Count: > 0 } ? null : new[] { descriptor.SchemaId });
            var snapshot = await FetchSnapshotAsync(resolvedSchemas, recordKeys).ConfigureAwait(false);
            return CultMesh.DecodeSnapshotDocuments<TDocument>(snapshot, _registry);
        }

        /// <summary>Closes the retained endpoint connection and rejects any active request.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_completionLock)
                _completion?.TrySetException(new ObjectDisposedException(nameof(CultMeshSnapshotSession)));
            _responseSubscription?.Dispose();
            _errorSubscription?.Dispose();
            if (_sharedSession == null) _client.Dispose();
            _requests.Dispose();
        }

        private void OnResponse(CultNetSnapshotResponseRawMessage response)
        {
            lock (_completionLock)
            {
                if (string.Equals(response.MessageId, _messageId, StringComparison.Ordinal))
                    _completion?.TrySetResult(response);
            }
        }

        private void OnError(CultNetErrorMessage error)
        {
            lock (_completionLock)
                _completion?.TrySetException(new InvalidOperationException(error.Error));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshSnapshotSession));
        }
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
    /// Describes one typed document handle to bind from a snapshot endpoint.
    /// </summary>
    public interface ICultMeshSnapshotDocumentBinding
    {
        /// <summary>Gets the CLR document type requested by this binding.</summary>
        Type DocumentType { get; }

        /// <summary>Gets the remote record key to read.</summary>
        string RecordKey { get; }

        /// <summary>Gets the optional semantic document id for the resulting handle.</summary>
        string? DocumentId { get; }

        /// <summary>Binds this document request to an endpoint-backed handle.</summary>
        ICultMeshDocumentHandle Bind(CultMeshSnapshotEndpoint endpoint);

        /// <summary>Binds this document request to an endpoint-backed handle that syncs into a local node before returning values.</summary>
        ICultMeshDocumentHandle BindSynced(CultMeshSnapshotEndpoint endpoint, CultMeshNode node, bool flush);
    }

    /// <summary>
    /// Describes one typed document handle to bind from a snapshot endpoint.
    /// </summary>
    public sealed class CultMeshSnapshotDocumentBinding<TDocument> : ICultMeshSnapshotDocumentBinding
        where TDocument : class
    {
        internal CultMeshSnapshotDocumentBinding(string recordKey, string? documentId)
        {
            RecordKey = string.IsNullOrWhiteSpace(recordKey)
                ? throw new ArgumentException("Value must be non-empty.", nameof(recordKey))
                : recordKey;
            DocumentId = documentId;
        }

        /// <inheritdoc />
        public Type DocumentType => typeof(TDocument);

        /// <inheritdoc />
        public string RecordKey { get; }

        /// <inheritdoc />
        public string? DocumentId { get; }

        /// <inheritdoc />
        public ICultMeshDocumentHandle Bind(CultMeshSnapshotEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            return endpoint.Document<TDocument>(RecordKey, DocumentId);
        }

        /// <inheritdoc />
        public ICultMeshDocumentHandle BindSynced(CultMeshSnapshotEndpoint endpoint, CultMeshNode node, bool flush)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            return endpoint.SyncedDocument<TDocument>(node, RecordKey, DocumentId, flush);
        }
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
    /// Snapshot endpoint view with a local node sync policy already bound.
    /// </summary>
    public sealed class CultMeshSyncedSnapshotEndpoint
    {
        internal CultMeshSyncedSnapshotEndpoint(
            CultMeshSnapshotEndpoint endpoint,
            CultMeshNode node,
            bool flush)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            Node = node ?? throw new ArgumentNullException(nameof(node));
            Flush = flush;
        }

        /// <summary>Gets the remote endpoint that provides snapshots.</summary>
        public CultMeshSnapshotEndpoint Endpoint { get; }

        /// <summary>Gets the local node snapshots are synced into.</summary>
        public CultMeshNode Node { get; }

        /// <summary>Gets whether each sync should flush the local node afterward.</summary>
        public bool Flush { get; }

        /// <summary>Creates a typed document handle that syncs into the local node before returning values.</summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string recordKey,
            string? documentId = null)
            where TDocument : class
        {
            return Endpoint.SyncedDocument<TDocument>(Node, recordKey, documentId, Flush);
        }

        /// <summary>Creates a schema-aware catalog from typed endpoint bindings that sync into the local node before returning values.</summary>
        public CultMeshDocumentCatalog Documents(params ICultMeshSnapshotDocumentBinding[] documents)
        {
            return Endpoint.SyncedDocuments(Node, Flush, documents);
        }
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

        /// <summary>Returns a view of this endpoint whose typed document handles sync into the supplied local node.</summary>
        public CultMeshSyncedSnapshotEndpoint SyncTo(CultMeshNode node, bool flush = false)
        {
            return new CultMeshSyncedSnapshotEndpoint(this, node, flush);
        }

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
                CreateRequest(ResolveDefaultSchemaFilter(schemaIds, recordKeys, descriptor), recordKeys),
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
                    ResolveDefaultSchemaFilter(schemaIds, recordKeys, descriptor),
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

        /// <summary>Creates a typed document handle that fetches one endpoint record and syncs it into a local node before returning values.</summary>
        public CultMeshDocumentHandle<TDocument> SyncedDocument<TDocument>(
            CultMeshNode node,
            string recordKey,
            string? documentId = null,
            bool flush = false)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var sources = new[]
            {
                CultMesh.ProjectionSource($"{SourceId}:{recordKey}", descriptor.SchemaId, "synced CultNet snapshot endpoint")
            };
            var watch = CultMesh.PollingQueryWatcher<CultMeshDocumentQueryParameters, TDocument>(
                async (_parameters, _context) => await SyncDocumentAsync<TDocument>(node, recordKey, flush).ConfigureAwait(false),
                new CultMeshPollingWatchOptions<TDocument>(PollInterval));

            return CultMesh.Document<TDocument>(
                string.IsNullOrWhiteSpace(documentId) ? recordKey : documentId!,
                Context,
                _ => SyncDocumentAsync<TDocument>(node, recordKey, flush),
                queryContext => watch(CultMeshDocumentQueryParameters.Empty, queryContext),
                sources,
                RouteHint);
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

        /// <summary>Creates a schema-aware catalog from endpoint-backed typed document bindings.</summary>
        public CultMeshDocumentCatalog Documents(params ICultMeshSnapshotDocumentBinding[] documents)
        {
            if (documents == null) throw new ArgumentNullException(nameof(documents));
            return CultMesh.Documents(documents.Select(document =>
            {
                if (document == null) throw new ArgumentNullException(nameof(documents));
                return document.Bind(this);
            }));
        }

        /// <summary>Creates a schema-aware catalog from typed endpoint bindings that sync into a local node before returning values.</summary>
        public CultMeshDocumentCatalog SyncedDocuments(
            CultMeshNode node,
            params ICultMeshSnapshotDocumentBinding[] documents)
        {
            return SyncedDocuments(node, flush: false, documents);
        }

        /// <summary>Creates a schema-aware catalog from typed endpoint bindings that sync into a local node before returning values.</summary>
        public CultMeshDocumentCatalog SyncedDocuments(
            CultMeshNode node,
            bool flush = false,
            params ICultMeshSnapshotDocumentBinding[] documents)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (documents == null) throw new ArgumentNullException(nameof(documents));
            return CultMesh.Documents(documents.Select(document =>
            {
                if (document == null) throw new ArgumentNullException(nameof(documents));
                return document.BindSynced(this, node, flush);
            }));
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

        private static IReadOnlyList<string>? ResolveDefaultSchemaFilter(
            IReadOnlyList<string>? schemaIds,
            IReadOnlyList<string>? recordKeys,
            CultDocumentDescriptor descriptor)
        {
            if (schemaIds != null)
                return schemaIds;

            return recordKeys is { Count: > 0 }
                ? null
                : new[] { descriptor.SchemaId };
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
        /// Opens one reusable CultNet snapshot connection for ordered bulk document transfer.
        /// </summary>
        public static CultMeshSnapshotSession SnapshotSession(
            string endpoint,
            CultMeshSnapshotRequestOptions? options = null,
            CultNetDocumentRegistry? registry = null)
        {
            return new CultMeshSnapshotSession(
                endpoint,
                options ?? new CultMeshSnapshotRequestOptions(),
                registry);
        }

        /// <summary>
        /// Describes one typed document to bind from a CultMesh snapshot endpoint.
        /// </summary>
        public static CultMeshSnapshotDocumentBinding<TDocument> SnapshotDocument<TDocument>(
            string recordKey,
            string? documentId = null)
            where TDocument : class
        {
            return new CultMeshSnapshotDocumentBinding<TDocument>(recordKey, documentId);
        }

        /// <summary>
        /// Fetches one typed snapshot document and syncs it into a local node in one call.
        /// </summary>
        public static Task<TDocument> SyncDocumentFromPeerSnapshotAsync<TDocument>(
            CultMeshNode node,
            string endpoint,
            string recordKey,
            CultMeshSnapshotEndpointOptions? options = null,
            bool flush = false)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));

            return SnapshotEndpoint(endpoint, options).SyncDocumentAsync<TDocument>(node, recordKey, flush);
        }

        /// <summary>
        /// Fetches one typed snapshot document through a caller-provided schema client and syncs it into a local node in one call.
        /// </summary>
        public static Task<TDocument> SyncDocumentFromPeerSnapshotAsync<TDocument>(
            CultMeshNode node,
            Func<ICultNetSchemaClient> createClient,
            string endpoint,
            string recordKey,
            CultMeshSnapshotEndpointOptions? options = null,
            bool flush = false)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (createClient == null) throw new ArgumentNullException(nameof(createClient));
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));

            var request = CloneSnapshotFacadeRequestOptions(options?.Request);
            request.CreateClient = createClient;
            var resolvedOptions = new CultMeshSnapshotEndpointOptions
            {
                Context = options?.Context,
                DocumentRegistry = options?.DocumentRegistry,
                Request = request,
                SourceId = options?.SourceId,
                RouteHint = options?.RouteHint,
                PollInterval = options?.PollInterval ?? TimeSpan.FromMilliseconds(250)
            };
            return SnapshotEndpoint(endpoint, resolvedOptions).SyncDocumentAsync<TDocument>(node, recordKey, flush);
        }

        private static CultMeshSnapshotRequestOptions CloneSnapshotFacadeRequestOptions(CultMeshSnapshotRequestOptions? source)
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
            await WaitForSnapshotClientConnectionAsync(
                    client,
                    endpoint,
                    resolvedOptions.ConnectTimeout,
                    (client as ICultNetSchemaClientHealth)?.BackgroundFailure)
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
                    messageId,
                    (client as ICultNetSchemaClientHealth)?.BackgroundFailure)
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

        internal static Func<ICultNetSchemaClient> CreateSnapshotClient(
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

        internal static CultMeshSnapshotRequestOptions CloneSnapshotRequestOptions(
            CultMeshSnapshotRequestOptions? source)
        {
            if (source == null) return new CultMeshSnapshotRequestOptions();
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

        internal static string CreateSnapshotMessageId(CultMeshSnapshotRequestOptions options)
        {
            var prefix = string.IsNullOrWhiteSpace(options.MessageIdPrefix)
                ? "cultmesh:snapshot"
                : options.MessageIdPrefix!;
            return $"{prefix}:{Guid.NewGuid():N}";
        }

        internal static async Task WaitForSnapshotClientConnectionAsync(
            ICultNetSchemaClient client,
            string endpoint,
            TimeSpan timeout,
            Task<Exception>? backgroundFailure = null)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!client.Connected)
            {
                if (backgroundFailure?.IsCompleted == true)
                {
                    var error = await backgroundFailure.ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"CultNet snapshot client failed while connecting to {endpoint}.",
                        error);
                }
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException($"Timed out connecting to CultNet snapshot endpoint {endpoint}.");

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        internal static async Task<CultNetSnapshotResponseRawMessage> WaitForSnapshotResponseAsync(
            Task<CultNetSnapshotResponseRawMessage> responseTask,
            string endpoint,
            CultMeshSnapshotRequestOptions options,
            string messageId,
            Task<Exception>? backgroundFailure = null)
        {
            var timeoutTask = Task.Delay(options.ResponseTimeout);
            var completed = backgroundFailure == null
                ? await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false)
                : await Task.WhenAny(responseTask, timeoutTask, backgroundFailure).ConfigureAwait(false);
            if (completed == backgroundFailure)
            {
                var error = await backgroundFailure.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"CultNet snapshot client failed while waiting for response '{messageId}' from {endpoint}.",
                    error);
            }
            if (completed != responseTask)
            {
                throw new TimeoutException(
                    $"Timed out waiting for CultNet snapshot response '{messageId}' from {endpoint} " +
                    $"for schemas [{string.Join(", ", CleanSnapshotFilter(options.SchemaIds) ?? Array.Empty<string>())}] " +
                    $"and records [{string.Join(", ", CleanSnapshotFilter(options.RecordKeys) ?? Array.Empty<string>())}].");
            }

            return await responseTask.ConfigureAwait(false);
        }

        internal static string[]? CleanSnapshotFilter(IReadOnlyList<string>? values)
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
                    string.Equals(record.SchemaId, descriptor.SchemaId, StringComparison.Ordinal) ||
                    (binding != null && IsSameCultDocumentSchema(binding.DocumentType, descriptor)) ||
                    RawSnapshotPayloadMatchesSchema(record.Payload, descriptor);
                if (!canDeserializeWithBinding && !canDeserializeAsSchemaAlias)
                    continue;

                if (!string.Equals(record.PayloadEncoding, "messagepack", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"CultNet raw document payloadEncoding must be \"messagepack\", not \"{record.PayloadEncoding}\".");
                }

                if (binding != null)
                {
                    var decoded = binding.PayloadDeserializer(record.Payload);
                    documents.Add(decoded is TDocument typed
                        ? typed
                        : ConvertUntypedDocument<TDocument>(decoded));
                }
                else
                {
                    documents.Add(CultDocumentMessagePackSerialization.Deserialize<TDocument>(record.Payload));
                }
            }

            return documents;
        }

        private static bool RawSnapshotPayloadMatchesSchema(
            byte[] payload,
            CultDocumentDescriptor descriptor)
        {
            var schemaVersion = TryReadSchemaVersion(payload);
            if (string.IsNullOrWhiteSpace(schemaVersion))
                return false;

            if (string.Equals(schemaVersion, descriptor.SchemaVersion, StringComparison.Ordinal))
                return true;

            var schemaName = InferSchemaName(schemaVersion!);
            return !string.IsNullOrWhiteSpace(schemaName) &&
                   string.Equals(schemaName, descriptor.SchemaName, StringComparison.Ordinal);
        }

        private static string? TryReadSchemaVersion(byte[] payload)
        {
            try
            {
                var array = MessagePackSerializer.Deserialize<object[]>(payload, CultNetSchemaMessageSerialization.Options);
                if (array.Length > 0 && array[0] is string schemaVersion)
                    return schemaVersion;
            }
            catch (Exception)
            {
                // Fall through to map decoding; different runtimes may encode object-like payloads.
            }

            try
            {
                var map = MessagePackSerializer.Deserialize<IReadOnlyDictionary<string, object?>>(payload, CultNetSchemaMessageSerialization.Options);
                if (map.TryGetValue("schemaVersion", out var schemaVersion) && schemaVersion is string schemaVersionText)
                    return schemaVersionText;
                if (map.TryGetValue("schema_version", out var snakeSchemaVersion) && snakeSchemaVersion is string snakeSchemaVersionText)
                    return snakeSchemaVersionText;
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        private static string? InferSchemaName(string schemaVersion)
        {
            var marker = schemaVersion.LastIndexOf(".v", StringComparison.Ordinal);
            if (marker <= 0 || marker + 2 >= schemaVersion.Length)
                return null;

            var version = schemaVersion.Substring(marker + 2);
            return version.All(char.IsDigit)
                ? schemaVersion.Substring(0, marker)
                : null;
        }
    }
}
