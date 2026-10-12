#ifndef CULTMATH_CYCLES_HLSL
#define CULTMATH_CYCLES_HLSL

// SPDX-License-Identifier: Apache-2.0
//
// Cycles node semantics: what Blender's Cycles renderer computes for the Math, Map Range, Clamp, Mix and
// RGB to BW nodes and for a Color Ramp lookup, ported function by function from the Cycles kernel
// (intern/cycles of the Blender v5.2.2 tag; SPDX-FileCopyrightText: 2011-2022 Blender Foundation, Apache-2.0):
// kernel/svm/{math_util,map_range,clamp,mix,color_util,ramp,convert}.h, util/math_base.h, util/color.h and
// util/math_float3.h. Each function's comment names its Cycles source. Modified by GameCult: Cycles C++
// becomes HLSL, a function per node operation, and the node's socket defaults and clamp options are left to
// the caller. This file is Apache-2.0 (section 4: it was changed); CultMath.hlsl, which includes it after its
// own definitions, is MIT (see THIRD-PARTY-NOTICES.md). math.Cycles.cs mirrors it bit for bit.
//
// Operands are finite floats. NaN and infinite operands are outside the oracle's reach and the mirror's.

// FLT_EPSILON, the least epsilon a Compare node honours.
static const float CULTMATH_CYCLES_FLT_EPSILON = 1.1920929e-07;

// The scene-linear luminance coefficients Cycles reads from Blender 5.2.2's default OCIO config
// (kernel/film linear_rgb_to_gray). The oracle fixture pins them with a case per primary.
static const float CULTMATH_CYCLES_RGB_TO_Y_R = 0.212639093;
static const float CULTMATH_CYCLES_RGB_TO_Y_G = 0.715169132;
static const float CULTMATH_CYCLES_RGB_TO_Y_B = 0.0721927434;

// ---- Math node (kernel/svm/math_util.h svm_math; util/math_base.h) ----

// safe_divide
float cultmath_cycles_divide(float a, float b) { return b != 0.0 ? a / b : 0.0; }

// safe_powf with compatible_powf. A negative base takes an integral exponent's parity; a base below zero
// with a fractional exponent is 0, and so is one with an exponent past the int range, where Cycles' int
// round trip cannot hold it.
float cultmath_cycles_power(float a, float b)
{
    float whole = b >= 0.0 ? floor(b) : ceil(b);
    if (a < 0.0 && (b != whole || b >= 2147483648.0 || b < -2147483648.0))
        return 0.0;
    if (b == 0.0)
        return 1.0;
    if (a == 0.0)
        return 0.0;
    if (a < 0.0)
    {
        float magnitude = pow(-a, b);
        return b - 2.0 * floor(0.5 * b) != 0.0 ? -magnitude : magnitude;
    }
    return pow(a, b);
}

// safe_logf
float cultmath_cycles_logarithm(float a, float b)
{
    if (a <= 0.0 || b <= 0.0)
        return 0.0;
    float denominator = log(b);
    return denominator != 0.0 ? log(a) / denominator : 0.0;
}

// safe_sqrtf
float cultmath_cycles_sqrt(float a) { return sqrt(max(a, 0.0)); }

// inversesqrtf
float cultmath_cycles_inverse_sqrt(float a) { return a > 0.0 ? 1.0 / sqrt(a) : 0.0; }

// safe_modulo is fmodf, the remainder with the dividend's sign, exactly. GLSL has no fmod, and a - b trunc(a / b)
// is wrong where the quotient rounds up to an integer or the product rounds (3 mod 0.3 is 0.29999995, not 0).
// So this subtracts the largest power-of-two multiple of the divisor not above the remainder until the
// remainder is below the divisor: every step is an exact subtraction, as in fmod. The loops are bounded by the
// binades a float spans.
float cultmath_cycles_modulo(float a, float b)
{
    float remainder = abs(a);
    float divisor = abs(b);
    for (int steps = 0; steps < 300 && remainder >= divisor && divisor > 0.0; steps++)
    {
        float multiple = divisor;
        for (int doublings = 0; doublings < 300 && multiple * 2.0 <= remainder; doublings++)
            multiple = multiple * 2.0;
        remainder = remainder - multiple;
    }
    return b != 0.0 ? (a < 0.0 ? -remainder : remainder) : 0.0;
}

// safe_floored_modulo
float cultmath_cycles_floored_modulo(float a, float b) { return b != 0.0 ? a - floor(a / b) * b : 0.0; }

