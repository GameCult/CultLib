using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CultMath;

namespace CultMath.Tests;

// The solver's decisions, recorded in order through the internal ISolveTrace seam, and the problems they are recorded on.
// BlsTrace is the only place that knows how a trace is written; BoundedLeastSquaresTraceTests compares it with the trace
// the base commit (dbcb2a8e) produced for the same problems, and with hand-built decision inputs.
internal static class BlsTrace
{
    internal sealed class Recorder : ISolveTrace
    {
        public readonly List<string> Lines = new();

        public void Record(SolveEvent kind, int column, double a, double b, double c)
            => Lines.Add($"{kind} {column} {F(a)} {F(b)} {F(c)}");
    }

    // Round-trip text: equal strings are equal doubles.
    internal static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    internal sealed record Case(string Name, int M, int N, float[] A, float[] B, float[] Lo, float[] Hi, float[] X0, double Tolerance, int MaxIterations);

    // The solve of one case: every decision, then the status, the iteration count and the bits of x.
    internal static string Trace(Case c)
    {
        var rec = new Recorder();
        var x = (float[])c.X0.Clone();
        var status = BoundedLeastSquares.SolveTraced(c.M, c.N, c.A, c.B, c.Lo, c.Hi, x,
            new float[BoundedLeastSquares.WorkspaceLength(c.N)], out var iterations, c.Tolerance, c.MaxIterations, rec);
        var sb = new StringBuilder();
        foreach (var line in rec.Lines) sb.Append(line).Append('\n');
        sb.Append($"end {status} {iterations}");
        foreach (var v in x) sb.Append(' ').Append(BitConverter.SingleToInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static float[] Fill(float value, int n)
    {
        var v = new float[n];
        Array.Fill(v, value);
        return v;
    }

    private static Case Make(string name, int m, int n, float[] a, float[] b, float[] lo, float[] hi, float[]? x0 = null,
        double tol = BoundedLeastSquares.DefaultKktRelativeTolerance, int max = BoundedLeastSquares.DefaultMaxIterations)
        => new(name, m, n, a, b, lo, hi, x0 ?? (float[])lo.Clone(), tol, max);

    internal static IEnumerable<Case> Cases()
    {
        // Equal violations: which column is released first is the tie-break (min |x0 + x1 - 1| on [0,1]^2, then three copies).
        yield return Make("tie-pair", 1, 2, new[] { 1f, 1f }, new[] { 1f }, new float[2], Fill(1f, 2));
        yield return Make("tie-triple", 2, 3, Fill(1f, 6), new[] { 1f, 1f }, new float[3], Fill(1f, 3));
        yield return Make("tie-pair-at-hi", 1, 2, new[] { 1f, 1f }, new[] { -1f }, new float[2], Fill(1f, 2), Fill(1f, 2));

        // A step that lands exactly on a bound is a full step, not a blocked one.
        yield return Make("step-lands-on-bound", 1, 1, new[] { 1f }, new[] { 1f }, new[] { 0f }, new[] { 1f }, new[] { 0.5f });
        yield return Make("step-blocked", 2, 2, new[] { 1f, 0f, 0f, 1f }, new[] { 3f, -0.5f }, new[] { 0f, -1f }, new[] { 1f, 1f }, new[] { 0.5f, 0.5f });

        // Soul's W5, with throttle 7 released from every column position in turn (column 0 included).
        foreach (var shift in new[] { 0, 1, 5, 7, 11 })
        {
            var (m, n, a, b, lo, hi) = AllocatorW5(shift);
            yield return Make($"w5-shift{shift}-1e-12", m, n, a, b, lo, hi, new float[n], 1e-12);
        }
        {
            var (m, n, a, b, lo, hi) = AllocatorW5(0);
            yield return Make("w5-default", m, n, a, b, lo, hi, new float[n]);
            yield return Make("w5-1e-9", m, n, a, b, lo, hi, new float[n], 1e-9);
            yield return Make("w5-cap-3", m, n, a, b, lo, hi, new float[n], 1e-12, 3);
        }

        // A release followed by a stationarity miss: the first miss is not yet a stall.
        yield return Make("confirm-pass-1", 4, 2,
            new[] { -0.0067761745f, -207.21786f, -0.004115388f, -751.1994f, -8.1700025E-05f, -481.7794f, 0.0038280683f, 480.12833f },
            new[] { 1.7477255f, 2.4914713f, -2.3861952f, 2.187128f }, new[] { -1f, -0.25f }, new[] { 1f, 0.1f }, tol: 1e-12);
        yield return Make("confirm-pass-2", 5, 2,
            new[] { 70.07762f, 5.7015634f, 111.790436f, -12.586686f, 75.29796f, 12.872999f, -402.25464f, -1.8093401f, -524.51056f, -8.649797f },
            new[] { -1.8312539f, 2.9556413f, 0.7550676f, -2.1365998f, 2.30717f }, new[] { -1f, 0f }, new[] { 1f, 1f }, tol: 1e-12);

        // The noise-level problems: columns rescaled so the gate is the only thing between a release and a spin.
        {
            const int m = 2, n = 6;
            var a0 = new[]
            {
                1.0213068f, 0.97530603f, -0.5673449f, -0.64136946f, -0.07432885f, -0.35571396f,
                0.4326824f, 1.1969734f, -0.1343108f, -0.23431005f, -1.4009786f, 0.10656489f,
            };
            var lo0 = new[] { -0.44782114f, 0f, -0.008422324f, -0.03878026f, 0f, -6.6434336f };
            var hi0 = new[] { 0.0448007f, 0f, 0.005942123f, 0.0785512f, 0f, 0.23079813f };
            var x0 = new[] { -0.28347927f, 0f, 0.0051532285f, -0.03878026f, 0f, 0.23079813f };
            foreach (var (name, scale) in new (string, float[])[]
            {
                ("one-heavy", new[] { 1f, 1f, 1e4f, 1f, 1f, 1f }),
                ("one-light", new[] { 1e3f, 1e3f, 1e-3f, 1e3f, 1e3f, 1e3f }),
                ("ascending", new[] { 1f, 1e1f, 1e2f, 1e3f, 1e4f, 1e5f }),
            })
            {
                var a = new float[m * n];
                var lo = new float[n];
                var hi = new float[n];
                var x = new float[n];
                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < m; i++) a[i * n + j] = a0[i * n + j] * scale[j];
                    lo[j] = lo0[j] / scale[j];
                    hi[j] = hi0[j] / scale[j];
                    x[j] = Math.Clamp(x0[j] / scale[j], lo[j], hi[j]);
                }
                yield return Make($"noise-{name}", m, n, a, new float[m], lo, hi, x);
            }
        }

        // Near-singular and below-the-noise-floor free sets: stationarity misses and stalls.
        foreach (var seed in new uint[] { 3, 11 })
        {
            var (a, b) = ConditionedProblem(28, 24, 3e7, seed);
            yield return Make($"near-singular-{seed}", 28, 24, a, b, Fill(-1e9f, 24), Fill(1e9f, 24), new float[24]);
        }
        foreach (var seed in new uint[] { 2, 9 })
        {
            var (a, b) = ConditionedProblem(28, 24, 1e7, seed, 1.0, bAlongSmallestSingularVector: true);
            yield return Make($"below-noise-{seed}", 28, 24, a, b, Fill(-1e9f, 24), Fill(1e9f, 24), new float[24]);
        }
        {
            var (a, b) = ConditionedProblem(12, 8, 1e5, 4);
            yield return Make("boxed-conditioned", 12, 8, a, b, Fill(-0.05f, 8), Fill(0.05f, 8), new float[8], 1e-9);
        }

        // Boxed random problems, cold and from a far warm start.
        foreach (var seed in new uint[] { 5, 17, 40 })
        {
            var rng = new CultMath.Random(seed * 7919u);
            var n = rng.NextInt(2, 9);
            var m = rng.NextInt(n, 14);
            var a = new float[m * n];
            var b = new float[m];
            for (var i = 0; i < a.Length; i++) a[i] = rng.NextFloat(-1f, 1f);
            for (var i = 0; i < m; i++) b[i] = rng.NextFloat(-3f, 3f);
            var lo = new float[n];
            var hi = new float[n];
            for (var j = 0; j < n; j++)
            {
                lo[j] = rng.NextInt(2) == 0 ? -1f : 0f;
                hi[j] = rng.NextInt(2) == 0 ? 1f : 0.1f;
            }
            yield return Make($"random-{seed}", m, n, a, b, lo, hi, new float[n]);
            yield return Make($"random-{seed}-warm-hi", m, n, a, b, lo, hi, (float[])hi.Clone());
        }
    }

