namespace CultMath;

public static partial class math
{
    // Rotation algebra on quaternion (design.md, "Where HLSL is silent"). Conventions: Hamilton product,
    // components stored xyzw, angles in radians, rotations right-handed about their axis, and
    // mul(a, b) applies b first. Rotation functions assume unit quaternions and unit axes; like the
    // rest of math these preconditions are documented, not checked.

    private static float3 vec(quaternion q) => new(q.x, q.y, q.z);

    /// <summary>
    /// The Hamilton product. <c>rotate(mul(a, b), v) == rotate(a, rotate(b, v))</c>: b is applied first.
    /// </summary>
    public static quaternion mul(quaternion a, quaternion b)
    {
        var va = vec(a);
        var vb = vec(b);
        var v = a.w * vb + b.w * va + cross(va, vb);
        return new quaternion(v.x, v.y, v.z, a.w * b.w - dot(va, vb));
    }

    /// <summary>Rotates <paramref name="v"/> by the unit quaternion <paramref name="q"/>.</summary>
    public static float3 rotate(quaternion q, float3 v)
    {
        var u = vec(q);
        var t = 2.0f * cross(u, v);
        return v + q.w * t + cross(u, t);
    }

    /// <summary>(-x, -y, -z, w). For a unit quaternion this is its inverse.</summary>
    public static quaternion conjugate(quaternion q) => new(-q.x, -q.y, -q.z, q.w);

    /// <summary>
    /// <c>conjugate(q) / dot(q, q)</c>. Not guarded: a zero quaternion yields non-finite components,
    /// as HLSL division does.
    /// </summary>
    public static quaternion inverse(quaternion q)
    {
        var n = 1.0f / (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return new quaternion(-q.x * n, -q.y * n, -q.z * n, q.w * n);
    }

    /// <summary>
    /// Spherical interpolation along the shortest arc (b is negated when <c>dot(a, b) &lt; 0</c>).
    /// Nearly parallel inputs (|dot| above 0.9995) use a normalised lerp, which is stable where
    /// sin(theta) vanishes. t is not clamped. a and b must be unit.
    /// </summary>
    public static quaternion slerp(quaternion a, quaternion b, float t)
    {
        float4 fa = a;
        float4 fb = b;
        var d = dot(fa, fb);
        if (d < 0.0f)
        {
            fb = -fb;
            d = -d;
        }

        if (d > 0.9995f)
        {
            return normalize(new quaternion(
                lerp(fa.x, fb.x, t), lerp(fa.y, fb.y, t), lerp(fa.z, fb.z, t), lerp(fa.w, fb.w, t)));
        }

        var theta = acos(d);
        var s = sin(theta);
        var r = fa * (sin((1.0f - t) * theta) / s) + fb * (sin(t * theta) / s);
        return new quaternion(r.x, r.y, r.z, r.w);
    }

    /// <summary>
    /// Splits unit <paramref name="q"/> into a twist about <paramref name="unitTwistAxis"/> (q's projection
    /// onto the axis, normalised) and the remaining swing, so that <c>q == mul(swing, twist)</c>: the twist
    /// is applied first and the swing's axis is perpendicular to the twist axis. When the projection
    /// vanishes (length below 1e-6: a half-turn swing) the twist is identity and swing is q.
    /// </summary>
    public static void swing_twist(quaternion q, float3 unitTwistAxis, out quaternion swing, out quaternion twist)
    {
        var p = unitTwistAxis * dot(vec(q), unitTwistAxis);
        var len = sqrt(dot(p, p) + q.w * q.w);
        if (len < 1e-6f)
        {
            twist = quaternion.identity;
        }
        else
        {
            var inv = 1.0f / len;
            twist = new quaternion(p.x * inv, p.y * inv, p.z * inv, q.w * inv);
        }

        swing = mul(q, conjugate(twist));
    }

    /// <summary>
    /// Limits a twist (a rotation about <paramref name="unitAxis"/>, as from swing_twist) to
    /// [<paramref name="min"/>, <paramref name="max"/>] radians, min &lt;= max. The angle is
    /// <c>2 atan2(dot(twist.xyz, axis), twist.w)</c> wrapped to (-pi, pi]. Inside the limit the input is
    /// returned unchanged; outside, <c>AxisAngle(axis, clamp(angle, min, max))</c>.
    /// </summary>
    public static quaternion clamp_twist(quaternion twist, float3 unitAxis, float min, float max)
    {
        var angle = 2.0f * atan2(dot(vec(twist), unitAxis), twist.w);
        const float pi = MathF.PI;
        if (angle > pi) angle -= 2.0f * pi;
        else if (angle <= -pi) angle += 2.0f * pi;

        if (angle >= min && angle <= max) return twist;
        return quaternion.AxisAngle(unitAxis, clamp(angle, min, max));
    }

    /// <summary>
    /// A hinge about <paramref name="unitAxis"/>: <c>clamp_twist</c> of q's twist about the axis, with
    /// the swing discarded. min &lt;= max.
    /// </summary>
    public static quaternion clamp_hinge(quaternion q, float3 unitAxis, float min, float max)
    {
        swing_twist(q, unitAxis, out _, out var twist);
        return clamp_twist(twist, unitAxis, min, max);
    }

    /// <summary>
    /// Limits a swing to a cone of half-angle <paramref name="maxAngle"/> about <paramref name="unitAxis"/>.
    /// The angle is that between <c>rotate(swing, axis)</c> and the axis. Inside the cone the input is
    /// returned unchanged; outside, the rotation by maxAngle about <c>normalize(cross(axis,
    /// rotate(swing, axis)))</c>, or about the swing's own axis at a half turn, where that cross
    /// product vanishes (length below 1e-6).
    /// </summary>
    public static quaternion clamp_cone(quaternion swing, float3 unitAxis, float maxAngle)
    {
        var d = rotate(swing, unitAxis);
        var angle = acos(clamp(dot(unitAxis, d), -1.0f, 1.0f));
        if (angle <= maxAngle) return swing;

        var c = cross(unitAxis, d);
        var axis = dot(c, c) < 1e-12f ? normalize(vec(swing)) : normalize(c);
        return quaternion.AxisAngle(axis, maxAngle);
    }
}
