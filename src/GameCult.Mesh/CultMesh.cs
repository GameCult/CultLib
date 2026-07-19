using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;
using R3;

namespace GameCult.Mesh
{
    /// <summary>
    /// Options for creating a local CultMesh node.
    /// </summary>
    public sealed class CultMeshNodeOptions
    {
        /// <summary>
        /// Gets or sets the cache-open options used for the underlying CultCache.
        /// </summary>
        public CultCacheOpenOptions? CacheOptions { get; set; }

        /// <summary>
        /// Gets or sets the server security configuration. When omitted, development defaults are used.
        /// </summary>
        public ServerSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets whether the server should start immediately after creation.
        /// </summary>
        public bool StartServer { get; set; } = true;

        /// <summary>
        /// Gets or sets the mesh database sharding options used for the hosted CultCache.
        /// </summary>
        public CultNetDatabaseOptions? DatabaseOptions { get; set; }

        /// <summary>
        /// Gets or sets whether CultMesh should attach a file-backed authoritative shard-log store when none is supplied.
        /// </summary>
        public bool EnableDurableShardLogs { get; set; }

        /// <summary>
        /// Gets or sets the directory used for file-backed authoritative shard logs.
        /// </summary>
        public string? ShardLogPath { get; set; }

        /// <summary>
        /// Gets or sets the database server bridge options used for shard routing and forwarding.
        /// </summary>
        public CultNetDatabaseServerOptions? DatabaseServerOptions { get; set; }

        /// <summary>
        /// Gets or sets an optional callback used to customize the server before start.
        /// </summary>
        public Action<Server>? ConfigureServer { get; set; }

        internal CultNetHostOptions ToCultNetOptions(string cachePath)
        {
            return new CultNetHostOptions
            {
                CacheOptions = CacheOptions,
                Security = Security,
                StartServer = StartServer,
                DatabaseOptions = CreateDatabaseOptions(cachePath),
                DatabaseServerOptions = DatabaseServerOptions,
                ConfigureServer = ConfigureServer
            };
        }

        private CultNetDatabaseOptions? CreateDatabaseOptions(string cachePath)
        {
            if (!EnableDurableShardLogs)
            {
                return DatabaseOptions;
            }

            var source = DatabaseOptions ?? new CultNetDatabaseOptions();
            if (source.MutationLogStore != null)
            {
                return source;
            }

            return new CultNetDatabaseOptions
            {
                RuntimeId = source.RuntimeId,
                Shards = source.Shards,
                ClientAuthorityScopes = source.ClientAuthorityScopes,
                DocumentRegistry = source.DocumentRegistry,
                MutationLogStore = new CultNetFileShardMutationLogStore(ResolveShardLogPath(cachePath))
            };
        }

        private string ResolveShardLogPath(string cachePath)
        {
            if (!string.IsNullOrWhiteSpace(ShardLogPath))
            {
                return ShardLogPath!;
            }

            var fullPath = Path.GetFullPath(cachePath);
            var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var name = Path.GetFileNameWithoutExtension(fullPath);
            return Path.Combine(directory, name + ".cultmesh", "shard-logs");
        }
    }

    /// <summary>
    /// Options for opening one typed document from a remote CultNet snapshot endpoint.
    /// </summary>
    public sealed class CultMeshPeerSnapshotDocumentOptions
    {
        /// <summary>Gets or sets the semantic document id. Defaults to the record key.</summary>
        public string? DocumentId { get; set; }

        /// <summary>Gets or sets the source id advertised by diagnostics. Defaults to the record key.</summary>
        public string? SourceId { get; set; }

        /// <summary>Gets or sets the route hint for the resulting handle.</summary>
        public CultMeshRouteHint? RouteHint { get; set; }

        /// <summary>Gets or sets the response timeout for each snapshot request.</summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets the connection timeout for endpoint-created clients.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets the polling interval for watch fallback.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>Gets or sets the message id prefix used for snapshot requests.</summary>
        public string? MessageIdPrefix { get; set; }

        /// <summary>Gets or sets schema ids to request. Empty means the document type schema is requested.</summary>
        public IReadOnlyList<string>? SchemaIds { get; set; }

        /// <summary>Gets or sets the target shard id, when the endpoint is shard-aware.</summary>
        public string? ShardId { get; set; }

        /// <summary>Gets or sets the target shard epoch, when the endpoint is shard-aware.</summary>
        public long? ShardEpoch { get; set; }

        /// <summary>Gets or sets client security options for endpoint-created clients.</summary>
        public ClientSecurityOptions? Security { get; set; }

        /// <summary>Gets or sets a callback used to configure endpoint-created LiteNetLib clients.</summary>
        public Action<Client>? ConfigureClient { get; set; }

        /// <summary>Gets or sets the runtime id used by endpoint-created RUDP clients.</summary>
        public string? RudpRuntimeId { get; set; }

        /// <summary>Gets or sets the connection id used by endpoint-created RUDP clients.</summary>
        public uint RudpConnectionId { get; set; } = 0x43554c54;

        /// <summary>Gets or sets the connect payload used by endpoint-created RUDP clients.</summary>
        public string RudpConnectPayload { get; set; } = "cultnet-schema-rudp";

        /// <summary>Gets or sets the maximum fragment size used by endpoint-created RUDP clients.</summary>
        public int RudpMaxFragmentBytes { get; set; } = 1024;

        /// <summary>Gets or sets the resend delay used by endpoint-created RUDP clients.</summary>
        public long RudpResendDelayMs { get; set; } = 25;
    }

    /// <summary>
    /// Options for opening one typed document from a CultCache backing-store publication.
    /// </summary>
    public sealed class CultMeshStoreDocumentOptions
    {
        /// <summary>Gets or sets the semantic document id. Defaults to the record key.</summary>
        public string? DocumentId { get; set; }

        /// <summary>Gets or sets the source id advertised by diagnostics. Defaults to the record key.</summary>
        public string? SourceId { get; set; }

        /// <summary>Gets or sets the route hint for the resulting handle.</summary>
        public CultMeshRouteHint? RouteHint { get; set; }

        /// <summary>Gets or sets the polling interval for watch fallback.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>Gets or sets the document registry used to resolve persisted schema aliases.</summary>
        public CultDocumentRegistry? Registry { get; set; }
    }

    /// <summary>
    /// Describes where a typed document publication should be read from.
    /// </summary>
    public abstract class CultMeshDocumentPublicationSource
    {
        private CultMeshDocumentPublicationSource()
        {
        }

        /// <summary>Creates a publication source from a CultCache backing store.</summary>
        public static CultMeshDocumentPublicationSource Store(CacheBackingStore store)
        {
            return new StoreSource(store);
        }

        /// <summary>Creates a publication source from a single-file MessagePack CultCache publication.</summary>
        public static CultMeshDocumentPublicationSource SingleFile(string path)
        {
            return new SingleFileSource(path);
        }

        /// <summary>Creates a publication source from a remote CultNet snapshot response provider.</summary>
        public static CultMeshDocumentPublicationSource PeerSnapshot(Func<CultMeshQueryContext, Task<CultNetSnapshotResponseRawMessage>> snapshot)
        {
            return new PeerSnapshotSource(snapshot);
        }

        /// <summary>Creates a publication source from a remote CultNet schema endpoint.</summary>
        public static CultMeshDocumentPublicationSource PeerSnapshot(string endpoint)
        {
            return new PeerSnapshotEndpointSource(endpoint);
        }

        /// <summary>Creates a publication source from a remote CultNet schema client factory.</summary>
        public static CultMeshDocumentPublicationSource PeerSnapshot(Func<ICultNetSchemaClient> createClient, string endpoint)
        {
            return new PeerSnapshotClientSource(createClient, endpoint);
        }

        internal sealed class StoreSource : CultMeshDocumentPublicationSource
        {
            public StoreSource(CacheBackingStore store)
            {
                BackingStore = store ?? throw new ArgumentNullException(nameof(store));
            }

            public CacheBackingStore BackingStore { get; }
        }

        internal sealed class SingleFileSource : CultMeshDocumentPublicationSource
        {
            public SingleFileSource(string path)
            {
                Path = string.IsNullOrWhiteSpace(path)
                    ? throw new ArgumentException("Value must be non-empty.", nameof(path))
                    : path;
            }

            public string Path { get; }
        }

        internal sealed class PeerSnapshotSource : CultMeshDocumentPublicationSource
        {
            public PeerSnapshotSource(Func<CultMeshQueryContext, Task<CultNetSnapshotResponseRawMessage>> snapshot)
            {
                Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            }

            public Func<CultMeshQueryContext, Task<CultNetSnapshotResponseRawMessage>> Snapshot { get; }
        }

        internal sealed class PeerSnapshotEndpointSource : CultMeshDocumentPublicationSource
        {
            public PeerSnapshotEndpointSource(string endpoint)
            {
                Endpoint = string.IsNullOrWhiteSpace(endpoint)
                    ? throw new ArgumentException("Value must be non-empty.", nameof(endpoint))
                    : endpoint;
            }

            public string Endpoint { get; }
        }

        internal sealed class PeerSnapshotClientSource : CultMeshDocumentPublicationSource
        {
            public PeerSnapshotClientSource(Func<ICultNetSchemaClient> createClient, string endpoint)
            {
                CreateClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
                Endpoint = string.IsNullOrWhiteSpace(endpoint)
                    ? throw new ArgumentException("Value must be non-empty.", nameof(endpoint))
                    : endpoint;
            }

            public Func<ICultNetSchemaClient> CreateClient { get; }

            public string Endpoint { get; }
        }
    }

    /// <summary>
    /// Describes one typed document to bind from a CultMesh publication source.
    /// </summary>
    public interface ICultMeshPublicationDocumentBinding
    {
        /// <summary>Gets the publication source override for this document, when it differs from the catalog source.</summary>
        CultMeshDocumentPublicationSource? Source { get; }

        /// <summary>Binds this document request to a publication-backed handle.</summary>
        ICultMeshDocumentHandle Bind(
            CultMeshDocumentPublicationSource source,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions,
            CultMeshPeerSnapshotDocumentOptions? peerOptions);

        /// <summary>Reads this publication document and hydrates it into a local node.</summary>
        Task<ICultMeshDocumentHandle> SyncAsync(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions,
            CultMeshPeerSnapshotDocumentOptions? peerOptions);
    }

