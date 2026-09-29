#nullable enable
using System;
using System.IO;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Copied into a checkout of the C0 merge (e382bb4) by old-reader-refusal.sh. Not part of this tree's suite.
    public class OldReaderV3Probe
    {
        [Test]
        public void ACopyOfV3BaseIsRefusedByHeader()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cultlib-oldreader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var file = Path.Combine(root, "store.msgpack");
                File.Copy(Path.Combine(Environment.GetEnvironmentVariable("CULTLIB_ROOT")!, "tests", "vectors", "document-variants-c2a", "v3-base.msgpack"), file);
                using var cache = new CultCache();
                var error = Assert.Catch(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(file)));
                Assert.That(error, Is.Not.Null);
                TestContext.Out.WriteLine("OLD-READER csharp refused: " + error!.Message);
                Assert.That(error.ToString(), Does.Contain("cultcache.store.v3"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
