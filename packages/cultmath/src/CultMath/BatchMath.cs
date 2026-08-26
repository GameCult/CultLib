using System;
using System.Numerics;

namespace CultMath;

public static class BatchMath
{
    private const float MinimumDistanceSquared = 1.0e-8f;

    public static int LaneCount => Vector<float>.Count;

    public static bool IsHardwareAccelerated => Vector.IsHardwareAccelerated;

    public static void Clear(Span<float> values) => values.Clear();

    public static void AddRadialFalloffAcceleration2D(
        ReadOnlySpan<float> positionX,
        ReadOnlySpan<float> positionY,
        float centerX,
        float centerY,
        float strength,
        float radius,
        Span<float> accelerationX,
        Span<float> accelerationY)
    {
        ValidateEqualLengths(positionX.Length, positionY.Length, accelerationX.Length, accelerationY.Length);

        if (radius <= 0.0f || strength == 0.0f || positionX.Length == 0)
        {
            return;
        }

        var radiusVector = new Vector<float>(radius);
        var centerXVector = new Vector<float>(centerX);
        var centerYVector = new Vector<float>(centerY);
        var strengthVector = new Vector<float>(strength);
        var one = Vector<float>.One;
        var zero = Vector<float>.Zero;
        var minimumDistanceSquared = new Vector<float>(MinimumDistanceSquared);

        var i = 0;
        var lanes = Vector<float>.Count;
        var pxBuffer = new float[lanes];
        var pyBuffer = new float[lanes];
        var axBuffer = new float[lanes];
        var ayBuffer = new float[lanes];
        for (; i <= positionX.Length - lanes; i += lanes)
        {
            CopySpanToArray(positionX.Slice(i, lanes), pxBuffer);
            CopySpanToArray(positionY.Slice(i, lanes), pyBuffer);
            CopySpanToArray(accelerationX.Slice(i, lanes), axBuffer);
            CopySpanToArray(accelerationY.Slice(i, lanes), ayBuffer);

            var px = new Vector<float>(pxBuffer);
            var py = new Vector<float>(pyBuffer);
            var dx = centerXVector - px;
            var dy = centerYVector - py;
            var distanceSquared = Vector.Max(dx * dx + dy * dy, minimumDistanceSquared);
            var distance = Vector.SquareRoot(distanceSquared);
            var inRange = Vector.LessThanOrEqual(distance, radiusVector);
            var falloff = Vector.Max(zero, one - distance / radiusVector);
            var magnitude = strengthVector * falloff;
            var ax = Vector.ConditionalSelect(inRange, dx / distance * magnitude, zero);
            var ay = Vector.ConditionalSelect(inRange, dy / distance * magnitude, zero);

            (new Vector<float>(axBuffer) + ax).CopyTo(axBuffer);
            (new Vector<float>(ayBuffer) + ay).CopyTo(ayBuffer);
            CopyArrayToSpan(axBuffer, accelerationX.Slice(i, lanes));
            CopyArrayToSpan(ayBuffer, accelerationY.Slice(i, lanes));
        }

        for (; i < positionX.Length; i++)
        {
            var dx = centerX - positionX[i];
            var dy = centerY - positionY[i];
            var distanceSquared = MathF.Max(dx * dx + dy * dy, MinimumDistanceSquared);
            var distance = MathF.Sqrt(distanceSquared);
            if (distance > radius)
            {
                continue;
            }

            var falloff = MathF.Max(0.0f, 1.0f - distance / radius);
            var magnitude = strength * falloff;
            accelerationX[i] += dx / distance * magnitude;
            accelerationY[i] += dy / distance * magnitude;
        }
    }

    public static void AddRadialFalloffAcceleration2D(
        ReadOnlySpan<float2> positions,
        float2 center,
        float strength,
        float radius,
        Span<float2> acceleration)
    {
        ValidateEqualLengths(positions.Length, acceleration.Length);

        if (radius <= 0.0f || strength == 0.0f || positions.Length == 0)
        {
            return;
        }

        for (var i = 0; i < positions.Length; i++)
        {
            var delta = center - positions[i];
            var distanceSquared = MathF.Max(delta.x * delta.x + delta.y * delta.y, MinimumDistanceSquared);
            var distance = MathF.Sqrt(distanceSquared);
            if (distance > radius)
            {
                continue;
            }

            var falloff = MathF.Max(0.0f, 1.0f - distance / radius);
            acceleration[i] += delta / distance * (strength * falloff);
        }
    }

