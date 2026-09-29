#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using R3;
using static GameCult.Networking.Tests.NetworkingTests;
using static GameCult.Networking.Tests.CultNetDocumentRegistryVariantTests;

namespace GameCult.Networking.Tests
{
    // The cache is the one owner of "a change was committed": whatever door wrote it, a subscriber over the wire and the
    // mutation log see it once. A database write door publishes its own change (it carries the shard-log entry); every
    // other change in the hold publishes from the cache.
    public sealed class CultNetPublishesCommitsTests
    {
        private static CultNetDatabaseChangeRawMessage[] Of(Wire wire, string key) =>
            wire.Changes.Where(change => KeyOf(change) == key).ToArray();

        private static string? KeyOf(CultNetDatabaseChangeRawMessage change) => change.Document?.RecordKey ?? change.RecordKey;

        private sealed class Wire : IDisposable
        {
            private readonly CancellationTokenSource _cancellation = new();
            private readonly Thread _thread;
            private readonly RudpCultNetSchemaServer _server;
            private readonly CultNetDatabaseSubscriptionServer _subscriptions;
            private readonly ICultNetSchemaClient _client;
            public ConcurrentQueue<CultNetDatabaseChangeRawMessage> Changes { get; } = new();
            public int Snapshots;
            public ConcurrentQueue<CultNetErrorMessage> Errors { get; } = new();

            private readonly CultNetDatabase _database;

            public Wire(CultNetDatabase database)
            {
                _database = database;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.ReceiveTimeout = 20;
                _server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions { RuntimeId = "publish-server", Socket = socket });
                _subscriptions = new CultNetDatabaseSubscriptionServer(_server, database);
                _thread = new Thread(() =>
                {
                    while (!_cancellation.IsCancellationRequested)
                    {
                        _ = _server.PollOnceAsync().GetAwaiter().GetResult();
                        Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                _thread.Start();
                _client = CultNetSchemaClients.CreateRudp("publish-client");
                _client.OnCultNet<CultNetDatabaseChangeRawMessage>(Changes.Enqueue);
                _client.OnCultNet<CultNetErrorMessage>(Errors.Enqueue);
                _client.OnCultNet<CultNetSnapshotResponseRawMessage>(_ => Interlocked.Increment(ref Snapshots));
                _client.Connect("127.0.0.1", _server.LocalEndPoint.Port);
            }

            public async Task SubscribeAsync(bool includeSnapshot, params Type[] documentTypes)
            {
                await Until(() => _client.Connected);
                _client.SendCultNet(new CultNetDatabaseSubscribeMessage
                {
                    MessageId = "subscribe",
                    SubscriptionId = "all",
                    SchemaIds = documentTypes.Select(type => _database.Cache.Registry.GetRequired(type).SchemaId).ToArray(),
                    IncludeSnapshot = includeSnapshot
                });
                await Until(() => Snapshots > 0 || !Errors.IsEmpty);
                Assert.That(Errors.Select(error => error.Error), Is.Empty, "the subscription was refused");
            }

            public void Dispose()
            {
                _cancellation.Cancel();
                _thread.Join();
                _client.Dispose();
                _subscriptions.Dispose();
                _server.Dispose();
            }
        }

        private static async Task Until(Func<bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition())
            {
                if (DateTimeOffset.UtcNow > deadline)
                    Assert.Fail("Condition was not satisfied before timeout.");
                await Task.Delay(5);
            }
        }

        // One reliable ordered channel: a sentinel write that has arrived proves every earlier write has had its chance
        // to arrive twice.
        private static async Task SettleAsync(CultNetDatabase database, Wire wire)
        {
            var sentinel = "tests:publish:sentinel:" + Guid.NewGuid().ToString("N");
            await database.PutAsync(new CultRecordKey(sentinel), new NetworkSchemaNote { Schema = "tests.networking_note.v1" });
            await Until(() => wire.Changes.Any(change => KeyOf(change) == sentinel));
        }

        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };

        [Test]
        public async Task ACacheCommitAndUpsertReachASubscriberOnce()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            using var wire = new Wire(database);
            await wire.SubscribeAsync(false, typeof(NetworkSchemaNote));
            var key = new CultRecordKey("tests:publish:commit");

            cache.Commit(batch => batch.Upsert(Note("committed"), new CultRecordHandle<NetworkSchemaNote>(key)));
            await Until(() => Of(wire, key.Value).Length >= 1);
            cache.Commit(batch => batch.Remove(key));
            await Until(() => Of(wire, key.Value).Length >= 2);
            await cache.UpsertAsync(Note("upserted"), new CultRecordHandle<NetworkSchemaNote>(key));
            await SettleAsync(database, wire);

