using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using R3;

namespace GameCult.Networking
{
    /// <summary>
    /// Describes the kind of domain change observed through a distributed CultCache surface.
    /// </summary>
    public enum CultNetDatabaseChangeKind
    {
        /// <summary>
        /// A document was added.
        /// </summary>
        Added,
        /// <summary>
        /// A document was updated.
        /// </summary>
        Updated,
        /// <summary>
        /// A document was removed.
        /// </summary>
        Removed,
        /// <summary>
        /// A document was accepted through compatible schema migration.
        /// </summary>
        SchemaMigrated,
        /// <summary>
        /// A document change was rejected.
        /// </summary>
        Rejected
    }

    /// <summary>
    /// Describes one shard owned or observed by a CultNet database surface.
    /// </summary>
    public sealed class CultNetShardDescriptor
    {
        /// <summary>
        /// Creates a shard descriptor.
        /// </summary>
        public CultNetShardDescriptor(
            string shardId,
            string ownerRuntimeId,
            long epoch,
            bool isPrimary,
            IEnumerable<string>? schemaIds = null,
            string? keyPrefix = null,
            IEnumerable<string>? primaryEndpoints = null,
            IEnumerable<string>? replicaEndpoints = null,
            IEnumerable<string>? readReplicaEndpoints = null,
            string? region = null)
        {
            ShardId = RequireNonEmpty(shardId, nameof(shardId));
            OwnerRuntimeId = RequireNonEmpty(ownerRuntimeId, nameof(ownerRuntimeId));
            Epoch = epoch;
            IsPrimary = isPrimary;
            SchemaIds = schemaIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            KeyPrefix = keyPrefix;
            PrimaryEndpoints = CleanEndpoints(primaryEndpoints);
            ReplicaEndpoints = CleanEndpoints(replicaEndpoints);
            ReadReplicaEndpoints = CleanEndpoints(readReplicaEndpoints);
            Region = region;
        }

        /// <summary>
        /// Gets the stable shard identifier.
        /// </summary>
        public string ShardId { get; }
        /// <summary>
        /// Gets the runtime that currently owns writes for this shard.
        /// </summary>
        public string OwnerRuntimeId { get; }
        /// <summary>
        /// Gets the monotonic shard authority epoch.
        /// </summary>
        public long Epoch { get; }
        /// <summary>
        /// Gets whether the local database instance is authoritative for writes to this shard.
        /// </summary>
        public bool IsPrimary { get; }
        /// <summary>
        /// Gets the schema ids governed by this shard. Empty means all schemas.
        /// </summary>
        public IReadOnlyList<string> SchemaIds { get; }
        /// <summary>
        /// Gets the optional record-key prefix governed by this shard.
        /// </summary>
        public string? KeyPrefix { get; }
        /// <summary>
        /// Gets endpoints that can accept authoritative writes for this shard.
        /// </summary>
        public IReadOnlyList<string> PrimaryEndpoints { get; }
        /// <summary>
        /// Gets endpoints that replicate authoritative shard mutations.
        /// </summary>
        public IReadOnlyList<string> ReplicaEndpoints { get; }
        /// <summary>
        /// Gets endpoints intended for low-latency read and subscription traffic.
        /// </summary>
        public IReadOnlyList<string> ReadReplicaEndpoints { get; }
        /// <summary>
        /// Gets an optional locality label for this shard owner.
        /// </summary>
        public string? Region { get; }

        /// <summary>
        /// Creates a primary shard that accepts every schema and key.
        /// </summary>
        public static CultNetShardDescriptor PrimaryAll(string ownerRuntimeId = "local", string shardId = "primary")
        {
            return new CultNetShardDescriptor(shardId, ownerRuntimeId, epoch: 1, isPrimary: true);
        }

        /// <summary>
        /// Creates a read-only shard descriptor.
        /// </summary>
        public static CultNetShardDescriptor ReadOnly(
            string shardId,
            string ownerRuntimeId,
            long epoch = 1,
            IEnumerable<string>? schemaIds = null,
            string? keyPrefix = null)
        {
            return new CultNetShardDescriptor(shardId, ownerRuntimeId, epoch, isPrimary: false, schemaIds, keyPrefix);
        }

