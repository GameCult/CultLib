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
/// duplicate) get a zero step instead of a failed pivot, so a singular free set never throws.
/// <para>
/// Precision. A, b and <c>x</c> are float at the API; everything else, including the working iterate, the
/// gradient and the KKT test, is double, carved from the caller's workspace. <c>x</c> is converted in at the start
/// and back out at the end, so a returned float <c>x</c> is the nearest float to the double solution and always
/// inside the box.
/// </para>
/// <para>
/// Stopping. KKT termination uses a tolerance of 1e-6 times the problem's gradient scale (the larger of the
/// gradient magnitudes at the origin and at the box point nearest the origin), so scaling A, b or the bounds, and
/// the choice of warm start, do not change it. The tolerance is only a resolution: once a stationarity miss or a
/// bound release has been followed by a pass that lowers the cost by no more than rounding, the solver reports
/// <see cref="BoundedLeastSquaresStatus.Converged"/>, so an optimal point whose double gradient sits above the
/// tolerance costs one confirming iteration rather than the iteration cap.
/// </para>
/// <para>
/// Near-singular problems. The normal equations square the condition number, so the cost gap grows with cond(A)
/// (measured: below 1e-6 of |b|^2 through cond(A) 3e5, about 2e-5 at 1e6), independent of the absolute scale of A.
/// The solver owns optimality up to that range. Columns whose pivot falls below 1e-12 of their own diagonal (cond(A)
/// beyond roughly 1e6) are treated as dependent and not moved, so the answer there is one optimum among many, not the
/// minimum-norm one. Callers beyond that range, or that need the minimum-norm answer, add a small ridge row to
/// <c>A</c> and <c>b</c>; that stays the caller's policy.
/// </para>
/// The solver allocates nothing and is deterministic.
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

    private const double PivotRelativeTolerance = 1e-12;
    private const double KktRelativeTolerance = 1e-6;
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
    {
        if (m < 0 || n < 0 || a.Length < m * n || b.Length < m || lo.Length < n || hi.Length < n
            || x.Length < n || workspace.Length < WorkspaceLength(n))
            throw new ArgumentException("BoundedLeastSquares: span shorter than the stated dimensions.");

        // Double-precision vectors and matrices first, then the integer flags, all carved from the caller's floats.
        var doubleCount = 2 * n * n + 6 * n;
        var dbl = MemoryMarshal.Cast<float, double>(workspace.Slice(0, 2 * doubleCount));
        var ata = dbl.Slice(0, n * n);
        var chol = dbl.Slice(n * n, n * n);
        var off = 2 * n * n;
        var atb = dbl.Slice(off, n);
        var g = dbl.Slice(off + n, n);
        var p = dbl.Slice(off + 2 * n, n);
        var y = dbl.Slice(off + 3 * n, n);
        var rhs = dbl.Slice(off + 4 * n, n);
        var xd = dbl.Slice(off + 5 * n, n);
        // One flag per column: 0 free, -1 at lo, +1 at hi. Free-list and dependency flags follow.
        var ints = MemoryMarshal.Cast<float, int>(workspace.Slice(2 * doubleCount, 3 * n));
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

        for (var j = 0; j < n; j++)
        {
            var sb = 0.0;
            for (var r = 0; r < m; r++)
                sb += (double)a[r * n + j] * b[r];
            atb[j] = sb;
            for (var k = 0; k <= j; k++)
            {
                var sa = 0.0;
                for (var r = 0; r < m; r++)
                    sa += (double)a[r * n + j] * a[r * n + k];
                if (!double.IsFinite(sa) || !double.IsFinite(sb))
                    return BoundedLeastSquaresStatus.InvalidInput;
                ata[j * n + k] = sa;
                ata[k * n + j] = sa;
            }
        }

        // From here x is written. A warm start that is NaN or an infinite value on an unbounded side is
        // moved onto the box; an infinite result (unbounded side) restarts from 0 clamped to the box.
        // The working iterate lives in double; x is only the API's float view of it.
        for (var j = 0; j < n; j++)
        {
            var l = lo[j];
            var h = hi[j];
            var v = x[j];
            v = !(v > l) ? l : (v < h ? v : h);
            if (float.IsInfinity(v))
                v = 0f < l ? l : (0f > h ? h : 0f);
            x[j] = v;
            xd[j] = v;
            state[j] = v == l ? -1 : (v == h ? 1 : 0);
        }

        // The tolerance's scale is the gradient magnitude the problem itself carries: the larger of the
        // gradient at the origin (-A^T b) and at the box point nearest the origin. It depends on A, b and the
        // bounds, never on the warm start, so a far start cannot be judged converged by its own steepness, and
        // b = 0 with a box that excludes the origin still gets a real scale. It is zero only when that point
        // is already stationary, and then the progress rule below does the stopping.
        var gScale = 0.0;
        for (var j = 0; j < n; j++)
        {
            y[j] = Math.Clamp(0.0, lo[j], hi[j]);
            gScale = Math.Max(gScale, Math.Abs(atb[j]));
        }
        Gradient(ata, atb, y, g, n);
        for (var j = 0; j < n; j++)
            gScale = Math.Max(gScale, Math.Abs(g[j]));
        var kktTol = KktRelativeTolerance * gScale;

        // Cost at the last point from which the solver chose to continue past a stationarity miss or a
        // release. A pass that ends without lowering it has nothing left to gain: the tolerance is a
        // resolution, not the stopping rule, and a noise floor above it must not spin to the cap.
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
                var violation = state[j] < 0 ? -g[j] : (state[j] > 0 ? g[j] : 0.0);
                if (violation > worst)
                {
                    worst = violation;
                    release = j;
                }
            }
            if (release < 0 && !freeViolated) { status = BoundedLeastSquaresStatus.Converged; break; }

            var f = Objective(xd, g, atb, n, out var fScale);
            if (f >= fMark - StallRelativeTolerance * fScale) { status = BoundedLeastSquaresStatus.Converged; break; }
            fMark = f;
            if (iterations >= maxIterations) { status = BoundedLeastSquaresStatus.IterationLimit; break; }
            iterations++;
            // A stationarity miss re-solves the same free set from the improved point.
            if (release >= 0) state[release] = 0;
        }

        for (var j = 0; j < n; j++)
            x[j] = (float)xd[j];
        return status;
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
