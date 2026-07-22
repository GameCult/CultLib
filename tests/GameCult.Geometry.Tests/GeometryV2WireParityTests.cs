using System;
using CultMath;
using FluentAssertions;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Geometry.Tests;

public sealed class GeometryV2WireParityTests
{
    private const string DomainHex = "95AE666978747572652D646F6D61696EAC666978747572652F726F6F74B467616D6563756C742D67656F6D657472792D727397A4726F6F74A4526F6F7493CA3F800000CAC0200000CA0000000094CA00000000CA00000000CA00000000CA3F8000002A9090B4323032362D30372D32325430303A30303A30305A";
    private const string RequestHex = "9FAF726571756573742D66697874757265D95067656F6D657472793A646F6D61696E3A35303563393465373339333538306134653562303438623136396161346461393535646136303666666432386330353462373636613462393465353035303265A7776F726B65727393CA3F800000CA40000000CA4040000093CAC0800000CAC0A00000CAC0C0000093CA40800000CA40A00000CA40C00000CA44870000CA3F800000CA3E800000643291A4526F6F749090B4323032362D30372D32325430303A30303A30315A";
    private const string CutHex = "96AB6375742D66697874757265B867656F6D657472793A726571756573743A6669787475726591AC666978747572652F726F6F74909090";
    private const string ChunkHex = "9EAD666978747572652F6368756E6BB467656F6D657472793A6375743A66697874757265AB6375742D6669787475726593CABF800000CAC0000000CAC040000093CA3F800000CA40000000CA4040000091AC666978747572652F726F6F7491B2666978747572652F726F6F742F636C61696D959393CA00000000CA00000000CA0000000093CA3F800000CA00000000CA0000000093CA00000000CA3F800000CA000000009393CA00000000CA00000000CA3F80000093CA00000000CA00000000CA3F80000093CA00000000CA00000000CA3F8000009392CA00000000CA0000000092CA3F800000CA0000000092CA00000000CA3F800000930001029107C0010000CF0123456789ABCDEFC3";

    [Test]
    public void Domain_MatchesRustV2BytesAndKey()
    {
        var document = Domain();
        AssertWire(document, DomainHex);
        CultGeometryDomainDocument.CreateRecordKey(document).Value.Should().Be(
            "geometry:domain:505c94e7393580a4e5b048b169aa4da955da606ffd28c054b766a4b94e50502e");
    }

    [Test]
    public void BuildRequest_MatchesRustV2BytesAndKey()
    {
        var document = Request();
        AssertWire(document, RequestHex);
        CultGeometryBuildRequest.CreateRecordKey(document).Value.Should().Be(
            "geometry:request:36e25b80c04a6c54c8b3137abb4a8c961468a9b1c0e7dd4d5632cf5a6f847966");
    }

    [Test]
    public void SelectedCut_MatchesRustV2BytesAndKey()
    {
        var document = Cut();
        AssertWire(document, CutHex);
        CultGeometrySelectedCutManifest.CreateRecordKey(document).Value.Should().Be(
            "geometry:cut:5614eae3fe2a17dca651d1e32f401c50e1c3b9cf941f5789655990325da64fb3");
    }

    [Test]
    public void Chunk_MatchesRustV2BytesAndKey()
    {
        var document = Chunk();
        AssertWire(document, ChunkHex);
        CultGeometryChunkArtifact.CreateRecordKey(document).Value.Should().Be(
            "geometry:chunk:ca2874d3638800179c906087ec817ea715c43b70b99ead04edccce2a1b3d6ebb");
    }

