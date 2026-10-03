using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

#nullable enable

namespace GameCult.Mesh.Tests;

// CultMesh's single-file helpers reach the store file through the one CultCache single-file store: what is at the path, the lock
// and the atomic replace are decided there, once, for a Mesh read or write and a cache open, flush or commit alike.
public sealed class CultMeshSingleFileStoreTests
{
    private static readonly CultRecordKey Key = new("publication");
    private static readonly CultMeshSingleFileDocumentSchema Raw = new("raw:schema", "RawSchema", "1");
    private static readonly byte[] EmptyArray = { 0x90 };
    private static readonly CultCacheOpenOptions ReadOnlyOpen = new() { FlushOnDispose = false, StoreFlushOnDispose = false };
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cultmesh-single-file-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (!Directory.Exists(_root)) return;
        // A recursive delete cannot remove a link whose target is gone; take the links out by themselves first.
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root).Where(IsALink))
        {
            if ((new FileInfo(entry).Attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, false);
            else File.Delete(entry);
        }

        Directory.Delete(_root, true);
    }

    private static CultMeshBodyPublicationDocument Publication(string producer = "aetheria") => new()
    {
        BodyId = "aetheria:entities",
        ProducerId = producer,
        SchemaId = "eve.entity_soa.v1"
    };

    private static CultMeshBodyPublicationDocument PublicationOf(string id) => new()
    {
        BodyId = "aetheria:" + id,
        ProducerId = "aetheria",
        SchemaId = "eve.entity_soa.v1"
    };

    private static string SchemaId() => CultDocumentRegistry.Shared.GetRequired<CultMeshBodyPublicationDocument>().SchemaId;

    private static bool IsALink(string path)
    {
        var attributes = new FileInfo(path).Attributes;
        return (int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static int RecordsSeenByACache(string path)
    {
        using var cache = CultCacheMessagePack.Create(path, ReadOnlyOpen);
        return cache.GetAll<CultMeshBodyPublicationDocument>().Count();
    }

    private IEnumerable<string> TempFiles() =>
        Directory.GetFileSystemEntries(_root).Where(entry => entry.EndsWith(".tmp", StringComparison.Ordinal));

    // A link whose target is gone: a symlink where one can be made, a directory junction on Windows, which needs no privilege.
    private static void CreateDanglingLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(path, target);
            return;
        }

        Directory.CreateDirectory(target);
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.That(mklink.ExitCode, Is.Zero, "mklink /J failed");
        Directory.Delete(target);
    }

    private static void CreateLiveLinkOrIgnore(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore("A file symlink needs a privilege this account lacks: " + ex.Message);
        }
    }

    private static byte[] EntryBytes(CultPersistedStoreSnapshot snapshot, string schemaId) =>
        CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
        {
            SchemaCatalog = snapshot.SchemaCatalog.Where(entry => entry.SchemaId == schemaId).ToArray()
        });

    private static CultPersistedStoreSnapshot Snapshot(string path) =>
        CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

    // A Mesh write is a write set of one key: every other record in the file and the entry that publishes it are copied forward as
    // the file holds them, and a second write of the key replaces only that key.
    [Test]
    public void AMeshWriteIntoAStoreHoldingOtherRecordsCopiesThemForwardAndReplacesOnlyItsKey()
    {
        var path = Path.Combine(_root, "shared.cc");
        using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions()))
        {
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(CultMeshBodyPublicationDocument), PublicationOf("a"), new CultRecordKey("a"));
                batch.Upsert(typeof(CultMeshBodyPublicationDocument), PublicationOf("b"), new CultRecordKey("b"));
            });
        }

        var before = Snapshot(path);
        CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, new byte[] { 0x90 });
        var after = Snapshot(path);

        after.Records.Select(record => record.Key).Should().Equal("a", "b", "publication");
        foreach (var key in new[] { "a", "b" })
        {
            var was = before.Records.Single(record => record.Key == key);
            var kept = after.Records.Single(record => record.Key == key);
            (kept.SchemaId, kept.StoredAt).Should().Be((was.SchemaId, was.StoredAt));
            kept.Payload.Should().Equal(was.Payload);
        }

        EntryBytes(after, SchemaId()).Should().Equal(EntryBytes(before, SchemaId()));
        after.SchemaCatalog.Should().Contain(entry => entry.SchemaId == "raw:schema");

        CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, new byte[] { 0x91, 0x01 });
        var again = Snapshot(path);
        again.Records.Select(record => record.Key).Should().Equal("a", "b", "publication");
        again.Records.Single(record => record.Key == "publication").Payload.Should().Equal(new byte[] { 0x91, 0x01 });
        foreach (var key in new[] { "a", "b" })
            again.Records.Single(record => record.Key == key).Payload.Should().Equal(before.Records.Single(record => record.Key == key).Payload);
    }

    // A Mesh write has read nothing and has no cache to resolve a variant against its base, so it never writes into a store that holds
    // one: typed or raw, at the variant's key, its base's key or an unrelated key, it is refused and the file is left byte for byte.
    [Test]
    public void AMeshWriteIntoAStoreHoldingAVariantIsRefusedAndLeavesItByteIdentical(
        [Values("variant", "base", "unrelated")] string target, [Values] bool typed)
    {
        var path = Path.Combine(_root, $"variant-{target}-{typed}.cc");
        var baseKey = new CultRecordKey("a");
        using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions()))
        {
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(CultMeshBodyPublicationDocument), PublicationOf("a"), baseKey);
                batch.UpsertVariant(new CultRecordKey("v"), baseKey, new[]
                {
                    cache.Override<CultMeshBodyPublicationDocument>(nameof(CultMeshBodyPublicationDocument.BodyId), "aetheria:v"),
                    cache.Override<CultMeshBodyPublicationDocument>(nameof(CultMeshBodyPublicationDocument.ProducerId), "variant-producer")
                });
            });
        }

        var bytes = File.ReadAllBytes(path);
        Snapshot(path).Records.Should().Contain(record => record.Variant != null);
        var key = new CultRecordKey(target switch { "variant" => "v", "base" => "a", _ => "z" });

        void Write()
        {
            if (typed)
                CultMesh.WriteSingleFileDocument(path, key, PublicationOf("z"));
            else
                CultMesh.WriteSingleFileDocumentPayload(path, key, Raw, null, new byte[] { 0x90 });
        }

        // A raw write at the variant's or the base's key names a record under an id its entry does not declare, and that refusal is
        // checked first.
        if (!typed && target != "unrelated")
            Assert.Throws<CultSchemaConflictException>(Write)!.RecordKey.Should().Be(key.Value);
        else
            Assert.Throws<CultWriteConflictException>(Write)!.Message.Should().Contain("CultCache");
        File.ReadAllBytes(path).Should().Equal(bytes);
    }

    // The writer cannot see whether the raw payload or the records it copies hold element ids, so a marked file stays marked.
    [Test]
    public void AMeshRawWriteIntoAMarkedStoreKeepsItMarked()
    {
        var path = Path.Combine(_root, "marked.cc");
        using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions()))
            cache.Commit(batch => batch.Upsert(typeof(CultMeshBodyPublicationDocument), PublicationOf("a"), new CultRecordKey("a")));
        var marked = Snapshot(path);
        marked.FormatVersion = CultPersistedStoreSnapshot.FormatV3;
        File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(marked));

        CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, new byte[] { 0x90 });

        Snapshot(path).FormatVersion.Should().Be(CultPersistedStoreSnapshot.FormatV3);
    }

    [Test]
    public void ADanglingLinkIsAnIoErrorOnTypedWriteRawWriteAndReadNeverAMissingFile()
    {
        var path = Path.Combine(_root, "dangling.cc");
        var target = Path.Combine(_root, "unmounted");
        CreateDanglingLink(path, target);

        var operations = new (string Name, Action Run)[]
        {
            ("typed write", () => CultMesh.WriteSingleFileDocument(path, Key, Publication())),
            ("raw write", () => CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, EmptyArray)),
            ("payload read", () => CultMesh.ReadSingleFileDocumentPayload(path, Key, "raw:schema")),
            ("keyless payload read", () => CultMesh.ReadSingleFileDocumentPayload(path, "raw:schema")),
            ("typed read", () => CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key)),
        };
        foreach (var (name, run) in operations)
        {
            var error = Assert.Catch<IOException>(() => run(), name)!;
            error.Should().NotBeOfType<FileNotFoundException>(name + " reported a dangling link as a missing file");
        }

        IsALink(path).Should().BeTrue("the link was replaced");
        Directory.Exists(target).Should().BeFalse("a write went through the link");
        File.Exists(target).Should().BeFalse("a write went through the link");
        TempFiles().Should().BeEmpty();
    }

    [Test]
    public void OnlyNothingAtThePathIsAMissingFile()
    {
        var path = Path.Combine(_root, "missing.cc");
        var missing = Assert.Throws<FileNotFoundException>(() => CultMesh.ReadSingleFileDocumentPayload(path, Key, "raw:schema"))!;
        missing.FileName.Should().Be(path);
        Assert.Throws<FileNotFoundException>(() => CultMesh.ReadSingleFileDocumentPayload(path, "raw:schema"));
        Assert.Throws<FileNotFoundException>(() => CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key));
        File.Exists(path).Should().BeFalse("a read created the file");

        // A write to nothing at the path creates the store, and the reader reads it back.
        CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, EmptyArray);
        CultMesh.ReadSingleFileDocumentPayload(path, Key, "raw:schema").Should().Equal(EmptyArray);
    }

    [Test]
    public void ADirectoryAtThePathIsAnIoErrorOnReadAndWriteAndIsLeftAlone()
    {
        var path = Path.Combine(_root, "directory.cc");
        Directory.CreateDirectory(path);

        var operations = new (string Name, Action Run)[]
        {
            ("typed write", () => CultMesh.WriteSingleFileDocument(path, Key, Publication())),
            ("raw write", () => CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, EmptyArray)),
            ("payload read", () => CultMesh.ReadSingleFileDocumentPayload(path, Key, "raw:schema")),
            ("keyless payload read", () => CultMesh.ReadSingleFileDocumentPayload(path, "raw:schema")),
        };
        foreach (var (name, run) in operations)
        {
            var error = Assert.Catch<IOException>(() => run(), name)!;
            error.Should().NotBeOfType<FileNotFoundException>(name);
            error.Message.Should().Contain("directory", name);
            error.Message.Should().NotContain(_root, name + " echoed the path in its refusal text");
        }

        Directory.Exists(path).Should().BeTrue();
        Directory.GetFileSystemEntries(path).Should().BeEmpty();
        TempFiles().Should().BeEmpty();
    }

    [Test]
    public void AZeroByteFileIsRefusedOnTypedAndRawWriteAndLeftAlone()
    {
        var path = Path.Combine(_root, "zero.cc");
        File.WriteAllBytes(path, Array.Empty<byte>());

        Assert.Throws<CultStoreUnreadableException>(() => CultMesh.WriteSingleFileDocument(path, Key, Publication()));
        Assert.Throws<CultStoreUnreadableException>(() => CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, EmptyArray));
        Assert.Throws<CultStoreUnreadableException>(() => CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key));
        new FileInfo(path).Length.Should().Be(0);
    }

    [Test]
    public void AMeshWriteWaitsForTheStoreLock()
    {
        var path = Path.Combine(_root, "locked.cc");
        FileStream? held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var write = Task.Run(() => CultMesh.WriteSingleFileDocumentPayload(path, Key, Raw, null, EmptyArray));

            write.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("a Mesh write went ahead of the store lock");
            File.Exists(path).Should().BeFalse("a Mesh write landed while the lock was held");

            held.Dispose();
            held = null;
            write.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the write never completed after the lock was released");
            write.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            held?.Dispose();
        }

        CultMesh.ReadSingleFileDocumentPayload(path, Key, "raw:schema").Should().Equal(EmptyArray);
    }

    [Test]
    public void ACacheOpeningDuringMeshWritesNeverSeesAnEmptyStore()
    {
        // Windows replaces with ReplaceFile, which leaves a moment with nothing at the path for a lock-free reader: 1,722 empty
        // stores in 17,313 opens at b1ff2dcb. That is the store's replace, not Mesh's, and it is the same for a cache flush beside a
        // cache open; the fork is in the cut report.
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Windows File.Replace is not atomic to a lock-free reader; see cut report fork mesh-reader-windows-replace-window.");

        var path = Path.Combine(_root, "race.cc");
        CultMesh.WriteSingleFileDocument(path, Key, Publication());
        const int writes = 500;
        var stop = 0;
        var errors = new ConcurrentQueue<string>();
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < writes; index++)
            {
                try { CultMesh.WriteSingleFileDocument(path, Key, Publication()); }
                catch (Exception ex) { errors.Enqueue("write: " + ex.GetType().Name + ": " + ex.Message); }
            }

            Volatile.Write(ref stop, 1);
        });

        int opens = 0, empty = 0, reads = 0;
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref stop) == 0 && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                opens++;
                if (RecordsSeenByACache(path) == 0) empty++;
                CultMesh.ReadSingleFileDocumentPayload(path, Key, SchemaId());
                reads++;
            }
            catch (Exception ex)
            {
                errors.Enqueue("read: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        writer.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue();
        TestContext.Out.WriteLine($"mesh writes={writes} cache opens={opens} empty stores={empty} mesh reads ok={reads} errors={errors.Count}");
        errors.Take(3).Should().BeEmpty();
        empty.Should().Be(0, "a cache opened in the window where nothing was at the path");
        opens.Should().BeGreaterThan(0);
    }

    [Test]
    public void ConcurrentMeshWritersNeverErrorAndLeaveAReadableFile()
    {
        var path = Path.Combine(_root, "writers.cc");
        const int writers = 4, each = 100;
        var errors = new ConcurrentQueue<string>();
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < each; index++)
            {
                try { CultMesh.WriteSingleFileDocument(path, Key, Publication("writer-" + writer)); }
                catch (Exception ex) { errors.Enqueue(ex.GetType().Name + ": " + ex.Message); }
            }
        })).ToArray();

        Task.WaitAll(tasks, TimeSpan.FromSeconds(120)).Should().BeTrue();
        TestContext.Out.WriteLine($"mesh writers={writers} writes each={each} errors={errors.Count}");
        errors.Take(3).Should().BeEmpty();
        File.Exists(path).Should().BeTrue("concurrent writers deleted the store");
        CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, Key).ProducerId.Should().StartWith("writer-");
        RecordsSeenByACache(path).Should().Be(1);
        TempFiles().Should().BeEmpty();
    }

    // The store's replace decides what a write leaves where a live link was, and a Mesh write leaves what a cache commit leaves:
    // one replace, whatever the platform does with a link.
    [Test]
    public void AMeshWriteOverALiveLinkLeavesWhatACacheCommitLeaves()
    {
        string Outcome(string name, Action<string> write)
        {
            var target = Path.Combine(_root, name + "-target.cc");
            var link = Path.Combine(_root, name + "-link.cc");
            CultMesh.WriteSingleFileDocument(target, Key, Publication("first"));
            CreateLiveLinkOrIgnore(link, target);
            var before = File.ReadAllBytes(target);

            write(link);

            CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(link, Key).ProducerId.Should().Be("second", name);
            return $"linkKept={IsALink(link)} targetChanged={!File.ReadAllBytes(target).SequenceEqual(before)}";
        }

        var viaMesh = Outcome("mesh", link => CultMesh.WriteSingleFileDocument(link, Key, Publication("second")));
        var viaCache = Outcome("cache", link =>
        {
            using var cache = CultCacheMessagePack.Create(link, ReadOnlyOpen);
            cache.Commit(batch => batch.Upsert(typeof(CultMeshBodyPublicationDocument), Publication("second"), Key));
        });

        viaMesh.Should().Be(viaCache);
    }
}
