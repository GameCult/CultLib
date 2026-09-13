#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GameCult.Caching;
using GameCult.Caching.MessagePack.Generator;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class UnkeyedDocumentMemberTests
    {
        [Test]
        public void UnkeyedDocumentMemberIsRejected()
        {
            var reflective = Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.ForTypes(new[] { EmitUnkeyedDocument() }))!;

            const string source = @"
using GameCult.Caching;
using MessagePack;
[CultDocument(""tests.unkeyed"", ""tests.unkeyed.v1"")]
public class UnkeyedDocument
{
    [Key(0)] public string Name = string.Empty;
    public int Count;
}";
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Append(typeof(CultDocumentAttribute).Assembly.Location)
                .Append(typeof(KeyAttribute).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                "UnkeyedConsumer",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            CSharpGeneratorDriver.Create(new CultDocumentMessagePackGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

            var generated = diagnostics.Single(diagnostic => diagnostic.Id == "GCC001");
            Assert.That(generated.Severity, Is.EqualTo(DiagnosticSeverity.Error));
            Assert.That(generated.GetMessage(), Is.EqualTo(reflective.Message));
            Assert.That(reflective.Message, Does.Contain("UnkeyedDocument member Count has no [Key]"));
        }

        private static Type EmitUnkeyedDocument()
        {
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UnkeyedDocuments"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("UnkeyedDocuments");
            var type = module.DefineType("UnkeyedDocument", TypeAttributes.Public | TypeAttributes.Class);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
                new object[] { "tests.unkeyed", "tests.unkeyed.v1" }));
            type.DefineField("Name", typeof(string), FieldAttributes.Public)
                .SetCustomAttribute(new CustomAttributeBuilder(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { 0 }));
            type.DefineField("Count", typeof(int), FieldAttributes.Public);
            return type.CreateType()!;
        }
    }
}
