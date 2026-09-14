using System.Text.Json;
using Xunit;

namespace CultMath.Tests;

public sealed class HlslSemanticsTests
{
    [Fact]
    public void ComponentsAndIndexersAreWritable()
    {
        var value = new float4(1.0f, 2.0f, 3.0f, 4.0f);
        value.w = 9.0f;
        value[0] = -1.0f;

        Assert.Equal(new float4(-1.0f, 2.0f, 3.0f, 9.0f), value);

        var cell = new int3(1, 2, 3);
        cell[2] = 7;
        Assert.Equal(new int3(1, 2, 7), cell);

        ref var component = ref value.y;
        component = 20.0f;
        Assert.Equal(20.0f, value.y);
    }

    [Fact]
    public void Float2SwizzlesReadAndWrite()
    {
        var value = new float2(1.0f, 2.0f);
        Assert.Equal(new float2(2.0f, 1.0f), value.yx);

        value.yx = new float2(5.0f, 6.0f);
        Assert.Equal(new float2(6.0f, 5.0f), value);
    }

    [Fact]
    public void Float3SwizzlesReadAndWrite()
    {
        var source = new float3(1.0f, 2.0f, 3.0f);
        Assert.Equal(new float2(1.0f, 2.0f), source.xy);
        Assert.Equal(new float2(1.0f, 3.0f), source.xz);
        Assert.Equal(new float2(2.0f, 1.0f), source.yx);
        Assert.Equal(new float2(2.0f, 3.0f), source.yz);
        Assert.Equal(new float2(3.0f, 2.0f), source.zy);
        Assert.Equal(new float3(1.0f, 2.0f, 3.0f), source.xyz);
        Assert.Equal(new float3(1.0f, 3.0f, 2.0f), source.xzy);
        Assert.Equal(new float3(2.0f, 3.0f, 1.0f), source.yzx);
        Assert.Equal(new float3(3.0f, 2.0f, 1.0f), source.zyx);

        Assert.Equal(new float3(8.0f, 9.0f, 3.0f), Write(source, (ref float3 v) => v.xy = new float2(8.0f, 9.0f)));
        Assert.Equal(new float3(8.0f, 2.0f, 9.0f), Write(source, (ref float3 v) => v.xz = new float2(8.0f, 9.0f)));
        Assert.Equal(new float3(9.0f, 8.0f, 3.0f), Write(source, (ref float3 v) => v.yx = new float2(8.0f, 9.0f)));
        Assert.Equal(new float3(1.0f, 8.0f, 9.0f), Write(source, (ref float3 v) => v.yz = new float2(8.0f, 9.0f)));
        Assert.Equal(new float3(1.0f, 9.0f, 8.0f), Write(source, (ref float3 v) => v.zy = new float2(8.0f, 9.0f)));
        Assert.Equal(new float3(7.0f, 8.0f, 9.0f), Write(source, (ref float3 v) => v.xyz = new float3(7.0f, 8.0f, 9.0f)));
        Assert.Equal(new float3(7.0f, 9.0f, 8.0f), Write(source, (ref float3 v) => v.xzy = new float3(7.0f, 8.0f, 9.0f)));
        Assert.Equal(new float3(9.0f, 7.0f, 8.0f), Write(source, (ref float3 v) => v.yzx = new float3(7.0f, 8.0f, 9.0f)));
        Assert.Equal(new float3(9.0f, 8.0f, 7.0f), Write(source, (ref float3 v) => v.zyx = new float3(7.0f, 8.0f, 9.0f)));
    }

