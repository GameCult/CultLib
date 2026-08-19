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
    /// </summary>
    public sealed class CultNetDatabaseSubscriptionServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly CultNetDatabase _database;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, Task> _subscribe;
        private readonly Func<CultNetDatabaseUnsubscribeMessage, ICultNetSchemaServerPeer, Task> _unsubscribe;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, bool>? _authorizeRequest;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, string, string, bool>? _authorizeRecord;
        private readonly ICultNetSchemaServerPeerLifecycle? _peerLifecycle;
        private readonly ConcurrentDictionary<SubscriptionKey, IDisposable> _subscriptions =
            new ConcurrentDictionary<SubscriptionKey, IDisposable>(SubscriptionKeyComparer.Instance);
        private readonly ConcurrentDictionary<SubscriptionKey, CultNetDatabaseSubscribeMessage> _requests =
            new ConcurrentDictionary<SubscriptionKey, CultNetDatabaseSubscribeMessage>(SubscriptionKeyComparer.Instance);
        private bool _disposed;

        /// <summary>Attaches live database subscription handlers to a schema server.</summary>
        public CultNetDatabaseSubscriptionServer(
            ICultNetSchemaServer server,
            CultNetDatabase database,
            Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, bool>? authorizeRequest = null,
            Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, string, string, bool>? authorizeRecord = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _authorizeRequest = authorizeRequest;
            _authorizeRecord = authorizeRecord;
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

        /// <summary>Detaches handlers and releases all active watches.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeMessage>(_subscribe);
            _server.RemoveCultNetMessageListener<CultNetDatabaseUnsubscribeMessage>(_unsubscribe);
            if (_peerLifecycle != null)
                _peerLifecycle.PeerDisconnected -= HandlePeerDisconnected;
            foreach (var entry in _subscriptions)
            {
                entry.Value.Dispose();
                if (_requests.TryRemove(entry.Key, out var request))
                    PublishDemand(request, entry.Key, active: false);
            }
            _subscriptions.Clear();
            _requests.Clear();
        }

        private void HandlePeerDisconnected(ICultNetSchemaServerPeer peer)
        {
            foreach (var key in _subscriptions.Keys.Where(candidate =>
                         SubscriptionKeyComparer.SamePeer(candidate.Peer, peer)).ToArray())
            {
                if (_subscriptions.TryRemove(key, out var subscription))
                    subscription.Dispose();
                if (_requests.TryRemove(key, out var request))
                    PublishDemand(request, key, active: false);
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

            var key = new SubscriptionKey(peer, subscriptionId);
            if (_requests.TryGetValue(key, out var previous))
                PublishDemand(previous, key, active: false);
            _subscriptions.AddOrUpdate(
                key,
                _ => Watch(request, subscriptionId, peer),
                (_, current) =>
                {
                    current.Dispose();
                    return Watch(request, subscriptionId, peer);
                });
            _requests[key] = request;
            PublishDemand(request, key, active: true);
            if (request.IncludeSnapshot)
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
                if (_authorizeRecord != null)
                {
                    snapshot.Documents = snapshot.Documents
                        .Where(document => _authorizeRecord(
                            request, peer, document.RecordKey, document.SchemaId))
                        .ToArray();
                }
                peer.SendCultNet(snapshot);
            }
            else
            {
                peer.SendCultNet(new CultNetSnapshotResponseRawMessage
                {
                    MessageId = request.MessageId,
                    Documents = Array.Empty<CultNetRawDocumentRecord>()
                });
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
                if (_subscriptions.TryRemove(key, out var subscription)) subscription.Dispose();
                if (_requests.TryRemove(key, out var subscribed))
                    PublishDemand(subscribed, key, active: false);
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
            ICultNetSchemaServerPeer peer)
        {
            return _database.WatchAllChanges().Subscribe(change =>
            {
                var message = CreateAuthorizedChange(change, request, subscriptionId, peer);
                if (message != null) peer.SendCultNet(message);
            });
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
