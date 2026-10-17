using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The weighted lattice cell function and its public steps. The oracle and the double reference are written here
/// from the public steps and the spec formula; the ring search is never the reference for itself.
/// </summary>
public sealed class LatticeCellsTests
{
    private const float Cell = 260.0f;
    private const float Falloff = 120.0f;
    private const float Drift = 0.7f;
    private static readonly float2 Normal = new(0.6f, 0.8f);

    private sealed record Look(float Boost, float Stretch, float Smoothing, float Falloff = LatticeCellsTests.Falloff);

    private static readonly Look[] Looks =
    {
        new(1.6f, 1.0f, 0.35f), new(1.6f, 2.0f, 0.35f), new(LatticeMaxBoost, LatticeMaxStretch, LatticeMaxSmoothing), new(0.0f, 1.0f, 0.05f),
    };

    private static float4[] Bodies(float t)
    {
        var bodies = new float4[6];
        for (var i = 0; i < bodies.Length; i++)
        {
            var angle = 0.001f * (i + 1) * t + i;
            var radius = 300.0f + 220.0f * i;
            bodies[i] = new float4(radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0.0f, 40.0f + 40.0f * i);
        }
        return bodies;
    }

    private static float CellWeight(float2 cell, float time, float4[] bodies, Look look)
    {
        var site = lattice_site(cell, time, Drift);
        var border = power_border(new float3((cell + 0.5f) * Cell, 0.0f), bodies).w;
        return lattice_weight(site.z, border, look.Boost, look.Falloff);
    }

    // Folds F1, F2, id and smooth over the box of the given radius in absolute row-major order.
    private static float4 BoxFold(float2 p, float time, float4[] bodies, Look look, int radius,
        Dictionary<(int, int), float> weights, float2? unitNormal = null)
    {
        var q = p / Cell;
        var c0 = floor(q);
        var cells = new List<(float2 cell, float d)>();
        for (var dz = -radius; dz <= radius; dz++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                var cell = c0 + new float2(dx, dz);
                var key = ((int)cell.x, (int)cell.y);
                if (!weights.TryGetValue(key, out var w))
                    weights[key] = w = CellWeight(cell, time, bodies, look);
                var site = lattice_site(cell, time, Drift);
                cells.Add((cell, lattice_distance(q, site.xy, w, unitNormal ?? Normal, look.Stretch)));
            }
        float f1 = 1.0e30f, f2 = 1.0e30f;
        var win = default(float2);
        foreach (var (cell, d) in cells)
        {
            if (d < f1) { f2 = f1; f1 = d; win = cell; }
            else if (d < f2) f2 = d;
        }
        var m = f1 + look.Smoothing;
        foreach (var (_, d) in cells)
            m = lattice_smooth_step(m, d, f1, look.Smoothing);
        var id = pcg4d(new int4((int)win.x, (int)win.y, 0, 0));
        return new float4(f1, f2, m, cellular_unit(id.w));
    }

    private static float4 Call(float2 p, float time, float4[] bodies, Look look, float2? normal = null) =>
        weighted_lattice_cells(p, time, bodies, Cell, Drift, look.Boost, look.Falloff, look.Smoothing, normal ?? Normal, look.Stretch);

    private static float2 RandomPoint(System.Random r) =>
        new((r.NextSingle() * 2.0f - 1.0f) * 1500.0f, (r.NextSingle() * 2.0f - 1.0f) * 1500.0f);

    [Fact]
    public void RingSearchEqualsTheFullBoxOracle()
    {
        var r = new System.Random(41);
        foreach (var look in Looks)
            for (var batch = 0; batch < 20; batch++)
            {
                var time = 13.0f * batch;
                var bodies = Bodies(time);
                var weights = new Dictionary<(int, int), float>();
                for (var i = 0; i < 1000; i++)
                {
                    var p = RandomPoint(r);
                    var expected = BoxFold(p, time, bodies, look, 12, weights);
                    var actual = Call(p, time, bodies, look);
                    Assert.Equal(expected, actual);
                }
            }
    }

