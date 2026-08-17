using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// Presents one logical CultNet schema server over several physical transports.
    /// It owns handler fan-out and peer-lifecycle aggregation, but never owns or
    /// disposes the transport servers themselves.
    /// </summary>
    public sealed class CultNetSchemaServerGroup :
        ICultNetSchemaServer,
        ICultNetSchemaServerPeerLifecycle,
        IDisposable
    {
        private readonly IReadOnlyList<ICultNetSchemaServer> _servers;
        private readonly IReadOnlyList<ICultNetSchemaServerPeerLifecycle> _lifecycles;
        private bool _disposed;

        /// <summary>Creates one logical server from two or more transport servers.</summary>
        public CultNetSchemaServerGroup(params ICultNetSchemaServer[] servers)
        {
            if (servers == null) throw new ArgumentNullException(nameof(servers));
            if (servers.Length < 2)
                throw new ArgumentException("A CultNet schema server group requires at least two transports.", nameof(servers));
            if (servers.Any(server => server == null))
                throw new ArgumentException("A CultNet schema server group cannot contain a null transport.", nameof(servers));
            for (var left = 0; left < servers.Length; left++)
            for (var right = left + 1; right < servers.Length; right++)
            {
                if (ReferenceEquals(servers[left], servers[right]))
                    throw new ArgumentException("A CultNet schema server group cannot contain the same transport twice.", nameof(servers));
            }

            _servers = Array.AsReadOnly(servers.ToArray());
            _lifecycles = Array.AsReadOnly(servers.OfType<ICultNetSchemaServerPeerLifecycle>().ToArray());
            foreach (var lifecycle in _lifecycles) lifecycle.PeerDisconnected += ForwardPeerDisconnected;
        }

        /// <inheritdoc />
        public event Action<ICultNetSchemaServerPeer>? PeerDisconnected;

        /// <inheritdoc />
        public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            ThrowIfDisposed();
            var registered = 0;
            try
            {
                foreach (var server in _servers)
                {
                    server.OnCultNet(callback);
                    registered++;
                }
            }
            catch
            {
                for (var index = 0; index < registered; index++)
                    _servers[index].RemoveCultNetMessageListener<TMessage>(callback);
                throw;
            }
        }

        /// <inheritdoc />
        public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
            where TMessage : ICultNetSchemaMessage
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (_disposed) return;
            foreach (var server in _servers) server.RemoveCultNetMessageListener<TMessage>(callback);
        }

        /// <summary>
        /// Detaches lifecycle forwarding. The physical transports retain their
        /// independent ownership and must be disposed by their creator.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lifecycle in _lifecycles) lifecycle.PeerDisconnected -= ForwardPeerDisconnected;
            PeerDisconnected = null;
        }

        private void ForwardPeerDisconnected(ICultNetSchemaServerPeer peer) =>
            PeerDisconnected?.Invoke(peer);

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultNetSchemaServerGroup));
        }
    }
}
