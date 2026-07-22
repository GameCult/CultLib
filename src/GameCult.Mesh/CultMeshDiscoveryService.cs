using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;
using R3;

namespace GameCult.Mesh
{
    public static class CultMeshDiscoverySchemaVersions
    {
        public const string State = "gamecult.mesh.discovery_state.v1";
    }

    public enum CultMeshDiscoveryFreshness
    {
        Fresh,
        Degraded,
        Stale,
        Unavailable
    }

    public enum CultMeshDiscoveryTrust
    {
        Rejected,
        Unsigned,
        Signed
    }

    public sealed class CultMeshDiscoveryQuery
    {
        public CultMeshDiscoveryQuery(string endpointId, IEnumerable<string>? verseIds = null)
        {
            EndpointId = Require(endpointId, nameof(endpointId));
            VerseIds = verseIds?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
        }

        public string EndpointId { get; }
        public IReadOnlyList<string> VerseIds { get; }

        private static string Require(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must be non-empty.", name) : value;
    }

    public sealed class CultMeshDiscoveryObservation
    {
        public CultMeshDiscoveryObservation(
            CultMeshVerseDescriptor candidate,
            string sourceId,
            DateTimeOffset observedAtUtc,
            DateTimeOffset expiresAtUtc,
            CultMeshDiscoveryTrust trust = CultMeshDiscoveryTrust.Unsigned,
            string evidence = "")
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            SourceId = string.IsNullOrWhiteSpace(sourceId)
                ? throw new ArgumentException("Value must be non-empty.", nameof(sourceId))
                : sourceId;
            if (expiresAtUtc < observedAtUtc) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
            ObservedAtUtc = observedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            Trust = trust;
            Evidence = evidence ?? "";
        }

        public CultMeshVerseDescriptor Candidate { get; }
        public string SourceId { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public CultMeshDiscoveryTrust Trust { get; }
        public string Evidence { get; }
    }

    public interface ICultMeshLookupSource
    {
        string SourceId { get; }

        Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default);
    }

    public interface ICultMeshDiscoveryStore
    {
        Task<CultMeshDiscoveryState?> LoadAsync(string endpointId, CancellationToken cancellationToken = default);

        Task SaveAsync(CultMeshDiscoveryState state, CancellationToken cancellationToken = default);
    }

    public sealed class CultMeshDiscoveryCandidate
    {
        public CultMeshDiscoveryCandidate(
            CultMeshVerseDescriptor descriptor,
            string sourceId,
            DateTimeOffset observedAtUtc,
            DateTimeOffset expiresAtUtc,
            CultMeshDiscoveryTrust trust)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            SourceId = sourceId ?? "";
            ObservedAtUtc = observedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            Trust = trust;
        }

        public CultMeshVerseDescriptor Descriptor { get; }
        public string SourceId { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public CultMeshDiscoveryTrust Trust { get; }
    }

    public sealed class CultMeshDiscoveryState
    {
        public CultMeshDiscoveryState(
            string endpointId,
            CultMeshDiscoveryFreshness freshness,
            IEnumerable<CultMeshDiscoveryCandidate>? candidates,
            DateTimeOffset evaluatedAtUtc,
            DateTimeOffset? retryAfterUtc = null,
            IEnumerable<string>? failedSourceIds = null,
            string? queryKey = null)
        {
            EndpointId = endpointId ?? "";
            Freshness = freshness;
            Candidates = candidates?.OrderBy(candidate => candidate.Descriptor.VerseId, StringComparer.Ordinal).ToArray()
                ?? Array.Empty<CultMeshDiscoveryCandidate>();
            EvaluatedAtUtc = evaluatedAtUtc;
            RetryAfterUtc = retryAfterUtc;
            FailedSourceIds = failedSourceIds?.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            QueryKey = string.IsNullOrWhiteSpace(queryKey) ? EndpointId : queryKey;
        }

        public string EndpointId { get; }
        public CultMeshDiscoveryFreshness Freshness { get; }
        public IReadOnlyList<CultMeshDiscoveryCandidate> Candidates { get; }
        public DateTimeOffset EvaluatedAtUtc { get; }
        public DateTimeOffset? RetryAfterUtc { get; }
        public IReadOnlyList<string> FailedSourceIds { get; }
        public string QueryKey { get; }
    }