        internal bool Matches(string schemaId, CultRecordKey key)
        {
            var schemaMatches = SchemaIds.Count == 0 || SchemaIds.Contains(schemaId, StringComparer.Ordinal);
            var keyMatches = string.IsNullOrEmpty(KeyPrefix) ||
                             key.Value.StartsWith(KeyPrefix!, StringComparison.Ordinal);
            return schemaMatches && keyMatches;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be non-empty.", paramName);
            }

            return value;
        }

        private static string[] CleanEndpoints(IEnumerable<string>? endpoints)
        {
            return endpoints?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Options for creating a distributed CultCache database surface.
    /// </summary>
    public sealed class CultNetDatabaseOptions
    {
        /// <summary>
        /// Gets or sets the local runtime id advertised as shard owner for default primary shards.
        /// </summary>
        public string RuntimeId { get; set; } = "local";
        /// <summary>
        /// Gets or sets explicit shard descriptors. When omitted, this instance owns one primary shard for all records.
        /// </summary>
        public IReadOnlyList<CultNetShardDescriptor>? Shards { get; set; }
        /// <summary>
        /// Gets or sets the document registry used to create and apply raw CultNet document messages.
        /// </summary>
        public CultNetDocumentRegistry? DocumentRegistry { get; set; }
    }

    /// <summary>
    /// A typed domain change emitted by a CultNet database surface.
    /// </summary>
    public sealed class CultNetDatabaseChange<T> where T : class
    {
        /// <summary>
        /// Creates a database change.
        /// </summary>
        public CultNetDatabaseChange(
            CultNetDatabaseChangeKind kind,
            CultRecordKey key,
            string schemaId,
            CultNetShardDescriptor shard,
            T? document,
            T? previousDocument,
            string? message = null)
        {
            Kind = kind;
            Key = key;
            SchemaId = schemaId;
            Shard = shard;
            Document = document;
            PreviousDocument = previousDocument;
            Message = message;
        }

        /// <summary>
        /// Gets the change kind.
        /// </summary>
        public CultNetDatabaseChangeKind Kind { get; }
        /// <summary>
        /// Gets the changed record key.
        /// </summary>
        public CultRecordKey Key { get; }
        /// <summary>
        /// Gets the schema id of the changed document.
        /// </summary>
        public string SchemaId { get; }
        /// <summary>
        /// Gets the shard that accepted or rejected the change.
        /// </summary>
        public CultNetShardDescriptor Shard { get; }
        /// <summary>
        /// Gets the current document for added and updated events.
        /// </summary>
        public T? Document { get; }
        /// <summary>
        /// Gets the previous document for updated and removed events.
        /// </summary>
        public T? PreviousDocument { get; }
        /// <summary>
        /// Gets an optional diagnostic message.
        /// </summary>
        public string? Message { get; }
    }

    /// <summary>
    /// One ordered mutation accepted for a shard.
    /// </summary>
    public sealed class CultNetShardMutationLogEntry
    {
        /// <summary>
        /// Creates a shard mutation log entry.
        /// </summary>
        public CultNetShardMutationLogEntry(
            string shardId,
            long shardEpoch,
            long sequence,
            string committedAt,
            CultNetDatabaseChangeKind kind,
            string schemaId,
            CultRecordKey key,
            object? document,
            object? previousDocument)
        {
            ShardId = shardId;
            ShardEpoch = shardEpoch;
            Sequence = sequence;
            CommittedAt = committedAt;
            Kind = kind;
            SchemaId = schemaId;
            Key = key;
            Document = document;
            PreviousDocument = previousDocument;
        }

        /// <summary>
        /// Gets the shard id.
        /// </summary>
        public string ShardId { get; }
        /// <summary>
        /// Gets the shard epoch.
        /// </summary>
        public long ShardEpoch { get; }
        /// <summary>
        /// Gets the per-shard mutation sequence.
        /// </summary>
        public long Sequence { get; }
        /// <summary>
        /// Gets the commit timestamp.
        /// </summary>
        public string CommittedAt { get; }
        /// <summary>
        /// Gets the mutation kind.
        /// </summary>
        public CultNetDatabaseChangeKind Kind { get; }
        /// <summary>
        /// Gets the schema id.
        /// </summary>
        public string SchemaId { get; }
        /// <summary>
        /// Gets the record key.
        /// </summary>
        public CultRecordKey Key { get; }
        /// <summary>
        /// Gets the current document, when present.
        /// </summary>
        public object? Document { get; }
        /// <summary>
        /// Gets the previous document, when present.
        /// </summary>
        public object? PreviousDocument { get; }
    }

    /// <summary>
    /// Raised when a database write targets a shard this node does not own.
    /// </summary>
    public sealed class CultNetShardAuthorityException : InvalidOperationException
    {
        /// <summary>
        /// Creates a shard authority exception.
        /// </summary>
        public CultNetShardAuthorityException(CultNetShardDescriptor shard, string message, string reason = "not_primary") : base(message)
        {
            Shard = shard;
            Reason = reason;
        }

        /// <summary>
        /// Gets the shard that rejected the write.
        /// </summary>
        public CultNetShardDescriptor Shard { get; }
        /// <summary>
        /// Gets the machine-readable authority rejection reason.
        /// </summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Database-style CultNet facade over a CultCache shard set.
    /// </summary>
    public sealed class CultNetDatabase : IDisposable
    {
        private readonly CultCache _cache;
        private readonly CultNetDocumentRegistry _documents;
        private readonly List<CultNetShardDescriptor> _shards;
        private readonly Dictionary<string, List<CultNetShardMutationLogEntry>> _mutationLogs =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _nextLogSequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _appliedShardSequences = new(StringComparer.Ordinal);
        private readonly Subject<object> _changes = new();
        private bool _disposed;

        /// <summary>
        /// Creates a database surface over a CultCache instance.
        /// </summary>
        public CultNetDatabase(CultCache cache, CultNetDatabaseOptions? options = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            options ??= new CultNetDatabaseOptions();
            _documents = options.DocumentRegistry ?? new CultNetDocumentRegistry(cache.Registry);
            _shards = (options.Shards == null || options.Shards.Count == 0
                    ? new[] { CultNetShardDescriptor.PrimaryAll(options.RuntimeId) }
                    : options.Shards)
                .ToList();
            _cache.OnUpdate += PublishCacheUpdate;
        }

        /// <summary>
        /// Gets the local cache backing this database surface.
        /// </summary>
        public CultCache Cache => _cache;
        /// <summary>
        /// Gets the document registry used by the raw CultNet lane.
        /// </summary>
        public CultNetDocumentRegistry Documents => _documents;
        /// <summary>
        /// Gets the shard map known to this database surface.
        /// </summary>
        public IReadOnlyList<CultNetShardDescriptor> Shards => _shards;

        /// <summary>
        /// Gets mutation log entries for a shard after the supplied sequence.
        /// </summary>
        public IReadOnlyList<CultNetShardMutationLogEntry> GetMutationLog(
            string shardId,
            long afterSequence = 0,
            int? limit = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            if (!_mutationLogs.TryGetValue(shardId, out var entries))
            {
                return Array.Empty<CultNetShardMutationLogEntry>();
            }

            var query = entries.Where(entry => entry.Sequence > afterSequence);
            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            return query.ToArray();
        }

        /// <summary>
        /// Gets the last replicated sequence applied for a shard.
        /// </summary>
        public long GetAppliedShardSequence(string shardId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            return _appliedShardSequences.TryGetValue(shardId, out var sequence)
                ? sequence
                : 0;
        }

        /// <summary>
        /// Creates a shard catalog response for optional schema/key filters.
        /// </summary>
        public CultNetShardCatalogResponseMessage CreateShardCatalogResponse(CultNetShardCatalogRequestMessage request)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));

            var schemaIds = request.SchemaIds == null || request.SchemaIds.Length == 0
                ? null
                : request.SchemaIds;
            var recordKeys = request.RecordKeys == null || request.RecordKeys.Length == 0
                ? null
                : request.RecordKeys.Select(key => new CultRecordKey(key)).ToArray();

            var descriptors = _shards
                .Where(shard => MatchesCatalogFilter(shard, schemaIds, recordKeys))
                .Select(ToMessage)
                .ToArray();

            return new CultNetShardCatalogResponseMessage
            {
                MessageId = string.IsNullOrWhiteSpace(request.MessageId)
                    ? Guid.NewGuid().ToString("N")
                    : request.MessageId,
                Shards = descriptors
            };
        }

