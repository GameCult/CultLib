using Xunit;

namespace CultMath.Tests;

public sealed class PlanetaryGpuPageTests
{
    [Fact]
    public void UploadContentUsesCanonicalDirectionsSpacingAndMetadata()
    {
        var layout = new PlanetaryPageLayout(new(PlanetaryCubeFace.NegativeY, 3, 4, 2), 9, 2);
        var content = PlanetaryGpuPageBuilder.BuildContent(layout, 8.4f);
        Assert.Equal(layout.StorageSize * layout.StorageSize, content.Inputs.Length);
        Assert.True(content.ParentSampleSpacing > content.SampleSpacing);
        var first = content.Inputs[0];
        Assert.Equal(PlanetaryPageSampling.Direction(layout, 0, 0), first.DirectionRadius.xyz);
        Assert.Equal(8.4f, first.DirectionRadius.w);
        Assert.Equal(content.SampleSpacing, first.Sampling.x);
        Assert.Equal(content.ParentSampleSpacing, first.Sampling.y);

        var metadata = PlanetaryGpuPageBuilder.Metadata(content, 37, 0.25f);
        Assert.Equal(new float4((int)layout.Tile.Face, layout.Tile.Level, layout.Tile.X, layout.Tile.Y), metadata.Address);
        Assert.Equal(new float4(37, layout.StorageSize, layout.InteriorSize, layout.BorderSize), metadata.Layout);
        Assert.Equal(0.25f, metadata.State.y);
    }
}
