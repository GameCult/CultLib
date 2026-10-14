// SPDX-License-Identifier: Apache-2.0
//
// Cycles node semantics: what Blender's Cycles renderer computes for the Math, Map Range, Clamp, Mix and
// RGB to BW nodes and for a Color Ramp lookup, ported function by function from the Cycles kernel
// (intern/cycles of the Blender v5.2.2 tag; SPDX-FileCopyrightText: 2011-2022 Blender Foundation, Apache-2.0):
// kernel/svm/{math_util,map_range,clamp,mix,color_util,ramp,convert}.h, util/math_base.h, util/color.h and
// util/math_float3.h. Each function's comment names its Cycles source. Modified by GameCult: Cycles C++
// becomes C#, a function per node operation, and the node's socket defaults and clamp options are left to
// the caller. This file is Apache-2.0 (section 4: it was changed); the rest of CultMath is MIT, except the
// MPL-2.0 Phacelle files (see THIRD-PARTY-NOTICES.md). shaders/CultMath.Cycles.hlsl mirrors it bit for bit.
//
// Operands are finite floats. NaN and infinite operands are outside the oracle's reach and the mirror's.
// CyclesOracleTests holds every function to the results of Blender 5.2.2's own CPU Cycles.

namespace CultMath;

public static partial class math
{
    // FLT_EPSILON, the least epsilon a Compare node honours.
    private const float CYCLES_FLT_EPSILON = 1.1920929e-07f;

    /// <summary>The scene-linear red luminance coefficient Cycles' RGB to BW node reads from Blender 5.2.2's default OCIO config.</summary>
    public const float CYCLES_RGB_TO_Y_R = 0.212639093f;

    /// <summary>The scene-linear green luminance coefficient Cycles' RGB to BW node reads from Blender 5.2.2's default OCIO config.</summary>
    public const float CYCLES_RGB_TO_Y_G = 0.715169132f;

    /// <summary>The scene-linear blue luminance coefficient Cycles' RGB to BW node reads from Blender 5.2.2's default OCIO config.</summary>
    public const float CYCLES_RGB_TO_Y_B = 0.0721927434f;

    // ---- Math node (kernel/svm/math_util.h svm_math; util/math_base.h) ----

    /// <summary>Math node Divide (safe_divide): a / b, or 0 when b is 0.</summary>
    public static float cycles_divide(float a, float b) => b != 0.0f ? a / b : 0.0f;

    /// <summary>
    /// Math node Power (safe_powf with compatible_powf): 0 for a negative base with a fractional exponent,
    /// 1 for exponent 0, 0 for base 0. A negative base takes an integral exponent's parity.
    /// </summary>
    public static float cycles_power(float a, float b)
    {
        var whole = b >= 0.0f ? floor(b) : ceil(b);
        if (a < 0.0f && b != whole)
            return 0.0f;
        if (b == 0.0f)
            return 1.0f;
        if (a == 0.0f)
            return 0.0f;
        if (a < 0.0f)
        {
            var magnitude = pow(-a, b);
            return b - 2.0f * floor(0.5f * b) != 0.0f ? -magnitude : magnitude;
        }

        return pow(a, b);
    }

    /// <summary>Math node Logarithm (safe_logf): log(a) / log(b), or 0 when a or b is not positive or log(b) is 0.</summary>
    public static float cycles_logarithm(float a, float b)
    {
        if (a <= 0.0f || b <= 0.0f)
            return 0.0f;
        var denominator = log(b);
        return denominator != 0.0f ? log(a) / denominator : 0.0f;
    }

    /// <summary>Math node Square Root (safe_sqrtf): the square root of max(a, 0).</summary>
    public static float cycles_sqrt(float a) => sqrt(max(a, 0.0f));

    /// <summary>Math node Inverse Square Root (inversesqrtf): 1 / sqrt(a), or 0 when a is not positive.</summary>
    public static float cycles_inverse_sqrt(float a) => a > 0.0f ? 1.0f / sqrt(a) : 0.0f;

