#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // tests/vectors/content-version: the content version of a stored record, the lowercase hex SHA-256 of its persisted encoding
    // (variants:ruling:write-set-content-version-declared-ownership), as the reference reads it from a single-file store. The C#
    // reference is the only writer: under CULTCACHE_WRITE_VECTORS=1 the store is written and the versions read from it are written
    // beside it; every other run opens the committed store.cc and compares what the reference reads to versions.msgpack. Another
    // runtime computes the version from the same file and must equal these rows.
    public class ContentVersionVectorTests
    {
        private const string Vector = "content-version";

        private static readonly Type Item =
            EmittedDocumentTypes.Emit("CvItem", "tests.cv_item", "tests.cv_item.v1", new[] { new EmittedDocumentTypes.Field("Text", typeof(string), 0) });

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { Item });

        private static readonly (string Key, string Text)[] Records = { ("alpha", "first"), ("beta", "second") };

        private static byte[] StoreBytes()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-cvv-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "store.cc");
                using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, StoreFlushOnDispose = true }))
                {
                    cache.Clock = () => VectorFiles.StoredAt;
                    foreach (var (key, text) in Records)
                        cache.UpsertAsync(Item, EmittedDocumentTypes.New(Item, ("Text", text)), new CultRecordKey(key)).GetAwaiter().GetResult();
                    cache.FlushAsync().GetAwaiter().GetResult();
                }

                return File.ReadAllBytes(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // What the reference reads from a store file: [key, version] for each record, sorted by key.
        private static (string Key, string Version)[] ReadVersions(byte[] store)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-cvr-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "store.cc");
                File.WriteAllBytes(path, store);
                using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
                return cache.AllStoredDocuments
                    .Select(stored => (Key: stored.Key.Value, Version: stored.StoredVersion))
                    .OrderBy(row => row.Key, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static byte[] RowsFile((string Key, string Version)[] rows)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(rows.Length);
            foreach (var (key, version) in rows)
            {
                writer.WriteArrayHeader(2);
                writer.Write(key);
                writer.Write(version);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        [Test]
        public void TheReferenceReadsTheCommittedVersions()
        {
            if (VectorFiles.Writing)
            {
                var store = StoreBytes();
                VectorFiles.Check(Vector, "store.cc", store);
                VectorFiles.Check(Vector, "versions.msgpack", RowsFile(ReadVersions(store)));
                return;
            }

            var committed = VectorFiles.Committed(Vector, "store.cc");
            var rows = ReadVersions(committed);
            Assert.That(rows.Select(row => row.Key), Is.EqualTo(Records.Select(record => record.Key)), "the store holds the two records");
            Assert.That(rows.Select(row => row.Version).Distinct().Count(), Is.EqualTo(rows.Length), "two different records, two versions");
            Assert.That(rows.All(row => row.Version.Length == 64 && row.Version == row.Version.ToLowerInvariant()), Is.True, "lowercase hex SHA-256");
            Assert.That(Convert.ToHexString(RowsFile(rows)), Is.EqualTo(Convert.ToHexString(VectorFiles.Committed(Vector, "versions.msgpack"))), "versions.msgpack");
        }

        [Test]
        public void TheCommittedStoreIsWhatTheReferenceWrites()
        {
            if (VectorFiles.Writing) return;
            Assert.That(Convert.ToHexString(StoreBytes()), Is.EqualTo(Convert.ToHexString(VectorFiles.Committed(Vector, "store.cc"))), "store.cc");
        }
    }
}
