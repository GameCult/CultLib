using CultMath;
using Xunit;

namespace CultMath.Tests;

// math.first_order_intercept_time / first_order_intercept had one happy-path test before this
// (MathTests.FirstOrderInterceptSolvesSimpleClosingTarget). Stryker's baseline on
// hands/adopt-stryker found L264 (zero relative velocity), L270 (the a-approx-zero linear
// fallback), L280/L283 (the quadratic determinant and its roots) and L285 (the t1>0 branch)
// all undefended by fixtures that never varied relative velocity, never went linear, never
// lacked a solution, and never used off-axis/off-origin shooter and target state.
//
// For the closing cases (a genuine positive root exists), the assertion is behavioural: the
// returned aim point, fired at shotSpeed from a moving shooter, and the target's own motion
// must put the shot and the target at the same place at the returned time. That is verified by
// computing both positions independently in the test from math.first_order_intercept's own
// contract (targetPosition + time * targetRelativeVelocity is the aim point; the shot's actual
// world position folds in the shooter's velocity), not by re-deriving a second copy of the
// production formula.
//
// For the no-solution branches, there is no physical coincidence to check (the fallback is not
// a root of the intercept equation), so the assertion instead pins the contract read directly
// from the source at math.cs:264-298: velocitySquared < 0.001 => 0; determinant < 0 => 0;
// determinant > 0 with both roots <= 0 => 0 (via max(t2, 0)).
public sealed class FirstOrderInterceptTests
{
    private const float Precision = 4;

    [Fact]
    public void ZeroRelativeVelocityReturnsZeroTime()
    {
        // Target and shooter share a velocity: targetRelativeVelocity is exactly zero, well
        // under the 0.001 lengthsq guard, regardless of either party's absolute speed.
        var time = math.first_order_intercept_time(
            shotSpeed: 50.0f,
            targetRelativePosition: new float3(12.0f, -7.0f, 3.0f),
            targetRelativeVelocity: float3.zero);

        Assert.Equal(0.0f, time);

        var aimPoint = math.first_order_intercept(
            shooterPosition: new float3(1.0f, 2.0f, 3.0f),
            shooterVelocity: new float3(4.0f, 4.0f, 4.0f),
            shotSpeed: 50.0f,
            targetPosition: new float3(13.0f, -5.0f, 6.0f),
            targetVelocity: new float3(4.0f, 4.0f, 4.0f));

        // Contract: with time 0, the aim point is exactly the target's current position.
        Assert.Equal(13.0f, aimPoint.x, Precision);
        Assert.Equal(-5.0f, aimPoint.y, Precision);
        Assert.Equal(6.0f, aimPoint.z, Precision);
    }

    [Fact]
    public void LinearFallbackWhenRelativeSpeedMatchesShotSpeedProducesPhysicalIntercept()
    {
        // velocitySquared == shotSpeed*shotSpeed makes a == 0 (|a| < 0.001), forcing the
        // linear branch: time = -lengthsq(pos) / (2 * dot(vel, pos)), clamped to >= 0.
        var shooterPosition = new float3(2.0f, -3.0f, 1.0f);
        var shooterVelocity = new float3(1.0f, 0.5f, -0.5f);
        var targetRelativePosition = new float3(10.0f, 0.0f, 0.0f);
        var targetRelativeVelocity = new float3(-2.0f, 0.0f, 0.0f);
        const float shotSpeed = 2.0f; // |targetRelativeVelocity| == 2 == shotSpeed => a == 0

        var time = math.first_order_intercept_time(shotSpeed, targetRelativePosition, targetRelativeVelocity);

        // -100 / (2 * -20) = 2.5, computed independently of the production formula's code path.
        Assert.Equal(2.5f, time, Precision);

        var targetPosition = shooterPosition + targetRelativePosition;
        var targetVelocity = shooterVelocity + targetRelativeVelocity;

        var aimPoint = math.first_order_intercept(shooterPosition, shooterVelocity, shotSpeed, targetPosition, targetVelocity);
        AssertShotAndTargetCoincideAtTime(shooterPosition, shooterVelocity, shotSpeed, aimPoint, targetPosition, targetVelocity, time);
    }

