using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Threading.Tasks;
using GameCult.Caching;
using R3;

namespace GameCult.Networking
{
    /// <summary>
    /// Publishes live database changes through any schema-v0 server transport.
    /// Optional record projection runs after authorization and owns both initial
    /// snapshot and live-update delivery for a peer.
    /// </summary>
    public sealed class CultNetDatabaseSubscriptionServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly CultNetDatabase _database;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, Task> _subscribe;
        private readonly Func<CultNetDatabaseUnsubscribeMessage, ICultNetSchemaServerPeer, Task> _unsubscribe;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, bool>? _authorizeRequest;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, string, string, bool>? _authorizeRecord;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, CultNetRawDocumentRecord, CultNetRawDocumentRecord?>? _projectRecord;
        private readonly ICultNetSchemaServerPeerLifecycle? _peerLifecycle;
        private readonly ConcurrentDictionary<SubscriptionKey, IDisposable> _subscriptions =
            new ConcurrentDictionary<SubscriptionKey, IDisposable>(SubscriptionKeyComparer.Instance);
        private readonly ConcurrentDictionary<SubscriptionKey, CultNetDatabaseSubscribeMessage> _requests =
            new ConcurrentDictionary<SubscriptionKey, CultNetDatabaseSubscribeMessage>(SubscriptionKeyComparer.Instance);
        private readonly ConcurrentDictionary<SubscriptionKey, SubscriptionProjectionState> _projections =
            new ConcurrentDictionary<SubscriptionKey, SubscriptionProjectionState>(SubscriptionKeyComparer.Instance);
        private readonly object _lifecycleGate = new();
        private bool _disposed;

        /// <summary>Attaches live database subscription handlers to a schema server.</summary>
        public CultNetDatabaseSubscriptionServer(
            ICultNetSchemaServer server,
            CultNetDatabase database,
            Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, bool>? authorizeRequest = null,
            Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, string, string, bool>? authorizeRecord = null,
            Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, CultNetRawDocumentRecord, CultNetRawDocumentRecord?>? projectRecord = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _authorizeRequest = authorizeRequest;
            _authorizeRecord = authorizeRecord;
            _projectRecord = projectRecord;
            _subscribe = HandleSubscribeAsync;
            _unsubscribe = HandleUnsubscribeAsync;
            _server.OnCultNet(_subscribe);
            _server.OnCultNet(_unsubscribe);
            _peerLifecycle = server as ICultNetSchemaServerPeerLifecycle;
            if (_peerLifecycle != null)
                _peerLifecycle.PeerDisconnected += HandlePeerDisconnected;
        }

        /// <summary>
        /// Raised when an exact subscription is activated or withdrawn. Providers can use
        /// the requested records and schemas to materialize reactive state only while a
        /// consumer needs it; body publishers additionally use the negotiated body route.
        /// </summary>
        public event Action<CultNetDatabaseSubscriptionDemand>? DemandChanged;

