using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameCult.Caching;
using MessagePack;
using static GameCult.Geometry.CultGeometryDocuments;

namespace GameCult.Geometry
{
    /// <summary>
    /// Schema versions for CultCache-native geometry pipeline documents.
    /// </summary>
    public static class CultGeometrySchemaVersions
    {
        public const string Domain = "gamecult.geometry.domain.v1";
        public const string BuildRequest = "gamecult.geometry.build_request.v1";
        public const string SelectedCut = "gamecult.geometry.selected_cut.v1";
        public const string ChunkArtifact = "gamecult.geometry.chunk_artifact.v1";
    }

    /// <summary>
    /// CultCache document containing a hierarchical geometry domain tree.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.geometry.domain", CultGeometrySchemaVersions.Domain)]
    public sealed class CultGeometryDomainDocument
    {
        [Key(0)]
        [CultName]
        public string DomainId { get; set; } = string.Empty;

        [Key(1)]
        [CultIndex]
        public string RootKey { get; set; } = string.Empty;

        [Key(2)]
        public string SourceRuntime { get; set; } = string.Empty;

        [Key(3)]
        public CultGeometryDomainNode Root { get; set; } = new CultGeometryDomainNode();

        [Key(4)]
        public string CreatedAt { get; set; } = string.Empty;

        public static CultRecordKey CreateRecordKey(CultGeometryDomainDocument domain)
        {
            if (domain == null) throw new ArgumentNullException(nameof(domain));
            return new CultRecordKey("geometry:domain:" + StableHash(
                domain.RootKey,
                domain.SourceRuntime,
                domain.Root.StableFingerprint()));
        }
    }

    /// <summary>
    /// One node in a geometry domain tree.
    /// </summary>
    [MessagePackObject]
    public sealed class CultGeometryDomainNode
    {
        [Key(0)]
        public string Name { get; set; } = string.Empty;

        [Key(1)]
        public string Kind { get; set; } = string.Empty;

        [Key(2)]
        public float[] Translation { get; set; } = Array.Empty<float>();

        [Key(3)]
        public float[] RotationXyzw { get; set; } = Array.Empty<float>();

        [Key(4)]
        public ulong Seed { get; set; }

        [Key(5)]
        public CultGeometryFeatureClaim[] Claims { get; set; } = Array.Empty<CultGeometryFeatureClaim>();

        [Key(6)]
        public CultGeometryDomainNode[] Children { get; set; } = Array.Empty<CultGeometryDomainNode>();

        public string StableFingerprint()
        {
            var parts = new[]
            {
                Name,
                Kind,
                StableVector(Translation),
                StableVector(RotationXyzw),
                Seed.ToString(CultureInfo.InvariantCulture)
            }
                .Concat(Claims.Select(claim => claim.StableFingerprint()))
                .Concat(Children.Select(child => child.StableFingerprint()))
                .ToArray();

            return StableArray(parts);
        }
    }

    /// <summary>
    /// A domain feature claim before CSG lowering.
    /// </summary>
    [MessagePackObject]
    public sealed class CultGeometryFeatureClaim
    {
        [Key(0)]
        public string Name { get; set; } = string.Empty;

        [Key(1)]
        public float[] Translation { get; set; } = Array.Empty<float>();

        [Key(2)]
        public float[] RotationXyzw { get; set; } = Array.Empty<float>();

        [Key(3)]
        public float[] SupportCenter { get; set; } = Array.Empty<float>();

        [Key(4)]
        public float[] SupportSize { get; set; } = Array.Empty<float>();

        [Key(5)]
        public string Kind { get; set; } = string.Empty;

        [Key(6)]
        public uint Material { get; set; }

        [Key(7)]
        public string LoweringPolicy { get; set; } = string.Empty;

        public string StableFingerprint() => StableArray([
            Name,
            StableVector(Translation),
            StableVector(RotationXyzw),
            StableVector(SupportCenter),
            StableVector(SupportSize),
            Kind,
            Material.ToString(CultureInfo.InvariantCulture),
            LoweringPolicy
        ]);
    }

