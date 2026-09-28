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

    // Projected gradient with step 1/L, L bounded by the Frobenius norm squared of A.
    private static double ReferenceCost(int m, int n, float[] a, float[] b, float[] lo, float[] hi)
    {
        var frob = 0.0;
        foreach (var v in a) frob += (double)v * v;
        var step = 1.0 / Math.Max(frob, 1e-12);
        var x = new float[n];
        for (var j = 0; j < n; j++) x[j] = Math.Clamp(0f, lo[j], hi[j]);
        var xd = new double[n];
        for (var j = 0; j < n; j++) xd[j] = x[j];
        for (var it = 0; it < 200000; it++)
        {
            for (var j = 0; j < n; j++) x[j] = (float)xd[j];
            var g = Gradient(m, n, a, b, x);
            for (var j = 0; j < n; j++) xd[j] = Math.Clamp(xd[j] - step * g[j], lo[j], hi[j]);
        }
        for (var j = 0; j < n; j++) x[j] = (float)xd[j];
        return Cost(m, n, a, b, x);
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

        // Independent solve of (A^T A) x = A^T b by Gauss-Jordan in double.
        var mat = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
                for (var r = 0; r < m; r++) mat[i, j] += (double)A54[r * n + i] * A54[r * n + j];
            for (var r = 0; r < m; r++) mat[i, n] += (double)A54[r * n + i] * B5[r];
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
        for (var i = 0; i < n; i++) Assert.Equal(mat[i, n], x[i], 1e-4);
    }

    [Fact]
    public void KktHoldsOnRandomBoxedProblems()
    {
        var anyActive = false;
        for (uint seed = 1; seed <= 150; seed++)
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
        var status = Solve(m, n, a, b, lo, hi, x, out _);
        Assert.Equal(BoundedLeastSquaresStatus.Converged, status);
        Assert.Equal(new[] { 1f, -1f, 1f }, x);
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
        var lo = new[] { -1f, -1f, -1f };
        var hi = new[] { 1f, -1f, 1f };
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
    public void ShortSpansThrowArgumentException()
    {
        var x = new float[3];
        Assert.Throws<ArgumentException>(() => BoundedLeastSquares.Solve(5, 3, A54, B5, Fill(-1f, 3), Fill(1f, 3), x, new float[4], out _));
    }
}
