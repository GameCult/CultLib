#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using GameCult.Logging;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// A schema server's poll serves every peer whatever one peer sends. A message that cannot be
    /// decoded is answered message_invalid and its session kept until MaxConsecutiveRefusedFrames in a
    /// row end it; one that cannot be handled ends that peer's session. The fault is logged, and the
    /// drain goes on to the other peers.
    /// </summary>
    public class CultNetRudpSchemaServerPeerInputTests
    {
        private const uint ConnectionId = 0x10203096;
        private const string Canary = "canary-5f1c9e0b";

        private sealed class Client
        {
            public Socket Socket = null!;
            public CultNetRudpSession Session = null!;
            public EndPoint EndPoint => Socket.LocalEndPoint!;
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Errors { get; } = new();
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) => Errors.Add(message);
            public void LogDebug(string message) { }
        }

        private static Socket Bind()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return socket;
        }

        private static List<CultNetRudpPacket> Drain(Socket socket)
        {
            var packets = new List<CultNetRudpPacket>();
            var buffer = new byte[65535];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (socket.Poll(20_000, SelectMode.SelectRead))
            {
                var received = socket.ReceiveFrom(buffer, ref from);
                packets.Add(CultNetRudpPacketCodec.Decode(buffer.AsSpan(0, received).ToArray()));
            }
            return packets;
        }

        private static async Task<Client> Connect(RudpCultNetSchemaServer server)
        {
            var client = new Client
            {
                Socket = Bind(),
                Session = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId })
            };
            client.Socket.SendTo(CultNetRudpPacketCodec.Encode(client.Session.CreateConnect(0)), server.LocalEndPoint);
            await server.PollAvailableAsync(8);
            client.Session.Receive(Drain(client.Socket).Single(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            return client;
        }

        private static void SendSchema(RudpCultNetSchemaServer server, Client client, byte[] payload) =>
            client.Socket.SendTo(
                CultNetRudpPacketCodec.Encode(client.Session.Send("schema", payload, new CultNetRudpSendOptions { Reliable = true, Ordered = true })),
                server.LocalEndPoint);

        private static byte[] Malformed() => new byte[] { 0xc1 };

        // Well-formed MessagePack naming a schema version no runtime knows. The decoder's own error
        // quotes that version.
        private static byte[] UnknownSchemaVersion() =>
            MessagePackSerializer.Serialize(
                new Dictionary<string, object> { ["schemaVersion"] = "cultnet." + Canary + ".v0", ["messageId"] = Canary },
                CultNetSchemaMessageSerialization.Options);

        private sealed class Scene : IDisposable
        {
            public RudpCultNetSchemaServer Server = null!;
            public Client Bad = null!;
            public Client Good = null!;
            public CapturingLogger Logger = new();
            public List<(string MessageId, EndPoint From)> Served = new();
            public List<EndPoint> Ended = new();

            public void Dispose()
            {
                Server.Dispose();
                Bad.Socket.Dispose();
                Good.Socket.Dispose();
            }
        }

        // The bad peer's frame is read first, so the drain reaches the good peer only by getting past it.
        private static async Task<Scene> BadThenGood(byte[] badPayload)
        {
            var scene = new Scene
            {
                Server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
                {
                    RuntimeId = "csharp-peer-input",
                    ConnectionId = ConnectionId
                })
            };
            scene.Server.Logger = scene.Logger;
            scene.Server.OnCultNet<CultNetShardCatalogRequestMessage>((message, peer) =>
                scene.Served.Add((message.MessageId, peer.RemoteEndPoint)));
            scene.Server.PeerDisconnected += peer => scene.Ended.Add(((RudpCultNetSchemaServerPeer)peer).RemoteEndPoint);
            scene.Bad = await Connect(scene.Server);
            scene.Good = await Connect(scene.Server);

            SendSchema(scene.Server, scene.Bad, badPayload);
            SendSchema(scene.Server, scene.Good, CultNetSchemaMessageSerialization.Serialize(
                new CultNetShardCatalogRequestMessage { MessageId = "good-request" }));
            return scene;
        }

        [Test]
        public async Task AMalformedPayloadDoesNotStopTheDrainServingAnotherPeer()
        {
            using var scene = await BadThenGood(Malformed());

            Assert.DoesNotThrowAsync(async () => await scene.Server.PollAvailableAsync(16));

            Assert.That(scene.Served, Is.EqualTo(new[] { ("good-request", scene.Good.EndPoint) }));
        }

        [Test]
        public async Task AnUnknownSchemaVersionDoesNotStopTheDrainServingAnotherPeer()
        {
            using var scene = await BadThenGood(UnknownSchemaVersion());

            Assert.DoesNotThrowAsync(async () => await scene.Server.PollAvailableAsync(16));

            Assert.That(scene.Served, Is.EqualTo(new[] { ("good-request", scene.Good.EndPoint) }));
        }

        [Test]
        public async Task PollOnceDoesNotThrowForAPeersUndecodableMessage()
        {
            using var scene = await BadThenGood(Malformed());

            Assert.DoesNotThrowAsync(async () =>
            {
                for (var attempt = 0; attempt < 16; attempt++)
                    await scene.Server.PollOnceAsync();
            });

            Assert.That(scene.Served, Is.EqualTo(new[] { ("good-request", scene.Good.EndPoint) }));
        }

        [Test]
        public async Task APeerWhoseMessageCannotBeDecodedIsAnsweredAndKept()
        {
            using var scene = await BadThenGood(Malformed());

            try { await scene.Server.PollAvailableAsync(16); }
            catch (MessagePackSerializationException) { Assert.Fail("the poll threw for a peer's input"); }

            Assert.That(scene.Ended, Is.Empty);
            Assert.That(scene.Server.Peers, Has.Count.EqualTo(2));
            var refusal = ErrorsReceivedBy(scene.Bad).Single();
            Assert.That(refusal.Code, Is.EqualTo("message_invalid"));
            Assert.That(refusal.Details!.Field, Is.EqualTo("message"));
            Assert.That(Drain(scene.Good.Socket).Select(packet => packet.PacketType), Has.None.EqualTo(CultNetRudpPacketType.Disconnect));
            Assert.That(scene.Logger.Errors, Has.Count.EqualTo(1));
        }

        // The RUDP schema server implements no receipt rules, so it must not advertise them (write-receipt.md, Capability and the
        // conformance table): whatever hello it sends lists neither frame.
        [Test]
        public async Task TheRudpSchemaServerNeverAdvertisesTheReceiptFrames()
        {
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-rudp-no-receipts",
                ConnectionId = ConnectionId
            });
            var client = await Connect(server);
            try
            {
                SendSchema(server, client, CultNetSchemaMessageSerialization.Serialize(new CultNetHelloMessage
                {
                    RuntimeId = "receipt-client",
                    RuntimeKind = "test",
                    SupportedMessageVersions = [CultNetSchemaVersions.WriteAdmitted, CultNetSchemaVersions.ErrorV1]
                }));
                await server.PollAvailableAsync(16);

                var hellos = new List<CultNetHelloMessage>();
                foreach (var packet in Drain(client.Socket))
                    foreach (var frame in client.Session.Receive(packet, 1).Delivered)
                        if (CultNetSchemaMessageSerialization.Deserialize(frame.Payload) is CultNetHelloMessage hello)
                            hellos.Add(hello);

                foreach (var hello in hellos)
                {
                    var listed = hello.SupportedMessageVersions ?? Array.Empty<string>();
                    Assert.That(listed, Has.None.EqualTo(CultNetSchemaVersions.WriteAdmitted));
                    Assert.That(listed, Has.None.EqualTo(CultNetSchemaVersions.ErrorV1));
                }
            }
            finally
            {
                client.Socket.Dispose();
            }
        }

        private static List<CultNetErrorMessage> ErrorsReceivedBy(Client client)
        {
            var errors = new List<CultNetErrorMessage>();
            foreach (var packet in Drain(client.Socket))
            {
                var received = client.Session.Receive(packet, 1);
                foreach (var frame in received.Delivered)
                    if (CultNetSchemaMessageSerialization.Deserialize(frame.Payload) is CultNetErrorMessage error)
                        errors.Add(error);
            }
            return errors;
        }

        // The bad peer acknowledges what it received and the server polls on.
        private static async Task Acknowledge(Scene scene)
        {
            scene.Bad.Socket.SendTo(CultNetRudpPacketCodec.Encode(scene.Bad.Session.CreateAck()), scene.Server.LocalEndPoint);
            await scene.Server.PollAvailableAsync(16);
        }

        // The bad peer has sent seven undecodable frames (BadThenGood sent the first), each answered.
        private static async Task<Scene> AtTheCap()
        {
            var scene = await BadThenGood(Malformed());
            await scene.Server.PollAvailableAsync(16);
            ErrorsReceivedBy(scene.Bad);
            for (var i = 0; i < 6; i++)
            {
                SendSchema(scene.Server, scene.Bad, Malformed());
                await scene.Server.PollAvailableAsync(16);
                ErrorsReceivedBy(scene.Bad);
            }
            Assert.That(scene.Ended, Is.Empty);
            return scene;
        }

        // The capped frame's answer is lost on the wire: the server resends it, and the goodbye follows only
        // once the peer has acknowledged it.
        [Test]
        public async Task ALostCappedAnswerIsResentAndTheGoodbyeWaitsForItsAcknowledgement()
        {
            using var scene = await AtTheCap();
            SendSchema(scene.Server, scene.Bad, Malformed());
            await scene.Server.PollAvailableAsync(16);

            var lost = Drain(scene.Bad.Socket);
            Assert.That(lost.Where(p => p.PacketType == CultNetRudpPacketType.Data), Is.Not.Empty, "fixture: the answer was sent and is lost here");
            Assert.That(lost.Select(p => p.PacketType), Has.None.EqualTo(CultNetRudpPacketType.Disconnect), "no goodbye before the answer is acknowledged");
            Assert.That(scene.Ended, Is.Empty);

            var resent = new List<CultNetRudpPacket>();
            for (var attempt = 0; attempt < 40 && resent.Count == 0; attempt++)
            {
                await Task.Delay(50);
                scene.Server.PollResends();
                resent = Drain(scene.Bad.Socket);
            }
            Assert.That(resent.Select(p => p.PacketType), Has.None.EqualTo(CultNetRudpPacketType.Disconnect), "still no goodbye");
            var answers = new List<CultNetErrorMessage>();
            foreach (var packet in resent)
                foreach (var frame in scene.Bad.Session.Receive(packet, 1).Delivered)
                    if (CultNetSchemaMessageSerialization.Deserialize(frame.Payload) is CultNetErrorMessage error)
                        answers.Add(error);
            Assert.That(answers.Select(e => e.Code), Is.EqualTo(new[] { "message_invalid" }), "the lost answer arrived by resend");
            Assert.That(scene.Ended, Is.Empty);

            await Acknowledge(scene);
            Assert.That(scene.Ended, Is.EqualTo(new[] { scene.Bad.EndPoint }), "acknowledged: the session ends");
            Assert.That(Drain(scene.Bad.Socket).Select(p => p.PacketType), Has.Some.EqualTo(CultNetRudpPacketType.Disconnect));
        }

        // A peer that never acknowledges the answer is not kept for ever: the session ends after the grace.
        [Test]
        public async Task ACappedSessionWhoseAnswerIsNeverAcknowledgedEndsAfterTheGrace()
        {
            using var scene = await AtTheCap();
            SendSchema(scene.Server, scene.Bad, Malformed());
            await scene.Server.PollAvailableAsync(16);
            Drain(scene.Bad.Socket);

            await Task.Delay(1500); // the grace is two seconds
            scene.Server.PollResends();
            Assert.That(scene.Ended, Is.Empty, "inside the grace");
            await Task.Delay(700);
            scene.Server.PollResends();
            Assert.That(scene.Ended, Is.EqualTo(new[] { scene.Bad.EndPoint }), "the grace ran out");
        }

        // Nothing behind the capped frame is served while its answer is delivered.
        [Test]
        public async Task NothingBehindTheCappedFrameIsServed()
        {
            using var scene = await AtTheCap();
            SendSchema(scene.Server, scene.Bad, Malformed());
            SendSchema(scene.Server, scene.Bad, CultNetSchemaMessageSerialization.Serialize(
                new CultNetShardCatalogRequestMessage { MessageId = "behind-the-cap" }));
            await scene.Server.PollAvailableAsync(16);
            await scene.Server.PollAvailableAsync(16);

            Assert.That(scene.Served.Select(s => s.MessageId), Does.Not.Contain("behind-the-cap"));
        }

        // The numbers are the shared cross-runtime cap, spelled out so changing the constant fails here.
        [Test]
        public async Task EightConsecutiveUndecodableFramesEndTheSessionAfterTheEighthIsAnswered()
        {
            Assert.That(RudpCultNetSchemaServer.MaxConsecutiveRefusedFrames, Is.EqualTo(8));
            using var scene = await BadThenGood(Malformed());
            await scene.Server.PollAvailableAsync(16);
            ErrorsReceivedBy(scene.Bad);
            async Task Refuse(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    SendSchema(scene.Server, scene.Bad, Malformed());
                    await scene.Server.PollAvailableAsync(16);
                }
            }

            await Refuse(6); // with the first, seven in a row
            Assert.That(ErrorsReceivedBy(scene.Bad), Has.Count.EqualTo(6));
            Assert.That(scene.Ended, Is.Empty);
            SendSchema(scene.Server, scene.Bad, CultNetSchemaMessageSerialization.Serialize(
                new CultNetShardCatalogRequestMessage { MessageId = "resets-the-run" }));
            await scene.Server.PollAvailableAsync(16);
            Assert.That(scene.Served.Select(s => s.MessageId), Does.Contain("resets-the-run"));
            await Refuse(7);
            Assert.That(ErrorsReceivedBy(scene.Bad), Has.Count.EqualTo(7));
            Assert.That(scene.Ended, Is.Empty, "a decoded frame reset the run");

            await Refuse(1);
            Assert.That(ErrorsReceivedBy(scene.Bad).Select(e => e.Code), Is.EqualTo(new[] { "message_invalid" }), "the eighth is answered too");
            Assert.That(scene.Ended, Is.Empty, "the session ends once the answer is acknowledged");
            await Acknowledge(scene);
            Assert.That(scene.Ended, Is.EqualTo(new[] { scene.Bad.EndPoint }));
            Assert.That(scene.Server.Peers.Select(peer => peer.RemoteEndPoint), Is.EqualTo(new[] { scene.Good.EndPoint }));
        }

        // The peer's malformed message and a well-formed one after it arrive in one datagram's
        // delivery: the second is held for order, and both are released together. The session is
        // kept through the first, so the second is handled.
        [Test]
        public async Task AFrameQueuedBehindAnUndecodableOneIsStillHandled()
        {
            var logger = new CapturingLogger();
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-peer-input",
                ConnectionId = ConnectionId
            });
            server.Logger = logger;
            var served = new List<string>();
            server.OnCultNet<CultNetShardCatalogRequestMessage>((message, _) => served.Add(message.MessageId));
            var bad = await Connect(server);
            var options = new CultNetRudpSendOptions { Reliable = true, Ordered = true };
            var malformed = bad.Session.Send("schema", Malformed(), options);
            var after = bad.Session.Send("schema", CultNetSchemaMessageSerialization.Serialize(
                new CultNetShardCatalogRequestMessage { MessageId = "after-the-refused-one" }), options);
            bad.Socket.SendTo(CultNetRudpPacketCodec.Encode(after), server.LocalEndPoint);
            bad.Socket.SendTo(CultNetRudpPacketCodec.Encode(malformed), server.LocalEndPoint);

            await server.PollAvailableAsync(16);

            Assert.That(served, Is.EqualTo(new[] { "after-the-refused-one" }));
            Assert.That(server.Peers, Has.Count.EqualTo(1));
            Assert.That(logger.Errors, Has.Count.EqualTo(1));
            bad.Socket.Dispose();
        }

        private static async Task<(RudpCultNetSchemaServer Server, Client Client, CapturingLogger Logger)> ServerWhoseHandlerAnswers(
            byte[] response, int? maxFragmentBytes, int? unsendableAfter = null)
        {
            var logger = new CapturingLogger();
            var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-peer-input",
                ConnectionId = ConnectionId,
                MaxFragmentBytes = maxFragmentBytes
            });
            server.Logger = logger;
            server.OnCultNet<CultNetShardCatalogRequestMessage>((_, peer) =>
            {
                if (unsendableAfter is { } count)
                    server.Transport.UnsendableAfter[peer.RemoteEndPoint] = count;
                server.Transport.SendSchema(peer.TransportPeer, response);
            });
            var client = await Connect(server);
            SendSchema(server, client, CultNetSchemaMessageSerialization.Serialize(
                new CultNetShardCatalogRequestMessage { MessageId = "ask" }));
            return (server, client, logger);
        }

        // A handler whose own response can never be sent (larger than a datagram, unfragmented) fails
        // on the server's account, not the peer's. The session still ends, but its goodbye does not say
        // the peer's packet was refused.
        [Test]
        public async Task AHandlerFaultEndsTheSessionWithAReasonThatDoesNotBlameThePeer()
        {
            var (server, client, logger) = await ServerWhoseHandlerAnswers(new byte[70_000], maxFragmentBytes: null);
            using (server)
            {
                await server.PollAvailableAsync(16);

                var goodbye = Drain(client.Socket).Single(packet => packet.PacketType == CultNetRudpPacketType.Disconnect);
                Assert.That(Encoding.UTF8.GetString(goodbye.Payload!), Is.EqualTo("server could not handle a message"));
                Assert.That(server.Peers, Is.Empty);
                Assert.That(logger.Errors, Has.Count.EqualTo(1));
                Assert.That(logger.Errors[0], Does.Contain("handler failed (SocketException)"));
                Assert.That(logger.Errors[0], Does.Contain("that peer's session ended."));
            }
            client.Socket.Dispose();
        }

        // A response that fails after its first fragment has already ended the session over the send,
        // with a goodbye that names the send failure. The backstop ends nothing more and says so.
        [Test]
        public async Task AHandlerWhoseResponseEndedTheSessionIsLoggedAsAlreadyEnded()
        {
            var (server, client, logger) = await ServerWhoseHandlerAnswers(new byte[3000], maxFragmentBytes: 1000, unsendableAfter: 1);
            using (server)
            {
                await server.PollAvailableAsync(16);
                await server.PollAvailableAsync(16);

                var goodbyes = Drain(client.Socket).Where(packet => packet.PacketType == CultNetRudpPacketType.Disconnect).ToList();
                Assert.That(goodbyes, Has.Count.EqualTo(1));
                Assert.That(Encoding.UTF8.GetString(goodbyes[0].Payload!), Does.StartWith("packet could not be sent: "));
                Assert.That(server.Peers, Is.Empty);
                Assert.That(logger.Errors, Has.Count.EqualTo(1));
                Assert.That(logger.Errors[0], Does.Contain("that peer's session had already ended."));
            }
            client.Socket.Dispose();
        }

        [Test]
        public async Task TheDropIsLoggedByErrorTypeWithoutEchoingWhatThePeerSent()
        {
            using var scene = await BadThenGood(UnknownSchemaVersion());

            try { await scene.Server.PollAvailableAsync(16); }
            catch (MessagePackSerializationException) { Assert.Fail("the poll threw for a peer's input"); }

            // The decoder's own error message quotes the unknown version, which is the peer's input.
            Assert.That(scene.Logger.Errors, Has.Count.EqualTo(1));
            Assert.That(scene.Logger.Errors[0], Does.Contain(nameof(MessagePackSerializationException)));
            Assert.That(scene.Logger.Errors[0], Does.Not.Contain(Canary));
        }
    }
}