    [Fact]
    public void Float4SwizzlesReadAndWrite()
    {
        var source = new float4(1.0f, 2.0f, 3.0f, 4.0f);
        Assert.Equal(new float2(1.0f, 2.0f), source.xy);
        Assert.Equal(new float3(1.0f, 2.0f, 3.0f), source.xyz);
        Assert.Equal(new float3(1.0f, 2.0f, 4.0f), source.xyw);
        Assert.Equal(new float3(2.0f, 3.0f, 1.0f), source.yzx);

        var a = source;
        a.xy = new float2(8.0f, 9.0f);
        Assert.Equal(new float4(8.0f, 9.0f, 3.0f, 4.0f), a);

        var b = source;
        b.xyz = new float3(7.0f, 8.0f, 9.0f);
        Assert.Equal(new float4(7.0f, 8.0f, 9.0f, 4.0f), b);

        var c = source;
        c.xyw = new float3(7.0f, 8.0f, 9.0f);
        Assert.Equal(new float4(7.0f, 8.0f, 3.0f, 9.0f), c);

        var d = source;
        d.yzx = new float3(7.0f, 8.0f, 9.0f);
        Assert.Equal(new float4(9.0f, 7.0f, 8.0f, 4.0f), d);
    }

    [Fact]
    public void GeneratedSwizzlesCoverRepeatsColorNamesAndAllVectorFamilies()
    {
        var v = new float4(1.0f, 2.0f, 3.0f, 4.0f);
        Assert.Equal(new float4(4.0f, 4.0f, 1.0f, 2.0f), v.wwxy);
        Assert.Equal(new float3(3.0f, 2.0f, 1.0f), v.bgr);
        Assert.Equal(4.0f, v.a);
        Assert.Equal(new float4(2.0f, 2.0f, 2.0f, 2.0f), new float2(1.0f, 2.0f).yyyy);

        v.wx = new float2(9.0f, 8.0f);
        Assert.Equal(new float4(8.0f, 2.0f, 3.0f, 9.0f), v);
        v.r = 0.5f;
        v.gb += new float2(1.0f, 1.0f);
        Assert.Equal(new float4(0.5f, 3.0f, 4.0f, 9.0f), v);

        Assert.Equal(new int3(3, 1, 1), new int3(1, 2, 3).zxx);
        Assert.Equal(new bool2(false, true), new bool4(true, false, false, true).zw);
        Assert.Equal(new double2(2.0, 1.0), new double3(1.0, 2.0, 3.0).yx);

        var cell = new int4(1, 2, 3, 4);
        cell.xyz = new int3(7, 8, 9);
        Assert.Equal(new int4(7, 8, 9, 4), cell);
    }

    [Fact]
    public void MixedConstructorsMatchHlsl()
    {
        var xy = new float2(1.0f, 2.0f);
        var zw = new float2(3.0f, 4.0f);
        var expected = new float4(1.0f, 2.0f, 3.0f, 4.0f);

        Assert.Equal(expected, math.float4(xy, zw));
        Assert.Equal(expected, math.float4(1.0f, new float3(2.0f, 3.0f, 4.0f)));
        Assert.Equal(expected, math.float4(new float3(1.0f, 2.0f, 3.0f), 4.0f));
        Assert.Equal(expected, math.float4(xy, 3.0f, 4.0f));
        Assert.Equal(expected, math.float4(1.0f, 2.0f, zw));
        Assert.Equal(expected, math.float4(1.0f, new float2(2.0f, 3.0f), 4.0f));
        Assert.Equal(expected, new float4(xy, zw));
        Assert.Equal(new float3(1.0f, 2.0f, 3.0f), math.float3(1.0f, zw - 1.0f));
        Assert.Equal(new int4(1, 2, 3, 4), math.int4(new int2(1, 2), new int2(3, 4)));
        Assert.Equal(new bool3(true, false, true), math.bool3(true, new bool2(false, true)));
        Assert.Equal(new double3(1.0, 2.0, 3.0), new double3(new double2(1.0, 2.0), 3.0));
    }

    [Fact]
    public void MatrixRowElementsAreWritable()
    {
        var m = float3x3.identity;
        m[1][2] = 5.0f;
        m[2].x = 7.0f;
        m._m00 = 2.0f;

        Assert.Equal(5.0f, m._m12);
        Assert.Equal(7.0f, m._m20);
        Assert.Equal(math.float3x3(2.0f, 0.0f, 0.0f, 0.0f, 1.0f, 5.0f, 7.0f, 0.0f, 1.0f), m);
        Assert.Equal(float3x3.identity, math.float3x3(1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f));

        var r = float2x2.identity;
        r[0][1] = 3.0f;
        Assert.Equal(math.float2x2(1.0f, 3.0f, 0.0f, 1.0f), r);
        Assert.Equal(new float2(0.0f, 1.0f), float2x2.identity[1]);
    }

