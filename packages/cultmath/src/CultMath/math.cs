using System;

namespace CultMath;

public static partial class math
{
    public const float PI = MathF.PI;
    public const float TAU = MathF.PI * 2.0f;
    public const float HALF_PI = MathF.PI * 0.5f;

    /// <summary>
    /// Extrinsic Euler order for <see cref="CultMath.float3x3.Euler(CultMath.float3, RotationOrder)"/>:
    /// the first letter is the axis applied first. Nested here so <c>using static CultMath.math;</c>
    /// brings it into scope with the rest of the shader vocabulary.
    /// </summary>
    public enum RotationOrder : byte
    {
        XYZ,
        XZY,
        YXZ,
        YZX,
        ZXY,
        ZYX,
        Default = ZXY,
    }

    public static float2 float2(float x, float y) => new(x, y);
    public static float2 float2(float value) => new(value, value);
    public static bool2 bool2(bool x, bool y) => new(x, y);
    public static bool2 bool2(bool value) => new(value, value);
    public static int2 int2(int x, int y) => new(x, y);
    public static int2 int2(int value) => new(value, value);
    // Mixed constructor functions (float4(float2, float2), ...) are generated in Swizzles.g.cs.
    public static float3 float3(float x, float y, float z) => new(x, y, z);
    public static float3 float3(float value) => new(value, value, value);
    public static float4 float4(float x, float y, float z, float w) => new(x, y, z, w);
    public static float4 float4(float value) => new(value, value, value, value);
    public static bool3 bool3(bool x, bool y, bool z) => new(x, y, z);
    public static bool3 bool3(bool value) => new(value, value, value);
    public static bool4 bool4(bool x, bool y, bool z, bool w) => new(x, y, z, w);
    public static bool4 bool4(bool value) => new(value, value, value, value);
    public static int3 int3(int x, int y, int z) => new(x, y, z);
    public static int3 int3(int value) => new(value, value, value);
    public static int4 int4(int x, int y, int z, int w) => new(x, y, z, w);
    public static int4 int4(int value) => new(value, value, value, value);

