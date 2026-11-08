#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// Heimdall's invariant: no client treats a reliable-transport ack as write admission. A put completes
    /// only on its result frame (write_not_authorized, or the success reply). The C# schema client has no
    /// completion call: SendCultNet returns once the put is queued, and the outcome reaches the caller only
    /// as the result frame, through OnCultNet. These tests serve it a peer that acknowledges the put and
    /// refuses it, the ack before the refusal and after it, and require the refusal to arrive as the outcome.
    /// </summary>
    public class CultNetClientWriteCompletionTests
    {
        private const uint ConnectionId = 0x10203197;

        // Acknowledges the first reliable schema frame it receives and answers it with write_not_authorized,
        // the second packet 150 ms after the first. The refusal carries no acknowledgement of its own.
        private static Task RefusingPeer(Socket socket, bool refusalFirst, CancellationToken stop)
        {
            return Task.Run(() =>
            {
                var session = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = ConnectionId, ResendDelayMs = 25 });
                var buffer = new byte[65535];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var answered = false;
                while (!stop.IsCancellationRequested)
                {
                    if (!socket.Poll(10_000, SelectMode.SelectRead))
                        continue;
                    var received = socket.ReceiveFrom(buffer, ref from);
                    var packet = CultNetRudpPacketCodec.Decode(buffer.AsSpan(0, received).ToArray());
                    void Send(CultNetRudpPacket p) => socket.SendTo(CultNetRudpPacketCodec.Encode(p), from);
                    if (packet.PacketType == CultNetRudpPacketType.Connect)
                    {
                        Send(session.AcceptConnect(packet));
                        continue;
                    }
                    var result = session.Receive(packet, 1);
                    if (result.Reply != null)
                        Send(result.Reply);
                    if (answered || result.Delivered.Count == 0)
                        continue;
                    answered = true;
                    var ack = session.CreateAckForReceived(packet.Sequence);
                    var refusal = session.Send("schema", CultNetSchemaMessageSerialization.Serialize(new CultNetErrorMessage
                    {
                        Error = "write_not_authorized: the catalog refused the document",
                        Code = "write_not_authorized"
                    }), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
                    refusal.Ack = 0;
                    refusal.AckMask = 0;
                    if (refusalFirst)
                    {
                        Send(refusal);
                        Thread.Sleep(150);
                        Send(ack);
                    }
                    else
                    {
                        Send(ack);
                        Thread.Sleep(150);
                        Send(refusal);
                    }
                }
            });
        }

        [TestCase(false, TestName = "AckThenRefusal")]
        [TestCase(true, TestName = "RefusalThenAck")]
        public async Task ARefusedPutIsReportedRefusedWhateverTheOrderOfItsAck(bool refusalFirst)
        {
            using var serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var stop = new CancellationTokenSource();
            var peer = RefusingPeer(serverSocket, refusalFirst, stop.Token);
            using var client = new RudpCultNetSchemaClient("completion", ConnectionId);
            var outcomes = new BlockingCollection<CultNetErrorMessage>();
            client.OnCultNet<CultNetErrorMessage>(outcomes.Add);
            client.Connect("127.0.0.1", ((IPEndPoint)serverSocket.LocalEndPoint!).Port);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!client.Connected && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            Assert.That(client.Connected, "fixture: the client connected");

            client.SendCultNet(new CultNetDocumentPutRawMessage { MessageId = "completion" });

            Assert.That(outcomes.TryTake(out var refusal, 3000), Is.True, "the result frame reached the caller");
            Assert.That(refusal!.Code, Is.EqualTo("write_not_authorized"));
            stop.Cancel();
            await peer;
        }
    }
}
