using System;
using System.IO;
using System.Threading.Tasks;

namespace GameCult.Caching.MessagePack
{
    public sealed class CultCacheOpenOptions
    {
        public CultDocumentRegistry? Registry { get; set; }

        public bool PullOnOpen { get; set; } = true;

        public bool FlushOnDispose { get; set; }

        public bool StoreFlushOnDispose { get; set; }

        public bool UseDirectoryStore { get; set; }
    }

    public static class CultCacheMessagePack
    {
        public static CultCache Create(string filePath, CultCacheOpenOptions? options = null)
        {
            options ??= new CultCacheOpenOptions();
            return CreateCore(filePath, options, initializeGlobals: true);
        }

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

            if (options.UseDirectoryStore || Directory.Exists(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath)))
            {
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(filePath)
                {
                    FlushOnDispose = options.StoreFlushOnDispose
                });
            }
            else
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(filePath)
                {
                    FlushOnDispose = options.StoreFlushOnDispose
                });
            }

            return cache;
        }
    }
}