    // A = U diag(s) V^T with s geometric from 1 down to 1/cond, so cond(A) is the requested value (float rounding of A
    // perturbs it slightly). m x n with m >= n; b is generic, so it has a real residual when m > n.
    internal static (float[] a, float[] b) ConditionedProblem(int m, int n, double cond, uint seed, double aScale = 1.0, bool bAlongSmallestSingularVector = false)
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
        for (var i = 0; i < m; i++) b[i] = bAlongSmallestSingularVector ? (float)u[n - 1][i] : rng.NextFloat(-3f, 3f);
        return (a, b);
    }

    // Column j of the returned problem is column (j - shift) mod 12 of Soul's W5.
    internal static (int m, int n, float[] a, float[] b, float[] lo, float[] hi) AllocatorW5(int shift)
    {
        const int m = 15, n = 12;
        var w = new float[m * n];
        w[0 * n + 2] = 100f; w[0 * n + 8] = -100f; w[0 * n + 9] = 100f;
        var row1 = new[] { 13.882518f, 20.86695f, 0f, 14.8965845f, 12.38046f, 22.115599f, -13.706654f, 15.85789f, 0f, 0f, -100f, 100f };
        var row2 = new[] { -187.72859f, 239.31168f, 158.87828f, -270.31165f, 235.38467f, -14.704258f, 121.61232f, 244.81305f };
        for (var j = 0; j < n; j++) w[1 * n + j] = row1[j];
        for (var j = 0; j < row2.Length; j++) w[2 * n + j] = row2[j];
        for (var k = 0; k < 4; k++) w[(3 + k) * n + 8 + k] = 0.3f;
        for (var i = 0; i < 8; i++) w[(7 + i) * n + i] = 0.1f;
        var b = new float[m];
        b[1] = 67.385445f; b[2] = 558.70917f;
        for (var k = 0; k < 4; k++) b[3 + k] = -3f;
        var lo = new float[n];
        var hi = new float[n];
        var a = new float[m * n];
        for (var j = 0; j < n; j++)
        {
            var to = (j + shift) % n;
            hi[to] = j < 8 ? 1f : 2f;
            for (var r = 0; r < m; r++) a[r * n + to] = w[r * n + j];
        }
        return (m, n, a, b, lo, hi);
    }
}
