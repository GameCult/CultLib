using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

#nullable enable

namespace GameCult.Mesh.Tests;

// C2a: a single-file document written or read through CultMesh is judged as a cache judges it: ids minted, a list that
// cannot hold ids refused, the header decided by what the file holds.
public sealed class CultMeshSingleFileElementIdTests
{
    private const string HexA = "aaaaaaaaaaaa";
    private static readonly CultRecordKey Key = new("publication");

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cultmesh-single-file-ids", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static CultMeshBodyPublicationDocument Publication(params CultMeshBodyDescriptor[] representations) => new()
    {
        BodyId = "aetheria:entities",
        ProducerId = "aetheria",
        SchemaId = "eve.entity_soa.v1",
        Representations = representations
    };

    private static string HeaderOf(string path) =>
        CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

    private static CultMeshBodyDescriptor Descriptor(string id = "") => new() { Id = id, BodyId = "body" };

    private string Path_(string name) => Path.Combine(_root, name);

    [Test]
    public void TypedWriteMintsTheElementIdsAndMarksTheFileThatHoldsThem()
    {
        var path = Path_("ids.cc");
        var publication = Publication(Descriptor(), Descriptor());

        CultMesh.WriteSingleFileDocument(path, Key, publication);

        publication.Representations.Select(entry => entry.Id).Should().OnlyContain(id => id.Length == 12).And.OnlyHaveUniqueItems();
        HeaderOf(path).Should().Be("cultcache.store.v3");
        CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key).Representations.Select(entry => entry.Id)
            .Should().Equal(publication.Representations.Select(entry => entry.Id));
    }

    [Test]
    public void TypedWriteOfADocumentHoldingNoIdStaysV1AndAMarkedFileStaysMarked()
    {
        var path = Path_("none.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication());
        HeaderOf(path).Should().Be("cultcache.store.v1");

        CultMesh.WriteSingleFileDocument(path, Key, Publication(Descriptor()));
        HeaderOf(path).Should().Be("cultcache.store.v3");
        CultMesh.WriteSingleFileDocument(path, Key, Publication());
        HeaderOf(path).Should().Be("cultcache.store.v3", "a file already marked on disk stays marked");
    }

    [Test]
    public void TypedWriteRefusesADuplicateIdAndAnElementObjectTwiceAndLeavesTheFileAlone()
    {
        var path = Path_("refused.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication(Descriptor()));
        var before = File.ReadAllBytes(path);

        var duplicate = Publication(Descriptor(HexA), Descriptor(HexA));
        var refusal = Assert.Throws<CultElementIdException>(() => CultMesh.WriteSingleFileDocument(path, Key, duplicate))!;
        refusal.ElementId.Should().Be(HexA);

        var once = Descriptor();
        var twice = Publication(once, once);
        Assert.Throws<CultElementIdException>(() => CultMesh.WriteSingleFileDocument(path, Key, twice))!.Message.Should().Contain("twice");
        once.Id.Should().BeEmpty("a refusal mints nothing");

        File.ReadAllBytes(path).Should().Equal(before);
    }

    [Test]
    public void TypedReadRefusesWhatALoadRefusesAndMintsWhatALoadMints()
    {
        var path = Path_("disk.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication(Descriptor()));
        var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        var duplicate = Publication(Descriptor(HexA), Descriptor(HexA));
        snapshot.Records[0].Payload = MessagePackSerializer.Serialize(duplicate);
        File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        var refusal = Assert.Throws<CultElementIdException>(() => CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key))!;
        refusal.ElementId.Should().Be(HexA);

        // A payload written before ids: two reads, and a cache load, agree on the ids minted.
        snapshot.Records[0].Payload = MessagePackSerializer.Serialize(Publication(Descriptor(), Descriptor()));
        File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        var first = CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key).Representations.Select(entry => entry.Id).ToArray();
        first.Should().OnlyContain(id => id.Length == 12);
        CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key).Representations.Select(entry => entry.Id).Should().Equal(first);
        using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions
        {
            Registry = CultDocumentRegistry.ForTypes(new[] { typeof(CultMeshBodyPublicationDocument) }),
            ReadOnly = true
        });
        cache.Get<CultMeshBodyPublicationDocument>(Key)!.Representations.Select(entry => entry.Id).Should().Equal(first);
    }
}