    public static void IntegrateSemiImplicitEuler2D(
        float deltaTime,
        ReadOnlySpan<float> dynamicMask,
        Span<float> positionX,
        Span<float> positionY,
        Span<float> velocityX,
        Span<float> velocityY,
        ReadOnlySpan<float> accelerationX,
        ReadOnlySpan<float> accelerationY)
    {
        if (deltaTime <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be positive.");
        }

        ValidateEqualLengths(
            dynamicMask.Length,
            positionX.Length,
            positionY.Length,
            velocityX.Length,
            velocityY.Length,
            accelerationX.Length,
            accelerationY.Length);

        var delta = new Vector<float>(deltaTime);
        var i = 0;
        var lanes = Vector<float>.Count;
        var maskBuffer = new float[lanes];
        var pxBuffer = new float[lanes];
        var pyBuffer = new float[lanes];
        var vxBuffer = new float[lanes];
        var vyBuffer = new float[lanes];
        var axBuffer = new float[lanes];
        var ayBuffer = new float[lanes];
        for (; i <= positionX.Length - lanes; i += lanes)
        {
            CopySpanToArray(dynamicMask.Slice(i, lanes), maskBuffer);
            CopySpanToArray(velocityX.Slice(i, lanes), vxBuffer);
            CopySpanToArray(velocityY.Slice(i, lanes), vyBuffer);
            CopySpanToArray(positionX.Slice(i, lanes), pxBuffer);
            CopySpanToArray(positionY.Slice(i, lanes), pyBuffer);
            CopySpanToArray(accelerationX.Slice(i, lanes), axBuffer);
            CopySpanToArray(accelerationY.Slice(i, lanes), ayBuffer);

            var mask = new Vector<float>(maskBuffer);
            var vx = new Vector<float>(vxBuffer);
            var vy = new Vector<float>(vyBuffer);
            var px = new Vector<float>(pxBuffer);
            var py = new Vector<float>(pyBuffer);
            var ax = new Vector<float>(axBuffer);
            var ay = new Vector<float>(ayBuffer);

            vx += ax * delta * mask;
            vy += ay * delta * mask;
            px += vx * delta * mask;
            py += vy * delta * mask;

            vx.CopyTo(vxBuffer);
            vy.CopyTo(vyBuffer);
            px.CopyTo(pxBuffer);
            py.CopyTo(pyBuffer);
            CopyArrayToSpan(vxBuffer, velocityX.Slice(i, lanes));
            CopyArrayToSpan(vyBuffer, velocityY.Slice(i, lanes));
            CopyArrayToSpan(pxBuffer, positionX.Slice(i, lanes));
            CopyArrayToSpan(pyBuffer, positionY.Slice(i, lanes));
        }

        for (; i < positionX.Length; i++)
        {
            var mask = dynamicMask[i];
            velocityX[i] += accelerationX[i] * deltaTime * mask;
            velocityY[i] += accelerationY[i] * deltaTime * mask;
            positionX[i] += velocityX[i] * deltaTime * mask;
            positionY[i] += velocityY[i] * deltaTime * mask;
        }
    }

    public static void IntegrateSemiImplicitEuler2D(
        float deltaTime,
        ReadOnlySpan<float> dynamicMask,
        Span<float2> position,
        Span<float2> velocity,
        ReadOnlySpan<float2> acceleration)
    {
        if (deltaTime <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be positive.");
        }

        ValidateEqualLengths(dynamicMask.Length, position.Length, velocity.Length, acceleration.Length);

        for (var i = 0; i < position.Length; i++)
        {
            var mask = dynamicMask[i];
            velocity[i] += acceleration[i] * deltaTime * mask;
            position[i] += velocity[i] * deltaTime * mask;
        }
    }

    private static void ValidateEqualLengths(params int[] lengths)
    {
        var length = lengths[0];
        for (var i = 1; i < lengths.Length; i++)
        {
            if (lengths[i] != length)
            {
                throw new ArgumentException("CultMath batch spans must have equal length.");
            }
        }
    }

    private static void CopySpanToArray(ReadOnlySpan<float> source, float[] destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = source[i];
        }
    }

    private static void CopyArrayToSpan(float[] source, Span<float> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = source[i];
        }
    }
}
