using System;
using System.Linq;
using System.Text.Json;
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

        private static void RoundTrip<T>(T value) => Parse<T>(Json(value)).Should().Be(value);
    }
}
