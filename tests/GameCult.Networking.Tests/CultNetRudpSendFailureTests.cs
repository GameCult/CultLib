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

        private static (CultNetRudpSocketTransportServer Server, EndPoint ServerEndPoint, Peer X, Peer Y) TwoPeers(int? maxFragmentBytes = null)
        {
            var serverSocket = Bind();
            var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-send-failure",
                Socket = serverSocket,
                ConnectionId = ConnectionId,
                ResendDelayMs = 5,
                MaxFragmentBytes = maxFragmentBytes
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

        // A permanent send failure is one that can never succeed for the datagram as built. A
        // caller-directed send throws it and queues nothing; inside a poll it ends that peer's
        // session and never the poll.

        private static bool IsUnsendableReason(byte[]? reason)
        {
            var text = Encoding.UTF8.GetString(reason ?? Array.Empty<byte>());
            return text.StartsWith("packet could not be sent: ", StringComparison.Ordinal)
                && text.Length > "packet could not be sent: ".Length;
        }

        private static List<CultNetRudpSocketServerPeer> RecordDisconnects(CultNetRudpSocketTransportServer server)
        {
            var ended = new List<CultNetRudpSocketServerPeer>();
            server.PeerDisconnected += ended.Add;
            return ended;
        }

        [Test]
        public void AnOversizedSendThrowsTheErrorAndQueuesNothing()
        {
            var (server, _, x, y) = TwoPeers();
            using (server)
            {
                var error = Assert.Throws<SocketException>(() => server.SendSchema(x.Server, new byte[70_000]))!;
                Assert.That(CultNetRudpSession.IsPermanentSendError(error), Is.True, error.SocketErrorCode.ToString());
                Assert.That(server.Stats.SendFailures, Is.Zero);
                Assert.That(server.Stats.FramesSent, Is.Zero);
                Assert.That(server.Peers, Has.Count.EqualTo(2), "the caller's error ends no session");
                Assert.That(x.Server.Session.OutstandingReliablePacketCount, Is.Zero, "nothing is pending or queued to resend");

                // The refused send consumed no sequence: the next frame is delivered in order instead
                // of waiting behind a gap that never fills.
                server.SendSchema(x.Server, "next");
                var data = Drain(x.Socket).Single(p => p.PacketType == CultNetRudpPacketType.Data);
                var delivered = x.Session.Receive(data, 1).Delivered;
                Assert.That(delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)), Is.EqualTo(new[] { "next" }));
                Assert.That(y.Server.Session.OutstandingReliablePacketCount, Is.Zero);
            }
        }

        [Test]
        public void ASendThatFailsPermanentlyAfterAFragmentLeftEndsTheSession()
        {
            var (server, _, x, _) = TwoPeers(maxFragmentBytes: 1000);
            using (server)
            {
                var ended = RecordDisconnects(server);
                server.UnsendableAfter[x.EndPoint] = 1;

                var error = Assert.Throws<SocketException>(() => server.SendSchema(x.Server, new byte[3000]))!;
                Assert.That(CultNetRudpSession.IsPermanentSendError(error), Is.True);
                Assert.That(server.Peers, Has.Count.EqualTo(1));
                Assert.That(ended, Is.EqualTo(new[] { x.Server }));
                Assert.That(IsUnsendableReason(x.Server.DisconnectReason), Is.True);
            }
        }

        [Test]
        public void APermanentFailureInResendsEndsOnlyThatPeersSession()
        {
            var (server, _, x, y) = TwoPeers();
            using (server)
            {
                var ended = RecordDisconnects(server);
                server.FailingSendPeers.Add(x.EndPoint);
                server.FailingSendPeers.Add(y.EndPoint);
                server.SendSchema(x.Server, "for-x");
                server.SendSchema(y.Server, "for-y");

                // X's datagram can now never be sent as built; Y's path recovers. X is visited first.
                server.FailingSendPeers.Clear();
                server.UnsendableAfter[x.EndPoint] = 0;
                Thread.Sleep(30);
                Assert.DoesNotThrow(() => server.PollResends());

                Assert.That(server.Peers, Is.EqualTo(new[] { y.Server }));
                Assert.That(ended, Is.EqualTo(new[] { x.Server }));
                Assert.That(IsUnsendableReason(x.Server.DisconnectReason), Is.True);
                var resent = Drain(y.Socket).Where(p => p.PacketType == CultNetRudpPacketType.Data).ToList();
                Assert.That(resent, Has.Count.EqualTo(1));
                Assert.That(Encoding.UTF8.GetString(resent[0].Payload!), Is.EqualTo("for-y"));
                var goodbye = Drain(x.Socket).Single(p => p.PacketType == CultNetRudpPacketType.Disconnect);
                Assert.That(IsUnsendableReason(goodbye.Payload), Is.True);
            }
        }

        [Test]
        public void APermanentFailureAcknowledgingAFrameEndsTheSessionAfterTheFrameIsDelivered()
        {
            var (server, listenerEndPoint, x, _) = TwoPeers();
            using (server)
            {
                var ended = RecordDisconnects(server);
                server.UnsendableAfter[x.EndPoint] = 0;
                x.Socket.SendTo(CultNetRudpPacketCodec.Encode(ReliableFrame(x, "from-x")), listenerEndPoint);

                CultNetRudpSocketServerFrame? frame = null;
                Assert.DoesNotThrow(() => frame = server.ReceiveOnce());

                Assert.That(frame, Is.Not.Null, "the frame is delivered before the session ends");
                Assert.That(Encoding.UTF8.GetString(frame!.Frame.Payload), Is.EqualTo("from-x"));
                Assert.That(ended, Is.EqualTo(new[] { x.Server }));
                Assert.That(IsUnsendableReason(x.Server.DisconnectReason), Is.True);
                Assert.That(server.Peers, Has.Count.EqualTo(1));
                Assert.That(server.Stats.SendFailures, Is.Zero);
            }
        }

        [Test]
        public void APermanentFailureAnsweringARepeatedConnectEndsTheSession()
        {
            var serverSocket = Bind();
            using var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-send-failure",
                Socket = serverSocket,
                ConnectionId = ConnectionId,
                ResendDelayMs = 5
            });
            var ended = RecordDisconnects(server);
            var peer = Bind();
            var connect = CultNetRudpPacketCodec.Encode(
                new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId }).CreateConnect(0));
            peer.SendTo(connect, serverSocket.LocalEndPoint!);
            server.ReceiveOnce();
            Assert.That(server.Peers, Has.Count.EqualTo(1));
            var admitted = server.Peers.Single();

            server.UnsendableAfter[peer.LocalEndPoint!] = 0;
            peer.SendTo(connect, serverSocket.LocalEndPoint!);
            Assert.DoesNotThrow(() => server.ReceiveOnce());

            Assert.That(server.Peers, Is.Empty);
            Assert.That(ended, Is.EqualTo(new[] { admitted }));
            Assert.That(IsUnsendableReason(admitted.DisconnectReason), Is.True);
        }

        [Test]
        public void AConnectWhoseAcceptCanNeverBeSentStartsNoSession()
        {
            var serverSocket = Bind();
            using var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-send-failure",
                Socket = serverSocket,
                ConnectionId = ConnectionId,
                ResendDelayMs = 5
            });
            var peer = Bind();
            server.UnsendableAfter[peer.LocalEndPoint!] = 0;
            peer.SendTo(
                CultNetRudpPacketCodec.Encode(new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId }).CreateConnect(0)),
                serverSocket.LocalEndPoint!);

            Assert.DoesNotThrow(() => server.ReceiveOnce());

            Assert.That(server.Peers, Is.Empty);
            Assert.That(server.Stats.PacketsDropped, Is.EqualTo(1));
        }

        [Test]
        public void AHealthySendCountsItsBytesAndNoFailure()
        {
            var (server, _, x, _) = TwoPeers();
            using (server)
            {
                var before = server.Stats.BytesSent;
                server.SendSchema(x.Server, "hello");
                var received = Drain(x.Socket).Sum(p => CultNetRudpPacketCodec.Encode(p).Length);
                Assert.That(received, Is.GreaterThan("hello".Length));
                Assert.That(server.Stats.BytesSent - before, Is.EqualTo(received));
                Assert.That(server.Stats.SendFailures, Is.Zero);
            }
        }

        [Test]
        public void OnlyADatagramThatCanNeverBeSentIsAPermanentFailure()
        {
            foreach (var permanent in new[] { SocketError.MessageSize, SocketError.InvalidArgument, SocketError.AddressFamilyNotSupported })
                Assert.That(CultNetRudpSession.IsPermanentSendError(new SocketException((int)permanent)), Is.True, permanent.ToString());
            foreach (var transient in new[]
            {
                SocketError.HostUnreachable, SocketError.NetworkUnreachable, SocketError.NoBufferSpaceAvailable,
                SocketError.WouldBlock, SocketError.ConnectionReset, SocketError.ConnectionRefused,
                SocketError.AccessDenied, SocketError.NetworkDown, SocketError.AddressNotAvailable,
                SocketError.Interrupted, SocketError.TimedOut, SocketError.Shutdown
            })
                Assert.That(CultNetRudpSession.IsPermanentSendError(new SocketException((int)transient)), Is.False, transient.ToString());
        }
    }
}
