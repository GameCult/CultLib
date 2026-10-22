using System;
using System.IO;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

#nullable enable

namespace GameCult.Mesh.Tests;

// A CultMesh single-file write replaces the whole file, so it must read what it replaces first. A store stamped with a
// newer format version, one it cannot read, or one holding a record the write would drop is refused and left byte-identical.
public sealed class CultMeshSingleFileWriteGuardTests
{
    private static readonly CultMeshSingleFileDocumentSchema Schema = new("sha256:guard", "guard.item", "1");
    private string _root = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cultmesh-write-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "store.cc");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void Write(string key, byte payload) =>
        CultMesh.WriteSingleFileDocumentPayload(_path, new CultRecordKey(key), Schema, null, new[] { payload });

    [TestCase("cultcache.store.v3")]
    [TestCase("cultcache.store.v9")]
    public void WriteRefusesAStoreStampedWithANewerFormat(string newer)
    {
        Write("item:a", 1);
        var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_path));
        snapshot.FormatVersion = newer;
        File.WriteAllBytes(_path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        var before = File.ReadAllBytes(_path);

        var error = Assert.Throws<NotSupportedException>(() => Write("item:a", 2))!;

        error.Message.Should().Contain(newer);
        File.ReadAllBytes(_path).Should().Equal(before);
    }

    [Test]
    public void WriteRefusesAFileItCannotRead()
    {
        var garbage = new byte[] { 0xc1, 0xc1, 0xc1, 0xc1 };
        File.WriteAllBytes(_path, garbage);

        Assert.Catch(() => Write("item:a", 1));

        File.ReadAllBytes(_path).Should().Equal(garbage);
    }

    [Test]
    public void WriteRefusesToDropAnotherRecord()
    {
        Write("item:a", 1);
        var before = File.ReadAllBytes(_path);

        var error = Assert.Throws<InvalidOperationException>(() => Write("item:b", 2))!;

        error.Message.Should().Contain("item:a");
        File.ReadAllBytes(_path).Should().Equal(before);
    }

    [Test]
    public void WriteReplacesTheSameRecordAndCreatesAMissingFile()
    {
        Write("item:a", 1);
        Write("item:a", 2);

        CultMesh.ReadSingleFileDocumentPayload(_path, new CultRecordKey("item:a"), Schema.SchemaId).Should().Equal(2);
    }

    // The write takes the same <path>.lock a CultCache single-file store takes: while another holder has it, the write waits.
    [Test]
    public void WriteWaitsForTheStoreLockAnotherWriterHolds()
    {
        Write("item:a", 1);
        var write = default(System.Threading.Tasks.Task);
        using (new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            write = System.Threading.Tasks.Task.Run(() => Write("item:a", 2));

            write.Wait(TimeSpan.FromMilliseconds(750)).Should().BeFalse("the lock is held, so the write must wait");
            CultMesh.ReadSingleFileDocumentPayload(_path, new CultRecordKey("item:a"), Schema.SchemaId).Should().Equal(1);
        }

        write.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        CultMesh.ReadSingleFileDocumentPayload(_path, new CultRecordKey("item:a"), Schema.SchemaId).Should().Equal(2);
    }

    // A variant at the very key being written is still not this write's to replace: a plain record would flatten it.
    [Test]
    public void WriteRefusesToReplaceAVariantAtTheSameKey()
    {
        Write("item:a", 1);
        var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_path));
        snapshot.FormatVersion = CultPersistedStoreSnapshot.FormatV2;
        snapshot.Records[0].Variant = new CultVariantDelta("item:base", Array.Empty<CultVariantOverride>());
        File.WriteAllBytes(_path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        var before = File.ReadAllBytes(_path);

        Assert.Throws<InvalidOperationException>(() => Write("item:a", 2));

        File.ReadAllBytes(_path).Should().Equal(before);
    }
}