    private delegate bool Stop(int k, float f1, float f2, Look look);

    private static readonly (string name, Stop stop)[] Rules =
    {
        ("correct", (k, f1, f2, l) => k > 1 && 0.5f * (k - 1) >= max(f2, f1 + 2.0f * l.Smoothing)),
        ("cutoff F1 + k", (k, f1, f2, l) => k > 1 && 0.5f * (k - 1) >= max(f2, f1 + l.Smoothing)),
        ("bound 0.5*k", (k, f1, f2, l) => k > 0 && 0.5f * k >= max(f2, f1 + 2.0f * l.Smoothing)),
        ("greatest weight", (k, f1, f2, l) => k > 1 && (1.0f + l.Boost) * (k - 1) >= max(f2, f1 + 2.0f * l.Smoothing)),
        ("F1 only", (k, f1, f2, l) => k > 1 && 0.5f * (k - 1) >= f1 + 2.0f * l.Smoothing),
    };

    // The radius of the box a search with the given exit rule visits (rings 0 to k - 1 when it stops before ring k).
    private static int Radius(float2 p, float time, float4[] bodies, Look look, Stop stop,
        Dictionary<(int, int), float> weights)
    {
        var q = p / Cell;
        var c0 = floor(q);
        float f1 = 1.0e30f, f2 = 1.0e30f;
        for (var k = 0; k <= LatticeMaxRing; k++)
        {
            if (stop(k, f1, f2, look))
                return k - 1;
            for (var dz = -k; dz <= k; dz++)
                for (var dx = -k; dx <= k; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != k)
                        continue;
                    var cell = c0 + new float2(dx, dz);
                    var key = ((int)cell.x, (int)cell.y);
                    if (!weights.TryGetValue(key, out var w))
                        weights[key] = w = CellWeight(cell, time, bodies, look);
                    var d = lattice_distance(q, lattice_site(cell, time, Drift).xy, w, Normal, look.Stretch);
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
        }
        return LatticeMaxRing;
    }

    [Fact]
    public void ConstructedPointsRealizeTheExitBound()
    {
        var failed = new Dictionary<string, int>();
        var r = new System.Random(7);
        foreach (var look in new[] { Looks[2], Looks[3] })
        for (var batch = 0; batch < 20; batch++)
        {
            var time = 29.0f * batch;
            var bodies = Bodies(time);
            var weights = new Dictionary<(int, int), float>();
            for (var i = 0; i < 1500; i++)
            {
                var p = RandomPoint(r);
                var expected = BoxFold(p, time, bodies, look, 12, weights);
                foreach (var (name, stop) in Rules)
                {
                    var got = BoxFold(p, time, bodies, look, Math.Max(0, Radius(p, time, bodies, look, stop, weights)), weights);
                    if (!got.Equals(expected))
                        failed[name] = failed.GetValueOrDefault(name) + 1;
                }
            }
        }
        Assert.False(failed.ContainsKey("correct"));
        foreach (var (name, _) in Rules.Skip(1))
            Assert.True(failed.GetValueOrDefault(name) > 0, name + " is not caught by any point");
    }

    [Fact]
    public void SmoothIgnoresCellsBeyondTheBand()
    {
        var r = new System.Random(3);
        for (var i = 0; i < 20000; i++)
        {
            var k = 0.05f + 0.95f * r.NextSingle();
            var f1 = 8.0f * r.NextSingle();
            var m = f1 + k - (r.NextSingle() < 0.5f ? 0.0f : 2.0f * k * r.NextSingle());
            var far = f1 + 2.0f * k + 5.0f * r.NextSingle();
            Assert.Equal(m, lattice_smooth_step(m, far, f1, k));
            var near = f1 + 2.0f * k * r.NextSingle();
            Assert.True(lattice_smooth_step(m, near, f1, k) <= f1 + k);
        }
        Assert.NotEqual(1.0f, lattice_smooth_step(1.0f + 0.35f, 1.0f + 0.35f, 1.0f, 0.35f));
    }

