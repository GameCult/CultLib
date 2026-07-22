using CultMath;
using GameCult.Caching.MessagePack;
using MessagePack;
using MessagePack.Formatters;

[assembly: CultDocumentMessagePackResolvers(typeof(GameCult.Geometry.CultMathMessagePackResolver))]

namespace GameCult.Geometry;

/// <summary>
/// Owns the persisted positional representation of CultMath values used by
/// GameCult.Geometry documents. CultMath remains serialization-agnostic.
/// </summary>
public sealed class CultMathMessagePackResolver : IFormatterResolver
{
    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(float2)) return (IMessagePackFormatter<T>)(object)Float2Formatter.Instance;
        if (typeof(T) == typeof(float3)) return (IMessagePackFormatter<T>)(object)Float3Formatter.Instance;
        if (typeof(T) == typeof(float4)) return (IMessagePackFormatter<T>)(object)Float4Formatter.Instance;
        if (typeof(T) == typeof(quaternion)) return (IMessagePackFormatter<T>)(object)QuaternionFormatter.Instance;
        return null;
    }

    private static void RequireComponents(ref MessagePackReader reader, int expected, string typeName)
    {
        var actual = reader.ReadArrayHeader();
        if (actual != expected)
        {
            throw new MessagePackSerializationException($"{typeName} requires exactly {expected} components; payload contained {actual}.");
        }
    }

    internal sealed class Float2Formatter : IMessagePackFormatter<float2>
    {
        public static readonly Float2Formatter Instance = new();
        public void Serialize(ref MessagePackWriter writer, float2 value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(2);
            writer.Write(value.x);
            writer.Write(value.y);
        }

        public float2 Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            RequireComponents(ref reader, 2, nameof(float2));
            return new float2(reader.ReadSingle(), reader.ReadSingle());
        }
    }

    internal sealed class Float3Formatter : IMessagePackFormatter<float3>
    {
        public static readonly Float3Formatter Instance = new();
        public void Serialize(ref MessagePackWriter writer, float3 value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(3);
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        public float3 Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            RequireComponents(ref reader, 3, nameof(float3));
            return new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }

    internal sealed class Float4Formatter : IMessagePackFormatter<float4>
    {
        public static readonly Float4Formatter Instance = new();
        public void Serialize(ref MessagePackWriter writer, float4 value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(4);
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        public float4 Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            RequireComponents(ref reader, 4, nameof(float4));
            return new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }

    internal sealed class QuaternionFormatter : IMessagePackFormatter<quaternion>
    {
        public static readonly QuaternionFormatter Instance = new();
        public void Serialize(ref MessagePackWriter writer, quaternion value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(4);
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        public quaternion Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            RequireComponents(ref reader, 4, nameof(quaternion));
            return new quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
