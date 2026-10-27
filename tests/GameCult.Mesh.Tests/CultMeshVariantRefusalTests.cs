using System;
using System.IO;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

#nullable enable

namespace GameCult.Mesh.Tests;

// Shared refusal vectors: tests/vectors/document-variants-c0, read by every runtime's tests.
public sealed class CultMeshVariantRefusalTests
{
    private const string ItemSchemaId = "sha256:88d3fdf0a927acf3b163940d8f8c7fe62b3316542ce771a67ec8bc038f594788";

    private static string VectorPath(string name)
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c0", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Shared vector {name} not found above {TestContext.CurrentContext.TestDirectory}.");
    }

    private static CultStoreUnreadableException Refusal(string vector) =>
        Assert.Throws<CultStoreUnreadableException>(() =>
            CultMesh.ReadSingleFileDocumentPayload(VectorPath(vector), new CultRecordKey("item:bellows"), ItemSchemaId))!;

    [Test]
    public void ReadSingleFileDocument_RefusesUnknownHeaderByName() =>
        Refusal("unknown-header.msgpack").Message.Should().Contain("cultcache.store.v9");

    [Test]
    public void ReadSingleFileDocument_RefusesExtraRecordSlotNamingTheRecord()
    {
        var message = Refusal("extra-slot-full-payload.msgpack").Message;
        message.Should().Contain("item:anvil").And.Contain(ItemSchemaId);
    }

    // The older catalog layout (the content hash at entry slot 5) is a store no C# reader reads: CultMesh refuses it as CultCache
    // does, on a read and on a raw write, and a refused write leaves the bytes as they were.
    [TestCase("legacy-catalog-plain.msgpack")]
    [TestCase("legacy-catalog-extra-slot.msgpack")]
    [TestCase("legacy-catalog-trailing.msgpack")]
    public void TheOlderCatalogLayoutIsRefusedByMeshAndCultCacheAlike(string vector)
    {
        Refusal(vector).Path.Should().Be(VectorPath(vector));

        var directory = Path.Combine(Path.GetTempPath(), "cultmesh-legacy-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, vector);
            File.Copy(VectorPath(vector), path);
            var bytes = File.ReadAllBytes(path);
            var raw = new CultMeshSingleFileDocumentSchema("raw:schema", "RawSchema", "1");

            Assert.Throws<CultStoreUnreadableException>(() =>
                CultMesh.WriteSingleFileDocumentPayload(path, new CultRecordKey("item:bellows"), raw, null, new byte[] { 0x90 }))!
                .Path.Should().Be(path);
            File.ReadAllBytes(path).Should().Equal(bytes);

            Assert.Throws<CultStoreUnreadableException>(() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { FlushOnDispose = false, StoreFlushOnDispose = false }))!
                .Path.Should().Be(path);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // A store in the current layout with one catalog slot malformed is refused as CultCache refuses it.
    [TestCase("current-catalog-bad-canonical.msgpack")]
    [TestCase("current-catalog-bad-members.msgpack")]
    [TestCase("current-catalog-bad-compat.msgpack")]
    [TestCase("current-catalog-bad-slot5-nil.msgpack")]
    [TestCase("current-catalog-bad-slot5-int.msgpack")]
    public void ReadSingleFileDocument_RefusesAMalformedCurrentCatalogEntry(string vector)
    {
        Refusal(vector).Path.Should().Be(VectorPath(vector));
    }

    // Q6: CultMesh does not resolve variants. It refuses a variant key naming it and its base, and still reads the plain
    // records of the same v2 store.
    [Test]
    public void ReadSingleFileDocument_RefusesAVariantKeyByNameButReadsItsPlainNeighbours()
    {
        var refusal = Assert.Throws<NotSupportedException>(() =>
            CultMesh.ReadSingleFileDocumentPayload(VectorPath("variant-v2.msgpack"), new CultRecordKey("item:anvil-big"), ItemSchemaId))!;
        refusal.Message.Should().Contain("item:anvil-big").And.Contain("item:anvil'");

        CultMesh.ReadSingleFileDocumentPayload(VectorPath("variant-v2.msgpack"), new CultRecordKey("item:bellows"), ItemSchemaId)
            .Should().NotBeEmpty();
    }
}
