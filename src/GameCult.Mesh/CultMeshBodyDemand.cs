using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Networking;

#nullable enable

namespace GameCult.Mesh
{
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
            RequiresNetwork = consumers.Any(value => value.Transport == CultMeshBodyTransportKind.Network);
        }

        public string BodyId { get; }
        public bool HasConsumers => Consumers.Count > 0;
        public bool RequiresSharedMemory { get; }
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
