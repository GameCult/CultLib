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

    private const float Eps = 2e-5f;

    private static float Uniform(System.Random r, float lo, float hi) => lo + (hi - lo) * r.NextSingle();

    private static float3 RandomUnit3(System.Random r)
    {
        while (true)
        {
            var v = new float3(Uniform(r, -1, 1), Uniform(r, -1, 1), Uniform(r, -1, 1));
            var l = length(v);
            if (l > 0.1f && l <= 1.0f) return v / l;
        }
    }

    private static quaternion RandomRotation(System.Random r) =>
        quaternion.AxisAngle(RandomUnit3(r), Uniform(r, -MathF.PI, MathF.PI));

    private static void AssertVec(float3 expected, float3 actual, float eps = Eps)
    {
        Assert.True(length(expected - actual) <= eps, $"expected {expected}, got {actual}");
    }

    private static void AssertSameRotation(quaternion a, quaternion b, float eps = Eps)
    {
        var d = abs(dot((float4)a, (float4)b));
        Assert.True(d >= 1.0f - eps, $"|dot| = {d} for {a} vs {b}");
    }

    private static float AngleBetween(float3 a, float3 b) =>
        acos(clamp(dot(a, b) / (length(a) * length(b)), -1.0f, 1.0f));

    [Fact]
    public void CompositionAppliesTheRightOperandFirst()
    {
        var r = new System.Random(11);
        for (var i = 0; i < 200; i++)
        {
            var a = RandomRotation(r);
            var b = RandomRotation(r);
            var v = new float3(Uniform(r, -3, 3), Uniform(r, -3, 3), Uniform(r, -3, 3));
            AssertVec(rotate(a, rotate(b, v)), rotate(mul(a, b), v), 1e-4f);
        }
    }

    [Fact]
    public void MulByInverseIsIdentity()
    {
        var r = new System.Random(12);
        for (var i = 0; i < 100; i++)
        {
            var q = RandomRotation(r);
            AssertSameRotation(mul(q, inverse(q)), quaternion.identity);
            AssertSameRotation(mul(inverse(q), q), quaternion.identity);
        }
        // Not a unit quaternion: inverse divides by dot(q, q).
        var s = new quaternion(0.0f, 0.0f, 2.0f, 0.0f);
        var p = mul(s, inverse(s));
        Assert.True(abs(p.w - 1.0f) <= 1e-6f && abs(p.x) <= 1e-6f && abs(p.y) <= 1e-6f && abs(p.z) <= 1e-6f);
    }

    [Fact]
    public void InverseOfZeroIsNonFinite()
    {
        var q = inverse(new quaternion(0.0f, 0.0f, 0.0f, 0.0f));
        Assert.False(float.IsFinite(q.w));
    }

    [Fact]
    public void ConjugateNegatesTheVectorPartAndEqualsInverseForUnit()
    {
        var q = new quaternion(1.0f, -2.0f, 3.0f, 4.0f);
        Assert.Equal(new quaternion(-1.0f, 2.0f, -3.0f, 4.0f), conjugate(q));
        var r = new System.Random(13);
        for (var i = 0; i < 50; i++)
        {
            var u = RandomRotation(r);
            var c = conjugate(u);
            var n = inverse(u);
            Assert.True(abs(c.x - n.x) <= 1e-6f && abs(c.y - n.y) <= 1e-6f && abs(c.z - n.z) <= 1e-6f && abs(c.w - n.w) <= 1e-6f);
        }
    }

    [Fact]
    public void AxisAngleIsRightHanded()
    {
        var q = quaternion.AxisAngle(new float3(0, 0, 1), MathF.PI / 2);
        AssertVec(new float3(0, 1, 0), rotate(q, new float3(1, 0, 0)));
        AssertVec(new float3(-1, 0, 0), rotate(q, new float3(0, 1, 0)));
        AssertVec(new float3(0, 0, 1), rotate(q, new float3(0, 0, 1)));
        var h = quaternion.AxisAngle(new float3(0, 0, 1), 0.8f);
        Assert.Equal(sin(0.4f) , h.z);
        Assert.Equal(cos(0.4f), h.w);
    }

    [Fact]
    public void SlerpEndpointsAndMidpoint()
    {
        var r = new System.Random(14);
        for (var i = 0; i < 50; i++)
        {
            var a = RandomRotation(r);
            var b = RandomRotation(r);
            AssertSameRotation(slerp(a, b, 0.0f), a);
            AssertSameRotation(slerp(a, b, 1.0f), b);
        }
        var mid = slerp(quaternion.identity, quaternion.AxisAngle(new float3(0, 0, 1), 1.0f), 0.5f);
        AssertSameRotation(mid, quaternion.AxisAngle(new float3(0, 0, 1), 0.5f));
    }

    [Fact]
    public void SlerpTakesTheShortestArc()
    {
        var z = new float3(0, 0, 1);
        var a = quaternion.identity;
        var b = quaternion.AxisAngle(z, 1.0f);
        // -b is the same rotation with dot(a, -b) < 0; the long way would pass through 1 - pi.
        var nb = new quaternion(-b.x, -b.y, -b.z, -b.w);
        Assert.True(dot((float4)a, (float4)nb) < 0.0f);
        AssertSameRotation(slerp(a, nb, 0.5f), quaternion.AxisAngle(z, 0.5f));
        AssertSameRotation(slerp(a, nb, 0.25f), quaternion.AxisAngle(z, 0.25f));
    }

    [Fact]
    public void SlerpOfNearlyParallelQuaternionsIsFiniteAndUnit()
    {
        var a = quaternion.AxisAngle(new float3(0, 1, 0), 0.3f);
        foreach (var b in new[] { a, quaternion.AxisAngle(new float3(0, 1, 0), 0.3f + 1e-6f), normalize(new quaternion(a.x + 1e-7f, a.y, a.z, a.w)) })
        {
            var s = slerp(a, b, 0.37f);
            var l = length((float4)s);
            Assert.True(float.IsFinite(l) && abs(l - 1.0f) <= 1e-5f, $"{s}");
            AssertSameRotation(s, a, 1e-5f);
        }
    }

    [Fact]
    public void SwingTwistRecomposesAndSplitsAboutTheAxis()
    {
        var r = new System.Random(15);
        for (var i = 0; i < 200; i++)
        {
            var q = RandomRotation(r);
            var axis = RandomUnit3(r);
            swing_twist(q, axis, out var swing, out var twist);
            AssertSameRotation(mul(swing, twist), q, 1e-4f);
            Assert.True(length(cross(new float3(twist.x, twist.y, twist.z), axis)) <= 1e-5f, "twist axis parallel");
            Assert.True(abs(dot(new float3(swing.x, swing.y, swing.z), axis)) <= 1e-5f, "swing axis perpendicular");
        }
    }

    [Fact]
    public void SwingTwistOfAHalfTurnSwingHasIdentityTwist()
    {
        var q = quaternion.AxisAngle(new float3(1, 0, 0), MathF.PI);
        swing_twist(q, new float3(0, 0, 1), out var swing, out var twist);
        AssertSameRotation(twist, quaternion.identity);
        AssertSameRotation(swing, q);
    }

    [Fact]
    public void ClampTwistReturnsTheInputBitIdenticalInsideTheLimit()
    {
        var z = new float3(0, 0, 1);
        var r = new System.Random(16);
        for (var i = 0; i < 50; i++)
        {
            var twist = quaternion.AxisAngle(z, Uniform(r, -0.9f, 0.9f));
            Assert.True(twist == clamp_twist(twist, z, -1.0f, 1.0f));
        }
    }

    [Fact]
    public void ClampTwistReturnsTheBoundaryAngleOutside()
    {
        var z = new float3(0, 0, 1);
        AssertSameRotation(clamp_twist(quaternion.AxisAngle(z, 2.0f), z, -1.0f, 0.5f), quaternion.AxisAngle(z, 0.5f));
        AssertSameRotation(clamp_twist(quaternion.AxisAngle(z, -2.0f), z, -1.0f, 0.5f), quaternion.AxisAngle(z, -1.0f));
        // Wrapped: 3 pi / 2 about z is -pi / 2, below the lower limit.
        AssertSameRotation(clamp_twist(quaternion.AxisAngle(z, 3.0f * MathF.PI / 2.0f), z, -1.0f, 1.0f), quaternion.AxisAngle(z, -1.0f));
    }

    [Fact]
    public void ClampHingeDiscardsTheSwing()
    {
        var z = new float3(0, 0, 1);
        var r = new System.Random(17);
        for (var i = 0; i < 50; i++)
        {
            var q = RandomRotation(r);
            var h = clamp_hinge(q, z, -0.7f, 0.7f);
            // A hinge result is a pure rotation about the axis within the limits.
            Assert.True(abs(h.x) <= 1e-6f && abs(h.y) <= 1e-6f);
            var angle = 2.0f * atan2(h.z, h.w);
            Assert.True(angle >= -0.7f - 1e-5f && angle <= 0.7f + 1e-5f);
        }
        // Inside the limit about the axis, the twist survives and the swing does not.
        var swing = quaternion.AxisAngle(new float3(1, 0, 0), 0.6f);
        var twist = quaternion.AxisAngle(z, 0.4f);
        AssertSameRotation(clamp_hinge(mul(swing, twist), z, -1.0f, 1.0f), twist, 1e-4f);
    }

    [Fact]
    public void ClampConeReturnsTheInputBitIdenticalInsideTheCone()
    {
        var z = new float3(0, 0, 1);
        var r = new System.Random(18);
        for (var i = 0; i < 50; i++)
        {
            var swing = quaternion.AxisAngle(RandomUnit3(r), Uniform(r, 0.0f, 0.5f));
            var inside = AngleBetween(rotate(swing, z), z);
            Assert.True(inside < 0.5f);
            Assert.True(swing == clamp_cone(swing, z, 0.6f));
        }
    }

    [Fact]
    public void ClampConeReturnsTheBoundaryAngleOutside()
    {
        var z = new float3(0, 0, 1);
        var r = new System.Random(19);
        for (var i = 0; i < 50; i++)
        {
            var swing = quaternion.AxisAngle(RandomUnit3(r), Uniform(r, 1.0f, 3.0f));
            if (AngleBetween(rotate(swing, z), z) <= 0.4f) continue;
            var c = clamp_cone(swing, z, 0.4f);
            Assert.True(abs(AngleBetween(rotate(c, z), z) - 0.4f) <= 1e-4f);
            // Clamped toward the original direction: same plane, so the clamped and original axes agree.
            Assert.True(dot(cross(z, rotate(c, z)), cross(z, rotate(swing, z))) > 0.0f);
        }
        // Half turn: the cross product vanishes; the clamp still lands on the boundary.
        var half = quaternion.AxisAngle(new float3(1, 0, 0), MathF.PI);
        var ch = clamp_cone(half, z, 0.4f);
        Assert.True(abs(AngleBetween(rotate(ch, z), z) - 0.4f) <= 1e-4f);
    }
}
