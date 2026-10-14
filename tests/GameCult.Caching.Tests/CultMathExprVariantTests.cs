#nullable enable
using System;
using System.IO;
using CultMath.Expr;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // An ExprGraph is a plain value with no element-id lists. It survives a single-file cache, a variant inherits it,
    // a variant may override it whole, and an edit to the base reaches the inheriting variant.
    public class CultMathExprVariantTests
    {
        [CultDocument("tests.expr_variant", "tests.expr_variant.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class ExprDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public ExprGraph Graph = default!;
            [Key(2)] public float Scale;
        }

        private static ExprGraph Graph(float k)
        {
            var b = new ExprBuilder();
            var x = b.Input("x");
            var p = b.Param("k", k);
            b.Output("o", b.Call("mul", x, p));
            return b.Build();
        }

        private static byte[] Enc(ExprGraph g) => MessagePackSerializer.Serialize(g, CultDocumentMessagePackSerialization.Options);

        [Test]
        public void ExprGraphRoundTripsThroughACacheAndIsInheritedOrOverriddenByVariants()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cultlib-exprvariant-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "expr.cc");
                var registry = CultDocumentRegistry.ForTypes(new[] { typeof(ExprDoc) });
                var baseKey = new CultRecordKey("base");
                var scaledKey = new CultRecordKey("scaled");
                var regraphedKey = new CultRecordKey("regraphed");

                using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry }))
                {
                    cache.Commit(b => b.Upsert(typeof(ExprDoc), new ExprDoc { Name = "base", Graph = Graph(1.5f), Scale = 2f }, baseKey));
                    cache.Commit(b => b.UpsertVariant(scaledKey, baseKey, new[] { cache.Override<ExprDoc>(nameof(ExprDoc.Name), "scaled"), cache.Override<ExprDoc>(nameof(ExprDoc.Scale), 3f) }));
                    cache.Commit(b => b.UpsertVariant(regraphedKey, baseKey, new[] { cache.Override<ExprDoc>(nameof(ExprDoc.Name), "regraphed"), cache.Override<ExprDoc>(nameof(ExprDoc.Graph), Graph(4f)) }));
                }

                using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry }))
                {
                    var baseDoc = cache.Get<ExprDoc>(baseKey)!;
                    var scaled = cache.Get<ExprDoc>(scaledKey)!;
                    var regraphed = cache.Get<ExprDoc>(regraphedKey)!;

                    Assert.That(Enc(baseDoc.Graph), Is.EqualTo(Enc(Graph(1.5f))), "the base graph survives the file");
                    Assert.That(scaled.Scale, Is.EqualTo(3f));
                    Assert.That(Enc(scaled.Graph), Is.EqualTo(Enc(Graph(1.5f))), "a variant that does not override the graph inherits it");
                    Assert.That(Enc(regraphed.Graph), Is.EqualTo(Enc(Graph(4f))), "a variant that overrides the graph replaces it whole");
                    Assert.That(regraphed.Scale, Is.EqualTo(2f), "and keeps the rest of the base");

                    cache.Commit(b => b.Upsert(typeof(ExprDoc), new ExprDoc { Name = "base", Graph = Graph(9f), Scale = 2f }, baseKey));
                    Assert.That(Enc(cache.Get<ExprDoc>(scaledKey)!.Graph), Is.EqualTo(Enc(Graph(9f))), "an edit to the base reaches the inheriting variant");
                    Assert.That(Enc(cache.Get<ExprDoc>(regraphedKey)!.Graph), Is.EqualTo(Enc(Graph(4f))), "and not the overriding one");
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
