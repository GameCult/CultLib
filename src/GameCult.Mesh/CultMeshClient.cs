using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using R3;

namespace GameCult.Mesh
{
    /// <summary>Configures one application-lifetime CultMesh client.</summary>
    public sealed class CultMeshClientOptions
    {
        /// <summary>Gets or sets the rendezvous endpoints used to bootstrap stable identities.</summary>
        public IReadOnlyList<string> RendezvousEndpoints { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets Verse discovery transport and persistence options.</summary>
        public CultMeshVerseDiscoveryClientOptions Discovery { get; set; } = new();

        /// <summary>Gets or sets session path and diagnostic policy.</summary>
        public CultMeshSessionManagerOptions Sessions { get; set; } = new();

        /// <summary>Gets or sets optional transport connectors. The CultNet schema connector is used by default.</summary>
        public IReadOnlyList<ICultMeshTransportConnector>? Connectors { get; set; }
    }

    /// <summary>
    /// Owns discovery and reusable sessions for one application lifetime.
    /// Applications address stable identities; CultMesh owns physical routes and reconnection.
    /// </summary>
    public sealed class CultMeshClient : IDisposable
    {
        private readonly CultMeshDiscoveryService _discovery;
        private readonly CultMeshSessionManager _sessions;
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _documents = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _collections = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<IDisposable> _documentResources = new();
        private bool _disposed;

        public CultMeshClient(CultMeshClientOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var endpoints = options.RendezvousEndpoints
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (endpoints.Length == 0)
                throw new ArgumentException("At least one CultMesh rendezvous endpoint is required.", nameof(options));

            var discoveryClient = new CultMeshVerseDiscoveryClient(options.Discovery);
            _discovery = new CultMeshDiscoveryService(
                endpoints.Select(endpoint => new RendezvousLookupSource(endpoint, discoveryClient, options.Discovery)),
                new CultMeshDiscoveryServiceOptions
                {
                    Clock = options.Discovery.Clock,
                    Diagnostics = options.Discovery.Diagnostics,
                    Store = options.Discovery.DiscoveryStore
                });
            _sessions = new CultMeshSessionManager(
                _discovery,
                options.Connectors ?? new ICultMeshTransportConnector[]
                {
                    new CultMeshSchemaTransportConnector(clock: options.Sessions.Clock)
                },
                options.Sessions);
        }

        /// <summary>Connects to a stable endpoint identity using one application protocol.</summary>
        public Task<CultMeshSession> ConnectAsync(
            string endpointId,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _sessions.ConnectAsync(CultMeshEndpointId.Parse(endpointId), protocol, cancellationToken);
        }

        /// <summary>Connects to a stable endpoint identity using one application protocol.</summary>
        public Task<CultMeshSession> ConnectAsync(
            CultMeshEndpointId endpointId,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _sessions.ConnectAsync(endpointId, protocol, cancellationToken);
        }

