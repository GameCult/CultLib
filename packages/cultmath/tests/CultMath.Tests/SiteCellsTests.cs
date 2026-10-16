using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The site cells (power and multiplicative scores, the fold, the border), the Gabriel margin and gate, and the
/// out-free segment distance. Every reference is written here in double from the site data, never by calling the
/// function under test.
/// </summary>
public sealed class SiteCellsTests
{
    private static float4[] RandomSites(System.Random r, int max)
    {
        var count = r.Next(max + 1);
        var sites = new float4[count];
        for (var i = 0; i < count; i++)
        {
            sites[i] = i > 0 && r.Next(4) == 0
                ? sites[r.Next(i)]
                : new float4(Span(r, 10.0f), Span(r, 10.0f), Span(r, 10.0f), 0.5f + 2.5f * r.NextSingle());
        }

        return sites;
    }

    private static float Span(System.Random r, float extent) => (r.NextSingle() * 2.0f - 1.0f) * extent;

    private static float3 Point(System.Random r) => new(Span(r, 10.0f), Span(r, 10.0f), Span(r, 10.0f));

    private static double PowerReference(float3 p, float4 s)
    {
        double dx = (double)p.x - s.x, dy = (double)p.y - s.y, dz = (double)p.z - s.z;
        return dx * dx + dy * dy + dz * dz - (double)s.w * s.w;
    }

    private static double MultiplicativeReference(float3 p, float4 s)
    {
        double dx = (double)p.x - s.x, dy = (double)p.y - s.y, dz = (double)p.z - s.z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / s.w;
    }

    // Checks cells against the stable (score, index) order of a double reference. A pair whose reference scores
    // differ by less than a float's resolution may legitimately order either way, so such a pair is skipped; an
    // exact tie (a duplicated site) is compared.
    private static int CheckFold(CultSiteCells cells, double[] reference)
    {
        var order = Enumerable.Range(0, reference.Length).OrderBy(i => reference[i]).ThenBy(i => i).ToArray();
        if (order.Length < 2)
        {
            Assert.Equal(-1.0f, cells.secondSite);
            Assert.Equal(1.0e30f, cells.second.w);
        }

        if (order.Length < 1)
        {
            Assert.Equal(-1.0f, cells.nearestSite);
            Assert.Equal(1.0e30f, cells.nearest.w);
            return 0;
        }

        bool Clear(int a, int b) => reference[a] == reference[b] || Math.Abs(reference[b] - reference[a]) > 1.0e-4 * (1.0 + Math.Abs(reference[b]));
        if (order.Length >= 2 && !Clear(order[0], order[1]))
            return 0;
        Assert.Equal((float)order[0], cells.nearestSite);
        if (order.Length < 2 || (order.Length > 2 && !Clear(order[1], order[2])))
            return 1;
        Assert.Equal((float)order[1], cells.secondSite);
        return 1;
    }

    [Fact]
    public void PowerAndMultiplicativeCellsAreTheTwoSmallestScoresInIndexOrder()
    {
        var r = new System.Random(0x517E);
        var checkedSpans = 0;
        for (var n = 0; n < 4000; n++)
        {
            var sites = RandomSites(r, 12);
            var p = Point(r);
            checkedSpans += CheckFold(power_cells(p, sites), sites.Select(s => PowerReference(p, s)).ToArray());
            checkedSpans += CheckFold(multiplicative_cells(p, sites), sites.Select(s => MultiplicativeReference(p, s)).ToArray());
        }

        Assert.True(checkedSpans > 5000, $"only {checkedSpans} spans were comparable");
    }

    [Fact]
    public void ExactTiesKeepTheLowerIndexFirst()
    {
        var site = new float4(1.0f, 2.0f, 3.0f, 1.5f);
        var sites = new[] { site, site, site };
        var cells = power_cells(new float3(0.0f, 0.0f, 0.0f), sites);
        Assert.Equal(0.0f, cells.nearestSite);
        Assert.Equal(1.0f, cells.secondSite);
    }