    [Fact]
    public void IntrinsicEdgeCasesFollowHlsl()
    {
        Assert.True(math.any(new float3(0.0f, 0.0f, -2.0f)));
        Assert.False(math.any(float4.zero));
        Assert.True(math.all(new int2(1, -1)));
        Assert.False(math.all(new int3(1, 0, 1)));
        Assert.True(math.any(new float2(float.NaN, 0.0f)));

        Assert.Equal(new int3(1, 2, 3), math.min(new int3(1, 5, 3), new int3(4, 2, 6)));
        Assert.Equal(new int2(4, 5), math.max(new int2(1, 5), new int2(4, 2)));
        Assert.Equal(new int4(1, 2, 0, 3), math.abs(new int4(-1, 2, 0, -3)));
        Assert.Equal(7, math.abs(-7));

        Assert.Equal(0.0f, math.step(1.0f, float.NaN));
        Assert.Equal(0.0f, math.step(float.NaN, 1.0f));
        Assert.Equal(1.0f, math.step(1.0f, 1.0f));

        Assert.Equal(0, math.sign(float.NaN));
        Assert.Equal(-1, math.sign(-0.5f));
        Assert.Equal(new int3(-1, 0, 1), math.sign(new float3(-2.0f, 0.0f, 4.0f)));
        Assert.Equal(new int2(1, -1), math.sign(new int2(9, -9)));
    }

    [Fact]
    public void ComparisonOperatorsReturnComponentWiseBoolVectors()
    {
        var a = new float3(1.0f, 2.0f, 3.0f);
        var b = new float3(3.0f, 2.0f, 1.0f);

        Assert.Equal(new bool3(true, false, false), a < b);
        Assert.Equal(new bool3(false, false, true), a > b);
        Assert.Equal(new bool3(true, true, false), a <= b);
        Assert.Equal(new bool3(false, true, true), a >= b);
        Assert.Equal(new bool3(false, true, false), a == b);
        Assert.Equal(new bool3(true, false, true), a != b);

        Assert.Equal(new bool3(true, false, false), a < 2.0f);
        Assert.Equal(new bool3(false, false, true), 2.0f < a);
        Assert.Equal(new bool2(true, false), new float2(0.0f, 1.0f) == 0.0f);
        Assert.Equal(new bool4(false, true, true, true), new float4(0.0f, 1.0f, 2.0f, 3.0f) >= 1.0f);
        Assert.Equal(new bool2(false, true), new int2(1, 2) > new int2(1, 1));
        Assert.Equal(new bool3(true, false, true), new int3(1, 2, 3) != new int3(0, 2, 4));
        Assert.Equal(new bool4(true, true, false, false), new int4(1, 2, 3, 4) <= new int4(1, 2, 2, 2));
        Assert.Equal(new bool2(true, false), new double2(0.5, 1.0) < new double2(1.0, 1.0));
        Assert.Equal(new bool3(false, false, true), new double3(1.0, 1.0, 2.0) > new double3(1.0, 1.0, 1.0));
    }

