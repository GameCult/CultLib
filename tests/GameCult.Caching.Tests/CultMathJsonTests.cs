using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CultMath;
using FluentAssertions;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public sealed class CultMathJsonTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions().AddCultMathConverters();

        private static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

        private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

        [Test]
        public void EveryShape_IsPinned()
        {
            Json(new float2(1f, 2f)).Should().Be("""{"x":1,"y":2}""");
            Json(new float3(1f, 2f, 3f)).Should().Be("""{"x":1,"y":2,"z":3}""");
            Json(new float4(1f, 2f, 3f, 4f)).Should().Be("""{"x":1,"y":2,"z":3,"w":4}""");
            Json(new double2(1d, 2.5d)).Should().Be("""{"x":1,"y":2.5}""");
            Json(new double3(1d, 2d, 3d)).Should().Be("""{"x":1,"y":2,"z":3}""");
            Json(new int2(3, -4)).Should().Be("""{"x":3,"y":-4}""");
            Json(new int3(1, 2, 3)).Should().Be("""{"x":1,"y":2,"z":3}""");
            Json(new int4(1, 2, 3, 4)).Should().Be("""{"x":1,"y":2,"z":3,"w":4}""");
            Json(new bool2(true, false)).Should().Be("""{"x":true,"y":false}""");
            Json(new bool3(true, false, true)).Should().Be("""{"x":true,"y":false,"z":true}""");
            Json(new bool4(true, false, false, true)).Should().Be("""{"x":true,"y":false,"z":false,"w":true}""");
            Json(new quaternion(0f, 0f, 0f, 1f)).Should().Be("""{"x":0,"y":0,"z":0,"w":1}""");
            Json(new Color32(255, 128, 0, 7)).Should().Be("""{"r":255,"g":128,"b":0,"a":7}""");
            Json(new CultMath.Random(12345u)).Should().Be("""{"state":12345}""");
            Json(new float2x2(1f, 2f, 3f, 4f)).Should().Be("""[{"x":1,"y":2},{"x":3,"y":4}]""");
            Json(new float3x3(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f)).Should().Be(
                """[{"x":1,"y":2,"z":3},{"x":4,"y":5,"z":6},{"x":7,"y":8,"z":9}]""");
            Json(new rect(5f, 7f, -1f, -2f)).Should().Be("""{"min":{"x":-1,"y":-2},"max":{"x":5,"y":7}}""");
            Json(new CultCellular(new float4(1f, 2f, 3f, 4f), new float4(5f, 6f, 7f, 8f), 0.25f)).Should().Be(
                """{"nearest":{"x":1,"y":2,"z":3,"w":4},"edge":{"x":5,"y":6,"z":7,"w":8},"id":0.25}""");
        }

        [Test]
        public void EveryShape_RoundTrips()
        {
            RoundTrip(new float2(1.5f, -2f));
            RoundTrip(new float3(1f, 2f, 3f));
            RoundTrip(new float4(1f, 2f, 3f, 4f));
            RoundTrip(new double2(1e300, -2.5));
            RoundTrip(new double3(1, 2, 3));
            RoundTrip(new int2(-7, 900000));
            RoundTrip(new int3(1, -2, 3));
            RoundTrip(new int4(1, 2, 3, int.MinValue));
            RoundTrip(new bool2(true, false));
            RoundTrip(new bool3(false, true, true));
            RoundTrip(new bool4(true, false, false, true));
            RoundTrip(new quaternion(0.1f, 0.2f, 0.3f, 0.9f));
            RoundTrip(new Color32(255, 128, 0, 7));
            RoundTrip(new CultMath.Random(12345u));
            RoundTrip(new float2x2(1f, 2f, 3f, 4f));
            RoundTrip(new float3x3(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f));
            RoundTrip(new rect(-1f, -2f, 5f, 7f));
            RoundTrip(new CultCellular(new float4(1, 2, 3, 4), new float4(5, 6, 7, 8), 0.25f));
        }

        [Test]
        public void Reading_SkipsUnknownPropertiesAndZeroesMissingOnes()
        {
            Parse<float3>("""{"z":3,"extra":[1,2],"x":1}""").Should().Be(new float3(1f, 0f, 3f));
            Parse<Color32>("""{"r":1,"g":2,"b":3}""").Should().Be(new Color32(1, 2, 3, 255));
            Parse<float2?>("null").Should().BeNull();
        }

        [Test]
        public void Reading_RefusesShapesThatAreNotTheType()
        {
            Action array = () => Parse<float2>("[1,2]");
            array.Should().Throw<JsonException>();
            Action shortMatrix = () => Parse<float2x2>("""[{"x":1,"y":2}]""");
            shortMatrix.Should().Throw<JsonException>();
            Action nil = () => Parse<rect>("null");
            nil.Should().Throw<JsonException>();
        }

        [Test]
        public void EveryPublicCultMathValueType_HasAConverter()
        {
            var valueTypes = typeof(float2).Assembly.GetExportedTypes()
                .Where(t => t.IsValueType && !t.IsEnum)
                .ToHashSet();

            valueTypes.Except(CultMathJson.ConvertedTypes).Should().BeEmpty();
            CultMathJson.ConvertedTypes.Except(valueTypes).Should().BeEmpty();
            foreach (var type in valueTypes)
            {
                var json = JsonSerializer.Serialize(Activator.CreateInstance(type), type, Options);
                JsonSerializer.Deserialize(json, type, Options).Should().Be(Activator.CreateInstance(type));
            }
        }

        private static JsonSerializerOptions With(JsonNumberHandling handling = JsonNumberHandling.Strict, bool ignoreCase = false) =>
            new JsonSerializerOptions { NumberHandling = handling, PropertyNameCaseInsensitive = ignoreCase }.AddCultMathConverters();

        private const JsonNumberHandling Named = JsonNumberHandling.AllowNamedFloatingPointLiterals;

        [Test]
        public void NonFiniteFloats_NeedNamedLiterals_AsInSystemTextJson()
        {
            Action nan = () => JsonSerializer.Serialize(new float2(float.NaN, 1f), Options);
            nan.Should().Throw<JsonException>();
            Action inf = () => JsonSerializer.Serialize(new double3(1d, double.PositiveInfinity, 0d), Options);
            inf.Should().Throw<JsonException>();
            Action named = () => JsonSerializer.Deserialize<float2>("""{"x":"NaN","y":1}""", Options);
            named.Should().Throw<JsonException>();

            var options = With(Named);
            JsonSerializer.Serialize(new float2(float.NaN, 1f), options).Should().Be("""{"x":"NaN","y":1}""");
            JsonSerializer.Serialize(new float3(float.PositiveInfinity, float.NegativeInfinity, 0f), options)
                .Should().Be("""{"x":"Infinity","y":"-Infinity","z":0}""");
            var back = JsonSerializer.Deserialize<float3>("""{"x":"Infinity","y":"-Infinity","z":"NaN"}""", options);
            back.x.Should().Be(float.PositiveInfinity);
            back.y.Should().Be(float.NegativeInfinity);
            float.IsNaN(back.z).Should().BeTrue();
            JsonSerializer.Deserialize<double2>("""{"x":"NaN","y":"-Infinity"}""", options).y.Should().Be(double.NegativeInfinity);
        }

        [Test]
        public void Numbers_OutsideTheComponentType_AreRefused()
        {
            foreach (var json in new[] { """{"x":1e40}""", """{"x":-1e40}""" })
            {
                Action tooBig = () => Parse<float2>(json);
                tooBig.Should().Throw<JsonException>(json);
            }

            Action doubleTooBig = () => Parse<double2>("""{"x":1e400}""");
            doubleTooBig.Should().Throw<JsonException>();
            Action alpha = () => Parse<Color32>("""{"r":300}""");
            alpha.Should().Throw<JsonException>();
            Action negativeByte = () => Parse<Color32>("""{"g":-1}""");
            negativeByte.Should().Throw<JsonException>();
            Action fraction = () => Parse<int2>("""{"x":1.5}""");
            fraction.Should().Throw<JsonException>();
            Action wrongToken = () => Parse<float2>("""{"x":"a"}""");
            wrongToken.Should().Throw<JsonException>();
            Action boolean = () => Parse<bool2>("""{"x":1}""");
            boolean.Should().Throw<JsonException>();
            Action negativeState = () => Parse<CultMath.Random>("""{"state":-1}""");
            negativeState.Should().Throw<JsonException>();
        }

        [Test]
        public void NumberHandling_QuotesAndUnquotesNumbers()
        {
            JsonSerializer.Serialize(new float2(1f, 2.5f), With(JsonNumberHandling.WriteAsString)).Should().Be("""{"x":"1","y":"2.5"}""");
            JsonSerializer.Serialize(new int2(1, 2), With(JsonNumberHandling.WriteAsString)).Should().Be("""{"x":"1","y":"2"}""");
            JsonSerializer.Deserialize<float2>("""{"x":"1.5","y":2}""", With(JsonNumberHandling.AllowReadingFromString))
                .Should().Be(new float2(1.5f, 2f));
            JsonSerializer.Deserialize<int2>("""{"x":"7","y":2}""", With(JsonNumberHandling.AllowReadingFromString))
                .Should().Be(new int2(7, 2));
            Action strict = () => Parse<float2>("""{"x":"1.5"}""");
            strict.Should().Throw<JsonException>();
            Action tooBig = () => JsonSerializer.Deserialize<float2>("""{"x":"1e40"}""", With(JsonNumberHandling.AllowReadingFromString));
            tooBig.Should().Throw<JsonException>();
        }

        [Test]
        public void PropertyNames_FollowTheCaseSensitivityOption()
        {
            JsonSerializer.Deserialize<float2>("""{"X":1,"Y":2}""", With(ignoreCase: true)).Should().Be(new float2(1f, 2f));
            JsonSerializer.Deserialize<rect>("""{"MIN":{"X":1,"y":2},"Max":{"x":3,"Y":4}}""", With(ignoreCase: true))
                .Should().Be(new rect(1f, 2f, 3f, 4f));
            Parse<float2>("""{"X":1,"Y":2}""").Should().Be(float2.zero);
        }

        [Test]
        public void EveryShape_WorksAsADictionaryKey()
        {
            Json(new Dictionary<int2, string> { [new int2(1, -2)] = "a" }).Should().Be("""{"1,-2":"a"}""");
            Parse<Dictionary<int2, string>>("""{"1,-2":"a"}""")[new int2(1, -2)].Should().Be("a");

            KeyOf(new float2(1.5f, -2f)).Should().Be("1.5,-2");
            KeyOf(new float3(0.1f, 2f, 3f)).Should().Be("0.1,2,3");
            KeyOf(new bool2(true, false)).Should().Be("true,false");
            KeyOf(new Color32(255, 128, 0, 7)).Should().Be("255,128,0,7");
            KeyOf(new CultMath.Random(12345u)).Should().Be("12345");
            KeyOf(new float2x2(1f, 2f, 3f, 4f)).Should().Be("1,2,3,4");
            KeyOf(new rect(5f, 7f, -1f, -2f)).Should().Be("-1,-2,5,7");
            KeyOf(new float2(float.NaN, float.NegativeInfinity), Named).Should().Be("NaN,-Infinity");

            var nan = JsonSerializer.Deserialize<Dictionary<float2, int>>("""{"NaN,Infinity":1}""", With(Named))!;
            nan.Keys.Single().y.Should().Be(float.PositiveInfinity);

            Action refuseNonFinite = () => KeyOf(new float2(float.NaN, 0f));
            refuseNonFinite.Should().Throw<JsonException>();
            Action wrongArity = () => Parse<Dictionary<float3, int>>("""{"1,2":1}""");
            wrongArity.Should().Throw<JsonException>();
            Action overflow = () => Parse<Dictionary<float2, int>>("""{"1e40,0":1}""");
            overflow.Should().Throw<JsonException>();
        }

        [Test]
        public void EveryPublicCultMathValueType_RoundTripsAsADictionaryKey()
        {
            foreach (var type in typeof(float2).Assembly.GetExportedTypes().Where(t => t.IsValueType && !t.IsEnum))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type, typeof(int)))!;
                dictionary.Add(Activator.CreateInstance(type)!, 7);

                var json = JsonSerializer.Serialize(dictionary, dictionary.GetType(), Options);
                var back = (IDictionary)JsonSerializer.Deserialize(json, dictionary.GetType(), Options)!;

                back.Count.Should().Be(1, type.Name);
                back[Activator.CreateInstance(type)!].Should().Be(7, type.Name);
            }
        }

        private static string KeyOf<T>(T value, JsonNumberHandling handling = JsonNumberHandling.Strict) where T : notnull
        {
            var json = JsonSerializer.Serialize(new Dictionary<T, int> { [value] = 1 }, With(handling));
            return JsonDocument.Parse(json).RootElement.EnumerateObject().Single().Name;
        }

        private const JsonNumberHandling ReadStrings = JsonNumberHandling.AllowReadingFromString;
        private const JsonNumberHandling WriteStrings = JsonNumberHandling.WriteAsString;

        // Every row was measured against System.Text.Json's own float.
        [Test]
        public void NamedLiterals_FollowTheSameFlagsAsSystemTextJsonFloat()
        {
            foreach (var handling in new[] { Named, ReadStrings, ReadStrings | Named })
            {
                JsonSerializer.Deserialize<float2>("""{"x":"NaN","y":"-Infinity"}""", With(handling))!.y.Should().Be(float.NegativeInfinity, handling.ToString());
            }

            foreach (var handling in new[] { JsonNumberHandling.Strict, WriteStrings })
            {
                Action read = () => JsonSerializer.Deserialize<float2>("""{"x":"NaN"}""", With(handling));
                read.Should().Throw<JsonException>(handling.ToString());
            }

            foreach (var handling in new[] { Named, WriteStrings, WriteStrings | Named })
            {
                JsonSerializer.Serialize(new float2(float.NaN, float.PositiveInfinity), With(handling))
                    .Should().Be("""{"x":"NaN","y":"Infinity"}""", handling.ToString());
            }

            foreach (var handling in new[] { JsonNumberHandling.Strict, ReadStrings })
            {
                Action write = () => JsonSerializer.Serialize(new float2(float.NaN, 0f), With(handling));
                write.Should().Throw<JsonException>(handling.ToString());
            }

            JsonSerializer.Serialize(new float2(1f, float.NaN), With(WriteStrings)).Should().Be("""{"x":"1","y":"NaN"}""");
            JsonSerializer.Deserialize<double3>("""{"z":"Infinity"}""", With(ReadStrings))!.z.Should().Be(double.PositiveInfinity);
            JsonSerializer.Serialize(new double2(double.NegativeInfinity, 2d), With(WriteStrings)).Should().Be("""{"x":"-Infinity","y":"2"}""");
        }

        [Test]
        public void QuotedNumbers_RefuseWhitespace()
        {
            var options = With(ReadStrings);
            foreach (var json in new[] { """{"x":" 1.5"}""", """{"x":"1.5 "}""", """{"x":"\t1"}""" })
            {
                Action floatRead = () => JsonSerializer.Deserialize<float2>(json, options);
                floatRead.Should().Throw<JsonException>(json);
            }

            Action doubleRead = () => JsonSerializer.Deserialize<double2>("""{"y":" 2"}""", options);
            doubleRead.Should().Throw<JsonException>();
            Action intRead = () => JsonSerializer.Deserialize<int2>("""{"x":" 1"}""", options);
            intRead.Should().Throw<JsonException>();
            Action padded = () => JsonSerializer.Deserialize<float2>("""{"x":" NaN"}""", With(ReadStrings | Named));
            padded.Should().Throw<JsonException>();
        }

        [Test]
        public void UnknownProperties_AreSkippedWhateverTheyContain()
        {
            var expected = new float3(1f, 2f, 3f);
            foreach (var json in new[]
            {
                """{"u":{"a":[1,{"b":2}]},"x":1,"y":2,"z":3}""",
                """{"x":1,"u":[[1],[2,[3]]],"y":2,"v":{"a":{}},"z":3}""",
                """{"x":1,"y":2,"z":3,"u":{"a":{"b":[]}}}""",
                """{"x":1,"y":2,"z":3,"u":[{"a":[{}]}]}""",
            })
            {
                Parse<float3>(json).Should().Be(expected, json);
            }

            Parse<rect>("""{"min":{"q":[{}],"x":1,"y":2},"skip":{"a":[1]},"max":{"x":3,"y":4,"q":{"a":[]}}}""")
                .Should().Be(new rect(1f, 2f, 3f, 4f));
            Parse<float2x2>("""[{"x":1,"u":[1],"y":2},{"y":4,"u":{"a":1},"x":3}]""").Should().Be(new float2x2(1f, 2f, 3f, 4f));
        }

        [Test]
        public void UnmappedMemberHandling_Disallow_RefusesUnknownProperties()
        {
            var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }.AddCultMathConverters();
            Action flat = () => JsonSerializer.Deserialize<float2>("""{"x":1,"z":3}""", options);
            flat.Should().Throw<JsonException>();
            Action nested = () => JsonSerializer.Deserialize<rect>("""{"min":{"x":1,"q":1},"max":{}}""", options);
            nested.Should().Throw<JsonException>();
            JsonSerializer.Deserialize<float2>("""{"x":1,"y":2}""", options).Should().Be(new float2(1f, 2f));
        }

        [Test]
        public void DuplicateProperties_ReplaceTheWholeValue()
        {
            Parse<float2>("""{"x":1,"x":2,"y":5}""").Should().Be(new float2(2f, 5f));
            Parse<rect>("""{"min":{"x":1,"y":2},"min":{"x":3},"max":{"x":4,"y":5}}""").Should().Be(new rect(3f, 0f, 4f, 5f));
            Parse<Color32>("""{"a":9,"a":1}""").Should().Be(new Color32(0, 0, 0, 1));
            Parse<Color32>("""{"r":3,"r":4}""").Should().Be(new Color32(4, 0, 0, 255));
            Parse<CultCellular>("""{"nearest":{"x":1,"y":2},"nearest":{"z":3},"id":1}""")
                .Should().Be(new CultCellular(new float4(0f, 0f, 3f, 0f), float4.zero, 1f));
        }

        [Test]
        public void AllowDuplicatePropertiesFalse_RefusesRepeats()
        {
            var property = typeof(JsonSerializerOptions).GetProperty("AllowDuplicateProperties");
            Assert.That(property, Is.Not.Null, "the test runtime's System.Text.Json has AllowDuplicateProperties");
            var options = new JsonSerializerOptions().AddCultMathConverters();
            property!.SetValue(options, false);

            Action flat = () => JsonSerializer.Deserialize<float2>("""{"x":1,"x":2}""", options);
            flat.Should().Throw<JsonException>();
            Action nested = () => JsonSerializer.Deserialize<rect>("""{"min":{"x":1,"x":2}}""", options);
            nested.Should().Throw<JsonException>();
            JsonSerializer.Deserialize<float2>("""{"x":1,"y":2}""", options).Should().Be(new float2(1f, 2f));
        }

        [Test]
        public void DictionaryKeys_ReadOnlyTheCanonicalSpelling()
        {
            foreach (var key in new[] { "1.0,2", "+1,2", " 1,2", "1, 2", "01,2", "1,2 ", "1e0,2", "1,2,", ",1" })
            {
                Action read = () => Parse<Dictionary<float2, int>>($$"""{"{{key}}":1}""");
                read.Should().Throw<JsonException>(key);
            }

            foreach (var key in new[] { "01,2", "+1,2", "1,-0" })
            {
                Action read = () => Parse<Dictionary<int2, int>>($$"""{"{{key}}":1}""");
                read.Should().Throw<JsonException>(key);
            }

            Action bools = () => Parse<Dictionary<bool2, int>>("""{"True,false":1}""");
            bools.Should().Throw<JsonException>();
            Parse<Dictionary<float2, int>>("""{"1,2":1}""").Should().ContainKey(new float2(1f, 2f));
        }

        [Test]
        public void NegativeZero_IsADistinctKey()
        {
            var negative = new float2(-0f, 0f);
            KeyOf(negative).Should().Be("-0,0");
            KeyOf(float2.zero).Should().Be("0,0");
            KeyOf(new double2(0d, -0d)).Should().Be("0,-0");

            var parsed = Parse<Dictionary<float2, int>>("""{"-0,0":1}""").Keys.Single();
            float.IsNegative(parsed.x).Should().BeTrue();
            var positive = Parse<Dictionary<float2, int>>("""{"0,0":1}""").Keys.Single();
            float.IsNegative(positive.x).Should().BeFalse();
        }

        [Test]
        public void Culture_DoesNotChangeKeysOrValues()
        {
            var german = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            german.NumberFormat.NumberDecimalSeparator = ",";
            german.NumberFormat.NumberGroupSeparator = ".";
            german.NumberFormat.NegativeSign = "−";
            german.NumberFormat.PositiveSign = "➕";
            var swedish = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            swedish.NumberFormat.NegativeSign = "−";
            swedish.NumberFormat.NumberDecimalSeparator = ",";

            var original = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { german, swedish })
                {
                    CultureInfo.CurrentCulture = culture;
                    var options = With(ReadStrings | WriteStrings | Named);

                    KeyOf(new float2(-1.5f, 2.25f)).Should().Be("-1.5,2.25");
                    KeyOf(new double3(-1e-7, 1234.5, -0d)).Should().Be("-1E-07,1234.5,-0");
                    KeyOf(new int2(-3, 4)).Should().Be("-3,4");
                    KeyOf(new float2(float.NaN, float.NegativeInfinity), Named).Should().Be("NaN,-Infinity");
                    Parse<Dictionary<float2, int>>("""{"-1.5,2.25":1}""").Keys.Single().Should().Be(new float2(-1.5f, 2.25f));
                    Parse<Dictionary<int2, int>>("""{"-3,4":1}""").Keys.Single().Should().Be(new int2(-3, 4));

                    JsonSerializer.Serialize(new float2(-1.5f, 2.25f), options).Should().Be("""{"x":"-1.5","y":"2.25"}""");
                    JsonSerializer.Deserialize<float2>("""{"x":"-1.5","y":"2.25"}""", options).Should().Be(new float2(-1.5f, 2.25f));
                    JsonSerializer.Serialize(new int2(-3, 4), options).Should().Be("""{"x":"-3","y":"4"}""");
                    JsonSerializer.Deserialize<int2>("""{"x":"-3","y":"4"}""", options).Should().Be(new int2(-3, 4));
                    Json(new float2(-1.5f, 2.25f)).Should().Be("""{"x":-1.5,"y":2.25}""");
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        private static void RoundTrip<T>(T value) => Parse<T>(Json(value)).Should().Be(value);
    }
}
