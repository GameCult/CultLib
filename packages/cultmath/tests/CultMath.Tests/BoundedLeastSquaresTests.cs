using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

// Fixtures are deliberately non-degenerate: A is non-square and non-identity, and b is outside A's range,
// so the unconstrained optimum has a real residual and the box actually bites.
public sealed class BoundedLeastSquaresTests
{
    private static double Cost(int m, int n, float[] a, float[] b, float[] x)
    {
        var c = 0.0;
        for (var r = 0; r < m; r++)
        {
            var s = -(double)b[r];
            for (var j = 0; j < n; j++) s += (double)a[r * n + j] * x[j];
            c += s * s;
        }
        return c;
    }

    // Gradient of 0.5*||Ax-b||^2, in double.
    private static double[] Gradient(int m, int n, float[] a, float[] b, float[] x)
    {
        var g = new double[n];
        for (var r = 0; r < m; r++)
        {
            var s = -(double)b[r];
            for (var j = 0; j < n; j++) s += (double)a[r * n + j] * x[j];
            for (var j = 0; j < n; j++) g[j] += a[r * n + j] * s;
        }
        return g;
    }

    private static double AtbScale(int m, int n, float[] a, float[] b)
    {
        var mx = 0.0;
        for (var j = 0; j < n; j++)
        {
            var s = 0.0;
            for (var r = 0; r < m; r++) s += (double)a[r * n + j] * b[r];
            mx = Math.Max(mx, Math.Abs(s));
        }
        return 1 + mx;
    }

    // Asserts feasibility and the KKT sign rule: free columns have ~zero gradient, columns at lo have
    // gradient >= 0, columns at hi have gradient <= 0.
    private static void AssertKkt(int m, int n, float[] a, float[] b, float[] lo, float[] hi, float[] x)
    {
        var g = Gradient(m, n, a, b, x);
        var tol = 1e-3 * AtbScale(m, n, a, b);
        for (var j = 0; j < n; j++)
        {
            Assert.True(x[j] >= lo[j] && x[j] <= hi[j], $"column {j} outside its box: {x[j]}");
            if (lo[j] == hi[j]) continue;
            if (x[j] == lo[j]) Assert.True(g[j] >= -tol, $"column {j} at lo with gradient {g[j]}");
            else if (x[j] == hi[j]) Assert.True(g[j] <= tol, $"column {j} at hi with gradient {g[j]}");
            else Assert.True(Math.Abs(g[j]) <= tol, $"column {j} free with gradient {g[j]}");
        }
    }

    // Accelerated projected gradient (FISTA) with step 1/L, L bounded by the Frobenius norm squared of A.
    // Independent of the solver's active-set logic; run to a fixed point.
    private static double ReferenceCost(int m, int n, float[] a, float[] b, float[] lo, float[] hi)
    {
        var frob = 0.0;
        foreach (var v in a) frob += (double)v * v;
        var step = 1.0 / Math.Max(frob, 1e-12);
        var x = new double[n];
        var y = new double[n];
        var xf = new float[n];
        for (var j = 0; j < n; j++) x[j] = y[j] = Math.Clamp(0.0, lo[j], hi[j]);
        var t = 1.0;
        for (var it = 0; it < 100000; it++)
        {
            for (var j = 0; j < n; j++) xf[j] = (float)y[j];
            var g = Gradient(m, n, a, b, xf);
            var moved = 0.0;
            var tNext = (1.0 + Math.Sqrt(1.0 + 4.0 * t * t)) / 2.0;
            for (var j = 0; j < n; j++)
            {
                var next = Math.Clamp(y[j] - step * g[j], lo[j], hi[j]);
                moved = Math.Max(moved, Math.Abs(next - x[j]));
                y[j] = next + (t - 1.0) / tNext * (next - x[j]);
                x[j] = next;
            }
            t = tNext;
            if (moved < 1e-10) break;
        }
        for (var j = 0; j < n; j++) xf[j] = (float)x[j];
        return Cost(m, n, a, b, xf);
    }

    private static BoundedLeastSquaresStatus Solve(int m, int n, float[] a, float[] b, float[] lo, float[] hi, float[] x,
        out int iterations, int maxIterations = BoundedLeastSquares.DefaultMaxIterations)
        => BoundedLeastSquares.Solve(m, n, a, b, lo, hi, x, new float[BoundedLeastSquares.WorkspaceLength(n)], out iterations, maxIterations);

    private static float[] Fill(float value, int n)
    {
        var v = new float[n];
        Array.Fill(v, value);
        return v;
    }

