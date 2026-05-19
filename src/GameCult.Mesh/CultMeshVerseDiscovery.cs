using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Networking;
using LiteNetLib;

namespace GameCult.Mesh
{
    /// <summary>
    /// Conversion helpers for CultMesh Verse wire messages.
    /// </summary>
    public static class CultMeshVerseMessages
    {
        /// <summary>
        /// Converts a local Verse descriptor to its schema-v0 wire shape.
        /// </summary>
        public static CultMeshVerseDescriptorMessage ToMessage(this CultMeshVerseDescriptor verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return new CultMeshVerseDescriptorMessage
            {
                VerseId = verse.VerseId,
                DisplayName = verse.DisplayName,
                AuthorityModel = verse.AuthorityModel.ToString(),
                Compatibility = new CultMeshVerseCompatibilityMessage
                {
                    TransportVersion = verse.Compatibility.TransportVersion,
                    RulesHash = verse.Compatibility.RulesHash,
                    CompatibleVerseIds = verse.Compatibility.CompatibleVerseIds.ToArray(),
                    RequiredPluginIds = verse.Compatibility.RequiredPluginIds.ToArray(),
                    OptionalPluginIds = verse.Compatibility.OptionalPluginIds.ToArray()
                },
                DiscoveryEndpoints = verse.DiscoveryEndpoints.ToArray(),
                AuthorityRuntimeIds = verse.AuthorityRuntimeIds.ToArray(),
                ParentVerseId = verse.ParentVerseId,
                Description = verse.Description
            };
        }

        /// <summary>
        /// Converts a schema-v0 Verse descriptor to the local public API shape.
        /// </summary>
        public static CultMeshVerseDescriptor ToVerseDescriptor(this CultMeshVerseDescriptorMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var authorityModel = Enum.TryParse<CultMeshVerseAuthorityModel>(
                message.AuthorityModel,
                ignoreCase: true,
                out var parsed)
                ? parsed
                : CultMeshVerseAuthorityModel.SubscribedOverlay;
            return new CultMeshVerseDescriptor(
                message.VerseId,
                message.DisplayName,
                authorityModel,
                new CultMeshVerseCompatibility(
                    message.Compatibility.TransportVersion,
                    message.Compatibility.RulesHash,
                    message.Compatibility.CompatibleVerseIds,
                    message.Compatibility.RequiredPluginIds,
                    message.Compatibility.OptionalPluginIds),
                message.DiscoveryEndpoints,
                message.AuthorityRuntimeIds,
                message.ParentVerseId,
                message.Description);
        }
    }

    /// <summary>
    /// Answers CultMesh Verse discovery requests from a local Verse catalog.
    /// </summary>
    public sealed class CultMeshVerseDiscoveryServer : IDisposable
    {
        private readonly Server _server;
        private readonly CultMeshVerseCatalog _catalog;
        private readonly Func<CultMeshVerseCatalogRequestMessage, NetPeer, Task> _requestHandler;
        private bool _disposed;

        /// <summary>
        /// Creates and attaches a Verse discovery bridge.
        /// </summary>
        public CultMeshVerseDiscoveryServer(Server server, CultMeshVerseCatalog catalog)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _requestHandler = HandleRequestAsync;
            _server.OnCultNet(_requestHandler);
        }

        /// <summary>
        /// Creates a Verse catalog response for a request.
        /// </summary>
        public CultMeshVerseCatalogResponseMessage CreateResponse(CultMeshVerseCatalogRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var verseIds = request.VerseIds == null || request.VerseIds.Length == 0
                ? null
                : new HashSet<string>(request.VerseIds, StringComparer.Ordinal);
            var verses = _catalog.Verses
                .Where(verse => verseIds == null || verseIds.Contains(verse.VerseId))
                .Where(verse => string.IsNullOrWhiteSpace(request.TransportVersion) ||
                                string.Equals(verse.Compatibility.TransportVersion, request.TransportVersion, StringComparison.Ordinal))
                .Select(verse => verse.ToMessage())
                .ToArray();

            return new CultMeshVerseCatalogResponseMessage
            {
                MessageId = string.IsNullOrWhiteSpace(request.MessageId)
                    ? Guid.NewGuid().ToString("N")
                    : request.MessageId,
                Verses = verses
            };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _server.RemoveCultNetMessageListener<CultMeshVerseCatalogRequestMessage>(_requestHandler);
        }

        private Task HandleRequestAsync(CultMeshVerseCatalogRequestMessage request, NetPeer peer)
        {
            peer.SendCultNet(CreateResponse(request));
            return Task.CompletedTask;
        }
    }
}