            Assert.That(Of(wire, key.Value).Select(change => change.ChangeKind), Is.EqualTo(new[] { "added", "removed", "added" }));
            Assert.That(database.GetMutationLog(database.Shards[0].ShardId).Count(entry => entry.Key.Value == key.Value), Is.EqualTo(3));
        }

        [Test]
        public async Task ADatabasePutAndDeleteStillPublishExactlyOnce()
        {
            var database = new CultNetDatabase(new CultCache());
            using var wire = new Wire(database);
            await wire.SubscribeAsync(false, typeof(NetworkSchemaNote));
            var key = new CultRecordKey("tests:publish:put");

            await database.PutAsync(key, Note("put"));
            await database.DeleteAsync<NetworkSchemaNote>(key);
            await SettleAsync(database, wire);

            Assert.That(Of(wire, key.Value).Select(change => change.ChangeKind), Is.EqualTo(new[] { "added", "removed" }));
            Assert.That(database.GetMutationLog(database.Shards[0].ShardId).Count(entry => entry.Key.Value == key.Value), Is.EqualTo(2));
        }

        [Test]
        public async Task AReplicatedWriteIsPublishedAndLoggedOnceNotEchoed()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            using var wire = new Wire(database);
            await wire.SubscribeAsync(false, typeof(NetworkSchemaNote));
            var key = new CultRecordKey("tests:publish:replicated");
            var message = database.Documents.CreateRawDocumentPutMessage(
                "put-replicated", new CultRecordHandle<NetworkSchemaNote>(key), Note("remote"));
            message.ShardId = database.Shards[0].ShardId;
            message.ShardEpoch = database.Shards[0].Epoch;

            await database.ApplyPutAsync(message);
            await SettleAsync(database, wire);

