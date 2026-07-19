using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Networking;

#nullable enable

namespace GameCult.Mesh
{
    /// <summary>
    /// Declares one logical body demand. CultMesh owns locality detection and transport selection;
    /// consumers name the body and the planes they can actually read.
    /// </summary>
    public sealed class CultMeshLiveBodySubscription
    {
        public CultMeshLiveBodySubscription(
            string subscriptionId,
            string consumerRuntimeId,
            string bodyId)
        {
            SubscriptionId = Require(subscriptionId, nameof(subscriptionId));
            ConsumerRuntimeId = Require(consumerRuntimeId, nameof(consumerRuntimeId));
            BodyId = Require(bodyId, nameof(bodyId));
        }

        public string SubscriptionId { get; }
        public string ConsumerRuntimeId { get; }
        public string BodyId { get; }
        public string PublicationRecordKey =>
            CultMeshBodyPublicationDocument.CreateLatestRecordKey(BodyId).Value;

        private static string Require(string value, string parameterName) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value is required.", parameterName)
                : value;
    }

    /// <summary>
    /// One ephemeral body subscription. Publication metadata remains reactive control state while
    /// body bytes stay on the negotiated body plane.
    /// </summary>
    public sealed class CultMeshLiveBody : IDisposable
    {
        private readonly CultNetDatabaseSubscriptionClient _owner;
        private readonly CultMeshBodyPublicationResolver _resolver;
        private readonly CultMeshLiveBodySubscription _subscription;
        private readonly object _gate = new();
        private CultMeshBodyPublicationDocument? _current;
        private bool _hasValue;
        private bool _disposed;

        internal CultMeshLiveBody(
            CultNetDatabaseSubscriptionClient owner,
            CultMeshBodyPublicationResolver resolver,
            CultMeshLiveBodySubscription subscription)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
            _owner.Changed += OnChanged;
        }

        /// <summary>Raised when the provider publishes a new generation descriptor.</summary>
        public event Action<CultMeshBodyPublicationDocument>? Changed;

        /// <summary>Raised when the provider withdraws the logical body.</summary>
        public event Action? Removed;

        public string BodyId => _subscription.BodyId;

        public bool HasValue
        {
            get { lock (_gate) return _hasValue; }
        }

        public CultMeshBodyPublicationDocument Current
        {
            get
            {
                lock (_gate)
                    return _hasValue
                        ? _current!
                        : throw new InvalidOperationException(
                            $"Live body '{_subscription.BodyId}' is not currently published.");
            }
        }