    /// <summary>
    /// Describes one typed document to bind from a CultMesh publication source.
    /// </summary>
    public sealed class CultMeshPublicationDocumentBinding<TDocument> : ICultMeshPublicationDocumentBinding
        where TDocument : class
    {
        internal CultMeshPublicationDocumentBinding(
            CultRecordKey key,
            string? documentId,
            string? sourceId,
            CultMeshDocumentPublicationSource? source)
        {
            Key = key;
            DocumentId = documentId;
            SourceId = sourceId;
            Source = source;
        }

        /// <summary>Gets the record key to read.</summary>
        public CultRecordKey Key { get; }

        /// <summary>Gets the semantic document id. Defaults to the record key.</summary>
        public string? DocumentId { get; }

        /// <summary>Gets the source id advertised by diagnostics. Defaults to the document id or record key.</summary>
        public string? SourceId { get; }

        /// <inheritdoc />
        public CultMeshDocumentPublicationSource? Source { get; }

        /// <inheritdoc />
        public ICultMeshDocumentHandle Bind(
            CultMeshDocumentPublicationSource source,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions,
            CultMeshPeerSnapshotDocumentOptions? peerOptions)
        {
            return CultMesh.DocumentFromPublication<TDocument>(
                Source ?? source,
                Key,
                context,
                CultMesh.WithPublicationBindingOptions(storeOptions, Key, DocumentId, SourceId),
                CultMesh.WithPublicationBindingOptions(peerOptions, Key, DocumentId, SourceId));
        }

        /// <inheritdoc />
        public async Task<ICultMeshDocumentHandle> SyncAsync(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions,
            CultMeshPeerSnapshotDocumentOptions? peerOptions)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var publication = CultMesh.DocumentFromPublication<TDocument>(
                Source ?? source,
                Key,
                context,
                CultMesh.WithPublicationBindingOptions(storeOptions, Key, DocumentId, SourceId),
                CultMesh.WithPublicationBindingOptions(peerOptions, Key, DocumentId, SourceId));
            var local = CultMesh.Document<TDocument>(
                node,
                Key,
                context,
                string.IsNullOrWhiteSpace(DocumentId) ? Key.Value : DocumentId);
            await local.ReplaceAsync(await publication.LatestAsync().ConfigureAwait(false)).ConfigureAwait(false);
            return local;
        }
    }

    /// <summary>
    /// A locally hosted CultMesh node over CultCache, CultNet transport, and the mesh database facade.
    /// </summary>
    public sealed class CultMeshNode : IDisposable
    {
        private readonly CultNetHost _host;

        internal CultMeshNode(CultNetHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Gets the durable cache used by the node.
        /// </summary>
        public CultCache Cache => _host.Cache;

        /// <summary>
        /// Gets the canonical single-file MessagePack store, when one is attached.
        /// </summary>
        public SingleFileMessagePackBackingStore? Store => _host.Store;

        /// <summary>
        /// Gets the underlying CultNet server.
        /// </summary>
        public Server Server => _host.Server;

        /// <summary>
        /// Gets the distributed realtime database facade.
        /// </summary>
        public CultNetDatabase Database => _host.Database;

        /// <summary>
        /// Gets the schema-v0 server bridge for distributed database messages.
        /// </summary>
        public CultNetDatabaseServer DatabaseServer => _host.DatabaseServer;

        /// <summary>
        /// Starts the underlying server.
        /// </summary>
        public void Start()
        {
            _host.Start();
        }

        /// <summary>
        /// Flushes the durable cache.
        /// </summary>
        public Task FlushAsync(bool soft = false)
        {
            return _host.FlushAsync(soft);
        }

        /// <summary>
        /// Creates a typed document handle over one distributed record hosted by this node.
        /// </summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultRecordKey key,
            CultMeshVerseContext context,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            return CultMesh.Document<TDocument>(Database, key, context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle over one distributed record hosted by this node.
        /// </summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultRecordKey key,
            CultMeshVerse verse,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document<TDocument>(key, verse.Context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle over one distributed record hosted by this node.
        /// </summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string recordKey,
            CultMeshVerseContext context,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            return Document<TDocument>(new CultRecordKey(recordKey), context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle over one distributed record hosted by this node.
        /// </summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string recordKey,
            CultMeshVerse verse,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document<TDocument>(recordKey, verse.Context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a managed reactive document mirror over one distributed record hosted by this node.
        /// </summary>
        public Task<CultMeshReactiveDocument<TDocument>> ReactiveDocumentAsync<TDocument>(
            CultRecordKey key,
            CultMeshVerseContext context,
            CultMeshReactiveDocumentOptions? options = null)
            where TDocument : class
        {
            return Document<TDocument>(key, context).ReactiveAsync(options);
        }

        /// <summary>
        /// Creates a managed reactive document mirror over one distributed record hosted by this node.
        /// </summary>
        public Task<CultMeshReactiveDocument<TDocument>> ReactiveDocumentAsync<TDocument>(
            CultRecordKey key,
            CultMeshVerse verse,
            CultMeshReactiveDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return ReactiveDocumentAsync<TDocument>(key, verse.Context, options);
        }

        /// <summary>
        /// Creates a managed reactive document mirror over one distributed record hosted by this node.
        /// </summary>
        public Task<CultMeshReactiveDocument<TDocument>> ReactiveDocumentAsync<TDocument>(
            string recordKey,
            CultMeshVerseContext context,
            CultMeshReactiveDocumentOptions? options = null)
            where TDocument : class
        {
            return ReactiveDocumentAsync<TDocument>(new CultRecordKey(recordKey), context, options);
        }

        /// <summary>
        /// Creates a managed reactive document mirror over one distributed record hosted by this node.
        /// </summary>
        public Task<CultMeshReactiveDocument<TDocument>> ReactiveDocumentAsync<TDocument>(
            string recordKey,
            CultMeshVerse verse,
            CultMeshReactiveDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return ReactiveDocumentAsync<TDocument>(recordKey, verse.Context, options);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _host.Dispose();
        }
    }

    /// <summary>
    /// Parsed CultMesh RUDP endpoint.
    /// </summary>
    public sealed class CultMeshRudpEndpoint
    {
        /// <summary>
        /// Creates a parsed RUDP endpoint.
        /// </summary>
        public CultMeshRudpEndpoint(string host, int port, string uri)
        {
            Host = string.IsNullOrWhiteSpace(host)
                ? throw new ArgumentException("Host must be non-empty.", nameof(host))
                : host;
            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
            }

            Port = port;
            Uri = string.IsNullOrWhiteSpace(uri)
                ? throw new ArgumentException("Uri must be non-empty.", nameof(uri))
                : uri;
        }

        /// <summary>Gets the endpoint host.</summary>
        public string Host { get; }
        /// <summary>Gets the endpoint port.</summary>
        public int Port { get; }
        /// <summary>Gets the normalized rudp:// URI.</summary>
        public string Uri { get; }
    }

    /// <summary>
    /// Options for CultMesh-branded RUDP socket helpers.
    /// </summary>
    public sealed class CultMeshRudpSocketOptions
    {
        /// <summary>Gets or sets the local bind host.</summary>
        public string BindHost { get; set; } = "0.0.0.0";
        /// <summary>Gets or sets the local bind port.</summary>
        public int BindPort { get; set; }
        /// <summary>Gets or sets a caller-owned bound socket.</summary>
        public Socket? Socket { get; set; }
        /// <summary>Gets or sets the first local packet sequence.</summary>
        public uint InitialSequence { get; set; } = 1;
        /// <summary>Gets or sets the reliable resend delay in milliseconds.</summary>
        public long ResendDelayMs { get; set; } = 250;
        /// <summary>Gets or sets the advertised transport id.</summary>
        public string TransportId { get; set; } = "rudp";
        /// <summary>Gets or sets the advertised maximum payload size.</summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>Gets or sets the maximum RUDP fragment size.</summary>
        public int? MaxFragmentBytes { get; set; }
        /// <summary>Gets or sets the maximum pending reliable packet count.</summary>
        public int? MaxPendingReliablePackets { get; set; }
        /// <summary>Gets or sets the advertised reconnect policy.</summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
    }

    /// <summary>
    /// Options for CultMesh-branded connected RUDP client helpers.
    /// </summary>
    public sealed class CultMeshRudpClientOptions
    {
        /// <summary>Gets or sets the socket construction and transport profile options.</summary>
        public CultMeshRudpSocketOptions SocketOptions { get; set; } = new();
        /// <summary>Gets or sets the handshake payload.</summary>
        public byte[] ConnectPayload { get; set; } = Array.Empty<byte>();
        /// <summary>Gets or sets the handshake timeout.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Gets or sets the handshake polling interval.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(5);
    }

    /// <summary>
    /// Public CultMesh entrypoints.
    /// </summary>
    public static partial class CultMesh
    {
        /// <summary>
        /// Creates a local CultMesh node with a durable CultCache and the canonical MessagePack backing store.
        /// </summary>
        public static async Task<CultMeshNode> CreateNodeAsync(string cachePath, CultMeshNodeOptions? options = null)
        {
            var host = await CultNetLocal.CreateHostAsync(
                cachePath,
                (options ?? new CultMeshNodeOptions()).ToCultNetOptions(cachePath)).ConfigureAwait(false);
            return new CultMeshNode(host);
        }

        /// <summary>
        /// Creates and starts a local CultMesh node with development-friendly defaults.
        /// </summary>
        public static Task<CultMeshNode> StartNodeAsync(string cachePath, CultMeshNodeOptions? options = null)
        {
            options ??= new CultMeshNodeOptions();
            options.StartServer = true;
            return CreateNodeAsync(cachePath, options);
        }

        /// <summary>
        /// Creates a local reactive catalog for discovered Verses.
        /// </summary>
        public static CultMeshVerseCatalog CreateVerseCatalog()
        {
            return new CultMeshVerseCatalog();
        }

        /// <summary>
        /// Creates a local reactive catalog for discovered peers.
        /// </summary>
        public static CultMeshPeerCatalog CreatePeerCatalog()
        {
            return new CultMeshPeerCatalog();
        }

        /// <summary>
        /// Creates a local authority lease catalog.
        /// </summary>
        public static CultMeshAuthorityLeaseCatalog CreateAuthorityLeaseCatalog()
        {
            return new CultMeshAuthorityLeaseCatalog();
        }

        /// <summary>
        /// Creates a local stream catalog for zero-copy-oriented audio, video, tensor, and byte frame streams.
        /// </summary>
        public static CultMeshStreamCatalog CreateStreamCatalog()
        {
            return new CultMeshStreamCatalog();
        }

        /// <summary>
        /// Packs bytes into content-addressed CDN chunk documents and a versioned artifact manifest.
        /// </summary>
        public static CultMeshCdnArtifact PackCdnArtifact(
            string artifactId,
            byte[] payload,
            CultMeshCdnPackOptions? options = null)
        {
            return CultMeshCdn.PackArtifact(artifactId, payload, options);
        }

        /// <summary>
        /// Publishes a packed CDN artifact into a local cache.
        /// </summary>
        public static Task<CultMeshCdnArtifactManifest> PublishCdnArtifactAsync(
            CultCache cache,
            CultMeshCdnArtifact artifact)
        {
            return CultMeshCdn.PublishAsync(cache, artifact);
        }

        /// <summary>
        /// Publishes a packed CDN artifact into a distributed CultNet database.
        /// </summary>
        public static Task<CultMeshCdnArtifactManifest> PublishCdnArtifactAsync(
            CultNetDatabase database,
            CultMeshCdnArtifact artifact)
        {
            return CultMeshCdn.PublishAsync(database, artifact);
        }

        /// <summary>
        /// Reassembles and verifies a CDN artifact from a local cache.
        /// </summary>
        public static byte[] ReadCdnArtifact(
            CultCache cache,
            CultMeshCdnArtifactManifest manifest)
        {
            return CultMeshCdn.ReadArtifact(cache, manifest);
        }

        /// <summary>
        /// Reassembles and verifies a CDN artifact from a distributed CultNet database.
        /// </summary>
        public static Task<byte[]> ReadCdnArtifactAsync(
            CultNetDatabase database,
            CultMeshCdnArtifactManifest manifest)
        {
            return CultMeshCdn.ReadArtifactAsync(database, manifest);
        }

        /// <summary>
        /// Creates a CultNet document registry for CDN artifact manifests and chunks.
        /// </summary>
        public static CultNetDocumentRegistry CreateCdnDocumentRegistry(CultDocumentRegistry? documents = null)
        {
            return CultMeshCdn.CreateDocumentRegistry(documents);
        }

        /// <summary>
        /// Computes and stamps the content hash for a portable entity prefab package.
        /// </summary>
        public static CultMeshEntityPrefabPackage FinalizeEntityPrefabPackage(
            CultMeshEntityPrefabPackage package)
        {
            return CultMeshEntityPrefabs.FinalizePackage(package);
        }

        /// <summary>
        /// Publishes a portable entity prefab package into a local cache.
        /// </summary>
        public static Task<CultMeshEntityPrefabPackage> PublishEntityPrefabPackageAsync(
            CultCache cache,
            CultMeshEntityPrefabPackage package)
        {
            return CultMeshEntityPrefabs.PublishAsync(cache, package);
        }

        /// <summary>
        /// Publishes a portable entity prefab package into a distributed CultNet database.
        /// </summary>
        public static Task<CultMeshEntityPrefabPackage> PublishEntityPrefabPackageAsync(
            CultNetDatabase database,
            CultMeshEntityPrefabPackage package)
        {
            return CultMeshEntityPrefabs.PublishAsync(database, package);
        }

        /// <summary>
        /// Creates a CultNet document registry for portable entity prefab packages.
        /// </summary>
        public static CultNetDocumentRegistry CreateEntityPrefabDocumentRegistry(CultDocumentRegistry? documents = null)
        {
            return CultMeshEntityPrefabs.CreateDocumentRegistry(documents);
        }

        /// <summary>
        /// Creates a CultNet document registry for CDN assets and portable entity prefab packages.
        /// </summary>
        public static CultNetDocumentRegistry CreateAssetPipelineDocumentRegistry(CultDocumentRegistry? documents = null)
        {
            return CultMeshEntityPrefabs.CreateAssetPipelineDocumentRegistry(documents);
        }

        /// <summary>
        /// Creates a managed Verse context for generated domain sugar.
        /// </summary>
        public static CultMeshVerse Verse(
            string verseId,
            string runtimeId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshAuthorityClaim>? claims = null)
        {
            return new CultMeshVerse(new CultMeshVerseContext(verseId, runtimeId, routeHint, claims));
        }

        /// <summary>
        /// Creates a managed Verse context for generated domain sugar.
        /// </summary>
        public static Task<CultMeshVerse> ConnectVerseAsync(
            string verseId,
            string runtimeId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshAuthorityClaim>? claims = null)
        {
            return Task.FromResult(Verse(verseId, runtimeId, routeHint, claims));
        }

        /// <summary>
        /// Starts a fluent typed operation context for one runtime.
        /// </summary>
        public static CultMeshOperationContextBuilder OperationContextFor(string runtimeId)
        {
            return new CultMeshOperationContextBuilder(runtimeId);
        }

        /// <summary>
        /// Starts a fluent typed query context for one runtime.
        /// </summary>
        public static CultMeshQueryContextBuilder QueryContextFor(string runtimeId)
        {
            return new CultMeshQueryContextBuilder(runtimeId);
        }

        /// <summary>
        /// Binds a typed operation handle to a Verse context.
        /// </summary>
        public static CultMeshBoundOperationHandle<TRequest, TResponse> BindOperation<TRequest, TResponse>(
            CultMeshVerseContext context,
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            return new CultMeshBoundOperationHandle<TRequest, TResponse>(context, operation);
        }

        /// <summary>
        /// Binds a typed operation handle to a Verse.
        /// </summary>
        public static CultMeshBoundOperationHandle<TRequest, TResponse> BindOperation<TRequest, TResponse>(
            CultMeshVerse verse,
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindOperation(verse.Context, operation);
        }

        /// <summary>
        /// Binds a typed query surface to a Verse context.
        /// </summary>
        public static CultMeshBoundQuerySurface<TParameters, TResult> BindQuery<TParameters, TResult>(
            CultMeshVerseContext context,
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            return new CultMeshBoundQuerySurface<TParameters, TResult>(context, query);
        }

        /// <summary>
        /// Binds a typed query surface to a Verse.
        /// </summary>
        public static CultMeshBoundQuerySurface<TParameters, TResult> BindQuery<TParameters, TResult>(
            CultMeshVerse verse,
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindQuery(verse.Context, query);
        }

        /// <summary>
        /// Binds a typed live feed to a Verse context.
        /// </summary>
        public static CultMeshBoundLiveFeed<TParameters, TResult> BindLiveFeed<TParameters, TResult>(
            CultMeshVerseContext context,
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            return new CultMeshBoundLiveFeed<TParameters, TResult>(context, feed);
        }

        /// <summary>
        /// Binds a typed live feed to a Verse.
        /// </summary>
        public static CultMeshBoundLiveFeed<TParameters, TResult> BindLiveFeed<TParameters, TResult>(
            CultMeshVerse verse,
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindLiveFeed(verse.Context, feed);
        }

        /// <summary>
        /// Binds a typed document live feed to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerseContext context,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed)
            where TDocument : class
        {
            return new CultMeshDocumentHandle<TDocument>(BindLiveFeed(context, feed));
        }

        /// <summary>
        /// Binds a mutable typed document live feed to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerseContext context,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed,
            Func<TDocument, Task> replace)
            where TDocument : class
        {
            if (replace == null) throw new ArgumentNullException(nameof(replace));
            return new CultMeshDocumentHandle<TDocument>(BindLiveFeed(context, feed), replace);
        }

        /// <summary>
        /// Binds a mutable predicted typed document live feed to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerseContext context,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed,
            Func<TDocument, Task> replace,
            Func<TDocument, Task> submitPrediction)
            where TDocument : class
        {
            if (replace == null) throw new ArgumentNullException(nameof(replace));
            if (submitPrediction == null) throw new ArgumentNullException(nameof(submitPrediction));
            return new CultMeshDocumentHandle<TDocument>(BindLiveFeed(context, feed), replace, submitPrediction);
        }

        /// <summary>
        /// Binds a typed document live feed to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerse verse,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindDocument(verse.Context, feed);
        }

        /// <summary>
        /// Binds a mutable typed document live feed to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerse verse,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed,
            Func<TDocument, Task> replace)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindDocument(verse.Context, feed, replace);
        }

        /// <summary>
        /// Binds a mutable predicted typed document live feed to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerse verse,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed,
            Func<TDocument, Task> replace,
            Func<TDocument, Task> submitPrediction)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindDocument(verse.Context, feed, replace, submitPrediction);
        }

        /// <summary>
        /// Binds a typed state pointer to a Verse context.
        /// </summary>
        public static CultMeshBoundStatePointer<TValue> BindStatePointer<TValue>(
            CultMeshVerseContext context,
            CultMeshStatePointer<TValue> pointer)
        {
            return new CultMeshBoundStatePointer<TValue>(context, pointer);
        }

        /// <summary>
        /// Binds a typed state pointer to a Verse.
        /// </summary>
        public static CultMeshBoundStatePointer<TValue> BindStatePointer<TValue>(
            CultMeshVerse verse,
            CultMeshStatePointer<TValue> pointer)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindStatePointer(verse.Context, pointer);
        }

        /// <summary>
        /// Binds a mutable typed state pointer to a Verse context.
        /// </summary>
        public static CultMeshBoundMutableStatePointer<TValue> BindMutableStatePointer<TValue>(
            CultMeshVerseContext context,
            CultMeshMutableStatePointer<TValue> pointer)
        {
            return new CultMeshBoundMutableStatePointer<TValue>(context, pointer);
        }

        /// <summary>
        /// Binds a mutable typed state pointer to a Verse.
        /// </summary>
        public static CultMeshBoundMutableStatePointer<TValue> BindMutableStatePointer<TValue>(
            CultMeshVerse verse,
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindMutableStatePointer(verse.Context, pointer);
        }

        /// <summary>
        /// Creates a typed state pointer to Verse state.
        /// </summary>
        public static CultMeshStatePointer<TValue> StatePointer<TValue>(
            string pointerId,
            Func<Task<TValue?>> resolve,
            Func<R3.Observable<TValue>> watch,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a typed state pointer to Verse state that can resolve through a query context.
        /// </summary>
        public static CultMeshStatePointer<TValue> StatePointer<TValue>(
            string pointerId,
            Func<CultMeshQueryContext, Task<TValue?>> resolve,
            Func<CultMeshQueryContext, R3.Observable<TValue>> watch,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a mutable typed state pointer to Verse state.
        /// </summary>
        public static CultMeshMutableStatePointer<TValue> MutableStatePointer<TValue>(
            string pointerId,
            Func<Task<TValue?>> resolve,
            Func<R3.Observable<TValue>> watch,
            Func<TValue, Task> replace,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshMutableStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                replace,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a mutable typed state pointer to Verse state that can operate through a query context.
        /// </summary>
        public static CultMeshMutableStatePointer<TValue> MutableStatePointer<TValue>(
            string pointerId,
            Func<CultMeshQueryContext, Task<TValue?>> resolve,
            Func<CultMeshQueryContext, R3.Observable<TValue>> watch,
            Func<CultMeshQueryContext, TValue, Task> replace,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshMutableStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                replace,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a typed projection source descriptor.
        /// </summary>
        public static CultMeshProjectionSource ProjectionSource(
            string sourceId,
            string? schemaId = null,
            string? description = null)
        {
            return new CultMeshProjectionSource(sourceId, schemaId, description);
        }

        /// <summary>
        /// Creates a named state-reference resolver for portable UI/tool surfaces.
        /// </summary>
        public static CultMeshStateRefResolver StateRefResolver(
            string resolverId,
            Func<string, string?> resolve,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            return new CultMeshStateRefResolver(
                resolverId,
                (stateRef, _context) => resolve(stateRef),
                sources,
                routeHint);
        }

        /// <summary>
        /// Creates a named state-reference resolver for portable UI/tool surfaces.
        /// </summary>
        public static CultMeshStateRefResolver StateRefResolver(
            string resolverId,
            Func<string, CultMeshQueryContext, string?> resolve,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshStateRefResolver(resolverId, resolve, sources, routeHint);
        }

        /// <summary>
        /// Describes a state-reference resolver for catalogs and tools.
        /// </summary>
        public static CultMeshStateRefResolverDiagnostic DescribeStateRefResolver(
            CultMeshStateRefResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            return new CultMeshStateRefResolverDiagnostic(
                resolver.ResolverId,
                resolver.RouteHint,
                resolver.Sources);
        }

        /// <summary>
        /// Creates a UI/tool binding descriptor from a component property to a typed state pointer.
        /// </summary>
        public static CultMeshStateBindingDescriptor StateBinding<TValue>(
            string targetProp,
            CultMeshStatePointer<TValue> pointer)
        {
            return CultMeshStateBindingDescriptor.FromPointer(targetProp, pointer);
        }

        /// <summary>
        /// Creates a UI/tool binding descriptor from a component property to a mutable typed state pointer.
        /// </summary>
        public static CultMeshStateBindingDescriptor StateBinding<TValue>(
            string targetProp,
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return CultMeshStateBindingDescriptor.FromPointer(targetProp, pointer.AsStatePointer());
        }

        /// <summary>
        /// Creates a UI/tool binding descriptor from a component property to a state pointer id.
        /// </summary>
        public static CultMeshStateBindingDescriptor StateBinding(
            string targetProp,
            string pointerId,
            string? sourceId = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshStateBindingDescriptor(targetProp, pointerId, sourceId, schemaId, routeHint);
        }

        /// <summary>
        /// Creates transport-friendly fields from a state binding descriptor.
        /// </summary>
        public static CultMeshStateBindingRecord StateBindingRecord(CultMeshStateBindingDescriptor? binding)
        {
            return CultMeshStateBindingRecord.FromBinding(binding);
        }

        /// <summary>
        /// Creates transport-friendly state binding fields directly.
        /// </summary>
        public static CultMeshStateBindingRecord StateBindingRecord(
            string? targetProp,
            string? pointerId,
            string? sourceId = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null)
        {
            return new CultMeshStateBindingRecord(
                targetProp,
                pointerId,
                sourceId,
                schemaId,
                routeKind,
                routeDescription);
        }

        /// <summary>
        /// Creates a UI/tool command binding descriptor from a typed operation handle.
        /// </summary>
        public static CultMeshOperationBindingDescriptor OperationBinding<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation,
            string? label = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            return CultMeshOperationBindingDescriptor.FromOperation(operation, label, schemaId, routeHint);
        }

        /// <summary>
        /// Creates a UI/tool command binding descriptor from a typed operation id.
        /// </summary>
        public static CultMeshOperationBindingDescriptor OperationBinding(
            string operationId,
            string? label = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshOperationBindingDescriptor(operationId, label, schemaId, routeHint);
        }

        /// <summary>
        /// Creates transport-friendly fields from an operation binding descriptor.
        /// </summary>
        public static CultMeshOperationBindingRecord OperationBindingRecord(
            CultMeshOperationBindingDescriptor? binding)
        {
            return CultMeshOperationBindingRecord.FromBinding(binding);
        }

        /// <summary>
        /// Creates transport-friendly operation binding fields directly.
        /// </summary>
        public static CultMeshOperationBindingRecord OperationBindingRecord(
            string? operationId,
            string? label = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null)
        {
            return new CultMeshOperationBindingRecord(
                operationId,
                label,
                schemaId,
                routeKind,
                routeDescription);
        }

        /// <summary>
        /// Creates a concrete invocation descriptor from an advertised operation binding.
        /// </summary>
        public static CultMeshOperationInvocationDescriptor OperationInvocation(
            CultMeshOperationBindingDescriptor binding,
            string? idempotencyKey = null)
        {
            return CultMeshOperationInvocationDescriptor.FromBinding(binding, idempotencyKey);
        }

        /// <summary>
        /// Creates a concrete invocation descriptor from a typed operation handle.
        /// </summary>
        public static CultMeshOperationInvocationDescriptor OperationInvocation<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            return CultMeshOperationInvocationDescriptor.FromOperation(
                operation,
                schemaId,
                routeHint,
                idempotencyKey);
        }

        /// <summary>
        /// Creates a concrete invocation descriptor from a typed operation id.
        /// </summary>
        public static CultMeshOperationInvocationDescriptor OperationInvocation(
            string operationId,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            return new CultMeshOperationInvocationDescriptor(operationId, schemaId, routeHint, idempotencyKey);
        }

        /// <summary>
        /// Creates transport-friendly fields from a route hint.
        /// </summary>
        public static CultMeshRouteRecord RouteRecord(CultMeshRouteHint? routeHint)
        {
            return CultMeshRouteRecord.FromRoute(routeHint);
        }

        /// <summary>
        /// Creates transport-friendly route fields directly.
        /// </summary>
        public static CultMeshRouteRecord RouteRecord(string? kind, string? description = null)
        {
            return new CultMeshRouteRecord(kind, description);
        }

        /// <summary>
        /// Creates transport-friendly fields from a typed operation invocation.
        /// </summary>
        public static CultMeshOperationInvocationRecord OperationInvocationRecord(
            CultMeshOperationInvocationDescriptor? invocation,
            string? fallbackOperationId = null,
            string? fallbackSchemaId = null,
            CultMeshRouteHint? fallbackRouteHint = null,
            string? fallbackIdempotencyKey = null)
        {
            return CultMeshOperationInvocationRecord.FromInvocation(
                invocation,
                fallbackOperationId,
                fallbackSchemaId,
                fallbackRouteHint,
                fallbackIdempotencyKey);
        }

        /// <summary>
        /// Creates transport-friendly operation invocation fields directly.
        /// </summary>
        public static CultMeshOperationInvocationRecord OperationInvocationRecord(
            string? operationId,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null,
            string? idempotencyKey = null)
        {
            return new CultMeshOperationInvocationRecord(
                operationId,
                schemaId,
                routeKind,
                routeDescription,
                idempotencyKey);
        }

        /// <summary>
        /// Creates an empty shared operation payload.
        /// </summary>
        public static CultMeshOperationPayload OperationPayload()
        {
            return CultMeshOperationPayload.Empty;
        }

        /// <summary>
        /// Creates a shared operation payload from string-compatible fields.
        /// </summary>
        public static CultMeshOperationPayload OperationPayload(
            IEnumerable<KeyValuePair<string, string>>? fields)
        {
            return CultMeshOperationPayload.FromStrings(fields);
        }

        /// <summary>
        /// Creates a shared operation payload from key/value pairs.
        /// </summary>
        public static CultMeshOperationPayload OperationPayload(params (string Key, string Value)[] fields)
        {
            var pairs = fields?.Select(field => new KeyValuePair<string, string>(field.Key, field.Value));
            return CultMeshOperationPayload.FromStrings(pairs);
        }

        /// <summary>
        /// Creates a reusable projection recipe over typed source state.
        /// </summary>
        public static CultMeshProjectionRecipe<TParameters, TResult> ProjectionRecipe<TParameters, TResult>(
            string projectionId,
            IEnumerable<CultMeshProjectionSource> sources,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> project,
            CultMeshRouteHint? routeHint = null,
            Func<TParameters, CultMeshQueryContext, R3.Observable<TResult>>? watch = null)
        {
            return new CultMeshProjectionRecipe<TParameters, TResult>(
                projectionId,
                sources,
                project,
                routeHint,
                watch);
        }

        /// <summary>
        /// Creates a typed live feed surface for coherent client snapshots.
        /// </summary>
        public static CultMeshLiveFeed<TParameters, TResult> LiveFeed<TParameters, TResult>(
            string feedId,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> snapshot,
            Func<TParameters, CultMeshQueryContext, R3.Observable<TResult>>? watch = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshLiveFeed<TParameters, TResult>(
                feedId,
                snapshot,
                watch,
                sources,
                routeHint);
        }

        /// <summary>
        /// Creates a typed document handle from snapshot/watch delegates and binds it to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerseContext context,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (latest == null) throw new ArgumentNullException(nameof(latest));
            if (watch == null) throw new ArgumentNullException(nameof(watch));

            var feed = LiveFeed<CultMeshDocumentQueryParameters, TDocument>(
                documentId,
                (_parameters, queryContext) => latest(queryContext),
                (_parameters, queryContext) => watch(queryContext),
                sources,
                routeHint);
            return BindDocument(context, feed);
        }

        /// <summary>
        /// Creates a mutable typed document handle from snapshot/watch/replace delegates and binds it to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerseContext context,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            Func<TDocument, Task> replace,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (replace == null) throw new ArgumentNullException(nameof(replace));

            var feed = LiveFeed<CultMeshDocumentQueryParameters, TDocument>(
                documentId,
                (_parameters, queryContext) => latest(queryContext),
                (_parameters, queryContext) => watch(queryContext),
                sources,
                routeHint);
            return BindDocument(context, feed, replace);
        }

        /// <summary>
        /// Creates a mutable predicted typed document handle from snapshot/watch/replace/prediction delegates and binds it to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerseContext context,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            Func<TDocument, Task> replace,
            Func<TDocument, Task> submitPrediction,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (replace == null) throw new ArgumentNullException(nameof(replace));
            if (submitPrediction == null) throw new ArgumentNullException(nameof(submitPrediction));

            var feed = LiveFeed<CultMeshDocumentQueryParameters, TDocument>(
                documentId,
                (_parameters, queryContext) => latest(queryContext),
                (_parameters, queryContext) => watch(queryContext),
                sources,
                routeHint);
            return BindDocument(context, feed, replace, submitPrediction);
        }

        /// <summary>
        /// Creates a typed document handle from snapshot/watch delegates and binds it to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerse verse,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document(documentId, verse.Context, latest, watch, sources, routeHint);
        }

        /// <summary>
        /// Creates a mutable typed document handle from snapshot/watch/replace delegates and binds it to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerse verse,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            Func<TDocument, Task> replace,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document(documentId, verse.Context, latest, watch, replace, sources, routeHint);
        }

        /// <summary>
        /// Creates a mutable predicted typed document handle from snapshot/watch/replace/prediction delegates and binds it to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerse verse,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            Func<TDocument, Task> replace,
            Func<TDocument, Task> submitPrediction,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document(documentId, verse.Context, latest, watch, replace, submitPrediction, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle directly over one CultCache record.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultCache cache,
            CultRecordKey key,
            CultMeshVerseContext context,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var descriptor = cache.Registry.GetRequired<TDocument>();
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(key.Value, descriptor.SchemaId, "CultCache record") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "CultCache document");
            return Document<TDocument>(
                ResolveDocumentId(documentId, key),
                context,
                _ => Task.FromResult(ReadRequired<TDocument>(cache, key)),
                _ => cache.WatchRecord<TDocument>(key)
                    .Where(change => change.Document != null)
                    .Select(change => change.Document!),
                async value =>
                {
                    await cache.UpsertAsync(value, new CultRecordHandle<TDocument>(key)).ConfigureAwait(false);
                },
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed document handle directly over one CultCache record.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultCache cache,
            CultRecordKey key,
            CultMeshVerse verse,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document<TDocument>(cache, key, verse.Context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle directly over one CultCache record with a local Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultCache cache,
            CultRecordKey key,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            return Document<TDocument>(
                cache,
                key,
                Verse("local", "local", routeHint).Context,
                documentId,
                sources,
                routeHint);
        }

        /// <summary>
        /// Creates a read-only typed document handle over one CultCache backing-store publication.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromStore<TDocument>(
            CacheBackingStore store,
            CultRecordKey key,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? options = null)
            where TDocument : class
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var resolvedOptions = options ?? new CultMeshStoreDocumentOptions();
            var cache = new CultCache(resolvedOptions.Registry ?? CultDocumentRegistry.Shared);
            cache.AddBackingStore(store);

            var descriptor = cache.Registry.GetRequired<TDocument>();
            var documentId = ResolveDocumentId(resolvedOptions.DocumentId, key);
            var route = resolvedOptions.RouteHint ?? new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "CultCache backing store");
            var sources = new[]
            {
                ProjectionSource(
                    string.IsNullOrWhiteSpace(resolvedOptions.SourceId) ? key.Value : resolvedOptions.SourceId!,
                    descriptor.SchemaId,
                    "CultCache backing store")
            };
            Func<CultMeshQueryContext, Task<TDocument>> latest = async _ =>
            {
                await cache.PullAllBackingStoresAsync().ConfigureAwait(false);
                return ReadRequired<TDocument>(cache, key);
            };
            var watch = PollingQueryWatcher<CultMeshDocumentQueryParameters, TDocument>(
                async (_parameters, queryContext) => await latest(queryContext).ConfigureAwait(false),
                new CultMeshPollingWatchOptions<TDocument>(resolvedOptions.PollInterval));

            return Document<TDocument>(
                documentId,
                context,
                latest,
                queryContext => watch(CultMeshDocumentQueryParameters.Empty, queryContext),
                sources,
                route);
        }

        /// <summary>
        /// Creates a read-only typed document handle over one CultCache backing-store publication.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromStore<TDocument>(
            CacheBackingStore store,
            CultRecordKey key,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentFromStore<TDocument>(store, key, verse.Context, options);
        }

        /// <summary>
        /// Creates a read-only typed document handle over one single-file MessagePack CultCache publication.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromSingleFile<TDocument>(
            string path,
            CultRecordKey key,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? options = null)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value must be non-empty.", nameof(path));
            var resolvedOptions = options ?? new CultMeshStoreDocumentOptions();
            var fileOptions = new CultMeshStoreDocumentOptions
            {
                DocumentId = resolvedOptions.DocumentId,
                SourceId = resolvedOptions.SourceId,
                RouteHint = resolvedOptions.RouteHint ?? new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, path),
                PollInterval = resolvedOptions.PollInterval,
                Registry = resolvedOptions.Registry
            };
            return DocumentFromStore<TDocument>(
                new SingleFileMessagePackBackingStore(path),
                key,
                context,
                fileOptions);
        }

        /// <summary>
        /// Creates a read-only typed document handle over one single-file MessagePack CultCache publication.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromSingleFile<TDocument>(
            string path,
            CultRecordKey key,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentFromSingleFile<TDocument>(path, key, verse.Context, options);
        }

        /// <summary>
        /// Creates a typed document handle from a configured publication source.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPublication<TDocument>(
            CultMeshDocumentPublicationSource source,
            CultRecordKey key,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null)
            where TDocument : class
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (context == null) throw new ArgumentNullException(nameof(context));

            switch (source)
            {
                case CultMeshDocumentPublicationSource.StoreSource store:
                    return DocumentFromStore<TDocument>(store.BackingStore, key, context, storeOptions);
                case CultMeshDocumentPublicationSource.SingleFileSource file:
                    return DocumentFromSingleFile<TDocument>(file.Path, key, context, storeOptions);
                case CultMeshDocumentPublicationSource.PeerSnapshotSource snapshot:
                    return DocumentFromPeerSnapshot<TDocument>(snapshot.Snapshot, key.Value, context, peerOptions);
                case CultMeshDocumentPublicationSource.PeerSnapshotEndpointSource endpoint:
                    return DocumentFromPeerSnapshot<TDocument>(endpoint.Endpoint, key.Value, context, peerOptions);
                case CultMeshDocumentPublicationSource.PeerSnapshotClientSource client:
                    return DocumentFromPeerSnapshot<TDocument>(client.CreateClient, client.Endpoint, key.Value, context, peerOptions);
                default:
                    throw new NotSupportedException(
                        $"Unsupported CultMesh document publication source '{source.GetType().FullName}'.");
            }
        }

        /// <summary>
        /// Creates a typed document handle from a configured publication source.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPublication<TDocument>(
            CultMeshDocumentPublicationSource source,
            CultRecordKey key,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentFromPublication<TDocument>(source, key, verse.Context, storeOptions, peerOptions);
        }

        /// <summary>
        /// Describes one typed document to bind from a CultMesh publication source.
        /// </summary>
        public static CultMeshPublicationDocumentBinding<TDocument> PublicationDocument<TDocument>(
            CultRecordKey key,
            string? documentId = null,
            string? sourceId = null,
            CultMeshDocumentPublicationSource? source = null)
            where TDocument : class
        {
            return new CultMeshPublicationDocumentBinding<TDocument>(key, documentId, sourceId, source);
        }

        /// <summary>
        /// Describes one typed document to bind from a CultMesh publication source.
        /// </summary>
        public static CultMeshPublicationDocumentBinding<TDocument> PublicationDocument<TDocument>(
            string recordKey,
            string? documentId = null,
            string? sourceId = null,
            CultMeshDocumentPublicationSource? source = null)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            return PublicationDocument<TDocument>(new CultRecordKey(recordKey), documentId, sourceId, source);
        }

        /// <summary>
        /// Creates a schema-aware document catalog from one or more configured publication bindings.
        /// </summary>
        public static CultMeshDocumentCatalog DocumentsFromPublication(
            CultMeshDocumentPublicationSource source,
            IEnumerable<ICultMeshPublicationDocumentBinding> bindings,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            if (context == null) throw new ArgumentNullException(nameof(context));

            return Documents(bindings.Select(binding =>
            {
                if (binding == null) throw new ArgumentException("CultMesh publication bindings cannot contain null.", nameof(bindings));
                return binding.Bind(source, context, storeOptions, peerOptions);
            }).ToArray());
        }

        /// <summary>
        /// Creates a schema-aware document catalog from one or more configured publication bindings.
        /// </summary>
        public static CultMeshDocumentCatalog DocumentsFromPublication(
            CultMeshDocumentPublicationSource source,
            IEnumerable<ICultMeshPublicationDocumentBinding> bindings,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentsFromPublication(source, bindings, verse.Context, storeOptions, peerOptions);
        }

        /// <summary>
        /// Reads configured publication documents, hydrates them into a local node, and returns local handles as a catalog.
        /// </summary>
        public static async Task<CultMeshDocumentCatalog> SyncDocumentsFromPublicationAsync(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            IEnumerable<ICultMeshPublicationDocumentBinding> bindings,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null,
            bool flush = false)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var handles = await Task.WhenAll(bindings.Select(binding =>
            {
                if (binding == null) throw new ArgumentException("CultMesh publication bindings cannot contain null.", nameof(bindings));
                return binding.SyncAsync(node, source, context, storeOptions, peerOptions);
            })).ConfigureAwait(false);

            if (flush)
                await node.FlushAsync().ConfigureAwait(false);

            return Documents(handles);
        }

        /// <summary>
        /// Reads configured publication documents, hydrates them into a local node, and returns local handles as a catalog.
        /// </summary>
        public static Task<CultMeshDocumentCatalog> SyncDocumentsFromPublicationAsync(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            IEnumerable<ICultMeshPublicationDocumentBinding> bindings,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null,
            bool flush = false)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return SyncDocumentsFromPublicationAsync(
                node,
                source,
                bindings,
                verse.Context,
                storeOptions,
                peerOptions,
                flush);
        }

        /// <summary>
        /// Reads one typed document from a configured publication source and hydrates it into a local node.
        /// </summary>
        public static async Task<TDocument> SyncDocumentFromPublicationAsync<TDocument>(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            CultRecordKey key,
            CultMeshVerseContext context,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null,
            bool flush = false)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var document = await DocumentFromPublication<TDocument>(
                    source,
                    key,
                    context,
                    storeOptions,
                    peerOptions)
                .LatestAsync()
                .ConfigureAwait(false);

            await Document<TDocument>(node, key, context)
                .ReplaceAsync(document)
                .ConfigureAwait(false);

            if (flush)
                await node.FlushAsync().ConfigureAwait(false);

            return document;
        }

        /// <summary>
        /// Reads one typed document from a configured publication source and hydrates it into a local node.
        /// </summary>
        public static Task<TDocument> SyncDocumentFromPublicationAsync<TDocument>(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            CultRecordKey key,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null,
            bool flush = false)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return SyncDocumentFromPublicationAsync<TDocument>(
                node,
                source,
                key,
                verse.Context,
                storeOptions,
                peerOptions,
                flush);
        }

        /// <summary>
        /// Reads one typed document from a configured publication source and hydrates it into a local node.
        /// </summary>
        public static Task<TDocument> SyncDocumentFromPublicationAsync<TDocument>(
            CultMeshNode node,
            CultMeshDocumentPublicationSource source,
            string recordKey,
            CultMeshVerse verse,
            CultMeshStoreDocumentOptions? storeOptions = null,
            CultMeshPeerSnapshotDocumentOptions? peerOptions = null,
            bool flush = false)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            return SyncDocumentFromPublicationAsync<TDocument>(
                node,
                source,
                new CultRecordKey(recordKey),
                verse,
                storeOptions,
                peerOptions,
                flush);
        }

        internal static CultMeshStoreDocumentOptions WithPublicationBindingOptions(
            CultMeshStoreDocumentOptions? options,
            CultRecordKey key,
            string? documentId,
            string? sourceId)
        {
            return new CultMeshStoreDocumentOptions
            {
                DocumentId = string.IsNullOrWhiteSpace(documentId) ? key.Value : documentId,
                SourceId = string.IsNullOrWhiteSpace(sourceId)
                    ? (string.IsNullOrWhiteSpace(documentId) ? key.Value : documentId)
                    : sourceId,
                RouteHint = options?.RouteHint,
                PollInterval = options?.PollInterval ?? TimeSpan.FromMilliseconds(250),
                Registry = options?.Registry
            };
        }

        internal static CultMeshPeerSnapshotDocumentOptions WithPublicationBindingOptions(
            CultMeshPeerSnapshotDocumentOptions? options,
            CultRecordKey key,
            string? documentId,
            string? sourceId)
        {
            return new CultMeshPeerSnapshotDocumentOptions
            {
                DocumentId = string.IsNullOrWhiteSpace(documentId) ? key.Value : documentId,
                SourceId = string.IsNullOrWhiteSpace(sourceId)
                    ? (string.IsNullOrWhiteSpace(documentId) ? key.Value : documentId)
                    : sourceId,
                RouteHint = options?.RouteHint,
                ResponseTimeout = options?.ResponseTimeout ?? TimeSpan.FromSeconds(5),
                ConnectTimeout = options?.ConnectTimeout ?? TimeSpan.FromSeconds(5),
                PollInterval = options?.PollInterval ?? TimeSpan.FromMilliseconds(250),
                MessageIdPrefix = options?.MessageIdPrefix,
                SchemaIds = options?.SchemaIds,
                ShardId = options?.ShardId,
                ShardEpoch = options?.ShardEpoch,
                Security = options?.Security,
                ConfigureClient = options?.ConfigureClient,
                RudpRuntimeId = options?.RudpRuntimeId,
                RudpConnectionId = options?.RudpConnectionId ?? 0x43554c54,
                RudpConnectPayload = options?.RudpConnectPayload ?? "cultnet-schema-rudp",
                RudpMaxFragmentBytes = options?.RudpMaxFragmentBytes ?? 1024,
                RudpResendDelayMs = options?.RudpResendDelayMs ?? 25
            };
        }

        /// <summary>
        /// Creates a typed document handle directly over one distributed CultNet database record.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultNetDatabase database,
            CultRecordKey key,
            CultMeshVerseContext context,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(key.Value, descriptor.SchemaId, "CultNet database record") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.Automatic, "CultNet database document");
            return Document<TDocument>(
                ResolveDocumentId(documentId, key),
                context,
                async _ => await ReadDatabaseDocumentRequiredAsync<TDocument>(database, key).ConfigureAwait(false),
                _ => WatchDatabaseRecordAs<TDocument>(database, key),
                async value =>
                {
                    await PutDatabaseDocumentAsync(database, key, value, predicted: false).ConfigureAwait(false);
                },
                async value =>
                {
                    await PutDatabaseDocumentAsync(database, key, value, predicted: true).ConfigureAwait(false);
                },
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed document handle directly over one distributed CultNet database record.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultNetDatabase database,
            CultRecordKey key,
            CultMeshVerse verse,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document<TDocument>(database, key, verse.Context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle over one remote CultNet snapshot response provider.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPeerSnapshot<TDocument>(
            Func<CultMeshQueryContext, Task<CultNetSnapshotResponseRawMessage>> snapshot,
            string recordKey,
            CultMeshVerseContext context,
            CultMeshPeerSnapshotDocumentOptions? options = null)
            where TDocument : class
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Value must be non-empty.", nameof(recordKey));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var resolvedOptions = options ?? new CultMeshPeerSnapshotDocumentOptions();
            var documentId = string.IsNullOrWhiteSpace(resolvedOptions.DocumentId)
                ? recordKey
                : resolvedOptions.DocumentId!;
            var route = resolvedOptions.RouteHint ?? new CultMeshRouteHint(CultMeshLocalityKind.Network, "CultNet snapshot");
            var sources = new[]
            {
                ProjectionSource(
                    string.IsNullOrWhiteSpace(resolvedOptions.SourceId) ? recordKey : resolvedOptions.SourceId!,
                    descriptor.SchemaId,
                    "CultNet snapshot")
            };
            Func<CultMeshQueryContext, Task<TDocument>> latest = async queryContext =>
                ReadDocumentFromSnapshotResponse<TDocument>(
                    await snapshot(queryContext).ConfigureAwait(false),
                    descriptor.SchemaId,
                    recordKey);
            var watch = PollingQueryWatcher<CultMeshDocumentQueryParameters, TDocument>(
                async (_parameters, queryContext) => await latest(queryContext).ConfigureAwait(false),
                new CultMeshPollingWatchOptions<TDocument>(resolvedOptions.PollInterval));

            return Document<TDocument>(
                documentId,
                context,
                latest,
                queryContext => watch(CultMeshDocumentQueryParameters.Empty, queryContext),
                sources,
                route);
        }

        /// <summary>
        /// Creates a typed document handle over one remote CultNet snapshot response provider.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPeerSnapshot<TDocument>(
            Func<CultMeshQueryContext, Task<CultNetSnapshotResponseRawMessage>> snapshot,
            string recordKey,
            CultMeshVerse verse,
            CultMeshPeerSnapshotDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentFromPeerSnapshot<TDocument>(snapshot, recordKey, verse.Context, options);
        }

        /// <summary>
        /// Creates a typed document handle over one remote CultNet schema endpoint.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPeerSnapshot<TDocument>(
            string endpoint,
            string recordKey,
            CultMeshVerseContext context,
            CultMeshPeerSnapshotDocumentOptions? options = null)
            where TDocument : class
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            var resolvedOptions = options ?? new CultMeshPeerSnapshotDocumentOptions();
            return DocumentFromPeerSnapshot<TDocument>(
                queryContext => RequestPeerSnapshotAsync<TDocument>(
                    () => CultNetSchemaClients.CreateForEndpoint(endpoint, resolvedOptions.Security, resolvedOptions.ConfigureClient),
                    endpoint,
                    recordKey,
                    queryContext,
                    resolvedOptions),
                recordKey,
                context,
                resolvedOptions);
        }

        /// <summary>
        /// Creates a typed document handle over one remote CultNet schema endpoint.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPeerSnapshot<TDocument>(
            string endpoint,
            string recordKey,
            CultMeshVerse verse,
            CultMeshPeerSnapshotDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentFromPeerSnapshot<TDocument>(endpoint, recordKey, verse.Context, options);
        }

        /// <summary>
        /// Creates a typed document handle over one remote CultNet schema client factory.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPeerSnapshot<TDocument>(
            Func<ICultNetSchemaClient> createClient,
            string endpoint,
            string recordKey,
            CultMeshVerseContext context,
            CultMeshPeerSnapshotDocumentOptions? options = null)
            where TDocument : class
        {
            if (createClient == null) throw new ArgumentNullException(nameof(createClient));
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            var resolvedOptions = options ?? new CultMeshPeerSnapshotDocumentOptions();
            return DocumentFromPeerSnapshot<TDocument>(
                queryContext => RequestPeerSnapshotAsync<TDocument>(
                    createClient,
                    endpoint,
                    recordKey,
                    queryContext,
                    resolvedOptions),
                recordKey,
                context,
                resolvedOptions);
        }

        /// <summary>
        /// Creates a typed document handle over one remote CultNet schema client factory.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> DocumentFromPeerSnapshot<TDocument>(
            Func<ICultNetSchemaClient> createClient,
            string endpoint,
            string recordKey,
            CultMeshVerse verse,
            CultMeshPeerSnapshotDocumentOptions? options = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return DocumentFromPeerSnapshot<TDocument>(createClient, endpoint, recordKey, verse.Context, options);
        }

        /// <summary>
        /// Creates a typed document handle directly over one CultMesh node database record.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultMeshNode node,
            CultRecordKey key,
            CultMeshVerseContext context,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return Document<TDocument>(node.Database, key, context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed document handle directly over one CultMesh node database record.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultMeshNode node,
            CultRecordKey key,
            CultMeshVerse verse,
            string? documentId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document<TDocument>(node, key, verse.Context, documentId, sources, routeHint);
        }

        /// <summary>
        /// Creates a schema-aware catalog over typed document handles.
        /// </summary>
        public static CultMeshDocumentCatalog Documents(IEnumerable<ICultMeshDocumentHandle> documents)
        {
            return new CultMeshDocumentCatalog(documents);
        }

        /// <summary>
        /// Creates a schema-aware catalog over typed document handles.
        /// </summary>
        public static CultMeshDocumentCatalog Documents(params ICultMeshDocumentHandle[] documents)
        {
            return new CultMeshDocumentCatalog(documents);
        }

        /// <summary>
        /// Creates a schema-aware catalog over typed collection handles.
        /// </summary>
        public static CultMeshCollectionCatalog Collections(IEnumerable<ICultMeshCollectionHandle> collections)
        {
            return new CultMeshCollectionCatalog(collections);
        }

        /// <summary>
        /// Creates a schema-aware catalog over typed collection handles.
        /// </summary>
        public static CultMeshCollectionCatalog Collections(params ICultMeshCollectionHandle[] collections)
        {
            return new CultMeshCollectionCatalog(collections);
        }

        /// <summary>
        /// Reads two typed document handles and projects them into one caller-defined view.
        /// </summary>
        public static async Task<TResult> LatestAsync<TFirst, TSecond, TResult>(
            CultMeshDocumentHandle<TFirst> first,
            CultMeshDocumentHandle<TSecond> second,
            Func<TFirst, TSecond, TResult> project)
            where TFirst : class
            where TSecond : class
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (project == null) throw new ArgumentNullException(nameof(project));

            var firstTask = first.LatestAsync();
            var secondTask = second.LatestAsync();
            await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
            return project(
                await firstTask.ConfigureAwait(false),
                await secondTask.ConfigureAwait(false));
        }

        /// <summary>
        /// Reads three typed document handles and projects them into one caller-defined view.
        /// </summary>
        public static async Task<TResult> LatestAsync<TFirst, TSecond, TThird, TResult>(
            CultMeshDocumentHandle<TFirst> first,
            CultMeshDocumentHandle<TSecond> second,
            CultMeshDocumentHandle<TThird> third,
            Func<TFirst, TSecond, TThird, TResult> project)
            where TFirst : class
            where TSecond : class
            where TThird : class
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (third == null) throw new ArgumentNullException(nameof(third));
            if (project == null) throw new ArgumentNullException(nameof(project));

            var firstTask = first.LatestAsync();
            var secondTask = second.LatestAsync();
            var thirdTask = third.LatestAsync();
            await Task.WhenAll(firstTask, secondTask, thirdTask).ConfigureAwait(false);
            return project(
                await firstTask.ConfigureAwait(false),
                await secondTask.ConfigureAwait(false),
                await thirdTask.ConfigureAwait(false));
        }

        /// <summary>
        /// Creates a typed collection handle over all local CultCache records assignable to the document type.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> Collection<TDocument>(
            CultCache cache,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));

            var descriptor = cache.Registry.GetRequired<TDocument>();
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(CollectionId(collectionId, descriptor), descriptor.SchemaId, "CultCache collection") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "CultCache collection");
            return new CultMeshCollectionHandle<TDocument>(
                CollectionId(collectionId, descriptor),
                () => Task.FromResult<IReadOnlyList<TDocument>>(cache.GetAll<TDocument>().ToArray()),
                () => cache.Watch<TDocument>().Select(change => ToCollectionChange(change, descriptor.SchemaId)),
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed collection handle over the local CultCache record with the supplied CultName.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> CollectionByName<TDocument>(
            CultCache cache,
            string name,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value must be non-empty.", nameof(name));

            var descriptor = cache.Registry.GetRequired<TDocument>();
            var resolvedId = collectionId ?? $"{descriptor.SchemaId}:name:{name}";
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(resolvedId, descriptor.SchemaId, "CultCache named collection") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "CultCache named collection");
            return new CultMeshCollectionHandle<TDocument>(
                resolvedId,
                () => Task.FromResult<IReadOnlyList<TDocument>>(Optional(cache.GetByName<TDocument>(name))),
                () => cache.Watch<TDocument>()
                    .Where(change => MatchesName(cache, name, change))
                    .Select(change => ToCollectionChange(change, descriptor.SchemaId)),
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed collection handle over local CultCache records matched by an indexed value.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> CollectionByIndex<TDocument>(
            CultCache cache,
            string alias,
            string value,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Value must be non-empty.", nameof(alias));
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value must be non-empty.", nameof(value));

            var descriptor = cache.Registry.GetRequired<TDocument>();
            var resolvedId = collectionId ?? $"{descriptor.SchemaId}:index:{alias}:{value}";
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(resolvedId, descriptor.SchemaId, "CultCache indexed collection") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "CultCache indexed collection");
            return new CultMeshCollectionHandle<TDocument>(
                resolvedId,
                () => Task.FromResult<IReadOnlyList<TDocument>>(Optional(cache.GetByIndex<TDocument>(alias, value))),
                () => cache.Watch<TDocument>()
                    .Where(change => MatchesIndex(cache, alias, value, change))
                    .Select(change => ToCollectionChange(change, descriptor.SchemaId)),
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed collection handle over all distributed CultNet database records assignable to the document type.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> Collection<TDocument>(
            CultNetDatabase database,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(CollectionId(collectionId, descriptor), descriptor.SchemaId, "CultNet database collection") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.Automatic, "CultNet database collection");
            return new CultMeshCollectionHandle<TDocument>(
                CollectionId(collectionId, descriptor),
                () => Task.FromResult<IReadOnlyList<TDocument>>(database.GetAll<TDocument>().ToArray()),
                () => database.Watch<TDocument>().Select(ToCollectionChange),
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed collection handle over the distributed CultNet database record with the supplied CultName.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> CollectionByName<TDocument>(
            CultNetDatabase database,
            string name,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value must be non-empty.", nameof(name));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var resolvedId = collectionId ?? $"{descriptor.SchemaId}:name:{name}";
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(resolvedId, descriptor.SchemaId, "CultNet database named collection") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.Automatic, "CultNet database named collection");
            return new CultMeshCollectionHandle<TDocument>(
                resolvedId,
                () => Task.FromResult<IReadOnlyList<TDocument>>(Optional(database.GetByName<TDocument>(name))),
                () => database.WatchByName<TDocument>(name).Select(ToCollectionChange),
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed collection handle over distributed CultNet database records matched by an indexed value.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> CollectionByIndex<TDocument>(
            CultNetDatabase database,
            string alias,
            string value,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Value must be non-empty.", nameof(alias));
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value must be non-empty.", nameof(value));

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            var resolvedId = collectionId ?? $"{descriptor.SchemaId}:index:{alias}:{value}";
            var sourceList = sources?.ToArray()
                ?? new[] { ProjectionSource(resolvedId, descriptor.SchemaId, "CultNet database indexed collection") };
            var route = routeHint ?? new CultMeshRouteHint(CultMeshLocalityKind.Automatic, "CultNet database indexed collection");
            return new CultMeshCollectionHandle<TDocument>(
                resolvedId,
                () => Task.FromResult<IReadOnlyList<TDocument>>(Optional(database.GetByIndex<TDocument>(alias, value))),
                () => database.WatchByIndex<TDocument>(alias, value).Select(ToCollectionChange),
                sourceList,
                route);
        }

        /// <summary>
        /// Creates a typed collection handle over all CultMesh node database records assignable to the document type.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> Collection<TDocument>(
            CultMeshNode node,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return Collection<TDocument>(node.Database, collectionId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed collection handle over the CultMesh node database record with the supplied CultName.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> CollectionByName<TDocument>(
            CultMeshNode node,
            string name,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return CollectionByName<TDocument>(node.Database, name, collectionId, sources, routeHint);
        }

        /// <summary>
        /// Creates a typed collection handle over CultMesh node database records matched by an indexed value.
        /// </summary>
        public static CultMeshCollectionHandle<TDocument> CollectionByIndex<TDocument>(
            CultMeshNode node,
            string alias,
            string value,
            string? collectionId = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return CollectionByIndex<TDocument>(node.Database, alias, value, collectionId, sources, routeHint);
        }

        /// <summary>
        /// Describes a typed live feed surface for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshLiveFeedDiagnostic DescribeLiveFeed<TParameters, TResult>(
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            return new CultMeshLiveFeedDiagnostic(feed.FeedId, feed.RouteHint, feed.Sources);
        }

        /// <summary>
        /// Describes a typed query surface for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshQuerySurfaceDiagnostic DescribeQuerySurface<TParameters, TResult>(
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            return new CultMeshQuerySurfaceDiagnostic(query.QueryId, query.RouteHint, query.Sources);
        }

        /// <summary>
        /// Describes a typed operation handle for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshOperationHandleDiagnostic DescribeOperationHandle<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return new CultMeshOperationHandleDiagnostic(operation.OperationId);
        }

        /// <summary>
        /// Describes a typed state pointer for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshStatePointerDiagnostic DescribeStatePointer<TValue>(
            CultMeshStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshStatePointerDiagnostic(pointer.PointerId, pointer.RouteHint, pointer.Sources);
        }

        /// <summary>
        /// Describes a mutable typed state pointer for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshStatePointerDiagnostic DescribeStatePointer<TValue>(
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshStatePointerDiagnostic(pointer.PointerId, pointer.RouteHint, pointer.Sources);
        }

        /// <summary>
        /// Describes a native slice view for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshNativeSliceViewDiagnostic DescribeNativeSliceView(
            CultMeshNativeSliceViewDescriptor view)
        {
            return new CultMeshNativeSliceViewDiagnostic(view);
        }

        /// <summary>
        /// Describes a typed projection recipe for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshProjectionRecipeDiagnostic DescribeProjectionRecipe<TParameters, TResult>(
            CultMeshProjectionRecipe<TParameters, TResult> recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            return new CultMeshProjectionRecipeDiagnostic(recipe.ProjectionId, recipe.RouteHint, recipe.Sources);
        }

        /// <summary>
        /// Describes a typed query surface as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TParameters, TResult>(
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.Query, query.QueryId, query.RouteHint, query.Sources);
        }

        /// <summary>
        /// Describes a typed operation handle as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.Operation, operation.OperationId);
        }

        /// <summary>
        /// Describes a typed projection recipe as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TParameters, TResult>(
            CultMeshProjectionRecipe<TParameters, TResult> recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.ProjectionRecipe, recipe.ProjectionId, recipe.RouteHint, recipe.Sources);
        }

        /// <summary>
        /// Describes a typed live feed as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TParameters, TResult>(
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.LiveFeed, feed.FeedId, feed.RouteHint, feed.Sources);
        }

        /// <summary>
        /// Describes a typed document handle as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface(
            ICultMeshDocumentHandle document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return new CultMeshSurfaceDiagnostic(
                CultMeshSurfaceKind.Document,
                document.DocumentId,
                document.RouteHint,
                document.Sources);
        }

        /// <summary>
        /// Describes a typed collection handle as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TDocument>(
            CultMeshCollectionHandle<TDocument> collection)
            where TDocument : class
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            return new CultMeshSurfaceDiagnostic(
                CultMeshSurfaceKind.Collection,
                collection.CollectionId,
                collection.RouteHint,
                collection.Sources);
        }

        /// <summary>
        /// Describes a typed state pointer as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TValue>(
            CultMeshStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshSurfaceDiagnostic(
                CultMeshSurfaceKind.StatePointer,
                pointer.PointerId,
                pointer.RouteHint,
                pointer.Sources);
        }

        /// <summary>
        /// Describes a mutable typed state pointer as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TValue>(
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshSurfaceDiagnostic(
                CultMeshSurfaceKind.StatePointer,
                pointer.PointerId,
                pointer.RouteHint,
                pointer.Sources);
        }

        /// <summary>
        /// Describes a native slice view as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface(
            CultMeshNativeSliceViewDescriptor view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.NativeSliceView, view.ViewId, view.Route);
        }

        /// <summary>
        /// Creates a typed surface catalog diagnostic for tooling, UI, and generated bindings.
        /// </summary>
        public static CultMeshSurfaceCatalogDiagnostic DescribeSurfaceCatalog(
            string catalogId,
            IEnumerable<CultMeshSurfaceDiagnostic> surfaces)
        {
            return new CultMeshSurfaceCatalogDiagnostic(catalogId, surfaces);
        }

        /// <summary>
        /// Creates a kind-indexed surface catalog diagnostic for generated bindings, UI, and tools.
        /// </summary>
        public static CultMeshSurfaceCatalogIndexDiagnostic DescribeSurfaceCatalogIndex(
            CultMeshSurfaceCatalogDiagnostic catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            return catalog.IndexByKind();
        }

        /// <summary>
        /// Creates a timer-backed query watcher for runtimes that do not yet have native reactive transport.
        /// </summary>
        public static Func<TParameters, CultMeshQueryContext, R3.Observable<TResult>> PollingQueryWatcher<TParameters, TResult>(
            Func<TParameters, CultMeshQueryContext, Task<TResult>> sample,
            CultMeshPollingWatchOptions<TResult>? options = null)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            var resolvedOptions = options ?? new CultMeshPollingWatchOptions<TResult>();
            return (parameters, context) => R3.Observable.Create<TResult>(observer =>
            {
                var watcher = new CultMeshPollingWatcher<TParameters, TResult>(
                    sample,
                    parameters,
                    context,
                    resolvedOptions);
                var subscription = watcher.Observable.Subscribe(observer);
                return new CultMeshCompositeDisposable(subscription, watcher);
            });
        }

        private static async Task<CultNetSnapshotResponseRawMessage> RequestPeerSnapshotAsync<TDocument>(
            Func<ICultNetSchemaClient> createClient,
            string endpoint,
            string recordKey,
            CultMeshQueryContext context,
            CultMeshPeerSnapshotDocumentOptions options)
            where TDocument : class
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            return await FetchSnapshotAsync(
                    createClient,
                    endpoint,
                    ToSnapshotRequestOptions(
                        options,
                        context,
                        options.SchemaIds ?? new[] { descriptor.SchemaId },
                        new[] { recordKey }))
                .ConfigureAwait(false);
        }

        private static CultMeshSnapshotRequestOptions ToSnapshotRequestOptions(
            CultMeshPeerSnapshotDocumentOptions options,
            CultMeshQueryContext context,
            IReadOnlyList<string>? schemaIds,
            IReadOnlyList<string>? recordKeys)
        {
            return new CultMeshSnapshotRequestOptions
            {
                SchemaIds = schemaIds,
                RecordKeys = recordKeys,
                ShardId = options.ShardId,
                ShardEpoch = options.ShardEpoch,
                ResponseTimeout = options.ResponseTimeout,
                ConnectTimeout = options.ConnectTimeout,
                MessageIdPrefix = string.IsNullOrWhiteSpace(options.MessageIdPrefix)
                    ? $"cultmesh:{context.RuntimeId}:snapshot"
                    : options.MessageIdPrefix,
                Security = options.Security,
                ConfigureClient = options.ConfigureClient,
                RudpRuntimeId = string.IsNullOrWhiteSpace(options.RudpRuntimeId)
                    ? context.RuntimeId
                    : options.RudpRuntimeId,
                RudpConnectionId = options.RudpConnectionId,
                RudpConnectPayload = options.RudpConnectPayload,
                RudpMaxFragmentBytes = options.RudpMaxFragmentBytes,
                RudpResendDelayMs = options.RudpResendDelayMs
            };
        }

        private static TDocument ReadDocumentFromSnapshotResponse<TDocument>(
            CultNetSnapshotResponseRawMessage response,
            string schemaId,
            string recordKey)
            where TDocument : class
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            var record = response.Documents.FirstOrDefault(candidate =>
                    string.Equals(candidate.SchemaId, schemaId, StringComparison.Ordinal) &&
                    string.Equals(candidate.RecordKey, recordKey, StringComparison.Ordinal))
                ?? response.Documents.FirstOrDefault(candidate =>
                    string.Equals(candidate.RecordKey, recordKey, StringComparison.Ordinal) &&
                    TryDecodeSnapshotDocument(candidate, out TDocument? _))
                ?? response.Documents.FirstOrDefault(candidate =>
                    string.Equals(candidate.SchemaId, schemaId, StringComparison.Ordinal))
                ?? response.Documents.FirstOrDefault(candidate =>
                    string.Equals(candidate.RecordKey, recordKey, StringComparison.Ordinal));
            if (record == null)
            {
                throw new InvalidOperationException(
                    $"CultNet snapshot response '{response.MessageId}' did not contain schema '{schemaId}' record '{recordKey}'.");
            }

            if (!string.Equals(record.PayloadEncoding, "messagepack", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CultNet raw document payloadEncoding must be \"messagepack\", not \"{record.PayloadEncoding}\".");
            }

            return DecodeSnapshotDocument<TDocument>(record);
        }

        private static bool TryDecodeSnapshotDocument<TDocument>(
            CultNetRawDocumentRecord record,
            out TDocument? document)
            where TDocument : class
        {
            document = null;
            if (!string.Equals(record.PayloadEncoding, "messagepack", StringComparison.Ordinal))
                return false;

            try
            {
                document = DecodeSnapshotDocument<TDocument>(record);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static TDocument DecodeSnapshotDocument<TDocument>(CultNetRawDocumentRecord record)
            where TDocument : class
        {
            return (TDocument)CultDocumentMessagePackSerialization.DeserializeUntyped(
                typeof(TDocument),
                record.Payload);
        }

        /// <summary>
        /// Parses a rudp://host:port endpoint into its host/port parts.
        /// </summary>
        public static CultMeshRudpEndpoint ParseRudpEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("RUDP endpoint must use the rudp:// scheme.", nameof(endpoint));
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.Port > 65535)
            {
                throw new ArgumentException("RUDP endpoint must include a host and port.", nameof(endpoint));
            }

            var host = uri.Host;
            var uriHost = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
                ? $"[{host}]"
                : host;
            return new CultMeshRudpEndpoint(host, uri.Port, $"rudp://{uriHost}:{uri.Port}");
        }

        /// <summary>
        /// Opens one exact hot-body subscription and advertises every body plane this runtime can
        /// actually open. The provider derives locality and publishes only the selected plane.
        /// </summary>
        public static Task<IReadOnlyList<object>> SubscribeHotBodyAsync(
            CultNetDatabaseSubscriptionClient subscriptions,
            IEnumerable<CultMeshBodyTransportKind> supportedBodyTransports,
            CultMeshHotBodySubscription subscription,
            CultNetDatabaseSubscriptionDeliveryMode deliveryMode = CultNetDatabaseSubscriptionDeliveryMode.Live)
        {
            if (subscriptions == null) throw new ArgumentNullException(nameof(subscriptions));
            if (supportedBodyTransports == null) throw new ArgumentNullException(nameof(supportedBodyTransports));
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            var transports = supportedBodyTransports.Distinct().ToArray();
            if (transports.Length == 0)
                throw new ArgumentException("At least one readable body transport is required.", nameof(supportedBodyTransports));
            return subscriptions.SubscribeAsync(
                subscription.SubscriptionId,
                recordKeys: subscription.RecordKeys,
                schemaIds: subscription.SchemaIds,
                consumerRuntimeId: subscription.ConsumerRuntimeId,
                bodyIds: new[] { subscription.BodyId },
                supportedBodyTransports: transports.Select(value => value.ToString()),
                deliveryMode: deliveryMode);
        }

        /// <summary>Opens an exact hot-body subscription using a resolver's readable planes.</summary>
        public static Task<IReadOnlyList<object>> SubscribeHotBodyAsync(
            CultNetDatabaseSubscriptionClient subscriptions,
            CultMeshBodyPublicationResolver bodyResolver,
            CultMeshHotBodySubscription subscription,
            CultNetDatabaseSubscriptionDeliveryMode deliveryMode = CultNetDatabaseSubscriptionDeliveryMode.Live)
        {
            if (bodyResolver == null) throw new ArgumentNullException(nameof(bodyResolver));
            return SubscribeHotBodyAsync(subscriptions, bodyResolver.SupportedTransports, subscription, deliveryMode);
        }

        /// <summary>Resolves a CultMesh URI or RUDP endpoint into a concrete RUDP endpoint.</summary>
        public static CultMeshRudpEndpoint ResolveRudpEndpoint(string endpointOrUri)
        {
            if (string.IsNullOrWhiteSpace(endpointOrUri))
                throw new ArgumentException("Value must be non-empty.", nameof(endpointOrUri));
            if (!Uri.TryCreate(endpointOrUri, UriKind.Absolute, out var uri))
                return ParseRudpEndpoint(NormalizeBareRudpEndpoint(endpointOrUri));
            if (string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase))
                return ParseRudpEndpoint(endpointOrUri);
            if (!string.Equals(uri.Scheme, "cultmesh", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("RUDP endpoint must use the cultmesh:// or rudp:// scheme.", nameof(endpointOrUri));
            if (string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("CultMesh URI must include an authority.", nameof(endpointOrUri));

            var authorityKey = MakeCultMeshAuthorityEnvironmentKey(uri.Host);
            var endpoint = Environment.GetEnvironmentVariable($"CULTMESH_URI_{authorityKey}_RUDP") ??
                Environment.GetEnvironmentVariable($"{authorityKey}_CULTMESH_RUDP_ENDPOINT");
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException(
                    $"CultMesh URI '{endpointOrUri}' is unresolved. Set CULTMESH_URI_{authorityKey}_RUDP to a rudp:// endpoint.");
            return ParseRudpEndpoint(endpoint);
        }

        private static string NormalizeBareRudpEndpoint(string endpoint)
        {
            var trimmed = endpoint.Trim();
            return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"rudp://{trimmed}";
        }

        private static string MakeCultMeshAuthorityEnvironmentKey(string authority)
        {
            var chars = authority.Select(static value =>
                ((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9'))
                    ? char.ToUpperInvariant(value)
                    : '_');
            var key = string.Concat(chars).Trim('_');
            return string.IsNullOrWhiteSpace(key) ? "ODIN" : key;
        }

        /// <summary>
        /// Creates a CultNet RUDP server transport with CultMesh-branded defaults.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpServer(
            string runtimeId,
            uint connectionId,
            CultMeshRudpSocketOptions? options = null)
        {
            options ??= new CultMeshRudpSocketOptions();
            return new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = runtimeId,
                Socket = options.Socket ?? BindRudpSocket(options.BindHost, options.BindPort),
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = connectionId,
                InitialSequence = options.InitialSequence,
                ResendDelayMs = options.ResendDelayMs,
                TransportId = options.TransportId,
                MaxPayloadBytes = options.MaxPayloadBytes,
                MaxFragmentBytes = options.MaxFragmentBytes,
                MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                ReconnectPolicy = options.ReconnectPolicy
            });
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport for a parsed endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClient(
            string runtimeId,
            uint connectionId,
            CultMeshRudpEndpoint endpoint,
            CultMeshRudpSocketOptions? options = null)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            options ??= new CultMeshRudpSocketOptions();
            return new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = runtimeId,
                Socket = options.Socket ?? BindRudpSocket(options.BindHost, options.BindPort),
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = new IPEndPoint(ResolveRudpAddress(endpoint.Host), endpoint.Port),
                ConnectionId = connectionId,
                InitialSequence = options.InitialSequence,
                ResendDelayMs = options.ResendDelayMs,
                TransportId = options.TransportId,
                MaxPayloadBytes = options.MaxPayloadBytes,
                MaxFragmentBytes = options.MaxFragmentBytes,
                MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                ReconnectPolicy = options.ReconnectPolicy
            });
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport for a rudp://host:port endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClient(
            string runtimeId,
            uint connectionId,
            string endpoint,
            CultMeshRudpSocketOptions? options = null)
        {
            return CreateRudpClient(runtimeId, connectionId, ResolveRudpEndpoint(endpoint), options);
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport from a peer-card contact hint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClientForPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCard peer,
            CultMeshRudpSocketOptions? options = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var endpoint = peer.Endpoints.FirstOrDefault(value => value.StartsWith("rudp://", StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
            {
                throw new InvalidOperationException($"Peer {peer.PeerId} does not advertise a RUDP endpoint.");
            }

            return CreateRudpClient(runtimeId, connectionId, endpoint, options);
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport from the first peer authorized for a Verse role.
        /// </summary>
        [Obsolete("Supply CultMeshAuthorityResolver and an explicit authority epoch. This compatibility path denies unverifiable leases.")]
        public static CultNetRudpSocketTransportConnection CreateRudpClientForAuthorizedPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCatalog peers,
            CultMeshAuthorityLeaseCatalog leases,
            string verseId,
            string role,
            string? shardId = null,
            DateTimeOffset? at = null,
            CultMeshRudpSocketOptions? options = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (leases == null) throw new ArgumentNullException(nameof(leases));
            var resolver = CultMeshAuthorityResolver.CreateDenyByDefault(leases,
                at.HasValue ? new CultMeshFixedAuthorityClock(at.Value) : null);
            return CreateRudpClientForAuthorizedPeer(runtimeId, connectionId, peers, resolver, 0, verseId, role, shardId, null, options);
        }

        /// <summary>Creates a RUDP client from the first contact accepted by the authority resolver.</summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClientForAuthorizedPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCatalog peers,
            CultMeshAuthorityResolver authority,
            long authorityEpoch,
            string verseId,
            string role,
            string? shardId = null,
            string? resourceScope = null,
            CultMeshRudpSocketOptions? options = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (authority == null) throw new ArgumentNullException(nameof(authority));
            var peer = peers.FirstAuthorized(verseId, role, authority, authorityEpoch, shardId, resourceScope);
            if (peer == null)
            {
                throw new InvalidOperationException($"No authorized RUDP peer for role {role} in Verse {verseId}.");
            }

            return CreateRudpClientForPeer(runtimeId, connectionId, peer, options);
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport for a parsed endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClient(
            string runtimeId,
            uint connectionId,
            CultMeshRudpEndpoint endpoint,
            CultMeshRudpClientOptions? options = null)
        {
            options ??= new CultMeshRudpClientOptions();
            var client = CreateRudpClient(runtimeId, connectionId, endpoint, options.SocketOptions);
            if (!client.ConnectAndWait(options.ConnectPayload, options.ConnectTimeout, options.PollInterval))
            {
                client.Dispose();
                throw new TimeoutException($"Timed out waiting for RUDP client {runtimeId} to connect.");
            }

            return client;
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport for a rudp://host:port endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClient(
            string runtimeId,
            uint connectionId,
            string endpoint,
            CultMeshRudpClientOptions? options = null)
        {
            return ConnectRudpClient(runtimeId, connectionId, ParseRudpEndpoint(endpoint), options);
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport from a peer-card contact hint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClientForPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCard peer,
            CultMeshRudpClientOptions? options = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var endpoint = peer.Endpoints.FirstOrDefault(value => value.StartsWith("rudp://", StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
            {
                throw new InvalidOperationException($"Peer {peer.PeerId} does not advertise a RUDP endpoint.");
            }

            return ConnectRudpClient(runtimeId, connectionId, endpoint, options);
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport from the first peer authorized for a Verse role.
        /// </summary>
        [Obsolete("Supply CultMeshAuthorityResolver and an explicit authority epoch. This compatibility path denies unverifiable leases.")]
        public static CultNetRudpSocketTransportConnection ConnectRudpClientForAuthorizedPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCatalog peers,
            CultMeshAuthorityLeaseCatalog leases,
            string verseId,
            string role,
            string? shardId = null,
            DateTimeOffset? at = null,
            CultMeshRudpClientOptions? options = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (leases == null) throw new ArgumentNullException(nameof(leases));
            var resolver = CultMeshAuthorityResolver.CreateDenyByDefault(leases,
                at.HasValue ? new CultMeshFixedAuthorityClock(at.Value) : null);
            return ConnectRudpClientForAuthorizedPeer(runtimeId, connectionId, peers, resolver, 0, verseId, role, shardId, null, options);
        }

        /// <summary>Creates and handshakes a RUDP client from the first contact accepted by the authority resolver.</summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClientForAuthorizedPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCatalog peers,
            CultMeshAuthorityResolver authority,
            long authorityEpoch,
            string verseId,
            string role,
            string? shardId = null,
            string? resourceScope = null,
            CultMeshRudpClientOptions? options = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (authority == null) throw new ArgumentNullException(nameof(authority));
            var peer = peers.FirstAuthorized(verseId, role, authority, authorityEpoch, shardId, resourceScope);
            if (peer == null)
            {
                throw new InvalidOperationException($"No authorized RUDP peer for role {role} in Verse {verseId}.");
            }

            return ConnectRudpClientForPeer(runtimeId, connectionId, peer, options);
        }

        private sealed class CultMeshFixedAuthorityClock : ICultMeshClock
        {
            public CultMeshFixedAuthorityClock(DateTimeOffset utcNow) => UtcNow = utcNow;
            public DateTimeOffset UtcNow { get; }
            public System.Threading.Tasks.Task DelayAsync(TimeSpan delay, System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// Creates a committer for writing quorum simulation facts to a mesh database.
        /// </summary>
        public static CultMeshSimulationFactCommitter CreateSimulationFactCommitter(CultNetDatabase database)
        {
            return new CultMeshSimulationFactCommitter(database);
        }

        /// <summary>
        /// Creates a gameplay-facing session facade over a CultMesh node.
        /// </summary>
        public static CultMeshGameSession CreateGameSession(
            CultMeshNode node,
            CultMeshGameSessionOptions? options = null)
        {
            return new CultMeshGameSession(node, options);
        }

        /// <summary>
        /// Attaches Verse discovery responses to a CultMesh node.
        /// </summary>
        public static CultMeshVerseDiscoveryServer ServeVerseCatalog(
            CultMeshNode node,
            CultMeshVerseCatalog catalog)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return new CultMeshVerseDiscoveryServer(node.Server, catalog);
        }

        /// <summary>
        /// Creates a client for fetching Verse catalogs from discovery endpoints.
        /// </summary>
        public static CultMeshVerseDiscoveryClient CreateVerseDiscoveryClient(
            CultMeshVerseDiscoveryClientOptions? options = null)
        {
            return new CultMeshVerseDiscoveryClient(options);
        }

        /// <summary>
        /// Attaches peer exchange responses to a CultMesh node.
        /// </summary>
        public static CultMeshPeerExchangeServer ServePeerExchange(
            CultMeshNode node,
            CultMeshPeerCatalog catalog)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return new CultMeshPeerExchangeServer(node.Server, catalog);
        }

        /// <summary>
        /// Creates a client for fetching peer cards from peer exchange endpoints.
        /// </summary>
        public static CultMeshPeerExchangeClient CreatePeerExchangeClient(
            CultMeshPeerExchangeClientOptions? options = null)
        {
            return new CultMeshPeerExchangeClient(options);
        }

        /// <summary>
        /// Creates a CultNet client configured for CultMesh use.
        /// </summary>
        public static Client CreateClient(ClientSecurityOptions? security = null, Action<Client>? configureClient = null)
        {
            return CultNetLocal.CreateClient(security, configureClient);
        }

        /// <summary>
        /// Creates and connects a CultNet client to a CultMesh node.
        /// </summary>
        public static Client ConnectClient(
            string host = "localhost",
            int port = 3075,
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            return CultNetLocal.ConnectClient(host, port, security, configureClient);
        }

        private static Socket BindRudpSocket(string host, int port)
        {
            var address = ResolveRudpAddress(host);
            var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(address, port));
            socket.ReceiveTimeout = 20;
            return socket;
        }

        private static IPAddress ResolveRudpAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Value must be non-empty.", nameof(host));
            if (IPAddress.TryParse(host, out var parsed))
            {
                return parsed;
            }

            return Dns.GetHostAddresses(host).FirstOrDefault()
                   ?? throw new InvalidOperationException($"Could not resolve RUDP host {host}.");
        }

        private static string ResolveDocumentId(string? documentId, CultRecordKey key)
        {
            return string.IsNullOrWhiteSpace(documentId)
                ? key.Value
                : documentId!;
        }

        private static string CollectionId(string? collectionId, CultDocumentDescriptor descriptor)
        {
            return string.IsNullOrWhiteSpace(collectionId)
                ? descriptor.SchemaId
                : collectionId!;
        }

        private static IReadOnlyList<TDocument> Optional<TDocument>(TDocument? document)
            where TDocument : class
        {
            return document == null
                ? Array.Empty<TDocument>()
                : new[] { document };
        }

        private static bool MatchesName<TDocument>(
            CultCache cache,
            string name,
            CultCacheDocumentChange<TDocument> change)
            where TDocument : class
        {
            var current = cache.GetByName<TDocument>(name);
            return (change.Document != null && ReferenceEquals(current, change.Document)) ||
                   (change.PreviousDocument != null && ReferenceEquals(current, change.PreviousDocument));
        }

        private static bool MatchesIndex<TDocument>(
            CultCache cache,
            string alias,
            string value,
            CultCacheDocumentChange<TDocument> change)
            where TDocument : class
        {
            var current = cache.GetByIndex<TDocument>(alias, value);
            return (change.Document != null && ReferenceEquals(current, change.Document)) ||
                   (change.PreviousDocument != null && ReferenceEquals(current, change.PreviousDocument));
        }

        private static CultMeshCollectionChange<TDocument> ToCollectionChange<TDocument>(
            CultCacheDocumentChange<TDocument> change,
            string schemaId)
            where TDocument : class
        {
            return new CultMeshCollectionChange<TDocument>(
                change.Kind switch
                {
                    CultCacheDocumentChangeKind.Added => CultMeshCollectionChangeKind.Added,
                    CultCacheDocumentChangeKind.Updated => CultMeshCollectionChangeKind.Updated,
                    CultCacheDocumentChangeKind.Removed => CultMeshCollectionChangeKind.Removed,
                    _ => CultMeshCollectionChangeKind.Updated
                },
                change.Key,
                schemaId,
                change.Document,
                change.PreviousDocument);
        }

        private static CultMeshCollectionChange<TDocument> ToCollectionChange<TDocument>(
            CultNetDatabaseChange<TDocument> change)
            where TDocument : class
        {
            return new CultMeshCollectionChange<TDocument>(
                change.Kind switch
                {
                    CultNetDatabaseChangeKind.Added => CultMeshCollectionChangeKind.Added,
                    CultNetDatabaseChangeKind.Updated => CultMeshCollectionChangeKind.Updated,
                    CultNetDatabaseChangeKind.Removed => CultMeshCollectionChangeKind.Removed,
                    CultNetDatabaseChangeKind.Predicted => CultMeshCollectionChangeKind.Predicted,
                    CultNetDatabaseChangeKind.Reconciled => CultMeshCollectionChangeKind.Reconciled,
                    CultNetDatabaseChangeKind.SchemaMigrated => CultMeshCollectionChangeKind.SchemaMigrated,
                    CultNetDatabaseChangeKind.Rejected => CultMeshCollectionChangeKind.Rejected,
                    _ => CultMeshCollectionChangeKind.Updated
                },
                change.Key,
                change.SchemaId,
                change.Document,
                change.PreviousDocument,
                change.Message);
        }

        private static TDocument ReadRequired<TDocument>(CultCache cache, CultRecordKey key)
            where TDocument : class
        {
            var document = cache.Get<TDocument>(key);
            if (document != null)
                return document;

            var untyped = cache.Get(key);
            if (untyped != null && IsSameCultDocumentSchema<TDocument>(untyped.GetType()))
                return ConvertUntypedDocument<TDocument>(untyped);

            throw new KeyNotFoundException(
                $"CultMesh document '{key.Value}' was not found as {typeof(TDocument).FullName}.");
        }

        private static async Task<TDocument> ReadRequiredAsync<TDocument>(
            CultNetDatabase database,
            CultRecordKey key)
            where TDocument : class
        {
            var document = await database.GetAsync<TDocument>(key).ConfigureAwait(false);
            return document
                   ?? throw new KeyNotFoundException(
                       $"CultMesh document '{key.Value}' was not found as {typeof(TDocument).FullName}.");
        }

        private static async Task<TDocument> ReadDatabaseDocumentRequiredAsync<TDocument>(
            CultNetDatabase database,
            CultRecordKey key)
            where TDocument : class
        {
            var document = await database.GetAsync<TDocument>(key).ConfigureAwait(false);
            if (document != null)
                return document;

            var untyped = database.Cache.Get(key);
            if (untyped != null && IsSameCultDocumentSchema<TDocument>(untyped.GetType()))
                return ConvertUntypedDocument<TDocument>(untyped);

            throw new KeyNotFoundException(
                $"CultMesh document '{key.Value}' was not found as {typeof(TDocument).FullName}.");
        }

        private static R3.Observable<TDocument> WatchDatabaseRecordAs<TDocument>(
            CultNetDatabase database,
            CultRecordKey key)
            where TDocument : class
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            return database.WatchAllChanges()
                .Where(change => TryConvertDatabaseChange(change, key, descriptor, out TDocument? _))
                .Select(change =>
                {
                    TryConvertDatabaseChange(change, key, descriptor, out TDocument? document);
                    return document!;
                });
        }

        private static bool TryConvertDatabaseChange<TDocument>(
            object change,
            CultRecordKey key,
            CultDocumentDescriptor descriptor,
            out TDocument? document)
            where TDocument : class
        {
            document = null;
            if (change == null) return false;

            var changeType = change.GetType();
            if (changeType.GetProperty("Key")?.GetValue(change) is not CultRecordKey changedKey ||
                !changedKey.Equals(key))
            {
                return false;
            }

            var current = changeType.GetProperty("Document")?.GetValue(change);
            if (current == null)
                return false;

            var schemaId = changeType.GetProperty("SchemaId")?.GetValue(change) as string;
            if (!IsSameCultDocumentSchema(current.GetType(), descriptor, schemaId))
                return false;

            document = current as TDocument ?? ConvertUntypedDocument<TDocument>(current);
            return true;
        }

        private static bool IsSameCultDocumentSchema<TDocument>(Type documentType)
            where TDocument : class
        {
            return IsSameCultDocumentSchema(
                documentType,
                CultDocumentRegistry.Shared.GetRequired<TDocument>());
        }

        private static bool IsSameCultDocumentSchema(
            Type documentType,
            CultDocumentDescriptor descriptor,
            string? schemaId = null)
        {
            if (!string.IsNullOrWhiteSpace(schemaId) &&
                string.Equals(schemaId, descriptor.SchemaId, StringComparison.Ordinal))
            {
                return true;
            }

            CultDocumentDescriptor storedDescriptor;
            try
            {
                storedDescriptor = CultDocumentRegistry.Shared.GetRequired(documentType);
            }
            catch (Exception)
            {
                return false;
            }

            return string.Equals(storedDescriptor.SchemaName, descriptor.SchemaName, StringComparison.Ordinal) &&
                   string.Equals(storedDescriptor.SchemaVersion, descriptor.SchemaVersion, StringComparison.Ordinal);
        }

        private static TDocument ConvertUntypedDocument<TDocument>(object document)
            where TDocument : class
        {
            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType());
            return (TDocument)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(TDocument), payload);
        }

        private static object ConvertUntypedDocument(object document, Type targetType)
        {
            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType());
            return CultDocumentMessagePackSerialization.DeserializeUntyped(targetType, payload);
        }

        private static async Task PutDatabaseDocumentAsync<TDocument>(
            CultNetDatabase database,
            CultRecordKey key,
            TDocument value,
            bool predicted)
            where TDocument : class
        {
            var stored = database.Cache.Get(key);
            object outgoing = value;
            var outgoingType = typeof(TDocument);
            if (stored != null &&
                !outgoingType.IsInstanceOfType(stored) &&
                IsSameCultDocumentSchema(stored.GetType(), CultDocumentRegistry.Shared.GetRequired<TDocument>()))
            {
                outgoing = ConvertUntypedDocument(value, stored.GetType());
                outgoingType = stored.GetType();
            }

            var method = typeof(CultNetDatabase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(candidate => candidate.Name == (predicted
                    ? nameof(CultNetDatabase.PutPredictedAsync)
                    : nameof(CultNetDatabase.PutAsync)))
                .Single(candidate =>
                    candidate.IsGenericMethodDefinition &&
                    candidate.GetParameters() is { Length: 2 } parameters &&
                    parameters[0].ParameterType == typeof(CultRecordKey));

            var task = (Task)method
                .MakeGenericMethod(outgoingType)
                .Invoke(database, new object[] { key, outgoing })!;
            await task.ConfigureAwait(false);
        }
    }
}
