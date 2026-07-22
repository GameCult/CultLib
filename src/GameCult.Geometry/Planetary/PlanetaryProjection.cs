using System;
using CultMath;

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

namespace GameCult.Geometry;

public enum PlanetaryProjectionKind
{
    Equirectangular,
    WebMercator,
    EqualEarth,
    Orthographic,
    AzimuthalEquidistant,
    AzimuthalEqualArea,
    CubeAtlas,
    LocalTangent,
}

public readonly record struct PlanetaryProjectionParameters(
    PlanetaryProjectionKind Kind,
    double CenterLongitude = 0,
    double CenterLatitude = 0,
    double Scale = 1)
{
    public PlanetaryProjectionParameters Validate()
    {
        if (!double.IsFinite(CenterLongitude) || !double.IsFinite(CenterLatitude) || !double.IsFinite(Scale) || Scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(Scale));
        if (CenterLatitude is < -Math.PI / 2 or > Math.PI / 2) throw new ArgumentOutOfRangeException(nameof(CenterLatitude));
        return this;
    }
}

public static class PlanetaryProjection
{
    private const double EqualEarthA1 = 1.340264;
    private const double EqualEarthA2 = -0.081106;
    private const double EqualEarthA3 = 0.000893;
    private const double EqualEarthA4 = 0.003796;
    private static readonly double EqualEarthM = Math.Sqrt(3) / 2;
    private static readonly double EqualEarthXMax = Math.PI / (EqualEarthM * EqualEarthA1);
    private static readonly double EqualEarthYMax = EqualEarthY(Math.Asin(EqualEarthM));
    public static readonly double WebMercatorMaximumLatitude = Math.Atan(Math.Sinh(Math.PI));

    public static bool TryForward(float3 direction, PlanetaryProjectionParameters parameters, out double2 coordinate)
    {
        parameters.Validate();
        PlanetaryTopology.ValidateDirection(direction);
        var d = math.normalize(direction);
        if (parameters.Kind == PlanetaryProjectionKind.CubeAtlas)
        {
            var face = PlanetaryTopology.FaceCoordinate(d);
            var index = (int)face.Face;
            var column = index % 3;
            var row = index / 3;
            coordinate = new(
                (-1 + (column + (face.U + 1) * 0.5) * (2.0 / 3.0)) / parameters.Scale,
                (1 - (row + (face.V + 1) * 0.5)) / parameters.Scale);
            return true;
        }

        var longitude = Math.Atan2(d.y, d.x);
        var latitude = Math.Asin(Math.Clamp(d.z, -1, 1));
        var deltaLongitude = WrapLongitude(longitude - parameters.CenterLongitude);
        var centered = CenteredForward(deltaLongitude, latitude, parameters, out coordinate);
        coordinate /= parameters.Scale;
        return centered;
    }

    public static bool TryInverse(double2 coordinate, PlanetaryProjectionParameters parameters, out float3 direction)
    {
        parameters.Validate();
        if (!double.IsFinite(coordinate.x) || !double.IsFinite(coordinate.y))
        {
            direction = float3.zero;
            return false;
        }
        coordinate *= parameters.Scale;
        if (parameters.Kind == PlanetaryProjectionKind.CubeAtlas)
            return TryInverseCubeAtlas(coordinate, out direction);

        if (!CenteredInverse(coordinate, parameters, out var deltaLongitude, out var latitude))
        {
            direction = float3.zero;
            return false;
        }
        var longitude = WrapLongitude(deltaLongitude + parameters.CenterLongitude);
        var cosLatitude = Math.Cos(latitude);
        direction = math.normalize(new float3(
            (float)(cosLatitude * Math.Cos(longitude)),
            (float)(cosLatitude * Math.Sin(longitude)),
            (float)Math.Sin(latitude)));
        return true;
    }

