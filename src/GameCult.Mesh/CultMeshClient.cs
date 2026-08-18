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

        /// <summary>Gets or sets schema connectors. Length-prefixed TCP is used by default.</summary>
        public IReadOnlyList<ICultMeshTransportConnector>? Connectors { get; set; }

        /// <summary>
        /// Gets or sets streaming content connectors. TCP content delivery is used by default;
        /// applications may replace it or explicitly add legacy RUDP.
        /// </summary>
        public IReadOnlyList<ICultMeshContentTransportConnector>? ContentConnectors { get; set; }

        /// <summary>
        /// Gets or sets realtime state connectors. No connector is installed implicitly;
        /// promoted runtimes should register QUIC and may explicitly add a legacy fallback.
        /// </summary>
        public IReadOnlyList<ICultMeshRealtimeTransportConnector> RealtimeConnectors { get; set; } =
            Array.Empty<ICultMeshRealtimeTransportConnector>();

        /// <summary>Gets or sets the deadline before an unanswered subscription intent is replayed.</summary>
        public TimeSpan SubscriptionResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);
    }

    /// <summary>Owns one shared live document subscription until disposed.</summary>
    public sealed class CultMeshDocumentLease<TDocument> : IDisposable where TDocument : class
    {
        private Action? _release;

        internal CultMeshDocumentLease(CultMeshDocumentHandle<TDocument> handle, Action release)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>Gets the live typed document handle owned by this lease.</summary>
        public CultMeshDocumentHandle<TDocument> Handle { get; }

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>Owns one shared live collection subscription until disposed.</summary>
    public sealed class CultMeshCollectionLease<TDocument> : IDisposable where TDocument : class
    {
        private Action? _release;

        internal CultMeshCollectionLease(CultMeshCollectionHandle<TDocument> handle, Action release)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>Gets the live typed collection handle owned by this lease.</summary>
        public CultMeshCollectionHandle<TDocument> Handle { get; }

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>
    /// Owns discovery and reusable sessions for one application lifetime.
    /// Applications address stable identities; CultMesh owns physical routes and reconnection.
    /// </summary>
    public sealed class CultMeshClient : IDisposable
    {
        private readonly CultMeshDiscoveryService _discovery;
        private readonly CultMeshSessionManager _sessions;
        private readonly TimeSpan _subscriptionResponseTimeout;
        private readonly ConcurrentDictionary<string, SharedResource> _documents = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SharedResource> _collections = new(StringComparer.Ordinal);
        private bool _disposed;

        public CultMeshClient(CultMeshClientOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.SubscriptionResponseTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options.SubscriptionResponseTimeout));
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
                    new CultMeshTcpSchemaTransportConnector(clock: options.Sessions.Clock)
                },
                options.ContentConnectors ?? new ICultMeshContentTransportConnector[]
                {
                    new CultMeshTcpContentTransportConnector()
                },
                options.RealtimeConnectors,
                options.Sessions);
            _subscriptionResponseTimeout = options.SubscriptionResponseTimeout;
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

        /// <summary>Connects the reusable realtime state plane by stable endpoint identity.</summary>
        public Task<CultMeshRealtimeSession> ConnectRealtimeAsync(
            string endpointId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _sessions.ConnectRealtimeAsync(CultMeshEndpointId.Parse(endpointId), cancellationToken);
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

        /// <summary>Creates a verified-transfer provider over this client's reusable content session owner.</summary>
        public CultMeshSessionContentProvider ContentProvider(
            string providerId,
            string endpointId)
        {
            ThrowIfDisposed();
            return new CultMeshSessionContentProvider(
                providerId,
                _sessions,
                CultMeshEndpointId.Parse(endpointId));
        }

        /// <summary>Creates a direct body provider over this client's reusable negotiated session owner.</summary>
        public CultMeshSessionBodyProvider BodyProvider(
            string providerId,
            string endpointId,
            CultMeshSessionBodyProviderOptions? options = null)
        {
            ThrowIfDisposed();
            return new CultMeshSessionBodyProvider(
                providerId,
                _sessions,
                CultMeshEndpointId.Parse(endpointId),
                options);
        }

        /// <summary>Leases one shared live typed document by stable provider identity and record key.</summary>
        public async Task<CultMeshDocumentLease<TDocument>> LeaseDocumentAsync<TDocument>(
            string endpointId,
            string recordKey,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Record key is required.", nameof(recordKey));
            var key = endpointId.Trim() + "\u001f" + typeof(TDocument).AssemblyQualifiedName + "\u001f" + recordKey.Trim();
            while (true)
            {
                var resource = _documents.GetOrAdd(key, _ => new SharedResource(
                    async owner => await OpenDocumentAsync<TDocument>(endpointId.Trim(), recordKey.Trim(), owner).ConfigureAwait(false)));
                if (!resource.TryAcquire())
                {
                    RemoveExact(_documents, key, resource);
                    continue;
                }
                try
                {
                    var binding = (RemoteDocumentBinding<TDocument>)await AwaitForCallerAsync(resource.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return new CultMeshDocumentLease<TDocument>(
                        binding.Handle,
                        () => Release(_documents, key, resource));
                }
                catch
                {
                    Release(_documents, key, resource);
                    throw;
                }
            }
        }

        /// <summary>Leases a shared live collection of one typed schema by stable provider identity.</summary>
        public Task<CultMeshCollectionLease<TDocument>> LeaseCollectionAsync<TDocument>(
            string endpointId,
            CancellationToken cancellationToken)
            where TDocument : class =>
            LeaseCollectionAsync<TDocument>(endpointId, includeInitialSnapshot: true, cancellationToken);

        /// <summary>Leases a live collection and optionally skips replaying its initial snapshot.</summary>
        public async Task<CultMeshCollectionLease<TDocument>> LeaseCollectionAsync<TDocument>(
            string endpointId,
            bool includeInitialSnapshot = true,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            var identity = endpointId.Trim();
            var key = identity + "\u001f" + typeof(TDocument).AssemblyQualifiedName + "\u001f" + includeInitialSnapshot;
            while (true)
            {
                var resource = _collections.GetOrAdd(key, _ => new SharedResource(
                    async owner => await OpenCollectionAsync<TDocument>(identity, includeInitialSnapshot, owner).ConfigureAwait(false)));
                if (!resource.TryAcquire())
                {
                    RemoveExact(_collections, key, resource);
                    continue;
                }
                try
                {
                    var binding = (RemoteCollectionBinding<TDocument>)await AwaitForCallerAsync(resource.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return new CultMeshCollectionLease<TDocument>(
                        binding.Handle,
                        () => Release(_collections, key, resource));
                }
                catch
                {
                    Release(_collections, key, resource);
                    throw;
                }
            }
        }

        /// <summary>
        /// Submits one typed document to a provider by stable identity. Submission is uncommitted input;
        /// only a provider-authored receipt or resulting state can establish acceptance.
        /// </summary>
        public async Task SubmitDocumentAsync<TDocument>(
            string endpointId,
            string recordKey,
            TDocument document,
            string sourceRuntimeId,
            string sourceRole,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Endpoint identity is required.", nameof(endpointId));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Record key is required.", nameof(recordKey));
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(sourceRuntimeId)) throw new ArgumentException("Source runtime identity is required.", nameof(sourceRuntimeId));
            if (string.IsNullOrWhiteSpace(sourceRole)) throw new ArgumentException("Source role is required.", nameof(sourceRole));

            var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(typeof(TDocument));
            var networkRegistry = CultMesh.CreateCultNetDocumentRegistry(new[] { typeof(TDocument) }, cacheRegistry);
            var message = networkRegistry.CreateRawDocumentPutMessage(
                "cultmesh-client-submit-" + Guid.NewGuid().ToString("N"),
                new CultRecordHandle<TDocument>(new CultRecordKey(recordKey.Trim())),
                document,
                new CultNetDocumentMessageOptions
                {
                    SourceRuntimeId = sourceRuntimeId.Trim(),
                    SourceRole = sourceRole.Trim()
                });
            var session = await ConnectAsync(endpointId.Trim(), CultMeshProtocols.Documents, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            session.OpenSchemaClient().SendCultNet(message);
        }

        /// <summary>Gets the number of currently leased shared document subscriptions.</summary>
        public int ActiveDocumentResourceCount => _documents.Count;

        /// <summary>Gets the number of currently leased shared collection subscriptions.</summary>
        public int ActiveCollectionResourceCount => _collections.Count;

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
            foreach (var resource in _documents.Values.Concat(_collections.Values).Distinct()) resource.Dispose();
            _documents.Clear();
            _collections.Clear();
            _sessions.Dispose();
            _discovery.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshClient));
        }

        private async Task<object> OpenDocumentAsync<TDocument>(
            string endpointId,
            string recordKey,
            SharedResource owner)
            where TDocument : class
        {
            var session = await ConnectAsync(endpointId, CultMeshProtocols.Documents).ConfigureAwait(false);
            var binding = new RemoteDocumentBinding<TDocument>(session, endpointId, recordKey, _subscriptionResponseTimeout);
            owner.Attach(binding);
            try
            {
                await binding.StartAsync().ConfigureAwait(false);
                return binding;
            }
            catch
            {
                owner.Detach(binding);
                binding.Dispose();
                throw;
            }
        }

        private async Task<object> OpenCollectionAsync<TDocument>(
            string endpointId,
            bool includeInitialSnapshot,
            SharedResource owner)
            where TDocument : class
        {
            var session = await ConnectAsync(endpointId, CultMeshProtocols.Documents).ConfigureAwait(false);
            var binding = new RemoteCollectionBinding<TDocument>(session, endpointId, includeInitialSnapshot, _subscriptionResponseTimeout);
            owner.Attach(binding);
            try
            {
                await binding.StartAsync().ConfigureAwait(false);
                return binding;
            }
            catch
            {
                owner.Detach(binding);
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

        private static void Release(
            ConcurrentDictionary<string, SharedResource> resources,
            string key,
            SharedResource resource)
        {
            if (!resource.Release()) return;
            RemoveExact(resources, key, resource);
            resource.Dispose();
        }

        private static void RemoveExact(
            ConcurrentDictionary<string, SharedResource> resources,
            string key,
            SharedResource resource)
        {
            ((ICollection<KeyValuePair<string, SharedResource>>)resources)
                .Remove(new KeyValuePair<string, SharedResource>(key, resource));
        }

        private sealed class SharedResource : IDisposable
        {
            private readonly object _gate = new();
            private readonly Lazy<Task<object>> _value;
            private IDisposable? _attached;
            private int _leases;
            private bool _retired;

            public SharedResource(Func<SharedResource, Task<object>> open)
            {
                if (open == null) throw new ArgumentNullException(nameof(open));
                _value = new Lazy<Task<object>>(() => open(this), LazyThreadSafetyMode.ExecutionAndPublication);
            }

            public Task<object> Value => _value.Value;

            public bool TryAcquire()
            {
                lock (_gate)
                {
                    if (_retired) return false;
                    _leases++;
                    return true;
                }
            }

            public bool Release()
            {
                lock (_gate)
                {
                    if (_leases <= 0) return false;
                    _leases--;
                    if (_leases != 0) return false;
                    _retired = true;
                    return true;
                }
            }

            public void Attach(IDisposable resource)
            {
                if (resource == null) throw new ArgumentNullException(nameof(resource));
                lock (_gate)
                {
                    if (_retired)
                    {
                        resource.Dispose();
                        throw new ObjectDisposedException(nameof(SharedResource));
                    }
                    if (_attached != null) throw new InvalidOperationException("CultMesh shared resource already has a live binding.");
                    _attached = resource;
                }
            }

            public void Detach(IDisposable resource)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_attached, resource)) _attached = null;
                }
            }

            public void Dispose()
            {
                IDisposable? attached;
                lock (_gate)
                {
                    _retired = true;
                    attached = _attached;
                    _attached = null;
                }
                attached?.Dispose();
            }
        }

        private sealed class RemoteDocumentBinding<TDocument> : IDisposable where TDocument : class
        {
            private readonly CultMeshSession _session;
            private readonly string _recordKey;
            private readonly string _subscriptionId;
            private readonly CultCache _cache;
            private readonly CultNetDatabaseSubscriptionClient _subscription;
            private readonly TimeSpan _responseTimeout;
            private readonly CancellationTokenSource _lifetime = new();
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _subscribeLock = new();
            private Task? _subscribeTask;
            private long _subscribedGeneration = -1;
            private IDisposable? _stateWatch;
            private bool _disposed;

            public RemoteDocumentBinding(
                CultMeshSession session,
                string endpointId,
                string recordKey,
                TimeSpan responseTimeout)
            {
                _session = session;
                _responseTimeout = responseTimeout;
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
                    var generation = _session.PhysicalGeneration;
                    if (_subscribedGeneration == generation && _subscribeTask != null) return _subscribeTask;
                    _subscribedGeneration = generation;
                    return _subscribeTask = SubscribeAsync();
                }
            }

            private async Task SubscribeAsync()
            {
                try
                {
                    while (!_disposed)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        deadline.CancelAfter(_responseTimeout);
                        try
                        {
                            await _subscription.SubscribeAsync(
                                    _subscriptionId,
                                    recordKeys: new[] { _recordKey },
                                    schemaIds: new[] { CultDocumentRegistry.Shared.GetRequired<TDocument>().SchemaId },
                                    cancellationToken: deadline.Token)
                                .ConfigureAwait(false);
                            _ready.TrySetResult(true);
                            return;
                        }
                        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                        {
                        }
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                _stateWatch?.Dispose();
                _subscription.Dispose();
                _ready.TrySetException(new ObjectDisposedException(GetType().FullName));
                _cache.Dispose();
                _lifetime.Dispose();
            }
        }

        private sealed class RemoteCollectionBinding<TDocument> : IDisposable where TDocument : class
        {
            private readonly CultMeshSession _session;
            private readonly string _subscriptionId;
            private readonly CultCache _cache;
            private readonly CultNetDatabaseSubscriptionClient _subscription;
            private readonly TimeSpan _responseTimeout;
            private readonly CancellationTokenSource _lifetime = new();
            private readonly bool _includeInitialSnapshot;
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _subscribeLock = new();
            private Task? _subscribeTask;
            private long _subscribedGeneration = -1;
            private IDisposable? _stateWatch;
            private bool _disposed;

            public RemoteCollectionBinding(
                CultMeshSession session,
                string endpointId,
                bool includeInitialSnapshot,
                TimeSpan responseTimeout)
            {
                _session = session;
                _responseTimeout = responseTimeout;
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
                    var generation = _session.PhysicalGeneration;
                    if (_subscribedGeneration == generation && _subscribeTask != null) return _subscribeTask;
                    _subscribedGeneration = generation;
                    return _subscribeTask = SubscribeAsync();
                }
            }

            private async Task SubscribeAsync()
            {
                try
                {
                    while (!_disposed)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        deadline.CancelAfter(_responseTimeout);
                        try
                        {
                            await _subscription.SubscribeAsync(
                                    _subscriptionId,
                                    schemaIds: new[] { CultDocumentRegistry.Shared.GetRequired<TDocument>().SchemaId },
                                    includeSnapshot: _includeInitialSnapshot,
                                    cancellationToken: deadline.Token)
                                .ConfigureAwait(false);
                            _ready.TrySetResult(true);
                            return;
                        }
                        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                        {
                        }
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                _stateWatch?.Dispose();
                _subscription.Dispose();
                _ready.TrySetException(new ObjectDisposedException(GetType().FullName));
                _cache.Dispose();
                _lifetime.Dispose();
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
                        TransportVersion = _options.TransportVersion
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
