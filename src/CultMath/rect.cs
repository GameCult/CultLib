namespace CultMath;

/// <summary>
/// Axis-aligned 2D bounds stored canonically as normalized minimum and maximum
/// corners. Point containment and intersection include the boundary.
/// </summary>
public readonly record struct rect
{
    public rect(float minX, float minY, float maxX, float maxY)
        : this(new float2(minX, minY), new float2(maxX, maxY))
    {
    }

    public rect(float2 min, float2 max)
    {
        this.min = new float2(MathF.Min(min.x, max.x), MathF.Min(min.y, max.y));
        this.max = new float2(MathF.Max(min.x, max.x), MathF.Max(min.y, max.y));
    }

    public float2 min { get; }
    public float2 max { get; }

    public float width => max.x - min.x;
    public float height => max.y - min.y;
    public float2 size => max - min;
    public float2 center => (min + max) * 0.5f;
    public float area => width * height;
    public bool isEmpty => width == 0.0f || height == 0.0f;

    public bool Contains(float2 point) =>
        point.x >= min.x && point.x <= max.x &&
        point.y >= min.y && point.y <= max.y;

    public bool Contains(rect other) =>
        other.min.x >= min.x && other.max.x <= max.x &&
        other.min.y >= min.y && other.max.y <= max.y;

    public bool Intersects(rect other) =>
        min.x <= other.max.x && max.x >= other.min.x &&
        min.y <= other.max.y && max.y >= other.min.y;

    public rect Intersection(rect other)
    {
        if (!Intersects(other))
            return new rect(0.0f, 0.0f, 0.0f, 0.0f);

        return new rect(
            MathF.Max(min.x, other.min.x),
            MathF.Max(min.y, other.min.y),
            MathF.Min(max.x, other.max.x),
            MathF.Min(max.y, other.max.y));
    }

    public rect Encapsulate(float2 point) => new(
        MathF.Min(min.x, point.x),
        MathF.Min(min.y, point.y),
        MathF.Max(max.x, point.x),
        MathF.Max(max.y, point.y));
}
