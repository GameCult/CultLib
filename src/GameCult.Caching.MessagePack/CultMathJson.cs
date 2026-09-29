using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using CultMath;
using Random = CultMath.Random;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The canonical System.Text.Json shape of every public CultMath value type, the JSON twin of
/// <see cref="CultMathResolver"/>. Vectors, quaternions and colors are objects keyed by their
/// field names (<c>{"x":1,"y":2}</c>), matrices are arrays of row objects, <c>rect</c> is
/// <c>{"min":..,"max":..}</c>, and <c>CultCellular</c> is <c>{"nearest":..,"edge":..,"id":..}</c>.
/// Reading skips unknown properties and leaves missing ones at zero (a missing <c>Color32.a</c>
/// is 255); a value that is not an object, or a matrix that is not an array of exactly its row count, is refused.
/// Register with <see cref="AddCultMathConverters"/>; JSON options are the consumer's own.
/// </summary>
public static class CultMathJson
{
    /// <summary>The CultMath types that have a converter, for coverage checks.</summary>
    public static IReadOnlyCollection<Type> ConvertedTypes => Converters.Keys;

    /// <summary>Adds the CultMath converters to <paramref name="options"/> and returns it.</summary>
    public static JsonSerializerOptions AddCultMathConverters(this JsonSerializerOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        foreach (var converter in Converters.Values) options.Converters.Add(converter);
        return options;
    }

    private sealed class ObjectShape<T> : JsonConverter<T>
    {
        private readonly Action<Utf8JsonWriter, T> write;
        private readonly Func<JsonElement, T> read;

        public ObjectShape(Action<Utf8JsonWriter, T> write, Func<JsonElement, T> read)
        {
            this.write = write;
            this.read = read;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return read(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => write(writer, value);
    }

    private static KeyValuePair<Type, JsonConverter> Shape<T>(Action<Utf8JsonWriter, T> write, Func<JsonElement, T> read) =>
        new(typeof(T), new ObjectShape<T>(write, read));

    private static JsonElement Obj(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");
        return e;
    }

    private static JsonElement Rows(JsonElement e, int count)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != count)
            throw new JsonException($"Expected a JSON array of {count} rows.");
        return e;
    }

    private static bool Has(JsonElement e, string name, out JsonElement value) => Obj(e).TryGetProperty(name, out value);
    private static float F(JsonElement e, string name) => Has(e, name, out var v) ? v.GetSingle() : 0f;
    private static double D(JsonElement e, string name) => Has(e, name, out var v) ? v.GetDouble() : 0d;
    private static int I(JsonElement e, string name) => Has(e, name, out var v) ? v.GetInt32() : 0;
    private static bool B(JsonElement e, string name) => Has(e, name, out var v) && v.GetBoolean();
    private static JsonElement Member(JsonElement e, string name) =>
        Has(e, name, out var v) ? v : Empty;
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    private static void PutFloat2(Utf8JsonWriter w, float2 v) { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteEndObject(); }
    private static void PutFloat3(Utf8JsonWriter w, float3 v) { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteNumber("z", v.z); w.WriteEndObject(); }
    private static void PutFloat4(Utf8JsonWriter w, float4 v) { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteNumber("z", v.z); w.WriteNumber("w", v.w); w.WriteEndObject(); }
    private static float2 GetFloat2(JsonElement e) => new(F(e, "x"), F(e, "y"));
    private static float3 GetFloat3(JsonElement e) => new(F(e, "x"), F(e, "y"), F(e, "z"));
    private static float4 GetFloat4(JsonElement e) => new(F(e, "x"), F(e, "y"), F(e, "z"), F(e, "w"));

    private static readonly Dictionary<Type, JsonConverter> Converters = Build();

