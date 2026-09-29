using System;
using System.Collections.Generic;
using CultMath;
using MessagePack;
using MessagePack.Formatters;
using Random = CultMath.Random;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The canonical MessagePack encoding of every public CultMath value type. Each type is an
/// array of its components in declaration order: vectors and quaternions are flat scalar
/// arrays, matrices are arrays of row vectors, and <c>rect</c> is <c>[min, max]</c>. Decoding
/// skips trailing elements and leaves missing ones at zero (a missing Color32 alpha is 255); nil is refused. The resolver is
/// part of the default <see cref="CultDocumentMessagePackSerialization"/> chain, so a consumer
/// registers nothing.
/// </summary>
public sealed class CultMathResolver : IFormatterResolver
{
    public static readonly CultMathResolver Instance = new();
    private CultMathResolver() { }

    /// <summary>The CultMath types this resolver formats, for coverage checks.</summary>
    public static IReadOnlyCollection<Type> FormattedTypes => Formatters.Keys;

    public IMessagePackFormatter<T>? GetFormatter<T>() => Cache<T>.Formatter;

    private static class Cache<T>
    {
        public static readonly IMessagePackFormatter<T>? Formatter =
            Formatters.TryGetValue(typeof(T), out var formatter) ? (IMessagePackFormatter<T>)formatter : null;
    }

    private delegate void WriteFn<T>(ref MessagePackWriter writer, T value);
    private delegate T ReadFn<T>(ref MessagePackReader reader);

    private sealed class ArrayShape<T> : IMessagePackFormatter<T>
    {
        private readonly WriteFn<T> write;
        private readonly ReadFn<T> read;