// wrapf (note the argument order: value, max, min)
float cultmath_cycles_wrap(float value, float max_value, float min_value)
{
    float range = max_value - min_value;
    return range != 0.0 ? value - (range * floor((value - min_value) / range)) : min_value;
}

// floorf(safe_divide(a, b)) * b
float cultmath_cycles_snap(float a, float b) { return floor(cultmath_cycles_divide(a, b)) * b; }

// pingpongf
float cultmath_cycles_pingpong(float a, float b)
{
    float scaled = (a - b) / (b * 2.0);
    return b != 0.0 ? abs((scaled - floor(scaled)) * b * 2.0 - b) : 0.0;
}

// smoothminf
float cultmath_cycles_smooth_min(float a, float b, float k)
{
    float h = max(k - abs(a - b), 0.0) / k;
    return k != 0.0 ? min(a, b) - h * h * h * k * 0.16666667 : min(a, b);
}

// -smoothminf(-a, -b, k)
float cultmath_cycles_smooth_max(float a, float b, float k) { return -cultmath_cycles_smooth_min(-a, -b, k); }

// NODE_MATH_COMPARE
float cultmath_cycles_compare(float a, float b, float epsilon)
{
    return (a == b || abs(a - b) <= max(epsilon, CULTMATH_CYCLES_FLT_EPSILON)) ? 1.0 : 0.0;
}

// NODE_MATH_ROUND
float cultmath_cycles_round(float a) { return floor(a + 0.5); }

// NODE_MATH_TRUNC
float cultmath_cycles_trunc(float a) { return a >= 0.0 ? floor(a) : ceil(a); }

// NODE_MATH_FRACTION
float cultmath_cycles_fract(float a) { return a - floor(a); }

// compatible_signf
float cultmath_cycles_sign(float a) { return a == 0.0 ? 0.0 : (a < 0.0 ? -1.0 : 1.0); }

// ---- Map Range node (kernel/svm/map_range.h svm_node_map_range) ----
// A Map Range node's Clamp option is a Clamp node of type Range on its result (MapRangeNode::expand):
// compose cultmath_cycles_clamp_range over these.

float cultmath_cycles_map_range_linear(float value, float from_min, float from_max, float to_min, float to_max)
{
    float factor = (value - from_min) / (from_max - from_min);
    return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0;
}

float cultmath_cycles_map_range_stepped(float value, float from_min, float from_max, float to_min, float to_max, float steps)
{
    float factor = (value - from_min) / (from_max - from_min);
    factor = steps > 0.0 ? floor(factor * (steps + 1.0)) / steps : 0.0;
    return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0;
}

// The value itself, not a normalized one, goes through smoothstep(from_min, from_max, .), flipped when
// from_min > from_max (util/math_base.h smoothstep).
float cultmath_cycles_map_range_smoothstep(float value, float from_min, float from_max, float to_min, float to_max)
{
    float low = min(from_min, from_max);
    float high = max(from_min, from_max);
    float t = (value - low) / (high - low);
    float eased = value < low ? 0.0 : (value >= high ? 1.0 : (3.0 - 2.0 * t) * (t * t));
    float factor = from_min > from_max ? 1.0 - eased : eased;
    return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0;
}

// smootherstep of map_range.h, flipped when from_min > from_max.
float cultmath_cycles_map_range_smootherstep(float value, float from_min, float from_max, float to_min, float to_max)
{
    float low = min(from_min, from_max);
    float high = max(from_min, from_max);
    float x = clamp(cultmath_cycles_divide(value - low, high - low), 0.0, 1.0);
    float eased = x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
    float factor = from_min > from_max ? 1.0 - eased : eased;
    return from_max != from_min ? to_min + factor * (to_max - to_min) : 0.0;
}

// ---- Clamp node (kernel/svm/clamp.h svm_node_clamp) ----

// Type Min Max
float cultmath_cycles_clamp_minmax(float value, float min_value, float max_value) { return clamp(value, min_value, max_value); }

// Type Range: the bounds swap when min_value > max_value.
float cultmath_cycles_clamp_range(float value, float min_value, float max_value)
{
    return min_value > max_value ? clamp(value, max_value, min_value) : clamp(value, min_value, max_value);
}

// ---- Mix node (kernel/svm/mix.h, color_util.h) ----
// Factor clamping (svm_node_mix_color's saturatef) and result clamping (saturate) are the caller's.

// svm_node_mix_float
float cultmath_cycles_mix_float(float t, float a, float b) { return a * (1.0 - t) + b * t; }

