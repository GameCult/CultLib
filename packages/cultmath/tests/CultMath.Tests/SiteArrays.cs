using CultMath;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The shader's site-array functions as C# twins. A shader function cannot take a span, so
/// <c>cultmath_power_border</c>, <c>cultmath_lattice_cell_distance</c> and <c>cultmath_weighted_lattice_cells</c> take
/// <c>float4 sites[CULTMATH_MAX_SITES]</c> and a count; reflection cannot pass a <see cref="ReadOnlySpan{T}"/>, so each
/// twin takes the array and the count and calls the span function on <c>sites[..count]</c>. This table is the one
/// place that maps them, read by the mirror test and by the GLSL fixture. The draws are the cases both run.
/// </summary>
internal static class SiteArrays
{
    /// <summary>The shader's default CULTMATH_MAX_SITES; the device families spell their array with it.</summary>
    internal const int Capacity = 128;

    internal static float4 power_border(float3 p, float4[] sites, int count) => math.power_border(p, sites.AsSpan(0, count));

    internal static float lattice_cell_distance(
        float2 q, float2 cell, float time, float drift, float4[] bodies, int count, float cellSize, float boost, float falloff,
        float2 normal, float stretch) =>
        math.lattice_cell_distance(q, cell, time, drift, bodies.AsSpan(0, count), cellSize, boost, falloff, normal, stretch);

    internal static float4 weighted_lattice_cells(
        float2 p, float time, float4[] bodies, int count, float cellSize, float drift, float boost, float falloff,
        float smoothing, float2 stretchNormal, float stretch) =>
        math.weighted_lattice_cells(p, time, bodies.AsSpan(0, count), cellSize, drift, boost, falloff, smoothing, stretchNormal, stretch);

    // The array the shader receives: the sites, then filler a loop running past the count would read. The filler is
    // finite and large, so a loop bound that ignores the count changes the result.
    private static float4[] Padded(IReadOnlyList<float4> sites)
    {
        var array = new float4[Capacity];
        for (var i = 0; i < Capacity; i++)
            array[i] = i < sites.Count ? sites[i] : new float4(40.0f * (i + 1), -30.0f * (i + 1), 7.0f, 90.0f + i);
        return array;
    }

    private static float Uniform(System.Random r, float lo, float hi) => lo + (hi - lo) * r.NextSingle();

    // Counts 0, 1, 6, 16 and the capacity are drawn often; the rest of [0, Capacity] fills between.
    private static int Count(System.Random r) => r.Next(3) == 0 ? r.Next(Capacity + 1) : new[] { 0, 1, 6, 16, Capacity }[r.Next(5)];

    // Bodies on slow orbits (float4(position.xy, 0, sigma)), spread over the plane the lattice cells cover.
    private static float4[] Orbit(System.Random r, int count, float time)
    {
        var bodies = new List<float4>();
        for (var i = 0; i < count; i++)
        {
            var angle = 0.001f * (i + 1) * time + i;
            var radius = 100.0f + 2400.0f * r.NextSingle();
            bodies.Add(new float4(radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0.0f, Uniform(r, 20.0f, 300.0f)));
        }

        return Padded(bodies);
    }

    internal static object[] PowerBorderArguments(System.Random r)
    {
        var count = Count(r);
        var sites = new List<float4>();
        for (var i = 0; i < count; i++)
            sites.Add(new float4(Uniform(r, -1500.0f, 1500.0f), Uniform(r, -1500.0f, 1500.0f), Uniform(r, -200.0f, 200.0f), Uniform(r, 10.0f, 300.0f)));
        return new object[] { new float3(Uniform(r, -1500.0f, 1500.0f), Uniform(r, -1500.0f, 1500.0f), Uniform(r, -200.0f, 200.0f)), Padded(sites), count };
    }

