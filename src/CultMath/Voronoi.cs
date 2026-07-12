using System;
using System.Runtime.InteropServices;

namespace CultMath;

public static class Voronoi
{
    public static bool NativeAvailable { get; private set; } = true;

    public static Color32 SampleTone(float x, float y, float resolutionY, int frameIndex, CultMathTone tone, float span)
    {
        Span<float> xs = stackalloc[] { x };
        Span<float> ys = stackalloc[] { y };
        Span<CultMathTone> tones = stackalloc[] { tone };
        Span<float> spans = stackalloc[] { span };
        Span<Color32> colors = stackalloc Color32[1];
        SampleTones(xs, ys, tones, spans, resolutionY, frameIndex, colors);
        return colors[0];
    }

    public static void SampleTones(
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        ReadOnlySpan<CultMathTone> tones,
        ReadOnlySpan<float> spans,
        float resolutionY,
        int frameIndex,
        Span<Color32> colors)
    {
        if (xs.Length != ys.Length || xs.Length != tones.Length || xs.Length != spans.Length || xs.Length != colors.Length)
        {
            throw new ArgumentException("CultMath Voronoi batch spans must have equal length.");
        }

        if (xs.IsEmpty)
        {
            return;
        }

        if (NativeAvailable && TrySampleNative(xs, ys, tones, spans, resolutionY, frameIndex, colors))
        {
            return;
        }

        for (var index = 0; index < xs.Length; index++)
        {
            colors[index] = SampleManaged(xs[index], ys[index], resolutionY, frameIndex, tones[index], spans[index]);
        }
    }

    private static unsafe bool TrySampleNative(
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        ReadOnlySpan<CultMathTone> tones,
        ReadOnlySpan<float> spans,
        float resolutionY,
        int frameIndex,
        Span<Color32> colors)
    {
        try
        {
            fixed (float* xPtr = xs)
            fixed (float* yPtr = ys)
            fixed (CultMathTone* tonePtr = tones)
            fixed (float* spanPtr = spans)
            fixed (Color32* colorPtr = colors)
            {
                var result = Native.cultmath_apollonian_voronoi_tones(
                    xPtr,
                    yPtr,
                    (byte*)tonePtr,
                    spanPtr,
                    (nuint)xs.Length,
                    resolutionY,
                    frameIndex,
                    colorPtr);
                if (result == 0)
                {
                    return true;
                }
            }
        }
        catch (DllNotFoundException)
        {
            NativeAvailable = false;
        }
        catch (EntryPointNotFoundException)
        {
            NativeAvailable = false;
        }

        return false;
    }

    private static Color32 SampleManaged(float x, float y, float resolutionY, int frameIndex, CultMathTone tone, float span)
    {
        var sample = StableSample(x, y, resolutionY, frameIndex, tone, span);
        var luminance = math.saturate(sample.x * 0.299f + sample.y * 0.587f + sample.z * 0.114f);
        if (tone == CultMathTone.Header)
        {
            var glow = 0.62f + luminance * 0.38f;
            return new Color32((byte)(255 * glow), (byte)(96 * glow), (byte)(14 * glow));
        }

        if (tone == CultMathTone.Body)
        {
            var glow = 0.68f + luminance * 0.32f;
            return new Color32((byte)(245 * glow), (byte)(248 * glow), (byte)(252 * glow));
        }

        var gain = tone switch
        {
            CultMathTone.Panel => 0.42f,
            CultMathTone.Edge => 1.05f,
            _ => 0.24f,
        };
        var lift = tone switch
        {
            CultMathTone.Panel => 14.0f,
            CultMathTone.Edge => 24.0f,
            _ => 4.0f,
        };
        return new Color32(
            (byte)Math.Clamp(lift + sample.x * 255.0f * gain, 0, 255),
            (byte)Math.Clamp(lift + sample.y * 255.0f * gain, 0, 255),
            (byte)Math.Clamp(lift + sample.z * 255.0f * gain, 0, 255));
    }