        /// <summary>
        /// Resolves the shard that governs the supplied schema and key.
        /// </summary>
        public CultNetShardDescriptor ResolveShard(string schemaId, CultRecordKey key)
        {
            ThrowIfDisposed();
            return ResolveShardInternal(schemaId, key);
        }

        /// <summary>
        /// Gets a document by key.
        /// </summary>
        public Task<T?> GetAsync<T>(CultRecordKey key) where T : class
        {
            ThrowIfDisposed();
            return Task.FromResult(_cache.Get<T>(key));
        }

        /// <summary>
        /// Adds or replaces a document at a specific key.
        /// </summary>
        public async Task<CultRecordHandle<T>> PutAsync<T>(CultRecordKey key, T document) where T : class
        {
            ThrowIfDisposed();
            if (document == null) throw new ArgumentNullException(nameof(document));

            var descriptor = _cache.Registry.GetRequired<T>();
            var shard = ResolveShardInternal(descriptor.SchemaId, key);
            EnsurePrimary(shard, descriptor.SchemaId, key);

            var previous = _cache.Get<T>(key);
            var handle = await _cache.UpsertAsync(document, new CultRecordHandle<T>(key)).ConfigureAwait(false);
            AppendMutationLogEntry(
                shard,
                previous == null ? CultNetDatabaseChangeKind.Added : CultNetDatabaseChangeKind.Updated,
                key,
                descriptor.SchemaId,
                document,
                previous);
            Publish(new CultNetDatabaseChange<T>(
                previous == null ? CultNetDatabaseChangeKind.Added : CultNetDatabaseChangeKind.Updated,
                key,
                descriptor.SchemaId,
                shard,
                document,
                previous));
            return handle;
        }