    // TN2: for power scores F2 - F1 is affine, so site_cells_border is the distance to the bisector plane of the
    // two winners {x : |x - s1|^2 - w1^2 = |x - s2|^2 - w2^2}, computed here in double. That is not the distance
    // to the nearest site's cell boundary (PowerBorderIsTheLeastRadicalPlaneDistance).
    [Fact]
    public void SiteCellsBorderIsTheDistanceToTheWinnersBisector()
    {
        var r = new System.Random(0xB0DE);
        var compared = 0;
        for (var n = 0; n < 3000; n++)
        {
            var sites = RandomSites(r, 8);
            if (sites.Length < 3)
                continue;
            var p = Point(r);
            var reference = sites.Select(s => PowerReference(p, s)).ToArray();
            var order = Enumerable.Range(0, sites.Length).OrderBy(i => reference[i]).ThenBy(i => i).ToArray();
            if (reference[order[1]] - reference[order[0]] < 1.0e-2 || reference[order[2]] - reference[order[1]] < 1.0e-2)
                continue;
            var (a, b) = (sites[order[0]], sites[order[1]]);
            double nx = 2.0 * ((double)a.x - b.x), ny = 2.0 * ((double)a.y - b.y), nz = 2.0 * ((double)a.z - b.z);
            var norm = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (norm < 1.0e-2)
                continue;
            var expected = (reference[order[1]] - reference[order[0]]) / norm;

            var border = site_cells_border(power_cells(p, sites));
            Assert.InRange(border.w, expected - 1.0e-4 * expected - 1.0e-3, expected + 1.0e-4 * expected + 1.0e-3);
            // F2 - F1 grows toward the nearest site: the normal is (s1 - s2) / |s1 - s2|.
            Assert.InRange(border.x, nx / norm - 1.0e-3, nx / norm + 1.0e-3);
            Assert.InRange(border.y, ny / norm - 1.0e-3, ny / norm + 1.0e-3);
            Assert.InRange(border.z, nz / norm - 1.0e-3, nz / norm + 1.0e-3);
            compared++;
        }

        Assert.True(compared > 1000, $"only {compared} borders were comparable");
    }

    [Fact]
    public void BorderIsEmptyWithoutASecondSite()
    {
        var one = power_cells(new float3(1.0f, 2.0f, 3.0f), new[] { new float4(0.0f, 0.0f, 0.0f, 1.0f) });
        Assert.Equal(new float4(0.0f, 0.0f, 0.0f, 1.0e30f), site_cells_border(one));
        Assert.Equal(new float4(0.0f, 0.0f, 0.0f, 1.0e30f), site_cells_border(site_cells_empty()));
    }

    // Two sites with weights w1 != w2: the border |p - s1| / w1 = |p - s2| / w2 is the Apollonius circle, centre
    // (w1^2 s2 - w2^2 s1) / (w1^2 - w2^2), radius w1 w2 |s1 - s2| / |w1^2 - w2^2| (built in double). On it the
    // multiplicative border distance vanishes; off it the distance is nonzero.
    [Fact]
    public void MultiplicativeBorderVanishesOnTheApolloniusCircle()
    {
        var r = new System.Random(0xA9011);
        for (var n = 0; n < 300; n++)
        {
            var s1 = new float4(Span(r, 5.0f), Span(r, 5.0f), 0.0f, 1.0f + 2.0f * r.NextSingle());
            var s2 = new float4(Span(r, 5.0f), Span(r, 5.0f), 0.0f, 1.0f + 2.0f * r.NextSingle());
            double w1 = s1.w, w2 = s2.w, k = w1 * w1 - w2 * w2;
            double sep = Math.Sqrt(Math.Pow((double)s1.x - s2.x, 2) + Math.Pow((double)s1.y - s2.y, 2));
            if (Math.Abs(k) < 0.5 || sep < 1.0)
                continue;
            double cx = (w1 * w1 * s2.x - w2 * w2 * s1.x) / k, cy = (w1 * w1 * s2.y - w2 * w2 * s1.y) / k;
            var radius = w1 * w2 * sep / Math.Abs(k);
            if (radius > 50.0)
                continue;

            var sites = new[] { s1, s2 };
            var angle = r.NextDouble() * 2.0 * Math.PI;
            var on = new float3((float)(cx + radius * Math.Cos(angle)), (float)(cy + radius * Math.Sin(angle)), 0.0f);
            var onBorder = site_cells_border(multiplicative_cells(on, sites));
            Assert.True(Math.Abs(onBorder.w) < 1.0e-3 * radius + 1.0e-3, $"on the circle: {onBorder.w}");

            var off = new float3((float)(cx + 1.5 * radius * Math.Cos(angle)), (float)(cy + 1.5 * radius * Math.Sin(angle)), 0.0f);
            var offBorder = site_cells_border(multiplicative_cells(off, sites));
            Assert.True(Math.Abs(offBorder.w) > 1.0e-2 * radius, $"off the circle: {offBorder.w}");
        }
    }

