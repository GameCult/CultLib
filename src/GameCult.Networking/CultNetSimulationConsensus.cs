using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// One witness report about a deterministic simulation fact.
    /// </summary>
    [MessagePackObject]
    public sealed class CultNetSimulationObservation
    {
        /// <summary>
        /// Gets or sets the observing runtime id.
        /// </summary>
        [Key("witnessRuntimeId")] public string WitnessRuntimeId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard id containing the observed simulation.
        /// </summary>
        [Key("shardId")] public string ShardId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard epoch observed by the witness.
        /// </summary>
        [Key("shardEpoch")] public long ShardEpoch { get; set; }
        /// <summary>
        /// Gets or sets the simulation frame or tick.
        /// </summary>
        [Key("frame")] public long Frame { get; set; }
        /// <summary>
        /// Gets or sets the subject this observation is about.
        /// </summary>
        [Key("subjectId")] public string SubjectId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the kind of claim, such as hit, input, collision, or ownership.
        /// </summary>
        [Key("claimKind")] public string ClaimKind { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the content hash of the witness claim.
        /// </summary>
        [Key("claimHash")] public string ClaimHash { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets optional human-readable or app-specific claim detail.
        /// </summary>
        [Key("claimSummary")] public string? ClaimSummary { get; set; }
        /// <summary>
        /// Gets or sets the witness weight. Normal clients should use 1.
        /// </summary>
        [Key("weight")] public double Weight { get; set; } = 1d;
        /// <summary>
        /// Gets or sets the observation timestamp.
        /// </summary>
        [Key("observedAt")] public string ObservedAt { get; set; } = string.Empty;

        /// <summary>
        /// Computes a stable claim hash from ordered claim parts.
        /// </summary>
        public static string ComputeClaimHash(params string[] parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            var canonical = string.Join("\u001F", parts.Select(part => part ?? string.Empty));
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return string.Concat(bytes.Select(value => value.ToString("x2")));
        }
    }

    /// <summary>
    /// Controls deterministic witness aggregation.
    /// </summary>
    public sealed class CultNetSimulationConsensusOptions
    {
        /// <summary>
        /// Gets or sets the minimum distinct witnesses required before a claim can be accepted.
        /// </summary>
        public int MinimumWitnesses { get; set; } = 1;
        /// <summary>
        /// Gets or sets the minimum weight required before a claim can be accepted.
        /// </summary>
        public double MinimumWeight { get; set; } = 1d;
        /// <summary>
        /// Gets or sets the fraction of observed weight required for quorum.
        /// </summary>
        public double QuorumRatio { get; set; } = 0.5d;
    }

    /// <summary>
    /// A deterministic consensus candidate derived from witness observations.
    /// </summary>
    public sealed class CultNetSimulationConsensusCandidate
    {
        /// <summary>
        /// Creates a consensus candidate.
        /// </summary>
        public CultNetSimulationConsensusCandidate(
            string shardId,
            long shardEpoch,
            long frame,
            string subjectId,
            string claimKind,
            string claimHash,
            string? claimSummary,
            int witnessCount,
            double supportWeight,
            double totalWeight,
            bool hasQuorum)
        {
            ShardId = shardId;
            ShardEpoch = shardEpoch;
            Frame = frame;
            SubjectId = subjectId;
            ClaimKind = claimKind;
            ClaimHash = claimHash;
            ClaimSummary = claimSummary;
            WitnessCount = witnessCount;
            SupportWeight = supportWeight;
            TotalWeight = totalWeight;
            HasQuorum = hasQuorum;
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
        /// Gets the simulation frame.
        /// </summary>
        public long Frame { get; }
        /// <summary>
        /// Gets the subject id.
        /// </summary>
        public string SubjectId { get; }
        /// <summary>
        /// Gets the claim kind.
        /// </summary>
        public string ClaimKind { get; }
        /// <summary>
        /// Gets the claim hash.
        /// </summary>
        public string ClaimHash { get; }
        /// <summary>
        /// Gets optional claim detail.
        /// </summary>
        public string? ClaimSummary { get; }
        /// <summary>
        /// Gets the distinct witness count supporting this candidate.
        /// </summary>
        public int WitnessCount { get; }
        /// <summary>
        /// Gets supporting witness weight.
        /// </summary>
        public double SupportWeight { get; }
        /// <summary>
        /// Gets total observed witness weight for the same subject/frame/kind.
        /// </summary>
        public double TotalWeight { get; }
        /// <summary>
        /// Gets whether the candidate crossed the configured quorum.
        /// </summary>
        public bool HasQuorum { get; }
        /// <summary>
        /// Gets support divided by total observed weight.
        /// </summary>
        public double Confidence => TotalWeight <= 0d ? 0d : SupportWeight / TotalWeight;
    }

    /// <summary>
    /// Aggregates simulation observations into deterministic consensus candidates.
    /// </summary>
    public sealed class CultNetSimulationConsensus
    {
        private readonly CultNetSimulationConsensusOptions _options;

        /// <summary>
        /// Creates a consensus aggregator.
        /// </summary>
        public CultNetSimulationConsensus(CultNetSimulationConsensusOptions? options = null)
        {
            _options = options ?? new CultNetSimulationConsensusOptions();
        }

        /// <summary>
        /// Returns the strongest candidate for each observed shard/frame/subject/claim kind.
        /// </summary>
        public IReadOnlyList<CultNetSimulationConsensusCandidate> BuildCandidates(
            IEnumerable<CultNetSimulationObservation> observations)
        {
            if (observations == null) throw new ArgumentNullException(nameof(observations));
            var clean = observations
                .Where(IsValid)
                .GroupBy(DeduplicationKey)
                .Select(group => group
                    .OrderByDescending(observation => observation.Weight)
                    .ThenBy(observation => observation.WitnessRuntimeId, StringComparer.Ordinal)
                    .First())
                .ToArray();

            return clean
                .GroupBy(SubjectKey)
                .Select(BuildCandidate)
                .OrderBy(candidate => candidate.ShardId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ShardEpoch)
                .ThenBy(candidate => candidate.Frame)
                .ThenBy(candidate => candidate.SubjectId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ClaimKind, StringComparer.Ordinal)
                .ToArray();
        }

        private CultNetSimulationConsensusCandidate BuildCandidate(
            IGrouping<string, CultNetSimulationObservation> subjectGroup)
        {
            var totalWeight = subjectGroup.Sum(WeightOf);
            var bestClaim = subjectGroup
                .GroupBy(observation => observation.ClaimHash, StringComparer.Ordinal)
                .Select(group => new
                {
                    ClaimHash = group.Key,
                    Observations = group.ToArray(),
                    Weight = group.Sum(WeightOf)
                })
                .OrderByDescending(candidate => candidate.Weight)
                .ThenBy(candidate => candidate.ClaimHash, StringComparer.Ordinal)
                .First();
            var first = bestClaim.Observations
                .OrderBy(observation => observation.WitnessRuntimeId, StringComparer.Ordinal)
                .First();
            var witnessCount = bestClaim.Observations
                .Select(observation => observation.WitnessRuntimeId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var hasQuorum = witnessCount >= _options.MinimumWitnesses &&
                            bestClaim.Weight >= _options.MinimumWeight &&
                            totalWeight > 0d &&
                            bestClaim.Weight / totalWeight >= _options.QuorumRatio;

            return new CultNetSimulationConsensusCandidate(
                first.ShardId,
                first.ShardEpoch,
                first.Frame,
                first.SubjectId,
                first.ClaimKind,
                bestClaim.ClaimHash,
                first.ClaimSummary,
                witnessCount,
                bestClaim.Weight,
                totalWeight,
                hasQuorum);
        }

        private static bool IsValid(CultNetSimulationObservation observation)
        {
            return observation != null &&
                   !string.IsNullOrWhiteSpace(observation.WitnessRuntimeId) &&
                   !string.IsNullOrWhiteSpace(observation.ShardId) &&
                   !string.IsNullOrWhiteSpace(observation.SubjectId) &&
                   !string.IsNullOrWhiteSpace(observation.ClaimKind) &&
                   !string.IsNullOrWhiteSpace(observation.ClaimHash) &&
                   observation.Weight > 0d;
        }

        private static double WeightOf(CultNetSimulationObservation observation)
        {
            return observation.Weight <= 0d ? 0d : observation.Weight;
        }

        private static string DeduplicationKey(CultNetSimulationObservation observation)
        {
            return string.Join("|",
                observation.WitnessRuntimeId,
                observation.ShardId,
                observation.ShardEpoch,
                observation.Frame,
                observation.SubjectId,
                observation.ClaimKind);
        }

        private static string SubjectKey(CultNetSimulationObservation observation)
        {
            return string.Join("|",
                observation.ShardId,
                observation.ShardEpoch,
                observation.Frame,
                observation.SubjectId,
                observation.ClaimKind);
        }
    }
}