    internal static object[] LatticeCellDistanceArguments(System.Random r)
    {
        var angle = Uniform(r, 0.0f, 6.2831855f);
        var look = LatticeCellsTests.Looks[r.Next(LatticeCellsTests.Looks.Length)];
        var cell = new float2(r.Next(-300, 300), r.Next(-300, 300));
        var count = Count(r);
        return new object[]
        {
            new float2(Uniform(r, -1500.0f, 1500.0f), Uniform(r, -1500.0f, 1500.0f)) / LatticeCellsTests.Cell, cell, Uniform(r, 0.0f, 200.0f), LatticeCellsTests.Drift,
            Orbit(r, count, Uniform(r, 0.0f, 200.0f)), count, LatticeCellsTests.Cell, look.Boost, look.Falloff, new float2(MathF.Cos(angle), MathF.Sin(angle)), look.Stretch,
        };
    }

    // The unit-less normals the function normalises itself: ordinary, 3-4-5, tiny, huge, and zero (stretch 1 only).
    private static readonly float2[] Normals = { LatticeCellsTests.Normal, new(3.0f, 4.0f), new(3.0e-30f, -4.0e-30f), new(1.0e30f, 2.0e30f), new(0.0f, 0.0f) };

    internal static readonly LatticeCellsTests.Look WidestLook = new(LatticeMaxBoost, LatticeMaxStretch, LatticeMaxSmoothing, 1.0e6f);

    /// <summary>
    /// Points whose ring search runs to ring 8 or past it under the widest look (boost, stretch and smoothing at their
    /// maxima, falloff so wide that every cell is boosted), found once by the radius the exit rule visits, and the
    /// ring-17 point of LatticeCellsTests.TheCapIsReachedAndMatchesTheFullBox first. Each is (p, time).
    /// </summary>
    internal static readonly IReadOnlyList<(float2 P, float Time, int Ring)> FarPoints = FindFarPoints();

    private static List<(float2, float, int)> FindFarPoints()
    {
        var found = new List<(float2, float, int)>();
        var cap = (new float2(1446.7345f, -680.7637f), 97.69269f);
        found.Add((cap.Item1, cap.Item2, LatticeCellsTests.Radius(cap.Item1, cap.Item2, LatticeCellsTests.Bodies(cap.Item2), WidestLook, LatticeCellsTests.Rules[0].stop, new Dictionary<(int, int), float>())));
        var r = new System.Random(0x1A77);
        while (found.Count < 12)
        {
            var p = LatticeCellsTests.RandomPoint(r);
            var time = 13.0f * r.Next(20);
            var ring = LatticeCellsTests.Radius(p, time, LatticeCellsTests.Bodies(time), WidestLook, LatticeCellsTests.Rules[0].stop, new Dictionary<(int, int), float>());
            if (ring >= 8)
                found.Add((p, time, ring));
        }

        return found;
    }

    internal static object[] LatticeArguments(System.Random r)
    {
        if (r.Next(3) == 0)
        {
            var far = FarPoints[r.Next(FarPoints.Count)];
            return Lattice(far.P, far.Time, Padded(LatticeCellsTests.Bodies(far.Time)), 6, WidestLook, LatticeCellsTests.Normal);
        }

        var time = 13.0f * r.Next(20);
        var normal = Normals[r.Next(Normals.Length)];
        var look = normal.x == 0.0f ? LatticeCellsTests.Looks[0] : LatticeCellsTests.Looks[r.Next(LatticeCellsTests.Looks.Length)];
        var count = Count(r);
        return Lattice(LatticeCellsTests.RandomPoint(r), time, Orbit(r, count, time), count, look, normal);
    }

    private static object[] Lattice(float2 p, float time, float4[] bodies, int count, LatticeCellsTests.Look look, float2 normal) =>
        new object[] { p, time, bodies, count, LatticeCellsTests.Cell, LatticeCellsTests.Drift,
            look.Boost, look.Falloff, look.Smoothing, normal, look.Stretch };
}