    // Central differences in double of each score's value against the returned gradient.
    [Fact]
    public void ScoreGradientsAreAnalytic()
    {
        var r = new System.Random(0x6AAD);
        const double h = 1.0e-3;
        for (var n = 0; n < 500; n++)
        {
            var site = RandomSites(r, 1).FirstOrDefault(new float4(1.0f, -2.0f, 0.5f, 1.5f));
            var p = Point(r);
            if (Math.Abs(p.x - site.x) + Math.Abs(p.y - site.y) + Math.Abs(p.z - site.z) < 1.0f)
                continue;
            foreach (var (score, reference) in new (Func<float3, float4, float4>, Func<float3, float4, double>)[] { (power_score, PowerReference), (multiplicative_score, MultiplicativeReference) })
            {
                var got = score(p, site);
                var axes = new[] { new float3((float)h, 0.0f, 0.0f), new float3(0.0f, (float)h, 0.0f), new float3(0.0f, 0.0f, (float)h) };
                var expected = axes.Select(a => (reference(p + a, site) - reference(p - a, site)) / (2.0 * h)).ToArray();
                var scale = 1.0 + Math.Abs(expected[0]) + Math.Abs(expected[1]) + Math.Abs(expected[2]);
                Assert.True(Math.Abs(got.x - expected[0]) < 2.0e-3 * scale, "x");
                Assert.True(Math.Abs(got.y - expected[1]) < 2.0e-3 * scale, "y");
                Assert.True(Math.Abs(got.z - expected[2]) < 2.0e-3 * scale, "z");
                Assert.Equal(reference(p, site), got.w, 1.0e-3 * (1.0 + Math.Abs(got.w)));
            }
        }
    }

    [Fact]
    public void MultiplicativeGradientIsZeroAtTheSite()
    {
        var site = new float4(1.0f, 2.0f, 3.0f, 2.0f);
        var score = multiplicative_score(new float3(1.0f, 2.0f, 3.0f), site);
        Assert.Equal(new float4(0.0f, 0.0f, 0.0f, 0.0f), score);
    }

    private static double MarginReference(float4 a, float4 b, float4 o)
    {
        double mx = ((double)a.x + b.x) / 2, my = ((double)a.y + b.y) / 2, mz = ((double)a.z + b.z) / 2;
        var rr = Math.Sqrt(Math.Pow((double)a.x - b.x, 2) + Math.Pow((double)a.y - b.y, 2) + Math.Pow((double)a.z - b.z, 2)) / 2;
        var d = Math.Sqrt(Math.Pow(o.x - mx, 2) + Math.Pow(o.y - my, 2) + Math.Pow(o.z - mz, 2));
        return (d - rr) / rr;
    }

