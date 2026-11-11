#nullable enable
using System;
using System.IO;
using NUnit.Framework;

// snippet:begin
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

[CultDocument("gamecult.first_call_item", "gamecult.first_call_item.v1")]
[MessagePackObject]
public sealed class Item
{
    [Key(0)] [CultName] public string Name = string.Empty;
    [Key(1)] public string Category = string.Empty;
    [Key(2)] public int Value;
}

public static class FirstCall
{
    public static async Task<Item?> Run(string path)
    {
        var store = new SingleFileMessagePackBackingStore(path);
        var cache = new CultCache();
        cache.AddBackingStore(store);
        var handle = await cache.AddAsync(new Item { Name = "Potion", Category = "Consumable", Value = 50 });
        store.PushAll();                                   // write the .cc file

        var reopened = new CultCache();
        reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
        await reopened.PullAllBackingStoresAsync();        // read it back
        var potion = reopened.Get<Item>(handle.Key);       // by key
        var byName = reopened.GetByName<Item>("Potion");   // by [CultName]
        return byName ?? potion;
    }
}
// snippet:end

namespace GameCult.Caching.Tests
{
    // The C# first-call snippet that docs/start/README.md shows verbatim: define a typed document, store it
    // in a .cc file, read it back through a second cache. DocsShowTheTestedSnippet fails when they differ.
    public class FirstCallTests
    {
        [Test]
        public async Task DefineStoreAndReadBackATypedDocument()
        {
            var path = Path.Combine(Path.GetTempPath(), $"first-call-{Guid.NewGuid():N}.cc");
            try
            {
                var potion = await FirstCall.Run(path);
                Assert.That(potion, Is.Not.Null);
                Assert.That(potion!.Value, Is.EqualTo(50));
                Assert.That(potion.Category, Is.EqualTo("Consumable"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void DocsShowTheTestedSnippet()
        {
            var source = File.ReadAllText(SourcePath());
            const string begin = "// snippet:begin\n";
            var start = source.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
            var snippet = source[start..source.IndexOf("// snippet:end\n", StringComparison.Ordinal)];
            var docs = File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "..", "docs", "start", "README.md"));
            Assert.That(docs.Replace("\r\n", "\n"), Does.Contain("```csharp\n" + snippet + "```"),
                "docs/start/README.md no longer shows tests/GameCult.Caching.Tests/FirstCallTests.cs verbatim");
        }

        static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
