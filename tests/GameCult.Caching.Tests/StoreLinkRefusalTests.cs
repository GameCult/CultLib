#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;
using static GameCult.Caching.Tests.StoreReadabilityTests;

namespace GameCult.Caching.Tests
{
    // A write never replaces a link at a store-owned path. The refusal holds for every way a write reaches a store (waiting or
    // not, as a judge of another store's commit), for a link put there while the write waited on the store's lock, and for the
    // directory store's manifest.
    public class StoreLinkRefusalTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck), typeof(VectorItem) });
        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-link-refusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (!Directory.Exists(_directory))
                return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(_directory).Where(IsALink))
                File.Delete(entry);
            Directory.Delete(_directory, true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static bool IsALink(string path)
        {
            var attributes = new FileInfo(path).Attributes;
            return (int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static void Link(string path, string target)
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

        private static void SeedSingle(string path)
        {
            using var seed = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            seed.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "seed" }, new CultRecordKey("seed")));
        }

        private static void SeedItem(string path)
        {
            using var seed = new CultCache(Registry);
            seed.AddBackingStore(new SingleFileMessagePackBackingStore(path), typeof(VectorItem));
            seed.Commit(batch => batch.Upsert(typeof(VectorItem), new VectorItem(), new CultRecordKey("item")));
        }

        // Runs `write` while another holder has `lockPath`; `swap` runs while it waits, then the lock is released.
        private static Exception? WriteWhileWaiting(string lockPath, Action write, Action swap)
        {
            using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var task = Task.Run(write);
            Assert.That(task.Wait(TimeSpan.FromMilliseconds(600)), Is.False, "the lock is held, so the write must wait");
            swap();
            held.Dispose();
            try
            {
                Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True);
                return null;
            }
            catch (AggregateException ex)
            {
                return ex.GetBaseException();
            }
        }

        private static void ReplaceWithLink(string path, string target)
        {
            File.Delete(path);
            Link(path, target);
        }

        // ---- a link put at the store path while the write waited on the lock ----

        [Test]
        public void ACommitRefusesALinkPutAtTheStoreWhileItWaitedOnTheLock()
        {
            var path = PathOf("store.cc");
            var target = PathOf("shared.cc");
            SeedSingle(path);
            SeedSingle(target);
            var before = File.ReadAllBytes(target);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, FlushOnDispose = false, StoreFlushOnDispose = false });

            var error = WriteWhileWaiting(path + ".lock",
                () => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "late" }, new CultRecordKey("late"))),
                () => ReplaceWithLink(path, target));

            Assert.That(error, Is.InstanceOf<CultStoreLinkedException>(), error?.ToString());
            Assert.That(((CultStoreLinkedException)error!).Path, Is.EqualTo(path));
            Assert.That(IsALink(path), "the link was replaced");
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(before));
        }

        [Test]
        public void AFlushRefusesALinkPutAtTheStoreWhileItWaitedOnTheLock()
        {
            var path = PathOf("store.cc");
            var target = PathOf("shared.cc");
            SeedSingle(path);
            SeedSingle(target);
            var before = File.ReadAllBytes(target);
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, FlushOnDispose = false, StoreFlushOnDispose = false });
            cache.UpsertAsync(typeof(IdDeck), new IdDeck { Name = "late" }, new CultRecordKey("late")).GetAwaiter().GetResult();

            var error = WriteWhileWaiting(path + ".lock", () => cache.FlushAllBackingStores(), () => ReplaceWithLink(path, target));

            Assert.That(error, Is.InstanceOf<CultStoreLinkedException>(), error?.ToString());
            Assert.That(IsALink(path), "the link was replaced");
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(before));
        }

        // ---- the non-waiting and judging write paths ----

        [Test]
        public void ATryCommitAtALiveLinkIsRefusedNotContendedOrReplaced()
        {
            var path = PathOf("store.cc");
            var link = PathOf("link.cc");
            SeedSingle(path);
            Link(link, path);
            var before = File.ReadAllBytes(path);
            using var cache = CultCacheMessagePack.Create(link, new CultCacheOpenOptions { Registry = Registry, FlushOnDispose = false, StoreFlushOnDispose = false });

            var refusal = Assert.Throws<CultStoreLinkedException>(() =>
                cache.TryCommit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "t" }, new CultRecordKey("t"))))!;

            Assert.That(refusal.Path, Is.EqualTo(link));
            Assert.That(refusal.Message, Does.Contain(link));
            Assert.That(IsALink(link), "the link was replaced");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(File.Exists(link + ".lock"), Is.False, "the refused write took the lock");
        }

        [Test]
        public void ATryCommitAtALiveLinkWhoseLockIsHeldIsStillRefused()
        {
            var path = PathOf("store.cc");
            var link = PathOf("link.cc");
            SeedSingle(path);
            Link(link, path);
            using var cache = CultCacheMessagePack.Create(link, new CultCacheOpenOptions { Registry = Registry, FlushOnDispose = false, StoreFlushOnDispose = false });
            using var held = new FileStream(link + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            Assert.Throws<CultStoreLinkedException>(() =>
                cache.TryCommit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "t" }, new CultRecordKey("t"))));
            Assert.That(IsALink(link), "the link was replaced");
        }

        // A batch that upserts into one store and removes a key the cache does not hold has the other store judge that removal
        // under its own lock (JudgeChecks).
        private CultCache OpenTwoStores(string homePath, string otherPath)
        {
            var cache = new CultCache(Registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(homePath), typeof(IdDeck));
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(otherPath), typeof(VectorItem));
            return cache;
        }

        [Test]
        public void AnotherStoreAtALiveLinkRefusesTheChecksItIsAskedToJudge()
        {
            var other = PathOf("other.cc");
            var link = PathOf("other-link.cc");
            SeedItem(other);
            Link(link, other);
            var before = File.ReadAllBytes(other);
            using var cache = OpenTwoStores(PathOf("home.cc"), link);

            var refusal = Assert.Throws<CultStoreLinkedException>(() => cache.Commit(batch =>
            {
                batch.Upsert(typeof(IdDeck), new IdDeck { Name = "home" }, new CultRecordKey("home"));
                batch.Remove(new CultRecordKey("ghost"));
            }))!;

            Assert.That(refusal.Path, Is.EqualTo(link));
            Assert.That(IsALink(link), "the link was replaced");
            Assert.That(File.ReadAllBytes(other), Is.EqualTo(before));
            Assert.That(File.Exists(PathOf("home.cc")), Is.False, "the home store was written though a judge refused");
        }

        [Test]
        public void AJudgeReadsTheFileItHoldsUnderTheLockNotTheFileBeforeItWaited()
        {
            var other = PathOf("other.cc");
            SeedItem(other);
            using var cache = OpenTwoStores(PathOf("home.cc"), other);

            var error = WriteWhileWaiting(other + ".lock",
                () => cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), new IdDeck { Name = "home" }, new CultRecordKey("home"));
                    batch.Remove(new CultRecordKey("ghost"));
                }),
                () => File.WriteAllBytes(other, Array.Empty<byte>()));

            Assert.That(error, Is.InstanceOf<CultStoreUnreadableException>(), error?.ToString());
        }

        // ---- the directory store's manifest ----

        private DirectoryMessagePackBackingStore SeedDirectory(string manifest)
        {
            using (var seed = new CultCache(Registry))
            {
                seed.AddBackingStore(new DirectoryMessagePackBackingStore(manifest), typeof(IdDeck));
                seed.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "seed" }, new CultRecordKey("seed")));
            }

            return new DirectoryMessagePackBackingStore(manifest);
        }

        private CultCache OpenDirectory(DirectoryMessagePackBackingStore store)
        {
            var cache = new CultCache(Registry);
            cache.AddBackingStore(store, typeof(IdDeck));
            return cache;
        }

        [Test]
        public void ADirectoryStoreCommitAtALiveManifestLinkIsRefusedAndNothingChanges()
        {
            var manifest = PathOf("real.cc");
            var link = PathOf("link.cc");
            SeedDirectory(manifest);
            Link(link, manifest);
            Directory.Move(manifest + ".records", link + ".records");
            var before = File.ReadAllBytes(manifest);
            var pages = Directory.GetFileSystemEntries(link + ".records").OrderBy(entry => entry).ToArray();
            using var cache = OpenDirectory(new DirectoryMessagePackBackingStore(link));

            var refusal = Assert.Throws<CultStoreLinkedException>(() =>
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "b" }, new CultRecordKey("b"))))!;

            Assert.That(refusal.Path, Is.EqualTo(link));
            Assert.That(IsALink(link), "the link was replaced");
            Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(before));
            Assert.That(Directory.GetFileSystemEntries(link + ".records").OrderBy(entry => entry), Is.EqualTo(pages), "the refused write left files beside the manifest");
        }

        // The refusal comes before the records folder or the lease file is made: a store that was never written leaves nothing behind.
        [Test]
        public void ADirectoryStoreCommitAtAManifestLinkPutThereAfterOpenCreatesNothing()
        {
            var link = PathOf("link.cc");
            using var cache = OpenDirectory(new DirectoryMessagePackBackingStore(link));
            Link(link, PathOf("unmounted.cc"));

            Assert.Throws<CultStoreLinkedException>(() =>
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "b" }, new CultRecordKey("b"))));

            Assert.That(IsALink(link), "the link was replaced");
            Assert.That(Directory.Exists(link + ".records"), Is.False, "the refused write made the records folder or its lease");
        }

        [Test]
        public void ADirectoryStoreFlushAtALiveManifestLinkIsRefused()
        {
            var manifest = PathOf("real.cc");
            var link = PathOf("link.cc");
            SeedDirectory(manifest);
            Link(link, manifest);
            Directory.Move(manifest + ".records", link + ".records");
            var before = File.ReadAllBytes(manifest);
            using var cache = OpenDirectory(new DirectoryMessagePackBackingStore(link));
            cache.UpsertAsync(typeof(IdDeck), new IdDeck { Name = "f" }, new CultRecordKey("f")).GetAwaiter().GetResult();

            Assert.Throws<CultStoreLinkedException>(() => cache.FlushAllBackingStores());

            Assert.That(IsALink(link), "the link was replaced");
            Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(before));
        }

        [Test]
        public void ADirectoryStoreCommitRefusesAManifestLinkPutThereWhileItWaitedOnTheLease()
        {
            var manifest = PathOf("store.cc");
            var target = PathOf("shared.cc");
            SeedDirectory(manifest);
            SeedDirectory(target);
            var before = File.ReadAllBytes(target);
            using var cache = OpenDirectory(new DirectoryMessagePackBackingStore(manifest));

            var error = WriteWhileWaiting(Path.Combine(manifest + ".records", ".commit.lock"),
                () => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "late" }, new CultRecordKey("late"))),
                () => ReplaceWithLink(manifest, target));

            Assert.That(error, Is.InstanceOf<CultStoreLinkedException>(), error?.ToString());
            Assert.That(IsALink(manifest), "the link was replaced");
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(before));
        }

        [Test]
        public void ADirectoryStoreReadsThroughALiveManifestLink()
        {
            var manifest = PathOf("real.cc");
            var link = PathOf("link.cc");
            SeedDirectory(manifest);
            Link(link, manifest);
            Directory.Move(manifest + ".records", link + ".records");

            using var cache = OpenDirectory(new DirectoryMessagePackBackingStore(link));

            Assert.That(cache.Get<IdDeck>(new CultRecordKey("seed"))?.Name, Is.EqualTo("seed"));
        }
    }
}
