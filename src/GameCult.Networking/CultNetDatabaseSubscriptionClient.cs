using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;

namespace GameCult.Networking
{
    /// <summary>Controls whether subscription payloads become replica state or remain ephemeral typed events.</summary>
    public enum CultNetDatabaseSubscriptionDeliveryMode
    {
        /// <summary>Decode each payload and write it into the local CultCache before notifying observers.</summary>
        ReplicateToCache,
        /// <summary>Decode each payload and notify observers without writing replica state.</summary>
        Live
    }

    /// <summary>
    /// Replicates selected remote database records into a local typed CultCache over one retained session.
    /// </summary>
    public sealed class CultNetDatabaseSubscriptionClient : IDisposable
    {
        private readonly ICultNetSchemaClient _client;
        private readonly CultCache _cache;
        private readonly CultNetDocumentRegistry _documents;
        private readonly ConcurrentDictionary<string, PendingSubscription> _initialSnapshots = new();
        private readonly ConcurrentDictionary<string, CultNetDatabaseSubscriptionDeliveryMode> _deliveryModes =
            new(StringComparer.Ordinal);
        private readonly object _queueLock = new();
        private Task _queue = Task.CompletedTask;
        private bool _disposed;

        public CultNetDatabaseSubscriptionClient(
            ICultNetSchemaClient client,
            CultCache cache,
            CultNetDocumentRegistry documents)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _documents = documents ?? throw new ArgumentNullException(nameof(documents));
            _client.OnCultNet<CultNetSnapshotResponseRawMessage>(HandleSnapshot);
            _client.OnCultNet<CultNetDatabaseChangeRawMessage>(HandleChange);
        }

        /// <summary>Raised after a live change has been applied to the local typed cache.</summary>
        public event Action<CultNetReplicatedDocumentChange>? Changed;

        /// <summary>Gets the local reactive cache receiving replicated documents.</summary>
        public CultCache Cache => _cache;

        /// <summary>Completes if the retained transport pump terminates with an unrecoverable error.</summary>
        public Task<Exception>? BackgroundFailure =>
            (_client as RudpCultNetSchemaClient)?.BackgroundFailure;

