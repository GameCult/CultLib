using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Mesh
{
    /// <summary>
    /// Moves immutable content bytes for one selected physical path. Content identity,
    /// verification, checkpoints, and promotion remain owned by
    /// <see cref="CultMeshContentTransferService"/>.
    /// </summary>
    public interface ICultMeshContentTransport : IDisposable
    {
        string TransportId { get; }
        string Endpoint { get; }

        /// <summary>
        /// Writes exactly one advertised chunk into the caller-owned destination.
        /// The transport must honor destination backpressure and must not close it.
        /// </summary>
        Task CopyChunkToAsync(
            CultMeshCdnChunkRef chunk,
            Stream destination,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Creates physical content transports. Lower priority values are attempted first;
    /// a fallback tier is not raced against a preferred tier.
    /// </summary>
    public interface ICultMeshContentTransportConnector
    {
        string ConnectorId { get; }
        int Priority { get; }
        bool CanConnect(CultMeshTransportCandidate candidate);
        Task<ICultMeshContentTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshSessionTarget target,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Reusable identity-bound content session selected and owned by
    /// <see cref="CultMeshSessionManager"/>.
    /// </summary>
    public sealed class CultMeshContentSession : IDisposable
    {
        private readonly object _gate = new();
        private readonly Action<CultMeshContentSession> _onTransportFailure;
        private ICultMeshContentTransport _transport;
        private bool _disposed;

        internal CultMeshContentSession(
            CultMeshSessionTarget target,
            ICultMeshContentTransport transport,
            CultMeshSessionState state,
            Action<CultMeshContentSession> onTransportFailure)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            State = state ?? throw new ArgumentNullException(nameof(state));
            _onTransportFailure = onTransportFailure ?? throw new ArgumentNullException(nameof(onTransportFailure));
        }

        public CultMeshSessionTarget Target { get; }
        public CultMeshSessionState State { get; private set; }
        public string TransportId { get { lock (_gate) return _transport.TransportId; } }

        public async Task CopyChunkToAsync(
            CultMeshCdnChunkRef chunk,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            ICultMeshContentTransport transport;
            lock (_gate)
            {
                ThrowIfDisposed();
                transport = _transport;
            }
            try
            {
                await transport.CopyChunkToAsync(chunk, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _onTransportFailure(this);
                throw;
            }
        }

        internal void Replace(ICultMeshContentTransport transport, CultMeshSessionState state)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            ICultMeshContentTransport previous;
            lock (_gate)
            {
                ThrowIfDisposed();
                previous = _transport;
                _transport = transport;
                State = state;
            }
            previous.Dispose();
        }

        internal void MarkOffline(CultMeshSessionState state)
        {
            lock (_gate)
            {
                if (_disposed) return;
                State = state;
            }
        }

        public void Dispose()
        {
            ICultMeshContentTransport transport;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                transport = _transport;
            }
            transport.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshContentSession));
        }
    }

    /// <summary>
    /// Adapts a selected streaming transport to the verified content-transfer source port.
    /// </summary>
    public sealed class CultMeshSessionContentProvider : ICultMeshContentProvider
    {
        private readonly CultMeshSessionManager _sessions;
        private readonly CultMeshSessionTarget _target;

        public CultMeshSessionContentProvider(
            string providerId,
            CultMeshSessionManager sessions,
            CultMeshSessionTarget target)
        {
            ProviderId = string.IsNullOrWhiteSpace(providerId)
                ? throw new ArgumentException("Provider identity is required.", nameof(providerId))
                : providerId;
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        public string ProviderId { get; }

        public async Task CopyChunkToAsync(
            CultMeshCdnChunkRef chunk,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            var session = await _sessions.ConnectContentAsync(_target, cancellationToken).ConfigureAwait(false);
            await session.CopyChunkToAsync(chunk, destination, cancellationToken).ConfigureAwait(false);
        }
    }
}
