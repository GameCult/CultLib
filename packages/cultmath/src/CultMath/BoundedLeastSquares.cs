using System.Runtime.InteropServices;

namespace CultMath;

/// <summary>Outcome of <see cref="BoundedLeastSquares.Solve"/>.</summary>
public enum BoundedLeastSquaresStatus
{
    /// <summary>The KKT conditions hold: every free variable has zero gradient and every bound has a gradient pointing into it.</summary>
    Converged,

    /// <summary>The iteration cap was reached. <c>x</c> holds the last feasible iterate, which is never worse than the clamped start.</summary>
    IterationLimit,

    /// <summary>The input broke the contract; nothing was solved and <c>x</c> is exactly as passed.</summary>
    InvalidInput,
}

/// <summary>
/// Dense bounded least squares: <c>min ||A x - b||^2</c> subject to <c>lo &lt;= x &lt;= hi</c>, for small problems
/// (tens of rows and columns). C#-only, like the intercept helpers in <see cref="math"/>; there is no HLSL mirror.
/// </summary>
/// <remarks>
/// Primal active set. Each iteration solves the free variables by Cholesky on the normal equations, takes the
/// step to the first bound it would cross, and, once a full step lands, releases the bound with the most
/// violated gradient sign. Free columns that are linearly dependent on earlier free columns (a zero column, a
/// duplicate) get a zero step instead of a failed pivot, so a singular free set never throws. The normal
/// equations square the condition number and run in float: callers that need a minimum-norm answer on
/// rank-deficient problems add a small ridge row to <c>A</c> and <c>b</c>; each free-set solve gets one step of
/// iterative refinement. The KKT tolerance is relative to the problem's gradient scale (max |A^T b|), so
/// scaling A, b or the bounds does not change the answer. The solver allocates nothing and is deterministic.
/// <para>
/// Input contract: <c>a</c> and <c>b</c> must be finite; <c>lo</c> and <c>hi</c> must not be NaN and need
/// <c>lo &lt;= hi</c>. Infinite bounds are allowed and mean unbounded on that side (<c>lo = -Inf</c>,
/// <c>hi = +Inf</c>); <c>lo = +Inf</c> or <c>hi = -Inf</c> is invalid. Any violation returns
/// <see cref="BoundedLeastSquaresStatus.InvalidInput"/> without throwing, with <c>iterations</c> 0 and
/// <c>x</c> exactly as passed. A NaN or infinite warm start is legal: it is clamped into the box (an infinite
/// result on an unbounded side becomes 0 clamped to the box). Spans shorter than the stated dimensions are a
/// caller bug and throw <see cref="ArgumentException"/>.
/// </para>
/// </remarks>
public static class BoundedLeastSquares
{
    /// <summary>Default iteration cap for <see cref="Solve"/>.</summary>
    public const int DefaultMaxIterations = 100;

    private const float PivotRelativeTolerance = 1e-6f;
    private const float KktRelativeTolerance = 1e-5f;

    /// <summary>Floats of caller-supplied workspace needed for <paramref name="n"/> columns.</summary>
    public static int WorkspaceLength(int n) => 2 * n * n + 9 * n;