        /// <summary>
        /// Subscribes to one exact typed record as an ephemeral reactive value. The returned value
        /// owns filtering and unsubscribe; no received payload is written into the local cache.
        /// </summary>
        public async Task<CultNetLiveValue<TDocument>> SubscribeLiveValueAsync<TDocument>(
            string subscriptionId,
            string recordKey,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(recordKey))
                throw new ArgumentException("Record key is required.", nameof(recordKey));
            var schemaId = _documents.GetByDocumentType(typeof(TDocument))?.SchemaId ??
                _cache.Registry.GetRequired<TDocument>().SchemaId;
            var value = new CultNetLiveValue<TDocument>(this, subscriptionId, recordKey);
            try
            {
                var initial = await SubscribeAsync(
                        subscriptionId,
                        recordKeys: new[] { recordKey },
                        schemaIds: new[] { schemaId },
                        deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var documents = initial.OfType<TDocument>().ToArray();
                if (documents.Length > 1)
                    throw new InvalidOperationException(
                        $"Live value subscription '{subscriptionId}' expected at most one '{schemaId}' record '{recordKey}' but received {documents.Length}.");
                if (documents.Length == 1)
                    value.Initialize(documents[0]);
                return value;
            }
            catch
            {
                value.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Starts a remote subscription and waits until its initial matching snapshot is in the local cache.
        /// </summary>
        public Task<IReadOnlyList<object>> SubscribeAsync(
            string subscriptionId,
            IEnumerable<string>? recordKeys = null,
            IEnumerable<string>? schemaIds = null,
            bool includeSnapshot = true,
            CancellationToken cancellationToken = default,
            string? consumerRuntimeId = null,
            IEnumerable<string>? bodyIds = null,
            IEnumerable<string>? supportedBodyTransports = null,
            CultNetDatabaseSubscriptionDeliveryMode deliveryMode = CultNetDatabaseSubscriptionDeliveryMode.ReplicateToCache)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultNetDatabaseSubscriptionClient));
            if (!_client.Connected) throw new InvalidOperationException("CultNet schema client must be connected before subscribing.");
            if (string.IsNullOrWhiteSpace(subscriptionId)) throw new ArgumentException("Subscription id is required.", nameof(subscriptionId));

            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<IReadOnlyList<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_initialSnapshots.TryAdd(messageId, new PendingSubscription(subscriptionId, deliveryMode, completion)))
                throw new InvalidOperationException($"Duplicate database subscription message id '{messageId}'.");

            CancellationTokenRegistration cancellation = default;
            if (cancellationToken.CanBeCanceled)
            {
                cancellation = cancellationToken.Register(() =>
                {
                    if (_initialSnapshots.TryRemove(messageId, out var pending))
                        pending.Completion.TrySetCanceled(cancellationToken);
                });
                _ = completion.Task.ContinueWith(
                    _ => cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                _client.SendCultNet(new CultNetDatabaseSubscribeMessage
                {
                    MessageId = messageId,
                    SubscriptionId = subscriptionId,
                    RecordKeys = recordKeys?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray(),
                    SchemaIds = schemaIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray(),
                    IncludeSnapshot = includeSnapshot,
                    ConsumerRuntimeId = consumerRuntimeId,
                    BodyIds = bodyIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray(),
                    SupportedBodyTransports = supportedBodyTransports?
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                });
            }
            catch
            {
                if (_initialSnapshots.TryRemove(messageId, out var pending))
                    pending.Completion.TrySetException(new InvalidOperationException("CultNet subscription request could not be sent."));
                throw;
            }
            return completion.Task;
        }

        /// <summary>Stops one live remote subscription.</summary>
        public void Unsubscribe(string subscriptionId)
        {
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(subscriptionId)) throw new ArgumentException("Subscription id is required.", nameof(subscriptionId));
            _deliveryModes.TryRemove(subscriptionId, out _);
            _client.SendCultNet(new CultNetDatabaseUnsubscribeMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscriptionId
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _initialSnapshots.Values)
                pending.Completion.TrySetException(new ObjectDisposedException(nameof(CultNetDatabaseSubscriptionClient)));
            _initialSnapshots.Clear();
            _deliveryModes.Clear();
            _client.Dispose();
        }

        private void HandleSnapshot(CultNetSnapshotResponseRawMessage message)
        {
            Enqueue(async () =>
            {
                if (!_initialSnapshots.TryRemove(message.MessageId, out var pending)) return;
                try
                {
                    IReadOnlyList<object> applied;
                    if (pending.DeliveryMode == CultNetDatabaseSubscriptionDeliveryMode.Live)
                    {
                        applied = message.Documents.Select(_documents.DeserializeRawDocument).ToArray();
                    }
                    else
                    {
                        applied = await _documents.ApplyRawSnapshotResponseAsync(_cache, message).ConfigureAwait(false);
                    }
                    _deliveryModes[pending.SubscriptionId] = pending.DeliveryMode;
                    pending.Completion.TrySetResult(applied);
                }
                catch (Exception error)
                {
                    pending.Completion.TrySetException(error);
                }
            });
        }

        private void HandleChange(CultNetDatabaseChangeRawMessage message)
        {
            Enqueue(async () =>
            {
                var deliveryMode = _deliveryModes.TryGetValue(message.SubscriptionId, out var configured)
                    ? configured
                    : CultNetDatabaseSubscriptionDeliveryMode.ReplicateToCache;
                if (string.Equals(message.ChangeKind, "removed", StringComparison.Ordinal))
                {
                    if (deliveryMode == CultNetDatabaseSubscriptionDeliveryMode.ReplicateToCache)
                        await DeleteAsync(message).ConfigureAwait(false);
                    Changed?.Invoke(new CultNetReplicatedDocumentChange(
                        message.SubscriptionId,
                        "removed",
                        message.RecordKey ?? string.Empty,
                        message.SchemaId ?? string.Empty,
                        null));
                    return;
                }

                if (message.Document == null) return;
                var document = deliveryMode == CultNetDatabaseSubscriptionDeliveryMode.Live
                    ? _documents.DeserializeRawDocument(message.Document)
                    : await _documents.ApplyRawDocumentPutMessageAsync(
                        _cache,
                        new CultNetDocumentPutRawMessage
                        {
                            MessageId = message.MessageId,
                            Document = message.Document
                        }).ConfigureAwait(false);
                Changed?.Invoke(new CultNetReplicatedDocumentChange(
                    message.SubscriptionId,
                    message.ChangeKind,
                    message.Document.RecordKey,
                    message.Document.SchemaId,
                    document));
            });
        }

        private async Task DeleteAsync(CultNetDatabaseChangeRawMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.RecordKey) || string.IsNullOrWhiteSpace(message.SchemaId)) return;
            var descriptor = _documents.ResolveDescriptorForSchemaId(message.SchemaId!);
            var handle = Activator.CreateInstance(
                typeof(CultRecordHandle<>).MakeGenericType(descriptor.DocumentType),
                new object[] { new CultRecordKey(message.RecordKey!) });
            var task = (Task)typeof(CultCache)
                .GetMethod(nameof(CultCache.DeleteAsync), BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(descriptor.DocumentType)
                .Invoke(_cache, new[] { handle })!;
            await task.ConfigureAwait(false);
        }

        private void Enqueue(Func<Task> operation)
        {
            lock (_queueLock)
            {
                _queue = _queue.ContinueWith(
                    _ => operation(),
                    TaskScheduler.Default).Unwrap();
            }
        }

        private sealed class PendingSubscription
        {
            public PendingSubscription(
                string subscriptionId,
                CultNetDatabaseSubscriptionDeliveryMode deliveryMode,
                TaskCompletionSource<IReadOnlyList<object>> completion)
            {
                SubscriptionId = subscriptionId;
                DeliveryMode = deliveryMode;
                Completion = completion;
            }

            public string SubscriptionId { get; }
            public CultNetDatabaseSubscriptionDeliveryMode DeliveryMode { get; }
            public TaskCompletionSource<IReadOnlyList<object>> Completion { get; }
        }
    }

    /// <summary>One exact typed remote record kept as an ephemeral current value.</summary>
    public sealed class CultNetLiveValue<TDocument> : IDisposable where TDocument : class
    {
        private readonly CultNetDatabaseSubscriptionClient _owner;
        private readonly string _subscriptionId;
        private readonly string _recordKey;
        private readonly object _gate = new();
        private TDocument? _current;
        private bool _hasValue;
        private bool _disposed;

        internal CultNetLiveValue(
            CultNetDatabaseSubscriptionClient owner,
            string subscriptionId,
            string recordKey)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _subscriptionId = string.IsNullOrWhiteSpace(subscriptionId)
                ? throw new ArgumentException("Subscription id is required.", nameof(subscriptionId))
                : subscriptionId;
            _recordKey = recordKey;
            _owner.Changed += OnChanged;
        }

        /// <summary>Raised when the provider publishes a new typed value.</summary>
        public event Action<TDocument>? Changed;

        /// <summary>Raised when the provider removes the selected record.</summary>
        public event Action? Removed;

        /// <summary>Gets whether the selected record currently exists.</summary>
        public bool HasValue
        {
            get { lock (_gate) return _hasValue; }
        }

        /// <summary>Gets the current value, or fails when the record has not been published.</summary>
        public TDocument Current
        {
            get
            {
                lock (_gate)
                    return _hasValue
                        ? _current!
                        : throw new InvalidOperationException(
                            $"Live value '{_recordKey}' is not currently published.");
            }
        }

        internal void Initialize(TDocument document)
        {
            lock (_gate)
            {
                if (_disposed || _hasValue) return;
                _current = document;
                _hasValue = true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _owner.Changed -= OnChanged;
            _owner.Unsubscribe(_subscriptionId);
        }

        private void OnChanged(CultNetReplicatedDocumentChange change)
        {
            if (!string.Equals(change.SubscriptionId, _subscriptionId, StringComparison.Ordinal) ||
                !string.Equals(change.RecordKey, _recordKey, StringComparison.Ordinal))
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

            if (change.Document is not TDocument document)
                return;
            lock (_gate)
            {
                if (_disposed) return;
                _current = document;
                _hasValue = true;
            }
            Changed?.Invoke(document);
        }
    }

    /// <summary>Describes one typed document change after local replication.</summary>
    public sealed class CultNetReplicatedDocumentChange
    {
        public CultNetReplicatedDocumentChange(
            string subscriptionId,
            string changeKind,
            string recordKey,
            string schemaId,
            object? document)
        {
            SubscriptionId = subscriptionId;
            ChangeKind = changeKind;
            RecordKey = recordKey;
            SchemaId = schemaId;
            Document = document;
        }

        public string SubscriptionId { get; }
        public string ChangeKind { get; }
        public string RecordKey { get; }
        public string SchemaId { get; }
        public object? Document { get; }
    }
}
