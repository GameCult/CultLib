using System;
using System.IO;
using FluentAssertions;
using GameCult.Caching;
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

    [Test]
    public void ReadSingleFileDocument_RefusesExtraRecordSlotInTheLegacyCatalogLayoutToo()
    {
        var message = Refusal("legacy-catalog-extra-slot.msgpack").Message;
        message.Should().Contain("item:anvil").And.Contain(ItemSchemaId);
    }

    // The older catalog layout is read by the same store reader, so it is held to one array too: the same store with bytes after it
    // is refused, and only the bytes make the difference.
    [Test]
    public void ReadSingleFileDocument_ReadsTheLegacyCatalogLayoutButNotWithBytesAfterIt()
    {
        CultMesh.ReadSingleFileDocumentPayload(VectorPath("legacy-catalog-plain.msgpack"), new CultRecordKey("item:bellows"), ItemSchemaId)
            .Should().NotBeEmpty();

        Refusal("legacy-catalog-trailing.msgpack").Message.Should().Contain("after its MessagePack array");
    }

    // A store in the current layout with one catalog slot malformed is refused as CultCache refuses it. It is not the older layout,
    // so it is not read with the older reader, which would take the malformed canonical schema slot for a slot to skip.
    [TestCase("current-catalog-bad-canonical.msgpack")]
    [TestCase("current-catalog-bad-members.msgpack")]
    [TestCase("current-catalog-bad-compat.msgpack")]
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
