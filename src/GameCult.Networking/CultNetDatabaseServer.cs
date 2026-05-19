using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameCult.Caching;
using LiteNetLib;
using R3;

namespace GameCult.Networking
{
    /// <summary>
    /// Routes CultNet schema-v0 document messages through a <see cref="CultNetDatabase"/>.
    /// </summary>
    public sealed class CultNetDatabaseServer : IDisposable
    {
        private readonly Server _server;
        private readonly CultNetDatabase _database;
        private readonly Func<CultNetSnapshotRequestMessage, NetPeer, Task> _snapshotHandler;
        private readonly Func<CultNetDocumentPutRawMessage, NetPeer, Task> _putHandler;
        private readonly Func<CultNetDocumentDeleteMessage, NetPeer, Task> _deleteHandler;
        private readonly Func<CultNetShardCatalogRequestMessage, NetPeer, Task> _shardCatalogHandler;
        private readonly Func<CultNetDatabaseSubscribeMessage, NetPeer, Task> _subscribeHandler;
        private readonly Func<CultNetDatabaseUnsubscribeMessage, NetPeer, Task> _unsubscribeHandler;
        private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a database message bridge to a server.
        /// </summary>
        public CultNetDatabaseServer(Server server, CultNetDatabase database)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _snapshotHandler = HandleSnapshotRequestAsync;
            _putHandler = HandlePutAsync;
            _deleteHandler = HandleDeleteAsync;
            _shardCatalogHandler = HandleShardCatalogRequestAsync;
            _subscribeHandler = HandleSubscribeAsync;
            _unsubscribeHandler = HandleUnsubscribeAsync;

            _server.OnCultNet(_snapshotHandler);
            _server.OnCultNet(_putHandler);
            _server.OnCultNet(_deleteHandler);
            _server.OnCultNet(_shardCatalogHandler);
            _server.OnCultNet(_subscribeHandler);
            _server.OnCultNet(_unsubscribeHandler);
        }

        /// <summary>
        /// Gets the database used by this bridge.
        /// </summary>
        public CultNetDatabase Database => _database;

        /// <summary>
        /// Applies a raw put message through the database shard policy.
        /// </summary>
        public Task<object> ApplyPutAsync(CultNetDocumentPutRawMessage message)
        {
            return _database.ApplyPutAsync(message);
        }

        /// <summary>
        /// Applies a raw delete message through the database shard policy.
        /// </summary>
        public Task ApplyDeleteAsync(CultNetDocumentDeleteMessage message)
        {
            return _database.ApplyDeleteAsync(message);
        }

        /// <summary>
        /// Creates a raw snapshot response from the database cache.
        /// </summary>
        public CultNetSnapshotResponseRawMessage CreateSnapshotResponse(CultNetSnapshotRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return _database.Documents.CreateRawSnapshotResponse(
                _database.Cache,
                string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                request);
        }

        /// <summary>
        /// Creates a shard catalog response from the database shard map.
        /// </summary>
        public CultNetShardCatalogResponseMessage CreateShardCatalogResponse(CultNetShardCatalogRequestMessage request)
        {
            return _database.CreateShardCatalogResponse(request);
        }

        /// <summary>
        /// Detaches handlers from the server.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _server.RemoveCultNetMessageListener<CultNetSnapshotRequestMessage>(_snapshotHandler);
            _server.RemoveCultNetMessageListener<CultNetDocumentPutRawMessage>(_putHandler);
            _server.RemoveCultNetMessageListener<CultNetDocumentDeleteMessage>(_deleteHandler);
            _server.RemoveCultNetMessageListener<CultNetShardCatalogRequestMessage>(_shardCatalogHandler);
            _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeMessage>(_subscribeHandler);
            _server.RemoveCultNetMessageListener<CultNetDatabaseUnsubscribeMessage>(_unsubscribeHandler);
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        private Task HandleSnapshotRequestAsync(CultNetSnapshotRequestMessage request, NetPeer peer)
        {
            peer.SendCultNet(CreateSnapshotResponse(request));
            return Task.CompletedTask;
        }

        private Task HandleShardCatalogRequestAsync(CultNetShardCatalogRequestMessage request, NetPeer peer)
        {
            peer.SendCultNet(CreateShardCatalogResponse(request));
            return Task.CompletedTask;
        }

