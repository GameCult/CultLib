using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The one sensor of an unknown union arm, an unnamed subtype and a slot past the reader's declared slots. A union is an abstract class or interface carrying
/// its own [Union] attributes (never inherited: a union whose arms are also a union has its own key set). MessagePack-CSharp
/// still encodes the [key, armSlots] shape; this wraps its formatter so that a key the union does not declare is refused on
/// read, and a value whose runtime type is not a declared arm is refused on write, where MessagePack alone yields null and nil.
/// </summary>
internal sealed class CultUnionGuard(IFormatterResolver inner) : IFormatterResolver
{
    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        var arms = typeof(T).GetCustomAttributes<UnionAttribute>(inherit: false).ToArray();
        if (arms.Length == 0)
            return inner.GetFormatter<T>() is { } objectFormatter && KeyLayout(objectFormatter) ? new SlotFormatter<T>(objectFormatter) : null;
        return inner.GetFormatter<T>() is not { } formatter ? null : new Formatter<T>(formatter, arms);
    }

    // The formatter that writes and reads the bytes: the one a guard wrapper delegates to, else the formatter itself. A check that a
    // generated formatter is the one in use asks this (tests/GameCult.Caching.GeneratorConsumer).
    internal static object Inner(object formatter) => formatter is IWrapper wrapper ? wrapper.Inner : formatter;

    private interface IWrapper
    {
        object Inner { get; }
    }

    // The one decision of whether a type is measured: the formatter the chain hands out for an int-keyed [MessagePackObject] type is
    // MessagePack's own [Key]-layout formatter, the very instance StandardResolver (or its AllowPrivate twin) builds for it. A formatter a
    // consumer resolver or a [MessagePackFormatter] attribute supplies owns its layout and its tolerance, and a [MessagePackObject]
    // collection is written as its items by the generic resolver: none of those is measured. Identity, as ResolverClaim asks it.
    private static bool KeyLayout<T>(IMessagePackFormatter<T> formatter)
    {
        if (typeof(T).IsAbstract || typeof(T).IsInterface ||
            typeof(T).GetCustomAttribute<MessagePackObjectAttribute>(inherit: true) is not { KeyAsPropertyName: false })
            return false;
        if (ReferenceEquals(formatter, Of<T>(AttributeFormatterResolver.Instance)) || ReferenceEquals(formatter, Of<T>(DynamicGenericResolver.Instance)))
            return false;
        return ReferenceEquals(formatter, Of<T>(StandardResolver.Instance)) || ReferenceEquals(formatter, Of<T>(StandardResolverAllowPrivate.Instance));
    }

    private static IMessagePackFormatter<T>? Of<T>(IFormatterResolver resolver)
    {
        try
        {
            return resolver.GetFormatter<T>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Counts a concrete object's array header against its declared slots and notes the excess into the open CultSlotScope, then lets the
    // inner formatter read it unchanged: no byte scan, no second decode. With no scope open it is a depth-free pass-through.
    // Declared slots are what the type's own formatter writes: the array header of one instance written through it, measured on the
    // first read inside a scope (not in GetFormatter, so a self-referential type never recurses into its own resolver cache) and kept.
    private sealed class SlotFormatter<T>(IMessagePackFormatter<T> inner) : IMessagePackFormatter<T>, IWrapper
    {
        object IWrapper.Inner => inner;

        private const int NotMeasured = -1;

        // A type no instance of which can be written is not guarded: no count can exceed this.
        private const int Unguarded = int.MaxValue;

        private int declared = NotMeasured;

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options) =>
            inner.Serialize(ref writer, value, options);

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            var scope = CultSlotScope.Current;
            if (scope == null) return inner.Deserialize(ref reader, options);
            var peek = reader.CreatePeekReader();
            if (!peek.TryReadNil() && peek.NextMessagePackType == MessagePackType.Array)
            {
                var count = peek.ReadArrayHeader();
                if (declared == NotMeasured) declared = Measure(options);
                if (count > declared) scope.Note(typeof(T).Name, count, declared);
            }

            scope.Depth++;
            try
            {
                return inner.Deserialize(ref reader, options);
            }
            finally
            {
                scope.Depth--;
            }
        }

        // The uninitialized instance, else one from a parameterless constructor (public or not) when that write throws.
        private int Measure(MessagePackSerializerOptions options)
        {
            if (Written(() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T)), options) is { } slots) return slots;
            var constructor = typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return constructor != null && Written(() => (T)constructor.Invoke(null), options) is { } constructed ? constructed : Unguarded;
        }

        private int? Written(Func<T> instance, MessagePackSerializerOptions options)
        {
            try
            {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new MessagePackWriter(buffer);
                inner.Serialize(ref writer, instance(), options);
                writer.Flush();
                var written = new MessagePackReader(buffer.WrittenMemory);
                return written.NextMessagePackType == MessagePackType.Array ? written.ReadArrayHeader() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private sealed class Formatter<T>(IMessagePackFormatter<T> inner, UnionAttribute[] arms) : IMessagePackFormatter<T>, IWrapper
    {
        object IWrapper.Inner => inner;

        private readonly HashSet<int> keys = arms.Select(arm => arm.Key).ToHashSet();
        private readonly HashSet<Type> types = arms.Select(arm => arm.SubType).ToHashSet();

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            if (value != null && !types.Contains(value.GetType()))
                throw new MessagePackSerializationException($"{value.GetType().FullName} is not an arm of union {typeof(T).FullName}; it would be written as nil.");
            inner.Serialize(ref writer, value, options);
        }

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            // Peek only the arm's first two tokens; any other shape is the inner formatter's to refuse.
            var peek = reader.CreatePeekReader();
            if (!peek.TryReadNil() && peek.NextMessagePackType == MessagePackType.Array && peek.ReadArrayHeader() == 2)
            {
                var declared = string.Join(", ", keys.OrderBy(k => k));
                if (peek.NextMessagePackType != MessagePackType.Integer)
                    throw new MessagePackSerializationException($"Union {typeof(T).FullName} has a non-integer arm key (slot 0 of the arm); declared keys: {declared}.");
                if (!TryReadKey(ref peek, out var key))
                    throw new MessagePackSerializationException($"Union {typeof(T).FullName} has an arm key outside Int32 (slot 0 of the arm); declared keys: {declared}.");
                if (!keys.Contains(key))
                    throw new MessagePackSerializationException($"Union {typeof(T).FullName} has no arm for key {key} (slot 0 of the arm); declared keys: {declared}.");
                if (peek.TryReadNil())
                    throw new MessagePackSerializationException($"Union {typeof(T).FullName} arm {key} has nil slots (slot 1 of the arm); declared keys: {declared}.");
            }

            return inner.Deserialize(ref reader, options);
        }

        // An arm key is an Int32; a wider or unsigned-64 integer is out of range, never an OverflowException.
        private static bool TryReadKey(ref MessagePackReader reader, out int key)
        {
            key = 0;
            if (reader.NextCode == MessagePackCode.UInt64)
            {
                var wide = reader.ReadUInt64();
                if (wide > int.MaxValue) return false;
                key = (int)wide;
                return true;
            }

            var value = reader.ReadInt64();
            if (value < int.MinValue || value > int.MaxValue) return false;
            key = (int)value;
            return true;
        }
    }
}
