using CultMath;
using GameCult.Geometry;

if (args.Length != 1) throw new ArgumentException("Expected one output path.");
var field = GameCult.Geometry.PlanetaryFieldDefinition.Create(0x55aa, 10, 7, GameCult.Geometry.AdvancedErosionParameters.Default);
var layout = new GameCult.Geometry.PlanetaryMapTileLayout(new(GameCult.Geometry.PlanetaryProjectionKind.EqualEarth), 0, 0, 0, 5, 1);
var key = new GameCult.Geometry.PlanetaryMapTileKey(field.FieldVersion, 3, 29, layout, GameCult.Geometry.PlanetaryQueryScale.AtFootprint(0.25f, 0.125f));
var tile = GameCult.Geometry.PlanetaryMapTileBaker.Bake(field, key, new FixtureField());
var path = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
using var stream = File.Create(path);
GameCult.Geometry.PlanetaryMapTileEncoding.Write(stream, tile);

readonly struct FixtureField : GameCult.Geometry.IPlanetaryBaseField
{
    public GameCult.Geometry.PlanetaryBaseFieldSample Sample(float3 direction)
    {
        var value = direction.x * 0.2f + direction.z * 0.4f;
        var gradient = new float3(0.2f, 0, 0.4f);
        gradient -= direction * math.dot(gradient, direction);
        return new(value, gradient, value, gradient * 0.1f, value);
    }
}
