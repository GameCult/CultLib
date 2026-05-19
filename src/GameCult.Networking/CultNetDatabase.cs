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
            string? keyPrefix = null)
        {
            ShardId = RequireNonEmpty(shardId, nameof(shardId));
            OwnerRuntimeId = RequireNonEmpty(ownerRuntimeId, nameof(ownerRuntimeId));
            Epoch = epoch;
            IsPrimary = isPrimary;
            SchemaIds = schemaIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            KeyPrefix = keyPrefix;
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
    /// Raised when a database write targets a shard this node does not own.
    /// </summary>
    public sealed class CultNetShardAuthorityException : InvalidOperationException
    {
        /// <summary>
        /// Creates a shard authority exception.
        /// </summary>
        public CultNetShardAuthorityException(CultNetShardDescriptor shard, string message) : base(message)
        {
            Shard = shard;
        }

        /// <summary>
        /// Gets the shard that rejected the write.
        /// </summary>
        public CultNetShardDescriptor Shard { get; }
    }

    /// <summary>
    /// Database-style CultNet facade over a CultCache shard set.
    /// </summary>
    public sealed class CultNetDatabase : IDisposable
    {
        private readonly CultCache _cache;
        private readonly CultNetDocumentRegistry _documents;
        private readonly List<CultNetShardDescriptor> _shards;
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
            var shard = ResolveShard(descriptor.SchemaId, key);
            EnsurePrimary(shard, descriptor.SchemaId, key);

            var previous = _cache.Get<T>(key);
            var handle = await _cache.UpsertAsync(document, new CultRecordHandle<T>(key)).ConfigureAwait(false);
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
            var shard = ResolveShard(descriptor.SchemaId, key);
            EnsurePrimary(shard, descriptor.SchemaId, key);

            var previous = _cache.Get<T>(key);
            if (previous != null)
            {
                _cache.Remove(new CultRecordHandle<T>(key));
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
            var shard = ResolveShard(message.Document.SchemaId, key);
            EnsurePrimary(shard, message.Document.SchemaId, key);
            var descriptor = _cache.Registry.GetRequiredBySchemaId(message.Document.SchemaId);
            var previous = _cache.Get(key);
            var document = await _documents.ApplyRawDocumentPutMessageAsync(_cache, message).ConfigureAwait(false);
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
            var shard = ResolveShard(message.SchemaId, key);
            EnsurePrimary(shard, message.SchemaId, key);
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
            var shard = ResolveShard(descriptor.SchemaId, key);
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

        private CultNetShardDescriptor ResolveShard(string schemaId, CultRecordKey key)
        {
            return _shards.FirstOrDefault(shard => shard.Matches(schemaId, key)) ?? _shards[0];
        }

        private static void EnsurePrimary(CultNetShardDescriptor shard, string schemaId, CultRecordKey key)
        {
            if (shard.IsPrimary)
            {
                return;
            }

            throw new CultNetShardAuthorityException(
                shard,
                $"Shard '{shard.ShardId}' owned by '{shard.OwnerRuntimeId}' does not accept local writes for schema '{schemaId}' key '{key.Value}'.");
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
