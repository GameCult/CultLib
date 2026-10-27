#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using RefListHolder = GameCult.Caching.Tests.RefListDriftTests.RefListHolder;
using RefListTarget = GameCult.Caching.Tests.RefListDriftTests.RefListTarget;

namespace GameCult.Caching.Tests
{
    // Copied into a checkout of cultlib-unity-v2.0.1 (b8fc5eb0) by old-reader-refusal.sh. Not part of this tree's suite.
    // It attaches the store the 1.0.60 writer produced and prints the REFLIST-SUMMARY line that
    // RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1 prints at head; 'gate csharp' diffs the two lines. The document
    // types are 2.0.1's own RefListDriftTests.RefListTarget and RefListHolder.
    public class OldReaderRefListProbe
    {
        // RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1 is this test's body at head; the two must stay the same code.
        [Test]
        public async Task A_1_0_60_Written_Store_Attaches_As_Compatible_Drift()
        {
            var copy = Path.Combine(Path.GetTempPath(), $"reflist-gate-{Guid.NewGuid():N}.cc");
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("CULTLIB_GATE_OUT")!, "v1-0-60-ref-list.cc"), copy);
            try
            {
                string[] warnings;
                using (var cache = new CultCache())
                {
                    var store = new SingleFileMessagePackBackingStore(copy);
                    cache.AddBackingStore(store);
                    await cache.PullAllBackingStoresAsync();
                    warnings = store.LastSchemaMigrationReports.SelectMany(report => report.Warnings).Select(warning => warning.Code).Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray();
                    Assert.That(warnings, Does.Contain("gained_reference_semantics"));
                    var holder = cache.GetAll<RefListHolder>().Single();
                    Assert.That(holder.Items.Select(item => item.Key.Value), Is.EqualTo(new[] { "alpha", "beta" }));

                    // The write both runtimes make: the targets as they are, the holder with its list reversed.
                    foreach (var target in cache.GetAll<RefListTarget>().ToArray()) await cache.UpsertAsync(target, cache.TryGetHandle(target));
                    holder.Items = holder.Items.AsEnumerable().Reverse().ToArray();
                    await cache.UpsertAsync(holder, cache.TryGetHandle(holder));
                    await cache.FlushAsync();
                }

                var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(copy));
                var records = snapshot.Records.OrderBy(record => record.Key, StringComparer.Ordinal).Select(record => record.Key + "=" + record.SchemaId).ToArray();
                var listId = snapshot.Records.Single(record => record.Key == "list").SchemaId;
                var slot = snapshot.SchemaCatalog.Single(entry => entry.SchemaId == listId).Members.Single(member => member.MemberName == "Items");
                using var reopened = new CultCache();
                reopened.AddBackingStore(new SingleFileMessagePackBackingStore(copy));
                await reopened.PullAllBackingStoresAsync();
                var items = reopened.GetAll<RefListHolder>().Single().Items.Select(item => item.Key.Value).ToArray();
                Assert.That(items, Is.EqualTo(new[] { "beta", "alpha" }));
                Assert.That(reopened.Get<RefListTarget>(reopened.GetAll<RefListHolder>().Single().Items[0].Key)!.Name, Is.EqualTo("beta"));
                TestContext.Out.WriteLine("REFLIST-SUMMARY " + System.Text.Json.JsonSerializer.Serialize(new { records, items, itemsSlot = new { slot.IsReference, slot.TargetSchemaName }, warnings, header = snapshot.FormatVersion }));
            }
            finally
            {
                File.Delete(copy);
            }
    }
}
