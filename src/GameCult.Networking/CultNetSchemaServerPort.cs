using System;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// Transport-neutral schema-v0 peer surface for service bridges.
    /// </summary>
    public interface ICultNetSchemaServerPeer
    {
        /// <summary>
        /// Sends a schema-v0 message to this peer.
        /// </summary>
        void SendCultNet<TMessage>(TMessage message)
            where TMessage : ICultNetSchemaMessage;
    }

    /// <summary>
    /// Transport-neutral schema-v0 server surface for service bridges.
    /// </summary>
    public interface ICultNetSchemaServer
    {
        /// <summary>
        /// Registers a schema-v0 handler.
        /// </summary>
        void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage;

        /// <summary>
        /// Removes a previously registered schema-v0 handler.
        /// </summary>
        void RemoveCultNetMessageListener<TMessage>(Delegate callback)
            where TMessage : ICultNetSchemaMessage;
    }
}
