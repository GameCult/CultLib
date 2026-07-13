using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using NUnit.Framework;

namespace GameCult.Caching.Tests;

[TestFixture]
public sealed class CultDocumentRegistryTests
{
    [Test]
    public void IdenticalTypeRegistrationIsIdempotent()
    {
        var registry = new CultDocumentRegistry();
        var type = CreateDocumentType("idempotent", "tests.registry.idempotent", "v1");

        var first = registry.GetRequired(type);
        var second = registry.GetRequired(type);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void ExactWireCompatibleAliasRegistersByTypeWithOneCanonicalSchemaDescriptor()
    {
        var registry = new CultDocumentRegistry();
        var firstType = CreateDocumentType("alias_owner", "tests.registry.alias", "v1");
        var aliasType = CreateDocumentType("alias_claimant", "tests.registry.alias", "v1");

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
        var firstType = CreateDocumentType("version_one", "tests.registry.versioned", "v1");
        var secondType = CreateDocumentType("version_two", "tests.registry.versioned", "v2");

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
        var firstType = CreateDocumentType("layout_owner", "tests.registry.layout_collision", "v1");
        var secondType = CreateDocumentType(
            "layout_claimant",
            "tests.registry.layout_collision",
            "v1",
            addStringField: true);
        var first = registry.GetRequired(firstType);

        Assert.That(
            () => registry.GetRequired(secondType),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("schema name and version 'tests.registry.layout_collision' version 'v1'")
                .And.Message.Contains(firstType.FullName)
                .And.Message.Contains(secondType.FullName));
        Assert.That(registry.GetRequiredBySchemaId(first.SchemaId), Is.SameAs(first));
        Assert.That(registry.AllDescriptors.Any(descriptor => descriptor.DocumentType == secondType), Is.False);
    }

    private static Type CreateDocumentType(
        string typeName,
        string schemaName,
        string schemaVersion,
        bool addStringField = false)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("GameCult.Caching.RegistryTests." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");
        var type = module.DefineType(
            typeName,
            TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);
        var attributeConstructor = typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
        type.SetCustomAttribute(new CustomAttributeBuilder(
            attributeConstructor,
            new object[] { schemaName, schemaVersion }));
        if (addStringField)
        {
            type.DefineField("Value", typeof(string), FieldAttributes.Public);
        }

        return type.CreateType()!;
    }
}
