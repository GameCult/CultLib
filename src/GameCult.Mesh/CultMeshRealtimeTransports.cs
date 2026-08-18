using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Mesh
{
    /// <summary>Delivery semantics requested from a realtime state transport.</summary>
    public enum CultMeshRealtimeDelivery
    {
        ReliableOrdered,
        LatestOnly,
        Unreliable
    }

    /// <summary>
    /// Transport-neutral realtime state frame. Schema identity and logical generation
    /// remain stable when the physical connector changes.
    /// </summary>
    public sealed class CultMeshRealtimeFrame
    {
        public string ChannelId { get; set; } = string.Empty;
        public string SchemaId { get; set; } = string.Empty;
        public string BodyId { get; set; } = string.Empty;
        public long ProducerEpoch { get; set; }
        public long Sequence { get; set; }
        public CultMeshRealtimeDelivery Delivery { get; set; }
        public ReadOnlyMemory<byte> Payload { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(ChannelId))
                throw new InvalidOperationException("Realtime channel identity is required.");
            if (string.IsNullOrWhiteSpace(SchemaId))
                throw new InvalidOperationException("Realtime schema identity is required.");
            if (string.IsNullOrWhiteSpace(BodyId))
                throw new InvalidOperationException("Realtime body identity is required.");
            if (ProducerEpoch < 0 || Sequence < 0)
                throw new InvalidOperationException("Realtime epoch and sequence must be non-negative.");
        }
    }

    /// <summary>
    /// Bidirectional realtime state plane. Commands, receipts, manifests, and other
    /// registered schema documents do not travel through this port.
    /// </summary>
    public interface ICultMeshRealtimeTransport : IDisposable
    {
        string TransportId { get; }
        string Endpoint { get; }
        Task SendAsync(CultMeshRealtimeFrame frame, CancellationToken cancellationToken = default);
        Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Creates a realtime state transport, normally QUIC streams/datagrams.</summary>
    public interface ICultMeshRealtimeTransportConnector
    {
        string ConnectorId { get; }
        int Priority { get; }
        bool CanConnect(CultMeshTransportCandidate candidate);
        Task<ICultMeshRealtimeTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshEndpointId endpointId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Reusable identity-bound realtime state session.</summary>
    public sealed class CultMeshRealtimeSession : IDisposable
    {
        private readonly object _gate = new();
        private readonly Action<CultMeshRealtimeSession> _onTransportFailure;
        private ICultMeshRealtimeTransport _transport;
        private bool _disposed;

        internal CultMeshRealtimeSession(
            CultMeshSessionTarget target,
            ICultMeshRealtimeTransport transport,
            CultMeshSessionState state,
            Action<CultMeshRealtimeSession> onTransportFailure)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            State = state ?? throw new ArgumentNullException(nameof(state));
            _onTransportFailure = onTransportFailure ?? throw new ArgumentNullException(nameof(onTransportFailure));
        }

        public CultMeshSessionTarget Target { get; }
        public CultMeshSessionState State { get; private set; }
        public string TransportId { get { lock (_gate) return _transport.TransportId; } }

        public async Task SendAsync(
            CultMeshRealtimeFrame frame,
            CancellationToken cancellationToken = default)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            frame.Validate();
            var transport = GetTransport();
            try
            {
                await transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _onTransportFailure(this);
                throw;
            }
        }

        public async Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            var transport = GetTransport();
            try
            {
                var frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                frame.Validate();
                return frame;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _onTransportFailure(this);
                throw;
            }
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
            ICultMeshRealtimeTransport transport;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                transport = _transport;
            }
            transport.Dispose();
        }

        private ICultMeshRealtimeTransport GetTransport()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CultMeshRealtimeSession));
                return _transport;
            }
        }
    }
}
