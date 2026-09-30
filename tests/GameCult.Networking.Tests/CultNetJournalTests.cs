#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

            Assert.That(failure.Burned, Is.EqualTo(new[] { new CultNetBurnedSequence(ShardId, 2) }));
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
            Assert.That(replica.CurrentAsOf(ShardId), Is.EqualTo(3UL), "the replica's own watermark follows the primary's sequences");
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

            var failure = Assert.Throws<CultNetShardLogException>(() => cache.Commit(batch =>
            {
                batch.Upsert(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One));
                batch.Upsert(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
            }))!;

            Assert.That(failure.Burned, Is.EqualTo(new[] { new CultNetBurnedSequence(ShardId, 1), new CultNetBurnedSequence(ShardId, 2) }), "one exception carries every burned sequence");
            Assert.That(((AggregateException)failure.InnerException!).InnerExceptions, Has.Count.EqualTo(2), "and every store failure");
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

        [Test]
        public async Task ARestartOverARetainedLogResumesAboveItsHighestSequence()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var primary = Database(cache, primary: true, store);
            await primary.PutAsync(One, Note("one"));
            await primary.PutAsync(Two, Note("two"));
            primary.Dispose();

            var restarted = Database(cache, primary: true, store);
            Assert.That(restarted.GetLatestMutationLogSequence(ShardId), Is.EqualTo(2), "a restarted database reads its latest sequence from the durable log");
            await restarted.PutAsync(Three, Note("three"));

            Assert.That(Sequences(store), Is.EqualTo(new[] { 1L, 2L, 3L }));
        }

        [Test]
        public async Task APutAndDeleteDoorLogTheirOwnWireMessagesAndChangeKinds()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var database = Database(cache, primary: true, store);
            var added = database.Documents.CreateRawDocumentPutMessage("put-added", new CultRecordHandle<NetworkSchemaNote>(One), Note("one"));
            var updated = database.Documents.CreateRawDocumentPutMessage("put-updated", new CultRecordHandle<NetworkSchemaNote>(One), Note("uno"));

            await database.ApplyPutAsync(added);
            await database.ApplyPutAsync(updated);
            await database.ApplyDeleteAsync(new CultNetDocumentDeleteMessage { MessageId = "delete-one", SchemaId = SchemaId(cache), RecordKey = One.Value });

            var entries = store.Read(ShardId);
            Assert.That(entries.Select(entry => entry.ChangeKind), Is.EqualTo(new[] { "added", "updated", "removed" }));
            Assert.That(entries[0].Put!.MessageId, Is.EqualTo("put-added"));
            Assert.That(entries[1].Put!.MessageId, Is.EqualTo("put-updated"));
            Assert.That(entries[2].Delete!.MessageId, Is.EqualTo("delete-one"));
            Assert.That(DateTimeOffset.TryParse(entries[0].CommittedAt, out _), Is.True, "a primary stamps its entries");
        }

        [Test]
        public async Task AReconciledPutIsLoggedAsTheUpdateItIs()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var shard = database.Shards[0];
            var message = database.Documents.CreateRawDocumentPutMessage("authoritative", new CultRecordHandle<NetworkSchemaNote>(One), Note("authoritative"));
            message.ShardId = shard.ShardId;
            message.ShardEpoch = shard.Epoch;
            var published = new List<CultNetDatabaseChangeKind>();
            database.Watch<NetworkSchemaNote>().Subscribe(change => published.Add(change.Kind));

            await database.PutPredictedAsync(One, Note("predicted"));
            await database.ApplyPutAsync(message);

            Assert.That(published, Is.EqualTo(new[] { CultNetDatabaseChangeKind.Predicted, CultNetDatabaseChangeKind.Reconciled }));
            Assert.That(database.GetMutationLog(shard.ShardId).Select(entry => entry.Kind), Is.EqualTo(new[] { CultNetDatabaseChangeKind.Updated }),
                "a prediction is never logged and its reconciliation is an ordinary update");
        }

        [Test]
        public async Task AnAdmissionRacingDisposalLogsNothingAfterTheDatabaseIsDisposed()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var entered = new System.Threading.ManualResetEventSlim();
            CultNetDatabase? database = null;
            // Registered before the database, so it runs first inside the writer's hold, while another thread disposes the database.
            using var holdUntilDisposed = cache.AddJournal(_ =>
            {
                entered.Set();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        database!.GetLatestMutationLogSequence(ShardId);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    System.Threading.Thread.Sleep(1);
                }
            });
            database = Database(cache, primary: true, store);
            var writer = Task.Run(() => database.PutAsync(One, Note("one")));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            database.Dispose();
            await writer;

            Assert.That(Sequences(store), Is.Empty, "the admission was journaled after disposal began and must not reach the durable log");
        }

        [Test]
        public void ABatchAcrossTwoShardsWhoseStoreRefusesReachesTheWriterAsOneExceptionNamingBothShards()
        {
            var store = new FlakyLogStore { RefuseAppend = _ => true };
            var cache = new CultCache();
            _ = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor("shard-a", "primary", epoch: 1, isPrimary: true, schemaIds: [SchemaId(cache)], keyPrefix: "a:"),
                    new CultNetShardDescriptor("shard-b", "primary", epoch: 1, isPrimary: true, schemaIds: [SchemaId(cache)], keyPrefix: "b:")
                ],
                MutationLogStore = store
            });

            var failure = Assert.Throws<CultNetShardLogException>(() => cache.Commit(batch =>
            {
                batch.Upsert(Note("a"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("a:1")));
                batch.Upsert(Note("b"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("b:1")));
            }))!;

            Assert.That(failure.Burned, Is.EqualTo(new[] { new CultNetBurnedSequence("shard-a", 1), new CultNetBurnedSequence("shard-b", 1) }));
        }

        // The cache aggregates whatever else fails in the same hold; the documented unwrap finds the log failure inside.
        [Test]
        public void ALogFailureBesideAnotherFailureInTheSameHoldIsFoundByUnwrappingTheAggregate()
        {
            var store = new FlakyLogStore { RefuseAppend = _ => true };
            var cache = new CultCache();
            _ = Database(cache, primary: true, store);
            using var second = cache.AddJournal(_ => throw new InvalidOperationException("second journal"));

            var failure = Assert.Throws<AggregateException>(() => cache.Commit(batch =>
            {
                batch.Upsert(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One));
                batch.Upsert(Note("two"), new CultRecordHandle<NetworkSchemaNote>(Two));
            }))!;

            var logFailure = failure.InnerExceptions.OfType<CultNetShardLogException>().Single();
            Assert.That(logFailure.Burned.Select(burned => burned.Sequence), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(failure.InnerExceptions.OfType<InvalidOperationException>().Single().Message, Is.EqualTo("second journal"));
        }

        [Test]
        public async Task ABareWriteOfASchemaNoShardOwnsIsNeitherLoggedNorPublishedNorReplicated()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            var database = Database(cache, primary: true, store);
            var published = Record(database);

            await cache.UpsertAsync(new MeshQuickstartNote { NoteId = "n", Body = "unrelated" }, new CultRecordHandle<MeshQuickstartNote>(new CultRecordKey("unrelated:n")));
            await cache.UpsertAsync(Note("one"), new CultRecordHandle<NetworkSchemaNote>(One));

            Assert.That(Sequences(store), Is.EqualTo(new[] { 1L }), "the unrelated write minted no sequence");
            Assert.That(database.GetMutationLog(ShardId).Select(entry => entry.Key.Value), Is.EqualTo(new[] { One.Value }));
            Assert.That(published, Is.EqualTo(new[] { One.Value }), "no shard owns the unrelated write, so the database does not publish it");
        }

        // The database is the sharded write door: a schema and key no shard owns cannot be written on any path, and a refused
        // write caches and logs nothing.
        private static (CultNetDatabase Database, CultCache Cache, FlakyLogStore Store, string SchemaId) OwnedNothingOfNote()
        {
            var store = new FlakyLogStore();
            var cache = new CultCache();
            return (Database(cache, primary: true, store), cache, store, cache.Registry.GetRequired<MeshQuickstartNote>().SchemaId);
        }

        private static readonly CultRecordKey Unowned = new("unrelated:note");

        private static CultNetDocumentPutRawMessage UnownedPut(CultNetDatabase database)
        {
            var message = database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(Unowned), new MeshQuickstartNote { NoteId = "w", Body = "wire" });
            message.ShardId = database.Shards[0].ShardId;
            message.ShardEpoch = database.Shards[0].Epoch;
            return message;
        }

        [Test]
        public void ADatabasePutOrDeleteOfASchemaNoShardOwnsIsRefusedNamingTheSchema()
        {
            var (database, cache, store, unowned) = OwnedNothingOfNote();

            var put = Assert.ThrowsAsync<CultNetUnownedSchemaException>(async () =>
                await database.PutAsync(Unowned, new MeshQuickstartNote { NoteId = "p", Body = "plain" }))!;
            Assert.That(put.SchemaId, Is.EqualTo(unowned));
            Assert.That(put.Message, Does.Contain(unowned));
            Assert.That(put, Is.InstanceOf<InvalidOperationException>(), "a refused write reads as one");
            Assert.That(cache.Get<MeshQuickstartNote>(Unowned), Is.Null, "nothing was cached");
            Assert.ThrowsAsync<CultNetUnownedSchemaException>(async () => await database.DeleteAsync<MeshQuickstartNote>(Unowned));
            Assert.That(Sequences(store), Is.Empty);
        }

        [Test]
        public void ARemoteAuthoritativePutOfASchemaNoShardOwnsIsRefusedAndCachesNothing()
        {
            var (database, cache, store, unowned) = OwnedNothingOfNote();

            var refusal = Assert.ThrowsAsync<CultNetUnownedSchemaException>(async () => await database.ApplyPutAsync(UnownedPut(database)))!;

            Assert.That(refusal.SchemaId, Is.EqualTo(unowned));
            Assert.That(cache.Get<MeshQuickstartNote>(Unowned), Is.Null);
            Assert.That(Sequences(store), Is.Empty);
        }

        [Test]
        public async Task ARemoteDeleteOfASchemaNoShardOwnsIsRefusedEvenWhenTheKeyIsCached()
        {
            var (database, cache, store, unowned) = OwnedNothingOfNote();
            await cache.UpsertAsync(new MeshQuickstartNote { NoteId = "c", Body = "cached" }, new CultRecordHandle<MeshQuickstartNote>(Unowned));

            var refusal = Assert.ThrowsAsync<CultNetUnownedSchemaException>(async () => await database.ApplyDeleteAsync(new CultNetDocumentDeleteMessage
            {
                MessageId = "d",
                SchemaId = unowned,
                RecordKey = Unowned.Value,
                ShardId = database.Shards[0].ShardId,
                ShardEpoch = database.Shards[0].Epoch
            }))!;

            Assert.That(refusal.SchemaId, Is.EqualTo(unowned));
            Assert.That(cache.Get<MeshQuickstartNote>(Unowned), Is.Not.Null, "the refused delete removed nothing");
            Assert.That(Sequences(store), Is.Empty);
        }

        [Test]
        public void APredictionOfASchemaNoShardOwnsIsRefused()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                Shards = [new CultNetShardDescriptor("only-notes", "local", epoch: 1, isPrimary: true, schemaIds: [SchemaId(cache)])],
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });

            Assert.ThrowsAsync<CultNetUnownedSchemaException>(async () =>
                await database.PutPredictedAsync(Unowned, new MeshQuickstartNote { NoteId = "p", Body = "predicted" }));
            Assert.That(cache.Get<MeshQuickstartNote>(Unowned), Is.Null);
        }

        [Test]
        public void ASchemaNoShardOwnsCannotBeWrittenAuthoritativelyAndHasNoShardToResolve()
        {
            var (database, cache, _, unowned) = OwnedNothingOfNote();

            Assert.That(database.CanWriteAuthoritatively<MeshQuickstartNote>(Unowned), Is.False);
            Assert.That(database.CanWriteAuthoritatively<NetworkSchemaNote>(One), Is.True, "an owned schema is unaffected");
            var refusal = Assert.Throws<CultNetUnownedSchemaException>(() => database.ResolveShard(unowned, Unowned))!;
            Assert.That(refusal.SchemaId, Is.EqualTo(unowned));
            Assert.That(database.ResolveShard(SchemaId(cache), One).ShardId, Is.EqualTo(ShardId));
        }

        // The server answers a refused remote put the way it answers any other: it logs the failure and sends the peer a
        // CultNetErrorMessage carrying the refusal. The handler is reached through its private delegate with an
        // uninitialized peer, as the R-AM test does; the send then fails on that peer, after the failure was logged.
        [Test]
        public async Task TheServerAnswersARemotePutOfASchemaNoShardOwnsWithAnApplicationRejection()
        {
            var (database, cache, _, unowned) = OwnedNothingOfNote();
            using var server = new Server(cache, ServerSecurityOptions.Development());
            var logger = new CapturingLogger();
            server.Logger = logger;
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var handler = (Func<CultNetDocumentPutRawMessage, CultNetServerPeer, Task>)typeof(CultNetDatabaseServer)
                .GetField("_putHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(databaseServer)!;
            var peer = (CultNetServerPeer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CultNetServerPeer));

            try { await handler(UnownedPut(database), peer); } catch (Exception) { }

            Assert.That(logger.Errors, Has.Some.Contains("CultNet raw put failed").And.Contains(unowned));
            Assert.That(cache.Get<MeshQuickstartNote>(Unowned), Is.Null);
        }

        // One cached row of a schema no shard owns is not part of what this database serves: the selection answers with the
        // rest, it does not fail as a whole.
        [Test]
        public async Task ASelectionSkipsACachedRowNoShardOwnsAndAnswersWithTheRest()
        {
            var (database, cache, _, _) = OwnedNothingOfNote();
            await database.PutAsync(One, Note("owned"));
            await cache.UpsertAsync(new MeshQuickstartNote { NoteId = "u", Body = "unowned" }, new CultRecordHandle<MeshQuickstartNote>(Unowned));
            using var server = new Server(cache, ServerSecurityOptions.Development());
            using var databaseServer = new CultNetDatabaseServer(server, database);

            var response = databaseServer.CreateSelectionResponse(new CultNetSnapshotRequestV1Message
            {
                MessageId = "select-all",
                Selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document }
            });

            Assert.That(response.Documents!.Select(document => document.RecordKey), Is.EqualTo(new[] { One.Value }));
            Assert.That(response.Matched, Is.EqualTo(1u));
        }

        private sealed class CapturingLogger : GameCult.Logging.ILogger
        {
            public List<string> Errors { get; } = new();
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) => Errors.Add(message);
            public void LogDebug(string message) { }
        }

        private static async Task ApplyAuthoritativeAsync(CultNetDatabase database, CultRecordKey key, string text)
        {
            var shard = database.Shards[0];
            var message = database.Documents.CreateRawDocumentPutMessage("authoritative", new CultRecordHandle<NetworkSchemaNote>(key), Note(text));
            message.ShardId = shard.ShardId;
            message.ShardEpoch = shard.Epoch;
            await database.ApplyPutAsync(message);
        }

        private static CultNetDatabaseOptions ClientOptions() => new()
        {
            RuntimeId = "local",
            ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
        };

        [Test]
        public async Task APredictionOverAPredictionIsStillAPredictionAndTheAuthoritativeWriteReconcilesOnce()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, ClientOptions());
            var kinds = new List<CultNetDatabaseChangeKind>();
            database.Watch<NetworkSchemaNote>().Subscribe(change => kinds.Add(change.Kind));

            await database.PutPredictedAsync(One, Note("p1"));
            await database.PutPredictedAsync(One, Note("p2"));
            await ApplyAuthoritativeAsync(database, One, "authoritative");
            await ApplyAuthoritativeAsync(database, One, "again");

            Assert.That(kinds, Is.EqualTo(new[]
            {
                CultNetDatabaseChangeKind.Predicted,
                CultNetDatabaseChangeKind.Predicted,
                CultNetDatabaseChangeKind.Reconciled,
                CultNetDatabaseChangeKind.Updated
            }));
        }

        [Test]
        public async Task AGameSessionPredictionOfTheInstanceTheCacheAlreadyHoldsIsAPredictionThatIsNeverLogged()
        {
            var rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "journal-session", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            using var node = await GameCult.Mesh.CultMesh.CreateNodeAsync(Path.Combine(rootPath, "world.ccmp"), new GameCult.Mesh.CultMeshNodeOptions
            {
                StartServer = false,
                DatabaseOptions = ClientOptions()
            });
            using var session = GameCult.Mesh.CultMesh.CreateGameSession(node, new GameCult.Mesh.CultMeshGameSessionOptions
            {
                ServeSimulationObservations = false,
                ServeVerseDiscovery = false,
                ServePeerExchange = false
            });
            var kinds = new List<CultNetDatabaseChangeKind>();
            node.Database.Watch<NetworkSchemaNote>().Subscribe(change => kinds.Add(change.Kind));

            await session.PredictAsync(One, Note("p1"));
            var held = node.Cache.Get<NetworkSchemaNote>(One)!;
            held.Text = "p2";
            await session.PredictAsync(One, held);
            await ApplyAuthoritativeAsync(node.Database, One, "authoritative");

            Assert.That(kinds, Is.EqualTo(new[] { CultNetDatabaseChangeKind.Predicted, CultNetDatabaseChangeKind.Predicted, CultNetDatabaseChangeKind.Reconciled }));
            Assert.That(node.Database.GetMutationLog(node.Database.Shards[0].ShardId).Select(entry => entry.Kind), Is.EqualTo(new[] { CultNetDatabaseChangeKind.Updated }));
        }

        [Test]
        public async Task ADatabaseRecordsEveryDeleteEntryItAppliesWhileAnotherThreadRemovesTheSameKeys()
        {
            const int rounds = 20000;
            var replicaCache = new CultCache();
            var replica = Database(replicaCache, primary: false, store: null);
            var schemaId = SchemaId(replicaCache);
            for (var i = 1; i <= rounds; i++)
                await replicaCache.UpsertAsync(Note("n"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("race:" + i)));

            var current = 0;
            var stop = false;
            var remover = Task.Run(() =>
            {
                var next = 1;
                while (next <= rounds && !Volatile.Read(ref stop))
                {
                    if (Volatile.Read(ref current) >= next)
                    {
                        replicaCache.Remove(new CultRecordKey("race:" + next));
                        next++;
                    }
                }
            });
            for (var i = 1; i <= rounds; i++)
            {
                Volatile.Write(ref current, i);
                await replica.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
                {
                    ShardId = ShardId,
                    ShardEpoch = 1,
                    Entries =
                    [
                        new CultNetShardLogEntryMessage
                        {
                            Sequence = i,
                            CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                            ChangeKind = "removed",
                            Delete = new CultNetDocumentDeleteMessage
                            {
                                MessageId = "d" + i,
                                SchemaId = schemaId,
                                RecordKey = "race:" + i,
                                ShardId = ShardId,
                                ShardEpoch = 1
                            }
                        }
                    ]
                });
            }

            Volatile.Write(ref stop, true);
            await remover;

            Assert.That(replica.GetMutationLog(ShardId).Select(entry => entry.Sequence), Is.EqualTo(Enumerable.Range(1, rounds).Select(i => (long)i)),
                "a replica's log holds every entry of the primary's, whoever removed the key first");
        }
    }
}
