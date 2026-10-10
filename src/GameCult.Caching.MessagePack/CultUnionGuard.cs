using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The one place an unknown union arm or an unnamed subtype is decided. A union is an abstract class or interface carrying
/// its own [Union] attributes (never inherited: a union whose arms are also a union has its own key set). MessagePack-CSharp
/// still encodes the [key, armSlots] shape; this wraps its formatter so that a key the union does not declare is refused on
/// read, and a value whose runtime type is not a declared arm is refused on write, where MessagePack alone yields null and nil.
/// </summary>
internal sealed class CultUnionGuard(IFormatterResolver inner) : IFormatterResolver
{
    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        var arms = typeof(T).GetCustomAttributes<UnionAttribute>(inherit: false).ToArray();
        return arms.Length == 0 || inner.GetFormatter<T>() is not { } formatter ? null : new Formatter<T>(formatter, arms);
    }

    private sealed class Formatter<T>(IMessagePackFormatter<T> inner, UnionAttribute[] arms) : IMessagePackFormatter<T>
    {
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
