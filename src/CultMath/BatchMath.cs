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
        for (; i <= positionX.Length - lanes; i += lanes)
        {
            var px = new Vector<float>(positionX.Slice(i, lanes));
            var py = new Vector<float>(positionY.Slice(i, lanes));
            var dx = centerXVector - px;
            var dy = centerYVector - py;
            var distanceSquared = Vector.Max(dx * dx + dy * dy, minimumDistanceSquared);
            var distance = Vector.SquareRoot(distanceSquared);
            var inRange = Vector.LessThanOrEqual(distance, radiusVector);
            var falloff = Vector.Max(zero, one - distance / radiusVector);
            var magnitude = strengthVector * falloff;
            var ax = Vector.ConditionalSelect(inRange, dx / distance * magnitude, zero);
            var ay = Vector.ConditionalSelect(inRange, dy / distance * magnitude, zero);

            (new Vector<float>(accelerationX.Slice(i, lanes)) + ax).CopyTo(accelerationX.Slice(i, lanes));
            (new Vector<float>(accelerationY.Slice(i, lanes)) + ay).CopyTo(accelerationY.Slice(i, lanes));
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
        for (; i <= positionX.Length - lanes; i += lanes)
        {
            var mask = new Vector<float>(dynamicMask.Slice(i, lanes));
            var vx = new Vector<float>(velocityX.Slice(i, lanes));
            var vy = new Vector<float>(velocityY.Slice(i, lanes));
            var px = new Vector<float>(positionX.Slice(i, lanes));
            var py = new Vector<float>(positionY.Slice(i, lanes));
            var ax = new Vector<float>(accelerationX.Slice(i, lanes));
            var ay = new Vector<float>(accelerationY.Slice(i, lanes));

            vx += ax * delta * mask;
            vy += ay * delta * mask;
            px += vx * delta * mask;
            py += vy * delta * mask;

            vx.CopyTo(velocityX.Slice(i, lanes));
            vy.CopyTo(velocityY.Slice(i, lanes));
            px.CopyTo(positionX.Slice(i, lanes));
            py.CopyTo(positionY.Slice(i, lanes));
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
}
