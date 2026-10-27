#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GameCult.Caching;
using NUnit.Framework;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    // A peer's put or delete is applied or forwarded only when the server's RemoteWriteAuthority grants it. The wire handlers
    // are reached through their private delegates with an uninitialized peer, as CultNetJournalTests.WireHandlers does:
    // anything the handler tries to send throws on that peer, so a handler that throws answered the peer with a refusal and a
    // handler that returns quietly sent nothing.
    // The tests' own grant-everyone authority, for tests whose subject is not the grant. The library ships none.
    internal sealed class TestGrantsEverything : ICultNetRemoteWriteAuthority
    {
        public CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request) => CultNetRemoteWriteDecision.Grant();
    }

    public sealed class RemoteWriteAuthorityTests
    {
        private const string ShardId = "authority-shard";
        private static readonly CultRecordKey KeyA = new("tests:authority:a");
        private static readonly CultRecordKey KeyB = new("tests:authority:b");
        private static readonly CultRecordKey Unowned = new("unrelated:authority");

        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };

        private sealed class Rig
        {
            public CultCache Cache = null!;
            public CultNetDatabase Database = null!;
            public CultNetDatabaseServer DatabaseServer = null!;
            public Server Host = null!;
            public CultNetServerPeer Peer = null!;
            public CapturingLogger Logger = null!;
            public Func<CultNetDocumentPutRawMessage, Task> Put = null!;
            public Func<CultNetDocumentDeleteMessage, Task> Delete = null!;
            public string SchemaId = string.Empty;
            public List<object> Published = new();
            public IDisposable Subscription = null!;

            public string? TextOf(CultRecordKey key) => Cache.Get<NetworkSchemaNote>(key)?.Text;
            public int LogLength => Database.GetMutationLog(ShardId).Count;

            public CultNetDocumentPutRawMessage PutOf(CultRecordKey key, string text)
            {
                var message = Database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<NetworkSchemaNote>(key), Note(text));
                message.ShardId = ShardId;
                message.ShardEpoch = 1;
                return message;
            }

            public CultNetDocumentDeleteMessage DeleteOf(CultRecordKey key) =>
                new() { MessageId = "wire-delete", SchemaId = SchemaId, RecordKey = key.Value, ShardId = ShardId, ShardEpoch = 1 };
        }

        private static Rig Build(ICultNetRemoteWriteAuthority? authority, bool primary = true, ICultNetShardWriteForwarder? forwarder = null)
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor(ShardId, "primary", epoch: 1, isPrimary: primary, schemaIds: [schemaId],
                        primaryEndpoints: primary ? null : ["cultnet://runtime-owner:3075"])
                ]
            });
            var host = new Server(cache, ServerSecurityOptions.Development());
            var logger = new CapturingLogger();
            host.Logger = logger;
            var databaseServer = new CultNetDatabaseServer(host, database, new CultNetDatabaseServerOptions
            {
                RemoteWriteAuthority = authority,
                ForwardNonPrimaryWrites = forwarder != null,
                WriteForwarder = forwarder
            });
            var peer = (CultNetServerPeer)RuntimeHelpers.GetUninitializedObject(typeof(CultNetServerPeer));
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var put = (Func<CultNetDocumentPutRawMessage, CultNetServerPeer, Task>)typeof(CultNetDatabaseServer).GetField("_putHandler", flags)!.GetValue(databaseServer)!;
            var delete = (Func<CultNetDocumentDeleteMessage, CultNetServerPeer, Task>)typeof(CultNetDatabaseServer).GetField("_deleteHandler", flags)!.GetValue(databaseServer)!;
            var rig = new Rig
            {
                Cache = cache,
                Database = database,
                DatabaseServer = databaseServer,
                Host = host,
                Peer = peer,
                Logger = logger,
                SchemaId = schemaId,
                Put = message => put(message, peer),
                Delete = message => delete(message, peer)
            };
            rig.Subscription = R3.ObservableExtensions.Subscribe(database.WatchAllChanges(), change => rig.Published.Add(change));
            return rig;
        }

        // The server writes its own record through the host's door, which asks no remote authority.
        private static async Task Seed(Rig rig, CultRecordKey key, string text) => await rig.Database.ApplyPutAsync(rig.PutOf(key, text));

        [Test]
        public async Task WithNoAuthorityAPeersPutAndDeleteAreRefusedAndChangeNothing()
        {
            var rig = Build(authority: null);
            await Seed(rig, KeyA, "server");
            var publishedBefore = rig.Published.Count;
            var logBefore = rig.LogLength;
            Assert.That(rig.TextOf(KeyA), Is.EqualTo("server"), "the host's own door needs no remote authority");

            Assert.CatchAsync(async () => await rig.Put(rig.PutOf(KeyA, "peer")), "the peer is answered with a refusal");
            Assert.CatchAsync(async () => await rig.Delete(rig.DeleteOf(KeyA)), "the peer is answered with a refusal");
            Assert.CatchAsync(async () => await rig.Put(rig.PutOf(KeyB, "peer")), "a new key is refused too");

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("server"));
            Assert.That(rig.TextOf(KeyB), Is.Null);
            Assert.That(rig.Published, Has.Count.EqualTo(publishedBefore), "a refusal publishes no change");
            Assert.That(rig.LogLength, Is.EqualTo(logBefore), "a refusal appends no mutation log entry");
            Assert.That(rig.Logger.Warnings, Has.Some.Contains("raw put refused"));
            Assert.That(rig.Logger.Warnings, Has.Some.Contains("raw delete refused"));
            Assert.That(rig.Logger.Errors, Is.Empty, "a refusal is not a fault");
        }

        [Test]
        public async Task AGrantedPutAndDeleteCommitAndTheAuthoritySawThisPeer()
        {
            var authority = new RecordingAuthority(_ => CultNetRemoteWriteDecision.Grant());
            var rig = Build(authority);

            await rig.Put(rig.PutOf(KeyA, "peer"));
            Assert.That(rig.TextOf(KeyA), Is.EqualTo("peer"));
            Assert.That(rig.Published, Has.Count.EqualTo(1));
            Assert.That(rig.LogLength, Is.EqualTo(1));

            await rig.Delete(rig.DeleteOf(KeyA));
            Assert.That(rig.TextOf(KeyA), Is.Null);

            Assert.That(authority.Requests, Has.Count.EqualTo(2), "asked once per received write");
            Assert.That(authority.Requests.Select(request => request.Peer), Has.All.SameAs(rig.Peer));
            Assert.That(rig.Logger.Errors, Is.Empty);
        }

        [Test]
        public async Task TheRequestCarriesTheResolvedWriteAndScopedGrantsRefuseTheirNeighbours()
        {
            // The put names a foreign alias of the schema; the payload routes it to the canonical descriptor.
            var recorded = new RecordingAuthority(request =>
                request.Operation == CultNetRemoteWriteOperation.Put && request.RecordKey.Equals(KeyA)
                    ? CultNetRemoteWriteDecision.Grant()
                    : CultNetRemoteWriteDecision.Refuse("scoped"));
            var rig = Build(recorded);
            await Seed(rig, KeyA, "server");
            var aliased = rig.PutOf(KeyA, "aliased");
            aliased.Document.SchemaId = "runtime.generated.network-note.ui.42";

            await rig.Put(aliased);

            var request = recorded.Requests.Single();
            Assert.That(request.SchemaId, Is.EqualTo(rig.SchemaId), "the authority sees the canonical id, not the alias as sent");
            Assert.That(request.Operation, Is.EqualTo(CultNetRemoteWriteOperation.Put));
            Assert.That(request.RecordKey, Is.EqualTo(KeyA));
            Assert.That(request.ShardId, Is.EqualTo(ShardId));
            Assert.That(request.ShardEpoch, Is.EqualTo(1));
            Assert.That(rig.TextOf(KeyA), Is.EqualTo("aliased"));

            Assert.CatchAsync(async () => await rig.Put(rig.PutOf(KeyB, "other-key")));
            Assert.CatchAsync(async () => await rig.Delete(rig.DeleteOf(KeyA)));
            Assert.That(rig.TextOf(KeyB), Is.Null, "a grant scoped to key a refuses key b");
            Assert.That(rig.TextOf(KeyA), Is.EqualTo("aliased"), "a grant scoped to Put refuses Delete");
            Assert.That(recorded.Requests.Last().Operation, Is.EqualTo(CultNetRemoteWriteOperation.Delete));
            Assert.That(recorded.Requests.Last().SchemaId, Is.EqualTo(rig.SchemaId));
        }

        [Test]
        public async Task ARefusedWriteIsNeverForwardedAndAGrantedOneIs()
        {
            var forwarder = new CountingForwarder();
            var refusing = Build(new RecordingAuthority(_ => CultNetRemoteWriteDecision.Refuse("no")), primary: false, forwarder);

            Assert.CatchAsync(async () => await refusing.Put(refusing.PutOf(KeyA, "peer")));
            Assert.CatchAsync(async () => await refusing.Delete(refusing.DeleteOf(KeyA)));
            Assert.That(forwarder.Puts, Is.EqualTo(0), "a refused put is not forwarded");
            Assert.That(forwarder.Deletes, Is.EqualTo(0), "a refused delete is not forwarded");

            var unconfigured = Build(authority: null, primary: false, forwarder);
            Assert.CatchAsync(async () => await unconfigured.Put(unconfigured.PutOf(KeyA, "peer")));
            Assert.That(forwarder.Puts, Is.EqualTo(0), "no authority forwards nothing");

            var granting = Build(new RecordingAuthority(_ => CultNetRemoteWriteDecision.Grant()), primary: false, forwarder);
            await granting.Put(granting.PutOf(KeyA, "peer"));
            await granting.Delete(granting.DeleteOf(KeyA));
            Assert.That(forwarder.Puts, Is.EqualTo(1), "a granted write on a non-primary shard is forwarded as before");
            Assert.That(forwarder.Deletes, Is.EqualTo(1));
        }

        [Test]
        public void RefusalPrecedesTheUnownedSchemaCheck()
        {
            var unowned = new MeshQuickstartNote { NoteId = "u", Body = "unowned" };
            var rig = Build(authority: null);
            var put = rig.Database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(Unowned), unowned);
            put.ShardId = ShardId;
            put.ShardEpoch = 1;

            Assert.CatchAsync(async () => await rig.Put(put));

            Assert.That(rig.Logger.Warnings, Has.Some.Contains("authority"), "an ungranted put of an unowned schema is answered write_not_authorized");
            Assert.That(rig.Logger.Warnings, Has.None.Contains("No shard owns"));

            var granting = Build(new RecordingAuthority(_ => CultNetRemoteWriteDecision.Grant()));
            var grantedPut = granting.Database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(Unowned), unowned);
            grantedPut.ShardId = ShardId;
            grantedPut.ShardEpoch = 1;
            Assert.CatchAsync(async () => await granting.Put(grantedPut));
            Assert.That(granting.Logger.Warnings, Has.Some.Contains("No shard owns"), "a granted put still meets the unowned-schema refusal");
        }

        [Test]
        public async Task AnAuthorityThatThrowsRefusesAndIsLoggedAsAnError()
        {
            var rig = Build(new RecordingAuthority(_ => throw new InvalidOperationException("authority exploded")));
            await Seed(rig, KeyA, "server");

            Assert.CatchAsync(async () => await rig.Put(rig.PutOf(KeyA, "peer")), "the peer is answered with a refusal");
            Assert.CatchAsync(async () => await rig.Delete(rig.DeleteOf(KeyA)));

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("server"), "fail closed");
            Assert.That(rig.Logger.Errors, Has.Some.Contains("authority exploded"));
        }

        [Test]
        public void TheRefusalNamesTheKeyAndTheCode()
        {
            var request = new CultNetRemoteWriteRequest(null!, CultNetRemoteWriteOperation.Delete, "s", KeyA, null, null);
            var message = CultNetErrorMessage.ForRemoteWriteRefused(request, "why not");

            Assert.That(message.Code, Is.EqualTo("write_not_authorized"));
            Assert.That(message.Error, Is.EqualTo("write_not_authorized: why not"));
            Assert.That(message.Details!.Field, Is.EqualTo("recordKey"));
            Assert.That(message.Details.Value, Is.EqualTo(KeyA.Value));
            Assert.That(message.RoutingHint, Is.Null);
            Assert.That(CultNetErrorMessage.ForRemoteWriteRefused(request, null).Error, Is.EqualTo("write_not_authorized"));
        }

        private sealed class RecordingAuthority : ICultNetRemoteWriteAuthority
        {
            private readonly Func<CultNetRemoteWriteRequest, CultNetRemoteWriteDecision> _decide;
            public List<CultNetRemoteWriteRequest> Requests { get; } = new();
            public RecordingAuthority(Func<CultNetRemoteWriteRequest, CultNetRemoteWriteDecision> decide) => _decide = decide;

            public CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request)
            {
                Requests.Add(request);
                return _decide(request);
            }
        }

        private sealed class CountingForwarder : ICultNetShardWriteForwarder
        {
            public int Puts { get; private set; }
            public int Deletes { get; private set; }
            public Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message) { Puts++; return Task.CompletedTask; }
            public Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message) { Deletes++; return Task.CompletedTask; }
        }

        private sealed class CapturingLogger : GameCult.Logging.ILogger
        {
            public List<string> Errors { get; } = new();
            public List<string> Warnings { get; } = new();
            public void LogInfo(string message) { }
            public void LogWarning(string message) => Warnings.Add(message);
            public void LogError(string message) => Errors.Add(message);
            public void LogDebug(string message) { }
        }
    }
}
