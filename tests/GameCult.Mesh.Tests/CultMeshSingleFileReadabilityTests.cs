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
    public void AWriteReplacesAFileExactlyWhenItReads(string vector, bool reads)
    {
        var raw = new CultMeshSingleFileDocumentSchema("raw:schema", "RawSchema", "1");
        var writes = new (string Name, Action<string> Write)[]
        {
            ("typed", path => CultMesh.WriteSingleFileDocument(path, new CultRecordKey("publication"), Publication())),
            ("raw payload", path => CultMesh.WriteSingleFileDocumentPayload(path, new CultRecordKey("publication"), raw, null, new byte[] { 0x90 }))
        };
        foreach (var (name, write) in writes)
        {
            var path = Path.Combine(_root, name.Replace(' ', '-') + ".cc");
            var bytes = File.ReadAllBytes(Path.Combine(VectorRoot(), vector));
            File.WriteAllBytes(path, bytes);

            if (reads)
            {
                write(path);
                CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Should().ContainSingle(name);
                continue;
            }

            Assert.That(() => write(path), Throws.TypeOf<CultStoreUnreadableException>(), name);
            File.ReadAllBytes(path).Should().Equal(bytes, name + " rewrote a file this runtime cannot read");
        }
    }

    [Test]
    public void EveryVectorInTheFolderHasAManifestRow()
    {
        var rows = Vectors().Select(row => (string)row.Arguments[0]).Where(name => !name.StartsWith("..", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal);
        var files = Directory.GetFiles(VectorRoot(), "*.bin").Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal);
        rows.Should().Equal(files);
    }
}
