using System;
using System.Linq;
using System.Threading.Tasks;
using CultMath;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Geometry.Tests;

public sealed class CultMathValueIntegrationTests
{
    private static readonly MessagePackSerializerOptions GeometryOptions =
        CultDocumentMessagePackSerialization.OptionsFor(typeof(CultGeometryDomainDocument));

    [TestCaseSource(nameof(ValueEncodingCases))]
    public void GeometryResolver_UsesExactPositionalVectorEncoding(object value, Type type, string expectedHex)
    {
        var payload = MessagePackSerializer.Serialize(type, value, GeometryOptions);

        Assert.That(Convert.ToHexString(payload), Is.EqualTo(expectedHex));
        Assert.That(MessagePackSerializer.Deserialize(type, payload, GeometryOptions), Is.EqualTo(value));
    }

    [Test]
    public void GeometryResolver_RejectsWrongVectorWidth()
    {
        var twoComponents = Convert.FromHexString("92CA3F800000CA40000000");

        Assert.That(
            () => MessagePackSerializer.Deserialize<float3>(twoComponents, GeometryOptions),
            Throws.TypeOf<MessagePackSerializationException>());
    }

    [Test]
    public async Task GeometrySoa_ExposesCultMathVectorsAsIntactColumns()
    {
        var cache = new CultCache();
        await cache.UpsertAsync(new VectorColumnDocument
        {
            Name = "probe",
            Uv = new float2(0.25f, 0.75f),
            Position = new float3(1f, 2f, 3f),
        });

        var table = cache.Soa<VectorColumnDocument>();

        Assert.That(table.Column<float2>(nameof(VectorColumnDocument.Uv)).Span.ToArray(),
            Is.EqualTo(new[] { new float2(0.25f, 0.75f) }));
        Assert.That(table.Column<float3>(nameof(VectorColumnDocument.Position)).Span.ToArray(),
            Is.EqualTo(new[] { new float3(1f, 2f, 3f) }));
        Assert.That(() => table.Column<float>("Position.x"), Throws.TypeOf<System.Collections.Generic.KeyNotFoundException>());
    }

    private static object[] ValueEncodingCases =>
    [
        new object[] { new float2(1f, -2.5f), typeof(float2), "92CA3F800000CAC0200000" },
        new object[] { new float3(1f, -2.5f, 0.5f), typeof(float3), "93CA3F800000CAC0200000CA3F000000" },
        new object[] { new float4(1f, -2.5f, 0.5f, 4f), typeof(float4), "94CA3F800000CAC0200000CA3F000000CA40800000" },
        new object[] { new quaternion(1f, -2.5f, 0.5f, 4f), typeof(quaternion), "94CA3F800000CAC0200000CA3F000000CA40800000" },
    ];

    [CultDocument("tests.geometry_vector_column", "tests.geometry_vector_column.v1")]
    private sealed class VectorColumnDocument
    {
        [Key(0), CultName]
        public string Name { get; set; } = string.Empty;

        [Key(1)]
        public float2 Uv { get; set; }

        [Key(2)]
        public float3 Position { get; set; }
    }
}
