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
            Assert.That(client.OutstandingReliablePacketCount, Is.EqualTo(1));
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

        private static (CultNetRudpSocketTransportConnection Client, Socket Peer, CultNetRudpSession PeerSession) ConnectedClient()
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
            return (client, peerSocket, peer);
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
    }
}
