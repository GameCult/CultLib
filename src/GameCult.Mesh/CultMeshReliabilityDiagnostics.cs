using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Mesh
{
    /// <summary>
    /// Clock used by CultMesh reliability owners.
    /// </summary>
    public interface ICultMeshClock
    {
        DateTimeOffset UtcNow { get; }

        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Wall clock used outside deterministic tests.
    /// </summary>
    public sealed class CultMeshSystemClock : ICultMeshClock
    {
        public static CultMeshSystemClock Instance { get; } = new CultMeshSystemClock();

        private CultMeshSystemClock()
        {
        }

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public enum CultMeshReliabilityOrgan
    {
        Discovery,
        Session,
        Authority,
        ContentTransfer,
        Stream
    }

    public enum CultMeshDiagnosticKind
    {
        DiscoveryObservation,
        CandidateRejected,
        ConnectionAttempt,
        PathChanged,
        RetryDecision,
        AuthorityDecision,
        VerifiedRange,
        StreamGap
    }

    /// <summary>
    /// One bounded operational observation emitted by a CultMesh reliability owner.
    /// </summary>
    public sealed class CultMeshDiagnosticEvent
    {
        public CultMeshDiagnosticEvent(
            long sequence,
            DateTimeOffset observedAtUtc,
            CultMeshReliabilityOrgan organ,
            CultMeshDiagnosticKind kind,
            string operationId,
            string subjectId,
            string state,
            string reasonCode = "",
            string sourceId = "",
            string endpoint = "",
            string schemaVersion = "",
            string libraryVersion = "")
        {
            if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            Sequence = sequence;
            ObservedAtUtc = observedAtUtc;
            Organ = organ;
            Kind = kind;
            OperationId = operationId ?? "";
            SubjectId = subjectId ?? "";
            State = state ?? "";
            ReasonCode = reasonCode ?? "";
            SourceId = sourceId ?? "";
            Endpoint = endpoint ?? "";
            SchemaVersion = schemaVersion ?? "";
            LibraryVersion = string.IsNullOrWhiteSpace(libraryVersion)
                ? CultMeshLibraryVersion.Current
                : libraryVersion;
        }

        public long Sequence { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public CultMeshReliabilityOrgan Organ { get; }
        public CultMeshDiagnosticKind Kind { get; }
        public string OperationId { get; }
        public string SubjectId { get; }
        public string State { get; }
        public string ReasonCode { get; }
        public string SourceId { get; }
        public string Endpoint { get; }
        public string SchemaVersion { get; }
        public string LibraryVersion { get; }
    }

    public interface ICultMeshDiagnosticSink
    {
        void Emit(CultMeshDiagnosticEvent diagnostic);
    }

    public sealed class CultMeshNullDiagnosticSink : ICultMeshDiagnosticSink
    {
        public static CultMeshNullDiagnosticSink Instance { get; } = new CultMeshNullDiagnosticSink();

        private CultMeshNullDiagnosticSink()
        {
        }

        public void Emit(CultMeshDiagnosticEvent diagnostic)
        {
        }
    }

    /// <summary>
    /// Bounded diagnostic projection suitable for inspection and tests.
    /// </summary>
    public sealed class CultMeshDiagnosticBuffer : ICultMeshDiagnosticSink
    {
        private readonly object _gate = new object();
        private readonly Queue<CultMeshDiagnosticEvent> _events;

        public CultMeshDiagnosticBuffer(int capacity = 256)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            _events = new Queue<CultMeshDiagnosticEvent>(capacity);
        }

        public int Capacity { get; }

        public IReadOnlyList<CultMeshDiagnosticEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }

        public void Emit(CultMeshDiagnosticEvent diagnostic)
        {
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
            lock (_gate)
            {
                while (_events.Count >= Capacity)
                {
                    _events.Dequeue();
                }
                _events.Enqueue(diagnostic);
            }
        }
    }

    public static class CultMeshLibraryVersion
    {
        public static string Current { get; } = Resolve();

        private static string Resolve()
        {
            var assembly = typeof(CultMeshLibraryVersion).Assembly;
            var informational = assembly
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : informational;
        }
    }
}
