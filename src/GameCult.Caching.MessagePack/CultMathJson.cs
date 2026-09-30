using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CultMath;
using Random = CultMath.Random;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The canonical System.Text.Json shape of every public CultMath value type, the JSON twin of
/// <see cref="CultMathResolver"/>. Vectors, quaternions and colors are objects keyed by their
/// field names (<c>{"x":1,"y":2}</c>), matrices are arrays of row objects, <c>rect</c> is
/// <c>{"min":..,"max":..}</c>, <c>CultCellular</c> is <c>{"nearest":..,"edge":..,"id":..}</c>, and <c>CultPhasor</c> is <c>{"cos":..,"sin":..}</c>.
/// Property names are fixed: <c>PropertyNamingPolicy</c> does not rename them, and
/// <c>PropertyNameCaseInsensitive</c> makes reading case-insensitive.
/// <para>
/// Reading skips unknown properties (<c>UnmappedMemberHandling.Disallow</c> refuses them) and
/// leaves missing ones at zero (a missing <c>Color32.a</c> is 255). A repeated property replaces
/// the whole earlier value, never merging component-wise, and is refused when
/// <c>AllowDuplicateProperties</c> is false. A wrong token, a number outside its component type,
/// or a matrix that is not an array of exactly its row count throws
/// <see cref="JsonException"/>.
/// </para>
/// <para>
/// Numbers follow <see cref="JsonSerializerOptions.NumberHandling"/> as System.Text.Json's own
/// float does. NaN and the infinities are the strings <c>"NaN"</c>, <c>"Infinity"</c>,
/// <c>"-Infinity"</c>: written when <c>AllowNamedFloatingPointLiterals</c> or
/// <c>WriteAsString</c> is set (otherwise <see cref="JsonException"/>), read when
/// <c>AllowNamedFloatingPointLiterals</c> or <c>AllowReadingFromString</c> is set.
/// <c>AllowReadingFromString</c> reads quoted numbers without surrounding whitespace, and
/// <c>WriteAsString</c> writes quoted numbers. Two intended divergences: a bare number beyond
/// the component's range (<c>1e40</c> as a float) is refused where System.Text.Json reads
/// infinity, and refusals on write are <see cref="JsonException"/> where it throws
/// <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// Every type is also a dictionary key: the flattened components joined by commas in
/// declaration order, invariant culture, shortest round-trip floats (<c>"1,2"</c>,
/// <c>"true,false"</c>, matrices row by row, <c>rect</c> as <c>"minX,minY,maxX,maxY"</c>).
/// Only that exact spelling reads back, so two JSON keys never collapse into one dictionary key.
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

    private static bool LiteralsWrite(JsonSerializerOptions o) =>
        (o.NumberHandling & (JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.WriteAsString)) != 0;

    private static bool LiteralsRead(JsonSerializerOptions o) =>
        (o.NumberHandling & (JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString)) != 0;

    private static bool FromString(JsonSerializerOptions o) => (o.NumberHandling & JsonNumberHandling.AllowReadingFromString) != 0;
    private static bool AsString(JsonSerializerOptions o) => (o.NumberHandling & JsonNumberHandling.WriteAsString) != 0;

    // AllowDuplicateProperties arrived after the System.Text.Json this library compiles against.
    private static readonly PropertyInfo? AllowDuplicateProperties = typeof(JsonSerializerOptions).GetProperty("AllowDuplicateProperties");

    private static bool DuplicatesAllowed(JsonSerializerOptions o) => AllowDuplicateProperties == null || (bool)AllowDuplicateProperties.GetValue(o)!;

    private const NumberStyles FloatStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    // One component kind. Every CultMath type is a flat list of one kind, so a Scalar plus a Node tree describes it.
    private abstract class Scalar<S> where S : unmanaged
    {
        public abstract void Write(Utf8JsonWriter w, S value, JsonSerializerOptions o);
        public abstract S Read(ref Utf8JsonReader r, JsonSerializerOptions o);

        /// <summary>The canonical key spelling, whatever the options allow.</summary>
        public abstract string Spelling(S value);

        /// <summary>The key spelling, refusing what the options would refuse to write.</summary>
        public virtual string Text(S value, JsonSerializerOptions o) => Spelling(value);

        public abstract S Parse(string text, JsonSerializerOptions o);

        /// <summary>Reads a key component; keys are always text, so AllowReadingFromString does not apply.</summary>
        public virtual S ParseKey(string text, JsonSerializerOptions o) => Parse(text, o);
    }

    private abstract class FloatingScalar<S> : Scalar<S> where S : unmanaged
    {
        protected static bool IsLiteral(string s) => s == "NaN" || s == "Infinity" || s == "-Infinity";
        protected abstract bool IsFinite(S value);
        protected abstract string Literal(S value);
        protected abstract S FromLiteral(string literal);
        protected abstract bool TryNumber(string text, out S value);
        protected abstract bool TryToken(ref Utf8JsonReader r, out S value);
        protected abstract void WriteNumber(Utf8JsonWriter w, S value);
        protected abstract string Number(S value);

        public override void Write(Utf8JsonWriter w, S v, JsonSerializerOptions o)
        {
            if (!IsFinite(v))
            {
                if (!LiteralsWrite(o)) throw new JsonException($"A non-finite {typeof(S).Name} needs JsonNumberHandling.AllowNamedFloatingPointLiterals or WriteAsString.");
                w.WriteStringValue(Literal(v));
            }
            else if (AsString(o)) w.WriteStringValue(Number(v));
            else WriteNumber(w, v);
        }

        public override S Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!TryToken(ref r, out var v) || !IsFinite(v)) throw new JsonException($"The number is outside the {typeof(S).Name} range.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String) return Parse(r.GetString()!, o);
            throw new JsonException($"Expected a number, found {r.TokenType}.");
        }

        public override string Spelling(S v) => Number(v);

        public override string Text(S v, JsonSerializerOptions o)
        {
            if (!IsFinite(v) && !LiteralsWrite(o)) throw new JsonException($"A non-finite {typeof(S).Name} needs JsonNumberHandling.AllowNamedFloatingPointLiterals or WriteAsString.");
            return Spelling(v);
        }

        public override S Parse(string text, JsonSerializerOptions o)
        {
            if (!IsLiteral(text) && !FromString(o)) throw new JsonException($"Expected a number, found the string '{text}'.");
            return ParseKey(text, o);
        }

        public override S ParseKey(string text, JsonSerializerOptions o)
        {
            if (IsLiteral(text))
            {
                if (!LiteralsRead(o)) throw new JsonException($"'{text}' needs JsonNumberHandling.AllowNamedFloatingPointLiterals or AllowReadingFromString.");
                return FromLiteral(text);
            }

            if (TryNumber(text, out var v) && IsFinite(v)) return v;
            throw new JsonException($"A {typeof(S).Name} cannot be read from '{text}'.");
        }
    }

    private sealed class FloatScalar : FloatingScalar<float>
    {
        public static readonly FloatScalar Instance = new();
        protected override bool IsFinite(float v) => float.IsFinite(v);
        protected override string Literal(float v) => float.IsNaN(v) ? "NaN" : v > 0f ? "Infinity" : "-Infinity";
        protected override float FromLiteral(string s) => s == "NaN" ? float.NaN : s == "Infinity" ? float.PositiveInfinity : float.NegativeInfinity;
        protected override bool TryNumber(string s, out float v) => float.TryParse(s, FloatStyle, CultureInfo.InvariantCulture, out v);
        protected override bool TryToken(ref Utf8JsonReader r, out float v) => r.TryGetSingle(out v);
        protected override void WriteNumber(Utf8JsonWriter w, float v) => w.WriteNumberValue(v);
        protected override string Number(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    private sealed class DoubleScalar : FloatingScalar<double>
    {
        public static readonly DoubleScalar Instance = new();
        protected override bool IsFinite(double v) => double.IsFinite(v);
        protected override string Literal(double v) => double.IsNaN(v) ? "NaN" : v > 0d ? "Infinity" : "-Infinity";
        protected override double FromLiteral(string s) => s == "NaN" ? double.NaN : s == "Infinity" ? double.PositiveInfinity : double.NegativeInfinity;
        protected override bool TryNumber(string s, out double v) => double.TryParse(s, FloatStyle, CultureInfo.InvariantCulture, out v);
        protected override bool TryToken(ref Utf8JsonReader r, out double v) => r.TryGetDouble(out v);
        protected override void WriteNumber(Utf8JsonWriter w, double v) => w.WriteNumberValue(v);
        protected override string Number(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    private abstract class IntegerScalar<S> : Scalar<S> where S : unmanaged
    {
        protected abstract bool TryToken(ref Utf8JsonReader r, out S value);
        protected abstract bool TryText(string text, out S value);
        protected abstract void WriteNumber(Utf8JsonWriter w, S value);

        public override void Write(Utf8JsonWriter w, S v, JsonSerializerOptions o)
        {
            if (AsString(o)) w.WriteStringValue(Spelling(v)); else WriteNumber(w, v);
        }

        public override S Read(ref Utf8JsonReader r, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
            {
                if (!TryToken(ref r, out var v)) throw new JsonException($"The number is not a {typeof(S).Name}.");
                return v;
            }

            if (r.TokenType == JsonTokenType.String && FromString(o)) return Parse(r.GetString()!, o);
            throw new JsonException($"Expected an integer, found {r.TokenType}.");
        }

        public override S Parse(string text, JsonSerializerOptions o) =>
            TryText(text, out var v) ? v : throw new JsonException($"A {typeof(S).Name} cannot be read from '{text}'.");
    }

    private sealed class IntScalar : IntegerScalar<int>
    {
        public static readonly IntScalar Instance = new();
        protected override bool TryToken(ref Utf8JsonReader r, out int v) => r.TryGetInt32(out v);
        protected override bool TryText(string s, out int v) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
        protected override void WriteNumber(Utf8JsonWriter w, int v) => w.WriteNumberValue(v);
        public override string Spelling(int v) => v.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class UIntScalar : IntegerScalar<uint>
    {
        public static readonly UIntScalar Instance = new();
        protected override bool TryToken(ref Utf8JsonReader r, out uint v) => r.TryGetUInt32(out v);
        protected override bool TryText(string s, out uint v) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        protected override void WriteNumber(Utf8JsonWriter w, uint v) => w.WriteNumberValue(v);
        public override string Spelling(uint v) => v.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class ByteScalar : IntegerScalar<byte>
    {
        public static readonly ByteScalar Instance = new();
        protected override bool TryToken(ref Utf8JsonReader r, out byte v) => r.TryGetByte(out v);
        protected override bool TryText(string s, out byte v) => byte.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        protected override void WriteNumber(Utf8JsonWriter w, byte v) => w.WriteNumberValue(v);
        public override string Spelling(byte v) => v.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class BoolScalar : Scalar<bool>
    {
        public static readonly BoolScalar Instance = new();

        public override void Write(Utf8JsonWriter w, bool v, JsonSerializerOptions o) => w.WriteBooleanValue(v);

        public override bool Read(ref Utf8JsonReader r, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.True ? true
            : r.TokenType == JsonTokenType.False ? false
            : throw new JsonException($"Expected true or false, found {r.TokenType}.");

        public override string Spelling(bool v) => v ? "true" : "false";

        public override bool Parse(string text, JsonSerializerOptions o) =>
            text == "true" ? true : text == "false" ? false : throw new JsonException($"A bool cannot be read from '{text}'.");
    }

    // The JSON structure over a flat component list: leaves consume one component each, in order.
    // Read fills `flat` (pre-loaded with `defaults`), so a missing property keeps its default.
    private abstract class Node<S> where S : unmanaged
    {
        public int Count { get; protected set; }
        public abstract void Write(Utf8JsonWriter w, ReadOnlySpan<S> flat, Scalar<S> scalar, JsonSerializerOptions o);
        public abstract void Read(ref Utf8JsonReader r, scoped Span<S> flat, scoped ReadOnlySpan<S> defaults, Scalar<S> scalar, JsonSerializerOptions o);
    }

    private sealed class Leaf<S> : Node<S> where S : unmanaged
    {
        public Leaf() { Count = 1; }

        public override void Write(Utf8JsonWriter w, ReadOnlySpan<S> flat, Scalar<S> scalar, JsonSerializerOptions o) => scalar.Write(w, flat[0], o);

        public override void Read(ref Utf8JsonReader r, scoped Span<S> flat, scoped ReadOnlySpan<S> defaults, Scalar<S> scalar, JsonSerializerOptions o) => flat[0] = scalar.Read(ref r, o);
    }

    private sealed class Fields<S> : Node<S> where S : unmanaged
    {
        private readonly string[] names;
        private readonly byte[][] utf8Names;
        private readonly JsonEncodedText[] encoded;
        private readonly Node<S>[] children;
        private readonly int[] offsets;

        public Fields(params (string Name, Node<S> Child)[] fields)
        {
            names = new string[fields.Length];
            utf8Names = new byte[fields.Length][];
            encoded = new JsonEncodedText[fields.Length];
            children = new Node<S>[fields.Length];
            offsets = new int[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                names[i] = fields[i].Name;
                utf8Names[i] = Encoding.UTF8.GetBytes(fields[i].Name);
                encoded[i] = JsonEncodedText.Encode(fields[i].Name);
                children[i] = fields[i].Child;
                offsets[i] = Count;
                Count += fields[i].Child.Count;
            }
        }

        public override void Write(Utf8JsonWriter w, ReadOnlySpan<S> flat, Scalar<S> scalar, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            for (var i = 0; i < children.Length; i++)
            {
                w.WritePropertyName(encoded[i]);
                children[i].Write(w, flat.Slice(offsets[i], children[i].Count), scalar, o);
            }

            w.WriteEndObject();
        }

        public override void Read(ref Utf8JsonReader r, scoped Span<S> flat, scoped ReadOnlySpan<S> defaults, Scalar<S> scalar, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new JsonException($"Expected a JSON object, found {r.TokenType}.");
            var ignoreCase = o.PropertyNameCaseInsensitive;
            var seen = 0;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var index = IndexOf(ref r, ignoreCase);
                r.Read();
                if (index < 0)
                {
                    if (o.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
                        throw new JsonException("The JSON object has a property that is not a component.");
                    r.Skip();
                    continue;
                }

                var slice = flat.Slice(offsets[index], children[index].Count);
                var bit = 1 << index;
                if ((seen & bit) != 0)
                {
                    if (!DuplicatesAllowed(o)) throw new JsonException($"The property '{names[index]}' appears more than once.");
                    defaults.Slice(offsets[index], slice.Length).CopyTo(slice);
                }

                seen |= bit;
                children[index].Read(ref r, slice, defaults.Slice(offsets[index], slice.Length), scalar, o);
            }
        }

        private int IndexOf(ref Utf8JsonReader r, bool ignoreCase)
        {
            if (!ignoreCase)
            {
                for (var i = 0; i < utf8Names.Length; i++)
                    if (r.ValueTextEquals(utf8Names[i])) return i;
                return -1;
            }

            var name = r.GetString()!;
            for (var i = 0; i < names.Length; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    private sealed class Rows<S> : Node<S> where S : unmanaged
    {
        private readonly Node<S> row;
        private readonly int rows;

        public Rows(int rows, Node<S> row)
        {
            this.rows = rows;
            this.row = row;
            Count = rows * row.Count;
        }

        public override void Write(Utf8JsonWriter w, ReadOnlySpan<S> flat, Scalar<S> scalar, JsonSerializerOptions o)
        {
            w.WriteStartArray();
            for (var i = 0; i < rows; i++) row.Write(w, flat.Slice(i * row.Count, row.Count), scalar, o);
            w.WriteEndArray();
        }

        public override void Read(ref Utf8JsonReader r, scoped Span<S> flat, scoped ReadOnlySpan<S> defaults, Scalar<S> scalar, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartArray) throw new JsonException($"Expected a JSON array of {rows} rows, found {r.TokenType}.");
            for (var i = 0; i < rows; i++)
            {
                if (!r.Read() || r.TokenType == JsonTokenType.EndArray) throw new JsonException($"Expected {rows} rows.");
                row.Read(ref r, flat.Slice(i * row.Count, row.Count), defaults.Slice(i * row.Count, row.Count), scalar, o);
            }

            if (!r.Read() || r.TokenType != JsonTokenType.EndArray) throw new JsonException($"Expected {rows} rows.");
        }
    }

    private delegate T Make<T, S>(ReadOnlySpan<S> components) where S : unmanaged;
    private delegate void Flatten<T, S>(T value, Span<S> components) where S : unmanaged;

    private sealed class Shape<T, S> : JsonConverter<T> where S : unmanaged
    {
        private readonly Scalar<S> scalar;
        private readonly Node<S> root;
        private readonly S[] defaults;
        private readonly Make<T, S> make;
        private readonly Flatten<T, S> flatten;

        public Shape(Scalar<S> scalar, Node<S> root, Make<T, S> make, Flatten<T, S> flatten, S[]? initial)
        {
            this.scalar = scalar;
            this.root = root;
            this.make = make;
            this.flatten = flatten;
            defaults = initial ?? new S[root.Count];
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Span<S> flat = stackalloc S[root.Count];
            defaults.CopyTo(flat);
            root.Read(ref reader, flat, defaults, scalar, options);
            return make(flat);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            Span<S> flat = stackalloc S[root.Count];
            flatten(value, flat);
            root.Write(writer, flat, scalar, options);
        }

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException("A dictionary key cannot be null.");
            var parts = text.Split(',');
            if (parts.Length != root.Count) throw new JsonException($"A {typeof(T).Name} key has {root.Count} comma-separated components.");
            Span<S> flat = stackalloc S[root.Count];
            for (var i = 0; i < parts.Length; i++) flat[i] = scalar.ParseKey(parts[i], options);
            var value = make(flat);
            if (Spell(flat) != text) throw new JsonException($"'{text}' is not the canonical spelling of a {typeof(T).Name} key.");
            return value;
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            Span<S> flat = stackalloc S[root.Count];
            flatten(value, flat);
            for (var i = 0; i < flat.Length; i++) scalar.Text(flat[i], options);
            writer.WritePropertyName(Spell(flat));
        }

        private string Spell(ReadOnlySpan<S> flat)
        {
            var parts = new string[flat.Length];
            for (var i = 0; i < parts.Length; i++) parts[i] = scalar.Spelling(flat[i]);
            return string.Join(",", parts);
        }
    }

    private static Node<S> Val<S>() where S : unmanaged => new Leaf<S>();

    private static Node<S> Xy<S>() where S : unmanaged => new Fields<S>(("x", Val<S>()), ("y", Val<S>()));
    private static Node<S> Xyz<S>() where S : unmanaged => new Fields<S>(("x", Val<S>()), ("y", Val<S>()), ("z", Val<S>()));
    private static Node<S> Xyzw<S>() where S : unmanaged => new Fields<S>(("x", Val<S>()), ("y", Val<S>()), ("z", Val<S>()), ("w", Val<S>()));

    private static KeyValuePair<Type, JsonConverter> Add<T, S>(
        Scalar<S> scalar, Node<S> root, Make<T, S> make, Flatten<T, S> flatten, S[]? initial = null) where S : unmanaged =>
        new(typeof(T), new Shape<T, S>(scalar, root, make, flatten, initial));

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
                a => new Color32(a[0], a[1], a[2], a[3]), (v, a) => { a[0] = v.r; a[1] = v.g; a[2] = v.b; a[3] = v.a; },
                initial: new byte[] { 0, 0, 0, 255 }),
            Add<Random, uint>(UIntScalar.Instance, new Fields<uint>(("state", Val<uint>())),
                a => new Random { state = a[0] }, (v, a) => a[0] = v.state),

            // Compositions: matrices are arrays of row objects, rect is {min, max}, CultCellular is {nearest, edge, id}, CultPhasor is {cos, sin}.
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
            Add<CultPhasor, float>(f, new Fields<float>(("cos", Xyzw<float>()), ("sin", Xyzw<float>())),
                a => new CultPhasor(new float4(a[0], a[1], a[2], a[3]), new float4(a[4], a[5], a[6], a[7])),
                (v, a) =>
                {
                    a[0] = v.cos.x; a[1] = v.cos.y; a[2] = v.cos.z; a[3] = v.cos.w;
                    a[4] = v.sin.x; a[5] = v.sin.y; a[6] = v.sin.z; a[7] = v.sin.w;
                }),
        };

        var map = new Dictionary<Type, JsonConverter>();
        foreach (var shape in shapes) map.Add(shape.Key, shape.Value);
        return map;
    }
}