    private static Dictionary<Type, JsonConverter> Build()
    {
        var shapes = new[]
        {
            Shape<float2>(PutFloat2, GetFloat2),
            Shape<float3>(PutFloat3, GetFloat3),
            Shape<float4>(PutFloat4, GetFloat4),
            Shape<double2>((w, v) => { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteEndObject(); },
                e => new double2(D(e, "x"), D(e, "y"))),
            Shape<double3>((w, v) => { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteNumber("z", v.z); w.WriteEndObject(); },
                e => new double3(D(e, "x"), D(e, "y"), D(e, "z"))),
            Shape<int2>((w, v) => { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteEndObject(); },
                e => new int2(I(e, "x"), I(e, "y"))),
            Shape<int3>((w, v) => { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteNumber("z", v.z); w.WriteEndObject(); },
                e => new int3(I(e, "x"), I(e, "y"), I(e, "z"))),
            Shape<int4>((w, v) => { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteNumber("z", v.z); w.WriteNumber("w", v.w); w.WriteEndObject(); },
                e => new int4(I(e, "x"), I(e, "y"), I(e, "z"), I(e, "w"))),
            Shape<bool2>((w, v) => { w.WriteStartObject(); w.WriteBoolean("x", v.x); w.WriteBoolean("y", v.y); w.WriteEndObject(); },
                e => new bool2(B(e, "x"), B(e, "y"))),
            Shape<bool3>((w, v) => { w.WriteStartObject(); w.WriteBoolean("x", v.x); w.WriteBoolean("y", v.y); w.WriteBoolean("z", v.z); w.WriteEndObject(); },
                e => new bool3(B(e, "x"), B(e, "y"), B(e, "z"))),
            Shape<bool4>((w, v) => { w.WriteStartObject(); w.WriteBoolean("x", v.x); w.WriteBoolean("y", v.y); w.WriteBoolean("z", v.z); w.WriteBoolean("w", v.w); w.WriteEndObject(); },
                e => new bool4(B(e, "x"), B(e, "y"), B(e, "z"), B(e, "w"))),
            Shape<quaternion>((w, v) => { w.WriteStartObject(); w.WriteNumber("x", v.x); w.WriteNumber("y", v.y); w.WriteNumber("z", v.z); w.WriteNumber("w", v.w); w.WriteEndObject(); },
                e => new quaternion(F(e, "x"), F(e, "y"), F(e, "z"), F(e, "w"))),
            Shape<Color32>((w, v) => { w.WriteStartObject(); w.WriteNumber("r", v.r); w.WriteNumber("g", v.g); w.WriteNumber("b", v.b); w.WriteNumber("a", v.a); w.WriteEndObject(); },
                e => new Color32(
                    Has(e, "r", out var r) ? r.GetByte() : (byte)0,
                    Has(e, "g", out var g) ? g.GetByte() : (byte)0,
                    Has(e, "b", out var b) ? b.GetByte() : (byte)0,
                    Has(e, "a", out var a) ? a.GetByte() : (byte)255)),
            Shape<Random>((w, v) => { w.WriteStartObject(); w.WriteNumber("state", v.state); w.WriteEndObject(); },
                e => new Random { state = Has(e, "state", out var s) ? s.GetUInt32() : 0u }),

            // Compositions: matrices are arrays of row objects, rect is {min, max}, CultCellular is {nearest, edge, id}.
            Shape<float2x2>((w, v) => { w.WriteStartArray(); PutFloat2(w, v[0]); PutFloat2(w, v[1]); w.WriteEndArray(); },
                e =>
                {
                    var rows = Rows(e, 2);
                    return new float2x2(GetFloat2(rows[0]), GetFloat2(rows[1]));
                }),
            Shape<float3x3>((w, v) => { w.WriteStartArray(); PutFloat3(w, v[0]); PutFloat3(w, v[1]); PutFloat3(w, v[2]); w.WriteEndArray(); },
                e =>
                {
                    var rows = Rows(e, 3);
                    return new float3x3(GetFloat3(rows[0]), GetFloat3(rows[1]), GetFloat3(rows[2]));
                }),
            Shape<rect>((w, v) => { w.WriteStartObject(); w.WritePropertyName("min"); PutFloat2(w, v.min); w.WritePropertyName("max"); PutFloat2(w, v.max); w.WriteEndObject(); },
                e => new rect(GetFloat2(Member(e, "min")), GetFloat2(Member(e, "max")))),
            Shape<CultCellular>((w, v) => { w.WriteStartObject(); w.WritePropertyName("nearest"); PutFloat4(w, v.nearest); w.WritePropertyName("edge"); PutFloat4(w, v.edge); w.WriteNumber("id", v.id); w.WriteEndObject(); },
                e => new CultCellular(GetFloat4(Member(e, "nearest")), GetFloat4(Member(e, "edge")), F(e, "id"))),
        };

        var map = new Dictionary<Type, JsonConverter>();
        foreach (var shape in shapes) map.Add(shape.Key, shape.Value);
        return map;
    }
}