    [Fact]
    public void NegativeDeterminantReturnsZeroWhenTargetOutrunsTheProjectile()
    {
        // Target crossing perpendicular to the line of sight fast enough, with a slow shot,
        // that no real root of the intercept quadratic exists.
        var time = math.first_order_intercept_time(
            shotSpeed: 1.0f,
            targetRelativePosition: new float3(10.0f, 0.0f, 0.0f),
            targetRelativeVelocity: new float3(0.0f, 20.0f, 0.0f));

        Assert.Equal(0.0f, time);
    }

    [Fact]
    public void RecedingTargetWithBothRootsNonPositiveReturnsZero()
    {
        // Target moving directly away from the shooter, faster than a slow shot: a real
        // determinant exists but both quadratic roots are negative, so t1 > 0 is false and the
        // function falls through to max(t2, 0) == 0.
        var time = math.first_order_intercept_time(
            shotSpeed: 1.0f,
            targetRelativePosition: new float3(10.0f, 0.0f, 0.0f),
            targetRelativeVelocity: new float3(5.0f, 0.0f, 0.0f));

        Assert.Equal(0.0f, time);
    }

    [Fact]
    public void ClosingCaseWithOffOriginOffAxisPositionsProducesPhysicallyConsistentIntercept()
    {
        // Neither shooter nor target sits at the origin, no vector is axis-aligned, and both
        // parties are moving. This is the fixture shape ("degenerate fixtures are how these
        // survived") the earlier single test never exercised.
        var shooterPosition = new float3(2.0f, 3.0f, -1.0f);
        var shooterVelocity = new float3(1.0f, -1.0f, 0.5f);
        var shotSpeed = 10.0f;
        var targetPosition = new float3(15.0f, 8.0f, 4.0f);
        var targetVelocity = new float3(-3.0f, 2.0f, 1.0f);

        var targetRelativePosition = targetPosition - shooterPosition;
        var targetRelativeVelocity = targetVelocity - shooterVelocity;
        var time = math.first_order_intercept_time(shotSpeed, targetRelativePosition, targetRelativeVelocity);

        Assert.True(time > 0.0f, "fixture must produce a genuine closing solution");

        var aimPoint = math.first_order_intercept(shooterPosition, shooterVelocity, shotSpeed, targetPosition, targetVelocity);
        AssertShotAndTargetCoincideAtTime(shooterPosition, shooterVelocity, shotSpeed, aimPoint, targetPosition, targetVelocity, time);
    }

    /// <summary>
    /// Fires from <paramref name="shooterPosition"/>, inheriting <paramref name="shooterVelocity"/>
    /// and adding <paramref name="shotSpeed"/> toward <paramref name="aimPoint"/>, and asserts the
    /// shot's world position at <paramref name="time"/> coincides with the target's own actual
    /// motion (<paramref name="targetPosition"/> + time * <paramref name="targetVelocity"/>).
    /// This is the behavioural contract first_order_intercept exists to serve: aim here, and the
    /// target will be there when the shot arrives.
    /// </summary>
    private static void AssertShotAndTargetCoincideAtTime(
        float3 shooterPosition,
        float3 shooterVelocity,
        float shotSpeed,
        float3 aimPoint,
        float3 targetPosition,
        float3 targetVelocity,
        float time)
    {
        var direction = math.normalize(aimPoint - shooterPosition);
        var shotVelocity = shooterVelocity + shotSpeed * direction;
        var shotPositionAtTime = shooterPosition + time * shotVelocity;
        var targetPositionAtTime = targetPosition + time * targetVelocity;

        Assert.Equal(targetPositionAtTime.x, shotPositionAtTime.x, Precision);
        Assert.Equal(targetPositionAtTime.y, shotPositionAtTime.y, Precision);
        Assert.Equal(targetPositionAtTime.z, shotPositionAtTime.z, Precision);
    }
}