    private static float2 Dir(System.Random r)
    {
        var a = r.NextSingle() * TAU;
        return new float2(MathF.Cos(a), MathF.Sin(a));
    }

    [Fact]
    public void ContinuousAcrossCellBoundariesAndBorders()
    {
        var look = Looks[1];
        var bodies = Bodies(11.0f);
        var r = new System.Random(5);
        var worst = 0.0f;
        for (var line = 0; line < 6; line++)
        {
            var a = RandomPoint(r);
            var dir = Dir(r);
            const int steps = 4000;
            var length = 6.0f * Cell;
            var prev = Call(a, 11.0f, bodies, look);
            for (var s = 1; s <= steps; s++)
            {
                var cur = Call(a + dir * (length * s / steps), 11.0f, bodies, look);
                var step = length / steps / Cell;
                // A weighted distance changes at most weight * stretch per cell unit of travel.
                Assert.True(abs(cur.z - prev.z) < 12.0f * step + 1e-3f);
                Assert.True(abs(cur.x - prev.x) < 12.0f * step + 1e-5f * 12.0f);
                worst = max(worst, abs((cur.x - cur.z) - (prev.x - prev.z)));
                prev = cur;
            }
        }
        Assert.True(worst < 12.0f * 6.0f / 4000 * 2.0f + 1e-3f);
        // Straddling a lattice cell boundary by 1e-3 of a cell.
        for (var i = 0; i < 300; i++)
        {
            var p = RandomPoint(r);
            var edge = new float2(MathF.Round(p.x / Cell) * Cell, p.y);
            var lo = Call(edge - new float2(1e-3f * Cell, 0.0f), 11.0f, bodies, look);
            var hi = Call(edge + new float2(1e-3f * Cell, 0.0f), 11.0f, bodies, look);
            Assert.True(abs(lo.x - hi.x) < 1e-3f * 24.0f);
            Assert.True(abs(lo.y - hi.y) < 1e-3f * 24.0f);
            Assert.True(abs(lo.z - hi.z) < 1e-3f * 24.0f);
            Assert.True(abs((lo.x - lo.z) - (hi.x - hi.z)) < 1e-3f * 24.0f);
        }
    }

    [Fact]
    public void WeightsAreContinuousAsBodiesOrbit()
    {
        var look = Looks[2];
        var cells = Enumerable.Range(0, 576).Select(i => new float2(i % 24 - 12, i / 24 - 12)).ToArray();
        var prev = new float[cells.Length];
        for (var s = 0; s <= 300 * 60; s++)
        {
            var bodies = Bodies(s / 60.0f);
            for (var i = 0; i < cells.Length; i++)
            {
                var site = lattice_site(cells[i], 0.0f, Drift);
                var border = power_border(new float3((cells[i] + 0.5f) * Cell, 0.0f), bodies).w;
                var w = lattice_weight(site.z, border, look.Boost, Falloff) / site.z;
                if (s > 0)
                    Assert.True(abs(w - prev[i]) <= 0.01f * look.Boost, $"cell {cells[i]} at {s}: {prev[i]} -> {w}");
                prev[i] = w;
            }
        }
    }

