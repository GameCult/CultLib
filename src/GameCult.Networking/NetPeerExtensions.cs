using LiteNetLib;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Adds MessagePack-based send helpers for <see cref="NetPeer"/>.
    /// </summary>
    public static class NetPeerExtensions
    {
        /// <summary>
        /// Serializes and sends a message using reliable ordered delivery.
        /// </summary>
        /// <typeparam name="T">The concrete message type.</typeparam>
        /// <param name="peer">The peer to send through.</param>
        /// <param name="message">The message to serialize and send.</param>
        public static void Send<T>(this NetPeer peer, T message) where T : Message
        {
            peer.Send(MessageSerialization.Serialize(message), DeliveryMethod.ReliableOrdered);
        }
    }
}
