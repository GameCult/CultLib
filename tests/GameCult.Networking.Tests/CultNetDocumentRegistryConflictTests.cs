#nullable enable
using System;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // F7 (operator, 2026-09-30): "Schema aliasing sounds like a really bad idea, I support retiring it." One binding owns a
    // document type and a schema id, in the reference runtime as in Rust, Kotlin and TypeScript.
    public sealed class CultNetDocumentRegistryConflictTests
    {
        private static CultNetDocumentBinding Bind<T>(string? schemaId = null) where T : class =>
            CultNetDocumentBinding.ForDocument<T>(CultDocumentRegistry.Shared, schemaId);

        // Two bindings stand ahead of the holder, so a conflict check that looks only at the first holder misses it.
        private static CultNetDocumentRegistry RegistryHolding(CultNetDocumentBinding holder) =>
            new CultNetDocumentRegistry(CultDocumentRegistry.Shared)
                .Register(Bind<ConflictFillerA>())
                .Register(Bind<ConflictFillerB>())
                .Register(holder);

        [Test]
        public void A_second_type_for_one_schema_id_is_refused_typed_in_either_order()
        {
            var first = Bind<ConflictNote>("f5.shared");
            var second = Bind<ConflictOtherNote>("f5.shared");
            foreach (var (held, claimant) in new[] { (first, second), (second, first) })
            {
                var registry = RegistryHolding(held);
                var error = Assert.Throws<CultSchemaConflictException>(() => registry.Register(claimant))!;
                Assert.That(error.SchemaId, Is.EqualTo("f5.shared"));
                Assert.That(error.SchemaNames, Is.EqualTo(new[] { held.DocumentType.FullName, claimant.DocumentType.FullName }));
                Assert.That(error.RecordKey, Is.EqualTo(string.Empty));
                Assert.That(error.Message, Does.Contain($"type \"{claimant.DocumentType.FullName}\""));
                // Nothing was bound: the schema id still resolves to the first type, and the claimant's type to nothing.
                Assert.That(registry.GetBySchemaId("f5.shared"), Is.SameAs(held));
                Assert.That(registry.GetByDocumentType(claimant.DocumentType), Is.Null);
            }
        }

        [Test]
        public void A_type_bound_to_one_schema_id_cannot_be_bound_to_another()
        {
            var own = Bind<ConflictNote>();
            var registry = RegistryHolding(own);
            var error = Assert.Throws<CultSchemaConflictException>(() => registry.Register(Bind<ConflictNote>("f5.elsewhere")))!;
            Assert.That(error.SchemaId, Is.EqualTo("f5.elsewhere"));
            Assert.That(registry.GetBySchemaId("f5.elsewhere"), Is.Null);
            Assert.That(registry.GetByDocumentType(typeof(ConflictNote)), Is.SameAs(own));
        }

        [Test]
        public void Binding_a_type_to_the_schema_id_it_holds_again_replaces_the_binding()
        {
            var registry = RegistryHolding(Bind<ConflictNote>());
            var again = Bind<ConflictNote>();
            registry.Register(again);
            Assert.That(registry.GetByDocumentType(typeof(ConflictNote)), Is.SameAs(again));
            Assert.That(registry.GetBySchemaId(again.SchemaId), Is.SameAs(again));
        }

        [CultDocument("f5.conflict-note", "f5.conflict-note.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ConflictNote
        {
            [Key(0)] public string Body = string.Empty;
        }

        [CultDocument("f5.conflict-other-note", "f5.conflict-other-note.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ConflictOtherNote
        {
            [Key(0)] public string Body = string.Empty;
        }

        [CultDocument("f5.conflict-filler-a", "f5.conflict-filler-a.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ConflictFillerA
        {
            [Key(0)] public string Body = string.Empty;
        }

        [CultDocument("f5.conflict-filler-b", "f5.conflict-filler-b.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class ConflictFillerB
        {
            [Key(0)] public string Body = string.Empty;
        }
    }
}
