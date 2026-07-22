using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryRadialRefinementTests
{
    [Fact]
    public void FourBoundedStepsConvergeFromTheRasterCaptureShell()
    {
        var center=new float3(0.3f,-0.2f,0.7f);
        var radial=math.normalize(new float3(0.2f,-0.4f,0.9f));
        var targetRadius=3.85f;
        var target=center+radial*targetRadius;
        var origin=target+math.normalize(radial+new float3(0.12f,-0.05f,0.02f))*6.0f;
        var position=center+radial*(targetRadius+0.30f);
        var ray=math.normalize(position-origin);
        for(var step=0;step<4;step++)
            position=PlanetaryRadialRefinement.Step(position,ray,center,targetRadius);
        Assert.InRange(math.abs(math.length(position-center)-targetRadius),0.0f,1.0e-4f);
    }

    [Fact]
    public void StepClampsWeakDerivativeCorrections()
    {
        var position=new float3(0,0,4.1f);
        var result=PlanetaryRadialRefinement.Step(position,new float3(1,0,0),float3.zero,3.8f);
        Assert.InRange(math.length(result-position),0.07999f,0.08001f);
    }
}