    /// <summary>
    /// Math node Truncated Modulo (safe_modulo), the remainder with the dividend's sign, exactly as fmodf computes
    /// it, or 0 when b is 0. GLSL has no fmod, and a - b trunc(a / b) is wrong where the quotient rounds up to an
    /// integer or the product rounds (3 mod 0.3 is 0.29999995, not 0). So this subtracts the largest power-of-two
    /// multiple of the divisor not above the remainder until the remainder is below the divisor: every step is an
    /// exact subtraction, as in fmod. The loops are bounded by the binades a float spans.
    /// </summary>
    public static float cycles_modulo(float a, float b)
    {
        var remainder = abs(a);
        var divisor = abs(b);
        for (var steps = 0; steps < 300 && remainder >= divisor && divisor > 0.0f; steps++)
        {
            var multiple = divisor;
            for (var doublings = 0; doublings < 300 && multiple * 2.0f <= remainder; doublings++)
                multiple = multiple * 2.0f;
            remainder = remainder - multiple;
        }

        return b != 0.0f ? (a < 0.0f ? -remainder : remainder) : 0.0f;
    }

    /// <summary>Math node Floored Modulo (safe_floored_modulo): a - floor(a / b) b, or 0 when b is 0, with one IEEE single
    /// rounding per operation (the reference of ruling cycles-kernel-reference-ruled). A build that contracts
    /// a - floor(a / b) b into one fused operation differs by at most 2^-23 (|a / b| + 1) |b| (measured 0.91 of 2^-24 (|a| + |b|)
    /// on Blender 5.2.2's AVX2 kernel); a fast-math build may also move floor(a / b) by one near an integer quotient,
    /// so the result differs by a whole |b|. See tools/cycles-oracle/README.md.</summary>
    public static float cycles_floored_modulo(float a, float b) => b != 0.0f ? a - floor(a / b) * b : 0.0f;

    /// <summary>Math node Wrap (wrapf; note the argument order): value wrapped into [min_value, max_value), or min_value for an empty range,
    /// with one IEEE single rounding per operation. A build that contracts value - range floor(...) into one fused operation differs by at
    /// most 2^-23 (|value| + |min_value| + |range|) (measured 0.77 of 2^-24 on the AVX2 kernel); a fast-math build may also move floor by one,
    /// a whole |range|. See tools/cycles-oracle/README.md.</summary>
    public static float cycles_wrap(float value, float max_value, float min_value)
    {
        var range = max_value - min_value;
        return range != 0.0f ? value - (range * floor((value - min_value) / range)) : min_value;
    }

    /// <summary>Math node Snap: floor(safe_divide(a, b)) b.</summary>
    public static float cycles_snap(float a, float b) => floor(cycles_divide(a, b)) * b;

    /// <summary>Math node Ping-Pong (pingpongf): a triangle wave of a with period 2 b, or 0 when b is 0.</summary>
    public static float cycles_pingpong(float a, float b)
    {
        var scaled = (a - b) / (b * 2.0f);
        return b != 0.0f ? abs((scaled - floor(scaled)) * b * 2.0f - b) : 0.0f;
    }

    /// <summary>Math node Smooth Minimum (smoothminf): the polynomial smooth minimum of width k, or min(a, b) when k is 0.</summary>
    public static float cycles_smooth_min(float a, float b, float k)
    {
        var h = max(k - abs(a - b), 0.0f) / k;
        return k != 0.0f ? min(a, b) - h * h * h * k * 0.16666667f : min(a, b);
    }

    /// <summary>Math node Smooth Maximum: -smoothminf(-a, -b, k).</summary>
    public static float cycles_smooth_max(float a, float b, float k) => -cycles_smooth_min(-a, -b, k);

    /// <summary>Math node Compare: 1 when a equals b or they differ by at most max(epsilon, FLT_EPSILON), else 0.</summary>
    public static float cycles_compare(float a, float b, float epsilon) =>
        (a == b || abs(a - b) <= max(epsilon, CYCLES_FLT_EPSILON)) ? 1.0f : 0.0f;