    [Fact]
    public void F1F2AndSmoothAreContinuousThroughWinnerSwaps()
    {
        var r = new System.Random(19);
        var bodies = Bodies(11.0f);
        var swaps = 0;
        foreach (var look in new[] { Looks[1], Looks[2] })
            for (var line = 0; line < 6; line++)
            {
                var a = RandomPoint(r);
                var b = RandomPoint(r);
                float2 At(float t) => a + (b - a) * t;
                const int Steps = 4000;
                for (var s = 1; s <= Steps; s++)
                {
                    float lo = (s - 1) / (float)Steps, hi = s / (float)Steps;
                    if (Call(At(lo), 11.0f, bodies, look).w == Call(At(hi), 11.0f, bodies, look).w)
                        continue;
                    swaps++;
                    for (var i = 0; i < 40; i++)
                    {
                        var mid = 0.5f * (lo + hi);
                        if (Call(At(mid), 11.0f, bodies, look).w == Call(At(lo), 11.0f, bodies, look).w) lo = mid; else hi = mid;
                    }
                    var before = Call(At(lo), 11.0f, bodies, look);
                    var after = Call(At(hi), 11.0f, bodies, look);
                    var bound = 12.0f * length(At(hi) - At(lo)) / Cell + 1e-5f * 12.0f;
                    Assert.True(abs(before.x - after.x) < bound, $"F1 {before.x} -> {after.x}");
                    Assert.True(abs(before.y - after.y) < bound, $"F2 {before.y} -> {after.y}");
                    Assert.True(abs(before.z - after.z) < bound, $"smooth {before.z} -> {after.z}");
                }
            }
        Assert.True(swaps >= 20, $"only {swaps} winner swaps crossed");
    }

    [Fact]
    public void TheCapIsReachedAndMatchesTheFullBox()
    {
        // A cell whose every neighbour is boosted far and stretched needs ring 17, past the cap a smaller bound would set.
        var look = new Look(LatticeMaxBoost, LatticeMaxStretch, LatticeMaxSmoothing, 1.0e6f);
        var p = new float2(1446.7345f, -680.7637f);
        const float Time = 97.69269f;
        var bodies = Bodies(Time);
        var weights = new Dictionary<(int, int), float>();
        var ring = Radius(p, Time, bodies, look, Rules[0].stop, weights);
        Assert.InRange(ring, 13, LatticeMaxRing);
        Assert.Equal(BoxFold(p, Time, bodies, look, LatticeMaxRing, weights), Call(p, Time, bodies, look));
        // The cap is the greatest ring the range's worst case can need: the exit holds once 0.5 (k - 1) reaches the
        // larger of the F2 and F1 + 2 smoothing bounds, which are (1 + boost) stretch sqrt(3.25) and (1 + boost) stretch sqrt(2) + 2 smoothing.
        var f2Bound = (1.0f + LatticeMaxBoost) * LatticeMaxStretch * MathF.Sqrt(3.25f);
        var bandBound = (1.0f + LatticeMaxBoost) * LatticeMaxStretch * MathF.Sqrt(2.0f) + 2.0f * LatticeMaxSmoothing;
        Assert.Equal((int)MathF.Ceiling(2.0f * max(f2Bound, bandBound)), LatticeMaxRing);
    }

    [Fact]
    public void AnExactTieChangesOnlyTheIdToTheEarlierRowMajorCell()
    {
        // Cells sharing a base weight and, at these coordinates, a rounded site: a query on that site is at distance 0 from both.
        foreach (var (earlier, time) in new[] { (new float2(6003486, 3996 + 6000000), 0.0f), (new float2(6007572, 2713 + 6000000), 4.07f) })
        {
            var later = earlier + new float2(0, 1);
            var siteA = lattice_site(earlier, time, 1.0f);
            Assert.Equal(siteA, lattice_site(later, time, 1.0f));
            var got = weighted_lattice_cells(siteA.xy, time, Array.Empty<float4>(), 1.0f, 1.0f, 0.0f, 1.0f, 0.01f, Normal, 1.0f);
            var idOf = (float2 c) => cellular_unit(pcg4d(new int4((int)c.x, (int)c.y, 0, 0)).w);
            Assert.NotEqual(idOf(earlier), idOf(later));
            Assert.Equal(new float4(0.0f, 0.0f, got.z, idOf(earlier)), got);
        }
    }

    private static void Refuses(string parameter, Action call) =>
        Assert.Equal(parameter, Assert.Throws<ArgumentException>(call).ParamName);

