namespace CultMath;

public readonly record struct PlanetaryPageLayout(PlanetaryTileAddress Tile, int InteriorSize, int BorderSize)
{
    public int StorageSize => checked(InteriorSize + BorderSize * 2);

    public PlanetaryPageLayout Validate()
    {
        if (InteriorSize < 2) throw new ArgumentOutOfRangeException(nameof(InteriorSize));
        if (BorderSize < 0 || BorderSize >= InteriorSize) throw new ArgumentOutOfRangeException(nameof(BorderSize));
        return this;
    }
}

public readonly record struct PlanetaryPageSummary(
    float MinimumHeight,
    float MaximumHeight,
    float MaximumSlope,
    float UnresolvedHeightBound,
    float AngularTexelSize);

public readonly record struct PlanetaryPageSample(float Height, float3 TangentGradient, float Ridge, float Gully);

public static class PlanetaryPageSampling
{
    public static float3 Direction(PlanetaryPageLayout page, int storageX, int storageY)
    {
        page.Validate();
        if ((uint)storageX >= (uint)page.StorageSize || (uint)storageY >= (uint)page.StorageSize)
            throw new ArgumentOutOfRangeException(nameof(storageX));
        var localU = (storageX - page.BorderSize) / (double)(page.InteriorSize - 1);
        var localV = (storageY - page.BorderSize) / (double)(page.InteriorSize - 1);
        return DirectionAtLocal(page, localU, localV);
    }

    public static float3 DirectionAtLocal(PlanetaryPageLayout page, double localU, double localV)
    {
        page.Validate();
        if (!double.IsFinite(localU) || !double.IsFinite(localV)) throw new ArgumentOutOfRangeException(nameof(localU));
        var count = page.Tile.AxisTileCount;
        return PlanetaryTopology.Direction(new(
            page.Tile.Face,
            -1.0 + 2.0 * (page.Tile.X + localU) / count,
            -1.0 + 2.0 * (page.Tile.Y + localV) / count));
    }

    public static float AngularTexelSize(PlanetaryPageLayout page)
    {
        var center = page.BorderSize + (page.InteriorSize - 1) / 2;
        var a = Direction(page, center, center);
        var b = Direction(page, Math.Min(center + 1, page.StorageSize - 1), center);
        return MathF.Acos(math.clamp(math.dot(a, b), -1.0f, 1.0f));
    }

    public static float NominalAngularTexelSize(PlanetaryPageLayout page)
    {
        page.Validate();
        return MathF.PI * 0.5f / (page.Tile.AxisTileCount * (page.InteriorSize - 1));
    }

    public static PlanetaryPageSample Bilinear(
        ReadOnlySpan<PlanetaryPageSample> samples,
        PlanetaryPageLayout page,
        float2 local)
    {
        page.Validate();
        if (samples.Length != page.StorageSize * page.StorageSize) throw new ArgumentException("Page sample count does not match layout.", nameof(samples));
        var texel = local * (page.InteriorSize - 1) + page.BorderSize;
        var x0 = Math.Clamp((int)MathF.Floor(texel.x), 0, page.StorageSize - 1);
        var y0 = Math.Clamp((int)MathF.Floor(texel.y), 0, page.StorageSize - 1);
        var x1 = Math.Min(x0 + 1, page.StorageSize - 1);
        var y1 = Math.Min(y0 + 1, page.StorageSize - 1);
        var tx = math.frac(texel.x);
        var ty = math.frac(texel.y);
        var a = Lerp(samples[y0 * page.StorageSize + x0], samples[y0 * page.StorageSize + x1], tx);
        var b = Lerp(samples[y1 * page.StorageSize + x0], samples[y1 * page.StorageSize + x1], tx);
        return Lerp(a, b, ty);
    }

    public static PlanetaryPageSummary Summarize(ReadOnlySpan<PlanetaryPageSample> samples, PlanetaryPageLayout page, float unresolvedHeightBound)
    {
        page.Validate();
        if (samples.Length != page.StorageSize * page.StorageSize) throw new ArgumentException("Page sample count does not match layout.", nameof(samples));
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        var maxSlope = 0.0f;
        foreach (var sample in samples)
        {
            min = MathF.Min(min, sample.Height);
            max = MathF.Max(max, sample.Height);
            maxSlope = MathF.Max(maxSlope, math.length(sample.TangentGradient));
        }
        return new(min, max, maxSlope, MathF.Max(unresolvedHeightBound, 0), AngularTexelSize(page));
    }

