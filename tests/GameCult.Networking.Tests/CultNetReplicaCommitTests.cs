#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using R3;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // variants:ruling:replica-commit-grouped-changes. The authoritative cache judges a commit on its result; a replica applying the
    // changes one record at a time would judge each intermediate state, where a swap of two [CultIndex] values holds two holders.
    // The subscription server tags the changes of one cache commit, the client applies them as one commit, and a replica that
    // refuses a commit says so. No transport: the server and client are joined in memory, and every message crosses MessagePack.
    public sealed class CultNetReplicaCommitTests
    {
        [CultDocument("tests.replica_commit_item", "tests.replica_commit_item.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ReplicaItem
        {
            [Key(0)]
            [CultIndex("sku")]
            public string Sku = string.Empty;

            [Key(1)]
            public string Note = string.Empty;
        }

        // The change message as a peer from before commit identity knew it.
        [MessagePackObject]
        public sealed class LegacyChange
        {
            [Key("schemaVersion")] public string SchemaVersion { get; set; } = string.Empty;
            [Key("messageId")] public string MessageId { get; set; } = string.Empty;
            [Key("subscriptionId")] public string SubscriptionId { get; set; } = string.Empty;
            [Key("changeKind")] public string ChangeKind { get; set; } = string.Empty;
            [Key("document")] public CultNetRawDocumentRecord? Document { get; set; }
            [Key("recordKey")] public string? RecordKey { get; set; }
            [Key("schemaId")] public string? SchemaId { get; set; }
        }

        private sealed class LoopbackServer : ICultNetSchemaServer
        {
            private readonly Dictionary<Type, Delegate> _handlers = new();
            public void OnCultNet<T>(Func<T, ICultNetSchemaServerPeer, Task> callback) where T : ICultNetSchemaMessage => _handlers[typeof(T)] = callback;
            public void RemoveCultNetMessageListener<T>(Delegate callback) where T : ICultNetSchemaMessage => _handlers.Remove(typeof(T));
            public Task Dispatch<T>(T message, ICultNetSchemaServerPeer peer) where T : ICultNetSchemaMessage =>
                ((Func<T, ICultNetSchemaServerPeer, Task>)_handlers[typeof(T)])(message, peer);
        }

        private sealed class Client : ICultNetSchemaClient
        {
            private readonly LoopbackServer _server;
            private readonly Dictionary<Type, List<Delegate>> _handlers = new();
            private readonly Peer _peer;
            public Client(LoopbackServer server, Peer peer) { _server = server; _peer = peer; peer.Client = this; }
            public bool Connected => true;
            public void Connect(string host, int port) { }
            public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
            {
                if (message is CultNetDatabaseSubscribeMessage subscribe)
                    _server.Dispatch(subscribe, _peer).GetAwaiter().GetResult();
                else if (message is CultNetDatabaseUnsubscribeMessage unsubscribe)
                    _server.Dispatch(unsubscribe, _peer).GetAwaiter().GetResult();
            }
            public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) _handlers[typeof(T)] = list = new List<Delegate>();
                list.Add(callback);
            }
            public void Emit<T>(T message) where T : ICultNetSchemaMessage
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                    foreach (var handler in list.ToArray()) ((Action<T>)handler)(message);
            }
            public void Dispose() { }
        }

        private sealed class Peer : ICultNetSchemaServerPeer
        {
            public Client? Client;
            public ConcurrentQueue<byte[]> Sent { get; } = new();
            public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
            {
                // The wire: serialized, then read back as the receiver would.
                var bytes = CultNetSchemaMessageSerialization.Serialize(message);
                Sent.Enqueue(bytes);
                Client!.Emit(CultNetSchemaMessageSerialization.Deserialize<T>(bytes));
            }
            public CultNetDatabaseChangeRawMessage[] Changes => Sent
                .Select(bytes => CultNetSchemaMessageSerialization.Deserialize(bytes))
                .OfType<CultNetDatabaseChangeRawMessage>().ToArray();
        }

        private sealed class Replication : IDisposable
        {
            public CultCache Source { get; } = new();
            public CultCache Replica { get; } = new();
            public Peer Peer { get; } = new();
            public CultNetDatabaseSubscriptionClient Client { get; }
            public List<Exception> Faults { get; } = new();
            public CultNetDatabase Database { get; }
            private readonly CultNetDatabaseSubscriptionServer _server;

            public Replication(Action<CultCache>? seedSource = null, Action<CultCache>? seedReplica = null)
            {
                seedSource?.Invoke(Source);
                seedReplica?.Invoke(Replica);
                Database = new CultNetDatabase(Source);
                var wire = new LoopbackServer();
                _server = new CultNetDatabaseSubscriptionServer(wire, Database);
                var documents = new CultNetDocumentRegistry(Replica.Registry)
                    .Register(CultNetDocumentBinding.ForDocument<ReplicaItem>(Replica.Registry));
                Client = new CultNetDatabaseSubscriptionClient(new Client(wire, Peer), Replica, documents);
                Client.Faulted += Faults.Add;
                Client.SubscribeAsync("items", schemaIds: new[] { Source.Registry.GetRequired<ReplicaItem>().SchemaId })
                    .GetAwaiter().GetResult();
            }

            public ReplicaItem? ReplicaItem(string key) => Replica.Get<ReplicaItem>(new CultRecordKey(key));

            public void Dispose()
            {
                Client.Dispose();
                _server.Dispose();
                Database.Dispose();
            }
        }

        private static ReplicaItem Item(string sku) => new() { Sku = sku };

        private static void Stage(CultCache cache, params (string Key, string Sku)[] items) =>
            Assert.That(cache.Commit(batch =>
            {
                foreach (var (key, sku) in items)
                    batch.Upsert(typeof(ReplicaItem), Item(sku), new CultRecordKey(key));
            }), Is.True);

        private static async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The replica did not reach the expected state.");
                await Task.Delay(5);
            }
        }

        [Test]
        public async Task ASwapOfTwoIndexValuesReachesTheReplicaIntact()
        {
            using var run = new Replication(source => Stage(source, ("a", "one"), ("b", "two")));
            Assert.That(run.ReplicaItem("a")!.Sku, Is.EqualTo("one"));

            Stage(run.Source, ("a", "two"), ("b", "one"));
            await Until(() => run.ReplicaItem("a")!.Sku == "two");

            Assert.That(run.ReplicaItem("b")!.Sku, Is.EqualTo("one"));
            Assert.That(run.Client.Fault, Is.Null);
            Assert.That(run.Faults, Is.Empty);
            Assert.That(run.Replica.GetByIndex<ReplicaItem>("sku", "two"), Is.SameAs(run.ReplicaItem("a")));
            var swap = run.Peer.Changes;
            Assert.That(swap.Select(change => change.CommitId).Distinct().Single(), Is.Not.Null, "the commit is named on the wire");
            Assert.That(swap.Select(change => change.CommitCount), Is.All.EqualTo(2));
        }

        [Test]
        public async Task AValueMovedToANewRecordWhileItsOldHolderIsRemovedReachesTheReplica()
        {
            using var run = new Replication(source => Stage(source, ("a", "one")));

            Assert.That(run.Source.Commit(batch =>
            {
                batch.Remove(new CultRecordKey("a"));
                batch.Upsert(typeof(ReplicaItem), Item("one"), new CultRecordKey("c"));
            }), Is.True);
            await Until(() => run.ReplicaItem("c") != null);

            Assert.That(run.ReplicaItem("a"), Is.Null);
            Assert.That(run.Client.Fault, Is.Null);
            Assert.That(run.Replica.GetByIndex<ReplicaItem>("sku", "one"), Is.SameAs(run.ReplicaItem("c")));
        }

        [Test]
        public async Task ACommitOfOneChangeCarriesNoCommitFieldsAndIsApplied()
        {
            using var run = new Replication();

            Stage(run.Source, ("a", "one"));
            await Until(() => run.ReplicaItem("a") != null);

            var change = run.Peer.Changes.Single();
            Assert.That(change.CommitId, Is.Null);
            Assert.That(change.CommitCount, Is.Null);
        }

        [Test]
        public async Task AReplicaThatRefusesACommitFaultsLoudlyAndAppliesNothingMore()
        {
            // The replica holds a record the source does not, under the value the source now gives to another record: a conflict
            // the replica cannot resolve, not an intermediate state.
            using var run = new Replication(seedReplica: replica => Stage(replica, ("local", "taken")));

            Stage(run.Source, ("a", "taken"), ("b", "free"));
            await Until(() => run.Client.Fault != null);

            Assert.That(run.Faults, Has.Count.EqualTo(1));
            Assert.That(run.Faults[0], Is.SameAs(run.Client.Fault));
            Assert.That(run.Faults[0].Message, Does.Contain("sku"));
            Assert.That(run.ReplicaItem("a"), Is.Null, "the refused commit is not partly applied");
            Assert.That(run.ReplicaItem("b"), Is.Null, "the refused commit is not partly applied");

            Stage(run.Source, ("c", "later"));
            await Task.Delay(100);
            Assert.That(run.ReplicaItem("c"), Is.Null, "a replica known to differ takes nothing on top");
            Assert.That(run.Faults, Has.Count.EqualTo(1));
        }

        [Test]
        public void AnOlderPeerReadsTheChangeAndIgnoresTheCommitFields()
        {
            var tagged = new CultNetDatabaseChangeRawMessage
            {
                MessageId = "m",
                SubscriptionId = "items",
                ChangeKind = "removed",
                RecordKey = "a",
                SchemaId = "tests.replica_commit_item.v1",
                CommitId = "7",
                CommitCount = 2
            };

            var legacy = MessagePackSerializer.Deserialize<LegacyChange>(
                CultNetSchemaMessageSerialization.Serialize(tagged), CultNetSchemaMessageSerialization.Options);

            Assert.That(legacy.ChangeKind, Is.EqualTo("removed"));
            Assert.That(legacy.RecordKey, Is.EqualTo("a"));
            Assert.That(legacy.SubscriptionId, Is.EqualTo("items"));
        }

        [Test]
        public async Task ASwapFromAServerThatDoesNotTagCommitsStillFaultsInsteadOfDiverging()
        {
            // An older server sends the two halves of a swap untagged: each is judged alone and refused. The refusal is surfaced.
            using var run = new Replication(source => Stage(source, ("a", "one"), ("b", "two")));
            var raw = (string key, string sku) => run.Database.Documents.ToRawRecord(
                run.Source.Registry.GetRequired<ReplicaItem>(), new CultRecordKey(key), Item(sku), "2026-01-01T00:00:00Z");

            run.Peer.Client!.Emit(new CultNetDatabaseChangeRawMessage
            {
                MessageId = "x", SubscriptionId = "items", ChangeKind = "updated", Document = raw("a", "two")
            });
            await Until(() => run.Client.Fault != null);

            Assert.That(run.ReplicaItem("a")!.Sku, Is.EqualTo("one"));
        }

        [Test]
        public async Task ARotationOfThreeValuesReachesTheReplicaIntact()
        {
            using var run = new Replication(source => Stage(source, ("a", "one"), ("b", "two"), ("c", "three")));

            Stage(run.Source, ("a", "two"), ("b", "three"), ("c", "one"));
            await Until(() => run.ReplicaItem("a")!.Sku == "two");

            Assert.That(new[] { "a", "b", "c" }.Select(key => run.ReplicaItem(key)!.Sku), Is.EqualTo(new[] { "two", "three", "one" }));
            Assert.That(run.Client.Fault, Is.Null);
            Assert.That(run.Peer.Changes.Select(change => change.CommitCount), Is.All.EqualTo(3));
        }

        // The database server is the other producer of change messages; it names a commit the same way.
        [Test]
        public void TheDatabaseServerTagsTheChangesOfOneCommitAndNotALoneChange()
        {
            var cache = new CultCache();
            Stage(cache, ("a", "one"), ("b", "two"));
            using var database = new CultNetDatabase(cache);
            using var server = new Server(cache, ServerSecurityOptions.Development());
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var commits = new List<CultNetDatabaseCommit>();
            using var watch = database.WatchCommits().Subscribe(commits.Add);
            var selection = new CultNetSelection
            {
                Schemas = new[] { cache.Registry.GetRequired<ReplicaItem>().SchemaId },
                Projection = CultNetSelectionProjections.Document
            };
            IReadOnlyList<CultNetDatabaseChangeRawMessage> Deliver(CultNetDatabaseCommit commit) =>
                (IReadOnlyList<CultNetDatabaseChangeRawMessage>)typeof(CultNetDatabaseServer)
                    .GetMethod("DeliverCommit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(databaseServer, new object[] { commit, "sub", selection, new HashSet<string>(), (Action<CultNetErrorMessage>)(_ => { }) })!;

            Stage(cache, ("a", "two"), ("b", "one"));
            Stage(cache, ("c", "three"));

            var swap = Deliver(commits[0]);
            Assert.That(swap, Has.Count.EqualTo(2));
            Assert.That(swap.Select(change => change.CommitId).Distinct().Single(), Is.EqualTo(commits[0].Id));
            Assert.That(swap.Select(change => change.CommitCount), Is.All.EqualTo(2));
            var lone = Deliver(commits[1]).Single();
            Assert.That(lone.CommitId, Is.Null);
            Assert.That(lone.CommitCount, Is.Null);
            Assert.That(commits[0].Id, Is.Not.EqualTo(commits[1].Id));
        }

        [Test]
        public void ASnapshotThatSwapsIndexValuesAgainstWhatTheReplicaHoldsIsApplied()
        {
            using var run = new Replication(
                source => Stage(source, ("a", "two"), ("b", "one")),
                replica => Stage(replica, ("a", "one"), ("b", "two")));

            Assert.That(run.ReplicaItem("a")!.Sku, Is.EqualTo("two"));
            Assert.That(run.ReplicaItem("b")!.Sku, Is.EqualTo("one"));
        }

        // Hand-made wire messages: what a peer sends that the real server would not.
        private static CultNetDatabaseChangeRawMessage Put(Replication run, string subscription, string key, string sku, string? commit, int count, string? id = null) =>
            new()
            {
                MessageId = id ?? Guid.NewGuid().ToString("N"),
                SubscriptionId = subscription,
                ChangeKind = "updated",
                Document = run.Database.Documents.ToRawRecord(
                    run.Source.Registry.GetRequired<ReplicaItem>(), new CultRecordKey(key), Item(sku), "2026-01-01T00:00:00Z"),
                CommitId = commit,
                CommitCount = commit == null ? null : count
            };

        private static CultNetDatabaseChangeRawMessage Removal(string subscription, string key, string commit, int count) =>
            new()
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscription,
                ChangeKind = "removed",
                RecordKey = key,
                SchemaId = "tests.replica_commit_item.v1",
                CommitId = commit,
                CommitCount = count
            };

        [Test]
        public async Task TwoSubscriptionsMayUseOneCommitIdWithoutJoiningTheirCommits()
        {
            using var run = new Replication(source => Stage(source, ("a", "one"), ("b", "two")));
            var emit = run.Peer.Client!;

            emit.Emit(Put(run, "items", "a", "two", "9", 2));
            emit.Emit(Put(run, "other", "x", "ex", "9", 2));
            await Task.Delay(100);
            Assert.That(run.ReplicaItem("a")!.Sku, Is.EqualTo("one"), "one change of each of two commits is not a commit");
            Assert.That(run.ReplicaItem("x"), Is.Null);

            emit.Emit(Put(run, "items", "b", "one", "9", 2));
            await Until(() => run.ReplicaItem("a")!.Sku == "two");
            Assert.That(run.ReplicaItem("b")!.Sku, Is.EqualTo("one"));
            Assert.That(run.ReplicaItem("x"), Is.Null);
            Assert.That(run.Client.Fault, Is.Null);
        }

        [Test]
        public async Task ARemovalAfterTheFirstChangeOfACommitIsApplied()
        {
            using var run = new Replication(source => Stage(source, ("a", "one")));
            var emit = run.Peer.Client!;

            emit.Emit(Put(run, "items", "c", "one", "5", 2));
            emit.Emit(Removal("items", "a", "5", 2));
            await Until(() => run.ReplicaItem("c") != null);

            Assert.That(run.ReplicaItem("a"), Is.Null);
            Assert.That(run.Client.Fault, Is.Null);
        }

        [Test]
        public void ASnapshotOfManyDocumentsAppliesEveryOne()
        {
            using var run = new Replication(source => Stage(source, ("a", "one"), ("b", "two"), ("c", "three"), ("d", "four")));

            Assert.That(new[] { "a", "b", "c", "d" }.Select(key => run.ReplicaItem(key)?.Sku), Is.EqualTo(new[] { "one", "two", "three", "four" }));
        }

        [Test]
        public async Task ManyCommitsInARowAreNotMistakenForAnUnboundedBuffer()
        {
            using var run = new Replication(source => Stage(source, ("a", "s0"), ("b", "s1")));

            for (var round = 1; round <= 80; round++)
            {
                var a = "s" + (round % 2);
                var b = "s" + ((round + 1) % 2);
                Stage(run.Source, ("a", a), ("b", b));
            }

            Stage(run.Source, ("a", "last-a"), ("b", "last-b"));
            await Until(() => run.Client.Fault != null || run.ReplicaItem("a")!.Sku == "last-a");
            Assert.That(run.Client.Fault, Is.Null, "completed commits leave the buffer");
            Assert.That(run.ReplicaItem("b")!.Sku, Is.EqualTo("last-b"));
        }

        [Test]
        public async Task AMessageDeliveredTwiceInOneCommitFaults()
        {
            using var run = new Replication();

            run.Peer.Client!.Emit(Put(run, "items", "a", "one", "3", 3, id: "same"));
            run.Peer.Client!.Emit(Put(run, "items", "b", "two", "3", 3, id: "same"));
            await Until(() => run.Client.Fault != null);

            Assert.That(run.Faults, Has.Count.EqualTo(1));
            Assert.That(run.ReplicaItem("a"), Is.Null);
        }

        [Test]
        public async Task ACommitThatChangesItsAnnouncedSizeFaults()
        {
            using var run = new Replication();

            run.Peer.Client!.Emit(Put(run, "items", "a", "one", "3", 2));
            run.Peer.Client!.Emit(Put(run, "items", "b", "two", "3", 3));
            await Until(() => run.Client.Fault != null);

            Assert.That(run.ReplicaItem("a"), Is.Null);
        }

        [Test]
        public async Task ACommitAnnouncingMoreChangesThanAReplicaHoldsFaults()
        {
            using var run = new Replication();

            run.Peer.Client!.Emit(Put(run, "items", "a", "one", "3", 1_000_000));
            await Until(() => run.Client.Fault != null);
        }

        [Test]
        public async Task MoreIncompleteCommitsThanTheBoundFault()
        {
            using var run = new Replication();

            for (var commit = 0; commit < 70; commit++)
                run.Peer.Client!.Emit(Put(run, "items", "k" + commit, "v" + commit, "c" + commit, 2));
            await Until(() => run.Client.Fault != null);
        }

        [Test]
        public async Task UnsubscribingDropsTheCommitsStillWaiting()
        {
            using var run = new Replication();
            var emit = run.Peer.Client!;

            emit.Emit(Put(run, "items", "a", "one", "4", 2));
            await Task.Delay(50);
            run.Client.Unsubscribe("items");
            emit.Emit(Put(run, "items", "b", "two", "4", 2));
            await Task.Delay(100);

            Assert.That(run.ReplicaItem("a"), Is.Null);
            Assert.That(run.ReplicaItem("b"), Is.Null, "the second half of a dropped commit is not a commit");
        }

        [Test]
        public async Task AFaultedClientRefusesANewSubscription()
        {
            using var run = new Replication(seedReplica: replica => Stage(replica, ("local", "taken")));
            Stage(run.Source, ("a", "taken"));
            await Until(() => run.Client.Fault != null);

            var refusal = Assert.Throws<InvalidOperationException>(() => run.Client.SubscribeAsync("again"));
            Assert.That(refusal!.InnerException, Is.SameAs(run.Client.Fault));
        }
    }
}
