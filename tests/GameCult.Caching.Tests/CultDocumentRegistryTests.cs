using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using static GameCult.Caching.Tests.EmittedDocumentTypes;

namespace GameCult.Caching.Tests;

[TestFixture]
public sealed class CultDocumentRegistryTests
{
    [Test]
    public void ForTypesContainsOnlyExplicitDocuments()
    {
        var requested = Emit("explicit", "tests.registry.explicit", "v1");
        var unrelated = Emit("unrelated", "tests.registry.unrelated", "v1");

        var registry = CultDocumentRegistry.ForTypes(new[] { requested, requested });

        Assert.That(registry.AllDescriptors.Select(value => value.DocumentType), Is.EqualTo(new[] { requested }));
        Assert.That(registry.AllDescriptors.Any(value => value.DocumentType == unrelated), Is.False);
    }

    [Test]
    public void IdenticalTypeRegistrationIsIdempotent()
    {
        var registry = new CultDocumentRegistry();
        var type = Emit("idempotent", "tests.registry.idempotent", "v1");

        var first = registry.GetRequired(type);
        var second = registry.GetRequired(type);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void ExactWireCompatibleAliasRegistersByTypeWithOneCanonicalSchemaDescriptor()
    {
        var registry = new CultDocumentRegistry();
        var firstType = Emit("alias_owner", "tests.registry.alias", "v1");
        var aliasType = Emit("alias_claimant", "tests.registry.alias", "v1");

        var canonical = registry.GetRequired(firstType);
        var alias = registry.GetRequired(aliasType);

        Assert.Multiple(() =>
        {
            Assert.That(alias.DocumentType, Is.EqualTo(aliasType));
            Assert.That(alias.SchemaId, Is.EqualTo(canonical.SchemaId));
            Assert.That(registry.GetRequired(aliasType), Is.SameAs(alias));
            Assert.That(registry.GetRequiredBySchemaId(canonical.SchemaId), Is.SameAs(canonical));
            Assert.That(
                registry.AllDescriptors.Count(descriptor => descriptor.SchemaId == canonical.SchemaId),
                Is.EqualTo(2));
        });
    }

    [Test]
    public void DifferentVersionsSharingSchemaNameRegisterWithoutReflectionOrderSelection()
    {
        var registry = new CultDocumentRegistry();
        var firstType = Emit("version_one", "tests.registry.versioned", "v1");
        var secondType = Emit("version_two", "tests.registry.versioned", "v2");

        var first = registry.GetRequired(firstType);
        var second = registry.GetRequired(secondType);

        Assert.Multiple(() =>
        {
            Assert.That(registry.GetRequiredBySchemaId(first.SchemaId), Is.SameAs(first));
            Assert.That(registry.GetRequiredBySchemaId(second.SchemaId), Is.SameAs(second));
        });

        var persisted = new CultSchemaCatalogEntry
        {
            SchemaId = "tests.registry.versioned.unknown",
            SchemaName = "tests.registry.versioned",
            SchemaVersion = "legacy"
        };
        Assert.That(
            () => registry.ResolvePersistedSchema(persisted.SchemaId, new[] { persisted }),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("ambiguous across local versions")
                .And.Message.Contains("v1")
                .And.Message.Contains("v2"));
    }

    [Test]
    public void IncompatibleTypesClaimingSameSchemaNameAndVersionFailBeforeMutation()
    {
        var registry = new CultDocumentRegistry();
        var firstType = Emit("layout_owner", "tests.registry.layout_collision", "v1");
        var secondType = Emit(
            "layout_claimant",
            "tests.registry.layout_collision",
            "v1",
            new[] { new Field("Value", typeof(string), 0) });
        var first = registry.GetRequired(firstType);

        Assert.That(
            () => registry.GetRequired(secondType),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("schema name 'tests.registry.layout_collision' version 'v1'")
                .And.Message.Contains(firstType.FullName)
                .And.Message.Contains(secondType.FullName));
        Assert.That(registry.GetRequiredBySchemaId(first.SchemaId), Is.SameAs(first));
        Assert.That(registry.AllDescriptors.Any(descriptor => descriptor.DocumentType == secondType), Is.False);
    }

    [Test]
    public void ConcurrentRefreshAndReadsObserveCompleteRegistrySnapshots()
    {
        var registry = new CultDocumentRegistry();
        var type = Emit("refresh_visible", "tests.registry.refresh_visible", "v1");
        var descriptor = registry.GetRequired(type);

        Parallel.For(0, 20, _ =>
        {
            registry.Refresh();
            Assert.That(registry.GetRequired(type).SchemaId, Is.EqualTo(descriptor.SchemaId));
            Assert.That(registry.GetRequiredBySchemaId(descriptor.SchemaId).SchemaId, Is.EqualTo(descriptor.SchemaId));
        });
    }

    // One type carries each schema id, owned or declared compatible, whichever order the types register in: a second claimant
    // is refused, typed, naming both, and the registry keeps what it had.
    private static void AssertSecondClaimRefused(Type first, Type second, string schemaId)
    {
        var refusal = Assert.Throws<CultSchemaConflictException>(() => CultDocumentRegistry.ForTypes(new[] { first, second }))!;
        Assert.Multiple(() =>
        {
            Assert.That(refusal.SchemaId, Is.EqualTo(schemaId));
            Assert.That(refusal.Message, Does.Contain(first.FullName).And.Contain(second.FullName));
        });

        var registry = CultDocumentRegistry.ForTypes(new[] { first });
        Assert.That(() => registry.GetRequired(second), Throws.TypeOf<CultSchemaConflictException>());
        Assert.That(registry.AllDescriptors.Select(descriptor => descriptor.DocumentType), Is.EqualTo(new[] { first }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TwoTypesDeclaringOneCompatibleIdAreRefused(bool reversed)
    {
        var a = Emit("declares_a", "tests.registry.declares_a", "v1", compatibleSchemaIds: new[] { "tests.registry.shared" });
        var b = Emit("declares_b", "tests.registry.declares_b", "v1", compatibleSchemaIds: new[] { "tests.registry.shared" });

        AssertSecondClaimRefused(reversed ? b : a, reversed ? a : b, "tests.registry.shared");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ATypeDeclaringAnIdAnotherTypeOwnsIsRefused(bool reversed)
    {
        var owner = Emit("owns", "tests.registry.owns", "v1");
        var ownerId = CultDocumentRegistry.ForTypes(new[] { owner }).GetRequired(owner).SchemaId;
        var lister = Emit("lists", "tests.registry.lists", "v1", compatibleSchemaIds: new[] { ownerId });

        AssertSecondClaimRefused(reversed ? lister : owner, reversed ? owner : lister, ownerId);
    }

    // The same schema declaring different compatible ids is not an alias: whichever registered second would lose its declaration.
    [TestCase(false)]
    [TestCase(true)]
    public void TheSameSchemaWithADifferentDeclarationIsNotAnAlias(bool reversed)
    {
        var bare = Emit("bare", "tests.registry.declared_alias", "v1");
        var declaring = Emit("declaring", "tests.registry.declared_alias", "v1", compatibleSchemaIds: new[] { "tests.registry.older" });
        var schemaId = CultDocumentRegistry.ForTypes(new[] { bare }).GetRequired(bare).SchemaId;

        AssertSecondClaimRefused(reversed ? declaring : bare, reversed ? bare : declaring, schemaId);
    }

    [Test]
    public void TheSameSchemaWithTheSameDeclarationIsAnAlias()
    {
        var first = Emit("declaring_first", "tests.registry.same_declaration", "v1", compatibleSchemaIds: new[] { "tests.registry.same_older" });
        var second = Emit("declaring_second", "tests.registry.same_declaration", "v1", compatibleSchemaIds: new[] { "tests.registry.same_older" });

        var registry = CultDocumentRegistry.ForTypes(new[] { first, second });

        Assert.That(registry.GetRequired(second).SchemaId, Is.EqualTo(registry.GetRequired(first).SchemaId));
    }
}
