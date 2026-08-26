namespace CultMath;

public record struct quaternion(float x, float y, float z, float w)
{
    public static readonly quaternion identity = new(0.0f, 0.0f, 0.0f, 1.0f);

    public static quaternion LookRotation(float3 forward, float3 up)
    {
        forward = math.normalize(forward);
        up = math.normalize(up);

        var right = math.normalize(math.cross(up, forward));
        up = math.cross(forward, right);

        var m00 = right.x;
        var m01 = up.x;
        var m02 = forward.x;
        var m10 = right.y;
        var m11 = up.y;
        var m12 = forward.y;
        var m20 = right.z;
        var m21 = up.z;
        var m22 = forward.z;

        var trace = m00 + m11 + m22;
        if (trace > 0.0f)
        {
            var s = math.sqrt(trace + 1.0f) * 2.0f;
            return math.normalize(new quaternion(
                (m21 - m12) / s,
                (m02 - m20) / s,
                (m10 - m01) / s,
                0.25f * s));
        }

        if (m00 > m11 && m00 > m22)
        {
            var s = math.sqrt(1.0f + m00 - m11 - m22) * 2.0f;
            return math.normalize(new quaternion(
                0.25f * s,
                (m01 + m10) / s,
                (m02 + m20) / s,
                (m21 - m12) / s));
        }

        if (m11 > m22)
        {
            var s = math.sqrt(1.0f + m11 - m00 - m22) * 2.0f;
            return math.normalize(new quaternion(
                (m01 + m10) / s,
                0.25f * s,
                (m12 + m21) / s,
                (m02 - m20) / s));
        }

        {
            var s = math.sqrt(1.0f + m22 - m00 - m11) * 2.0f;
            return math.normalize(new quaternion(
                (m02 + m20) / s,
                (m12 + m21) / s,
                0.25f * s,
                (m10 - m01) / s));
        }
    }

    public static implicit operator quaternion(float4 value) => new(value.x, value.y, value.z, value.w);
    public static implicit operator float4(quaternion value) => new(value.x, value.y, value.z, value.w);
}
