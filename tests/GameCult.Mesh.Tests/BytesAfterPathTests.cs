using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

// The mesh's typed reads of a raw record decode the payload through the one-value rule: a tail is refused, never ignored.
public class BytesAfterPathTests
{
    private const string Clause = "bytes after its first MessagePack value";

    private static CultNetRawDocumentRecord WithTail(CultMeshCdnArtifactManifest manifest)
    {
        var payload = CultDocumentMessagePackSerialization.SerializeUntyped(manifest, typeof(CultMeshCdnArtifactManifest)).Concat(new byte[] { 0xc0 }).ToArray();
        return new CultNetRawDocumentRecord
        {
            SchemaId = CultDocumentRegistry.Shared.GetRequired<CultMeshCdnArtifactManifest>().SchemaId,
            RecordKey = "k",
            StoredAt = "1970-01-01T00:00:00.000Z",
            Payload = payload,
        };
    }

    private static CultMeshCdnArtifactManifest Manifest() => new() { ArtifactId = "a", Version = "1", ContentHash = "h" };

    private static string Refusal(Action read)
    {
        var thrown = Assert.Catch(read)!;
        return thrown is TargetInvocationException { InnerException: { } inner } ? inner.ToString() : thrown.ToString();
    }

    [Test]
    public void ASnapshotDecodeThroughABindingRefusesATail()
    {
        var registry = CultMeshCdn.CreateDocumentRegistry();
        var snapshot = new CultNetSnapshotResponseRawMessage { Documents = new[] { WithTail(Manifest()) } };
        Refusal(() => CultMesh.DecodeSnapshotDocuments<CultMeshCdnArtifactManifest>(snapshot, registry)).Should().Contain(Clause);
    }

    [Test]
    public void ASnapshotDecodeWithNoBindingRefusesATail()
    {
        var snapshot = new CultNetSnapshotResponseRawMessage { Documents = new[] { WithTail(Manifest()) } };
        Refusal(() => CultMesh.DecodeSnapshotDocuments<CultMeshCdnArtifactManifest>(snapshot, new CultNetDocumentRegistry())).Should().Contain(Clause);
    }

    [Test]
    public void ATypedReadOfOneSnapshotRecordRefusesATail()
    {
        var record = WithTail(Manifest());
        var snapshot = new CultNetSnapshotResponseRawMessage { MessageId = "m", Documents = new[] { record } };
        var read = typeof(CultMesh).GetMethod("ReadDocumentFromSnapshotResponse", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(CultMeshCdnArtifactManifest));
        Refusal(() => read.Invoke(null, new object[] { snapshot, record.SchemaId, record.RecordKey })).Should().Contain(Clause);
    }
}
