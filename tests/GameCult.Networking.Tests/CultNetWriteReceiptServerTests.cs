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
    // The database server's half of contracts/cultnet/interop/write-receipt.md. A peer whose hello lists both new frames is
    // answered with write_admitted or error.v1, each naming the write's id; any other peer is answered as before. The handlers
    // are reached through their private delegates; a peer's transport is a capture of what leaves it.
    public sealed class CultNetWriteReceiptServerTests
    {
        private const string ShardId = "receipt-shard";
        private static readonly CultRecordKey KeyA = new("tests:receipt:a");
        private static readonly CultRecordKey KeyB = new("tests:receipt:b");

        private sealed class Authority : ICultNetRemoteWriteAuthority
        {
            public HashSet<string> Denied { get; } = new();
            public CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request) =>
                Denied.Contains(request.RecordKey.Value) ? CultNetRemoteWriteDecision.Refuse("denied") : CultNetRemoteWriteDecision.Grant();
        }

        private sealed class Wire
        {
            public List<ICultNetSchemaMessage> Sent { get; } = new();
            public LiteNetLibTransportConnection Transport { get; }
            public CultNetServerPeer Peer { get; }

            public Wire()
            {
                Transport = new LiteNetLibTransportConnection(bytes =>
                    Sent.Add(LiteNetLibTransportConnection.DecodeSchema(LiteNetLibTransportConnection.Decode(bytes))));
                Peer = (CultNetServerPeer)RuntimeHelpers.GetUninitializedObject(typeof(CultNetServerPeer));
                typeof(CultNetServerPeer).GetField("<Transport>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(Peer, Transport);
            }
        }

        private sealed class Rig
        {
            public CultCache Cache = null!;
            public CultNetDatabase Database = null!;
            public CultNetDatabaseServer DatabaseServer = null!;
            public Authority Authority = null!;
            public string SchemaId = string.Empty;
            public Func<CultNetDocumentPutRawMessage, CultNetServerPeer, Task> PutHandler = null!;
            public Func<CultNetDocumentDeleteMessage, CultNetServerPeer, Task> DeleteHandler = null!;
            public Func<CultNetHelloMessage, CultNetServerPeer, Task> HelloHandler = null!;

            public int LogLength => Database.GetMutationLog(ShardId).Count;
            public string? TextOf(CultRecordKey key) => Cache.Get<NetworkSchemaNote>(key)?.Text;

            public CultNetDocumentPutRawMessage PutOf(string id, CultRecordKey key, string text)
            {
                var message = Database.Documents.CreateRawDocumentPutMessage(id, new CultRecordHandle<NetworkSchemaNote>(key),
                    new NetworkSchemaNote { Schema = "tests.networking_note.v1", Text = text });
                message.ShardId = ShardId;
                message.ShardEpoch = 1;
                return message;
            }

            public CultNetDocumentDeleteMessage DeleteOf(string id, CultRecordKey key) =>
                new() { MessageId = id, SchemaId = SchemaId, RecordKey = key.Value, ShardId = ShardId, ShardEpoch = 1 };

            public Task Put(Wire wire, string id, CultRecordKey key, string text) => PutHandler(PutOf(id, key, text), wire.Peer);
            public Task Delete(Wire wire, string id, CultRecordKey key) => DeleteHandler(DeleteOf(id, key), wire.Peer);
            public Task Hello(Wire wire, params string[] versions) => HelloHandler(HelloOf(versions), wire.Peer);
        }

        private static CultNetHelloMessage HelloOf(params string[] versions) =>
            new() { RuntimeId = "test-client", RuntimeKind = "test", SupportedMessageVersions = versions };

        private static readonly string[] BothFrames = [CultNetSchemaVersions.WriteAdmitted, CultNetSchemaVersions.ErrorV1];

        private static Rig Build(Authority? authority = null, bool primary = true, ICultNetShardWriteForwarder? forwarder = null)
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
            authority ??= new Authority();
            var databaseServer = new CultNetDatabaseServer(host, database, new CultNetDatabaseServerOptions
            {
                RemoteWriteAuthority = authority,
                ForwardNonPrimaryWrites = forwarder != null,
                WriteForwarder = forwarder
            });
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            T Handler<T>(string field) => (T)typeof(CultNetDatabaseServer).GetField(field, flags)!.GetValue(databaseServer)!;
            return new Rig
            {
                Cache = cache,
                Database = database,
                DatabaseServer = databaseServer,
                Authority = authority,
                SchemaId = schemaId,
                PutHandler = Handler<Func<CultNetDocumentPutRawMessage, CultNetServerPeer, Task>>("_putHandler"),
                DeleteHandler = Handler<Func<CultNetDocumentDeleteMessage, CultNetServerPeer, Task>>("_deleteHandler"),
                HelloHandler = Handler<Func<CultNetHelloMessage, CultNetServerPeer, Task>>("_helloHandler")
            };
        }

        private static async Task<(Rig Rig, Wire Wire)> Capable()
        {
            var rig = Build();
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear(); // the server's own hello is checked on its own
            return (rig, wire);
        }

        private static CultNetWriteAdmittedMessage AssertAdmitted(ICultNetSchemaMessage message, string id, string operation, string recordKey)
        {
            var admitted = (CultNetWriteAdmittedMessage)message;
            Assert.That(admitted.MessageId, Is.EqualTo(id));
            Assert.That(admitted.Operation, Is.EqualTo(operation));
            Assert.That(admitted.RecordKey, Is.EqualTo(recordKey));
            return admitted;
        }

        private static CultNetErrorV1Message AssertRefused(ICultNetSchemaMessage message, string? id, string? code)
        {
            var error = (CultNetErrorV1Message)message;
            Assert.That(error.MessageId, Is.EqualTo(id));
            Assert.That(error.Code, Is.EqualTo(code));
            return error;
        }

        [Test]
        public async Task AnAdmittedPutOnAReceiptConnectionIsAnsweredAfterItsCommit()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Clear();

            await rig.Put(wire, "m1", KeyA, "one");

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("one"));
            var admitted = AssertAdmitted(wire.Sent.Single(), "m1", "put", KeyA.Value);
            Assert.That(admitted.SchemaId, Is.EqualTo(rig.SchemaId));
        }

        [Test]
        public async Task AnAdmittedDeleteIsAnsweredWithAReceiptNamingTheDelete()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");
            wire.Sent.Clear();

            await rig.Delete(wire, "d1", KeyA);

            Assert.That(rig.TextOf(KeyA), Is.Null);
            AssertAdmitted(wire.Sent.Single(), "d1", "delete", KeyA.Value);
        }

        [Test]
        public async Task TheServerAnswersAReceiptHelloWithItsOwnListingBothFrames()
        {
            var rig = Build();
            var wire = new Wire();

            await rig.Hello(wire, BothFrames);

            var hello = (CultNetHelloMessage)wire.Sent.Single();
            Assert.That(hello.SupportedMessageVersions, Is.EquivalentTo(BothFrames));
        }

        [TestCase("")]
        [TestCase("cultnet.write_admitted.v0")]
        [TestCase("cultnet.error.v1")]
        public async Task APeerThatDoesNotListBothFramesIsAnsweredAsBeforeAndTheServerSaysNothingOfReceipts(string listed)
        {
            var rig = Build();
            var wire = new Wire();
            await rig.Hello(wire, listed.Length == 0 ? [] : [listed]);
            await rig.Put(wire, "m1", KeyA, "one");

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("one"), "admitted all the same");
            Assert.That(wire.Sent, Is.Empty, "no receipt, no hello");

            rig.Authority.Denied.Add(KeyB.Value);
            await rig.Put(wire, "m2", KeyB, "two");
            Assert.That(wire.Sent.Single(), Is.TypeOf<CultNetErrorMessage>(), "a refusal is the v0 error, without an id");
        }

        [Test]
        public async Task APeerThatNeverSaidHelloGetsNoReceipt()
        {
            var rig = Build();
            var wire = new Wire();

            await rig.Put(wire, "m1", KeyA, "one");

            Assert.That(wire.Sent, Is.Empty);
        }

        [Test]
        public async Task ASecondHelloDoesNotChangeTheCapability()
        {
            var (rig, wire) = await Capable();
            await rig.Hello(wire);
            await rig.Put(wire, "m1", KeyA, "one");
            AssertAdmitted(wire.Sent.Single(), "m1", "put", KeyA.Value);

            var late = Build();
            var legacy = new Wire();
            await late.Put(legacy, "m1", KeyA, "one");
            await late.Hello(legacy, BothFrames);
            await late.Put(legacy, "m2", KeyB, "two");
            Assert.That(legacy.Sent, Is.Empty, "a hello after the first write does not opt the connection in");
        }

        [Test]
        public async Task ACapabilityNeverAuthorizes()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Add(KeyA.Value);

            await rig.Put(wire, "m1", KeyA, "one");
            await rig.Delete(wire, "d1", KeyA);

            Assert.That(rig.TextOf(KeyA), Is.Null);
            AssertRefused(wire.Sent[0], "m1", "write_not_authorized");
            AssertRefused(wire.Sent[1], "d1", "write_not_authorized");
            Assert.That(wire.Sent, Has.Count.EqualTo(2), "one answer per write");
        }

        [Test]
        public async Task AResendOfAnAdmittedWriteIsReceiptedAgainWithoutBeingAppliedAgain()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");
            var logAfterFirst = rig.LogLength;

            await rig.Put(wire, "m1", KeyA, "one");

            Assert.That(wire.Sent, Has.Count.EqualTo(2));
            AssertAdmitted(wire.Sent[1], "m1", "put", KeyA.Value);
            Assert.That(rig.LogLength, Is.EqualTo(logAfterFirst), "the resend was not committed again");
        }

        [Test]
        public async Task TheSameIdWithOtherContentIsRefusedAndNeverReReceipted()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");

            await rig.Put(wire, "m1", KeyA, "other");
            await rig.Put(wire, "m1", KeyB, "one");
            await rig.Delete(wire, "m1", KeyA);

            Assert.That(rig.TextOf(KeyA), Is.EqualTo("one"));
            Assert.That(rig.TextOf(KeyB), Is.Null);
            Assert.That(wire.Sent, Has.Count.EqualTo(4));
            for (var index = 1; index < 4; index++)
            {
                var error = AssertRefused(wire.Sent[index], "m1", "message_invalid");
                Assert.That(error.Details!.Field, Is.EqualTo("messageId"));
            }
        }

        [Test]
        public async Task AuthorizationIsDecidedBeforeAReusedIdIsLookedAt()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");
            rig.Authority.Denied.Add(KeyB.Value);

            await rig.Put(wire, "m1", KeyB, "other");

            AssertRefused(wire.Sent[1], "m1", "write_not_authorized");
        }

        [Test]
        public async Task ARefusedWriteIsForgottenSoARepeatIsJudgedAfresh()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Add(KeyA.Value);
            await rig.Put(wire, "m1", KeyA, "one");
            rig.Authority.Denied.Clear();

            await rig.Put(wire, "m1", KeyA, "one");

            AssertRefused(wire.Sent[0], "m1", "write_not_authorized");
            AssertAdmitted(wire.Sent[1], "m1", "put", KeyA.Value);
            Assert.That(rig.TextOf(KeyA), Is.EqualTo("one"));
        }

        [Test]
        public async Task AFullDedupeTableRefusesNewWritesAndStillAnswersRememberedOnes()
        {
            var (rig, wire) = await Capable();
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
            {
                await rig.Put(wire, $"w{index}", new CultRecordKey($"tests:receipt:{index}"), "x");
            }

            wire.Sent.Clear();
            await rig.Put(wire, "over", KeyA, "x");
            await rig.Put(wire, "w0", new CultRecordKey("tests:receipt:0"), "x");

            AssertRefused(wire.Sent[0], "over", null);
            AssertAdmitted(wire.Sent[1], "w0", "put", "tests:receipt:0");
            Assert.That(rig.TextOf(KeyA), Is.Null);
        }

        [Test]
        public async Task AWriteWithNoIdIsRefusedWithNoId()
        {
            var (rig, wire) = await Capable();

            var message = rig.PutOf("placeholder", KeyA, "one");
            message.MessageId = string.Empty;
            await rig.PutHandler(message, wire.Peer);

            var error = AssertRefused(wire.Sent.Single(), null, "message_invalid");
            Assert.That(error.Details!.Field, Is.EqualTo("messageId"));
            Assert.That(rig.TextOf(KeyA), Is.Null);
        }

        [Test]
        public async Task AFrameThatDoesNotDecodeIsRefusedWithItsIdWhenItHoldsOne()
        {
            var (_, wire) = await Capable();
            var withId = MessagePack.MessagePackSerializer.Serialize(new Dictionary<string, object>
            {
                ["schemaVersion"] = "cultnet.document_put_raw.v9",
                ["messageId"] = "bad-1"
            });

            wire.Transport.AnswerUnreadableSchemaFrame(withId);
            wire.Transport.AnswerUnreadableSchemaFrame(new byte[] { 0xC1 });

            var error = AssertRefused(wire.Sent[0], "bad-1", "message_invalid");
            Assert.That(error.Details!.Field, Is.EqualTo("message"));
            AssertRefused(wire.Sent[1], null, "message_invalid");
        }

        [Test]
        public void ReadableMessageIdFindsOnlyANonEmptyStringIdInATopLevelMap()
        {
            string? IdOf(object value) => CultNetReadableMessageId.From(MessagePack.MessagePackSerializer.Serialize(value));

            Assert.That(IdOf(new Dictionary<string, object> { ["a"] = new[] { 1, 2 }, ["messageId"] = "x" }), Is.EqualTo("x"));
            Assert.That(IdOf(new Dictionary<string, object> { ["messageId"] = 5 }), Is.Null);
            Assert.That(IdOf(new Dictionary<string, object> { ["messageId"] = "" }), Is.Null);
            Assert.That(IdOf(new Dictionary<string, object> { ["nested"] = new Dictionary<string, object> { ["messageId"] = "deep" } }), Is.Null);
            Assert.That(IdOf("messageId"), Is.Null);
        }

        [Test]
        public void TheContentHashMatchesTheSpecVectors()
        {
            byte[] a = [0x81, 0xA2, 0x69, 0x64, 0xA1, 0x31];
            byte[] b = [0x81, 0xA2, 0x69, 0x64, 0xA1, 0x32];
            string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

            Assert.That(Hex(CultNetWriteContentHash.Compute("put", "note.v0", "note:1", a)), Is.EqualTo("7051c10071123b6ade50fbd5075e3859cae4ea70ce358a24a0b0c6980090e95d"));
            Assert.That(Hex(CultNetWriteContentHash.Compute("put", "note.v0", "note:1", b)), Is.EqualTo("58d3fc6b858d2605a68550c9d4811ccdca6b9db7a0c3bfebf304d279257f6f9f"));
            Assert.That(Hex(CultNetWriteContentHash.Compute("put", "note.v0", "note:2", a)), Is.EqualTo("a248cd920e58d41bc6ccca9e108030b1d491239000047fef088122364a32b6f8"));
            Assert.That(Hex(CultNetWriteContentHash.Compute("delete", "note.v0", "note:1", null)), Is.EqualTo("232b0f96651b28aa8ab7673a6106944a9f6a8d66852b6e21291a23ed084b8b37"));
        }

        [Test]
        public void TheNewFramesRoundTripOnTheWire()
        {
            var admitted = new CultNetWriteAdmittedMessage { MessageId = "m", Operation = "put", SchemaId = "s", RecordKey = "k" };
            var error = new CultNetErrorV1Message { Error = "e", Code = "message_invalid", MessageId = null };

            var admittedBack = (CultNetWriteAdmittedMessage)CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(admitted));
            var errorBack = (CultNetErrorV1Message)CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(error));

            Assert.That(admittedBack.MessageId, Is.EqualTo("m"));
            Assert.That(admittedBack.RecordKey, Is.EqualTo("k"));
            Assert.That(errorBack.MessageId, Is.Null);
            Assert.That(errorBack.Code, Is.EqualTo("message_invalid"));
        }

        private sealed class ScriptedForwarder : ICultNetShardWriteForwarder
        {
            public Func<CultNetDocumentPutRawMessage, Task> OnPut { get; set; } = _ => Task.CompletedTask;
            public Func<CultNetDocumentDeleteMessage, Task> OnDelete { get; set; } = _ => Task.CompletedTask;
            public int Forwarded { get; private set; }

            public Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message)
            {
                Forwarded++;
                return OnPut(message);
            }

            public Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message)
            {
                Forwarded++;
                return OnDelete(message);
            }
        }

        [Test]
        public async Task AForwardedWriteIsReceiptedUnderItsOriginalIdOnlyWhenThePrimaryAnswered()
        {
            var forwarder = new ScriptedForwarder();
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            await rig.Delete(wire, "fwd-2", KeyA);

            AssertAdmitted(wire.Sent[0], "fwd-1", "put", KeyA.Value);
            AssertAdmitted(wire.Sent[1], "fwd-2", "delete", KeyA.Value);
            Assert.That(forwarder.Forwarded, Is.EqualTo(2));
        }

        [Test]
        public async Task AForwardedWritesRefusalIsRelayedAsErrorV1UnderTheOriginalId()
        {
            var forwarder = new ScriptedForwarder
            {
                OnPut = _ => Task.FromException(new CultNetWriteRefusedException(new CultNetErrorMessage { Error = "primary says no", Code = "write_not_authorized" })),
                OnDelete = _ => Task.FromException(new CultNetWriteRefusedException(CultNetErrorMessage.ForMessageInvalid("recordKey", "bad")))
            };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            await rig.Delete(wire, "fwd-2", KeyA);

            AssertRefused(wire.Sent[0], "fwd-1", "write_not_authorized");
            Assert.That(((CultNetErrorV1Message)wire.Sent[0]).Error, Is.EqualTo("primary says no"));
            AssertRefused(wire.Sent[1], "fwd-2", "message_invalid");
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task AForwardThePrimaryNeverAnsweredRelaysNothingAndMayBeRetried(bool unconfirmed)
        {
            Exception failure = unconfirmed ? new CultNetWriteUnconfirmedException("fwd-1") : new TimeoutException("primary unreachable");
            var forwarder = new ScriptedForwarder { OnPut = _ => Task.FromException(failure) };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            Assert.That(wire.Sent, Is.Empty, "no receipt, no error, no synthesized refusal");

            forwarder.OnPut = _ => Task.CompletedTask;
            await rig.Put(wire, "fwd-1", KeyA, "one");
            AssertAdmitted(wire.Sent.Single(), "fwd-1", "put", KeyA.Value);
            Assert.That(forwarder.Forwarded, Is.EqualTo(2), "the unanswered attempt left no claim behind");
        }

        [Test]
        public async Task APeerThatDidNotOptInIsToldNothingOfAnUnansweredForward()
        {
            var forwarder = new ScriptedForwarder { OnPut = _ => Task.FromException(new CultNetWriteUnconfirmedException("fwd-1")) };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();

            await rig.Put(wire, "fwd-1", KeyA, "one");

            Assert.That(wire.Sent, Is.Empty);
        }

        [Test]
        public async Task AReceiptConnectionNotForwardableIsRefusedWithTheRoutingErrorAndItsId()
        {
            var rig = Build(primary: false);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");

            var error = AssertRefused(wire.Sent.Single(), "fwd-1", null);
            Assert.That(error.RoutingHint, Is.Not.Null);
        }

        [Test]
        public async Task AResendWhileTheFirstAttemptIsStillBeingAppliedIsNotAnsweredTwice()
        {
            var release = new TaskCompletionSource();
            var forwarder = new ScriptedForwarder { OnPut = _ => release.Task };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            var first = rig.Put(wire, "fwd-1", KeyA, "one");
            await rig.Put(wire, "fwd-1", KeyA, "one");
            Assert.That(wire.Sent, Is.Empty, "the resend waits for the first attempt's answer");
            release.SetResult();
            await first;

            AssertAdmitted(wire.Sent.Single(), "fwd-1", "put", KeyA.Value);
            Assert.That(forwarder.Forwarded, Is.EqualTo(1), "and is not forwarded again");
        }

        [Test]
        public void TheForwarderWaitsTheReceiptWindowByDefault()
        {
            Assert.That(new CultNetSchemaWriteForwarderOptions().ResultWindow, Is.EqualTo(CultNetWriteCompletion.ReceiptResultWindow));
        }
    }
}