            Assert.That(Of(wire, key.Value), Has.Length.EqualTo(1));
            Assert.That(database.GetMutationLog(database.Shards[0].ShardId).Count(entry => entry.Key.Value == key.Value), Is.EqualTo(1));
        }

        [Test]
        public async Task ALoadRemovalCarriesItsKey()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultnet-load-{Guid.NewGuid():N}.cc");
            try
            {
                var key = new CultRecordKey("tests:publish:loaded");
                var writer = new CultCache();
                writer.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await writer.UpsertAsync(Note("there"), new CultRecordHandle<NetworkSchemaNote>(key));
                writer.FlushAllBackingStores();

                var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                var database = new CultNetDatabase(cache);
                Assert.That(cache.Get<NetworkSchemaNote>(key), Is.Not.Null);
                using var wire = new Wire(database);
                await wire.SubscribeAsync(true, typeof(NetworkSchemaNote));

                writer.Remove(key);
                writer.FlushAllBackingStores();
                await cache.PullAllBackingStoresAsync();
                await SettleAsync(database, wire);

                Assert.That(Of(wire, key.Value).Select(change => change.ChangeKind), Is.EqualTo(new[] { "removed" }));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
            }
        }

        [Test]
        public async Task ABaseEditPublishesItsVariantDependentAndTheSubscriberIsRefusedItByKey()
        {
            var registry = CultDocumentRegistry.Shared;
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            var documents = new CultNetDocumentRegistry(registry)
                .Register(CultNetDocumentBinding.ForDocument<VariantWireFixture>(registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { DocumentRegistry = documents });
            var baseKey = new CultRecordKey("publish-variant-base");
            var variantKey = new CultRecordKey("publish-variant-big");
            await cache.UpsertAsync(new VariantWireFixture { Name = "base", Power = 1 }, new CultRecordHandle<VariantWireFixture>(baseKey));
            await cache.UpsertVariantAsync(variantKey, baseKey, new[]
            {
                cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big"),
                cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Power), 9)
            });
            using var wire = new Wire(database);
            await wire.SubscribeAsync(false, typeof(VariantWireFixture));
            var errorsBefore = wire.Errors.Count;

            await database.PutAsync(baseKey, new VariantWireFixture { Name = "base edited", Power = 2 });
            await Until(() => wire.Errors.Count > errorsBefore);
            await Until(() => Of(wire, baseKey.Value).Length >= 1);

            Assert.That(wire.Errors.Skip(errorsBefore).Single().Error, Does.Contain(variantKey.Value),
                "the re-resolved dependent is published, then refused (Q6) by key");
            Assert.That(Of(wire, baseKey.Value), Has.Length.EqualTo(1));
            Assert.That(Of(wire, variantKey.Value), Is.Empty, "a variant is never carried as a record");
        }

        // An observer that rewrites the very key a door just wrote is a second write: it publishes and logs on its own.
        [Test]
        public async Task AnObserverRewriteOfTheDoorsOwnKeyIsPublishedAndLoggedToo()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            using var wire = new Wire(database);
            await wire.SubscribeAsync(false, typeof(NetworkSchemaNote));
            var key = new CultRecordKey("tests:publish:normalized");
            using var normalizer = cache.Watch<NetworkSchemaNote>().Subscribe(change =>
            {
                if (change.Document?.Text == "raw")
                    cache.UpsertAsync(Note("normalized"), new CultRecordHandle<NetworkSchemaNote>(change.Key)).GetAwaiter().GetResult();
            });

            await database.PutAsync(key, Note("raw"));
            await Until(() => Of(wire, key.Value).Length >= 2);
            await SettleAsync(database, wire);

            Assert.That(cache.Get<NetworkSchemaNote>(key)!.Text, Is.EqualTo("normalized"));
            Assert.That(Of(wire, key.Value).Select(change => change.ChangeKind), Is.EqualTo(new[] { "added", "updated" }));
            Assert.That(database.GetMutationLog(database.Shards[0].ShardId).Count(entry => entry.Key.Value == key.Value), Is.EqualTo(2));
        }

        private static (CultCache Cache, CultNetDocumentRegistry Documents) VariantCache()
        {
            var registry = CultDocumentRegistry.Shared;
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            var documents = new CultNetDocumentRegistry(registry)
                .Register(CultNetDocumentBinding.ForDocument<VariantWireFixture>(registry));
            return (cache, documents);
        }

        private static System.Collections.Generic.List<string> Record(CultNetDatabase database, Func<object, string> describe)
        {
            var seen = new System.Collections.Generic.List<string>();
            database.WatchAllChanges().Subscribe(change => seen.Add(describe(change)));
            return seen;
        }

        private static string KeyOfChange(object change) => ((dynamic)change).Key.Value;

        [Test]
        public async Task ABaseWriteAndItsReResolvedVariantAreLoggedInCacheOrder()
        {
            var (cache, documents) = VariantCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { DocumentRegistry = documents });
            var baseKey = new CultRecordKey("publish-variant-base");
            var variantKey = new CultRecordKey("publish-variant-big");
            await cache.UpsertAsync(new VariantWireFixture { Name = "base", Power = 1 }, new CultRecordHandle<VariantWireFixture>(baseKey));
            await cache.UpsertVariantAsync(variantKey, baseKey, new[]
            {
                cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big")
            });
            var published = Record(database, KeyOfChange);
            var logged = database.GetMutationLog(database.Shards[0].ShardId).Count;

            await database.PutAsync(baseKey, new VariantWireFixture { Name = "base edited", Power = 2 });

            Assert.That(published, Is.EqualTo(new[] { baseKey.Value, variantKey.Value }));
            Assert.That(database.GetMutationLog(database.Shards[0].ShardId, logged).Select(entry => entry.Key.Value),
                Is.EqualTo(new[] { baseKey.Value, variantKey.Value }));
        }

        private static CultNetDatabase Replica(CultCache cache, CultNetDocumentRegistry? documents, string schemaId) =>
            new(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = documents,
                Shards =
                [
                    new CultNetShardDescriptor("replica-shard", "runtime-a", epoch: 3, isPrimary: false, schemaIds: [schemaId])
                ]
            });

        [Test]
        public async Task AReplicaLogHoldsOnlyThePrimarysSequencesWhateverItResolvesLocally()
        {
            var (sourceCache, sourceDocuments) = VariantCache();
            var (replicaCache, replicaDocuments) = VariantCache();
            var schemaId = sourceCache.Registry.GetRequired<VariantWireFixture>().SchemaId;
            var source = new CultNetDatabase(sourceCache, new CultNetDatabaseOptions
            {
                DocumentRegistry = sourceDocuments,
                Shards = [new CultNetShardDescriptor("replica-shard", "runtime-a", epoch: 3, isPrimary: true, schemaIds: [schemaId])]
            });
            var replica = Replica(replicaCache, replicaDocuments, schemaId);
            var baseKey = new CultRecordKey("publish-variant-base");
            var variantKey = new CultRecordKey("publish-variant-big");
            await replicaCache.UpsertAsync(new VariantWireFixture { Name = "base", Power = 1 }, new CultRecordHandle<VariantWireFixture>(baseKey));
            await replicaCache.UpsertVariantAsync(variantKey, baseKey, new[]
            {
                replicaCache.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big")
            });
            await source.PutAsync(baseKey, new VariantWireFixture { Name = "base edited", Power = 2 });

            await replica.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
            {
                ShardId = "replica-shard",
                ShardEpoch = 3,
                Entries = source.GetMutationLogMessages("replica-shard").ToArray()
            });

            Assert.That(replicaCache.Get<VariantWireFixture>(variantKey)!.Power, Is.EqualTo(2), "the dependent re-resolved");
            Assert.That(replica.GetMutationLog("replica-shard").Select(entry => entry.Sequence), Is.EqualTo(new[] { 1L }),
                "the replica log holds the primary's entry and mints none of its own");
        }

        [Test]
        public async Task ALocalCacheWriteOnAReplicaIsPublishedOnceAndNeverLogged()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;
            var replica = Replica(cache, null, schemaId);
            var published = Record(replica, KeyOfChange);

            await cache.UpsertAsync(Note("local"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("tests:publish:local")));

            Assert.That(published, Is.EqualTo(new[] { "tests:publish:local" }));
            Assert.That(replica.GetMutationLog("replica-shard"), Is.Empty);
        }

        [Test]
        public void ConcurrentWritersLoseNoLogEntryAndReuseNoSequence()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            const int writers = 4, perWriter = 5000;

            Parallel.For(0, writers, writer =>
            {
                for (var i = 0; i < perWriter; i++)
                    cache.UpsertAsync(Note("n"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey($"tests:publish:c:{writer}:{i}")))
                        .GetAwaiter().GetResult();
            });

            var sequences = database.GetMutationLog(database.Shards[0].ShardId).Select(entry => entry.Sequence).ToArray();
            Assert.That(sequences, Has.Length.EqualTo(writers * perWriter));
            Assert.That(sequences.Distinct().Count(), Is.EqualTo(writers * perWriter));
        }

        [Test]
        public async Task AWriteAfterADatabaseDeleteIsStillPublishedAndLogged()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var key = new CultRecordKey("tests:publish:after-delete");
            await database.PutAsync(key, Note("one"));
            await database.DeleteAsync<NetworkSchemaNote>(key);
            var published = Record(database, KeyOfChange);

            await cache.UpsertAsync(Note("two"), new CultRecordHandle<NetworkSchemaNote>(key));

            Assert.That(published, Is.EqualTo(new[] { key.Value }));
            Assert.That(database.GetMutationLog(database.Shards[0].ShardId).Select(entry => entry.Kind),
                Is.EqualTo(new[]
                {
                    CultNetDatabaseChangeKind.Added, CultNetDatabaseChangeKind.Removed, CultNetDatabaseChangeKind.Added
                }));
        }

        [Test]
        public async Task AShardSnapshotResyncPublishesItsRemovalsAndPutsOnceAndLogsNothing()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;
            var replica = Replica(cache, null, schemaId);
            var stale = new CultRecordKey("tests:publish:stale");
            var fresh = new CultRecordKey("tests:publish:fresh");
            await cache.UpsertAsync(Note("stale"), new CultRecordHandle<NetworkSchemaNote>(stale));
            var published = Record(replica, change => ((dynamic)change).Kind.ToString() + ":" + ((dynamic)change).Key.Value);
            var put = replica.Documents.CreateRawDocumentPutMessage(
                "snapshot-fresh", new CultRecordHandle<NetworkSchemaNote>(fresh), Note("fresh"));

            await replica.ApplyShardSnapshotResponseAsync(replica.Shards[0], new CultNetSnapshotResponseRawMessage
            {
                ShardId = "replica-shard",
                ShardEpoch = 3,
                Documents = [put.Document!],
                ShardLogSequence = 5
            });

            Assert.That(published, Is.EqualTo(new[] { "Removed:" + stale.Value, "Added:" + fresh.Value }));
            Assert.That(replica.GetMutationLog("replica-shard"), Is.Empty);
        }

        [Test]
        public async Task ADisposedDatabaseNoLongerObservesTheCache()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var published = Record(database, KeyOfChange);
            database.Dispose();

            await cache.UpsertAsync(Note("after"), new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("tests:publish:disposed")));

            Assert.That(published, Is.Empty);
        }
    }
}