        /// <summary>
        /// Deletes a document by key.
        /// </summary>
        public Task DeleteAsync<T>(CultRecordKey key) where T : class
        {
            ThrowIfDisposed();
            var descriptor = _cache.Registry.GetRequired<T>();
            var shard = ResolveShardInternal(descriptor.SchemaId, key);
            EnsurePrimary(shard, descriptor.SchemaId, key);

            var previous = _cache.Get<T>(key);
            if (previous != null)
            {
                _cache.Remove(new CultRecordHandle<T>(key));
                AppendMutationLogEntry(
                    shard,
                    CultNetDatabaseChangeKind.Removed,
                    key,
                    descriptor.SchemaId,
                    document: null,
                    previousDocument: previous);
                Publish(new CultNetDatabaseChange<T>(
                    CultNetDatabaseChangeKind.Removed,
                    key,
                    descriptor.SchemaId,
                    shard,
                    document: null,
                    previousDocument: previous));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Applies a raw document mutation after checking shard authority.
        /// </summary>
        public async Task<object> ApplyPutAsync(CultNetDocumentPutRawMessage message)
        {
            ThrowIfDisposed();
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (message.Document == null)
            {
                throw new ArgumentException("CultNet raw document message is missing its document payload.", nameof(message));
            }

            var key = new CultRecordKey(message.Document.RecordKey);
            var shard = ResolveShardInternal(message.Document.SchemaId, key);
            EnsurePrimary(shard, message.Document.SchemaId, key, message.ShardEpoch);
            var descriptor = _cache.Registry.GetRequiredBySchemaId(message.Document.SchemaId);
            var previous = _cache.Get(key);
            var document = await _documents.ApplyRawDocumentPutMessageAsync(_cache, message).ConfigureAwait(false);
            AppendMutationLogEntry(
                shard,
                previous == null ? CultNetDatabaseChangeKind.Added : CultNetDatabaseChangeKind.Updated,
                key,
                descriptor.SchemaId,
                document,
                previous);
            PublishUntyped(
                descriptor.DocumentType,
                previous == null ? CultNetDatabaseChangeKind.Added : CultNetDatabaseChangeKind.Updated,
                key,
                descriptor.SchemaId,
                shard,
                document,
                previous);
            return document;
        }

        /// <summary>
        /// Applies a raw document delete after checking shard authority.
        /// </summary>
        public Task ApplyDeleteAsync(CultNetDocumentDeleteMessage message)
        {
            ThrowIfDisposed();
            if (message == null) throw new ArgumentNullException(nameof(message));

            var key = new CultRecordKey(message.RecordKey);
            var shard = ResolveShardInternal(message.SchemaId, key);
            EnsurePrimary(shard, message.SchemaId, key, message.ShardEpoch);
            var descriptor = _cache.Registry.GetRequiredBySchemaId(message.SchemaId);
            var previous = _cache.Get(key);
            if (previous == null)
            {
                return Task.CompletedTask;
            }

            var removeMethod = typeof(CultCache).GetMethod(nameof(CultCache.Remove))!
                .MakeGenericMethod(descriptor.DocumentType);
            var handleType = typeof(CultRecordHandle<>).MakeGenericType(descriptor.DocumentType);
            var handle = Activator.CreateInstance(handleType, new object[] { key });
            removeMethod.Invoke(_cache, new[] { handle });
            AppendMutationLogEntry(
                shard,
                CultNetDatabaseChangeKind.Removed,
                key,
                descriptor.SchemaId,
                document: null,
                previousDocument: previous);
            PublishUntyped(
                descriptor.DocumentType,
                CultNetDatabaseChangeKind.Removed,
                key,
                descriptor.SchemaId,
                shard,
                document: null,
                previousDocument: previous);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Applies committed shard-log entries from an authoritative peer.
        /// </summary>
        public async Task<long> ApplyShardLogResponseAsync(CultNetShardLogResponseMessage response)
        {
            ThrowIfDisposed();
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (response.ResyncRequired)
            {
                throw new InvalidOperationException(
                    $"Shard '{response.ShardId}' requires snapshot resync before log application: {response.Reason ?? "unspecified"}.");
            }

            var shard = _shards.FirstOrDefault(candidate =>
                string.Equals(candidate.ShardId, response.ShardId, StringComparison.Ordinal));
            if (shard == null)
            {
                throw new InvalidOperationException($"Shard '{response.ShardId}' is not known by this database.");
            }

            if (response.ShardEpoch != shard.Epoch)
            {
                throw new CultNetShardAuthorityException(
                    shard,
                    $"Shard '{shard.ShardId}' is at epoch {shard.Epoch}, not response epoch {response.ShardEpoch}.",
                    "stale_epoch");
            }

            var lastApplied = GetAppliedShardSequence(response.ShardId);
            var orderedEntries = response.Entries.OrderBy(entry => entry.Sequence).ToArray();
            foreach (var entry in orderedEntries)
            {
                if (entry.Sequence <= lastApplied)
                {
                    continue;
                }

                if (entry.Sequence != lastApplied + 1)
                {
                    throw new InvalidOperationException(
                        $"Shard '{response.ShardId}' log has a gap. Expected sequence {lastApplied + 1}, received {entry.Sequence}.");
                }

                await ApplyCommittedShardLogEntryAsync(shard, entry).ConfigureAwait(false);
                lastApplied = entry.Sequence;
                _appliedShardSequences[response.ShardId] = lastApplied;
            }

            return lastApplied;
        }

        /// <summary>
        /// Watches all changes assignable to the requested document type.
        /// </summary>
        public Observable<CultNetDatabaseChange<T>> Watch<T>() where T : class
        {
            ThrowIfDisposed();
            return _changes
                .Where(change => change is CultNetDatabaseChange<T>)
                .Select(change => (CultNetDatabaseChange<T>)change);
        }

        /// <summary>
        /// Watches all typed domain changes as boxed change objects.
        /// </summary>
        public Observable<object> WatchAllChanges()
        {
            ThrowIfDisposed();
            return _changes;
        }

        /// <summary>
        /// Watches one record.
        /// </summary>
        public Observable<CultNetDatabaseChange<T>> WatchRecord<T>(CultRecordKey key) where T : class
        {
            return Watch<T>().Where(change => change.Key.Equals(key));
        }

        /// <summary>
        /// Watches the global record for a document type.
        /// </summary>
        public Observable<CultNetDatabaseChange<T>> WatchGlobal<T>() where T : class
        {
            var descriptor = _cache.Registry.GetRequired<T>();
            return WatchRecord<T>(new CultRecordKey($"global:{descriptor.SchemaId}"));
        }

        /// <summary>
        /// Watches changes for a named document.
        /// </summary>
        public Observable<CultNetDatabaseChange<T>> WatchByName<T>(string name) where T : class
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value must be non-empty.", nameof(name));
            return Watch<T>().Where(change =>
                (change.Document != null && ReferenceEquals(_cache.GetByName<T>(name), change.Document)) ||
                (change.PreviousDocument != null && ReferenceEquals(_cache.GetByName<T>(name), change.PreviousDocument)));
        }

        /// <summary>
        /// Watches changes for the current document mapped by an indexed value.
        /// </summary>
        public Observable<CultNetDatabaseChange<T>> WatchByIndex<T>(string alias, string value) where T : class
        {
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Value must be non-empty.", nameof(alias));
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value must be non-empty.", nameof(value));
            return Watch<T>().Where(change =>
                (change.Document != null && ReferenceEquals(_cache.GetByIndex<T>(alias, value), change.Document)) ||
                (change.PreviousDocument != null && ReferenceEquals(_cache.GetByIndex<T>(alias, value), change.PreviousDocument)));
        }

        /// <summary>
        /// Releases database subscriptions.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.OnUpdate -= PublishCacheUpdate;
            _changes.Dispose();
        }

        private void PublishCacheUpdate(object? previous, object? current)
        {
            var document = current ?? previous;
            if (document == null)
            {
                return;
            }

            var documentType = document.GetType();
            var descriptor = _cache.Registry.GetRequired(documentType);
            var key = current != null
                ? GetTrackedKey(current, documentType)
                : new CultRecordKey(string.Empty);
            var shard = ResolveShardInternal(descriptor.SchemaId, key);
            var changeType = typeof(CultNetDatabaseChange<>).MakeGenericType(documentType);
            var change = Activator.CreateInstance(
                changeType,
                current == null ? CultNetDatabaseChangeKind.Removed :
                previous == null ? CultNetDatabaseChangeKind.Added :
                CultNetDatabaseChangeKind.Updated,
                key,
                descriptor.SchemaId,
                shard,
                current,
                previous,
                null);
            if (change != null)
            {
                _changes.OnNext(change);
            }
        }

        private CultRecordKey GetTrackedKey(object document, Type documentType)
        {
            var method = typeof(CultCache).GetMethod(nameof(CultCache.TryGetHandle))!
                .MakeGenericMethod(documentType);
            var handleObject = method.Invoke(_cache, new[] { document });
            if (handleObject == null)
            {
                return new CultRecordKey(string.Empty);
            }

            var keyProperty = handleObject.GetType().GetProperty("Key");
            return keyProperty?.GetValue(handleObject) is CultRecordKey key
                ? key
                : new CultRecordKey(string.Empty);
        }

        private void Publish<T>(CultNetDatabaseChange<T> change) where T : class
        {
            _changes.OnNext(change);
        }

        private void AppendMutationLogEntry(
            CultNetShardDescriptor shard,
            CultNetDatabaseChangeKind kind,
            CultRecordKey key,
            string schemaId,
            object? document,
            object? previousDocument)
        {
            var sequence = NextMutationLogSequence(shard.ShardId);
            RecordMutationLogEntry(new CultNetShardMutationLogEntry(
                shard.ShardId,
                shard.Epoch,
                sequence,
                DateTimeOffset.UtcNow.ToString("O"),
                kind,
                schemaId,
                key,
                document,
                previousDocument));
        }

        private void RecordMutationLogEntry(CultNetShardMutationLogEntry entry)
        {
            if (!_mutationLogs.TryGetValue(entry.ShardId, out var entries))
            {
                entries = new List<CultNetShardMutationLogEntry>();
                _mutationLogs[entry.ShardId] = entries;
            }

            entries.Add(entry);
            if (!_nextLogSequences.TryGetValue(entry.ShardId, out var next) ||
                next <= entry.Sequence)
            {
                _nextLogSequences[entry.ShardId] = entry.Sequence + 1;
            }
        }

        private long NextMutationLogSequence(string shardId)
        {
            var sequence = _nextLogSequences.TryGetValue(shardId, out var next)
                ? next
                : 1;
            _nextLogSequences[shardId] = sequence + 1;
            return sequence;
        }

        private async Task ApplyCommittedShardLogEntryAsync(
            CultNetShardDescriptor shard,
            CultNetShardLogEntryMessage entry)
        {
            if (entry.Put != null)
            {
                await ApplyCommittedPutAsync(shard, entry).ConfigureAwait(false);
                return;
            }

            if (entry.Delete != null)
            {
                ApplyCommittedDelete(shard, entry);
                return;
            }

            throw new InvalidOperationException(
                $"Shard '{shard.ShardId}' log entry {entry.Sequence} has no put or delete payload.");
        }

        private async Task ApplyCommittedPutAsync(
            CultNetShardDescriptor shard,
            CultNetShardLogEntryMessage entry)
        {
            var message = entry.Put!;
            if (message.Document == null)
            {
                throw new InvalidOperationException(
                    $"Shard '{shard.ShardId}' log entry {entry.Sequence} put is missing its document payload.");
            }

            if (!string.Equals(message.ShardId, shard.ShardId, StringComparison.Ordinal) ||
                message.ShardEpoch != shard.Epoch)
            {
                throw new CultNetShardAuthorityException(
                    shard,
                    $"Shard '{shard.ShardId}' log entry {entry.Sequence} targets a different shard or epoch.",
                    "stale_epoch");
            }

            var key = new CultRecordKey(message.Document.RecordKey);
            var descriptor = _cache.Registry.GetRequiredBySchemaId(message.Document.SchemaId);
            var previous = _cache.Get(key);
            var document = await _documents.ApplyRawDocumentPutMessageAsync(_cache, message).ConfigureAwait(false);
            var kind = ChangeKindFromWire(entry.ChangeKind);
            if (kind == CultNetDatabaseChangeKind.Removed)
            {
                throw new InvalidOperationException(
                    $"Shard '{shard.ShardId}' log entry {entry.Sequence} has a removed kind with a put payload.");
            }

            RecordMutationLogEntry(new CultNetShardMutationLogEntry(
                shard.ShardId,
                shard.Epoch,
                entry.Sequence,
                entry.CommittedAt,
                kind,
                descriptor.SchemaId,
                key,
                document,
                previous));
            PublishUntyped(
                descriptor.DocumentType,
                kind,
                key,
                descriptor.SchemaId,
                shard,
                document,
                previous);
        }

        private void ApplyCommittedDelete(
            CultNetShardDescriptor shard,
            CultNetShardLogEntryMessage entry)
        {
            var message = entry.Delete!;
            if (!string.Equals(message.ShardId, shard.ShardId, StringComparison.Ordinal) ||
                message.ShardEpoch != shard.Epoch)
            {
                throw new CultNetShardAuthorityException(
                    shard,
                    $"Shard '{shard.ShardId}' log entry {entry.Sequence} targets a different shard or epoch.",
                    "stale_epoch");
            }

            var key = new CultRecordKey(message.RecordKey);
            var descriptor = _cache.Registry.GetRequiredBySchemaId(message.SchemaId);
            var previous = _cache.Get(key);
            if (previous != null)
            {
                var removeMethod = typeof(CultCache).GetMethod(nameof(CultCache.Remove))!
                    .MakeGenericMethod(descriptor.DocumentType);
                var handleType = typeof(CultRecordHandle<>).MakeGenericType(descriptor.DocumentType);
                var handle = Activator.CreateInstance(handleType, new object[] { key });
                removeMethod.Invoke(_cache, new[] { handle });
            }

            RecordMutationLogEntry(new CultNetShardMutationLogEntry(
                shard.ShardId,
                shard.Epoch,
                entry.Sequence,
                entry.CommittedAt,
                CultNetDatabaseChangeKind.Removed,
                descriptor.SchemaId,
                key,
                document: null,
                previousDocument: previous));
            PublishUntyped(
                descriptor.DocumentType,
                CultNetDatabaseChangeKind.Removed,
                key,
                descriptor.SchemaId,
                shard,
                document: null,
                previousDocument: previous);
        }

        private static CultNetDatabaseChangeKind ChangeKindFromWire(string changeKind)
        {
            return changeKind switch
            {
                "added" => CultNetDatabaseChangeKind.Added,
                "updated" => CultNetDatabaseChangeKind.Updated,
                "removed" => CultNetDatabaseChangeKind.Removed,
                _ => throw new InvalidOperationException($"Unsupported shard log change kind '{changeKind}'.")
            };
        }

        private void PublishUntyped(
            Type documentType,
            CultNetDatabaseChangeKind kind,
            CultRecordKey key,
            string schemaId,
            CultNetShardDescriptor shard,
            object? document,
            object? previousDocument)
        {
            var changeType = typeof(CultNetDatabaseChange<>).MakeGenericType(documentType);
            var change = Activator.CreateInstance(
                changeType,
                kind,
                key,
                schemaId,
                shard,
                document,
                previousDocument,
                null);
            if (change != null)
            {
                _changes.OnNext(change);
            }
        }

        private CultNetShardDescriptor ResolveShardInternal(string schemaId, CultRecordKey key)
        {
            return _shards.FirstOrDefault(shard => shard.Matches(schemaId, key)) ?? _shards[0];
        }

        private static void EnsurePrimary(CultNetShardDescriptor shard, string schemaId, CultRecordKey key, long? expectedEpoch = null)
        {
            if (expectedEpoch.HasValue && expectedEpoch.Value != shard.Epoch)
            {
                throw new CultNetShardAuthorityException(
                    shard,
                    $"Shard '{shard.ShardId}' is at epoch {shard.Epoch}, not requested epoch {expectedEpoch.Value}.",
                    "stale_epoch");
            }

            if (shard.IsPrimary)
            {
                return;
            }

            throw new CultNetShardAuthorityException(
                shard,
                $"Shard '{shard.ShardId}' owned by '{shard.OwnerRuntimeId}' does not accept local writes for schema '{schemaId}' key '{key.Value}'.",
                "not_primary");
        }

        private static bool MatchesCatalogFilter(
            CultNetShardDescriptor shard,
            IReadOnlyList<string>? schemaIds,
            IReadOnlyList<CultRecordKey>? recordKeys)
        {
            var schemaMatches = schemaIds == null ||
                                schemaIds.Count == 0 ||
                                shard.SchemaIds.Count == 0 ||
                                schemaIds.Any(schemaId => shard.SchemaIds.Contains(schemaId, StringComparer.Ordinal));
            var keyMatches = recordKeys == null ||
                             recordKeys.Count == 0 ||
                             recordKeys.Any(key => string.IsNullOrEmpty(shard.KeyPrefix) ||
                                                   key.Value.StartsWith(shard.KeyPrefix!, StringComparison.Ordinal));
            return schemaMatches && keyMatches;
        }

        internal static CultNetShardDescriptorMessage ToMessage(CultNetShardDescriptor shard)
        {
            return new CultNetShardDescriptorMessage
            {
                ShardId = shard.ShardId,
                OwnerRuntimeId = shard.OwnerRuntimeId,
                Epoch = shard.Epoch,
                IsPrimary = shard.IsPrimary,
                SchemaIds = shard.SchemaIds.ToArray(),
                KeyPrefix = shard.KeyPrefix,
                PrimaryEndpoints = shard.PrimaryEndpoints.ToArray(),
                ReplicaEndpoints = shard.ReplicaEndpoints.ToArray(),
                ReadReplicaEndpoints = shard.ReadReplicaEndpoints.ToArray(),
                Region = shard.Region
            };
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultNetDatabase));
            }
        }
    }
}
