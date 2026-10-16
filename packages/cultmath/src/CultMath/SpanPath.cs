namespace CultMath;

/// <summary>
/// The span <see cref="math.span_fbm"/> and <see cref="math.span_ridged"/> bound over: the path q(t),
/// |t| &lt;= <see cref="halfLength"/>, through <see cref="midpoint"/> at t = 0 with <see cref="velocity"/> there,
/// |q'| &lt;= <see cref="speed"/> and |q''| &lt;= <see cref="acceleration"/> along it. speed is at least
/// |velocity|; speed, acceleration and halfLength are non-negative. Name every field: the three floats
/// have no order a caller could mistake for a convention.
/// </summary>
public struct SpanPath
{
    /// <summary>q(0), the point the field and its gradient are evaluated at.</summary>
    public float3 midpoint;

    /// <summary>q'(0).</summary>
    public float3 velocity;

    /// <summary>An upper bound on |q'(t)| over the span; at least |velocity|.</summary>
    public float speed;

    /// <summary>An upper bound on |q''(t)| over the span.</summary>
    public float acceleration;

    /// <summary>The span covers |t| &lt;= halfLength; 0 is the midpoint alone.</summary>
    public float halfLength;
}
