namespace CultMath;

/// <summary>
/// The transform of one octave stage of a warp chain, as <see cref="math.span_fbm"/> and
/// <see cref="math.span_ridged"/> read it. Octave o samples snoise at x_o = f_o (M q + shift) + offset +
/// o octaveStep, with f_o = lacunarity^o and M = <see cref="frequency"/> (I + (1/<see cref="stretch"/> - 1) a a^T),
/// a = normalize(<see cref="axis"/>); a zero axis, or a stretch of 1, means no stretch. stretch must be
/// positive and finite, and nothing guards it. <c>default(SpanStage)</c> has frequency 0, which samples
/// one point: start from <see cref="Identity"/>.
/// </summary>
public struct SpanStage
{
    /// <summary>The stage whose span_fbm/span_ridged field equals fbm_grad/ridged_grad: frequency 1, stretch 1, every vector zero.</summary>
    public static readonly SpanStage Identity = new SpanStage { frequency = 1.0f, stretch = 1.0f };

    /// <summary>The scale of M; its sign is kept.</summary>
    public float frequency;

    /// <summary>The factor the axis direction is stretched by (M scales it by 1/stretch); 1 is none.</summary>
    public float stretch;

    /// <summary>The direction stretch applies along; any length, zero for none.</summary>
    public float3 axis;

    /// <summary>Added to M q before the per-octave scaling.</summary>
    public float3 shift;

    /// <summary>Added after the per-octave scaling.</summary>
    public float3 offset;

    /// <summary>Added to the octave coordinate once per octave index: o octaveStep.</summary>
    public float3 octaveStep;
}