    [Fact]
    public void ScalarOperatorsApplyPerComponentOnBothSides()
    {
        var value = new float3(1.0f, 2.0f, 4.0f);

        Assert.Equal(new float3(3.0f, 2.0f, 0.0f), 4.0f - value);
        Assert.Equal(new float3(0.0f, 1.0f, 3.0f), value - 1.0f);
        Assert.Equal(new float3(2.0f, 3.0f, 5.0f), 1.0f + value);
        Assert.Equal(new float3(4.0f, 2.0f, 1.0f), 4.0f / value);
        Assert.Equal(new float3(0.5f, 1.0f, 2.0f), value / 2.0f);
        Assert.Equal(new float3(2.0f, 4.0f, 8.0f), 2.0f * value);
        Assert.Equal(new float3(1.0f, 0.0f, 0.0f), value % 2.0f);
        Assert.Equal(new float2(1.5f, 2.5f), new float2(1.0f, 2.0f) + 0.5f);
        Assert.Equal(new float4(0.0f, 1.0f, 2.0f, 3.0f), new float4(1.0f, 2.0f, 3.0f, 4.0f) - 1.0f);
    }

    [Fact]
    public void EqualsAndHashCodeStayValueBasedForDictionaryKeys()
    {
        var map = new Dictionary<float3, string> { [new float3(1.0f, 2.0f, 3.0f)] = "hit" };
        Assert.Equal("hit", map[new float3(1.0f, 2.0f, 3.0f)]);
        Assert.False(map.ContainsKey(new float3(1.0f, 2.0f, 3.5f)));

        var cells = new HashSet<int2> { new int2(3, 4) };
        Assert.Contains(new int2(3, 4), cells);
        Assert.Contains(new int3(1, 2, 3), new HashSet<int3> { new int3(1, 2, 3) });

        var nan = new float2(float.NaN, 0.0f);
        var sameNan = nan;
        Assert.True(nan.Equals(sameNan));
        Assert.False((nan == sameNan).x);

        Assert.True(new quaternion(0.0f, 0.0f, 0.0f, 1.0f) == quaternion.identity);
        Assert.Equal(float2x2.identity, math.float2x2(1.0f, 0.0f, 0.0f, 1.0f));
        Assert.Equal("float3(1, 2.5, -3)", new float3(1.0f, 2.5f, -3.0f).ToString());
    }

    [Fact]
    public void AnyAllAndSelectFollowHlsl()
    {
        Assert.True(math.any(new bool2(false, true)));
        Assert.False(math.any(bool3.@false));
        Assert.True(math.all(bool4.@true));
        Assert.False(math.all(new bool3(true, false, true)));

        var mask = new bool3(true, false, true);
        Assert.Equal(new float3(1.0f, 20.0f, 3.0f), math.select(mask, new float3(1.0f, 2.0f, 3.0f), new float3(10.0f, 20.0f, 30.0f)));
        Assert.Equal(7.0f, math.select(true, 7.0f, 8.0f));
        Assert.Equal(new float2(0.0f, 5.0f), math.select(new bool2(false, true), math.float2(5.0f), math.float2(0.0f)));
        Assert.Equal(new float4(1.0f, 0.0f, 1.0f, 0.0f), math.select(new bool4(true, false, true, false), 1.0f, 0.0f));

        Assert.Equal(new bool2(false, true), !new bool2(true, false));
        Assert.Equal(new bool3(true, false, false), new bool3(true, true, false) & new bool3(true, false, true));
        Assert.Equal(new bool3(true, true, true), new bool3(true, true, false) | new bool3(true, false, true));
    }

