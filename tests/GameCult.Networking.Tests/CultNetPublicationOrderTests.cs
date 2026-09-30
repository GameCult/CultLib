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
    public sealed class CultNetPublicationOrderTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };

        private static string? TextOf(object? document) => (document as NetworkSchemaNote)?.Text;

        // A1: two writers to one key. A is admitted first, B second; the cache ends at B. A's publication is delayed
        // (the window between leaving the cache gate and reaching the handler), B runs to completion meanwhile.
        [Test]
        public async Task S1_TheLogOrdersOneKeysWritesInAdmissionOrder()
        {
            var cache = new CultCache();
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var widen = cache.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text == "A")
                {
                    entered.Set();
                    release.Wait(Wait);
                }
            });
            var database = new CultNetDatabase(cache);
            var seen = new List<string?>();
            using var subscriber = database.Watch<NetworkSchemaNote>().Subscribe(change => { lock (seen) seen.Add(change.Document?.Text); });
            var key = new CultRecordKey("soul:order");

            var a = Task.Run(() => cache.UpsertAsync(Note("A"), new CultRecordHandle<NetworkSchemaNote>(key)).GetAwaiter().GetResult());
            Assert.That(entered.Wait(Wait), Is.True);
            await database.PutAsync(key, Note("B"));
            release.Set();
            await a;

            var shard = database.Shards[0].ShardId;
            var last = database.GetMutationLog(shard).Where(entry => entry.Key.Value == key.Value).OrderBy(entry => entry.Sequence).Last();

            // A replica built from the log.
            var replicaCache = new CultCache();
            var replica = new CultNetDatabase(replicaCache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(shard, "other", epoch: database.Shards[0].Epoch, isPrimary: false)]
            });
            await replica.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
            {
                ShardId = shard,
                ShardEpoch = database.Shards[0].Epoch,
                Entries = database.GetMutationLogMessages(shard).ToArray()
            });

            Assert.Multiple(() =>
            {
                Assert.That(cache.Get<NetworkSchemaNote>(key)!.Text, Is.EqualTo("B"), "the cache admitted A then B");
                Assert.That(TextOf(last.Document), Is.EqualTo("B"), "the log's last write for the key must be the cache's last write");
                Assert.That(replicaCache.Get<NetworkSchemaNote>(key)?.Text, Is.EqualTo("B"), "a replica built from the log must match the primary");
                // Delivery is not ordered across threads (order is data: a wire peer drops the stale change by Sequence, Cut 6),
                // so a subscriber sees both changes, once each, in either order.
                Assert.That(seen, Is.EquivalentTo(new[] { "A", "B" }), "each change is published exactly once");
            });
        }

        // Same property, no instrumentation: two writers hammer one key, then the log's last entry is compared with the cache.
        [Test]
        public void S1b_UninstrumentedOneKeyRaceLeavesLogAndCacheAgreeing()
        {
            var divergent = 0;
            const int rounds = 300;
            for (var round = 0; round < rounds; round++)
            {
                var cache = new CultCache();
                var database = new CultNetDatabase(cache);
                var key = new CultRecordKey("soul:hammer");
                using var start = new Barrier(2);
                Parallel.For(0, 2, writer =>
                {
                    start.SignalAndWait();
                    for (var i = 0; i < 40; i++)
                        cache.UpsertAsync(Note($"{writer}:{i}"), new CultRecordHandle<NetworkSchemaNote>(key)).GetAwaiter().GetResult();
                });
                var last = database.GetMutationLog(database.Shards[0].ShardId).OrderBy(entry => entry.Sequence).Last();
                if (!ReferenceEquals(last.Document, cache.Get<NetworkSchemaNote>(key)))
                    divergent++;
            }

            TestContext.Progress.WriteLine($"SOUL S1b divergent rounds: {divergent}/{rounds}");
            Assert.That(divergent, Is.EqualTo(0), $"{divergent}/{rounds} rounds ended with the log's last write differing from the cache");
        }

        // A2: a door's context is keyed by instance. A plain removal of the instance the door just admitted, landing in
        // the window before the door's own change is handled, consumes the door's context.
        [Test]
        public async Task S2_ARemovalNeverWearsAReconcilingPutsContext()
        {
            var cache = new CultCache();
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var widen = cache.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text == "authoritative")
                {
                    entered.Set();
                    release.Wait(Wait);
                }
            });
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var key = new CultRecordKey("soul:reconcile");
            await database.PutPredictedAsync(key, Note("predicted"));
            var published = new ConcurrentQueue<(CultNetDatabaseChangeKind Kind, string? Text)>();
            using var subscriber = database.Watch<NetworkSchemaNote>().Subscribe(change => published.Enqueue((change.Kind, change.Document?.Text)));
            var logged = database.GetMutationLog(database.Shards[0].ShardId).Count;
            var message = database.Documents.CreateRawDocumentPutMessage("m", new CultRecordHandle<NetworkSchemaNote>(key), Note("authoritative"));
            message.ShardId = database.Shards[0].ShardId;
            message.ShardEpoch = database.Shards[0].Epoch;

            var a = Task.Run(() => database.ApplyPutAsync(message));
            Assert.That(entered.Wait(Wait), Is.True);
            cache.Remove(key);
            release.Set();
            await a;

            var log = database.GetMutationLog(database.Shards[0].ShardId, logged).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(published.Count(change => change.Kind == CultNetDatabaseChangeKind.Removed), Is.EqualTo(1),
                    "the removal is published as a removal: " + string.Join(", ", published));
                Assert.That(published.Where(change => change.Kind == CultNetDatabaseChangeKind.Reconciled).Select(change => change.Text),
                    Is.EqualTo(new[] { "authoritative" }), "the reconcile carries the authoritative document");
                Assert.That(log.Where(entry => entry.Kind != CultNetDatabaseChangeKind.Removed).All(entry => entry.Document != null), Is.True,
                    "no put entry without a document: " + string.Join(", ", log.Select(entry => entry.Kind + ":" + TextOf(entry.Document))));
            });
        }

        // A4: chained replication. B is a replica of P, C a replica of B. A replica-applied delete of a key B does not
        // hold (B removed it locally, a supported write) must still keep B's log contiguous for C.
        [Test]
        public async Task S3_AChainedReplicaSeesNoGapWhenItsSourceAppliesADeleteOfAnAbsentKey()
        {
            const string shardId = "chain";
            var schemaId = new CultCache().Registry.GetRequired<NetworkSchemaNote>().SchemaId;
            CultNetDatabase Make(CultCache cache, bool primary) => new(cache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(shardId, "p", epoch: 2, isPrimary: primary, schemaIds: [schemaId])]
            });
            var p = Make(new CultCache(), true);
            var bCache = new CultCache();
            var b = Make(bCache, false);
            var c = Make(new CultCache(), false);
            async Task Pull(CultNetDatabase from, CultNetDatabase to)
            {
                await to.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
                {
                    ShardId = shardId,
                    ShardEpoch = 2,
                    Entries = from.GetMutationLogMessages(shardId, to.GetAppliedShardSequence(shardId)).ToArray()
                });
            }

            var k = new CultRecordKey("soul:chain:k");
            await p.PutAsync(k, Note("x"));
            await Pull(p, b);
            await Pull(b, c);
            bCache.Remove(k);
            await p.DeleteAsync<NetworkSchemaNote>(k);
            await p.PutAsync(new CultRecordKey("soul:chain:k2"), Note("y"));
            await Pull(p, b);

            Assert.Multiple(() =>
            {
                Assert.That(b.GetMutationLog(shardId).Select(entry => entry.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L }),
                    "B's log holds every primary entry it applied");
                Assert.DoesNotThrowAsync(() => Pull(b, c), "C pulls B's log without a gap");
            });
        }

        // A5: a subscriber that answers a Reconciled change by predicting the same key again keeps its prediction.
        [Test]
        public async Task S4_APredictionMadeInReactionToAReconcileSurvives()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var key = new CultRecordKey("soul:repredict");
            var kinds = new ConcurrentQueue<CultNetDatabaseChangeKind>();
            var repredicted = false;
            using var subscriber = database.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                kinds.Enqueue(change.Kind);
                if (change.Kind == CultNetDatabaseChangeKind.Reconciled && !repredicted)
                {
                    repredicted = true;
                    database.PutPredictedAsync(key, Note("predicted again")).GetAwaiter().GetResult();
                }
            });
            CultNetDocumentPutRawMessage Put(string text)
            {
                var message = database.Documents.CreateRawDocumentPutMessage(text, new CultRecordHandle<NetworkSchemaNote>(key), Note(text));
                message.ShardId = database.Shards[0].ShardId;
                message.ShardEpoch = database.Shards[0].Epoch;
                return message;
            }

            await database.PutPredictedAsync(key, Note("predicted"));
            await database.ApplyPutAsync(Put("auth1"));
            await database.ApplyPutAsync(Put("auth2"));

            Assert.That(kinds, Is.EqualTo(new[]
            {
                CultNetDatabaseChangeKind.Predicted,
                CultNetDatabaseChangeKind.Reconciled,
                CultNetDatabaseChangeKind.Predicted,
                CultNetDatabaseChangeKind.Reconciled
            }));
        }
        // Target of AWriteAfterADatabaseDeleteIsStillPublishedAndLogged: a door context must not outlive its door. A
        // refused prediction leaves nothing behind that a later write of the same instance would wear.
        [Test]
        public async Task S6_ARefusedDoorLeavesNoContextForALaterWriteOfTheSameInstance()
        {
            var registry = CultDocumentRegistry.Shared;
            var cache = new CultCache(registry, GameCult.Caching.MessagePack.CultCacheMessagePack.CreateCodec(registry));
            var documents = new CultNetDocumentRegistry(registry)
                .Register(CultNetDocumentBinding.ForDocument<CultNetDocumentRegistryVariantTests.VariantWireFixture>(registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = documents,
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var baseKey = new CultRecordKey("soul-refuse-base");
            var variantKey = new CultRecordKey("soul-refuse-variant");
            await cache.UpsertAsync(new CultNetDocumentRegistryVariantTests.VariantWireFixture { Name = "base", Power = 1 },
                new CultRecordHandle<CultNetDocumentRegistryVariantTests.VariantWireFixture>(baseKey));
            await cache.UpsertVariantAsync(variantKey, baseKey, new[]
            {
                cache.Override<CultNetDocumentRegistryVariantTests.VariantWireFixture>(nameof(CultNetDocumentRegistryVariantTests.VariantWireFixture.Name), "v")
            });
            var instance = new CultNetDocumentRegistryVariantTests.VariantWireFixture { Name = "reused", Power = 3 };
            Assert.That(async () => await database.PutPredictedAsync(variantKey, instance), Throws.InvalidOperationException);
            var kinds = new ConcurrentQueue<CultNetDatabaseChangeKind>();
            using var subscriber = database.Watch<CultNetDocumentRegistryVariantTests.VariantWireFixture>().Subscribe(change => kinds.Enqueue(change.Kind));
            var logged = database.GetMutationLog(database.Shards[0].ShardId).Count;

            await cache.UpsertAsync(instance, new CultRecordHandle<CultNetDocumentRegistryVariantTests.VariantWireFixture>(new CultRecordKey("soul-refuse-other")));

            Assert.Multiple(() =>
            {
                Assert.That(kinds, Is.EqualTo(new[] { CultNetDatabaseChangeKind.Added }));
                Assert.That(database.GetMutationLog(database.Shards[0].ShardId, logged), Has.Count.EqualTo(1));
            });
        }

        // Target of ADisposedDatabaseNoLongerObservesTheCache: after Dispose the database must stop logging, which a
        // durable log store can observe even though the disposed Subject hides publication.
        [Test]
        public async Task S7_ADisposedDatabaseAppendsNothingToItsDurableLog()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soul-log-" + Guid.NewGuid().ToString("N"));
            var store = new CultNetFileShardMutationLogStore(root);
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { MutationLogStore = store });
            var shard = database.Shards[0].ShardId;
            await cache.UpsertAsync(Note("before"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul:dispose:before")));
            Assert.That(store.Read(shard), Has.Count.EqualTo(1));
            database.Dispose();

            try { await cache.UpsertAsync(Note("after"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("soul:dispose:after"))); }
            catch { /* a throwing handler would also be a failure below */ }

            Assert.That(store.Read(shard), Has.Count.EqualTo(1));
            System.IO.Directory.Delete(root, true);
        }

        // The log entry exists before its change is published: a subscriber that reads the log from its handler finds it.
        [Test]
        public async Task TheLogEntryIsWrittenBeforeTheChangeIsPublished()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var key = new CultRecordKey("soul:log-first");
            var loggedWhenPublished = new ConcurrentQueue<bool>();
            using var subscriber = database.Watch<NetworkSchemaNote>().Subscribe(change =>
                loggedWhenPublished.Enqueue(database.GetMutationLog(database.Shards[0].ShardId).Any(entry => entry.Key.Value == key.Value)));

            await database.PutAsync(key, Note("one"));
            cache.Remove(key);

            Assert.That(loggedWhenPublished, Is.EqualTo(new[] { true, true }));
        }

        // An observer that mutates the admitted instance in place and upserts it again writes a second change of the same
        // instance. It is published as its own update and never wears the context of the door's change, even when it
        // reaches the database first.
        [Test]
        public async Task AnInPlaceReUpsertOfTheDoorsInstanceDoesNotWearTheDoorsContext()
        {
            var cache = new CultCache();
            using var normalizer = cache.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text == "authoritative")
                {
                    change.Document.Text = "normalized";
                    cache.UpsertAsync(change.Document, new CultRecordHandle<NetworkSchemaNote>(change.Key)).GetAwaiter().GetResult();
                }
            });
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "local",
                ClientAuthorityScopes = [new CultNetClientAuthorityScope("local")]
            });
            var key = new CultRecordKey("soul:in-place");
            var seen = new ConcurrentQueue<(CultNetDatabaseChangeKind Kind, string? Previous)>();
            using var subscriber = database.Watch<NetworkSchemaNote>().Subscribe(change => seen.Enqueue((change.Kind, change.PreviousDocument?.Text)));
            var message = database.Documents.CreateRawDocumentPutMessage("m", new CultRecordHandle<NetworkSchemaNote>(key), Note("authoritative"));
            message.ShardId = database.Shards[0].ShardId;
            message.ShardEpoch = database.Shards[0].Epoch;

            await database.PutPredictedAsync(key, Note("predicted"));
            await database.ApplyPutAsync(message);

            // The observer's nested write is delivered depth-first, inside the delivery of the change that caused it, so
            // the database may publish the two in either order; what is pinned is that each carries its own context.
            Assert.That(seen, Is.EquivalentTo(new (CultNetDatabaseChangeKind, string?)[]
            {
                (CultNetDatabaseChangeKind.Predicted, null),
                (CultNetDatabaseChangeKind.Reconciled, "predicted"),
                (CultNetDatabaseChangeKind.Updated, "normalized")
            }));
        }
    }
}
