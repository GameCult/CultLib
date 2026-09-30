using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
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
        Rejected,
        /// <summary>
        /// A document was applied locally before authoritative commit.
        /// </summary>
        Predicted,
        /// <summary>
        /// A predicted document was replaced by the authoritative committed state.
        /// </summary>
        Reconciled
    }

    /// <summary>
    /// The one schema-alias matcher for every runtime-side schema match (docs/cultnet-selection-cut.md, D4).
    /// </summary>
    public static class CultNetSchemaAliasMatching
    {
        public static bool MatchesAny(IReadOnlyList<string> candidates, string schemaId)
        {
            return candidates.Count == 0 ||
                   candidates.Any(candidate => Matches(candidate, schemaId));
        }

        public static bool MatchesAny(IReadOnlyList<string> candidates, CultDocumentDescriptor descriptor)
        {
            return candidates.Count == 0 ||
                   candidates.Any(candidate => Matches(candidate, descriptor));
        }

        public static bool Matches(string candidate, string schemaId)
        {
            if (string.Equals(candidate, schemaId, StringComparison.Ordinal))
                return true;

            var candidateName = InferSchemaName(candidate) ?? candidate;
            var schemaName = InferSchemaName(schemaId) ?? schemaId;
            return string.Equals(candidateName, schemaName, StringComparison.Ordinal);
        }

        public static bool Matches(string candidate, CultDocumentDescriptor descriptor)
        {
            if (string.Equals(candidate, descriptor.SchemaId, StringComparison.Ordinal) ||
                string.Equals(candidate, descriptor.SchemaName, StringComparison.Ordinal) ||
                string.Equals(candidate, descriptor.SchemaVersion, StringComparison.Ordinal))
                return true;

            if (descriptor.ToCatalogEntry().CompatibleSchemaIds.Any(compatible =>
                    string.Equals(candidate, compatible, StringComparison.Ordinal)))
                return true;

            var candidateName = InferSchemaName(candidate) ?? candidate;
            return string.Equals(candidateName, descriptor.SchemaName, StringComparison.Ordinal);
        }

        private static string? InferSchemaName(string schemaId)
        {
            var marker = schemaId.LastIndexOf(".v", StringComparison.Ordinal);
            if (marker <= 0 || marker + 2 >= schemaId.Length)
                return null;

            var version = schemaId.Substring(marker + 2);
            return version.All(char.IsDigit)
                ? schemaId.Substring(0, marker)
                : null;
        }

        // R-M: WithBindingSchemaAlias (a second binding-alias reconciler, scoped to one descriptor) is
        // deleted - CultNetDocumentRegistry.ExpandSchemaBindingAliases is the one copy every caller
        // uses, CultNetDatabaseServer and CultNetDatabaseSubscriptionServer included.
    }

    /// <summary>
    /// Declares which documents this runtime may predict locally before authoritative commit.
    /// </summary>
    public sealed class CultNetClientAuthorityScope
    {
        /// <summary>
        /// Creates a client authority scope.
        /// </summary>
        public CultNetClientAuthorityScope(
            string ownerRuntimeId,
            IEnumerable<string>? schemaIds = null,
            string? keyPrefix = null)
        {
            OwnerRuntimeId = string.IsNullOrWhiteSpace(ownerRuntimeId)
                ? throw new ArgumentException("Value must be non-empty.", nameof(ownerRuntimeId))
                : ownerRuntimeId;
            SchemaIds = schemaIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            KeyPrefix = keyPrefix;
        }

        /// <summary>
        /// Gets the runtime that owns authoring for this scope.
        /// </summary>
        public string OwnerRuntimeId { get; }
        /// <summary>
        /// Gets the schema ids governed by this scope. Empty means all schemas.
        /// </summary>
        public IReadOnlyList<string> SchemaIds { get; }
        /// <summary>
        /// Gets the optional record-key prefix governed by this scope.
        /// </summary>
        public string? KeyPrefix { get; }

        internal bool Matches(string runtimeId, string schemaId, CultRecordKey key)
        {
            return string.Equals(OwnerRuntimeId, runtimeId, StringComparison.Ordinal) &&
                   CultNetSchemaAliasMatching.MatchesAny(SchemaIds, schemaId) &&
                   (string.IsNullOrEmpty(KeyPrefix) || key.Value.StartsWith(KeyPrefix!, StringComparison.Ordinal));
        }

        internal bool Matches(string runtimeId, CultDocumentDescriptor descriptor, CultRecordKey key)
        {
            return string.Equals(OwnerRuntimeId, runtimeId, StringComparison.Ordinal) &&
                   CultNetSchemaAliasMatching.MatchesAny(SchemaIds, descriptor) &&
                   (string.IsNullOrEmpty(KeyPrefix) || key.Value.StartsWith(KeyPrefix!, StringComparison.Ordinal));
        }
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
            var schemaMatches = CultNetSchemaAliasMatching.MatchesAny(SchemaIds, schemaId);
            var keyMatches = string.IsNullOrEmpty(KeyPrefix) ||
                             key.Value.StartsWith(KeyPrefix!, StringComparison.Ordinal);
            return schemaMatches && keyMatches;
        }

        internal bool Matches(CultDocumentDescriptor descriptor, CultRecordKey key)
        {
            var schemaMatches = CultNetSchemaAliasMatching.MatchesAny(SchemaIds, descriptor);
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
        /// Gets or sets document scopes this runtime may predict before authoritative commit.
        /// </summary>
        public IReadOnlyList<CultNetClientAuthorityScope>? ClientAuthorityScopes { get; set; }
        /// <summary>
        /// Gets or sets the document registry used to create and apply raw CultNet document messages.
        /// </summary>
        public CultNetDocumentRegistry? DocumentRegistry { get; set; }
        /// <summary>
        /// Gets or sets the durable store for accepted shard mutation logs.
        /// </summary>
        public ICultNetShardMutationLogStore? MutationLogStore { get; set; }
        /// <summary>
        /// Gets or sets the process cursor key (R-O). Omitted in production, where a fresh random key
        /// per <see cref="CultNetDatabase"/> is exactly the point; a test that must mint a cursor under a
        /// known key, or reuse the pre-restart key deliberately, sets this explicitly.
        /// </summary>
        public CultNetSelectionCursorKey? CursorKey { get; set; }
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
    /// One shard-log sequence that a primary minted for a committed change and burned because it could not log it.
    /// </summary>
    public readonly struct CultNetBurnedSequence : IEquatable<CultNetBurnedSequence>
    {
        /// <summary>
        /// Creates a burned sequence.
        /// </summary>
        public CultNetBurnedSequence(string shardId, long sequence)
        {
            ShardId = shardId;
            Sequence = sequence;
        }

        /// <summary>
        /// Gets the shard whose log burned the sequence.
        /// </summary>
        public string ShardId { get; }

        /// <summary>
        /// Gets the burned shard-log sequence.
        /// </summary>
        public long Sequence { get; }

        /// <inheritdoc />
        public bool Equals(CultNetBurnedSequence other) => Sequence == other.Sequence && string.Equals(ShardId, other.ShardId, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultNetBurnedSequence other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (StringComparer.Ordinal.GetHashCode(ShardId ?? string.Empty) * 397) ^ Sequence.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => $"{ShardId}:{Sequence}";
    }

    /// <summary>
    /// Raised to the writer when a shard primary committed changes and could not log them. The commits stand and the
    /// changes were published; each shard's log burned the sequences in <see cref="Burned"/> and compacted past them,
    /// so every replica behind one resynchronizes from a snapshot. A single journal call raises one of these carrying
    /// every burned sequence, whatever number of changes or shards failed. It is deliberately not an
    /// <see cref="InvalidOperationException"/>: callers that read that type as a refused write must not mistake a
    /// committed one for it.
    /// </summary>
    /// <remarks>
    /// The cache may still wrap this in an <see cref="AggregateException"/> when something else failed in the same hold
    /// (an observer, a second database's journal). A writer that wants the log failure catches this type, or unwraps
    /// <see cref="AggregateException.InnerExceptions"/> and looks for it. <see cref="Exception.InnerException"/> is the
    /// store's failure, or an <see cref="AggregateException"/> of them when several changes failed.
    /// </remarks>
    public sealed class CultNetShardLogException : Exception
    {
        /// <summary>
        /// Creates a shard log exception.
        /// </summary>
        public CultNetShardLogException(IReadOnlyList<CultNetBurnedSequence> burned, Exception cause)
            : base($"Committed changes could not be logged; sequences {string.Join(", ", burned.Select(entry => $"{entry.ShardId}:{entry.Sequence}"))} are burned and compacted past.", cause)
        {
            Burned = burned;
        }

        /// <summary>
        /// Gets every sequence burned by the journal call, in commit order.
        /// </summary>
        public IReadOnlyList<CultNetBurnedSequence> Burned { get; }
    }

    /// <summary>
    /// Database-style CultNet facade over a CultCache shard set.
    /// </summary>
    public sealed class CultNetDatabase : IDisposable
    {
        private readonly CultCache _cache;
        private readonly CultNetDocumentRegistry _documents;
        private readonly string _runtimeId;
        private readonly List<CultNetShardDescriptor> _shards;
        private readonly List<CultNetClientAuthorityScope> _clientAuthorityScopes;
        private readonly ICultNetShardMutationLogStore? _mutationLogStore;
        // Keyed by sequence, like the durable store: recording an entry twice replaces it.
        private readonly Dictionary<string, SortedList<long, CultNetShardMutationLogEntry>> _mutationLogs =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _nextLogSequences = new(StringComparer.Ordinal);
        // The highest sequence this primary burned per shard (a change it committed and could not log). Serves as the
        // shard's compaction floor beside the store's own, so history the log cannot serve is answered compacted_history.
        private readonly Dictionary<string, long> _burnedThrough = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _appliedShardSequences = new(StringComparer.Ordinal);
        // CultNet typed selection, section 2: "ordinal is the sequence of the commit that last wrote
        // the row". Kept on every append (LogPrimaryChange and RecordReplicatedEntry are the sites), keyed by (schemaId,
        // recordKey) so the evaluator's order and cursor never read a clock.
        private readonly Dictionary<(string SchemaId, string RecordKey), long> _lastWriteSequence =
            new();
        private readonly Subject<object> _changes = new();
        private readonly IDisposable _cacheChanges;
        private readonly IDisposable _cacheJournal;
        // What a write door of this database knows about the change it is admitting and the cache does not: the wire
        // message it carries, a prediction it reconciles, the replicated log entry it applies. The journal is the only
        // logger and consumes the context under the cache gate; a door registers it under the identity of the change it
        // admits (the instance it writes, or the instance it removes), and the journal takes it once, for that change
        // alone. A write to the same key by anyone else is a different instance and gets no context.
        private readonly ConcurrentDictionary<object, DoorContext> _doors = new(ReferenceIdentity.Instance);
        // One gate over the mutation log, its sequence counters, the last-write map and the publication stash. Lock order:
        // the cache gate, then this, then the log store's own lock. Nothing holding this calls the cache, and a log store
        // must not either (ICultNetShardMutationLogStore). Publication stays outside it: a subscriber may write the cache
        // from its handler.
        private readonly object _logGate = new();
        // Changes the journal has decided and the Watch observer has not yet published, by cache Sequence. The journal
        // runs under the cache gate before any observer, so each change is stashed before it can be published.
        private readonly Dictionary<long, Publication> _stash = new();
        // Predictions this runtime made and has not seen reconciled. Only the journal reads or writes it, under the cache gate.
        private readonly HashSet<string> _predictedDocuments = new(StringComparer.Ordinal);
        private volatile bool _disposed;

        /// <summary>
        /// Creates a database surface over a CultCache instance.
        /// </summary>
        public CultNetDatabase(CultCache cache, CultNetDatabaseOptions? options = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            options ??= new CultNetDatabaseOptions();
            _runtimeId = options.RuntimeId;
            _documents = options.DocumentRegistry ?? new CultNetDocumentRegistry(cache.Registry);
            _mutationLogStore = options.MutationLogStore;
            _shards = (options.Shards == null || options.Shards.Count == 0
                    ? new[] { CultNetShardDescriptor.PrimaryAll(options.RuntimeId) }
                    : options.Shards)
                .ToList();
            _clientAuthorityScopes = (options.ClientAuthorityScopes ?? Array.Empty<CultNetClientAuthorityScope>()).ToList();
            CursorKey = options.CursorKey ?? CultNetSelectionCursorKey.Random();
            InitializeLogSequencesFromStore();
            // The observer first, the journal second: a change the journal stashed always finds its publisher.
            _cacheChanges = _cache.Watch<object>().Subscribe(Publish);
            _cacheJournal = _cache.AddJournal(Journal);
        }

        /// <summary>
        /// This process's cursor key (R-O): random per instance unless <see cref="CultNetDatabaseOptions.CursorKey"/>
        /// supplies one, so a page minted by this database cannot be answered by a differently-keyed one,
        /// and a fresh <see cref="CultNetDatabase"/> - a real process restart, or a simulated one in tests -
        /// mints under a fresh key that a prior cursor's digest does not verify against.
        /// </summary>
        public CultNetSelectionCursorKey CursorKey { get; }

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
        /// Gets the document scopes this runtime may predict before authoritative commit.
        /// </summary>
        public IReadOnlyList<CultNetClientAuthorityScope> ClientAuthorityScopes => _clientAuthorityScopes;

        /// <summary>Gets whether this database instance can authoritatively replace the document at a key.</summary>
        public bool CanWriteAuthoritatively<T>(CultRecordKey key) where T : class
        {
            ThrowIfDisposed();
            var descriptor = _cache.Registry.GetRequired<T>();
            return ResolveShardInternal(descriptor, key).IsPrimary;
        }

        /// <summary>Gets whether this database instance can submit a prediction for the document at a key.</summary>
        public bool CanSubmitPrediction<T>(CultRecordKey key) where T : class
        {
            ThrowIfDisposed();
            var descriptor = _cache.Registry.GetRequired<T>();
            return _clientAuthorityScopes.Any(scope => scope.Matches(_runtimeId, descriptor, key));
        }

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
            lock (_logGate)
            {
                if (!_mutationLogs.TryGetValue(shardId, out var entries))
                {
                    return Array.Empty<CultNetShardMutationLogEntry>();
                }

                var query = entries.Values.Where(entry => entry.Sequence > afterSequence);
                if (limit.HasValue)
                {
                    query = query.Take(limit.Value);
                }

                return query.ToArray();
            }
        }

        /// <summary>
        /// Gets replica catch-up log entries for a shard after the supplied sequence.
        /// </summary>
        public IReadOnlyList<CultNetShardLogEntryMessage> GetMutationLogMessages(
            string shardId,
            long afterSequence = 0,
            int? limit = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            if (_mutationLogStore != null)
            {
                return _mutationLogStore.Read(shardId, afterSequence, limit);
            }

            return GetMutationLog(shardId, afterSequence, limit)
                .Select(ToLogEntryMessage)
                .ToArray();
        }

        /// <summary>
        /// Gets the highest retained-log sequence that has been compacted away for a shard.
        /// </summary>
        public long GetCompactedMutationLogSequence(string shardId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            lock (_logGate)
                return CompactedFloor(shardId);
        }

        // The store's compaction point or this primary's own burned sequences, whichever is higher. Under _logGate.
        private long CompactedFloor(string shardId)
        {
            var burned = _burnedThrough.TryGetValue(shardId, out var through) ? through : 0;
            return Math.Max(burned, _mutationLogStore?.GetCompactedThrough(shardId) ?? 0);
        }

        /// <summary>
        /// Gets the latest known shard-log sequence for a shard.
        /// </summary>
        public long GetLatestMutationLogSequence(string shardId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            lock (_logGate)
            {
                var retained = _mutationLogStore != null
                    ? _mutationLogStore.Read(shardId).Select(entry => entry.Sequence)
                    : GetMutationLog(shardId).Select(entry => entry.Sequence);
                return retained.Append(CompactedFloor(shardId)).Max();
            }
        }

        /// <summary>
        /// Creates a raw document snapshot bounded to one shard. Answers through the one evaluator
        /// (<see cref="CultNetSelectionEvaluator"/>, via <see cref="CultNetDocumentRegistry.SelectPage"/>),
        /// binding-alias expansion included, with the shard membership check as the row filter -
        /// no loop of its own (docs/cultnet-selection-cut.md, Self's rulings 2026-09-22, S2-3).
        /// </summary>
        public CultNetSnapshotResponseRawMessage CreateShardSnapshotResponse(
            CultNetShardDescriptor shard,
            string messageId,
            CultNetSnapshotRequestMessage? filter = null)
        {
            ThrowIfDisposed();
            if (shard == null) throw new ArgumentNullException(nameof(shard));

            // The log sequence is read before any document: a write that lands in between is then replayed from the log on
            // top of documents that already hold it (whole-document puts and deletes, applied in order), and a snapshot can
            // never claim a sequence whose write its documents lack.
            var logSequence = GetLatestMutationLogSequence(shard.ShardId);
            var lowSchemas = CultNetV0SelectionLowering.Lower(filter?.SchemaIds);
            var lowKeys = CultNetV0SelectionLowering.Lower(filter?.RecordKeys);

            // R-R: an explicit empty schemas or keys list is v0's own "answer nothing" - it must not
            // reach the v1 door, which refuses an empty selection.schemas/keys outright.
            if (lowSchemas is { Length: 0 } || lowKeys is { Length: 0 })
            {
                return new CultNetSnapshotResponseRawMessage
                {
                    MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId,
                    Documents = Array.Empty<CultNetRawDocumentRecord>(),
                    ShardId = shard.ShardId,
                    ShardEpoch = shard.Epoch,
                    ShardLogSequence = logSequence
                };
            }

            var selection = new CultNetSelection
            {
                Schemas = lowSchemas,
                Keys = lowKeys,
                Projection = CultNetSelectionProjections.Document
            };
            bool RowFilter(CultDocumentDescriptor descriptor, CultRecordKey key) =>
                !string.IsNullOrWhiteSpace(key.Value) && shard.Matches(descriptor.SchemaId, key);

            // R-G: one evaluation answers the whole shard snapshot, not a loop that re-filters and
            // re-sorts the shard's row set once per 200-row page.
            var page = _documents.SelectAll(_cache, selection, ordinalOf: static (_, _) => 0, asOf: 0, options: null, rowFilter: RowFilter);

            return new CultNetSnapshotResponseRawMessage
            {
                MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId,
                Documents = page.Documents ?? Array.Empty<CultNetRawDocumentRecord>(),
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch,
                ShardLogSequence = logSequence
            };
        }

        /// <summary>
        /// Applies a shard-bounded snapshot and advances the replica cursor to its represented sequence.
        /// </summary>
        public async Task<long> ApplyShardSnapshotResponseAsync(
            CultNetShardDescriptor shard,
            CultNetSnapshotResponseRawMessage response)
        {
            ThrowIfDisposed();
            if (shard == null) throw new ArgumentNullException(nameof(shard));
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (!string.IsNullOrWhiteSpace(response.ShardId) &&
                !string.Equals(response.ShardId, shard.ShardId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Snapshot for shard '{response.ShardId}' cannot be applied to shard '{shard.ShardId}'.");
            }

            if (response.ShardEpoch.HasValue && response.ShardEpoch.Value != shard.Epoch)
            {
                throw new CultNetShardAuthorityException(
                    shard,
                    $"Shard '{shard.ShardId}' is at epoch {shard.Epoch}, not snapshot epoch {response.ShardEpoch.Value}.",
                    "stale_epoch");
            }

            var incomingKeys = response.Documents
                .Where(document => ShardMatchesRawDocument(shard, document))
                .Select(document => document.RecordKey)
                .ToHashSet(StringComparer.Ordinal);
            var local = GetLocalShardDocuments(shard).ToArray();
            foreach (var existing in local.Where(item => !incomingKeys.Contains(item.Key.Value)))
            {
                using (Door(existing.Document, new DoorContext(shard) { NoLog = true, Removal = true }))
                    _cache.Remove(existing.Key);
            }

            foreach (var document in response.Documents.Where(document => ShardMatchesRawDocument(shard, document)))
            {
                var key = new CultRecordKey(document.RecordKey);
                var descriptor = _documents.ResolveDescriptorForRawDocument(document);
                var applied = _documents.DeserializeRawDocument(document);
                using (Door(applied, new DoorContext(shard) { NoLog = true }))
                    await _cache.UpsertAsync(descriptor.DocumentType, applied, key).ConfigureAwait(false);
            }

            var sequence = response.ShardLogSequence ?? 0;
            SetAppliedShardSequence(shard.ShardId, sequence);
            return sequence;
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

        internal void SetAppliedShardSequence(string shardId, long sequence)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be non-negative.");
            _appliedShardSequences[shardId] = sequence;
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
        /// Gets all documents assignable to the requested type.
        /// </summary>
        public IEnumerable<T> GetAll<T>() where T : class
        {
            ThrowIfDisposed();
            return _cache.GetAll<T>();
        }

        /// <summary>
        /// Gets a typed document by its CultName value.
        /// </summary>
        public T? GetByName<T>(string name) where T : class
        {
            ThrowIfDisposed();
            return _cache.GetByName<T>(name);
        }

        /// <summary>
        /// Gets a typed document by an indexed value.
        /// </summary>
        public T? GetByIndex<T>(string alias, string value) where T : class
        {
            ThrowIfDisposed();
            return _cache.GetByIndex<T>(alias, value);
        }

        /// <summary>
        /// Adds or replaces a document at a specific key.
        /// </summary>
        public async Task<CultRecordHandle<T>> PutAsync<T>(CultRecordKey key, T document) where T : class
        {
            ThrowIfDisposed();
            if (document == null) throw new ArgumentNullException(nameof(document));

            var descriptor = _cache.Registry.GetRequired(document.GetType());
            var shard = ResolveShardInternal(descriptor, key);
            EnsurePrimary(shard, descriptor.SchemaId, key);

            return await _cache.UpsertAsync(document, new CultRecordHandle<T>(key)).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies a locally predicted document for a client-owned input scope.
        /// </summary>
        public async Task<CultRecordHandle<T>> PutPredictedAsync<T>(CultRecordKey key, T document) where T : class
        {
            ThrowIfDisposed();
            if (document == null) throw new ArgumentNullException(nameof(document));

            var descriptor = _cache.Registry.GetRequired(document.GetType());
            var shard = ResolveShardInternal(descriptor, key);
            EnsureClientAuthority(descriptor, key);

            CultRecordHandle<T> handle;
            using (Door(document, new DoorContext(shard)
            {
                Kind = CultNetDatabaseChangeKind.Predicted,
                NoLog = true,
                PredictionKey = PredictionKey(descriptor.SchemaId, key),
                Predicts = true
            }))
                handle = await _cache.UpsertAsync(document, new CultRecordHandle<T>(key)).ConfigureAwait(false);
            return handle;
        }

        /// <summary>
        /// Deletes a document by key.
        /// </summary>
        public Task DeleteAsync<T>(CultRecordKey key) where T : class
        {
            ThrowIfDisposed();
            var descriptor = _cache.Registry.GetRequired<T>();
            var shard = ResolveShardInternal(descriptor, key);
            EnsurePrimary(shard, descriptor.SchemaId, key);

            _cache.Remove(new CultRecordHandle<T>(key));
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
            var descriptor = _documents.ResolveDescriptorForRawDocument(message.Document);
            var shard = ResolveShardInternal(descriptor, key);
            EnsurePrimary(shard, descriptor.SchemaId, key, message.ShardEpoch);
            var document = _documents.DeserializeRawDocument(message.Document);
            using (Door(document, new DoorContext(shard) { Put = message, PredictionKey = PredictionKey(descriptor.SchemaId, key) }))
                await _cache.UpsertAsync(descriptor.DocumentType, document, key).ConfigureAwait(false);
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
            var descriptor = _documents.ResolveDescriptorForSchemaId(message.SchemaId);
            var shard = ResolveShardInternal(descriptor, key);
            EnsurePrimary(shard, descriptor.SchemaId, key, message.ShardEpoch);
            var previous = _cache.Get(key);
            if (previous == null)
            {
                return Task.CompletedTask;
            }

            using (Door(previous, new DoorContext(shard) { Delete = message, Removal = true }))
                _cache.Remove(key);
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
            _cacheJournal.Dispose();
            _cacheChanges.Dispose();
            lock (_logGate)
                _stash.Clear();
            _changes.Dispose();
        }

        // The cache is the one owner of "a change was committed"; the journal is this database's one decision point and its
        // one logger. It runs under the cache gate, in cache Sequence order, before any observer: it takes the change's door,
        // settles the change kind and the prediction bookkeeping, logs the change when this runtime is the shard's primary
        // (a replica records only the primary's own entry, which its door carries), and stashes what the publisher needs.
        // A change with no door was written by someone else (a bare cache write, a load, a pull) and is logged like any
        // unattributed commit. It never enters the cache and calls nothing that does.
        private void Journal(IReadOnlyList<CultCacheDocumentChange<object>> changes)
        {
            if (_disposed)
            {
                return;
            }

            List<Exception>? failures = null;
            List<CultNetBurnedSequence>? burned = null;
            List<Exception>? burnCauses = null;
            foreach (var change in changes)
            {
                var identity = change.Document ?? change.PreviousDocument;
                if (identity == null)
                {
                    continue;
                }

                var door = TakeDoor(change);
                var documentType = identity.GetType();
                var descriptor = _cache.Registry.GetRequired(documentType);
                var shard = door?.Shard ?? ResolveShardInternal(descriptor, change.Key);
                var kind = door?.Kind ?? change.Kind switch
                {
                    CultCacheDocumentChangeKind.Removed => CultNetDatabaseChangeKind.Removed,
                    CultCacheDocumentChangeKind.Added => CultNetDatabaseChangeKind.Added,
                    _ => CultNetDatabaseChangeKind.Updated
                };
                if (door?.PredictionKey is { } predictionKey)
                {
                    if (door.Predicts)
                        _predictedDocuments.Add(predictionKey);
                    else if (_predictedDocuments.Remove(predictionKey))
                        kind = CultNetDatabaseChangeKind.Reconciled;
                }

                // The log records a reconciled change as the update it is.
                var logKind = kind == CultNetDatabaseChangeKind.Reconciled ? CultNetDatabaseChangeKind.Updated : kind;
                try
                {
                    if (door?.Replica is { } replicated)
                    {
                        RecordReplicatedEntry(shard, logKind, descriptor.SchemaId, change, replicated);
                    }
                    else if (door?.NoLog != true && shard.IsPrimary && (door != null || _shards.Any(candidate => candidate.Matches(descriptor, change.Key))))
                    {
                        // A door chose its shard; a bare write belongs to a shard only when one matches it.
                        if (LogPrimaryChange(shard, logKind, descriptor.SchemaId, change, door) is { } burn)
                        {
                            (burned ??= new List<CultNetBurnedSequence>()).Add(new CultNetBurnedSequence(shard.ShardId, burn.Sequence));
                            (burnCauses ??= new List<Exception>()).Add(burn.Cause);
                        }
                    }
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }

                lock (_logGate)
                    _stash[change.Sequence] = new Publication(documentType, kind, change.Key, descriptor.SchemaId, shard, change.Document, change.PreviousDocument);
            }

            // Every change is stashed and will be published; the writer hears of a failed log afterwards, in one exception.
            if (burned != null)
            {
                (failures ??= new List<Exception>()).Insert(
                    0,
                    new CultNetShardLogException(burned, burnCauses!.Count == 1 ? burnCauses[0] : new AggregateException(burnCauses)));
            }

            if (failures != null)
            {
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
            }
        }

        // The Watch observer publishes, on the writer's thread after the cache gate opens and before the write returns, what
        // the journal stashed for this change. A change with no stash was admitted before this database existed.
        private void Publish(CultCacheDocumentChange<object> change)
        {
            Publication? publication;
            lock (_logGate)
            {
                if (!_stash.Remove(change.Sequence, out publication))
                {
                    return;
                }
            }

            PublishUntyped(
                publication.DocumentType,
                publication.Kind,
                publication.Key,
                publication.SchemaId,
                publication.Shard,
                publication.Document,
                publication.PreviousDocument);
        }

        private void RecordReplicatedEntry(
            CultNetShardDescriptor shard,
            CultNetDatabaseChangeKind logKind,
            string schemaId,
            CultCacheDocumentChange<object> change,
            CultNetShardLogEntryMessage replicated)
        {
            lock (_logGate)
            {
                RecordMutationLogEntry(new CultNetShardMutationLogEntry(
                    shard.ShardId,
                    shard.Epoch,
                    replicated.Sequence,
                    replicated.CommittedAt,
                    logKind,
                    schemaId,
                    change.Key,
                    change.Document,
                    change.PreviousDocument),
                    replicated);
                _lastWriteSequence[(schemaId, change.Key.Value)] = replicated.Sequence;
            }
        }

        // A primary mints the sequence, appends durably, then records in memory. If it cannot log the change, the change is
        // still committed: the sequence is burned and compacted past, so a replica behind it is told compacted_history and
        // resynchronizes by snapshot. The burn is returned for the journal to tell the writer in one CultNetShardLogException.
        private (long Sequence, Exception Cause)? LogPrimaryChange(
            CultNetShardDescriptor shard,
            CultNetDatabaseChangeKind logKind,
            string schemaId,
            CultCacheDocumentChange<object> change,
            DoorContext? door)
        {
            lock (_logGate)
            {
                var sequence = NextMutationLogSequence(shard.ShardId);
                _lastWriteSequence[(schemaId, change.Key.Value)] = sequence;
                try
                {
                    var entry = new CultNetShardMutationLogEntry(
                        shard.ShardId,
                        shard.Epoch,
                        sequence,
                        DateTimeOffset.UtcNow.ToString("O"),
                        logKind,
                        schemaId,
                        change.Key,
                        change.Document,
                        change.PreviousDocument);
                    // A door's own wire message is what the log stores (its kind, sequence and shard are restated from the entry).
                    var wireEntry = _mutationLogStore == null
                        ? null
                        : door is { Put: not null } or { Delete: not null }
                            ? new CultNetShardLogEntryMessage { Put = door.Put, Delete = door.Delete }
                            : ToLogEntryMessage(entry);
                    RecordMutationLogEntry(entry, wireEntry);
                    return null;
                }
                catch (Exception cause)
                {
                    _burnedThrough[shard.ShardId] = Math.Max(_burnedThrough.TryGetValue(shard.ShardId, out var burned) ? burned : 0, sequence);
                    try
                    {
                        _mutationLogStore?.CompactThrough(shard.ShardId, sequence);
                    }
                    catch (Exception compactCause)
                    {
                        cause = new AggregateException(cause, compactCause);
                    }

                    return (sequence, cause);
                }
            }
        }

        // A door's context belongs to one specific change, recognised by shape: a put door's change carries the instance as
        // its new document; a removal door removes an instance, so its change carries it as the previous document and no
        // new one. A plain removal of an instance a put door admitted is neither, and gets no context. A door's own change
        // is journaled first, in its own hold, and takes its context there.
        private DoorContext? TakeDoor(CultCacheDocumentChange<object> change)
        {
            var identity = change.Document ?? change.PreviousDocument;
            if (identity == null || !_doors.TryGetValue(identity, out var door))
            {
                return null;
            }

            var matches = change.Document == null
                ? door.Removal
                : !door.Removal;
            return matches && ((ICollection<KeyValuePair<object, DoorContext>>)_doors)
                .Remove(new KeyValuePair<object, DoorContext>(identity, door))
                ? door
                : null;
        }

        private DoorRegistration Door(object identity, DoorContext context) =>
            new(_doors, identity, context, _doors.TryAdd(identity, context));

        // Cleanup only: a door whose change never arrived (a refused write) must not leave its context behind.
        private readonly struct DoorRegistration : IDisposable
        {
            private readonly ConcurrentDictionary<object, DoorContext> _doors;
            private readonly object _identity;
            private readonly DoorContext _context;
            private readonly bool _owned;

            public DoorRegistration(ConcurrentDictionary<object, DoorContext> doors, object identity, DoorContext context, bool owned)
            {
                _doors = doors;
                _identity = identity;
                _context = context;
                _owned = owned;
            }

            // True when the journal took the context: the door's write was admitted.
            public bool Consumed => !_owned || !_doors.TryGetValue(_identity, out var held) || !ReferenceEquals(held, _context);

            public void Dispose()
            {
                if (_owned)
                    ((ICollection<KeyValuePair<object, DoorContext>>)_doors).Remove(new KeyValuePair<object, DoorContext>(_identity, _context));
            }
        }

        // What the publisher needs of a change the journal decided.
        private sealed class Publication
        {
            public Publication(
                Type documentType,
                CultNetDatabaseChangeKind kind,
                CultRecordKey key,
                string schemaId,
                CultNetShardDescriptor shard,
                object? document,
                object? previousDocument)
            {
                DocumentType = documentType;
                Kind = kind;
                Key = key;
                SchemaId = schemaId;
                Shard = shard;
                Document = document;
                PreviousDocument = previousDocument;
            }

            public Type DocumentType { get; }
            public CultNetDatabaseChangeKind Kind { get; }
            public CultRecordKey Key { get; }
            public string SchemaId { get; }
            public CultNetShardDescriptor Shard { get; }
            public object? Document { get; }
            public object? PreviousDocument { get; }
        }

        private sealed class DoorContext
        {
            public DoorContext(CultNetShardDescriptor shard) => Shard = shard;

            public CultNetShardDescriptor Shard { get; }
            public CultNetDatabaseChangeKind? Kind { get; set; }
            public bool NoLog { get; set; }
            public bool Removal { get; set; }
            public CultNetDocumentPutRawMessage? Put { get; set; }
            public CultNetDocumentDeleteMessage? Delete { get; set; }
            public CultNetShardLogEntryMessage? Replica { get; set; }
            // The prediction this door's change either records (Predicts) or ends.
            public string? PredictionKey { get; set; }
            public bool Predicts { get; set; }
        }

        private sealed class ReferenceIdentity : IEqualityComparer<object>
        {
            public static readonly ReferenceIdentity Instance = new();

            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
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

        private IEnumerable<TrackedShardDocument> GetLocalShardDocuments(CultNetShardDescriptor shard)
        {
            foreach (var document in _cache.AllEntries)
            {
                var documentType = document.GetType();
                var descriptor = _cache.Registry.GetRequired(documentType);
                var key = GetTrackedKey(document, documentType);
                if (!string.IsNullOrWhiteSpace(key.Value) && shard.Matches(descriptor, key))
                {
                    yield return new TrackedShardDocument(documentType, descriptor.SchemaId, key, document);
                }
            }
        }

        private bool ShardMatchesRawDocument(CultNetShardDescriptor shard, CultNetRawDocumentRecord document)
        {
            if (document == null ||
                string.IsNullOrWhiteSpace(document.SchemaId) ||
                string.IsNullOrWhiteSpace(document.RecordKey))
            {
                return false;
            }

            try
            {
                var descriptor = _documents.ResolveDescriptorForRawDocument(document);
                return shard.Matches(descriptor, new CultRecordKey(document.RecordKey));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private sealed class TrackedShardDocument
        {
            public TrackedShardDocument(Type documentType, string schemaId, CultRecordKey key, object document)
            {
                DocumentType = documentType;
                SchemaId = schemaId;
                Key = key;
                Document = document;
            }

            public Type DocumentType { get; }
            public string SchemaId { get; }
            public CultRecordKey Key { get; }
            public object Document { get; }
        }


        /// <summary>
        /// The shard-log sequence of the commit that last wrote this row, or null when the row has
        /// never been committed through this database. The order the CultNet selection evaluator
        /// pages by (docs/cultnet-selection-cut.md, section 2).
        /// </summary>
        public long? LastWriteSequence(string schemaId, CultRecordKey key)
        {
            lock (_logGate)
                return _lastWriteSequence.TryGetValue((schemaId, key.Value), out var sequence) ? sequence : null;
        }

        /// <summary>
        /// The current write-sequence watermark across every shard this database holds, 0 when nothing
        /// has been committed yet. Only meaningful for an answer that is not scoped to any particular
        /// shard's log - a selection's own <c>asOf</c> must use <see cref="CurrentAsOf(string)"/> instead
        /// (S-9): the shard-log sequence is a per-shard counter (<see cref="NextMutationLogSequence"/>),
        /// so taking the maximum across every shard's rows mixes counters that do not compare to one
        /// another and can report a page as exact "as of" a sequence another, unrelated shard advanced to
        /// while the page's own shard sat still.
        /// </summary>
        public ulong CurrentAsOf()
        {
            lock (_logGate)
                return _lastWriteSequence.Count == 0 ? 0UL : (ulong)_lastWriteSequence.Values.Max();
        }

        /// <summary>
        /// The write-sequence watermark of one shard's own log (S-9): the sequence of the last commit
        /// that shard's log recorded, or 0 when the shard has never taken a write. This is the evaluator's
        /// <c>asOf</c> for a v1 snapshot or subscription page whose matched rows all come from one shard
        /// (<see cref="CultNetDocumentRegistry"/>'s single-shard check) - it advances only when that
        /// shard's own log advances, so a cursor minted against it refuses <c>cursor_stale</c> exactly
        /// when that shard's log has moved, never because an unrelated shard elsewhere took a write the
        /// selection never touched.
        /// </summary>
        public ulong CurrentAsOf(string shardId)
        {
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            lock (_logGate)
                return _nextLogSequences.TryGetValue(shardId, out var next) ? (ulong)(next - 1) : 0UL;
        }

        private void RecordMutationLogEntry(
            CultNetShardMutationLogEntry entry,
            CultNetShardLogEntryMessage? wireEntry = null)
        {
            if (!_mutationLogs.TryGetValue(entry.ShardId, out var entries))
            {
                entries = new SortedList<long, CultNetShardMutationLogEntry>();
                _mutationLogs[entry.ShardId] = entries;
            }

            // Disk first: memory never serves an entry the durable log lacks, and a refused append records nothing.
            if (_mutationLogStore != null && wireEntry != null)
            {
                _mutationLogStore.Append(entry.ShardId, NormalizeWireLogEntry(entry, wireEntry));
            }

            entries[entry.Sequence] = entry;
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

        private void InitializeLogSequencesFromStore()
        {
            if (_mutationLogStore == null)
            {
                return;
            }

            foreach (var shard in _shards)
            {
                // R-Q: the log is the durable record of every commit, replica-applied ones included, so
                // the last-write sequence a restart resumes with is rebuilt from it exactly as
                // LogPrimaryChange/the replica apply paths maintain it live - a row's ordinal for
                // the selection evaluator's order and cursor must not reset to "never written" on restart.
                var entries = _mutationLogStore.Read(shard.ShardId).OrderBy(entry => entry.Sequence).ToArray();
                var compacted = _mutationLogStore.GetCompactedThrough(shard.ShardId);
                var highest = 0L;
                var expected = compacted + 1;
                var hole = 0L;
                foreach (var entry in entries)
                {
                    // A sequence the log lacks between two it holds was burned (its append and its compaction both failed, or
                    // a compaction was interrupted). Replicas must be told compacted_history for it, not served a gapped log.
                    if (entry.Sequence > compacted && entry.Sequence > expected)
                    {
                        hole = entry.Sequence - 1;
                    }

                    expected = Math.Max(expected, entry.Sequence + 1);
                    if (entry.Sequence > highest)
                    {
                        highest = entry.Sequence;
                    }

                    var (schemaId, recordKey) = KeyOf(entry);
                    if (schemaId != null && recordKey != null)
                    {
                        _lastWriteSequence[(schemaId, recordKey)] = entry.Sequence;
                    }
                }

                // Sequences at or below the compaction point are spent even when the log holds none of them.
                highest = Math.Max(highest, compacted);
                if (hole > 0)
                {
                    _burnedThrough[shard.ShardId] = hole;
                }

                if (highest > 0)
                {
                    _nextLogSequences[shard.ShardId] = highest + 1;
                }
            }
        }

        private static (string? SchemaId, string? RecordKey) KeyOf(CultNetShardLogEntryMessage entry)
        {
            if (entry.Put?.Document != null)
            {
                return (entry.Put.Document.SchemaId, entry.Put.Document.RecordKey);
            }

            if (entry.Delete != null)
            {
                return (entry.Delete.SchemaId, entry.Delete.RecordKey);
            }

            return (null, null);
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
            var descriptor = _documents.ResolveDescriptorForRawDocument(message.Document);
            var kind = ChangeKindFromWire(entry.ChangeKind);
            if (kind == CultNetDatabaseChangeKind.Removed)
            {
                throw new InvalidOperationException(
                    $"Shard '{shard.ShardId}' log entry {entry.Sequence} has a removed kind with a put payload.");
            }

            // The replica's log records the primary's own entry under the primary's own sequence, in the same hold that
            // admits the change; the journal does both. A retry after a refused append is a fresh instance, so it matches
            // its door again and re-records the entry.
            var document = _documents.DeserializeRawDocument(message.Document);
            using (Door(document, new DoorContext(shard) { Kind = kind, Replica = entry, PredictionKey = PredictionKey(descriptor.SchemaId, key) }))
                await _cache.UpsertAsync(descriptor.DocumentType, document, key).ConfigureAwait(false);
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
            var admitted = false;
            var previous = _cache.Get(key);
            if (previous != null)
            {
                using var door = Door(previous, new DoorContext(shard) { Kind = CultNetDatabaseChangeKind.Removed, Replica = entry, Removal = true });
                _cache.Remove(key);
                admitted = door.Consumed;
            }

            if (!admitted)
            {
                // The key is not here, so nothing was admitted to log. This replica's log must still hold every entry of the
                // primary's, or a replica chained on it sees a gap.
                lock (_logGate)
                {
                    RecordMutationLogEntry(new CultNetShardMutationLogEntry(
                        shard.ShardId, shard.Epoch, entry.Sequence, entry.CommittedAt, CultNetDatabaseChangeKind.Removed,
                        descriptor.SchemaId, key, document: null, previousDocument: null), entry);
                    _lastWriteSequence[(descriptor.SchemaId, key.Value)] = entry.Sequence;
                }
            }
        }

        private CultNetShardLogEntryMessage ToLogEntryMessage(CultNetShardMutationLogEntry entry)
        {
            if (entry.Kind == CultNetDatabaseChangeKind.Removed || entry.Document == null)
            {
                return new CultNetShardLogEntryMessage
                {
                    Sequence = entry.Sequence,
                    CommittedAt = entry.CommittedAt,
                    ChangeKind = "removed",
                    Delete = new CultNetDocumentDeleteMessage
                    {
                        MessageId = Guid.NewGuid().ToString("N"),
                        SchemaId = entry.SchemaId,
                        RecordKey = entry.Key.Value,
                        ShardId = entry.ShardId,
                        ShardEpoch = entry.ShardEpoch
                    }
                };
            }

            return new CultNetShardLogEntryMessage
            {
                Sequence = entry.Sequence,
                CommittedAt = entry.CommittedAt,
                ChangeKind = entry.Kind == CultNetDatabaseChangeKind.Added ? "added" : "updated",
                Put = new CultNetDocumentPutRawMessage
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    Document = CreateRawDocumentRecord(entry.Key, entry.Document),
                    ShardId = entry.ShardId,
                    ShardEpoch = entry.ShardEpoch
                }
            };
        }

        private CultNetShardLogEntryMessage NormalizeWireLogEntry(
            CultNetShardMutationLogEntry entry,
            CultNetShardLogEntryMessage wireEntry)
        {
            return new CultNetShardLogEntryMessage
            {
                Sequence = entry.Sequence,
                CommittedAt = string.IsNullOrWhiteSpace(entry.CommittedAt) ? wireEntry.CommittedAt : entry.CommittedAt,
                ChangeKind = entry.Kind == CultNetDatabaseChangeKind.Removed
                    ? "removed"
                    : entry.Kind == CultNetDatabaseChangeKind.Added ? "added" : "updated",
                Put = wireEntry.Put == null
                    ? null
                    : new CultNetDocumentPutRawMessage
                    {
                        MessageId = string.IsNullOrWhiteSpace(wireEntry.Put.MessageId)
                            ? Guid.NewGuid().ToString("N")
                            : wireEntry.Put.MessageId,
                        Document = wireEntry.Put.Document,
                        ShardId = entry.ShardId,
                        ShardEpoch = entry.ShardEpoch
                    },
                Delete = wireEntry.Delete == null
                    ? null
                    : new CultNetDocumentDeleteMessage
                    {
                        MessageId = string.IsNullOrWhiteSpace(wireEntry.Delete.MessageId)
                            ? Guid.NewGuid().ToString("N")
                            : wireEntry.Delete.MessageId,
                        SchemaId = wireEntry.Delete.SchemaId,
                        RecordKey = wireEntry.Delete.RecordKey,
                        ShardId = entry.ShardId,
                        ShardEpoch = entry.ShardEpoch
                    }
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
                .Invoke(_documents, new[] { Guid.NewGuid().ToString("N"), handle, document, null });
            return ((CultNetDocumentPutRawMessage)message!).Document;
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

        private void EnsureClientAuthority(string schemaId, CultRecordKey key)
        {
            if (_clientAuthorityScopes.Any(scope => scope.Matches(_runtimeId, schemaId, key)))
            {
                return;
            }

            var shard = ResolveShardInternal(schemaId, key);
            throw new CultNetShardAuthorityException(
                shard,
                $"Runtime '{_runtimeId}' does not have client prediction authority for schema '{schemaId}' key '{key.Value}'.",
                "not_client_authority");
        }

        private void EnsureClientAuthority(CultDocumentDescriptor descriptor, CultRecordKey key)
        {
            if (_clientAuthorityScopes.Any(scope => scope.Matches(_runtimeId, descriptor, key)))
            {
                return;
            }

            var shard = ResolveShardInternal(descriptor, key);
            throw new CultNetShardAuthorityException(
                shard,
                $"Runtime '{_runtimeId}' does not have client prediction authority for schema '{descriptor.SchemaId}' key '{key.Value}'.",
                "not_client_authority");
        }

        private static string PredictionKey(string schemaId, CultRecordKey key)
        {
            return $"{schemaId}:{key.Value}";
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

        private CultNetShardDescriptor ResolveShardInternal(CultDocumentDescriptor descriptor, CultRecordKey key)
        {
            return _shards.FirstOrDefault(shard => shard.Matches(descriptor, key)) ?? _shards[0];
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

        private bool MatchesCatalogFilter(
            CultNetShardDescriptor shard,
            IReadOnlyList<string>? schemaIds,
            IReadOnlyList<CultRecordKey>? recordKeys)
        {
            var schemaMatches = SchemaMatchesCatalogFilter(shard, schemaIds);
            var keyMatches = recordKeys == null ||
                             recordKeys.Count == 0 ||
                             recordKeys.Any(key => string.IsNullOrEmpty(shard.KeyPrefix) ||
                                                   key.Value.StartsWith(shard.KeyPrefix!, StringComparison.Ordinal));
            return schemaMatches && keyMatches;
        }

        private bool SchemaMatchesCatalogFilter(
            CultNetShardDescriptor shard,
            IReadOnlyList<string>? schemaIds)
        {
            if (schemaIds == null || schemaIds.Count == 0 || shard.SchemaIds.Count == 0)
                return true;

            var requestedSchemaIds = schemaIds.ToHashSet(StringComparer.Ordinal);
            if (shard.SchemaIds.Any(requestedSchemaIds.Contains))
                return true;

            foreach (var shardSchemaId in shard.SchemaIds)
            {
                try
                {
                    if (CultNetSchemaAliasMatching.MatchesAny(schemaIds, _cache.Registry.GetRequiredBySchemaId(shardSchemaId)))
                        return true;
                }
                catch (InvalidOperationException)
                {
                    // Shards can advertise schemas that are not loaded in this process.
                }
            }

            return false;
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
