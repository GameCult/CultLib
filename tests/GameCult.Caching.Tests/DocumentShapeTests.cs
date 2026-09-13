#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Accepted shapes are ordinary test types. Rejected shapes are emitted into a dynamic assembly, which registry discovery skips,
    // so a refused shape cannot break a registry built from the loaded assemblies.
    public class DocumentShapeTests
    {
        private const string DivergentOverride = "overrides {0} with a different [Key] or [IgnoreMember]; MessagePack reads the base declaration's, so an override must repeat or omit them.";

        private static readonly ModuleBuilder Rejected = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("RejectedShapes"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("RejectedShapes");

        [Test]
        public void FlatKeyedDocument()
        {
            Accept(new Plain { Name = "p", Maybe = 7, Tint = Color.Blue, Tags = { "a" }, Counts = { ["x"] = 1 }, Skip = 9 });
            var members = CultDocumentRegistry.ForTypes(new[] { typeof(Plain) }).GetRequired<Plain>().ToCatalogEntry().Members;
            Assert.That(members.Select(member => $"{member.Slot}:{member.MemberName}"), Is.EqualTo(new[] { "0:Name", "1:Maybe", "2:Tint", "3:Tags", "4:Counts" }));
        }

        [Test]
        public void NameAndIndexOnABaseSharedByTwoDocuments()
        {
            Accept(new Gear { Name = "helm", Slot = "head" });
            Accept(new Weapon { Name = "lance", Slot = "hand", Damage = 4 });
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(Gear), typeof(Weapon) });
            foreach (var type in new[] { typeof(Gear), typeof(Weapon) })
            {
                var descriptor = registry.GetRequired(type);
                var members = descriptor.ToCatalogEntry().Members;
                Assert.That(descriptor.NameMember, Is.EqualTo("Name"), type.Name);
                Assert.That(members.Single(member => member.MemberName == "Slot").IndexAlias, Is.EqualTo("slot"), type.Name);
            }

            Assert.That(registry.GetRequired<Weapon>().ToCatalogEntry().Members.Select(member => member.Slot), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void OverrideKeepsBaseKey()
        {
            var payload = Accept(new OvrSame { Name = "n", X = 3, Z = 5 });

            // Captured from MessagePack's DynamicObjectResolver: [Name, X, Z].
            Assert.That(Convert.ToHexString(payload), Is.EqualTo("93A16E0305"));
        }

        [Test]
        public void NotAMessagePackObject()
        {
            var type = Rejected.DefineType("NoObject", TypeAttributes.Public);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, new object[] { "shape.no_object", "shape.no_object.v1" }));
            Field(type, "Name", typeof(string), Key(0));
            Reject(type.CreateType()!, "Cult document NoObject is not a MessagePack object; add [MessagePackObject] so MessagePack serializes its [Key(n)] members.");
        }

        [Test]
        public void NonPublicWithoutAllowPrivate()
        {
            var type = Rejected.DefineType("Hidden", TypeAttributes.NotPublic);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, new object[] { "shape.hidden", "shape.hidden.v1" }));
            type.SetCustomAttribute(new CustomAttributeBuilder(typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false }));
            Field(type, "Name", typeof(string), Key(0));
            Reject(type.CreateType()!, "Cult document Hidden is not public; MessagePack serializes a non-public type only with [MessagePackObject(AllowPrivate = true)], so make it public or set AllowPrivate.");
        }

        [Test]
        public void AllowPrivateWithUnmarkedNonPublicMember()
        {
            var type = Rejected.DefineType("PrivateField", TypeAttributes.NotPublic);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, new object[] { "shape.private_field", "shape.private_field.v1" }));
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false },
                new[] { typeof(MessagePackObjectAttribute).GetProperty(nameof(MessagePackObjectAttribute.AllowPrivate))! }, new object[] { true }));
            Field(type, "Name", typeof(string), Key(0));
            type.DefineField("_cache", typeof(int), FieldAttributes.Private);
            Reject(type.CreateType()!, "Cult document PrivateField member PrivateField._cache is non-public; [MessagePackObject(AllowPrivate = true)] makes MessagePack read it, so mark it [IgnoreMember].");
        }

        [Test]
        public void NonPublicWithAllowPrivateRoundTrips()
        {
            var payload = Accept(new InternalNote { Name = "n", Count = 2 });
            Assert.That(Convert.ToHexString(payload), Is.EqualTo("92A16E02"));
        }

        [Test]
        public void UnkeyedMember()
        {
            var type = Document("Unkeyed");
            Field(type, "Name", typeof(string), Key(0));
            Field(type, "Count", typeof(int));
            Reject(type.CreateType()!, "Cult document Unkeyed member Count has no [Key]; every persisted member of a [CultDocument] type needs an explicit [Key(n)].");
        }

        // MessagePack's DynamicObjectResolver reads the base declaration: it throws "key is duplicated" for this shape.
        [Test]
        public void OverrideReKeyed()
        {
            var baseType = Rejected.DefineType("OvrDiffBase", TypeAttributes.Public);
            Property(baseType, "Name", MethodAttributes.Virtual, MethodAttributes.Public, Key(0));
            var type = Document("OvrDiff", baseType.CreateType());
            Property(type, "Name", MethodAttributes.Virtual, MethodAttributes.Public, Key(2));
            Property(type, "X", 0, MethodAttributes.Public, Key(1));
            Property(type, "Y", 0, MethodAttributes.Public, Key(0));
            Reject(type.CreateType()!, "Cult document OvrDiff member OvrDiff.Name " + string.Format(DivergentOverride, "OvrDiffBase.Name"));
        }

        // MessagePack's DynamicObjectResolver keeps the base [Key(1)] and serializes Name despite the override's [IgnoreMember].
        [Test]
        public void OverrideIgnored()
        {
            var baseType = Rejected.DefineType("OvrIgnoreBase", TypeAttributes.Public);
            Property(baseType, "Name", MethodAttributes.Virtual, MethodAttributes.Public, Key(1));
            var type = Document("OvrIgnore", baseType.CreateType());
            Property(type, "Name", MethodAttributes.Virtual, MethodAttributes.Public, new CustomAttributeBuilder(typeof(IgnoreMemberAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
            Property(type, "X", 0, MethodAttributes.Public, Key(0));
            Reject(type.CreateType()!, "Cult document OvrIgnore member OvrIgnore.Name " + string.Format(DivergentOverride, "OvrIgnoreBase.Name"));
        }

        [Test]
        public void NewHiddenField()
        {
            var baseType = Rejected.DefineType("NewFieldBase", TypeAttributes.Public);
            Field(baseType, "Name", typeof(string), Key(0));
            var type = Document("NewField", baseType.CreateType());
            Field(type, "Name", typeof(string), Key(0));
            Field(type, "X", typeof(int), Key(1));
            Reject(type.CreateType()!, "Cult document NewField members NewField.Name and NewFieldBase.Name share [Key(0)]; every persisted member needs a distinct [Key(n)].");
        }

        [Test]
        public void NewHiddenProperty()
        {
            var baseType = Rejected.DefineType("NewPropBase", TypeAttributes.Public);
            Property(baseType, "Name", 0, MethodAttributes.Public, Key(0));
            var type = Document("NewProp", baseType.CreateType());
            Property(type, "Name", 0, MethodAttributes.Public, Key(0));
            Property(type, "X", 0, MethodAttributes.Public, Key(1));
            Reject(type.CreateType()!, "Cult document NewProp members NewProp.Name and NewPropBase.Name share [Key(0)]; every persisted member needs a distinct [Key(n)].");
        }

        [Test]
        public void DuplicateSlot()
        {
            var type = Document("Dup");
            Field(type, "B", typeof(int), Key(0));
            Field(type, "A", typeof(int), Key(0));
            Reject(type.CreateType()!, "Cult document Dup members Dup.A and Dup.B share [Key(0)]; every persisted member needs a distinct [Key(n)].");
        }

        [Test]
        public void InheritedPrivateSetter()
        {
            var baseType = Rejected.DefineType("PrivSetBase", TypeAttributes.Public);
            Property(baseType, "Name", 0, MethodAttributes.Private, Key(0));
            var type = Document("PrivSetDerived", baseType.CreateType());
            Property(type, "X", 0, MethodAttributes.Public, Key(1));
            Reject(type.CreateType()!, "Cult document PrivSetDerived member Name is not writable; a persisted member needs a non-readonly field or a public or internal set accessor.");
        }

        [Test]
        public void StringKey()
        {
            var type = Document("StringKeyed");
            Property(type, "Name", 0, MethodAttributes.Public, new CustomAttributeBuilder(typeof(KeyAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { "name" }));
            Reject(type.CreateType()!, "Cult document StringKeyed member Name has a string [Key]; string keys are not supported; use integer [Key(n)].");
        }

        private static byte[] Accept(object sample)
        {
            var type = sample.GetType();
            var registry = CultDocumentRegistry.ForTypes(new[] { type });
            var expected = MessagePackSerializer.Serialize(type, sample, CultDocumentMessagePackSerialization.OptionsFor(type.Assembly));
            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(sample, type, registry);
            var decoded = CultDocumentMessagePackSerialization.DeserializeUntyped(type, payload, registry);
            Assert.That(Convert.ToHexString(payload), Is.EqualTo(Convert.ToHexString(expected)), $"{type.Name}: payload");
            Assert.That(Convert.ToHexString(CultDocumentMessagePackSerialization.SerializeUntyped(decoded, type, registry)), Is.EqualTo(Convert.ToHexString(payload)), $"{type.Name}: round trip");
            return payload;
        }

        private static void Reject(Type type, string expected) =>
            Assert.That(Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.ForTypes(new[] { type }))!.Message, Is.EqualTo(expected));

        private static CustomAttributeBuilder Key(int slot) =>
            new(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { slot });

        private static TypeBuilder Document(string name, Type? parent = null)
        {
            var type = Rejected.DefineType(name, TypeAttributes.Public, parent);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
                new object[] { "shape." + name, "shape." + name + ".v1" }));
            type.SetCustomAttribute(new CustomAttributeBuilder(typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false }));
            return type;
        }

        private static void Field(TypeBuilder type, string name, Type fieldType, params CustomAttributeBuilder[] attributes)
        {
            var field = type.DefineField(name, fieldType, FieldAttributes.Public);
            foreach (var attribute in attributes) field.SetCustomAttribute(attribute);
        }

        private static void Property(TypeBuilder type, string name, MethodAttributes shape, MethodAttributes setterAccess, params CustomAttributeBuilder[] attributes)
        {
            var propertyType = typeof(string);
            var backing = type.DefineField("_" + name, propertyType, FieldAttributes.Private);
            var getter = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | shape, propertyType, Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, backing);
            il.Emit(OpCodes.Ret);
            var setter = type.DefineMethod("set_" + name, setterAccess | MethodAttributes.SpecialName | MethodAttributes.HideBySig | shape, null, new[] { propertyType });
            il = setter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, backing);
            il.Emit(OpCodes.Ret);
            var property = type.DefineProperty(name, PropertyAttributes.None, propertyType, null);
            property.SetGetMethod(getter);
            property.SetSetMethod(setter);
            foreach (var attribute in attributes) property.SetCustomAttribute(attribute);
        }

        public enum Color { Red, Blue }

        [CultDocument("shape.plain", "shape.plain.v1")]
        [MessagePackObject]
        public class Plain
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public int? Maybe;
            [Key(2)] public Color Tint { get; set; }
            [Key(3)] public List<string> Tags = new();
            [Key(4)] public Dictionary<string, int> Counts { get; set; } = new();
            [IgnoreMember] public int Skip { get; set; }
        }

        [CultDocument("shape.gear", "shape.gear.v1")]
        [MessagePackObject]
        public class Gear
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] [CultIndex("slot")] public string Slot { get; set; } = "";
        }

        [CultDocument("shape.weapon", "shape.weapon.v1")]
        [MessagePackObject]
        public sealed class Weapon : Gear
        {
            [Key(2)] public int Damage { get; set; }
        }

        [CultDocument("shape.internal_note", "shape.internal_note.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class InternalNote
        {
            [Key(0)] public string Name = "";
            [Key(1)] public int Count;
        }

        [MessagePackObject]
        public class OvrSameBase
        {
            [Key(0)] public virtual string Name { get; set; } = "";
            [Key(2)] public virtual int Z { get; set; }
        }

        [CultDocument("shape.ovr_same", "shape.ovr_same.v1")]
        [MessagePackObject]
        public sealed class OvrSame : OvrSameBase
        {
            [Key(0)] public override string Name { get; set; } = "";
            public override int Z { get; set; }
            [Key(1)] public int X { get; set; }
        }
    }
}
