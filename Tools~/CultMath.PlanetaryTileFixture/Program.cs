using CultMath;

if (args.Length != 1) throw new ArgumentException("Expected one output path.");
var field = PlanetaryFieldDefinition.Create(0x55aa, 10, 7, AdvancedErosionParameters.Default);
var layout = new PlanetaryMapTileLayout(new(PlanetaryProjectionKind.EqualEarth), 0, 0, 0, 5, 1);
var key = new PlanetaryMapTileKey(field.FieldVersion, 3, 29, layout, PlanetaryQueryScale.AtFootprint(0.25f, 0.125f));
var tile = PlanetaryMapTileBaker.Bake(field, key, new FixtureField());
var path = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
using var stream = File.Create(path);
PlanetaryMapTileEncoding.Write(stream, tile);

readonly struct FixtureField : IPlanetaryBaseField
{
    public PlanetaryBaseFieldSample Sample(float3 direction)
    {
        var value = direction.x * 0.2f + direction.z * 0.4f;
        var gradient = new float3(0.2f, 0, 0.4f);
        gradient -= direction * math.dot(gradient, direction);
        return new(value, gradient, value, gradient * 0.1f, value);
    }
}
