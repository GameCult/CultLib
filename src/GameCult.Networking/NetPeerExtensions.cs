using LiteNetLib;

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
            new LiteNetLibTransportConnection(peer).SendLegacy(message);
        }

        /// <summary>
        /// Serializes and sends a modern CultNet schema-v0 message using reliable ordered delivery.
        /// </summary>
        /// <typeparam name="T">The concrete schema-v0 message type.</typeparam>
        /// <param name="peer">The peer to send through.</param>
        /// <param name="message">The schema-v0 message to serialize and send.</param>
        public static void SendCultNet<T>(this NetPeer peer, T message) where T : ICultNetSchemaMessage
        {
            new LiteNetLibTransportConnection(peer).SendSchema(message);
        }
    }
}