        /// <summary>Opens one live typed document by stable provider identity and record key.</summary>
        public async Task<CultMeshDocumentHandle<TDocument>> DocumentAsync<TDocument>(
            string endpointId,
            string recordKey,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Record key is required.", nameof(recordKey));
            var key = endpointId.Trim() + "\u001f" + typeof(TDocument).AssemblyQualifiedName + "\u001f" + recordKey.Trim();
            var lazy = _documents.GetOrAdd(key, _ => new Lazy<Task<object>>(
                async () => await OpenDocumentAsync<TDocument>(endpointId.Trim(), recordKey.Trim()).ConfigureAwait(false),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var binding = (RemoteDocumentBinding<TDocument>)await AwaitForCallerAsync(lazy.Value, cancellationToken)
                    .ConfigureAwait(false);
                return binding.Handle;
            }
            catch
            {
                _documents.TryRemove(key, out _);
                throw;
            }
        }

        /// <summary>Opens a live collection of one typed schema by stable provider identity.</summary>
        public Task<CultMeshCollectionHandle<TDocument>> CollectionAsync<TDocument>(
            string endpointId,
            CancellationToken cancellationToken)
            where TDocument : class =>
            CollectionAsync<TDocument>(endpointId, includeInitialSnapshot: true, cancellationToken);

        /// <summary>Opens a live collection and optionally skips replaying its initial snapshot.</summary>
        public async Task<CultMeshCollectionHandle<TDocument>> CollectionAsync<TDocument>(
            string endpointId,
            bool includeInitialSnapshot = true,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            var identity = endpointId.Trim();
            var key = identity + "\u001f" + typeof(TDocument).AssemblyQualifiedName + "\u001f" + includeInitialSnapshot;
            var lazy = _collections.GetOrAdd(key, _ => new Lazy<Task<object>>(
                async () => await OpenCollectionAsync<TDocument>(identity, includeInitialSnapshot).ConfigureAwait(false),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var binding = (RemoteCollectionBinding<TDocument>)await AwaitForCallerAsync(lazy.Value, cancellationToken)
                    .ConfigureAwait(false);
                return binding.Handle;
            }
            catch
            {
                _collections.TryRemove(key, out _);
                throw;
            }
        }

        /// <summary>Reads one typed record once through the reusable document session.</summary>
        public async Task<TDocument> ReadAsync<TDocument>(
            string endpointId,
            string recordKey,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Record key is required.", nameof(recordKey));
            var documents = await ReadManyAsync<TDocument>(endpointId, new[] { recordKey }, cancellationToken)
                .ConfigureAwait(false);
            return documents.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"CultMesh endpoint '{endpointId}' did not publish {typeof(TDocument).FullName} record '{recordKey}'.");
        }

        /// <summary>Reads one typed record once with an explicit response deadline.</summary>
        public async Task<TDocument> ReadAsync<TDocument>(
            string endpointId,
            string recordKey,
            TimeSpan responseTimeout,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            var documents = await ReadManyCoreAsync<TDocument>(
                    endpointId,
                    new[] { recordKey },
                    responseTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return documents.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"CultMesh endpoint '{endpointId}' did not publish {typeof(TDocument).FullName} record '{recordKey}'.");
        }

        /// <summary>Reads typed records once in one request through the reusable document session.</summary>
        public async Task<IReadOnlyList<TDocument>> ReadManyAsync<TDocument>(
            string endpointId,
            IReadOnlyList<string> recordKeys,
            CancellationToken cancellationToken = default)
            where TDocument : class
            => await ReadManyCoreAsync<TDocument>(
                    endpointId,
                    recordKeys,
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                .ConfigureAwait(false);

        private async Task<IReadOnlyList<TDocument>> ReadManyCoreAsync<TDocument>(
            string endpointId,
            IReadOnlyList<string> recordKeys,
            TimeSpan responseTimeout,
            CancellationToken cancellationToken)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            if (recordKeys == null || recordKeys.Count == 0 || recordKeys.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("At least one non-empty record key is required.", nameof(recordKeys));
            var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(typeof(TDocument));
            var networkRegistry = CultMesh.CreateCultNetDocumentRegistry(new[] { typeof(TDocument) }, cacheRegistry);
            using var snapshot = await CultMeshSnapshotSession.ConnectAsync(
                    _sessions,
                    CultMeshEndpointId.Parse(endpointId),
                    CultMeshProtocols.Content,
                    new CultMeshSnapshotRequestOptions
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                        ResponseTimeout = responseTimeout,
                        MessageIdPrefix = "cultmesh-client-read"
                    },
                    networkRegistry,
                    cancellationToken)
                .ConfigureAwait(false);
            return await snapshot.FetchDocumentsAsync<TDocument>(
                    recordKeys: recordKeys,
                    schemaIds: new[] { cacheRegistry.GetRequired<TDocument>().SchemaId })
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var resource in _documentResources) resource.Dispose();
            _documents.Clear();
            _collections.Clear();
            _sessions.Dispose();
            _discovery.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshClient));
        }

