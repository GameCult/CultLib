using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

// Stryker's mtp baseline on hands/adopt-stryker found BatchMath undefended in three ways:
// both existing tests used 2-3 particles while LaneCount >= 4, so the vector body never ran
// and only the scalar tail executed; both held y at 0 for every particle, so every y-axis
// mutation was invisible; and the guard branches (early return, negative deltaTime, length
// mismatch) were never exercised. These tests read LaneCount at runtime, use
// LaneCount * 3 + 1 particles (a non-empty vector body AND a non-empty remainder), give every
// particle distinct nonzero x and y, and pin results against a scalar reference computed here
// rather than by calling the code under test.
public sealed class BatchMathTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void RadialFalloffMatchesScalarReferenceAcrossVectorBodyAndRemainder()
    {
        var lanes = BatchMath.LaneCount;
        var count = lanes * 3 + 1;
        Assert.True(count > lanes, "fixture must exceed one lane to exercise the vector body");
        Assert.True((count % lanes) != 0, "fixture must leave a non-empty scalar remainder");

        var px = new float[count];
        var py = new float[count];
        var ax = new float[count];
        var ay = new float[count];
        // Distinct, asymmetric per-particle values: x and y follow different formulas so an
        // x/y swap mutation is observable, and no particle sits at the falloff center.
        for (var i = 0; i < count; i++)
        {
            px[i] = i * 1.3f - 4.0f;
            py[i] = i * -0.7f + 2.5f;
        }

        const float centerX = 3.0f;
        const float centerY = -1.0f;
        const float strength = 5.0f;
        // Chosen so some particles fall inside the radius and some fall outside it, exercising
        // both the ConditionalSelect(inRange, ...) vector path and the scalar "continue".
        const float radius = 6.0f;

        // The scalar remainder is exactly one particle (index count-1). The generator above puts
        // it far outside the radius, which would leave any y-axis mutation in the scalar tail's
        // "in range" arithmetic unobserved (the only remainder particle never reaches it). Pin it
        // inside the radius with a nonzero, non-degenerate y offset instead.
        px[count - 1] = centerX + 2.0f;
        py[count - 1] = centerY + 1.5f;

        BatchMath.AddRadialFalloffAcceleration2D(px, py, centerX, centerY, strength, radius, ax, ay);

        var insideCount = 0;
        var outsideCount = 0;
        for (var i = 0; i < count; i++)
        {
            var dx = centerX - px[i];
            var dy = centerY - py[i];
            var distanceSquared = Math.Max(dx * dx + dy * dy, 1.0e-8f);
            var distance = MathF.Sqrt(distanceSquared);

            if (distance > radius)
            {
                outsideCount++;
                Assert.Equal(0.0f, ax[i], Tolerance);
                Assert.Equal(0.0f, ay[i], Tolerance);
                continue;
            }

            insideCount++;
            var falloff = Math.Max(0.0f, 1.0f - distance / radius);
            var magnitude = strength * falloff;
            var expectedAx = dx / distance * magnitude;
            var expectedAy = dy / distance * magnitude;
            Assert.Equal(expectedAx, ax[i], Tolerance);
            Assert.Equal(expectedAy, ay[i], Tolerance);
        }

        Assert.True(insideCount > 0, "fixture must include particles inside the radius");
        Assert.True(outsideCount > 0, "fixture must include particles outside the radius");
    }

    [Fact]
    public void RadialFalloffAccumulatesOntoExistingAcceleration()
    {
        // Guards against a mutation that assigns instead of accumulating: seed a nonzero
        // acceleration and confirm the falloff contribution is added to it, not overwritten.
        var px = new[] { 0.0f };
        var py = new[] { 0.0f };
        var ax = new[] { 10.0f };
        var ay = new[] { -20.0f };

        BatchMath.AddRadialFalloffAcceleration2D(px, py, 1.0f, 0.0f, 4.0f, 8.0f, ax, ay);

        var dx = 1.0f - 0.0f;
        var dy = 0.0f - 0.0f;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        var falloff = Math.Max(0.0f, 1.0f - distance / 8.0f);
        var magnitude = 4.0f * falloff;
        Assert.Equal(10.0f + dx / distance * magnitude, ax[0], Tolerance);
        Assert.Equal(-20.0f + dy / distance * magnitude, ay[0], Tolerance);
    }

    [Fact]
    public void RadialFalloffValidatesEqualSpanLengths()
    {
        var px = new float[3];
        var py = new float[2];
        var ax = new float[3];
        var ay = new float[3];

        Assert.Throws<ArgumentException>(() =>
            BatchMath.AddRadialFalloffAcceleration2D(px, py, 0.0f, 0.0f, 1.0f, 1.0f, ax, ay));
    }

    [Fact]
    public void RadialFalloffIsNoOpWhenRadiusIsNonPositive()
    {
        var px = new[] { 1.0f, 2.0f };
        var py = new[] { 3.0f, -4.0f };
        var ax = new[] { 5.0f, 6.0f };
        var ay = new[] { 7.0f, 8.0f };

        BatchMath.AddRadialFalloffAcceleration2D(px, py, 0.0f, 0.0f, 9.0f, 0.0f, ax, ay);

        Assert.Equal(new[] { 5.0f, 6.0f }, ax);
        Assert.Equal(new[] { 7.0f, 8.0f }, ay);
    }

    [Fact]
    public void RadialFalloffIsNoOpWhenStrengthIsZero()
    {
        var px = new[] { 1.0f, 2.0f };
        var py = new[] { 3.0f, -4.0f };
        var ax = new[] { 5.0f, 6.0f };
        var ay = new[] { 7.0f, 8.0f };

        BatchMath.AddRadialFalloffAcceleration2D(px, py, 0.0f, 0.0f, 0.0f, 10.0f, ax, ay);

        Assert.Equal(new[] { 5.0f, 6.0f }, ax);
        Assert.Equal(new[] { 7.0f, 8.0f }, ay);
    }

    [Fact]
    public void RadialFalloffIsNoOpWhenSpansAreEmpty()
    {
        var px = Array.Empty<float>();
        var py = Array.Empty<float>();
        var ax = Array.Empty<float>();
        var ay = Array.Empty<float>();

        // Must not throw for a valid but degenerate zero-length batch.
        BatchMath.AddRadialFalloffAcceleration2D(px, py, 0.0f, 0.0f, 1.0f, 1.0f, ax, ay);

        Assert.Empty(ax);
        Assert.Empty(ay);
    }

    [Fact]
    public void EulerIntegrationMatchesScalarReferenceAcrossVectorBodyAndRemainder()
    {
        var lanes = BatchMath.LaneCount;
        var count = lanes * 3 + 1;
        Assert.True((count % lanes) != 0, "fixture must leave a non-empty scalar remainder");

        const float deltaTime = 0.25f;
        var dynamicMask = new float[count];
        var px = new float[count];
        var py = new float[count];
        var vx = new float[count];
        var vy = new float[count];
        var ax = new float[count];
        var ay = new float[count];

        for (var i = 0; i < count; i++)
        {
            // Every third particle is frozen (mask 0) so the mask branch is exercised across
            // both the vector body and the remainder, not only at the array's edges.
            dynamicMask[i] = i % 3 == 0 ? 0.0f : 1.0f;
            px[i] = i * 0.4f - 1.0f;
            py[i] = i * -0.6f + 3.0f;
            vx[i] = i * 0.2f + 1.0f;
            vy[i] = i * -0.3f - 2.0f;
            ax[i] = i * 0.5f - 2.5f;
            ay[i] = i * -0.9f + 1.5f;
        }

        // The scalar remainder is exactly one particle (index count-1). The mask pattern above
        // would leave it frozen for lane counts divisible by 3, which masks every y-axis (and
        // x-axis) mutation in the scalar tail's arithmetic to zero regardless of the mutation.
        // Force it dynamic so the remainder actually exercises the scalar tail's math.
        dynamicMask[count - 1] = 1.0f;

        // Independent copies for the scalar reference; the arrays under test are mutated in place.
        var expectedPx = (float[])px.Clone();
        var expectedPy = (float[])py.Clone();
        var expectedVx = (float[])vx.Clone();
        var expectedVy = (float[])vy.Clone();

        BatchMath.IntegrateSemiImplicitEuler2D(deltaTime, dynamicMask, px, py, vx, vy, ax, ay);

        var anyFrozen = false;
        var anyMoving = false;
        for (var i = 0; i < count; i++)
        {
            var mask = dynamicMask[i];
            expectedVx[i] += ax[i] * deltaTime * mask;
            expectedVy[i] += ay[i] * deltaTime * mask;
            expectedPx[i] += expectedVx[i] * deltaTime * mask;
            expectedPy[i] += expectedVy[i] * deltaTime * mask;

            if (mask == 0.0f) anyFrozen = true; else anyMoving = true;

            Assert.Equal(expectedVx[i], vx[i], Tolerance);
            Assert.Equal(expectedVy[i], vy[i], Tolerance);
            Assert.Equal(expectedPx[i], px[i], Tolerance);
            Assert.Equal(expectedPy[i], py[i], Tolerance);
        }

        Assert.True(anyFrozen, "fixture must include masked-off (frozen) particles");
        Assert.True(anyMoving, "fixture must include dynamic particles");
    }

    [Fact]
    public void EulerIntegrationRejectsNonPositiveDeltaTime()
    {
        var dynamicMask = new[] { 1.0f };
        var px = new[] { 0.0f };
        var py = new[] { 0.0f };
        var vx = new[] { 0.0f };
        var vy = new[] { 0.0f };
        var ax = new[] { 0.0f };
        var ay = new[] { 0.0f };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BatchMath.IntegrateSemiImplicitEuler2D(0.0f, dynamicMask, px, py, vx, vy, ax, ay));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BatchMath.IntegrateSemiImplicitEuler2D(-1.0f, dynamicMask, px, py, vx, vy, ax, ay));
    }

    [Fact]
    public void EulerIntegrationValidatesEqualSpanLengths()
    {
        var dynamicMask = new float[3];
        var px = new float[3];
        var py = new float[3];
        var vx = new float[2];
        var vy = new float[3];
        var ax = new float[3];
        var ay = new float[3];

        Assert.Throws<ArgumentException>(() =>
            BatchMath.IntegrateSemiImplicitEuler2D(0.5f, dynamicMask, px, py, vx, vy, ax, ay));
    }

    [Fact]
    public void Float2RadialFalloffMatchesScalarReferenceWithNonzeroYAndOutOfRangeParticles()
    {
        var count = 5;
        var positions = new float2[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = new float2(i * 1.1f - 2.0f, i * -0.8f + 1.0f);
        }

        var center = new float2(1.5f, -0.5f);
        const float strength = 3.0f;
        const float radius = 3.0f;
        var acceleration = new float2[count];

        BatchMath.AddRadialFalloffAcceleration2D(positions, center, strength, radius, acceleration);

        var insideCount = 0;
        var outsideCount = 0;
        for (var i = 0; i < count; i++)
        {
            var delta = center - positions[i];
            var distanceSquared = Math.Max(delta.x * delta.x + delta.y * delta.y, 1.0e-8f);
            var distance = MathF.Sqrt(distanceSquared);

            if (distance > radius)
            {
                outsideCount++;
                Assert.Equal(0.0f, acceleration[i].x, Tolerance);
                Assert.Equal(0.0f, acceleration[i].y, Tolerance);
                continue;
            }

            insideCount++;
            var falloff = Math.Max(0.0f, 1.0f - distance / radius);
            var expected = delta / distance * (strength * falloff);
            Assert.Equal(expected.x, acceleration[i].x, Tolerance);
            Assert.Equal(expected.y, acceleration[i].y, Tolerance);
        }

        Assert.True(insideCount > 0, "fixture must include particles inside the radius");
        Assert.True(outsideCount > 0, "fixture must include particles outside the radius");
    }

    [Fact]
    public void Float2RadialFalloffAccumulatesOntoExistingAcceleration()
    {
        // Guards against a mutation that assigns instead of accumulating (BatchMath.cs:115):
        // seed a nonzero, per-element-distinct acceleration and confirm the falloff
        // contribution is added to it, not overwritten. An out-of-range particle must be left
        // exactly at its seed value, not reset to zero.
        var count = 5;
        var positions = new float2[count];
        var seedAcceleration = new float2[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = new float2(i * 1.1f - 2.0f, i * -0.8f + 1.0f);
            seedAcceleration[i] = new float2(i * 2.3f + 4.0f, i * -1.7f - 6.0f);
        }

        var center = new float2(1.5f, -0.5f);
        const float strength = 3.0f;
        const float radius = 3.0f;
        var acceleration = (float2[])seedAcceleration.Clone();

        BatchMath.AddRadialFalloffAcceleration2D(positions, center, strength, radius, acceleration);

        var insideCount = 0;
        var outsideCount = 0;
        for (var i = 0; i < count; i++)
        {
            var delta = center - positions[i];
            var distanceSquared = Math.Max(delta.x * delta.x + delta.y * delta.y, 1.0e-8f);
            var distance = MathF.Sqrt(distanceSquared);

            if (distance > radius)
            {
                outsideCount++;
                Assert.Equal(seedAcceleration[i].x, acceleration[i].x, Tolerance);
                Assert.Equal(seedAcceleration[i].y, acceleration[i].y, Tolerance);
                continue;
            }

            insideCount++;
            var falloff = Math.Max(0.0f, 1.0f - distance / radius);
            var contribution = delta / distance * (strength * falloff);
            Assert.Equal(seedAcceleration[i].x + contribution.x, acceleration[i].x, Tolerance);
            Assert.Equal(seedAcceleration[i].y + contribution.y, acceleration[i].y, Tolerance);
        }

        Assert.True(insideCount > 0, "fixture must include particles inside the radius");
        Assert.True(outsideCount > 0, "fixture must include particles outside the radius");
    }

    [Fact]
    public void Float2EulerIntegrationMatchesScalarReferenceWithNonzeroYAndMask()
    {
        const float deltaTime = 0.2f;
        var dynamicMask = new[] { 1.0f, 0.0f, 1.0f };
        var position = new[] { new float2(0.0f, 0.0f), new float2(1.0f, 1.0f), new float2(-2.0f, 3.0f) };
        var velocity = new[] { new float2(1.0f, -1.0f), new float2(2.0f, 2.0f), new float2(-1.0f, 0.5f) };
        var acceleration = new[] { new float2(0.5f, 1.5f), new float2(3.0f, -3.0f), new float2(2.0f, -0.5f) };

        var expectedPosition = (float2[])position.Clone();
        var expectedVelocity = (float2[])velocity.Clone();

        BatchMath.IntegrateSemiImplicitEuler2D(deltaTime, dynamicMask, position, velocity, acceleration);

        for (var i = 0; i < position.Length; i++)
        {
            var mask = dynamicMask[i];
            expectedVelocity[i] += acceleration[i] * deltaTime * mask;
            expectedPosition[i] += expectedVelocity[i] * deltaTime * mask;

            Assert.Equal(expectedVelocity[i].x, velocity[i].x, Tolerance);
            Assert.Equal(expectedVelocity[i].y, velocity[i].y, Tolerance);
            Assert.Equal(expectedPosition[i].x, position[i].x, Tolerance);
            Assert.Equal(expectedPosition[i].y, position[i].y, Tolerance);
        }
    }

    [Fact]
    public void Float2EulerIntegrationRejectsNonPositiveDeltaTime()
    {
        var dynamicMask = new[] { 1.0f };
        var position = new[] { float2.zero };
        var velocity = new[] { float2.zero };
        var acceleration = new[] { float2.zero };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BatchMath.IntegrateSemiImplicitEuler2D(0.0f, dynamicMask, position, velocity, acceleration));
    }

    [Fact]
    public void Float2EulerIntegrationValidatesEqualSpanLengths()
    {
        var dynamicMask = new float[3];
        var position = new float2[3];
        var velocity = new float2[2];
        var acceleration = new float2[3];

        Assert.Throws<ArgumentException>(() =>
            BatchMath.IntegrateSemiImplicitEuler2D(0.5f, dynamicMask, position, velocity, acceleration));
    }

    [Fact]
    public void Float2RadialFalloffValidatesEqualSpanLengths()
    {
        var positions = new float2[3];
        var acceleration = new float2[2];

        Assert.Throws<ArgumentException>(() =>
            BatchMath.AddRadialFalloffAcceleration2D(positions, float2.zero, 1.0f, 1.0f, acceleration));
    }

    [Fact]
    public void Float2RadialFalloffIsNoOpWhenRadiusIsNonPositiveOrStrengthIsZero()
    {
        var positions = new[] { new float2(1.0f, 3.0f), new float2(2.0f, -4.0f) };
        var acceleration = new[] { new float2(5.0f, 7.0f), new float2(6.0f, 8.0f) };

        BatchMath.AddRadialFalloffAcceleration2D(positions, float2.zero, 9.0f, 0.0f, acceleration);
        Assert.Equal(new float2(5.0f, 7.0f), acceleration[0]);
        Assert.Equal(new float2(6.0f, 8.0f), acceleration[1]);

        BatchMath.AddRadialFalloffAcceleration2D(positions, float2.zero, 0.0f, 10.0f, acceleration);
        Assert.Equal(new float2(5.0f, 7.0f), acceleration[0]);
        Assert.Equal(new float2(6.0f, 8.0f), acceleration[1]);
    }
}
