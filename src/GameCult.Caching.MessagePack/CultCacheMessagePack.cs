using System;
using System.IO;
using System.Threading.Tasks;

namespace GameCult.Caching.MessagePack
{
    public sealed class CultCacheOpenOptions
    {
        public CultDocumentRegistry? Registry { get; set; }

        public bool ReadOnly { get; set; }

        public bool FlushOnDispose { get; set; }

        public bool StoreFlushOnDispose { get; set; }

        public bool UseDirectoryStore { get; set; }
    }

    public static class CultCacheMessagePack
    {
        // Opening hydrates. A consumer that wants a fresh store removes the records it does not keep, upserts, and
        // flushes; the atomic replace does the rest.
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

            CacheBackingStore store = options.UseDirectoryStore || Directory.Exists(DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(filePath))
                ? new DirectoryMessagePackBackingStore(filePath, readOnly: options.ReadOnly)
                : new SingleFileMessagePackBackingStore(filePath, options.ReadOnly);
            store.FlushOnDispose = options.StoreFlushOnDispose;
            cache.AddBackingStore(store);
            return cache;
        }

        public static Task<CultCache> OpenAsync(string filePath, CultCacheOpenOptions? options = null)
        {
            return Task.FromResult(Create(filePath, options));
        }
    }
}
