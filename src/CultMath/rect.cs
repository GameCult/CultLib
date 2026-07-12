namespace CultMath;

public readonly record struct rect(float2 min, float2 max)
{
    public rect(float minX, float minY, float maxX, float maxY)
        : this(math.float2(math.min(minX, maxX), math.min(minY, maxY)),
            math.float2(math.max(minX, maxX), math.max(minY, maxY)))
    {
    }

    public float2 size => max - min;
    public float2 center => (min + max) * 0.5f;

    public bool Contains(float2 point)
    {
        return point.x >= min.x &&
               point.x <= max.x &&
               point.y >= min.y &&
               point.y <= max.y;
    }
}