    public sealed class CultMeshDiscoveryServiceOptions
    {
        public ICultMeshClock Clock { get; set; } = CultMeshSystemClock.Instance;
        public ICultMeshDiagnosticSink Diagnostics { get; set; } = CultMeshNullDiagnosticSink.Instance;
        public ICultMeshDiscoveryStore? Store { get; set; }
        public TimeSpan NegativeTtl { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class CultMeshDiscoveryService : IDisposable
    {
        private readonly ICultMeshLookupSource[] _sources;
        private readonly CultMeshDiscoveryServiceOptions _options;
        private readonly ConcurrentDictionary<string, Lazy<Task<CultMeshDiscoveryState>>> _inFlight = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CultMeshDiscoveryState> _states = new(StringComparer.Ordinal);
        private readonly Subject<CultMeshDiscoveryState> _updates = new();
        private long _diagnosticSequence;
        private bool _disposed;

        public CultMeshDiscoveryService(
            IEnumerable<ICultMeshLookupSource> sources,
            CultMeshDiscoveryServiceOptions? options = null)
        {
            _sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
            if (_sources.Any(source => source == null)) throw new ArgumentException("Lookup sources cannot contain null.", nameof(sources));
            _options = options ?? new CultMeshDiscoveryServiceOptions();
            if (_options.NegativeTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        }

        public Observable<CultMeshDiscoveryState> Watch() => _updates;

        public CultMeshDiscoveryState? Current(string endpointId)
        {
            _states.TryGetValue(endpointId, out var state);
            return state == null ? null : EvaluateAt(state, _options.Clock.UtcNow);
        }

        public async Task<CultMeshDiscoveryState> ResolveAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            ThrowIfDisposed();
            var lookupKey = QueryKey(query);
            var lazy = _inFlight.GetOrAdd(
                lookupKey,
                _ => new Lazy<Task<CultMeshDiscoveryState>>(
                    () => ResolveOwnedAsync(query, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var shared = lazy.Value;
            _ = shared.ContinueWith(
                completed => _inFlight.TryRemove(lookupKey, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return await AwaitForCallerAsync(shared, cancellationToken).ConfigureAwait(false);
        }

        private static string QueryKey(CultMeshDiscoveryQuery query) =>
            query.VerseIds.Count == 0
                ? query.EndpointId
                : query.EndpointId + "\u001f" + string.Join("\u001f", query.VerseIds);

        private static async Task<T> AwaitForCallerAsync<T>(Task<T> shared, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
                return await shared.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false) != shared)
                    throw new OperationCanceledException(cancellationToken);
            }
            return await shared.ConfigureAwait(false);
        }

        private async Task<CultMeshDiscoveryState> ResolveOwnedAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken)
        {
            var queryKey = QueryKey(query);
            var previous = await LoadPreviousAsync(queryKey, cancellationToken).ConfigureAwait(false);
            var now = _options.Clock.UtcNow;
            if (previous != null &&
                ((previous.Freshness == CultMeshDiscoveryFreshness.Fresh &&
                  previous.Candidates.Count > 0 && previous.Candidates.All(candidate => candidate.ExpiresAtUtc > now)) ||
                 (previous.Freshness == CultMeshDiscoveryFreshness.Unavailable && previous.RetryAfterUtc > now)))
            {
                return previous;
            }
            var lookups = _sources.Select(source => ObserveSourceAsync(source, query, cancellationToken)).ToArray();
            var results = await Task.WhenAll(lookups).ConfigureAwait(false);
            var failures = results.Where(result => result.Error != null).Select(result => result.SourceId).ToArray();
            var observations = results.SelectMany(result => result.Observations)
                .Where(observation => query.VerseIds.Count == 0 || query.VerseIds.Contains(observation.Candidate.VerseId, StringComparer.Ordinal))
                .ToArray();

            foreach (var rejected in observations.Where(observation => observation.Trust == CultMeshDiscoveryTrust.Rejected))
            {
                Emit(CultMeshDiagnosticKind.CandidateRejected, query.EndpointId, rejected.Candidate.VerseId,
                    "rejected", "trust_rejected", rejected.SourceId);
            }

            var accepted = observations.Where(observation => observation.Trust != CultMeshDiscoveryTrust.Rejected)
                .GroupBy(observation => observation.Candidate.VerseId, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(observation => observation.Trust)
                    .ThenByDescending(observation => observation.ObservedAtUtc)
                    .First())
                .Select(observation => new CultMeshDiscoveryCandidate(
                    observation.Candidate,
                    observation.SourceId,
                    observation.ObservedAtUtc,
                    observation.ExpiresAtUtc,
                    observation.Trust))
                .ToArray();

            IReadOnlyList<CultMeshDiscoveryCandidate> candidates = accepted.Length > 0
                ? accepted
                : previous?.Candidates ?? Array.Empty<CultMeshDiscoveryCandidate>();
            var freshCount = candidates.Count(candidate => candidate.ExpiresAtUtc > now);
            CultMeshDiscoveryFreshness freshness;
            if (accepted.Length > 0)
                freshness = freshCount == 0
                    ? CultMeshDiscoveryFreshness.Stale
                    : failures.Length > 0 || freshCount < candidates.Count
                        ? CultMeshDiscoveryFreshness.Degraded
                        : CultMeshDiscoveryFreshness.Fresh;
            else if (candidates.Count > 0)
                freshness = freshCount > 0 ? CultMeshDiscoveryFreshness.Degraded : CultMeshDiscoveryFreshness.Stale;
            else
                freshness = CultMeshDiscoveryFreshness.Unavailable;

            var state = new CultMeshDiscoveryState(
                query.EndpointId,
                freshness,
                candidates,
                now,
                freshness == CultMeshDiscoveryFreshness.Fresh ? null : now + _options.NegativeTtl,
                failures,
                queryKey);
            _states[queryKey] = state;
            if (_options.Store != null) await _options.Store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _updates.OnNext(state);
            Emit(CultMeshDiagnosticKind.DiscoveryObservation, query.EndpointId, query.EndpointId,
                freshness.ToString().ToLowerInvariant(), failures.Length == 0 ? "" : "source_failure", "discovery-service");
            return state;
        }

        private static CultMeshDiscoveryState EvaluateAt(CultMeshDiscoveryState state, DateTimeOffset now)
        {
            if (state.Candidates.Count == 0 || state.Freshness == CultMeshDiscoveryFreshness.Unavailable)
                return state;
            var freshCount = state.Candidates.Count(candidate => candidate.ExpiresAtUtc > now);
            var freshness = freshCount == 0
                ? CultMeshDiscoveryFreshness.Stale
                : freshCount < state.Candidates.Count || state.FailedSourceIds.Count > 0
                    ? CultMeshDiscoveryFreshness.Degraded
                    : CultMeshDiscoveryFreshness.Fresh;
            return freshness == state.Freshness
                ? state
                : new CultMeshDiscoveryState(
                    state.EndpointId, freshness, state.Candidates, now,
                    state.RetryAfterUtc, state.FailedSourceIds, state.QueryKey);
        }

        private async Task<CultMeshDiscoveryState?> LoadPreviousAsync(string queryKey, CancellationToken cancellationToken)
        {
            if (_states.TryGetValue(queryKey, out var current)) return current;
            if (_options.Store == null) return null;
            var stored = await _options.Store.LoadAsync(queryKey, cancellationToken).ConfigureAwait(false);
            if (stored != null) _states[queryKey] = stored;
            return stored;
        }

        private static async Task<LookupResult> ObserveSourceAsync(
            ICultMeshLookupSource source,
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken)
        {
            try
            {
                var observations = await source.LookupAsync(query, cancellationToken).ConfigureAwait(false);
                return new LookupResult(source.SourceId, observations ?? Array.Empty<CultMeshDiscoveryObservation>(), null);
            }
            catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                return new LookupResult(source.SourceId, Array.Empty<CultMeshDiscoveryObservation>(), error);
            }
        }

        private void Emit(CultMeshDiagnosticKind kind, string operationId, string subjectId, string state, string reason, string source)
        {
            _options.Diagnostics.Emit(new CultMeshDiagnosticEvent(
                Interlocked.Increment(ref _diagnosticSequence), _options.Clock.UtcNow,
                CultMeshReliabilityOrgan.Discovery, kind, operationId, subjectId, state,
                reason, source, subjectId, CultMeshDiscoverySchemaVersions.State));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _updates.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshDiscoveryService));
        }