        public ArrayShape(WriteFn<T> write, ReadFn<T> read)
        {
            this.write = write;
            this.read = read;
        }

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options) => write(ref writer, value);

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return read(ref reader);
        }
    }

    private static KeyValuePair<Type, object> Shape<T>(WriteFn<T> write, ReadFn<T> read) =>
        new(typeof(T), new ArrayShape<T>(write, read));

    // Each reader takes the first dst.Length elements and skips the rest.
    private static void Singles(ref MessagePackReader r, scoped Span<float> dst)
    {
        for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
            if (i < dst.Length) dst[i] = r.ReadSingle(); else r.Skip();
    }

    private static void Doubles(ref MessagePackReader r, scoped Span<double> dst)
    {
        for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
            if (i < dst.Length) dst[i] = r.ReadDouble(); else r.Skip();
    }

    private static void Ints(ref MessagePackReader r, scoped Span<int> dst)
    {
        for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
            if (i < dst.Length) dst[i] = r.ReadInt32(); else r.Skip();
    }

    private static void Bools(ref MessagePackReader r, scoped Span<bool> dst)
    {
        for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
            if (i < dst.Length) dst[i] = r.ReadBoolean(); else r.Skip();
    }

    private static void Bytes(ref MessagePackReader r, scoped Span<byte> dst)
    {
        for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
            if (i < dst.Length) dst[i] = r.ReadByte(); else r.Skip();
    }

    private static void Put(ref MessagePackWriter w, scoped ReadOnlySpan<float> v)
    {
        w.WriteArrayHeader(v.Length);
        foreach (var e in v) w.Write(e);
    }

    private static void Put(ref MessagePackWriter w, scoped ReadOnlySpan<double> v)
    {
        w.WriteArrayHeader(v.Length);
        foreach (var e in v) w.Write(e);
    }

    private static void Put(ref MessagePackWriter w, scoped ReadOnlySpan<int> v)
    {
        w.WriteArrayHeader(v.Length);
        foreach (var e in v) w.Write(e);
    }

    private static void Put(ref MessagePackWriter w, scoped ReadOnlySpan<bool> v)
    {
        w.WriteArrayHeader(v.Length);
        foreach (var e in v) w.Write(e);
    }

    private static void Put(ref MessagePackWriter w, scoped ReadOnlySpan<byte> v)
    {
        w.WriteArrayHeader(v.Length);
        foreach (var e in v) w.Write(e);
    }

    private static void PutFloat2(ref MessagePackWriter w, float2 v) => Put(ref w, stackalloc float[] { v.x, v.y });
    private static void PutFloat3(ref MessagePackWriter w, float3 v) => Put(ref w, stackalloc float[] { v.x, v.y, v.z });
    private static void PutFloat4(ref MessagePackWriter w, float4 v) => Put(ref w, stackalloc float[] { v.x, v.y, v.z, v.w });

    private static float2 GetFloat2(ref MessagePackReader r)
    {
        Span<float> s = stackalloc float[2];
        Singles(ref r, s);
        return new float2(s[0], s[1]);
    }

    private static float3 GetFloat3(ref MessagePackReader r)
    {
        Span<float> s = stackalloc float[3];
        Singles(ref r, s);
        return new float3(s[0], s[1], s[2]);
    }

    private static float4 GetFloat4(ref MessagePackReader r)
    {
        Span<float> s = stackalloc float[4];
        Singles(ref r, s);
        return new float4(s[0], s[1], s[2], s[3]);
    }

    private static readonly Dictionary<Type, object> Formatters = Build();

    private static Dictionary<Type, object> Build()
    {
        var shapes = new[]
        {
            Shape<float2>(PutFloat2, GetFloat2),
            Shape<float3>(PutFloat3, GetFloat3),
            Shape<float4>(PutFloat4, GetFloat4),
            Shape<double2>((ref MessagePackWriter w, double2 v) => Put(ref w, stackalloc double[] { v.x, v.y }),
                (ref MessagePackReader r) => { Span<double> s = stackalloc double[2]; Doubles(ref r, s); return new double2(s[0], s[1]); }),
            Shape<double3>((ref MessagePackWriter w, double3 v) => Put(ref w, stackalloc double[] { v.x, v.y, v.z }),
                (ref MessagePackReader r) => { Span<double> s = stackalloc double[3]; Doubles(ref r, s); return new double3(s[0], s[1], s[2]); }),
            Shape<int2>((ref MessagePackWriter w, int2 v) => Put(ref w, stackalloc int[] { v.x, v.y }),
                (ref MessagePackReader r) => { Span<int> s = stackalloc int[2]; Ints(ref r, s); return new int2(s[0], s[1]); }),
            Shape<int3>((ref MessagePackWriter w, int3 v) => Put(ref w, stackalloc int[] { v.x, v.y, v.z }),
                (ref MessagePackReader r) => { Span<int> s = stackalloc int[3]; Ints(ref r, s); return new int3(s[0], s[1], s[2]); }),
            Shape<int4>((ref MessagePackWriter w, int4 v) => Put(ref w, stackalloc int[] { v.x, v.y, v.z, v.w }),
                (ref MessagePackReader r) => { Span<int> s = stackalloc int[4]; Ints(ref r, s); return new int4(s[0], s[1], s[2], s[3]); }),
            Shape<bool2>((ref MessagePackWriter w, bool2 v) => Put(ref w, stackalloc bool[] { v.x, v.y }),
                (ref MessagePackReader r) => { Span<bool> s = stackalloc bool[2]; Bools(ref r, s); return new bool2(s[0], s[1]); }),
            Shape<bool3>((ref MessagePackWriter w, bool3 v) => Put(ref w, stackalloc bool[] { v.x, v.y, v.z }),
                (ref MessagePackReader r) => { Span<bool> s = stackalloc bool[3]; Bools(ref r, s); return new bool3(s[0], s[1], s[2]); }),
            Shape<bool4>((ref MessagePackWriter w, bool4 v) => Put(ref w, stackalloc bool[] { v.x, v.y, v.z, v.w }),
                (ref MessagePackReader r) => { Span<bool> s = stackalloc bool[4]; Bools(ref r, s); return new bool4(s[0], s[1], s[2], s[3]); }),
            Shape<quaternion>((ref MessagePackWriter w, quaternion v) => Put(ref w, stackalloc float[] { v.x, v.y, v.z, v.w }),
                (ref MessagePackReader r) => { Span<float> s = stackalloc float[4]; Singles(ref r, s); return new quaternion(s[0], s[1], s[2], s[3]); }),
            Shape<Color32>((ref MessagePackWriter w, Color32 v) => Put(ref w, stackalloc byte[] { v.r, v.g, v.b, v.a }),
                (ref MessagePackReader r) => { Span<byte> s = stackalloc byte[4]; s[3] = 255; Bytes(ref r, s); return new Color32(s[0], s[1], s[2], s[3]); }),
            Shape<Random>((ref MessagePackWriter w, Random v) => { w.WriteArrayHeader(1); w.Write(v.state); },
                (ref MessagePackReader r) =>
                {
                    var value = default(Random);
                    for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
                        if (i == 0) value.state = r.ReadUInt32(); else r.Skip();
                    return value;
                }),

            // Compositions: matrices are rows, rect is [min, max], CultCellular is [nearest, edge, id].
            Shape<float2x2>((ref MessagePackWriter w, float2x2 v) => { w.WriteArrayHeader(2); PutFloat2(ref w, v[0]); PutFloat2(ref w, v[1]); },
                (ref MessagePackReader r) =>
                {
                    var rows = new float2[2];
                    for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
                        if (i < 2) rows[i] = GetFloat2(ref r); else r.Skip();
                    return new float2x2(rows[0], rows[1]);
                }),
            Shape<float3x3>((ref MessagePackWriter w, float3x3 v) => { w.WriteArrayHeader(3); PutFloat3(ref w, v[0]); PutFloat3(ref w, v[1]); PutFloat3(ref w, v[2]); },
                (ref MessagePackReader r) =>
                {
                    var rows = new float3[3];
                    for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
                        if (i < 3) rows[i] = GetFloat3(ref r); else r.Skip();
                    return new float3x3(rows[0], rows[1], rows[2]);
                }),
            Shape<rect>((ref MessagePackWriter w, rect v) => { w.WriteArrayHeader(2); PutFloat2(ref w, v.min); PutFloat2(ref w, v.max); },
                (ref MessagePackReader r) =>
                {
                    var corners = new float2[2];
                    for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
                        if (i < 2) corners[i] = GetFloat2(ref r); else r.Skip();
                    return new rect(corners[0], corners[1]);
                }),
            Shape<CultCellular>((ref MessagePackWriter w, CultCellular v) => { w.WriteArrayHeader(3); PutFloat4(ref w, v.nearest); PutFloat4(ref w, v.edge); w.Write(v.id); },
                (ref MessagePackReader r) =>
                {
                    var value = default(CultCellular);
                    for (int i = 0, n = r.ReadArrayHeader(); i < n; i++)
                    {
                        switch (i)
                        {
                            case 0: value.nearest = GetFloat4(ref r); break;
                            case 1: value.edge = GetFloat4(ref r); break;
                            case 2: value.id = r.ReadSingle(); break;
                            default: r.Skip(); break;
                        }
                    }
                    return value;
                }),
        };

        var map = new Dictionary<Type, object>();
        foreach (var shape in shapes) map.Add(shape.Key, shape.Value);
        return map;
    }
}
