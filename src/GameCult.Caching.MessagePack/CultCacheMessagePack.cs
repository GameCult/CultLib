using System;
using System.IO;
using System.Threading.Tasks;

namespace GameCult.Caching.MessagePack
{
    /// <summary>
    /// Options for opening a CultCache over the canonical single-file MessagePack backing store.
    /// </summary>
    public sealed class CultCacheOpenOptions
    {
        /// <summary>
        /// Gets or sets the document registry to use. When omitted, the shared registry is used.
        /// </summary>
        public CultDocumentRegistry? Registry { get; set; }

        /// <summary>
        /// Gets or sets whether the cache should pull the existing snapshot during open.
        /// </summary>
        public bool PullOnOpen { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the cache should flush attached stores when disposed.
        /// </summary>
        public bool FlushOnDispose { get; set; }

        /// <summary>
        /// Gets or sets whether the backing store should flush when disposed.
        /// </summary>
        public bool StoreFlushOnDispose { get; set; }

        /// <summary>
        /// Gets or sets an optional callback used to customize the cache before opening.
        /// </summary>
        public Action<CultCache>? ConfigureCache { get; set; }

        /// <summary>
        /// Gets or sets an optional callback used to customize the backing store before opening.
        /// </summary>
        public Action<SingleFileMessagePackBackingStore>? ConfigureStore { get; set; }

        /// <summary>
        /// Gets or sets whether the cache should use a paged directory store instead of one whole-file snapshot.
        /// </summary>
        public bool UseDirectoryStore { get; set; }

        /// <summary>
        /// Gets or sets the directory used for paged records. When omitted, the store uses the file path plus ".records".
        /// </summary>
        public string? DirectoryStorePath { get; set; }

        /// <summary>
        /// Gets or sets the records to hydrate when opening a paged directory store.
        /// The directory manifest is always read; rejected record payloads are never opened.
        /// </summary>
        public Func<CultPersistedRecordMetadata, bool>? DirectoryStoreHydrationFilter { get; set; }

        /// <summary>
        /// Gets or sets an optional callback used to customize the directory backing store before opening.
        /// </summary>
        public Action<DirectoryMessagePackBackingStore>? ConfigureDirectoryStore { get; set; }
    }

    /// <summary>
    /// Friendly MessagePack entrypoints for opening a CultCache with the canonical backing store.
    /// </summary>
    public static class CultCacheMessagePack
    {
        /// <summary>
        /// Creates a cache with the canonical single-file MessagePack backing store attached.
        /// </summary>
        public static CultCache Create(string filePath, CultCacheOpenOptions? options = null)
        {
            options ??= new CultCacheOpenOptions();
            return CreateCore(filePath, options, initializeGlobals: true);
        }

        /// <summary>
        /// Creates a cache with the canonical single-file MessagePack backing store attached and optionally pulls the on-disk snapshot.
        /// </summary>
        public static async Task<CultCache> OpenAsync(string filePath, CultCacheOpenOptions? options = null)
        {
            options ??= new CultCacheOpenOptions();
            var cache = CreateCore(filePath, options, initializeGlobals: false);
            if (options.PullOnOpen)
            {
                await cache.PullAllBackingStoresAsync().ConfigureAwait(false);
            }

            cache.MaterializeMissingGlobals();

            return cache;
        }

        private static CultCache CreateCore(
            string filePath,
            CultCacheOpenOptions options,
            bool initializeGlobals)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must be non-empty.", nameof(filePath));
            }

            var cache = new CultCache(options.Registry, initializeGlobals)
            {
                FlushAttachedStoresOnDispose = options.FlushOnDispose
            };
            options.ConfigureCache?.Invoke(cache);

            if (options.UseDirectoryStore || Directory.Exists(options.DirectoryStorePath ?? DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath)))
            {
                var directoryStore = new DirectoryMessagePackBackingStore(filePath, options.DirectoryStorePath)
                {
                    FlushOnDispose = options.StoreFlushOnDispose,
                    HydrationFilter = options.DirectoryStoreHydrationFilter
                };
                options.ConfigureDirectoryStore?.Invoke(directoryStore);
                cache.AddBackingStore(directoryStore);
            }
            else
            {
                var store = new SingleFileMessagePackBackingStore(filePath)
                {
                    FlushOnDispose = options.StoreFlushOnDispose
                };
                options.ConfigureStore?.Invoke(store);
                cache.AddBackingStore(store);
            }

            return cache;
        }
    }
}