    private static double[] NormalEquationSolution(int m, int n, float[] a, float[] b)
    {
        var mat = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
                for (var r = 0; r < m; r++) mat[i, j] += (double)a[r * n + i] * a[r * n + j];
            for (var r = 0; r < m; r++) mat[i, n] += (double)a[r * n + i] * b[r];
        }
        for (var c = 0; c < n; c++)
        {
            var piv = mat[c, c];
            for (var j = 0; j <= n; j++) mat[c, j] /= piv;
            for (var i = 0; i < n; i++)
            {
                if (i == c) continue;
                var f = mat[i, c];
                for (var j = 0; j <= n; j++) mat[i, j] -= f * mat[c, j];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = mat[i, n];
        return x;
    }

    private static (int m, int n, float[] a, float[] b, float[] lo, float[] hi) RandomProblem(uint seed)
    {
        var rng = new CultMath.Random(seed * 7919u);
        var n = rng.NextInt(2, 17);
        var m = rng.NextInt(n, 25);
        var a = new float[m * n];
        var b = new float[m];
        for (var i = 0; i < a.Length; i++) a[i] = rng.NextFloat(-1f, 1f);
        for (var i = 0; i < m; i++) b[i] = rng.NextFloat(-3f, 3f);
        var lo = new float[n];
        var hi = new float[n];
        for (var j = 0; j < n; j++)
        {
            switch (rng.NextInt(3))
            {
                case 0: lo[j] = -1f; hi[j] = 1f; break;
                case 1: lo[j] = 0f; hi[j] = 1f; break;
                default: lo[j] = -0.25f; hi[j] = 0.1f; break;
            }
        }
        return (m, n, a, b, lo, hi);
    }

    private static readonly float[] A54 =
    {
        1.0f, 0.3f, -0.2f,
        0.2f, 1.1f, 0.4f,
        -0.5f, 0.6f, 0.9f,
        0.7f, -0.4f, 0.5f,
        0.1f, 0.8f, -0.6f,
    };

    private static readonly float[] B5 = { 0.9f, -0.4f, 1.3f, 0.2f, -0.7f };

    [Fact]
    public void InteriorOptimumEqualsNormalEquations()
    {
        const int m = 5, n = 3;
        var x = new float[n];
        var status = Solve(m, n, A54, B5, Fill(-100f, n), Fill(100f, n), x, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);

        var expected = NormalEquationSolution(m, n, A54, B5);
        for (var i = 0; i < n; i++) Assert.Equal(expected[i], x[i], 1e-4);
    }

    [Fact]
    public void KktHoldsOnRandomBoxedProblems()
    {
        var anyActive = false;
        for (uint seed = 1; seed <= 150; seed++)
        {
            var (m, n, a, b, lo, hi) = RandomProblem(seed);
            var x = new float[n];
            var status = Solve(m, n, a, b, lo, hi, x, out _);
            Assert.True(status == BoundedLeastSquaresStatus.Converged, $"seed {seed} ({m}x{n}) hit the cap");
            AssertKkt(m, n, a, b, lo, hi, x);
            for (var j = 0; j < n; j++) anyActive |= x[j] == lo[j] || x[j] == hi[j];

            var cs = Cost(m, n, a, b, x);
            var cr = ReferenceCost(m, n, a, b, lo, hi);
            Assert.True(Math.Abs(cs - cr) <= 1e-4 * cr + 1e-6, $"seed {seed} ({m}x{n}): solver cost {cs}, reference {cr}");
        }
        Assert.True(anyActive);
    }

    [Fact]
    public void AllBoundsActive()
    {
        const int m = 4, n = 3;
        var a = new[] { 1.0f, 0.2f, 0.1f, 0.1f, 1.0f, 0.3f, -0.2f, 0.2f, 1.0f, 0.5f, 0.5f, 0.4f };
        var b = new[] { 10f, -10f, 10f, 1f };
        var lo = Fill(-1f, n);
        var hi = Fill(1f, n);
        var x = new float[n];
        var status = Solve(m, n, a, b, lo, hi, x, out var iterations);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(new[] { 1f, -1f, 1f }, x);
        Assert.Equal(3, iterations);
        AssertKkt(m, n, a, b, lo, hi, x);
    }

    [Fact]
    public void ZeroColumn()
    {
        const int m = 5, n = 3;
        var a = (float[])A54.Clone();
        for (var r = 0; r < m; r++) a[r * n + 1] = 0f;
        var lo = Fill(-1f, n);
        var hi = Fill(1f, n);
        var x = new float[n];
        var status = Solve(m, n, a, B5, lo, hi, x, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        AssertKkt(m, n, a, B5, lo, hi, x);
        var cs = Cost(m, n, a, B5, x);
        var cr = ReferenceCost(m, n, a, B5, lo, hi);
        Assert.True(Math.Abs(cs - cr) <= 1e-4 * cr + 1e-6);
    }

    [Fact]
    public void DuplicateColumns()
    {
        const int m = 5, n = 3;
        var a = (float[])A54.Clone();
        for (var r = 0; r < m; r++) a[r * n + 2] = a[r * n + 0];
        var lo = Fill(-1f, n);
        var hi = new[] { 0.3f, 1f, 0.3f };
        var x = new float[n];
        var status = Solve(m, n, a, B5, lo, hi, x, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        AssertKkt(m, n, a, B5, lo, hi, x);
        var cs = Cost(m, n, a, B5, x);
        var cr = ReferenceCost(m, n, a, B5, lo, hi);
        Assert.True(Math.Abs(cs - cr) <= 1e-4 * cr + 1e-6);
    }

    [Fact]
    public void WarmStartMatchesColdStart()
    {
        const int m = 5, n = 3;
        var lo = new[] { -0.2f, -1f, 0f };
        var hi = new[] { 0.5f, 0.3f, 1f };
        var cold = new float[n];
        Solve(m, n, A54, B5, lo, hi, cold, out _);
        AssertKkt(m, n, A54, B5, lo, hi, cold);

        var starts = new[] { new[] { 0.5f, -1f, 1f }, new[] { 9f, 9f, -9f }, new[] { 0.1f, 0.1f, 0.5f }, (float[])cold.Clone() };
        foreach (var start in starts)
        {
            var status = Solve(m, n, A54, B5, lo, hi, start, out var iterations);
            Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
            for (var j = 0; j < n; j++) Assert.Equal(cold[j], start[j], 1e-4);
            if (ReferenceEquals(start, starts[^1])) Assert.Equal(0, iterations);
        }
    }

    [Fact]
    public void AsymmetricBounds()
    {
        const int m = 5, n = 3;
        var lo = new[] { -1f, 0f, -1f };
        var hi = new[] { 1f, 1f, 1f };
        // Column 1 wants to go negative (b has a strong component against it) but its floor is 0.
        var b = new[] { 0.2f, -2.5f, 0.4f, -1.5f, -2.0f };
        var x = new float[n];
        var status = Solve(m, n, A54, b, lo, hi, x, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        AssertKkt(m, n, A54, b, lo, hi, x);
        Assert.Equal(0f, x[1]);
        Assert.True(x[0] < 0f || x[2] < 0f, "a symmetric-bounds column should use its negative side");
        var cs = Cost(m, n, A54, b, x);
        var cr = ReferenceCost(m, n, A54, b, lo, hi);
        Assert.True(Math.Abs(cs - cr) <= 1e-4 * cr + 1e-6);
    }

    [Fact]
    public void IterationCapReturnsFeasibleIterateAndLimitStatus()
    {
        const int m = 4, n = 3;
        var a = new[] { 1.0f, 0.2f, 0.1f, 0.1f, 1.0f, 0.3f, -0.2f, 0.2f, 1.0f, 0.5f, 0.5f, 0.4f };
        var b = new[] { 10f, -10f, 10f, 1f };
        var lo = Fill(-1f, n);
        var hi = Fill(1f, n);
        var x = new float[n];
        var start = Cost(m, n, a, b, x);
        var status = Solve(m, n, a, b, lo, hi, x, out var iterations, maxIterations: 1);
        Assert.Equal(BoundedLeastSquaresStatus.IterationLimit, status);
        Assert.Equal(1, iterations);
        for (var j = 0; j < n; j++) Assert.InRange(x[j], lo[j], hi[j]);
        Assert.True(Cost(m, n, a, b, x) < start);

        var zero = new float[n];
        Assert.Equal(BoundedLeastSquaresStatus.IterationLimit, Solve(m, n, a, b, lo, hi, zero, out iterations, maxIterations: 0));
        Assert.Equal(0, iterations);
        Assert.Equal(new float[n], zero);
    }

    [Fact]
    public void StartOutsideTheBoxIsClampedFirst()
    {
        const int m = 5, n = 3;
        var lo = Fill(-0.1f, n);
        var hi = Fill(0.1f, n);
        var x = new[] { 50f, -50f, float.NaN };
        Solve(m, n, A54, B5, lo, hi, x, out _, maxIterations: 0);
        Assert.Equal(new[] { 0.1f, -0.1f, -0.1f }, x);
    }

    [Fact]
    public void FixedColumnStaysPutEvenWhenItsGradientWantsOut()
    {
        const int m = 5, n = 3;
        var lo = new[] { -100f, -1f, -100f };
        var hi = new[] { 100f, -1f, 100f };
        var x = new float[n];
        var status = Solve(m, n, A54, B5, lo, hi, x, out var iterations);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(-1f, x[1]);
        Assert.True(Gradient(m, n, A54, B5, x)[1] < -1e-3, "fixture must pull the fixed column inward");
        Assert.Equal(0, iterations);
        AssertKkt(m, n, A54, B5, lo, hi, x);
    }

    [Fact]
    public void WarmCallAllocatesNothing()
    {
        const int m = 5, n = 3;
        var lo = new[] { -0.2f, -1f, 0f };
        var hi = new[] { 0.5f, 0.3f, 1f };
        var x = new float[n];
        var workspace = new float[BoundedLeastSquares.WorkspaceLength(n)];
        BoundedLeastSquares.Solve(m, n, A54, B5, lo, hi, x, workspace, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = BoundedLeastSquares.Solve(m, n, A54, B5, lo, hi, x, workspace, out _);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void LargeInteriorOptimumEqualsNormalEquations()
    {
        const int m = 24, n = 16;
        var rng = new CultMath.Random(4242u);
        var a = new float[m * n];
        var b = new float[m];
        for (var i = 0; i < a.Length; i++) a[i] = rng.NextFloat(-1f, 1f);
        for (var i = 0; i < m; i++) b[i] = rng.NextFloat(-3f, 3f);
        var x = new float[n];
        var status = Solve(m, n, a, b, Fill(-1000f, n), Fill(1000f, n), x, out var iterations);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(0, iterations);
        var expected = NormalEquationSolution(m, n, a, b);
        for (var i = 0; i < n; i++) Assert.Equal(expected[i], x[i], 1e-3);
    }

    [Fact]
    public void ScaledAndNearDuplicateColumnsAreOptimal()
    {
        const int m = 5, n = 3;
        foreach (var noise in new[] { 0f, 1e-5f })
        {
            var a = (float[])A54.Clone();
            for (var r = 0; r < m; r++) a[r * n + 2] = 0.37f * a[r * n + 0] + noise * (r - 2);
            var lo = Fill(-1f, n);
            var hi = new[] { 0.6f, 1f, 0.6f };
            var x = new float[n];
            var status = Solve(m, n, a, B5, lo, hi, x, out _);
            Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
            AssertKkt(m, n, a, B5, lo, hi, x);
            var cs = Cost(m, n, a, B5, x);
            var cr = ReferenceCost(m, n, a, B5, lo, hi);
            Assert.True(Math.Abs(cs - cr) <= 1e-4 * cr + 1e-6, $"noise {noise}: solver cost {cs}, reference {cr}");
        }
    }

    [Fact]
    public void WorkspaceContentsOnEntryAreIgnored()
    {
        const int m = 5, n = 3;
        var zeroCol = (float[])A54.Clone();
        for (var r = 0; r < m; r++) zeroCol[r * n + 1] = 0f;
        var dupCol = (float[])A54.Clone();
        for (var r = 0; r < m; r++) dupCol[r * n + 2] = dupCol[r * n + 0];
        foreach (var a in new[] { A54, zeroCol, dupCol })
        {
            var lo = new[] { -1f, -1f, -1f };
            var hi = new[] { 0.3f, 1f, 0.3f };
            var clean = new float[n];
            Solve(m, n, a, B5, lo, hi, clean, out var cleanIterations);
            foreach (var junk in new[] { float.NaN, 7f, -1e30f })
            {
                var workspace = new float[BoundedLeastSquares.WorkspaceLength(n)];
                Array.Fill(workspace, junk);
                var x = new float[n];
                BoundedLeastSquares.Solve(m, n, a, B5, lo, hi, x, workspace, out var iterations);
                Assert.Equal(clean, x);
                Assert.Equal(cleanIterations, iterations);
            }
        }
    }

    [Fact]
    public void WarmStartAtAVertexTakesNoIterations()
    {
        const int m = 4, n = 3;
        var a = new[] { 1.0f, 0.2f, 0.1f, 0.1f, 1.0f, 0.3f, -0.2f, 0.2f, 1.0f, 0.5f, 0.5f, 0.4f };
        var b = new[] { 10f, -10f, 10f, 1f };
        var x = new[] { 1f, -1f, 1f };
        var status = Solve(m, n, a, b, Fill(-1f, n), Fill(1f, n), x, out var iterations);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(0, iterations);
        Assert.Equal(new[] { 1f, -1f, 1f }, x);
    }

    [Fact]
    public void IterationCountFollowsTheCapExactly()
    {
        var sawRelease = false;
        for (uint seed = 1; seed <= 40; seed++)
        {
            var (m, n, a, b, lo, hi) = RandomProblem(seed);
            var full = new float[n];
            Solve(m, n, a, b, lo, hi, full, out var needed);
            var previousCost = double.MaxValue;
            for (var cap = 0; cap <= needed; cap++)
            {
                var x = new float[n];
                var status = Solve(m, n, a, b, lo, hi, x, out var iterations, cap);
                Assert.Equal(cap, iterations);
                Assert.Equal(cap < needed ? BoundedLeastSquaresStatus.IterationLimit : BoundedLeastSquaresStatus.Converged, status);
                for (var j = 0; j < n; j++) Assert.InRange(x[j], lo[j], hi[j]);
                var cost = Cost(m, n, a, b, x);
                Assert.True(cost <= previousCost * (1 + 1e-6) + 1e-9, $"seed {seed} cap {cap}: cost rose from {previousCost} to {cost}");
                previousCost = cost;
            }
            sawRelease |= needed > n;
        }
        Assert.True(sawRelease, "no random problem needed more iterations than columns, so no bound was ever released");
    }

    [Fact]
    public void EachShortSpanIsRejected()
    {
        const int m = 5, n = 3;
        var ws = new float[BoundedLeastSquares.WorkspaceLength(n)];
        var lo = Fill(-1f, n);
        var hi = Fill(1f, n);
        void Expect(Action call) => Assert.Contains("BoundedLeastSquares", Assert.Throws<ArgumentException>(call).Message);
        Expect(() => BoundedLeastSquares.Solve(m, n, new float[m * n - 1], B5, lo, hi, new float[n], ws, out _));
        Expect(() => BoundedLeastSquares.Solve(m, n, A54, new float[m - 1], lo, hi, new float[n], ws, out _));
        Expect(() => BoundedLeastSquares.Solve(m, n, A54, B5, new float[n - 1], hi, new float[n], ws, out _));
        Expect(() => BoundedLeastSquares.Solve(m, n, A54, B5, lo, new float[n - 1], new float[n], ws, out _));
        Expect(() => BoundedLeastSquares.Solve(m, n, A54, B5, lo, hi, new float[n - 1], ws, out _));
        Expect(() => BoundedLeastSquares.Solve(m, n, A54, B5, lo, hi, new float[n], new float[ws.Length - 1], out _));
        Expect(() => BoundedLeastSquares.Solve(-1, n, A54, B5, lo, hi, new float[n], ws, out _));
        Expect(() => BoundedLeastSquares.Solve(m, -1, A54, B5, lo, hi, new float[n], ws, out _));
        // Exactly sized spans, and empty problems, are accepted.
        BoundedLeastSquares.Solve(m, n, A54, B5, lo, hi, new float[n], ws, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, BoundedLeastSquares.Solve(0, 0, default, default, default, default, default, default, out _));
        Assert.Equal(BoundedLeastSquaresStatus.Converged, BoundedLeastSquares.Solve(0, n, default, default, lo, hi, new float[n], ws, out _));
    }

    // Well-conditioned enough (m = n + 6) that x is comparable across scales.
    private static (int m, int n, float[] a, float[] b, float[] lo, float[] hi) ScaleProblem(uint seed)
    {
        var rng = new CultMath.Random(seed * 104729u);
        var n = rng.NextInt(3, 11);
        var m = n + 6;
        var a = new float[m * n];
        var b = new float[m];
        for (var i = 0; i < a.Length; i++) a[i] = rng.NextFloat(-1f, 1f);
        for (var i = 0; i < m; i++) b[i] = rng.NextFloat(-3f, 3f);
        var lo = new float[n];
        var hi = new float[n];
        for (var j = 0; j < n; j++)
        {
            lo[j] = rng.NextFloat(-0.6f, -0.05f);
            hi[j] = rng.NextFloat(0.05f, 0.6f);
        }
        return (m, n, a, b, lo, hi);
    }

    private static float[] Scaled(float[] v, float k)
    {
        var r = new float[v.Length];
        for (var i = 0; i < v.Length; i++) r[i] = v[i] * k;
        return r;
    }

    [Fact]
    public void SolutionIsScaleFree()
    {
        var sawActive = false;
        for (uint seed = 1; seed <= 40; seed++)
        {
            var (m, n, a, b, lo, hi) = ScaleProblem(seed);
            var x = (float[])lo.Clone();
            Assert.Equal(BoundedLeastSquaresStatus.Converged, Solve(m, n, a, b, lo, hi, x, out _));
            AssertKkt(m, n, a, b, lo, hi, x);
            for (var j = 0; j < n; j++) sawActive |= x[j] == lo[j] || x[j] == hi[j];

            foreach (var k in new[] { 1e-6f, 1e3f, 1e6f })
            {
                // b and the bounds scaled: x scales with them.
                var xb = Scaled(lo, k);
                var status = Solve(m, n, a, Scaled(b, k), Scaled(lo, k), Scaled(hi, k), xb, out _);
                Assert.True(status == BoundedLeastSquaresStatus.Converged, $"seed {seed} k {k}: {status}");
                for (var j = 0; j < n; j++)
                    Assert.True(Math.Abs(xb[j] - x[j] * k) <= 1e-3 * Math.Abs(k) * (1 + Math.Abs(x[j])), $"seed {seed} b*{k} col {j}: {xb[j]} vs {x[j] * k}");

                // A scaled: x scales inversely, so the bounds stay put and only b is scaled with A to keep the same box.
                var xa = (float[])lo.Clone();
                var lo2 = Scaled(lo, 1f / k);
                var hi2 = Scaled(hi, 1f / k);
                for (var j = 0; j < n; j++) xa[j] = lo2[j];
                status = Solve(m, n, Scaled(a, k), b, lo2, hi2, xa, out _);
                Assert.True(status == BoundedLeastSquaresStatus.Converged, $"seed {seed} A*{k}: {status}");
                for (var j = 0; j < n; j++)
                    Assert.True(Math.Abs(xa[j] - x[j] / k) <= 1e-3 * Math.Abs(1 / k) * (1 + Math.Abs(x[j])), $"seed {seed} A*{k} col {j}: {xa[j]} vs {x[j] / k}");
            }
        }
        Assert.True(sawActive);
    }

    [Fact]
    public void NonFiniteOrInvertedInputIsRejectedAndLeavesXUntouched()
    {
        const int m = 5, n = 3;
        var nan = float.NaN;
        var inf = float.PositiveInfinity;
        var cases = new (string name, Action<float[], float[], float[], float[]> corrupt)[]
        {
            ("A NaN", (a, b, lo, hi) => a[4] = nan),
            ("A +Inf", (a, b, lo, hi) => a[7] = inf),
            ("A -Inf", (a, b, lo, hi) => a[1] = -inf),
            ("b NaN", (a, b, lo, hi) => b[2] = nan),
            ("b +Inf", (a, b, lo, hi) => b[0] = inf),
            ("lo NaN", (a, b, lo, hi) => lo[1] = nan),
            ("hi NaN", (a, b, lo, hi) => hi[2] = nan),
            ("lo > hi", (a, b, lo, hi) => { lo[1] = 0.5f; hi[1] = 0.25f; }),
            ("lo = +Inf", (a, b, lo, hi) => { lo[0] = inf; hi[0] = inf; }),
            ("hi = -Inf", (a, b, lo, hi) => { lo[0] = -inf; hi[0] = -inf; }),
        };
        foreach (var (name, corrupt) in cases)
        {
            var a = (float[])A54.Clone();
            var b = (float[])B5.Clone();
            var lo = Fill(-1f, n);
            var hi = Fill(1f, n);
            corrupt(a, b, lo, hi);
            foreach (var start in new[] { new[] { 0.5f, -3f, 9f }, new[] { nan, 0f, 0f } })
            {
                var x = (float[])start.Clone();
                var status = Solve(m, n, a, b, lo, hi, x, out var iterations);
                Assert.True(status == BoundedLeastSquaresStatus.InvalidInput, $"{name}: {status}");
                Assert.Equal(0, iterations);
                for (var j = 0; j < n; j++)
                    Assert.Equal(BitConverter.SingleToInt32Bits(start[j]), BitConverter.SingleToInt32Bits(x[j]));
            }
        }
    }

    [Fact]
    public void InfiniteBoundsMeanUnbounded()
    {
        const int m = 5, n = 3;
        var inf = float.PositiveInfinity;
        var x = new float[n];
        Assert.Equal(BoundedLeastSquaresStatus.Converged, Solve(m, n, A54, B5, Fill(-inf, n), Fill(inf, n), x, out _));
        var expected = NormalEquationSolution(m, n, A54, B5);
        for (var j = 0; j < n; j++) Assert.Equal(expected[j], x[j], 1e-4);

        // One-sided, with an infinite warm start: the start is clamped to a finite point first.
        var y = new[] { -inf, inf, float.NaN };
        var lo = new[] { -inf, 0.2f, -inf };
        var hi = new[] { inf, inf, 0.1f };
        Assert.Equal(BoundedLeastSquaresStatus.Converged, Solve(m, n, A54, B5, lo, hi, y, out _));
        AssertKkt(m, n, A54, B5, lo, hi, y);
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }

    // Exact reference: every assignment of each column to {lo, hi, free}, free set solved in double.
    private static double ExactBoxedCost(int m, int n, float[] a, float[] b, float[] lo, float[] hi)
    {
        var best = double.MaxValue;
        var total = 1;
        for (var j = 0; j < n; j++) total *= 3;
        var x = new double[n];
        for (var code = 0; code < total; code++)
        {
            var c = code;
            var free = new System.Collections.Generic.List<int>();
            for (var j = 0; j < n; j++)
            {
                var d = c % 3; c /= 3;
                if (d == 0) x[j] = lo[j]; else if (d == 1) x[j] = hi[j]; else free.Add(j);
            }
            var k = free.Count;
            if (k > 0)
            {
                var mat = new double[k, k + 1];
                for (var i = 0; i < k; i++)
                {
                    for (var l = 0; l < k; l++)
                        for (var r = 0; r < m; r++) mat[i, l] += (double)a[r * n + free[i]] * a[r * n + free[l]];
                    for (var r = 0; r < m; r++)
                    {
                        var fixedPart = 0.0;
                        for (var j = 0; j < n; j++) if (!free.Contains(j)) fixedPart += (double)a[r * n + j] * x[j];
                        mat[i, k] += (double)a[r * n + free[i]] * (b[r] - fixedPart);
                    }
                }
                var ok = true;
                for (var col = 0; col < k && ok; col++)
                {
                    var piv = col;
                    for (var r = col + 1; r < k; r++) if (Math.Abs(mat[r, col]) > Math.Abs(mat[piv, col])) piv = r;
                    if (Math.Abs(mat[piv, col]) < 1e-9) { ok = false; break; }
                    for (var l = 0; l <= k; l++) (mat[col, l], mat[piv, l]) = (mat[piv, l], mat[col, l]);
                    for (var r = 0; r < k; r++)
                    {
                        if (r == col) continue;
                        var f = mat[r, col] / mat[col, col];
                        for (var l = col; l <= k; l++) mat[r, l] -= f * mat[col, l];
                    }
                }
                if (!ok) continue;
                for (var i = 0; i < k; i++)
                {
                    x[free[i]] = mat[i, k] / mat[i, i];
                    if (x[free[i]] < lo[free[i]] - 1e-12 || x[free[i]] > hi[free[i]] + 1e-12) ok = false;
                }
                if (!ok) continue;
            }
            var cost = 0.0;
            for (var r = 0; r < m; r++)
            {
                var s = -(double)b[r];
                for (var j = 0; j < n; j++) s += (double)a[r * n + j] * x[j];
                cost += s * s;
            }
            best = Math.Min(best, cost);
        }
        return best;
    }

    // m < n with columns that are nearly parallel (condition number of A around 1e2), b outside the reachable set.
    // The absolute scale of A must not matter: the KKT tolerance is relative, and the working iterate is double, so a
    // 1e6 A with untouched bounds reaches the same optimum to the same allowance.
    [Theory]
    [InlineData(1f)]
    [InlineData(1e6f)]
    public void NearCollinearColumnsReachTheExactOptimum(float scale)
    {
        const int m = 2, n = 7;
        for (var family = 0; family < 4; family++)
        {
            for (uint seed = 1; seed <= 120; seed++)
            {
                var rng = new CultMath.Random(seed * 15485863u + (uint)family * 977u);
                var a = new float[m * n];
                for (var j = 0; j < n; j++)
                {
                    switch (family)
                    {
                        case 0: a[j] = rng.NextFloat(0.5f, 1f); a[n + j] = 0.01f * rng.NextFloat(-1f, 1f); break;
                        case 1: a[j] = 1f + 0.02f * rng.NextFloat(-1f, 1f); a[n + j] = 0.01f * rng.NextFloat(-1f, 1f); break;
                        case 2: a[j] = rng.NextFloat(-1f, 1f); a[n + j] = 0.01f * a[j] + 0.01f * rng.NextFloat(-1f, 1f); break;
                        default: a[j] = rng.NextFloat(0.5f, 1f); a[n + j] = a[j] * 0.9f + 0.01f * rng.NextFloat(-1f, 1f); break;
                    }
                }
                for (var i = 0; i < a.Length; i++) a[i] *= scale;
                var b = new[] { rng.NextFloat(-8f, 8f), rng.NextFloat(-8f, 8f) };
                var lo = new float[n];
                var hi = new float[n];
                for (var j = 0; j < n; j++) { lo[j] = rng.NextFloat() < 0.5f ? -1f : 0f; hi[j] = 1f; }
                var x = new float[n];
                var status = Solve(m, n, a, b, lo, hi, x, out _);
                Assert.True(status == BoundedLeastSquaresStatus.Converged, $"family {family} seed {seed}: {status}");
                var gap = Cost(m, n, a, b, x) - ExactBoxedCost(m, n, a, b, lo, hi);
                var allowed = 1e-5 * (b[0] * b[0] + b[1] * b[1]);
                Assert.True(gap <= allowed, $"family {family} seed {seed}: cost {gap} above the exact optimum (allowed {allowed})");
            }
        }
    }

    // A = U diag(s) V^T with s geometric from 1 down to 1/cond, so cond(A) is the requested value (float rounding of A
    // perturbs it slightly). m x n with m >= n; b is generic, so it has a real residual when m > n.
    private static (float[] a, float[] b) ConditionedProblem(int m, int n, double cond, uint seed, double aScale = 1.0)
    {
        var rng = new CultMath.Random(seed * 2654435761u + 17u);
        double[][] Orthonormal(int dim, int count)
        {
            var q = new double[count][];
            for (var k = 0; k < count; k++)
            {
                var v = new double[dim];
                for (var i = 0; i < dim; i++) v[i] = rng.NextFloat(-1f, 1f);
                for (var pass = 0; pass < 2; pass++)
                    for (var l = 0; l < k; l++)
                    {
                        var dot = 0.0;
                        for (var i = 0; i < dim; i++) dot += v[i] * q[l][i];
                        for (var i = 0; i < dim; i++) v[i] -= dot * q[l][i];
                    }
                var norm = 0.0;
                for (var i = 0; i < dim; i++) norm += v[i] * v[i];
                norm = Math.Sqrt(norm);
                for (var i = 0; i < dim; i++) v[i] /= norm;
                q[k] = v;
            }
            return q;
        }
        var u = Orthonormal(m, n);
        var w = Orthonormal(n, n);
        var a = new float[m * n];
        for (var k = 0; k < n; k++)
        {
            var s = aScale * Math.Pow(cond, -(double)k / Math.Max(1, n - 1));
            for (var r = 0; r < m; r++)
                for (var c = 0; c < n; c++)
                    a[r * n + c] += (float)(u[k][r] * s * w[k][c]);
        }
        var b = new float[m];
        for (var i = 0; i < m; i++) b[i] = rng.NextFloat(-3f, 3f);
        return (a, b);
    }

    // Unsaturated allocator-shaped ticks: 24 columns, every column free at the optimum. The solve is one Newton step;
    // at most one more may confirm it. Before the working iterate moved to double, the float gradient's noise floor
    // exceeded the tolerance from cond ~1e3 and the solver spun to the 100-iteration cap on optimal points.
    [Theory]
    [InlineData(1e2)]
    [InlineData(1e3)]
    [InlineData(1e4)]
    public void UnsaturatedIllConditionedProblemsConvergeInAtMostTwoIterations(double cond)
    {
        const int m = 28, n = 24;
        for (uint seed = 1; seed <= 30; seed++)
        {
            var (a, b) = ConditionedProblem(m, n, cond, seed);
            var x = new float[n];
            var status = Solve(m, n, a, b, Fill(-1e9f, n), Fill(1e9f, n), x, out var iterations);
            Assert.True(status == BoundedLeastSquaresStatus.Converged, $"cond {cond} seed {seed}: {status} after {iterations}");
            Assert.True(iterations <= 2, $"cond {cond} seed {seed}: {iterations} iterations");
            var bb = 0.0;
            foreach (var v in b) bb += (double)v * v;
            var expected = NormalEquationSolution(m, n, a, b);
            var xs = new float[n];
            for (var j = 0; j < n; j++) xs[j] = (float)expected[j];
            Assert.True(Cost(m, n, a, b, x) - Cost(m, n, a, b, xs) <= 1e-6 * bb, $"cond {cond} seed {seed}: cost above the exact optimum");
        }
    }

    // Near the pivot cutoff (cond(A) ~ 3e7, cond(A^T A) ~ 1e15) a Newton step leaves a free gradient above the
    // tolerance on some problems. Those take one confirming iteration and stop because it lowers nothing; the rest
    // are stationary at once. Never the cap, and never spinning.
    [Fact]
    public void NearSingularFreeSetsConfirmOnceAndStop()
    {
        const int m = 28, n = 24;
        var confirmed = 0;
        for (uint seed = 1; seed <= 60; seed++)
        {
            var (a, b) = ConditionedProblem(m, n, 3e7, seed);
            var x = new float[n];
            var status = Solve(m, n, a, b, Fill(-1e9f, n), Fill(1e9f, n), x, out var iterations);
            Assert.True(status == BoundedLeastSquaresStatus.Converged, $"seed {seed}: {status}");
            Assert.True(iterations <= 3, $"seed {seed}: {iterations} iterations");
            if (iterations > 0) confirmed++;
        }
        Assert.InRange(confirmed, 1, 20);
    }

    // Scale-freeness was only claimed for scaling A, b and the bounds together; A alone at 1e6 with the bounds left
    // alone must also converge, and as fast.
    [Theory]
    [InlineData(1e2)]
    [InlineData(1e4)]
    public void LargeAWithUnscaledBoundsConvergesInAtMostTwoIterations(double cond)
    {
        const int m = 28, n = 24;
        for (uint seed = 1; seed <= 30; seed++)
        {
            var (a, b) = ConditionedProblem(m, n, cond, seed, 1e6);
            var x = new float[n];
            var status = Solve(m, n, a, b, Fill(-1e3f, n), Fill(1e3f, n), x, out var iterations);
            Assert.True(status == BoundedLeastSquaresStatus.Converged, $"cond {cond} seed {seed}: {status} after {iterations}");
            Assert.True(iterations <= 2, $"cond {cond} seed {seed}: {iterations} iterations");
        }
    }

    // b = 0 must not borrow its tolerance from the start point: a far start's own gradient is enormous, so that
    // scale judged it converged after one iteration at cost 5001. The optimum here is ~1e-9.
    [Fact]
    public void ZeroTargetFromAFarStartIsNotJudgedConvergedByItsOwnSteepness()
    {
        var a = new[] { 1f, 1f, 1f, 1.0001f };
        var b = new float[2];
        var lo = new[] { 0.5f, -1e6f };
        var hi = new[] { 1e6f, 1e6f };
        var x = new[] { 1e6f, 1e6f };
        var status = Solve(2, 2, a, b, lo, hi, x, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        AssertKkt(2, 2, a, b, lo, hi, x);
        Assert.True(Cost(2, 2, a, b, x) < 1e-6, $"cost {Cost(2, 2, a, b, x)}");
        Assert.True(x[0] >= 0.5f);

        // The origin inside the box: the answer is the origin, from any start.
        var lo2 = new[] { -1e6f, -1e6f };
        var y = new[] { 1e6f, -1e6f };
        Assert.Equal(BoundedLeastSquaresStatus.Converged, Solve(2, 2, a, b, lo2, hi, y, out _));
        Assert.True(Cost(2, 2, a, b, y) < 1e-9, $"cost {Cost(2, 2, a, b, y)}");
    }

    [Fact]
    public void WarmStartClampIsExact()
    {
        var inf = float.PositiveInfinity;
        var cases = new (float lo, float hi, float start, float expected)[]
        {
            (-1f, 1f, 0.25f, 0.25f),
            (-1f, 1f, 5f, 1f),
            (-1f, 1f, -5f, -1f),
            (-1f, 1f, float.NaN, -1f),
            (-inf, inf, float.NaN, 0f),
            (-inf, inf, inf, 0f),
            (-inf, inf, -inf, 0f),
            (0.2f, inf, inf, 0.2f),
            (0.2f, inf, -inf, 0.2f),
            (-inf, -2f, -inf, -2f),
            (-inf, -2f, 3f, -2f),
            (-inf, 3f, float.NaN, 0f),
            (-2f, inf, float.NaN, -2f),
        };
        foreach (var (l, h, start, expected) in cases)
        {
            var x = new[] { start, start, start };
            // No rows: every column is dependent and takes a zero step, so x is exactly the clamped start.
            Solve(0, 3, default, default, Fill(l, 3), Fill(h, 3), x, out var iterations);
            Assert.Equal(0, iterations);
            Assert.Equal(new[] { expected, expected, expected }, x);
        }
    }

    // A duplicate column that is free and interior is stepped by neither the factor nor the refinement: the columns
    // share one gradient, and moving both along it would overshoot.
    [Fact]
    public void FreeDuplicateColumnGetsNoStep()
    {
        const int m = 5, n = 3;
        var a = (float[])A54.Clone();
        for (var r = 0; r < m; r++) a[r * n + 2] = a[r * n + 0];
        var x = new float[n];
        var status = Solve(m, n, a, B5, Fill(-100f, n), Fill(100f, n), x, out var iterations);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(0, iterations);
        Assert.Equal(0f, x[2]);
        var reduced = new float[m * 2];
        for (var r = 0; r < m; r++) { reduced[r * 2] = a[r * n]; reduced[r * 2 + 1] = a[r * n + 1]; }
        var xr = NormalEquationSolution(m, 2, reduced, B5);
        Assert.Equal(xr[0], x[0], 1e-4);
        Assert.Equal(xr[1], x[1], 1e-4);
    }

    // Columns 1000x apart in scale and exactly parallel: the pivot tolerance is relative to the column's own
    // diagonal, so it catches the duplicate at any absolute scale.
    [Theory]
    [InlineData(1e3f)]
    [InlineData(1e-3f)]
    public void ParallelColumnsAreDependentAtAnyAbsoluteScale(float scale)
    {
        const int m = 5, n = 3;
        var a = (float[])A54.Clone();
        for (var r = 0; r < m; r++) a[r * n + 2] = 0.37f * a[r * n + 0];
        for (var i = 0; i < a.Length; i++) a[i] *= scale;
        var lo = Fill(-1000f / scale, n);
        var hi = Fill(1000f / scale, n);
        var x = new float[n];
        var status = Solve(m, n, a, B5, lo, hi, x, out var iterations);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.True(iterations <= 2, $"{iterations} iterations");
        var reduced = new float[m * 2];
        for (var r = 0; r < m; r++) { reduced[r * 2] = a[r * n]; reduced[r * 2 + 1] = a[r * n + 1]; }
        var xr = NormalEquationSolution(m, 2, reduced, B5);
        var xs = new float[n];
        xs[0] = (float)xr[0];
        xs[1] = (float)xr[1];
        var bb = 0.0;
        foreach (var v in B5) bb += (double)v * v;
        Assert.True(Cost(m, n, a, B5, x) - Cost(m, n, a, B5, xs) <= 1e-6 * bb, "cost above the reduced optimum");
    }

    // No path allocates: not the first InvalidInput, not a cap hit, not a solve that releases bounds. Each path is
    // measured on its first call and again after, so a one-time runtime cost shows up as a difference between the two
    // rather than as a solver allocation.
    [Fact]
    public void EveryPathAllocatesNothing()
    {
        const int m = 4, n = 3;
        var a = new[] { 1.0f, 0.2f, 0.1f, 0.1f, 1.0f, 0.3f, -0.2f, 0.2f, 1.0f, 0.5f, 0.5f, 0.4f };
        var b = new[] { 10f, -10f, 10f, 1f };
        var lo = Fill(-1f, n);
        var hi = Fill(1f, n);
        var workspace = new float[BoundedLeastSquares.WorkspaceLength(n)];
        var x = new float[n];
        var bad = (float[])a.Clone();
        bad[4] = float.NaN;

        var paths = new (string name, float[] a, int cap, BoundedLeastSquaresStatus expected)[]
        {
            ("invalid", bad, 100, BoundedLeastSquaresStatus.InvalidInput),
            ("converged", a, 100, BoundedLeastSquaresStatus.Converged),
            ("limit", a, 1, BoundedLeastSquaresStatus.IterationLimit),
        };
        foreach (var (name, matrix, cap, expected) in paths)
        {
            var bytes = new long[2];
            for (var call = 0; call < 2; call++)
            {
                Array.Clear(x);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var status = BoundedLeastSquares.Solve(m, n, matrix, b, lo, hi, x, workspace, out _, cap);
                bytes[call] = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(expected, status);
            }
            Assert.True(bytes[0] == 0 && bytes[1] == 0, $"{name}: first call {bytes[0]} B, second {bytes[1]} B");
        }
    }
}
