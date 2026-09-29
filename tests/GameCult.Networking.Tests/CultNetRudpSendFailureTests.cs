#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// One peer whose datagrams cannot be sent must not stop the listener serving the others. The
    /// failure is injected at the listener's one send function through <c>FailingSendPeers</c>.
    /// </summary>
    public class CultNetRudpSendFailureTests
    {
        private const uint ConnectionId = 0x10203099;

        private sealed class Peer
        {
            public Socket Socket = null!;
            public CultNetRudpSession Session = null!;
            public CultNetRudpSocketServerPeer Server = null!;
            public EndPoint EndPoint => Socket.LocalEndPoint!;
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

        private static Peer Connect(CultNetRudpSocketTransportServer server, EndPoint serverEndPoint)
        {
            var peer = new Peer
            {
                Socket = Bind(),
                Session = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId })
            };
            peer.Socket.SendTo(CultNetRudpPacketCodec.Encode(peer.Session.CreateConnect(0)), serverEndPoint);
            server.ReceiveOnce();
            peer.Session.Receive(Drain(peer.Socket).Single(p => p.PacketType == CultNetRudpPacketType.Accept), 0);
            peer.Server = server.Peers.Single(p => p.RemoteEndPoint.Equals(peer.EndPoint));
            return peer;
        }

        private static (CultNetRudpSocketTransportServer Server, EndPoint ServerEndPoint, Peer X, Peer Y) TwoPeers()
        {
            var serverSocket = Bind();
            var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-send-failure",
                Socket = serverSocket,
                ConnectionId = ConnectionId,
                ResendDelayMs = 5
            });
            var x = Connect(server, serverSocket.LocalEndPoint!);
            var y = Connect(server, serverSocket.LocalEndPoint!);
            return (server, serverSocket.LocalEndPoint!, x, y);
        }

        private static CultNetRudpPacket ReliableFrame(Peer peer, string text) =>
            peer.Session.Send("schema", Encoding.UTF8.GetBytes(text), new CultNetRudpSendOptions { Reliable = true, Ordered = true });

        [Test]
        public void AFailedResendToOnePeerDoesNotStopTheOthersResends()
        {
            var (server, _, x, y) = TwoPeers();
            using (server)
            {
                // Both peers' first datagram is lost, so both have a reliable resend due.
                server.FailingSendPeers.Add(x.EndPoint);
                server.FailingSendPeers.Add(y.EndPoint);
                server.SendSchema(x.Server, "for-x");
                server.SendSchema(y.Server, "for-y");
                Assert.That(server.Stats.SendFailures, Is.EqualTo(2));

                // Y's path recovers; X's stays dead. X is visited first.
                server.FailingSendPeers.Remove(y.EndPoint);
                Thread.Sleep(30);
                Assert.DoesNotThrow(() => server.PollResends());

                var resent = Drain(y.Socket).Where(p => p.PacketType == CultNetRudpPacketType.Data).ToList();
                Assert.That(resent, Has.Count.EqualTo(1));
                Assert.That(Encoding.UTF8.GetString(resent[0].Payload!), Is.EqualTo("for-y"));
                Assert.That(server.Stats.SendFailures, Is.GreaterThanOrEqualTo(3));
                Assert.That(server.Peers, Has.Count.EqualTo(2), "a send failure ends no session");
            }
        }

        [Test]
        public void AFailedAckToOnePeerDoesNotStopTheListenerServingTheOthers()
        {
            var (server, listenerEndPoint, x, y) = TwoPeers();
            using (server)
            {
                server.FailingSendPeers.Add(x.EndPoint);
                x.Socket.SendTo(CultNetRudpPacketCodec.Encode(ReliableFrame(x, "from-x")), listenerEndPoint);
                y.Socket.SendTo(CultNetRudpPacketCodec.Encode(ReliableFrame(y, "from-y")), listenerEndPoint);

                var frames = new List<string>();
                for (var attempt = 0; attempt < 20 && frames.Count < 2; attempt++)
                {
                    Assert.DoesNotThrow(() =>
                    {
                        if (server.ReceiveOnce() is { } frame)
                            frames.Add(Encoding.UTF8.GetString(frame.Frame.Payload));
                    });
                }

                Assert.That(frames, Is.EquivalentTo(new[] { "from-x", "from-y" }));
                Assert.That(server.Stats.SendFailures, Is.GreaterThanOrEqualTo(1), "X's ack was counted as lost");
                Assert.That(Drain(y.Socket).Select(p => p.PacketType), Does.Contain(CultNetRudpPacketType.Ack));
                Assert.That(server.Peers, Has.Count.EqualTo(2));
            }
        }
    }
}
