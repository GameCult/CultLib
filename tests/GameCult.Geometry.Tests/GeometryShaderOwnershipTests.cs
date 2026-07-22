using System;
using System.IO;
using NUnit.Framework;

namespace GameCult.Geometry.Tests;

public sealed class GeometryShaderOwnershipTests
{
    [Test]
    public void GeometryUmbrellaOwnsTheCompleteShaderSurface()
    {
        var shaders = Path.Combine(GetRepositoryRoot(), "src", "GameCult.Geometry", "Shaders");
        var umbrella = File.ReadAllText(Path.Combine(shaders, "GameCult.Geometry.hlsl"));
        var erosion = File.ReadAllText(Path.Combine(shaders, "AdvancedErosionFilter.hlsl"));
        var planetary = File.ReadAllText(Path.Combine(shaders, "Planetary.hlsl"));
        var spherical = File.ReadAllText(Path.Combine(shaders, "SphericalErosion.hlsl"));
        var refinement = File.ReadAllText(Path.Combine(shaders, "PlanetaryRadialRefinement.hlsl"));

        Assert.That(umbrella, Does.Contain("#define GAMECULT_GEOMETRY_CULTMATH_INCLUDE \"CultMath.hlsl\"")
            .And.Contain("#include GAMECULT_GEOMETRY_CULTMATH_INCLUDE")
            .And.Contain("#include \"AdvancedErosionFilter.hlsl\"")
            .And.Contain("#include \"Planetary.hlsl\"")
            .And.Contain("#include \"SphericalErosion.hlsl\"")
            .And.Contain("#include \"PlanetaryRadialRefinement.hlsl\""));
        Assert.That(erosion, Does.Contain("gamecult_geometry_advanced_erosion_filter")
            .And.Contain("GameCultGeometryAdvancedErosionParameters"));
        Assert.That(planetary, Does.Contain("gamecult_geometry_planetary_face_direction")
            .And.Contain("gamecult_geometry_planetary_field_sample")
            .And.Contain("GameCultGeometryPlanetarySurfaceSample"));
        Assert.That(spherical, Does.Contain("gamecult_geometry_spherical_erosion")
            .And.Contain("GameCultGeometrySphericalErosionParameters"));
        Assert.That(refinement, Does.Contain("gamecult_geometry_planetary_radial_refinement_step"));
    }

    [Test]
    public void GeometryShadersDoNotPublishCultMathGeometrySymbols()
    {
        var shaders = Path.Combine(GetRepositoryRoot(), "src", "GameCult.Geometry", "Shaders");
        foreach (var path in Directory.GetFiles(shaders, "*.hlsl"))
        {
            var source = File.ReadAllText(path);
            Assert.That(source, Does.Not.Contain("cultmath_planetary_"), Path.GetFileName(path));
            Assert.That(source, Does.Not.Contain("cultmath_spherical_"), Path.GetFileName(path));
            Assert.That(source, Does.Not.Contain("cultmath_advanced_erosion_"), Path.GetFileName(path));
        }
    }

    [Test]
    public void UnityPlanetaryViewerUsesTheGeometryUmbrellaAndOwnedSymbols()
    {
        var shader = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "unity", "org.gamecult.geometry", "Samples~", "PlanetaryViewer", "Runtime",
            "GameCultGeometryPlanetaryViewer.shader"));

        Assert.That(shader, Does.Contain(
                "#define GAMECULT_GEOMETRY_CULTMATH_INCLUDE \"Packages/org.gamecult.cultmath/shaders/CultMath.hlsl\"")
            .And.Contain("#include \"Packages/org.gamecult.geometry/Shaders/GameCult.Geometry.hlsl\"")
            .And.Contain("gamecult_geometry_planetary_field_sample")
            .And.Not.Contain("cultmath_planetary_")
            .And.Not.Contain("CultMathPlanetary"));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate CultLib repository root.");
    }
}