        private sealed class LookupResult
        {
            public LookupResult(string sourceId, IReadOnlyList<CultMeshDiscoveryObservation> observations, Exception? error)
            {
                SourceId = sourceId;
                Observations = observations;
                Error = error;
            }

            public string SourceId { get; }
            public IReadOnlyList<CultMeshDiscoveryObservation> Observations { get; }
            public Exception? Error { get; }
        }
    }

    [MessagePackObject]
    [CultDocument("gamecult.mesh.discovery_state", CultMeshDiscoverySchemaVersions.State)]
    public sealed class CultMeshDiscoveryStateDocument
    {
        [Key(0), CultName] public string EndpointId { get; set; } = "";
        [Key(1)] public CultMeshDiscoveryFreshness Freshness { get; set; }
        [Key(2)] public string EvaluatedAtUtc { get; set; } = "";
        [Key(3)] public string? RetryAfterUtc { get; set; }
        [Key(4)] public string[] FailedSourceIds { get; set; } = Array.Empty<string>();
        [Key(5)] public CultMeshDiscoveryCandidateDocument[] Candidates { get; set; } = Array.Empty<CultMeshDiscoveryCandidateDocument>();
        [Key(6)] public string QueryKey { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class CultMeshDiscoveryCandidateDocument
    {
        [Key(0)] public CultMeshVerseDescriptorMessage Descriptor { get; set; } = new CultMeshVerseDescriptorMessage();
        [Key(1)] public string SourceId { get; set; } = "";
        [Key(2)] public string ObservedAtUtc { get; set; } = "";
        [Key(3)] public string ExpiresAtUtc { get; set; } = "";
        [Key(4)] public CultMeshDiscoveryTrust Trust { get; set; }
    }

    public sealed class CultMeshCultCacheDiscoveryStore : ICultMeshDiscoveryStore
    {
        private readonly CultCache _cache;

        public CultMeshCultCacheDiscoveryStore(CultCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public Task<CultMeshDiscoveryState?> LoadAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = _cache.Get<CultMeshDiscoveryStateDocument>(Key(endpointId));
            return Task.FromResult(document == null ? null : FromDocument(document));
        }

        public async Task SaveAsync(CultMeshDiscoveryState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _cache.UpsertAsync(typeof(CultMeshDiscoveryStateDocument), ToDocument(state), Key(state.QueryKey)).ConfigureAwait(false);
            await _cache.FlushAsync().ConfigureAwait(false);
        }

        private static CultRecordKey Key(string endpointId) => new CultRecordKey("mesh:discovery:" + StableToken(endpointId));

        private static string StableToken(string value)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value ?? "")).Select(item => item.ToString("x2")));
        }

        private static CultMeshDiscoveryStateDocument ToDocument(CultMeshDiscoveryState state) => new CultMeshDiscoveryStateDocument
        {
            EndpointId = state.EndpointId,
            Freshness = state.Freshness,
            EvaluatedAtUtc = state.EvaluatedAtUtc.ToString("O"),
            RetryAfterUtc = state.RetryAfterUtc?.ToString("O"),
            FailedSourceIds = state.FailedSourceIds.ToArray(),
            QueryKey = state.QueryKey,
            Candidates = state.Candidates.Select(candidate => new CultMeshDiscoveryCandidateDocument
            {
                Descriptor = candidate.Descriptor.ToMessage(),
                SourceId = candidate.SourceId,
                ObservedAtUtc = candidate.ObservedAtUtc.ToString("O"),
                ExpiresAtUtc = candidate.ExpiresAtUtc.ToString("O"),
                Trust = candidate.Trust
            }).ToArray()
        };

        private static CultMeshDiscoveryState FromDocument(CultMeshDiscoveryStateDocument document) => new CultMeshDiscoveryState(
            document.EndpointId,
            document.Freshness,
            document.Candidates.Select(candidate => new CultMeshDiscoveryCandidate(
                candidate.Descriptor.ToVerseDescriptor(),
                candidate.SourceId,
                DateTimeOffset.Parse(candidate.ObservedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(candidate.ExpiresAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
                candidate.Trust)),
            DateTimeOffset.Parse(document.EvaluatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            string.IsNullOrWhiteSpace(document.RetryAfterUtc) ? null : DateTimeOffset.Parse(document.RetryAfterUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            document.FailedSourceIds,
            string.IsNullOrWhiteSpace(document.QueryKey) ? document.EndpointId : document.QueryKey);
    }
}
