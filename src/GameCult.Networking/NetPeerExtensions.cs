using LiteNetLib;
using MessagePack;

namespace GameCult.Networking
{
    public static class NetPeerExtensions
    {
        public static void Send<T>(this NetPeer peer, T message) where T : Message
        {
            peer.Send(MessagePackSerializer.Serialize(message), DeliveryMethod.ReliableOrdered);
        }
    }
}