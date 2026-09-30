#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using NUnit.Framework;
using R3;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    // Soul pass on Cut 4 (scratch; not for merge). Category Finding asserts the invariant Soul expects and is expected to
    // fail on 13072fc5; Pin asserts a promise that should hold; Measure reports numbers.
    public sealed class SoulP4ProbeTests
    {
        private const string ShardId = "soul-shard";
        private static readonly CultRecordKey One = new("soul:p4:one");
        private static readonly CultRecordKey Two = new("soul:p4:two");
        private static readonly CultRecordKey Three = new("soul:p4:three");

        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };
        private static string SchemaId(CultCache cache) => cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;

        private static CultNetDatabase Db(CultCache cache, bool primary, ICultNetShardMutationLogStore? store) =>
            new(cache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(ShardId, "primary", epoch: 1, isPrimary: primary, schemaIds: [SchemaId(cache)])],
                MutationLogStore = store
            });

        private sealed class Store : ICultNetShardMutationLogStore
        {
            private readonly object _gate = new();
            private readonly SortedDictionary<long, CultNetShardLogEntryMessage> _entries = new();
            private long _compacted;
            public Func<long, bool> RefuseAppend { get; set; } = _ => false;
            public bool RefuseCompact { get; set; }
            public int AppendDelayMs { get; set; }
            public ManualResetEventSlim InAppend { get; } = new();

            public IReadOnlyList<CultNetShardLogEntryMessage> Read(string shardId, long afterSequence = 0, int? limit = null)
            {
                lock (_gate)
                {
                    var e = _entries.Values.Where(x => x.Sequence > afterSequence);
                    return (limit.HasValue ? e.Take(limit.Value) : e).ToArray();
                }
            }

            public void Append(string shardId, CultNetShardLogEntryMessage entry)
            {
                InAppend.Set();
                if (AppendDelayMs > 0) Thread.Sleep(AppendDelayMs);
                lock (_gate)
                {
                    if (RefuseAppend(entry.Sequence)) throw new IOException($"refused {entry.Sequence}");
                    _entries[entry.Sequence] = entry;
                }
            }

            public long GetCompactedThrough(string shardId) { lock (_gate) return _compacted; }

            public void CompactThrough(string shardId, long sequence)
            {
                lock (_gate)
                {
                    if (RefuseCompact) throw new IOException("refused compact");
                    _compacted = Math.Max(_compacted, sequence);
                    foreach (var k in _entries.Keys.Where(k => k <= sequence).ToArray()) _entries.Remove(k);
                }
            }
        }

        private sealed class Peer : ICultNetShardLogFetcher, ICultNetShardSnapshotFetcher, IDisposable
        {
            private readonly Server _server;
            private readonly CultNetDatabaseServer _dbServer;
            private readonly CultNetDatabase _db;
            public Peer(CultCache cache, CultNetDatabase db)
            {
                _db = db;
                _server = new Server(cache, ServerSecurityOptions.Development());
                _dbServer = new CultNetDatabaseServer(_server, db);
            }
            public CultNetShardLogResponseMessage Log(long after) => _dbServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            { MessageId = "pull", ShardId = ShardId, ShardEpoch = 1, AfterSequence = after });
            public Task<CultNetShardLogResponseMessage> FetchAsync(CultNetShardDescriptor shard, long afterSequence, int? limit = null) => Task.FromResult(Log(afterSequence));
            public Task<CultNetSnapshotResponseRawMessage> FetchAsync(CultNetShardDescriptor shard) => Task.FromResult(_db.CreateShardSnapshotResponse(_db.Shards[0], "snap"));
            public void Dispose() { _dbServer.Dispose(); _server.Dispose(); }
        }

        private static int StashCount(CultNetDatabase db)
        {
            var field = typeof(CultNetDatabase).GetField("_stash", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var stash = (System.Collections.ICollection)field.GetValue(db)!;
            lock (typeof(CultNetDatabase).GetField("_logGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!)
                return stash.Count;
        }

        // F1: the recorded residual is "tail reuse". A hole in the middle of the log after a double refusal and a restart
        // strands a replica behind it for good: the restarted primary has no floor, the store serves S+1 after S-1.
        [Test, Category("SoulFinding")]
        public async Task F1_MidLogHoleAfterDoubleRefusalAndRestartStrandsAReplica()
        {
            var store = new Store { RefuseAppend = s => s == 2, RefuseCompact = true };
            var cache = new CultCache();
            var primary = Db(cache, true, store);
            var replicaCache = new CultCache();
            var replica = Db(replicaCache, false, null);
            await primary.PutAsync(One, Note("one"));
            using (var p0 = new Peer(cache, primary))
            using (var r0 = new CultNetShardReplicator(replica, new CultNetShardReplicatorOptions { Fetcher = p0, SnapshotFetcher = p0 }))
                await r0.PullOnceAsync(ShardId);
            try { await primary.PutAsync(Two, Note("two")); } catch (CultNetShardLogException) { }
            store.RefuseCompact = false;
            await primary.PutAsync(Three, Note("three"));
            primary.Dispose();

            var restarted = Db(cache, true, store);
            using var peer = new Peer(cache, restarted);
            using var replicator = new CultNetShardReplicator(replica, new CultNetShardReplicatorOptions { Fetcher = peer, SnapshotFetcher = peer });
            var errors = new List<string>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { await replicator.PullOnceAsync(ShardId); }
                catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
            }

            TestContext.Out.WriteLine($"store={string.Join(",", store.Read(ShardId).Select(e => e.Sequence))} floor={restarted.GetCompactedMutationLogSequence(ShardId)} errors=[{string.Join(" | ", errors)}]");
            Assert.That(new[] { One, Two, Three }.Select(k => replicaCache.Get<NetworkSchemaNote>(k)?.Text), Is.EqualTo(new[] { "one", "two", "three" }));
        }

        // F2: Q-P5 A says the writer gets a typed CultNetShardLogException. A commit of two changes on a refusing store
        // hands the writer an AggregateException; a writer's catch (CultNetShardLogException) misses it.
        [Test, Category("SoulFinding")]
        public void F2_ABatchOnARefusingStoreReachesTheWriterAsTheTypedException()
        {
            var store = new Store { RefuseAppend = _ => true };
            var cache = new CultCache();
            _ = Db(cache, true, store);
            Exception? caught = null;
            try
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One));
                    batch.Upsert(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
                });
            }
            catch (Exception e) { caught = e; }
            TestContext.Out.WriteLine($"writer caught {caught?.GetType().FullName}");
            Assert.That(caught, Is.InstanceOf<CultNetShardLogException>());
        }

        // F3: main publishes PutPredictedAsync as Predicted and records the prediction whatever instance it is given. The
        // shape rule refuses a door whose instance is already the cached one, so a mutate-then-predict is published as
        // Updated, is logged on a primary, and its reconciliation is published as Updated.
        [Test, Category("SoulFinding")]
        public async Task F3_APredictionOfTheCachedInstanceIsPublishedAsAPrediction()
        {
            var cache = new CultCache();
            var db = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var shard = db.Shards[0];
            var kinds = new List<CultNetDatabaseChangeKind>();
            db.Watch<NetworkSchemaNote>().Subscribe(c => kinds.Add(c.Kind));
            await db.PutPredictedAsync(One, Note("p1"));
            var held = cache.Get<NetworkSchemaNote>(One)!;
            held.Text = "p2";
            await db.PutPredictedAsync(One, held);
            var auth = db.Documents.CreateRawDocumentPutMessage("a", new CultRecordHandle<NetworkSchemaNote>(One), Note("auth"));
            auth.ShardId = shard.ShardId;
            auth.ShardEpoch = shard.Epoch;
            await db.ApplyPutAsync(auth);
            TestContext.Out.WriteLine($"kinds=[{string.Join(",", kinds)}] log=[{string.Join(",", db.GetMutationLog(shard.ShardId).Select(e => e.Kind))}]");
            Assert.That(kinds, Is.EqualTo(new[] { CultNetDatabaseChangeKind.Predicted, CultNetDatabaseChangeKind.Predicted, CultNetDatabaseChangeKind.Reconciled }));
            Assert.That(db.GetMutationLog(shard.ShardId).Select(e => e.Kind), Is.EqualTo(new[] { CultNetDatabaseChangeKind.Updated }));
        }

        // F4: the journal holds the cache gate across the log append, and cache reads take the gate, so a reader of an
        // unrelated key waits for the whole append.
        [Test, Category("SoulFinding"), Ignore("Accepted follow-up F4: a log append runs under the cache gate, so readers wait; documented on ICultNetShardMutationLogStore.")]
        public async Task F4_AReaderOfAnotherKeyDoesNotWaitForALogAppend()
        {
            var store = new Store { AppendDelayMs = 400 };
            var cache = new CultCache();
            await cache.UpsertAsync(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
            var db = Db(cache, true, store);
            var writer = Task.Run(() => cache.UpsertAsync(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One)).GetAwaiter().GetResult());
            Assert.That(store.InAppend.Wait(5000), Is.True);
            var clock = Stopwatch.StartNew();
            _ = cache.Get<NetworkSchemaNote>(Two);
            var waited = clock.Elapsed.TotalMilliseconds;
            await writer;
            TestContext.Out.WriteLine($"reader of an unrelated key waited {waited:0.0} ms for a 400 ms append");
            Assert.That(waited, Is.LessThan(100));
        }

        // P1: the constructor registers the observer before the journal, so a change stashed by the journal always finds
        // its publisher, even while another thread writes the cache during construction.
        [Test, Category("SoulPin")]
        public void P1_ConstructionUnderConcurrentWritesOrphansNoStash()
        {
            var orphans = 0;
            for (var round = 0; round < 200; round++)
            {
                var cache = new CultCache();
                using var stop = new CancellationTokenSource();
                var writer = Task.Run(() =>
                {
                    var i = 0;
                    while (!stop.IsCancellationRequested)
                        cache.UpsertAsync(Note("w" + i++), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul:p4:race:" + (i % 4)))).GetAwaiter().GetResult();
                });
                Thread.SpinWait(200 * (round % 7));
                var db = Db(cache, true, null);
                Thread.SpinWait(2000);
                stop.Cancel();
                writer.GetAwaiter().GetResult();
                orphans += StashCount(db);
                db.Dispose();
            }

            Assert.That(orphans, Is.EqualTo(0), "no stash entry outlives its admission");
        }

        // P2: every stash entry is removed by its publication.
        [Test, Category("SoulPin")]
        public async Task P2_TheStashDrainsAfterEveryWrite()
        {
            var cache = new CultCache();
            var db = Db(cache, true, new Store());
            await db.PutAsync(One, Note("one"));
            await cache.UpsertAsync(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
            cache.Commit(batch => batch.Upsert(Note("three"), new CultRecordHandle<NetworkSchemaNote>(Three)));
            await db.DeleteAsync<NetworkSchemaNote>(One);
            Assert.That(StashCount(db), Is.EqualTo(0));
        }

        // P3: a replica never applies an entry past a hole. The primary burns 2 with compaction refused, then logs 3; a
        // replica at 1 is answered compacted_history and resyncs, never applying 3 on top of 1.
        [Test, Category("SoulPin")]
        public async Task P3_AReplicaAtTheHoleResyncsRatherThanSkippingIt()
        {
            var store = new Store { RefuseAppend = s => s == 2, RefuseCompact = true };
            var cache = new CultCache();
            var primary = Db(cache, true, store);
            using var peer = new Peer(cache, primary);
            var replicaCache = new CultCache();
            var replica = Db(replicaCache, false, null);
            using var replicator = new CultNetShardReplicator(replica, new CultNetShardReplicatorOptions { Fetcher = peer, SnapshotFetcher = peer });
            await primary.PutAsync(One, Note("one"));
            await replicator.PullOnceAsync(ShardId);
            try { await primary.PutAsync(Two, Note("two")); } catch (CultNetShardLogException) { }
            await primary.PutAsync(Three, Note("three"));
            Assert.That(peer.Log(1).Reason, Is.EqualTo("compacted_history"));
            var applied = await replicator.PullOnceAsync(ShardId);
            Assert.That(applied, Is.EqualTo(3));
            Assert.That(new[] { One, Two, Three }.Select(k => replicaCache.Get<NetworkSchemaNote>(k)?.Text), Is.EqualTo(new[] { "one", "two", "three" }));
        }

        [Test, Category("SoulMeasure"), Explicit("Measurement, no assertion; writes a log file.")]
        public async Task M1_BareWriteLatencyWithTheFileLogStore()
        {
            var root = Path.Combine(Path.GetTempPath(), "soulp4-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new CultCache();
                _ = Db(cache, true, new CultNetFileShardMutationLogStore(root));
                var times = new List<double>();
                for (var i = 0; i < 1000; i++)
                {
                    var clock = Stopwatch.StartNew();
                    await cache.UpsertAsync(Note("n" + i), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul:p4:lat:" + i)));
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }

                TestContext.Out.WriteLine($"file store bare write ms: first100 avg {times.Take(100).Average():0.000}, 400-500 avg {times.Skip(400).Take(100).Average():0.000}, last100 avg {times.Skip(900).Average():0.000}, max {times.Max():0.0}");
                var bare = new CultCache();
                var t2 = new List<double>();
                for (var i = 0; i < 1000; i++)
                {
                    var clock = Stopwatch.StartNew();
                    await bare.UpsertAsync(Note("n" + i), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul:p4:lat:" + i)));
                    t2.Add(clock.Elapsed.TotalMilliseconds);
                }

                TestContext.Out.WriteLine($"no database bare write ms: last100 avg {t2.Skip(900).Average():0.000}");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
