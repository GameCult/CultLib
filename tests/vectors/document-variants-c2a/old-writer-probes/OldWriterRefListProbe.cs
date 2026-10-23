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
    // Copied into a checkout of cultlib-unity-v1.0.60 (45c2f400) by old-reader-refusal.sh. Not part of this tree's suite.
    // It writes the store the 1.0.x writer produces for a list of record references (the shapes are RefListDriftTests'), and
    // is the only author of tests/vectors/ref-list-drift/v1-0-60-written.cc.
    public class OldWriterRefListProbe
    {
        [CultDocument("tests.reflist_target", "tests.reflist_target.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RefListTarget
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.reflist_holder", "tests.reflist_holder.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RefListHolder
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public CultRecordRef<RefListTarget>[] Items = Array.Empty<CultRecordRef<RefListTarget>>();
        }

        [Test]
        public async Task WritesAStoreWhoseRefListSlotIsAPlainValue()
        {
            var output = Environment.GetEnvironmentVariable("CULTLIB_GATE_OUT")!;
            Directory.CreateDirectory(output);
            var file = Path.Combine(output, "v1-0-60-ref-list.cc");
            File.Delete(file);

            using var cache = new CultCache();
            var store = new SingleFileMessagePackBackingStore(file);
            cache.AddBackingStore(store);
            await cache.AddAsync(new RefListTarget { Name = "alpha" });
            await cache.AddAsync(new RefListTarget { Name = "beta" });
            await cache.AddAsync(new RefListHolder
            {
                Name = "list",
                Items = new[] { new CultRecordRef<RefListTarget>(new CultRecordKey("alpha")), new CultRecordRef<RefListTarget>(new CultRecordKey("beta")) }
            });
            store.PushAll();

            var header = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file)).FormatVersion;
            Assert.That(header, Is.EqualTo("cultcache.store.v1"));
            TestContext.Out.WriteLine("OLD-WRITER 1.0.60 wrote " + file + " as " + header);
        }
    }
}
