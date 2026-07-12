using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        private readonly ConcurrentDictionary<SubscriptionKey, IDisposable> _subscriptions =
            new ConcurrentDictionary<SubscriptionKey, IDisposable>(SubscriptionKeyComparer.Instance);
        private bool _disposed;

        /// <summary>Attaches live database subscription handlers to a schema server.</summary>
        public CultNetDatabaseSubscriptionServer(ICultNetSchemaServer server, CultNetDatabase database)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _subscribe = HandleSubscribeAsync;
            _unsubscribe = HandleUnsubscribeAsync;
            _server.OnCultNet(_subscribe);
            _server.OnCultNet(_unsubscribe);
        }

        /// <summary>Detaches handlers and releases all active watches.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeMessage>(_subscribe);
            _server.RemoveCultNetMessageListener<CultNetDatabaseUnsubscribeMessage>(_unsubscribe);
            foreach (var subscription in _subscriptions.Values) subscription.Dispose();
            _subscriptions.Clear();
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

            var key = new SubscriptionKey(peer, subscriptionId);
            _subscriptions.AddOrUpdate(
                key,
                _ => Watch(request, subscriptionId, peer),
                (_, current) =>
                {
                    current.Dispose();
                    return Watch(request, subscriptionId, peer);
                });
            if (request.IncludeSnapshot)
            {
                peer.SendCultNet(_database.Documents.CreateRawSnapshotResponse(
                    _database.Cache,
                    request.MessageId,
                    new CultNetSnapshotRequestMessage
                    {
                        MessageId = request.MessageId,
                        SchemaIds = request.SchemaIds,
                        RecordKeys = request.RecordKeys
                    }));
            }
            return Task.CompletedTask;
        }

        private Task HandleUnsubscribeAsync(CultNetDatabaseUnsubscribeMessage request, ICultNetSchemaServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(request.SubscriptionId)
                ? request.MessageId
                : request.SubscriptionId;
            if (!string.IsNullOrWhiteSpace(subscriptionId) &&
                _subscriptions.TryRemove(new SubscriptionKey(peer, subscriptionId), out var subscription))
                subscription.Dispose();
            return Task.CompletedTask;
        }

        private IDisposable Watch(
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId,
            ICultNetSchemaServerPeer peer)
        {
            return _database.WatchAllChanges().Subscribe(change =>
            {
                var message = CreateChange(change, request, subscriptionId);
                if (message != null) peer.SendCultNet(message);
            });
        }

        private CultNetDatabaseChangeRawMessage? CreateChange(
            object change,
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId)
        {
            var changeType = change.GetType();
            var key = (CultRecordKey)(changeType.GetProperty("Key")?.GetValue(change) ?? new CultRecordKey(""));
            var schemaId = (string?)changeType.GetProperty("SchemaId")?.GetValue(change) ?? "";
            if (request.RecordKeys is { Length: > 0 } && !request.RecordKeys.Contains(key.Value, StringComparer.Ordinal))
                return null;
            if (request.SchemaIds is { Length: > 0 } && !request.SchemaIds.Contains(schemaId, StringComparer.Ordinal))
                return null;

            var kind = (CultNetDatabaseChangeKind)(changeType.GetProperty("Kind")?.GetValue(change) ?? CultNetDatabaseChangeKind.Updated);
            var document = changeType.GetProperty("Document")?.GetValue(change);
            if (kind == CultNetDatabaseChangeKind.Removed || document == null)
            {
                return new CultNetDatabaseChangeRawMessage
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    SubscriptionId = subscriptionId,
                    ChangeKind = "removed",
                    RecordKey = key.Value,
                    SchemaId = schemaId
                };
            }

            return new CultNetDatabaseChangeRawMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscriptionId,
                ChangeKind = kind == CultNetDatabaseChangeKind.Added ? "added" : "updated",
                Document = CreateRawRecord(key, document)
            };
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
                x != null && y != null && ReferenceEquals(x.Peer, y.Peer) && string.Equals(x.Id, y.Id, StringComparison.Ordinal);
            public int GetHashCode(SubscriptionKey value) =>
                (RuntimeHelpers.GetHashCode(value.Peer) * 397) ^ StringComparer.Ordinal.GetHashCode(value.Id);
        }
    }
}