    /// <summary>
    /// CultCache document requesting one geometry LOD build.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.geometry.build_request", CultGeometrySchemaVersions.BuildRequest)]
    public sealed class CultGeometryBuildRequest
    {
        [Key(0)]
        [CultName]
        public string RequestId { get; set; } = string.Empty;

        [Key(1)]
        [CultReference(typeof(CultGeometryDomainDocument))]
        public string DomainKey { get; set; } = string.Empty;

        [Key(2)]
        [CultIndex]
        public string WorkerGroup { get; set; } = string.Empty;

        [Key(3)]
        public float[] CameraPosition { get; set; } = Array.Empty<float>();

        [Key(4)]
        public float[] FrustumMin { get; set; } = Array.Empty<float>();

        [Key(5)]
        public float[] FrustumMax { get; set; } = Array.Empty<float>();

        [Key(6)]
        public float ViewportHeightPixels { get; set; }

        [Key(7)]
        public float VerticalFovRadians { get; set; }

        [Key(8)]
        public float TargetError { get; set; }

        [Key(9)]
        public int TriangleBudget { get; set; }

        [Key(10)]
        public int ColliderBudget { get; set; }

        [Key(11)]
        public string[] SemanticFilter { get; set; } = Array.Empty<string>();

        [Key(12)]
        public string[] RequestedChunkKeys { get; set; } = Array.Empty<string>();

        [Key(13)]
        public string[] DirtyDomainKeys { get; set; } = Array.Empty<string>();

        [Key(14)]
        public string CreatedAt { get; set; } = string.Empty;

        public static CultRecordKey CreateRecordKey(CultGeometryBuildRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new CultRecordKey("geometry:request:" + StableHash(
                request.DomainKey,
                request.WorkerGroup,
                StableVector(request.CameraPosition),
                StableVector(request.FrustumMin),
                StableVector(request.FrustumMax),
                request.ViewportHeightPixels.ToString("R", CultureInfo.InvariantCulture),
                request.VerticalFovRadians.ToString("R", CultureInfo.InvariantCulture),
                request.TargetError.ToString("R", CultureInfo.InvariantCulture),
                request.TriangleBudget.ToString(CultureInfo.InvariantCulture),
                request.ColliderBudget.ToString(CultureInfo.InvariantCulture),
                StableArray(request.SemanticFilter),
                StableArray(request.RequestedChunkKeys),
                StableArray(request.DirtyDomainKeys)));
        }
    }

    /// <summary>
    /// CultCache document describing the selected domain cut produced for one build request.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.geometry.selected_cut", CultGeometrySchemaVersions.SelectedCut)]
    public sealed class CultGeometrySelectedCutManifest
    {
        [Key(0)]
        [CultName]
        public string CutId { get; set; } = string.Empty;

        [Key(1)]
        [CultReference(typeof(CultGeometryBuildRequest))]
        public string RequestKey { get; set; } = string.Empty;

        [Key(2)]
        public string[] SelectedNodes { get; set; } = Array.Empty<string>();

        [Key(3)]
        public string[] DeferredChildRequests { get; set; } = Array.Empty<string>();

        [Key(4)]
        public string[] ParentFallbackNodes { get; set; } = Array.Empty<string>();

        [Key(5)]
        public CultGeometryContributionRow[] Diagnostics { get; set; } = Array.Empty<CultGeometryContributionRow>();

        public static CultRecordKey CreateRecordKey(CultGeometrySelectedCutManifest cut)
        {
            if (cut == null) throw new ArgumentNullException(nameof(cut));
            return new CultRecordKey("geometry:cut:" + StableHash(
                cut.RequestKey,
                cut.CutId,
                StableArray(cut.SelectedNodes),
                StableArray(cut.DeferredChildRequests),
                StableArray(cut.ParentFallbackNodes)));
        }
    }

    /// <summary>
    /// One diagnostic row explaining selected-cut pressure.
    /// </summary>
    [MessagePackObject]
    public sealed class CultGeometryContributionRow
    {
        [Key(0)]
        public string DomainKey { get; set; } = string.Empty;

        [Key(1)]
        public string Kind { get; set; } = string.Empty;

        [Key(2)]
        public float Contribution { get; set; }

        [Key(3)]
        public float ProjectedScreenError { get; set; }

        [Key(4)]
        public float SemanticPriority { get; set; }

