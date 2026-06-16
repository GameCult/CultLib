using System;
using System.Linq;
using System.Threading.Tasks;
using LiteNetLib;

namespace GameCult.Networking
{
    /// <summary>
    /// Bridges schema-v0 simulation observation messages into a local observation hub.
    /// </summary>
    public sealed class CultNetSimulationObservationServer : IDisposable
    {
        private readonly Server _server;
        private readonly CultNetSimulationObservationHub _hub;
        private readonly Func<CultNetSimulationObservationMessage, NetPeer, Task> _observationHandler;
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a simulation observation bridge.
        /// </summary>
        public CultNetSimulationObservationServer(
            Server server,
            CultNetSimulationObservationHub hub)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _observationHandler = HandleObservationAsync;
            _server.OnCultNet(_observationHandler);
        }

        /// <summary>
        /// Gets the observation hub used by this bridge.
        /// </summary>
        public CultNetSimulationObservationHub Hub => _hub;

        /// <summary>
        /// Applies an observation message and returns current candidate messages for the same subject.
        /// </summary>
        public CultNetSimulationConsensusCandidateMessage[] CreateCandidateMessages(
            CultNetSimulationObservationMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return _hub.Submit(message)
                .Select(candidate => CultNetSimulationConsensusCandidateMessage.FromCandidate(
                    message.MessageId,
                    candidate))
                .ToArray();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _server.RemoveCultNetMessageListener<CultNetSimulationObservationMessage>(_observationHandler);
        }

        private Task HandleObservationAsync(CultNetSimulationObservationMessage message, NetPeer peer)
        {
            try
            {
                foreach (var candidate in CreateCandidateMessages(message))
                {
                    _server.SendCultNet(peer, candidate);
                }
            }
            catch (Exception ex)
            {
                _server.SendCultNet(peer, new CultNetErrorMessage { Error = ex.Message });
            }

            return Task.CompletedTask;
        }
    }
}
