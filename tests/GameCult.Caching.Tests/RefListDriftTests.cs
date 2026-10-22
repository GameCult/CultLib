#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // 1.0.x persisted a list of record references (CultRecordRef<T>[]) as a plain value slot: isReference false, no target.
    // 2.0 classifies the same slot as a reference to T. The payload is the same array of key strings, so a store written by
    // 1.0.x opens: gaining reference classification on an otherwise identical slot is compatible drift. A reference that is
    // lost, retargeted, or that arrives with a different type or cardinality stays a refusal.
    public class RefListDriftTests
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

        // The reverse of the 1.0.x drift: a slot persisted as a reference list, declared by a later version as a plain value.
        [CultDocument("tests.reflist_loss", "tests.reflist_loss.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RefListLossBefore
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public CultRecordRef<RefListTarget>[] Items = Array.Empty<CultRecordRef<RefListTarget>>();
        }

        [CultDocument("tests.reflist_loss", "tests.reflist_loss.v2")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class RefListLossAfter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string[] Items = Array.Empty<string>();
        }

        private static string VectorPath(string name)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = Path.Combine(dir.FullName, "tests", "vectors", "ref-list-drift");
                if (Directory.Exists(vectors)) return Path.Combine(vectors, name);
            }

            throw new DirectoryNotFoundException($"tests/vectors/ref-list-drift not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        [Test]
        public async Task A_1_0_Store_With_A_Ref_List_Slot_Opens_And_Rewrites_Under_The_Reference_Schema()
        {
            var copy = Path.Combine(Path.GetTempPath(), $"reflist-drift-{Guid.NewGuid():N}.cc");
            File.Copy(VectorPath("old-store.cc"), copy);
            try
            {
                using var cache = new CultCache();
                var store = new SingleFileMessagePackBackingStore(copy);
                cache.AddBackingStore(store);
                await cache.PullAllBackingStoresAsync();
                Assert.That(
                    store.LastSchemaMigrationReports.Single(report => report.PersistedSchemaName == "tests.reflist_holder").Kind,
                    Is.EqualTo(CultSchemaMigrationKind.CompatibleDrift));

                var holder = cache.GetAll<RefListHolder>().Single();
                Assert.That(holder.Items.Select(item => item.Key.Value), Is.EqualTo(new[] { "alpha", "beta" }));
                Assert.That(cache.Get<RefListTarget>(holder.Items[0].Key)!.Name, Is.EqualTo("alpha"));

                // A write flushes the store under the 2.0 schema; the list reads back unchanged.
                await cache.AddAsync(new RefListTarget { Name = "gamma" });
                store.PushAll();
                using var reopened = new CultCache();
                reopened.AddBackingStore(new SingleFileMessagePackBackingStore(copy));
                await reopened.PullAllBackingStoresAsync();
                Assert.That(string.Join(",", reopened.GetAll<RefListTarget>().Select(target => target.Name).OrderBy(name => name)), Is.EqualTo("alpha,beta,gamma"));
                Assert.That(reopened.GetAll<RefListHolder>().Single().Items.Select(item => item.Key.Value).ToArray(), Is.EqualTo(new[] { "alpha", "beta" }));
            }
            finally
            {
                File.Delete(copy);
            }
        }

        private static CultSchemaMigrationReport Resolve(Action<CultSchemaMemberCatalogEntry> persistedItems)
        {
            var catalog = CultDocumentRegistry.Shared.GetRequired<RefListHolder>().ToCatalogEntry();
            catalog.SchemaId = "persisted.tests.reflist_holder.v1";
            catalog.ContentHash = "sha256:persisted-by-1.0.x";
            catalog.CompatibleSchemaIds = new[] { CultDocumentRegistry.Shared.GetRequired<RefListHolder>().SchemaId };
            persistedItems(catalog.Members.Single(member => member.Slot == 1));
            return CultDocumentRegistry.Shared.ResolvePersistedSchemaReport(catalog.SchemaId, new[] { catalog });
        }

        private static void AsWrittenBy10(CultSchemaMemberCatalogEntry member)
        {
            member.IsReference = false;
            member.TargetSchemaName = null;
        }

        [Test]
        public void A_Reference_Gained_On_An_Otherwise_Identical_Slot_Is_Compatible_Drift()
        {
            var report = Resolve(AsWrittenBy10);
            Assert.That(report.Kind, Is.EqualTo(CultSchemaMigrationKind.CompatibleDrift));
            Assert.That(report.Warnings.Select(warning => warning.Code), Does.Contain("gained_reference_semantics"));
        }

        [Test]
        public void A_Persisted_Reference_Retargeted_Or_Gaining_Over_A_Foreign_Target_Still_Refuses()
        {
            Assert.That(
                () => Resolve(member => member.TargetSchemaName = "tests.other_target"),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed target schema"));
            Assert.That(
                () => Resolve(member => { member.IsReference = false; member.TargetSchemaName = "tests.other_target"; }),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void A_Lost_Reference_Still_Refuses()
        {
            var registry = CultDocumentRegistry.Shared;
            var catalog = registry.GetRequired<RefListLossBefore>().ToCatalogEntry();
            catalog.SchemaId = "persisted.tests.reflist_loss.v1";
            catalog.ContentHash = "sha256:persisted-by-2.0";
            catalog.CompatibleSchemaIds = new[] { registry.GetRequired<RefListLossAfter>().SchemaId };
            catalog.Members.Single(member => member.Slot == 1).TypeName = "System.String[]";
            Assert.That(
                () => registry.ResolvePersistedSchemaReport(catalog.SchemaId, new[] { catalog }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed reference semantics"));
        }

        [Test]
        public void A_Gained_Reference_Does_Not_Excuse_A_Changed_Type_Or_Cardinality()
        {
            Assert.That(
                () => Resolve(member => { AsWrittenBy10(member); member.TypeName = "System.String[]"; }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed type"));
            Assert.That(
                () => Resolve(member => { AsWrittenBy10(member); member.IsMany = !member.IsMany; }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed cardinality"));
        }
    }
}
