#nullable enable
using System;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The one per-key predicate of write ownership (variants:ruling:write-ownership-by-written-type, own-entry-reads-whole), over
    // catalogs the production types publish. Each refusal is paired with the accepted case that differs in only the one thing the
    // rule looks at. Where the predicate is wired in is WriteOwnershipTests.
    public class WrittenOwnsTests
    {
        // One schema name at three versions, nothing declared: N2 reads N1's records whole (it adds a slot) and Wide's only by
        // dropping a slot; Renamed has N2's exact shape under another schema name.
        [CultDocument("tests.wo_n", "tests.wo_n.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WoN1
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
        }

        [CultDocument("tests.wo_n", "tests.wo_n.v2")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WoN2
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
            [Key(2)] public string Colour = string.Empty;
        }

        [CultDocument("tests.wo_n", "tests.wo_n.v3")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WoNWide
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
            [Key(2)] public string Colour = string.Empty;
            [Key(3)] public string Extra = string.Empty;
        }

        [CultDocument("tests.wo_nx", "tests.wo_nx.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WoNRenamed
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public int Value;
            [Key(2)] public string Colour = string.Empty;
        }

        private static CultSchemaCatalogEntry EntryOf<T>() where T : class =>
            CultDocumentRegistry.ForTypes(new[] { typeof(T) }).GetRequired<T>().ToCatalogEntry();

        private static CultSchemaCatalogEntry Listing(CultSchemaCatalogEntry entry, params string[] ids) =>
            new()
            {
                SchemaId = entry.SchemaId,
                ContentHash = entry.ContentHash,
                SchemaName = entry.SchemaName,
                SchemaVersion = entry.SchemaVersion,
                CanonicalSchemaJson = entry.CanonicalSchemaJson,
                Members = entry.Members,
                CompatibleSchemaIds = ids,
            };

        [Test]
        public void WrittenOwnsDeclaredIdsAreTheEntrysOwnOrAnyItListsNotOnlyTheFirst()
        {
            var a = EntryOf<WriteOwnershipTests.WoA>();
            var b = EntryOf<WriteOwnershipTests.WoB>();
            var catalog = new[] { a };

            Assert.That(CultDocumentRegistry.WrittenOwns(a, a.SchemaId, catalog), Is.True, "its own id");
            Assert.That(CultDocumentRegistry.WrittenOwns(b, a.SchemaId, catalog), Is.False, "same shape, another schema: not its record");
            Assert.That(CultDocumentRegistry.WrittenOwns(Listing(b, b.SchemaId, a.SchemaId), a.SchemaId, catalog), Is.True, "listed last");
            Assert.That(CultDocumentRegistry.WrittenOwns(Listing(b, a.SchemaId, b.SchemaId), a.SchemaId, catalog), Is.True, "listed first");
            Assert.That(CultDocumentRegistry.WrittenOwns(Listing(b, b.SchemaId, "tests.wo.other"), a.SchemaId, catalog), Is.False, "lists another id");
            Assert.That(CultDocumentRegistry.WrittenOwns(a, a.SchemaId, Array.Empty<CultSchemaCatalogEntry>()), Is.True, "declared needs no catalog");
        }

        [Test]
        public void WrittenOwnsAnOlderIdOfItsOwnNameOnlyWhenItReadsTheRecordWhole()
        {
            var n1 = EntryOf<WoN1>();
            var n2 = EntryOf<WoN2>();
            var wide = EntryOf<WoNWide>();
            var renamed = EntryOf<WoNRenamed>();

            Assert.That(CultDocumentRegistry.WrittenOwns(n2, n1.SchemaId, new[] { n1 }), Is.True, "same name, reads whole");
            Assert.That(CultDocumentRegistry.WrittenOwns(renamed, n1.SchemaId, new[] { n1 }), Is.False, "the same shape under another name");
            Assert.That(CultDocumentRegistry.WrittenOwns(n2, wide.SchemaId, new[] { wide }), Is.False, "same name, drops a persisted slot");
            Assert.That(CultDocumentRegistry.WrittenOwns(n2, "tests.wo.unpublished", new[] { n1 }), Is.False, "an id no catalog entry publishes");
            Assert.That(CultDocumentRegistry.WrittenOwns(n2, n1.SchemaId, new[] { n1, wide }), Is.True, "another entry in the catalog changes nothing");
        }
    }
}
