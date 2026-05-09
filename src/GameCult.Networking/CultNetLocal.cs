using System;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Options for creating a local CultNet host over CultCache and the canonical MessagePack store.
    /// </summary>
    public sealed class CultNetHostOptions
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
        /// Gets or sets an optional callback used to customize the server before start.
        /// </summary>
        public Action<Server>? ConfigureServer { get; set; }
    }

    /// <summary>
    /// Convenience wrapper for a locally hosted CultNet server over a durable CultCache.
    /// </summary>
    public sealed class CultNetHost : IDisposable
    {
        internal CultNetHost(CultCache cache, SingleFileMessagePackBackingStore? store, Server server)
        {
            Cache = cache;
            Store = store;
            Server = server;
        }

        /// <summary>
        /// Gets the durable cache used by the host.
        /// </summary>
        public CultCache Cache { get; }

        /// <summary>
        /// Gets the canonical single-file MessagePack store, when one is attached.
        /// </summary>
        public SingleFileMessagePackBackingStore? Store { get; }

        /// <summary>
        /// Gets the underlying CultNet server.
        /// </summary>
        public Server Server { get; }

        /// <summary>
        /// Starts the server.
        /// </summary>
        public void Start()
        {
            Server.Start();
        }

        /// <summary>
        /// Flushes the durable cache.
        /// </summary>
        public Task FlushAsync(bool soft = false)
        {
            return Cache.FlushAsync(soft);
        }

        /// <summary>
        /// Stops the server and disposes the host resources.
        /// </summary>
        public void Dispose()
        {
            Server.Dispose();
            Cache.Dispose();
        }
    }

    /// <summary>
    /// Friendly local entrypoints for CultNet host/client creation.
    /// </summary>
    public static class CultNetLocal
    {
        /// <summary>
        /// Creates a local host with a durable CultCache and the canonical MessagePack backing store.
        /// </summary>
        public static async Task<CultNetHost> CreateHostAsync(string cachePath, CultNetHostOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                throw new ArgumentException("Cache path must be non-empty.", nameof(cachePath));
            }

            options ??= new CultNetHostOptions();
            var cache = await CultCacheMessagePack.OpenAsync(cachePath, options.CacheOptions).ConfigureAwait(false);
            var store = cache.BackingStores.OfType<SingleFileMessagePackBackingStore>().FirstOrDefault();
            var server = new Server(cache, options.Security ?? ServerSecurityOptions.Development());
            options.ConfigureServer?.Invoke(server);

            var host = new CultNetHost(cache, store, server);
            if (options.StartServer)
            {
                host.Start();
            }

            return host;
        }

        /// <summary>
        /// Creates and starts a local host with development-friendly defaults.
        /// </summary>
        public static Task<CultNetHost> StartHostAsync(string cachePath, CultNetHostOptions? options = null)
        {
            options ??= new CultNetHostOptions();
            options.StartServer = true;
            return CreateHostAsync(cachePath, options);
        }

        /// <summary>
        /// Creates a client configured for local CultNet use.
        /// </summary>
        public static Client CreateClient(ClientSecurityOptions? security = null, Action<Client>? configureClient = null)
        {
            var client = new Client(security ?? ClientSecurityOptions.Development());
            configureClient?.Invoke(client);
            return client;
        }

        /// <summary>
        /// Creates and connects a client to the specified host.
        /// </summary>
        public static Client ConnectClient(
            string host = "localhost",
            int port = 3075,
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            var client = CreateClient(security, configureClient);
            client.Connect(host, port);
            return client;
        }

        /// <summary>
        /// Creates and connects a client to a local development host.
        /// </summary>
        public static Client ConnectLocal(
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            return ConnectClient("localhost", 3075, security, configureClient);
        }
    }
}
