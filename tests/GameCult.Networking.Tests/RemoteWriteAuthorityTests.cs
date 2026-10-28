#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GameCult.Caching;
using LiteNetLib;
using NUnit.Framework;
using R3;
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

        private static Rig Build(ICultNetRemoteWriteAuthority? authority, bool primary = true, ICultNetShardWriteForwarder? forwarder = null, CultNetServerPeer? peer = null)
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
            peer ??= (CultNetServerPeer)RuntimeHelpers.GetUninitializedObject(typeof(CultNetServerPeer));
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
                Put = message => put(message, peer!),
                Delete = message => delete(message, peer!)
            };
            rig.Subscription = database.WatchAllChanges().Subscribe(change => rig.Published.Add(change));
            return rig;
        }

        // The handler answered the peer with a refusal: the send reached the (uninitialized) peer and failed there, so the
        // refusal message was built first. A failure earlier, in building it, would not pass through the peer's send.
        private static void AssertAnswered(Func<Task> handler, string because)
        {
            var failure = Assert.CatchAsync(async () => await handler(), because)!;
            Assert.That(failure.ToString(), Does.Contain("CultNetServerPeer.SendCultNet").And.Not.Contain("ForRemoteWriteRefused"), because);
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

            AssertAnswered(() => rig.Put(rig.PutOf(KeyA, "peer")), "the peer is answered with a refusal");
            AssertAnswered(() => rig.Delete(rig.DeleteOf(KeyA)), "the peer is answered with a refusal");
            AssertAnswered(() => rig.Put(rig.PutOf(KeyB, "peer")), "a new key is refused too");

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

            AssertAnswered(() => rig.Put(rig.PutOf(KeyB, "other-key")), "the peer is answered with a refusal");
            AssertAnswered(() => rig.Delete(rig.DeleteOf(KeyA)), "the peer is answered with a refusal");
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

            AssertAnswered(() => refusing.Put(refusing.PutOf(KeyA, "peer")), "the peer is answered with a refusal");
            AssertAnswered(() => refusing.Delete(refusing.DeleteOf(KeyA)), "the peer is answered with a refusal");
            Assert.That(forwarder.Puts, Is.EqualTo(0), "a refused put is not forwarded");
            Assert.That(forwarder.Deletes, Is.EqualTo(0), "a refused delete is not forwarded");

            var unconfigured = Build(authority: null, primary: false, forwarder);
            AssertAnswered(() => unconfigured.Put(unconfigured.PutOf(KeyA, "peer")), "the peer is answered with a refusal");
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

            AssertAnswered(() => rig.Put(put), "the peer is answered with a refusal");

            Assert.That(rig.Logger.Warnings, Has.Some.Contains("authority"), "an ungranted put of an unowned schema is answered write_not_authorized");
            Assert.That(rig.Logger.Warnings, Has.None.Contains("No shard owns"));

            var granting = Build(new RecordingAuthority(_ => CultNetRemoteWriteDecision.Grant()));
            var grantedPut = granting.Database.Documents.CreateRawDocumentPutMessage("wire", new CultRecordHandle<MeshQuickstartNote>(Unowned), unowned);
            grantedPut.ShardId = ShardId;
            grantedPut.ShardEpoch = 1;
            AssertAnswered(() => granting.Put(grantedPut), "the peer is answered with a refusal");
            Assert.That(granting.Logger.Warnings, Has.Some.Contains("No shard owns"), "a granted put still meets the unowned-schema refusal");
        }

        [Test]
        public async Task AnAuthorityThatThrowsRefusesAndIsLoggedAsAnError()
        {
            var rig = Build(new RecordingAuthority(_ => throw new InvalidOperationException("authority exploded")));
            await Seed(rig, KeyA, "server");

            AssertAnswered(() => rig.Put(rig.PutOf(KeyA, "peer")), "the peer is answered with a refusal");
            AssertAnswered(() => rig.Delete(rig.DeleteOf(KeyA)), "the peer is answered with a refusal");

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("server"), "fail closed");
            Assert.That(rig.Logger.Errors, Has.Some.Contains("authority exploded"));
        }

        [Test]
        public void ARefusalWithNoReasonStillSaysWhy()
        {
            var rig = Build(new RecordingAuthority(_ => CultNetRemoteWriteDecision.Refuse()));

            AssertAnswered(() => rig.Put(rig.PutOf(KeyA, "peer")), "refused");

            Assert.That(rig.Logger.Warnings, Has.Some.Contains("raw put refused: the write was not authorized"));
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

        // A real LiteNetLib pair on loopback with both sockets bound to ephemeral ports (Server and Client bind fixed ports,
        // which a parallel suite cannot share). The server side is a real NetPeer wrapped as the CultNetServerPeer the handlers
        // answer; the client side decodes what arrives on the wire with the production schema decoder.
        private sealed class WirePair : IDisposable
        {
            private readonly NetManager _serverManager;
            private readonly NetManager _clientManager;
            public CultNetServerPeer Peer { get; }
            public ConcurrentQueue<CultNetErrorMessage> Errors { get; } = new();

            public WirePair()
            {
                var serverListener = new EventBasedNetListener();
                _serverManager = new NetManager(serverListener) { UnsyncedEvents = true };
                var clientListener = new EventBasedNetListener();
                _clientManager = new NetManager(clientListener) { UnsyncedEvents = true };
                var connected = new TaskCompletionSource<NetPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
                serverListener.ConnectionRequestEvent += request => request.AcceptIfKey("rwa-wire");
                serverListener.PeerConnectedEvent += serverSide => connected.TrySetResult(serverSide);
                var decoder = new LiteNetLibTransportConnection();
                clientListener.NetworkReceiveEvent += (_, reader, _, _) =>
                {
                    var frame = decoder.Receive(reader.GetRemainingBytes());
                    if (LiteNetLibTransportConnection.DecodeSchema(frame) is CultNetErrorMessage error) Errors.Enqueue(error);
                };
                _serverManager.Start(0);
                _clientManager.Start(0);
                _clientManager.Connect("127.0.0.1", _serverManager.LocalPort, "rwa-wire");
                if (!connected.Task.Wait(TimeSpan.FromSeconds(10))) Assert.Fail("the wire pair did not connect");
                var serverPeer = connected.Task.Result;
                Peer = new CultNetServerPeer(serverPeer, new LiteNetLibTransportConnection(serverPeer));
            }

            public async Task<CultNetErrorMessage> NextErrorAsync()
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                CultNetErrorMessage? error;
                while (!Errors.TryDequeue(out error))
                {
                    if (DateTime.UtcNow > deadline) Assert.Fail("no reply arrived on the wire");
                    await Task.Delay(5);
                }

                return error;
            }

            public void Dispose()
            {
                _clientManager.Stop();
                _serverManager.Stop();
            }
        }

        [Test]
        public async Task ThePeerReceivesTheTypedRefusalOnTheWire()
        {
            using var wire = new WirePair();
            var rig = Build(authority: null, peer: wire.Peer);
            await Seed(rig, KeyA, "server");
            var logBefore = rig.LogLength;

            await rig.Put(rig.PutOf(KeyA, "peer"));
            var putReply = await wire.NextErrorAsync();
            await rig.Delete(rig.DeleteOf(KeyA));
            var deleteReply = await wire.NextErrorAsync();
            await rig.Put(rig.PutOf(KeyB, "peer"));
            var newKeyReply = await wire.NextErrorAsync();

            foreach (var (reply, key) in new[] { (putReply, KeyA), (deleteReply, KeyA), (newKeyReply, KeyB) })
            {
                Assert.That(reply.Code, Is.EqualTo("write_not_authorized"));
                Assert.That(reply.Error, Does.StartWith("write_not_authorized: "));
                Assert.That(reply.Details!.Field, Is.EqualTo("recordKey"));
                Assert.That(reply.Details.Value, Is.EqualTo(key.Value));
            }

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("server"));
            Assert.That(rig.TextOf(KeyB), Is.Null);
            Assert.That(rig.LogLength, Is.EqualTo(logBefore));
            Assert.That(wire.Errors, Is.Empty, "one reply per refused write");
        }

        [Test]
        public async Task AnAuthoritysRefusalReasonReachesThePeerAndAGrantSendsNothingBack()
        {
            using var wire = new WirePair();
            var rig = Build(new RecordingAuthority(request => request.RecordKey.Equals(KeyA)
                ? CultNetRemoteWriteDecision.Grant()
                : CultNetRemoteWriteDecision.Refuse("only a")), peer: wire.Peer);

            await rig.Put(rig.PutOf(KeyA, "peer"));
            await rig.Put(rig.PutOf(KeyB, "peer"));
            var reply = await wire.NextErrorAsync();

            Assert.That(reply.Code, Is.EqualTo("write_not_authorized"));
            Assert.That(reply.Error, Is.EqualTo("write_not_authorized: only a"));
            Assert.That(reply.Details!.Value, Is.EqualTo(KeyB.Value));
            Assert.That(rig.TextOf(KeyA), Is.EqualTo("peer"));
            await Task.Delay(100);
            Assert.That(wire.Errors, Is.Empty, "the granted write was not answered with an error");
        }

        [Test]
        public async Task ADeleteSentUnderASchemaAliasAsksTheAuthorityWithTheCanonicalId()
        {
            var authority = new RecordingAuthority(_ => CultNetRemoteWriteDecision.Grant());
            var rig = Build(authority);
            await Seed(rig, KeyA, "server");
            var aliased = rig.DeleteOf(KeyA);
            aliased.SchemaId = "tests.networking_note.v1";
            Assert.That(aliased.SchemaId, Is.Not.EqualTo(rig.SchemaId), "the fixture really sends an alias");

            await rig.Delete(aliased);

            var request = authority.Requests.Single();
            Assert.That(request.Operation, Is.EqualTo(CultNetRemoteWriteOperation.Delete));
            Assert.That(request.SchemaId, Is.EqualTo(rig.SchemaId), "the authority sees the canonical id, not the alias as sent");
            Assert.That(rig.TextOf(KeyA), Is.Null, "the aliased delete was granted and applied");
        }

        [Test]
        public async Task ANullDecisionRefusesPutAndDelete()
        {
            var rig = Build(new RecordingAuthority(_ => null!));
            await Seed(rig, KeyA, "server");

            AssertAnswered(() => rig.Put(rig.PutOf(KeyA, "peer")), "a null decision is a refusal");
            AssertAnswered(() => rig.Delete(rig.DeleteOf(KeyA)), "a null decision is a refusal");
            AssertAnswered(() => rig.Put(rig.PutOf(KeyB, "peer")), "a null decision is a refusal");

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("server"));
            Assert.That(rig.TextOf(KeyB), Is.Null);
            Assert.That(rig.Logger.Warnings, Has.Some.Contains("raw put refused: the write was not authorized"));
            Assert.That(rig.Logger.Warnings, Has.Some.Contains("raw delete refused: the write was not authorized"));
            Assert.That(rig.Logger.Errors, Is.Empty, "a null decision is a refusal, not a fault");
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
