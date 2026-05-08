namespace CultMath;

public static class math
{
    public const float PI = MathF.PI;
    public const float TAU = MathF.PI * 2.0f;
    public const float HALF_PI = MathF.PI * 0.5f;

    public static float radians(float degrees) => degrees * (PI / 180.0f);
    public static float degrees(float radians) => radians * (180.0f / PI);

    public static float abs(float value) => MathF.Abs(value);
    public static float2 abs(float2 value) => new(abs(value.x), abs(value.y));
    public static float3 abs(float3 value) => new(abs(value.x), abs(value.y), abs(value.z));
    public static float4 abs(float4 value) => new(abs(value.x), abs(value.y), abs(value.z), abs(value.w));

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

    public static float min(float left, float right) => MathF.Min(left, right);
    public static float2 min(float2 left, float2 right) => new(min(left.x, right.x), min(left.y, right.y));
    public static float3 min(float3 left, float3 right) => new(min(left.x, right.x), min(left.y, right.y), min(left.z, right.z));
    public static float4 min(float4 left, float4 right) => new(min(left.x, right.x), min(left.y, right.y), min(left.z, right.z), min(left.w, right.w));

    public static float max(float left, float right) => MathF.Max(left, right);
    public static float2 max(float2 left, float2 right) => new(max(left.x, right.x), max(left.y, right.y));
    public static float3 max(float3 left, float3 right) => new(max(left.x, right.x), max(left.y, right.y), max(left.z, right.z));
    public static float4 max(float4 left, float4 right) => new(max(left.x, right.x), max(left.y, right.y), max(left.z, right.z), max(left.w, right.w));

    public static float clamp(float value, float minimum, float maximum) => min(max(value, minimum), maximum);
    public static float2 clamp(float2 value, float2 minimum, float2 maximum) => min(max(value, minimum), maximum);
    public static float3 clamp(float3 value, float3 minimum, float3 maximum) => min(max(value, minimum), maximum);
    public static float4 clamp(float4 value, float4 minimum, float4 maximum) => min(max(value, minimum), maximum);

    public static float saturate(float value) => clamp(value, 0.0f, 1.0f);
    public static float2 saturate(float2 value) => clamp(value, 0.0f, 1.0f);
    public static float3 saturate(float3 value) => clamp(value, 0.0f, 1.0f);
    public static float4 saturate(float4 value) => clamp(value, 0.0f, 1.0f);

    public static float lerp(float start, float end, float amount) => start + (end - start) * amount;
    public static float2 lerp(float2 start, float2 end, float2 amount) => start + (end - start) * amount;
    public static float3 lerp(float3 start, float3 end, float3 amount) => start + (end - start) * amount;
    public static float4 lerp(float4 start, float4 end, float4 amount) => start + (end - start) * amount;

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

    public static float2 normalize(float2 value) => value / MathF.Max(length(value), 1.0e-20f);
    public static float3 normalize(float3 value) => value / MathF.Max(length(value), 1.0e-20f);
    public static float4 normalize(float4 value) => value / MathF.Max(length(value), 1.0e-20f);

    public static float2 reflect(float2 incident, float2 normal) => incident - 2.0f * dot(normal, incident) * normal;
    public static float3 reflect(float3 incident, float3 normal) => incident - 2.0f * dot(normal, incident) * normal;
}
