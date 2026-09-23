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
        private readonly CultNetDatabaseServerOptions _options;
        private readonly Func<CultNetSnapshotRequestMessage, CultNetServerPeer, Task> _snapshotHandler;
        private readonly Func<CultNetSnapshotRequestV1Message, CultNetServerPeer, Task> _snapshotHandlerV1;
        private readonly Func<CultNetDocumentPutRawMessage, CultNetServerPeer, Task> _putHandler;
        private readonly Func<CultNetDocumentDeleteMessage, CultNetServerPeer, Task> _deleteHandler;
        private readonly Func<CultNetShardCatalogRequestMessage, CultNetServerPeer, Task> _shardCatalogHandler;
        private readonly Func<CultNetShardLogRequestMessage, CultNetServerPeer, Task> _shardLogHandler;
        private readonly Func<CultNetDatabaseSubscribeMessage, CultNetServerPeer, Task> _subscribeHandler;
        private readonly Func<CultNetDatabaseSubscribeV1Message, CultNetServerPeer, Task> _subscribeHandlerV1;
        private readonly Func<CultNetDatabaseUnsubscribeMessage, CultNetServerPeer, Task> _unsubscribeHandler;
        private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a database message bridge to a server.
        /// </summary>
        public CultNetDatabaseServer(
            Server server,
            CultNetDatabase database,
            CultNetDatabaseServerOptions? options = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _options = options ?? new CultNetDatabaseServerOptions();
            _snapshotHandler = HandleSnapshotRequestAsync;
            _snapshotHandlerV1 = HandleSnapshotRequestV1Async;
            _putHandler = HandlePutAsync;
            _deleteHandler = HandleDeleteAsync;
            _shardCatalogHandler = HandleShardCatalogRequestAsync;
            _shardLogHandler = HandleShardLogRequestAsync;
            _subscribeHandler = HandleSubscribeAsync;
            _subscribeHandlerV1 = HandleSubscribeV1Async;
            _unsubscribeHandler = HandleUnsubscribeAsync;

            _server.OnCultNet(_snapshotHandler);
            _server.OnCultNet(_snapshotHandlerV1);
            _server.OnCultNet(_putHandler);
            _server.OnCultNet(_deleteHandler);
            _server.OnCultNet(_shardCatalogHandler);
            _server.OnCultNet(_shardLogHandler);
            _server.OnCultNet(_subscribeHandler);
            _server.OnCultNet(_subscribeHandlerV1);
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
            if (!string.IsNullOrWhiteSpace(request.ShardId))
            {
                var shard = _database.Shards.FirstOrDefault(candidate =>
                    string.Equals(candidate.ShardId, request.ShardId, StringComparison.Ordinal));
                if (shard == null)
                {
                    throw new InvalidOperationException($"Shard '{request.ShardId}' is not known by this database.");
                }

                if (request.ShardEpoch.HasValue && request.ShardEpoch.Value != shard.Epoch)
                {
                    throw new CultNetShardAuthorityException(
                        shard,
                        $"Shard '{shard.ShardId}' is at epoch {shard.Epoch}, not request epoch {request.ShardEpoch.Value}.",
                        "stale_epoch");
                }

                return _database.CreateShardSnapshotResponse(
                    shard,
                    string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                    request);
            }

            return _database.Documents.CreateRawSnapshotResponse(
                _database.Cache,
                string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                request);
        }

        /// <summary>
        /// Answers a typed-selection snapshot request (R-A, docs/cultnet-selection-cut.md): the door,
        /// then one evaluation over the database's rows in last-write order, through
        /// <see cref="CultNetDocumentRegistry.CreateSelectionResponse"/>.
        /// </summary>
        public CultNetSnapshotResponseRawV1Message CreateSelectionResponse(CultNetSnapshotRequestV1Message request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return _database.Documents.CreateSelectionResponse(
                _database.Cache,
                string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                request.Selection,
                ordinalOf: (schemaId, key) => _database.LastWriteSequence(schemaId, key) ?? 0,
                asOf: _database.CurrentAsOf(),
                // R-Q: asOf is one shard-log watermark, so a selection whose matched rows span more than
                // one shard's log is refused rather than answered against a watermark that is not
                // exact for all of them.
                shardIdOf: (schemaId, key) => _database.ResolveShard(schemaId, key).ShardId,
                cursorKey: _database.CursorKey,
                // S-9: once the matched rows resolve to one shard, asOf is that shard's own watermark,
                // not CurrentAsOf()'s database-wide maximum across every shard.
                asOfForShard: _database.CurrentAsOf);
        }

        /// <summary>
        /// Creates a shard catalog response from the database shard map.
        /// </summary>
        public CultNetShardCatalogResponseMessage CreateShardCatalogResponse(CultNetShardCatalogRequestMessage request)
        {
            return _database.CreateShardCatalogResponse(request);
        }

        /// <summary>
        /// Creates a shard mutation-log response from the database log.
        /// </summary>
        public CultNetShardLogResponseMessage CreateShardLogResponse(CultNetShardLogRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ShardId))
            {
                throw new ArgumentException("Shard log request requires a shardId.", nameof(request));
            }

            var shard = _database.Shards.FirstOrDefault(candidate =>
                string.Equals(candidate.ShardId, request.ShardId, StringComparison.Ordinal));
            if (shard == null)
            {
                return new CultNetShardLogResponseMessage
                {
                    MessageId = RequestMessageId(request.MessageId),
                    ShardId = request.ShardId,
                    ResyncRequired = true,
                    Reason = "unknown_shard"
                };
            }

            if (request.ShardEpoch.HasValue && request.ShardEpoch.Value != shard.Epoch)
            {
                return new CultNetShardLogResponseMessage
                {
                    MessageId = RequestMessageId(request.MessageId),
                    ShardId = request.ShardId,
                    ShardEpoch = shard.Epoch,
                    ResyncRequired = true,
                    Reason = "stale_epoch"
                };
            }

            var compactedThrough = _database.GetCompactedMutationLogSequence(request.ShardId);
            if (request.AfterSequence < compactedThrough)
            {
                return new CultNetShardLogResponseMessage
                {
                    MessageId = RequestMessageId(request.MessageId),
                    ShardId = request.ShardId,
                    ShardEpoch = shard.Epoch,
                    ResyncRequired = true,
                    Reason = "compacted_history",
                    CompactedThrough = compactedThrough
                };
            }

            var entries = _database.GetMutationLogMessages(request.ShardId, request.AfterSequence, request.Limit)
                .ToArray();

            return new CultNetShardLogResponseMessage
            {
                MessageId = RequestMessageId(request.MessageId),
                ShardId = request.ShardId,
                ShardEpoch = shard.Epoch,
                Entries = entries
            };
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
            _server.RemoveCultNetMessageListener<CultNetSnapshotRequestV1Message>(_snapshotHandlerV1);
            _server.RemoveCultNetMessageListener<CultNetDocumentPutRawMessage>(_putHandler);
            _server.RemoveCultNetMessageListener<CultNetDocumentDeleteMessage>(_deleteHandler);
            _server.RemoveCultNetMessageListener<CultNetShardCatalogRequestMessage>(_shardCatalogHandler);
            _server.RemoveCultNetMessageListener<CultNetShardLogRequestMessage>(_shardLogHandler);
            _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeMessage>(_subscribeHandler);
            _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeV1Message>(_subscribeHandlerV1);
            _server.RemoveCultNetMessageListener<CultNetDatabaseUnsubscribeMessage>(_unsubscribeHandler);
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        private Task HandleSnapshotRequestAsync(CultNetSnapshotRequestMessage request, CultNetServerPeer peer)
        {
            peer.SendCultNet(CreateSnapshotResponse(request));
            return Task.CompletedTask;
        }

        private Task HandleSnapshotRequestV1Async(CultNetSnapshotRequestV1Message request, CultNetServerPeer peer)
        {
            try
            {
                peer.SendCultNet(CreateSelectionResponse(request));
            }
            catch (CultNetSelectionInvalidException ex)
            {
                peer.SendCultNet(CultNetErrorMessage.ForSelectionInvalid(ex));
            }
            catch (CultNetSelectionCursorException ex)
            {
                peer.SendCultNet(CultNetErrorMessage.ForCursor(ex));
            }
            catch (CultNetSelectionReferenceOutsideTargetException ex)
            {
                peer.SendCultNet(CultNetErrorMessage.ForReferenceOutsideTarget(ex));
            }
            // R-AM: an untyped fault answers like HandleShardLogRequestAsync/HandlePutAsync's own
            // catch (Exception) - a CultNetErrorMessage on the wire, not silence. Before this, only
            // the three typed selection exceptions were caught here; anything else propagated up into
            // the dispatch backstop (CultNetRudpSchemaServer.cs), which swallowed it with no log and
            // hid SM-6's ArgumentNullException regression for a whole batch.
            catch (Exception ex)
            {
                _server.Logger.LogError($"CultNet snapshot v1 request failed: {ex.Message}");
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }

            return Task.CompletedTask;
        }

        private Task HandleShardCatalogRequestAsync(CultNetShardCatalogRequestMessage request, CultNetServerPeer peer)
        {
            peer.SendCultNet(CreateShardCatalogResponse(request));
            return Task.CompletedTask;
        }

        private Task HandleShardLogRequestAsync(CultNetShardLogRequestMessage request, CultNetServerPeer peer)
        {
            try
            {
                peer.SendCultNet(CreateShardLogResponse(request));
            }
            catch (Exception ex)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }

            return Task.CompletedTask;
        }

        private async Task HandlePutAsync(CultNetDocumentPutRawMessage message, CultNetServerPeer peer)
        {
            try
            {
                await ApplyPutAsync(message).ConfigureAwait(false);
            }
            catch (CultNetShardAuthorityException ex)
            {
                if (!await TryForwardPutAsync(ex, message).ConfigureAwait(false))
                {
                    _server.Logger.LogError($"CultNet raw put authority failed: {ex.Message}");
                    peer.SendCultNet(CreateRoutingError(ex));
                }
            }
            catch (Exception ex)
            {
                _server.Logger.LogError($"CultNet raw put failed: {ex.Message}");
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }
        }

        private async Task HandleDeleteAsync(CultNetDocumentDeleteMessage message, CultNetServerPeer peer)
        {
            try
            {
                await ApplyDeleteAsync(message).ConfigureAwait(false);
            }
            catch (CultNetShardAuthorityException ex)
            {
                if (!await TryForwardDeleteAsync(ex, message).ConfigureAwait(false))
                {
                    peer.SendCultNet(CreateRoutingError(ex));
                }
            }
            catch (Exception ex)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }
        }

        private Task HandleSubscribeAsync(CultNetDatabaseSubscribeMessage message, CultNetServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(message.SubscriptionId)
                ? message.MessageId
                : message.SubscriptionId;
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = "Database subscription requires a subscriptionId or messageId." });
                return Task.CompletedTask;
            }

            var key = SubscriptionKey(peer.Peer, subscriptionId);
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
            else
            {
                peer.SendCultNet(new CultNetSnapshotResponseRawMessage
                {
                    MessageId = message.MessageId,
                    Documents = Array.Empty<CultNetRawDocumentRecord>()
                });
            }

            return Task.CompletedTask;
        }

        private Task HandleSubscribeV1Async(CultNetDatabaseSubscribeV1Message message, CultNetServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(message.SubscriptionId)
                ? message.MessageId
                : message.SubscriptionId;
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = "Database subscription requires a subscriptionId or messageId." });
                return Task.CompletedTask;
            }

            // S-11: the door runs first, unconditionally (R-F) - a selection that is invalid for reasons
            // unrelated to its hop (an empty keys list, an undeclared index, ...) reports that refusal,
            // not a hop refusal that has nothing to do with why it was actually rejected.
            try
            {
                message.Selection.Validate(_database.Cache.Registry.AllDescriptors.ToArray());
            }
            catch (CultNetSelectionInvalidException ex)
            {
                peer.SendCultNet(CultNetErrorMessage.ForSelectionInvalid(ex));
                return Task.CompletedTask;
            }

            // This server delivers live changes through the single-row fast path (CreateChangeMessage /
            // CultNetSelectionEvaluator.Matches), which is set-independent by construction - the same
            // ceiling v0 subscriptions on this server already have. A hop-bearing selection is
            // set-dependent (D6) and needs the subscription server's reconcile loop instead, so it is
            // refused here, after the door, rather than reaching Matches' own InvalidOperationException
            // on the first change. The refused field names whichever hop is actually set - cited, not
            // always cites - so the refusal describes the selection that was actually sent.
            if (message.Selection.HasHop)
            {
                var hopField = message.Selection.Cites != null ? "cites" : "cited";
                peer.SendCultNet(new CultNetErrorMessage
                {
                    Error = $"selection_invalid: selection.{hopField} needs the subscription server's reconcile loop (D6); this server only fast-matches a single row.",
                    Code = "selection_invalid",
                    Details = new CultNetErrorDetails { Field = hopField }
                });
                return Task.CompletedTask;
            }

            var key = SubscriptionKey(peer.Peer, subscriptionId);
            _subscriptions.AddOrUpdate(
                key,
                _ => CreateSubscription(message.Selection, subscriptionId, peer),
                (_, existing) =>
                {
                    existing.Dispose();
                    return CreateSubscription(message.Selection, subscriptionId, peer);
                });

            if (message.IncludeSnapshot)
            {
                try
                {
                    peer.SendCultNet(CreateSelectionResponse(new CultNetSnapshotRequestV1Message
                    {
                        MessageId = message.MessageId,
                        Selection = message.Selection
                    }));
                }
                catch (CultNetSelectionInvalidException ex)
                {
                    peer.SendCultNet(CultNetErrorMessage.ForSelectionInvalid(ex));
                }
                catch (CultNetSelectionCursorException ex)
                {
                    peer.SendCultNet(CultNetErrorMessage.ForCursor(ex));
                }
                catch (CultNetSelectionReferenceOutsideTargetException ex)
                {
                    peer.SendCultNet(CultNetErrorMessage.ForReferenceOutsideTarget(ex));
                }
            }
            else
            {
                peer.SendCultNet(new CultNetSnapshotResponseRawV1Message
                {
                    MessageId = message.MessageId,
                    AsOf = _database.CurrentAsOf()
                });
            }

            return Task.CompletedTask;
        }

        private Task HandleUnsubscribeAsync(CultNetDatabaseUnsubscribeMessage message, CultNetServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(message.SubscriptionId)
                ? message.MessageId
                : message.SubscriptionId;
            if (!string.IsNullOrWhiteSpace(subscriptionId) &&
                _subscriptions.TryRemove(SubscriptionKey(peer.Peer, subscriptionId), out var subscription))
            {
                subscription.Dispose();
            }

            return Task.CompletedTask;
        }

        private IDisposable CreateSubscription(
            CultNetDatabaseSubscribeMessage request,
            string subscriptionId,
            CultNetServerPeer peer)
        {
            // v0 has no engine of its own (docs/cultnet-selection-cut.md, D3/D4): it lowers into a
            // Selection and is matched by the one evaluator, same as a v1 subscribe.
            var selection = new CultNetSelection
            {
                Schemas = request.SchemaIds,
                Keys = request.RecordKeys,
                Projection = CultNetSelectionProjections.Document
            };
            return CreateSubscription(selection, subscriptionId, peer);
        }

        private IDisposable CreateSubscription(
            CultNetSelection selection,
            string subscriptionId,
            CultNetServerPeer peer)
        {
            return _database.WatchAllChanges().Subscribe(change =>
            {
                var outbound = CreateChangeMessage(change, subscriptionId, selection);
                if (outbound != null)
                {
                    peer.SendCultNet(outbound);
                }
            });
        }

        internal CultNetDatabaseChangeRawMessage? CreateChangeMessage(
            object change,
            string subscriptionId,
            CultNetSelection selection)
        {
            var changeType = change.GetType();
            var key = (CultRecordKey)(changeType.GetProperty("Key")?.GetValue(change) ?? new CultRecordKey(string.Empty));
            var schemaId = (string?)changeType.GetProperty("SchemaId")?.GetValue(change) ?? string.Empty;
            var documentType = changeType.IsGenericType ? changeType.GetGenericArguments()[0] : null;
            var descriptor = documentType == null ? null : _database.Cache.Registry.GetRequired(documentType);
            var kind = (CultNetDatabaseChangeKind)(changeType.GetProperty("Kind")?.GetValue(change) ?? CultNetDatabaseChangeKind.Updated);
            var document = changeType.GetProperty("Document")?.GetValue(change);

            if (descriptor == null)
            {
                return null;
            }

            // R-M: the one binding-alias reconciler (CultNetDocumentRegistry.ExpandSchemaBindingAliases).
            var effectiveSelection = _database.Documents.ExpandSchemaBindingAliases(selection);
            if (!CultNetSelectionEvaluator.Matches(descriptor, key, document, effectiveSelection))
            {
                return null;
            }

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
                Document = _database.Documents.ToRawRecord(descriptor, key, document, DateTimeOffset.UtcNow.ToString("O"))
            };
        }

        private static string SubscriptionKey(NetPeer peer, string subscriptionId)
        {
            return $"{peer.Id}:{subscriptionId}";
        }

        private static string RequestMessageId(string messageId)
        {
            return string.IsNullOrWhiteSpace(messageId)
                ? Guid.NewGuid().ToString("N")
                : messageId;
        }

        private static CultNetErrorMessage CreateRoutingError(CultNetShardAuthorityException exception)
        {
            return new CultNetErrorMessage
            {
                Error = exception.Message,
                RoutingHint = new CultNetShardRoutingHint
                {
                    Reason = exception.Reason,
                    Shard = CultNetDatabase.ToMessage(exception.Shard)
                }
            };
        }

        /// <summary>
        /// Attempts to forward a rejected raw put to the shard owner.
        /// </summary>
        public async Task<bool> TryForwardPutAsync(
            CultNetShardAuthorityException exception,
            CultNetDocumentPutRawMessage message)
        {
            if (!_options.ForwardNonPrimaryWrites ||
                _options.WriteForwarder == null ||
                exception.Reason != "not_primary")
            {
                return false;
            }

            message.ShardId ??= exception.Shard.ShardId;
            message.ShardEpoch ??= exception.Shard.Epoch;
            await _options.WriteForwarder.ForwardPutAsync(exception.Shard, message).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Attempts to forward a rejected raw delete to the shard owner.
        /// </summary>
        public async Task<bool> TryForwardDeleteAsync(
            CultNetShardAuthorityException exception,
            CultNetDocumentDeleteMessage message)
        {
            if (!_options.ForwardNonPrimaryWrites ||
                _options.WriteForwarder == null ||
                exception.Reason != "not_primary")
            {
                return false;
            }

            message.ShardId ??= exception.Shard.ShardId;
            message.ShardEpoch ??= exception.Shard.Epoch;
            await _options.WriteForwarder.ForwardDeleteAsync(exception.Shard, message).ConfigureAwait(false);
            return true;
        }
    }
}
