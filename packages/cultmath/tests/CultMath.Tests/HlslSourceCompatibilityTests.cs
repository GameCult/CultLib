using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// HLSL written against CultMath compiles as C# under <c>using static CultMath.math;</c>.
/// The only spelling C# forces is float literal suffixes (<c>0.5f</c>): an unsuffixed
/// literal is a double in C#.
/// </summary>
public sealed class HlslSourceCompatibilityTests
{
    // ---- HLSL-shaped source: keep this block free of C#-only constructs. ----
    static float3 shade(float3 position, float3 normal, float2 wind, float time)
    {
        float3 p = position;
        p.xz = p.xz + wind * time;
        p.y = saturate(p.y);

        float3x3 basis = float3x3(float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1));
        float3 n = normalize(mul(basis, normal));
        float ndotl = saturate(dot(n, normalize(float3(0.3f, 1, 0.2f))));
        float3 color = lerp(float3(0.1f, 0.1f, 0.2f), float3(1, 0.9f, 0.7f), ndotl);

        if (any(p < 0.0f))
            color *= 0.5f;
        if (all(abs(n) <= 1.0f))
            color.yzx = color.yzx * float3(1, 1, 1);

        color = select(p > 10.0f, float3(0), color);
        float2 uv = frac(p.xz * 0.25f);
        color.xy = lerp(color.xy, uv, 0.1f);
        return color;
    }

    static float2 steer(float2 direction, float torque)
    {
        float2x2 turn = float2x2(cos(torque), -sin(torque), sin(torque), cos(torque));
        float2 turned = mul(direction, turn);
        return normalize(turned) * pow(length(turned), 1.0f);
    }

    static float fbm(float3 p, int octaves)
    {
        float sum = 0.0f;
        float amp = 0.5f;
        float freq = 1.0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += abs(snoise(p * freq)) * amp;
            freq *= 2.0f;
            amp *= 0.5f;
        }
        return sum;
    }
    // ---- end HLSL-shaped source ----

    [Fact]
    public void ShaderStyleMathCompilesAndEvaluates()
    {
        var color = shade(new float3(1.0f, 0.5f, 2.0f), new float3(0.0f, 1.0f, 0.0f), new float2(1.0f, 0.0f), 0.0f);

        Assert.True(color.x > 0.1f && color.x <= 1.0f);
        Assert.True(float.IsFinite(color.y) && float.IsFinite(color.z));

        var far = shade(new float3(20.0f, 20.0f, 20.0f), new float3(0.0f, 1.0f, 0.0f), float2(0.0f), 0.0f);
        Assert.Equal(0.0f, far.z);
    }

    [Fact]
    public void AetheriaStyleRotationAndNoiseCompile()
    {
        var steered = steer(new float2(1.0f, 0.0f), 0.5f);
        var viaRotate = mul(new float2(1.0f, 0.0f), CultMath.float2x2.Rotate(0.5f));

        Assert.Equal(viaRotate.x, steered.x, precision: 5);
        Assert.Equal(viaRotate.y, steered.y, precision: 5);

        var view = mul(float3(0, 0, 1), CultMath.float3x3.Euler(float3(float2(0.3f, -1.1f).yx, 0), RotationOrder.YXZ));
        Assert.Equal(1.0f, length(view), precision: 5);

        var noise = fbm(float3(1.5f, -2.25f, 0.75f), 4);
        Assert.InRange(noise, 0.0f, 1.0f);
    }
}
