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
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(Store($"wrong-slot-{slot}.cc", scratch)));
                var error = Assert.CatchAsync<Exception>(async () => await cache.PullAllBackingStoresAsync());
                Assert.That(error!.Message, Does.Contain($"slot {slot} changed type"));
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}
