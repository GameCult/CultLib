#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Copied into a checkout of cultlib-unity-v2.0.1 (b8fc5eb0) by old-reader-refusal.sh. Not part of this tree's suite.
    // It attaches the store the 1.0.60 writer produced and prints the REFLIST-SUMMARY line that
    // RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1 prints at head; 'gate csharp' diffs the two lines. The document
    // types are 2.0.1's own RefListDriftTests.RefListTarget and RefListHolder.
    public class OldReaderRefListProbe
    {
        [Test]
        public async Task A_1_0_60_Written_Store_Attaches_As_Compatible_Drift()
        {
            var copy = Path.Combine(Path.GetTempPath(), $"reflist-gate-{Guid.NewGuid():N}.cc");
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("CULTLIB_GATE_OUT")!, "v1-0-60-ref-list.cc"), copy);
            try
            {
                using var cache = new CultCache();
                var store = new SingleFileMessagePackBackingStore(copy);
                cache.AddBackingStore(store);
                await cache.PullAllBackingStoresAsync();
                var warnings = store.LastSchemaMigrationReports.SelectMany(report => report.Warnings).Select(warning => warning.Code).Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray();
                Assert.That(warnings, Does.Contain("gained_reference_semantics"));
                var items = cache.GetAll<RefListDriftTests.RefListHolder>().Single().Items.Select(item => item.Key.Value).ToArray();
                Assert.That(items, Is.EqualTo(new[] { "alpha", "beta" }));

                store.PushAll();
                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(copy));
                var records = snapshot.Records.OrderBy(record => record.Key, StringComparer.Ordinal).Select(record => record.Key + "=" + record.SchemaId).ToArray();
                TestContext.Out.WriteLine("REFLIST-SUMMARY " + JsonSerializer.Serialize(new { records, items, warnings, header = snapshot.FormatVersion }));
            }
            finally
            {
                File.Delete(copy);
            }
        }
    }
}