        /// <summary>
        /// Re-evaluates every live peer projection against current authorization and source state.
        /// Records that lost visibility are explicitly removed and body demand is withdrawn while
        /// the request is unauthorized. The subscription intent remains available for a later
        /// authorized generation on the same established peer.
        /// </summary>
        public void Reconcile()
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                foreach (var entry in _requests.ToArray())
                    Reconcile(entry.Key, entry.Value);
            }
        }

        /// <summary>Detaches handlers and releases all active watches.</summary>
        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                _disposed = true;
                _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeMessage>(_subscribe);
                _server.RemoveCultNetMessageListener<CultNetDatabaseUnsubscribeMessage>(_unsubscribe);
                if (_peerLifecycle != null)
                    _peerLifecycle.PeerDisconnected -= HandlePeerDisconnected;
                foreach (var entry in _subscriptions.ToArray())
                    Withdraw(entry.Key, sendRemovals: false, forgetRequest: true);
                _subscriptions.Clear();
                _requests.Clear();
                _projections.Clear();
            }
        }

        private void HandlePeerDisconnected(ICultNetSchemaServerPeer peer)
        {
            lock (_lifecycleGate)
            {
                foreach (var key in _subscriptions.Keys.Where(candidate =>
                             SubscriptionKeyComparer.SamePeer(candidate.Peer, peer)).ToArray())
                    Withdraw(key, sendRemovals: false, forgetRequest: true);
            }
        }

        private Task HandleSubscribeAsync(CultNetDatabaseSubscribeMessage request, ICultNetSchemaServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(request.SubscriptionId)
                ? request.MessageId
                : request.SubscriptionId;
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = "Database subscription requires a subscriptionId or messageId." });
                return Task.CompletedTask;
            }
            if (_authorizeRequest?.Invoke(request, peer) == false)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = "Database subscription is not authorized for this peer." });
                return Task.CompletedTask;
            }

            lock (_lifecycleGate)
            {
                var key = new SubscriptionKey(peer, subscriptionId);
                Withdraw(key, sendRemovals: true, forgetRequest: true);
                var projection = new SubscriptionProjectionState();
                _projections[key] = projection;
                _requests[key] = request;
                try
                {
                    _subscriptions[key] = Watch(request, subscriptionId, peer, key);
                    if (request.IncludeSnapshot)
                    {
                        var snapshot = CreateProjectedSnapshot(request, peer);
                        foreach (var entry in snapshot.BySourceRecordKey)
                            projection.DeliveredBySourceRecordKey[entry.Key] = entry.Value;
                        peer.SendCultNet(new CultNetSnapshotResponseRawMessage
                        {
                            MessageId = request.MessageId,
                            Documents = snapshot.Documents
                        });
                    }
                    else
                    {
                        peer.SendCultNet(new CultNetSnapshotResponseRawMessage
                        {
                            MessageId = request.MessageId,
                            Documents = Array.Empty<CultNetRawDocumentRecord>()
                        });
                    }
                    PublishDemand(request, key, active: true);
                    projection.DemandActive = true;
                }
                catch
                {
                    Withdraw(key, sendRemovals: false, forgetRequest: true);
                    throw;
                }
            }
            return Task.CompletedTask;
        }

        private Task HandleUnsubscribeAsync(CultNetDatabaseUnsubscribeMessage request, ICultNetSchemaServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(request.SubscriptionId)
                ? request.MessageId
                : request.SubscriptionId;
            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                var key = new SubscriptionKey(peer, subscriptionId);
                lock (_lifecycleGate)
                    Withdraw(key, sendRemovals: false, forgetRequest: true);
            }
            return Task.CompletedTask;
        }

        private void PublishDemand(
            CultNetDatabaseSubscribeMessage request,
            SubscriptionKey key,
            bool active)
        {
            DemandChanged?.Invoke(new CultNetDatabaseSubscriptionDemand(
                string.IsNullOrWhiteSpace(request.ConsumerRuntimeId) ? key.Id : request.ConsumerRuntimeId!,
                key.Id,
                (request.RecordKeys ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                (request.SchemaIds ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                (request.BodyIds ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                (request.SupportedBodyTransports ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                IsSameMachine(key.Peer),
                active));
        }

        private static bool IsSameMachine(ICultNetSchemaServerPeer peer)
        {
            if (peer is ICultNetSchemaServerPeerLocation located && located.RemoteEndPoint is IPEndPoint ip)
                return IPAddress.IsLoopback(ip.Address);
            return false;
        }

        private IDisposable Watch(
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId,
            ICultNetSchemaServerPeer peer,
            SubscriptionKey key)
        {
            return _database.WatchAllChanges().Subscribe(change =>
            {
                lock (_lifecycleGate)
                {
                    if (_disposed || !_requests.ContainsKey(key) || !_projections.TryGetValue(key, out var projection))
                        return;
                    if (_authorizeRequest?.Invoke(request, peer) == false)
                    {
                        Reconcile(key, request);
                        return;
                    }

                    var sourceRecordKey = ResolveChangeRecordKey(change);
                    var message = CreateAuthorizedChange(change, request, subscriptionId, peer);
                    var projected = message?.Document;
                    if (projected != null && _projectRecord != null)
                        projected = _projectRecord(request, peer, projected);
                    ApplyProjectedChange(
                        projection,
                        sourceRecordKey,
                        projected,
                        peer,
                        subscriptionId);
                }
            });
        }

        private void Reconcile(SubscriptionKey key, CultNetDatabaseSubscribeMessage request)
        {
            if (!_projections.TryGetValue(key, out var projection)) return;
            var authorized = _authorizeRequest?.Invoke(request, key.Peer) != false;
            var next = authorized
                ? CreateProjectedSnapshot(request, key.Peer)
                : ProjectedSnapshot.Empty;

            foreach (var previous in projection.DeliveredBySourceRecordKey.ToArray())
            {
                if (!next.BySourceRecordKey.TryGetValue(previous.Key, out var current))
                {
                    SendRemoval(key.Peer, key.Id, previous.Value);
                    continue;
                }
                if (!string.Equals(previous.Value.RecordKey, current.RecordKey, StringComparison.Ordinal) ||
                    !string.Equals(previous.Value.SchemaId, current.SchemaId, StringComparison.Ordinal))
                {
                    SendRemoval(key.Peer, key.Id, previous.Value);
                    SendUpsert(key.Peer, key.Id, current, added: true);
                }
                else if (!Equivalent(previous.Value, current))
                {
                    SendUpsert(key.Peer, key.Id, current, added: false);
                }
            }
            foreach (var current in next.BySourceRecordKey)
            {
                if (!projection.DeliveredBySourceRecordKey.ContainsKey(current.Key))
                    SendUpsert(key.Peer, key.Id, current.Value, added: true);
            }

            projection.DeliveredBySourceRecordKey.Clear();
            foreach (var current in next.BySourceRecordKey)
                projection.DeliveredBySourceRecordKey[current.Key] = current.Value;

            if (projection.DemandActive != authorized)
            {
                projection.DemandActive = authorized;
                PublishDemand(request, key, authorized);
            }
        }

        private ProjectedSnapshot CreateProjectedSnapshot(
            CultNetDatabaseSubscribeMessage request,
            ICultNetSchemaServerPeer peer)
        {
            var snapshot = _database.Documents.CreateRawSnapshotResponse(
                _database.Cache,
                request.MessageId,
                new CultNetSnapshotRequestMessage
                {
                    MessageId = request.MessageId,
                    SchemaIds = request.SchemaIds,
                    RecordKeys = request.RecordKeys
                });
            var bySourceRecordKey = new Dictionary<string, CultNetRawDocumentRecord>(StringComparer.Ordinal);
            var projectedRecordKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in snapshot.Documents)
            {
                var sourceRecordKey = source.RecordKey;
                if (_authorizeRecord?.Invoke(request, peer, sourceRecordKey, source.SchemaId) == false)
                    continue;
                var projected = _projectRecord == null ? source : _projectRecord(request, peer, source);
                if (projected == null) continue;
                if (!projectedRecordKeys.Add(projected.RecordKey))
                    throw new InvalidOperationException(
                        $"Database subscription projection produced duplicate record key '{projected.RecordKey}'.");
                bySourceRecordKey[sourceRecordKey] = projected;
            }
            return new ProjectedSnapshot(bySourceRecordKey);
        }

        private void ApplyProjectedChange(
            SubscriptionProjectionState projection,
            string sourceRecordKey,
            CultNetRawDocumentRecord? current,
            ICultNetSchemaServerPeer peer,
            string subscriptionId)
        {
            projection.DeliveredBySourceRecordKey.TryGetValue(sourceRecordKey, out var previous);
            if (current == null)
            {
                if (previous != null)
                {
                    projection.DeliveredBySourceRecordKey.Remove(sourceRecordKey);
                    SendRemoval(peer, subscriptionId, previous);
                }
                return;
            }
            if (previous == null)
            {
                projection.DeliveredBySourceRecordKey[sourceRecordKey] = current;
                SendUpsert(peer, subscriptionId, current, added: true);
                return;
            }
            if (!string.Equals(previous.RecordKey, current.RecordKey, StringComparison.Ordinal) ||
                !string.Equals(previous.SchemaId, current.SchemaId, StringComparison.Ordinal))
            {
                SendRemoval(peer, subscriptionId, previous);
                projection.DeliveredBySourceRecordKey[sourceRecordKey] = current;
                SendUpsert(peer, subscriptionId, current, added: true);
                return;
            }
            if (!Equivalent(previous, current))
            {
                projection.DeliveredBySourceRecordKey[sourceRecordKey] = current;
                SendUpsert(peer, subscriptionId, current, added: false);
            }
        }

        private static string ResolveChangeRecordKey(object change)
        {
            var value = change.GetType().GetProperty("Key")?.GetValue(change);
            return value is CultRecordKey key ? key.Value : string.Empty;
        }

        private static bool Equivalent(CultNetRawDocumentRecord left, CultNetRawDocumentRecord right) =>
            string.Equals(left.SchemaId, right.SchemaId, StringComparison.Ordinal) &&
            string.Equals(left.SchemaName, right.SchemaName, StringComparison.Ordinal) &&
            string.Equals(left.SchemaVersion, right.SchemaVersion, StringComparison.Ordinal) &&
            string.Equals(left.SchemaContentHash, right.SchemaContentHash, StringComparison.Ordinal) &&
            string.Equals(left.RecordKey, right.RecordKey, StringComparison.Ordinal) &&
            string.Equals(left.PayloadEncoding, right.PayloadEncoding, StringComparison.Ordinal) &&
            left.Payload.SequenceEqual(right.Payload) &&
            string.Equals(left.SourceRuntimeId, right.SourceRuntimeId, StringComparison.Ordinal) &&
            string.Equals(left.SourceAgentId, right.SourceAgentId, StringComparison.Ordinal) &&
            string.Equals(left.SourceRole, right.SourceRole, StringComparison.Ordinal);

        private static void SendUpsert(
            ICultNetSchemaServerPeer peer,
            string subscriptionId,
            CultNetRawDocumentRecord document,
            bool added) =>
            peer.SendCultNet(new CultNetDatabaseChangeRawMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscriptionId,
                ChangeKind = added ? "added" : "updated",
                Document = document
            });

        private static void SendRemoval(
            ICultNetSchemaServerPeer peer,
            string subscriptionId,
            CultNetRawDocumentRecord document) =>
            peer.SendCultNet(new CultNetDatabaseChangeRawMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscriptionId,
                ChangeKind = "removed",
                RecordKey = document.RecordKey,
                SchemaId = document.SchemaId
            });

        private void Withdraw(SubscriptionKey key, bool sendRemovals, bool forgetRequest)
        {
            if (_subscriptions.TryRemove(key, out var subscription))
                subscription.Dispose();
            if (_projections.TryRemove(key, out var projection))
            {
                if (sendRemovals)
                {
                    foreach (var delivered in projection.DeliveredBySourceRecordKey.Values)
                        SendRemoval(key.Peer, key.Id, delivered);
                }
                if (projection.DemandActive && _requests.TryGetValue(key, out var activeRequest))
                    PublishDemand(activeRequest, key, active: false);
            }
            if (forgetRequest)
                _requests.TryRemove(key, out _);
        }

        private CultNetDatabaseChangeRawMessage? CreateAuthorizedChange(
            object change,
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId,
            ICultNetSchemaServerPeer peer)
        {
            return CreateChangeCore(change, request, subscriptionId, (recordKey, schemaId) =>
                _authorizeRecord?.Invoke(request, peer, recordKey, schemaId) != false);
        }

        private CultNetDatabaseChangeRawMessage? CreateChange(
            object change,
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId)
        {
            return CreateChangeCore(change, request, subscriptionId, (_, _) => true);
        }

        private CultNetDatabaseChangeRawMessage? CreateChangeCore(
            object change,
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId,
            Func<string, string, bool> authorizeRecord)
        {
            var changeType = change.GetType();
            var key = (CultRecordKey)(changeType.GetProperty("Key")?.GetValue(change) ?? new CultRecordKey(""));
            if (request.RecordKeys is { Length: > 0 } && !request.RecordKeys.Contains(key.Value, StringComparer.Ordinal))
                return null;

            var kind = (CultNetDatabaseChangeKind)(changeType.GetProperty("Kind")?.GetValue(change) ?? CultNetDatabaseChangeKind.Updated);
            var document = changeType.GetProperty("Document")?.GetValue(change);
            if (kind == CultNetDatabaseChangeKind.Removed || document == null)
            {
                var previous = changeType.GetProperty("PreviousDocument")?.GetValue(change);
                var schemaId = ResolveWireSchemaId(previous, (string?)changeType.GetProperty("SchemaId")?.GetValue(change) ?? "");
                if (!MatchesRequestedSchema(request.SchemaIds, previous, schemaId))
                    return null;
                if (!authorizeRecord(key.Value, schemaId))
                    return null;
                return new CultNetDatabaseChangeRawMessage
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    SubscriptionId = subscriptionId,
                    ChangeKind = "removed",
                    RecordKey = key.Value,
                    SchemaId = schemaId
                };
            }

            var raw = CreateRawRecord(key, document);
            if (!MatchesRequestedSchema(request.SchemaIds, document, raw.SchemaId))
                return null;
            if (!authorizeRecord(key.Value, raw.SchemaId))
                return null;
            return new CultNetDatabaseChangeRawMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscriptionId,
                ChangeKind = kind == CultNetDatabaseChangeKind.Added ? "added" : "updated",
                Document = raw
            };
        }

        private string ResolveWireSchemaId(object? document, string fallback)
        {
            if (document == null) return fallback;
            return _database.Documents.GetByDocumentType(document.GetType())?.SchemaId ?? fallback;
        }

        private bool MatchesRequestedSchema(string[]? requestedSchemaIds, object? document, string wireSchemaId)
        {
            if (requestedSchemaIds is not { Length: > 0 }) return true;
            if (requestedSchemaIds.Contains(wireSchemaId, StringComparer.Ordinal)) return true;
            return document != null && CultNetSchemaAliasMatching.MatchesAny(
                requestedSchemaIds,
                _database.Cache.Registry.GetRequired(document.GetType()));
        }

        private CultNetRawDocumentRecord CreateRawRecord(CultRecordKey key, object document)
        {
            var method = typeof(CultNetDocumentRegistry)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(candidate => candidate.Name == nameof(CultNetDocumentRegistry.CreateRawDocumentPutMessage) &&
                                     candidate.IsGenericMethodDefinition);
            var documentType = document.GetType();
            var handle = Activator.CreateInstance(
                typeof(CultRecordHandle<>).MakeGenericType(documentType),
                new object[] { key });
            var put = method.MakeGenericMethod(documentType)
                .Invoke(_database.Documents, new[] { Guid.NewGuid().ToString("N"), handle, document, null });
            return ((CultNetDocumentPutRawMessage)put!).Document;
        }

        private sealed class SubscriptionProjectionState
        {
            public Dictionary<string, CultNetRawDocumentRecord> DeliveredBySourceRecordKey { get; } =
                new(StringComparer.Ordinal);
            public bool DemandActive { get; set; }
        }

        private sealed class ProjectedSnapshot
        {
            public ProjectedSnapshot(Dictionary<string, CultNetRawDocumentRecord> bySourceRecordKey)
            {
                BySourceRecordKey = bySourceRecordKey;
                Documents = bySourceRecordKey.Values.ToArray();
            }

            public static ProjectedSnapshot Empty { get; } =
                new(new Dictionary<string, CultNetRawDocumentRecord>(StringComparer.Ordinal));
            public Dictionary<string, CultNetRawDocumentRecord> BySourceRecordKey { get; }
            public CultNetRawDocumentRecord[] Documents { get; }
        }

        private sealed class SubscriptionKey
        {
            public SubscriptionKey(ICultNetSchemaServerPeer peer, string id)
            {
                Peer = peer;
                Id = id;
            }
            public ICultNetSchemaServerPeer Peer { get; }
            public string Id { get; }
        }

        private sealed class SubscriptionKeyComparer : IEqualityComparer<SubscriptionKey>
        {
            public static SubscriptionKeyComparer Instance { get; } = new SubscriptionKeyComparer();
            public bool Equals(SubscriptionKey? x, SubscriptionKey? y) =>
                x != null && y != null && SamePeer(x.Peer, y.Peer) && string.Equals(x.Id, y.Id, StringComparison.Ordinal);
            public int GetHashCode(SubscriptionKey value) =>
                (PeerHash(value.Peer) * 397) ^ StringComparer.Ordinal.GetHashCode(value.Id);

            internal static bool SamePeer(ICultNetSchemaServerPeer left, ICultNetSchemaServerPeer right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (left is RudpCultNetSchemaServerPeer leftRudp && right is RudpCultNetSchemaServerPeer rightRudp)
                    return ReferenceEquals(leftRudp.TransportPeer, rightRudp.TransportPeer);
                if (left is CultNetServerPeer leftLite && right is CultNetServerPeer rightLite)
                    return ReferenceEquals(leftLite.Peer, rightLite.Peer);
                return false;
            }

            private static int PeerHash(ICultNetSchemaServerPeer peer)
            {
                if (peer is RudpCultNetSchemaServerPeer rudp)
                    return RuntimeHelpers.GetHashCode(rudp.TransportPeer);
                if (peer is CultNetServerPeer lite)
                    return RuntimeHelpers.GetHashCode(lite.Peer);
                return RuntimeHelpers.GetHashCode(peer);
            }
        }
    }

    /// <summary>One active or withdrawn exact state subscription and its optional hot-body route.</summary>
    public sealed class CultNetDatabaseSubscriptionDemand
    {
        public CultNetDatabaseSubscriptionDemand(
            string consumerRuntimeId,
            string subscriptionId,
            IReadOnlyList<string> bodyIds,
            IReadOnlyList<string> supportedBodyTransports,
            bool sameMachine,
            bool active)
            : this(
                consumerRuntimeId,
                subscriptionId,
                Array.Empty<string>(),
                Array.Empty<string>(),
                bodyIds,
                supportedBodyTransports,
                sameMachine,
                active)
        {
        }

        public CultNetDatabaseSubscriptionDemand(
            string consumerRuntimeId,
            string subscriptionId,
            IReadOnlyList<string> recordKeys,
            IReadOnlyList<string> schemaIds,
            IReadOnlyList<string> bodyIds,
            IReadOnlyList<string> supportedBodyTransports,
            bool sameMachine,
            bool active)
        {
            ConsumerRuntimeId = consumerRuntimeId;
            SubscriptionId = subscriptionId;
            RecordKeys = recordKeys;
            SchemaIds = schemaIds;
            BodyIds = bodyIds;
            SupportedBodyTransports = supportedBodyTransports;
            SameMachine = sameMachine;
            Active = active;
        }

        public string ConsumerRuntimeId { get; }
        public string SubscriptionId { get; }
        public IReadOnlyList<string> RecordKeys { get; }
        public IReadOnlyList<string> SchemaIds { get; }
        public IReadOnlyList<string> BodyIds { get; }
        public IReadOnlyList<string> SupportedBodyTransports { get; }
        public bool SameMachine { get; }
        public bool Active { get; }
    }
}