    [Fact]
    public void AddedIntrinsicsAreComponentWise()
    {
        Assert.Equal(MathF.Log(3.0f), math.log(3.0f));
        Assert.Equal(new float3(0.0f, MathF.Log(2.0f), MathF.Log(4.0f)), math.log(new float3(1.0f, 2.0f, 4.0f)));
        Assert.Equal(new float2(8.0f, 9.0f), math.pow(new float2(2.0f, 3.0f), new float2(3.0f, 2.0f)));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), math.sqrt(new float4(1.0f, 4.0f, 9.0f, 16.0f)));
        var sines = math.sin(new float3(0.0f, math.HALF_PI, math.PI));
        Assert.Equal(0.0f, sines.x, precision: 5);
        Assert.Equal(1.0f, sines.y, precision: 5);
        Assert.Equal(0.0f, sines.z, precision: 5);
        var cosines = math.cos(new float2(0.0f, math.PI));
        Assert.Equal(1.0f, cosines.x, precision: 5);
        Assert.Equal(-1.0f, cosines.y, precision: 5);
        var arccosines = math.acos(new float3(1.0f, 0.0f, -1.0f));
        Assert.Equal(0.0f, arccosines.x, precision: 5);
        Assert.Equal(math.HALF_PI, arccosines.y, precision: 5);
        Assert.Equal(math.PI, arccosines.z, precision: 5);
        Assert.Equal(math.atan2(1.0f, -1.0f), math.atan2(new float2(1.0f, 0.0f), new float2(-1.0f, 1.0f)).x);
        Assert.Equal(0.0f, math.atan2(new float4(1.0f, 0.0f, 1.0f, 0.0f), new float4(1.0f, 1.0f, 1.0f, 1.0f)).y);
        Assert.Equal(2.5, math.min(2.5, 3.5));
        Assert.Equal(3.5, math.max(2.5, 3.5));
    }

    [Fact]
    public void IntVectorsPromoteToFloatVectors()
    {
        float3 promoted3 = new int3(1, 2, 3);
        float4 promoted4 = new int4(1, 2, 3, 4);

        Assert.Equal(new float3(1.0f, 2.0f, 3.0f), promoted3);
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), promoted4);
        Assert.Equal(new int3(2, 4, 6), new int3(1, 2, 3) * 2);
        Assert.Equal(new int4(1, 0, 1, 0), new int4(3, 4, 5, 6) % 2);
        Assert.Equal(new int3(1, 2, 3), math.int3(1, 2, 3));
        Assert.Equal(new int4(5, 5, 5, 5), math.int4(5));
    }

    [Fact]
    public void MatrixMulFollowsHlslRowMajorConvention()
    {
        var m = math.float2x2(1.0f, 2.0f, 3.0f, 4.0f);
        var v = new float2(5.0f, 6.0f);

        Assert.Equal(new float2(1.0f, 2.0f), m[0]);
        Assert.Equal(new float2(17.0f, 39.0f), math.mul(m, v));
        Assert.Equal(new float2(23.0f, 34.0f), math.mul(v, m));
        Assert.Equal(math.float2x2(7.0f, 10.0f, 15.0f, 22.0f), math.mul(m, m));

        var m3 = math.float3x3(new float3(1.0f, 2.0f, 3.0f), new float3(4.0f, 5.0f, 6.0f), new float3(7.0f, 8.0f, 9.0f));
        var v3 = new float3(1.0f, 0.0f, -1.0f);
        Assert.Equal(new float3(-2.0f, -2.0f, -2.0f), math.mul(m3, v3));
        Assert.Equal(new float3(-6.0f, -6.0f, -6.0f), math.mul(v3, m3));
        Assert.Equal(math.float3x3(30.0f, 36.0f, 42.0f, 66.0f, 81.0f, 96.0f, 102.0f, 126.0f, 150.0f), math.mul(m3, m3));
        Assert.Equal(m3, math.mul(m3, float3x3.identity));

        m3[1] = new float3(0.0f, 1.0f, 0.0f);
        Assert.Equal(1.0f, m3._m11);
        Assert.Equal(12.0f, math.mul(3.0f, 4.0f));
    }

    [Fact]
    public void Float2x2RotateMatchesRotateAndUnityMathematics()
    {
        var rotation = float2x2.Rotate(0.5f);
        var column = math.mul(rotation, new float2(1.0f, 0.0f));
        var row = math.mul(new float2(1.0f, 0.0f), rotation);
        var reference = math.rotate(new float2(1.0f, 0.0f), 0.5f);

        Assert.Equal(reference.x, column.x, precision: 6);
        Assert.Equal(reference.y, column.y, precision: 6);

        // Unity.Mathematics 1.x reference values: mul(float2(1, 0), float2x2.Rotate(0.5f)).
        Assert.Equal(0.87758255f, row.x, precision: 6);
        Assert.Equal(-0.47942555f, row.y, precision: 6);
    }

    [Fact]
    public void Float3x3EulerMatchesUnityMathematicsReference()
    {
        // Unity.Mathematics 1.x reference values captured from float3x3.Euler.
        AssertRows(
            float3x3.Euler(new float3(0.4f, -0.7f, 1.3f), math.RotationOrder.YXZ),
            0.44632244f, -0.8874959f, 0.114662126f,
            0.6698625f, 0.24638279f, -0.700414f,
            0.59336376f, 0.38941833f, 0.70446634f);
        AssertRows(
            float3x3.Euler(new float3(0.4f, -0.7f, 1.3f)),
            -0.037133574f, -0.8040775f, -0.59336376f,
            0.8874959f, 0.24638279f, -0.38941833f,
            0.45931715f, -0.5410684f, 0.70446634f);

        // Aetheria ActionGameManager view direction: mul(float3(0, 0, 1), Euler(float3(yawPitch.yx, 0), YXZ)).
        var yawPitch = new float2(0.3f, -1.1f);
        var view = math.mul(new float3(0.0f, 0.0f, 1.0f), float3x3.Euler(new float3(yawPitch.yx, 0.0f), math.RotationOrder.YXZ));
        Assert.Equal(-0.13404681f, view.x, precision: 5);
        Assert.Equal(-0.8912074f, view.y, precision: 5);
        Assert.Equal(0.4333369f, view.z, precision: 5);

        var composed = math.mul(float3x3.RotateZ(0.2f), math.mul(float3x3.RotateY(0.3f), float3x3.RotateX(0.1f)));
        AssertRows(float3x3.Euler(0.1f, 0.3f, 0.2f, math.RotationOrder.XYZ),
            composed._m00, composed._m01, composed._m02,
            composed._m10, composed._m11, composed._m12,
            composed._m20, composed._m21, composed._m22);
    }

    [Theory]
    [InlineData(0.0f, 0.0f, 0.0f, -0.41219875f)]
    [InlineData(0.25f, -0.5f, 1.75f, 0.5250269f)]
    [InlineData(12.25f, -4.5f, 3.125f, 0.008141451f)]
    [InlineData(-7.3f, 2.9f, 101.4f, 0.578015f)]
    public void Snoise3MatchesUnityMathematicsReference(float x, float y, float z, float expected)
    {
        Assert.Equal(expected, math.snoise(new float3(x, y, z)), precision: 5);
    }

    [Fact]
    public void RandomFloat3OverloadsShareOneDrawOrder()
    {
        var a = new Random(99);
        var b = new Random(99);
        var c = new Random(99);

        var unit = a.NextFloat3();
        Assert.Equal(new float3(b.NextFloat(), b.NextFloat(), b.NextFloat()), unit);
        Assert.Equal(unit * new float3(2.0f, 4.0f, 8.0f), c.NextFloat3(new float3(2.0f, 4.0f, 8.0f)));

        var d = new Random(99);
        Assert.Equal(math.float3(-1.0f) + unit * 2.0f, d.NextFloat3(math.float3(-1.0f), math.float3(1.0f)));
    }

    [Fact]
    public void CoreAssemblyStaysEngineFree()
    {
        var references = typeof(math).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("UnityEngine", StringComparison.Ordinal));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "packages", "cultmath", "src", "CultMath", "CultMath.asmdef")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var asmdefPath = Path.Combine(root!.FullName, "packages", "cultmath", "src", "CultMath", "CultMath.asmdef");
        using var asmdef = JsonDocument.Parse(File.ReadAllText(asmdefPath));
        Assert.True(asmdef.RootElement.GetProperty("noEngineReferences").GetBoolean());
    }

    private static float3 Write(float3 value, WriteAction action)
    {
        action(ref value);
        return value;
    }

    private delegate void WriteAction(ref float3 value);

    private static void AssertRows(float3x3 m, params float[] expected)
    {
        var actual = new[] { m._m00, m._m01, m._m02, m._m10, m._m11, m._m12, m._m20, m._m21, m._m22 };
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index], precision: 5);
        }
    }
}
