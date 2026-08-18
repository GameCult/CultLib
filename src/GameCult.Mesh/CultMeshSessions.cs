using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Networking;
using R3;

namespace GameCult.Mesh
{
    public sealed class CultMeshRuntimeId : IEquatable<CultMeshRuntimeId>
    {
        private CultMeshRuntimeId(string value) { Value = value; }
        public string Value { get; }
        public static CultMeshRuntimeId Parse(string value) => new CultMeshRuntimeId(
            string.IsNullOrWhiteSpace(value) ? throw new FormatException("CultMesh runtime identity must be non-empty.") : value.Trim());
        public bool Equals(CultMeshRuntimeId? other) => other != null && string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => Equals(obj as CultMeshRuntimeId);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
    }

    /// <summary>
    /// Identifies one provider inside one Verse. Odin resolves this stable pair to
    /// disposable physical routes; neither value is a socket address.
    /// </summary>
    public sealed class CultMeshSessionTarget : IEquatable<CultMeshSessionTarget>
    {
        /// <summary>Creates one stable provider target inside one Verse.</summary>
        public CultMeshSessionTarget(string verseId, string authorityRuntimeId)
        {
            VerseId = string.IsNullOrWhiteSpace(verseId)
                ? throw new ArgumentException("Verse identity is required.", nameof(verseId))
                : verseId.Trim();
            AuthorityRuntimeId = CultMeshRuntimeId.Parse(authorityRuntimeId).Value;
        }

        /// <summary>Gets the stable Verse identity.</summary>
        public string VerseId { get; }
        /// <summary>Gets the stable authority runtime identity advertised by that Verse.</summary>
        public string AuthorityRuntimeId { get; }
        /// <summary>Compatibility projection. Use <see cref="AuthorityRuntimeId"/>.</summary>
        [Obsolete("Use AuthorityRuntimeId. Provider identity is not a physical endpoint.")]
        public string ProviderRuntimeId => AuthorityRuntimeId;
        internal string SessionKey => VerseId + "\u001f" + AuthorityRuntimeId;

