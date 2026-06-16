using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Fetches committed shard-log entries from an authoritative shard owner.
    /// </summary>
    public interface ICultNetShardLogFetcher
    {
        /// <summary>
        /// Fetches shard-log entries after the supplied sequence.
        /// </summary>
        Task<CultNetShardLogResponseMessage> FetchAsync(
            CultNetShardDescriptor shard,
            long afterSequence,
            int? limit = null);
    }

    /// <summary>
    /// Fetches shard-bounded snapshots from an authoritative shard owner.
    /// </summary>
    public interface ICultNetShardSnapshotFetcher
    {
        /// <summary>
        /// Fetches a shard snapshot.
        /// </summary>
        Task<CultNetSnapshotResponseRawMessage> FetchAsync(CultNetShardDescriptor shard);
    }

    /// <summary>
    /// Persisted replica cursor for one shard.
    /// </summary>
    [MessagePackObject]
    public sealed class CultNetShardReplicaCursor
    {
        /// <summary>
        /// Gets or sets the shard id.
        /// </summary>
        [Key("shardId")] public string ShardId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard epoch.
        /// </summary>
        [Key("shardEpoch")] public long ShardEpoch { get; set; }
        /// <summary>
        /// Gets or sets the last applied shard-log sequence.
        /// </summary>
        [Key("lastAppliedSequence")] public long LastAppliedSequence { get; set; }
        /// <summary>
        /// Gets or sets the cursor update timestamp.
        /// </summary>
        [Key("updatedAt")] public string UpdatedAt { get; set; } = string.Empty;
    }

    /// <summary>
    /// Stores restart-safe replica cursors.
    /// </summary>
    public interface ICultNetShardReplicaCursorStore
    {
        /// <summary>
        /// Reads the cursor for a shard, if one exists.
        /// </summary>
        Task<CultNetShardReplicaCursor?> ReadAsync(string shardId);

        /// <summary>
        /// Writes the cursor for a shard.
        /// </summary>
        Task WriteAsync(CultNetShardReplicaCursor cursor);
    }

    /// <summary>
    /// Options for background shard-log replication.
    /// </summary>
    public sealed class CultNetShardReplicatorOptions
    {
        /// <summary>
        /// Gets or sets the interval used by the background pull loop.
        /// </summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets the maximum number of log entries requested per pull.
        /// </summary>
        public int? BatchSize { get; set; } = 256;

        /// <summary>
        /// Gets or sets the fetcher used to read from shard primaries.
        /// </summary>
        public ICultNetShardLogFetcher? Fetcher { get; set; }

        /// <summary>
        /// Gets or sets the fetcher used when log history has been compacted.
        /// </summary>
        public ICultNetShardSnapshotFetcher? SnapshotFetcher { get; set; }

        /// <summary>
        /// Gets or sets a callback for background pull failures.
        /// </summary>
        public Action<Exception>? OnError { get; set; }

        /// <summary>
        /// Gets or sets optional restart-safe cursor storage.
        /// </summary>
        public ICultNetShardReplicaCursorStore? CursorStore { get; set; }
    }

    /// <summary>
    /// Pulls committed shard logs from primaries and applies them to a local replica database.
    /// </summary>
    public sealed class CultNetShardReplicator : IDisposable
    {
        private readonly CultNetDatabase _database;
        private readonly CultNetShardReplicatorOptions _options;
        private readonly List<IDisposable> _subscriptions = new();
        private readonly ConcurrentDictionary<string, byte> _pullsInFlight = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>
        /// Creates a shard replicator.
        /// </summary>
        public CultNetShardReplicator(
            CultNetDatabase database,
            CultNetShardReplicatorOptions? options = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _options = options ?? new CultNetShardReplicatorOptions();
        }

        /// <summary>
        /// Starts background pull loops for non-primary shards with advertised primaries.
        /// </summary>
        public void Start()
        {
            ThrowIfDisposed();
            if (_options.Fetcher == null)
            {
                throw new InvalidOperationException("A shard log fetcher is required before starting replication.");
            }

            if (_subscriptions.Count > 0)
            {
                return;
            }

            foreach (var shard in _database.Shards.Where(shard => !shard.IsPrimary && shard.PrimaryEndpoints.Count > 0))
            {
                _subscriptions.Add(new Timer(
                    _ => _ = PullIfIdleAsync(shard),
                    state: null,
                    dueTime: TimeSpan.Zero,
                    period: _options.PollInterval));
            }
        }

        /// <summary>
        /// Pulls and applies one batch for a shard id.
        /// </summary>
        public Task<long> PullOnceAsync(string shardId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            var shard = _database.Shards.FirstOrDefault(candidate =>
                string.Equals(candidate.ShardId, shardId, StringComparison.Ordinal));
            if (shard == null)
            {
                throw new InvalidOperationException($"Shard '{shardId}' is not known by this database.");
            }

            return PullOnceAsync(shard);
        }

        /// <summary>
        /// Pulls and applies one batch for a shard.
        /// </summary>
        public async Task<long> PullOnceAsync(CultNetShardDescriptor shard)
        {
            ThrowIfDisposed();
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            if (_options.Fetcher == null)
            {
                throw new InvalidOperationException("A shard log fetcher is required before pulling replication.");
            }

            if (shard.IsPrimary)
            {
                throw new InvalidOperationException($"Shard '{shard.ShardId}' is primary on this node and does not need replica pulling.");
            }

            var afterSequence = await GetAfterSequenceAsync(shard).ConfigureAwait(false);
            var response = await _options.Fetcher
                .FetchAsync(shard, afterSequence, _options.BatchSize)
                .ConfigureAwait(false);
            if (response.ResyncRequired &&
                string.Equals(response.Reason, "compacted_history", StringComparison.Ordinal))
            {
                if (_options.SnapshotFetcher == null)
                {
                    throw new InvalidOperationException(
                        $"Shard '{shard.ShardId}' requires snapshot resync, but no shard snapshot fetcher is configured.");
                }

                var snapshot = await _options.SnapshotFetcher.FetchAsync(shard).ConfigureAwait(false);
                var snapshotSequence = await _database.ApplyShardSnapshotResponseAsync(shard, snapshot).ConfigureAwait(false);
                await WriteCursorAsync(shard, snapshotSequence).ConfigureAwait(false);
                return snapshotSequence;
            }

            var appliedSequence = await _database.ApplyShardLogResponseAsync(response).ConfigureAwait(false);
            await WriteCursorAsync(shard, appliedSequence).ConfigureAwait(false);
            return appliedSequence;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
            _pullsInFlight.Clear();
        }

        private async Task PullIfIdleAsync(CultNetShardDescriptor shard)
        {
            if (!_pullsInFlight.TryAdd(shard.ShardId, 0))
            {
                return;
            }

            try
            {
                await PullOnceAsync(shard).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _options.OnError?.Invoke(ex);
            }
            finally
            {
                _pullsInFlight.TryRemove(shard.ShardId, out _);
            }
        }

        private async Task<long> GetAfterSequenceAsync(CultNetShardDescriptor shard)
        {
            var afterSequence = _database.GetAppliedShardSequence(shard.ShardId);
            if (afterSequence > 0 || _options.CursorStore == null)
            {
                return afterSequence;
            }

            var cursor = await _options.CursorStore.ReadAsync(shard.ShardId).ConfigureAwait(false);
            if (cursor == null || cursor.ShardEpoch != shard.Epoch)
            {
                return afterSequence;
            }

            _database.SetAppliedShardSequence(shard.ShardId, cursor.LastAppliedSequence);
            return cursor.LastAppliedSequence;
        }

        private async Task WriteCursorAsync(CultNetShardDescriptor shard, long appliedSequence)
        {
            if (_options.CursorStore == null)
            {
                return;
            }

            await _options.CursorStore.WriteAsync(new CultNetShardReplicaCursor
            {
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch,
                LastAppliedSequence = appliedSequence,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            }).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultNetShardReplicator));
            }
        }
    }

    /// <summary>
    /// Stores replica cursors in one local MessagePack file.
    /// </summary>
    public sealed class CultNetFileShardReplicaCursorStore : ICultNetShardReplicaCursorStore
    {
        private readonly string _filePath;
        private readonly object _gate = new();

        /// <summary>
        /// Creates a file-backed cursor store.
        /// </summary>
        public CultNetFileShardReplicaCursorStore(string filePath)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? throw new ArgumentException("Value must be non-empty.", nameof(filePath))
                : filePath;
        }

        /// <inheritdoc />
        public Task<CultNetShardReplicaCursor?> ReadAsync(string shardId)
        {
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            lock (_gate)
            {
                CultNetShardReplicaCursor? cursor = ReadAll().FirstOrDefault(candidate =>
                    string.Equals(candidate.ShardId, shardId, StringComparison.Ordinal));
                return Task.FromResult<CultNetShardReplicaCursor?>(cursor);
            }
        }

        /// <inheritdoc />
        public Task WriteAsync(CultNetShardReplicaCursor cursor)
        {
            if (cursor == null) throw new ArgumentNullException(nameof(cursor));
            if (string.IsNullOrWhiteSpace(cursor.ShardId))
            {
                throw new ArgumentException("Cursor requires a shardId.", nameof(cursor));
            }

            lock (_gate)
            {
                var cursors = ReadAll()
                    .Where(existing => !string.Equals(existing.ShardId, cursor.ShardId, StringComparison.Ordinal))
                    .Concat(new[] { cursor })
                    .OrderBy(existing => existing.ShardId, StringComparer.Ordinal)
                    .ToArray();
                var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(_filePath, MessagePackSerializer.Serialize(cursors));
                return Task.CompletedTask;
            }
        }

        private CultNetShardReplicaCursor[] ReadAll()
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<CultNetShardReplicaCursor>();
            }

            var bytes = File.ReadAllBytes(_filePath);
            return bytes.Length == 0
                ? Array.Empty<CultNetShardReplicaCursor>()
                : MessagePackSerializer.Deserialize<CultNetShardReplicaCursor[]>(bytes);
        }
    }

    /// <summary>
    /// Options for the schema-v0 client-based shard-log fetcher.
    /// </summary>
    public sealed class CultNetSchemaShardLogFetcherOptions
    {
        /// <summary>
        /// Gets or sets client security options used to connect to primary endpoints.
        /// </summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets how long to wait for a connection before failing.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets how long to wait for a log response before failing.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets a callback used to customize each ephemeral fetch client.
        /// </summary>
        public Action<Client>? ConfigureClient { get; set; }

        /// <summary>
        /// Gets or sets the schema-v0 client factory. Defaults to the C# LiteNetLib adapter.
        /// </summary>
        public Func<ICultNetSchemaClient>? CreateClient { get; set; }
    }

    /// <summary>
    /// Fetches shard-log batches over the CultNet schema-v0 client transport.
    /// </summary>
    public sealed class CultNetSchemaShardLogFetcher : ICultNetShardLogFetcher
    {
        private readonly CultNetSchemaShardLogFetcherOptions _options;

        /// <summary>
        /// Creates a schema-v0 shard-log fetcher.
        /// </summary>
        public CultNetSchemaShardLogFetcher(CultNetSchemaShardLogFetcherOptions? options = null)
        {
            _options = options ?? new CultNetSchemaShardLogFetcherOptions();
        }

        /// <inheritdoc />
        public async Task<CultNetShardLogResponseMessage> FetchAsync(
            CultNetShardDescriptor shard,
            long afterSequence,
            int? limit = null)
        {
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            var endpoint = ResolvePrimaryEndpoint(shard);
            var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(endpoint);
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultNetShardLogResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var client = CreateClient();
            client.OnCultNet<CultNetShardLogResponseMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                {
                    completion.TrySetResult(response);
                }
            });
            client.OnCultNet<CultNetErrorMessage>(error =>
                completion.TrySetException(new InvalidOperationException(error.Error)));

            client.Connect(host, port);
            await WaitForConnectionAsync(client, endpoint).ConfigureAwait(false);
            client.SendCultNet(new CultNetShardLogRequestMessage
            {
                MessageId = messageId,
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch,
                AfterSequence = afterSequence,
                Limit = limit
            });

            return await WaitForResponseAsync(completion.Task, endpoint).ConfigureAwait(false);
        }

        private ICultNetSchemaClient CreateClient()
        {
            return _options.CreateClient?.Invoke()
                   ?? CultNetSchemaClients.CreateLiteNetLib(_options.Security, _options.ConfigureClient);
        }

        private async Task WaitForConnectionAsync(ICultNetSchemaClient client, string endpoint)
        {
            var deadline = DateTimeOffset.UtcNow + _options.ConnectTimeout;
            while (!client.Connected)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out connecting to shard primary endpoint {endpoint}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        private async Task<CultNetShardLogResponseMessage> WaitForResponseAsync(
            Task<CultNetShardLogResponseMessage> responseTask,
            string endpoint)
        {
            var timeoutTask = Task.Delay(_options.ResponseTimeout);
            var completed = await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false);
            if (completed != responseTask)
            {
                throw new TimeoutException($"Timed out waiting for shard log response from {endpoint}.");
            }

            return await responseTask.ConfigureAwait(false);
        }

        private static string ResolvePrimaryEndpoint(CultNetShardDescriptor shard)
        {
            return shard.PrimaryEndpoints.FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       $"Shard '{shard.ShardId}' does not advertise a primary endpoint.");
        }
    }

    /// <summary>
    /// Options for the schema-v0 client-based shard snapshot fetcher.
    /// </summary>
    public sealed class CultNetSchemaShardSnapshotFetcherOptions
    {
        /// <summary>
        /// Gets or sets client security options used to connect to primary endpoints.
        /// </summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets how long to wait for a connection before failing.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets how long to wait for a snapshot response before failing.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets a callback used to customize each ephemeral fetch client.
        /// </summary>
        public Action<Client>? ConfigureClient { get; set; }

        /// <summary>
        /// Gets or sets the schema-v0 client factory. Defaults to the C# LiteNetLib adapter.
        /// </summary>
        public Func<ICultNetSchemaClient>? CreateClient { get; set; }
    }

    /// <summary>
    /// Fetches shard snapshots over the CultNet schema-v0 client transport.
    /// </summary>
    public sealed class CultNetSchemaShardSnapshotFetcher : ICultNetShardSnapshotFetcher
    {
        private readonly CultNetSchemaShardSnapshotFetcherOptions _options;

        /// <summary>
        /// Creates a schema-v0 shard snapshot fetcher.
        /// </summary>
        public CultNetSchemaShardSnapshotFetcher(CultNetSchemaShardSnapshotFetcherOptions? options = null)
        {
            _options = options ?? new CultNetSchemaShardSnapshotFetcherOptions();
        }

        /// <inheritdoc />
        public async Task<CultNetSnapshotResponseRawMessage> FetchAsync(CultNetShardDescriptor shard)
        {
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            var endpoint = ResolvePrimaryEndpoint(shard);
            var (host, port) = CultNetSchemaWriteForwarder.ParseEndpoint(endpoint);
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultNetSnapshotResponseRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var client = CreateClient();
            client.OnCultNet<CultNetSnapshotResponseRawMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
                {
                    completion.TrySetResult(response);
                }
            });
            client.OnCultNet<CultNetErrorMessage>(error =>
                completion.TrySetException(new InvalidOperationException(error.Error)));

            client.Connect(host, port);
            await WaitForConnectionAsync(client, endpoint).ConfigureAwait(false);
            client.SendCultNet(new CultNetSnapshotRequestMessage
            {
                MessageId = messageId,
                SchemaIds = shard.SchemaIds.Count == 0 ? null : shard.SchemaIds.ToArray(),
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch
            });

            return await WaitForResponseAsync(completion.Task, endpoint).ConfigureAwait(false);
        }

        private ICultNetSchemaClient CreateClient()
        {
            return _options.CreateClient?.Invoke()
                   ?? CultNetSchemaClients.CreateLiteNetLib(_options.Security, _options.ConfigureClient);
        }

        private async Task WaitForConnectionAsync(ICultNetSchemaClient client, string endpoint)
        {
            var deadline = DateTimeOffset.UtcNow + _options.ConnectTimeout;
            while (!client.Connected)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out connecting to shard primary endpoint {endpoint}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }

        private async Task<CultNetSnapshotResponseRawMessage> WaitForResponseAsync(
            Task<CultNetSnapshotResponseRawMessage> responseTask,
            string endpoint)
        {
            var timeoutTask = Task.Delay(_options.ResponseTimeout);
            var completed = await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false);
            if (completed != responseTask)
            {
                throw new TimeoutException($"Timed out waiting for shard snapshot response from {endpoint}.");
            }

            return await responseTask.ConfigureAwait(false);
        }

        private static string ResolvePrimaryEndpoint(CultNetShardDescriptor shard)
        {
            return shard.PrimaryEndpoints.FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       $"Shard '{shard.ShardId}' does not advertise a primary endpoint.");
        }
    }
}
