using System;
using System.Collections;
using System.Collections.Generic;
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

        private static void RoundTrip<T>(T value) => Parse<T>(Json(value)).Should().Be(value);
    }
}
