using System;
using System.Collections.Generic;
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
    // A raw write lands beside the typed record, never over it: neither declares the other's schema id.
    private static readonly CultRecordKey RawKey = new("raw");

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

    // A typed write replaces the whole file with a document it can see, so the ids that document holds decide the header.
    [Test]
    public void TypedWriteDecidesTheHeaderByTheDocumentItWritesEvenOverAMarkedFile()
    {
        var path = Path_("none.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication());
        HeaderOf(path).Should().Be("cultcache.store.v1");

        CultMesh.WriteSingleFileDocument(path, Key, Publication(Descriptor()));
        HeaderOf(path).Should().Be("cultcache.store.v3");
        CultMesh.WriteSingleFileDocument(path, Key, Publication());
        HeaderOf(path).Should().Be("cultcache.store.v1", "the whole file is replaced by a document that holds no id");
    }

    // A raw payload is opaque: the writer cannot see what it holds, so a marked file stays marked.
    [Test]
    public void RawPayloadWriteOverAMarkedFileKeepsItMarkedAndOverAnUnmarkedOneStaysV1()
    {
        var path = Path_("raw.cc");
        var schema = new CultMeshSingleFileDocumentSchema("raw:schema", "RawSchema", "1");
        var payload = new byte[] { 0x90 };

        CultMesh.WriteSingleFileDocumentPayload(path, RawKey, schema, null, payload);
        HeaderOf(path).Should().Be("cultcache.store.v1");

        CultMesh.WriteSingleFileDocument(path, Key, Publication(Descriptor()));
        HeaderOf(path).Should().Be("cultcache.store.v3");
        CultMesh.WriteSingleFileDocumentPayload(path, RawKey, schema, null, payload);
        HeaderOf(path).Should().Be("cultcache.store.v3", "the payload's ids are not the writer's to see");
    }

    // A raw write at the typed record's own key, by an entry that lists the typed record's id compatible and so may replace it:
    // the file then holds only the opaque payload, whose ids the writer cannot see, so the marked file stays marked. (Which files
    // a raw write may replace is CultMeshSingleFileReadabilityTests, over the shared vectors.)
    [Test]
    public void RawPayloadReplacingTheOnlyMarkedRecordKeepsTheFileMarked()
    {
        var path = Path_("replaced.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication(Descriptor()));
        HeaderOf(path).Should().Be("cultcache.store.v3");
        var typed = CultDocumentRegistry.Shared.GetRequired<CultMeshBodyPublicationDocument>().SchemaId;
        var schema = new CultMeshSingleFileDocumentSchema("raw:schema", "RawSchema", "1") { CompatibleSchemaIds = new[] { typed } };

        CultMesh.WriteSingleFileDocumentPayload(path, Key, schema, null, new byte[] { 0x90 });

        CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Should().ContainSingle()
            .Which.SchemaId.Should().Be("raw:schema");
        HeaderOf(path).Should().Be("cultcache.store.v3", "the payload's ids are not the writer's to see");
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
    public void AFailedTypedWriteGivesTheMintedIdsBack()
    {
        var blocked = Path_("blocked.cc");
        Directory.CreateDirectory(blocked);
        var element = Descriptor();

        Assert.That(() => CultMesh.WriteSingleFileDocument(blocked, Key, Publication(element)), Throws.Exception);

        element.Id.Should().BeEmpty("a write that does not land gives its ids back");
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

    // cultcache-py's record (tests/vectors/numeric-leaves/py-store/leaf-record.cc): v1 header, 12-hex element ids on its list. This
    // assembly reads it through the same schema id with the two slots it needs; the member layout is narrower than the writer's.
    [CultDocument("cultcache.leaf-record", "cultcache.leaf_record.v1")]
    [MessagePackObject]
    public sealed class MeshLeaf
    {
        [Key(0)] [CultName] public string Id { get; set; } = "";
        [Key(12)] public List<MeshLeafItem> Items { get; set; } = new();
    }

    [MessagePackObject]
    public sealed class MeshLeafItem
    {
        [Key(0)] public string N { get; set; } = "";
        [Key(1)] public float W { get; set; }
        [Key(2)] [CultElementId] public string Id { get; set; } = "";
    }

    private static readonly string[] PythonIds = { "000000000021", "000000000022" };

    private string PythonStore()
    {
        string? vectors = null;
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null && vectors == null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "vectors", "numeric-leaves", "py-store", "leaf-record.cc");
            if (File.Exists(candidate)) vectors = candidate;
        }

        vectors.Should().NotBeNull("the shared vectors sit above the test directory");
        var path = Path_("leaf-record.cc");
        File.Copy(vectors!, path);
        HeaderOf(path).Should().Be("cultcache.store.v1", "cultcache-py leaves the header unmarked");
        return path;
    }

    private static CultDocumentRegistry Registry(params Type[] types) => CultDocumentRegistry.ForTypes(types);

    // A CultMesh write is a writer with a type: it decides for the copied record from the bytes the file holds, whatever any
    // earlier read saw (a fresh store per call has read nothing).
    [Test]
    public void ACultMeshWriteThatCopiesAPythonRecordHoldingIdsMarksTheStore()
    {
        var path = PythonStore();

        CultMesh.WriteSingleFileDocument(path, Key, Publication(), registry: Registry(typeof(MeshLeaf), typeof(CultMeshBodyPublicationDocument)));

        HeaderOf(path).Should().Be("cultcache.store.v3");
        CultMesh.ReadSingleFileDocument<MeshLeaf>(path, new CultRecordKey("py")).Items.Select(item => item.Id).Should().Equal(PythonIds);
    }

    // Soul s1 P8, committed: the writer's registry has only the type it writes, so the python record is foreign to it and it
    // decides nothing for it (variants:question:non-csharp-writers-mark-ids-2).
    [Test]
    public void ACultMeshWriteWhoseRegistryLacksTheRecordsTypeDecidesNothingForIt()
    {
        var path = PythonStore();

        CultMesh.WriteSingleFileDocument(path, Key, Publication(), registry: Registry(typeof(CultMeshBodyPublicationDocument)));

        HeaderOf(path).Should().Be("cultcache.store.v1");
    }

    // A raw payload is opaque by design: it attaches no registry and decides nothing, even beside a record it could have read.
    [Test]
    public void ARawCultMeshWriteBesideAPythonRecordHoldingIdsLeavesTheHeaderAlone()
    {
        var path = PythonStore();

        CultMesh.WriteSingleFileDocumentPayload(path, RawKey, new CultMeshSingleFileDocumentSchema("raw:schema", "RawSchema", "1"), null, new byte[] { 0x90 });

        HeaderOf(path).Should().Be("cultcache.store.v1");
    }

    // The check only marks: a typed write over a store already marked does not look at, and cannot drop, the mark.
    [Test]
    public void ATypedWriteBesideARecordThatHoldsNoIdKeepsTheStoreUnmarked()
    {
        var path = Path_("plain.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication(), registry: Registry(typeof(CultMeshBodyPublicationDocument)));
        CultMesh.WriteSingleFileDocument(path, new CultRecordKey("second"), Publication(), registry: Registry(typeof(CultMeshBodyPublicationDocument)));

        HeaderOf(path).Should().Be("cultcache.store.v1");
    }
}
