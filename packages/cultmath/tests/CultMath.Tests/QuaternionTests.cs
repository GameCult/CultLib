using CultMath;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The rotation algebra of math.Quaternion.cs. Conventions: Hamilton, xyzw, radians, right-handed,
/// mul(a, b) applies b first. Rotations are compared by what they do to vectors or by |dot| near 1,
/// never by component equality (q and -q are one rotation), except where a test says bit-identical.
/// </summary>
public sealed class QuaternionTests
{
    [Fact]
    public void MulOfTwoFloat4IsTheInnerProduct()
    {
        // quaternion converts implicitly from float4; without the vector overloads this call would bind
        // to the quaternion product and return a float4.
        var a = new float4(1.0f, 2.0f, 3.0f, 4.0f);
        var b = new float4(5.0f, 6.0f, 7.0f, 8.0f);
        float r = mul(a, b);
        Assert.Equal(70.0f, r);
        Assert.Equal(dot(a, b), r);
        Assert.Equal(11.0f, mul(new float2(1.0f, 2.0f), new float2(3.0f, 4.0f)));
        Assert.Equal(32.0f, mul(new float3(1.0f, 2.0f, 3.0f), new float3(4.0f, 5.0f, 6.0f)));
    }
}