    [Fact]
    public void RefusesOutsideTheRange()
    {
        var b = Bodies(0.0f);
        var p = new float2(10.0f, 20.0f);
        var n = Normal;
        float4 W(float2? pp = null, float time = 0, float cell = Cell, float drift = Drift, float boost = 1.6f, float falloff = Falloff,
            float smoothing = 0.35f, float2? normal = null, float stretch = 1.5f, float4[]? bodies = null) =>
            weighted_lattice_cells(pp ?? p, time, bodies ?? b, cell, drift, boost, falloff, smoothing, normal ?? n, stretch);

        W(boost: 0.0f); W(boost: LatticeMaxBoost); W(stretch: 1.0f); W(stretch: LatticeMaxStretch);
        W(smoothing: LatticeMaxSmoothing); W(normal: float2.zero, stretch: 1.0f); W(normal: new float2(1.0e-45f, 0.0f)); W(bodies: Array.Empty<float4>());
        Refuses("p", () => W(pp: new float2(float.NaN, 0)));
        Refuses("cellSize", () => W(cell: 0.0f));
        Refuses("cellSize", () => W(cell: float.PositiveInfinity));
        Refuses("cellSize", () => W(cell: 1.0e37f));
        Refuses("p", () => W(pp: new float2(3.0e38f, 0)));
        Refuses("p", () => W(pp: new float2(0, 1.0e10f)));
        Refuses("time", () => W(time: float.PositiveInfinity));
        Refuses("drift", () => W(drift: float.NaN));
        Refuses("drift", () => W(time: 3.0e38f, drift: 2.0f));
        W(time: 1.0e30f, drift: 1.0e-30f);
        Refuses("boost", () => W(boost: -0.01f));
        Refuses("boost", () => W(boost: LatticeMaxBoost + 0.01f));
        Refuses("falloff", () => W(falloff: 0.0f));
        Refuses("falloff", () => W(falloff: float.NaN));
        Refuses("smoothing", () => W(smoothing: 0.0f));
        Refuses("smoothing", () => W(smoothing: LatticeMaxSmoothing + 0.01f));
        Refuses("stretch", () => W(stretch: 0.99f));
        Refuses("stretch", () => W(stretch: LatticeMaxStretch + 0.01f));
        Refuses("stretchNormal", () => W(normal: float2.zero));
        Refuses("stretchNormal", () => W(normal: new float2(float.NaN, 1)));
        Refuses("bodies", () => W(bodies: new[] { new float4(float.NaN, 0, 0, 1) }));
        Refuses("bodies", () => W(bodies: new[] { new float4(0, 0, 0, float.PositiveInfinity) }));
    }

    [Fact]
    public void RingsStayWellInsideTheCap()
    {
        var r = new System.Random(41);
        var rings = new List<int>();
        foreach (var look in Looks)
            for (var batch = 0; batch < 20; batch++)
            {
                var time = 13.0f * batch;
                var bodies = Bodies(time);
                var weights = new Dictionary<(int, int), float>();
                for (var i = 0; i < 300; i++)
                    rings.Add(Radius(RandomPoint(r), time, bodies, look, Rules[0].stop, weights));
            }
        Assert.True(rings.Max() <= LatticeMaxRing - 2, $"greatest ring {rings.Max()}");
        rings.Sort();
        Assert.Equal((3, 7, 8), (rings[rings.Count / 2], rings[rings.Count * 99 / 100], rings[^1]));
    }


    [Fact]
    public void StretchNormalIsNormalised()
    {
        var bodies = Bodies(3.0f);
        var r = new System.Random(8);
        for (var i = 0; i < 50; i++)
        {
            var p = RandomPoint(r);
            Assert.Equal(Call(p, 3.0f, bodies, Looks[1]), Call(p, 3.0f, bodies, Looks[1], Normal * 7.0f));
        }
    }

