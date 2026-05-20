using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    /// <summary>
    /// Committed simulation fact derived from witness consensus.
    /// </summary>
    [CultDocument("gamecult.mesh.simulation_fact", "gamecult.mesh.simulation_fact.v1")]
    public sealed class CultMeshSimulationFact
    {
        /// <summary>
        /// Stable fact identifier.
        /// </summary>
        [Key(0)]
        [CultIndex]
        public string FactId { get; set; } = string.Empty;

        /// <summary>
        /// Shard containing the simulation.
        /// </summary>
        [Key(1)]
        [CultIndex]
        public string ShardId { get; set; } = string.Empty;

        /// <summary>
        /// Shard authority epoch observed by witnesses.
        /// </summary>
        [Key(2)]
        public long ShardEpoch { get; set; }

        /// <summary>
        /// Simulation frame or tick.
        /// </summary>
        [Key(3)]
        [CultIndex]
        public long Frame { get; set; }

        /// <summary>
        /// Subject the fact is about.
        /// </summary>
        [Key(4)]
        [CultIndex]
        public string SubjectId { get; set; } = string.Empty;

        /// <summary>
        /// Fact kind, such as hit, collision, or ownership.
        /// </summary>
        [Key(5)]
        [CultIndex]
        public string ClaimKind { get; set; } = string.Empty;

        /// <summary>
        /// Hash of the accepted claim.
        /// </summary>
        [Key(6)]
        public string ClaimHash { get; set; } = string.Empty;

        /// <summary>
        /// Optional readable or app-specific claim detail.
        /// </summary>
        [Key(7)]
        public string? ClaimSummary { get; set; }

        /// <summary>
        /// Number of distinct witnesses supporting the fact.
        /// </summary>
        [Key(8)]
        public int WitnessCount { get; set; }

        /// <summary>
        /// Supporting witness weight.
        /// </summary>
        [Key(9)]
        public double SupportWeight { get; set; }

        /// <summary>
        /// Total observed weight for the same subject/frame/kind.
        /// </summary>
        [Key(10)]
        public double TotalWeight { get; set; }

        /// <summary>
        /// Support divided by total observed weight.
        /// </summary>
        [Key(11)]
        public double Confidence { get; set; }

        /// <summary>
        /// Commit timestamp.
        /// </summary>
        [Key(12)]
        public string CommittedAt { get; set; } = string.Empty;

        /// <summary>
        /// Builds a deterministic record key for a simulation fact.
        /// </summary>
        public static CultRecordKey CreateRecordKey(CultNetSimulationConsensusCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            return new CultRecordKey("simulation:" + ComputeFactId(candidate));
        }

        /// <summary>
        /// Builds a stable fact id for a consensus candidate.
        /// </summary>
        public static string ComputeFactId(CultNetSimulationConsensusCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            var canonical = string.Join("\u001F",
                candidate.ShardId,
                candidate.ShardEpoch,
                candidate.Frame,
                candidate.SubjectId,
                candidate.ClaimKind);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return string.Concat(bytes.Select(value => value.ToString("x2")));
        }

        /// <summary>
        /// Creates a committed fact document from a quorum candidate.
        /// </summary>
        public static CultMeshSimulationFact FromCandidate(
            CultNetSimulationConsensusCandidate candidate,
            string? committedAt = null)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            return new CultMeshSimulationFact
            {
                FactId = ComputeFactId(candidate),
                ShardId = candidate.ShardId,
                ShardEpoch = candidate.ShardEpoch,
                Frame = candidate.Frame,
                SubjectId = candidate.SubjectId,
                ClaimKind = candidate.ClaimKind,
                ClaimHash = candidate.ClaimHash,
                ClaimSummary = candidate.ClaimSummary,
                WitnessCount = candidate.WitnessCount,
                SupportWeight = candidate.SupportWeight,
                TotalWeight = candidate.TotalWeight,
                Confidence = candidate.Confidence,
                CommittedAt = string.IsNullOrWhiteSpace(committedAt)
                    ? DateTimeOffset.UtcNow.ToString("O")
                    : committedAt!
            };
        }
    }

    /// <summary>
    /// Commits quorum simulation candidates into the CultMesh database.
    /// </summary>
    public sealed class CultMeshSimulationFactCommitter
    {
        private readonly CultNetDatabase _database;

        /// <summary>
        /// Creates a simulation fact committer.
        /// </summary>
        public CultMeshSimulationFactCommitter(CultNetDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Commits a quorum candidate as a simulation fact document.
        /// </summary>
        public async Task<CultRecordHandle<CultMeshSimulationFact>> CommitAsync(
            CultNetSimulationConsensusCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (!candidate.HasQuorum)
            {
                throw new InvalidOperationException("Simulation candidate cannot be committed before quorum.");
            }

            var fact = CultMeshSimulationFact.FromCandidate(candidate);
            return await _database.PutAsync(CultMeshSimulationFact.CreateRecordKey(candidate), fact)
                .ConfigureAwait(false);
        }
    }
}