    private static bool CenteredForward(double longitude, double latitude, PlanetaryProjectionParameters p, out double2 coordinate)
    {
        switch (p.Kind)
        {
            case PlanetaryProjectionKind.Equirectangular:
                coordinate = new(longitude / Math.PI, latitude / (Math.PI * 0.5));
                return true;
            case PlanetaryProjectionKind.WebMercator:
                if (Math.Abs(latitude) > WebMercatorMaximumLatitude) { coordinate = double2.zero; return false; }
                coordinate = new(longitude / Math.PI, Math.Asinh(Math.Tan(latitude)) / Math.PI);
                return true;
            case PlanetaryProjectionKind.EqualEarth:
            {
                var theta = Math.Asin(EqualEarthM * Math.Sin(latitude));
                var theta2 = theta * theta;
                var theta6 = theta2 * theta2 * theta2;
                var denominator = EqualEarthM * (EqualEarthA1 + 3 * EqualEarthA2 * theta2 + theta6 * (7 * EqualEarthA3 + 9 * EqualEarthA4 * theta2));
                coordinate = new(longitude * Math.Cos(theta) / denominator / EqualEarthXMax, EqualEarthY(theta) / EqualEarthYMax);
                return true;
            }
            case PlanetaryProjectionKind.Orthographic:
            case PlanetaryProjectionKind.AzimuthalEquidistant:
            case PlanetaryProjectionKind.AzimuthalEqualArea:
            case PlanetaryProjectionKind.LocalTangent:
                return ForwardAzimuthal(longitude, latitude, p, out coordinate);
            default:
                throw new ArgumentOutOfRangeException(nameof(p));
        }
    }

    private static bool CenteredInverse(double2 coordinate, PlanetaryProjectionParameters p, out double longitude, out double latitude)
    {
        longitude = latitude = 0;
        switch (p.Kind)
        {
            case PlanetaryProjectionKind.Equirectangular:
                if (Math.Abs(coordinate.y) > 1) return false;
                longitude = coordinate.x * Math.PI; latitude = coordinate.y * Math.PI * 0.5; return Math.Abs(coordinate.x) <= 1;
            case PlanetaryProjectionKind.WebMercator:
                if (Math.Abs(coordinate.x) > 1 || Math.Abs(coordinate.y) > 1) return false;
                longitude = coordinate.x * Math.PI; latitude = Math.Atan(Math.Sinh(coordinate.y * Math.PI)); return true;
            case PlanetaryProjectionKind.EqualEarth:
            {
                if (Math.Abs(coordinate.x) > 1 || Math.Abs(coordinate.y) > 1) return false;
                var targetY = coordinate.y * EqualEarthYMax;
                var theta = targetY / EqualEarthA1;
                for (var i = 0; i < 8; i++)
                {
                    var theta2 = theta * theta;
                    var theta6 = theta2 * theta2 * theta2;
                    var f = EqualEarthY(theta) - targetY;
                    var derivative = EqualEarthA1 + 3 * EqualEarthA2 * theta2 + theta6 * (7 * EqualEarthA3 + 9 * EqualEarthA4 * theta2);
                    theta -= f / derivative;
                }
                var sinLatitude = Math.Sin(theta) / EqualEarthM;
                if (Math.Abs(sinLatitude) > 1.0000000001) return false;
                latitude = Math.Asin(Math.Clamp(sinLatitude, -1, 1));
                var thetaSquared = theta * theta;
                var thetaSixth = thetaSquared * thetaSquared * thetaSquared;
                var denominator = EqualEarthM * (EqualEarthA1 + 3 * EqualEarthA2 * thetaSquared + thetaSixth * (7 * EqualEarthA3 + 9 * EqualEarthA4 * thetaSquared));
                longitude = coordinate.x * EqualEarthXMax * denominator / Math.Cos(theta);
                return Math.Abs(longitude) <= Math.PI + 1.0e-10;
            }
            case PlanetaryProjectionKind.Orthographic:
            case PlanetaryProjectionKind.AzimuthalEquidistant:
            case PlanetaryProjectionKind.AzimuthalEqualArea:
            case PlanetaryProjectionKind.LocalTangent:
                return InverseAzimuthal(coordinate, p, out longitude, out latitude);
            default:
                throw new ArgumentOutOfRangeException(nameof(p));
        }
    }