    /// <summary>Math node Round: floor(a + 0.5).</summary>
    public static float cycles_round(float a) => floor(a + 0.5f);

    /// <summary>Math node Truncate: floor for a >= 0, ceil otherwise.</summary>
    public static float cycles_trunc(float a) => a >= 0.0f ? floor(a) : ceil(a);

    /// <summary>Math node Fraction: a - floor(a).</summary>
    public static float cycles_fract(float a) => a - floor(a);

    /// <summary>Math node Sign (compatible_signf): 0 for 0, -1 below, 1 above.</summary>
    public static float cycles_sign(float a) => a == 0.0f ? 0.0f : (a < 0.0f ? -1.0f : 1.0f);

    /// <summary>Math node Arcsine (safe_asinf, util/math_base.h): asin of a clamped to [-1, 1].</summary>
    public static float cycles_arcsine(float a) => asin(clamp(a, -1.0f, 1.0f));

    /// <summary>Math node Arccosine (safe_acosf, util/math_base.h): acos of a clamped to [-1, 1].</summary>
    public static float cycles_arccosine(float a) => acos(clamp(a, -1.0f, 1.0f));

    /// <summary>Math node Arctan2 (compatible_atan2, util/math_base.h; a is y, b is x): 0 at the origin, else atan2(a, b).</summary>
    public static float cycles_arctan2(float a, float b) => (b == 0.0f && a == 0.0f) ? 0.0f : atan2(a, b);

    // ---- Map Range node (kernel/svm/map_range.h svm_node_map_range) ----
    // A Map Range node's Clamp option is a Clamp node of type Range on its result (MapRangeNode::expand):
    // compose cycles_clamp_range over these.

    /// <summary>Map Range, Linear: to_min + (value - from_min) / (from_max - from_min) (to_max - to_min), or 0 when from_max equals from_min.</summary>
    public static float cycles_map_range_linear(float value, float from_min, float from_max, float to_min, float to_max)
    {
        var factor = (value - from_min) / (from_max - from_min);
        return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0f;
    }

    /// <summary>Map Range, Stepped: the linear factor quantized to `steps` steps (0 when steps is not positive), or 0 when from_max equals from_min.</summary>
    public static float cycles_map_range_stepped(float value, float from_min, float from_max, float to_min, float to_max, float steps)
    {
        var factor = (value - from_min) / (from_max - from_min);
        factor = steps > 0.0f ? floor(factor * (steps + 1.0f)) / steps : 0.0f;
        return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0f;
    }

    /// <summary>
    /// Map Range, Smooth Step: the value goes through smoothstep(from_min, from_max, .), flipped when from_min
    /// is above from_max (util/math_base.h smoothstep), or 0 when from_max equals from_min.
    /// </summary>
    public static float cycles_map_range_smoothstep(float value, float from_min, float from_max, float to_min, float to_max)
    {
        var low = min(from_min, from_max);
        var high = max(from_min, from_max);
        var t = (value - low) / (high - low);
        var eased = value < low ? 0.0f : (value >= high ? 1.0f : (3.0f - 2.0f * t) * (t * t));
        var factor = from_min > from_max ? 1.0f - eased : eased;
        return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0f;
    }

    /// <summary>Map Range, Smoother Step (map_range.h smootherstep), flipped when from_min is above from_max, or 0 when from_max equals from_min.</summary>
    public static float cycles_map_range_smootherstep(float value, float from_min, float from_max, float to_min, float to_max)
    {
        var low = min(from_min, from_max);
        var high = max(from_min, from_max);
        var x = clamp(cycles_divide(value - low, high - low), 0.0f, 1.0f);
        var eased = x * x * x * (x * (x * 6.0f - 15.0f) + 10.0f);
        var factor = from_min > from_max ? 1.0f - eased : eased;
        return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0f;
    }

