using System;
using System.Collections.Generic;
using System.Linq;
using CultMath;
using Xunit;

namespace CultMath.Tests;

// The stopping rule is a set of decisions, so it is pinned where it is decided. The solver reports each decision, in order and
// with the values it compared, through an internal trace sink (ISolveTrace); these tests assert that record with exact
// equality, never a status or a tolerance band. Two sources of truth:
//  - BlsBaseTraces: the same decisions as the base commit dbcb2a8e took them on the same problems, so Solve's defaults are
//    unchanged decision by decision, not only in the end;
//  - hand-built inputs to the two judgement functions, for states a solve may never reach (a release after a stationarity
//    miss with a finite stall mark) where only the decision itself can be asked.
public sealed class BoundedLeastSquaresTraceTests
{
    private static string Normal(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void EveryDecisionEqualsTheBaseCommitsOnTheSameProblem()
    {
        foreach (var c in BlsTrace.Cases())
        {
            Assert.True(BlsBaseTraces.Expected.TryGetValue(c.Name, out var expected), $"{c.Name}: no base trace");
            Assert.Equal(Normal(expected!), BlsTrace.Trace(c));
        }
        Assert.Equal(BlsTrace.Cases().Count(), BlsBaseTraces.Expected.Count);
    }

    // Each kind of decision, and the ones the stop rule's variants differ on, occurs in the recorded problems: a case table
    // that stopped exercising a decision would pin nothing about it.
    [Fact]
    public void TheCasesExerciseEveryKindOfDecision()
    {
        var lines = BlsTrace.Cases().SelectMany(c => BlsTrace.Trace(c).Split('\n')).ToList();
        foreach (var kind in Enum.GetValues<SolveEvent>())
            Assert.True(lines.Any(l => l.StartsWith(kind + " ", StringComparison.Ordinal)), $"{kind} is never recorded");
        Assert.Contains(lines, l => l.StartsWith("Release 0 ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Release 11 ", StringComparison.Ordinal));
    }

    // The sink only observes: with it, without it and through the public overload the solve is the same, bit for bit.
    [Fact]
    public void ARecordingSinkNeverChangesTheSolve()
    {
        foreach (var c in BlsTrace.Cases())
        {
            var plain = (float[])c.X0.Clone();
            var traced = (float[])c.X0.Clone();
            var viaPublic = (float[])c.X0.Clone();
            var ws = new float[BoundedLeastSquares.WorkspaceLength(c.N)];
            var s1 = BoundedLeastSquares.SolveTraced(c.M, c.N, c.A, c.B, c.Lo, c.Hi, plain, ws, out var i1, c.Tolerance, c.MaxIterations, null);
            var s2 = BoundedLeastSquares.SolveTraced(c.M, c.N, c.A, c.B, c.Lo, c.Hi, traced, ws, out var i2, c.Tolerance, c.MaxIterations, new BlsTrace.Recorder());
            var s3 = BoundedLeastSquares.Solve(c.M, c.N, c.A, c.B, c.Lo, c.Hi, viaPublic, ws, out var i3, c.Tolerance, c.MaxIterations);
            Assert.True(s1 == s2 && s2 == s3 && i1 == i2 && i2 == i3, c.Name);
            for (var j = 0; j < c.N; j++)
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(plain[j]), BitConverter.SingleToInt32Bits(traced[j]));
                Assert.Equal(BitConverter.SingleToInt32Bits(plain[j]), BitConverter.SingleToInt32Bits(viaPublic[j]));
            }
        }
    }

    private static string Judge(int release, bool freeViolated, ref double fMark, double[] x, double[] g, double[] atb, out bool over, double kktTol = 0.5)
    {
        var rec = new BlsTrace.Recorder();
        over = BoundedLeastSquares.JudgePass(release, freeViolated, kktTol, ref fMark, x, g, atb, x.Length, rec);
        return string.Join("\n", rec.Lines);
    }

    // Cost 0.5 * sum x (g - atb) = 2 and scale 0.5 * sum |x| (|g| + |atb|) = 4 for this point.
    private static readonly double[] X1 = { 2.0 };
    private static readonly double[] G1 = { 3.0 };
    private static readonly double[] Atb1 = { 1.0 };

    // A release is never judged by the cost, whichever column it is: a column-0 release with a stall mark the cost has not
    // beaten goes on, and the mark is reset to +Inf.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AReleaseOfAnyColumnIsNotJudgedByTheCostAndResetsTheMark(int column)
    {
        var x = new[] { 2.0, 2.0, 2.0 };
        var g = new[] { 3.0, 3.0, 3.0 };
        var atb = new[] { 1.0, 1.0, 1.0 };
        var mark = 1.0; // below the cost 6: a stationarity miss with this mark would be a stall
        var trace = Judge(column, true, ref mark, x, g, atb, out var over);
        Assert.False(over);
        Assert.Equal(double.PositiveInfinity, mark);
        Assert.Equal($"Release {column} Infinity 0 0", trace);
    }

    // The reset is to +Inf: not the cost (that would make the next equal cost a stall at once), not nothing.
    [Fact]
    public void AReleaseResetsTheMarkToPositiveInfinityNotToTheCostNorLeavesItStale()
    {
        var mark = 5.0;
        Judge(2, false, ref mark, X1, G1, Atb1, out var over);
        Assert.False(over);
        Assert.Equal(double.PositiveInfinity, mark);

        // After the reset a miss at the cost the stale mark would have stopped on is only recorded.
        var trace = Judge(-1, true, ref mark, X1, G1, Atb1, out over);
        Assert.False(over);
        Assert.Equal(2.0, mark);
        Assert.Equal($"Miss -1 2 Infinity {BlsTrace.F(1e-15 * 4.0)}", trace);
    }

    [Fact]
    public void AStationarityMissIsOverOnlyWhenTheCostHasNotFallenByMoreThanTheRoundingAllowance()
    {
        var allowance = 1e-15 * 4.0;

        var mark = 2.0 + 3e-15; // 2 >= mark - allowance: not enough progress
        var trace = Judge(-1, true, ref mark, X1, G1, Atb1, out var over);
        Assert.True(over);
        Assert.Equal($"Stall -1 2 {BlsTrace.F(2.0 + 3e-15)} {BlsTrace.F(allowance)}", trace);

        mark = 2.0 + 5e-15; // 2 < mark - allowance: progress, so go on and record the cost
        trace = Judge(-1, true, ref mark, X1, G1, Atb1, out over);
        Assert.False(over);
        Assert.Equal(2.0, mark);
        Assert.Equal($"Miss -1 2 {BlsTrace.F(2.0 + 5e-15)} {BlsTrace.F(allowance)}", trace);

        mark = 2.0; // equal cost is a stall
        trace = Judge(-1, true, ref mark, X1, G1, Atb1, out over);
        Assert.True(over);
        Assert.Equal($"Stall -1 2 2 {BlsTrace.F(allowance)}", trace);
    }

    // The cost and its scale are 0.5 sum x_j (g_j - atb_j) and 0.5 sum |x_j| (|g_j| + |atb_j|) over every column.
    [Fact]
    public void TheCostAndItsScaleCountEveryColumnAndTheirOwnSigns()
    {
        var x = new[] { 2.0, -1.0 };
        var g = new[] { 3.0, 1.0 };
        var atb = new[] { 1.0, 2.0 };
        var mark = double.PositiveInfinity;
        var trace = Judge(-1, true, ref mark, x, g, atb, out var over);
        Assert.False(over);
        Assert.Equal($"Miss -1 2.5 Infinity {BlsTrace.F(1e-15 * 5.5)}", trace);
        Assert.Equal(2.5, mark);
    }

    [Fact]
    public void NothingToReleaseAndAStationaryFreeSetIsKktAtOnce()
    {
        var mark = 1.0;
        var trace = Judge(-1, false, ref mark, X1, G1, Atb1, out var over, kktTol: 0.25);
        Assert.True(over);
        Assert.Equal("Kkt -1 0.25 0 0", trace);
        Assert.Equal(1.0, mark);
    }

    private static string Select(double[] ata, double[] atb, double[] x, double[] g, int[] state, float[] lo, float[] hi, double kktTol, out int release)
    {
        var rec = new BlsTrace.Recorder();
        release = BoundedLeastSquares.SelectRelease(ata, atb, x, g, state, lo, hi, kktTol, x.Length, rec);
        return string.Join("\n", rec.Lines);
    }

    // Equal violations: the first column wins, and the second is never even a candidate.
    [Fact]
    public void OfEquallyViolatedBoundsTheFirstIsReleased()
    {
        var ata = new[] { 1.0, 0.0, 0.0, 1.0 };
        var atb = new[] { 0.0, 0.0 };
        var x = new[] { 0.0, 0.0 };
        var trace = Select(ata, atb, x, new[] { -1.0, -1.0 }, new[] { -1, -1 }, new[] { 0f, 0f }, new[] { 1f, 1f }, 0.1, out var release);
        Assert.Equal(0, release);
        Assert.Equal("Candidate 0 1 0 0.1", trace);

        // The most violated wins wherever it is, and a later equal one does not take it over.
        ata = new[] { 1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0 };
        trace = Select(ata, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { -0.5, -2.0, -2.0 }, new[] { -1, -1, -1 }, new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f }, 0.1, out release);
        Assert.Equal(1, release);
        Assert.Equal("Candidate 0 0.5 0 0.1\nCandidate 1 2 0 0.5", trace);
    }

    [Fact]
    public void ABoundIsReleasedOnlyAboveBothTheToleranceAndItsRoundingBound()
    {
        var ata = new[] { 1.0 };
        var lo = new[] { 0f };
        var hi = new[] { 1f };
        var x = new[] { 0.0 };

        // violation == tolerance: not released (and not a candidate).
        var trace = Select(ata, new[] { 0.0 }, x, new[] { -0.25 }, new[] { -1 }, lo, hi, 0.25, out var release);
        Assert.Equal(-1, release);
        Assert.Equal("", trace);

        // violation == rounding bound: a candidate that is not released. The bound here is (1 + 1) eps |atb| = 2 eps.
        var noise = 2 * 2.220446049250313e-16 * 1.0;
        trace = Select(ata, new[] { 1.0 }, x, new[] { -noise }, new[] { -1 }, lo, hi, 0.0, out release);
        Assert.Equal(-1, release);
        Assert.Equal($"Candidate 0 {BlsTrace.F(noise)} {BlsTrace.F(noise)} 0", trace);

        // One step above both: released.
        var above = Math.BitIncrement(noise);
        trace = Select(ata, new[] { 1.0 }, x, new[] { -above }, new[] { -1 }, lo, hi, 0.0, out release);
        Assert.Equal(0, release);
        Assert.Equal($"Candidate 0 {BlsTrace.F(above)} {BlsTrace.F(noise)} 0", trace);

        // A bound at hi violates with a positive gradient; a fixed column (lo == hi) is never released however steep.
        trace = Select(ata, new[] { 0.0 }, new[] { 1.0 }, new[] { 4.0 }, new[] { 1 }, lo, hi, 0.5, out release);
        Assert.Equal(0, release);
        trace = Select(ata, new[] { 0.0 }, x, new[] { -9.0 }, new[] { -1 }, new[] { 0.5f }, new[] { 0.5f }, 0.5, out release);
        Assert.Equal(-1, release);
        Assert.Equal("", trace);
    }

    // gamma_(n+1) = (n + 1) eps, times the magnitude of the terms of the released column's own gradient: its |atb| and its row of
    // |ata x|.
    [Fact]
    public void TheRoundingBoundIsGammaTimesTheMagnitudeOfTheColumnsOwnTerms()
    {
        var ata = new[] { 2.0, 1.0, 1.0, 3.0 };
        var atb = new[] { 0.5, -4.0 };
        var x = new[] { 0.25, -2.0 };
        Assert.Equal(3 * 2.220446049250313e-16 * 10.25, BoundedLeastSquares.ReleaseNoise(ata, atb, x, 1, 2));
        Assert.Equal(3 * 2.220446049250313e-16 * 3.0, BoundedLeastSquares.ReleaseNoise(ata, atb, x, 0, 2));
    }
}
