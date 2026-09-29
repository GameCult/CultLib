#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using R3;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    // The database's shard log is written by a cache journal, under the cache gate, in cache order. A primary that
    // cannot log a committed change burns that change's sequence and compacts past it, so the log can lag the cache but
    // never lead it, and every replica behind the hole resynchronizes by snapshot.
    public sealed class CultNetJournalTests
    {
        private const string ShardId = "log-shard";

        private static readonly CultRecordKey One = new("tests:journal:one");
        private static readonly CultRecordKey Two = new("tests:journal:two");
        private static readonly CultRecordKey Three = new("tests:journal:three");

        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };

        private static string SchemaId(CultCache cache) => cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;

        private static CultNetShardDescriptor Shard(CultCache cache, bool primary) =>
            new(ShardId, "primary", epoch: 1, isPrimary: primary, schemaIds: [SchemaId(cache)]);

        private static CultNetDatabase Database(CultCache cache, bool primary, FlakyLogStore? store) =>
            new(cache, new CultNetDatabaseOptions { Shards = [Shard(cache, primary)], MutationLogStore = store });

        private static string KeyOfChange(object change) => ((dynamic)change).Key.Value;

        private static List<string> Record(CultNetDatabase database)
        {
            var seen = new List<string>();
            database.WatchAllChanges().Subscribe(change => seen.Add(KeyOfChange(change)));
            return seen;
        }

        private static long[] Sequences(FlakyLogStore store) => store.Read(ShardId).Select(entry => entry.Sequence).ToArray();

        // A store whose refusals and side effects the test scripts. One shard per store.
        private sealed class FlakyLogStore : ICultNetShardMutationLogStore
        {
            private readonly object _gate = new();
            private readonly SortedDictionary<long, CultNetShardLogEntryMessage> _entries = new();
            private long _compactedThrough;

            public Func<long, bool> RefuseAppend { get; set; } = _ => false;
            public bool RefuseCompact { get; set; }
            public Action? OnRead { get; set; }
            public Action? OnAppend { get; set; }

            public IReadOnlyList<CultNetShardLogEntryMessage> Read(string shardId, long afterSequence = 0, int? limit = null)
            {
                OnRead?.Invoke();
                lock (_gate)
                {
                    var entries = _entries.Values.Where(entry => entry.Sequence > afterSequence);
                    return (limit.HasValue ? entries.Take(limit.Value) : entries).ToArray();
                }
            }

            public void Append(string shardId, CultNetShardLogEntryMessage entry)
            {
                OnAppend?.Invoke();
                lock (_gate)
                {
                    if (RefuseAppend(entry.Sequence))
                        throw new IOException($"The log refused sequence {entry.Sequence}.");
                    _entries[entry.Sequence] = entry;
                }
            }

            public long GetCompactedThrough(string shardId)
            {
                lock (_gate)
                    return _compactedThrough;
            }

            public void CompactThrough(string shardId, long sequence)
            {
                lock (_gate)
                {
                    if (RefuseCompact)
                        throw new IOException("The log refused to compact.");
                    _compactedThrough = Math.Max(_compactedThrough, sequence);
                    foreach (var stale in _entries.Keys.Where(key => key <= sequence).ToArray())
                        _entries.Remove(stale);
                }
            }
        }

        // The primary as a replica meets it: the database server's own log answer, and the database's own snapshot.
        private sealed class PrimaryPeer : ICultNetShardLogFetcher, ICultNetShardSnapshotFetcher, IDisposable
        {
            private readonly Server _server;
            private readonly CultNetDatabaseServer _databaseServer;
            private readonly CultNetDatabase _database;

            public PrimaryPeer(CultCache cache, CultNetDatabase database)
            {
                _database = database;
                _server = new Server(cache, ServerSecurityOptions.Development());
                _databaseServer = new CultNetDatabaseServer(_server, database);
            }

            public CultNetShardLogResponseMessage Log(long afterSequence) =>
                _databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
                {
                    MessageId = "pull",
                    ShardId = ShardId,
                    ShardEpoch = 1,
                    AfterSequence = afterSequence
                });

            public Task<CultNetShardLogResponseMessage> FetchAsync(CultNetShardDescriptor shard, long afterSequence, int? limit = null) =>
                Task.FromResult(Log(afterSequence));

            public Task<CultNetSnapshotResponseRawMessage> FetchAsync(CultNetShardDescriptor shard) =>
                Task.FromResult(_database.CreateShardSnapshotResponse(_database.Shards[0], "snapshot"));

            public void Dispose()
            {
                _databaseServer.Dispose();
                _server.Dispose();
            }
        }

        private static CultNetShardReplicator Replicator(CultNetDatabase replica, PrimaryPeer peer) =>
            new(replica, new CultNetShardReplicatorOptions { Fetcher = peer, SnapshotFetcher = peer });

        private static string? TextOf(CultCache cache, CultRecordKey key) => cache.Get<NetworkSchemaNote>(key)?.Text;

        [Test]
        public async Task ARefusedPrimaryAppendPublishesTheChangeTheWriterHearsOfItAndAReplicaBehindItResyncs()
        {
            var store = new FlakyLogStore { RefuseAppend = sequence => sequence == 2 };
            var cache = new CultCache();
            var primary = Database(cache, primary: true, store);
            using var peer = new PrimaryPeer(cache, primary);
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, store: null);
            using var replicator = Replicator(replica, peer);
            await primary.PutAsync(One, Note("one"));
            Assert.That(await replicator.PullOnceAsync(ShardId), Is.EqualTo(1));
            var published = Record(primary);

            var failure = Assert.ThrowsAsync<CultNetShardLogException>(async () => await primary.PutAsync(Two, Note("two")))!;

            Assert.That(failure.ShardId, Is.EqualTo(ShardId));
            Assert.That(failure.Sequence, Is.EqualTo(2));
            Assert.That(failure, Is.Not.InstanceOf<InvalidOperationException>(), "a committed write must not read as a refused one");
            Assert.That(published, Is.EqualTo(new[] { Two.Value }), "the change was published before the writer heard");
            Assert.That(TextOf(cache, Two), Is.EqualTo("two"), "the commit stands");
            Assert.That(primary.LastWriteSequence(SchemaId(cache), Two), Is.EqualTo(2L), "the row was written at the burned sequence");
            await primary.PutAsync(Three, Note("three"));
            Assert.That(Sequences(store), Is.EqualTo(new[] { 3L }), "sequence 2 stays a hole below the compaction point");
            Assert.That(peer.Log(afterSequence: 1).Reason, Is.EqualTo("compacted_history"));

            Assert.That(await replicator.PullOnceAsync(ShardId), Is.EqualTo(3));
            Assert.That(new[] { One, Two, Three }.Select(key => TextOf(replicaCache, key)), Is.EqualTo(new[] { "one", "two", "three" }));
            Assert.That(await replicator.PullOnceAsync(ShardId), Is.EqualTo(3), "a later pull is a quiet no-op");
        }

        [Test]
        public async Task ARestartAfterARefusedTailAppendReusesNoSequence()
        {
            var store = new FlakyLogStore { RefuseAppend = sequence => sequence == 2 };
            var cache = new CultCache();
            var primary = Database(cache, primary: true, store);
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, store: null);
            using (var before = new PrimaryPeer(cache, primary))
            {
                await primary.PutAsync(One, Note("one"));
                using var replicator = Replicator(replica, before);
                await replicator.PullOnceAsync(ShardId);
            }

            Assert.ThrowsAsync<CultNetShardLogException>(async () => await primary.PutAsync(Two, Note("two")));
            primary.Dispose();

            var restarted = Database(cache, primary: true, store);
            await restarted.PutAsync(Three, Note("three"));

            Assert.That(Sequences(store), Is.EqualTo(new[] { 3L }), "the burned sequence was not minted again");
            using var after = new PrimaryPeer(cache, restarted);
            using var resumed = Replicator(replica, after);
            await resumed.PullOnceAsync(ShardId);
            Assert.That(new[] { One, Two, Three }.Select(key => TextOf(replicaCache, key)), Is.EqualTo(new[] { "one", "two", "three" }));
        }

        [Test]
        public async Task AStoreThatRefusesTheAppendAndTheCompactionStillCompactsInMemoryAndNamesBothFailures()
        {
            var store = new FlakyLogStore { RefuseAppend = sequence => sequence == 2, RefuseCompact = true };
            var cache = new CultCache();
            var primary = Database(cache, primary: true, store);
            using var peer = new PrimaryPeer(cache, primary);
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, store: null);
            using var replicator = Replicator(replica, peer);
            await primary.PutAsync(One, Note("one"));
            await replicator.PullOnceAsync(ShardId);

            var failure = Assert.ThrowsAsync<CultNetShardLogException>(async () => await primary.PutAsync(Two, Note("two")))!;

            Assert.That(failure.InnerException, Is.InstanceOf<AggregateException>());
            Assert.That(((AggregateException)failure.InnerException!).InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(primary.GetCompactedMutationLogSequence(ShardId), Is.EqualTo(2), "the primary's own floor covers a hole its store never compacted");
            Assert.That(primary.GetLatestMutationLogSequence(ShardId), Is.EqualTo(2), "the latest sequence is never below the floor");
            Assert.That(peer.Log(afterSequence: 1).Reason, Is.EqualTo("compacted_history"));
            Assert.That(await replicator.PullOnceAsync(ShardId), Is.EqualTo(2));
            Assert.That(TextOf(replicaCache, Two), Is.EqualTo("two"));
        }

        [Test]
        public async Task CompactingAndRestartingResumesTheNextSequenceAboveTheCompactionPoint()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var primary = Database(cache, primary: true, store);
            await primary.PutAsync(One, Note("one"));
            await primary.PutAsync(Two, Note("two"));
            store.CompactThrough(ShardId, 2);
            primary.Dispose();

            var restarted = Database(cache, primary: true, store);
            Assert.That(restarted.GetLatestMutationLogSequence(ShardId), Is.EqualTo(2), "the latest sequence is never below the compaction point");
            await restarted.PutAsync(Three, Note("three"));

            Assert.That(Sequences(store), Is.EqualTo(new[] { 3L }));
            Assert.That(restarted.GetLatestMutationLogSequence(ShardId), Is.EqualTo(3));
        }

        [Test]
        public async Task ARefusedReplicaAppendRecordsNothingAdvancesNoCursorAndTheRetryConverges()
        {
            var refusals = 0;
            var replicaStore = new FlakyLogStore { RefuseAppend = sequence => sequence == 1 && refusals++ == 0 };
            var cache = new CultCache();
            var primary = Database(cache, primary: true, new FlakyLogStore());
            await primary.PutAsync(One, Note("one"));
            await primary.PutAsync(Two, Note("two"));
            using var peer = new PrimaryPeer(cache, primary);
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, replicaStore);
            using var replicator = Replicator(replica, peer);

            Assert.ThrowsAsync<IOException>(async () => await replicator.PullOnceAsync(ShardId));

            Assert.That(replica.GetAppliedShardSequence(ShardId), Is.EqualTo(0));
            Assert.That(replica.GetMutationLog(ShardId), Is.Empty, "memory never holds what the durable log refused");
            Assert.That(await replicator.PullOnceAsync(ShardId), Is.EqualTo(2));
            Assert.That(replica.GetMutationLog(ShardId).Select(entry => entry.Sequence), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(Sequences(replicaStore), Is.EqualTo(new[] { 1L, 2L }));
        }

        [Test]
        public async Task AReentrantShardLogApplyFromAHandlerKeepsTheReplicaLogInPrimaryOrder()
        {
            var cache = new CultCache();
            var primary = Database(cache, primary: true, new FlakyLogStore());
            await primary.PutAsync(One, Note("one"));
            await primary.PutAsync(Two, Note("two"));
            await primary.PutAsync(Three, Note("three"));
            var response = new CultNetShardLogResponseMessage
            {
                ShardId = ShardId,
                ShardEpoch = 1,
                Entries = primary.GetMutationLogMessages(ShardId).ToArray()
            };
            var replicaStore = new FlakyLogStore();
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, replicaStore);
            var nested = false;
            long nestedApplied = 0;
            Exception? nestedFailure = null;
            replica.Watch<NetworkSchemaNote>().Where(change => change.Key.Equals(One)).Subscribe(_ =>
            {
                if (nested) return;
                nested = true;
                try
                {
                    nestedApplied = replica.ApplyShardLogResponseAsync(response).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    nestedFailure = exception;
                }
            });

            await replica.ApplyShardLogResponseAsync(response);

            Assert.That(nested, Is.True);
            Assert.That(nestedFailure, Is.Null, "the nested apply re-records entries the outer apply already logged");
            Assert.That(nestedApplied, Is.EqualTo(3));
            Assert.That(replica.GetMutationLog(ShardId).Select(entry => entry.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L }));
            Assert.That(Sequences(replicaStore), Is.EqualTo(new[] { 1L, 2L, 3L }));
            Assert.That(replica.GetAppliedShardSequence(ShardId), Is.EqualTo(3));
        }

        [Test]
        public async Task AReentrantDatabaseWriteFromAHandlerIsLoggedWithItsContextAndPublishedOnce()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var database = Database(cache, primary: true, store);
            var message = database.Documents.CreateRawDocumentPutMessage(
                "reentrant-two", new CultRecordHandle<NetworkSchemaNote>(Two), Note("two"));
            var published = Record(database);
            database.Watch<NetworkSchemaNote>().Where(change => change.Key.Equals(One)).Subscribe(_ =>
                database.ApplyPutAsync(message).GetAwaiter().GetResult());

            await database.PutAsync(One, Note("one"));

            Assert.That(published, Is.EqualTo(new[] { One.Value, Two.Value }));
            Assert.That(database.GetMutationLog(ShardId).Select(entry => entry.Key.Value), Is.EqualTo(new[] { One.Value, Two.Value }));
            Assert.That(store.Read(ShardId).Single(entry => entry.Sequence == 2).Put!.MessageId, Is.EqualTo("reentrant-two"),
                "the nested write's own wire message is what the log stored");
        }

        [Test]
        public async Task ALogStoreThatWritesTheCacheFailsThroughTheReentryGuardAndTheAdmissionStands()
        {
            var cache = new CultCache();
            var store = new FlakyLogStore();
            store.OnAppend = () =>
            {
                store.OnAppend = null;
                cache.UpsertAsync(Note("from the store"), new CultRecordHandle<NetworkSchemaNote>(Three)).GetAwaiter().GetResult();
            };
            var database = Database(cache, primary: true, store);
            var published = Record(database);

            var failure = Assert.ThrowsAsync<CultNetShardLogException>(async () => await database.PutAsync(One, Note("one")))!;

            Assert.That(failure.InnerException, Is.InstanceOf<InvalidOperationException>(), "the cache's guard refused the store's write");
            Assert.That(TextOf(cache, One), Is.EqualTo("one"));
            Assert.That(TextOf(cache, Three), Is.Null);
            Assert.That(published, Is.EqualTo(new[] { One.Value }));
        }

        [Test]
        public void SeveralRefusedAppendsInOneAdmissionAreAllPublishedAndAllReachTheWriter()
        {
            var store = new FlakyLogStore { RefuseAppend = _ => true };
            var cache = new CultCache();
            var database = Database(cache, primary: true, store);
            var published = Record(database);

            var failure = Assert.Throws<AggregateException>(() => cache.Commit(batch =>
            {
                batch.Upsert(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One));
                batch.Upsert(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
            }))!;

            Assert.That(failure.InnerExceptions.OfType<CultNetShardLogException>().Select(exception => exception.Sequence), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(published, Is.EqualTo(new[] { One.Value, Two.Value }));
            Assert.That(database.GetCompactedMutationLogSequence(ShardId), Is.EqualTo(2));
        }

        [Test]
        public async Task APullOnAPrimaryIsLoggedSoAReplicaConverges()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultnet-journal-pull-{Guid.NewGuid():N}.cc");
            try
            {
                var writer = new CultCache();
                writer.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await writer.UpsertAsync(Note("seed"), new CultRecordHandle<NetworkSchemaNote>(One));
                writer.FlushAllBackingStores();
                var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                var primary = Database(cache, primary: true, new FlakyLogStore());
                using var peer = new PrimaryPeer(cache, primary);
                var replicaCache = new CultCache();
                var replica = Database(replicaCache, primary: false, store: null);
                using var replicator = Replicator(replica, peer);

                await writer.UpsertAsync(Note("pulled"), new CultRecordHandle<NetworkSchemaNote>(Two));
                writer.FlushAllBackingStores();
                await cache.PullAllBackingStoresAsync();
                await replicator.PullOnceAsync(ShardId);

                Assert.That(primary.GetMutationLog(ShardId).Select(entry => entry.Key.Value), Does.Contain(Two.Value));
                Assert.That(TextOf(replicaCache, Two), Is.EqualTo("pulled"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
            }
        }

        [Test]
        public async Task AStoreAttachedAfterTheDatabaseExistsHasItsHydrationLogged()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultnet-journal-attach-{Guid.NewGuid():N}.cc");
            try
            {
                var writer = new CultCache();
                writer.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await writer.UpsertAsync(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One));
                await writer.UpsertAsync(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
                writer.FlushAllBackingStores();
                var cache = new CultCache();
                var database = Database(cache, primary: true, new FlakyLogStore());

                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));

                Assert.That(database.GetMutationLog(ShardId).Select(entry => entry.Key.Value), Is.EquivalentTo(new[] { One.Value, Two.Value }));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
            }
        }

        [Test]
        public async Task ASnapshotReadsItsLogSequenceBeforeItsDocumentsSoARacingWriteConverges()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var primary = Database(cache, primary: true, store);
            await primary.PutAsync(One, Note("one"));
            using var peer = new PrimaryPeer(cache, primary);
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, store: null);
            using var replicator = Replicator(replica, peer);
            store.OnRead = () =>
            {
                store.OnRead = null;
                primary.PutAsync(Two, Note("two")).GetAwaiter().GetResult();
            };

            var snapshot = primary.CreateShardSnapshotResponse(primary.Shards[0], "snapshot");
            await replica.ApplyShardSnapshotResponseAsync(replica.Shards[0], snapshot);
            await replicator.PullOnceAsync(ShardId);

            Assert.That(TextOf(replicaCache, Two), Is.EqualTo("two"), "a write racing the snapshot reaches the replica by one road or the other");
        }

        [Test]
        public async Task ASnapshotAppliedToAPrimaryShardLogsNothing()
        {
            var cache = new CultCache();
            await cache.UpsertAsync(Note("stale"), new CultRecordHandle<NetworkSchemaNote>(One));
            var database = Database(cache, primary: true, new FlakyLogStore());
            var put = database.Documents.CreateRawDocumentPutMessage("snapshot-two", new CultRecordHandle<NetworkSchemaNote>(Two), Note("two"));

            await database.ApplyShardSnapshotResponseAsync(database.Shards[0], new CultNetSnapshotResponseRawMessage
            {
                ShardId = ShardId,
                ShardEpoch = 1,
                Documents = [put.Document!],
                ShardLogSequence = 5
            });

            Assert.That(TextOf(cache, One), Is.Null);
            Assert.That(TextOf(cache, Two), Is.EqualTo("two"));
            Assert.That(database.GetMutationLog(ShardId), Is.Empty, "the snapshot's removal and its put are both a resync, not a commit");
        }
    }
}
