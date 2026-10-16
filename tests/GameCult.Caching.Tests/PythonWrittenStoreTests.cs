#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The reference opens a store a Python typed-leaf definition (cultcache-py) wrote. The stores are vectors written only
    // by cultcache-py's PythonWrittenStoreVectorTests (CULTCACHE_WRITE_VECTORS=1), values authored in Python.
    public class PythonWrittenStoreTests
    {
        private static string Store(string name, string scratch)
        {
            var copy = Path.Combine(scratch, name);
            File.Copy(NumericLeafVectorTests.VectorPath(Path.Combine("py-store", name)), copy, true);
            return copy;
        }

        [Test]
        public async Task APythonWrittenTypedLeafStoreOpensWithEverySlotItsCSharpValue()
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(Store("leaf-record.cc", scratch)));
                await cache.PullAllBackingStoresAsync();
                var doc = cache.AllEntries.OfType<NumericLeafVectorTests.LeafRecord>().Single();
                static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
                Assert.Multiple(() =>
                {
                    Assert.That(doc.Id, Is.EqualTo("py"));
                    Assert.That(doc.Flag, Is.False);
                    Assert.That(doc.Blob, Is.EqualTo(new byte[] { 1, 0xff }));
                    Assert.That(doc.I, Is.EqualTo(int.MinValue));
                    Assert.That(doc.L, Is.EqualTo(long.MaxValue));
                    Assert.That(Bits(doc.F), Is.EqualTo(Bits(0.1f)));
                    Assert.That(doc.D, Is.EqualTo(1e-300));
                    Assert.That(doc.MaybeI, Is.Null);
                    Assert.That(doc.Tags, Is.Empty);
                    Assert.That(doc.Names, Is.EqualTo(new[] { "é", "z" }));
                    var any = (IDictionary<object, object>)doc.Any!;
                    Assert.That(Convert.ToInt64(any["n"]), Is.EqualTo(-3));
                    var k = (object[])any["k"];
                    Assert.That(Convert.ToInt64(k[0]), Is.EqualTo(1));
                    Assert.That(k[1], Is.EqualTo(true));
                    Assert.That(k[2], Is.Null);
                    Assert.That(k[3], Is.EqualTo(2.5));
                    Assert.That(k[4], Is.EqualTo("s"));
                    Assert.That(doc.Nested, Is.Null);
                    Assert.That(doc.Items.Select(i => i.N), Is.EqualTo(new[] { "one", "two" }));
                    Assert.That(doc.Items.Select(i => i.Id), Is.EqualTo(new[] { "000000000021", "000000000022" }), "a Python-written list of records keeps its element ids");
                    Assert.That(Bits(doc.Items[0].W), Is.EqualTo(Bits((float)(9007199254740992L + 536870913L))));
                    Assert.That(Bits(doc.Items[1].W), Is.EqualTo(Bits(1e-40f)));
                    Assert.That(Bits(((NumericLeafVectorTests.LeafCircle)doc.Shape!).R), Is.EqualTo(Bits(0.1f)));
                    Assert.That(doc.Floats.Select(Bits), Is.EqualTo(new[] { 0.1f, 3f, -0.0f, float.PositiveInfinity }.Select(Bits)));
                    Assert.That(doc.MaybeF, Is.Null);
                });
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        private static string HeaderOf(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

        private static async Task<(CultCache Cache, SingleFileMessagePackBackingStore Store)> PulledAsync(string path)
        {
            var cache = new CultCache();
            var store = new SingleFileMessagePackBackingStore(path);
            cache.AddBackingStore(store);
            await cache.PullAllBackingStoresAsync();
            return (cache, store);
        }

        // cultcache-py writes element ids under an unmarked v1 header. A C# write that carries such a record forward marks the
        // store, because a reader from before ids would otherwise rewrite the elements without them.
        [Test]
        public async Task ACSharpWriteThatCopiesAPythonRecordHoldingIdsMarksTheStore()
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Store("leaf-record.cc", scratch);
                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1), "cultcache-py leaves the header unmarked");
                var (cache, store) = await PulledAsync(path);
                await cache.AddAsync(new NumericLeafVectorTests.LeafRecord { Id = "other" });
                store.PushAll();
                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));

                var (reloaded, _) = await PulledAsync(path);
                var py = reloaded.AllEntries.OfType<NumericLeafVectorTests.LeafRecord>().Single(record => record.Id == "py");
                Assert.That(py.Items.Select(item => item.Id), Is.EqualTo(new[] { "000000000021", "000000000022" }));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        // What a load saw is a fact about those bytes at that version. Another writer replaced the record since, so the copy this
        // write makes is not the record the load saw, and the header is not judged by it.
        [Test]
        public async Task ACopiedRecordAnotherWriterChangedIsNotJudgedByTheOldRead()
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Store("leaf-record.cc", scratch);
                var (reader, readerStore) = await PulledAsync(path);

                // Another writer replaces the record with one that holds no id.
                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
                var replaced = snapshot.Records.Single(record => record.Key == "py");
                replaced.Payload = CultDocumentMessagePackSerialization.SerializeUntyped(
                    new NumericLeafVectorTests.LeafRecord { Id = "py" }, typeof(NumericLeafVectorTests.LeafRecord));
                File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1), "the replacement holds no id");

                await reader.AddAsync(new NumericLeafVectorTests.LeafRecord { Id = "other" });
                readerStore.PushAll();
                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        // A v1 store holding only this record c0, opened by a cache that has pulled it; then another runtime appends the python
        // record (and its payload rewritten by `change`) to the file raw, header still v1, the way a writer that keeps the v1 it
        // found does. Returns the open cache and its store.
        private static async Task<(CultCache Cache, SingleFileMessagePackBackingStore Store)> PulledThenAppended(
            string path, string pySource, Action<CultPersistedRecord>? change = null)
        {
            using (var seed = new CultCache())
            {
                var seedStore = new SingleFileMessagePackBackingStore(path);
                seed.AddBackingStore(seedStore);
                await seed.AddAsync(new NumericLeafVectorTests.LeafRecord { Id = "c0" });
                seedStore.PushAll();
            }

            Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1));
            var opened = await PulledAsync(path);
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var pySnapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(pySource));
            var py = pySnapshot.Records.Single(record => record.Key == "py");
            change?.Invoke(py);
            snapshot.Records = snapshot.Records.Concat(new[] { py }).ToArray();
            snapshot.SchemaCatalog = snapshot.SchemaCatalog.Concat(pySnapshot.SchemaCatalog).GroupBy(entry => entry.SchemaId).Select(group => group.First()).ToArray();
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1), "the foreign writer keeps the v1 it found");
            return opened;
        }

        // The cache never read this record: it was appended after the pull. A per-instance memory of what the pull saw cannot
        // know about it; the write decides from the bytes the file holds under its lock.
        [Test]
        public async Task ARecordAForeignWriterAddsAfterThePullMarksTheStoreWhenCopied()
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Path.Combine(scratch, "after-pull.cc");
                var (cache, store) = await PulledThenAppended(path, NumericLeafVectorTests.VectorPath(Path.Combine("py-store", "leaf-record.cc")));
                await cache.AddAsync(new NumericLeafVectorTests.LeafRecord { Id = "c1" });
                store.PushAll();
                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));

                var (reloaded, _) = await PulledAsync(path);
                var py = reloaded.AllEntries.OfType<NumericLeafVectorTests.LeafRecord>().Single(record => record.Id == "py");
                Assert.That(py.Items.Select(item => item.Id), Is.EqualTo(new[] { "000000000021", "000000000022" }));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        // Ids a load minted in memory are not on disk: the check judges the file's bytes, never the document the cache holds.
        [Test]
        public async Task ARecordWhoseIdsExistOnlyInMemoryLeavesTheStoreUnmarked()
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Path.Combine(scratch, "in-memory.cc");
                var source = Store("leaf-record.cc", scratch);
                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(source));
                var idless = new NumericLeafVectorTests.LeafRecord { Id = "py" };
                idless.Items.Add(new NumericLeafVectorTests.LeafInner { N = "one" });
                idless.Items.Add(new NumericLeafVectorTests.LeafInner { N = "two" });
                snapshot.Records.Single().Payload = CultDocumentMessagePackSerialization.SerializeUntyped(idless, typeof(NumericLeafVectorTests.LeafRecord));
                File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
                var before = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Single().Payload;

                var (cache, store) = await PulledAsync(path);
                Assert.That(cache.AllEntries.OfType<NumericLeafVectorTests.LeafRecord>().Single().Items.Select(item => item.Id), Has.All.Length.EqualTo(12), "the load minted ids in memory");
                await cache.AddAsync(new NumericLeafVectorTests.LeafRecord { Id = "other" });
                store.PushAll();

                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1));
                var after = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Single(record => record.Key == "py").Payload;
                Assert.That(after, Is.EqualTo(before), "the copied record is the bytes the file held");
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        // The write-set rule copies records without interpreting them, so the check must never refuse a write: a record this
        // cache claims but cannot decode is copied as its bytes and decides nothing.
        [Test]
        public async Task ACopiedRecordThatDoesNotDecodeIsCopiedAndDecidesNothing()
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Path.Combine(scratch, "undecodable.cc");
                var garbage = new byte[] { 0xc1, 0xc1, 0xc1 };
                var (cache, store) = await PulledThenAppended(
                    path, NumericLeafVectorTests.VectorPath(Path.Combine("py-store", "leaf-record.cc")), record => record.Payload = garbage);

                await cache.AddAsync(new NumericLeafVectorTests.LeafRecord { Id = "c1" });
                Assert.DoesNotThrow(() => store.PushAll());

                Assert.That(HeaderOf(path), Is.EqualTo(CultPersistedStoreSnapshot.FormatV1));
                var py = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Single(record => record.Key == "py");
                Assert.That(py.Payload, Is.EqualTo(garbage));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        [TestCase(3)]
        [TestCase(5)]
        [TestCase(7)]
        [TestCase(14)]
        public async Task APythonWrittenCatalogNamingAnotherTypeForASlotIsRefused(int slot)
        {
            var scratch = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var cache = new CultCache();
                var path = Store($"wrong-slot-{slot}.cc", scratch);
                var error = Assert.CatchAsync<Exception>(async () =>
                {
                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                    await cache.PullAllBackingStoresAsync();
                });
                Assert.That(error!.Message, Does.Contain($"slot {slot} changed type"));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}
