#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using MessagePack;

namespace GameCult.Caching.Tests
{
    // Document types built at run time, each in its own dynamic assembly. CultDocumentRegistry.Shared discovers every
    // [CultDocument] type in the loaded non-dynamic assemblies and refuses two that claim one schema id, so a fixture that
    // needs such a pair (a type and its alias, two versions that share an id) emits them here instead of declaring them.
    internal static class EmittedDocumentTypes
    {
        internal readonly record struct Field(string Name, Type Type, int Key, bool IsName = false);

        internal static Type Emit(string typeName, string schemaName, string schemaVersion, Field[]? fields = null, string[]? compatibleSchemaIds = null)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("GameCult.Caching.EmittedDocuments." + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType(typeName, TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);
            var documentConstructor = typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
            type.SetCustomAttribute(compatibleSchemaIds == null
                ? new CustomAttributeBuilder(documentConstructor, new object[] { schemaName, schemaVersion })
                : new CustomAttributeBuilder(
                    documentConstructor,
                    new object[] { schemaName, schemaVersion },
                    new[] { typeof(CultDocumentAttribute).GetProperty(nameof(CultDocumentAttribute.CompatibleSchemaIds))! },
                    new object[] { compatibleSchemaIds }));
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!,
                new object[] { false }));
            foreach (var field in fields ?? Array.Empty<Field>())
            {
                var builder = type.DefineField(field.Name, field.Type, FieldAttributes.Public);
                builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { field.Key }));
                if (field.IsName)
                    builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(CultNameAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
            }

            return type.CreateType()!;
        }

        // An instance of an emitted type with the named fields set.
        internal static object New(Type type, params (string Field, object? Value)[] values)
        {
            var document = Activator.CreateInstance(type)!;
            foreach (var (field, value) in values)
                type.GetField(field)!.SetValue(document, value);
            return document;
        }

        internal static object? Read(object document, string field) => document.GetType().GetField(field)!.GetValue(document);
    }
}