    private static double SmoothstepReference(double lo, double hi, double x)
    {
        var t = Math.Clamp((x - lo) / (hi - lo), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    [Fact]
    public void SoftGabrielGateFollowsTheEmptyDiscMargin()
    {
        var a = new float4(-2.0f, 0.0f, 0.0f, 1.0f);
        var b = new float4(2.0f, 0.0f, 0.0f, 1.0f);
        // On the diameter circle (radius 2 about the origin): margin 0, gate 0.5. At half radius: margin -0.5, gate 0.
        Assert.Equal(0.5f, soft_gabriel_gate(new[] { a, b, new float4(0.0f, 2.0f, 0.0f, 1.0f) }, 0, 1, 0.15f), 6);
        Assert.Equal(0.0f, soft_gabriel_gate(new[] { a, b, new float4(0.0f, 1.0f, 0.0f, 1.0f) }, 0, 1, 0.15f));
        Assert.Equal(1.0f, soft_gabriel_gate(new[] { a, b }, 0, 1, 0.15f));

        var r = new System.Random(0x6ABE);
        for (var n = 0; n < 500; n++)
        {
            var sites = RandomSites(r, 9);
            if (sites.Length < 3 || sites[0].Equals(sites[1]))
                continue;
            var first = r.Next(sites.Length);
            var second = (first + 1 + r.Next(sites.Length - 1)) % sites.Length;
            var softness = 0.05f + 0.5f * r.NextSingle();
            var margin = Enumerable.Range(0, sites.Length).Where(k => k != first && k != second).Min(k => MarginReference(sites[first], sites[second], sites[k]));
            if (!double.IsFinite(margin))
                continue;
            Assert.Equal(SmoothstepReference(-softness, softness, margin), soft_gabriel_gate(sites, first, second, softness), 1.0e-3);
        }

        // Continuity: a tiny radial step of a third site moves the gate by a small share of the softness band.
        var prev = soft_gabriel_gate(new[] { a, b, new float4(0.0f, 1.8f, 0.0f, 1.0f) }, 0, 1, 0.3f);
        for (var y = 1.8f; y < 2.2f; y += 0.001f)
        {
            var gate = soft_gabriel_gate(new[] { a, b, new float4(0.0f, y, 0.0f, 1.0f) }, 0, 1, 0.3f);
            Assert.True(Math.Abs(gate - prev) < 0.01f, $"step at y={y}");
            prev = gate;
        }
    }

    [Fact]
    public void SpanFunctionsRefuseBadInputsWithoutEchoingThem()
    {
        var good = new float4(0.0f, 0.0f, 0.0f, 1.0f);
        var marker = 12345.678f;
        foreach (var weight in new[] { 0.0f, -marker, float.NaN, float.PositiveInfinity })
        {
            var e = Assert.Throws<ArgumentException>(() => multiplicative_cells(default, new[] { good, new float4(1.0f, 1.0f, 1.0f, weight) }));
            Assert.Equal("sites", e.ParamName);
            Assert.DoesNotContain("12345", e.Message);
        }

        foreach (var weight in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Equal("sites", Assert.Throws<ArgumentException>(() => power_cells(default, new[] { good, new float4(1.0f, 1.0f, 1.0f, weight) })).ParamName);

        var three = new[] { good, new float4(1.0f, 0.0f, 0.0f, 1.0f), new float4(0.0f, 1.0f, 0.0f, 1.0f) };
        Assert.Equal("second", Assert.Throws<ArgumentException>(() => soft_gabriel_gate(three, 1, 1, 0.1f)).ParamName);
        Assert.Equal("first", Assert.Throws<ArgumentOutOfRangeException>(() => soft_gabriel_gate(three, -1, 1, 0.1f)).ParamName);
        Assert.Equal("second", Assert.Throws<ArgumentOutOfRangeException>(() => soft_gabriel_gate(three, 0, 3, 0.1f)).ParamName);
        foreach (var softness in new[] { 0.0f, -marker, float.NaN, float.PositiveInfinity })
        {
            var e = Assert.Throws<ArgumentException>(() => soft_gabriel_gate(three, 0, 1, softness));
            Assert.Equal("softness", e.ParamName);
            Assert.DoesNotContain("12345", e.Message);
        }
    }

    [Fact]
    public void OutFreeDistanceToSegmentEqualsTheOutForm()
    {
        var r = new System.Random(0x5E61);
        for (var n = 0; n < 2000; n++)
        {
            var p = new float2(Span(r, 5.0f), Span(r, 5.0f));
            var start = new float2(Span(r, 5.0f), Span(r, 5.0f));
            var end = r.Next(8) == 0 ? start : new float2(Span(r, 5.0f), Span(r, 5.0f));
            var outForm = distance_to_segment(p, start, end, out var closest);
            Assert.Equal(BitConverter.SingleToInt32Bits(outForm), BitConverter.SingleToInt32Bits(distance_to_segment(p, start, end)));
            Assert.Equal(distance(p, closest), outForm);
            double sx = (double)end.x - start.x, sy = (double)end.y - start.y, qx = (double)p.x - start.x, qy = (double)p.y - start.y;
            var t2 = sx * sx + sy * sy > 0.0 ? Math.Clamp((qx * sx + qy * sy) / (sx * sx + sy * sy), 0.0, 1.0) : 0.0;
            var expected2 = Math.Sqrt(Math.Pow(qx - t2 * sx, 2) + Math.Pow(qy - t2 * sy, 2));
            Assert.Equal(expected2, outForm, 1.0e-4 * (1.0 + expected2));

            var p3 = new float3(Span(r, 5.0f), Span(r, 5.0f), Span(r, 5.0f));
            var a3 = new float3(Span(r, 5.0f), Span(r, 5.0f), Span(r, 5.0f));
            var b3 = r.Next(8) == 0 ? a3 : new float3(Span(r, 5.0f), Span(r, 5.0f), Span(r, 5.0f));
            var got = distance_to_segment(p3, a3, b3);
            if (a3.Equals(b3))
            {
                Assert.Equal(distance(p3, a3), got);
                continue;
            }

            double ex = (double)b3.x - a3.x, ey = (double)b3.y - a3.y, ez = (double)b3.z - a3.z;
            double px = (double)p3.x - a3.x, py = (double)p3.y - a3.y, pz = (double)p3.z - a3.z;
            var t = Math.Clamp((px * ex + py * ey + pz * ez) / (ex * ex + ey * ey + ez * ez), 0.0, 1.0);
            var expected = Math.Sqrt(Math.Pow(px - t * ex, 2) + Math.Pow(py - t * ey, 2) + Math.Pow(pz - t * ez, 2));
            Assert.Equal(expected, got, 1.0e-4 * (1.0 + expected));
        }
    }

    // The cell of the owner is the intersection of the half-spaces of its radical planes. Reference in double,
    // written here: x.d = c with d = s2 - s1, c = (|s2|^2 - |s1|^2 + w1^2 - w2^2) / 2, signed distance (c - p.d) / |d|.
    private static (double Distance, double[] Normal) CellBoundary(float3 p, float4[] sites, int owner)
    {
        var best = double.PositiveInfinity;
        var normal = new double[3];
        var s1 = new[] { (double)sites[owner].x, sites[owner].y, sites[owner].z };
        var q = new[] { (double)p.x, p.y, p.z };
        for (var k = 0; k < sites.Length; k++)
        {
            var s2 = new[] { (double)sites[k].x, sites[k].y, sites[k].z };
            var d = new[] { s2[0] - s1[0], s2[1] - s1[1], s2[2] - s1[2] };
            var dd = d[0] * d[0] + d[1] * d[1] + d[2] * d[2];
            if (k == owner || dd == 0.0)
                continue;
            double w1 = sites[owner].w, w2 = sites[k].w;
            var c = (s2.Sum(v => v * v) - s1.Sum(v => v * v) + w1 * w1 - w2 * w2) / 2.0;
            var distance = (c - (q[0] * d[0] + q[1] * d[1] + q[2] * d[2])) / Math.Sqrt(dd);
            if (distance < best)
            {
                best = distance;
                normal = new[] { -d[0] / Math.Sqrt(dd), -d[1] / Math.Sqrt(dd), -d[2] / Math.Sqrt(dd) };
            }
        }

        return (best, normal);
    }

    // Spans with duplicated sites, zero weights, collinear sites and single-site spans.
    private static float4[] AwkwardSites(System.Random r)
    {
        var count = 1 + r.Next(9);
        var collinear = r.Next(5) == 0;
        var sites = new float4[count];
        for (var i = 0; i < count; i++)
        {
            var weight = r.Next(5) == 0 ? 0.0f : 0.5f + 2.5f * r.NextSingle();
            sites[i] = i > 0 && r.Next(4) == 0 ? sites[r.Next(i)]
                : collinear ? new float4(Span(r, 10.0f), 0.0f, 0.0f, weight)
                : new float4(Span(r, 10.0f), Span(r, 10.0f), Span(r, 10.0f), weight);
        }

        return sites;
    }

    [Fact]
    public void PowerBorderIsTheLeastRadicalPlaneDistance()
    {
        var r = new System.Random(0xCE11);
        var finite = 0;
        var unbounded = 0;
        for (var n = 0; n < 6000; n++)
        {
            var sites = AwkwardSites(r);
            var p = Point(r);
            var reference = sites.Select(s => PowerReference(p, s)).ToArray();
            var order = Enumerable.Range(0, sites.Length).OrderBy(i => reference[i]).ThenBy(i => i).ToArray();
            if (order.Length > 1 && reference[order[1]] - reference[order[0]] < 1.0e-2 && reference[order[1]] != reference[order[0]])
                continue;
            var (distance, normal) = CellBoundary(p, sites, order[0]);
            var border = power_border(p, sites);
            if (double.IsPositiveInfinity(distance))
            {
                Assert.Equal(1.0e30f, border.w);
                unbounded++;
                continue;
            }

            Assert.InRange(border.w, distance - 1.0e-4 * Math.Abs(distance) - 1.0e-4, distance + 1.0e-4 * Math.Abs(distance) + 1.0e-4);
            finite++;
            // The normal is the minimising plane's, pointing into the cell, when that plane is clear of the rest.
            var others = Enumerable.Range(0, sites.Length).Where(k => k != order[0]).Select(k => CellBoundary(p, new[] { sites[order[0]], sites[k] }, 0).Distance).Where(v => !double.IsPositiveInfinity(v)).OrderBy(v => v).ToArray();
            if (others.Length > 1 && others[1] - others[0] > 1.0e-2)
            {
                Assert.InRange(border.x, normal[0] - 1.0e-3, normal[0] + 1.0e-3);
                Assert.InRange(border.y, normal[1] - 1.0e-3, normal[1] + 1.0e-3);
                Assert.InRange(border.z, normal[2] - 1.0e-3, normal[2] + 1.0e-3);
            }
        }

        Assert.True(finite > 2500 && unbounded > 50, $"{finite} bounded and {unbounded} unbounded cells were compared");
    }

    [Fact]
    public void ADuplicateOfTheOwnerLeavesTheCellBorderToTheOtherSites()
    {
        var a = new float4(0.0f, 0.0f, 0.0f, 1.0f);
        var far = new float4(10.0f, 0.0f, 0.0f, 1.0f);
        // p is nearer the owner's duplicate than anything else: the duplicate is the second-nearest site.
        var border = power_border(new float3(1.0f, 0.0f, 0.0f), new[] { a, a, far });
        Assert.Equal(4.0f, border.w, 1.0e-4f);
        Assert.Equal(-1.0f, border.x, 1.0e-5f);
        // Only coincident sites: the cell has no bounding plane.
        Assert.Equal(1.0e30f, power_border(new float3(1.0f, 0.0f, 0.0f), new[] { a, a }).w);
        Assert.Equal(1.0e30f, power_border(new float3(1.0f, 0.0f, 0.0f), new[] { a }).w);
        Assert.Equal(1.0e30f, power_border(new float3(1.0f, 0.0f, 0.0f), ReadOnlySpan<float4>.Empty).w);
    }

    // Close sites seen from a far query: the plane distance must not cancel (the old gradient-difference form
    // had 1.5e-4 relative error at this geometry and 3.5e-3 at coordinates near 1e4).
    [Theory]
    [InlineData(0.0f)]
    [InlineData(10000.0f)]
    public void PowerBorderKeepsItsPrecisionForCloseSitesAndAFarQuery(float shift)
    {
        var sites = new[] { new float4(shift - 1.453f, shift + 1.986f, 0.0f, 13.085f), new float4(shift - 1.539f, shift + 2.103f, 0.0f, 5.113f) };
        var p = new float3(shift - 154.2f, shift + 188.9f, 0.0f);
        var expected = CellBoundary(p, sites, (int)power_cells(p, sites).nearestSite).Distance;
        Assert.True(expected > 100.0);
        Assert.InRange(power_border(p, sites).w, expected * (1.0 - 1.0e-5), expected * (1.0 + 1.0e-5));
    }

    [Fact]
    public void CoincidentGabrielPairHasMarginMinusOne()
    {
        var a = new float3(1.0f, 2.0f, 3.0f);
        foreach (var other in new[] { a, new float3(0.0f, 0.0f, 0.0f), new float3(9.0f, -4.0f, 2.0f) })
            Assert.Equal(-1.0f, gabriel_margin(a, a, other));

        var pair = new[] { new float4(a, 1.0f), new float4(a, 1.0f), new float4(5.0f, 5.0f, 5.0f, 1.0f) };
        Assert.Equal(0.0f, soft_gabriel_gate(pair, 0, 1, 0.15f));
        // smoothstep(-3, 3, -1) = 3t^2 - 2t^3 at t = 1/3.
        Assert.Equal(7.0 / 27.0, soft_gabriel_gate(pair, 0, 1, 3.0f), 1.0e-5);
        Assert.Equal(1.0f, soft_gabriel_gate(pair[..2], 0, 1, 0.15f));
    }

    [Fact]
    public void CellSpansRefuseNonFinitePositionsAndQueriesAndOverlongSpans()
    {
        var good = new float4(0.0f, 0.0f, 0.0f, 1.0f);
        var origin = new float3(0.0f, 0.0f, 0.0f);
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var site = new[] { new float4(bad, 0.0f, 0.0f, 1.0f), good, new float4(2.0f, 0.0f, 0.0f, 1.0f) };
            Assert.Equal("sites", Assert.Throws<ArgumentException>(() => power_cells(origin, site)).ParamName);
            Assert.Equal("sites", Assert.Throws<ArgumentException>(() => multiplicative_cells(origin, site)).ParamName);
            Assert.Equal("sites", Assert.Throws<ArgumentException>(() => power_border(origin, site)).ParamName);
            Assert.Equal("sites", Assert.Throws<ArgumentException>(() => soft_gabriel_gate(site, 1, 2, 0.1f)).ParamName);
            var query = new float3(0.0f, bad, 0.0f);
            Assert.Equal("p", Assert.Throws<ArgumentException>(() => power_cells(query, new[] { good })).ParamName);
            Assert.Equal("p", Assert.Throws<ArgumentException>(() => multiplicative_cells(query, new[] { good })).ParamName);
            Assert.Equal("p", Assert.Throws<ArgumentException>(() => power_border(query, new[] { good })).ParamName);
        }

        // Indices are floats, exact only to 2^24: a longer span is refused before any site is read.
        var one = new[] { good };
        Assert.Equal("sites", Assert.Throws<ArgumentException>(() => power_cells(origin, System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref one[0], (1 << 24) + 1))).ParamName);
        Assert.Equal("sites", Assert.Throws<ArgumentException>(() => multiplicative_cells(origin, System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref one[0], (1 << 24) + 1))).ParamName);
    }

