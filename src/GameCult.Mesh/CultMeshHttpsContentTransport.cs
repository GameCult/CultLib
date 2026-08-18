using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Mesh
{
    /// <summary>
    /// Opens immutable content over an Odin-certified HTTPS route. Provider
    /// identity comes from the verified route and TLS endpoint; the transfer
    /// service remains responsible for checking the advertised chunk hash.
    /// </summary>
    public sealed class CultMeshHttpsContentTransportConnector : ICultMeshContentTransportConnector
    {
        private readonly Func<HttpMessageHandler> _createHandler;

        public CultMeshHttpsContentTransportConnector(Func<HttpMessageHandler>? createHandler = null)
        {
            _createHandler = createHandler ?? (() => new HttpClientHandler());
        }

        public string ConnectorId => "https-content";
        public int Priority => 0;

        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            candidate != null &&
            Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var endpoint) &&
            string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        public Task<ICultMeshContentTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshSessionTarget target,
            CancellationToken cancellationToken = default)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!CanConnect(candidate))
                throw new NotSupportedException($"HTTPS content connector does not support '{candidate.Endpoint}'.");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ICultMeshContentTransport>(new CultMeshHttpsContentTransport(
                candidate.Endpoint,
                new HttpClient(_createHandler(), disposeHandler: true),
                target,
                candidate.Generation));
        }
    }

    internal sealed class CultMeshHttpsContentTransport : ICultMeshContentTransport, ICultMeshVerifiedTransport
    {
        private readonly HttpClient _client;
        private readonly CultMeshSessionTarget _target;
        private readonly string _routeGeneration;
        private bool _disposed;

        public CultMeshHttpsContentTransport(
            string endpoint,
            HttpClient client,
            CultMeshSessionTarget target,
            string routeGeneration)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _routeGeneration = routeGeneration ?? string.Empty;
        }

        public string TransportId => "https-content";
        public string Endpoint { get; }

        public bool IsVerifiedFor(
            string verseId,
            string authorityRuntimeId,
            string protocolId,
            string routeGeneration) =>
            string.Equals(_target.VerseId, verseId, StringComparison.Ordinal) &&
            string.Equals(_target.AuthorityRuntimeId, authorityRuntimeId, StringComparison.Ordinal) &&
            string.Equals(CultMeshProtocols.Content.Value, protocolId, StringComparison.Ordinal) &&
            string.Equals(_routeGeneration, routeGeneration, StringComparison.Ordinal);

        public async Task CopyChunkToAsync(
            CultMeshCdnChunkRef chunk,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshHttpsContentTransport));
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new ArgumentException("Content destination must be writable.", nameof(destination));

            var requestUri = BuildChunkUri(Endpoint, chunk);
            using var response = await _client.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != chunk.SizeBytes)
            {
                throw new InvalidDataException(
                    $"HTTPS content length {contentLength} did not match advertised chunk size {chunk.SizeBytes}.");
            }
            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }

        private static Uri BuildChunkUri(string endpoint, CultMeshCdnChunkRef chunk)
        {
            var builder = new UriBuilder(endpoint);
            var separator = string.IsNullOrWhiteSpace(builder.Query) ? string.Empty : builder.Query.TrimStart('?') + "&";
            builder.Query = separator +
                "chunkHash=" + Uri.EscapeDataString(CultMeshCdn.NormalizeHash(chunk.ChunkHash, nameof(chunk.ChunkHash))) +
                "&recordKey=" + Uri.EscapeDataString(chunk.RecordKey ?? string.Empty);
            return builder.Uri;
        }
    }
}
