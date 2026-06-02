using System;
using System.IO;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;

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

        /// <inheritdoc />
        public void Dispose()
        {
            _host.Dispose();
        }
    }

    /// <summary>
    /// Public CultMesh entrypoints.
    /// </summary>
    public static class CultMesh
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
    }
}