    private static bool ForwardAzimuthal(double longitude, double latitude, PlanetaryProjectionParameters p, out double2 coordinate)
    {
        var sin0 = Math.Sin(p.CenterLatitude); var cos0 = Math.Cos(p.CenterLatitude);
        var sin = Math.Sin(latitude); var cos = Math.Cos(latitude); var cosLon = Math.Cos(longitude);
        var cosC = Math.Clamp(sin0 * sin + cos0 * cos * cosLon, -1, 1);
        var rawX = cos * Math.Sin(longitude);
        var rawY = cos0 * sin - sin0 * cos * cosLon;
        double factor;
        switch (p.Kind)
        {
            case PlanetaryProjectionKind.Orthographic:
                if (cosC < 0) { coordinate = double2.zero; return false; }
                factor = 1; break;
            case PlanetaryProjectionKind.AzimuthalEquidistant:
            {
                var c = Math.Acos(cosC); factor = c < 1.0e-12 ? 1 : c / Math.Sin(c); factor /= Math.PI; break;
            }
            case PlanetaryProjectionKind.AzimuthalEqualArea:
                factor = Math.Sqrt(2 / Math.Max(1 + cosC, 1.0e-20)) * 0.5; break;
            case PlanetaryProjectionKind.LocalTangent:
                if (cosC <= 0) { coordinate = double2.zero; return false; }
                factor = 1 / cosC; break;
            default: throw new ArgumentOutOfRangeException(nameof(p));
        }
        coordinate = new(rawX * factor, rawY * factor);
        return true;
    }

    private static bool InverseAzimuthal(double2 coordinate, PlanetaryProjectionParameters p, out double longitude, out double latitude)
    {
        longitude = latitude = 0;
        var rho = Math.Sqrt(coordinate.x * coordinate.x + coordinate.y * coordinate.y);
        double c;
        switch (p.Kind)
        {
            case PlanetaryProjectionKind.Orthographic: if (rho > 1) return false; c = Math.Asin(Math.Clamp(rho, 0, 1)); break;
            case PlanetaryProjectionKind.AzimuthalEquidistant: if (rho > 1) return false; c = rho * Math.PI; break;
            case PlanetaryProjectionKind.AzimuthalEqualArea: if (rho > 1) return false; c = 2 * Math.Asin(Math.Clamp(rho, 0, 1)); break;
            case PlanetaryProjectionKind.LocalTangent: c = Math.Atan(rho); break;
            default: throw new ArgumentOutOfRangeException(nameof(p));
        }
        if (rho < 1.0e-12) { latitude = p.CenterLatitude; return true; }
        var sinC = Math.Sin(c); var cosC = Math.Cos(c);
        var sin0 = Math.Sin(p.CenterLatitude); var cos0 = Math.Cos(p.CenterLatitude);
        latitude = Math.Asin(Math.Clamp(cosC * sin0 + coordinate.y * sinC * cos0 / rho, -1, 1));
        longitude = Math.Atan2(coordinate.x * sinC, rho * cos0 * cosC - coordinate.y * sin0 * sinC);
        return true;
    }

    private static bool TryInverseCubeAtlas(double2 coordinate, out float3 direction)
    {
        direction = float3.zero;
        if (coordinate.x is < -1 or > 1 || coordinate.y is < -1 or > 1) return false;
        var column = Math.Min((int)((coordinate.x + 1) * 1.5), 2);
        var row = Math.Min((int)((1 - coordinate.y)), 1);
        var u = ((coordinate.x + 1) * 1.5 - column) * 2 - 1;
        var v = ((1 - coordinate.y) - row) * 2 - 1;
        direction = PlanetaryTopology.Direction(new((PlanetaryCubeFace)(row * 3 + column), u, v));
        return true;
    }

