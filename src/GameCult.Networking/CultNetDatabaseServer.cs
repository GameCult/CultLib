using System;
using System.Threading.Tasks;
using LiteNetLib;

namespace GameCult.Networking
{
    /// <summary>
    /// Routes CultNet schema-v0 document messages through a <see cref="CultNetDatabase"/>.
    /// </summary>
    public sealed class CultNetDatabaseServer : IDisposable
    {
        private readonly Server _server;
        private readonly CultNetDatabase _database;
        private readonly Func<CultNetSnapshotRequestMessage, NetPeer, Task> _snapshotHandler;
        private readonly Func<CultNetDocumentPutRawMessage, NetPeer, Task> _putHandler;
        private readonly Func<CultNetDocumentDeleteMessage, NetPeer, Task> _deleteHandler;
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a database message bridge to a server.
        /// </summary>
        public CultNetDatabaseServer(Server server, CultNetDatabase database)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _snapshotHandler = HandleSnapshotRequestAsync;
            _putHandler = HandlePutAsync;
            _deleteHandler = HandleDeleteAsync;

            _server.OnCultNet(_snapshotHandler);
            _server.OnCultNet(_putHandler);
            _server.OnCultNet(_deleteHandler);
        }

        /// <summary>
        /// Gets the database used by this bridge.
        /// </summary>
        public CultNetDatabase Database => _database;

        /// <summary>
        /// Applies a raw put message through the database shard policy.
        /// </summary>
        public Task<object> ApplyPutAsync(CultNetDocumentPutRawMessage message)
        {
            return _database.ApplyPutAsync(message);
        }

        /// <summary>
        /// Applies a raw delete message through the database shard policy.
        /// </summary>
        public Task ApplyDeleteAsync(CultNetDocumentDeleteMessage message)
        {
            return _database.ApplyDeleteAsync(message);
        }

        /// <summary>
        /// Creates a raw snapshot response from the database cache.
        /// </summary>
        public CultNetSnapshotResponseRawMessage CreateSnapshotResponse(CultNetSnapshotRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return _database.Documents.CreateRawSnapshotResponse(
                _database.Cache,
                string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                request);
        }

        /// <summary>
        /// Detaches handlers from the server.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _server.RemoveCultNetMessageListener<CultNetSnapshotRequestMessage>(_snapshotHandler);
            _server.RemoveCultNetMessageListener<CultNetDocumentPutRawMessage>(_putHandler);
            _server.RemoveCultNetMessageListener<CultNetDocumentDeleteMessage>(_deleteHandler);
        }

        private Task HandleSnapshotRequestAsync(CultNetSnapshotRequestMessage request, NetPeer peer)
        {
            peer.SendCultNet(CreateSnapshotResponse(request));
            return Task.CompletedTask;
        }

        private async Task HandlePutAsync(CultNetDocumentPutRawMessage message, NetPeer peer)
        {
            try
            {
                await ApplyPutAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }
        }

        private async Task HandleDeleteAsync(CultNetDocumentDeleteMessage message, NetPeer peer)
        {
            try
            {
                await ApplyDeleteAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                peer.SendCultNet(new CultNetErrorMessage { Error = ex.Message });
            }
        }
    }
}
