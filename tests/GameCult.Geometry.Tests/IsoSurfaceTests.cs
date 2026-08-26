using System;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Geometry.Tests
{
    [TestFixture]
    public sealed class IsoSurfaceTests
    {
        [TestCase(-1f)]
        [TestCase(1f)]
        public void Uniform_field_has_no_surface(float value)
        {
            var samples = Filled(2, value);

            var mesh = CultGeometryIsoSurface.Extract(samples);

            mesh.Positions.Should().BeEmpty();
            mesh.Normals.Should().BeEmpty();
            mesh.Indices.Should().BeEmpty();
        }

        [Test]
        public void Plane_field_interpolates_to_the_isovalue_and_faces_outward()
        {
            var samples = new float[2, 2, 2];
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                samples[0, y, z] = -0.5f;
                samples[1, y, z] = 0.5f;
            }

            var mesh = CultGeometryIsoSurface.Extract(samples);

            mesh.TriangleCount.Should().BeGreaterThan(0);
            for (var index = 0; index < mesh.Positions.Length; index += 3)
            {
                mesh.Positions[index].Should().BeApproximately(0.5f, 1e-6f);
            }

            for (var index = 0; index < mesh.Normals.Length; index += 3)
            {
                mesh.Normals[index].Should().BeApproximately(1f, 1e-6f);
                mesh.Normals[index + 1].Should().BeApproximately(0f, 1e-6f);
                mesh.Normals[index + 2].Should().BeApproximately(0f, 1e-6f);
            }
        }

        [Test]
        public void Extraction_is_deterministic_and_applies_origin_and_cell_size()
        {
            var samples = new float[2, 2, 2];
            samples[0, 0, 0] = -1f;
            samples[1, 0, 0] = 1f;
            samples[0, 1, 0] = 1f;
            samples[1, 1, 0] = 1f;
            samples[0, 0, 1] = 1f;
            samples[1, 0, 1] = 1f;
            samples[0, 1, 1] = 1f;
            samples[1, 1, 1] = 1f;

            var first = CultGeometryIsoSurface.Extract(samples, origin: new CultVec3(10f, 20f, 30f), cellSize: 2f);
            var second = CultGeometryIsoSurface.Extract(samples, origin: new CultVec3(10f, 20f, 30f), cellSize: 2f);

            second.Positions.Should().Equal(first.Positions);
            second.Normals.Should().Equal(first.Normals);
            second.Indices.Should().Equal(first.Indices);
            first.Positions.Should().OnlyContain(value => value >= 10f);
        }

        [Test]
        public void Invalid_field_shape_is_rejected()
        {
            Action act = () => CultGeometryIsoSurface.Extract(new float[1, 2, 2]);

            act.Should().Throw<ArgumentException>();
        }

        private static float[,,] Filled(int size, float value)
        {
            var samples = new float[size, size, size];
            for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
            for (var z = 0; z < size; z++) samples[x, y, z] = value;
            return samples;
        }
    }
}
