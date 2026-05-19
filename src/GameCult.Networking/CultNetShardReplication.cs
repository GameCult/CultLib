using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        /// Gets or sets a callback for background pull failures.
        /// </summary>
        public Action<Exception>? OnError { get; set; }
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

            var afterSequence = _database.GetAppliedShardSequence(shard.ShardId);
            var response = await _options.Fetcher
                .FetchAsync(shard, afterSequence, _options.BatchSize)
                .ConfigureAwait(false);
            return await _database.ApplyShardLogResponseAsync(response).ConfigureAwait(false);
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultNetShardReplicator));
            }
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

            using var client = new Client(_options.Security ?? ClientSecurityOptions.Development())
            {
                AllowUnverifiedCultNetMessages = true
            };
            _options.ConfigureClient?.Invoke(client);
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

        private async Task WaitForConnectionAsync(Client client, string endpoint)
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
}
