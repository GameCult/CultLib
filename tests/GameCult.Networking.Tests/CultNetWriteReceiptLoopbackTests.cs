#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using LiteNetLib;
using NUnit.Framework;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    // The receipt server on a real socket: Server.Start on an ephemeral loopback port and real LiteNetLib clients. The
    // handler-level tests (CultNetWriteReceiptServerTests) hand frames to the handlers; only a running server decides what
    // a frame that does not decode is answered with.
    public sealed class CultNetWriteReceiptLoopbackTests
    {
        private static readonly TimeSpan Cap = TimeSpan.FromSeconds(10);
        private static readonly CultRecordKey KeyA = new("tests:loopback:a");
        private static readonly CultRecordKey KeyB = new("tests:loopback:b");
        private const string ShardId = "loopback-shard";

        private sealed class Client : IDisposable
        {
            private readonly NetManager _manager;
            private readonly NetPeer _peer;
            private readonly BlockingCollection<ICultNetSchemaMessage> _received = new();
            public int Closed;

            public Client(int port, string key)
            {
                var listener = new EventBasedNetListener();
                var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var decoder = new LiteNetLibTransportConnection();
                listener.PeerConnectedEvent += _ => connected.TrySetResult(true);
                listener.PeerDisconnectedEvent += (_, _) => Interlocked.Increment(ref Closed);
                listener.NetworkReceiveEvent += (_, reader, _, _) =>
                    _received.Add(LiteNetLibTransportConnection.DecodeSchema(decoder.Receive(reader.GetRemainingBytes())));
                _manager = new NetManager(listener) { UnsyncedEvents = true };
                _manager.Start(0);
                _peer = _manager.Connect("127.0.0.1", port, key);
                if (!connected.Task.Wait(Cap)) Assert.Fail("the client did not connect");
            }

            public void Send<T>(T message) where T : ICultNetSchemaMessage =>
                _peer.Send(CultNetSchemaMessageSerialization.Serialize(message), DeliveryMethod.ReliableOrdered);

            public void SendUnreliable<T>(T message) where T : ICultNetSchemaMessage =>
                _peer.Send(CultNetSchemaMessageSerialization.Serialize(message), DeliveryMethod.Unreliable);

            public void Send<T>(T message, DeliveryMethod method) where T : ICultNetSchemaMessage =>
                _peer.Send(CultNetSchemaMessageSerialization.Serialize(message), method);

            public void SendRaw(byte[] bytes) => _peer.Send(bytes, DeliveryMethod.ReliableOrdered);

            public T Next<T>() where T : ICultNetSchemaMessage
            {
                if (!_received.TryTake(out var message, Cap)) Assert.Fail($"no {typeof(T).Name} arrived within {Cap}");
                Assert.That(message, Is.InstanceOf<T>());
                return (T)message!;
            }

            public void ExpectNothingMore(string because)
            {
                // A reliable ordered channel: anything the server sent before the probe's answer has already arrived.
                Assert.That(_received.Count, Is.Zero, because);
            }

            public void Dispose()
            {
                _manager.Stop();
                _received.Dispose();
            }
        }

        private sealed class Authority : ICultNetRemoteWriteAuthority
        {
            public CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request) =>
                request.RecordKey.Equals(KeyB) ? CultNetRemoteWriteDecision.Refuse("denied") : CultNetRemoteWriteDecision.Grant();
        }

        private sealed class Running : IDisposable
        {
            public Server Server = null!;
            public CultCache Cache = null!;
            public CultNetDatabase Database = null!;
            public string SchemaId = string.Empty;
            private CultNetDatabaseServer _databaseServer = null!;

            public static Running Start()
            {
                var cache = new CultCache();
                var schemaId = cache.Registry.GetRequired<NetworkSchemaNote>().SchemaId;
                var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
                {
                    Shards = [new CultNetShardDescriptor(ShardId, "primary", epoch: 1, isPrimary: true, schemaIds: [schemaId])]
                });
                var server = new Server(cache, ServerSecurityOptions.Development()) { Port = 0 };
                var databaseServer = new CultNetDatabaseServer(server, database, new CultNetDatabaseServerOptions
                {
                    RemoteWriteAuthority = new Authority()
                });
                server.Start();
                return new Running { Server = server, Cache = cache, Database = database, SchemaId = schemaId, _databaseServer = databaseServer };
            }

            public Client Connect() => new(Server.Port, ServerSecurityOptions.Development().ConnectionKey);

            public CultNetDocumentPutRawMessage PutOf(string id, CultRecordKey key, string text)
            {
                var message = Database.Documents.CreateRawDocumentPutMessage(id, new CultRecordHandle<NetworkSchemaNote>(key),
                    new NetworkSchemaNote { Schema = "tests.networking_note.v1", Text = text });
                message.ShardId = ShardId;
                message.ShardEpoch = 1;
                return message;
            }

            public void Dispose()
            {
                _databaseServer.Dispose();
                Server.Dispose();
            }
        }

        private static CultNetHelloMessage HelloOf(params string[] versions) =>
            new() { RuntimeId = "loopback-client", RuntimeKind = "test", SupportedMessageVersions = versions };

        private static readonly string[] BothFrames = [CultNetSchemaVersions.WriteAdmitted, CultNetSchemaVersions.ErrorV1];

        [Test]
        public void StartBindsTheRequestedPortAndReportsTheOneBound()
        {
            using var running = Running.Start();
            Assert.That(running.Server.Port, Is.Not.Zero.And.Not.EqualTo(3075));
        }

        // contracts/cultnet/interop/write-receipt-vectors.json, error_v1_unreadable_frame_has_null_message_id: code
        // message_invalid, details.field "message", messageId null.
        [TestCase(new byte[] { 0xC1 }, TestName = "AnUnreadableFrameFromAReceiptPeerIsAnsweredWithMessageInvalidAndNoId_NeverUsedByte")]
        [TestCase(new byte[] { 0x92, 0x01, 0x02 }, TestName = "AnUnreadableFrameFromAReceiptPeerIsAnsweredWithMessageInvalidAndNoId_NotAMap")]
        public void AnUnreadableFrameFromAReceiptPeerIsAnsweredWithMessageInvalidAndNoId(byte[] garbage)
        {
            using var running = Running.Start();
            using var client = running.Connect();
            client.Send(HelloOf(BothFrames));
            client.Next<CultNetHelloMessage>();

            client.SendRaw(garbage);

            var error = client.Next<CultNetErrorV1Message>();
            Assert.That(error.Code, Is.EqualTo("message_invalid"));
            Assert.That(error.Details!.Field, Is.EqualTo("message"));
            Assert.That(error.MessageId, Is.Null);
        }

        [Test]
        public async Task AReceiptPeerIsStillServedAfterAnUnreadableFrameAndALegacyPeerIsAnsweredAsBefore()
        {
            using var running = Running.Start();
            using var receipt = running.Connect();
            receipt.Send(HelloOf(BothFrames));
            receipt.Next<CultNetHelloMessage>();
            receipt.SendRaw(new byte[] { 0xC1 });
            receipt.Next<CultNetErrorV1Message>();

            receipt.Send(running.PutOf("r1", KeyA, "one"));
            Assert.That(receipt.Next<CultNetWriteAdmittedMessage>().MessageId, Is.EqualTo("r1"));

            using var legacy = running.Connect();
            legacy.Send(HelloOf()); // a hello that lists neither frame is answered with nothing
            legacy.SendRaw(new byte[] { 0xC1 }); // unanswered: were it answered, error.v1 would arrive ahead of the refusal
            legacy.Send(running.PutOf("l1", KeyB, "two"));
            var refusal = legacy.Next<CultNetErrorMessage>();
            Assert.That(refusal, Is.TypeOf<CultNetErrorMessage>());
            Assert.That(refusal.Code, Is.EqualTo("write_not_authorized"));
            legacy.Send(running.PutOf("l2", KeyA, "three"));
            await WaitFor(() => running.Cache.Get<NetworkSchemaNote>(KeyA)?.Text == "three");
            legacy.ExpectNothingMore("a legacy peer is never sent a receipt or error.v1");
            Assert.That(running.Cache.Get<NetworkSchemaNote>(KeyB), Is.Null);
        }

        // write-receipt.md, Reliable channel only: a write on an unreliable channel of a receipt session is refused
        // message_invalid, details.field "channel", fixed text, carrying the write's id; it is not applied.
        [Test]
        public async Task AWriteOnAnUnreliableChannelOfAReceiptPeerIsRefusedAndNotApplied()
        {
            using var running = Running.Start();
            using var client = running.Connect();
            client.Send(HelloOf(BothFrames));
            client.Next<CultNetHelloMessage>();

            client.SendUnreliable(running.PutOf("u1", KeyA, "lost"));

            var error = client.Next<CultNetErrorV1Message>();
            Assert.That(error.Code, Is.EqualTo("message_invalid"));
            Assert.That(error.Details!.Field, Is.EqualTo("channel"));
            Assert.That(error.Error, Is.EqualTo("message_invalid: receipt sessions require the reliable channel"));
            Assert.That(error.MessageId, Is.EqualTo("u1"));

            // The session goes on, and the same id on the reliable channel is a new write, not a replayed refusal.
            client.Send(running.PutOf("u1", KeyA, "kept"));
            Assert.That(client.Next<CultNetWriteAdmittedMessage>().MessageId, Is.EqualTo("u1"));
            await WaitFor(() => running.Cache.Get<NetworkSchemaNote>(KeyA)?.Text == "kept");
        }

        // write-receipt.md, Reliable channel only: reliable and not sequenced. Reliable ordered and unordered qualify; reliable
        // sequenced does not, and nor does any unreliable method.
        [TestCase(DeliveryMethod.ReliableSequenced, true)]
        [TestCase(DeliveryMethod.Sequenced, true)]
        [TestCase(DeliveryMethod.Unreliable, true)]
        [TestCase(DeliveryMethod.ReliableUnordered, false)]
        [TestCase(DeliveryMethod.ReliableOrdered, false)]
        public async Task OnlyAReliableNonSequencedChannelCarriesAWriteOfAReceiptPeer(DeliveryMethod method, bool refused)
        {
            using var running = Running.Start();
            using var client = running.Connect();
            client.Send(HelloOf(BothFrames));
            client.Next<CultNetHelloMessage>();

            client.Send(running.PutOf("c1", KeyA, "text"), method);

            if (refused)
            {
                var error = client.Next<CultNetErrorV1Message>();
                Assert.That(error.Details!.Field, Is.EqualTo("channel"));
                Assert.That(error.MessageId, Is.EqualTo("c1"));
                Assert.That(running.Cache.Get<NetworkSchemaNote>(KeyA), Is.Null);
            }
            else
            {
                Assert.That(client.Next<CultNetWriteAdmittedMessage>().MessageId, Is.EqualTo("c1"));
                await WaitFor(() => running.Cache.Get<NetworkSchemaNote>(KeyA)?.Text == "text");
            }
        }

        [Test]
        public void ADeleteOnAnUnreliableChannelOfAReceiptPeerIsRefusedWithItsId()
        {
            using var running = Running.Start();
            using var client = running.Connect();
            client.Send(HelloOf(BothFrames));
            client.Next<CultNetHelloMessage>();

            client.SendUnreliable(new CultNetDocumentDeleteMessage
            {
                MessageId = "ud1", SchemaId = running.SchemaId, RecordKey = KeyA.Value, ShardId = ShardId, ShardEpoch = 1
            });

            var error = client.Next<CultNetErrorV1Message>();
            Assert.That(error.Code, Is.EqualTo("message_invalid"));
            Assert.That(error.Details!.Field, Is.EqualTo("channel"));
            Assert.That(error.MessageId, Is.EqualTo("ud1"));
        }

        [Test]
        public async Task OnlyWritesAreRefusedOnTheUnreliableChannelAndALegacyPeerIsLeftAsBefore()
        {
            using var running = Running.Start();
            using var receipt = running.Connect();
            receipt.Send(HelloOf(BothFrames));
            receipt.Next<CultNetHelloMessage>();
            receipt.SendUnreliable(HelloOf(BothFrames)); // not a write: answered with nothing on the unreliable channel
            receipt.Send(running.PutOf("r1", KeyA, "one"));
            Assert.That(receipt.Next<CultNetWriteAdmittedMessage>().MessageId, Is.EqualTo("r1"), "nothing else was sent ahead of the receipt");
            receipt.Send(running.PutOf("r2", KeyA, "two"));
            Assert.That(receipt.Next<CultNetWriteAdmittedMessage>().MessageId, Is.EqualTo("r2"));
            receipt.ExpectNothingMore("the unreliable hello was answered with nothing");

            using var legacy = running.Connect();
            legacy.Send(HelloOf());
            legacy.SendUnreliable(running.PutOf("l1", KeyB, "two")); // refused by the authority, answered as v0 as before
            Assert.That(legacy.Next<CultNetErrorMessage>().Code, Is.EqualTo("write_not_authorized"));
            legacy.SendUnreliable(running.PutOf("l2", KeyA, "three"));
            await WaitFor(() => running.Cache.Get<NetworkSchemaNote>(KeyA)?.Text == "three");
            legacy.ExpectNothingMore("a legacy peer is never sent a receipt, error.v1 or a channel refusal");
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Cap;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("the condition did not hold within the cap");
                await Task.Delay(5);
            }
        }
    }
}
