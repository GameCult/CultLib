using System.Runtime.InteropServices;

namespace CultMath;

/// <summary>Outcome of a <see cref="BoundedLeastSquares"/> solve.</summary>
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
/// duplicate) get a zero step instead of a failed pivot, so a singular free set never throws.
/// <para>
/// Precision. A, b and <c>x</c> are float at the API; everything else, including the working iterate, the
/// gradient and the KKT test, is double, carved from the caller's workspace. <c>x</c> is converted in at the start
/// and back out at the end, so a returned float <c>x</c> is the nearest float to the double solution and always
/// inside the box.
/// </para>
/// <para>
/// Stopping. KKT termination uses a tolerance of <see cref="DefaultKktRelativeTolerance"/> (1e-6) times the problem's
/// gradient scale (the largest gradient component at the box point nearest the origin), so scaling A, b or the
/// bounds, and the choice of warm start, do not change it. A caller whose rows span many decades of weight can pass
/// a smaller <c>kktRelativeTolerance</c>, which changes only this tolerance: the rounding bound and progress rule
/// below and the pivot threshold are fixed. A bound is released only when its violation exceeds both the tolerance
/// and the rounding bound of its double gradient, so a tolerance below the noise cannot cycle on a release. The
/// tolerance is otherwise only a resolution: once a stationarity miss that releases nothing has been followed by a
/// pass that lowers the cost by no more than rounding, the solver reports
/// <see cref="BoundedLeastSquaresStatus.Converged"/>, so a free set whose residual is rounding costs one confirming
/// iteration rather than the iteration cap. A pass that releases a bound is never judged by the cost.
/// </para>
/// <para>
/// Near-singular problems. The normal equations square the condition number, so the cost gap grows with cond(A)
/// (measured: below 1e-6 of |b|^2 through cond(A) 3e5, about 2e-5 at 1e6; the gap is relative to |b|^2), independent of the absolute scale of A.
/// The solver owns optimality up to that range. Columns whose pivot falls below 1e-12 of their own diagonal (cond(A)
/// beyond roughly 1e6) are treated as dependent and not moved, so the answer there is one optimum among many, not the
/// minimum-norm one. Callers beyond that range, or that need the minimum-norm answer, add a small ridge row to
/// <c>A</c> and <c>b</c>; that stays the caller's policy.
/// </para>
/// The solver allocates nothing and is deterministic.
/// <para>
/// Input contract: <c>kktRelativeTolerance</c> must be finite and greater than zero; <c>a</c> and <c>b</c> must be finite; <c>lo</c> and <c>hi</c> must not be NaN and need
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
    /// <summary>Default iteration cap for <c>Solve</c>.</summary>
    public const int DefaultMaxIterations = 100;

    /// <summary>Default KKT tolerance, relative to the gradient scale; see Stopping in the type remarks.</summary>
    public const double DefaultKktRelativeTolerance = 1e-6;

    private const double PivotRelativeTolerance = 1e-12;
    private const double StallRelativeTolerance = 1e-15;

    /// <summary>Floats of caller-supplied workspace needed for <paramref name="n"/> columns.</summary>
    public static int WorkspaceLength(int n) => 2 * (2 * n * n + 6 * n) + 3 * n;

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
        => Solve(m, n, a, b, lo, hi, x, workspace, out iterations, DefaultKktRelativeTolerance, maxIterations);

    /// <summary>
    /// Minimises <c>||A x - b||^2</c> subject to <c>lo &lt;= x &lt;= hi</c>. <paramref name="x"/> is the warm start
    /// on entry (clamped to the box first) and the result on exit. <paramref name="kktRelativeTolerance"/> is the
    /// stationarity resolution relative to the gradient scale defined under Stopping; the overload without it passes
    /// <see cref="DefaultKktRelativeTolerance"/> and gives the same result.
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
    /// <param name="kktRelativeTolerance">Finite and greater than zero, else <see cref="BoundedLeastSquaresStatus.InvalidInput"/>. Tighten it when rows span many decades of weight; releases below the gradient's rounding bound are not taken, and free-set residuals at rounding level end on the progress rule.</param>
    /// <param name="maxIterations">Iteration cap.</param>
    public static BoundedLeastSquaresStatus Solve(
        int m, int n,
        ReadOnlySpan<float> a, ReadOnlySpan<float> b,
        ReadOnlySpan<float> lo, ReadOnlySpan<float> hi,
        Span<float> x, Span<float> workspace,
        out int iterations,
        double kktRelativeTolerance,
        int maxIterations = DefaultMaxIterations)
    {
        if (m < 0 || n < 0 || a.Length < m * n || b.Length < m || lo.Length < n || hi.Length < n
            || x.Length < n || workspace.Length < WorkspaceLength(n))
            throw new ArgumentException("BoundedLeastSquares: span shorter than the stated dimensions.");

        // Double-precision vectors and matrices first, then the integer flags, all carved from the caller's floats.
        var doubleCount = 2 * n * n + 6 * n;
        var dbl = MemoryMarshal.Cast<float, double>(workspace.Slice(0, 2 * doubleCount));
        var ata = Take(ref dbl, n * n);
        var chol = Take(ref dbl, n * n);
        var atb = Take(ref dbl, n);
        var g = Take(ref dbl, n);
        var p = Take(ref dbl, n);
        var y = Take(ref dbl, n);
        var rhs = Take(ref dbl, n);
        var xd = Take(ref dbl, n);
        // One flag per column: 0 free, -1 at lo, +1 at hi. Free-list and dependency flags follow.
        var ints = MemoryMarshal.Cast<float, int>(workspace.Slice(2 * doubleCount, 3 * n));
        var state = Take(ref ints, n);
        var freeIndex = Take(ref ints, n);
        var dependent = Take(ref ints, n);

        iterations = 0;
        if (!(kktRelativeTolerance > 0.0) || double.IsPositiveInfinity(kktRelativeTolerance))
            return BoundedLeastSquaresStatus.InvalidInput;
        for (var j = 0; j < n; j++)
            if (!(lo[j] <= hi[j]) || float.IsPositiveInfinity(lo[j]) || float.IsNegativeInfinity(hi[j]))
                return BoundedLeastSquaresStatus.InvalidInput;

        // A float is never large enough to overflow a double product or a sum of tens of them, so a non-finite
        // A^T b entry is exactly a non-finite entry of A or b (NaN and infinity never cancel to a finite value).
        for (var j = 0; j < n; j++)
        {
            var sb = 0.0;
            for (var r = 0; r < m; r++)
                sb += (double)a[r * n + j] * b[r];
            if (!double.IsFinite(sb))
                return BoundedLeastSquaresStatus.InvalidInput;
            atb[j] = sb;
            for (var k = 0; k <= j; k++)
            {
                var sa = 0.0;
                for (var r = 0; r < m; r++)
                    sa += (double)a[r * n + j] * a[r * n + k];
                ata[j * n + k] = sa;
                ata[k * n + j] = sa;
            }
        }

        // From here x is written. A warm start that is NaN is moved to the lower bound, and an infinite result
        // (an unbounded side) restarts from 0 clamped to the box. The working iterate lives in double; x is only
        // the API's float view of it.
        for (var j = 0; j < n; j++)
        {
            var l = lo[j];
            var h = hi[j];
            var v = float.IsNaN(x[j]) ? l : Math.Clamp(x[j], l, h);
            if (float.IsInfinity(v))
                v = Math.Clamp(0f, l, h);
            x[j] = v;
            xd[j] = v;
            state[j] = v == l ? -1 : (v == h ? 1 : 0);
        }

        // The tolerance's scale is the gradient magnitude at the box point nearest the origin (the origin itself
        // when it is inside the box, where the gradient is -A^T b). It depends on A, b and the bounds, never on the
        // warm start, so a far start cannot be judged converged by its own steepness, and b = 0 with a box that
        // excludes the origin still gets a real scale. It is zero only when that point is already stationary, and
        // then the progress rule below does the stopping.
        var gScale = 0.0;
        for (var j = 0; j < n; j++)
        {
            var s = -atb[j];
            for (var k = 0; k < n; k++)
                s += ata[j * n + k] * Math.Clamp(0.0, lo[k], hi[k]);
            gScale = Math.Max(gScale, Math.Abs(s));
        }
        var kktTol = kktRelativeTolerance * gScale;

        // Cost at the last point from which the solver continued past a stationarity miss with no release; a
        // release resets it. A stationarity miss that ends without lowering it has nothing left to gain: the
        // tolerance is a resolution, not the stopping rule, and a noise floor above it must not spin to the cap.
        var fMark = double.PositiveInfinity;
        var status = BoundedLeastSquaresStatus.IterationLimit;
        while (true)
        {
            Gradient(ata, atb, xd, g, n);

            var freeCount = 0;
            for (var j = 0; j < n; j++)
                if (state[j] == 0)
                    freeIndex[freeCount++] = j;
            SolveFreeSet(ata, chol, g, p, y, rhs, freeIndex.Slice(0, freeCount), dependent, n);

            var t = 1.0;
            var block = -1;
            for (var i = 0; i < freeCount; i++)
            {
                var j = freeIndex[i];
                var pj = p[j];
                if (pj == 0.0) continue;
                var tj = pj > 0.0 ? (hi[j] - xd[j]) / pj : (lo[j] - xd[j]) / pj;
                if (tj < t)
                {
                    t = tj;
                    block = j;
                }
            }

            if (block >= 0)
            {
                if (iterations >= maxIterations) { status = BoundedLeastSquaresStatus.IterationLimit; break; }
                iterations++;
                for (var i = 0; i < freeCount; i++)
                {
                    var j = freeIndex[i];
                    xd[j] = Math.Clamp(xd[j] + t * p[j], lo[j], hi[j]);
                }
                if (p[block] > 0.0) { xd[block] = hi[block]; state[block] = 1; }
                else { xd[block] = lo[block]; state[block] = -1; }
                continue;
            }

            for (var i = 0; i < freeCount; i++)
            {
                var j = freeIndex[i];
                xd[j] = Math.Clamp(xd[j] + p[j], lo[j], hi[j]);
            }
            Gradient(ata, atb, xd, g, n);

            // Stationarity of the free set is part of KKT: a Newton step on a near-singular free set can leave a residual gradient.
            var freeViolated = false;
            for (var i = 0; i < freeCount; i++)
                if (Math.Abs(g[freeIndex[i]]) > kktTol)
                    freeViolated = true;

            var release = -1;
            var worst = kktTol;
            for (var j = 0; j < n; j++)
            {
                if (lo[j] == hi[j]) continue;
                // A bound at lo wants a positive gradient and one at hi a negative one; a free column has state 0.
                var violation = state[j] * g[j];
                if (violation > worst && violation > ReleaseNoise(ata, atb, xd, j, n))
                {
                    worst = violation;
                    release = j;
                }
            }
            if (release < 0 && !freeViolated) { status = BoundedLeastSquaresStatus.Converged; break; }

            if (release < 0)
            {
                var f = Objective(xd, g, atb, n, out var fScale);
                if (f >= fMark - StallRelativeTolerance * fScale) { status = BoundedLeastSquaresStatus.Converged; break; }
                fMark = f;
            }
            else
                fMark = double.PositiveInfinity;
            if (iterations >= maxIterations) { status = BoundedLeastSquaresStatus.IterationLimit; break; }
            iterations++;
            // A stationarity miss re-solves the same free set from the improved point.
            if (release >= 0) state[release] = 0;
        }

        for (var j = 0; j < n; j++)
            x[j] = (float)xd[j];
        return status;
    }

    private static Span<T> Take<T>(ref Span<T> pool, int length)
    {
        var head = pool.Slice(0, length);
        pool = pool.Slice(length);
        return head;
    }

    // 0.5 x^T A^T A x - x^T A^T b (the cost up to a constant), from the gradient at x; scale is the magnitude of
    // the terms summed, which is what the rounding error of the sum is proportional to.
    private static double Objective(ReadOnlySpan<double> x, ReadOnlySpan<double> g, ReadOnlySpan<double> atb, int n, out double scale)
    {
        var f = 0.0;
        scale = 0.0;
        for (var j = 0; j < n; j++)
        {
            f += 0.5 * x[j] * (g[j] - atb[j]);
            scale += 0.5 * Math.Abs(x[j]) * (Math.Abs(g[j]) + Math.Abs(atb[j]));
        }
        return f;
    }

    // Rounding bound of the double gradient component j: gamma_(n+1) (Higham, inner products) times the magnitude of its terms.
    private static double ReleaseNoise(ReadOnlySpan<double> ata, ReadOnlySpan<double> atb, ReadOnlySpan<double> x, int j, int n)
    {
        var s = Math.Abs(atb[j]);
        for (var k = 0; k < n; k++) s += Math.Abs(ata[j * n + k] * x[k]);
        return (n + 1) * 2.220446049250313e-16 * s;
    }

    private static void Gradient(ReadOnlySpan<double> ata, ReadOnlySpan<double> atb, ReadOnlySpan<double> x, Span<double> g, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var s = -atb[j];
            for (var k = 0; k < n; k++)
                s += ata[j * n + k] * x[k];
            g[j] = s;
        }
    }

    // Newton step over the free set: solves (A^T A)_FF p_F = -g_F by Cholesky. A free column whose pivot collapses
    // (dependent on earlier free columns) is dropped from the factor and gets p = 0. No refinement step: Cholesky is
    // backward stable, so the residual, which is the gradient the KKT test reads, is already at rounding level, and
    // refining in the same precision does not improve the forward error.
    private static void SolveFreeSet(
        ReadOnlySpan<double> ata, Span<double> l, ReadOnlySpan<double> g, Span<double> p, Span<double> y,
        Span<double> rhs, ReadOnlySpan<int> free, Span<int> dependent, int n)
    {
        var k = free.Length;
        for (var i = 0; i < k; i++)
        {
            var fi = free[i];
            var d = ata[fi * n + fi];
            for (var c = 0; c < i; c++)
                d -= l[i * n + c] * l[i * n + c];
            if (d <= PivotRelativeTolerance * ata[fi * n + fi])
            {
                dependent[i] = 1;
                for (var c = 0; c < i; c++) l[i * n + c] = 0.0;
                l[i * n + i] = 1.0;
                for (var r = i + 1; r < k; r++) l[r * n + i] = 0.0;
                continue;
            }
            dependent[i] = 0;
            var lii = Math.Sqrt(d);
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
            rhs[i] = dependent[i] != 0 ? 0.0 : -g[free[i]];
        Substitute(l, rhs, y, p, free, n);
    }

    // Solves L L^T out = rhs; rhs is indexed by free-list position, out by column.
    private static void Substitute(ReadOnlySpan<double> l, ReadOnlySpan<double> rhs, Span<double> y, Span<double> outByColumn, ReadOnlySpan<int> free, int n)
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
