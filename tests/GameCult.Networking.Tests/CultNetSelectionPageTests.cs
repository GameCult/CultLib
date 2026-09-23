#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // R-I (docs/cultnet-selection-cut.md): SelectPage/CreateSelectionResponse had no test at all before
    // this cut's fix batch, and Soul's whole-cut pass found two untested mutants survive 196/196:
    // dropping `wantDocument &&` in ToEdge (a header-projection edge would then carry the document's
    // payload bytes anyway), and always emitting Edges instead of `selection.HasHop ? ... : null` (a
    // hop-less selection's page would then carry an empty Edges array instead of no Edges member).
    public sealed class CultNetSelectionPageTests
    {
        private static (CultCache cache, CultNetDocumentRegistry documents) BuildFixture()
        {
            var cache = new CultCache();
            var documents = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PageFixtureLeaf>(cache.Registry))
                .Register(CultNetDocumentBinding.ForDocument<PageFixtureCiter>(cache.Registry));
            return (cache, documents);
        }

        private static async Task<CultRecordKey> AddLeafAsync(CultCache cache, string key, string kind)
        {
            var recordKey = new CultRecordKey(key);
            await cache.AddAsync(
                new PageFixtureLeaf { Name = key, Kind = kind },
                new CultRecordHandle<PageFixtureLeaf>(recordKey));
            return recordKey;
        }

        // Kills Soul's ToEdge mutant (CultNetDocumentRegistry.cs, ~:468): dropping `wantDocument &&`
        // from `if (wantDocument && edge.Payload != null)` would leak the dictionary edge's payload
        // bytes into a header-projection page, which carries no payloads by contract.
        [Test]
        public async Task CreateSelectionResponse_UnderHeaderProjection_CarriesNoEdgePayload()
        {
            var (cache, documents) = BuildFixture();
            var target = await AddLeafAsync(cache, "leaf-target", "weapon");
            await cache.AddAsync(
                new PageFixtureCiter { Name = "citer-1", Components = new Dictionary<CultRecordRef<PageFixtureLeaf>, float> { [new CultRecordRef<PageFixtureLeaf>(target)] = 2.5f } },
                new CultRecordHandle<PageFixtureCiter>(new CultRecordKey("citer-1")));

            var selection = new CultNetSelection
            {
                Cited = new CultNetIncoming { Role = "Components", Exists = true },
                Projection = CultNetSelectionProjections.Header
            };
            var response = documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1);

            Assert.That(response.Edges, Is.Not.Null.And.Not.Empty, "a hop-bearing selection with a match must carry the edge it traversed");
            foreach (var edge in response.Edges!)
            {
                Assert.That(edge.PayloadEncoding, Is.Null, "header projection must not carry an edge's payload encoding");
                Assert.That(edge.Payload, Is.Null, "header projection must not carry an edge's payload bytes");
            }
        }

        // The positive case for the same rule: document projection does carry the payload.
        [Test]
        public async Task CreateSelectionResponse_UnderDocumentProjection_CarriesEdgePayload()
        {
            var (cache, documents) = BuildFixture();
            var target = await AddLeafAsync(cache, "leaf-target", "weapon");
            await cache.AddAsync(
                new PageFixtureCiter { Name = "citer-1", Components = new Dictionary<CultRecordRef<PageFixtureLeaf>, float> { [new CultRecordRef<PageFixtureLeaf>(target)] = 2.5f } },
                new CultRecordHandle<PageFixtureCiter>(new CultRecordKey("citer-1")));

            var selection = new CultNetSelection
            {
                Cited = new CultNetIncoming { Role = "Components", Exists = true },
                Projection = CultNetSelectionProjections.Document
            };
            var response = documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1);

            Assert.That(response.Edges, Is.Not.Null.And.Not.Empty);
            Assert.That(response.Edges![0].PayloadEncoding, Is.EqualTo("messagepack"));
            Assert.That(response.Edges![0].Payload, Is.Not.Null.And.Not.Empty);
        }

        // Kills Soul's SelectPage/CreateSelectionResponse mutant (CultNetDocumentRegistry.cs, ~:378):
        // always emitting `Edges = ...` instead of `selection.HasHop ? ... : null` would give a
        // hop-less selection's page an empty Edges array (Is.Empty) instead of no Edges member at all
        // (Is.Null) - a mutant a shape-only "no edges reported" assertion cannot tell apart from the rule.
        [Test]
        public async Task CreateSelectionResponse_WithoutAHop_CarriesNoEdgesMember()
        {
            var (cache, documents) = BuildFixture();
            await AddLeafAsync(cache, "leaf-only", "weapon");

            var selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document };
            var response = documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1);

            Assert.That(response.Documents, Has.Length.EqualTo(1));
            Assert.That(response.Edges, Is.Null);
        }

        // SelectPage/CreateSelectionResponse's own paging and matched-total contract (R-G/C5): a
        // limited page reports the whole matching count, not the page's row count, and carries a
        // cursor while more rows remain.
        [Test]
        public async Task CreateSelectionResponse_ReportsTotalMatchedNotPageLength()
        {
            var (cache, documents) = BuildFixture();
            await AddLeafAsync(cache, "leaf-1", "weapon");
            await AddLeafAsync(cache, "leaf-2", "weapon");
            await AddLeafAsync(cache, "leaf-3", "weapon");

            var selection = new CultNetSelection
            {
                Fields = new[] { new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "weapon" } } },
                Limit = 2
            };
            var response = documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: static (_, _) => 1, asOf: 1);

            Assert.That(response.Headers, Has.Length.EqualTo(2), "the page itself is capped at the limit");
            Assert.That(response.Matched, Is.EqualTo(3), "matched is the selection's whole matching count, not this page's length");
            Assert.That(response.Next, Is.Not.Null.And.Not.Empty, "a cursor is minted while more rows remain");
        }

        [CultDocument("selection.page-fixture.leaf", "selection.page-fixture.leaf.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class PageFixtureLeaf
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultIndex("kind")]
            public string Kind = string.Empty;
        }

        [CultDocument("selection.page-fixture.citer", "selection.page-fixture.citer.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class PageFixtureCiter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(PageFixtureLeaf), many: true)]
            public Dictionary<CultRecordRef<PageFixtureLeaf>, float> Components = new();
        }
    }
}