    private static double EqualEarthY(double theta)
    {
        var theta2 = theta * theta;
        var theta6 = theta2 * theta2 * theta2;
        return theta * (EqualEarthA1 + EqualEarthA2 * theta2 + theta6 * (EqualEarthA3 + EqualEarthA4 * theta2));
    }

    private static double WrapLongitude(double longitude)
    {
        longitude %= Math.PI * 2;
        if (longitude > Math.PI) longitude -= Math.PI * 2;
        if (longitude < -Math.PI) longitude += Math.PI * 2;
        return longitude;
    }
}

public readonly record struct PlanetaryMapTileLayout(
    PlanetaryProjectionParameters Projection,
    int Level,
    int X,
    int Y,
    int InteriorSize,
    int BorderSize)
{
    public int StorageSize => checked(InteriorSize + BorderSize * 2);

    public PlanetaryMapTileLayout Validate()
    {
        Projection.Validate();
        if (Level is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(Level));
        var count = 1 << Level;
        if ((uint)X >= (uint)count || (uint)Y >= (uint)count) throw new ArgumentOutOfRangeException(nameof(X));
        if (InteriorSize < 2 || BorderSize < 0 || BorderSize >= InteriorSize) throw new ArgumentOutOfRangeException(nameof(InteriorSize));
        return this;
    }
}

public static class PlanetaryMapTileSampling
{
    public static bool TryDirection(PlanetaryMapTileLayout tile, int storageX, int storageY, out float3 direction)
    {
        tile.Validate();
        if ((uint)storageX >= (uint)tile.StorageSize || (uint)storageY >= (uint)tile.StorageSize) throw new ArgumentOutOfRangeException(nameof(storageX));
        var count = 1 << tile.Level;
        var localU = (storageX - tile.BorderSize) / (double)(tile.InteriorSize - 1);
        var localV = (storageY - tile.BorderSize) / (double)(tile.InteriorSize - 1);
        var map = new double2(-1 + 2 * (tile.X + localU) / count, 1 - 2 * (tile.Y + localV) / count);
        return PlanetaryProjection.TryInverse(map, tile.Projection, out direction);
    }
}

public readonly record struct PlanetaryMapTileKey(
    ulong FieldVersion,
    uint ProjectionVersion,
    uint LayerId,
    PlanetaryMapTileLayout Layout,
    PlanetaryQueryScale QueryScale)
{
    public PlanetaryMapTileKey Validate()
    {
        if (FieldVersion == 0 || ProjectionVersion == 0) throw new ArgumentOutOfRangeException(nameof(FieldVersion));
        Layout.Validate(); QueryScale.Validate();
        return this;
    }
}

public readonly record struct PlanetarySurfaceMapTile(
    PlanetaryMapTileKey Key,
    PlanetarySurfaceSample[] Samples,
    bool[] Validity);

public static class PlanetaryMapTileBaker
{
    public static PlanetarySurfaceMapTile Bake<TSource>(
        in PlanetaryFieldDefinition field,
        PlanetaryMapTileKey key,
        TSource source)
        where TSource : IPlanetaryBaseField
    {
        field.Validate(); key.Validate();
        if (field.FieldVersion != key.FieldVersion) throw new ArgumentException("Tile field version does not match definition.", nameof(key));
        var count = key.Layout.StorageSize * key.Layout.StorageSize;
        var samples = new PlanetarySurfaceSample[count];
        var validity = new bool[count];
        for (var y = 0; y < key.Layout.StorageSize; y++)
        for (var x = 0; x < key.Layout.StorageSize; x++)
        {
            var index = y * key.Layout.StorageSize + x;
            if (!PlanetaryMapTileSampling.TryDirection(key.Layout, x, y, out var direction)) continue;
            samples[index] = PlanetaryField.Sample(field, direction, source.Sample(direction), key.QueryScale);
            validity[index] = true;
        }
        return new(key, samples, validity);
    }
}