        public bool Equals(CultMeshSessionTarget? other) =>
            other != null &&
            string.Equals(VerseId, other.VerseId, StringComparison.Ordinal) &&
            string.Equals(AuthorityRuntimeId, other.AuthorityRuntimeId, StringComparison.Ordinal);
        public override bool Equals(object? obj) => Equals(obj as CultMeshSessionTarget);
        public override int GetHashCode() => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(VerseId),
            StringComparer.Ordinal.GetHashCode(AuthorityRuntimeId));
        public override string ToString() => VerseId + "/" + AuthorityRuntimeId;
    }

    public sealed class CultMeshProtocolId : IEquatable<CultMeshProtocolId>
    {
        public CultMeshProtocolId(string value) { Value = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Protocol identity is required.", nameof(value)) : value; }
        public string Value { get; }
        public bool Equals(CultMeshProtocolId? other) => other != null && string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => Equals(obj as CultMeshProtocolId);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
    }

    public static class CultMeshProtocols
    {
        public static CultMeshProtocolId Documents { get; } = new CultMeshProtocolId("cultmesh.documents.v1");
        public static CultMeshProtocolId Content { get; } = new CultMeshProtocolId("cultmesh.content.v1");
        public static CultMeshProtocolId Bodies { get; } = new CultMeshProtocolId("cultmesh.bodies.v1");
        public static CultMeshProtocolId Discovery { get; } = new CultMeshProtocolId("cultmesh.discovery.v1");
        public static CultMeshProtocolId PeerExchange { get; } = new CultMeshProtocolId("cultmesh.peer_exchange.v1");
        public static CultMeshProtocolId Subscriptions { get; } = new CultMeshProtocolId("cultmesh.subscriptions.v1");
        public static CultMeshProtocolId RealtimeState { get; } = new CultMeshProtocolId("cultmesh.realtime_state.v1");
    }

    public enum CultMeshTransportPathKind { Direct, Relay, Tunnel }

    public sealed class CultMeshTransportCandidate
    {
        public CultMeshTransportCandidate(
            string endpoint,
            CultMeshTransportPathKind pathKind = CultMeshTransportPathKind.Direct,
            int priority = 0,
            string authorityRuntimeId = "",
            string generation = "",
            CultMeshAuthorityRoute? authorityRoute = null)
        {
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? throw new ArgumentException("Physical endpoint is required.", nameof(endpoint)) : endpoint;
            PathKind = pathKind;
            Priority = priority;
            AuthorityRuntimeId = authorityRuntimeId?.Trim() ?? string.Empty;
            Generation = generation?.Trim() ?? string.Empty;
            AuthorityRoute = authorityRoute;
        }
        public string Endpoint { get; }
        public CultMeshTransportPathKind PathKind { get; }
        public int Priority { get; }
        public string AuthorityRuntimeId { get; }
        public string Generation { get; }
        public CultMeshAuthorityRoute? AuthorityRoute { get; }
        internal CultMeshVerifiedAuthorityRoute? VerifiedAuthority { get; private set; }
        internal CultMeshTransportCandidate WithVerifiedAuthority(CultMeshVerifiedAuthorityRoute verified)
        {
            VerifiedAuthority = verified ?? throw new ArgumentNullException(nameof(verified));
            return this;
        }
    }

    public enum CultMeshSessionStatus { Connecting, Online, Degraded, Reconnecting, Offline }
    public enum CultMeshSessionFailureReason { Resolution, Authentication, Transport, Protocol, Timeout, Cancellation, Authority, UnsupportedPath }

    public sealed class CultMeshSessionFailure
    {
        public CultMeshSessionFailure(CultMeshSessionFailureReason reason, string message, string endpoint = "")
        {
            Reason = reason;
            Message = message ?? "";
            Endpoint = endpoint ?? "";
        }
        public CultMeshSessionFailureReason Reason { get; }
        public string Message { get; }
        public string Endpoint { get; }
    }

    public sealed class CultMeshSessionState
    {
        public CultMeshSessionState(
            CultMeshSessionStatus status,
            DateTimeOffset observedAtUtc,
            CultMeshTransportCandidate? path = null,
            CultMeshSessionFailure? failure = null)
        {
            Status = status;
            ObservedAtUtc = observedAtUtc;
            Path = path;
            Failure = failure;
        }
        public CultMeshSessionStatus Status { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public CultMeshTransportCandidate? Path { get; }
        public CultMeshSessionFailure? Failure { get; }
    }

    public sealed class CultMeshSessionException : Exception
    {
        public CultMeshSessionException(CultMeshSessionFailure failure, Exception? inner = null)
            : base(failure?.Message, inner) { Failure = failure ?? throw new ArgumentNullException(nameof(failure)); }
        public CultMeshSessionFailure Failure { get; }
    }

    public interface ICultMeshTransportConnector
    {
        string ConnectorId { get; }
        int Priority { get; }
        bool CanConnect(CultMeshTransportCandidate candidate);
        Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Connects exact URI routes through a caller-supplied URI-capable CultNet client.
    /// This keeps transport packages outside CultMesh while preserving advertised paths.
    /// </summary>
    public sealed class CultMeshUriSchemaTransportConnector : ICultMeshTransportConnector
    {
        private readonly HashSet<string> _schemes;
        private readonly Func<string, ICultNetUriSchemaClient> _createClient;

        public CultMeshUriSchemaTransportConnector(
            string connectorId,
            IEnumerable<string> schemes,
            Func<string, ICultNetUriSchemaClient> createClient,
            int priority = 0)
        {
            if (string.IsNullOrWhiteSpace(connectorId))
                throw new ArgumentException("Connector identity is required.", nameof(connectorId));
            ConnectorId = connectorId.Trim();
            _schemes = new HashSet<string>(
                (schemes ?? throw new ArgumentNullException(nameof(schemes)))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (_schemes.Count == 0)
                throw new ArgumentException("At least one URI scheme is required.", nameof(schemes));
            _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
            Priority = priority;
        }

        public string ConnectorId { get; }
        public int Priority { get; }

        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            candidate != null &&
            Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var uri) &&
            _schemes.Contains(uri.Scheme);

        public async Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            if (!CanConnect(candidate))
                throw new NotSupportedException($"Connector '{ConnectorId}' does not support '{candidate?.Endpoint}'.");
            var endpoint = new Uri(candidate.Endpoint, UriKind.Absolute);
            var client = _createClient(candidate.Endpoint)
                ?? throw new InvalidOperationException($"Connector '{ConnectorId}' returned no schema client.");
            try
            {
                await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Explicit compatibility connector for the previous RUDP/LiteNetLib schema lanes.
    /// Prefer <see cref="CultMeshTcpSchemaTransportConnector"/> for registered schemas.
    /// </summary>
    public sealed class CultMeshSchemaTransportConnector : ICultMeshTransportConnector
    {
        private readonly Func<string, ICultNetSchemaClient> _createClient;
        private readonly ICultMeshClock _clock;
        private readonly TimeSpan _connectTimeout;

        public CultMeshSchemaTransportConnector(
            Func<string, ICultNetSchemaClient>? createClient = null,
            ICultMeshClock? clock = null,
            TimeSpan? connectTimeout = null)
        {
            _createClient = createClient ?? (endpoint => CultNetSchemaClients.CreateForEndpoint(endpoint));
            _clock = clock ?? CultMeshSystemClock.Instance;
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        }

        public string ConnectorId => "legacy-datagram-schema";
        public int Priority => 10_000;
        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, "cultnet", StringComparison.OrdinalIgnoreCase));

        public async Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            if (!CanConnect(candidate)) throw new NotSupportedException($"Unsupported CultNet endpoint '{candidate.Endpoint}'.");
            var client = _createClient(candidate.Endpoint);
            try
            {
                var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(candidate.Endpoint);
                client.Connect(host, port);
                var deadline = _clock.UtcNow + _connectTimeout;
                while (!client.Connected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_clock.UtcNow >= deadline)
                        throw new TimeoutException($"Timed out connecting to '{candidate.Endpoint}'.");
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
                }
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>Preferred connector for registered schema, command, receipt, and manifest traffic.</summary>
    public sealed class CultMeshTcpSchemaTransportConnector : ICultMeshTransportConnector
    {
        private readonly Func<string, ICultNetSchemaClient> _createClient;
        private readonly ICultMeshClock _clock;
        private readonly TimeSpan _connectTimeout;

        public CultMeshTcpSchemaTransportConnector(
            Func<string, ICultNetSchemaClient>? createClient = null,
            ICultMeshClock? clock = null,
            TimeSpan? connectTimeout = null)
        {
            _createClient = createClient ?? (_ => CultNetSchemaClients.CreateTcpFramed());
            _clock = clock ?? CultMeshSystemClock.Instance;
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        }

        public string ConnectorId => "tcp-schema";
        public int Priority => 0;
        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "cultnet+tcp", StringComparison.OrdinalIgnoreCase);

        public async Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            if (!CanConnect(candidate))
                throw new NotSupportedException($"TCP schema connector does not support '{candidate.Endpoint}'.");
            var client = _createClient(candidate.Endpoint);
            try
            {
                var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(candidate.Endpoint);
                client.Connect(host, port);
                var deadline = _clock.UtcNow + _connectTimeout;
                while (!client.Connected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_clock.UtcNow >= deadline)
                        throw new TimeoutException($"Timed out connecting to '{candidate.Endpoint}'.");
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
                }
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    public sealed class CultMeshSession : IDisposable
    {
        private readonly Subject<CultMeshSessionState> _states = new();
        private readonly ManagedSchemaChannel _channel;
        private long _physicalGeneration;
        private bool _disposed;
        internal CultMeshSession(CultMeshSessionTarget target, CultMeshProtocolId protocol, ICultNetSchemaClient channel, CultMeshSessionState state)
        {
            Target = target;
            Protocol = protocol;
            _channel = new ManagedSchemaChannel(channel);
            State = state;
        }
        public CultMeshSessionTarget Target { get; }
        public CultMeshProtocolId Protocol { get; }
        internal ICultNetSchemaClient Channel => _channel;
        public CultMeshSessionState State { get; private set; }
        internal long PhysicalGeneration => Interlocked.Read(ref _physicalGeneration);
        public Observable<CultMeshSessionState> WatchState() => _states;
        public ICultNetSchemaClient OpenSchemaClient() => new SessionSchemaClient(this);
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage => Channel.SendCultNet(message);
        public IDisposable OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            return _channel.Subscribe(callback);
        }
        internal void Transition(CultMeshSessionState state) { State = state; _states.OnNext(state); }
        internal void ReplacePhysicalChannel(ICultNetSchemaClient channel)
        {
            _channel.Replace(channel);
            Interlocked.Increment(ref _physicalGeneration);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.Dispose();
            _states.Dispose();
        }

        private sealed class ManagedSchemaChannel : ICultNetSchemaClient
        {
            private readonly object _gate = new();
            private readonly Dictionary<Type, IChannelRegistration> _registrations = new();
            private ICultNetSchemaClient _physical;
            private bool _disposed;

            public ManagedSchemaChannel(ICultNetSchemaClient physical) =>
                _physical = physical ?? throw new ArgumentNullException(nameof(physical));

            public bool Connected { get { lock (_gate) return !_disposed && _physical.Connected; } }
            public void Connect(string host, int port)
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    _physical.Connect(host, port);
                }
            }
            public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    _physical.SendCultNet(message);
                }
            }
            public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
            {
                _ = Subscribe(callback);
            }
            public IDisposable Subscribe<T>(Action<T> callback) where T : ICultNetSchemaMessage
            {
                if (callback == null) throw new ArgumentNullException(nameof(callback));
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (!_registrations.TryGetValue(typeof(T), out var existing))
                    {
                        var created = new ChannelRegistration<T>();
                        created.Attach(_physical);
                        _registrations.Add(typeof(T), created);
                        existing = created;
                    }
                    return ((ChannelRegistration<T>)existing).Subscribe(callback);
                }
            }
            public void Replace(ICultNetSchemaClient physical)
            {
                if (physical == null) throw new ArgumentNullException(nameof(physical));
                ICultNetSchemaClient previous;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    previous = _physical;
                    foreach (var registration in _registrations.Values) registration.Attach(physical);
                    _physical = physical;
                }
                previous.Dispose();
            }
            public void Dispose()
            {
                ICultNetSchemaClient physical;
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    physical = _physical;
                    foreach (var registration in _registrations.Values) registration.Dispose();
                    _registrations.Clear();
                }
                physical.Dispose();
            }
            private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ManagedSchemaChannel)); }

            private interface IChannelRegistration : IDisposable { void Attach(ICultNetSchemaClient physical); }
            private sealed class ChannelRegistration<T> : IChannelRegistration where T : ICultNetSchemaMessage
            {
                private readonly object _gate = new();
                private readonly Dictionary<long, Action<T>> _callbacks = new();
                private long _nextId;
                private bool _disposed;

                public IDisposable Subscribe(Action<T> callback)
                {
                    lock (_gate)
                    {
                        if (_disposed) throw new ObjectDisposedException(nameof(ChannelRegistration<T>));
                        var id = ++_nextId;
                        _callbacks.Add(id, callback);
                        return new Subscription(this, id);
                    }
                }

                public void Attach(ICultNetSchemaClient physical) => physical.OnCultNet<T>(Dispatch);

                public void Dispose()
                {
                    lock (_gate)
                    {
                        _disposed = true;
                        _callbacks.Clear();
                    }
                }

                private void Dispatch(T message)
                {
                    Action<T>[] callbacks;
                    lock (_gate) callbacks = _callbacks.Values.ToArray();
                    foreach (var callback in callbacks) callback(message);
                }

                private void Unsubscribe(long id)
                {
                    lock (_gate) _callbacks.Remove(id);
                }

                private sealed class Subscription : IDisposable
                {
                    private ChannelRegistration<T>? _owner;
                    private readonly long _id;

                    public Subscription(ChannelRegistration<T> owner, long id)
                    {
                        _owner = owner;
                        _id = id;
                    }

                    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_id);
                }
            }
        }

        private sealed class SessionSchemaClient : ICultNetSchemaClient
        {
            private readonly CultMeshSession _session;
            private readonly ConcurrentBag<IDisposable> _registrations = new();
            private bool _disposed;
            public SessionSchemaClient(CultMeshSession session) { _session = session; }
            public bool Connected => !_disposed && _session.State.Status == CultMeshSessionStatus.Online;
            public void Connect(string host, int port)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SessionSchemaClient));
                if (!Connected) throw new InvalidOperationException("The managed CultMesh session is not online.");
            }
            public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SessionSchemaClient));
                _session.SendCultNet(message);
            }
            public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SessionSchemaClient));
                _registrations.Add(_session.OnCultNet(callback));
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var registration in _registrations) registration.Dispose();
            }
        }
    }

    public sealed class CultMeshSessionManagerOptions
    {
        public ICultMeshClock Clock { get; set; } = CultMeshSystemClock.Instance;
        public ICultMeshDiagnosticSink Diagnostics { get; set; } = CultMeshNullDiagnosticSink.Instance;
        public int MaxRacedCandidates { get; set; } = 2;
        public string RuntimeId { get; set; } = "cultmesh-client";
        public TimeSpan IdentityHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>Gets or sets the consumer-owned trust roots and explicit local-development policy.</summary>
        public CultMeshAuthorityTrustPolicy Trust { get; set; } = new(
            CultMeshAuthorityTrustMode.AuthenticatedRemote);
    }

    public sealed class CultMeshSessionManager : IDisposable
    {
        private readonly CultMeshDiscoveryService _discovery;
        private readonly ICultMeshTransportConnector[] _connectors;
        private readonly ICultMeshContentTransportConnector[] _contentConnectors;
        private readonly ICultMeshRealtimeTransportConnector[] _realtimeConnectors;
        private readonly CultMeshSessionManagerOptions _options;
        private readonly ConcurrentDictionary<string, Lazy<Task<CultMeshSession>>> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<CultMeshContentSession>>> _contentSessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<CultMeshRealtimeSession>>> _realtimeSessions = new(StringComparer.Ordinal);
        private long _diagnosticSequence;
        private bool _disposed;

        public CultMeshSessionManager(
            CultMeshDiscoveryService discovery,
            IEnumerable<ICultMeshTransportConnector> connectors,
            CultMeshSessionManagerOptions? options = null)
            : this(
                discovery,
                connectors,
                Array.Empty<ICultMeshContentTransportConnector>(),
                Array.Empty<ICultMeshRealtimeTransportConnector>(),
                options)
        {
        }

        public CultMeshSessionManager(
            CultMeshDiscoveryService discovery,
            IEnumerable<ICultMeshTransportConnector> connectors,
            IEnumerable<ICultMeshContentTransportConnector> contentConnectors,
            CultMeshSessionManagerOptions? options = null)
            : this(
                discovery,
                connectors,
                contentConnectors,
                Array.Empty<ICultMeshRealtimeTransportConnector>(),
                options)
        {
        }

        public CultMeshSessionManager(
            CultMeshDiscoveryService discovery,
            IEnumerable<ICultMeshTransportConnector> connectors,
            IEnumerable<ICultMeshContentTransportConnector> contentConnectors,
            IEnumerable<ICultMeshRealtimeTransportConnector> realtimeConnectors,
            CultMeshSessionManagerOptions? options = null)
        {
            _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
            _connectors = connectors?.ToArray() ?? throw new ArgumentNullException(nameof(connectors));
            _contentConnectors = contentConnectors?.ToArray() ?? throw new ArgumentNullException(nameof(contentConnectors));
            _realtimeConnectors = realtimeConnectors?.ToArray() ?? throw new ArgumentNullException(nameof(realtimeConnectors));
            if (_contentConnectors.Any(connector => connector == null))
                throw new ArgumentException("Content transport connectors cannot contain null entries.", nameof(contentConnectors));
            if (_realtimeConnectors.Any(connector => connector == null))
                throw new ArgumentException("Realtime transport connectors cannot contain null entries.", nameof(realtimeConnectors));
            _options = options ?? new CultMeshSessionManagerOptions();
            if (_options.MaxRacedCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.RuntimeId)) throw new ArgumentException("Client runtime identity is required.", nameof(options));
            if (_options.IdentityHandshakeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        }

        public async Task<CultMeshSession> ConnectAsync(
            CultMeshSessionTarget target,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (protocol == null) throw new ArgumentNullException(nameof(protocol));
            ThrowIfDisposed();
            var key = target.SessionKey + "\u001f" + protocol.Value;
            while (true)
            {
                var lazy = _sessions.GetOrAdd(key, _ => new Lazy<Task<CultMeshSession>>(
                    () => ConnectOwnedAsync(key, target, protocol), LazyThreadSafetyMode.ExecutionAndPublication));
                try
                {
                    var session = await AwaitForCallerAsync(lazy.Value, cancellationToken).ConfigureAwait(false);
                    if (session.State.Status != CultMeshSessionStatus.Offline) return session;
                    _sessions.TryRemove(key, out _);
                }
                catch
                {
                    _sessions.TryRemove(key, out _);
                    throw;
                }
            }
        }

        /// <summary>
        /// Connects one reusable streaming content plane by stable Verse/provider identity.
        /// Preferred connector tiers are exhausted before legacy fallback tiers are attempted.
        /// </summary>
        public async Task<CultMeshContentSession> ConnectContentAsync(
            CultMeshSessionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ThrowIfDisposed();
            var key = target.SessionKey;
            while (true)
            {
                var lazy = _contentSessions.GetOrAdd(key, _ => new Lazy<Task<CultMeshContentSession>>(
                    () => ConnectContentOwnedAsync(key, target), LazyThreadSafetyMode.ExecutionAndPublication));
                try
                {
                    var session = await AwaitForCallerAsync(lazy.Value, cancellationToken).ConfigureAwait(false);
                    if (session.State.Status != CultMeshSessionStatus.Offline) return session;
                    _contentSessions.TryRemove(key, out _);
                }
                catch
                {
                    _contentSessions.TryRemove(key, out _);
                    throw;
                }
            }
        }

        private async Task<CultMeshContentSession> ConnectContentOwnedAsync(
            string key,
            CultMeshSessionTarget target)
        {
            var result = await ResolveContentPathAsync(target).ConfigureAwait(false);
            var session = new CultMeshContentSession(
                target,
                result.Transport,
                new CultMeshSessionState(CultMeshSessionStatus.Online, _options.Clock.UtcNow, result.Candidate),
                failed => InvalidateContentSession(key, failed));
            Emit(target, CultMeshProtocols.Content, "online", result.Candidate.Endpoint);
            return session;
        }

        private async Task<ConnectedContentPath> ResolveContentPathAsync(CultMeshSessionTarget target)
        {
            if (_contentConnectors.Length == 0)
                throw Failure(
                    CultMeshSessionFailureReason.UnsupportedPath,
                    "No streaming content connectors are configured. Register the TCP content connector or the explicit legacy RUDP connector.");

            var discovery = await _discovery.ResolveAsync(new CultMeshDiscoveryQuery(
                target.AuthorityRuntimeId,
                new[] { target.VerseId },
                target.AuthorityRuntimeId)).ConfigureAwait(false);
            var candidates = BoundCandidates(discovery, target, CultMeshProtocols.Content);
            var routes = candidates
                .SelectMany(candidate => _contentConnectors
                    .Where(connector => connector.CanConnect(candidate))
                    .Select(connector => new ContentRoute(candidate, connector)))
                .OrderBy(route => route.Connector.Priority)
                .ThenBy(route => route.Candidate.Priority)
                .ToArray();
            if (routes.Length == 0)
                throw Failure(
                    CultMeshSessionFailureReason.UnsupportedPath,
                    $"No streaming content connector supports an advertised route for '{target}'.");

            var failures = new List<Exception>();
            foreach (var tier in routes.GroupBy(route => route.Connector.Priority).OrderBy(group => group.Key))
            {
                var attempts = tier.Take(_options.MaxRacedCandidates)
                    .Select(route => ConnectContentCandidateAsync(target, route))
                    .ToList();
                while (attempts.Count > 0)
                {
                    var completed = await Task.WhenAny(attempts).ConfigureAwait(false);
                    attempts.Remove(completed);
                    try
                    {
                        var result = await completed.ConfigureAwait(false);
                        foreach (var loser in attempts)
                            _ = loser.ContinueWith(task =>
                            {
                                if (task.Status == TaskStatus.RanToCompletion) task.Result.Transport.Dispose();
                            }, TaskScheduler.Default);
                        return result;
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }
            }

            var last = failures.LastOrDefault();
            var reason = last is CultMeshSessionException typed
                ? typed.Failure.Reason
                : last is TimeoutException
                    ? CultMeshSessionFailureReason.Timeout
                    : CultMeshSessionFailureReason.Transport;
            throw Failure(
                reason,
                $"No streaming content path connected for '{target}'.",
                last);
        }

        private static async Task<ConnectedContentPath> ConnectContentCandidateAsync(
            CultMeshSessionTarget target,
            ContentRoute route)
        {
            var transport = await route.Connector.ConnectAsync(route.Candidate, target).ConfigureAwait(false);
            try
            {
                CultMeshTransportIdentity.RequireVerified(
                    transport, target, CultMeshProtocols.Content, route.Candidate);
                return new ConnectedContentPath(route.Candidate, transport);
            }
            catch
            {
                transport.Dispose();
                throw;
            }
        }

        private void InvalidateContentSession(string key, CultMeshContentSession session)
        {
            session.MarkOffline(new CultMeshSessionState(
                CultMeshSessionStatus.Offline,
                _options.Clock.UtcNow,
                session.State.Path,
                new CultMeshSessionFailure(
                    CultMeshSessionFailureReason.Transport,
                    "The selected streaming content transport failed.",
                    session.State.Path?.Endpoint ?? string.Empty)));
            if (_contentSessions.TryRemove(key, out var removed) &&
                removed.IsValueCreated && removed.Value.Status == TaskStatus.RanToCompletion)
                removed.Value.Result.Dispose();
            Emit(session.Target, CultMeshProtocols.Content, "offline", session.State.Path?.Endpoint ?? string.Empty);
        }

        /// <summary>
        /// Connects the realtime state plane. No connector is implied: promoted runtimes
        /// register QUIC explicitly, while RUDP compatibility remains opt-in.
        /// </summary>
        public async Task<CultMeshRealtimeSession> ConnectRealtimeAsync(
            CultMeshSessionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ThrowIfDisposed();
            var key = target.SessionKey;
            while (true)
            {
                var lazy = _realtimeSessions.GetOrAdd(key, _ => new Lazy<Task<CultMeshRealtimeSession>>(
                    () => ConnectRealtimeOwnedAsync(key, target), LazyThreadSafetyMode.ExecutionAndPublication));
                try
                {
                    var session = await AwaitForCallerAsync(lazy.Value, cancellationToken).ConfigureAwait(false);
                    if (session.State.Status != CultMeshSessionStatus.Offline) return session;
                    _realtimeSessions.TryRemove(key, out _);
                }
                catch
                {
                    _realtimeSessions.TryRemove(key, out _);
                    throw;
                }
            }
        }

        private async Task<CultMeshRealtimeSession> ConnectRealtimeOwnedAsync(
            string key,
            CultMeshSessionTarget target)
        {
            var result = await ResolveRealtimePathAsync(target).ConfigureAwait(false);
            var session = new CultMeshRealtimeSession(
                target,
                result.Transport,
                new CultMeshSessionState(CultMeshSessionStatus.Online, _options.Clock.UtcNow, result.Candidate),
                failed => InvalidateRealtimeSession(key, failed));
            Emit(target, CultMeshProtocols.RealtimeState, "online", result.Candidate.Endpoint);
            return session;
        }

        private async Task<ConnectedRealtimePath> ResolveRealtimePathAsync(CultMeshSessionTarget target)
        {
            if (_realtimeConnectors.Length == 0)
                throw Failure(
                    CultMeshSessionFailureReason.UnsupportedPath,
                    "No realtime state connectors are configured. Register a QUIC connector explicitly.");

            var discovery = await _discovery.ResolveAsync(new CultMeshDiscoveryQuery(
                target.AuthorityRuntimeId,
                new[] { target.VerseId },
                target.AuthorityRuntimeId)).ConfigureAwait(false);
            var routes = BoundCandidates(discovery, target, CultMeshProtocols.RealtimeState)
                .SelectMany(candidate => _realtimeConnectors
                    .Where(connector => connector.CanConnect(candidate))
                    .Select(connector => new RealtimeRoute(candidate, connector)))
                .OrderBy(route => route.Connector.Priority)
                .ThenBy(route => route.Candidate.Priority)
                .ToArray();
            if (routes.Length == 0)
                throw Failure(
                    CultMeshSessionFailureReason.UnsupportedPath,
                    $"No realtime connector supports an advertised route for '{target}'.");

            var failures = new List<Exception>();
            foreach (var tier in routes.GroupBy(route => route.Connector.Priority).OrderBy(group => group.Key))
            {
                var attempts = tier.Take(_options.MaxRacedCandidates)
                    .Select(route => ConnectRealtimeCandidateAsync(target, route))
                    .ToList();
                while (attempts.Count > 0)
                {
                    var completed = await Task.WhenAny(attempts).ConfigureAwait(false);
                    attempts.Remove(completed);
                    try
                    {
                        var result = await completed.ConfigureAwait(false);
                        foreach (var loser in attempts)
                            _ = loser.ContinueWith(task =>
                            {
                                if (task.Status == TaskStatus.RanToCompletion) task.Result.Transport.Dispose();
                            }, TaskScheduler.Default);
                        return result;
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }
            }

            var last = failures.LastOrDefault();
            var reason = last is CultMeshSessionException typed
                ? typed.Failure.Reason
                : last is TimeoutException
                    ? CultMeshSessionFailureReason.Timeout
                    : CultMeshSessionFailureReason.Transport;
            throw Failure(
                reason,
                $"No realtime state path connected for '{target}'.",
                last);
        }

        private static async Task<ConnectedRealtimePath> ConnectRealtimeCandidateAsync(
            CultMeshSessionTarget target,
            RealtimeRoute route)
        {
            var transport = await route.Connector.ConnectAsync(route.Candidate, target).ConfigureAwait(false);
            try
            {
                CultMeshTransportIdentity.RequireVerified(
                    transport, target, CultMeshProtocols.RealtimeState, route.Candidate);
                return new ConnectedRealtimePath(route.Candidate, transport);
            }
            catch
            {
                transport.Dispose();
                throw;
            }
        }

        private void InvalidateRealtimeSession(string key, CultMeshRealtimeSession session)
        {
            var endpoint = session.State.Path?.Endpoint ?? string.Empty;
            session.MarkOffline(new CultMeshSessionState(
                CultMeshSessionStatus.Offline,
                _options.Clock.UtcNow,
                session.State.Path,
                new CultMeshSessionFailure(
                    CultMeshSessionFailureReason.Transport,
                    "The selected realtime state transport failed.",
                    endpoint)));
            if (_realtimeSessions.TryRemove(key, out var removed) &&
                removed.IsValueCreated && removed.Value.Status == TaskStatus.RanToCompletion)
                removed.Value.Result.Dispose();
            Emit(session.Target, CultMeshProtocols.RealtimeState, "offline", endpoint);
        }

        private async Task<CultMeshSession> ConnectOwnedAsync(string key, CultMeshSessionTarget target, CultMeshProtocolId protocol)
        {
            var result = await ResolveConnectedPathAsync(target, protocol).ConfigureAwait(false);
            var session = new CultMeshSession(target, protocol, result.Client,
                new CultMeshSessionState(CultMeshSessionStatus.Online, _options.Clock.UtcNow, result.Candidate));
            ObservePhysicalFailure(key, session, result);
            Emit(target, protocol, "online", result.Candidate.Endpoint);
            return session;
        }

        private async Task<ConnectedPath> ResolveConnectedPathAsync(CultMeshSessionTarget target, CultMeshProtocolId protocol)
        {
            var discovery = await _discovery.ResolveAsync(new CultMeshDiscoveryQuery(
                target.AuthorityRuntimeId,
                new[] { target.VerseId },
                target.AuthorityRuntimeId)).ConfigureAwait(false);
            var candidates = BoundCandidates(discovery, target, protocol);
            var routes = candidates
                .SelectMany(candidate => _connectors
                    .Where(connector => connector.CanConnect(candidate))
                    .Select(connector => new SchemaRoute(candidate, connector)))
                .OrderBy(route => route.Connector.Priority)
                .ThenBy(route => route.Candidate.Priority)
                .ToArray();
            if (candidates.Length == 0)
                throw Failure(CultMeshSessionFailureReason.Resolution, $"No route candidates for '{target}'.");
            if (routes.Length == 0)
                throw Failure(
                    CultMeshSessionFailureReason.Transport,
                    $"No transport path connected for '{target}' and protocol '{protocol}'.",
                    Failure(
                        CultMeshSessionFailureReason.UnsupportedPath,
                        $"No registered connector supports an advertised route for '{target}'."));

            var failures = new List<Exception>();
            foreach (var tier in routes.GroupBy(route => route.Connector.Priority).OrderBy(group => group.Key))
            {
                var attempts = tier.Take(_options.MaxRacedCandidates)
                    .Select(route => ConnectCandidateAsync(target, route, protocol))
                    .ToList();
                while (attempts.Count > 0)
                {
                    var completed = await Task.WhenAny(attempts).ConfigureAwait(false);
                    attempts.Remove(completed);
                    try
                    {
                        var result = await completed.ConfigureAwait(false);
                        foreach (var loser in attempts)
                            _ = loser.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Client.Dispose(); }, TaskScheduler.Default);
                        return result;
                    }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            var last = failures.LastOrDefault();
            var reason = last is CultMeshSessionException typed
                ? typed.Failure.Reason
                : last is TimeoutException
                    ? CultMeshSessionFailureReason.Timeout
                    : CultMeshSessionFailureReason.Transport;
            throw Failure(reason,
                $"No transport path connected for '{target}' and protocol '{protocol}'.", last);
        }

        private void ObservePhysicalFailure(string key, CultMeshSession session, ConnectedPath path)
        {
            if (path.Client is not ICultNetSchemaClientHealth health) return;
            _ = health.BackgroundFailure.ContinueWith(
                task => ReconnectOwnedAsync(key, session, path, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }

        private async Task ReconnectOwnedAsync(
            string key,
            CultMeshSession session,
            ConnectedPath failedPath,
            Task<Exception> failureTask)
        {
            if (_disposed || session.State.Status == CultMeshSessionStatus.Offline) return;
            var error = failureTask.Status == TaskStatus.RanToCompletion ? failureTask.Result : failureTask.Exception;
            var failure = new CultMeshSessionFailure(
                CultMeshSessionFailureReason.Transport,
                error?.Message ?? "Transport background loop stopped.",
                failedPath.Candidate.Endpoint);
            session.Transition(new CultMeshSessionState(
                CultMeshSessionStatus.Reconnecting,
                _options.Clock.UtcNow,
                failedPath.Candidate,
                failure));
            Emit(session.Target, session.Protocol, "reconnecting", failedPath.Candidate.Endpoint);
            try
            {
                _discovery.Invalidate(new CultMeshDiscoveryQuery(
                    session.Target.AuthorityRuntimeId,
                    new[] { session.Target.VerseId },
                    session.Target.AuthorityRuntimeId));
                var replacement = await ResolveConnectedPathAsync(session.Target, session.Protocol).ConfigureAwait(false);
                session.ReplacePhysicalChannel(replacement.Client);
                session.Transition(new CultMeshSessionState(
                    CultMeshSessionStatus.Online,
                    _options.Clock.UtcNow,
                    replacement.Candidate));
                ObservePhysicalFailure(key, session, replacement);
                Emit(session.Target, session.Protocol, "online", replacement.Candidate.Endpoint);
            }
            catch (Exception reconnectError)
            {
                session.Transition(new CultMeshSessionState(
                    CultMeshSessionStatus.Offline,
                    _options.Clock.UtcNow,
                    failedPath.Candidate,
                    new CultMeshSessionFailure(
                        reconnectError is CultMeshSessionException typed ? typed.Failure.Reason : CultMeshSessionFailureReason.Transport,
                        reconnectError.Message,
                        failedPath.Candidate.Endpoint)));
                _sessions.TryRemove(key, out _);
                Emit(session.Target, session.Protocol, "offline", failedPath.Candidate.Endpoint);
            }
        }

        private async Task<ConnectedPath> ConnectCandidateAsync(
            CultMeshSessionTarget target,
            SchemaRoute route,
            CultMeshProtocolId protocol)
        {
            var client = await route.Connector.ConnectAsync(route.Candidate, protocol).ConfigureAwait(false);
            try
            {
                await CultMeshSessionIdentityClient.VerifyAsync(
                    client,
                    _options.RuntimeId,
                    target,
                    protocol,
                    route.Candidate,
                    _options.Trust,
                    _options.Clock.UtcNow,
                    _options.IdentityHandshakeTimeout).ConfigureAwait(false);
                return new ConnectedPath(route.Candidate, client);
            }
            catch
            {
                client.Dispose();
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

        private CultMeshSessionException Failure(CultMeshSessionFailureReason reason, string message, Exception? inner = null) =>
            new CultMeshSessionException(new CultMeshSessionFailure(reason, message), inner);

        private CultMeshTransportCandidate[] BoundCandidates(
            CultMeshDiscoveryState discovery,
            CultMeshSessionTarget target,
            CultMeshProtocolId protocol)
        {
            var routes = discovery.Candidates
                .SelectMany(candidate => candidate.Descriptor.AuthorityRoutes)
                .Where(route => string.Equals(
                    route.AuthorityRuntimeId,
                    target.AuthorityRuntimeId,
                    StringComparison.Ordinal))
                .Where(route => route.Supports(protocol))
                .ToArray();
            var verified = new List<CultMeshTransportCandidate>();
            var rejected = new List<Exception>();
            foreach (var route in routes)
            {
                try
                {
                    verified.Add(new CultMeshTransportCandidate(
                        route.Endpoint,
                        priority: route.Priority,
                        authorityRuntimeId: route.AuthorityRuntimeId,
                        generation: route.Generation,
                        authorityRoute: route)
                    .WithVerifiedAuthority(_options.Trust.Verify(
                        target.VerseId,
                        route,
                        _options.Clock.UtcNow)));
                }
                catch (CultMeshSessionException error)
                {
                    rejected.Add(error);
                }
            }
            if (verified.Count == 0 && rejected.Count > 0)
            {
                throw Failure(
                    CultMeshSessionFailureReason.Authentication,
                    $"No trusted route remained for CultMesh target '{target}' and protocol '{protocol}'.",
                    new AggregateException(rejected));
            }
            return verified
                .GroupBy(candidate => candidate.Endpoint, StringComparer.Ordinal)
                .Select(group => group.OrderBy(candidate => candidate.Priority).First())
                .OrderBy(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Endpoint, StringComparer.Ordinal)
                .ToArray();
        }

        private void Emit(CultMeshSessionTarget target, CultMeshProtocolId protocol, string state, string path)
        {
            _options.Diagnostics.Emit(new CultMeshDiagnosticEvent(
                Interlocked.Increment(ref _diagnosticSequence), _options.Clock.UtcNow,
                CultMeshReliabilityOrgan.Session, CultMeshDiagnosticKind.PathChanged,
                target.SessionKey + ":" + protocol.Value, target.AuthorityRuntimeId, state,
                sourceId: "session-manager", endpoint: path));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lazy in _sessions.Values)
                if (lazy.IsValueCreated && lazy.Value.Status == TaskStatus.RanToCompletion) lazy.Value.Result.Dispose();
            _sessions.Clear();
            foreach (var lazy in _contentSessions.Values)
                if (lazy.IsValueCreated && lazy.Value.Status == TaskStatus.RanToCompletion) lazy.Value.Result.Dispose();
            _contentSessions.Clear();
            foreach (var lazy in _realtimeSessions.Values)
                if (lazy.IsValueCreated && lazy.Value.Status == TaskStatus.RanToCompletion) lazy.Value.Result.Dispose();
            _realtimeSessions.Clear();
        }

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CultMeshSessionManager)); }

        private sealed class ConnectedPath
        {
            public ConnectedPath(CultMeshTransportCandidate candidate, ICultNetSchemaClient client) { Candidate = candidate; Client = client; }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultNetSchemaClient Client { get; }
        }

        private sealed class ConnectedContentPath
        {
            public ConnectedContentPath(CultMeshTransportCandidate candidate, ICultMeshContentTransport transport)
            {
                Candidate = candidate;
                Transport = transport;
            }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultMeshContentTransport Transport { get; }
        }

        private sealed class SchemaRoute
        {
            public SchemaRoute(CultMeshTransportCandidate candidate, ICultMeshTransportConnector connector)
            {
                Candidate = candidate;
                Connector = connector;
            }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultMeshTransportConnector Connector { get; }
        }

        private sealed class ContentRoute
        {
            public ContentRoute(CultMeshTransportCandidate candidate, ICultMeshContentTransportConnector connector)
            {
                Candidate = candidate;
                Connector = connector;
            }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultMeshContentTransportConnector Connector { get; }
        }

        private sealed class ConnectedRealtimePath
        {
            public ConnectedRealtimePath(CultMeshTransportCandidate candidate, ICultMeshRealtimeTransport transport)
            {
                Candidate = candidate;
                Transport = transport;
            }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultMeshRealtimeTransport Transport { get; }
        }

        private sealed class RealtimeRoute
        {
            public RealtimeRoute(CultMeshTransportCandidate candidate, ICultMeshRealtimeTransportConnector connector)
            {
                Candidate = candidate;
                Connector = connector;
            }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultMeshRealtimeTransportConnector Connector { get; }
        }
    }
}
