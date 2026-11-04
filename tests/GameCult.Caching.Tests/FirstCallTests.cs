#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The C# first-call snippet that docs/start/README.md links: define a typed document, store it in a
    // .cc file, read it back through a second cache. Keep it small; it is the front door.
    public class FirstCallTests
    {
        [CultDocument("gamecult.first_call_item", "gamecult.first_call_item.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class Item
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] [CultIndex] public string Category = string.Empty;
            [Key(2)] public int Value;
        }

        [Test]
        public async Task DefineStoreAndReadBackATypedDocument()
        {
            var path = Path.Combine(Path.GetTempPath(), $"first-call-{Guid.NewGuid():N}.cc");
            try
            {
                // Write: a cache with one backing store, one typed document.
                var store = new SingleFileMessagePackBackingStore(path);
                var cache = new CultCache();
                cache.AddBackingStore(store);
                var handle = await cache.AddAsync(new Item { Name = "Potion", Category = "Consumable", Value = 50 });
                store.PushAll();

                // Read: a fresh cache over the same file.
                var reopened = new CultCache();
                reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await reopened.PullAllBackingStoresAsync();

                var byKey = reopened.Get<Item>(handle.Key);
                Assert.That(byKey, Is.Not.Null);
                Assert.That(byKey!.Value, Is.EqualTo(50));
                Assert.That(reopened.GetByName<Item>("Potion")!.Category, Is.EqualTo("Consumable"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
