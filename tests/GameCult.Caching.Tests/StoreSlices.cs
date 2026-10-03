using System;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;

namespace GameCult.Caching.Tests
{
    // The exact bytes a store file holds for its catalog entries and records. A read snapshot carries none: the store that read the
    // file owns them, and these read them as the store's reader does.
    internal static class StoreSlices
    {
        internal static byte[] CatalogEntry(string path, string schemaId)
        {
            var read = CultDocumentMessagePackSerialization.ReadStore(File.ReadAllBytes(path));
            return read.CatalogBytes[Array.FindIndex(read.Snapshot.SchemaCatalog, entry => entry.SchemaId == schemaId)];
        }

        internal static byte[][] CatalogEntries(string path) =>
            CultDocumentMessagePackSerialization.ReadStore(File.ReadAllBytes(path)).CatalogBytes;

        internal static byte[] Record(string path, string key)
        {
            var read = CultDocumentMessagePackSerialization.ReadStore(File.ReadAllBytes(path));
            return read.RecordBytes[Array.FindIndex(read.Snapshot.Records, record => record.Key == key)];
        }
    }
}
