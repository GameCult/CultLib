#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class PreCut2StoreFormatTests
    {
        private static string FixtureRoot =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "pre-cut2");

        private static string CopyFixture(string name)
        {
            var target = Path.Combine(Path.GetTempPath(), $"cultlib-precut2-{Guid.NewGuid():N}");
            var source = Path.Combine(FixtureRoot, name);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            return target;
        }

        private static void AssertFixtureRecords(CultCache cache)
        {
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:anvil")), Is.Not.Null);
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:anvil"))!.Name, Is.EqualTo("anvil"));
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:anvil"))!.Count, Is.EqualTo(3));
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:bellows"))?.Name, Is.EqualTo("bellows"));
            Assert.That(cache.Get<PreCut2FixtureItem>(new CultRecordKey("item:bellows"))?.Count, Is.EqualTo(42));
            Assert.That(cache.Get<PreCut2FixtureNote>(new CultRecordKey("note:origin"))?.Text,
                Is.EqualTo("written before cut 2"));
            Assert.That(cache.GetAll<PreCut2FixtureItem>(), Has.Length.EqualTo(2));
            Assert.That(cache.GetAll<PreCut2FixtureNote>(), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task DirectoryStoreOpensV4ManifestWrittenBeforeCut2()
        {
            var root = CopyFixture("directory-v4");
            try
            {
                var manifest = Path.Combine(root, "store.cc");
                Assert.That(CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(manifest)).FormatVersion,
                    Is.EqualTo("cultcache.store.v4.directory-content-addressed-pages"));

                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                await cache.PullAllBackingStoresAsync();

                AssertFixtureRecords(cache);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SingleFileOpensV1SnapshotWrittenBeforeCut2()
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                Assert.That(CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file)).FormatVersion,
                    Is.EqualTo("cultcache.store.v1"));

                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();

                AssertFixtureRecords(cache);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // Shared refusal vectors: tests/vectors/document-variants-c0, read by every runtime's tests.
        private const string ItemSchemaId = "sha256:88d3fdf0a927acf3b163940d8f8c7fe62b3316542ce771a67ec8bc038f594788";

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c0", name);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException($"Shared vector {name} not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        private static string Refusal(string vector)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-vector-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                File.Copy(VectorPath(vector), file);
                using var cache = new CultCache();
                var error = Assert.Catch(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(file)));
                Assert.That(error, Is.Not.Null);
                Assert.That(error, Is.TypeOf<CultStoreUnreadableException>());
                return error.ToString();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void SingleFileRefusesUnknownHeaderByName()
        {
            Assert.That(Refusal("unknown-header.msgpack"), Does.Contain("cultcache.store.v9"));
        }

        // Slots of the current catalog layout that do not decode are refused by name, as a malformed store is.
        [TestCase("current-catalog-bad-canonical.msgpack")]
        [TestCase("current-catalog-bad-members.msgpack")]
        [TestCase("current-catalog-bad-compat.msgpack")]
        [TestCase("current-catalog-bad-slot5-nil.msgpack")]
        [TestCase("current-catalog-bad-slot5-int.msgpack")]
        public void SingleFileRefusesAMalformedCurrentCatalogEntry(string vector)
        {
            Assert.That(Refusal(vector), Is.Not.Empty);
        }

        // The release-now-c2a-bumps-format ruling rests on this: a store stamped with a newer format version is refused at
        // open, so this runtime cannot rewrite it. The file stays byte-identical through the refusal and through later
        // writes to the cache, whose refused store was never attached.
        [TestCase("cultcache.store.v4")]
        [TestCase("cultcache.store.v9")]
        public async Task SingleFileStampedWithANewerFormatIsRefusedAndNeverRewritten(string newerFormat)
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file));
                snapshot.FormatVersion = newerFormat;
                File.WriteAllBytes(file, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
                var before = File.ReadAllBytes(file);

                using var cache = new CultCache();
                var error = Assert.Throws<NotSupportedException>(() =>
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(file)))!;
                Assert.That(error.Message, Does.Contain(newerFormat));
                Assert.That(cache.BackingStores, Is.Empty);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "written after the refusal" });
                await cache.FlushAsync();

                Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // Two processes: this one attached a v1 store, then a later build rewrote it as v4. A plain flush, a staged write
        // and a staged delete must each refuse to downgrade it, leaving the file as the other process wrote it.
        [Test]
        public async Task SingleFileFlushRefusesAStoreAnotherProcessStampedNewerSinceAttach()
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();

                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file));
                snapshot.FormatVersion = "cultcache.store.v4";
                File.WriteAllBytes(file, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
                var before = File.ReadAllBytes(file);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "staged after the other process wrote v4" });
                Assert.Throws<NotSupportedException>(() => cache.FlushAllBackingStores());
                Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));

                cache.Remove(new CultRecordKey("item:anvil"));
                Assert.Throws<NotSupportedException>(() => cache.BackingStores.Single().PushAll());
                Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A write set cannot merge with a disk it cannot read: a flush refuses a store whose stamp is unreadable and leaves it
        // as it is.
        [Test]
        public async Task SingleFileFlushRefusesAStoreWhoseStampIsUnreadable()
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();
                var damaged = new byte[] { 0xc1, 0xc1 };
                File.WriteAllBytes(file, damaged);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "refused" });
                Assert.Throws<CultStoreUnreadableException>(() => cache.FlushAllBackingStores());

                Assert.That(File.ReadAllBytes(file), Is.EqualTo(damaged));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static byte[] Raw(params object[] parts)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            foreach (var part in parts)
            {
                switch (part)
                {
                    case byte single: bytes.Add(single); break;
                    case string text:
                        var encoded = System.Text.Encoding.UTF8.GetBytes(text);
                        bytes.Add(0xd9); bytes.Add((byte)encoded.Length); bytes.AddRange(encoded); break;
                    case byte[] binary:
                        bytes.Add(0xc4); bytes.Add((byte)binary.Length); bytes.AddRange(binary); break;
                    default: throw new ArgumentException(part.GetType().Name);
                }
            }
            return bytes.ToArray();
        }

        // The format decision comes from the header alone. A stamp this runtime does not read refuses whatever follows it,
        // so a body that will not parse can never make a newer store look like damage a flush may heal.
        private static System.Collections.Generic.IEnumerable<TestCaseData> NewerStampOverUnreadableBody()
        {
            byte garbage = 0xc1;
            yield return new TestCaseData(Raw((byte)0x93, "cultcache.store.v4", garbage, garbage)).SetName("v4 header over a bad body");
            yield return new TestCaseData(Raw((byte)0x93, System.Text.Encoding.UTF8.GetBytes("cultcache.store.v3"), garbage, garbage)).SetName("bin header v3 over a bad body");
            yield return new TestCaseData(Raw((byte)0x93, System.Text.Encoding.UTF8.GetBytes("cultcache.store.v9"), garbage, garbage)).SetName("bin header v9 over a bad body");
            yield return new TestCaseData(Raw((byte)0x93, "cultcache.store.v4", (byte)0x91, garbage, (byte)0x90)).SetName("v4 header over a bad catalog");
            yield return new TestCaseData(Raw((byte)0x92, "cultcache.store.v4", garbage)).SetName("v4 header over garbage");
            yield return new TestCaseData(Raw((byte)0x93, "cultcache.store.v5", garbage, garbage)).SetName("v5 header over a bad body");
        }

        [TestCaseSource(nameof(NewerStampOverUnreadableBody))]
        public async Task SingleFileFlushRefusesANewerStampWhateverItsBodyHolds(byte[] swapped)
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();
                File.WriteAllBytes(file, swapped);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "must not overwrite" });
                Assert.Throws<NotSupportedException>(() => cache.FlushAllBackingStores());
                Assert.That(File.ReadAllBytes(file), Is.EqualTo(swapped));
                Assert.Throws<NotSupportedException>(() => new SingleFileMessagePackBackingStore(file).PullAll());
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task DirectoryWritesRefuseANewerStampWhateverTheManifestBodyHolds()
        {
            var root = CopyFixture("directory-v4");
            try
            {
                var manifest = Path.Combine(root, "store.cc");
                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                await cache.PullAllBackingStoresAsync();
                var swapped = Raw((byte)0x93, "cultcache.store.v6.directory-content-addressed-pages", (byte)0xc1, (byte)0xc1);
                File.WriteAllBytes(manifest, swapped);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "must not overwrite" });
                Assert.Throws<NotSupportedException>(() => cache.FlushAllBackingStores());
                Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(swapped));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A file that is read but carries no readable stamp (empty, or cut off) is not a store: a flush refuses it, whatever its
        // length, and leaves it as it is.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(10)]
        [TestCase(-1)]
        public async Task SingleFileFlushRefusesAnEmptyOrTruncatedFile(int keep)
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();
                var whole = File.ReadAllBytes(file);
                var damaged = whole.Take(keep < 0 ? whole.Length / 2 : keep).ToArray();
                File.WriteAllBytes(file, damaged);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "refused" });
                Assert.Throws<CultStoreUnreadableException>(() => cache.FlushAllBackingStores());

                Assert.That(File.ReadAllBytes(file), Is.EqualTo(damaged));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A store subclass that overrides nothing beyond its abstract members is guarded by the base, not by an opt-in.
        private sealed class PlainCustomStore : SingleFileBackingStore
        {
            public PlainCustomStore(string path) : base(path) { }

            protected override byte[] SerializeStore(string formatVersion, System.Collections.Generic.IReadOnlyList<byte[]> catalogEntries, System.Collections.Generic.IReadOnlyList<byte[]> records) =>
                CultDocumentMessagePackSerialization.SerializeStore(formatVersion, catalogEntries, records);

            protected override byte[] SerializeRecord(CultPersistedRecord record) =>
                CultDocumentMessagePackSerialization.SerializePersistedRecord(record);

            protected override byte[] SerializeCatalogEntry(CultSchemaCatalogEntry entry) =>
                CultDocumentMessagePackSerialization.SerializeCatalogEntry(entry);

            protected override (CultPersistedStoreSnapshot Snapshot, byte[][] CatalogBytes, byte[][] RecordBytes) ReadStore(byte[] data)
            {
                var store = CultDocumentMessagePackSerialization.ReadStore(data);
                CultDocumentMessagePackSerialization.RequireSingleFileFormat(store.Snapshot);
                return store;
            }

            protected override byte[] SerializePayload(object document) =>
                CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType(), Registry);

            protected override object DeserializePayload(Type documentType, byte[] payload) =>
                CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload, Registry);
        }

        private static void StampHeader(string file, string header)
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file));
            snapshot.FormatVersion = header;
            File.WriteAllBytes(file, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        [Test]
        public async Task CustomSingleFileStoreThatOverridesNothingIsRefusedAtFlushOfANewerStore()
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                cache.AddBackingStore(new PlainCustomStore(file));
                await cache.PullAllBackingStoresAsync();
                StampHeader(file, "cultcache.store.v4");
                var before = File.ReadAllBytes(file);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "staged" });
                Assert.Throws<NotSupportedException>(() => cache.FlushAllBackingStores());
                Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // Attach, commit and flush share one rule for which formats are readable: a header the reader refuses at attach is
        // refused at flush, whatever its spelling.
        [TestCase("cultcache.store.v3-ids")]
        [TestCase("cultcache.store.v1000000")]
        [TestCase("cultcache.v3")]
        [TestCase("cultcache.store.v3")]
        [TestCase("cultcache.store.v2")]
        [TestCase("cultcache.store.v1")]
        public async Task FlushRefusesExactlyTheHeadersAttachRefuses(string header)
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
                await cache.PullAllBackingStoresAsync();
                StampHeader(file, header);
                var before = File.ReadAllBytes(file);

                bool attachRefuses;
                using (var fresh = new CultCache())
                {
                    try { fresh.AddBackingStore(new SingleFileMessagePackBackingStore(file)); attachRefuses = false; }
                    catch (NotSupportedException) { attachRefuses = true; }
                }

                await cache.AddAsync(new PreCut2FixtureNote { Text = "staged" });
                var flushRefuses = false;
                try { cache.FlushAllBackingStores(); }
                catch (NotSupportedException) { flushRefuses = true; }

                Assert.That(flushRefuses, Is.EqualTo(attachRefuses), header);
                if (attachRefuses) Assert.That(File.ReadAllBytes(file), Is.EqualTo(before), header);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // The refusal comes from the read, not from a failed write onto the held file: the store never reaches serialising.
        private sealed class WriteCountingStore : SingleFileMessagePackBackingStore
        {
            public int Serialisations;

            public WriteCountingStore(string path) : base(path) { }

            protected override byte[] SerializeStore(string formatVersion, System.Collections.Generic.IReadOnlyList<byte[]> catalogEntries, System.Collections.Generic.IReadOnlyList<byte[]> records)
            {
                Serialisations++;
                return base.SerializeStore(formatVersion, catalogEntries, records);
            }
        }

        // The public parser judges no stamp, as in 1.0.61: a v4 directory manifest and a v3 header both parse.
        [Test]
        public void PublicParserReadsAnyStampAsEveryReleaseHas()
        {
            var root = CopyFixture("directory-v4");
            try
            {
                var manifest = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(Path.Combine(root, "store.cc")));
                Assert.That(manifest.FormatVersion, Is.EqualTo("cultcache.store.v4.directory-content-addressed-pages"));

                var v3 = new CultPersistedStoreSnapshot { FormatVersion = "cultcache.store.v3", SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(), Records = Array.Empty<CultPersistedRecord>() };
                var parsed = CultDocumentMessagePackSerialization.DeserializeSnapshot(CultDocumentMessagePackSerialization.SerializeSnapshot(v3));
                Assert.That(parsed.FormatVersion, Is.EqualTo("cultcache.store.v3"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A read that fails on I/O is not an unreadable stamp to heal over: the flush refuses and the file is untouched.
        [Test]
        public async Task SingleFileFlushRefusesWhenReadingTheDiskFailsOnIO()
        {
            var root = CopyFixture("single-file-v1");
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                using var cache = new CultCache();
                var store = new WriteCountingStore(file);
                cache.AddBackingStore(store);
                await cache.PullAllBackingStoresAsync();
                var before = File.ReadAllBytes(file);

                await cache.AddAsync(new PreCut2FixtureNote { Text = "staged" });
                store.Serialisations = 0;
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Throws<IOException>(() => cache.FlushAllBackingStores());
                }
                Assert.That(store.Serialisations, Is.Zero, "the refusal is the read failing, not the write onto the held file failing");
                Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestCase("cultcache.store.v6.directory-content-addressed-pages")]
        [TestCase("cultcache.store.v9")]
        public async Task DirectoryWritesRefuseAStoreAnotherProcessStampedNewerSinceAttach(string newer)
        {
            var root = CopyFixture("directory-v4");
            try
            {
                var manifest = Path.Combine(root, "store.cc");
                using var cache = new CultCache();
                cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest));
                await cache.PullAllBackingStoresAsync();

                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(manifest));
                snapshot.FormatVersion = newer;
                File.WriteAllBytes(manifest, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
                Func<System.Collections.Generic.Dictionary<string, byte[]>> files = () => Directory
                    .GetFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
                    .ToDictionary(path => path, File.ReadAllBytes);
                var before = files();

                await cache.AddAsync(new PreCut2FixtureNote { Text = "staged after the other process wrote the newer format" });
                Assert.Throws<NotSupportedException>(() => cache.FlushAllBackingStores());
                Assert.That(files().Keys, Is.EquivalentTo(before.Keys));
                foreach (var pair in before) Assert.That(File.ReadAllBytes(pair.Key), Is.EqualTo(pair.Value), pair.Key);

                cache.Remove(new CultRecordKey("item:anvil"));
                Assert.Throws<NotSupportedException>(() => cache.FlushAllBackingStores());
                Assert.That(files().Keys, Is.EquivalentTo(before.Keys));
                foreach (var pair in before) Assert.That(File.ReadAllBytes(pair.Key), Is.EqualTo(pair.Value), pair.Key);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void SingleFileRefusesExtraRecordSlotNamingTheRecord()
        {
            var message = Refusal("extra-slot-full-payload.msgpack");
            Assert.That(message, Does.Contain("item:anvil"));
            Assert.That(message, Does.Contain(ItemSchemaId));
        }

        // C1 reads v2. This vector was written by hand before C1 existed and its variant keeps its base's name, so a
        // cache with a codec refuses it under R6, naming both records; a cache without one cannot resolve it at all.
        [Test]
        public void SingleFileRefusesTheHandWrittenVariantVectorNamingTheRecords()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-vector-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                File.Copy(VectorPath("variant-v2.msgpack"), file);

                using var withCodec = new CultCache(CultDocumentRegistry.Shared, CultCacheMessagePack.CreateCodec(CultDocumentRegistry.Shared));
                var named = Assert.Throws<InvalidOperationException>(() => withCodec.AddBackingStore(new SingleFileMessagePackBackingStore(file)))!;
                Assert.That(named.Message, Does.Contain("item:anvil-big").And.Contain("item:anvil"));

                using var without = new CultCache();
                var codecless = Assert.Throws<InvalidOperationException>(() => without.AddBackingStore(new SingleFileMessagePackBackingStore(file)))!;
                Assert.That(codecless.Message, Does.Contain("item:anvil-big").And.Contain("codec"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void SnapshotWithoutAFormatVersionIsRefusedNotDefaultedToV1()
        {
            // [nil, [], []]
            var bytes = new byte[] { 0x93, 0xc0, 0x90, 0x90 };

            Assert.That(() => CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes),
                Throws.TypeOf<CultStoreUnreadableException>().With.Message.Contains("format version"));
        }

        // Every runtime reads a store file the same way: a missing file is an empty store, an existing empty file is not
        // a store (no CultCache writer leaves one), and an empty array carries no header, so it claims no format and is an
        // empty store too.
        [Test]
        public async Task SingleFileRefusesAnEmptyFileAndOpensAMissingFileOrAnEmptyArrayAsEmpty()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-empty-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var empty = Path.Combine(root, "empty.msgpack");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                using (var cache = new CultCache())
                {
                    Assert.Catch(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(empty)));
                    Assert.That(cache.BackingStores, Is.Empty);
                }
                Assert.That(File.ReadAllBytes(empty), Is.Empty);

                var emptyArray = Path.Combine(root, "empty-array.msgpack");
                File.WriteAllBytes(emptyArray, new byte[] { 0x90 });
                foreach (var path in new[] { Path.Combine(root, "missing.msgpack"), emptyArray })
                {
                    using var cache = new CultCache();
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await cache.PullAllBackingStoresAsync();
                    Assert.That(cache.GetAll<PreCut2FixtureItem>(), Is.Empty, path);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A refusal echoes a header only in the shape cultcache.store.v<digits>; any other is described by its length.
        [TestCase("cultcache.store.v12", true)]
        [TestCase("cultcache.store.SECRET-HEADER", false)]
        [TestCase("cultcache.store.v12SECRET", false)]
        [TestCase("cultcache.store.v", false)]
        public void SingleFileRefusalEchoesAHeaderOnlyInTheKnownShape(string header, bool echoed)
        {
            var snapshot = new CultPersistedStoreSnapshot { FormatVersion = header };
            var error = Assert.Throws<CultStoreUnreadableException>(() => CultDocumentMessagePackSerialization.RequireSingleFileFormat(snapshot))!;
            Assert.That(error.Message, Does.Contain($"this runtime reads {CultPersistedStoreSnapshot.FormatV1}, {CultPersistedStoreSnapshot.FormatV2} and {CultPersistedStoreSnapshot.FormatV3}."));
            if (echoed)
            {
                Assert.That(error.Message, Does.Contain("format " + header + " is not"));
            }
            else
            {
                Assert.That(error.Message, Does.Not.Contain("SECRET").And.Not.Contain("format " + header + " is not"));
                Assert.That(error.Message, Does.Contain($"of {header.Length} bytes"));
            }
        }

        // The stamp decides readability and the body never does: a newer stamp is named whatever follows it, without the body parsed.
        [Test]
        public void ReadStoreNamesANewerSingleFileStampWithoutParsingTheBody()
        {
            var garbage = (byte)0xc1;
            var bytes = Raw((byte)0x93, "cultcache.store.v4", garbage, garbage);

            var error = Assert.Throws<CultStoreUnreadableException>(() =>
                CultDocumentMessagePackSerialization.ReadStore(bytes, CultDocumentMessagePackSerialization.RequireReadableSingleFileStamp))!;

            Assert.That(error.Message, Does.Contain("format cultcache.store.v4 is not readable"));
        }

        // A snapshot with no format version has no stamp to write, so serialising it is refused rather than writing a nil slot 0.
        [Test]
        public void SerializeSnapshotRefusesASnapshotWithNoFormatVersion()
        {
            var snapshot = new CultPersistedStoreSnapshot { FormatVersion = null! };

            Assert.Throws<ArgumentException>(() => CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        [Test]
        public void V1StoreWrittenAtTheBaseCommitStillDecodesByteForByte()
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(VectorPath("v1-base.msgpack")));
            CultDocumentMessagePackSerialization.RequireSingleFileFormat(snapshot);

            Assert.That(snapshot.FormatVersion, Is.EqualTo("cultcache.store.v1"));
            Assert.That(snapshot.Records.Select(record => record.Key), Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(snapshot.Records[0].Payload, Is.EqualTo(new byte[] { 0x92, 0xa5, (byte)'a', (byte)'l', (byte)'p', (byte)'h', (byte)'a', 0x01 }));
            Assert.That(snapshot.Records[1].Payload, Is.EqualTo(new byte[] { 0x92, 0xa4, (byte)'b', (byte)'e', (byte)'t', (byte)'a', 0x02 }));
        }

        // Format strings taken from DirectoryMessagePackBackingStore.cs at 0db1fe5.
        [TestCase("cultcache.store.v1")]
        [TestCase("cultcache.store.v1.directory")]
        [TestCase("cultcache.store.v2.directory-indexed")]
        [TestCase("cultcache.store.v3.directory-immutable-pages")]
        public void DirectoryStoreRefusesNonV4Manifest(string formatVersion)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-refuse-{Guid.NewGuid():N}");
            var manifest = Path.Combine(root, "store.cc");
            Directory.CreateDirectory(root);
            try
            {
                var bytes = CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
                {
                    FormatVersion = formatVersion,
                    SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
                    Records = Array.Empty<CultPersistedRecord>()
                });
                File.WriteAllBytes(manifest, bytes);

                using var cache = new CultCache();

                // The refusal names the manifest, and shows the header only when it is cultcache.store.v<digits>: the suffixed
                // names a directory store once wrote are stored text like any other.
                var shown = System.Text.RegularExpressions.Regex.IsMatch(formatVersion, @"^cultcache\.store\.v[0-9]+$");
                var refusal = Assert.Throws<CultStoreUnreadableException>(() => cache.AddBackingStore(new DirectoryMessagePackBackingStore(manifest)))!;
                Assert.That(refusal.Path, Is.EqualTo(manifest));
                Assert.That(refusal.Message.Contains(" is " + formatVersion + ";", StringComparison.Ordinal), Is.EqualTo(shown), refusal.Message);
                Assert.That(cache.BackingStores, Is.Empty);
                Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(bytes));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
