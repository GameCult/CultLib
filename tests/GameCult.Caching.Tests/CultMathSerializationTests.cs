using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using GameCult.Caching.MessagePack;
using CultMath;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public sealed class CultMathSerializationTests
    {
        private static readonly MessagePackSerializerOptions Options = CultDocumentMessagePackSerialization.Options;

        private static string Hex<T>(T value) => Convert.ToHexString(MessagePackSerializer.Serialize(value, Options));

        private static T Decode<T>(string hex) => MessagePackSerializer.Deserialize<T>(Convert.FromHexString(hex), Options);

        [MessagePackObject]
        public sealed class Carrier
        {
            [Key(0)] public float2 Position;
            [Key(1)] public float3? Velocity;
            [Key(2)] public float2[] Path = Array.Empty<float2>();
            [Key(3)] public List<int2> Cells = new();
        }

        // Bytes the pre-collapse CultVec2/CultVec3/CultRect encodings and Aetheria's Float2/Int2/Bool2/Float3/Float4
        // formatters produce: an array of components in declaration order, float32 as 0xCA.
        [TestCase("92CA3F800000CA40000000")]
        public void Float2_MatchesTheLegacyVectorBytes(string hex)
        {
            Hex(new float2(1f, 2f)).Should().Be(hex);
            Decode<float2>(hex).Should().Be(new float2(1f, 2f));
        }

        [Test]
        public void Float3_MatchesTheLegacyVectorBytes()
        {
            const string hex = "93CA3F800000CA40000000CA40400000";
            Hex(new float3(1f, 2f, 3f)).Should().Be(hex);
            Decode<float3>(hex).Should().Be(new float3(1f, 2f, 3f));
        }

        [Test]
        public void Rect_MatchesTheLegacyCultRectBytes()
        {
            const string hex = "9292CABF800000CAC000000092CA40A00000CA40E00000";
            Hex(new rect(5f, 7f, -1f, -2f)).Should().Be(hex);
            Decode<rect>(hex).Should().Be(new rect(-1f, -2f, 5f, 7f));
        }

        [Test]
        public void ConsumerFormatterShapes_MatchByteForByte()
        {
            Hex(new float4(1f, 2f, 3f, 4f)).Should().Be("94CA3F800000CA40000000CA40400000CA40800000");
            Hex(new int2(3, -4)).Should().Be("9203FC");
            Hex(new bool2(true, false)).Should().Be("92C3C2");
        }

        [Test]
        public void Decode_SkipsExtraElements_AndLeavesMissingOnesZero()
        {
            Decode<float2>("93CA3F800000CA40000000CA40400000").Should().Be(new float2(1f, 2f));
            Decode<float3>("92CA3F800000CA40000000").Should().Be(new float3(1f, 2f, 0f));
            Decode<rect>("9392CABF800000CAC000000092CA40A00000CA40E0000001").Should().Be(new rect(-1f, -2f, 5f, 7f));
        }

        [Test]
        public void Decode_RefusesNilAndNonArrays()
        {
            Action nil = () => Decode<float2>("C0");
            nil.Should().Throw<MessagePackSerializationException>();
            Action notArray = () => Decode<float3>("01");
            notArray.Should().Throw<MessagePackSerializationException>();
        }

        [Test]
        public void EveryFormattedType_RoundTrips()
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
        public void Matrices_AreArraysOfRows()
        {
            Hex(new float2x2(1f, 2f, 3f, 4f)).Should().Be("9292CA3F800000CA400000009 2CA40400000CA40800000".Replace(" ", ""));
        }

        [Test]
        public void EveryPublicCultMathValueType_HasAFormatter()
        {
            var valueTypes = typeof(float2).Assembly.GetExportedTypes()
                .Where(t => t.IsValueType && !t.IsEnum)
                .ToHashSet();

            valueTypes.Except(CultMathResolver.FormattedTypes).Should().BeEmpty(
                "every public CultMath value type serializes through CultMathResolver");
            CultMathResolver.FormattedTypes.Except(valueTypes).Should().BeEmpty();
        }

        [Test]
        public void TheDefaultChain_ComposesMathIntoNullablesCollectionsAndDocuments()
        {
            var value = new Carrier
            {
                Position = new float2(1f, 2f),
                Velocity = new float3(3f, 4f, 5f),
                Path = new[] { new float2(0f, 1f), new float2(2f, 3f) },
                Cells = new List<int2> { new int2(1, 2) },
            };

            var decoded = MessagePackSerializer.Deserialize<Carrier>(MessagePackSerializer.Serialize(value, Options), Options);

            decoded.Position.Should().Be(value.Position);
            decoded.Velocity.Should().Be(value.Velocity);
            decoded.Path.Should().Equal(value.Path);
            decoded.Cells.Should().Equal(value.Cells);
            MessagePackSerializer.Serialize(new Carrier(), Options).Should().NotBeEmpty();

            var absent = MessagePackSerializer.Deserialize<Carrier>(MessagePackSerializer.Serialize(new Carrier(), Options), Options);
            absent.Velocity.Should().BeNull();
        }

        private static void RoundTrip<T>(T value)
        {
            var bytes = MessagePackSerializer.Serialize(value, Options);
            MessagePackSerializer.Deserialize<T>(bytes, Options).Should().Be(value);
        }
    }
}
