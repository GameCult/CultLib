using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly Func<CultNetHelloMessage, CultNetServerPeer, Task> _helloHandler;
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
            _helloHandler = HandleHelloAsync;
            _shardCatalogHandler = HandleShardCatalogRequestAsync;
            _shardLogHandler = HandleShardLogRequestAsync;
            _subscribeHandler = HandleSubscribeAsync;
            _subscribeHandlerV1 = HandleSubscribeV1Async;
            _unsubscribeHandler = HandleUnsubscribeAsync;

            _server.OnCultNet(_snapshotHandler);
            _server.OnCultNet(_snapshotHandlerV1);
            _server.OnCultNet(_putHandler);
            _server.OnCultNet(_deleteHandler);
            _server.OnCultNet(_helloHandler);
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

            // A database serves only what its shards own.
            return _database.Documents.CreateRawSnapshotResponse(
                _database.Cache,
                string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                request,
                options: null,
                rowFilter: _database.Owns);
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
                // A row whose schema no shard owns is not part of what this database serves: it is left out, not an error.
                options: null,
                rowFilter: _database.Owns,
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
            _server.RemoveCultNetMessageListener<CultNetHelloMessage>(_helloHandler);
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

        // The one question asked of the write authority per received write. None configured refuses; an authority that throws
        // refuses too (fail closed) and the fault is logged as an error.
        private Action<string, CultRecordKey> Authorize(
            CultNetServerPeer peer,
            CultNetRemoteWriteOperation operation,
            string? shardId,
            long? shardEpoch)
        {
            return (schemaId, key) =>
            {
                var request = new CultNetRemoteWriteRequest(peer, operation, schemaId, key, shardId, shardEpoch);
                var authority = _options.RemoteWriteAuthority;
                if (authority == null)
                {
                    throw new CultNetRemoteWriteRefusedException(request, "no remote write authority is configured");
                }

                CultNetRemoteWriteDecision decision;
                try
                {
                    decision = authority.Decide(request);
                }
                catch (Exception ex)
                {
                    _server.Logger.LogError($"CultNet remote write authority failed: {ex.Message}");
                    throw new CultNetRemoteWriteRefusedException(request, "the write authority failed");
                }

                if (decision == null || !decision.IsGranted)
                {
                    throw new CultNetRemoteWriteRefusedException(request, decision?.Reason);
                }
            };
        }

        // A hello decides the connection's write framing if it is the first thing to (write-receipt.md, "Capability"). The
        // server answers a receipt-capable hello with its own, so the client can tell a receipt server from a legacy one.
        private Task HandleHelloAsync(CultNetHelloMessage hello, CultNetServerPeer peer)
        {
            if (peer.Transport != null && peer.Transport.SettleWriteFraming(hello))
            {
                Answer(peer, new CultNetHelloMessage
                {
                    RuntimeId = "gamecult-networking-database-server",
                    RuntimeKind = "dotnet",
                    SupportedMessageVersions = [CultNetSchemaVersions.WriteAdmitted, CultNetSchemaVersions.ErrorV1]
                });
            }

            return Task.CompletedTask;
        }

        // The write's receipt state, or null when its connection did not opt in (it is then answered exactly as before).
        private CultNetReceiptedWrite? BeginReceipted(
            CultNetServerPeer peer,
            string operation,
            string? messageId,
            string? schemaId,
            string? recordKey,
            byte[]? payload)
        {
            var transport = peer.Transport;
            if (transport == null) return null;
            transport.SettleWriteFraming(null);
            var session = transport.WriteReceipts;
            return session == null
                ? null
                : new CultNetReceiptedWrite(session, message => Answer(peer, message), operation, messageId, schemaId, recordKey, payload);
        }

        // Every frame a write can be answered with. Mock point for tests: the answer leaves through the peer's transport. An
        // answer that cannot be queued ends the connection: the peer settles its write as session_ended, never as silence.
        private void Answer(CultNetServerPeer peer, ICultNetSchemaMessage message)
        {
            try
            {
                Send(peer, message);
            }
            catch (Exception ex)
            {
                _server.Logger.LogWarning($"CultNet write answer could not be sent; ending the connection: {ex.Message}");
                peer.Transport?.End();
            }
        }

        private static void Send(CultNetServerPeer peer, ICultNetSchemaMessage message)
        {
            switch (message)
            {
                case CultNetWriteAdmittedMessage admitted: peer.SendCultNet(admitted); break;
                case CultNetErrorV1Message error: peer.SendCultNet(error); break;
                case CultNetErrorMessage error: peer.SendCultNet(error); break;
                case CultNetHelloMessage hello: peer.SendCultNet(hello); break;
                default: throw new ArgumentException($"{message.GetType().Name} is not a write answer.", nameof(message));
            }
        }

        // A refusal: error.v1 carrying the write's id for a receipt-capable connection, the v0 error for any other.
        private static void Refuse(CultNetServerPeer peer, CultNetReceiptedWrite? write, CultNetErrorMessage error)
        {
            if (write != null) write.Refused(error);
            else peer.SendCultNet(error);
        }

        // Rule 6: a forward is answered with what the primary answered, under the original id. A refusal the primary sent is
        // relayed; silence or any other failure relays nothing, so the client settles it unconfirmed.
        private void RelayForwardFailure(CultNetReceiptedWrite write, Exception failure)
        {
            if (failure is CultNetWriteRefusedException refused)
            {
                write.Refused(refused.Refusal);
                return;
            }

            _server.Logger.LogWarning($"CultNet forwarded write was not answered by its primary: {failure.Message}");
            // Only a forward that provably never left (not sent) may be forwarded again. Every other failure, silence or not, is
            // unconfirmed: the primary may have applied it, so the id is never forwarded again on this session.
            if (failure is CultNetWriteNotSentException) write.NotSent();
            else write.LeftSilent();
        }

        private async Task HandlePutAsync(CultNetDocumentPutRawMessage message, CultNetServerPeer peer)
        {
            var write = BeginReceipted(peer, "put", message.MessageId, message.Document?.SchemaId, message.Document?.RecordKey, message.Document?.Payload);
            var idRefusal = write?.IdRefusal;
            if (idRefusal != null)
            {
                write!.Refused(idRefusal);
                return;
            }

            var missing = message.Document == null ? "document" : message.Document.SchemaId == null ? "schemaId" : null;
            if (missing != null)
            {
                Refuse(peer, write, CultNetErrorMessage.ForMessageInvalid(missing, $"{missing} is required"));
                return;
            }

            if (write != null && !write.Begin()) return;

            var authorize = Authorize(peer, CultNetRemoteWriteOperation.Put, message.ShardId, message.ShardEpoch);
            try
            {
                await _database.ApplyPutAsync(message, authorize).ConfigureAwait(false);
                write?.Admitted();
            }
            catch (CultNetRemoteWriteRefusedException ex)
            {
                _server.Logger.LogWarning($"CultNet raw put refused: {ex.Message}");
                Refuse(peer, write, CultNetErrorMessage.ForRemoteWriteRefused(ex.Request, ex.Reason));
            }
            catch (CultNetShardAuthorityException ex)
            {
                bool forwarded;
                try
                {
                    forwarded = await TryForwardPutAsync(ex, message).ConfigureAwait(false);
                }
                catch (CultNetWriteUnconfirmedException) when (write == null)
                {
                    // A peer that did not opt in is told nothing of a forward its primary left unanswered.
                    return;
                }
                catch (Exception failure) when (write != null)
                {
                    RelayForwardFailure(write, failure);
                    return;
                }

                if (forwarded)
                {
                    write?.Admitted();
                }
                else
                {
                    _server.Logger.LogError($"CultNet raw put authority failed: {ex.Message}");
                    Refuse(peer, write, CreateRoutingError(ex));
                }
            }
            catch (CultNetUnownedSchemaException ex)
            {
                _server.Logger.LogWarning($"CultNet raw put refused: {ex.Message}");
                Refuse(peer, write, CultNetErrorMessage.ForUnownedSchema(ex));
            }
            catch (Exception ex) when (CommittedWithLogFailure(ex))
            {
                // The write committed and was published; only its shard log failed, so the peer is not told it was refused.
                _server.Logger.LogWarning($"CultNet raw put committed but its shard log failed: {ex.Message}");
                write?.Admitted();
            }
            catch (Exception ex)
            {
                _server.Logger.LogError($"CultNet raw put failed: {ex.Message}");
                Refuse(peer, write, new CultNetErrorMessage { Error = CultNetWriteReceiptSession.WriteFailedText });
            }
        }

        private async Task HandleDeleteAsync(CultNetDocumentDeleteMessage message, CultNetServerPeer peer)
        {
            var write = BeginReceipted(peer, "delete", message.MessageId, message.SchemaId, message.RecordKey, null);
            var idRefusal = write?.IdRefusal;
            if (idRefusal != null)
            {
                write!.Refused(idRefusal);
                return;
            }

            if (message.SchemaId == null)
            {
                Refuse(peer, write, CultNetErrorMessage.ForMessageInvalid("schemaId", "schemaId is required"));
                return;
            }

            if (write != null && !write.Begin()) return;

            var authorize = Authorize(peer, CultNetRemoteWriteOperation.Delete, message.ShardId, message.ShardEpoch);
            try
            {
                await _database.ApplyDeleteAsync(message, authorize).ConfigureAwait(false);
                write?.Admitted();
            }
            catch (CultNetRemoteWriteRefusedException ex)
            {
                _server.Logger.LogWarning($"CultNet raw delete refused: {ex.Message}");
                Refuse(peer, write, CultNetErrorMessage.ForRemoteWriteRefused(ex.Request, ex.Reason));
            }
            catch (CultNetShardAuthorityException ex)
            {
                bool forwarded;
                try
                {
                    forwarded = await TryForwardDeleteAsync(ex, message).ConfigureAwait(false);
                }
                catch (CultNetWriteUnconfirmedException) when (write == null)
                {
                    return;
                }
                catch (Exception failure) when (write != null)
                {
                    RelayForwardFailure(write, failure);
                    return;
                }

                if (forwarded)
                {
                    write?.Admitted();
                }
                else
                {
                    Refuse(peer, write, CreateRoutingError(ex));
                }
            }
            catch (CultNetUnownedSchemaException ex)
            {
                _server.Logger.LogWarning($"CultNet raw delete refused: {ex.Message}");
                Refuse(peer, write, CultNetErrorMessage.ForUnownedSchema(ex));
            }
            catch (Exception ex) when (CommittedWithLogFailure(ex))
            {
                _server.Logger.LogWarning($"CultNet raw delete committed but its shard log failed: {ex.Message}");
                write?.Admitted();
            }
            catch (Exception ex)
            {
                Refuse(peer, write, new CultNetErrorMessage { Error = CultNetWriteReceiptSession.WriteFailedText });
            }
        }

        private Task HandleSubscribeAsync(CultNetDatabaseSubscribeMessage message, CultNetServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(message.SubscriptionId)
                ? message.MessageId
                : message.SubscriptionId;
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                peer.SendCultNet(CultNetErrorMessage.ForMessageInvalid("subscriptionId", "Database subscription requires a subscriptionId or messageId."));
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
                peer.SendCultNet(CultNetErrorMessage.ForMessageInvalid("subscriptionId", "Database subscription requires a subscriptionId or messageId."));
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
            // Keys this subscriber only ever received as a refusal: it was never given them as records.
            var refused = new HashSet<string>(StringComparer.Ordinal);
            return _database.WatchAllChanges().Subscribe(change =>
            {
                var outbound = DeliverChange(change, subscriptionId, selection, refused, error => peer.SendCultNet(error));
                if (outbound != null)
                    peer.SendCultNet(outbound);
            });
        }

        // One subscriber's view of one change. A variant is not a record CultNet can carry (Q6): the subscriber is told, by key, and
        // is not later told the variant was removed, because it never held it.
        internal CultNetDatabaseChangeRawMessage? DeliverChange(
            object change,
            string subscriptionId,
            CultNetSelection selection,
            HashSet<string> refused,
            Action<CultNetErrorMessage> sendError)
        {
            CultNetDatabaseChangeRawMessage? outbound;
            try
            {
                outbound = CreateChangeMessage(change, subscriptionId, selection);
            }
            catch (NotSupportedException refusal)
            {
                var refusedKey = change.GetType().GetProperty("Key")?.GetValue(change) is CultRecordKey changed ? changed.Value : string.Empty;
                lock (refused)
                    refused.Add(refusedKey);
                sendError(new CultNetErrorMessage { Error = refusal.Message });
                return null;
            }

            if (outbound == null)
                return null;
            var recordKey = outbound.RecordKey ?? outbound.Document?.RecordKey ?? string.Empty;
            bool wasRefused;
            lock (refused)
                wasRefused = refused.Remove(recordKey);
            return wasRefused && outbound.ChangeKind == "removed" ? null : outbound;
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

            CultNetDocumentRegistry.RefuseVariantChange(_database.Cache, key);
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

        // A CultNetShardLogException means the write committed and was published (the writer hears of a failed log afterwards);
        // the cache wraps it in an AggregateException when something else failed in the same hold.
        private static bool CommittedWithLogFailure(Exception exception) =>
            exception is CultNetShardLogException ||
            exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => inner is CultNetShardLogException);

        // Fixed text: the routing refusal never carries an exception's own message (the shard and reason travel in the hint).
        internal const string RoutingRefusalText = "the write belongs to another shard primary";

        private static CultNetErrorMessage CreateRoutingError(CultNetShardAuthorityException exception)
        {
            return new CultNetErrorMessage
            {
                Error = RoutingRefusalText,
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
