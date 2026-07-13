namespace CultMath;

public readonly record struct PlanetaryRadialBounds(float MinimumDisplacement, float MaximumDisplacement)
{
    public PlanetaryRadialBounds Validate()
    {
        if (!float.IsFinite(MinimumDisplacement) || !float.IsFinite(MaximumDisplacement) || MinimumDisplacement > MaximumDisplacement)
            throw new ArgumentOutOfRangeException(nameof(MinimumDisplacement));
        return this;
    }
}

public readonly record struct PlanetaryRayHit(
    double Distance,
    double3 PlanetLocalPosition,
    PlanetarySurfaceSample Surface);

public readonly record struct PlanetaryPathSample(double Distance, PlanetarySurfaceSample Surface);

public readonly record struct PlanetaryRegionSummary(
    float MinimumDisplacement,
    float MaximumDisplacement,
    float MaximumSlope,
    float MaximumUnresolvedHeight);

public static class PlanetaryQueries
{
    public static bool TryIntersectRay<TSource>(
        in PlanetaryFieldDefinition field,
        double3 rayOrigin,
        double3 rayDirection,
        double maximumDistance,
        in PlanetaryQueryScale scale,
        in PlanetaryRadialBounds bounds,
        TSource source,
        out PlanetaryRayHit hit)
        where TSource : IPlanetaryBaseField
    {
        field.Validate(); scale.Validate(); bounds.Validate();
        ValidateFinite(rayOrigin, nameof(rayOrigin)); ValidateFinite(rayDirection, nameof(rayDirection));
        if (!double.IsFinite(maximumDistance) || maximumDistance <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        var directionLength = Length(rayDirection);
        if (directionLength < 1.0e-15) throw new ArgumentOutOfRangeException(nameof(rayDirection));
        var direction = rayDirection / directionLength;
        var outerRadius = field.Radius + bounds.MaximumDisplacement + scale.MaximumUnresolvedHeight;
        if (!double.IsFinite(outerRadius)) outerRadius = field.Radius + bounds.MaximumDisplacement;
        if (!TrySphereInterval(rayOrigin, direction, outerRadius, out var enter, out var leave)) { hit = default; return false; }
        var start = Math.Max(enter, 0);
        var end = Math.Min(leave, maximumDistance);
        if (start > end) { hit = default; return false; }

        var previousDistance = start;
        var previous = SignedSurfaceDistance(field, rayOrigin + direction * previousDistance, scale, source, out var previousSample);
        if (previous <= 0)
        {
            hit = new(previousDistance, rayOrigin + direction * previousDistance, previousSample);
            return true;
        }
        var minimumStep = Math.Max(scale.FootprintMeters * 0.5, field.Radius * 1.0e-7);
        for (var iteration = 0; iteration < 512 && previousDistance < end; iteration++)
        {
            var nextDistance = Math.Min(previousDistance + Math.Max(previous * 0.5, minimumStep), end);
            var next = SignedSurfaceDistance(field, rayOrigin + direction * nextDistance, scale, source, out var nextSample);
            if (next <= 0)
            {
                var low = previousDistance;
                var high = nextDistance;
                var surface = nextSample;
                for (var refine = 0; refine < 32; refine++)
                {
                    var middle = (low + high) * 0.5;
                    var value = SignedSurfaceDistance(field, rayOrigin + direction * middle, scale, source, out var middleSample);
                    if (value > 0) low = middle;
                    else { high = middle; surface = middleSample; }
                }
                var position = rayOrigin + direction * high;
                hit = new(high, position, surface);
                return true;
            }
            if (nextDistance >= end) break;
            previousDistance = nextDistance;
            previous = next;
            previousSample = nextSample;
        }
        hit = default;
        return false;
    }

    public static double SegmentClearance<TSource>(
        in PlanetaryFieldDefinition field,
        double3 start,
        double3 end,
        double sampleSpacing,
        in PlanetaryQueryScale scale,
        TSource source)
        where TSource : IPlanetaryBaseField
    {
        ValidateFinite(start, nameof(start)); ValidateFinite(end, nameof(end));
        if (!double.IsFinite(sampleSpacing) || sampleSpacing <= 0) throw new ArgumentOutOfRangeException(nameof(sampleSpacing));
        var delta = end - start;
        var distance = Length(delta);
        var steps = Math.Max((int)Math.Ceiling(distance / sampleSpacing), 1);
        var minimum = double.PositiveInfinity;
        for (var i = 0; i <= steps; i++)
        {
            var position = start + delta * (i / (double)steps);
            minimum = Math.Min(minimum, SignedSurfaceDistance(field, position, scale, source, out _));
        }
        return minimum;
    }

    public static PlanetaryPathSample[] SampleGreatCircle<TSource>(
        in PlanetaryFieldDefinition field,
        float3 startDirection,
        float3 endDirection,
        int segmentCount,
        in PlanetaryQueryScale scale,
        TSource source)
        where TSource : IPlanetaryBaseField
    {
        if (segmentCount < 1) throw new ArgumentOutOfRangeException(nameof(segmentCount));
        PlanetaryTopology.ValidateDirection(startDirection); PlanetaryTopology.ValidateDirection(endDirection);
        var a = math.normalize(startDirection); var b = math.normalize(endDirection);
        var angle = MathF.Acos(math.clamp(math.dot(a, b), -1, 1));
        var sinAngle = MathF.Sin(angle);
        var result = new PlanetaryPathSample[segmentCount + 1];
        for (var i = 0; i <= segmentCount; i++)
        {
            var t = i / (float)segmentCount;
            var direction = sinAngle < 1.0e-6f
                ? math.normalize(math.lerp(a, b, t))
                : math.normalize(a * (MathF.Sin((1 - t) * angle) / sinAngle) + b * (MathF.Sin(t * angle) / sinAngle));
            var sample = PlanetaryField.Sample(field, direction, source.Sample(direction), scale);
            result[i] = new(angle * field.Radius * t, sample);
        }
        return result;
    }

    public static PlanetaryRegionSummary Summarize<TSource>(
        in PlanetaryFieldDefinition field,
        ReadOnlySpan<float3> directions,
        in PlanetaryQueryScale scale,
        TSource source)
        where TSource : IPlanetaryBaseField
    {
        if (directions.IsEmpty) throw new ArgumentException("At least one direction is required.", nameof(directions));
        var min = float.PositiveInfinity; var max = float.NegativeInfinity; var slope = 0.0f; var unresolved = 0.0f;
        foreach (var direction in directions)
        {
            var normalized = math.normalize(direction);
            var sample = PlanetaryField.Sample(field, normalized, source.Sample(normalized), scale);
            min = MathF.Min(min, sample.RadialDisplacement);
            max = MathF.Max(max, sample.RadialDisplacement);
            slope = MathF.Max(slope, sample.Slope);
            unresolved = MathF.Max(unresolved, sample.UnresolvedHeightBound);
        }
        return new(min, max, slope, unresolved);
    }

    private static double SignedSurfaceDistance<TSource>(
        in PlanetaryFieldDefinition field,
        double3 position,
        in PlanetaryQueryScale scale,
        TSource source,
        out PlanetarySurfaceSample sample)
        where TSource : IPlanetaryBaseField
    {
        var radius = Length(position);
        if (radius < 1.0e-15) { sample = default; return double.NegativeInfinity; }
        var direction = new float3((float)(position.x / radius), (float)(position.y / radius), (float)(position.z / radius));
        sample = PlanetaryField.Sample(field, direction, source.Sample(direction), scale);
        return radius - sample.SurfaceRadius;
    }

    private static bool TrySphereInterval(double3 origin, double3 direction, double radius, out double enter, out double leave)
    {
        var b = Dot(origin, direction);
        var c = Dot(origin, origin) - radius * radius;
        var discriminant = b * b - c;
        if (discriminant < 0) { enter = leave = 0; return false; }
        var root = Math.Sqrt(discriminant);
        enter = -b - root; leave = -b + root; return leave >= 0;
    }

    private static double Dot(double3 a, double3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    private static double Length(double3 value) => Math.Sqrt(Dot(value, value));
    private static void ValidateFinite(double3 value, string name)
    {
        if (!double.IsFinite(value.x) || !double.IsFinite(value.y) || !double.IsFinite(value.z)) throw new ArgumentOutOfRangeException(name);
    }
}
