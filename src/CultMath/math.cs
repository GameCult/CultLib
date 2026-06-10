namespace CultMath;

public static class math
{
    public const float PI = MathF.PI;
    public const float TAU = MathF.PI * 2.0f;
    public const float HALF_PI = MathF.PI * 0.5f;

    public static float2 float2(float x, float y) => new(x, y);
    public static float2 float2(float value) => new(value, value);
    public static float3 float3(float x, float y, float z) => new(x, y, z);
    public static float3 float3(float2 xy, float z) => new(xy, z);
    public static float3 float3(float value) => new(value, value, value);
    public static float4 float4(float x, float y, float z, float w) => new(x, y, z, w);
    public static float4 float4(float2 xy, float z, float w) => new(xy, z, w);
    public static float4 float4(float3 xyz, float w) => new(xyz, w);
    public static float4 float4(float value) => new(value, value, value, value);

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
    public static float exp(float value) => MathF.Exp(value);
    public static float2 exp(float2 value) => new(exp(value.x), exp(value.y));
    public static float3 exp(float3 value) => new(exp(value.x), exp(value.y), exp(value.z));
    public static float4 exp(float4 value) => new(exp(value.x), exp(value.y), exp(value.z), exp(value.w));

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