    [Fact]
    public void SegmentClosestIsTheClampedFootOfThePerpendicular()
    {
        var start = new float3(0.0f, 0.0f, 0.0f);
        var end = new float3(4.0f, 0.0f, 0.0f);
        Assert.Equal(new float3(2.0f, 0.0f, 0.0f), segment_closest(new float3(2.0f, 3.0f, 1.0f), start, end));
        Assert.Equal(end, segment_closest(new float3(9.0f, 3.0f, 1.0f), start, end));
        Assert.Equal(start, segment_closest(new float3(-9.0f, 3.0f, 1.0f), start, end));
        Assert.Equal(start, segment_closest(new float3(2.0f, 3.0f, 1.0f), start, start));
        Assert.Equal(new float2(4.0f, 0.0f), segment_closest(new float2(9.0f, 3.0f), new float2(0.0f, 0.0f), new float2(4.0f, 0.0f)));
    }

    [Fact]
    public void PowerBorderStepLeavesTheBorderAloneForASiteCoincidentWithTheOwner()
    {
        var owner = new float4(1.0f, 2.0f, 3.0f, 1.0f);
        var heavier = new float4(1.0f, 2.0f, 3.0f, 2.0f);
        var running = new float4(1.0f, 0.0f, 0.0f, 5.0f);
        foreach (var other in new[] { owner, heavier })
            foreach (var border in new[] { running, new float4(0.0f, 0.0f, 0.0f, 1.0e30f) })
            {
                var result = power_border_step(new float3(4.0f, 4.0f, 4.0f), owner, other, border);
                Assert.Equal(border.x, result.x);
                Assert.Equal(border.w, result.w);
            }
    }
}
