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

        // Cache A pulls and upserts on two threads while cache B commits to the same directory store; the pull's load
        // callback and the upsert's push must take the cache gate and the store lock in one order. A is not disposed on
        // failure because a deadlocked gate would hang the test thread too.
        [Test]
        public void PullAndUpsertOnDirectoryStoreDoNotDeadlock()
        {
            var store = DirectoryStore(PathOf("deadlock.cc"));
            var a = store.Open();
            using var b = store.Open();
            var pulling = Task.Run(() =>
            {
                for (var i = 0; i < 300; i++)
                    a.PullAllBackingStoresAsync();
            });
            var upserting = Task.Run(() =>
            {
                for (var i = 0; i < 3000; i++)
                    a.UpsertAsync(new Counter { Name = $"a:{i}" }, new CultRecordHandle<Counter>(new CultRecordKey($"a:{i}")));
            });
            var writing = Task.Run(() =>
            {
                for (var i = 0; i < 150; i++)
                {
                    b.UpsertAsync(new Counter { Name = $"b:{i}" }, new CultRecordHandle<Counter>(new CultRecordKey($"b:{i}")));
                    b.FlushAllBackingStores();
                }
            });

            Assert.That(Task.WaitAll(new[] { pulling, upserting, writing }, TimeSpan.FromSeconds(60)), Is.True, "pull and upsert deadlocked");
            a.Dispose();
        }

        [Test]
        public void RepullNeverErasesAStagedWrite()
        {
            var path = PathOf("repull.cc");
            Create(SingleFile(path), 0);
            var store = new PausingStore(path);
            using var cache = new CultCache(Registry);
            cache.AddBackingStore(store);
            var staged = new CultRecordKey("staged");

            using (var release = new ManualResetEventSlim())
            {
                store.Pause = release;
                try
                {
                    var pulling = Task.Run(() => cache.PullAllBackingStoresAsync());
                    Assert.That(store.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the pull never read the file");
                    var writer = StartWriter(() => cache.UpsertAsync(new Counter { Name = "staged" }, new CultRecordHandle<Counter>(staged)), () => store.Pushed.IsSet);
                    store.Pause = null;
                    release.Set();
                    Assert.That(pulling.Wait(TimeSpan.FromSeconds(10)) && writer(), Is.True);
                }
                finally
                {
                    store.Pause = null;
                    release.Set();
                }
            }

            Assert.That(cache.Get(staged), Is.Not.Null, "the re-pull erased a staged write from the cache");
            cache.FlushAllBackingStores();
            using var reader = SingleFile(path).Open();
            Assert.That(reader.Get(staged), Is.Not.Null, "the flush after the re-pull lost the staged write");
        }

        // The pull pauses inside the directory store's own page load, under the commit lease.
        [Test]
        public void RepullNeverErasesAStagedWriteOnDirectoryStore()
        {
            var store = DirectoryStore(PathOf("repull-dir.cc"));
            using (var seed = store.Open())
                Assert.That(seed.Commit(batch => batch.Upsert(new Slow { Name = "slow", Blocks = true })), Is.True);
            using var cache = store.Open();
            var staged = new CultRecordKey("staged");

            using (var release = new ManualResetEventSlim())
            {
                Slow.Entered.Reset();
                Slow.Pause = release;
                try
                {
                    var pulling = Task.Run(() => cache.PullAllBackingStoresAsync());
                    Assert.That(Slow.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the pull never loaded a page");
                    var writer = StartWriter(() => cache.UpsertAsync(new Counter { Name = "staged" }, new CultRecordHandle<Counter>(staged)), () => false);
                    Slow.Pause = null;
                    release.Set();
                    Assert.That(pulling.Wait(TimeSpan.FromSeconds(10)) && writer(), Is.True);
                }
                finally
                {
                    Slow.Pause = null;
                    release.Set();
                }
            }

            Assert.That(cache.Get(staged), Is.Not.Null, "the re-pull erased a staged write from the cache");
            cache.FlushAllBackingStores();
            using var reader = store.Open();
            Assert.That(reader.Get(staged), Is.Not.Null, "the flush after the re-pull lost the staged write");
        }

        // Gate sharing is what makes a direct store call wait for its cache's I/O instead of interleaving with it.
        [Test]
        public void DirectCallsOnAnAttachedStoreShareTheCacheGate()
        {
            var path = PathOf("direct.cc");
            Create(SingleFile(path), 0);
            var store = new PausingStore(path);
            using var cache = new CultCache(Registry);
            cache.AddBackingStore(store);

            using var release = new ManualResetEventSlim();
            store.Pause = release;
            try
            {
                var pulling = Task.Run(() => cache.PullAllBackingStoresAsync());
                Assert.That(store.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the pull never read the file");
                var pushing = Task.Run(() => store.PushAll());
                Assert.That(pushing.Wait(200), Is.False, "a direct PushAll ran while the cache's pull held the gate");
                store.Pause = null;
                release.Set();
                Assert.That(Task.WaitAll(new[] { pulling, pushing }, TimeSpan.FromSeconds(10)), Is.True);
            }
            finally
            {
                // A failed assert must not leave the pull parked on the gate that Dispose needs.
                store.Pause = null;
                release.Set();
            }
        }

        [Test]
        public void ObserverDoesNotRunUnderTheGate()
        {
            var path = PathOf("observer.cc");
            using var cache = SingleFile(path).Open();
            var gated = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var seen = 0;
            using var subscription = cache.Watch<Counter>().Subscribe(change =>
            {
                Interlocked.Increment(ref seen);
                if (!Task.Run(() => cache.Get(change.Key)).Wait(TimeSpan.FromSeconds(5)))
                    gated.Enqueue(change.Key.Value);
            });

            cache.UpsertAsync(new Counter { Name = "upserted" }, new CultRecordHandle<Counter>(new CultRecordKey("upserted")));
            Assert.That(cache.Commit(batch => batch.Upsert(new Counter { Name = "committed" }, new CultRecordHandle<Counter>(new CultRecordKey("committed")))), Is.True);
            using (var other = SingleFile(path).Open())
                Assert.That(other.Commit(batch => batch.Upsert(new Counter { Name = "pulled" }, new CultRecordHandle<Counter>(new CultRecordKey("pulled")))), Is.True);
            cache.PullAllBackingStoresAsync();

            Assert.That(seen, Is.EqualTo(3));
            Assert.That(gated, Is.Empty, "an observer ran while the cache gate was held");
        }

        [Test]
        public void ThrowingOnUpdateHandlerDoesNotResurrectDeletedRecord()
        {
            var store = SingleFile(PathOf("resurrect.cc"));
            var doomed = new CultRecordKey("doomed");
            var later = new CultRecordKey("later");
            using (var seed = store.Open())
                Assert.That(seed.Commit(batch =>
                {
                    batch.Upsert(new Counter { Name = "doomed" }, new CultRecordHandle<Counter>(doomed));
                    batch.Upsert(new Counter { Name = "kept" });
                }), Is.True);
            using var a = store.Open();
            using (var b = store.Open())
            {
                Assert.That(b.Remove(doomed), Is.True);
                b.FlushAllBackingStores();
            }

            var throwing = true;
            a.OnUpdate += (_, _) =>
            {
                if (throwing)
                    throw new InvalidOperationException("observer failed");
            };
            Assert.That(() => a.PullAllBackingStoresAsync(), Throws.InvalidOperationException.With.Message.EqualTo("observer failed"));
            throwing = false;
            Assert.That(a.Get(doomed), Is.Null);

            a.UpsertAsync(new Counter { Name = "later" }, new CultRecordHandle<Counter>(later));
            a.FlushAllBackingStores();
            using var reader = store.Open();
            Assert.That(reader.Get(later), Is.Not.Null);
            Assert.That(reader.Get(doomed), Is.Null, "a throwing observer left the store holding a record the cache dropped");
        }

        // Runs write on its own thread and returns once it has reached the store or is blocked waiting; the result joins it.
        private static Func<bool> StartWriter(Action write, Func<bool> reachedStore)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { write(); }
                catch (Exception exception) { failure = exception; }
            });
            thread.Start();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!reachedStore() && thread.IsAlive && (thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                Assert.That(waited.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "the write neither reached the store nor blocked");
                Thread.Yield();
            }

            return () => thread.Join(TimeSpan.FromSeconds(10)) && failure == null;
        }

        [Test]
        public void UnconditionalCommitIsLastWriterWinsLikeFlush()
        {
            var committed = LastWriter(SingleFile(PathOf("commit.cc")), (cache, key) =>
                Assert.That(cache.Commit(batch => batch.Upsert(new Counter { Name = key.Value }, new CultRecordHandle<Counter>(key))), Is.True));
            var flushed = LastWriter(SingleFile(PathOf("flush.cc")), (cache, key) =>
            {
                cache.UpsertAsync(new Counter { Name = key.Value }, new CultRecordHandle<Counter>(key));
                cache.FlushAllBackingStores();
            });

            Assert.That(committed, Is.EqualTo(new[] { "a", "x" }), "an unconditional commit kept a record its cache never saw");
            Assert.That(committed, Is.EqualTo(flushed));

            // A directory store's flush lands staged keys onto the current manifest, and its commit does exactly the same.
            var directoryCommitted = LastWriter(DirectoryStore(PathOf("commit-dir.cc")), (cache, key) =>
                Assert.That(cache.Commit(batch => batch.Upsert(new Counter { Name = key.Value }, new CultRecordHandle<Counter>(key))), Is.True));
            var directoryFlushed = LastWriter(DirectoryStore(PathOf("flush-dir.cc")), (cache, key) =>
            {
                cache.UpsertAsync(new Counter { Name = key.Value }, new CultRecordHandle<Counter>(key));
                cache.FlushAllBackingStores();
            });
            Assert.That(directoryCommitted, Is.EqualTo(new[] { "a", "c", "x" }), "a directory commit dropped a key another writer added");
            Assert.That(directoryCommitted, Is.EqualTo(directoryFlushed));
        }

        [Test]
        public void UnconditionalCommitPersistsStagedWrites()
        {
            var path = PathOf("staged.cc");
            using var cache = SingleFile(path).Open();
            cache.UpsertAsync(new Counter { Name = "s" }, new CultRecordHandle<Counter>(new CultRecordKey("s")));
            Assert.That(cache.Commit(batch => batch.Upsert(new Counter { Name = "y" }, new CultRecordHandle<Counter>(new CultRecordKey("y")))), Is.True);

            Assert.That(cache.IsDirty, Is.False);
            using var reader = SingleFile(path).Open();
            Assert.That(reader.AllStoredDocuments.Select(stored => stored.Key.Value), Is.EqualTo(new[] { "s", "y" }));
        }

        [Test]
        public void OpeningACorruptStoreThrowsAndLeavesItUntouched()
        {
            foreach (var store in new[] { SingleFile(PathOf("corrupt.cc")), DirectoryStore(PathOf("corrupt-dir.cc")) })
            {
                File.WriteAllBytes(store.Path, new byte[] { 0xC1, 0x00, 0xFF, 0x13 });
                Assert.That(() => store.Open(), Throws.Exception, $"{store.Path}: a corrupt store opened");
                Assert.That(File.ReadAllBytes(store.Path), Is.EqualTo(new byte[] { 0xC1, 0x00, 0xFF, 0x13 }), $"{store.Path}: opening rewrote a corrupt store");
            }

            var unknown = PathOf("unknown-schema.cc");
            using (var writer = SingleFile(unknown).Open())
            {
                writer.UpsertAsync(new Tally { Name = "t" });
                writer.FlushAllBackingStores();
            }

            var bytes = File.ReadAllBytes(unknown);
            using var narrow = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(Counter) }));
            Assert.That(() => narrow.AddBackingStore(new SingleFileMessagePackBackingStore(unknown)), Throws.Exception, "a store with an unknown schema opened");
            Assert.That(File.ReadAllBytes(unknown), Is.EqualTo(bytes), "opening rewrote a store with an unknown schema");
        }

        // A pulls {a}; B commits c; A writes x. Returns the keys on disk afterwards.
        private static string[] LastWriter(StoreFactory store, Action<CultCache, CultRecordKey> write)
        {
            using (var seed = store.Open())
                Assert.That(seed.Commit(batch => batch.Upsert(new Counter { Name = "a" }, new CultRecordHandle<Counter>(new CultRecordKey("a")))), Is.True);
            using var a = store.Open();
            using (var b = store.Open())
                Assert.That(b.Commit(batch => batch.Upsert(new Counter { Name = "c" }, new CultRecordHandle<Counter>(new CultRecordKey("c")))), Is.True);
            write(a, new CultRecordKey("x"));
            using var reader = store.Open();
            return reader.AllStoredDocuments.Select(stored => stored.Key.Value).ToArray();
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

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(Counter), typeof(Tally), typeof(Slow) });

        private static StoreFactory SingleFile(string path) => new(path, () => new SingleFileMessagePackBackingStore(path));

        private static StoreFactory DirectoryStore(string path) => new(path, () => new DirectoryMessagePackBackingStore(path));

        // Pauses a pull between reading the file and handing the records to the cache.
        private sealed class PausingStore : SingleFileMessagePackBackingStore
        {
            public PausingStore(string path) : base(path)
            {
            }

            public ManualResetEventSlim? Pause;
            public readonly ManualResetEventSlim Entered = new();
            public readonly ManualResetEventSlim Pushed = new();

            public override void Push(CultStoredDocument entry)
            {
                base.Push(entry);
                Pushed.Set();
            }

            protected override CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data)
            {
                var snapshot = base.DeserializeSnapshot(data);
                if (Pause is { } pause)
                {
                    Entered.Set();
                    pause.Wait();
                }

                return snapshot;
            }
        }

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

        // Loading a record with Blocks set waits on Pause, which parks a directory pull inside its page load.
        [CultDocument("tests.conditional_slow", "tests.conditional_slow.v1")]
        internal sealed class Slow
        {
            internal static volatile ManualResetEventSlim? Pause;
            internal static readonly ManualResetEventSlim Entered = new();
            private bool _blocks;

            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public bool Blocks
            {
                get => _blocks;
                set
                {
                    _blocks = value;
                    if (value && Pause is { } pause)
                    {
                        Entered.Set();
                        pause.Wait();
                    }
                }
            }
        }
    }
}
