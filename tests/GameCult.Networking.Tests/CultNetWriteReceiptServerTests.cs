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
            public int Asked { get; private set; }
            public CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request)
            {
                Asked++;
                return Denied.Contains(request.RecordKey.Value) ? CultNetRemoteWriteDecision.Refuse("denied") : CultNetRemoteWriteDecision.Grant();
            }
        }

        private sealed class Wire
        {
            public List<ICultNetSchemaMessage> Sent { get; } = new();
            /// <summary>When set, the connection cannot queue what it is sent.</summary>
            public bool Unsendable { get; set; }
            public int Ended { get; private set; }
            public LiteNetLibTransportConnection Transport { get; }
            public CultNetServerPeer Peer { get; }

            public Wire()
            {
                Transport = new LiteNetLibTransportConnection(
                    bytes =>
                    {
                        if (Unsendable) throw new InvalidOperationException("the send queue is full");
                        Sent.Add(LiteNetLibTransportConnection.DecodeSchema(LiteNetLibTransportConnection.Decode(bytes)));
                    },
                    () => Ended++);
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
        public async Task AReusedIdIsJudgedBeforeAuthorityIsAskedAgain()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");
            rig.Authority.Denied.Add(KeyB.Value);

            await rig.Put(wire, "m1", KeyB, "other");

            AssertRefused(wire.Sent[1], "m1", "message_invalid");
            Assert.That(rig.Authority.Asked, Is.EqualTo(1), "authority is asked on an id's first sighting only");
        }

        [Test]
        public async Task AResendIsAnsweredFromMemoryWithoutAskingAuthorityAgain()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");
            rig.Authority.Denied.Add(KeyA.Value);

            await rig.Put(wire, "m1", KeyA, "one");

            AssertAdmitted(wire.Sent[1], "m1", "put", KeyA.Value);
            Assert.That(rig.Authority.Asked, Is.EqualTo(1));
        }

        [Test]
        public async Task ARefusedWriteIsRememberedSoItsResendReplaysTheRefusal()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Add(KeyA.Value);
            await rig.Put(wire, "m1", KeyA, "one");
            rig.Authority.Denied.Clear();

            await rig.Put(wire, "m1", KeyA, "one");

            var first = AssertRefused(wire.Sent[0], "m1", "write_not_authorized");
            var again = AssertRefused(wire.Sent[1], "m1", "write_not_authorized");
            Assert.That(again.Error, Is.EqualTo(first.Error));
            Assert.That(rig.TextOf(KeyA), Is.Null, "the resend was not decided afresh");
            Assert.That(rig.Authority.Asked, Is.EqualTo(1));
        }

        [Test]
        public async Task ARefusedIdReusedForOtherContentIsMessageInvalid()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Add(KeyA.Value);
            await rig.Put(wire, "m1", KeyA, "one");

            await rig.Put(wire, "m1", KeyB, "other");

            AssertRefused(wire.Sent[1], "m1", "message_invalid");
            Assert.That(rig.TextOf(KeyB), Is.Null);
        }

        [Test]
        public async Task RefusedWritesCountAgainstTheRememberedWriteLimit()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Add(KeyA.Value);
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
            {
                await rig.Put(wire, $"r{index}", KeyA, $"v{index}");
            }

            wire.Sent.Clear();
            await rig.Put(wire, "over", KeyB, "x");
            await rig.Put(wire, "r0", KeyA, "v0");

            AssertRefused(wire.Sent[0], "over", "limit_reached");
            AssertRefused(wire.Sent[1], "r0", "write_not_authorized");
            Assert.That(rig.TextOf(KeyB), Is.Null);
        }

        [Test]
        public async Task APutAndADeleteNeverShareAnId()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "p1", KeyA, "one");
            await rig.Delete(wire, "d1", KeyA);
            wire.Sent.Clear();

            await rig.Delete(wire, "p1", KeyA);
            await rig.Put(wire, "d1", KeyA, "one");

            AssertRefused(wire.Sent[0], "p1", "message_invalid");
            AssertRefused(wire.Sent[1], "d1", "message_invalid");
            Assert.That(rig.TextOf(KeyA), Is.Null, "neither reuse was applied");
        }

        [Test]
        public async Task ADeleteAsTheFirstFrameSettlesTheConnectionAsLegacy()
        {
            var rig = Build();
            var wire = new Wire();
            await rig.Delete(wire, "d1", KeyA);
            await rig.Hello(wire, BothFrames);
            await rig.Put(wire, "m1", KeyA, "one");

            Assert.That(wire.Sent, Is.Empty, "a hello after the first delete does not opt the connection in");
        }

        [Test]
        public async Task AnAnswerThatCannotBeSentEndsTheConnectionInsteadOfLeavingTheWriteSilent()
        {
            var (rig, wire) = await Capable();
            wire.Unsendable = true;
            await rig.Put(wire, "m1", KeyA, "one");
            Assert.That(wire.Ended, Is.EqualTo(1), "a receipt that cannot be queued");

            rig.Authority.Denied.Add(KeyB.Value);
            await rig.Put(wire, "m2", KeyB, "two");
            Assert.That(wire.Ended, Is.EqualTo(2), "a refusal that cannot be queued");

            wire.Transport.AnswerUnreadableSchemaFrame(new byte[] { 0xC1 });
            Assert.That(wire.Ended, Is.EqualTo(3), "an unreadable-frame answer that cannot be queued");
        }

        [Test]
        public async Task AnUnexpectedFailureIsRefusedWithFixedTextNotTheExceptionsOwn()
        {
            var (rig, wire) = await Capable();
            var message = rig.PutOf("m1", KeyA, "one");
            message.Document!.Payload = [0xC1];
            var legacy = new Wire();

            await rig.PutHandler(message, wire.Peer);
            await rig.PutHandler(message, legacy.Peer);
            await rig.DeleteHandler(new CultNetDocumentDeleteMessage { MessageId = "d1", SchemaId = "no.such.schema", RecordKey = "k" }, legacy.Peer);

            Assert.That(((CultNetErrorV1Message)wire.Sent.Single()).Error, Is.EqualTo(CultNetWriteReceiptSession.WriteFailedText));
            Assert.That(((CultNetErrorMessage)legacy.Sent[0]).Error, Is.EqualTo(CultNetWriteReceiptSession.WriteFailedText));
        }

        [Test]
        public void TheLimitReachedRefusalIsTheSpecVectorByteForByte()
        {
            const string vector = "86AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7631A56572726F72D93B6C696D69745F726561636865643A207468652073657373696F6E27732072656D656D62657265642D7772697465206C696D69742069732066756C6CAB726F7574696E6748696E74C0A4636F6465AD6C696D69745F72656163686564A764657461696C7384A56669656C64C0A576616C7565C0A461734F66C0A763757272656E74C0A96D6573736167654964A87075742D31303235";

            var bytes = CultNetSchemaMessageSerialization.Serialize(CultNetErrorV1Message.LimitReached("put-1025"));

            Assert.That(Convert.ToHexString(bytes), Is.EqualTo(vector));
        }

        [Test]
        public void TheSpecWireVectorsDecodeToTheirExpectedFields()
        {
            var admitted = (CultNetWriteAdmittedMessage)CultNetSchemaMessageSerialization.Deserialize(Convert.FromHexString(
                "85AD736368656D6156657273696F6EB963756C746E65742E77726974655F61646D69747465642E7630A96D6573736167654964A57075742D31A96F7065726174696F6EA3707574A8736368656D614964A76E6F74652E7630A97265636F72644B6579A66E6F74653A31"));
            Assert.That((admitted.MessageId, admitted.Operation, admitted.SchemaId, admitted.RecordKey),
                Is.EqualTo(("put-1", "put", "note.v0", "note:1")));

            var refused = (CultNetErrorV1Message)CultNetSchemaMessageSerialization.Deserialize(Convert.FromHexString(
                "86AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7631A56572726F72D93677726974655F6E6F745F617574686F72697A65643A2074686520636174616C6F6720726566757365642074686520646F63756D656E74AB726F7574696E6748696E74C0A4636F6465B477726974655F6E6F745F617574686F72697A6564A764657461696C7384A56669656C64A97265636F72644B6579A576616C7565A66E6F74653A32A461734F66C0A763757272656E74C0A96D6573736167654964A57075742D32"));
            Assert.That(refused.Error, Is.EqualTo("write_not_authorized: the catalog refused the document"));
            Assert.That(refused.Code, Is.EqualTo("write_not_authorized"));
            Assert.That(refused.MessageId, Is.EqualTo("put-2"));
            Assert.That((refused.Details!.Field, refused.Details.Value), Is.EqualTo(("recordKey", "note:2")));
            Assert.That(refused.Details.AsOf, Is.Null);
            Assert.That(refused.Details.Current, Is.Null);
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

            var limit = AssertRefused(wire.Sent[0], "over", "limit_reached");
            Assert.That(limit.Error, Is.EqualTo("limit_reached: the session's remembered-write limit is full"));
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

        [Test]
        public async Task AForwardThePrimaryLeftSilentIsNeverForwardedAgainOnTheSession()
        {
            var forwarder = new ScriptedForwarder { OnPut = _ => Task.FromException(new CultNetWriteUnconfirmedException("fwd-1")) };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            forwarder.OnPut = _ => Task.CompletedTask;
            await rig.Put(wire, "fwd-1", KeyA, "one");

            Assert.That(wire.Sent, Is.Empty, "no receipt, no error: the write stays unconfirmed");
            Assert.That(forwarder.Forwarded, Is.EqualTo(1), "the primary may have applied it, so it is not sent again");

            await rig.Put(wire, "fwd-1", KeyB, "other");
            AssertRefused(wire.Sent.Single(), "fwd-1", "message_invalid");
        }

        [Test]
        public async Task AForwardRefusedByThePrimaryReplaysItsRefusalOnResend()
        {
            var forwarder = new ScriptedForwarder
            {
                OnPut = _ => Task.FromException(new CultNetWriteRefusedException(new CultNetErrorMessage { Error = "primary says no", Code = "write_not_authorized" }))
            };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            await rig.Put(wire, "fwd-1", KeyA, "one");

            Assert.That(wire.Sent, Has.Count.EqualTo(2));
            Assert.That(((CultNetErrorV1Message)wire.Sent[1]).Error, Is.EqualTo("primary says no"));
            Assert.That(forwarder.Forwarded, Is.EqualTo(1));
        }

        [Test]
        public async Task AForwardThatWasNotSentMayBeRetriedUnderTheSameId()
        {
            var forwarder = new ScriptedForwarder { OnPut = _ => Task.FromException(new CultNetWriteNotSentException("fwd-1", new TimeoutException("primary unreachable"))) };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            Assert.That(wire.Sent, Is.Empty, "no receipt, no error, no synthesized refusal");

            forwarder.OnPut = _ => Task.CompletedTask;
            await rig.Put(wire, "fwd-1", KeyA, "one");
            AssertAdmitted(wire.Sent.Single(), "fwd-1", "put", KeyA.Value);
            Assert.That(forwarder.Forwarded, Is.EqualTo(2), "the unsent attempt left no claim behind");
        }

        // Once the frame may have gone out, whatever failed, the id is never forwarded again on the session.
        private static IEnumerable<TestCaseData> PostSendFailures()
        {
            yield return new TestCaseData(new TimeoutException("no answer")).SetName("timeout");
            yield return new TestCaseData(new System.IO.IOException("connection reset")).SetName("reset");
            yield return new TestCaseData(new InvalidOperationException("client disconnected")).SetName("disconnected");
            yield return new TestCaseData(new CultNetWriteUnconfirmedException("fwd-1")).SetName("no answer in the window");
            yield return new TestCaseData(new Exception("anything else, ambiguous")).SetName("ambiguous");
        }

        [TestCaseSource(nameof(PostSendFailures))]
        public async Task AForwardThatMayHaveBeenSentIsNeverForwardedAgainOnTheSession(Exception failure)
        {
            var forwarder = new ScriptedForwarder { OnPut = _ => Task.FromException(failure) };
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            await rig.Put(wire, "fwd-1", KeyA, "one");
            forwarder.OnPut = _ => Task.CompletedTask;
            await rig.Put(wire, "fwd-1", KeyA, "one");

            Assert.That(wire.Sent, Is.Empty, "unconfirmed: nothing answered, nothing synthesized");
            Assert.That(forwarder.Forwarded, Is.EqualTo(1), "the claim stays");
        }

        [Test]
        public async Task TheRealForwarderRetriesAConnectFailureButNotAFailureAfterTheWriteWasSent()
        {
            var clients = new Queue<ForwardingClient>();
            var forwarder = new CultNetSchemaWriteForwarder(new CultNetSchemaWriteForwarderOptions
            {
                CreateClient = () => clients.Dequeue(),
                ConnectTimeout = TimeSpan.FromMilliseconds(100),
                ResultWindow = TimeSpan.FromMilliseconds(100)
            });
            var rig = Build(primary: false, forwarder: forwarder);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();

            clients.Enqueue(new ForwardingClient { ConnectFails = true });
            await rig.Put(wire, "fwd-1", KeyA, "one");
            var sent = new ForwardingClient { AdmitWrites = true };
            clients.Enqueue(sent);
            await rig.Put(wire, "fwd-1", KeyA, "one");
            AssertAdmitted(wire.Sent.Single(), "fwd-1", "put", KeyA.Value);

            wire.Sent.Clear();
            var reset = new ForwardingClient { WriteSendFails = true };
            clients.Enqueue(reset);
            await rig.Put(wire, "fwd-2", KeyA, "two");
            clients.Enqueue(new ForwardingClient { AdmitWrites = true });
            await rig.Put(wire, "fwd-2", KeyA, "two");
            Assert.That(wire.Sent, Is.Empty, "the write send threw: it may have gone out");
            Assert.That(clients, Has.Count.EqualTo(1), "and the id was not forwarded again");
        }

        private sealed class ForwardingClient : ICultNetSchemaClient
        {
            private Action<CultNetWriteAdmittedMessage>? _admitted;
            public bool ConnectFails { get; set; }
            public bool NeverConnects { get; set; }
            public bool HelloSendFails { get; set; }
            public bool WriteSendFails { get; set; }
            public bool WriteNotSentFails { get; set; }
            public bool AdmitWrites { get; set; }
            public bool Connected { get; private set; }
            public List<ICultNetSchemaMessage> Sent { get; } = new();

            public void Connect(string host, int port)
            {
                if (ConnectFails) throw new System.Net.Sockets.SocketException();
                Connected = !NeverConnects;
            }

            public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
            {
                if (message is CultNetHelloMessage)
                {
                    if (HelloSendFails) throw new System.IO.IOException("hello failed");
                    return;
                }

                if (WriteNotSentFails) throw new CultNetWriteNotSentException("x");
                if (WriteSendFails) throw new System.IO.IOException("send failed after an unknown number of bytes");
                Sent.Add(message);
                if (AdmitWrites && message is CultNetDocumentPutRawMessage put)
                    _admitted?.Invoke(new CultNetWriteAdmittedMessage { MessageId = put.MessageId, Operation = "put", SchemaId = put.Document?.SchemaId, RecordKey = put.Document?.RecordKey });
            }

            public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
            {
                if (callback is Action<CultNetWriteAdmittedMessage> admitted) _admitted = admitted;
            }

            public void Dispose() { }
        }

        private static CultNetSchemaWriteForwarder RealForwarder(ForwardingClient client) => new(new CultNetSchemaWriteForwarderOptions
        {
            CreateClient = () => client,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            ResultWindow = TimeSpan.FromMilliseconds(100)
        });

        private static readonly CultNetShardDescriptor ForwardShard = new("notes", "primary", 1, isPrimary: false, primaryEndpoints: ["cultnet://primary.example.test:4075"]);

        private static CultNetDocumentPutRawMessage ForwardPut() => new() { MessageId = "fwd-1", Document = new CultNetRawDocumentRecord { SchemaId = "s", RecordKey = "k", Payload = [1] } };

        [TestCase("connect")]
        [TestCase("never connects")]
        [TestCase("hello")]
        [TestCase("write not sent")]
        public void TheForwarderNamesEveryFailureBeforeTheWriteWasSentAsNotSent(string stage)
        {
            var client = new ForwardingClient
            {
                ConnectFails = stage == "connect",
                NeverConnects = stage == "never connects",
                HelloSendFails = stage == "hello",
                WriteNotSentFails = stage == "write not sent"
            };

            Assert.ThrowsAsync<CultNetWriteNotSentException>(() => RealForwarder(client).ForwardPutAsync(ForwardShard, ForwardPut()));
            Assert.That(client.Sent, Is.Empty);
        }

        [Test]
        public void TheForwarderNamesNoPrimaryEndpointAsNotSent()
        {
            var shard = new CultNetShardDescriptor("notes", "primary", 1, isPrimary: false, primaryEndpoints: []);
            Assert.ThrowsAsync<CultNetWriteNotSentException>(() => RealForwarder(new ForwardingClient()).ForwardPutAsync(shard, ForwardPut()));
        }

        [Test]
        public void ASendThatThrewWithoutProvingNothingWasWrittenIsNotNotSent()
        {
            var client = new ForwardingClient { WriteSendFails = true };
            var failure = Assert.CatchAsync<Exception>(() => RealForwarder(client).ForwardPutAsync(ForwardShard, ForwardPut()));
            Assert.That(failure, Is.Not.InstanceOf<CultNetWriteNotSentException>(), "ambiguous: unconfirmed");
        }

        [Test]
        public void ASentWriteNobodyAnswersIsUnconfirmedNotNotSent()
        {
            var client = new ForwardingClient();
            Assert.ThrowsAsync<CultNetWriteUnconfirmedException>(() => RealForwarder(client).ForwardPutAsync(ForwardShard, ForwardPut()));
            Assert.That(client.Sent, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task TheRoutingRefusalAndTheUnownedSchemaRefusalCarryNoExceptionText()
        {
            var rig = Build(primary: false);
            var wire = new Wire();
            await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();
            await rig.Put(wire, "fwd-1", KeyA, "one");
            var routing = AssertRefused(wire.Sent.Single(), "fwd-1", null);
            Assert.That(routing.Error, Is.EqualTo(CultNetDatabaseServer.RoutingRefusalText));

            var secret = new CultNetUnownedSchemaException("secret.schema.v1", new CultRecordKey("secret:key"));
            var unowned = CultNetErrorMessage.ForUnownedSchema(secret);
            Assert.That(unowned.Error, Does.Not.Contain(secret.Message).And.Not.Contain("secret"));
            Assert.That(unowned.Details!.Value, Is.EqualTo("secret.schema.v1"), "the structured detail still names the schema");
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

        // ---- Heimdall round two ----

        private static string IdOfBytes(int bytes) => new string('i', bytes);

        [Test]
        public async Task AnIdOver256BytesIsRefusedBeforeAnythingIsRemembered()
        {
            var (rig, wire) = await Capable();
            var tooLong = IdOfBytes(257);

            await rig.Put(wire, tooLong, KeyA, "one");
            await rig.Put(wire, tooLong, KeyA, "one");

            Assert.That(wire.Sent, Has.Count.EqualTo(2));
            foreach (var sent in wire.Sent)
            {
                var error = AssertRefused(sent, tooLong, "message_invalid");
                Assert.That(error.Details!.Field, Is.EqualTo("messageId"));
                Assert.That(error.Error, Is.EqualTo("message_invalid: messageId exceeds 256 bytes"));
            }

            Assert.That(rig.TextOf(KeyA), Is.Null);
            // Nothing was remembered: the whole table is still free for admitted writes.
            wire.Sent.Clear();
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
                await rig.Put(wire, $"w{index}", new CultRecordKey($"tests:receipt:{index}"), "x");
            Assert.That(wire.Sent.OfType<CultNetWriteAdmittedMessage>().Count(), Is.EqualTo(CultNetWriteReceiptSession.MaxRememberedWrites));
        }

        [Test]
        public async Task TheIdLimitCountsUtf8BytesNotCharacters()
        {
            var (rig, wire) = await Capable();
            var atLimit = IdOfBytes(256);
            var multibyte = new string('\u00e9', 129); // 129 chars, 258 bytes

            await rig.Put(wire, atLimit, KeyA, "one");
            await rig.Put(wire, multibyte, KeyB, "two");

            AssertAdmitted(wire.Sent[0], atLimit, "put", KeyA.Value);
            AssertRefused(wire.Sent[1], multibyte, "message_invalid");
        }

        [Test]
        public void ARememberedEntryHoldsOnlyFixedSizeDigestsNotTheStrings()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var table = typeof(CultNetWriteReceiptSession).GetFields(flags)
                .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>));
            Assert.That(table, Is.Not.Empty);
            foreach (var field in table)
            {
                var args = field.FieldType.GetGenericArguments();
                Assert.That(args[0].IsValueType, Is.True, $"{field.Name} is keyed by {args[0]}");
                foreach (var held in args[1].IsValueType || args[1] == typeof(string) ? new[] { args[1] } : args[1].GetFields(flags).Select(f => f.FieldType).Concat(args[1].GetProperties(flags | BindingFlags.Public).Select(p => p.PropertyType)))
                {
                    Assert.That(held == typeof(string) || held == typeof(byte[]), Is.False, $"{field.Name} holds {held}");
                }
            }
        }

        [Test]
        public async Task AnAdmittedResendIsAnsweredFromDigestsWithTheResendsOwnBody()
        {
            var (rig, wire) = await Capable();
            await rig.Put(wire, "m1", KeyA, "one");
            await rig.Put(wire, "m1", KeyA, "one");

            AssertAdmitted(wire.Sent[1], "m1", "put", KeyA.Value);
            Assert.That(rig.LogLength, Is.EqualTo(1));
        }

        [Test]
        public async Task ARefusalIsReplayedForAResendAfterTheDigestChange()
        {
            var (rig, wire) = await Capable();
            rig.Authority.Denied.Add(KeyA.Value);
            await rig.Put(wire, "m1", KeyA, "one");
            await rig.Put(wire, "m1", KeyA, "one");

            AssertRefused(wire.Sent[0], "m1", "write_not_authorized");
            AssertRefused(wire.Sent[1], "m1", "write_not_authorized");
            Assert.That(rig.Authority.Asked, Is.EqualTo(1));
        }

        [Test]
        public async Task APutWithNoDocumentIsRefusedNamingTheDocumentWithFixedText([Values] bool capable)
        {
            var rig = Build();
            var wire = new Wire();
            if (capable) await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();
            var message = rig.PutOf("m1", KeyA, "one");
            message.Document = null!;

            await rig.PutHandler(message, wire.Peer);

            var sent = wire.Sent.Single();
            var error = capable ? AssertRefused(sent, "m1", "message_invalid").ToV0() : (CultNetErrorMessage)sent;
            Assert.That(error.Code, Is.EqualTo("message_invalid"));
            Assert.That(error.Details!.Field, Is.EqualTo("document"));
            Assert.That(error.Error, Is.EqualTo("message_invalid: document is required"));
        }

        [Test]
        public async Task APutWithNoSchemaIdIsRefusedNamingTheSchemaIdWithFixedText()
        {
            var (rig, wire) = await Capable();
            var message = rig.PutOf("m1", KeyA, "one");
            message.Document.SchemaId = null!;

            await rig.PutHandler(message, wire.Peer);

            var error = AssertRefused(wire.Sent.Single(), "m1", "message_invalid");
            Assert.That(error.Details!.Field, Is.EqualTo("schemaId"));
            Assert.That(error.Error, Is.EqualTo("message_invalid: schemaId is required"));
        }

        [Test]
        public async Task ADeleteWithNoSchemaIdIsRefusedNamingTheSchemaIdWithFixedText([Values] bool capable)
        {
            var rig = Build();
            var wire = new Wire();
            if (capable) await rig.Hello(wire, BothFrames);
            wire.Sent.Clear();
            var message = rig.DeleteOf("d1", KeyA);
            message.SchemaId = null!;

            await rig.DeleteHandler(message, wire.Peer);

            var sent = wire.Sent.Single();
            var error = capable ? AssertRefused(sent, "d1", "message_invalid").ToV0() : (CultNetErrorMessage)sent;
            Assert.That(error.Details!.Field, Is.EqualTo("schemaId"));
            Assert.That(error.Error, Is.EqualTo("message_invalid: schemaId is required"));
        }

        [Test]
        public async Task ARefusalAtTheCapIsRememberedSoAResendReplaysItAndADifferentContentResendIsRefused()
        {
            var (rig, wire) = await Capable();
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
                await rig.Put(wire, $"w{index}", new CultRecordKey($"tests:receipt:{index}"), "x");
            wire.Sent.Clear();

            await rig.Put(wire, "over", KeyA, "x");
            await rig.Put(wire, "over", KeyA, "x");
            await rig.Put(wire, "over", KeyB, "other");

            AssertRefused(wire.Sent[0], "over", "limit_reached");
            AssertRefused(wire.Sent[1], "over", "limit_reached");
            var changed = AssertRefused(wire.Sent[2], "over", "message_invalid");
            Assert.That(changed.Details!.Field, Is.EqualTo("messageId"));
            Assert.That(rig.TextOf(KeyA), Is.Null);
        }

        private static byte[] HashOf(string text) => CultNetWriteContentHash.Compute("put", "s", text, new byte[] { 1 });

        [Test]
        public void AnAbandonedClaimFreesNoSlotSoARefusedIdNeverBecomesAdmissible()
        {
            var session = new CultNetWriteReceiptSession();
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
                Assert.That(session.TryBegin($"w{index}", HashOf($"k{index}"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.New));
            Assert.That(session.TryBegin("over", HashOf("o"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));

            session.Abandoned("w0");

            Assert.That(session.TryBegin("over", HashOf("o"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));
            Assert.That(session.TryBegin("another", HashOf("a"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));
            // The abandoned id itself is judged afresh, in its own slot.
            Assert.That(session.TryBegin("w0", HashOf("k0"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.New));
        }

        [Test]
        public void TheRefusalSetIsBoundedAndNeverAdmitsAnId()
        {
            var session = new CultNetWriteReceiptSession();
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
                session.TryBegin($"w{index}", HashOf($"k{index}"), out _);
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites + 5; index++)
                Assert.That(session.TryBegin($"o{index}", HashOf("o"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));

            session.Abandoned("w1");

            Assert.That(session.TryBegin("o1030", HashOf("o"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));
        }

        [Test]
        public void TheRefusalSetStopsRememberingAtItsBound()
        {
            var session = new CultNetWriteReceiptSession();
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedWrites; index++)
                session.TryBegin($"w{index}", HashOf($"k{index}"), out _);
            for (var index = 0; index < CultNetWriteReceiptSession.MaxRememberedLimitRefusals; index++)
                session.TryBegin($"o{index}", HashOf("o"), out _);

            // Past the bound a refusal is not remembered: its changed-content resend is simply refused at the limit again.
            Assert.That(session.TryBegin("late", HashOf("a"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));
            Assert.That(session.TryBegin("late", HashOf("b"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Full));
            // Inside the bound it is remembered.
            Assert.That(session.TryBegin("o0", HashOf("changed"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.DifferentContent));
        }

        [Test]
        public void AnAbandonedIdMayBeResentWithOtherContentBecauseItWasNeverSent()
        {
            var session = new CultNetWriteReceiptSession();
            session.TryBegin("m1", HashOf("a"), out _);
            session.Abandoned("m1");

            Assert.That(session.TryBegin("m1", HashOf("b"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.New));
            Assert.That(session.TryBegin("m1", HashOf("b"), out _), Is.EqualTo(CultNetWriteReceiptSession.Begin.Unanswered));
        }
    }
}
