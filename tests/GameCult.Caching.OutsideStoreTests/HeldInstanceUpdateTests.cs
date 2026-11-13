using System;
using System.Collections.Generic;
using System.IO;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.OutsideStoreTests
{
    // variants:follow_up:update-copy-from-stored-bytes: Update takes its version and its copy from one durable read of the home
    // store, never from the held instance. Consumer side: public API only, so a change made in place to an instance the cache
    // hands out can never be committed as if it had been read.
    public class HeldInstanceUpdateTests
    {
        [CultDocument("tests.held_counter", "tests.held_counter.v1")]
        [MessagePackObject]
        public sealed class HeldCounter
        {
            [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
            [Key(1)] public int Value { get; set; }
        }

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(HeldCounter) });
        private static readonly CultRecordKey Key = new("counter");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-held-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private static CultCache Open(string path, bool directory)
        {
            var cache = new CultCache(Registry, CultCacheMessagePack.CreateCodec(Registry));
            cache.AddBackingStore(directory ? new DirectoryMessagePackBackingStore(path) : new SingleFileMessagePackBackingStore(path));
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            return cache;
        }

        private string Seed(bool directory, int value)
        {
            var path = Path.Combine(_directory, "held.cc");
            using var cache = Open(path, directory);
            Assert.That(cache.Commit(batch => batch.Upsert(new HeldCounter { Name = "counter", Value = value }, new CultRecordHandle<HeldCounter>(Key))), Is.True);
            return path;
        }

        private static int StoredValue(string path, bool directory)
        {
            using var reader = Open(path, directory);
            return reader.Get<HeldCounter>(Key)!.Value;
        }

        private static HeldCounter Plus(HeldCounter? counter, int by)
        {
            counter!.Value += by;
            return counter;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InPlaceChangeToHeldInstanceIsNeitherAppliedNorPersisted(bool directory)
        {
            var path = Seed(directory, 1);
            using var cache = Open(path, directory);
            cache.Get<HeldCounter>(Key)!.Value = 999;
            var seen = new List<int>();

            var result = cache.Update<HeldCounter>(Key, counter => { seen.Add(counter!.Value); return Plus(counter, 1); });

            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(seen, Is.EqualTo(new[] { 1 }), "apply saw the in-place change");
            Assert.That(result.Document!.Value, Is.EqualTo(2));
            Assert.That(StoredValue(path, directory), Is.EqualTo(2), "the in-place 999 was persisted");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InPlaceChangeToAPreviousResultIsNotCommitted(bool directory)
        {
            var path = Seed(directory, 1);
            using var cache = Open(path, directory);
            var first = cache.Update<HeldCounter>(Key, counter => Plus(counter, 1));
            Assert.That(first.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            first.Document!.Value = 999;
            var seen = new List<int>();

            var second = cache.Update<HeldCounter>(Key, counter => { seen.Add(counter!.Value); return Plus(counter, 1); });

            Assert.That(second.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(seen, Is.EqualTo(new[] { 2 }), "apply ran more than once or saw the in-place change");
            Assert.That(StoredValue(path, directory), Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DeclineAfterInPlaceChangeWritesNothing(bool directory)
        {
            var path = Seed(directory, 1);
            using var cache = Open(path, directory);
            cache.Get<HeldCounter>(Key)!.Value = 999;
            var before = Snapshot(path);

            Assert.That(cache.Update<HeldCounter>(Key, _ => null).Outcome, Is.EqualTo(CultUpdateOutcome.Declined));

            Assert.That(Snapshot(path), Is.EqualTo(before));
            Assert.That(StoredValue(path, directory), Is.EqualTo(1));
        }

        // Every file of the store: the single file, or the manifest and its pages.
        private static Dictionary<string, byte[]> Snapshot(string path)
        {
            var files = new Dictionary<string, byte[]> { [path] = File.ReadAllBytes(path) };
            var pages = path + ".records";
            if (Directory.Exists(pages))
                foreach (var file in Directory.GetFiles(pages, "*", SearchOption.AllDirectories))
                    files[file] = File.ReadAllBytes(file);
            return files;
        }
    }
}
