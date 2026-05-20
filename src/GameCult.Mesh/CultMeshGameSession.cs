using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using R3;

namespace GameCult.Mesh
{
    /// <summary>
    /// Options for creating a gameplay-facing CultMesh session facade.
    /// </summary>
    public sealed class CultMeshGameSessionOptions
    {
        /// <summary>
        /// Gets or sets the Verse catalog used by the session.
        /// </summary>
        public CultMeshVerseCatalog? VerseCatalog { get; set; }

        /// <summary>
        /// Gets or sets the peer catalog used by the session.
        /// </summary>
        public CultMeshPeerCatalog? PeerCatalog { get; set; }

        /// <summary>
        /// Gets or sets the authority lease catalog used by the session.
        /// </summary>
        public CultMeshAuthorityLeaseCatalog? AuthorityLeases { get; set; }

        /// <summary>
        /// Gets or sets simulation consensus options.
        /// </summary>
        public CultNetSimulationConsensusOptions? ConsensusOptions { get; set; }

        /// <summary>
        /// Gets or sets whether incoming observation messages should update the session hub.
        /// </summary>
        public bool ServeSimulationObservations { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the node should answer Verse catalog requests from the session catalog.
        /// </summary>
        public bool ServeVerseDiscovery { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the node should answer peer exchange requests from the session peer catalog.
        /// </summary>
        public bool ServePeerExchange { get; set; } = true;
    }

    /// <summary>
    /// Gameplay-facing facade over common CultMesh runtime pieces.
    /// </summary>
    public sealed class CultMeshGameSession : IDisposable
    {
        private readonly List<IDisposable> _owned = new();
        private readonly HashSet<string> _committedFactIds = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>
        /// Creates a game session facade.
        /// </summary>
        public CultMeshGameSession(CultMeshNode node, CultMeshGameSessionOptions? options = null)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
            options ??= new CultMeshGameSessionOptions();
            VerseCatalog = options.VerseCatalog ?? new CultMeshVerseCatalog();
            PeerCatalog = options.PeerCatalog ?? new CultMeshPeerCatalog();
            AuthorityLeases = options.AuthorityLeases ?? new CultMeshAuthorityLeaseCatalog();
            ObservationHub = new CultNetSimulationObservationHub(options.ConsensusOptions);
            FactCommitter = new CultMeshSimulationFactCommitter(Node.Database);

            if (options.VerseCatalog == null)
            {
                _owned.Add(VerseCatalog);
            }

            if (options.PeerCatalog == null)
            {
                _owned.Add(PeerCatalog);
            }

            if (options.ServeSimulationObservations)
            {
                _owned.Add(new CultNetSimulationObservationServer(Node.Server, ObservationHub));
            }

            if (options.ServeVerseDiscovery)
            {
                _owned.Add(new CultMeshVerseDiscoveryServer(Node.Server, VerseCatalog));
            }

            if (options.ServePeerExchange)
            {
                _owned.Add(new CultMeshPeerExchangeServer(Node.Server, PeerCatalog));
            }
        }

        /// <summary>
        /// Gets the underlying node.
        /// </summary>
        public CultMeshNode Node { get; }

        /// <summary>
        /// Gets the session Verse catalog.
        /// </summary>
        public CultMeshVerseCatalog VerseCatalog { get; }

        /// <summary>
        /// Gets the session peer catalog.
        /// </summary>
        public CultMeshPeerCatalog PeerCatalog { get; }

        /// <summary>
        /// Gets the session authority lease catalog.
        /// </summary>
        public CultMeshAuthorityLeaseCatalog AuthorityLeases { get; }

        /// <summary>
        /// Gets the session simulation observation hub.
        /// </summary>
        public CultNetSimulationObservationHub ObservationHub { get; }

        /// <summary>
        /// Gets the session simulation fact committer.
        /// </summary>
        public CultMeshSimulationFactCommitter FactCommitter { get; }

        /// <summary>
        /// Watches consensus candidate updates.
        /// </summary>
        public Observable<CultNetSimulationConsensusCandidate> WatchCandidates()
        {
            ThrowIfDisposed();
            return ObservationHub.WatchCandidates();
        }

        /// <summary>
        /// Watches committed simulation facts.
        /// </summary>
        public Observable<CultNetDatabaseChange<CultMeshSimulationFact>> WatchSimulationFacts()
        {
            ThrowIfDisposed();
            return Node.Database.Watch<CultMeshSimulationFact>();
        }

        /// <summary>
        /// Applies a local client prediction through the underlying database.
        /// </summary>
        public Task<CultRecordHandle<T>> PredictAsync<T>(CultRecordKey key, T document) where T : class
        {
            ThrowIfDisposed();
            return Node.Database.PutPredictedAsync(key, document);
        }

        /// <summary>
        /// Submits one witness observation and returns the current candidates for that subject.
        /// </summary>
        public IReadOnlyList<CultNetSimulationConsensusCandidate> SubmitObservation(
            CultNetSimulationObservation observation)
        {
            ThrowIfDisposed();
            return ObservationHub.Submit(observation);
        }

        /// <summary>
        /// Submits one witness observation and commits any new quorum facts it creates.
        /// </summary>
        public async Task<IReadOnlyList<CultRecordHandle<CultMeshSimulationFact>>> SubmitAndCommitAsync(
            CultNetSimulationObservation observation)
        {
            ThrowIfDisposed();
            var candidates = ObservationHub.Submit(observation);
            return await CommitQuorumCandidatesAsync(candidates).ConfigureAwait(false);
        }

        /// <summary>
        /// Commits quorum candidates that have not already been committed by this session.
        /// </summary>
        public async Task<IReadOnlyList<CultRecordHandle<CultMeshSimulationFact>>> CommitQuorumCandidatesAsync(
            IEnumerable<CultNetSimulationConsensusCandidate> candidates)
        {
            ThrowIfDisposed();
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));

            var handles = new List<CultRecordHandle<CultMeshSimulationFact>>();
            foreach (var candidate in candidates.Where(candidate => candidate.HasQuorum))
            {
                var factId = CultMeshSimulationFact.ComputeFactId(candidate);
                if (!_committedFactIds.Add(factId))
                {
                    continue;
                }

                var key = CultMeshSimulationFact.CreateRecordKey(candidate);
                if (await Node.Database.GetAsync<CultMeshSimulationFact>(key).ConfigureAwait(false) != null)
                {
                    continue;
                }

                handles.Add(await FactCommitter.CommitAsync(candidate).ConfigureAwait(false));
            }

            return handles;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var owned in _owned)
            {
                owned.Dispose();
            }

            ObservationHub.Dispose();
            _owned.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultMeshGameSession));
            }
        }
    }
}