    private static float3 StableSample(float x, float y, float resolutionY, int frameIndex, CultMathTone tone, float span)
    {
        var seed = x * 12.9898f + y * 78.233f + Hash1(((byte)tone + 1.0f) * 19.19f);
        var jitter = new float2((Hash1(seed) - 0.5f) * span, (Hash1(seed + 37.17f) - 0.5f) * span);
        return SampleField(x + jitter.x, y + jitter.y, resolutionY, frameIndex);
    }

    private static float3 SampleField(float px, float py, float resolutionY, int frameIndex)
    {
        var time = frameIndex / 120.0f;
        var p = new float2(6.0f * px / Math.Max(1.0f, resolutionY), 6.0f * py / Math.Max(1.0f, resolutionY));
        var n = math.floor(p);
        var f = math.frac(p);
        var distance = 8.0f;
        var color = float3.zero;
        const float smoothness = 0.005f;
        for (var j = -2; j <= 2; j++)
        {
            for (var i = -2; i <= 2; i++)
            {
                var g = new float2(i, j);
                var o = Hash2(n + g);
                var weight = o.x * 0.5f + 0.5f;
                o = 0.5f + 0.5f * new float2(math.sin(time + 6.2831f * o.x), math.sin(time + 6.2831f * o.y));
                var d2 = math.abs(g - f + o);
                var d = Math.Max(d2.x, d2.y) * weight;
                var seed = Hash1(math.dot(n + g, new float2(7.0f, 113.0f)));
                var hue = math.frac((n.x + g.x) * 0.173f + (n.y + g.y) * 0.379f + seed * 0.431f);
                var candidate = PastelSpectrum(hue);
                var h = math.smoothstep(0.0f, 1.0f, 0.5f + 0.5f * (distance - d) / smoothness);
                var correction = h * (1.0f - h) * smoothness / (1.0f + 3.0f * smoothness);
                distance = math.lerp(distance, d, h) - correction;
                color = math.lerp(color, candidate, h) - correction;
            }
        }

        return math.max(float3.zero, color * (1.0f - 0.1f * math.smoothstep(0.04f, 0.05f, distance)));
    }

    private static float Hash1(float value) => math.frac(math.sin(value) * 43758.5453f);

    private static float3 PastelSpectrum(float hue)
    {
        return HsvToRgb(hue, 0.52f, 0.98f);
    }

    private static float3 HsvToRgb(float hue, float saturation, float value)
    {
        var h = math.frac(hue) * 6.0f;
        var c = value * saturation;
        var x = c * (1.0f - Math.Abs(h % 2.0f - 1.0f));
        var m = value - c;
        float3 rgb;
        if (h < 1.0f)
        {
            rgb = new float3(c, x, 0.0f);
        }
        else if (h < 2.0f)
        {
            rgb = new float3(x, c, 0.0f);
        }
        else if (h < 3.0f)
        {
            rgb = new float3(0.0f, c, x);
        }
        else if (h < 4.0f)
        {
            rgb = new float3(0.0f, x, c);
        }
        else if (h < 5.0f)
        {
            rgb = new float3(x, 0.0f, c);
        }
        else
        {
            rgb = new float3(c, 0.0f, x);
        }

        return rgb + m;
    }

    private static float2 Hash2(float2 value)
    {
        var p = new float2(math.dot(value, new float2(127.1f, 311.7f)), math.dot(value, new float2(269.5f, 183.3f)));
        return math.frac(new float2(math.sin(p.x) * 43758.5453f, math.sin(p.y) * 43758.5453f));
    }

    private static unsafe partial class Native
    {
        [DllImport("cultmath_core", CallingConvention = CallingConvention.Cdecl)]
        public static extern int cultmath_apollonian_voronoi_tones(
            float* xs,
            float* ys,
            byte* tones,
            float* spans,
            nuint count,
            float resolutionY,
            int frameIndex,
            Color32* outColors);
    }
}
