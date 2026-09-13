#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    public class ConditionalCommitTests
    {
        private static readonly CultRecordKey Key = new("counter");
        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-conditional-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void StaleExpectFailsWithNothingChanged() => StaleExpect(SingleFile(PathOf("store.cc")));

        [Test]
        public void ExpectNullIsCreateOnce() => CreateOnce(SingleFile(PathOf("store.cc")));

        [Test]
        public void PerEntryCommitSurvivesUnrelatedWrite() => UnrelatedWrite(SingleFile(PathOf("store.cc")));

        [Test]
        public void ExpectUnchangedFailsOnUnrelatedInsert() => UnrelatedInsert(SingleFile(PathOf("store.cc")));

        [Test]
        public void DirectoryStoreHonorsConditions()
        {
            StaleExpect(DirectoryStore(PathOf("stale.cc")));
            CreateOnce(DirectoryStore(PathOf("create.cc")));
            UnrelatedWrite(DirectoryStore(PathOf("unrelated.cc")));
            UnrelatedInsert(DirectoryStore(PathOf("insert.cc")));
        }

        // The lock is a FileShare.None handle, which excludes another handle in this process exactly as it does one in
        // another process, so two store instances on two threads prove the property without a process boundary.
        [Test]
        public void TwoWritersRacingExactlyOneWins()
        {
            const int n = 100;
            var store = SingleFile(PathOf("race.cc"));
            Create(store, 0);
            using var a = store.Open();
            using var b = store.Open();

            Assert.That(Increment(a), Is.True);
            Assert.That(Increment(b), Is.False, "b observed before a committed and still won");
            b.PullAllBackingStoresAsync();
            Assert.That(Increment(b), Is.True);
            Assert.That(Increment(a), Is.False, "a committed over b's newer record from its stale view");

            var racers = new[] { a, b }.Select(cache => Task.Run(() =>
            {
                var wins = 0;
                var mismatches = 0;
                for (var attempt = 0; wins < n && attempt < 50 * n; attempt++)
                {
                    cache.PullAllBackingStoresAsync();
                    if (Increment(cache))
                        wins++;
                    else
                        mismatches++;
                }

                return (Wins: wins, Mismatches: mismatches);
            })).ToArray();
            Assert.That(Task.WaitAll(racers, TimeSpan.FromMinutes(2)), Is.True, "the racers did not finish");

            Assert.That(racers.Select(racer => racer.Result.Wins), Is.All.EqualTo(n));
            Assert.That(racers.Sum(racer => racer.Result.Mismatches), Is.GreaterThan(0), "the writers never actually raced");
            using var reader = store.Open();
            Assert.That(reader.Get<Counter>(Key)!.Value, Is.EqualTo(2 + 2 * n));
        }

        [Test]
        public void TryCommitReportsContended()
        {
            var path = PathOf("contended.cc");
            var store = SingleFile(path);
            Create(store, 0);
            using var cache = store.Open();
            var before = File.ReadAllBytes(path);
            Task<bool> waiting;

            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(cache.TryCommit(batch => batch.Upsert(new Counter { Name = "x" }, new CultRecordHandle<Counter>(new CultRecordKey("x")))),
                    Is.EqualTo(CultCommitOutcome.Contended));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
                Assert.That(cache.Get(new CultRecordKey("x")), Is.Null);

                waiting = Task.Run(() => cache.Commit(batch => batch.Upsert(new Counter { Name = "y" }, new CultRecordHandle<Counter>(new CultRecordKey("y")))));
                Assert.That(waiting.Wait(200), Is.False, "Commit did not wait for the held lock");
            }

            Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)) && waiting.Result, Is.True);
            Assert.That(cache.Get(new CultRecordKey("y")), Is.Not.Null);
        }

        [Test]
        public void TryCommitIsNotContendedByAConcurrentReader()
        {
            var path = PathOf("reader.cc");
            var store = SingleFile(path);
            Create(store, 0);
            using var cache = store.Open();
            var handle = new CultRecordHandle<Counter>(new CultRecordKey("read-under"));
            var running = true;
            var reads = 0;
            using var started = new ManualResetEventSlim();
            var reading = Task.Run(() =>
            {
                started.Set();
                while (Volatile.Read(ref running))
                {
                    _ = cache.AllStoredDocuments.Count();
                    Interlocked.Increment(ref reads);
                }
            });

            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True);
                for (var i = 0; i < 200; i++)
                    Assert.That(cache.TryCommit(batch => batch.Upsert(new Counter { Name = "read-under", Value = i }, handle)),
                        Is.EqualTo(CultCommitOutcome.Committed), $"attempt {i} lost to a reader, not a writer");
            }
            finally
            {
                Volatile.Write(ref running, false);
                Assert.That(reading.Wait(TimeSpan.FromSeconds(10)), Is.True);
            }

            Assert.That(reads, Is.GreaterThan(0));
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.That(cache.TryCommit(batch => batch.Upsert(new Counter { Name = "read-under" }, handle)),
                    Is.EqualTo(CultCommitOutcome.Contended));
        }

        [Test]
        public void PushAllWaitsForHeldStoreLock()
        {
            var path = PathOf("push-wait.cc");
            using var cache = SingleFile(path).Open();
            cache.UpsertAsync(new Counter { Name = "pushed" }, new CultRecordHandle<Counter>(Key));
            Task flush;

            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                flush = Task.Run(() => cache.FlushAllBackingStores());
                Assert.That(flush.Wait(150), Is.False, "PushAll did not wait for the held store lock");
                Assert.That(File.Exists(path), Is.False);
            }

            Assert.That(flush.Wait(TimeSpan.FromSeconds(10)), Is.True);
            using var reader = SingleFile(path).Open();
            Assert.That(reader.Get(Key), Is.Not.Null);
        }

        [Test]
        public void CommitWaitsForHeldStoreLock()
        {
            var path = PathOf("commit-wait.cc");
            using var cache = SingleFile(path).Open();
            Task<bool> commit;

            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                commit = Task.Run(() => CreateCommit(cache, 7));
                Assert.That(commit.Wait(150), Is.False, "Commit did not wait for the held store lock");
                Assert.That(File.Exists(path), Is.False);
            }

            Assert.That(commit.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(commit.Result, Is.True);
            using var reader = SingleFile(path).Open();
            Assert.That(reader.Get<Counter>(Key)!.Value, Is.EqualTo(7));
        }

        [Test]
        public void PlainFlushNeverInterleaves()
        {
            var path = PathOf("interleave.cc");
            var store = SingleFile(path);
            Create(store, 0);
            using var flusher = store.Open();
            using var committer = store.Open();
            var running = true;
            var reads = 0;

            var flushing = Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    flusher.UpsertAsync(new Counter { Name = "flushed", Value = i }, new CultRecordHandle<Counter>(new CultRecordKey("flushed")));
                    flusher.FlushAllBackingStores();
                }
            });
            var committing = Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var key = new CultRecordKey($"committed:{i}");
                    committer.Commit(batch =>
                    {
                        batch.Expect(key, null);
                        batch.Upsert(new Counter { Name = key.Value, Value = i }, new CultRecordHandle<Counter>(key));
                    });
                }
            });
            var reading = Task.Run(() =>
            {
                while (Volatile.Read(ref running))
                {
                    byte[] bytes;
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        bytes = buffer.ToArray();
                    }
                    catch (IOException)
                    {
                        // An open that loses to a replace in flight (moved away, or held by the replace) is not a read.
                        continue;
                    }

                    CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes);
                    reads++;
                }
            });

            Assert.That(Task.WaitAll(new[] { flushing, committing }, TimeSpan.FromMinutes(2)), Is.True, "the writers did not finish");
            Volatile.Write(ref running, false);
            Assert.That(() => reading.Wait(TimeSpan.FromSeconds(10)), Throws.Nothing);
            Assert.That(reads, Is.GreaterThan(0));
        }

        [Test]
        public void StoredAtIsStrictlyIncreasingPerKey()
        {
            using var cache = SingleFile(PathOf("stamps.cc")).Open();
            var stamps = new string[1000];
            for (var i = 0; i < stamps.Length; i++)
            {
                cache.UpsertAsync(new Counter { Name = "stamped", Value = i }, new CultRecordHandle<Counter>(Key));
                stamps[i] = cache.AllStoredDocuments.Single().StoredAt;
            }

            Assert.That(stamps.Distinct().Count(), Is.EqualTo(stamps.Length));
            var instants = stamps.Select(stamp => DateTimeOffset.Parse(stamp, null, System.Globalization.DateTimeStyles.RoundtripKind)).ToArray();
            Assert.That(instants.Zip(instants.Skip(1), (earlier, later) => later > earlier), Is.All.True);
        }

        [Test]
        public void DirtyStoreRefusesConditionalCommit()
        {
            using var cache = SingleFile(PathOf("dirty.cc")).Open();
            cache.UpsertAsync(new Counter { Name = "staged" });

            Assert.That(() => cache.Commit(batch =>
            {
                batch.Expect(Key, null);
                batch.Upsert(new Counter { Name = "counter" }, new CultRecordHandle<Counter>(Key));
            }), Throws.InvalidOperationException.With.Message.Contains("flush"));
            Assert.That(cache.Get(Key), Is.Null);
        }

        [Test]
        public void CrossStoreConditionalBatchThrowsBeforeAnyStore()
        {
            using var cache = new CultCache(Registry);
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("counters.cc")), typeof(Counter));
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(PathOf("tallies.cc")), typeof(Tally));

            Assert.That(() => cache.Commit(batch =>
            {
                batch.Expect(Key, null);
                batch.Upsert(new Counter { Name = "counter" }, new CultRecordHandle<Counter>(Key));
                batch.Upsert(new Tally { Name = "tally" }, new CultRecordHandle<Tally>(new CultRecordKey("tally")));
            }), Throws.InvalidOperationException.With.Message.Contains("one home store"));
            Assert.That(System.IO.Directory.GetFiles(_directory), Is.Empty, "a store file or lock file was touched");
        }

        private static void StaleExpect(StoreFactory store)
        {
            Create(store, 0);
            using var a = store.Open();
            using var b = store.Open();
            Assert.That(Increment(a), Is.True);
            var before = File.ReadAllBytes(store.Path);
            var observedByB = b.Get<Counter>(Key)!;
            var changes = 0;
            using var subscription = b.Watch<Counter>().Subscribe(_ => changes++);

            Assert.That(Commit(b, observedByB, 100), Is.False, $"{store.Path}: b's stale Expect committed");
            Assert.That(File.ReadAllBytes(store.Path), Is.EqualTo(before));
            Assert.That(b.Get<Counter>(Key), Is.SameAs(observedByB));
            Assert.That(b.IsDirty, Is.False);
            Assert.That(changes, Is.Zero);
        }

        private static void CreateOnce(StoreFactory store)
        {
            using var a = store.Open();
            using var b = store.Open();
            Assert.That(CreateCommit(a, 1), Is.True);
            Assert.That(CreateCommit(b, 2), Is.False, $"{store.Path}: a second create-once commit won");
            using var reader = store.Open();
            Assert.That(reader.Get<Counter>(Key)!.Value, Is.EqualTo(1));
        }

        private static void UnrelatedWrite(StoreFactory store)
        {
            Create(store, 0);
            using var a = store.Open();
            using var b = store.Open();
            Assert.That(a.Commit(batch => batch.Upsert(new Counter { Name = "b" }, new CultRecordHandle<Counter>(new CultRecordKey("b")))), Is.True);

            Assert.That(Increment(b), Is.True, $"{store.Path}: an unrelated write failed a per-entry condition");
            using var reader = store.Open();
            Assert.That(reader.Get<Counter>(Key)!.Value, Is.EqualTo(1));
            Assert.That(reader.Get(new CultRecordKey("b")), Is.Not.Null, $"{store.Path}: the commit dropped a record it never loaded");
        }

        private static void UnrelatedInsert(StoreFactory store)
        {
            Create(store, 0);
            using var a = store.Open();
            using var b = store.Open();
            Assert.That(a.Commit(batch => batch.Upsert(new Counter { Name = "c" }, new CultRecordHandle<Counter>(new CultRecordKey("c")))), Is.True);

            Assert.That(b.Commit(batch =>
            {
                batch.ExpectUnchanged();
                batch.Upsert(new Counter { Name = "counter", Value = 5 }, new CultRecordHandle<Counter>(Key));
            }), Is.False, $"{store.Path}: ExpectUnchanged ignored an insert");
        }

        private static void Create(StoreFactory store, int value)
        {
            using var cache = store.Open();
            Assert.That(CreateCommit(cache, value), Is.True);
        }

        private static bool CreateCommit(CultCache cache, int value) => cache.Commit(batch =>
        {
            batch.Expect(Key, null);
            batch.Upsert(new Counter { Name = "counter", Value = value }, new CultRecordHandle<Counter>(Key));
        });

        private static bool Increment(CultCache cache)
        {
            var observed = cache.Get<Counter>(Key)!;
            return Commit(cache, observed, observed.Value + 1);
        }

        private static bool Commit(CultCache cache, Counter observed, int value) => cache.Commit(batch =>
        {
            batch.Expect(Key, observed);
            batch.Upsert(new Counter { Name = "counter", Value = value }, new CultRecordHandle<Counter>(Key));
        });

        private string PathOf(string fileName) => System.IO.Path.Combine(_directory, fileName);

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(Counter), typeof(Tally) });

        private static StoreFactory SingleFile(string path) => new(path, () => new SingleFileMessagePackBackingStore(path));

        private static StoreFactory DirectoryStore(string path) => new(path, () => new DirectoryMessagePackBackingStore(path));

        private sealed class StoreFactory
        {
            private readonly Func<CacheBackingStore> _create;

            public StoreFactory(string path, Func<CacheBackingStore> create)
            {
                Path = path;
                _create = create;
            }

            public string Path { get; }

            public CultCache Open()
            {
                var cache = new CultCache(Registry);
                cache.AddBackingStore(_create());
                return cache;
            }
        }

        [CultDocument("tests.conditional_counter", "tests.conditional_counter.v1")]
        internal sealed class Counter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Value;
        }

        [CultDocument("tests.conditional_tally", "tests.conditional_tally.v1")]
        internal sealed class Tally
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }
    }
}