// svm_node_mix_vector
float3 cultmath_cycles_mix_vector(float t, float3 a, float3 b) { return a * (float3(1.0, 1.0, 1.0) - t) + b * t; }

float3 cultmath_cycles_mix_blend(float t, float3 col1, float3 col2) { return col1 + t * (col2 - col1); }

float3 cultmath_cycles_mix_add(float t, float3 col1, float3 col2) { return col1 + t * ((col1 + col2) - col1); }

float3 cultmath_cycles_mix_mul(float t, float3 col1, float3 col2) { return col1 + t * (col1 * col2 - col1); }

float3 cultmath_cycles_mix_screen(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    float3 one = float3(1.0, 1.0, 1.0);
    return one - (float3(tm, tm, tm) + t * (one - col2)) * (one - col1);
}

float3 cultmath_cycles_mix_overlay(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    return float3(
        col1.x < 0.5 ? col1.x * (tm + 2.0 * t * col2.x) : 1.0 - (tm + 2.0 * t * (1.0 - col2.x)) * (1.0 - col1.x),
        col1.y < 0.5 ? col1.y * (tm + 2.0 * t * col2.y) : 1.0 - (tm + 2.0 * t * (1.0 - col2.y)) * (1.0 - col1.y),
        col1.z < 0.5 ? col1.z * (tm + 2.0 * t * col2.z) : 1.0 - (tm + 2.0 * t * (1.0 - col2.z)) * (1.0 - col1.z));
}

float3 cultmath_cycles_mix_sub(float t, float3 col1, float3 col2) { return col1 + t * ((col1 - col2) - col1); }

float3 cultmath_cycles_mix_div(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    return float3(
        col2.x != 0.0 ? tm * col1.x + t * col1.x / col2.x : col1.x,
        col2.y != 0.0 ? tm * col1.y + t * col1.y / col2.y : col1.y,
        col2.z != 0.0 ? tm * col1.z + t * col1.z / col2.z : col1.z);
}

float3 cultmath_cycles_mix_diff(float t, float3 col1, float3 col2) { return col1 + t * (abs(col1 - col2) - col1); }

float3 cultmath_cycles_mix_exclusion(float t, float3 col1, float3 col2)
{
    return max(col1 + t * ((col1 + col2 - 2.0 * col1 * col2) - col1), float3(0.0, 0.0, 0.0));
}

float3 cultmath_cycles_mix_dark(float t, float3 col1, float3 col2) { return col1 + t * (min(col1, col2) - col1); }

float3 cultmath_cycles_mix_light(float t, float3 col1, float3 col2) { return col1 + t * (max(col1, col2) - col1); }

float3 cultmath_cycles_mix_dodge(float t, float3 col1, float3 col2)
{
    float3 divisor = float3(1.0, 1.0, 1.0) - t * col2;
    return float3(
        col1.x != 0.0 ? (divisor.x <= 0.0 ? 1.0 : min(col1.x / divisor.x, 1.0)) : col1.x,
        col1.y != 0.0 ? (divisor.y <= 0.0 ? 1.0 : min(col1.y / divisor.y, 1.0)) : col1.y,
        col1.z != 0.0 ? (divisor.z <= 0.0 ? 1.0 : min(col1.z / divisor.z, 1.0)) : col1.z);
}

float3 cultmath_cycles_mix_burn(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    float3 divisor = tm + t * col2;
    float3 burnt = float3(1.0, 1.0, 1.0) - (float3(1.0, 1.0, 1.0) - col1) / divisor;
    return float3(
        divisor.x <= 0.0 ? 0.0 : clamp(burnt.x, 0.0, 1.0),
        divisor.y <= 0.0 ? 0.0 : clamp(burnt.y, 0.0, 1.0),
        divisor.z <= 0.0 ? 0.0 : clamp(burnt.z, 0.0, 1.0));
}

// rgb_to_hsv of util/color.h
float3 cultmath_cycles_rgb_to_hsv(float3 rgb)
{
    float cmax = max(rgb.x, max(rgb.y, rgb.z));
    float cmin = min(rgb.x, min(rgb.y, rgb.z));
    float cdelta = cmax - cmin;
    float s = cmax != 0.0 ? cdelta / cmax : 0.0;
    float3 c = (float3(cmax, cmax, cmax) - rgb) / cdelta;
    float h = rgb.x == cmax ? c.z - c.y : (rgb.y == cmax ? 2.0 + c.x - c.z : 4.0 + c.y - c.x);
    h = h * 0.16666667; // Cycles' kernel is built with reciprocal math: h /= 6.0f computes h * (1 / 6.0f).
    h = h < 0.0 ? h + 1.0 : h;
    return float3(s != 0.0 ? h : 0.0, s, cmax);
}

