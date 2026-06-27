using System;
using System.Text.Json.Serialization;
using MessagePack;

namespace GameCult.Geometry
{
    /// <summary>
    /// Deterministic two-dimensional float vector for cross-runtime geometry and query contracts.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultVec2 : IEquatable<CultVec2>
    {
        /// <summary>Creates a two-dimensional vector.</summary>
        public CultVec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Gets the x component.</summary>
        [Key(0)]
        public float X { get; }

        /// <summary>Gets the y component.</summary>
        [Key(1)]
        public float Y { get; }

        /// <summary>Gets the zero vector.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public static CultVec2 Zero => new(0f, 0f);

        /// <summary>Gets the squared vector length.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public float LengthSquared => (X * X) + (Y * Y);

        /// <summary>Returns whether this vector is equal to another vector.</summary>
        public bool Equals(CultVec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultVec2 other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y);

        /// <inheritdoc />
        public override string ToString() => $"({X}, {Y})";

        public static CultVec2 operator +(CultVec2 left, CultVec2 right) => new(left.X + right.X, left.Y + right.Y);
        public static CultVec2 operator -(CultVec2 left, CultVec2 right) => new(left.X - right.X, left.Y - right.Y);
        public static CultVec2 operator *(CultVec2 value, float scalar) => new(value.X * scalar, value.Y * scalar);
        public static CultVec2 operator /(CultVec2 value, float scalar) => new(value.X / scalar, value.Y / scalar);
        public static bool operator ==(CultVec2 left, CultVec2 right) => left.Equals(right);
        public static bool operator !=(CultVec2 left, CultVec2 right) => !left.Equals(right);
    }

    /// <summary>
    /// Deterministic three-dimensional float vector for cross-runtime geometry and query contracts.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultVec3 : IEquatable<CultVec3>
    {
        /// <summary>Creates a three-dimensional vector.</summary>
        public CultVec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Gets the x component.</summary>
        [Key(0)]
        public float X { get; }

        /// <summary>Gets the y component.</summary>
        [Key(1)]
        public float Y { get; }

        /// <summary>Gets the z component.</summary>
        [Key(2)]
        public float Z { get; }

        /// <summary>Gets the zero vector.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public static CultVec3 Zero => new(0f, 0f, 0f);

        /// <summary>Gets the XY plane projection.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultVec2 Xy => new(X, Y);

        /// <summary>Gets the XZ plane projection for Unity compatibility adapters.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultVec2 Xz => new(X, Z);

        /// <summary>Gets the squared vector length.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public float LengthSquared => (X * X) + (Y * Y) + (Z * Z);

        /// <summary>Returns whether this vector is equal to another vector.</summary>
        public bool Equals(CultVec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultVec3 other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        /// <inheritdoc />
        public override string ToString() => $"({X}, {Y}, {Z})";

        public static CultVec3 operator +(CultVec3 left, CultVec3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static CultVec3 operator -(CultVec3 left, CultVec3 right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        public static CultVec3 operator *(CultVec3 value, float scalar) => new(value.X * scalar, value.Y * scalar, value.Z * scalar);
        public static CultVec3 operator /(CultVec3 value, float scalar) => new(value.X / scalar, value.Y / scalar, value.Z / scalar);
        public static bool operator ==(CultVec3 left, CultVec3 right) => left.Equals(right);
        public static bool operator !=(CultVec3 left, CultVec3 right) => !left.Equals(right);
    }

    /// <summary>
    /// Axis-aligned two-dimensional rectangle represented by canonical min and max corners.
    /// </summary>
    [MessagePackObject]
    public readonly struct CultRect : IEquatable<CultRect>
    {
        /// <summary>Creates a rectangle and canonicalizes min/max corners.</summary>
        public CultRect(CultVec2 min, CultVec2 max)
        {
            Min = new CultVec2(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y));
            Max = new CultVec2(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y));
        }

        /// <summary>Creates a rectangle and canonicalizes min/max corners.</summary>
        public CultRect(float minX, float minY, float maxX, float maxY)
            : this(new CultVec2(minX, minY), new CultVec2(maxX, maxY))
        {
        }

        /// <summary>Gets the canonical minimum corner.</summary>
        [Key(0)]
        public CultVec2 Min { get; }

        /// <summary>Gets the canonical maximum corner.</summary>
        [Key(1)]
        public CultVec2 Max { get; }

        /// <summary>Gets the rectangle size.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultVec2 Size => Max - Min;

        /// <summary>Gets the rectangle center.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultVec2 Center => (Min + Max) * 0.5f;

        /// <summary>Returns whether the point lies inside or on the rectangle edge.</summary>
        public bool Contains(CultVec2 point)
        {
            return point.X >= Min.X &&
                   point.X <= Max.X &&
                   point.Y >= Min.Y &&
                   point.Y <= Max.Y;
        }

        /// <summary>Returns whether this rectangle intersects another rectangle.</summary>
        public bool Intersects(CultRect other)
        {
            return Min.X <= other.Max.X &&
                   Max.X >= other.Min.X &&
                   Min.Y <= other.Max.Y &&
                   Max.Y >= other.Min.Y;
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
        public CultCircle(CultVec2 center, float radius)
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
        public CultVec2 Center { get; }

        /// <summary>Gets the circle radius.</summary>
        [Key(1)]
        public float Radius { get; }

        /// <summary>Gets the circle bounds.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultRect Bounds => new(
            Center.X - Radius,
            Center.Y - Radius,
            Center.X + Radius,
            Center.Y + Radius);

        /// <summary>Returns whether the point lies inside or on the circle edge.</summary>
        public bool Contains(CultVec2 point)
        {
            var delta = point - Center;
            return delta.LengthSquared <= Radius * Radius;
        }

        /// <summary>Returns whether this circle intersects a rectangle.</summary>
        public bool Intersects(CultRect rect)
        {
            var clampedX = Math.Max(rect.Min.X, Math.Min(Center.X, rect.Max.X));
            var clampedY = Math.Max(rect.Min.Y, Math.Min(Center.Y, rect.Max.Y));
            return Contains(new CultVec2(clampedX, clampedY));
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
        public CultSphere(CultVec3 center, float radius)
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
        public CultVec3 Center { get; }

        /// <summary>Gets the sphere radius.</summary>
        [Key(1)]
        public float Radius { get; }

        /// <summary>Gets the XY circle projection.</summary>
        [IgnoreMember]
        [JsonIgnore]
        public CultCircle XyCircle => new(Center.Xy, Radius);

        /// <summary>Returns whether the point lies inside or on the sphere surface.</summary>
        public bool Contains(CultVec3 point)
        {
            var delta = point - Center;
            return delta.LengthSquared <= Radius * Radius;
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
