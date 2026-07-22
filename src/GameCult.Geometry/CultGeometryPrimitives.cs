using System;
using System.Text.Json.Serialization;
using CultMath;
using MessagePack;

namespace GameCult.Geometry
{
    /// <summary>
    /// Axis-aligned two-dimensional rectangle represented by canonical min and max corners.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultRect : IEquatable<CultRect>
    {
        /// <summary>Creates a rectangle and canonicalizes min/max corners.</summary>
        public CultRect(float2 min, float2 max)
        {
            Min = new float2(Math.Min(min.x, max.x), Math.Min(min.y, max.y));
            Max = new float2(Math.Max(min.x, max.x), Math.Max(min.y, max.y));
        }

        /// <summary>Creates a rectangle and canonicalizes min/max corners.</summary>
        public CultRect(float minX, float minY, float maxX, float maxY)
            : this(new float2(minX, minY), new float2(maxX, maxY))
        {
        }

        /// <summary>Gets the canonical minimum corner.</summary>
        [Key(0)]
        public float2 Min { get; }

        /// <summary>Gets the canonical maximum corner.</summary>
        [Key(1)]
        public float2 Max { get; }

        /// <summary>Gets the rectangle size.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public float2 Size => Max - Min;

        /// <summary>Gets the rectangle center.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public float2 Center => (Min + Max) * 0.5f;

        /// <summary>Returns whether the point lies inside or on the rectangle edge.</summary>
        public bool Contains(float2 point)
        {
            return point.x >= Min.x &&
                   point.x <= Max.x &&
                   point.y >= Min.y &&
                   point.y <= Max.y;
        }

        /// <summary>Returns whether this rectangle intersects another rectangle.</summary>
        public bool Intersects(CultRect other)
        {
            return Min.x <= other.Max.x &&
                   Max.x >= other.Min.x &&
                   Min.y <= other.Max.y &&
                   Max.y >= other.Min.y;
        }

        /// <summary>Returns whether this rectangle is equal to another rectangle.</summary>
        public bool Equals(CultRect other) => Min == other.Min && Max == other.Max;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultRect other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Min, Max);

        /// <inheritdoc />
        public override string ToString() => $"[{Min}..{Max}]";

        public static bool operator ==(CultRect left, CultRect right) => left.Equals(right);
        public static bool operator !=(CultRect left, CultRect right) => !left.Equals(right);
    }

    /// <summary>
    /// Two-dimensional circle for spatial queries.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultCircle : IEquatable<CultCircle>
    {
        /// <summary>Creates a circle.</summary>
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
        public CultRect Bounds => new(
            Center.x - Radius,
            Center.y - Radius,
            Center.x + Radius,
            Center.y + Radius);

        /// <summary>Returns whether the point lies inside or on the circle edge.</summary>
        public bool Contains(float2 point)
        {
            var delta = point - Center;
            return ((delta.x * delta.x) + (delta.y * delta.y)) <= Radius * Radius;
        }

        /// <summary>Returns whether this circle intersects a rectangle.</summary>
        public bool Intersects(CultRect rect)
        {
            var clampedX = Math.Max(rect.Min.x, Math.Min(Center.x, rect.Max.x));
            var clampedY = Math.Max(rect.Min.y, Math.Min(Center.y, rect.Max.y));
            return Contains(new float2(clampedX, clampedY));
        }

        /// <summary>Returns whether this circle is equal to another circle.</summary>
        public bool Equals(CultCircle other) => Center == other.Center && Radius.Equals(other.Radius);

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
            return ((delta.x * delta.x) + (delta.y * delta.y) + (delta.z * delta.z)) <= Radius * Radius;
        }

        /// <summary>Returns whether this sphere is equal to another sphere.</summary>
        public bool Equals(CultSphere other) => Center == other.Center && Radius.Equals(other.Radius);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultSphere other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Center, Radius);

        public static bool operator ==(CultSphere left, CultSphere right) => left.Equals(right);
        public static bool operator !=(CultSphere left, CultSphere right) => !left.Equals(right);
    }
}
