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
        private const string NonPublicSetter = "has a non-public set accessor; MessagePack writes it but never reads it back without AllowPrivate, so make the setter public or add [MessagePackObject(AllowPrivate = true)].";
        private const string Unkeyed = "has no [Key]; MessagePack requires [Key(n)] or [IgnoreMember] on every public member, so mark it one or the other.";
        private const string ReadonlyUnfilled = "is a readonly field that no constructor fills; MessagePack writes it but never reads it back, so give the constructor MessagePack calls a parameter at position {0}, drop readonly, or add [MessagePackObject(AllowPrivate = true)].";
        private const string DivergentOverride ="overrides {0} with a different [Key] or [IgnoreMember]; MessagePack reads the base declaration's, so an override must repeat or omit them.";

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
            Reject(type.CreateType()!, "Cult document Unkeyed member Count " + Unkeyed);
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
            Reject(type.CreateType()!, "Cult document PrivSetDerived member Name " + NonPublicSetter);
        }

        // Without AllowPrivate MessagePack writes Name through the public getter and loads it back as null.
        [Test]
        public void InternalSetterWithoutAllowPrivate()
        {
            var type = Document("InternalSetter");
            Property(type, "Name", 0, MethodAttributes.Assembly, Key(0));
            Reject(type.CreateType()!, "Cult document InternalSetter member Name " + NonPublicSetter);
        }

        // Without AllowPrivate MessagePack neither writes nor reads a non-public member, [Key] or not.
        [Test]
        public void KeyedNonPublicMemberWithoutAllowPrivate()
        {
            var type = Document("KeyedPrivate");
            Field(type, "Name", typeof(string), Key(0));
            type.DefineField("_secret", typeof(int), FieldAttributes.Private).SetCustomAttribute(Key(1));
            Reject(type.CreateType()!, "Cult document KeyedPrivate member KeyedPrivate._secret is non-public; MessagePack skips it silently, so [Key] on a non-public member requires [MessagePackObject(AllowPrivate = true)].");
        }

        // The shape of `class PrimaryCtor(int p)`: MessagePack throws "can't find matched constructor" on first serialize.
        [Test]
        public void AllowPrivatePrimaryConstructor()
        {
            var type = Rejected.DefineType("PrimaryCtor", TypeAttributes.NotPublic);
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, new object[] { "shape.primary_ctor", "shape.primary_ctor.v1" }));
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false },
                new[] { typeof(MessagePackObjectAttribute).GetProperty(nameof(MessagePackObjectAttribute.AllowPrivate))! }, new object[] { true }));
            Field(type, "Name", typeof(string), Key(0));
            var il = type.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new[] { typeof(int) }).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Ret);
            Reject(type.CreateType()!, "Cult document PrimaryCtor has no constructor MessagePack can call; add a parameterless constructor (public unless AllowPrivate), or one whose parameters take the [Key(0)], [Key(1)], ... members in order.");
        }

        [Test]
        public void AllowPrivateSettersRoundTrip()
        {
            Assert.That(Convert.ToHexString(Accept(PrivateSetterNote.Make("n"))), Is.EqualTo("91A16E"));
            Assert.That(Convert.ToHexString(Accept(new InitOnlyNote { Name = "n" })), Is.EqualTo("91A16E"));
            Assert.That(Convert.ToHexString(Accept(new InternalSetterNote { Name = "n" })), Is.EqualTo("91A16E"));
        }

        // MessagePack fills a get-only property through the longest constructor whose parameters take the keyed members by position.
        [Test]
        public void GetOnlyPropertyFilledByConstructor()
        {
            Assert.That(Convert.ToHexString(Accept(new GetOnlyNote("n", 2))), Is.EqualTo("92A16E02"));
            AssertSlots<GetOnlyNote>("0:Name", "1:Count");
        }

        // The three-parameter constructor has no member at position 2, so MessagePack falls back to the one-parameter constructor.
        [Test]
        public void ReadonlyFieldFilledByConstructor()
        {
            Assert.That(Convert.ToHexString(Accept(new ReadonlyNote("n") { Count = 3 })), Is.EqualTo("92A16E03"));
            AssertSlots<ReadonlyNote>("0:Name", "1:Count");
        }

        // Under AllowPrivate MessagePack writes a readonly field directly; no constructor has to fill it.
        [Test]
        public void ReadonlyFieldUnderAllowPrivate()
        {
            var sample = new ReadonlyPrivateNote();
            typeof(ReadonlyPrivateNote).GetField(nameof(ReadonlyPrivateNote.Name))!.SetValue(sample, "n");
            Assert.That(Convert.ToHexString(Accept(sample)), Is.EqualTo("91A16E"));
            Assert.That(((ReadonlyPrivateNote)CultDocumentMessagePackSerialization.DeserializeUntyped(
                typeof(ReadonlyPrivateNote), Accept(sample), CultDocumentRegistry.ForTypes(new[] { typeof(ReadonlyPrivateNote) }))).Name, Is.EqualTo("n"));
            AssertSlots<ReadonlyPrivateNote>("0:Name");
        }

        // MessagePack throws "all public members must mark KeyAttribute or IgnoreMemberAttribute" for this shape.
        [Test]
        public void UnkeyedGetOnlyProperty()
        {
            var type = Document("UnkeyedGetter");
            Field(type, "Name", typeof(string), Key(0));
            GetOnlyProperty(type, "Upper");
            Reject(type.CreateType()!, "Cult document UnkeyedGetter member Upper " + Unkeyed);
        }

        // MessagePack writes Label and loads it back as the default: only the parameterless constructor exists.
        [Test]
        public void GetOnlyPropertyNoConstructorFills()
        {
            var type = Document("GetterUnfilled");
            Field(type, "Name", typeof(string), Key(0));
            GetOnlyProperty(type, "Label", Key(1));
            Reject(type.CreateType()!, "Cult document GetterUnfilled member Label is a get-only property that no constructor fills; MessagePack writes it but never reads it back, so give the constructor MessagePack calls a parameter at position 1, add a setter, or mark it [IgnoreMember].");
        }

        // Without AllowPrivate MessagePack writes Name and loads it back as the field initializer's value.
        [Test]
        public void ReadonlyFieldNoConstructorFills()
        {
            var type = Document("ReadonlyUnfilled");
            type.DefineField("Name", typeof(string), FieldAttributes.Public | FieldAttributes.InitOnly).SetCustomAttribute(Key(0));
            Reject(type.CreateType()!, "Cult document ReadonlyUnfilled member Name " + string.Format(ReadonlyUnfilled, 0));
        }

        // MessagePack picks the one-parameter constructor, which fills Name but not Other; Other loads back as its default.
        [Test]
        public void ReadonlyFieldBeyondPickedConstructor()
        {
            var type = Document("ReadonlyBeyond");
            var name = type.DefineField("Name", typeof(string), FieldAttributes.Public | FieldAttributes.InitOnly);
            name.SetCustomAttribute(Key(0));
            type.DefineField("Other", typeof(string), FieldAttributes.Public | FieldAttributes.InitOnly).SetCustomAttribute(Key(1));
            var il = type.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new[] { typeof(string) }).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, name);
            il.Emit(OpCodes.Ret);
            Reject(type.CreateType()!, "Cult document ReadonlyBeyond member Other " + string.Format(ReadonlyUnfilled, 1));
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

        private static void AssertSlots<T>(params string[] expected) where T : class =>
            Assert.That(CultDocumentRegistry.ForTypes(new[] { typeof(T) }).GetRequired<T>().ToCatalogEntry().Members.Select(member => $"{member.Slot}:{member.MemberName}"), Is.EqualTo(expected));

        private static void GetOnlyProperty(TypeBuilder type, string name, params CustomAttributeBuilder[] attributes)
        {
            var backing = type.DefineField("_" + name, typeof(string), FieldAttributes.Private);
            backing.SetCustomAttribute(new CustomAttributeBuilder(typeof(IgnoreMemberAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
            var getter = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(string), Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, backing);
            il.Emit(OpCodes.Ret);
            var property = type.DefineProperty(name, PropertyAttributes.None, typeof(string), null);
            property.SetGetMethod(getter);
            foreach (var attribute in attributes) property.SetCustomAttribute(attribute);
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

        [CultDocument("shape.private_setter", "shape.private_setter.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class PrivateSetterNote
        {
            [Key(0)] public string Name { get; private set; } = "";

            public static PrivateSetterNote Make(string name) => new() { Name = name };
        }

        [CultDocument("shape.init_only", "shape.init_only.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class InitOnlyNote
        {
            [Key(0)] public string Name { get; init; } = "";
        }

        [CultDocument("shape.internal_setter", "shape.internal_setter.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class InternalSetterNote
        {
            [Key(0)] public string Name { get; internal set; } = "";
        }

        [CultDocument("shape.get_only", "shape.get_only.v1")]
        [MessagePackObject]
        public sealed class GetOnlyNote
        {
            public GetOnlyNote(string name, int count)
            {
                Name = name;
                Count = count;
            }

            [Key(0)] public string Name { get; }
            [Key(1)] public int Count { get; set; }
        }

        [CultDocument("shape.readonly", "shape.readonly.v1")]
        [MessagePackObject]
        public sealed class ReadonlyNote
        {
            public ReadonlyNote() => Name = "";
            public ReadonlyNote(string name) => Name = name;
            public ReadonlyNote(string name, int count, string extra) => Name = name;

            [Key(0)] public readonly string Name;
            [Key(1)] public int Count { get; set; }
        }

        [CultDocument("shape.readonly_private", "shape.readonly_private.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class ReadonlyPrivateNote
        {
            [Key(0)] public readonly string Name = "init";
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
