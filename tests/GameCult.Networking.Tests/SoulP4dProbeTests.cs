#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using NUnit.Framework;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    // Soul pass 4d probes. They guard the replica log relay (a skipped entry is relayed as the primary sent it, in memory and on
    // disk), key-prefixed shard reads, and measure two costs; the measurements are explicit.
    public sealed class SoulP4dProbeTests
    {
        private const string ShardId = "soupd-shard";
        private static readonly string Now = DateTimeOffset.UtcNow.ToString("O");

        private static string NoteSchema(CultCache cache) => cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;
        private static string MeshSchema(CultCache cache) => cache.Registry.GetRequired<MeshQuickstartNote>().SchemaId;

        private static CultNetDatabase Db(CultCache cache, bool primary, string[] schemas, string? keyPrefix = null, ICultNetShardMutationLogStore? store = null) =>
            new(cache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(ShardId, "primary", epoch: 1, isPrimary: primary, schemaIds: schemas, keyPrefix: keyPrefix)],
                MutationLogStore = store
            });

        private static CultNetShardLogResponseMessage UnownedPutLog(CultNetDatabase anyDb, string key)
        {
            var put = anyDb.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(new CultRecordKey(key)), new MeshQuickstartNote { NoteId = "p", Body = "primary-put" });
            put.ShardId = ShardId;
            put.ShardEpoch = 1;
            return new CultNetShardLogResponseMessage
            {
                MessageId = "log",
                ShardId = ShardId,
                ShardEpoch = 1,
                Entries = [new CultNetShardLogEntryMessage { Sequence = 1, CommittedAt = Now, ChangeKind = "added", Put = put }]
            };
        }

        // Q3a: a replica (no store) that skips an unowned put relays it to a chained replica as a delete.
        [Test, Category("SoulFinding")]
        public async Task Q3a_MemoryLogRelaysASkippedPutAsARemoval()
        {
            var cache = new CultCache();
            var replica = Db(cache, primary: false, [NoteSchema(cache)]);
            await replica.ApplyShardLogResponseAsync(UnownedPutLog(replica, "relay:k"));
            var relayed = replica.GetMutationLogMessages(ShardId);
            TestContext.Out.WriteLine($"memory relay: kind={relayed[0].ChangeKind} put={relayed[0].Put != null} delete={relayed[0].Delete != null}");
            Assert.That(relayed[0].ChangeKind, Is.EqualTo("added"), "the relayed entry keeps the primary's kind");
        }

        // Q3b: the same replica with a durable store relays the primary's put (disk and memory disagree).
        [Test, Category("SoulFinding")]
        public async Task Q3b_StoreBackedLogRelaysTheSkippedPutAsAPut()
        {
            var dir = Path.Combine(Path.GetTempPath(), "soulp4d-" + Guid.NewGuid().ToString("N"));
            var cache = new CultCache();
            var replica = Db(cache, primary: false, [NoteSchema(cache)], store: new CultNetFileShardMutationLogStore(dir));
            await replica.ApplyShardLogResponseAsync(UnownedPutLog(replica, "relay:k"));
            var relayed = replica.GetMutationLogMessages(ShardId);
            var memory = replica.GetMutationLog(ShardId);
            TestContext.Out.WriteLine($"store relay: kind={relayed[0].ChangeKind} put={relayed[0].Put != null}; memory entry kind={memory[0].Kind} doc={(memory[0].Document == null ? "null" : "set")}");
            Assert.That(relayed[0].ChangeKind, Is.EqualTo("added"));
        }

        // Q3c: a chained replica that does own the row (config drift) and holds it deletes it on the relayed entry.
        [Test, Category("SoulFinding")]
        public async Task Q3c_AChainedReplicaThatOwnsTheRowDeletesWhatThePrimaryPut()
        {
            var cacheA = new CultCache();
            var a = Db(cacheA, primary: false, [NoteSchema(cacheA)]);
            await a.ApplyShardLogResponseAsync(UnownedPutLog(a, "relay:k"));

            var cacheC = new CultCache();
            var c = Db(cacheC, primary: false, [NoteSchema(cacheC), MeshSchema(cacheC)]);
            await cacheC.UpsertAsync(new MeshQuickstartNote { NoteId = "p", Body = "earlier" }, new CultRecordHandle<MeshQuickstartNote>(new CultRecordKey("relay:k")));
            await c.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
            {
                MessageId = "chain",
                ShardId = ShardId,
                ShardEpoch = 1,
                Entries = a.GetMutationLogMessages(ShardId).ToArray()
            });
            var row = cacheC.Get<MeshQuickstartNote>(new CultRecordKey("relay:k"));
            TestContext.Out.WriteLine($"chained row after relay: {row?.Body ?? "<deleted>"}");
            Assert.That(row, Is.Not.Null, "the primary put this row; a relay must not delete it");
        }

        // A skipped entry is relayed exactly as the primary sent it, whether the replica keeps its log in memory or on disk.
        [Test]
        public async Task SkippedEntriesAreRelayedIdenticallyFromMemoryAndFromDisk()
        {
            var dir = Path.Combine(Path.GetTempPath(), "soulp4d-parity-" + Guid.NewGuid().ToString("N"));
            try
            {
                var memoryCache = new CultCache();
                var inMemory = Db(memoryCache, primary: false, [NoteSchema(memoryCache)]);
                var diskCache = new CultCache();
                var onDisk = Db(diskCache, primary: false, [NoteSchema(diskCache)], store: new CultNetFileShardMutationLogStore(dir));
                var log = UnownedPutLog(inMemory, "relay:k");
                var removal = new CultNetShardLogEntryMessage
                {
                    Sequence = 2,
                    CommittedAt = Now,
                    ChangeKind = "removed",
                    Delete = new CultNetDocumentDeleteMessage
                    {
                        MessageId = "d",
                        SchemaId = memoryCache.Registry.GetRequired<MeshQuickstartNote>().SchemaId,
                        RecordKey = "relay:k",
                        ShardId = ShardId,
                        ShardEpoch = 1
                    }
                };
                log.Entries = [log.Entries[0], removal];

                await inMemory.ApplyShardLogResponseAsync(log);
                await onDisk.ApplyShardLogResponseAsync(log);

                var memory = inMemory.GetMutationLogMessages(ShardId);
                var disk = onDisk.GetMutationLogMessages(ShardId);
                Assert.That(memory.Select(e => e.ChangeKind), Is.EqualTo(new[] { "added", "removed" }));
                Assert.That(memory.Select(e => e.ChangeKind), Is.EqualTo(disk.Select(e => e.ChangeKind)));
                Assert.That(memory.Select(e => e.Put != null), Is.EqualTo(new[] { true, false }));
                Assert.That(memory.Select(e => e.Put != null), Is.EqualTo(disk.Select(e => e.Put != null)));
                Assert.That(memory.Select(e => e.Delete != null), Is.EqualTo(new[] { false, true }));
                Assert.That(memory.Select(e => e.Delete != null), Is.EqualTo(disk.Select(e => e.Delete != null)));
                Assert.That(memory[0].Put!.Document!.RecordKey, Is.EqualTo(disk[0].Put!.Document!.RecordKey));
                Assert.That(memory[0].Put!.Document!.Payload, Is.EqualTo(disk[0].Put!.Document!.Payload), "the document bytes are the primary's");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // Q2: key-prefixed shard: the schema is owned, a key outside the prefix is not.
        [Test, Category("SoulPin")]
        public async Task Q2_KeyPrefixedReadsServeOnlyTheOwnedKeys()
        {
            var cache = new CultCache();
            var database = Db(cache, primary: true, [NoteSchema(cache)], keyPrefix: "a:");
            await cache.UpsertAsync(new NetworkSchemaNote { Text = "a" }, new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("a:1")));
            await cache.UpsertAsync(new NetworkSchemaNote { Text = "b" }, new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("b:1")));
            Assert.That(database.GetAll<NetworkSchemaNote>().Select(n => n.Text), Is.EqualTo(new[] { "a" }));
            Assert.That(await database.GetAsync<NetworkSchemaNote>(new CultRecordKey("b:1")), Is.Null);
            Assert.That(await database.GetAsync<NetworkSchemaNote>(new CultRecordKey("a:1")), Is.Not.Null);
            using var server = new Server(cache, ServerSecurityOptions.Development());
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var snapshot = databaseServer.CreateSnapshotResponse(new CultNetSnapshotRequestMessage { MessageId = "all" });
            Assert.That(snapshot.Documents.Select(d => d.RecordKey), Is.EqualTo(new[] { "a:1" }));
        }

        [Test, Category("SoulPin")]
        public async Task Q2_PrimaryAllServesEveryCachedRow()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { Shards = [CultNetShardDescriptor.PrimaryAll()] });
            await cache.UpsertAsync(new MeshQuickstartNote { NoteId = "x", Body = "x" }, new CultRecordHandle<MeshQuickstartNote>(new CultRecordKey("any:1")));
            Assert.That(database.GetAll<object>().Count(), Is.EqualTo(1));
            Assert.That(database.GetByName<MeshQuickstartNote>("x"), Is.Not.Null);
        }

        // Q2 cost: GetAll through the database versus the cache, scoped shard (reflection per row).
        [Test, Category("SoulMeasure"), Explicit("Measurement, no assertion.")]
        public void Q2_GetAllCost()
        {
            var cache = new CultCache();
            var database = Db(cache, primary: false, [NoteSchema(cache)], keyPrefix: "n:");
            cache.Commit(batch =>
            {
                for (var i = 0; i < 20000; i++)
                    batch.Upsert(new NetworkSchemaNote { Text = "t" + i }, new CultRecordHandle<NetworkSchemaNote>(new CultRecordKey("n:" + i)));
            });
            _ = cache.GetAll<NetworkSchemaNote>().Count();
            _ = database.GetAll<NetworkSchemaNote>().Count();
            var sw = Stopwatch.StartNew();
            for (var r = 0; r < 5; r++) _ = cache.GetAll<NetworkSchemaNote>().Count();
            var cacheMs = sw.Elapsed.TotalMilliseconds / 5;
            sw.Restart();
            for (var r = 0; r < 5; r++) _ = database.GetAll<NetworkSchemaNote>().Count();
            var dbMs = sw.Elapsed.TotalMilliseconds / 5;
            TestContext.Out.WriteLine($"GetAll 20000 rows: cache={cacheMs:F2}ms database={dbMs:F2}ms ratio={dbMs / Math.Max(cacheMs, 0.001):F1}x");
        }

        // Wire: the bytes a peer receives for the unowned refusal.
        [Test, Category("SoulMeasure"), Explicit("Prints the wire bytes the Rust captured-bytes case pins.")]
        public void Wire_UnownedSchemaBytes()
        {
            var message = CultNetErrorMessage.ForUnownedSchema(new CultNetUnownedSchemaException("some.schema.v1", new CultRecordKey("some:key")));
            var bytes = MessagePack.MessagePackSerializer.Serialize(message, CultNetSchemaMessageSerialization.Options);
            TestContext.Out.WriteLine("HEX=" + Convert.ToHexString(bytes));
            TestContext.Out.WriteLine("JSON=" + MessagePack.MessagePackSerializer.ConvertToJson(bytes));
        }
    }
}
