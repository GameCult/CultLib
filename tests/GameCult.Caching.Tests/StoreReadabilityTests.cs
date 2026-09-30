#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;

namespace GameCult.Caching.Tests
{
    // A file is replaced by a flush or a commit exactly when this runtime's own reader opens it: one verdict per file, asked by
    // open, flush and commit alike. The bytes and the verdict of every runtime are shared: tests/vectors/document-variants-c2a/readability.
    public class StoreReadabilityTests
    {
        private const int CSharp = 1;
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-readability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private static string VectorRoot()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c2a", "readability");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            throw new DirectoryNotFoundException($"Shared readability vectors not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        public static IEnumerable<TestCaseData> Vectors() =>
            File.ReadAllLines(Path.Combine(VectorRoot(), "manifest.txt"))
                .Where(line => line.Length > 0 && line[0] != '#')
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Select(cells => new TestCaseData(cells[0], cells[CSharp] == "reads").SetName($"{cells[0]} {cells[CSharp]}"));

        private static string Header(string path) => CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

        private string Seed(string name)
        {
            var path = Path.Combine(_directory, name);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "d" }, new CultRecordKey("d")));
            return path;
        }

        [TestCaseSource(nameof(Vectors))]
        public void OpenReadsExactlyTheFilesTheVectorsSayItReads(string vector, bool reads)
        {
            var path = Path.Combine(_directory, "open.cc");
            File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(VectorRoot(), vector)));

            using var cache = new CultCache();
            void Open()
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            }

            if (reads)
                Assert.DoesNotThrow(Open);
            else
                Assert.That(Open, Throws.Exception);
        }

        [TestCaseSource(nameof(Vectors))]
        public void AFlushReplacesAFileExactlyWhenItReads(string vector, bool reads)
        {
            var path = Seed("flush.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            var bytes = File.ReadAllBytes(Path.Combine(VectorRoot(), vector));
            File.WriteAllBytes(path, bytes);
            var store = cache.BackingStores[0];

            if (reads)
            {
                store.PushAll();
                Assert.That(Header(path), Does.StartWith("cultcache.store.v"), "the file is a store this runtime wrote");
                return;
            }

            Assert.That(() => store.PushAll(), Throws.Exception);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "a file this runtime cannot read was rewritten");
        }

        [TestCaseSource(nameof(Vectors))]
        public void AnUnconditionalCommitReplacesAFileExactlyWhenItReads(string vector, bool reads)
        {
            var path = Seed("commit.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            var bytes = File.ReadAllBytes(Path.Combine(VectorRoot(), vector));
            File.WriteAllBytes(path, bytes);

            if (reads)
            {
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e")));
                Assert.That(Header(path), Does.StartWith("cultcache.store.v"), "the file is a store this runtime wrote");
                return;
            }

            Assert.That(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "e" }, new CultRecordKey("e"))), Throws.Exception);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "a file this runtime cannot read was rewritten");
        }

        // The record type of the shared valid store (v3-base.msgpack: alpha and beta), so a cache can open it.
        [CultDocument("vectors.item", "vectors.item.v1")]
        [MessagePackObject]
        public sealed class VectorItem
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public int Count { get; set; }
        }
    }
}
