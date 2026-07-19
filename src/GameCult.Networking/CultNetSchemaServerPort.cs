using System;
using System.Net;
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

    /// <summary>Optional physical peer location used for locality-sensitive transport negotiation.</summary>
    public interface ICultNetSchemaServerPeerLocation
    {
        /// <summary>Gets the remote physical endpoint when the transport exposes one.</summary>
        EndPoint? RemoteEndPoint { get; }
    }

    /// <summary>
    /// Optional transport-neutral peer lifetime surface for stateful service bridges.
    /// </summary>
    public interface ICultNetSchemaServerPeerLifecycle
    {
        /// <summary>
        /// Raised after a peer session is no longer able to receive service output.
        /// </summary>
        event Action<ICultNetSchemaServerPeer> PeerDisconnected;
    }
}