        private async Task<object> OpenDocumentAsync<TDocument>(string endpointId, string recordKey)
            where TDocument : class
        {
            var session = await ConnectAsync(endpointId, CultMeshProtocols.Documents).ConfigureAwait(false);
            var binding = new RemoteDocumentBinding<TDocument>(session, endpointId, recordKey);
            _documentResources.Add(binding);
            try
            {
                await binding.StartAsync().ConfigureAwait(false);
                return binding;
            }
            catch
            {
                binding.Dispose();
                throw;
            }
        }

        private async Task<object> OpenCollectionAsync<TDocument>(string endpointId, bool includeInitialSnapshot)
            where TDocument : class
        {
            var session = await ConnectAsync(endpointId, CultMeshProtocols.Documents).ConfigureAwait(false);
            var binding = new RemoteCollectionBinding<TDocument>(session, endpointId, includeInitialSnapshot);
            _documentResources.Add(binding);
            try
            {
                await binding.StartAsync().ConfigureAwait(false);
                return binding;
            }
            catch
            {
                binding.Dispose();
                throw;
            }
        }

        private static async Task<T> AwaitForCallerAsync<T>(Task<T> shared, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return await shared.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false) != shared)
                    throw new OperationCanceledException(cancellationToken);
            }
            return await shared.ConfigureAwait(false);
        }

        private sealed class RemoteDocumentBinding<TDocument> : IDisposable where TDocument : class
        {
            private readonly CultMeshSession _session;
            private readonly string _recordKey;
            private readonly string _subscriptionId;
            private readonly CultCache _cache;
            private readonly CultNetDatabaseSubscriptionClient _subscription;
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _subscribeLock = new();
            private Task? _subscribeTask;
            private IDisposable? _stateWatch;
            private bool _disposed;

            public RemoteDocumentBinding(CultMeshSession session, string endpointId, string recordKey)
            {
                _session = session;
                _recordKey = recordKey;
                _subscriptionId = "cultmesh-document:" + endpointId + ":" + recordKey;
                var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(typeof(TDocument));
                var networkRegistry = CultMesh.CreateCultNetDocumentRegistry(new[] { typeof(TDocument) }, cacheRegistry);
                _cache = new CultCache(cacheRegistry);
                _subscription = new CultNetDatabaseSubscriptionClient(session.OpenSchemaClient(), _cache, networkRegistry);
                Handle = CultMesh.Document<TDocument>(
                    _cache,
                    new CultRecordKey(recordKey),
                    CultMesh.Verse(endpointId, "cultmesh-client").Context,
                    routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, endpointId));
            }

            public CultMeshDocumentHandle<TDocument> Handle { get; }

            public async Task StartAsync()
            {
                _stateWatch = _session.WatchState()
                    .Where(state => state.Status == CultMeshSessionStatus.Online)
                    .Subscribe(state => { _ = EnsureSubscribedAsync(); });
                _ = EnsureSubscribedAsync();
                await _ready.Task.ConfigureAwait(false);
            }

            private Task EnsureSubscribedAsync()
            {
                lock (_subscribeLock)
                {
                    if (_subscribeTask is { IsCompleted: false }) return _subscribeTask;
                    return _subscribeTask = SubscribeAsync();
                }
            }

            private async Task SubscribeAsync()
            {
                try
                {
                    if (_disposed) return;
                    await _subscription.SubscribeAsync(
                        _subscriptionId,
                        recordKeys: new[] { _recordKey },
                        schemaIds: new[] { CultDocumentRegistry.Shared.GetRequired<TDocument>().SchemaId })
                        .ConfigureAwait(false);
                    _ready.TrySetResult(true);
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _stateWatch?.Dispose();
                _subscription.Dispose();
                _ready.TrySetException(new ObjectDisposedException(GetType().FullName));
                _cache.Dispose();
            }
        }

        private sealed class RemoteCollectionBinding<TDocument> : IDisposable where TDocument : class
        {
            private readonly CultMeshSession _session;
            private readonly string _subscriptionId;
            private readonly CultCache _cache;
            private readonly CultNetDatabaseSubscriptionClient _subscription;
            private readonly bool _includeInitialSnapshot;
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _subscribeLock = new();
            private Task? _subscribeTask;
            private IDisposable? _stateWatch;
            private bool _disposed;

            public RemoteCollectionBinding(CultMeshSession session, string endpointId, bool includeInitialSnapshot)
            {
                _session = session;
                _subscriptionId = "cultmesh-collection:" + endpointId + ":" + typeof(TDocument).FullName;
                _includeInitialSnapshot = includeInitialSnapshot;
                var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(typeof(TDocument));
                var networkRegistry = CultMesh.CreateCultNetDocumentRegistry(new[] { typeof(TDocument) }, cacheRegistry);
                _cache = new CultCache(cacheRegistry);
                _subscription = new CultNetDatabaseSubscriptionClient(session.OpenSchemaClient(), _cache, networkRegistry);
                Handle = CultMesh.Collection<TDocument>(
                    _cache,
                    routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, endpointId));
            }

            public CultMeshCollectionHandle<TDocument> Handle { get; }

            public async Task StartAsync()
            {
                _stateWatch = _session.WatchState()
                    .Where(state => state.Status == CultMeshSessionStatus.Online)
                    .Subscribe(state => { _ = EnsureSubscribedAsync(); });
                _ = EnsureSubscribedAsync();
                await _ready.Task.ConfigureAwait(false);
            }

            private Task EnsureSubscribedAsync()
            {
                lock (_subscribeLock)
                {
                    if (_subscribeTask is { IsCompleted: false }) return _subscribeTask;
                    return _subscribeTask = SubscribeAsync();
                }
            }

            private async Task SubscribeAsync()
            {
                try
                {
                    if (_disposed) return;
                    await _subscription.SubscribeAsync(
                        _subscriptionId,
                        schemaIds: new[] { CultDocumentRegistry.Shared.GetRequired<TDocument>().SchemaId },
                        includeSnapshot: _includeInitialSnapshot)
                        .ConfigureAwait(false);
                    _ready.TrySetResult(true);
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _stateWatch?.Dispose();
                _subscription.Dispose();
                _ready.TrySetException(new ObjectDisposedException(GetType().FullName));
                _cache.Dispose();
            }
        }

        private sealed class RendezvousLookupSource : ICultMeshLookupSource
        {
            private readonly string _endpoint;
            private readonly CultMeshVerseDiscoveryClient _client;
            private readonly CultMeshVerseDiscoveryClientOptions _options;

            public RendezvousLookupSource(
                string endpoint,
                CultMeshVerseDiscoveryClient client,
                CultMeshVerseDiscoveryClientOptions options)
            {
                _endpoint = endpoint;
                _client = client;
                _options = options;
            }

            public string SourceId => "rendezvous:" + _endpoint;

            public async Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
                CultMeshDiscoveryQuery query,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await _client.FetchAsync(
                    _endpoint,
                    new CultMeshVerseCatalogRequestMessage
                    {
                        VerseIds = query.VerseIds.Count == 0 ? null : query.VerseIds.ToArray(),
                        TransportVersion = "cultmesh.v0"
                    }).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var observedAt = _options.Clock.UtcNow;
                return response.Verses.Select(message => new CultMeshDiscoveryObservation(
                    message.ToVerseDescriptor(),
                    SourceId,
                    observedAt,
                    observedAt + _options.ObservationTtl,
                    CultMeshDiscoveryTrust.Unsigned,
                    response.SchemaVersion)).ToArray();
            }
        }
    }
}
