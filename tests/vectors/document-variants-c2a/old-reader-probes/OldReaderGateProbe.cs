#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Copied into a checkout of cultlib-unity-v2.0.1 (b8fc5eb0) by old-reader-refusal.sh. Not part of this tree's suite.
    // The 2.0.1 reader meets the stores C2a writes. The source is "committed" (tests/vectors/document-variants-c2a) or, when
    // CULTLIB_GATE_OUT is set, "live" (what C2aGateWriter just wrote at the tree under test). Every refusal names the format, and
    // the store is byte-identical after it.
    public class OldReaderGateProbe
    {
        private const string SingleName = "c2a-written-ids.msgpack";
        private const string DirectoryName = "c2a-written-ids-directory";
        private const string ManifestName = "store.cc";

        [CultDocument("tests.gate_probe", "tests.gate_probe.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class GateProbeDocument
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        public static IEnumerable<string> Sources()
        {
            yield return "committed";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CULTLIB_GATE_OUT")))
                yield return "live";
        }

        private static string SourceRoot(string source) => source == "live"
            ? Environment.GetEnvironmentVariable("CULTLIB_GATE_OUT")!
            : Path.Combine(Environment.GetEnvironmentVariable("CULTLIB_ROOT")!, "tests", "vectors", "document-variants-c2a");

        private string _temp = string.Empty;

        [SetUp]
        public void CreateTemp()
        {
            _temp = Path.Combine(Path.GetTempPath(), $"cultlib-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_temp);
        }

        [TearDown]
        public void DeleteTemp()
        {
            if (Directory.Exists(_temp))
                Directory.Delete(_temp, recursive: true);
        }

        private string CopySingle(string source, string name)
        {
            var file = Path.Combine(_temp, name);
            File.Copy(Path.Combine(SourceRoot(source), SingleName), file);
            return file;
        }

        private string CopyDirectory(string source, string name)
        {
            var root = Path.Combine(_temp, name);
            CopyTree(Path.Combine(SourceRoot(source), DirectoryName), root);
            return Path.Combine(root, ManifestName);
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var directory in Directory.GetDirectories(from))
                CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        }

        // Every file under the root with its content hash, in a fixed order: a tree compares equal only if nothing in it moved.
        private static string[] Tree(string root) =>
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(root, file) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
                .ToArray();

        private static void Names(Exception? error, string format, string what)
        {
            Assert.That(error, Is.Not.Null, what);
            TestContext.Out.WriteLine($"OLD-READER csharp refused {what}: " + error!.Message);
            Assert.That(error.ToString(), Does.Contain(format), what);
        }

        [TestCaseSource(nameof(Sources))]
        public void V3IsRefusedAtAttach(string source)
        {
            var file = CopySingle(source, "attach.cc");
            var before = File.ReadAllBytes(file);
            using var cache = new CultCache();
            Names(Assert.Catch(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(file))), "cultcache.store.v3", "attach");
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
        }

        [TestCaseSource(nameof(Sources))]
        public void V3IsRefusedByPushAllOfAStoreThatWasNeverAttached(string source)
        {
            var file = CopySingle(source, "detached.cc");
            var before = File.ReadAllBytes(file);
            var store = new SingleFileMessagePackBackingStore(file);
            Names(Assert.Catch(() => store.PushAll()), "cultcache.store.v3", "PushAll of a detached store");
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
        }

        [TestCaseSource(nameof(Sources))]
        public async Task V3SwappedInAfterAttachIsRefusedByFlushAndPushAll(string source)
        {
            var file = Path.Combine(_temp, "swap.cc");
            using var cache = new CultCache();
            var store = new SingleFileMessagePackBackingStore(file);
            cache.AddBackingStore(store);
            await cache.AddAsync(new GateProbeDocument { Name = "staged" });
            File.Copy(Path.Combine(SourceRoot(source), SingleName), file, overwrite: true);
            var before = File.ReadAllBytes(file);

            Names(Assert.Catch(() => cache.FlushAllBackingStores()), "cultcache.store.v3", "flush after a swap");
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
            Names(Assert.Catch(() => store.PushAll()), "cultcache.store.v3", "PushAll after a swap");
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
        }

        [TestCaseSource(nameof(Sources))]
        public void V5IsRefusedByTheDirectoryStoreAtAttach(string source)
        {
            var manifest = CopyDirectory(source, "attach-directory");
            var before = Tree(Path.GetDirectoryName(manifest)!);
            using var cache = new CultCache();
            Names(Assert.Catch(() => cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest))), "cultcache.store.v5", "directory attach");
            Assert.That(Tree(Path.GetDirectoryName(manifest)!), Is.EqualTo(before));
        }

        [TestCaseSource(nameof(Sources))]
        public async Task V5SwappedInAfterAttachIsRefusedByTheDirectoryStoreAtFlush(string source)
        {
            var root = Path.Combine(_temp, "swap-directory");
            Directory.CreateDirectory(root);
            var manifest = Path.Combine(root, ManifestName);
            using var cache = new CultCache();
            var store = new DirectoryMessagePackBackingStore(manifest);
            cache.AddBackingStore(store);
            await cache.AddAsync(new GateProbeDocument { Name = "staged" });
            CopyTree(Path.Combine(SourceRoot(source), DirectoryName), root);
            var before = Tree(root);

            Names(Assert.Catch(() => cache.FlushAllBackingStores()), "cultcache.store.v5", "directory flush after a swap");
            Assert.That(Tree(root), Is.EqualTo(before));
            Names(Assert.Catch(() => store.PushAll()), "cultcache.store.v5", "directory PushAll after a swap");
            Assert.That(Tree(root), Is.EqualTo(before));
        }
    }
}
