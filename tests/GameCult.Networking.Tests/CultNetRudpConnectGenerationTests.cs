#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GameCult.Mesh;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// A Connect's sequence says whether it repeats the one a session accepted, and the handshake
    /// alone seeds the watermark that orders delivery.
    /// </summary>
    public sealed class CultNetRudpConnectGenerationTests
    {
        private const uint ConnectionId = 0x10203090;
        private static readonly CultNetRudpSendOptions Ordered = new CultNetRudpSendOptions { Reliable = true, Ordered = true };

        private static CultNetRudpSession Session(uint initialSequence) =>
            new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = initialSequence });

        private static CultNetRudpPacket Send(CultNetRudpSession session, string text, bool ordered = true, string channel = "schema") =>
            session.Send(channel, Encoding.UTF8.GetBytes(text), new CultNetRudpSendOptions { Reliable = true, Ordered = ordered });

        private static string[] Names(CultNetRudpReceiveResult result) =>
            result.Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray();

        private static (CultNetRudpPacket Connect, CultNetRudpPacket Accept) Handshake(CultNetRudpSession client, CultNetRudpSession server)
        {
            var connect = client.CreateConnect(0);
            var accept = server.AcceptConnect(connect, 0);
            client.Receive(accept, 0);
            Assert.That(client.Connected && server.Connected, Is.True);
            return (connect, accept);
        }

        private static Socket Bind()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.ReceiveTimeout = 20;
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

        [Test]
        public void AReconnectOnTheSameSessionIsNotStrandedByAWriteTheEndedSessionOwed()
        {
            var client = Session(1);
            var server = Session(500);
            Handshake(client, server);
            Send(server, "lost");
            server.CreateDisconnect(Encoding.UTF8.GetBytes("gone"));

            client.Receive(server.AcceptConnect(client.CreateConnect(1), 1), 1);
            Assert.That(Names(client.Receive(Send(server, "after"), 2)), Is.EqualTo(new[] { "after" }));
        }

        [Test]
        public void AServerThatAcceptsANewClientForgetsTheOldClientsSequences()
        {
            var oldClient = Session(50);
            var server = Session(500);
            Handshake(oldClient, server);
            foreach (var name in new[] { "c1", "c2", "c3" })
                Assert.That(Names(server.Receive(Send(oldClient, name), 1)), Is.EqualTo(new[] { name }));
            server.CreateDisconnect(Encoding.UTF8.GetBytes("restart"));

            var newClient = Session(2);
            newClient.Receive(server.AcceptConnect(newClient.CreateConnect(2), 2), 2);
            Assert.That(Names(server.Receive(Send(newClient, "fresh"), 3)), Is.EqualTo(new[] { "fresh" }));
        }

        [Test]
        public void ARetransmittedConnectAfterDataHasFlowedDoesNotResetTheSession()
        {
            var client = Session(1);
            var server = Session(500);
            var (connect, _) = Handshake(client, server);
            var frame = Send(client, "once");
            Assert.That(Names(server.Receive(frame, 1)), Is.EqualTo(new[] { "once" }));

            Assert.That(server.ConnectRepeats(connect), Is.True);
            Assert.That(server.AcceptConnect(connect, 2).PacketType, Is.EqualTo(CultNetRudpPacketType.Ack), "the Accept was already acknowledged");
            Assert.That(server.OutstandingReliablePacketCount, Is.EqualTo(0), "a repeat queued something");
            Assert.That(server.Receive(frame, 3).Delivered, Is.Empty, "a retransmitted Connect made the server forget what it had delivered");
        }

        [Test]
        public void AConnectWithAnotherSequenceIsNotARepeatWhateverItCarries()
        {
            var server = Session(500);
            var connect = Session(1).CreateConnect(0, Encoding.UTF8.GetBytes("same payload"));
            server.AcceptConnect(connect, 0);
            Assert.That(server.ConnectRepeats(connect), Is.True);

            Assert.That(server.ConnectRepeats(Session(9).CreateConnect(0, Encoding.UTF8.GetBytes("same payload"))), Is.False);
            var notAConnect = CultNetRudpPacketCodec.Decode(CultNetRudpPacketCodec.Encode(connect));
            notAConnect.PacketType = CultNetRudpPacketType.Ping;
            Assert.That(server.ConnectRepeats(notAConnect), Is.False);
            server.CreateDisconnect();
            Assert.That(server.ConnectRepeats(connect), Is.False, "an ended session repeats nothing");
        }

        [Test]
        public void AnAcceptThatDoesNotNameThePendingConnectIsIgnored()
        {
            var client = Session(10);
            var server = Session(500);
            var connect = client.CreateConnect(0);
            var accept = server.AcceptConnect(connect, 0);

            var stale = CultNetRudpPacketCodec.Decode(CultNetRudpPacketCodec.Encode(accept));
            stale.Ack = connect.Sequence + 50;
            stale.AckMask = 0;
            client.Receive(stale, 1);
            Assert.That(client.Connected, Is.False, "a stale Accept connected the session");
            Assert.That(client.PendingReliableSequences, Is.EqualTo(new[] { connect.Sequence }));

            var named = CultNetRudpPacketCodec.Decode(CultNetRudpPacketCodec.Encode(accept));
            named.Ack = connect.Sequence + 3;
            named.AckMask = 1u << 2;
            client.Receive(named, 2);
            Assert.That(client.Connected, Is.True, "the mask names the Connect");
        }

        [Test]
        public void AnAcceptAfterALocalDisconnectDoesNotReconnectTheSession()
        {
            var client = Session(1);
            var server = Session(500);
            var (_, accept) = Handshake(client, server);
            client.CreateDisconnect(Encoding.UTF8.GetBytes("bye"));
            Assert.That(client.Connected, Is.False);

            client.Receive(accept, 5);
            Assert.That(client.Connected, Is.False, "a late Accept revived an ended session");
        }

        [Test]
        public void AReconnectForgetsWhatTheOldServerSent()
        {
            var client = Session(1);
            var oldServer = Session(500);
            Handshake(client, oldServer);
            foreach (var name in new[] { "d1", "d2", "d3" })
                Assert.That(Names(client.Receive(Send(oldServer, name), 1)), Is.EqualTo(new[] { name }));

            var newServer = Session(501);
            client.Receive(newServer.AcceptConnect(client.CreateConnect(2), 2), 2);
            Assert.That(Names(client.Receive(Send(newServer, "fresh"), 3)), Is.EqualTo(new[] { "fresh" }));
        }

        [Test]
        public void AnAcceptForAnAbandonedConnectDoesNotConnectTheSession()
        {
            var client = Session(1);
            var server = Session(500);
            var accept = server.AcceptConnect(client.CreateConnect(0), 0);
            client.CreateDisconnect(Encoding.UTF8.GetBytes("never mind"));

            client.Receive(accept, 1);
            Assert.That(client.Connected, Is.False, "an Accept revived an abandoned Connect");
        }

        [Test]
        public void AFullQueueOwedToAVanishedPeerDoesNotRefuseANewGeneration()
        {
            static CultNetRudpSession Bounded(uint initialSequence) => new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = ConnectionId,
                InitialSequence = initialSequence,
                MaxPendingReliablePackets = 1
            });

            var server = Bounded(500);
            server.AcceptConnect(Session(1).CreateConnect(0), 0);
            Assert.That(server.OutstandingReliablePacketCount, Is.EqualTo(1), "the Accept is owed and never acknowledged");
            var next = Session(9).CreateConnect(1);
            server.AcceptConnect(next, 1);
            Assert.That(server.ConnectRepeats(next), Is.True);

            var client = Bounded(1);
            client.CreateConnect(0);
            client.CreateConnect(1);
            Assert.That(client.OutstandingReliablePacketCount, Is.EqualTo(1), "only the new Connect is owed");
        }

        [Test]
        public void ADuplicateAcceptDoesNotReseedTheWatermark()
        {
            var client = Session(1);
            var server = Session(500);
            var (_, accept) = Handshake(client, server);
            var s1 = Send(server, "s1");
            var s2 = Send(server, "s2");
            var forged = CultNetRudpPacketCodec.Decode(CultNetRudpPacketCodec.Encode(accept));
            forged.Sequence = s2.Sequence;
            client.Receive(forged, 1);
            Assert.That(client.Receive(s2, 2).Delivered, Is.Empty, "s1 is still missing");
            Assert.That(Names(client.Receive(s1, 3)), Is.EqualTo(new[] { "s1", "s2" }));
        }

        [Test]
        public void ReliableDataBeforeTheAcceptIsNeitherDeliveredNorAcknowledged()
        {
            var client = Session(1);
            var server = Session(500);
            var accept = server.AcceptConnect(client.CreateConnect(0), 0);
            var a = Send(server, "A");
            var b = Send(server, "B");

            foreach (var early in new[] { b, a })
            {
                Assert.That(client.Receive(early, 1).Delivered, Is.Empty);
                var ack = client.CreateAckForReceived(early.Sequence);
                Assert.That((ack.Ack, ack.AckMask), Is.EqualTo((0u, 0u)), "an unhandled packet was acknowledged");
            }
            client.Receive(accept, 2);
            Assert.That(client.Connected, Is.True);
            Assert.That(server.OutstandingReliablePacketCount, Is.EqualTo(3), "nothing was acknowledged");

            Assert.That(Names(client.Receive(a, 3)), Is.EqualTo(new[] { "A" }));
            Assert.That(Names(client.Receive(b, 3)), Is.EqualTo(new[] { "B" }));
        }

        [Test]
        public void OrderedFramesAfterTheAcceptAreDeliveredInOrderWhateverArrivesFirst()
        {
            var client = Session(1);
            var server = Session(500);
            Handshake(client, server);
            var a = Send(server, "A");
            var b = Send(server, "B");
            Assert.That(client.Receive(b, 1).Delivered, Is.Empty);
            Assert.That(Names(client.Receive(a, 2)), Is.EqualTo(new[] { "A", "B" }));
        }

        [Test]
        public void AnUnorderedReliableFrameIsDeliveredOnReceiptNeverHeldBehindAGap()
        {
            var client = Session(1);
            var server = Session(500);
            Handshake(client, server);
            Send(server, "lost");
            var unordered = Send(server, "now", ordered: false, channel: "media");
            var ordered = Send(server, "later");
            Assert.That(Names(client.Receive(unordered, 1)), Is.EqualTo(new[] { "now" }));
            Assert.That(client.Receive(ordered, 2).Delivered, Is.Empty, "the ordered frame waits for the gap");
        }

        [Test]
        public void APeerDisconnectDoesNotForgetWhatThePeerSent()
        {
            var client = Session(1);
            var server = Session(500);
            Handshake(client, server);
            var s1 = Send(client, "s1");
            var s2 = Send(client, "s2");
            Assert.That(server.Receive(s2, 1).Delivered, Is.Empty, "s1 is missing, so s2 is held");

            server.Receive(client.CreateDisconnect(Encoding.UTF8.GetBytes("bye")), 2);
            Assert.That(Names(server.Receive(s1, 3)), Is.EqualTo(new[] { "s1", "s2" }), "the Disconnect forgot the held frame");
            Assert.That(server.Receive(s2, 4).Delivered, Is.Empty, "the Disconnect forgot what was received");
        }

        /// <summary>
        /// The options say nothing about the initial sequence; each session draws its own, so one options
        /// object serves many sessions without giving them one sequence.
        /// </summary>
        [Test]
        public void SessionsFromOneOptionsObjectEachDrawTheirOwnInitialSequence()
        {
            Assert.That(new CultNetRudpSessionOptions().InitialSequence, Is.Null);
            Assert.That(new CultNetRudpSocketTransportOptions().InitialSequence, Is.Null);
            Assert.That(new CultNetRudpSocketTransportServerOptions().InitialSequence, Is.Null);
            Assert.That(new RudpCultNetSchemaServerOptions().InitialSequence, Is.Null);
            Assert.That(new CultMeshRudpSocketOptions().InitialSequence, Is.Null);

            var options = new CultNetRudpSessionOptions { ConnectionId = ConnectionId };
            var firsts = Enumerable.Range(0, 64)
                .Select(_ => new CultNetRudpSession(options).CreateConnect(0).Sequence)
                .ToArray();
            Assert.That(firsts.Distinct().Count(), Is.GreaterThan(32), "a default session does not draw its own sequence");
            Assert.That(firsts.All(value => value >= 1 && value < (1u << 31)), Is.True, "a draw left [1, 2^31)");
        }

        /// <summary>
        /// A transport built from the same options each time (a reconnect loop's factory) opens each
        /// connection with its own Connect sequence.
        /// </summary>
        [Test]
        public void AReusedOptionsObjectYieldsADifferentConnectSequenceForEachTransport()
        {
            using var listener = Bind();
            using var clientSocket = Bind();
            var options = new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-client",
                Socket = clientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = listener.LocalEndPoint,
                ConnectionId = ConnectionId
            };
            var sequences = new HashSet<uint>();
            for (var index = 0; index < 8; index++)
            {
                var client = new CultNetRudpSocketTransportConnection(options);
                client.Connect("hello");
                sequences.Add(Drain(listener).Last(p => p.PacketType == CultNetRudpPacketType.Connect).Sequence);
            }
            Assert.That(sequences, Has.Count.EqualTo(8), "two connections opened with one Connect sequence");
        }

        /// <summary>
        /// A listener builds a session for every client it admits; each draws its own sequence, so no two
        /// clients are accepted with the same one.
        /// </summary>
        [Test]
        public void TheListenerAcceptsEachClientWithItsOwnSequence()
        {
            using var serverSocket = Bind();
            using var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-rudp-listener",
                Socket = serverSocket,
                ConnectionId = ConnectionId
            });
            var accepts = new HashSet<uint>();
            for (var index = 0; index < 8; index++)
            {
                using var peerSocket = Bind();
                peerSocket.SendTo(CultNetRudpPacketCodec.Encode(Session(1).CreateConnect(0)), serverSocket.LocalEndPoint!);
                server.ReceiveOnce();
                accepts.Add(Drain(peerSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept).Sequence);
            }
            Assert.That(accepts, Has.Count.EqualTo(8), "two clients were accepted with one sequence");
        }

        // Ack Cut 1d: a frame from an earlier generation is a duplicate, a delayed Connect from an
        // earlier attempt is stale, a Connect from a new endpoint is a new client.

        private static bool NamesSequence(CultNetRudpPacket ack, uint sequence) =>
            ack.Ack == sequence
            || Enumerable.Range(0, 32).Any(bit => (ack.AckMask & (1u << bit)) != 0 && ack.Ack > bit && ack.Ack - (uint)bit - 1 == sequence);

        /// <summary>
        /// A frame the client's old generation already delivered, retransmitted after the client
        /// reconnected, is acknowledged and not delivered into the new generation.
        /// </summary>
        [Test]
        public void AFrameFromTheClientsEarlierGenerationIsAcknowledgedNotDeliveredAgain()
        {
            var client = Session(10);
            var server = Session(500);
            Handshake(client, server);
            var old = Send(client, "old");
            Assert.That(Names(server.Receive(old, 1)), Is.EqualTo(new[] { "old" }));

            var connect = client.CreateConnect(2);
            client.Receive(server.AcceptConnect(connect, 2), 2);
            Assert.That(old.Sequence, Is.LessThan(connect.Sequence));
            Assert.That(server.Receive(old, 3).Delivered, Is.Empty, "an earlier generation's frame was delivered again");
            Assert.That(NamesSequence(server.CreateAckForReceived(old.Sequence), old.Sequence), Is.True, "the duplicate was not acknowledged");
            Assert.That(Names(server.Receive(Send(client, "fresh"), 4)), Is.EqualTo(new[] { "fresh" }));
        }

        [Test]
        public void AFrameFromTheServersEarlierGenerationIsAcknowledgedNotDeliveredAgain()
        {
            var client = Session(10);
            var server = Session(500);
            Handshake(client, server);
            var old = Send(server, "old");
            Assert.That(Names(client.Receive(old, 1)), Is.EqualTo(new[] { "old" }));

            client.Receive(server.AcceptConnect(client.CreateConnect(2), 2), 2);
            Assert.That(client.Receive(old, 3).Delivered, Is.Empty, "an earlier generation's frame was delivered again");
            Assert.That(NamesSequence(client.CreateAckForReceived(old.Sequence), old.Sequence), Is.True);
            Assert.That(Names(client.Receive(Send(server, "fresh"), 4)), Is.EqualTo(new[] { "fresh" }));
        }

        [Test]
        public void ADuplicateBelowTheReceiveWindowIsAcknowledgedByName()
        {
            var client = Session(10);
            var server = Session(500);
            Handshake(client, server);
            var first = Send(client, "first");
            Assert.That(Names(server.Receive(first, 1)), Is.EqualTo(new[] { "first" }));
            for (var index = 0; index < 4_200; index++)
            {
                server.Receive(Send(client, "x"), 1);
                client.Receive(server.CreateAck(), 1);
            }
            Assert.That(server.Receive(first, 2).Delivered, Is.Empty);
            Assert.That(server.CreateAckForReceived(first.Sequence).Ack, Is.EqualTo(first.Sequence), "a duplicate below the window was not acknowledged by name");
        }

        /// <summary>
        /// A Connect delayed from an earlier attempt of the client that is now connected does not
        /// restart the session: the server answers with an Ack and the client's ordered frames keep
        /// flowing.
        /// </summary>
        [Test]
        public void ADelayedConnectFromAnEarlierAttemptDoesNotStrandTheClient()
        {
            var client = Session(10);
            var server = Session(500);
            var earlier = client.CreateConnect(0);
            var retried = client.CreateConnect(300);
            var accept = server.AcceptConnect(retried, 301);
            client.Receive(accept, 302);
            server.Receive(client.CreateAckForReceived(accept.Sequence), 302);
            Assert.That(client.Connected, Is.True);
            var a = Send(client, "a");
            Assert.That(Names(server.Receive(a, 303)), Is.EqualTo(new[] { "a" }));
            client.Receive(server.CreateAckForReceived(a.Sequence), 303);

            Assert.That(server.ConnectRepeats(earlier), Is.True, "the delayed Connect starts nothing");
            var reply = server.AcceptConnect(earlier, 304);
            Assert.That(reply.PacketType, Is.EqualTo(CultNetRudpPacketType.Ack));
            client.Receive(reply, 305);
            Assert.That(client.Connected, Is.True);

            var b = Send(client, "b");
            Assert.That(Names(server.Receive(b, 306)), Is.EqualTo(new[] { "b" }), "the delayed Connect reset the server");
            client.Receive(server.CreateAckForReceived(b.Sequence), 307);
            Assert.That(client.PendingReliableSequences, Does.Not.Contain(b.Sequence));
        }

        /// <summary>
        /// A stale Connect is the peer's echo from the past, not evidence it is alive; a retransmit of
        /// the accepted Connect is.
        /// </summary>
        [Test]
        public void ARepeatedConnectRefreshesLivenessAndAStaleOneDoesNot()
        {
            var client = Session(10);
            var server = Session(500);
            var earlier = client.CreateConnect(0);
            var current = client.CreateConnect(1);
            server.AcceptConnect(current, 0);

            server.AcceptConnect(current, 900);
            Assert.That(server.CheckTimeout(1_000, 500), Is.False, "a repeated Connect did not refresh liveness");

            server.AcceptConnect(earlier, 1_400);
            Assert.That(server.CheckTimeout(1_600, 500), Is.True, "a stale Connect refreshed liveness");
        }

        /// <summary>
        /// An accepted peer that never speaks again times out: accepting a Connect is the first thing
        /// heard from it.
        /// </summary>
        [Test]
        public void AnAcceptedPeerThatGoesSilentTimesOut()
        {
            var server = Session(500);
            server.AcceptConnect(Session(10).CreateConnect(0), 0);
            Assert.That(server.CheckTimeout(100_000, 1_000), Is.True);
        }

        [Test]
        public void StaleConnectsAreRecognisedBySerialArithmetic()
        {
            var server = Session(500);
            server.AcceptConnect(Session(3).CreateConnect(0), 0);
            CultNetRudpPacket Connect(uint sequence) => Session(sequence).CreateConnect(0);
            Assert.That(server.ConnectRepeats(Connect(uint.MaxValue - 1)), Is.True, "just before the wrap");
            Assert.That(server.ConnectRepeats(Connect(4)), Is.False, "just after");
            Assert.That(server.ConnectRepeats(Connect(3 + 4_096)), Is.False, "far after");
            Assert.That(server.ConnectRepeats(Connect(unchecked(3u - 4_095))), Is.True, "the window's edge");
            Assert.That(server.ConnectRepeats(Connect(unchecked(3u - 4_096))), Is.False, "past the window");
        }

        /// <summary>
        /// A restarted client whose random first sequence lands just before the server's current
        /// Connect is answered with an Ack, never an Accept. It does not retransmit that Connect for
        /// ever: once the attempt times out it starts a fresh one with a newly drawn sequence, and is
        /// admitted.
        /// </summary>
        [Test]
        public void ARestartedClientWhoseFirstSequenceIsStaleConnectsAfterRedrawing()
        {
            var server = Session(500);
            var old = Session(1_000);
            var oldAccept = server.AcceptConnect(old.CreateConnect(0), 0);
            old.Receive(oldAccept, 0);

            var restarted = Session(900);
            var first = restarted.CreateConnect(0, Encoding.UTF8.GetBytes("join"));
            // The old client has not acknowledged its Accept yet, so that is the reply: it names the
            // old Connect, not this one.
            var early = server.AcceptConnect(first, 1);
            Assert.That(early.PacketType, Is.EqualTo(CultNetRudpPacketType.Accept));
            restarted.Receive(early, 1);
            Assert.That(restarted.Connected, Is.False, "an Accept for another Connect connected the client");
            server.Receive(old.CreateAckForReceived(oldAccept.Sequence), 1);
            var reply = server.AcceptConnect(first, 2);
            Assert.That(reply.PacketType, Is.EqualTo(CultNetRudpPacketType.Ack));
            restarted.Receive(reply, 2);
            Assert.That(restarted.Connected, Is.False);

            var retransmitted = restarted.DueResends(1_000);
            Assert.That(retransmitted, Has.Count.EqualTo(1));
            Assert.That(retransmitted[0].Sequence, Is.EqualTo(first.Sequence), "the attempt is still young");

            var fresh = restarted.DueResends(3_500);
            Assert.That(fresh, Has.Count.EqualTo(1));
            Assert.That(fresh[0].PacketType, Is.EqualTo(CultNetRudpPacketType.Connect));
            Assert.That(fresh[0].Sequence, Is.Not.EqualTo(first.Sequence), "the same Connect was retransmitted for ever");
            Assert.That(Encoding.UTF8.GetString(fresh[0].Payload), Is.EqualTo("join"), "the fresh attempt lost the Connect payload");

            var accept = server.AcceptConnect(fresh[0], 3_500);
            Assert.That(accept.PacketType, Is.EqualTo(CultNetRudpPacketType.Accept));
            restarted.Receive(accept, 3_500);
            Assert.That(restarted.Connected, Is.True);
            Assert.That(Names(server.Receive(Send(restarted, "hello"), 3_501)), Is.EqualTo(new[] { "hello" }));
        }

        [Test]
        public void AnAnsweredConnectIsNeverReplacedByTheAttemptTimeout()
        {
            var client = Session(10);
            var server = Session(500);
            var (connect, _) = Handshake(client, server);
            var resent = client.DueResends(60_000);
            Assert.That(resent.All(p => p.PacketType != CultNetRudpPacketType.Connect || p.Sequence == connect.Sequence), Is.True, "a connected client started a new attempt");
            Assert.That(client.Connected, Is.True);
        }

        /// <summary>
        /// A pinned client that restarts on a new port against a server-mode transport is a new client:
        /// the Connect starts a new generation and the client is admitted, though its sequence is the
        /// one the old client's Connect carried.
        /// </summary>
        [Test]
        public void ServerModeAdmitsAPinnedClientThatRestartsOnANewPort()
        {
            using var serverSocket = Bind();
            using var firstSocket = Bind();
            using var secondSocket = Bind();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = ConnectionId
            });
            var serverEndPoint = serverSocket.LocalEndPoint!;
            void ToServer(Socket from, CultNetRudpPacket packet) => from.SendTo(CultNetRudpPacketCodec.Encode(packet), serverEndPoint);

            var first = Session(1);
            ToServer(firstSocket, first.CreateConnect(0, Encoding.UTF8.GetBytes("join")));
            server.ReceiveOnce();
            first.Receive(Drain(firstSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            ToServer(firstSocket, Send(first, "hello"));
            Assert.That(Encoding.UTF8.GetString(server.ReceiveOnce()!.Payload), Is.EqualTo("hello"));

            var second = Session(1);
            ToServer(secondSocket, second.CreateConnect(0, Encoding.UTF8.GetBytes("join")));
            server.ReceiveOnce();
            var accepts = Drain(secondSocket).Where(p => p.PacketType == CultNetRudpPacketType.Accept).ToArray();
            Assert.That(accepts, Is.Not.Empty, "the restarted client on a new port was not admitted");
            second.Receive(accepts[0], 0);
            ToServer(secondSocket, Send(second, "after"));
            Assert.That(Encoding.UTF8.GetString(server.ReceiveOnce()!.Payload), Is.EqualTo("after"));
        }

        // Transports.

        [Test]
        public void ServerModeAdmitsARestartedClientKeepsItsSessionOnARetransmitAndAdmitsANewEndpoint()
        {
            using var serverSocket = Bind();
            using var peerSocket = Bind();
            using var otherSocket = Bind();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = ConnectionId
            });
            var serverEndPoint = serverSocket.LocalEndPoint!;
            void ToServer(Socket from, CultNetRudpPacket packet) => from.SendTo(CultNetRudpPacketCodec.Encode(packet), serverEndPoint);

            var first = Session(50_000);
            var connect = first.CreateConnect(0, Encoding.UTF8.GetBytes("same"));
            ToServer(peerSocket, connect);
            server.ReceiveOnce();
            first.Receive(Drain(peerSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            var sent = new[] { "c0", "c1", "c2" }.Select(name => Send(first, name)).ToArray();
            foreach (var packet in sent)
            {
                ToServer(peerSocket, packet);
                Assert.That(Encoding.UTF8.GetString(server.ReceiveOnce()!.Payload), Is.EqualTo(Encoding.UTF8.GetString(packet.Payload)));
            }

            // A retransmitted Connect is a repeat: what was delivered stays delivered once.
            ToServer(peerSocket, connect);
            server.ReceiveOnce();
            ToServer(peerSocket, sent[0]);
            Assert.That(server.ReceiveOnce(), Is.Null, "the retransmitted Connect reset the session");

            // The same client restarted on the same address, connection id and payload.
            Drain(peerSocket);
            var second = Session(7);
            ToServer(peerSocket, second.CreateConnect(0, Encoding.UTF8.GetBytes("same")));
            server.ReceiveOnce();
            second.Receive(Drain(peerSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            ToServer(peerSocket, Send(second, "after restart"));
            Assert.That(Encoding.UTF8.GetString(server.ReceiveOnce()!.Payload), Is.EqualTo("after restart"));

            // A Connect from a new endpoint claims the transport.
            var third = Session(9);
            ToServer(otherSocket, third.CreateConnect(0, Encoding.UTF8.GetBytes("elsewhere")));
            server.ReceiveOnce();
            third.Receive(Drain(otherSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            ToServer(otherSocket, Send(third, "from elsewhere"));
            Assert.That(Encoding.UTF8.GetString(server.ReceiveOnce()!.Payload), Is.EqualTo("from elsewhere"));
        }

        [Test]
        public void TheListenerAdmitsARestartedClientWithTheSameAddressIdAndPayload()
        {
            using var serverSocket = Bind();
            using var peerSocket = Bind();
            using var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-rudp-listener",
                Socket = serverSocket,
                ConnectionId = ConnectionId
            });
            var ended = new List<CultNetRudpSocketServerPeer>();
            server.PeerDisconnected += ended.Add;
            var serverEndPoint = serverSocket.LocalEndPoint!;
            void ToServer(CultNetRudpPacket packet) => peerSocket.SendTo(CultNetRudpPacketCodec.Encode(packet), serverEndPoint);

            var first = Session(50_000);
            ToServer(first.CreateConnect(0, Encoding.UTF8.GetBytes("same")));
            server.ReceiveOnce();
            first.Receive(Drain(peerSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            for (var index = 0; index < 3; index++)
            {
                ToServer(Send(first, $"c{index}"));
                Assert.That(server.ReceiveOnce(), Is.Not.Null);
            }
            Assert.That(ended, Is.Empty);

            var second = Session(7);
            ToServer(second.CreateConnect(0, Encoding.UTF8.GetBytes("same")));
            server.ReceiveOnce();
            Assert.That(ended, Has.Count.EqualTo(1), "the restarted client was taken for a retransmit");
            second.Receive(Drain(peerSocket).First(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            ToServer(Send(second, "after restart"));
            var frame = server.ReceiveOnce();
            Assert.That(frame, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(frame!.Frame.Payload), Is.EqualTo("after restart"));
        }

        /// <summary>
        /// Windows reports the ICMP port-unreachable for a datagram sent to a closed port as a connection
        /// reset on the next receive. Nothing ended, so nothing may be reported. Other platforms do not
        /// raise it, so the test is inconclusive there.
        /// </summary>
        [Test]
        public void AnIcmpConnectionResetDoesNotEndTheSession()
        {
            Assume.That(OperatingSystem.IsWindows(), "only Windows raises an ICMP connection reset on UDP");
            using var serverSocket = Bind();
            using var peerSocket = Bind();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = ConnectionId
            });
            peerSocket.SendTo(CultNetRudpPacketCodec.Encode(Session(1).CreateConnect(0)), serverSocket.LocalEndPoint!);
            server.ReceiveOnce();
            Assert.That(server.Connected, Is.True);

            var closed = Bind();
            var closedEndPoint = closed.LocalEndPoint!;
            closed.Dispose();
            serverSocket.SendTo(new byte[] { 1 }, closedEndPoint);
            System.Threading.Thread.Sleep(50);
            server.ReceiveOnce();
            Assert.That(server.DisconnectReason, Is.Null, "an ICMP reset claimed an ending that did not happen");
            Assert.That(server.Connected, Is.True);
        }
    }
}
