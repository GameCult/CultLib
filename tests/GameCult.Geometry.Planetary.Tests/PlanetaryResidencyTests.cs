using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryResidencyTests
{
    private static readonly PlanetaryFieldDefinition Field = new(
        1, 8.4f, 0, AdvancedErosionParameters.Default with { Scale = 0.63f, Strength = 0.12f, Octaves = 7 });
    private static readonly PlanetaryLodParameters Lod = new(5, 17, 2, 720, 8.4f / 65536);

    [Fact]
    public void SelectorReturnsSixRootsAndOneAncestorChain()
    {
        var direction = math.normalize(new float3(-0.2f, 0.91f, 0.37f));
        var selected = PlanetaryLodSelector.SelectAncestorChain(Field, direction, 8.45f, Lod);
        Assert.Equal(6, selected.Count(tile => tile.Level == 0));
        var descendants = selected.Where(tile => tile.Level > 0).OrderBy(tile => tile.Level).ToArray();
        for (var i = 1; i < descendants.Length; i++) Assert.Equal(descendants[i - 1], descendants[i].Parent());
        if (descendants.Length > 0) Assert.Equal(PlanetaryTopology.TileAt(direction, descendants[^1].Level), descendants[^1]);
    }

    [Fact]
    public void ResidencyFadesResidualsWithoutChangingContentDuringPresentation()
    {
        var root = new PlanetaryTileAddress(PlanetaryCubeFace.PositiveX, 0, 0, 0);
        var child = root.Child(1, 0);
        var residency = new PlanetaryResidualResidency();
        var arrival = residency.Update(new[] { root, child }, 0, 1);
        var midpoint = residency.Update(new[] { root, child }, 0.5f, 1);
        var settled = residency.Update(new[] { root, child }, 1, 1);
        Assert.Equal(arrival.ContentVersion, midpoint.ContentVersion);
        Assert.Equal(midpoint.ContentVersion, settled.ContentVersion);
        Assert.Equal(0, arrival.Tiles.Single(tile => tile.Tile == child).Blend);
        Assert.Equal(0.5f, midpoint.Tiles.Single(tile => tile.Tile == child).Blend);
        Assert.Equal(1, settled.Tiles.Single(tile => tile.Tile == child).Blend);

        var leaving = residency.Update(new[] { root }, 1.25f, 1);
        Assert.True(leaving.Tiles.Single(tile => tile.Tile == child).Departing);
        Assert.Equal(1, leaving.Tiles.Single(tile => tile.Tile == child).Blend);
        var gone = residency.Update(new[] { root }, 2.25f, 1);
        Assert.DoesNotContain(gone.Tiles, tile => tile.Tile == child);
    }

    [Fact]
    public void ReversingAnArrivalDepartsFromItsCurrentWeightWithoutJumping()
    {
        var root = new PlanetaryTileAddress(PlanetaryCubeFace.PositiveX, 0, 0, 0);
        var child = root.Child(0, 1);
        var residency = new PlanetaryResidualResidency();
        _ = residency.Update(new[] { root, child }, 0, 1);
        var arriving = residency.Update(new[] { root, child }, 0.25f, 1);
        var departing = residency.Update(new[] { root }, 0.25f, 1);
        Assert.Equal(0.25f, arriving.Tiles.Single(tile => tile.Tile == child).Blend);
        Assert.Equal(0.25f, departing.Tiles.Single(tile => tile.Tile == child).Blend);
        Assert.True(departing.Tiles.Single(tile => tile.Tile == child).Departing);
    }
}