        private async Task HandlePutAsync(CultNetDocumentPutRawMessage message, NetPeer peer)
        {
            try
            {
                await ApplyPutAsync(message).ConfigureAwait(false);
            }
            catch (CultNetShardAuthorityException ex)
            {
                peer.SendCultNet(CreateRoutingError(ex));
            }
            catch (Exception ex)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }
        }

        private async Task HandleDeleteAsync(CultNetDocumentDeleteMessage message, NetPeer peer)
        {
            try
            {
                await ApplyDeleteAsync(message).ConfigureAwait(false);
            }
            catch (CultNetShardAuthorityException ex)
            {
                peer.SendCultNet(CreateRoutingError(ex));
            }
            catch (Exception ex)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }
        }

        private Task HandleSubscribeAsync(CultNetDatabaseSubscribeMessage message, NetPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(message.SubscriptionId)
                ? message.MessageId
                : message.SubscriptionId;
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = "Database subscription requires a subscriptionId or messageId." });
                return Task.CompletedTask;
            }

            var key = SubscriptionKey(peer, subscriptionId);
            _subscriptions.AddOrUpdate(
                key,
                _ => CreateSubscription(message, subscriptionId, peer),
                (_, existing) =>
                {
                    existing.Dispose();
                    return CreateSubscription(message, subscriptionId, peer);
                });

            if (message.IncludeSnapshot)
            {
                peer.SendCultNet(CreateSnapshotResponse(new CultNetSnapshotRequestMessage
                {
                    MessageId = message.MessageId,
                    SchemaIds = message.SchemaIds,
                    RecordKeys = message.RecordKeys
                }));
            }

            return Task.CompletedTask;
        }

        private Task HandleUnsubscribeAsync(CultNetDatabaseUnsubscribeMessage message, NetPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(message.SubscriptionId)
                ? message.MessageId
                : message.SubscriptionId;
            if (!string.IsNullOrWhiteSpace(subscriptionId) &&
                _subscriptions.TryRemove(SubscriptionKey(peer, subscriptionId), out var subscription))
            {
                subscription.Dispose();
            }

            return Task.CompletedTask;
        }

        private IDisposable CreateSubscription(
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId,
            NetPeer peer)
        {
            return _database.WatchAllChanges().Subscribe(change =>
            {
                var outbound = CreateChangeMessage(change, subscriptionId, request);
                if (outbound != null)
                {
                    peer.SendCultNet(outbound);
                }
            });
        }

        internal CultNetDatabaseChangeRawMessage? CreateChangeMessage(
            object change,
            string subscriptionId,
            CultNetDatabaseSubscribeMessage request)
        {
            var changeType = change.GetType();
            var key = (CultRecordKey)(changeType.GetProperty("Key")?.GetValue(change) ?? new CultRecordKey(string.Empty));
            var schemaId = (string?)changeType.GetProperty("SchemaId")?.GetValue(change) ?? string.Empty;
            if (!Matches(request, schemaId, key))
            {
                return null;
            }

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
                Document = CreateRawDocumentRecord(key, document)
            };
        }

        private CultNetRawDocumentRecord CreateRawDocumentRecord(CultRecordKey key, object document)
        {
            var method = typeof(CultNetDocumentRegistry)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(candidate => candidate.Name == nameof(CultNetDocumentRegistry.CreateRawDocumentPutMessage) &&
                                     candidate.IsGenericMethodDefinition);
            var documentType = document.GetType();
            var handleType = typeof(CultRecordHandle<>).MakeGenericType(documentType);
            var handle = Activator.CreateInstance(handleType, new object[] { key });
            var message = method
                .MakeGenericMethod(documentType)
                .Invoke(_database.Documents, new[] { Guid.NewGuid().ToString("N"), handle, document, null });
            return ((CultNetDocumentPutRawMessage)message!).Document;
        }

        private static bool Matches(CultNetDatabaseSubscribeMessage request, string schemaId, CultRecordKey key)
        {
            var schemaMatches = request.SchemaIds == null ||
                                request.SchemaIds.Length == 0 ||
                                request.SchemaIds.Contains(schemaId, StringComparer.Ordinal);
            var keyMatches = request.RecordKeys == null ||
                             request.RecordKeys.Length == 0 ||
                             request.RecordKeys.Contains(key.Value, StringComparer.Ordinal);
            return schemaMatches && keyMatches;
        }

        private static string SubscriptionKey(NetPeer peer, string subscriptionId)
        {
            return $"{peer.Id}:{subscriptionId}";
        }

        private static CultNetErrorMessage CreateRoutingError(CultNetShardAuthorityException exception)
        {
            return new CultNetErrorMessage
            {
                Error = exception.Message,
                RoutingHint = new CultNetShardRoutingHint
                {
                    Reason = exception.Shard.IsPrimary ? "stale_epoch" : "not_primary",
                    Shard = CultNetDatabase.ToMessage(exception.Shard)
                }
            };
        }
    }
}
