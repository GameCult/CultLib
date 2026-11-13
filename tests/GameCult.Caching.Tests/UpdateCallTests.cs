#nullable enable
using System;
using System.IO;
using NUnit.Framework;

// snippet:begin
using GameCult.Caching;
using GameCult.Caching.MessagePack;

public static class UpdateCall
{
    public static CultUpdateResult<Item> Restock(string path, CultRecordKey key)
    {
        var cache = CultCacheMessagePack.Create(path);
        return cache.Update<Item>(key, item =>
        {
            item ??= new Item { Name = "Potion", Category = "Consumable" };
            item.Value += 10;
            return item;
        });
    }
}
// snippet:end

namespace GameCult.Caching.Tests
{
    // The C# update snippet that docs/start/README.md shows verbatim; it reuses FirstCallTests' Item.
    public class UpdateCallTests
    {
        [Test]
        public void RestockTwiceReadsTwenty()
        {
            var path = Path.Combine(Path.GetTempPath(), $"update-call-{Guid.NewGuid():N}.cc");
            try
            {
                var key = new CultRecordKey("potion");
                Assert.That(UpdateCall.Restock(path, key).Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
                Assert.That(UpdateCall.Restock(path, key).Outcome, Is.EqualTo(CultUpdateOutcome.Updated));

                using var reader = CultCacheMessagePack.Create(path);
                Assert.That(reader.Get<Item>(key)!.Value, Is.EqualTo(20));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void DocsShowTheTestedSnippet() => StartDocs.AssertShowsSnippet(SourcePath());

        static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
