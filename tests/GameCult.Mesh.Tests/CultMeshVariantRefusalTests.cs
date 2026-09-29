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

    private static NotSupportedException Refusal(string vector) =>
        Assert.Throws<NotSupportedException>(() =>
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

    [Test]
    public void ReadSingleFileDocument_RefusesVariantStoreByVersionOrRecord()
    {
        var message = Refusal("variant-v2.msgpack").Message;
        (message.Contains("cultcache.store.v2") || message.Contains("item:anvil-big")).Should().BeTrue(message);
    }
}
