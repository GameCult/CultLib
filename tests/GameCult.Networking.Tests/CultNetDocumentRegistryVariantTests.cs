#nullable enable
using System;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // Q6 (docs/document-variants-cut.md C1): CultNet carries no variant deltas yet, so building a row for one is refused
    // loudly, on the v1 selection path and on the v0 snapshot path alike.
    public sealed class CultNetDocumentRegistryVariantTests
    {
        private static readonly CultRecordKey BaseKey = new("variant-fixture-base");
        private static readonly CultRecordKey VariantKey = new("variant-fixture-big");

        private static async Task<(CultCache Cache, CultNetDocumentRegistry Documents)> BuildFixtureAsync(bool withVariant)
        {
            var registry = CultDocumentRegistry.Shared;
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            var documents = new CultNetDocumentRegistry(registry)
                .Register(CultNetDocumentBinding.ForDocument<VariantWireFixture>(registry));
            await cache.UpsertAsync(new VariantWireFixture { Name = "base", Power = 1 }, new CultRecordHandle<VariantWireFixture>(BaseKey));
            if (withVariant)
                await cache.UpsertVariantAsync(VariantKey, BaseKey, new[]
                {
                    cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big"),
                    cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Power), 9)
                });
            return (cache, documents);
        }

        [Test]
        public async Task SelectionResponseRefusesAVariantRowNamingIt()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: true);
            var selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document };

            var error = Assert.Throws<NotSupportedException>(() =>
                documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1))!;

            Assert.That(error.Message, Does.Contain(VariantKey.Value).And.Contain(BaseKey.Value));
        }

        [Test]
        public async Task RawSnapshotResponseRefusesAVariantRowNamingIt()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: true);

            var error = Assert.Throws<NotSupportedException>(() => documents.CreateRawSnapshotResponse(cache, "m1"))!;

            Assert.That(error.Message, Does.Contain(VariantKey.Value));
        }

        [Test]
        public async Task ACacheWithoutVariantsStillAnswersBothPaths()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: false);
            var selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document };

            Assert.That(documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1).Documents, Has.Length.EqualTo(1));
            Assert.That(documents.CreateRawSnapshotResponse(cache, "m2").Documents, Has.Length.EqualTo(1));
        }

        [CultDocument("variants.wire-fixture", "variants.wire-fixture.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class VariantWireFixture
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Power;
        }
    }
}