    /// <summary>
    /// Minimises <c>||A x - b||^2</c> subject to <c>lo &lt;= x &lt;= hi</c>. <paramref name="x"/> is the warm start
    /// on entry (clamped to the box first) and the result on exit.
    /// </summary>
    /// <param name="m">Rows of <paramref name="a"/>.</param>
    /// <param name="n">Columns of <paramref name="a"/>, and length of <paramref name="lo"/>, <paramref name="hi"/> and <paramref name="x"/>.</param>
    /// <param name="a">Row-major <c>m * n</c> matrix.</param>
    /// <param name="b">Length <c>m</c> target.</param>
    /// <param name="lo">Lower bounds; each must be <c>&lt;= hi</c>; <c>-Inf</c> means unbounded below.</param>
    /// <param name="hi">Upper bounds; <c>+Inf</c> means unbounded above.</param>
    /// <param name="x">Warm start in, solution out.</param>
    /// <param name="workspace">At least <see cref="WorkspaceLength"/> floats; contents on entry are ignored.</param>
    /// <param name="iterations">Active-set iterations used.</param>
    /// <param name="maxIterations">Iteration cap.</param>
    public static BoundedLeastSquaresStatus Solve(
        int m, int n,
        ReadOnlySpan<float> a, ReadOnlySpan<float> b,
        ReadOnlySpan<float> lo, ReadOnlySpan<float> hi,
        Span<float> x, Span<float> workspace,
        out int iterations,
        int maxIterations = DefaultMaxIterations)
    {
        if (m < 0 || n < 0 || a.Length < m * n || b.Length < m || lo.Length < n || hi.Length < n
            || x.Length < n || workspace.Length < WorkspaceLength(n))
            throw new ArgumentException("BoundedLeastSquares: span shorter than the stated dimensions.");

        var ata = workspace.Slice(0, n * n);
        var chol = workspace.Slice(n * n, n * n);
        var off = 2 * n * n;
        var atb = workspace.Slice(off, n);
        var g = workspace.Slice(off + n, n);
        var p = workspace.Slice(off + 2 * n, n);
        var y = workspace.Slice(off + 3 * n, n);
        var r1 = workspace.Slice(off + 4 * n, n);
        var q = workspace.Slice(off + 5 * n, n);
        // One flag per column: 0 free, -1 at lo, +1 at hi. Free-list and dependency flags follow.
        var ints = MemoryMarshal.Cast<float, int>(workspace.Slice(off + 6 * n, 3 * n));
        var state = ints.Slice(0, n);
        var freeIndex = ints.Slice(n, n);
        var dependent = ints.Slice(2 * n, n);

        iterations = 0;
        for (var j = 0; j < n; j++)
            if (!(lo[j] <= hi[j]) || float.IsPositiveInfinity(lo[j]) || float.IsNegativeInfinity(hi[j]))
                return BoundedLeastSquaresStatus.InvalidInput;
        for (var i = 0; i < m * n; i++)
            if (!float.IsFinite(a[i]))
                return BoundedLeastSquaresStatus.InvalidInput;
        for (var i = 0; i < m; i++)
            if (!float.IsFinite(b[i]))
                return BoundedLeastSquaresStatus.InvalidInput;

        var atbMax = 0f;
        for (var j = 0; j < n; j++)
        {
            var sb = 0f;
            for (var r = 0; r < m; r++)
                sb += a[r * n + j] * b[r];
            atb[j] = sb;
            atbMax = MathF.Max(atbMax, MathF.Abs(sb));
            for (var k = 0; k <= j; k++)
            {
                var sa = 0f;
                for (var r = 0; r < m; r++)
                    sa += a[r * n + j] * a[r * n + k];
                if (!float.IsFinite(sa) || !float.IsFinite(sb))
                    return BoundedLeastSquaresStatus.InvalidInput;
                ata[j * n + k] = sa;
                ata[k * n + j] = sa;
            }
        }

        // From here x is written. A warm start that is NaN or an infinite value on an unbounded side is
        // moved onto the box; an infinite result (unbounded side) restarts from 0 clamped to the box.
        for (var j = 0; j < n; j++)
        {
            var l = lo[j];
            var h = hi[j];
            var v = x[j];
            v = !(v > l) ? l : (v < h ? v : h);
            if (float.IsInfinity(v))
                v = 0f < l ? l : (0f > h ? h : 0f);
            x[j] = v;
            state[j] = v == l ? -1 : (v == h ? 1 : 0);
        }

        // Scale-free tolerance: relative to the gradient scale of the problem, with no absolute floor.
        // When A^T b is exactly zero the start's gradient supplies the scale (zero only if x is already optimal).
        var gScale = atbMax;
        if (gScale == 0f)
        {
            Gradient(ata, atb, x, g, n);
            for (var j = 0; j < n; j++)
                gScale = MathF.Max(gScale, MathF.Abs(g[j]));
        }
        var kktTol = KktRelativeTolerance * gScale;

        while (true)
        {
            Gradient(ata, atb, x, g, n);

            var freeCount = 0;
            for (var j = 0; j < n; j++)
                if (state[j] == 0)
                    freeIndex[freeCount++] = j;
            SolveFreeSet(ata, chol, g, p, y, r1, q, freeIndex.Slice(0, freeCount), dependent, n);

            var t = 1f;
            var block = -1;
            for (var i = 0; i < freeCount; i++)
            {
                var j = freeIndex[i];
                var pj = p[j];
                if (pj == 0f) continue;
                var tj = pj > 0f ? (hi[j] - x[j]) / pj : (lo[j] - x[j]) / pj;
                if (tj < t)
                {
                    t = tj;
                    block = j;
                }
            }

            if (block >= 0)
            {
                if (iterations >= maxIterations) return BoundedLeastSquaresStatus.IterationLimit;
                iterations++;
                for (var i = 0; i < freeCount; i++)
                {
                    var j = freeIndex[i];
                    x[j] += t * p[j];
                }
                if (p[block] > 0f) { x[block] = hi[block]; state[block] = 1; }
                else { x[block] = lo[block]; state[block] = -1; }
                continue;
            }

            for (var i = 0; i < freeCount; i++)
            {
                var j = freeIndex[i];
                var v = x[j] + p[j];
                x[j] = v < lo[j] ? lo[j] : (v > hi[j] ? hi[j] : v);
            }
            Gradient(ata, atb, x, g, n);

            // Stationarity of the free set is part of KKT: float Cholesky can leave a residual gradient.
            var freeViolated = false;
            for (var i = 0; i < freeCount; i++)
                if (dependent[i] == 0 && MathF.Abs(g[freeIndex[i]]) > kktTol)
                    freeViolated = true;

            var release = -1;
            var worst = kktTol;
            for (var j = 0; j < n; j++)
            {
                if (lo[j] == hi[j]) continue;
                var violation = state[j] < 0 ? -g[j] : (state[j] > 0 ? g[j] : 0f);
                if (violation > worst)
                {
                    worst = violation;
                    release = j;
                }
            }
            if (release < 0 && !freeViolated) return BoundedLeastSquaresStatus.Converged;
            if (iterations >= maxIterations) return BoundedLeastSquaresStatus.IterationLimit;
            iterations++;
            // A stationarity miss re-solves the same free set from the improved point.
            if (release >= 0) state[release] = 0;
        }
    }