        [Key(5)]
        public int EstimatedTriangleCost { get; set; }

        [Key(6)]
        public int ChildCost { get; set; }

        [Key(7)]
        public int RemainingTriangleBudget { get; set; }

        [Key(8)]
        public bool Requested { get; set; }

        [Key(9)]
        public bool Dirty { get; set; }

        [Key(10)]
        public bool Selected { get; set; }

        [Key(11)]
        public bool UsedFallback { get; set; }

        [Key(12)]
        public bool DeferredByBudget { get; set; }
    }

    /// <summary>
    /// CultCache document carrying one emitted geometry chunk and its collider payload.
    /// </summary>
    [MessagePackObject]
    [CultDocument("gamecult.geometry.chunk_artifact", CultGeometrySchemaVersions.ChunkArtifact)]
    public sealed class CultGeometryChunkArtifact
    {
        [Key(0)]
        [CultName]
        public string ChunkId { get; set; } = string.Empty;

        [Key(1)]
        [CultReference(typeof(CultGeometrySelectedCutManifest))]
        public string CutKey { get; set; } = string.Empty;

        [Key(2)]
        [CultIndex]
        public string SelectedCutId { get; set; } = string.Empty;

        [Key(3)]
        public float[] BoundsMin { get; set; } = Array.Empty<float>();

        [Key(4)]
        public float[] BoundsMax { get; set; } = Array.Empty<float>();

        [Key(5)]
        public string[] SourceDomainKeys { get; set; } = Array.Empty<string>();

        [Key(6)]
        public string[] SourceClaimKeys { get; set; } = Array.Empty<string>();

        [Key(7)]
        public CultGeometryTriangleMesh RenderMesh { get; set; } = new CultGeometryTriangleMesh();

        [Key(8)]
        public CultGeometryTriangleMesh? ColliderMesh { get; set; }

        [Key(9)]
        public int InputBrushes { get; set; }

        [Key(10)]
        public int CandidatePairs { get; set; }

        [Key(11)]
        public int RejectedPairs { get; set; }

        [Key(12)]
        public ulong StableClipSeed { get; set; }

        [Key(13)]
        public bool SupportsParentChildCoexistence { get; set; }

        public static CultRecordKey CreateRecordKey(CultGeometryChunkArtifact chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            return new CultRecordKey("geometry:chunk:" + StableHash(
                chunk.CutKey,
                chunk.ChunkId,
                chunk.SelectedCutId,
                StableArray(chunk.SourceDomainKeys),
                StableArray(chunk.SourceClaimKeys),
                chunk.RenderMesh.StableFingerprint(),
                chunk.ColliderMesh?.StableFingerprint() ?? string.Empty));
        }
    }

    /// <summary>
    /// Packed triangle mesh payload for CultCache persistence and CultNet replication.
    /// </summary>
    [MessagePackObject]
    public sealed class CultGeometryTriangleMesh
    {
        [Key(0)]
        public float[] Positions { get; set; } = Array.Empty<float>();

        [Key(1)]
        public float[] Normals { get; set; } = Array.Empty<float>();

        [Key(2)]
        public float[] Uvs { get; set; } = Array.Empty<float>();

        [Key(3)]
        public uint[] Indices { get; set; } = Array.Empty<uint>();

        [Key(4)]
        public uint[] TriangleMaterials { get; set; } = Array.Empty<uint>();

        [IgnoreMember]
        public int TriangleCount => Indices.Length / 3;

        public string StableFingerprint() => StableHash(
            StableVector(Positions),
            StableVector(Normals),
            StableVector(Uvs),
            string.Join(",", Indices.Select(value => value.ToString(CultureInfo.InvariantCulture))),
            string.Join(",", TriangleMaterials.Select(value => value.ToString(CultureInfo.InvariantCulture))));
    }

    internal static class CultGeometryDocuments
    {
        public static string StableHash(params string[] parts)
        {
            using var sha = SHA256.Create();
            var canonical = string.Join("\u001f", parts);
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return string.Concat(bytes.Select(value => value.ToString("x2")));
        }

        public static string StableVector(float[] values)
        {
            return string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }

        public static string StableArray(string[] values)
        {
            return string.Join("\u001e", values);
        }
    }
}
