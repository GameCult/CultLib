#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Caching.MessagePack.Generator;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Every shape compiles through the generator; its generated descriptor and codec must equal the reflective
    // descriptor and MessagePack's own dynamic payload, or both paths must reject it with the same message.
    public class DocumentShapeSweepTests
    {
        // Color sits in a namespace: global-namespace member types name differently in the generator and CultSchemaTypeNames.
        private const string Prelude = "using System.Collections.Generic;\nusing GameCult.Caching;\nusing MessagePack;\nusing Shapes;\nnamespace Shapes { public enum Color { Red, Blue } }\n";

        [Test]
        public void FlatKeyedDocument() => Accept(@"
[CultDocument(""sweep.plain"", ""sweep.plain.v1"")]
[MessagePackObject]
public class Plain
{
    [Key(0)] [CultName] public string Name { get; set; } = """";
    [Key(1)] public int? Maybe;
    [Key(2)] public Color Tint { get; set; }
    [Key(3)] public List<string> Tags = new List<string>();
    [Key(4)] public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
    [IgnoreMember] public int Skip { get; set; }
}
public static class Samples
{
    public static object Plain() => new Plain { Name = ""p"", Maybe = 7, Tint = Color.Blue, Tags = { ""a"" }, Counts = { [""x""] = 1 }, Skip = 9 };
}", "Plain");

        [Test]
        public void DerivedDocument() => Accept(@"
[CultDocument(""sweep.gear"", ""sweep.gear.v1"")]
[MessagePackObject]
public class Gear { [Key(0)] public string Name { get; set; } = """"; }
[CultDocument(""sweep.weapon"", ""sweep.weapon.v1"")]
[MessagePackObject]
public sealed class Weapon : Gear { [Key(1)] public int Damage { get; set; } }
public static class Samples
{
    public static object Gear() => new Gear { Name = ""helm"" };
    public static object Weapon() => new Weapon { Name = ""lance"", Damage = 4 };
}", "Gear", "Weapon");

        [Test]
        public void NameAndIndexOnABaseSharedByTwoDocuments() => Accept(@"
[CultDocument(""sweep.gear"", ""sweep.gear.v1"")]
[MessagePackObject]
public class Gear
{
    [Key(0)] [CultName] public string Name { get; set; } = """";
    [Key(1)] [CultIndex(""slot"")] public string Slot { get; set; } = """";
}
[CultDocument(""sweep.weapon"", ""sweep.weapon.v1"")]
[MessagePackObject]
public sealed class Weapon : Gear { [Key(2)] public int Damage { get; set; } }
public static class Samples
{
    public static object Gear() => new Gear { Name = ""helm"", Slot = ""head"" };
    public static object Weapon() => new Weapon { Name = ""lance"", Slot = ""hand"", Damage = 4 };
}", "Gear", "Weapon");

        [Test]
        public void OverrideKeepsBaseKey()
        {
            var payloads = Accept(@"
[MessagePackObject]
public class OvrSameBase { [Key(0)] public virtual string Name { get; set; } = """"; [Key(2)] public virtual int Z { get; set; } }
[CultDocument(""sweep.ovr_same"", ""sweep.ovr_same.v1"")]
[MessagePackObject]
public sealed class OvrSame : OvrSameBase
{
    [Key(0)] public override string Name { get; set; } = """";
    public override int Z { get; set; }
    [Key(1)] public int X { get; set; }
}
public static class Samples
{
    public static object OvrSame() => new OvrSame { Name = ""n"", X = 3, Z = 5 };
}", "OvrSame");

            // Captured from MessagePack's DynamicObjectResolver: [Name, X, Z].
            Assert.That(Convert.ToHexString(payloads["OvrSame"]), Is.EqualTo("93A16E0305"));
        }

        // MessagePack's DynamicObjectResolver reads the base declaration: it throws "key is duplicated" for this shape.
        [Test]
        public void OverrideReKeyed() => Reject(@"
[MessagePackObject]
public class OvrDiffBase { [Key(0)] public virtual string Name { get; set; } = """"; }
[CultDocument(""sweep.ovr_diff"", ""sweep.ovr_diff.v1"")]
[MessagePackObject]
public sealed class OvrDiff : OvrDiffBase
{
    [Key(2)] public override string Name { get; set; } = """";
    [Key(1)] public int X { get; set; }
    [Key(0)] public int Y { get; set; }
}",
            "OvrDiff",
            "Cult document OvrDiff member OvrDiff.Name overrides OvrDiffBase.Name with a different [Key] or [IgnoreMember]; MessagePack reads the base declaration's, so an override must repeat or omit them.");

        // MessagePack's DynamicObjectResolver keeps the base [Key(1)] and serializes Name despite the override's [IgnoreMember].
        [Test]
        public void OverrideIgnored() => Reject(@"
[MessagePackObject]
public class OvrIgnoreBase { [Key(1)] public virtual string Name { get; set; } = """"; }
[CultDocument(""sweep.ovr_ignore"", ""sweep.ovr_ignore.v1"")]
[MessagePackObject]
public sealed class OvrIgnore : OvrIgnoreBase
{
    [IgnoreMember] public override string Name { get; set; } = """";
    [Key(0)] public int X { get; set; }
}",
            "OvrIgnore",
            "Cult document OvrIgnore member OvrIgnore.Name overrides OvrIgnoreBase.Name with a different [Key] or [IgnoreMember]; MessagePack reads the base declaration's, so an override must repeat or omit them.");

        [Test]
        public void NewHiddenField() => Reject(@"
public class NewFieldBase { [Key(0)] public string Name = ""base""; }
[CultDocument(""sweep.new_field"", ""sweep.new_field.v1"")]
[MessagePackObject]
public sealed class NewField : NewFieldBase { [Key(0)] public new string Name = """"; [Key(1)] public int X; }",
            "NewField",
            "Cult document NewField members NewField.Name and NewFieldBase.Name share [Key(0)]; every persisted member needs a distinct [Key(n)].");

        [Test]
        public void NewHiddenProperty() => Reject(@"
public class NewPropBase { [Key(0)] public string Name { get; set; } = ""base""; }
[CultDocument(""sweep.new_prop"", ""sweep.new_prop.v1"")]
[MessagePackObject]
public sealed class NewProp : NewPropBase { [Key(0)] public new string Name { get; set; } = """"; [Key(1)] public int X { get; set; } }",
            "NewProp",
            "Cult document NewProp members NewProp.Name and NewPropBase.Name share [Key(0)]; every persisted member needs a distinct [Key(n)].");

        [Test]
        public void DuplicateSlot() => Reject(@"
[CultDocument(""sweep.dup"", ""sweep.dup.v1"")]
[MessagePackObject]
public sealed class Dup { [Key(0)] public int B; [Key(0)] public int A; }",
            "Dup",
            "Cult document Dup members Dup.A and Dup.B share [Key(0)]; every persisted member needs a distinct [Key(n)].");

        [Test]
        public void InheritedPrivateSetter() => Reject(@"
public class PrivSetBase { [Key(0)] public string Name { get; private set; } = ""fixed""; }
[CultDocument(""sweep.privset"", ""sweep.privset.v1"")]
[MessagePackObject]
public sealed class PrivSetDerived : PrivSetBase { [Key(1)] public int X { get; set; } }",
            "PrivSetDerived",
            "Cult document PrivSetDerived member Name is not writable; a persisted member needs a non-readonly field or a public or internal set accessor.");

        [Test]
        public void StringKey() => Reject(@"
[CultDocument(""sweep.string_key"", ""sweep.string_key.v1"")]
[MessagePackObject]
public sealed class StringKeyed { [Key(""name"")] public string Name { get; set; } = """"; }",
            "StringKeyed",
            "Cult document StringKeyed member Name has a string [Key]; string keys are not supported; use integer [Key(n)].");

        private static Dictionary<string, byte[]> Accept(string source, params string[] documentNames)
        {
            var assembly = Compile(source, out var generatorErrors);
            Assert.That(generatorErrors, Is.Empty);
            var samples = assembly.GetType("Samples")!;
            var payloads = new Dictionary<string, byte[]>();
            foreach (var name in documentNames)
            {
                var type = assembly.GetType(name)!;
                var sample = samples.GetMethod(name)!.Invoke(null, null)!;
                var generated = CultDocumentRegistry.ForTypes(new[] { type }).GetRequired(type);
                var reflective = CultDocumentRegistry.BuildDescriptor(type);
                Assert.That(generated.GeneratedPayloadSerializer, Is.Not.Null, $"{name}: no generated codec");
                Assert.That(generated.GeneratedPayloadDeserializer, Is.Not.Null, $"{name}: no generated decoder");

                var options = CultDocumentMessagePackSerialization.OptionsFor(assembly);
                var dynamicBytes = MessagePackSerializer.Serialize(type, sample, options);
                var generatedBytes = generated.GeneratedPayloadSerializer!(sample);
                var decoded = generated.GeneratedPayloadDeserializer!(generatedBytes);
                Assert.Multiple(() =>
                {
                    Assert.That(generated.SchemaId, Is.EqualTo(reflective.SchemaId), $"{name}: schema id");
                    Assert.That(generated.CanonicalSchemaJson, Is.EqualTo(reflective.CanonicalSchemaJson), $"{name}: schema json");
                    Assert.That(Catalog(generated), Is.EqualTo(Catalog(reflective)), $"{name}: catalog members");
                    Assert.That(generated.NameMember, Is.EqualTo(reflective.NameMember), $"{name}: name member");
                    Assert.That(generated.NameAccessor?.Invoke(sample), Is.EqualTo(reflective.NameAccessor?.Invoke(sample)), $"{name}: name value");
                    Assert.That(Indexes(generated, sample), Is.EqualTo(Indexes(reflective, sample)), $"{name}: index values");
                    Assert.That(Convert.ToHexString(generatedBytes), Is.EqualTo(Convert.ToHexString(dynamicBytes)), $"{name}: payload");
                    Assert.That(Convert.ToHexString(generated.GeneratedPayloadSerializer(decoded)), Is.EqualTo(Convert.ToHexString(generatedBytes)), $"{name}: round trip");
                    Assert.That(Convert.ToHexString(generated.GeneratedPayloadSerializer(MessagePackSerializer.Deserialize(type, dynamicBytes, options)!)),
                        Is.EqualTo(Convert.ToHexString(dynamicBytes)), $"{name}: dynamic round trip");
                });
                payloads[name] = generatedBytes;
            }

            return payloads;
        }

        private static void Reject(string source, string documentName, string expected)
        {
            var assembly = Compile(source, out var generatorErrors);
            var reflective = Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.BuildDescriptor(assembly.GetType(documentName)!))!;
            Assert.Multiple(() =>
            {
                Assert.That(generatorErrors.FirstOrDefault(), Is.EqualTo(expected), "generator");
                Assert.That(reflective.Message, Is.EqualTo(expected), "registry");
            });
        }

        private static string[] Catalog(CultDocumentDescriptor descriptor) =>
            descriptor.ToCatalogEntry().Members
                .Select(member => $"{member.Slot}:{member.MemberName}:{member.TypeName}:{member.IsReference}:{member.IsMany}:{member.TargetSchemaName}:{member.IsName}:{member.IndexAlias}")
                .ToArray();

        private static string[] Indexes(CultDocumentDescriptor descriptor, object sample) =>
            descriptor.IndexAccessors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value(sample)}").ToArray();

        private static Assembly Compile(string source, out string[] generatorErrors)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Append(typeof(CultDocumentAttribute).Assembly.Location)
                .Append(typeof(CultDocumentMessagePackSerialization).Assembly.Location)
                .Append(typeof(KeyAttribute).Assembly.Location)
                .Append(typeof(MessagePackSerializer).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                "Sweep_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(Prelude + source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            CSharpGeneratorDriver.Create(new CultDocumentMessagePackGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            generatorErrors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.GetMessage()).ToArray();

            using var stream = new MemoryStream();
            var emitted = output.Emit(stream);
            Assert.That(emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()), Is.Empty, "compile");
            return Assembly.Load(stream.ToArray());
        }
    }
}
