using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Property names are fixed: <c>PropertyNamingPolicy</c> does not rename them, and
/// <c>PropertyNameCaseInsensitive</c> makes reading case-insensitive.
/// <para>
/// Reading skips unknown properties and leaves missing ones at zero (a missing <c>Color32.a</c>
/// is 255). A wrong token, a number outside its component type (including a float beyond
/// <see cref="float.MaxValue"/>, which is refused rather than becoming infinity), or a matrix
/// that is not an array of exactly its row count throws <see cref="JsonException"/>.
/// </para>
/// <para>
/// Numbers follow <see cref="JsonSerializerOptions.NumberHandling"/> as System.Text.Json's own
/// float does: NaN and the infinities need <c>AllowNamedFloatingPointLiterals</c> (written and
/// read as <c>"NaN"</c>, <c>"Infinity"</c>, <c>"-Infinity"</c>) and are otherwise refused with
/// <see cref="JsonException"/>; <c>AllowReadingFromString</c> reads quoted numbers;
/// <c>WriteAsString</c> writes quoted numbers.
/// </para>
/// <para>
/// Every type is also a dictionary key: the flattened components joined by commas in
/// declaration order, invariant culture, shortest round-trip floats (<c>"1,2"</c>,
/// <c>"true,false"</c>, matrices row by row, <c>rect</c> as <c>"minX,minY,maxX,maxY"</c>).
/// </para>
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

    private static bool Named(JsonSerializerOptions o) => (o.NumberHandling & JsonNumberHandling.AllowNamedFloatingPointLiterals) != 0;
    private static bool FromString(JsonSerializerOptions o) => (o.NumberHandling & JsonNumberHandling.AllowReadingFromString) != 0;
    private static bool AsString(JsonSerializerOptions o) => (o.NumberHandling & JsonNumberHandling.WriteAsString) != 0;

    private static string Invalid(string what, string got) => $"{what} cannot be read from '{got}'.";

    // One component kind. Every CultMath type is a flat list of one kind, so a Scalar plus a Node tree describes it.
    private abstract class Scalar<S>
    {
        public virtual S[] Defaults(int count) => new S[count];
        public abstract void Write(Utf8JsonWriter w, S value, JsonSerializerOptions o);
        public abstract S Read(ref Utf8JsonReader r, JsonSerializerOptions o);
        public abstract string Text(S value, JsonSerializerOptions o);
        public abstract S Parse(string text, JsonSerializerOptions o);
    }

    private sealed class FloatScalar : Scalar<float>
    {
        public static readonly FloatScalar Instance = new();

        private static bool IsLiteral(string s) => s == "NaN" || s == "Infinity" || s == "-Infinity";

        private static string Literal(float v) => float.IsNaN(v) ? "NaN" : v > 0f ? "Infinity" : "-Infinity";

        public override void Write(Utf8JsonWriter w, float v, JsonSerializerOptions o)
        {
            if (!float.IsFinite(v))
            {
                if (!Named(o)) throw new JsonException("A non-finite float needs JsonNumberHandling.AllowNamedFloatingPointLiterals.");
                w.WriteStringValue(Literal(v));
            }
            else if (AsString(o)) w.WriteStringValue(v.ToString("R", CultureInfo.InvariantCulture));
            else w.WriteNumberValue(v);
        }

        public override float Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!r.TryGetSingle(out var v) || float.IsInfinity(v)) throw new JsonException("The number is outside the float range.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String)
            {
                var text = r.GetString()!;
                if (!FromString(o) && !(Named(o) && IsLiteral(text))) throw new JsonException($"Expected a number, found the string '{text}'.");
                return Parse(text, o);
            }

            throw new JsonException($"Expected a number, found {r.TokenType}.");
        }

        public override string Text(float v, JsonSerializerOptions o)
        {
            if (!float.IsFinite(v) && !Named(o)) throw new JsonException("A non-finite float needs JsonNumberHandling.AllowNamedFloatingPointLiterals.");
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public override float Parse(string text, JsonSerializerOptions o)
        {
            if (Named(o))
            {
                if (text == "NaN") return float.NaN;
                if (text == "Infinity") return float.PositiveInfinity;
                if (text == "-Infinity") return float.NegativeInfinity;
            }

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)) return v;
            throw new JsonException(Invalid("A float", text));
        }
    }

    private sealed class DoubleScalar : Scalar<double>
    {
        public static readonly DoubleScalar Instance = new();

        private static bool IsLiteral(string s) => s == "NaN" || s == "Infinity" || s == "-Infinity";

        private static string Literal(double v) => double.IsNaN(v) ? "NaN" : v > 0d ? "Infinity" : "-Infinity";

        public override void Write(Utf8JsonWriter w, double v, JsonSerializerOptions o)
        {
            if (!double.IsFinite(v))
            {
                if (!Named(o)) throw new JsonException("A non-finite double needs JsonNumberHandling.AllowNamedFloatingPointLiterals.");
                w.WriteStringValue(Literal(v));
            }
            else if (AsString(o)) w.WriteStringValue(v.ToString("R", CultureInfo.InvariantCulture));
            else w.WriteNumberValue(v);
        }

        public override double Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!r.TryGetDouble(out var v) || double.IsInfinity(v)) throw new JsonException("The number is outside the double range.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String)
            {
                var text = r.GetString()!;
                if (!FromString(o) && !(Named(o) && IsLiteral(text))) throw new JsonException($"Expected a number, found the string '{text}'.");
                return Parse(text, o);
            }

            throw new JsonException($"Expected a number, found {r.TokenType}.");
        }

        public override string Text(double v, JsonSerializerOptions o)
        {
            if (!double.IsFinite(v) && !Named(o)) throw new JsonException("A non-finite double needs JsonNumberHandling.AllowNamedFloatingPointLiterals.");
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public override double Parse(string text, JsonSerializerOptions o)
        {
            if (Named(o))
            {
                if (text == "NaN") return double.NaN;
                if (text == "Infinity") return double.PositiveInfinity;
                if (text == "-Infinity") return double.NegativeInfinity;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
            throw new JsonException(Invalid("A double", text));
        }
    }

    private sealed class IntScalar : Scalar<int>
    {
        public static readonly IntScalar Instance = new();

        public override void Write(Utf8JsonWriter w, int v, JsonSerializerOptions o)
        {
            if (AsString(o)) w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); else w.WriteNumberValue(v);
        }

        public override int Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!r.TryGetInt32(out var v)) throw new JsonException("The number is not an int.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String && FromString(o)) return Parse(r.GetString()!, o);
            throw new JsonException($"Expected an integer, found {r.TokenType}.");
        }

        public override string Text(int v, JsonSerializerOptions o) => v.ToString(CultureInfo.InvariantCulture);

        public override int Parse(string text, JsonSerializerOptions o) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : throw new JsonException(Invalid("An int", text));
    }

    private sealed class UIntScalar : Scalar<uint>
    {
        public static readonly UIntScalar Instance = new();

        public override void Write(Utf8JsonWriter w, uint v, JsonSerializerOptions o)
        {
            if (AsString(o)) w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); else w.WriteNumberValue(v);
        }

        public override uint Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!r.TryGetUInt32(out var v)) throw new JsonException("The number is not a uint.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String && FromString(o)) return Parse(r.GetString()!, o);
            throw new JsonException($"Expected an unsigned integer, found {r.TokenType}.");
        }

        public override string Text(uint v, JsonSerializerOptions o) => v.ToString(CultureInfo.InvariantCulture);

        public override uint Parse(string text, JsonSerializerOptions o) =>
            uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new JsonException(Invalid("A uint", text));
    }

    private sealed class ByteScalar : Scalar<byte>
    {
        public static readonly ByteScalar Instance = new();

        public override byte[] Defaults(int count)
        {
            var defaults = new byte[count];
            if (count == 4) defaults[3] = 255; // Color32 alpha
            return defaults;
        }

        public override void Write(Utf8JsonWriter w, byte v, JsonSerializerOptions o)
        {
            if (AsString(o)) w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); else w.WriteNumberValue(v);
        }

        public override byte Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!r.TryGetByte(out var v)) throw new JsonException("The number is not a byte.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String && FromString(o)) return Parse(r.GetString()!, o);
            throw new JsonException($"Expected a byte, found {r.TokenType}.");
        }

        public override string Text(byte v, JsonSerializerOptions o) => v.ToString(CultureInfo.InvariantCulture);

        public override byte Parse(string text, JsonSerializerOptions o) =>
            byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new JsonException(Invalid("A byte", text));
    }

    private sealed class BoolScalar : Scalar<bool>
    {
        public static readonly BoolScalar Instance = new();

        public override void Write(Utf8JsonWriter w, bool v, JsonSerializerOptions o) => w.WriteBooleanValue(v);

        public override bool Read(ref Utf8JsonReader r, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.True ? true
            : r.TokenType == JsonTokenType.False ? false
            : throw new JsonException($"Expected true or false, found {r.TokenType}.");

        public override string Text(bool v, JsonSerializerOptions o) => v ? "true" : "false";

        public override bool Parse(string text, JsonSerializerOptions o) =>
            text == "true" ? true : text == "false" ? false : throw new JsonException(Invalid("A bool", text));
    }

    // The JSON structure over a flat component list: leaves consume one component each, in order.
    private abstract class Node<S>
    {
        public int Count { get; protected set; }
        public abstract void Write(Utf8JsonWriter w, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o);
        public abstract void Read(ref Utf8JsonReader r, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o);
    }

    private sealed class Leaf<S> : Node<S>
    {
        public Leaf() { Count = 1; }

        public override void Write(Utf8JsonWriter w, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o) => scalar.Write(w, flat[offset], o);

        public override void Read(ref Utf8JsonReader r, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o) => flat[offset] = scalar.Read(ref r, o);
    }

    private sealed class Fields<S> : Node<S>
    {
        private readonly string[] names;
        private readonly Node<S>[] children;
        private readonly int[] offsets;

        public Fields(params (string Name, Node<S> Child)[] fields)
        {
            names = new string[fields.Length];
            children = new Node<S>[fields.Length];
            offsets = new int[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                names[i] = fields[i].Name;
                children[i] = fields[i].Child;
                offsets[i] = Count;
                Count += fields[i].Child.Count;
            }
        }

        public override void Write(Utf8JsonWriter w, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            for (var i = 0; i < names.Length; i++)
            {
                w.WritePropertyName(names[i]);
                children[i].Write(w, flat, offset + offsets[i], scalar, o);
            }

            w.WriteEndObject();
        }

        public override void Read(ref Utf8JsonReader r, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new JsonException($"Expected a JSON object, found {r.TokenType}.");
            var comparison = o.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString()!;
                var index = Array.FindIndex(names, n => string.Equals(n, name, comparison));
                r.Read();
                if (index < 0) r.Skip(); else children[index].Read(ref r, flat, offset + offsets[index], scalar, o);
            }
        }
    }

    private sealed class Rows<S> : Node<S>
    {
        private readonly Node<S> row;
        private readonly int rows;

        public Rows(int rows, Node<S> row)
        {
            this.rows = rows;
            this.row = row;
            Count = rows * row.Count;
        }

        public override void Write(Utf8JsonWriter w, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o)
        {
            w.WriteStartArray();
            for (var i = 0; i < rows; i++) row.Write(w, flat, offset + (i * row.Count), scalar, o);
            w.WriteEndArray();
        }

        public override void Read(ref Utf8JsonReader r, S[] flat, int offset, Scalar<S> scalar, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartArray) throw new JsonException($"Expected a JSON array of {rows} rows, found {r.TokenType}.");
            for (var i = 0; i < rows; i++)
            {
                if (!r.Read() || r.TokenType == JsonTokenType.EndArray) throw new JsonException($"Expected {rows} rows.");
                row.Read(ref r, flat, offset + (i * row.Count), scalar, o);
            }

            if (!r.Read() || r.TokenType != JsonTokenType.EndArray) throw new JsonException($"Expected {rows} rows.");
        }
    }

    private sealed class Shape<T, S> : JsonConverter<T>
    {
        private readonly Scalar<S> scalar;
        private readonly Node<S> root;
        private readonly Func<S[], T> make;
        private readonly Action<T, S[]> flatten;

        public Shape(Scalar<S> scalar, Node<S> root, Func<S[], T> make, Action<T, S[]> flatten)
        {
            this.scalar = scalar;
            this.root = root;
            this.make = make;
            this.flatten = flatten;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var flat = scalar.Defaults(root.Count);
            root.Read(ref reader, flat, 0, scalar, options);
            return make(flat);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var flat = new S[root.Count];
            flatten(value, flat);
            root.Write(writer, flat, 0, scalar, options);
        }

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = (reader.GetString() ?? throw new JsonException("A dictionary key cannot be null.")).Split(',');
            if (parts.Length != root.Count) throw new JsonException($"A {typeof(T).Name} key has {root.Count} comma-separated components.");
            var flat = new S[root.Count];
            for (var i = 0; i < flat.Length; i++) flat[i] = scalar.Parse(parts[i], options);
            return make(flat);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var flat = new S[root.Count];
            flatten(value, flat);
            writer.WritePropertyName(string.Join(",", Array.ConvertAll(flat, c => scalar.Text(c, options))));
        }
    }

    private static Node<S> Val<S>() => new Leaf<S>();

    private static Node<S> Xy<S>() => new Fields<S>(("x", Val<S>()), ("y", Val<S>()));
    private static Node<S> Xyz<S>() => new Fields<S>(("x", Val<S>()), ("y", Val<S>()), ("z", Val<S>()));
    private static Node<S> Xyzw<S>() => new Fields<S>(("x", Val<S>()), ("y", Val<S>()), ("z", Val<S>()), ("w", Val<S>()));

    private static KeyValuePair<Type, JsonConverter> Add<T, S>(
        Scalar<S> scalar, Node<S> root, Func<S[], T> make, Action<T, S[]> flatten) =>
        new(typeof(T), new Shape<T, S>(scalar, root, make, flatten));

    private static readonly Dictionary<Type, JsonConverter> Converters = Build();

    private static Dictionary<Type, JsonConverter> Build()
    {
        var f = FloatScalar.Instance;
        var shapes = new[]
        {
            Add<float2, float>(f, Xy<float>(), a => new float2(a[0], a[1]), (v, a) => { a[0] = v.x; a[1] = v.y; }),
            Add<float3, float>(f, Xyz<float>(), a => new float3(a[0], a[1], a[2]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; }),
            Add<float4, float>(f, Xyzw<float>(), a => new float4(a[0], a[1], a[2], a[3]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; a[3] = v.w; }),
            Add<quaternion, float>(f, Xyzw<float>(), a => new quaternion(a[0], a[1], a[2], a[3]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; a[3] = v.w; }),
            Add<double2, double>(DoubleScalar.Instance, Xy<double>(), a => new double2(a[0], a[1]), (v, a) => { a[0] = v.x; a[1] = v.y; }),
            Add<double3, double>(DoubleScalar.Instance, Xyz<double>(), a => new double3(a[0], a[1], a[2]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; }),
            Add<int2, int>(IntScalar.Instance, Xy<int>(), a => new int2(a[0], a[1]), (v, a) => { a[0] = v.x; a[1] = v.y; }),
            Add<int3, int>(IntScalar.Instance, Xyz<int>(), a => new int3(a[0], a[1], a[2]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; }),
            Add<int4, int>(IntScalar.Instance, Xyzw<int>(), a => new int4(a[0], a[1], a[2], a[3]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; a[3] = v.w; }),
            Add<bool2, bool>(BoolScalar.Instance, Xy<bool>(), a => new bool2(a[0], a[1]), (v, a) => { a[0] = v.x; a[1] = v.y; }),
            Add<bool3, bool>(BoolScalar.Instance, Xyz<bool>(), a => new bool3(a[0], a[1], a[2]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; }),
            Add<bool4, bool>(BoolScalar.Instance, Xyzw<bool>(), a => new bool4(a[0], a[1], a[2], a[3]), (v, a) => { a[0] = v.x; a[1] = v.y; a[2] = v.z; a[3] = v.w; }),
            Add<Color32, byte>(ByteScalar.Instance,
                new Fields<byte>(("r", Val<byte>()), ("g", Val<byte>()), ("b", Val<byte>()), ("a", Val<byte>())),
                a => new Color32(a[0], a[1], a[2], a[3]), (v, a) => { a[0] = v.r; a[1] = v.g; a[2] = v.b; a[3] = v.a; }),
            Add<Random, uint>(UIntScalar.Instance, new Fields<uint>(("state", Val<uint>())),
                a => new Random { state = a[0] }, (v, a) => a[0] = v.state),

            // Compositions: matrices are arrays of row objects, rect is {min, max}, CultCellular is {nearest, edge, id}.
            Add<float2x2, float>(f, new Rows<float>(2, Xy<float>()),
                a => new float2x2(a[0], a[1], a[2], a[3]),
                (v, a) => { a[0] = v[0].x; a[1] = v[0].y; a[2] = v[1].x; a[3] = v[1].y; }),
            Add<float3x3, float>(f, new Rows<float>(3, Xyz<float>()),
                a => new float3x3(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]),
                (v, a) =>
                {
                    for (var row = 0; row < 3; row++)
                    {
                        a[row * 3] = v[row].x;
                        a[(row * 3) + 1] = v[row].y;
                        a[(row * 3) + 2] = v[row].z;
                    }
                }),
            Add<rect, float>(f, new Fields<float>(("min", Xy<float>()), ("max", Xy<float>())),
                a => new rect(a[0], a[1], a[2], a[3]),
                (v, a) => { a[0] = v.min.x; a[1] = v.min.y; a[2] = v.max.x; a[3] = v.max.y; }),
            Add<CultCellular, float>(f, new Fields<float>(("nearest", Xyzw<float>()), ("edge", Xyzw<float>()), ("id", Val<float>())),
                a => new CultCellular(new float4(a[0], a[1], a[2], a[3]), new float4(a[4], a[5], a[6], a[7]), a[8]),
                (v, a) =>
                {
                    a[0] = v.nearest.x; a[1] = v.nearest.y; a[2] = v.nearest.z; a[3] = v.nearest.w;
                    a[4] = v.edge.x; a[5] = v.edge.y; a[6] = v.edge.z; a[7] = v.edge.w;
                    a[8] = v.id;
                }),
        };

        var map = new Dictionary<Type, JsonConverter>();
        foreach (var shape in shapes) map.Add(shape.Key, shape.Value);
        return map;
    }
}