    private static PlanetaryPageSample Lerp(PlanetaryPageSample a, PlanetaryPageSample b, float t) => new(
        math.lerp(a.Height, b.Height, t),
        math.lerp(a.TangentGradient, b.TangentGradient, t),
        math.lerp(a.Ridge, b.Ridge, t),
        math.lerp(a.Gully, b.Gully, t));
}

public readonly record struct PlanetaryBakedPage(
    PlanetaryPageLayout Layout,
    PlanetaryPageSample[] Samples,
    PlanetaryPageSummary Summary);

public static class PlanetaryPageBaker
{
    public static PlanetaryBakedPage Bake<TSource>(
        in PlanetaryFieldDefinition field,
        PlanetaryPageLayout layout,
        TSource source)
        where TSource : IPlanetaryBaseField
    {
        layout.Validate(); field.Validate();
        var spacing = PlanetaryPageSampling.NominalAngularTexelSize(layout) * field.Radius;
        var parentSpacing = layout.Tile.Level == 0
            ? 0
            : PlanetaryPageSampling.NominalAngularTexelSize(new(layout.Tile.Parent(), layout.InteriorSize, layout.BorderSize)) * field.Radius;
        var samples = new PlanetaryPageSample[layout.StorageSize * layout.StorageSize];
        var unresolved = 0.0f;
        for (var y = 0; y < layout.StorageSize; y++)
        for (var x = 0; x < layout.StorageSize; x++)
        {
            var direction = PlanetaryPageSampling.Direction(layout, x, y);
            var baseSample = source.Sample(direction);
            var child = PlanetaryField.Sample(field, direction, baseSample, PlanetaryQueryScale.AtFootprint(spacing));
            var height = child.RadialDisplacement;
            var gradient = child.TangentGradient;
            if (parentSpacing > 0)
            {
                var parent = PlanetaryField.Sample(field, direction, baseSample, PlanetaryQueryScale.AtFootprint(parentSpacing));
                height -= parent.RadialDisplacement;
                gradient -= parent.TangentGradient;
            }
            samples[y * layout.StorageSize + x] = new(height, gradient, child.Ridge, child.Gully);
            unresolved = MathF.Max(unresolved, child.UnresolvedHeightBound);
        }
        return new(layout, samples, PlanetaryPageSampling.Summarize(samples, layout, unresolved));
    }
}

public readonly record struct PlanetaryResidentPage(PlanetaryBakedPage Page, float Blend)
{
    public PlanetaryResidentPage Validate()
    {
        if (!float.IsFinite(Blend) || Blend is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(Blend));
        Page.Layout.Validate();
        if (Page.Samples.Length != Page.Layout.StorageSize * Page.Layout.StorageSize) throw new ArgumentException("Page payload does not match layout.");
        return this;
    }
}

public static class PlanetaryPageSetSampling
{
    public static bool TrySample(ReadOnlySpan<PlanetaryResidentPage> pages, float3 direction, out PlanetaryPageSample sample)
    {
        PlanetaryTopology.ValidateDirection(direction);
        var height = 0.0f;
        var gradient = float3.zero;
        var ridge = 0.0f;
        var gully = 0.0f;
        var found = false;
        var deepestLevel = -1;
        foreach (var resident in pages)
        {
            resident.Validate();
            if (!PlanetaryTopology.TryLocalCoordinate(direction, resident.Page.Layout.Tile, out var local)) continue;
            var contribution = PlanetaryPageSampling.Bilinear(resident.Page.Samples, resident.Page.Layout, local);
            var blend = math.saturate(resident.Blend);
            height += contribution.Height * blend;
            gradient += contribution.TangentGradient * blend;
            if (resident.Page.Layout.Tile.Level >= deepestLevel)
            {
                ridge = found ? math.lerp(ridge, contribution.Ridge, blend) : contribution.Ridge;
                gully = found ? math.lerp(gully, contribution.Gully, blend) : contribution.Gully;
                deepestLevel = resident.Page.Layout.Tile.Level;
            }
            found = true;
        }
        sample = new(height, gradient, ridge, gully);
        return found;
    }
}