    [Fact]
    public void ANormalOfAnyMagnitudeActsAsItsDirection()
    {
        // A tiny normal once normalised to length 0.944 and a huge one to zero; both then missed a winning cell.
        var none = Array.Empty<float4>();
        var look = new Look(0.0f, 1.0f, 0.01f);
        var invSqrt2 = new float2(0.70710677f, 0.70710677f);
        var cases = new (float2 given, float2 unit)[]
        {
            (new float2(3.0e-23f, 4.0e-23f), Normal), (new float2(6.0e29f, 8.0e29f), Normal),
            (new float2(1.0e30f, 1.0e30f), invSqrt2), (new float2(1.0e-44f, 1.0e-44f), invSqrt2), (new float2(3.0e38f, 3.0e38f), invSqrt2),
        };
        var weights = new Dictionary<(int, int), float>();
        foreach (var (given, unit) in cases)
        {
            var miss = new float2(-1040.0f, -1481.7773f);
            Assert.Equal(BoxFold(miss, 54.725235f, none, look, 12, weights, unit), Call(miss, 54.725235f, none, look, given));
            var r = new System.Random(3);
            for (var i = 0; i < 300; i++)
            {
                var p = RandomPoint(r);
                Assert.Equal(BoxFold(p, 7.0f, none, Looks[1], 12, weights, unit), Call(p, 7.0f, none, Looks[1], given));
            }
        }
    }

    [Fact]
    public void MatchesTheProbeFormula()
    {
        var look = Looks[1];
        var r = new System.Random(2);
        var bodies = Bodies(5.0f);
        for (var i = 0; i < 2000; i++)
        {
            var p = RandomPoint(r);
            var got = Call(p, 5.0f, bodies, look);
            var nrm = (x: (double)Normal.x, y: (double)Normal.y);
            var q = (x: (double)p.x / Cell, y: (double)p.y / Cell);
            var ds = new List<double>();
            for (var cz = (int)Math.Floor(q.y) - 8; cz <= (int)Math.Floor(q.y) + 8; cz++)
                for (var cx = (int)Math.Floor(q.x) - 8; cx <= (int)Math.Floor(q.x) + 8; cx++)
                {
                    var h = pcg3d(new int3(cx, cz, 0));
                    double u0 = cellular_unit(h.x), u1 = cellular_unit(h.y), u2 = cellular_unit(h.z);
                    var ph = 2 * Math.PI;
                    var sx = cx + 0.5 + 0.5 * Math.Sin(Drift * 5.0 + ph * u0);
                    var sy = cz + 0.5 + 0.5 * Math.Sin(Drift * 5.0 + ph * u1);
                    var border = power_border(new float3(((float)cx + 0.5f) * Cell, ((float)cz + 0.5f) * Cell, 0), bodies).w;
                    var weight = (0.5 + 0.5 * u2) * (1 + look.Boost * Math.Exp(-Math.Pow(border / (double)Falloff, 2)));
                    double vx = sx - q.x, vy = sy - q.y;
                    double a = vx * nrm.x + vy * nrm.y, c = vx * nrm.y - vy * nrm.x;
                    ds.Add(weight * Math.Sqrt(look.Stretch * look.Stretch * a * a + c * c));
                }
            var d = ds.OrderBy(x => x).ToArray();
            double m = d[0] + look.Smoothing;
            foreach (var dd in ds)
            {
                if (dd >= d[0] + 2 * look.Smoothing) continue;
                var h = Math.Clamp(0.5 + 0.5 * (m - dd) / look.Smoothing, 0, 1);
                h = h * h * (3 - 2 * h);
                m = Math.Min(m + (dd - m) * h - h * (1 - h) * look.Smoothing / (1 + 3 * look.Smoothing), d[0] + look.Smoothing);
            }
            Assert.InRange(got.x - d[0], -1e-4, 1e-4);
            Assert.InRange(got.y - d[1], -1e-4, 1e-4);
            Assert.InRange(got.z - m, -1e-4, 1e-4);
        }
    }
}