    // ---- Clamp node (kernel/svm/clamp.h svm_node_clamp) ----

    /// <summary>Clamp node, Min Max: clamp(value, min_value, max_value).</summary>
    public static float cycles_clamp_minmax(float value, float min_value, float max_value) => clamp(value, min_value, max_value);

    /// <summary>Clamp node, Range: as Min Max, with the bounds swapped when min_value is above max_value.</summary>
    public static float cycles_clamp_range(float value, float min_value, float max_value) =>
        min_value > max_value ? clamp(value, max_value, min_value) : clamp(value, min_value, max_value);

    // ---- Mix node (kernel/svm/mix.h, color_util.h) ----
    // Factor clamping (svm_node_mix_color's saturatef) and result clamping (saturate) are the caller's.

    /// <summary>Mix node, Float (svm_node_mix_float): a (1 - t) + b t.</summary>
    public static float cycles_mix_float(float t, float a, float b) => a * (1.0f - t) + b * t;

    /// <summary>Mix node, Vector, uniform factor (svm_node_mix_vector): a (1 - t) + b t per component.</summary>
    public static float3 cycles_mix_vector(float t, float3 a, float3 b) => a * (new float3(1.0f, 1.0f, 1.0f) - t) + b * t;

    /// <summary>Mix node, Mix blend mode (svm_mix_blend).</summary>
    public static float3 cycles_mix_blend(float t, float3 col1, float3 col2) => col1 + t * (col2 - col1);

    /// <summary>Mix node, Add blend mode (svm_mix_add).</summary>
    public static float3 cycles_mix_add(float t, float3 col1, float3 col2) => col1 + t * ((col1 + col2) - col1);

    /// <summary>Mix node, Multiply blend mode (svm_mix_mul).</summary>
    public static float3 cycles_mix_mul(float t, float3 col1, float3 col2) => col1 + t * (col1 * col2 - col1);

    /// <summary>Mix node, Screen blend mode (svm_mix_screen).</summary>
    public static float3 cycles_mix_screen(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        var one = new float3(1.0f, 1.0f, 1.0f);
        return one - (new float3(tm, tm, tm) + t * (one - col2)) * (one - col1);
    }

    /// <summary>Mix node, Overlay blend mode (svm_mix_overlay).</summary>
    public static float3 cycles_mix_overlay(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        return new float3(
            col1.x < 0.5f ? col1.x * (tm + 2.0f * t * col2.x) : 1.0f - (tm + 2.0f * t * (1.0f - col2.x)) * (1.0f - col1.x),
            col1.y < 0.5f ? col1.y * (tm + 2.0f * t * col2.y) : 1.0f - (tm + 2.0f * t * (1.0f - col2.y)) * (1.0f - col1.y),
            col1.z < 0.5f ? col1.z * (tm + 2.0f * t * col2.z) : 1.0f - (tm + 2.0f * t * (1.0f - col2.z)) * (1.0f - col1.z));
    }

    /// <summary>Mix node, Subtract blend mode (svm_mix_sub).</summary>
    public static float3 cycles_mix_sub(float t, float3 col1, float3 col2) => col1 + t * ((col1 - col2) - col1);

    /// <summary>Mix node, Divide blend mode (svm_mix_div): a channel whose col2 is 0 keeps col1.</summary>
    public static float3 cycles_mix_div(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        return new float3(
            col2.x != 0.0f ? tm * col1.x + t * col1.x / col2.x : col1.x,
            col2.y != 0.0f ? tm * col1.y + t * col1.y / col2.y : col1.y,
            col2.z != 0.0f ? tm * col1.z + t * col1.z / col2.z : col1.z);
    }

    /// <summary>Mix node, Difference blend mode (svm_mix_diff).</summary>
    public static float3 cycles_mix_diff(float t, float3 col1, float3 col2) => col1 + t * (abs(col1 - col2) - col1);

