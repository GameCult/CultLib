using System;
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
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must be non-empty.", nameof(filePath));
            }

            options ??= new CultCacheOpenOptions();

            var cache = new CultCache(options.Registry)
            {
                FlushAttachedStoresOnDispose = options.FlushOnDispose
            };
            options.ConfigureCache?.Invoke(cache);

            var store = new SingleFileMessagePackBackingStore(filePath)
            {
                FlushOnDispose = options.StoreFlushOnDispose
            };
            options.ConfigureStore?.Invoke(store);

            cache.AddBackingStore(store);
            return cache;
        }

        /// <summary>
        /// Creates a cache with the canonical single-file MessagePack backing store attached and optionally pulls the on-disk snapshot.
        /// </summary>
        public static async Task<CultCache> OpenAsync(string filePath, CultCacheOpenOptions? options = null)
        {
            options ??= new CultCacheOpenOptions();
            var cache = Create(filePath, options);
            if (options.PullOnOpen)
            {
                await cache.PullAllBackingStoresAsync().ConfigureAwait(false);
            }

            return cache;
        }
    }
}
