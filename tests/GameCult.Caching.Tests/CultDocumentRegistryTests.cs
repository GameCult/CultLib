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

    // A second type for one schema is refused, typed, naming both and the claimed id, and the registry keeps what it had.
    private static void AssertSecondClaimRefused(Type first, Type second)
    {
        var schemaId = CultDocumentRegistry.ForTypes(new[] { second }).GetRequired(second).SchemaId;
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

    private static CultSchemaCatalogEntry[] CatalogOf(params Type[] types) =>
        types.Select(type => CultDocumentRegistry.ForTypes(new[] { type }).GetRequired(type).ToCatalogEntry()).ToArray();

    // Owner beats lister: a type that owns an id and one that lists it register together in either order (the v1 class kept
    // beside a v2 class declaring v1's id). A record under the id resolves to its owner; one under the lister's own id to the
    // lister; without the owner registered, the id resolves to the lister.
    [TestCase(false)]
    [TestCase(true)]
    public void AnOwnerAndATypeListingItsIdRegisterTogetherAndTheOwnerWins(bool reversed)
    {
        var owner = Emit("owns", "tests.registry.owns", "v1");
        var ownerId = CultDocumentRegistry.ForTypes(new[] { owner }).GetRequired(owner).SchemaId;
        var lister = Emit("lists", "tests.registry.owns", "v2", new[] { new Field("Extra", typeof(string), 0) }, new[] { ownerId });
        var listerId = CultDocumentRegistry.ForTypes(new[] { lister }).GetRequired(lister).SchemaId;
        var catalog = CatalogOf(owner, lister);

        var registry = CultDocumentRegistry.ForTypes(reversed ? new[] { lister, owner } : new[] { owner, lister });

        Assert.Multiple(() =>
        {
            Assert.That(registry.AllDescriptors.Select(descriptor => descriptor.DocumentType), Is.EquivalentTo(new[] { owner, lister }));
            Assert.That(registry.ResolvePersistedSchema(ownerId, catalog).DocumentType, Is.EqualTo(owner));
            Assert.That(registry.ResolvePersistedSchema(listerId, catalog).DocumentType, Is.EqualTo(lister));
            Assert.That(CultDocumentRegistry.ForTypes(new[] { lister }).ResolvePersistedSchema(ownerId, catalog).DocumentType, Is.EqualTo(lister));
        });
    }

    // Two types listing one id register together in either order. With an owner registered, a record under the id is the
    // owner's; with none, the id names no single type, and a record under it is refused, typed, naming the listers.
    [TestCase(false)]
    [TestCase(true)]
    public void TwoTypesListingOneIdRegisterAndARecordUnderItNeedsAnOwner(bool reversed)
    {
        var owner = Emit("shared_owner", "tests.registry.shared", "v1");
        var sharedId = CultDocumentRegistry.ForTypes(new[] { owner }).GetRequired(owner).SchemaId;
        var a = Emit("declares_a", "tests.registry.declares_a", "v1", compatibleSchemaIds: new[] { sharedId });
        var b = Emit("declares_b", "tests.registry.declares_b", "v1", compatibleSchemaIds: new[] { sharedId });
        var listers = reversed ? new[] { b, a } : new[] { a, b };
        var catalog = CatalogOf(owner);

        var ambiguous = CultDocumentRegistry.ForTypes(listers);
        var refusal = Assert.Throws<CultSchemaConflictException>(() => ambiguous.ResolvePersistedSchema(sharedId, catalog))!;
        Assert.Multiple(() =>
        {
            Assert.That(refusal.SchemaId, Is.EqualTo(sharedId));
            Assert.That(refusal.SchemaNames, Is.EquivalentTo(new[] { "tests.registry.declares_a", "tests.registry.declares_b" }));
            Assert.That(refusal.Message, Does.Contain(a.FullName).And.Contain(b.FullName));
        });

        Assert.That(CultDocumentRegistry.ForTypes(listers.Append(owner)).ResolvePersistedSchema(sharedId, catalog).DocumentType, Is.EqualTo(owner));
        Assert.That(CultDocumentRegistry.ForTypes(listers.Prepend(owner)).ResolvePersistedSchema(sharedId, catalog).DocumentType, Is.EqualTo(owner));
    }

    // One type per schema, in every runtime: a second type of the same schema name and version is refused whichever registers
    // first, whether it is identical to the first, declares different compatible ids, or has different members.
    [TestCase("identical", false)]
    [TestCase("identical", true)]
    [TestCase("declaration", false)]
    [TestCase("declaration", true)]
    [TestCase("members", false)]
    [TestCase("members", true)]
    public void ASecondTypeForOneSchemaIsRefused(string difference, bool reversed)
    {
        var schemaName = $"tests.registry.claimed_twice_{difference}";
        var first = Emit($"first_{difference}", schemaName, "v1");
        var second = difference switch
        {
            "identical" => Emit($"second_{difference}", schemaName, "v1"),
            "declaration" => Emit($"second_{difference}", schemaName, "v1", compatibleSchemaIds: new[] { "tests.registry.older" }),
            _ => Emit($"second_{difference}", schemaName, "v1", new[] { new Field("Value", typeof(string), 0) }),
        };

        AssertSecondClaimRefused(reversed ? second : first, reversed ? first : second);
    }
}