        /// <summary>
        /// Opens the current generation read-only using the fastest valid advertised representation.
        /// The caller owns the returned lease and should dispose it before opening a later generation.
        /// </summary>
        public CultMeshBodyNegotiationResult OpenCurrentReadOnly(DateTimeOffset? nowUtc = null)
        {
            CultMeshBodyPublicationDocument publication;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CultMeshLiveBody));
                if (!_hasValue)
                    throw new InvalidOperationException(
                        $"Live body '{_subscription.BodyId}' is not currently published.");
                publication = _current!;
            }

            return _resolver.NegotiateReadOnly(publication, new CultMeshBodyValidationRequest
            {
                BodyId = publication.BodyId,
                SchemaId = publication.SchemaId,
                LayoutVersion = publication.LayoutVersion,
                ProducerEpoch = publication.ProducerEpoch,
                Sequence = publication.Sequence,
                Capacity = publication.Capacity,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                NowUtc = nowUtc ?? DateTimeOffset.UtcNow
            });
        }

        internal void Initialize(CultMeshBodyPublicationDocument publication)
        {
            CultMeshBodyPublicationValidator.Validate(publication, expectedBodyId: _subscription.BodyId);
            lock (_gate)
            {
                if (_disposed || _hasValue) return;
                _current = publication;
                _hasValue = true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _current = null;
                _hasValue = false;
            }
            _owner.Changed -= OnChanged;
            _owner.Unsubscribe(_subscription.SubscriptionId);
        }

        private void OnChanged(CultNetReplicatedDocumentChange change)
        {
            if (!string.Equals(change.SubscriptionId, _subscription.SubscriptionId, StringComparison.Ordinal) ||
                !string.Equals(change.RecordKey, _subscription.PublicationRecordKey, StringComparison.Ordinal))
                return;

            if (string.Equals(change.ChangeKind, "removed", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _current = null;
                    _hasValue = false;
                }
                Removed?.Invoke();
                return;
            }

            if (change.Document is not CultMeshBodyPublicationDocument publication)
                return;
            CultMeshBodyPublicationValidator.Validate(publication, expectedBodyId: _subscription.BodyId);
            lock (_gate)
            {
                if (_disposed) return;
                _current = publication;
                _hasValue = true;
            }
            Changed?.Invoke(publication);
        }
    }

    /// <summary>
    /// One exact hot-body subscription. The view record, latest-publication record, consumer body
    /// demand, and locally supported planes are one contract so callers cannot accidentally stream
    /// body bytes through retained document snapshots.
    /// </summary>
    public sealed class CultMeshHotBodySubscription
    {
        public CultMeshHotBodySubscription(
            string subscriptionId,
            string consumerRuntimeId,
            string viewRecordKey,
            string viewSchemaId,
            string bodyId,
            IEnumerable<string>? additionalRecordKeys = null,
            IEnumerable<string>? additionalSchemaIds = null)
        {
            SubscriptionId = Require(subscriptionId, nameof(subscriptionId));
            ConsumerRuntimeId = Require(consumerRuntimeId, nameof(consumerRuntimeId));
            BodyId = Require(bodyId, nameof(bodyId));
            RecordKeys = new[]
                {
                    Require(viewRecordKey, nameof(viewRecordKey)),
                    CultMeshBodyPublicationDocument.CreateLatestRecordKey(bodyId).Value
                }
                .Concat(additionalRecordKeys ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            SchemaIds = new[]
                {
                    Require(viewSchemaId, nameof(viewSchemaId)),
                    CultMeshBodyPublicationSchemaVersions.Publication
                }
                .Concat(additionalSchemaIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public string SubscriptionId { get; }
        public string ConsumerRuntimeId { get; }
        public string BodyId { get; }
        public IReadOnlyList<string> RecordKeys { get; }
        public IReadOnlyList<string> SchemaIds { get; }

        private static string Require(string value, string parameterName) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value is required.", parameterName)
                : value;
    }

    /// <summary>Derived transport work for one logical hot body.</summary>
    public sealed class CultMeshBodyDemandPlan
    {
        internal CultMeshBodyDemandPlan(
            string bodyId,
            IReadOnlyList<CultMeshBodyConsumerRoute> consumers)
        {
            BodyId = bodyId;
            Consumers = consumers;
            RequiresSharedMemory = consumers.Any(value => value.Transport == CultMeshBodyTransportKind.SharedMemory);
            RequiresSharedFileMapping = consumers.Any(value => value.Transport == CultMeshBodyTransportKind.SharedFileMapping);
            RequiresNetwork = consumers.Any(value => value.Transport == CultMeshBodyTransportKind.Network);
        }

        public string BodyId { get; }
        public bool HasConsumers => Consumers.Count > 0;
        public bool RequiresSharedMemory { get; }
        public bool RequiresSharedFileMapping { get; }
        public bool RequiresNetwork { get; }
        public IReadOnlyList<CultMeshBodyConsumerRoute> Consumers { get; }
    }

    /// <summary>One consumer's owner-derived route for one logical body.</summary>
    public sealed class CultMeshBodyConsumerRoute
    {
        internal CultMeshBodyConsumerRoute(
            string consumerRuntimeId,
            string subscriptionId,
            CultMeshBodyTransportKind transport)
        {
            ConsumerRuntimeId = consumerRuntimeId;
            SubscriptionId = subscriptionId;
            Transport = transport;
        }

        public string ConsumerRuntimeId { get; }
        public string SubscriptionId { get; }
        public CultMeshBodyTransportKind Transport { get; }
    }

    /// <summary>
    /// Owns the live projection from exact state subscriptions to required body transport planes.
    /// A same-machine claim is transport evidence only; shared memory is selected only when both
    /// the server peer and consumer capability agree.
    /// </summary>
    public sealed class CultMeshBodyDemandTracker : IDisposable
    {
        private readonly CultNetDatabaseSubscriptionServer? _subscriptions;
        private readonly object _gate = new();
        private readonly Dictionary<string, CultNetDatabaseSubscriptionDemand> _demands =
            new(StringComparer.Ordinal);
        private bool _disposed;

        public CultMeshBodyDemandTracker(CultNetDatabaseSubscriptionServer subscriptions)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            _subscriptions.DemandChanged += OnDemandChanged;
        }

        /// <summary>Creates a tracker fed explicitly by a subscription/session control plane.</summary>
        public CultMeshBodyDemandTracker()
        {
        }

        public CultMeshBodyDemandPlan Plan(string bodyId)
        {
            if (string.IsNullOrWhiteSpace(bodyId)) throw new ArgumentException("Body identity is required.", nameof(bodyId));
            lock (_gate)
            {
                ThrowIfDisposed();
                var routes = _demands.Values
                    .Where(value => value.BodyIds.Contains(bodyId, StringComparer.Ordinal))
                    .Select(value => Route(value))
                    .Where(value => value != null)
                    .Cast<CultMeshBodyConsumerRoute>()
                    .OrderBy(value => value.ConsumerRuntimeId, StringComparer.Ordinal)
                    .ThenBy(value => value.SubscriptionId, StringComparer.Ordinal)
                    .ToArray();
                return new CultMeshBodyDemandPlan(bodyId, routes);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_subscriptions != null) _subscriptions.DemandChanged -= OnDemandChanged;
                _demands.Clear();
            }
        }

        /// <summary>Applies one active or withdrawn typed body-demand observation.</summary>
        public void Observe(CultNetDatabaseSubscriptionDemand demand)
        {
            if (demand == null) throw new ArgumentNullException(nameof(demand));
            if (demand.BodyIds.Count == 0) return;
            var key = demand.ConsumerRuntimeId + "\u001f" + demand.SubscriptionId;
            lock (_gate)
            {
                if (_disposed) return;
                if (demand.Active) _demands[key] = demand;
                else _demands.Remove(key);
            }
        }

        private void OnDemandChanged(CultNetDatabaseSubscriptionDemand demand) => Observe(demand);

        private static CultMeshBodyConsumerRoute? Route(CultNetDatabaseSubscriptionDemand demand)
        {
            var supported = demand.SupportedBodyTransports;
            if (demand.SameMachine && Supports(supported, CultMeshBodyTransportKind.SharedMemory))
                return new CultMeshBodyConsumerRoute(
                    demand.ConsumerRuntimeId,
                    demand.SubscriptionId,
                    CultMeshBodyTransportKind.SharedMemory);
            if (demand.SameMachine && Supports(supported, CultMeshBodyTransportKind.SharedFileMapping))
                return new CultMeshBodyConsumerRoute(
                    demand.ConsumerRuntimeId,
                    demand.SubscriptionId,
                    CultMeshBodyTransportKind.SharedFileMapping);
            if (Supports(supported, CultMeshBodyTransportKind.Network))
                return new CultMeshBodyConsumerRoute(
                    demand.ConsumerRuntimeId,
                    demand.SubscriptionId,
                    CultMeshBodyTransportKind.Network);
            return null;
        }

        private static bool Supports(
            IReadOnlyList<string> supported,
            CultMeshBodyTransportKind transport) =>
            supported.Any(value => string.Equals(value, transport.ToString(), StringComparison.OrdinalIgnoreCase));

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshBodyDemandTracker));
        }
    }
}
