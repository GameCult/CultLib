namespace CultMath;

/// <summary>
/// The result of <see cref="math.span_fbm"/> and <see cref="math.span_ridged"/> (invariant 8's one struct
/// return shape): an octave stage's value and gradient at the midpoint of a span, with the bounds that
/// hold over the whole span.
/// </summary>
public struct SpanNoise
{
    /// <summary>(&#8711;, value): the stage's gradient in the span's space and its value, in the <c>float4(gradient.xyz, value.w)</c> layout of a bare value-and-gradient return.</summary>
    public float4 field;

    /// <summary>An upper bound, at every point the span can reach, on the operator norm of the field's Hessian in the span's space.</summary>
    public float curvature;

    /// <summary>
    /// An upper bound on how far a ridged sum can leave its midpoint branch over the span (each octave's
    /// 1 - |n| against the same octave continued with the midpoint's sign of n); 0 for
    /// <see cref="math.span_fbm"/> and for any octave that cannot cross zero over the span.
    /// </summary>
    public float fold;

    public SpanNoise(float4 field, float curvature, float fold)
    {
        this.field = field;
        this.curvature = curvature;
        this.fold = fold;
    }
}
