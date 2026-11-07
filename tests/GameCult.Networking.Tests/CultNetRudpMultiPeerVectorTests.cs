#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// The multi-peer listener's golden in tests/vectors/rudp-multipeer-server: two peers on one socket, the
    /// datagrams each sends and the datagrams the C# reference answers with. The C# reference is the only writer
    /// (CULTNET_WRITE_VECTORS=1); every other run replays the scenario and compares it byte for byte. Other runtimes'
    /// listeners replay the same transcript and are held to the same answers.
    /// </summary>
    public class CultNetRudpMultiPeerVectorTests
    {
        private const string WriteVariable = "CULTNET_WRITE_VECTORS";
        private const uint ConnectionId = 0x43554c54;

        private sealed class Client
        {
            public string Label = "";
            public Socket Socket = null!;
            public CultNetRudpSession Session = null!;
        }

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static Socket Bind()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return socket;
        }

        [Test]
        public void TheReferenceListenerAnswersTwoPeersWithThePinnedBytes()
        {
            var transcript = Run();
            var path = Path.Combine(VectorDirectory(), "transcript.txt");
            if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
            {
                File.WriteAllText(path, transcript);
                Assert.Pass("wrote " + path);
            }
            Assert.That(transcript, Is.EqualTo(File.ReadAllText(path)));
        }

        private static string VectorDirectory()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "vectors")))
                directory = directory.Parent;
            if (directory == null) throw new InvalidOperationException("tests/vectors not found above the test directory.");
            return Path.Combine(directory.FullName, "tests", "vectors", "rudp-multipeer-server");
        }

        private static string Run()
        {
            var log = new StringBuilder();
            log.Append("# config connection_id=0x43554c54 initial_sequence=500 max_fragment_bytes=8 accept_payload=616363\n");
            log.Append("# in <peer> <datagram> | send <peer> <channel> <payload> | out <peer> <datagram>; peer labels name remote endpoints\n");
            var serverSocket = Bind();
            using var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-vector",
                Socket = serverSocket,
                ConnectionId = ConnectionId,
                InitialSequence = 500,
                MaxFragmentBytes = 8,
                AcceptPayload = Encoding.ASCII.GetBytes("acc")
            });
            var target = serverSocket.LocalEndPoint!;
            Client Make(string label, uint sequence) => new Client
            {
                Label = label,
                Socket = Bind(),
                Session = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = sequence })
            };
            var x = Make("X", 10);
            var y = Make("Y", 20);
            var clients = new[] { x, y };

            void Deliver(Client client, CultNetRudpPacket packet)
            {
                var wire = CultNetRudpPacketCodec.Encode(packet);
                log.Append($"in {client.Label} {Hex(wire)}\n");
                client.Socket.SendTo(wire, target);
                while (!server.TryReceiveOnce(out _))
                    System.Threading.Thread.Sleep(1);
                Flush();
            }

            void Flush()
            {
                foreach (var client in clients)
                {
                    var buffer = new byte[65535];
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    while (client.Socket.Poll(30_000, SelectMode.SelectRead))
                    {
                        var received = client.Socket.ReceiveFrom(buffer, ref from);
                        var wire = buffer.AsSpan(0, received).ToArray();
                        log.Append($"out {client.Label} {Hex(wire)}\n");
                        // Clients that stay connected take the server's packets so their acks stay true.
                        var packet = CultNetRudpPacketCodec.Decode(wire);
                        if (client.Session.Connected || packet.PacketType == CultNetRudpPacketType.Accept)
                            client.Session.Receive(packet, 0);
                    }
                }
            }

            CultNetRudpSocketServerPeer PeerOf(Client client) =>
                server.Peers.Single(p => p.RemoteEndPoint.Equals(client.Socket.LocalEndPoint));

            void Send(Client client, string channel, byte[] payload)
            {
                log.Append($"send {client.Label} {channel} {Hex(payload)}\n");
                server.Send(PeerOf(client), channel, payload);
                Flush();
            }

            CultNetRudpPacket Data(Client client, string text) =>
                client.Session.Send("schema", Encoding.ASCII.GetBytes(text), new CultNetRudpSendOptions { Reliable = true, Ordered = true });

            var xConnect = x.Session.CreateConnect(0, Encoding.ASCII.GetBytes("hx"));
            Deliver(x, xConnect);
            Deliver(y, y.Session.CreateConnect(0, Encoding.ASCII.GetBytes("hy")));
            // A repeat of X's accepted Connect is answered with the Accept owed, not a new session.
            Deliver(x, xConnect);
            Deliver(x, Data(x, "ping-x"));
            Deliver(y, Data(y, "ping-y"));
            Send(x, "schema", Encoding.ASCII.GetBytes("pong"));
            // Larger than the fragment size: three fragments, one per datagram.
            Send(y, "schema", Encoding.ASCII.GetBytes("0123456789abcdefghijklmn"));
            Send(x, "latest", Encoding.ASCII.GetBytes("sample"));
            // X leaves; Y is undisturbed, and what X sends afterwards belongs to no session.
            var late = Data(x, "after-bye");
            Deliver(x, x.Session.CreateDisconnect(Encoding.ASCII.GetBytes("bye")));
            Deliver(x, late);
            Send(y, "schema", Encoding.ASCII.GetBytes("still-here"));
            // Y reconnects from the same endpoint: a new generation replaces only Y.
            y.Session = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, InitialSequence = 30 });
            Deliver(y, y.Session.CreateConnect(0, Encoding.ASCII.GetBytes("hy2")));
            Send(y, "schema", Encoding.ASCII.GetBytes("new-gen"));
            log.Append($"# peers {server.Peers.Count} dropped {server.Stats.PacketsDropped}\n");
            foreach (var client in clients) client.Socket.Dispose();
            return log.ToString();
        }
    }
}
