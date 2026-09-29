using System;
using System.Text.Json.Serialization;
using CultMath;
using MessagePack;

namespace GameCult.Geometry
{
    /// <summary>
    /// Two-dimensional circle for spatial queries.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultCircle : IEquatable<CultCircle>
    {
        /// <summary>Creates a circle.</summary>
        [JsonConstructor]
        public CultCircle(float2 center, float radius)
        {
            if (radius < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius cannot be negative.");
            }

            Center = center;
            Radius = radius;
        }

        /// <summary>Gets the circle center.</summary>
        [Key(0)]
        public float2 Center { get; }

        /// <summary>Gets the circle radius.</summary>
        [Key(1)]
        public float Radius { get; }

        /// <summary>Gets the circle bounds.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public rect Bounds => new(
            Center.x - Radius,
            Center.y - Radius,
            Center.x + Radius,
            Center.y + Radius);

        /// <summary>Returns whether the point lies inside or on the circle edge.</summary>
        public bool Contains(float2 point)
        {
            var delta = point - Center;
            return math.lengthsq(delta) <= Radius * Radius;
        }

        /// <summary>Returns whether this circle intersects a rectangle.</summary>
        public bool Intersects(rect rect)
        {
            var clampedX = Math.Max(rect.min.x, Math.Min(Center.x, rect.max.x));
            var clampedY = Math.Max(rect.min.y, Math.Min(Center.y, rect.max.y));
            return Contains(new float2(clampedX, clampedY));
        }

        /// <summary>Returns whether this circle is equal to another circle.</summary>
        public bool Equals(CultCircle other) => Center.Equals(other.Center) && Radius.Equals(other.Radius);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultCircle other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Center, Radius);

        public static bool operator ==(CultCircle left, CultCircle right) => left.Equals(right);
        public static bool operator !=(CultCircle left, CultCircle right) => !left.Equals(right);
    }

    /// <summary>
    /// Three-dimensional sphere for physics and spatial queries.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultSphere : IEquatable<CultSphere>
    {
        /// <summary>Creates a sphere.</summary>
        [JsonConstructor]
        public CultSphere(float3 center, float radius)
        {
            if (radius < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius cannot be negative.");
            }

            Center = center;
            Radius = radius;
        }

        /// <summary>Gets the sphere center.</summary>
        [Key(0)]
        public float3 Center { get; }

        /// <summary>Gets the sphere radius.</summary>
        [Key(1)]
        public float Radius { get; }

        /// <summary>Gets the XY circle projection.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultCircle XyCircle => new(Center.xy, Radius);

        /// <summary>Returns whether the point lies inside or on the sphere surface.</summary>
        public bool Contains(float3 point)
        {
            var delta = point - Center;
            return math.lengthsq(delta) <= Radius * Radius;
        }

        /// <summary>Returns whether this sphere is equal to another sphere.</summary>
        public bool Equals(CultSphere other) => Center.Equals(other.Center) && Radius.Equals(other.Radius);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultSphere other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Center, Radius);

        public static bool operator ==(CultSphere left, CultSphere right) => left.Equals(right);
        public static bool operator !=(CultSphere left, CultSphere right) => !left.Equals(right);
    }
}
