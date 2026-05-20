using System;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Portable manifest for one inspectable pipeline witness bundle.
    /// </summary>
    [MessagePackObject]
    [CultDocument("cultnet.witness_artifact_bundle", CultNetSchemaVersions.WitnessArtifactBundle)]
    public sealed class CultWitnessArtifactBundle
    {
        /// <summary>
        /// Gets or sets the stable bundle identifier.
        /// </summary>
        [Key(0)]
        [CultName]
        public string BundleId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the witness family or pipeline kind.
        /// </summary>
        [Key(1)]
        public string WitnessKind { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the manifest was captured.
        /// </summary>
        [Key(2)]
        public string CapturedAt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pinned subject this witness bundle describes.
        /// </summary>
        [Key(3)]
        public CultWitnessSubjectPin Subject { get; set; } = new CultWitnessSubjectPin();

        /// <summary>
        /// Gets or sets the schema and contract pins required to interpret the witness.
        /// </summary>
        [Key(4)]
        public CultWitnessContractPin[] Contracts { get; set; } = Array.Empty<CultWitnessContractPin>();

        /// <summary>
        /// Gets or sets the inspectable artifact pointers carried by the bundle.
        /// </summary>
        [Key(5)]
        public CultWitnessArtifactEntry[] Artifacts { get; set; } = Array.Empty<CultWitnessArtifactEntry>();

        /// <summary>
        /// Gets or sets stage timing witnesses associated with the bundle.
        /// </summary>
        [Key(6)]
        public CultWitnessTimingEntry[] TimingWitnesses { get; set; } = Array.Empty<CultWitnessTimingEntry>();

        /// <summary>
        /// Gets or sets provenance metadata for the bundle producer.
        /// </summary>
        [Key(7)]
        public CultWitnessProvenance Provenance { get; set; } = new CultWitnessProvenance();
    }

    /// <summary>
    /// Pins the document or payload instance the witness is about.
    /// </summary>
    [MessagePackObject]
    public sealed class CultWitnessSubjectPin
    {
        /// <summary>
        /// Gets or sets the subject document type or domain identifier.
        /// </summary>
        [Key(0)]
        public string DocumentType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the persisted record key or equivalent stable subject id.
        /// </summary>
        [Key(1)]
        public string SubjectId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pinned subject schema version.
        /// </summary>
        [Key(2)]
        public string? SchemaVersion { get; set; }

        /// <summary>
        /// Gets or sets the pinned subject schema identifier when one exists.
        /// </summary>
        [Key(3)]
        public string? SchemaId { get; set; }

        /// <summary>
        /// Gets or sets the pinned subject content hash when one exists.
        /// </summary>
        [Key(4)]
        public string? ContentHash { get; set; }
    }

    /// <summary>
    /// Pins one schema or contract dependency for the witness bundle.
    /// </summary>
    [MessagePackObject]
    public sealed class CultWitnessContractPin
    {
        /// <summary>
        /// Gets or sets the contract role inside the bundle.
        /// </summary>
        [Key(0)]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the stable schema identifier.
        /// </summary>
        [Key(1)]
        public string SchemaId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pinned schema version.
        /// </summary>
        [Key(2)]
        public string? SchemaVersion { get; set; }

        /// <summary>
        /// Gets or sets the canonical schema content hash when one exists.
        /// </summary>
        [Key(3)]
        public string? ContentHash { get; set; }
    }

    /// <summary>
    /// Points at one inspectable artifact emitted by the witnessed pipeline.
    /// </summary>
    [MessagePackObject]
    public sealed class CultWitnessArtifactEntry
    {
        /// <summary>
        /// Gets or sets the artifact role inside the witness bundle.
        /// </summary>
        [Key(0)]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the portable URI for the artifact.
        /// </summary>
        [Key(1)]
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the artifact media type.
        /// </summary>
        [Key(2)]
        public string MediaType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the artifact content hash when one exists.
        /// </summary>
        [Key(3)]
        public string? ContentHash { get; set; }

        /// <summary>
        /// Gets or sets the artifact size in bytes when known.
        /// </summary>
        [Key(4)]
        public long? ByteLength { get; set; }

        /// <summary>
        /// Gets or sets when the artifact was produced.
        /// </summary>
        [Key(5)]
        public string? ProducedAt { get; set; }
    }

    /// <summary>
    /// Records timestamp and latency evidence for one stage in the witnessed pipeline.
    /// </summary>
    [MessagePackObject]
    public sealed class CultWitnessTimingEntry
    {
        /// <summary>
        /// Gets or sets the pipeline stage name.
        /// </summary>
        [Key(0)]
        public string Stage { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the stage started.
        /// </summary>
        [Key(1)]
        public string StartedAt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the stage completed.
        /// </summary>
        [Key(2)]
        public string CompletedAt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the observed stage latency in milliseconds.
        /// </summary>
        [Key(3)]
        public double LatencyMs { get; set; }

        /// <summary>
        /// Gets or sets the URI for the timing witness artifact when one exists.
        /// </summary>
        [Key(4)]
        public string? WitnessArtifactUri { get; set; }
    }

    /// <summary>
    /// Carries provenance metadata for the witness bundle producer.
    /// </summary>
    [MessagePackObject]
    public sealed class CultWitnessProvenance
    {
        /// <summary>
        /// Gets or sets the stable pipeline identifier.
        /// </summary>
        [Key(0)]
        public string PipelineId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pipeline run identifier.
        /// </summary>
        [Key(1)]
        public string RunId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the runtime identifier that produced the bundle.
        /// </summary>
        [Key(2)]
        public string RuntimeId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the producing agent identifier when one exists.
        /// </summary>
        [Key(3)]
        public string? AgentId { get; set; }

        /// <summary>
        /// Gets or sets the producing agent role when one exists.
        /// </summary>
        [Key(4)]
        public string? AgentRole { get; set; }

        /// <summary>
        /// Gets or sets the pipeline or tool version when one exists.
        /// </summary>
        [Key(5)]
        public string? ToolVersion { get; set; }
    }
}
