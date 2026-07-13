namespace CultMath;

public enum PlanetaryCubeFace
{
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
}

public readonly record struct PlanetaryFaceCoordinate(PlanetaryCubeFace Face, double U, double V)
{
    public PlanetaryFaceCoordinate Validate(bool allowOutsideFace = false)
    {
        if (!double.IsFinite(U) || !double.IsFinite(V)) throw new ArgumentOutOfRangeException(nameof(U));
        if (!allowOutsideFace && (U is < -1.0 or > 1.0 || V is < -1.0 or > 1.0))
            throw new ArgumentOutOfRangeException(nameof(U), "Face coordinates must be in [-1, 1].");
        if (!Enum.IsDefined(typeof(PlanetaryCubeFace), Face)) throw new ArgumentOutOfRangeException(nameof(Face));
        return this;
    }
}

public readonly record struct PlanetaryTileAddress
{
    public const int MaxLevel = 30;

    public PlanetaryTileAddress(PlanetaryCubeFace face, int level, int x, int y)
    {
        if (!Enum.IsDefined(typeof(PlanetaryCubeFace), face)) throw new ArgumentOutOfRangeException(nameof(face));
        if (level is < 0 or > MaxLevel) throw new ArgumentOutOfRangeException(nameof(level));
        var count = 1 << level;
        if ((uint)x >= (uint)count) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)count) throw new ArgumentOutOfRangeException(nameof(y));
        Face = face;
        Level = level;
        X = x;
        Y = y;
    }

    public PlanetaryCubeFace Face { get; }
    public int Level { get; }
    public int X { get; }
    public int Y { get; }
    public int AxisTileCount => 1 << Level;

    public PlanetaryTileAddress Parent() => Level == 0
        ? throw new InvalidOperationException("Root cube tile has no parent.")
        : new(Face, Level - 1, X >> 1, Y >> 1);

    public PlanetaryTileAddress Child(int childX, int childY)
    {
        if ((uint)childX > 1) throw new ArgumentOutOfRangeException(nameof(childX));
        if ((uint)childY > 1) throw new ArgumentOutOfRangeException(nameof(childY));
        if (Level == MaxLevel) throw new InvalidOperationException($"Level {MaxLevel} cannot be subdivided.");
        return new(Face, Level + 1, (X << 1) | childX, (Y << 1) | childY);
    }

    public PlanetaryFaceCoordinate PositionAt(double localU, double localV)
    {
        if (!double.IsFinite(localU) || localU is < 0.0 or > 1.0) throw new ArgumentOutOfRangeException(nameof(localU));
        if (!double.IsFinite(localV) || localV is < 0.0 or > 1.0) throw new ArgumentOutOfRangeException(nameof(localV));
        var count = AxisTileCount;
        return new(Face, -1.0 + 2.0 * (X + localU) / count, -1.0 + 2.0 * (Y + localV) / count);
    }

    public ulong StableKey
    {
        get
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            hash = unchecked((hash ^ (uint)Face) * prime);
            hash = unchecked((hash ^ (uint)Level) * prime);
            hash = unchecked((hash ^ (uint)X) * prime);
            hash = unchecked((hash ^ (uint)Y) * prime);
            return hash == 0 ? 1 : hash;
        }
    }
    public override string ToString() => $"{Face}/L{Level:D2}/{X}/{Y}";
}

public static class PlanetaryTopology
{
    private const float QuarterPi = MathF.PI * 0.25f;

    public static float3 Direction(PlanetaryFaceCoordinate coordinate)
    {
        coordinate.Validate(allowOutsideFace: true);
        var u = MathF.Tan((float)coordinate.U * QuarterPi);
        var v = MathF.Tan((float)coordinate.V * QuarterPi);
        var cube = coordinate.Face switch
        {
            PlanetaryCubeFace.PositiveX => new float3(1, v, -u),
            PlanetaryCubeFace.NegativeX => new float3(-1, v, u),
            PlanetaryCubeFace.PositiveY => new float3(u, 1, -v),
            PlanetaryCubeFace.NegativeY => new float3(u, -1, v),
            PlanetaryCubeFace.PositiveZ => new float3(u, v, 1),
            PlanetaryCubeFace.NegativeZ => new float3(-u, v, -1),
            _ => throw new ArgumentOutOfRangeException(nameof(coordinate)),
        };
        return math.normalize(cube);
    }

    public static PlanetaryFaceCoordinate FaceCoordinate(float3 direction)
    {
        ValidateDirection(direction);
        var d = math.normalize(direction);
        var a = math.abs(d);
        if (a.x >= a.y && a.x >= a.z)
            return d.x >= 0
                ? FromCubeUv(PlanetaryCubeFace.PositiveX, -d.z / a.x, d.y / a.x)
                : FromCubeUv(PlanetaryCubeFace.NegativeX, d.z / a.x, d.y / a.x);
        if (a.y >= a.z)
            return d.y >= 0
                ? FromCubeUv(PlanetaryCubeFace.PositiveY, d.x / a.y, -d.z / a.y)
                : FromCubeUv(PlanetaryCubeFace.NegativeY, d.x / a.y, d.z / a.y);
        return d.z >= 0
            ? FromCubeUv(PlanetaryCubeFace.PositiveZ, d.x / a.z, d.y / a.z)
            : FromCubeUv(PlanetaryCubeFace.NegativeZ, -d.x / a.z, d.y / a.z);
    }

    public static PlanetaryTileAddress TileAt(float3 direction, int level)
    {
        if (level is < 0 or > PlanetaryTileAddress.MaxLevel) throw new ArgumentOutOfRangeException(nameof(level));
        var face = FaceCoordinate(direction);
        var count = 1 << level;
        var x = Math.Clamp((int)((face.U * 0.5 + 0.5) * count), 0, count - 1);
        var y = Math.Clamp((int)((face.V * 0.5 + 0.5) * count), 0, count - 1);
        return new(face.Face, level, x, y);
    }

    public static bool TryLocalCoordinate(float3 direction, PlanetaryTileAddress tile, out float2 local)
    {
        var face = FaceCoordinate(direction);
        if (face.Face != tile.Face) { local = float2.zero; return false; }
        var scaledU = (face.U * 0.5 + 0.5) * tile.AxisTileCount;
        var scaledV = (face.V * 0.5 + 0.5) * tile.AxisTileCount;
        local = new((float)(scaledU - tile.X), (float)(scaledV - tile.Y));
        return local.x is >= 0 and <= 1 && local.y is >= 0 and <= 1;
    }

    public static float3 SurfaceNormal(float3 unitDirection, float3 worldDistanceTangentGradient)
    {
        ValidateDirection(unitDirection);
        ValidateFinite(worldDistanceTangentGradient, nameof(worldDistanceTangentGradient));
        var direction = math.normalize(unitDirection);
        var tangent = worldDistanceTangentGradient - direction * math.dot(worldDistanceTangentGradient, direction);
        return math.normalize(direction - tangent);
    }

    private static PlanetaryFaceCoordinate FromCubeUv(PlanetaryCubeFace face, float cubeU, float cubeV) => new(
        face,
        MathF.Atan(cubeU) / QuarterPi,
        MathF.Atan(cubeV) / QuarterPi);

    internal static void ValidateDirection(float3 direction)
    {
        ValidateFinite(direction, nameof(direction));
        if (math.length(direction) < 1.0e-12f) throw new ArgumentOutOfRangeException(nameof(direction));
    }

    internal static void ValidateFinite(float3 value, string name)
    {
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z))
            throw new ArgumentOutOfRangeException(name);
    }
}
