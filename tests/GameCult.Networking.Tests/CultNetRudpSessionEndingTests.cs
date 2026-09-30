#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// A write belongs to the session generation it was issued in. Every way a generation ends drops
    /// the writes it still owed, and a flush belongs to the generation it started in.
    /// </summary>
    public sealed class CultNetRudpSessionEndingTests
    {
        private const uint ConnectionId = 0x10203070;

        private static Socket Bind()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.ReceiveTimeout = 20;
            return socket;
        }

        private static (CultNetRudpSession Client, CultNetRudpSession Server) ClientWithALostWrite()
        {
            var client = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 1 });
            var server = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 500 });
            client.Receive(server.AcceptConnect(client.CreateConnect(0), 0), 0);
            client.Send("schema", Encoding.UTF8.GetBytes("owed to the old session"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            // A fragmented write larger than the send window leaves part of it queued.
            client.SendMany("schema", new byte[40 * 8], new CultNetRudpSendOptions { Reliable = true, Ordered = true }, maxFragmentBytes: 8);
            Assert.That(client.OutstandingReliablePacketCount, Is.EqualTo(41));
            Assert.That(client.QueuedReliablePacketCount, Is.GreaterThan(0));
            return (client, server);
        }

        [TestCase("peer Disconnect")]
        [TestCase("local disconnect")]
        [TestCase("timeout")]
        [TestCase("reset")]
        public void EveryWayASessionEndsDropsTheWritesItOwed(string ending)
        {
            var (client, server) = ClientWithALostWrite();
            switch (ending)
            {
                case "peer Disconnect": client.Receive(server.CreateDisconnect(Encoding.UTF8.GetBytes("bye")), 1); break;
                case "local disconnect": client.CreateDisconnect(Encoding.UTF8.GetBytes("bye")); break;
                case "timeout": Assert.That(client.CheckTimeout(1_000, 10), Is.True); break;
                case "reset": client.ResetPeerState(); break;
                default: throw new ArgumentException(ending);
            }
            Assert.That(client.OutstandingReliablePacketCount, Is.EqualTo(0), "the write survived the end");

            var nextServer = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 900 });
            nextServer.AcceptConnect(client.CreateConnect(2_000), 2_000);
            foreach (var resend in client.DueResends(60_000))
            {
                Assert.That(
                    nextServer.Receive(resend, 60_000).Delivered,
                    Is.Empty,
                    "the old session's write was delivered in the next session");
            }
        }

        private static (CultNetRudpSocketTransportConnection Client, Socket Peer, EndPoint ClientEndPoint) ConnectedClient()
        {
            var peerSocket = Bind();
            var client = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-client",
                Socket = Bind(),
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = peerSocket.LocalEndPoint!,
                ConnectionId = ConnectionId
            });
            client.Connect("hello");
            var buffer = new byte[65535];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            var received = peerSocket.ReceiveFrom(buffer, ref from);
            var peer = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 500 });
            var accept = peer.AcceptConnect(CultNetRudpPacketCodec.Decode(buffer.AsSpan(0, received).ToArray()), 0);
            peerSocket.SendTo(CultNetRudpPacketCodec.Encode(accept), from);
            client.ReceiveOnce();
            Assert.That(client.Connected, Is.True);
            return (client, peerSocket, from);
        }

        [Test]
        public void AFlushFailsAfterALocalDisconnectOrATimeout()
        {
            var (client, peerSocket, _) = ConnectedClient();
            using (client)
            using (peerSocket)
            {
                client.Send("schema", Encoding.UTF8.GetBytes("owed"));
                client.Disconnect();
                Assert.Throws<InvalidOperationException>(() => client.FlushReliable(TimeSpan.FromMilliseconds(200)));
            }

            var (timedOut, secondPeer, _) = ConnectedClient();
            using (timedOut)
            using (secondPeer)
            {
                timedOut.Send("schema", Encoding.UTF8.GetBytes("owed"));
                Thread.Sleep(5);
                Assert.That(timedOut.CheckTimeout(1), Is.True);
                Assert.Throws<InvalidOperationException>(() => timedOut.FlushReliable(TimeSpan.FromMilliseconds(200)));
            }
        }

        [Test]
        public void AFlushThatStartedBeforeTheEndFailsEvenAfterALaterConnect()
        {
            var (client, peerSocket, _) = ConnectedClient();
            using (client)
            using (peerSocket)
            {
                client.Send("schema", Encoding.UTF8.GetBytes("owed"));
                var flush = Task.Run(() => client.FlushReliable(TimeSpan.FromSeconds(5)));
                Thread.Sleep(50);
                client.Disconnect();
                client.Connect("hello again");

                var error = Assert.Throws<AggregateException>(() => flush.Wait(TimeSpan.FromSeconds(10)))!;
                Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>());
            }
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
        public void AFlushStartedAfterAReconnectWaitsOnTheNewSession()
        {
            var (client, peerSocket, clientEndPoint) = ConnectedClient();
            using (client)
            using (peerSocket)
            {
                client.Send("schema", Encoding.UTF8.GetBytes("owed to the old session"));
                client.Disconnect();
                client.Connect("hello again");
                var connect = Drain(peerSocket).Last(packet => packet.PacketType == CultNetRudpPacketType.Connect);
                var accept = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 700 }).AcceptConnect(connect, 0);
                peerSocket.SendTo(CultNetRudpPacketCodec.Encode(accept), clientEndPoint);
                client.ReceiveOnce();

                Assert.DoesNotThrow(() => client.FlushReliable(TimeSpan.FromMilliseconds(500)));
            }
        }

        [Test]
        public void AFlushStartedAfterTheServerAcceptsANewConnectWaitsOnTheNewSession()
        {
            using var serverSocket = Bind();
            using var peerSocket = Bind();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = ConnectionId
            });
            var first = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId });
            peerSocket.SendTo(CultNetRudpPacketCodec.Encode(first.CreateConnect(0)), serverSocket.LocalEndPoint!);
            server.ReceiveOnce();
            peerSocket.SendTo(CultNetRudpPacketCodec.Encode(first.CreateDisconnect()), serverSocket.LocalEndPoint!);
            server.ReceiveOnce();

            var next = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId });
            peerSocket.SendTo(CultNetRudpPacketCodec.Encode(next.CreateConnect(0)), serverSocket.LocalEndPoint!);
            server.ReceiveOnce();
            var accept = Drain(peerSocket).Last(packet => packet.PacketType == CultNetRudpPacketType.Accept);
            next.Receive(accept, 0);
            peerSocket.SendTo(CultNetRudpPacketCodec.Encode(next.CreateAck()), serverSocket.LocalEndPoint!);
            server.ReceiveOnce();

            Assert.DoesNotThrow(() => server.FlushReliable(TimeSpan.FromMilliseconds(500)));
        }

        [Test]
        public void AnEndingDoesNotForgetWhatWasReceivedFromThePeer()
        {
            var client = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 1 });
            var server = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 500 });
            client.Receive(server.AcceptConnect(client.CreateConnect(0), 0), 0);
            var ordered = new CultNetRudpSendOptions { Reliable = true, Ordered = true };
            var s1 = client.Send("schema", Encoding.UTF8.GetBytes("s1"), ordered);
            var s2 = client.Send("schema", Encoding.UTF8.GetBytes("s2"), ordered);
            Assert.That(server.Receive(s2, 1).Delivered, Is.Empty, "s1 is missing, so s2 is held");

            Assert.That(server.CheckTimeout(1_000, 10), Is.True);
            Assert.That(
                server.Receive(s1, 1_001).Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray(),
                Is.EqualTo(new[] { "s1", "s2" }),
                "the ending forgot the held frame");
            Assert.That(server.Receive(s2, 1_002).Delivered, Is.Empty, "the ending forgot what was received");
        }
    }
}
