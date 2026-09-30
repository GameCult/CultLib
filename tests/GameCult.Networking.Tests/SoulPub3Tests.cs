#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using NUnit.Framework;
using R3;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    [CultDocument("tests.soul_throwing_name", "tests.soul_throwing_name.v1")]
    [MessagePack.MessagePackObject]
    public sealed class SoulThrowingName
    {
        [MessagePack.Key(0)]
        public string Text { get; set; } = string.Empty;

        [MessagePack.Key(1)]
        [CultName]
        public string Name
        {
            get => Text == "boom" ? throw new InvalidOperationException("name getter boom") : Text;
            set { }
        }
    }

    [NonParallelizable]
    public sealed class SoulPub3Tests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };
        private static string SchemaId => CultDocumentRegistry.Shared.GetRequired<NetworkSchemaNote>().SchemaId;

        // Another thread becomes the drainer and parks inside a subscriber on the change "slow".
        private static (Task Writer, ManualResetEventSlim Release) BlockDrainer(CultNetDatabase database, CultCache cache, out IDisposable subscription)
        {
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            subscription = database.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text == "slow")
                {
                    entered.Set();
                    release.Wait(Wait);
                }
            });
            var writer = Task.Run(() => cache.UpsertAsync(Note("slow"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul3:slow"))).GetAwaiter().GetResult());
            Assert.That(entered.Wait(Wait), Is.True, "drainer parked");
            return (writer, release);
        }

        // P1: a write that returns has been logged and published.
        [Test]
        public async Task P1_AWriteThatReturnedIsLoggedAndPublished()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var seen = new ConcurrentQueue<(string? Text, int Thread)>();
            using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change => seen.Enqueue((change.Document?.Text, Environment.CurrentManagedThreadId)));
            var (writer, release) = BlockDrainer(database, cache, out var block);
            var fast = new CultRecordKey("soul3:fast");
            var writerThread = 0;
            await Task.Run(async () => { writerThread = Environment.CurrentManagedThreadId; await database.PutAsync(fast, Note("fast")); });

            var logged = database.LastWriteSequence(SchemaId, fast);
            var published = seen.Any(entry => entry.Text == "fast");
            release.Set();
            await writer;
            block.Dispose();
            var deliveredOn = seen.First(entry => entry.Text == "fast").Thread;
            TestContext.Progress.WriteLine($"SOUL P1 logged-at-return={logged} published-at-return={published} writer={writerThread} deliveredOn={deliveredOn}");
            Assert.Multiple(() =>
            {
                Assert.That(logged, Is.Not.Null, "PutAsync returned before its change was logged");
                Assert.That(published, Is.True, "PutAsync returned before its change was published");
            });
        }

        // P2: a subscriber that waits (bounded) for another thread's write to be observed.
        [Test]
        public async Task P2_ASubscriberWaitingForAnotherThreadsWriteIsNotStarved()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var other = new ManualResetEventSlim();
            var observedInTime = false;
            using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text == "other")
                    other.Set();
                if (change.Document?.Text == "trigger")
                {
                    Task.Run(() => database.PutAsync(new CultRecordKey("soul3:other"), Note("other")));
                    observedInTime = other.Wait(TimeSpan.FromSeconds(2));
                }
            });
            await database.PutAsync(new CultRecordKey("soul3:trigger"), Note("trigger"));
            Assert.That(observedInTime, Is.True, "another thread's committed write never reached subscribers while this handler ran");
        }

        // P3: one observer's escaped exception (fail-fast R3 handler) loses one change; it must not stall all later ones.
        [Test]
        public async Task P3_AnEscapedObserverExceptionDoesNotStallLaterChanges()
        {
            var previous = ObservableSystem.GetUnhandledExceptionHandler();
            ObservableSystem.RegisterUnhandledExceptionHandler(exception => throw exception);
            try
            {
                var cache = new CultCache();
                using var thrower = cache.Watch<NetworkSchemaNote>().Subscribe(change =>
                {
                    if (change.Document?.Text == "boom") throw new InvalidOperationException("observer boom");
                });
                var database = new CultNetDatabase(cache);
                var seen = new ConcurrentQueue<string?>();
                using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change => seen.Enqueue(change.Document?.Text));
                try { await cache.UpsertAsync(Note("boom"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul3:boom"))); }
                catch (InvalidOperationException) { }

                ObservableSystem.RegisterUnhandledExceptionHandler(previous);
                var after = new CultRecordKey("soul3:after");
                await database.PutAsync(after, Note("after"));
                Assert.Multiple(() =>
                {
                    Assert.That(seen, Does.Contain("after"), "a later commit was never published");
                    Assert.That(database.LastWriteSequence(SchemaId, after), Is.Not.Null, "a later commit was never logged");
                });
            }
            finally
            {
                ObservableSystem.RegisterUnhandledExceptionHandler(previous);
            }
        }

        // P4: an admission that throws inside Apply after minting a Sequence (a [CultName] getter) must not stall later changes.
        [Test]
        public async Task P4_AThrowMidApplyDoesNotStallLaterChanges()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var seen = new ConcurrentQueue<string?>();
            using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change => seen.Enqueue(change.Document?.Text));
            try
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(new SoulThrowingName { Text = "ok" }, new CultRecordHandle<SoulThrowingName>(new CultRecordKey("soul3:ok")));
                    batch.Upsert(new SoulThrowingName { Text = "boom" }, new CultRecordHandle<SoulThrowingName>(new CultRecordKey("soul3:boomname")));
                });
            }
            catch (InvalidOperationException) { }

            var after = new CultRecordKey("soul3:after-apply");
            await database.PutAsync(after, Note("after"));
            TestContext.Progress.WriteLine($"SOUL P4 ok-in-cache={cache.Get(new CultRecordKey("soul3:ok")) != null}");
            Assert.Multiple(() =>
            {
                Assert.That(seen, Does.Contain("after"), "a later commit was never published");
                Assert.That(database.LastWriteSequence(SchemaId, after), Is.Not.Null, "a later commit was never logged");
            });
        }

        // P5: writers no longer carry their own delivery; the backlog behind one drainer grows without bound.
        [Test]
        public async Task P5_OtherWritersDoNotPileAnUnboundedBacklogOnOneDrainer()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var delivered = 0;
            var backlogAtBDone = -1;
            const int bWrites = 2000;
            using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text?.StartsWith("b") == true) Interlocked.Increment(ref delivered);
                if (change.Document?.Text == "a")
                {
                    Task.Run(async () =>
                    {
                        for (var i = 0; i < bWrites; i++)
                            await database.PutAsync(new CultRecordKey($"soul3:b:{i}"), Note($"b{i}"));
                    }).Wait(Wait);
                    backlogAtBDone = bWrites - Volatile.Read(ref delivered);
                }
            });
            await database.PutAsync(new CultRecordKey("soul3:a"), Note("a"));
            TestContext.Progress.WriteLine($"SOUL P5 undelivered after B's {bWrites} writes returned: {backlogAtBDone}");
            Assert.That(backlogAtBDone, Is.EqualTo(0));
        }

        // P6: a prediction still queued when the authoritative put arrives.
        [Test]
        public async Task P6_AnAuthoritativePutArrivingBeforeItsPredictionIsDeliveredStillReconciles()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var key = new CultRecordKey("soul3:predict");
            var kinds = new ConcurrentQueue<CultNetDatabaseChangeKind>();
            using var watch = database.WatchRecord<NetworkSchemaNote>(key).Subscribe(change => kinds.Enqueue(change.Kind));
            CultNetDocumentPutRawMessage Put(string text)
            {
                var message = database.Documents.CreateRawDocumentPutMessage(text, new CultRecordHandle<NetworkSchemaNote>(key), Note(text));
                message.ShardId = database.Shards[0].ShardId;
                message.ShardEpoch = database.Shards[0].Epoch;
                return message;
            }

            var (writer, release) = BlockDrainer(database, cache, out var block);
            await database.PutPredictedAsync(key, Note("predicted"));
            await database.ApplyPutAsync(Put("auth1"));
            release.Set();
            await writer;
            block.Dispose();
            await database.ApplyPutAsync(Put("auth2"));

            Assert.That(kinds, Is.EqualTo(new[]
            {
                CultNetDatabaseChangeKind.Predicted,
                CultNetDatabaseChangeKind.Reconciled,
                CultNetDatabaseChangeKind.Updated
            }));
        }

        // P7: a replica's absent-key delete is logged ahead of an earlier entry still queued; a bounded pull then sees a gap.
        [Test]
        public async Task P7_AReplicaLogStaysInSequenceOrderWhenAnAbsentDeleteOvertakesAQueuedPut()
        {
            const string shardId = "chain3";
            CultNetDatabase Make(CultCache cache, bool primary) => new(cache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(shardId, "p", epoch: 2, isPrimary: primary, schemaIds: [SchemaId])]
            });
            var p = Make(new CultCache(), true);
            var rCache = new CultCache();
            var r = Make(rCache, false);
            var c = Make(new CultCache(), false);
            async Task Pull(CultNetDatabase from, CultNetDatabase to, int? limit = null)
            {
                await to.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
                {
                    ShardId = shardId,
                    ShardEpoch = 2,
                    Entries = from.GetMutationLogMessages(shardId, to.GetAppliedShardSequence(shardId), limit).ToArray()
                });
            }

            var z = new CultRecordKey("soul3:z");
            await p.PutAsync(z, Note("z"));
            await Pull(p, r);
            await Pull(r, c);
            rCache.Remove(z);
            await p.PutAsync(new CultRecordKey("soul3:a"), Note("a"));
            await p.DeleteAsync<NetworkSchemaNote>(z);

            var (writer, release) = BlockDrainer(r, rCache, out var block);
            await Pull(p, r);
            release.Set();
            await writer;
            block.Dispose();

            TestContext.Progress.WriteLine($"SOUL P7 r log order: {string.Join(",", r.GetMutationLog(shardId).Select(e => e.Sequence))}");
            Assert.Multiple(() =>
            {
                Assert.That(r.GetMutationLog(shardId).Select(entry => entry.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L }));
                Assert.DoesNotThrowAsync(() => Pull(r, c, limit: 1), "a bounded pull from R");
            });
        }
    }
}