    private static void Gradient(ReadOnlySpan<float> ata, ReadOnlySpan<float> atb, ReadOnlySpan<float> x, Span<float> g, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var s = -atb[j];
            for (var k = 0; k < n; k++)
                s += ata[j * n + k] * x[k];
            g[j] = s;
        }
    }

    // Newton step over the free set: solves (A^T A)_FF p_F = -g_F by Cholesky, then one step of iterative
    // refinement with the same factor. A free column whose pivot collapses (dependent on earlier free columns)
    // is dropped from the factor and gets p = 0.
    private static void SolveFreeSet(
        ReadOnlySpan<float> ata, Span<float> l, ReadOnlySpan<float> g, Span<float> p, Span<float> y,
        Span<float> rhs, Span<float> dp, ReadOnlySpan<int> free, Span<int> dependent, int n)
    {
        var k = free.Length;
        for (var i = 0; i < k; i++)
        {
            var fi = free[i];
            var d = ata[fi * n + fi];
            for (var c = 0; c < i; c++)
                d -= l[i * n + c] * l[i * n + c];
            if (d <= PivotRelativeTolerance * ata[fi * n + fi] || d <= 0f)
            {
                dependent[i] = 1;
                for (var c = 0; c < i; c++) l[i * n + c] = 0f;
                l[i * n + i] = 1f;
                for (var r = i + 1; r < k; r++) l[r * n + i] = 0f;
                continue;
            }
            dependent[i] = 0;
            var lii = MathF.Sqrt(d);
            l[i * n + i] = lii;
            for (var r = i + 1; r < k; r++)
            {
                var s = ata[free[r] * n + fi];
                for (var c = 0; c < i; c++)
                    s -= l[r * n + c] * l[i * n + c];
                l[r * n + i] = s / lii;
            }
        }

        for (var i = 0; i < k; i++)
            rhs[i] = dependent[i] != 0 ? 0f : -g[free[i]];
        Substitute(l, rhs, y, p, free, n);

        for (var i = 0; i < k; i++)
        {
            if (dependent[i] != 0) { rhs[i] = 0f; continue; }
            var s = -g[free[i]];
            for (var c = 0; c < k; c++)
                if (dependent[c] == 0)
                    s -= ata[free[i] * n + free[c]] * p[free[c]];
            rhs[i] = s;
        }
        Substitute(l, rhs, y, dp, free, n);
        for (var i = 0; i < k; i++)
            p[free[i]] += dp[free[i]];
    }

    // Solves L L^T out = rhs; rhs is indexed by free-list position, out by column.
    private static void Substitute(ReadOnlySpan<float> l, ReadOnlySpan<float> rhs, Span<float> y, Span<float> outByColumn, ReadOnlySpan<int> free, int n)
    {
        var k = free.Length;
        for (var i = 0; i < k; i++)
        {
            var s = rhs[i];
            for (var c = 0; c < i; c++)
                s -= l[i * n + c] * y[c];
            y[i] = s / l[i * n + i];
        }
        for (var i = k - 1; i >= 0; i--)
        {
            var s = y[i];
            for (var r = i + 1; r < k; r++)
                s -= l[r * n + i] * outByColumn[free[r]];
            outByColumn[free[i]] = s / l[i * n + i];
        }
    }
}
