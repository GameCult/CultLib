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
    public sealed class CultMeshEndpointId : IEquatable<CultMeshEndpointId>
    {
        private CultMeshEndpointId(string value) { Value = value; }
        public string Value { get; }
        public static CultMeshEndpointId Parse(string value) => new CultMeshEndpointId(
            string.IsNullOrWhiteSpace(value) ? throw new FormatException("CultMesh endpoint identity must be non-empty.") : value.Trim());
        public bool Equals(CultMeshEndpointId? other) => other != null && string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => Equals(obj as CultMeshEndpointId);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
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
        public static CultMeshProtocolId Discovery { get; } = new CultMeshProtocolId("cultmesh.discovery.v1");
        public static CultMeshProtocolId PeerExchange { get; } = new CultMeshProtocolId("cultmesh.peer_exchange.v1");
        public static CultMeshProtocolId Subscriptions { get; } = new CultMeshProtocolId("cultmesh.subscriptions.v1");
    }

    public enum CultMeshTransportPathKind { Direct, Relay, Tunnel }

    public sealed class CultMeshTransportCandidate
    {
        public CultMeshTransportCandidate(string endpoint, CultMeshTransportPathKind pathKind = CultMeshTransportPathKind.Direct, int priority = 0)
        {
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? throw new ArgumentException("Physical endpoint is required.", nameof(endpoint)) : endpoint;
            PathKind = pathKind;
            Priority = priority;
        }
        public string Endpoint { get; }
        public CultMeshTransportPathKind PathKind { get; }
        public int Priority { get; }
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
        bool CanConnect(CultMeshTransportCandidate candidate);
        Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default);
    }

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

        public string ConnectorId => "cultnet-schema";
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

    public sealed class CultMeshSession : IDisposable
    {
        private readonly Subject<CultMeshSessionState> _states = new();
        private readonly ConcurrentBag<IDisposable> _subscriptions = new();
        private readonly ManagedSchemaChannel _channel;
        private bool _disposed;
        internal CultMeshSession(CultMeshEndpointId endpointId, CultMeshProtocolId protocol, ICultNetSchemaClient channel, CultMeshSessionState state)
        {
            EndpointId = endpointId;
            Protocol = protocol;
            _channel = new ManagedSchemaChannel(channel);
            State = state;
        }
        public CultMeshEndpointId EndpointId { get; }
        public CultMeshProtocolId Protocol { get; }
        internal ICultNetSchemaClient Channel => _channel;
        public CultMeshSessionState State { get; private set; }
        public Observable<CultMeshSessionState> WatchState() => _states;
        public ICultNetSchemaClient OpenSchemaClient() => new SessionSchemaClient(this);
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage => Channel.SendCultNet(message);
        public IDisposable OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var subscription = new SessionSubscription<T>(callback);
            Channel.OnCultNet<T>(subscription.Invoke);
            _subscriptions.Add(subscription);
            return subscription;
        }
        internal void Transition(CultMeshSessionState state) { State = state; _states.OnNext(state); }
        internal void ReplacePhysicalChannel(ICultNetSchemaClient channel) => _channel.Replace(channel);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _channel.Dispose();
            _states.Dispose();
        }

        private sealed class ManagedSchemaChannel : ICultNetSchemaClient
        {
            private readonly object _gate = new();
            private readonly List<IChannelRegistration> _registrations = new();
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
                if (callback == null) throw new ArgumentNullException(nameof(callback));
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var registration = new ChannelRegistration<T>(callback);
                    registration.Attach(_physical);
                    _registrations.Add(registration);
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
                    foreach (var registration in _registrations) registration.Attach(physical);
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
                    _registrations.Clear();
                }
                physical.Dispose();
            }
            private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ManagedSchemaChannel)); }

            private interface IChannelRegistration { void Attach(ICultNetSchemaClient physical); }
            private sealed class ChannelRegistration<T> : IChannelRegistration where T : ICultNetSchemaMessage
            {
                private readonly Action<T> _callback;
                public ChannelRegistration(Action<T> callback) => _callback = callback;
                public void Attach(ICultNetSchemaClient physical) => physical.OnCultNet(_callback);
            }
        }

        private sealed class SessionSubscription<T> : IDisposable where T : ICultNetSchemaMessage
        {
            private Action<T>? _callback;
            public SessionSubscription(Action<T> callback) { _callback = callback; }
            public void Invoke(T message) => Volatile.Read(ref _callback)?.Invoke(message);
            public void Dispose() => Interlocked.Exchange(ref _callback, null);
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
    }

    public sealed class CultMeshSessionManager : IDisposable
    {
        private readonly CultMeshDiscoveryService _discovery;
        private readonly ICultMeshTransportConnector[] _connectors;
        private readonly CultMeshSessionManagerOptions _options;
        private readonly ConcurrentDictionary<string, Lazy<Task<CultMeshSession>>> _sessions = new(StringComparer.Ordinal);
        private long _diagnosticSequence;
        private bool _disposed;

        public CultMeshSessionManager(
            CultMeshDiscoveryService discovery,
            IEnumerable<ICultMeshTransportConnector> connectors,
            CultMeshSessionManagerOptions? options = null)
        {
            _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
            _connectors = connectors?.ToArray() ?? throw new ArgumentNullException(nameof(connectors));
            _options = options ?? new CultMeshSessionManagerOptions();
            if (_options.MaxRacedCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        }

        public async Task<CultMeshSession> ConnectAsync(
            CultMeshEndpointId endpointId,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            if (endpointId == null) throw new ArgumentNullException(nameof(endpointId));
            if (protocol == null) throw new ArgumentNullException(nameof(protocol));
            ThrowIfDisposed();
            var key = endpointId.Value + "\u001f" + protocol.Value;
            while (true)
            {
                var lazy = _sessions.GetOrAdd(key, _ => new Lazy<Task<CultMeshSession>>(
                    () => ConnectOwnedAsync(key, endpointId, protocol), LazyThreadSafetyMode.ExecutionAndPublication));
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

        private async Task<CultMeshSession> ConnectOwnedAsync(string key, CultMeshEndpointId endpointId, CultMeshProtocolId protocol)
        {
            var result = await ResolveConnectedPathAsync(endpointId, protocol).ConfigureAwait(false);
            var session = new CultMeshSession(endpointId, protocol, result.Client,
                new CultMeshSessionState(CultMeshSessionStatus.Online, _options.Clock.UtcNow, result.Candidate));
            ObservePhysicalFailure(key, session, result);
            Emit(endpointId, protocol, "online", result.Candidate.Endpoint);
            return session;
        }

        private async Task<ConnectedPath> ResolveConnectedPathAsync(CultMeshEndpointId endpointId, CultMeshProtocolId protocol)
        {
            var discovery = await _discovery.ResolveAsync(new CultMeshDiscoveryQuery(endpointId.Value)).ConfigureAwait(false);
            var candidates = discovery.Candidates.SelectMany(candidate => candidate.Descriptor.DiscoveryEndpoints)
                .Distinct(StringComparer.Ordinal)
                .Select(endpoint => new CultMeshTransportCandidate(endpoint))
                .OrderBy(candidate => candidate.Priority)
                .Take(_options.MaxRacedCandidates)
                .ToArray();
            if (candidates.Length == 0)
                throw Failure(CultMeshSessionFailureReason.Resolution, $"No route candidates for '{endpointId}'.");

            var attempts = candidates.Select(candidate => ConnectCandidateAsync(candidate, protocol)).ToList();
            var failures = new List<Exception>();
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
            var last = failures.LastOrDefault();
            throw Failure(last is TimeoutException ? CultMeshSessionFailureReason.Timeout : CultMeshSessionFailureReason.Transport,
                $"No transport path connected for '{endpointId}' and protocol '{protocol}'.", last);
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
            Emit(session.EndpointId, session.Protocol, "reconnecting", failedPath.Candidate.Endpoint);
            try
            {
                var replacement = await ResolveConnectedPathAsync(session.EndpointId, session.Protocol).ConfigureAwait(false);
                session.ReplacePhysicalChannel(replacement.Client);
                session.Transition(new CultMeshSessionState(
                    CultMeshSessionStatus.Online,
                    _options.Clock.UtcNow,
                    replacement.Candidate));
                ObservePhysicalFailure(key, session, replacement);
                Emit(session.EndpointId, session.Protocol, "online", replacement.Candidate.Endpoint);
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
                Emit(session.EndpointId, session.Protocol, "offline", failedPath.Candidate.Endpoint);
            }
        }

        private async Task<ConnectedPath> ConnectCandidateAsync(CultMeshTransportCandidate candidate, CultMeshProtocolId protocol)
        {
            var connector = _connectors.FirstOrDefault(item => item.CanConnect(candidate));
            if (connector == null) throw Failure(CultMeshSessionFailureReason.UnsupportedPath, $"No connector supports '{candidate.Endpoint}'.");
            var client = await connector.ConnectAsync(candidate, protocol).ConfigureAwait(false);
            return new ConnectedPath(candidate, client);
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

        private void Emit(CultMeshEndpointId endpoint, CultMeshProtocolId protocol, string state, string path)
        {
            _options.Diagnostics.Emit(new CultMeshDiagnosticEvent(
                Interlocked.Increment(ref _diagnosticSequence), _options.Clock.UtcNow,
                CultMeshReliabilityOrgan.Session, CultMeshDiagnosticKind.PathChanged,
                endpoint.Value + ":" + protocol.Value, endpoint.Value, state,
                sourceId: "session-manager", endpoint: path));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lazy in _sessions.Values)
                if (lazy.IsValueCreated && lazy.Value.Status == TaskStatus.RanToCompletion) lazy.Value.Result.Dispose();
            _sessions.Clear();
        }

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CultMeshSessionManager)); }

        private sealed class ConnectedPath
        {
            public ConnectedPath(CultMeshTransportCandidate candidate, ICultNetSchemaClient client) { Candidate = candidate; Client = client; }
            public CultMeshTransportCandidate Candidate { get; }
            public ICultNetSchemaClient Client { get; }
        }
    }
}
