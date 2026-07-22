using System;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Geometry
{
    public static class CultGeometryWorkerSchemaVersions
    {
        public const string State = "gamecult.geometry.worker_state.v1";
    }

    [MessagePackObject]
    [CultDocument("gamecult.geometry.worker_state", CultGeometryWorkerSchemaVersions.State)]
    public sealed class CultGeometryWorkerState
    {
        [Key(0), CultName]
        public string WorkerId { get; set; } = string.Empty;

        [Key(1)]
        public string Phase { get; set; } = string.Empty;

        [Key(2), CultReference(typeof(CultGeometryBuildRequest))]
        public string ActiveRequestKey { get; set; } = string.Empty;

        [Key(3), CultReference(typeof(CultGeometrySelectedCutManifest))]
        public string LastSelectedCutKey { get; set; } = string.Empty;

        [Key(4)]
        public string[] LastArtifactKeys { get; set; } = Array.Empty<string>();

        [Key(5)]
        public string LastError { get; set; } = string.Empty;

        [Key(6)]
        public string UpdatedAt { get; set; } = string.Empty;

        [Key(7)]
        public string ServedPackageVersion { get; set; } = string.Empty;

        public static CultRecordKey CreateRecordKey(string workerId)
        {
            if (string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Value must be non-empty.", nameof(workerId));
            return new CultRecordKey("geometry:worker:" + workerId);
        }
    }

    [MessagePackObject]
    public sealed class CultGeometryBuildCommand
    {
        [Key(0)]
        public string RequestKey { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public sealed class CultGeometryBuildReceipt
    {
        [Key(0)]
        public string RequestKey { get; set; } = string.Empty;

        [Key(1)]
        public string SelectedCutKey { get; set; } = string.Empty;

        [Key(2)]
        public string[] ArtifactKeys { get; set; } = Array.Empty<string>();

        [Key(3)]
        public string[] ContentHashes { get; set; } = Array.Empty<string>();
    }

    public sealed class CultGeometryBuildOutput
    {
        public CultGeometrySelectedCutManifest SelectedCut { get; init; } = new();
        public CultGeometryChunkArtifact[] Artifacts { get; init; } = Array.Empty<CultGeometryChunkArtifact>();
    }

    public sealed class CultGeometryDevelopmentProbe
    {
        public string Owner { get; init; } = string.Empty;
        public string SchemaVersion { get; init; } = string.Empty;
        public string SourceRecordKey { get; init; } = string.Empty;
        public string SelectedCutKey { get; init; } = string.Empty;
        public string[] SelectedNodes { get; init; } = Array.Empty<string>();
        public string[] ArtifactKeys { get; init; } = Array.Empty<string>();
        public string[] ContentHashes { get; init; } = Array.Empty<string>();
        public string ServedPackageVersion { get; init; } = string.Empty;
    }
}
