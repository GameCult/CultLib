#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    /// <summary>
    /// Ordered delivery has one owner: the contiguous received-through watermark. A frame is held for
    /// exactly one reason, a reliable sequence below it has not arrived, and it is delivered in the call
    /// that fills the last such gap, whichever channel the gap belonged to.
    /// </summary>
    public sealed class CultNetRudpOrderedDeliveryTests
    {
        private static (CultNetRudpSession Client, CultNetRudpSession Server) Handshake()
        {
            var client = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = 410, InitialSequence = 1 });
            var server = new CultNetRudpSession(new CultNetRudpSessionOptions { ConnectionId = 410, InitialSequence = 500 });
            var accept = server.AcceptConnect(client.CreateConnect(0), 0);
            client.Receive(accept, 0);
            return (client, server);
        }

        private static CultNetRudpSendOptions Reliable(bool ordered) => new CultNetRudpSendOptions { Reliable = true, Ordered = ordered };

        private static CultNetRudpPacket Send(CultNetRudpSession session, string channel, string payload, bool ordered)
            => session.Send(channel, Encoding.UTF8.GetBytes(payload), Reliable(ordered));

        private static string[] Names(CultNetRudpReceiveResult result)
            => result.Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray();

        [Test]
        public void OrderedFrameWaitsForAGapFilledByAnotherChannel()
        {
            var (sender, receiver) = Handshake();
            var o1 = Send(sender, "schema", "o1", true);
            var u = Send(sender, "rel", "u", false);
            var o2 = Send(sender, "schema", "o2", true);
            var o3 = Send(sender, "schema", "o3", true);

            Assert.That(Names(receiver.Receive(o1, 1)), Is.EqualTo(new[] { "o1" }));
            Assert.That(Names(receiver.Receive(o2, 2)), Is.Empty, "u is missing");
            Assert.That(Names(receiver.Receive(o3, 3)), Is.Empty, "u is missing");
            Assert.That(Names(receiver.Receive(u, 4)), Is.EqualTo(new[] { "u", "o2", "o3" }));
        }

        [Test]
        public void TwoOrderedChannelsReleaseEachOtherInSequenceOrder()
        {
            var (sender, receiver) = Handshake();
            var a1 = Send(sender, "schema", "A1", true);
            var b1 = Send(sender, "other", "B1", true);
            var a2 = Send(sender, "schema", "A2", true);
            var a3 = Send(sender, "schema", "A3", true);

            var delivered = new List<string>();
            delivered.AddRange(Names(receiver.Receive(a1, 1)));
            delivered.AddRange(Names(receiver.Receive(a2, 2)));
            delivered.AddRange(Names(receiver.Receive(a3, 3)));
            Assert.That(delivered, Is.EqualTo(new[] { "A1" }), "B1 is missing, so A2 and A3 wait");
            Assert.That(Names(receiver.Receive(b1, 4)), Is.EqualTo(new[] { "B1", "A2", "A3" }));
        }

        [Test]
        public void ChannelFirstUsedAfterOtherTrafficLosesNothing()
        {
            var (sender, receiver) = Handshake();
            var c1 = Send(sender, "late", "C1", true);
            var c2 = Send(sender, "late", "C2", true);
            var x = Send(sender, "schema", "X", true);

            Assert.That(Names(receiver.Receive(x, 1)), Is.Empty, "C1 is missing");
            Assert.That(Names(receiver.Receive(c2, 2)), Is.Empty, "C1 is missing");
            Assert.That(Names(receiver.Receive(c1, 3)), Is.EqualTo(new[] { "C1", "C2", "X" }));
        }

        [Test]
        public void AcceptSeedsTheWatermarkOfTheConnectingSide()
        {
            var (client, server) = Handshake();
            var s1 = Send(server, "schema", "s1", true);
            var s2 = Send(server, "schema", "s2", true);

            Assert.That(Names(client.Receive(s2, 1)), Is.Empty);
            Assert.That(Names(client.Receive(s1, 2)), Is.EqualTo(new[] { "s1", "s2" }));
        }

        [Test]
        public void ConnectSeedsTheWatermarkOfTheAcceptingSide()
        {
            var (client, server) = Handshake();
            var first = Send(client, "schema", "first", true);
            var second = Send(client, "schema", "second", true);

            Assert.That(Names(server.Receive(second, 1)), Is.Empty);
            Assert.That(Names(server.Receive(first, 2)), Is.EqualTo(new[] { "first", "second" }));
        }

        [Test]
        public void DuplicateOfAHeldFrameIsNotDeliveredTwice()
        {
            var (sender, receiver) = Handshake();
            var s1 = Send(sender, "schema", "s1", true);
            var s2 = Send(sender, "schema", "s2", true);

            Assert.That(Names(receiver.Receive(s2, 1)), Is.Empty);
            Assert.That(Names(receiver.Receive(s2, 2)), Is.Empty);
            Assert.That(Names(receiver.Receive(s1, 3)), Is.EqualTo(new[] { "s1", "s2" }));
            Assert.That(Names(receiver.Receive(s2, 4)), Is.Empty);
        }

        [Test]
        public void ResetPeerStateForgetsHeldFramesAndTheWatermark()
        {
            var (sender, receiver) = Handshake();
            var s1 = Send(sender, "schema", "s1", true);
            var s2 = Send(sender, "schema", "s2", true);
            Assert.That(Names(receiver.Receive(s2, 1)), Is.Empty);

            receiver.ResetPeerState();
            receiver.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 410, Sequence = 1, ChannelId = "control" }, 2);

            Assert.That(Names(receiver.Receive(s1, 3)), Is.EqualTo(new[] { "s1" }), "the held s2 died with the reset");
        }
    }
}