    [Test]
    public void BuildRequest_ScalarIdentityUsesExactIeeeBits()
    {
        var document = Request();
        document.ViewportHeightPixels = -0f;
        document.VerticalFovRadians = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc01234));
        document.TargetError = float.NegativeInfinity;

        CultGeometryBuildRequest.CreateRecordKey(document).Value.Should().Be(
            "geometry:request:978dd0913fc3e12c04766e065320d485d172d60d61be920a9a32637d1c439c81");

        var payload = CultDocumentMessagePackSerialization.Serialize(document);
        var decoded = CultDocumentMessagePackSerialization.Deserialize<CultGeometryBuildRequest>(payload);
        BitConverter.SingleToInt32Bits(decoded.ViewportHeightPixels).Should().Be(unchecked((int)0x80000000));
        BitConverter.SingleToInt32Bits(decoded.VerticalFovRadians).Should().Be(unchecked((int)0x7fc01234));
        BitConverter.SingleToInt32Bits(decoded.TargetError).Should().Be(unchecked((int)0xff800000));
    }

    private static void AssertWire<T>(T document, string expectedHex)
    {
        var payload = CultDocumentMessagePackSerialization.Serialize(document);
        Convert.ToHexString(payload).Should().Be(expectedHex);
        var decoded = CultDocumentMessagePackSerialization.Deserialize<T>(payload);
        Convert.ToHexString(CultDocumentMessagePackSerialization.Serialize(decoded)).Should().Be(expectedHex);

        var rustPayload = Convert.FromHexString(expectedHex);
        var decodedFromRust = CultDocumentMessagePackSerialization.Deserialize<T>(rustPayload);
        Convert.ToHexString(CultDocumentMessagePackSerialization.Serialize(decodedFromRust)).Should().Be(expectedHex);
    }

    private static CultGeometryDomainDocument Domain() => new()
    {
        DomainId = "fixture-domain",
        RootKey = "fixture/root",
        SourceRuntime = "gamecult-geometry-rs",
        Root = new CultGeometryDomainNode
        {
            Name = "root",
            Kind = "Root",
            Translation = new float3(1f, -2.5f, 0f),
            Rotation = quaternion.identity,
            Seed = 42,
            Claims = [],
            Children = [],
        },
        CreatedAt = "2026-07-22T00:00:00Z",
    };

    private static CultGeometryBuildRequest Request() => new()
    {
        RequestId = "request-fixture",
        DomainKey = CultGeometryDomainDocument.CreateRecordKey(Domain()).Value,
        WorkerGroup = "workers",
        CameraPosition = new float3(1f, 2f, 3f),
        FrustumMin = new float3(-4f, -5f, -6f),
        FrustumMax = new float3(4f, 5f, 6f),
        ViewportHeightPixels = 1080f,
        VerticalFovRadians = 1f,
        TargetError = 0.25f,
        TriangleBudget = 100,
        ColliderBudget = 50,
        SemanticFilter = ["Root"],
        RequestedChunkKeys = [],
        DirtyDomainKeys = [],
        CreatedAt = "2026-07-22T00:00:01Z",
    };

    private static CultGeometrySelectedCutManifest Cut() => new()
    {
        CutId = "cut-fixture",
        RequestKey = "geometry:request:fixture",
        SelectedNodes = ["fixture/root"],
        DeferredChildRequests = [],
        ParentFallbackNodes = [],
        Diagnostics = [],
    };

    private static CultGeometryChunkArtifact Chunk() => new()
    {
        ChunkId = "fixture/chunk",
        CutKey = "geometry:cut:fixture",
        SelectedCutId = "cut-fixture",
        BoundsMin = new float3(-1f, -2f, -3f),
        BoundsMax = new float3(1f, 2f, 3f),
        SourceDomainKeys = ["fixture/root"],
        SourceClaimKeys = ["fixture/root/claim"],
        RenderMesh = new CultGeometryTriangleMesh
        {
            Positions = [new float3(0f, 0f, 0f), new float3(1f, 0f, 0f), new float3(0f, 1f, 0f)],
            Normals = [new float3(0f, 0f, 1f), new float3(0f, 0f, 1f), new float3(0f, 0f, 1f)],
            Uvs = [new float2(0f, 0f), new float2(1f, 0f), new float2(0f, 1f)],
            Indices = [0, 1, 2],
            TriangleMaterials = [7],
        },
        ColliderMesh = null,
        InputBrushes = 1,
        CandidatePairs = 0,
        RejectedPairs = 0,
        StableClipSeed = 0x0123_4567_89ab_cdef,
        SupportsParentChildCoexistence = true,
    };
}