// hsv_to_rgb of util/color.h
float3 cultmath_cycles_hsv_to_rgb(float3 hsv)
{
    float h = hsv.x == 1.0 ? 0.0 : hsv.x;
    h = h * 6.0;
    float i = floor(h);
    float f = h - i;
    float p = hsv.z * (1.0 - hsv.y);
    float q = hsv.z * (1.0 - (hsv.y * f));
    float t = hsv.z * (1.0 - (hsv.y * (1.0 - f)));
    float3 rgb = i == 0.0 ? float3(hsv.z, t, p) :
        (i == 1.0 ? float3(q, hsv.z, p) :
        (i == 2.0 ? float3(p, hsv.z, t) :
        (i == 3.0 ? float3(p, q, hsv.z) :
        (i == 4.0 ? float3(t, p, hsv.z) : float3(hsv.z, p, q)))));
    return hsv.y != 0.0 ? rgb : float3(hsv.z, hsv.z, hsv.z);
}

float3 cultmath_cycles_mix_hue(float t, float3 col1, float3 col2)
{
    float3 hsv2 = cultmath_cycles_rgb_to_hsv(col2);
    float3 hsv = cultmath_cycles_rgb_to_hsv(col1);
    float3 shifted = cultmath_cycles_hsv_to_rgb(float3(hsv2.x, hsv.y, hsv.z));
    return hsv2.y != 0.0 ? col1 + t * (shifted - col1) : col1;
}

float3 cultmath_cycles_mix_sat(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    float3 hsv = cultmath_cycles_rgb_to_hsv(col1);
    float3 hsv2 = cultmath_cycles_rgb_to_hsv(col2);
    return hsv.y != 0.0 ? cultmath_cycles_hsv_to_rgb(float3(hsv.x, tm * hsv.y + t * hsv2.y, hsv.z)) : col1;
}

float3 cultmath_cycles_mix_val(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    float3 hsv = cultmath_cycles_rgb_to_hsv(col1);
    float3 hsv2 = cultmath_cycles_rgb_to_hsv(col2);
    return cultmath_cycles_hsv_to_rgb(float3(hsv.x, hsv.y, tm * hsv.z + t * hsv2.z));
}

float3 cultmath_cycles_mix_color(float t, float3 col1, float3 col2)
{
    float3 hsv2 = cultmath_cycles_rgb_to_hsv(col2);
    float3 hsv = cultmath_cycles_rgb_to_hsv(col1);
    float3 shifted = cultmath_cycles_hsv_to_rgb(float3(hsv2.x, hsv2.y, hsv.z));
    return hsv2.y != 0.0 ? col1 + t * (shifted - col1) : col1;
}

float3 cultmath_cycles_mix_soft(float t, float3 col1, float3 col2)
{
    float tm = 1.0 - t;
    float3 one = float3(1.0, 1.0, 1.0);
    float3 scr = one - (one - col2) * (one - col1);
    return tm * col1 + t * ((one - col1) * col2 * col1 + col1 * scr);
}

float3 cultmath_cycles_mix_linear(float t, float3 col1, float3 col2)
{
    return col1 + t * (2.0 * col2 + float3(-1.0, -1.0, -1.0));
}

// ---- RGB to BW node (kernel/svm/convert.h NODE_CONVERT_CF) ----

float cultmath_cycles_rgb_to_bw(float3 color)
{
    return color.x * CULTMATH_CYCLES_RGB_TO_Y_R + color.y * CULTMATH_CYCLES_RGB_TO_Y_G + color.z * CULTMATH_CYCLES_RGB_TO_Y_B;
}

// ---- Color Ramp lookup (kernel/svm/ramp.h rgb_ramp_lookup, without extrapolation) ----

// Where a ramp of `size` table entries is read at t: (index, weight), the lower entry and the weight of the
// next one. The colour is table[index] when weight is 0 and (1 - weight) table[index] + weight table[index + 1]
// otherwise. The ramp table itself is the caller's; Blender's holds 257 entries, the ramp sampled at i / 256.
float2 cultmath_cycles_ramp_index(float t, int size)
{
    float f = (t > 0.0 ? (t < 1.0 ? t : 1.0) : 0.0) * (float)(size - 1);
    int index = clamp((int)f, 0, size - 1);
    float lower = (float)index;
    return float2(lower, f - lower);
}

#endif
