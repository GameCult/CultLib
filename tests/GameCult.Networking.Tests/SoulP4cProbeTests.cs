#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using NUnit.Framework;
using R3;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    // Soul pass 4c scratch probes (not for merge).
    public sealed class SoulP4cProbeTests
    {
        private const string ShardId = "soulc-shard";
        private static readonly CultRecordKey One = new("soulc:one");
        private static readonly CultRecordKey Unowned = new("soulc:unowned");
        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };
        private static string SchemaId(CultCache cache) => cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;

        private sealed class RefusingStore : ICultNetShardMutationLogStore
        {
            public IReadOnlyList<CultNetShardLogEntryMessage> Read(string shardId, long afterSequence = 0, int? limit = null) => Array.Empty<CultNetShardLogEntryMessage>();
            public void Append(string shardId, CultNetShardLogEntryMessage entry) => throw new IOException("refused " + entry.Sequence);
            public long GetCompactedThrough(string shardId) => 0;
            public void CompactThrough(string shardId, long sequence) { }
        }

        private sealed class Logger : GameCult.Logging.ILogger
        {
            public List<string> Errors { get; } = new();
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) => Errors.Add(message);
            public void LogDebug(string message) { }
        }

        private static CultNetDatabase Db(CultCache cache, bool primary, ICultNetShardMutationLogStore? store) =>
            new(cache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(ShardId, "primary", epoch: 1, isPrimary: primary, schemaIds: [SchemaId(cache)])],
                MutationLogStore = store
            });

        // Q1: the server's own remote-put path is a writer. A put that committed and could not be logged is answered as a
        // failure: the peer is told its write failed, while the cache holds it and it was published.
        [Test, Category("SoulFinding")]
        public async Task Q1_ServerAnswersACommittedButUnloggedRemotePutAsAFailure()
        {
            var cache = new CultCache();
            var database = Db(cache, primary: true, new RefusingStore());
            using var server = new Server(cache, ServerSecurityOptions.Development());
            var logger = new Logger();
            server.Logger = logger;
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var handler = (Func<CultNetDocumentPutRawMessage, CultNetServerPeer, Task>)typeof(CultNetDatabaseServer)
                .GetField("_putHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(databaseServer)!;
            var peer = (CultNetServerPeer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CultNetServerPeer));
            var message = database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<NetworkSchemaNote>(One), Note("wire"));
            message.ShardId = ShardId;
            message.ShardEpoch = 1;

            try { await handler(message, peer); } catch (Exception) { }

            TestContext.WriteLine("errors: " + string.Join(" | ", logger.Errors));
            Assert.That(cache.Get<NetworkSchemaNote>(One)?.Text, Is.EqualTo("wire"), "the put committed");
            Assert.That(logger.Errors, Has.None.Contains("raw put failed"), "a committed put must not be reported to the peer as a failed one");
        }

        // Q2: reads serve a cached row no shard owns, the database watch never publishes its changes: a view built from
        // GetAll + Watch (CultMesh Collection, the subscription server's snapshot + WatchAllChanges) goes stale.
        [Test, Category("SoulFinding")]
        public async Task Q2_ReadsServeAnUnownedRowWhoseChangesTheWatchNeverPublishes()
        {
            var cache = new CultCache();
            var database = Db(cache, primary: true, store: null);
            var seen = new List<string>();
            database.Watch<MeshQuickstartNote>().Subscribe(change => seen.Add(change.Document?.Body ?? "<removed>"));
            await cache.UpsertAsync(new MeshQuickstartNote { NoteId = "u", Body = "v1" }, new CultRecordHandle<MeshQuickstartNote>(Unowned));
            await cache.UpsertAsync(new MeshQuickstartNote { NoteId = "u", Body = "v2" }, new CultRecordHandle<MeshQuickstartNote>(Unowned));

            var read = database.GetAll<MeshQuickstartNote>().Select(note => note.Body).ToArray();
            TestContext.WriteLine($"GetAll={string.Join(",", read)} watch={string.Join(",", seen)}");
            Assert.That(read.Length == 0 || seen.Count > 0, Is.True, "a row the database reads out must be one whose changes it publishes");
        }

        // Q3: a replica's log apply door writes a row its shard does not own. The snapshot apply path filters by
        // ShardMatchesRawDocument; the log path does not.
        [Test, Category("SoulFinding")]
        public async Task Q3_AReplicaLogApplyWritesARowItsShardDoesNotOwn()
        {
            var cache = new CultCache();
            var replica = Db(cache, primary: false, store: null);
            var put = replica.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(Unowned), new MeshQuickstartNote { NoteId = "r", Body = "replicated" });
            put.ShardId = ShardId;
            put.ShardEpoch = 1;
            Exception? thrown = null;
            try
            {
                await replica.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
                {
                    MessageId = "log",
                    ShardId = ShardId,
                    ShardEpoch = 1,
                    Entries = [new CultNetShardLogEntryMessage { Sequence = 1, CommittedAt = DateTimeOffset.UtcNow.ToString("O"), ChangeKind = "added", Put = put }]
                });
            }
            catch (Exception exception) { thrown = exception; }

            TestContext.WriteLine($"thrown={thrown?.GetType().Name}: {thrown?.Message}; cached={cache.Get<MeshQuickstartNote>(Unowned)?.Body}; log={replica.GetMutationLog(ShardId).Count}");
            Assert.That(cache.Get<MeshQuickstartNote>(Unowned), Is.Null, "no shard owns it here, so no path writes it");
        }

        // Q4 (pin): the unowned refusal is not a shard-authority exception, so the server's routing path does not see it.
        [Test, Category("SoulPin")]
        public void Q4_TheUnownedRefusalIsNotAShardAuthorityException()
        {
            var cache = new CultCache();
            var database = Db(cache, primary: true, store: null);
            var put = database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(Unowned), new MeshQuickstartNote { NoteId = "w", Body = "w" });
            var refusal = Assert.ThrowsAsync<CultNetUnownedSchemaException>(async () => await database.ApplyPutAsync(put))!;
            Assert.That(refusal, Is.Not.InstanceOf<CultNetShardAuthorityException>());
        }

        // Q5 (pin): two databases over one cache, both refusing: a writer's catch (CultNetShardLogException) misses.
        [Test, Category("SoulPin")]
        public void Q5_TwoDatabasesOnOneCacheAggregateTheirLogFailures()
        {
            var cache = new CultCache();
            _ = Db(cache, primary: true, new RefusingStore());
            _ = Db(cache, primary: true, new RefusingStore());
            var failure = Assert.Throws<AggregateException>(() => cache.Commit(batch =>
                batch.Upsert(Note("x"), new CultRecordHandle<NetworkSchemaNote>(One))))!;
            Assert.That(failure.InnerExceptions.OfType<CultNetShardLogException>().Count(), Is.EqualTo(2));
        }
    }
}
