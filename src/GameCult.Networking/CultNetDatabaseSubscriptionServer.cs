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
    /// Selection match runs after authorization and owns both initial
    /// snapshot and live-update delivery for a peer. Projection (header vs.
    /// document) is a value on the request's selection, not a delegate.
    /// </summary>
    public sealed class CultNetDatabaseSubscriptionServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly CultNetDatabase _database;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, Task> _subscribe;
        private readonly Func<CultNetDatabaseSubscribeV1Message, ICultNetSchemaServerPeer, Task> _subscribeV1;
        private readonly Func<CultNetDatabaseUnsubscribeMessage, ICultNetSchemaServerPeer, Task> _unsubscribe;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, bool>? _authorizeRequest;
        private readonly Func<CultNetDatabaseSubscribeMessage, ICultNetSchemaServerPeer, string, string, bool>? _authorizeRecord;
        private readonly ICultNetSchemaServerPeerLifecycle? _peerLifecycle;
        private readonly ConcurrentDictionary<SubscriptionKey, IDisposable> _subscriptions =
            new ConcurrentDictionary<SubscriptionKey, IDisposable>(SubscriptionKeyComparer.Instance);
        private readonly ConcurrentDictionary<SubscriptionKey, SubscriptionRequest> _requests =
            new ConcurrentDictionary<SubscriptionKey, SubscriptionRequest>(SubscriptionKeyComparer.Instance);
        private readonly ConcurrentDictionary<SubscriptionKey, SubscriptionProjectionState> _projections =
            new ConcurrentDictionary<SubscriptionKey, SubscriptionProjectionState>(SubscriptionKeyComparer.Instance);
        private readonly object _lifecycleGate = new();
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
            _subscribeV1 = HandleSubscribeV1Async;
            _unsubscribe = HandleUnsubscribeAsync;
            _server.OnCultNet(_subscribe);
            _server.OnCultNet(_subscribeV1);
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
                _server.RemoveCultNetMessageListener<CultNetDatabaseSubscribeV1Message>(_subscribeV1);
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
            // v0 has no engine of its own (docs/cultnet-selection-cut.md, D3/D4): it lowers into a
            // Selection and is matched by the one evaluator, same as a v1 subscribe.
            var selection = new CultNetSelection
            {
                Schemas = request.SchemaIds,
                Keys = request.RecordKeys,
                Projection = CultNetSelectionProjections.Document
            };
            var subscriptionRequest = new SubscriptionRequest(
                selection, request.MessageId, request.SubscriptionId, request.IncludeSnapshot,
                request.ConsumerRuntimeId, request.BodyIds, request.SupportedBodyTransports, isV1: false);
            return Subscribe(subscriptionRequest, peer);
        }

        private Task HandleSubscribeV1Async(CultNetDatabaseSubscribeV1Message request, ICultNetSchemaServerPeer peer)
        {
            var subscriptionRequest = new SubscriptionRequest(
                request.Selection, request.MessageId, request.SubscriptionId, request.IncludeSnapshot,
                request.ConsumerRuntimeId, request.BodyIds, request.SupportedBodyTransports, isV1: true);
            return Subscribe(subscriptionRequest, peer);
        }

        // R-A: the one subscribe core both v0 (lowered) and v1 selections run through - the door
        // (validate), the watch (D6: reconcile on every change for a hop-bearing selection, the fast
        // single-row path otherwise), the initial snapshot in each version's own wire shape, and demand.
        private Task Subscribe(SubscriptionRequest request, ICultNetSchemaServerPeer peer)
        {
            var subscriptionId = string.IsNullOrWhiteSpace(request.SubscriptionId)
                ? request.MessageId
                : request.SubscriptionId;
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = "Database subscription requires a subscriptionId or messageId." });
                return Task.CompletedTask;
            }

            try
            {
                request.Selection.Validate(_database.Cache.Registry.AllDescriptors.ToArray());
            }
            catch (CultNetSelectionInvalidException ex)
            {
                peer.SendCultNet(CultNetErrorMessage.ForSelectionInvalid(ex));
                return Task.CompletedTask;
            }

            var legacyRequest = request.ToLegacyShape(subscriptionId);
            if (_authorizeRequest?.Invoke(legacyRequest, peer) == false)
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
                        SendSnapshot(peer, request, snapshot);
                    }
                    else
                    {
                        SendSnapshot(peer, request, ProjectedSnapshot.Empty);
                    }
                    PublishDemand(request, key, active: true);
                    projection.DemandActive = true;
                }
                catch (CultNetSelectionInvalidException ex)
                {
                    // R-N: a hop-bearing selection re-validates inside CreateProjectedSnapshot's
                    // EvaluateAll, so a selection that passed the door check above can still refuse here.
                    Withdraw(key, sendRemovals: false, forgetRequest: true);
                    peer.SendCultNet(CultNetErrorMessage.ForSelectionInvalid(ex));
                    return Task.CompletedTask;
                }
                catch (CultNetSelectionReferenceOutsideTargetException ex)
                {
                    Withdraw(key, sendRemovals: false, forgetRequest: true);
                    peer.SendCultNet(CultNetErrorMessage.ForReferenceOutsideTarget(ex));
                    return Task.CompletedTask;
                }
                catch
                {
                    Withdraw(key, sendRemovals: false, forgetRequest: true);
                    throw;
                }
            }
            return Task.CompletedTask;
        }

        private static void SendSnapshot(ICultNetSchemaServerPeer peer, SubscriptionRequest request, ProjectedSnapshot snapshot)
        {
            if (request.IsV1)
            {
                var wantDocument = request.Selection.Projection == CultNetSelectionProjections.Document;
                peer.SendCultNet(new CultNetSnapshotResponseRawV1Message
                {
                    MessageId = request.MessageId,
                    Matched = (uint)snapshot.Documents.Length,
                    Documents = wantDocument ? snapshot.Documents : null,
                    Headers = wantDocument ? null : snapshot.Documents.Select(CultNetRawDocumentHeader.FromRecord).ToArray(),
                    Edges = request.Selection.HasHop ? snapshot.Edges : null
                });
            }
            else
            {
                peer.SendCultNet(new CultNetSnapshotResponseRawMessage
                {
                    MessageId = request.MessageId,
                    Documents = snapshot.Documents
                });
            }
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
            SubscriptionRequest request,
            SubscriptionKey key,
            bool active)
        {
            DemandChanged?.Invoke(new CultNetDatabaseSubscriptionDemand(
                string.IsNullOrWhiteSpace(request.ConsumerRuntimeId) ? key.Id : request.ConsumerRuntimeId!,
                key.Id,
                (request.Selection.Keys ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                (request.Selection.Schemas ?? Array.Empty<string>())
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
            SubscriptionRequest request,
            string subscriptionId,
            ICultNetSchemaServerPeer peer,
            SubscriptionKey key)
        {
            var legacyRequest = request.ToLegacyShape(subscriptionId);
            return _database.WatchAllChanges().Subscribe(change =>
            {
                lock (_lifecycleGate)
                {
                    if (_disposed || !_requests.ContainsKey(key) || !_projections.TryGetValue(key, out var projection))
                        return;
                    if (_authorizeRequest?.Invoke(legacyRequest, peer) == false)
                    {
                        Reconcile(key, request);
                        return;
                    }

                    // D6: cites/cited are set-dependent - a change to row B can change whether row A
                    // matches - so a hop-bearing selection runs the full diff-against-delivered
                    // reconcile on every change instead of the single-row fast path.
                    if (request.Selection.HasHop)
                    {
                        Reconcile(key, request);
                        return;
                    }

                    var sourceRecordKey = ResolveChangeRecordKey(change);
                    var matched = CreateMatchedRecord(change, request, peer);
                    ApplyProjectedChange(
                        projection,
                        sourceRecordKey,
                        matched,
                        peer,
                        subscriptionId);
                }
            });
        }

        private void Reconcile(SubscriptionKey key, SubscriptionRequest request)
        {
            if (!_projections.TryGetValue(key, out var projection)) return;
            var legacyRequest = request.ToLegacyShape(key.Id);
            var authorized = _authorizeRequest?.Invoke(legacyRequest, key.Peer) != false;
            var next = authorized
                ? CreateProjectedSnapshot(request, key.Peer)
                : ProjectedSnapshot.Empty;

            // With _projectRecord gone (docs/cultnet-selection-cut.md, S2-2), a projection's dictionary
            // key is always the record's own RecordKey/SchemaId, so the two can never disagree here;
            // the identity branch that used to catch a re-projected key is dead and deleted.
            foreach (var previous in projection.DeliveredBySourceRecordKey.ToArray())
            {
                if (!next.BySourceRecordKey.TryGetValue(previous.Key, out var current))
                {
                    SendRemoval(key.Peer, key.Id, previous.Value);
                    continue;
                }
                if (!Equivalent(previous.Value, current))
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
            SubscriptionRequest request,
            ICultNetSchemaServerPeer peer)
        {
            var legacyRequest = request.ToLegacyShape(request.SubscriptionId);
            // Both v0 (lowered) and v1 selections evaluate through SelectAll: one evaluation over the
            // whole matching set (R-G), documents always (this projects the wire record regardless of
            // the selection's own header/document projection - SendSnapshot applies that afterward).
            var documentSelection = new CultNetSelection
            {
                Schemas = request.Selection.Schemas,
                Keys = request.Selection.Keys,
                Fields = request.Selection.Fields,
                Cites = request.Selection.Cites,
                Cited = request.Selection.Cited,
                Projection = CultNetSelectionProjections.Document
            };
            var page = _database.Documents.SelectAll(
                _database.Cache,
                documentSelection,
                ordinalOf: (schemaId, key) => _database.LastWriteSequence(schemaId, key) ?? 0,
                asOf: _database.CurrentAsOf(),
                options: null);
            // The snapshot is one row per record key by construction, so the duplicate-key throw this
            // used to guard can no longer fire (docs/cultnet-selection-cut.md, S2-2); deleted rather
            // than tested around.
            var bySourceRecordKey = new Dictionary<string, CultNetRawDocumentRecord>(StringComparer.Ordinal);
            foreach (var source in page.Documents ?? Array.Empty<CultNetRawDocumentRecord>())
            {
                var sourceRecordKey = source.RecordKey;
                if (_authorizeRecord?.Invoke(legacyRequest, peer, sourceRecordKey, source.SchemaId) == false)
                    continue;
                bySourceRecordKey[sourceRecordKey] = source;
            }
            return new ProjectedSnapshot(bySourceRecordKey, page.Edges ?? Array.Empty<CultNetEdge>());
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
            // Same S2-2 fact as Reconcile: previous/current are looked up by the record's own key, so
            // they can never disagree in RecordKey/SchemaId here either.
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

        // One evaluator (docs/cultnet-selection-cut.md, D3/D4): a v0 subscribe request lowers into a
        // Selection with no engine of its own, matched and projected exactly as a v1 request would be.
        // Only called for a non-hop selection (Watch routes a hop-bearing one to Reconcile instead, per
        // D6) - CultNetSelectionEvaluator.Matches refuses a hop-bearing selection on this fast path.
        // Record-level authorization (_authorizeRecord) is not selection and stays layered on top.
        private CultNetRawDocumentRecord? CreateMatchedRecord(
            object change,
            SubscriptionRequest request,
            ICultNetSchemaServerPeer peer)
        {
            var changeType = change.GetType();
            var key = (CultRecordKey)(changeType.GetProperty("Key")?.GetValue(change) ?? new CultRecordKey(""));
            var kind = (CultNetDatabaseChangeKind)(changeType.GetProperty("Kind")?.GetValue(change) ?? CultNetDatabaseChangeKind.Updated);
            var document = changeType.GetProperty("Document")?.GetValue(change);
            var forDescriptor = document ?? changeType.GetProperty("PreviousDocument")?.GetValue(change);
            if (forDescriptor == null)
                return null;

            var descriptor = _database.Cache.Registry.GetRequired(forDescriptor.GetType());
            var selection = new CultNetSelection
            {
                Schemas = request.Selection.Schemas,
                Keys = request.Selection.Keys,
                Fields = request.Selection.Fields,
                Projection = CultNetSelectionProjections.Document
            };
            // R-M: the one binding-alias reconciler (CultNetDocumentRegistry.ExpandSchemaBindingAliases).
            var effectiveSelection = _database.Documents.ExpandSchemaBindingAliases(selection);
            if (!CultNetSelectionEvaluator.Matches(descriptor, key, document, effectiveSelection))
                return null;
            var legacyRequest = request.ToLegacyShape(request.SubscriptionId);
            // R-P: one id reaches the authorizer, and it is the wire id, on every path. The snapshot
            // path (CreateProjectedSnapshot, below) authorizes against the raw record's own SchemaId,
            // which is the wire id ToRawRecord/binding emit; this live fast path used to pass
            // descriptor.SchemaId instead - the CLR type's own registered id, not the id a binding
            // overrides to - so a row delivered by the snapshot could be refused on its first live
            // change, or leak through a denylist keyed on the wire id.
            if (_authorizeRecord?.Invoke(legacyRequest, peer, key.Value, _database.Documents.WireSchemaId(descriptor)) == false)
                return null;

            if (kind == CultNetDatabaseChangeKind.Removed || document == null)
                return null;

            return _database.Documents.ToRawRecord(descriptor, key, document, DateTimeOffset.UtcNow.ToString("O"));
        }

        // R-A: the one internal shape both a lowered v0 subscribe and a v1 subscribe carry through
        // Subscribe/Watch/Reconcile/CreateProjectedSnapshot/CreateMatchedRecord/PublishDemand. The
        // authorize/authorizeRecord delegates keep their existing public constructor contract (typed
        // against the v0 message) rather than becoming a breaking API change in this fix batch;
        // ToLegacyShape carries every field those delegates read (schemas/keys/consumer/body demand) -
        // fields/cites/cited reach neither delegate under v0 today either, so nothing regresses.
        private sealed class SubscriptionRequest
        {
            public SubscriptionRequest(
                CultNetSelection selection,
                string messageId,
                string subscriptionId,
                bool includeSnapshot,
                string? consumerRuntimeId,
                string[]? bodyIds,
                string[]? supportedBodyTransports,
                bool isV1)
            {
                Selection = selection;
                MessageId = messageId;
                SubscriptionId = subscriptionId;
                IncludeSnapshot = includeSnapshot;
                ConsumerRuntimeId = consumerRuntimeId;
                BodyIds = bodyIds;
                SupportedBodyTransports = supportedBodyTransports;
                IsV1 = isV1;
            }

            public CultNetSelection Selection { get; }
            public string MessageId { get; }
            public string SubscriptionId { get; }
            public bool IncludeSnapshot { get; }
            public string? ConsumerRuntimeId { get; }
            public string[]? BodyIds { get; }
            public string[]? SupportedBodyTransports { get; }
            public bool IsV1 { get; }

            public CultNetDatabaseSubscribeMessage ToLegacyShape(string subscriptionId) => new()
            {
                MessageId = MessageId,
                SubscriptionId = subscriptionId,
                SchemaIds = Selection.Schemas,
                RecordKeys = Selection.Keys,
                IncludeSnapshot = IncludeSnapshot,
                ConsumerRuntimeId = ConsumerRuntimeId,
                BodyIds = BodyIds,
                SupportedBodyTransports = SupportedBodyTransports
            };
        }

        private sealed class SubscriptionProjectionState
        {
            public Dictionary<string, CultNetRawDocumentRecord> DeliveredBySourceRecordKey { get; } =
                new(StringComparer.Ordinal);
            public bool DemandActive { get; set; }
        }

        private sealed class ProjectedSnapshot
        {
            public ProjectedSnapshot(Dictionary<string, CultNetRawDocumentRecord> bySourceRecordKey, CultNetEdge[]? edges = null)
            {
                BySourceRecordKey = bySourceRecordKey;
                Documents = bySourceRecordKey.Values.ToArray();
                Edges = edges ?? Array.Empty<CultNetEdge>();
            }

            public static ProjectedSnapshot Empty { get; } =
                new(new Dictionary<string, CultNetRawDocumentRecord>(StringComparer.Ordinal));
            public Dictionary<string, CultNetRawDocumentRecord> BySourceRecordKey { get; }
            public CultNetEdge[] Edges { get; }
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
