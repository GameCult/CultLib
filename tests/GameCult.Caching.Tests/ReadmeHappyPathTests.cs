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
    // The Happy Path of src/GameCult.Caching/README.md, run as written: its statements are the region between snippet:begin and
    // snippet:end below, and ReadmeShowsTheTestedSnippet fails when the README stops showing them verbatim.
    public class ReadmeHappyPathTests
    {
        [CultDocument("gamecult.item_data", "gamecult.item_data.v1")]
        [MessagePackObject]
        public sealed class ItemData
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] [CultIndex] public string Sku = string.Empty;
            [Key(2)] public string Category = string.Empty;
            [Key(3)] public int Value;
        }

        private static async Task<CultCache> HappyPath()
        {
// snippet:begin
var cache = await CultCacheMessagePack.OpenAsync("Data.msgpack");
var key = new CultRecordKey("potion");

// The golden path: read, change a private copy, commit only if the record is unchanged, re-run on a lost race.
// Durable when it returns; no flush.
var result = cache.Update<ItemData>(key, item =>
{
    item ??= new ItemData { Name = "Potion", Sku = "POT-1", Category = "Consumable" };
    item.Value += 10;
    return item;
});

// The blind opt-out: last writer wins per key. Name the key, or a new key claims the same unique Sku and is refused.
var handle = await cache.UpsertAsync(new ItemData
{
    Name = "Potion",
    Sku = "POT-1",
    Category = "Consumable",
    Value = 50
}, new CultRecordHandle<ItemData>(key));

await cache.FlushAsync();
// snippet:end
            return cache;
        }

        [Test]
        public async Task HappyPathRunsAsWritten()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"readme-happy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = directory; // the README opens a relative path
                using var cache = await HappyPath();
                Assert.That(cache.Get<ItemData>(new CultRecordKey("potion"))!.Value, Is.EqualTo(50));
                Assert.That(cache.GetByIndex<ItemData>("Sku", "POT-1"), Is.Not.Null);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void ReadmeShowsTheTestedSnippet() =>
            StartDocs.AssertShowsSnippet(SourcePath(), Path.Combine("..", "..", "src", "GameCult.Caching", "README.md"), wholeBlock: false);

        static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