    /// <summary>Mix node, Exclusion blend mode (svm_mix_exclusion).</summary>
    public static float3 cycles_mix_exclusion(float t, float3 col1, float3 col2) =>
        max(col1 + t * ((col1 + col2 - 2.0f * col1 * col2) - col1), new float3(0.0f, 0.0f, 0.0f));

    /// <summary>Mix node, Darken blend mode (svm_mix_dark).</summary>
    public static float3 cycles_mix_dark(float t, float3 col1, float3 col2) => col1 + t * (min(col1, col2) - col1);

    /// <summary>Mix node, Lighten blend mode (svm_mix_light).</summary>
    public static float3 cycles_mix_light(float t, float3 col1, float3 col2) => col1 + t * (max(col1, col2) - col1);

    /// <summary>Mix node, Dodge blend mode (svm_mix_dodge).</summary>
    public static float3 cycles_mix_dodge(float t, float3 col1, float3 col2)
    {
        var divisor = new float3(1.0f, 1.0f, 1.0f) - t * col2;
        return new float3(
            col1.x != 0.0f ? (divisor.x <= 0.0f ? 1.0f : min(col1.x / divisor.x, 1.0f)) : col1.x,
            col1.y != 0.0f ? (divisor.y <= 0.0f ? 1.0f : min(col1.y / divisor.y, 1.0f)) : col1.y,
            col1.z != 0.0f ? (divisor.z <= 0.0f ? 1.0f : min(col1.z / divisor.z, 1.0f)) : col1.z);
    }

    /// <summary>Mix node, Burn blend mode (svm_mix_burn).</summary>
    public static float3 cycles_mix_burn(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        var divisor = tm + t * col2;
        var burnt = new float3(1.0f, 1.0f, 1.0f) - (new float3(1.0f, 1.0f, 1.0f) - col1) / divisor;
        return new float3(
            divisor.x <= 0.0f ? 0.0f : clamp(burnt.x, 0.0f, 1.0f),
            divisor.y <= 0.0f ? 0.0f : clamp(burnt.y, 0.0f, 1.0f),
            divisor.z <= 0.0f ? 0.0f : clamp(burnt.z, 0.0f, 1.0f));
    }

    /// <summary>RGB to HSV as Cycles computes it (util/color.h rgb_to_hsv): hue in [0, 1), saturation, value.</summary>
    public static float3 cycles_rgb_to_hsv(float3 rgb)
    {
        var cmax = max(rgb.x, max(rgb.y, rgb.z));
        var cmin = min(rgb.x, min(rgb.y, rgb.z));
        var cdelta = cmax - cmin;
        var s = cmax != 0.0f ? cdelta / cmax : 0.0f;
        var c = (new float3(cmax, cmax, cmax) - rgb) / cdelta;
        var h = rgb.x == cmax ? c.z - c.y : (rgb.y == cmax ? 2.0f + c.x - c.z : 4.0f + c.y - c.x);
        h = h * 0.16666667f; // Cycles' kernel is built with reciprocal math: h /= 6.0f computes h * (1 / 6.0f).
        h = h < 0.0f ? h + 1.0f : h;
        return new float3(s != 0.0f ? h : 0.0f, s, cmax);
    }

    /// <summary>HSV to RGB as Cycles computes it (util/color.h hsv_to_rgb).</summary>
    public static float3 cycles_hsv_to_rgb(float3 hsv)
    {
        var h = hsv.x == 1.0f ? 0.0f : hsv.x;
        h = h * 6.0f;
        var i = floor(h);
        var f = h - i;
        var p = hsv.z * (1.0f - hsv.y);
        var q = hsv.z * (1.0f - (hsv.y * f));
        var t = hsv.z * (1.0f - (hsv.y * (1.0f - f)));
        var rgb = i == 0.0f ? new float3(hsv.z, t, p) :
            (i == 1.0f ? new float3(q, hsv.z, p) :
            (i == 2.0f ? new float3(p, hsv.z, t) :
            (i == 3.0f ? new float3(p, q, hsv.z) :
            (i == 4.0f ? new float3(t, p, hsv.z) : new float3(hsv.z, p, q)))));
        return hsv.y != 0.0f ? rgb : new float3(hsv.z, hsv.z, hsv.z);
    }