    // Matrix constructor functions take rows, as HLSL does. Under `using static CultMath.math;`
    // these method groups hide the type names in member access, so static members need a
    // qualified type: `CultMath.float3x3.Euler(...)`, `CultMath.float2x2.Rotate(...)`.
    public static float2x2 float2x2(float m00, float m01, float m10, float m11) => new(m00, m01, m10, m11);
    public static float2x2 float2x2(float2 row0, float2 row1) => new(row0, row1);
    public static float3x3 float3x3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22) =>
        new(m00, m01, m02, m10, m11, m12, m20, m21, m22);
    public static float3x3 float3x3(float3 row0, float3 row1, float3 row2) => new(row0, row1, row2);
    public static float radians(float degrees) => degrees * (PI / 180.0f);
    public static float degrees(float radians) => radians * (180.0f / PI);
    public static float sin(float value) => MathF.Sin(value);
    public static float cos(float value) => MathF.Cos(value);
    public static float tan(float value) => MathF.Tan(value);
    public static float asin(float value) => MathF.Asin(value);
    public static float acos(float value) => MathF.Acos(value);
    public static float atan(float value) => MathF.Atan(value);
    public static float atan2(float y, float x) => MathF.Atan2(y, x);
    public static float sqrt(float value) => MathF.Sqrt(value);
    public static float pow(float value, float power) => MathF.Pow(value, power);
    public static bool isinf(float value) => float.IsInfinity(value);
    public static float exp(float value) => MathF.Exp(value);
    public static double exp(double value) => Math.Exp(value);
    public static float2 exp(float2 value) => new(exp(value.x), exp(value.y));
    public static float3 exp(float3 value) => new(exp(value.x), exp(value.y), exp(value.z));
    public static float4 exp(float4 value) => new(exp(value.x), exp(value.y), exp(value.z), exp(value.w));
    public static double2 exp(double2 value) => new(exp(value.x), exp(value.y));
    public static double3 exp(double3 value) => new(exp(value.x), exp(value.y), exp(value.z));

    public static float abs(float value) => MathF.Abs(value);
    public static float2 abs(float2 value) => new(abs(value.x), abs(value.y));
    public static float3 abs(float3 value) => new(abs(value.x), abs(value.y), abs(value.z));
    public static float4 abs(float4 value) => new(abs(value.x), abs(value.y), abs(value.z), abs(value.w));
    public static int abs(int value) => Math.Abs(value);
    public static int2 abs(int2 value) => new(abs(value.x), abs(value.y));
    public static int3 abs(int3 value) => new(abs(value.x), abs(value.y), abs(value.z));
    public static int4 abs(int4 value) => new(abs(value.x), abs(value.y), abs(value.z), abs(value.w));

    // HLSL sign returns int. NaN compares false both ways, so it yields 0 instead of throwing.
    public static int sign(float value) => value > 0.0f ? 1 : value < 0.0f ? -1 : 0;
    public static int2 sign(float2 value) => new(sign(value.x), sign(value.y));
    public static int3 sign(float3 value) => new(sign(value.x), sign(value.y), sign(value.z));
    public static int4 sign(float4 value) => new(sign(value.x), sign(value.y), sign(value.z), sign(value.w));
    public static int sign(int value) => value > 0 ? 1 : value < 0 ? -1 : 0;
    public static int2 sign(int2 value) => new(sign(value.x), sign(value.y));
    public static int3 sign(int3 value) => new(sign(value.x), sign(value.y), sign(value.z));
    public static int4 sign(int4 value) => new(sign(value.x), sign(value.y), sign(value.z), sign(value.w));

    public static float floor(float value) => MathF.Floor(value);
    public static float2 floor(float2 value) => new(floor(value.x), floor(value.y));
    public static float3 floor(float3 value) => new(floor(value.x), floor(value.y), floor(value.z));
    public static float4 floor(float4 value) => new(floor(value.x), floor(value.y), floor(value.z), floor(value.w));

    public static float ceil(float value) => MathF.Ceiling(value);
    public static float2 ceil(float2 value) => new(ceil(value.x), ceil(value.y));
    public static float3 ceil(float3 value) => new(ceil(value.x), ceil(value.y), ceil(value.z));
    public static float4 ceil(float4 value) => new(ceil(value.x), ceil(value.y), ceil(value.z), ceil(value.w));

    public static float frac(float value) => value - floor(value);
    public static float2 frac(float2 value) => value - floor(value);
    public static float3 frac(float3 value) => value - floor(value);
    public static float4 frac(float4 value) => value - floor(value);
    // dxc has no double Frc: it demotes to float, as it does for exp and lerp. CultMath's double
    // overloads keep double precision instead, so frac(double) is x - floor(x) in double.
    public static double frac(double value) => value - Math.Floor(value);

    // dxc lowers float min/max/clamp to DXIL FMin/FMax (saturate to Saturate, defined as FMin(1, FMax(0, x))).
    // DXIL.rst: FMin(a, b) is a < b ? a : b, FMax(a, b) is a >= b ? a : b, and a NaN operand returns the other.
    public static float min(float left, float right) => left < right || float.IsNaN(right) ? left : right;
    public static int min(int left, int right) => Math.Min(left, right);
    public static float2 min(float2 left, float2 right) => new(min(left.x, right.x), min(left.y, right.y));
    public static float3 min(float3 left, float3 right) => new(min(left.x, right.x), min(left.y, right.y), min(left.z, right.z));
    public static float4 min(float4 left, float4 right) => new(min(left.x, right.x), min(left.y, right.y), min(left.z, right.z), min(left.w, right.w));
    public static int2 min(int2 left, int2 right) => new(min(left.x, right.x), min(left.y, right.y));
    public static int3 min(int3 left, int3 right) => new(min(left.x, right.x), min(left.y, right.y), min(left.z, right.z));
    public static int4 min(int4 left, int4 right) => new(min(left.x, right.x), min(left.y, right.y), min(left.z, right.z), min(left.w, right.w));

    public static float max(float left, float right) => left >= right || float.IsNaN(right) ? left : right;
    public static int max(int left, int right) => Math.Max(left, right);
    public static float2 max(float2 left, float2 right) => new(max(left.x, right.x), max(left.y, right.y));
    public static float3 max(float3 left, float3 right) => new(max(left.x, right.x), max(left.y, right.y), max(left.z, right.z));
    public static float4 max(float4 left, float4 right) => new(max(left.x, right.x), max(left.y, right.y), max(left.z, right.z), max(left.w, right.w));
    public static int2 max(int2 left, int2 right) => new(max(left.x, right.x), max(left.y, right.y));
    public static int3 max(int3 left, int3 right) => new(max(left.x, right.x), max(left.y, right.y), max(left.z, right.z));
    public static int4 max(int4 left, int4 right) => new(max(left.x, right.x), max(left.y, right.y), max(left.z, right.z), max(left.w, right.w));

    public static float clamp(float value, float minimum, float maximum) => min(max(value, minimum), maximum);
    public static float2 clamp(float2 value, float2 minimum, float2 maximum) => min(max(value, minimum), maximum);
    public static float3 clamp(float3 value, float3 minimum, float3 maximum) => min(max(value, minimum), maximum);
    public static float4 clamp(float4 value, float4 minimum, float4 maximum) => min(max(value, minimum), maximum);
    // dxc lowers int clamp to IMin(IMax(x, a), b): inverted bounds return b rather than throwing like Math.Clamp.
    public static int clamp(int value, int minimum, int maximum) => min(max(value, minimum), maximum);
    public static int2 clamp(int2 value, int2 minimum, int2 maximum) => min(max(value, minimum), maximum);
    public static int3 clamp(int3 value, int3 minimum, int3 maximum) => min(max(value, minimum), maximum);
    public static int4 clamp(int4 value, int4 minimum, int4 maximum) => min(max(value, minimum), maximum);

    // DXIL Saturate is FMin(1, FMax(0, x)); that operand order, unlike clamp(x, 0, 1), maps -0 to +0.
    public static float saturate(float value) => min(1.0f, max(0.0f, value));
    public static float2 saturate(float2 value) => min(1.0f, max(0.0f, value));
    public static float3 saturate(float3 value) => min(1.0f, max(0.0f, value));
    public static float4 saturate(float4 value) => min(1.0f, max(0.0f, value));

    public static float lerp(float start, float end, float amount) => start + (end - start) * amount;
    public static double lerp(double start, double end, double amount) => start + (end - start) * amount;
    public static float2 lerp(float2 start, float2 end, float2 amount) => start + (end - start) * amount;
    public static float3 lerp(float3 start, float3 end, float3 amount) => start + (end - start) * amount;
    public static float4 lerp(float4 start, float4 end, float4 amount) => start + (end - start) * amount;
    public static double2 lerp(double2 start, double2 end, double2 amount) => start + (end - start) * amount;
    public static double3 lerp(double3 start, double3 end, double3 amount) => start + (end - start) * amount;

    public static float unlerp(float start, float end, float value) => (value - start) / (end - start);
    public static double unlerp(double start, double end, double value) => (value - start) / (end - start);
    public static float2 unlerp(float2 start, float2 end, float2 value) => (value - start) / (end - start);
    public static float3 unlerp(float3 start, float3 end, float3 value) => (value - start) / (end - start);
    public static float4 unlerp(float4 start, float4 end, float4 value) => (value - start) / (end - start);
    public static double2 unlerp(double2 start, double2 end, double2 value) => (value - start) / (end - start);
    public static double3 unlerp(double3 start, double3 end, double3 value) => (value - start) / (end - start);

    // dxc lowers step(y, x) to x < y ? 0 : 1 (fcmp olt, then select), so a NaN on either side yields 1.
    public static float step(float edge, float value) => value < edge ? 0.0f : 1.0f;
    public static float2 step(float2 edge, float2 value) => new(step(edge.x, value.x), step(edge.y, value.y));
    public static float3 step(float3 edge, float3 value) => new(step(edge.x, value.x), step(edge.y, value.y), step(edge.z, value.z));
    public static float4 step(float4 edge, float4 value) => new(step(edge.x, value.x), step(edge.y, value.y), step(edge.z, value.z), step(edge.w, value.w));

    public static float smoothstep(float minimum, float maximum, float value)
    {
        var t = saturate((value - minimum) / (maximum - minimum));
        return t * t * (3.0f - 2.0f * t);
    }

    public static float2 smoothstep(float2 minimum, float2 maximum, float2 value)
    {
        var t = saturate((value - minimum) / (maximum - minimum));
        return t * t * (3.0f - 2.0f * t);
    }

    public static float3 smoothstep(float3 minimum, float3 maximum, float3 value)
    {
        var t = saturate((value - minimum) / (maximum - minimum));
        return t * t * (3.0f - 2.0f * t);
    }

    public static float4 smoothstep(float4 minimum, float4 maximum, float4 value)
    {
        var t = saturate((value - minimum) / (maximum - minimum));
        return t * t * (3.0f - 2.0f * t);
    }

    public static float dot(float2 left, float2 right) => left.x * right.x + left.y * right.y;
    public static float dot(float3 left, float3 right) => left.x * right.x + left.y * right.y + left.z * right.z;
    public static float dot(float4 left, float4 right) => left.x * right.x + left.y * right.y + left.z * right.z + left.w * right.w;

    public static float3 cross(float3 left, float3 right) =>
        new(
            left.y * right.z - left.z * right.y,
            left.z * right.x - left.x * right.z,
            left.x * right.y - left.y * right.x);

    public static float lengthsq(float2 value) => dot(value, value);
    public static float lengthsq(float3 value) => dot(value, value);
    public static float lengthsq(float4 value) => dot(value, value);

    public static float length(float2 value) => MathF.Sqrt(lengthsq(value));
    public static float length(float3 value) => MathF.Sqrt(lengthsq(value));
    public static float length(float4 value) => MathF.Sqrt(lengthsq(value));

    public static float distance(float2 left, float2 right) => length(left - right);
    public static float distance(float3 left, float3 right) => length(left - right);
    public static float distance(float4 left, float4 right) => length(left - right);

    // dxc lowers normalize(x) to x * Rsqrt(Dot(x, x)), and DXIL Rsqrt is 1 / sqrt(src): a zero vector is 0 * inf = NaN.
    public static float2 normalize(float2 value) => value * (1.0f / MathF.Sqrt(dot(value, value)));
    public static float3 normalize(float3 value) => value * (1.0f / MathF.Sqrt(dot(value, value)));
    public static float4 normalize(float4 value) => value * (1.0f / MathF.Sqrt(dot(value, value)));
    public static quaternion normalize(quaternion value)
    {
        var length = MathF.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
        if (length <= 1.0e-20f)
        {
            return quaternion.identity;
        }

        var invLength = 1.0f / length;
        return new quaternion(value.x * invLength, value.y * invLength, value.z * invLength, value.w * invLength);
    }

    public static float2 reflect(float2 incident, float2 normal) => incident - 2.0f * dot(normal, incident) * normal;
    public static float3 reflect(float3 incident, float3 normal) => incident - 2.0f * dot(normal, incident) * normal;

    public static float2 rotate(float2 value, float radians)
    {
        var s = sin(radians);
        var c = cos(radians);
        return new float2(value.x * c - value.y * s, value.x * s + value.y * c);
    }

    public static float2 rotate_degrees(float2 value, float degrees) => rotate(value, radians(degrees));

    public static float csum(float2 value) => value.x + value.y;
    public static float csum(float3 value) => value.x + value.y + value.z;
    public static float csum(float4 value) => value.x + value.y + value.z + value.w;

    public static float decay(float source, float lambda, float dt) => source * exp(-lambda * dt);
    public static float2 decay(float2 source, float lambda, float dt) => source * exp(-lambda * dt);
    public static float3 decay(float3 source, float lambda, float dt) => source * exp(-lambda * dt);
    public static float4 decay(float4 source, float lambda, float dt) => source * exp(-lambda * dt);
    public static float2 decay(float2 source, float2 lambda, float dt) => source * exp(-lambda * dt);
    public static float3 decay(float3 source, float3 lambda, float dt) => source * exp(-lambda * dt);
    public static float4 decay(float4 source, float4 lambda, float dt) => source * exp(-lambda * dt);

    public static float damp(float start, float end, float lambda, float dt) => lerp(start, end, 1.0f - exp(-lambda * dt));
    public static float2 damp(float2 start, float2 end, float lambda, float dt) => lerp(start, end, 1.0f - exp(-lambda * dt));
    public static float3 damp(float3 start, float3 end, float lambda, float dt) => lerp(start, end, 1.0f - exp(-lambda * dt));
    public static float4 damp(float4 start, float4 end, float lambda, float dt) => lerp(start, end, 1.0f - exp(-lambda * dt));

    public static float first_order_intercept_time(float shotSpeed, float3 targetRelativePosition, float3 targetRelativeVelocity)
    {
        var velocitySquared = lengthsq(targetRelativeVelocity);
        if (velocitySquared < 0.001f)
        {
            return 0.0f;
        }

        var a = velocitySquared - shotSpeed * shotSpeed;
        if (abs(a) < 0.001f)
        {
            var time = -lengthsq(targetRelativePosition) / (2.0f * dot(targetRelativeVelocity, targetRelativePosition));
            return max(time, 0.0f);
        }

        var b = 2.0f * dot(targetRelativeVelocity, targetRelativePosition);
        var c = lengthsq(targetRelativePosition);
        var determinant = b * b - 4.0f * a * c;

        if (determinant > 0.0f)
        {
            var root = sqrt(determinant);
            var t1 = (-b + root) / (2.0f * a);
            var t2 = (-b - root) / (2.0f * a);
            if (t1 > 0.0f)
            {
                return t2 > 0.0f ? min(t1, t2) : t1;
            }

            return max(t2, 0.0f);
        }

        if (determinant < 0.0f)
        {
            return 0.0f;
        }

        return max(-b / (2.0f * a), 0.0f);
    }

    public static float3 first_order_intercept(
        float3 shooterPosition,
        float3 shooterVelocity,
        float shotSpeed,
        float3 targetPosition,
        float3 targetVelocity)
    {
        var targetRelativePosition = targetPosition - shooterPosition;
        var targetRelativeVelocity = targetVelocity - shooterVelocity;
        var time = first_order_intercept_time(shotSpeed, targetRelativePosition, targetRelativeVelocity);
        return targetPosition + time * targetRelativeVelocity;
    }

    public static float distance_to_segment(float2 point, float2 start, float2 end, out float2 closest)
    {
        var segment = end - start;
        var segmentLengthSq = lengthsq(segment);
        if (segmentLengthSq <= 0.0f)
        {
            closest = start;
            return distance(point, start);
        }

        var t = saturate(dot(point - start, segment) / segmentLengthSq);
        closest = start + segment * t;
        return distance(point, closest);
    }

    public static float catmullrom(float p0, float p1, float p2, float p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * ((2.0f * p1) + (p2 - p0) * t + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);
    }

    public static float2 catmullrom(float2 p0, float2 p1, float2 p2, float2 p3, float t) =>
        0.5f * ((2.0f * p1) + (p2 - p0) * t + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * (t * t) + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * (t * t * t));

    public static float3 catmullrom(float3 p0, float3 p1, float3 p2, float3 p3, float t) =>
        0.5f * ((2.0f * p1) + (p2 - p0) * t + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * (t * t) + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * (t * t * t));

    public static float4 catmullrom(float4 p0, float4 p1, float4 p2, float4 p3, float t) =>
        0.5f * ((2.0f * p1) + (p2 - p0) * t + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * (t * t) + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * (t * t * t));

    public static float quadratic_bezier(float p0, float p1, float p2, float t)
    {
        t = saturate(t);
        var inv = 1.0f - t;
        return inv * inv * p0 + 2.0f * inv * t * p1 + t * t * p2;
    }

    public static float3 quadratic_bezier(float3 p0, float3 p1, float3 p2, float t)
    {
        t = saturate(t);
        var inv = 1.0f - t;
        return inv * inv * p0 + 2.0f * inv * t * p1 + t * t * p2;
    }

    public static float cubic_bezier(float p0, float p1, float p2, float p3, float t)
    {
        t = saturate(t);
        var inv = 1.0f - t;
        return inv * inv * inv * p0 + 3.0f * inv * inv * t * p1 + 3.0f * inv * t * t * p2 + t * t * t * p3;
    }

    public static float3 cubic_bezier(float3 p0, float3 p1, float3 p2, float3 p3, float t)
    {
        t = saturate(t);
        var inv = 1.0f - t;
        return inv * inv * inv * p0 + 3.0f * inv * inv * t * p1 + 3.0f * inv * t * t * p2 + t * t * t * p3;
    }

    public static float smoothstep01(float value) => smoothstep(0.0f, 1.0f, value);

    public static float smootherstep(float value)
    {
        value = saturate(value);
        return value * value * value * (value * (value * 6.0f - 15.0f) + 10.0f);
    }

    public static float hash(float value) => frac(sin(value) * 43758.5453f);
    public static float hash(float2 value) => hash(dot(value, new float2(127.1f, 311.7f)));
    public static float hash(float3 value) => hash(dot(value, new float3(127.1f, 311.7f, 74.7f)));

    public static uint asuint(float value) => (uint)BitConverter.SingleToInt32Bits(value);

    // PCG hashes from Jarzynski and Olano, "Hash Functions for GPU Rendering" (JCGT 9(3), 2020). Integer-only, so
    // dxc and C# agree bit for bit. pcg3d/pcg4d carry uint bit patterns in int3/int4; float overloads hash IEEE bits.
    public static uint pcg(uint value)
    {
        var state = value * 747796405u + 2891336453u;
        var word = ((state >> (int)((state >> 28) + 4)) ^ state) * 277803737u;
        return (word >> 22) ^ word;
    }

    public static int3 pcg3d(int3 value)
    {
        uint x = (uint)value.x * 1664525u + 1013904223u, y = (uint)value.y * 1664525u + 1013904223u, z = (uint)value.z * 1664525u + 1013904223u;
        x += y * z; y += z * x; z += x * y;
        x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
        x += y * z; y += z * x; z += x * y;
        return new int3((int)x, (int)y, (int)z);
    }

    // The paper hashes 2 -> N inputs through pcg3d with the unused component held constant.
    public static int3 pcg3d(float2 value) => pcg3d(new float3(value, 0.0f));
    public static int3 pcg3d(float3 value) => pcg3d(new int3((int)asuint(value.x), (int)asuint(value.y), (int)asuint(value.z)));

    public static int4 pcg4d(int4 value)
    {
        uint x = (uint)value.x * 1664525u + 1013904223u, y = (uint)value.y * 1664525u + 1013904223u;
        uint z = (uint)value.z * 1664525u + 1013904223u, w = (uint)value.w * 1664525u + 1013904223u;
        x += y * w; y += z * x; z += x * y; w += y * z;
        x ^= x >> 16; y ^= y >> 16; z ^= z >> 16; w ^= w >> 16;
        x += y * w; y += z * x; z += x * y; w += y * z;
        return new int4((int)x, (int)y, (int)z, (int)w);
    }

    public static int4 pcg4d(float4 value) =>
        pcg4d(new int4((int)asuint(value.x), (int)asuint(value.y), (int)asuint(value.z), (int)asuint(value.w)));

    public static float log(float value) => MathF.Log(value);
    public static float2 log(float2 value) => new(log(value.x), log(value.y));
    public static float3 log(float3 value) => new(log(value.x), log(value.y), log(value.z));
    public static float4 log(float4 value) => new(log(value.x), log(value.y), log(value.z), log(value.w));

    public static float2 pow(float2 value, float2 power) => new(pow(value.x, power.x), pow(value.y, power.y));
    public static float3 pow(float3 value, float3 power) => new(pow(value.x, power.x), pow(value.y, power.y), pow(value.z, power.z));
    public static float4 pow(float4 value, float4 power) => new(pow(value.x, power.x), pow(value.y, power.y), pow(value.z, power.z), pow(value.w, power.w));

    public static float2 sqrt(float2 value) => new(sqrt(value.x), sqrt(value.y));
    public static float3 sqrt(float3 value) => new(sqrt(value.x), sqrt(value.y), sqrt(value.z));
    public static float4 sqrt(float4 value) => new(sqrt(value.x), sqrt(value.y), sqrt(value.z), sqrt(value.w));

    public static float2 sin(float2 value) => new(sin(value.x), sin(value.y));
    public static float3 sin(float3 value) => new(sin(value.x), sin(value.y), sin(value.z));
    public static float4 sin(float4 value) => new(sin(value.x), sin(value.y), sin(value.z), sin(value.w));

    public static float2 cos(float2 value) => new(cos(value.x), cos(value.y));
    public static float3 cos(float3 value) => new(cos(value.x), cos(value.y), cos(value.z));
    public static float4 cos(float4 value) => new(cos(value.x), cos(value.y), cos(value.z), cos(value.w));

    public static float2 acos(float2 value) => new(acos(value.x), acos(value.y));
    public static float3 acos(float3 value) => new(acos(value.x), acos(value.y), acos(value.z));
    public static float4 acos(float4 value) => new(acos(value.x), acos(value.y), acos(value.z), acos(value.w));

    public static float2 atan2(float2 y, float2 x) => new(atan2(y.x, x.x), atan2(y.y, x.y));
    public static float3 atan2(float3 y, float3 x) => new(atan2(y.x, x.x), atan2(y.y, x.y), atan2(y.z, x.z));
    public static float4 atan2(float4 y, float4 x) => new(atan2(y.x, x.x), atan2(y.y, x.y), atan2(y.z, x.z), atan2(y.w, x.w));

    // DXIL FMin/FMax take double overloads with the same rules as float (see min/max above).
    public static double min(double left, double right) => left < right || double.IsNaN(right) ? left : right;
    public static double max(double left, double right) => left >= right || double.IsNaN(right) ? left : right;

    public static bool any(bool2 value) => value.x || value.y;
    public static bool any(bool3 value) => value.x || value.y || value.z;
    public static bool any(bool4 value) => value.x || value.y || value.z || value.w;
    public static bool all(bool2 value) => value.x && value.y;
    public static bool all(bool3 value) => value.x && value.y && value.z;
    public static bool all(bool4 value) => value.x && value.y && value.z && value.w;

    // HLSL any/all on numeric vectors test components against zero (NaN != 0 counts as true).
    public static bool any(float2 value) => value.x != 0.0f || value.y != 0.0f;
    public static bool any(float3 value) => value.x != 0.0f || value.y != 0.0f || value.z != 0.0f;
    public static bool any(float4 value) => value.x != 0.0f || value.y != 0.0f || value.z != 0.0f || value.w != 0.0f;
    public static bool all(float2 value) => value.x != 0.0f && value.y != 0.0f;
    public static bool all(float3 value) => value.x != 0.0f && value.y != 0.0f && value.z != 0.0f;
    public static bool all(float4 value) => value.x != 0.0f && value.y != 0.0f && value.z != 0.0f && value.w != 0.0f;
    public static bool any(int2 value) => value.x != 0 || value.y != 0;
    public static bool any(int3 value) => value.x != 0 || value.y != 0 || value.z != 0;
    public static bool any(int4 value) => value.x != 0 || value.y != 0 || value.z != 0 || value.w != 0;
    public static bool all(int2 value) => value.x != 0 && value.y != 0;
    public static bool all(int3 value) => value.x != 0 && value.y != 0 && value.z != 0;
    public static bool all(int4 value) => value.x != 0 && value.y != 0 && value.z != 0 && value.w != 0;

    // HLSL 2021 argument order: select(condition, whenTrue, whenFalse).
    // Unity.Mathematics' select(falseValue, trueValue, condition) is the reverse.
    public static float select(bool condition, float whenTrue, float whenFalse) => condition ? whenTrue : whenFalse;
    public static float2 select(bool2 condition, float2 whenTrue, float2 whenFalse) =>
        new(condition.x ? whenTrue.x : whenFalse.x, condition.y ? whenTrue.y : whenFalse.y);
    public static float3 select(bool3 condition, float3 whenTrue, float3 whenFalse) =>
        new(condition.x ? whenTrue.x : whenFalse.x, condition.y ? whenTrue.y : whenFalse.y, condition.z ? whenTrue.z : whenFalse.z);
    public static float4 select(bool4 condition, float4 whenTrue, float4 whenFalse) =>
        new(condition.x ? whenTrue.x : whenFalse.x, condition.y ? whenTrue.y : whenFalse.y, condition.z ? whenTrue.z : whenFalse.z, condition.w ? whenTrue.w : whenFalse.w);

    // HLSL mul: mul(m, v) treats v as a column vector, mul(v, m) as a row vector.
    public static float mul(float left, float right) => left * right;

    public static float2 mul(float2x2 m, float2 v) =>
        new(m._m00 * v.x + m._m01 * v.y, m._m10 * v.x + m._m11 * v.y);

    public static float2 mul(float2 v, float2x2 m) =>
        new(v.x * m._m00 + v.y * m._m10, v.x * m._m01 + v.y * m._m11);

    public static float2x2 mul(float2x2 a, float2x2 b) =>
        new(
            a._m00 * b._m00 + a._m01 * b._m10, a._m00 * b._m01 + a._m01 * b._m11,
            a._m10 * b._m00 + a._m11 * b._m10, a._m10 * b._m01 + a._m11 * b._m11);

    public static float3 mul(float3x3 m, float3 v) =>
        new(
            m._m00 * v.x + m._m01 * v.y + m._m02 * v.z,
            m._m10 * v.x + m._m11 * v.y + m._m12 * v.z,
            m._m20 * v.x + m._m21 * v.y + m._m22 * v.z);

    public static float3 mul(float3 v, float3x3 m) =>
        new(
            v.x * m._m00 + v.y * m._m10 + v.z * m._m20,
            v.x * m._m01 + v.y * m._m11 + v.z * m._m21,
            v.x * m._m02 + v.y * m._m12 + v.z * m._m22);

    public static float3x3 mul(float3x3 a, float3x3 b) =>
        new(
            a._m00 * b._m00 + a._m01 * b._m10 + a._m02 * b._m20,
            a._m00 * b._m01 + a._m01 * b._m11 + a._m02 * b._m21,
            a._m00 * b._m02 + a._m01 * b._m12 + a._m02 * b._m22,
            a._m10 * b._m00 + a._m11 * b._m10 + a._m12 * b._m20,
            a._m10 * b._m01 + a._m11 * b._m11 + a._m12 * b._m21,
            a._m10 * b._m02 + a._m11 * b._m12 + a._m12 * b._m22,
            a._m20 * b._m00 + a._m21 * b._m10 + a._m22 * b._m20,
            a._m20 * b._m01 + a._m21 * b._m11 + a._m22 * b._m21,
            a._m20 * b._m02 + a._m21 * b._m12 + a._m22 * b._m22);

    // Abramowitz & Stegun 7.1.26, a single-precision rational/exponential fit with a stated
    // max absolute error of 1.5e-7. erf is odd, so only x >= 0 is fit and the sign is restored.
    // Measured against an independent reference: worst absolute error 6.621e-7, inside the
    // 2e-6 bar this package's tests pin.
    public static float erf(float value)
    {
        const float p = 0.3275911f;
        const float a1 = 0.254829592f;
        const float a2 = -0.284496736f;
        const float a3 = 1.421413741f;
        const float a4 = -1.453152027f;
        const float a5 = 1.061405429f;

        var s = sign(value);
        var x = abs(value);
        var t = 1.0f / (1.0f + p * x);
        var poly = ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t;
        var result = 1.0f - poly * exp(-x * x);
        return s * result;
    }

    // Giles, "Approximating the erfinv function" (GPU Computing Gems, 2010): a single-precision
    // minimax polynomial in two ranges of w = -ln((1-x)(1+x)), central and tail.
    // Domain is [-1, 1]. Outside it, including +-infinity, the result is NaN (there is no real
    // inverse). At the edges the mathematical limit is infinite: erfinv(1) = +infinity and
    // erfinv(-1) = -infinity. The unguarded polynomial hits log(0) = -infinity at those edges and
    // the tail branch's leading coefficient is negative, so it delivers the wrong sign; guard the
    // edges explicitly instead of trusting the fit through the singularity.
    // Measured against an independent reference: worst absolute error 5.066e-7, and the
    // erf(erfinv(y)) round trip 6.109e-7 worst case, both inside the 2e-6 bar this package's
    // tests pin.
    public static float erfinv(float value)
    {
        if (value >= 1.0f) return value > 1.0f ? float.NaN : float.PositiveInfinity;
        if (value <= -1.0f) return value < -1.0f ? float.NaN : float.NegativeInfinity;

        var w = -log((1.0f - value) * (1.0f + value));
        float p;
        if (w < 5.0f)
        {
            w -= 2.5f;
            p = 2.81022636e-08f;
            p = 3.43273939e-07f + p * w;
            p = -3.5233877e-06f + p * w;
            p = -4.39150654e-06f + p * w;
            p = 0.00021858087f + p * w;
            p = -0.00125372503f + p * w;
            p = -0.00417768164f + p * w;
            p = 0.246640727f + p * w;
            p = 1.50140941f + p * w;
        }
        else
        {
            w = sqrt(w) - 3.0f;
            p = -0.000200214257f;
            p = 0.000100950558f + p * w;
            p = 0.00134934322f + p * w;
            p = -0.00367342844f + p * w;
            p = 0.00573950773f + p * w;
            p = -0.0076224613f + p * w;
            p = 0.00943887047f + p * w;
            p = 1.00167406f + p * w;
            p = 2.83297682f + p * w;
        }

        return p * value;
    }

    // Ashima Arts / Ian McEwan 3D simplex noise (MIT), mirrored by cultmath_snoise(float3)
    // in shaders/CultMath.hlsl with the same float32 evaluation order.
    public static float snoise(float3 value)
    {
        const float cx = 1.0f / 6.0f;
        const float cy = 1.0f / 3.0f;
        var i = floor(value + dot(value, new float3(cy, cy, cy)));
        var x0 = value - i + dot(i, new float3(cx, cx, cx));
        var g = step(new float3(x0.y, x0.z, x0.x), x0);
        var l = 1.0f - g;
        var lzxy = new float3(l.z, l.x, l.y);
        var i1 = min(g, lzxy);
        var i2 = max(g, lzxy);
        var x1 = x0 - i1 + cx;
        var x2 = x0 - i2 + cy;
        var x3 = x0 - 0.5f;

        i = snoise_mod289(i);
        var p = snoise_permute(snoise_permute(snoise_permute(
            i.z + new float4(0.0f, i1.z, i2.z, 1.0f)) + i.y + new float4(0.0f, i1.y, i2.y, 1.0f)) + i.x + new float4(0.0f, i1.x, i2.x, 1.0f));

        const float n = 0.142857142857f;
        var ns = new float3(2.0f * n, 0.5f * n - 1.0f, n);
        var j = p - 49.0f * floor(p * ns.z * ns.z);
        var xs = floor(j * ns.z);
        var ys = floor(j - 7.0f * xs);
        var x = xs * ns.x + ns.y;
        var y = ys * ns.x + ns.y;
        var h = 1.0f - abs(x) - abs(y);

        var b0 = new float4(x.x, x.y, y.x, y.y);
        var b1 = new float4(x.z, x.w, y.z, y.w);
        var s0 = floor(b0) * 2.0f + 1.0f;
        var s1 = floor(b1) * 2.0f + 1.0f;
        var sh = -step(h, 0.0f);
        var a0 = new float4(b0.x, b0.z, b0.y, b0.w) + new float4(s0.x, s0.z, s0.y, s0.w) * new float4(sh.x, sh.x, sh.y, sh.y);
        var a1 = new float4(b1.x, b1.z, b1.y, b1.w) + new float4(s1.x, s1.z, s1.y, s1.w) * new float4(sh.z, sh.z, sh.w, sh.w);

        var p0 = new float3(a0.x, a0.y, h.x);
        var p1 = new float3(a0.z, a0.w, h.y);
        var p2 = new float3(a1.x, a1.y, h.z);
        var p3 = new float3(a1.z, a1.w, h.w);
        var norm = 1.79284291400159f - 0.85373472095314f * new float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
        p0 *= norm.x;
        p1 *= norm.y;
        p2 *= norm.z;
        p3 *= norm.w;

        var m = max(0.6f - new float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0f);
        m = m * m;
        return 42.0f * dot(m * m, new float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
    }

    // Ashima Arts / Ian McEwan 2D simplex noise, kept component-explicit so
    // the C# and HLSL mirrors preserve the same float32 evaluation order.
    public static float snoise(float2 value)
    {
        var c = new float4(
            0.211324865405187f,
            0.366025403784439f,
            -0.577350269189626f,
            0.024390243902439f);
        var i = floor(value + dot(value, new float2(c.y, c.y)));
        var x0 = value - i + dot(i, new float2(c.x, c.x));
        var i1 = x0.x > x0.y ? new float2(1.0f, 0.0f) : new float2(0.0f, 1.0f);
        var x12 = new float4(
            x0.x + c.x - i1.x,
            x0.y + c.x - i1.y,
            x0.x + c.z,
            x0.y + c.z);

        i = snoise_mod289(i);
        var p = snoise_permute(snoise_permute(i.y + new float3(0.0f, i1.y, 1.0f)) + i.x + new float3(0.0f, i1.x, 1.0f));
        var m = max(0.5f - new float3(
            dot(x0, x0),
            x12.x * x12.x + x12.y * x12.y,
            x12.z * x12.z + x12.w * x12.w), 0.0f);
        m = m * m;
        m = m * m;

        var x = 2.0f * frac(p * c.w) - 1.0f;
        var h = abs(x) - 0.5f;
        var ox = floor(x + 0.5f);
        var a0 = x - ox;
        m *= 1.79284291400159f - 0.85373472095314f * (a0 * a0 + h * h);

        var g = new float3(
            a0.x * x0.x + h.x * x0.y,
            a0.y * x12.x + h.y * x12.y,
            a0.z * x12.z + h.z * x12.w);
        return 130.0f * dot(m, g);
    }

    private static float2 snoise_mod289(float2 value) => value - floor(value * (1.0f / 289.0f)) * 289.0f;
    private static float3 snoise_mod289(float3 value) => value - floor(value * (1.0f / 289.0f)) * 289.0f;
    private static float4 snoise_mod289(float4 value) => value - floor(value * (1.0f / 289.0f)) * 289.0f;
    private static float3 snoise_permute(float3 value) => snoise_mod289(((value * 34.0f) + 1.0f) * value);
    private static float4 snoise_permute(float4 value) => snoise_mod289(((value * 34.0f) + 1.0f) * value);

    // Inigo Quilez, "smooth minimum" (https://iquilezles.org/articles/smin/): the cubic-polynomial
    // smin, carried to value-and-gradient form (invariant 8). h is affine in (b.w - a.w) inside the
    // smoothing band, so differentiating value = lerp(b.w, a.w, h) - k*h*(1-h) with respect to
    // position, the terms carrying dh/dp cancel exactly (dh/dp * [(a.w-b.w) - k + 2*k*h] and the
    // bracket is identically zero given how h is built from a.w-b.w). What is left is exactly
    // lerp(∇b, ∇a, h): the gradient blend is not an approximation, it is the analytic gradient.
    // Outside the band (|a.w - b.w| >= k), h saturates to 0 or 1 and this is exactly min(a, b) with
    // that input's own gradient, continuously (h and the correction term both reach the boundary at
    // the same value from the smooth side, so there is no kink to exclude here the way cellular's
    // F1 = F2 seam needs one).
    public static float4 smin_grad(float4 a, float4 b, float k)
    {
        var h = saturate(0.5f + 0.5f * (b.w - a.w) / k);
        var value = lerp(b.w, a.w, h) - k * h * (1.0f - h);
        var gradient = lerp(new float3(b.x, b.y, b.z), new float3(a.x, a.y, a.z), h);
        return new float4(gradient, value);
    }

    // Worley, "A Cellular Texture Basis Function" (SIGGRAPH 1996): F1/F2 and their analytic
    // gradients, searched over the jittered 3x3x3 neighbourhood of feature points. Feature points
    // are selected with pcg3d (design.md, "Integer hashing"), never the sin-based hash. The gradient
    // of a distance field, del|p - c|, is the unit vector (p - c)/|p - c|; that is undefined exactly
    // at a feature point (F1 = 0), so cellular follows the repo's existing degenerate-normal
    // convention (GameCult.Geometry.CultGeometryIsoSurface.EmitOrientedTriangle: guard the zero-length
    // case and return the zero vector instead of the NaN a bare normalize would produce there).
    public static CultCellular cellular(float3 p)
    {
        var cell = floor(p);
        var f1 = 1.0e30f;
        var f2 = 1.0e30f;
        var c1 = new float3(0.0f, 0.0f, 0.0f);
        var c2 = new float3(0.0f, 0.0f, 0.0f);
        var hash1 = 0;

        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var neighbor = cell + new float3(dx, dy, dz);
                    var hash = pcg3d(neighbor);
                    var jitter = new float3(cellular_unit(hash.x), cellular_unit(hash.y), cellular_unit(hash.z));
                    var feature = neighbor + jitter;
                    var d = distance(p, feature);

                    if (d < f1)
                    {
                        f2 = f1; c2 = c1;
                        f1 = d; c1 = feature; hash1 = hash.x;
                    }
                    else if (d < f2)
                    {
                        f2 = d; c2 = feature;
                    }
                }
            }
        }

        // Only F1's degenerate point is guarded, matching the spec: F2 = 0 needs two distinct
        // integer cells' pcg3d-jittered feature points to land on the exact same float32 value in
        // every component, which the jitter's ~24-bit float precision makes unreachable for any p
        // this function is actually called with, so there is no reachable case for a guard to defend.
        var grad1 = f1 > 0.0f ? (p - c1) / f1 : new float3(0.0f, 0.0f, 0.0f);
        var grad2 = (p - c2) / f2;

        return new CultCellular(
            new float4(grad1, f1),
            new float4(grad2 - grad1, f2 - f1),
            cellular_unit(hash1));
    }

    // Maps a pcg3d output component's uint bit pattern to [0, 1) by the standard uint-to-float
    // divide-by-2^32; the division is by an exact power of two, and the uint-to-float conversion is
    // the same IEEE-754 round-to-nearest both dxc and C# use, so this is bit-exact on both sides.
    private static float cellular_unit(int bits) => (uint)bits * (1.0f / 4294967296.0f);

    public static float value_noise(float2 position)
    {
        var cell = floor(position);
        var local = frac(position);
        var u = local * local * (3.0f - 2.0f * local);

        var a = hash(cell);
        var b = hash(cell + new float2(1.0f, 0.0f));
        var c = hash(cell + new float2(0.0f, 1.0f));
        var d = hash(cell + new float2(1.0f, 1.0f));

        return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
    }

    public static float value_noise_bicubic(float2 position)
    {
        var cell = floor(position);
        var local = frac(position);

        var y0 = catmullrom(
            hash(cell + new float2(-1.0f, -1.0f)),
            hash(cell + new float2(0.0f, -1.0f)),
            hash(cell + new float2(1.0f, -1.0f)),
            hash(cell + new float2(2.0f, -1.0f)),
            local.x);
        var y1 = catmullrom(
            hash(cell + new float2(-1.0f, 0.0f)),
            hash(cell + new float2(0.0f, 0.0f)),
            hash(cell + new float2(1.0f, 0.0f)),
            hash(cell + new float2(2.0f, 0.0f)),
            local.x);
        var y2 = catmullrom(
            hash(cell + new float2(-1.0f, 1.0f)),
            hash(cell + new float2(0.0f, 1.0f)),
            hash(cell + new float2(1.0f, 1.0f)),
            hash(cell + new float2(2.0f, 1.0f)),
            local.x);
        var y3 = catmullrom(
            hash(cell + new float2(-1.0f, 2.0f)),
            hash(cell + new float2(0.0f, 2.0f)),
            hash(cell + new float2(1.0f, 2.0f)),
            hash(cell + new float2(2.0f, 2.0f)),
            local.x);

        return catmullrom(y0, y1, y2, y3, local.y);
    }
}
