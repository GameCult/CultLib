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

        [Test]
        public void DefaultInitialSequencesAreDrawnAtRandomFromOneToTwoToTheThirtyFirst()
        {
            static void AssertRandom(string label, Func<uint> draw)
            {
                var draws = Enumerable.Range(0, 64).Select(_ => draw()).ToArray();
                Assert.That(draws.Distinct().Count(), Is.GreaterThan(32), $"{label} does not draw at random");
                Assert.That(draws.All(value => value >= 1 && value < (1u << 31)), Is.True, $"{label} left [1, 2^31)");
            }

            AssertRandom("RandomInitialSequence", CultNetRudpSession.RandomInitialSequence);
            AssertRandom("session options", () => new CultNetRudpSessionOptions().InitialSequence);
            AssertRandom("socket transport options", () => new CultNetRudpSocketTransportOptions().InitialSequence);
            AssertRandom("listener options", () => new CultNetRudpSocketTransportServerOptions().InitialSequence);
            AssertRandom("schema server options", () => new RudpCultNetSchemaServerOptions().InitialSequence);
            AssertRandom("cultmesh options", () => new CultMeshRudpSocketOptions().InitialSequence);
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

            var first = Session(50);
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

            var first = Session(50);
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