    /// <summary>Mix node, Hue blend mode (svm_mix_hue).</summary>
    public static float3 cycles_mix_hue(float t, float3 col1, float3 col2)
    {
        var hsv2 = cycles_rgb_to_hsv(col2);
        var hsv = cycles_rgb_to_hsv(col1);
        var shifted = cycles_hsv_to_rgb(new float3(hsv2.x, hsv.y, hsv.z));
        return hsv2.y != 0.0f ? col1 + t * (shifted - col1) : col1;
    }

    /// <summary>Mix node, Saturation blend mode (svm_mix_sat).</summary>
    public static float3 cycles_mix_sat(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        var hsv = cycles_rgb_to_hsv(col1);
        var hsv2 = cycles_rgb_to_hsv(col2);
        return hsv.y != 0.0f ? cycles_hsv_to_rgb(new float3(hsv.x, tm * hsv.y + t * hsv2.y, hsv.z)) : col1;
    }

    /// <summary>Mix node, Value blend mode (svm_mix_val).</summary>
    public static float3 cycles_mix_val(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        var hsv = cycles_rgb_to_hsv(col1);
        var hsv2 = cycles_rgb_to_hsv(col2);
        return cycles_hsv_to_rgb(new float3(hsv.x, hsv.y, tm * hsv.z + t * hsv2.z));
    }

    /// <summary>Mix node, Color blend mode (svm_mix_color).</summary>
    public static float3 cycles_mix_color(float t, float3 col1, float3 col2)
    {
        var hsv2 = cycles_rgb_to_hsv(col2);
        var hsv = cycles_rgb_to_hsv(col1);
        var shifted = cycles_hsv_to_rgb(new float3(hsv2.x, hsv2.y, hsv.z));
        return hsv2.y != 0.0f ? col1 + t * (shifted - col1) : col1;
    }

    /// <summary>Mix node, Soft Light blend mode (svm_mix_soft).</summary>
    public static float3 cycles_mix_soft(float t, float3 col1, float3 col2)
    {
        var tm = 1.0f - t;
        var one = new float3(1.0f, 1.0f, 1.0f);
        var scr = one - (one - col2) * (one - col1);
        return tm * col1 + t * ((one - col1) * col2 * col1 + col1 * scr);
    }

    /// <summary>Mix node, Linear Light blend mode (svm_mix_linear).</summary>
    public static float3 cycles_mix_linear(float t, float3 col1, float3 col2) =>
        col1 + t * (2.0f * col2 + new float3(-1.0f, -1.0f, -1.0f));

    // ---- RGB to BW node (kernel/svm/convert.h NODE_CONVERT_CF) ----

    /// <summary>RGB to BW node: the scene-linear luminance of a colour.</summary>
    public static float cycles_rgb_to_bw(float3 color) =>
        color.x * CYCLES_RGB_TO_Y_R + color.y * CYCLES_RGB_TO_Y_G + color.z * CYCLES_RGB_TO_Y_B;

    // ---- Color Ramp lookup (kernel/svm/ramp.h rgb_ramp_lookup, without extrapolation) ----

    /// <summary>
    /// Where a Color Ramp of `size` table entries is read at t: (index, weight), the lower entry and the
    /// weight of the next one. The colour is table[index] when weight is 0 and
    /// (1 - weight) table[index] + weight table[index + 1] otherwise. The ramp table itself is the caller's;
    /// Blender's holds 257 entries, the ramp sampled at i / 256.
    /// </summary>
    public static float2 cycles_ramp_index(float t, int size)
    {
        var f = (t > 0.0f ? (t < 1.0f ? t : 1.0f) : 0.0f) * (float)(size - 1);
        var index = clamp((int)f, 0, size - 1);
        var lower = (float)index;
        return new float2(lower, f - lower);
    }
}
