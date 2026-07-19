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
