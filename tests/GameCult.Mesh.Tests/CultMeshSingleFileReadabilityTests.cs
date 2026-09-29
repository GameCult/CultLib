using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

#nullable enable

namespace GameCult.Mesh.Tests;

// A single-file write replaces a file exactly when the runtime's own reader opens it, as a cache's flush does. The bytes and
// the verdict are shared with every runtime: tests/vectors/document-variants-c2a/readability.
public sealed class CultMeshSingleFileReadabilityTests
{
    private const int CSharp = 1;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cultmesh-single-file-readability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string VectorRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c2a", "readability");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"Shared readability vectors not found above {TestContext.CurrentContext.TestDirectory}.");
    }

    public static IEnumerable<TestCaseData> Vectors() =>
        File.ReadAllLines(Path.Combine(VectorRoot(), "manifest.txt"))
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(cells => new TestCaseData(cells[0], cells[CSharp] == "reads").SetName($"{cells[0]} {cells[CSharp]}"));

    private static CultMeshBodyPublicationDocument Publication() => new()
    {
        BodyId = "aetheria:entities",
        ProducerId = "aetheria",
        SchemaId = "eve.entity_soa.v1"
    };

    [TestCaseSource(nameof(Vectors))]
    public void ATypedWriteReplacesAFileExactlyWhenItReads(string vector, bool reads)
    {
        var path = Path.Combine(_root, "doc.cc");
        var bytes = File.ReadAllBytes(Path.Combine(VectorRoot(), vector));
        File.WriteAllBytes(path, bytes);

        if (reads)
        {
            CultMesh.WriteSingleFileDocument(path, new CultRecordKey("publication"), Publication());
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Should().ContainSingle();
            return;
        }

        Assert.That(() => CultMesh.WriteSingleFileDocument(path, new CultRecordKey("publication"), Publication()), Throws.Exception);
        File.ReadAllBytes(path).Should().Equal(bytes, "a file this runtime cannot read was rewritten");
    }
}
