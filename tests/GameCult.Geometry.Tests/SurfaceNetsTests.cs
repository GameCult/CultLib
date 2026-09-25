using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Geometry.Tests
{
    [TestFixture]
    public sealed class SurfaceNetsTests
    {
        [Test]
        public void A_sample_exactly_on_the_isovalue_is_inside_like_a_sample_below_it()
        {
            var atIso = SingleInsideSample(3, 3, 3, 1, 1, 1, 0f);
            var belowIso = SingleInsideSample(3, 3, 3, 1, 1, 1, -1e-4f);
            var aboveIso = SingleInsideSample(3, 3, 3, 1, 1, 1, 1e-4f);

            var meshAtIso = CultGeometrySurfaceNets.Extract(atIso);
            var meshBelowIso = CultGeometrySurfaceNets.Extract(belowIso);
            var meshAboveIso = CultGeometrySurfaceNets.Extract(aboveIso);

            meshAtIso.QuadEdges.Should().Equal(meshBelowIso.QuadEdges);
            meshAtIso.QuadCount.Should().Be(6);
            meshAboveIso.QuadCount.Should().Be(0);
        }

        [Test]
        public void A_single_interior_inside_sample_names_its_six_quads_by_their_crossing_edge()
        {
            var samples = SingleInsideSample(3, 3, 3, 1, 1, 1, -1f);

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().Be(6);
            mesh.QuadEdges.Should().BeEquivalentTo(new[]
            {
                new CultGeometryGridEdge(1, 1, 1, 0),
                new CultGeometryGridEdge(1, 1, 1, 1),
                new CultGeometryGridEdge(1, 1, 1, 2),
                new CultGeometryGridEdge(0, 1, 1, 0),
                new CultGeometryGridEdge(1, 0, 1, 1),
                new CultGeometryGridEdge(1, 1, 0, 2),
            });
        }

        [Test]
        public void A_non_cubic_grid_names_quads_by_edge_without_swapping_axes()
        {
            var samples = SingleInsideSample(4, 3, 5, 2, 1, 2, -1f);

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().Be(6);
            mesh.QuadEdges.Should().BeEquivalentTo(new[]
            {
                new CultGeometryGridEdge(2, 1, 2, 0),
                new CultGeometryGridEdge(2, 1, 2, 1),
                new CultGeometryGridEdge(2, 1, 2, 2),
                new CultGeometryGridEdge(1, 1, 2, 0),
                new CultGeometryGridEdge(2, 0, 2, 1),
                new CultGeometryGridEdge(2, 1, 1, 2),
            });
        }

        [Test]
        public void Every_quad_winds_outward_from_inside_to_outside()
        {
            var samples = SingleInsideSample(3, 3, 3, 1, 1, 1, -1f);

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().Be(6);
            for (var quad = 0; quad < mesh.QuadCount; quad++)
            {
                var edge = mesh.QuadEdges[quad];
                var insideCoord = new CultVec3(1f, 1f, 1f);
                var edgeStart = new CultVec3(edge.X, edge.Y, edge.Z);
                var edgeEnd = OtherEndpoint(edge);

                // The single inside sample is (1,1,1); the edge's other endpoint is outside.
                var outsideCoord = edgeStart.Equals(insideCoord) ? edgeEnd : edgeStart;
                var outward = Subtract(outsideCoord, insideCoord);

                var baseIndex = quad * 4;
                var v0 = Position(mesh.Positions, mesh.Quads[baseIndex]);
                var v1 = Position(mesh.Positions, mesh.Quads[baseIndex + 1]);
                var v2 = Position(mesh.Positions, mesh.Quads[baseIndex + 2]);
                var v3 = Position(mesh.Positions, mesh.Quads[baseIndex + 3]);
                var cross = Cross(Subtract(v2, v0), Subtract(v3, v1));

                Dot(cross, outward).Should().BePositive($"quad for edge {edge} should wind outward");
            }
        }

        [Test]
        public void A_cells_vertex_is_the_mean_of_its_symmetric_edge_crossing_midpoints()
        {
            // A single cell with one inside corner cut symmetrically by +-1 values: each of the
            // three crossing edges interpolates to its own midpoint.
            var samples = new float[2, 2, 2];
            samples[0, 0, 0] = -1f;
            samples[1, 0, 0] = 1f;
            samples[0, 1, 0] = 1f;
            samples[0, 0, 1] = 1f;
            samples[1, 1, 0] = 5f;
            samples[1, 0, 1] = 5f;
            samples[0, 1, 1] = 5f;
            samples[1, 1, 1] = 5f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(1f / 6f, 1e-6f);
            mesh.Positions[1].Should().BeApproximately(1f / 6f, 1e-6f);
            mesh.Positions[2].Should().BeApproximately(1f / 6f, 1e-6f);
        }

        [Test]
        public void A_cells_vertex_is_the_mean_of_unequal_edge_crossings()
        {
            var samples = new float[2, 2, 2];
            samples[0, 0, 0] = -1f;
            samples[1, 0, 0] = 3f; // crossing at 0.25 along x
            samples[0, 1, 0] = 1f; // crossing at 0.5 along y
            samples[0, 0, 1] = 4f; // crossing at 0.2 along z
            samples[1, 1, 0] = 5f;
            samples[1, 0, 1] = 6f;
            samples[0, 1, 1] = 7f;
            samples[1, 1, 1] = 8f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(0.25f / 3f, 1e-6f);
            mesh.Positions[1].Should().BeApproximately(0.5f / 3f, 1e-6f);
            mesh.Positions[2].Should().BeApproximately(0.2f / 3f, 1e-6f);
        }

        [Test]
        public void Vertex_placement_applies_origin_and_cell_size()
        {
            var samples = new float[2, 2, 2];
            samples[0, 0, 0] = -1f;
            samples[1, 0, 0] = 1f;
            samples[0, 1, 0] = 1f;
            samples[0, 0, 1] = 1f;
            samples[1, 1, 0] = 5f;
            samples[1, 0, 1] = 5f;
            samples[0, 1, 1] = 5f;
            samples[1, 1, 1] = 5f;

            var mesh = CultGeometrySurfaceNets.Extract(samples, origin: new CultVec3(10f, 20f, 30f), cellSize: 2f);

            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(10f + (1f / 6f * 2f), 1e-5f);
            mesh.Positions[1].Should().BeApproximately(20f + (1f / 6f * 2f), 1e-5f);
            mesh.Positions[2].Should().BeApproximately(30f + (1f / 6f * 2f), 1e-5f);
        }

        [TestCase(6)]
        [TestCase(8)]
        [TestCase(10)]
        public void A_closed_sphere_field_welds_into_a_genus_zero_manifold(int size)
        {
            var samples = Sphere(size);

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().BeGreaterThan(0);
            var usage = EdgeUsage(mesh);
            usage.Values.Should().OnlyContain(count => count == 2);
            (mesh.VertexCount - mesh.QuadCount).Should().Be(2);
        }

        [Test]
        public void An_ambiguous_checkerboard_field_has_even_but_not_necessarily_manifold_edge_usage()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++)
            {
                samples[x, y, z] = ((x + y + z) % 2 == 0) ? -1f : 1f;
            }

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().BeGreaterThan(0);
            var usage = EdgeUsage(mesh);
            usage.Values.Should().OnlyContain(count => count % 2 == 0);
        }

        [Test]
        public void An_open_plane_emits_quads_only_where_all_four_surrounding_cells_exist()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++)
            {
                samples[x, y, z] = x - 1.5f;
            }

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            // The plane crosses 16 grid edges (all y,z at the x=1|2 boundary), but only the
            // interior y,z in {1,2} have all four surrounding cells, so only 4 quads are emitted.
            mesh.QuadCount.Should().Be(4);
            mesh.QuadEdges.Should().OnlyContain(edge =>
                edge.X == 1 && edge.Axis == 0 && edge.Y is 1 or 2 && edge.Z is 1 or 2);
        }

        [Test]
        public void Extraction_is_deterministic_and_quad_edges_are_strictly_ascending()
        {
            var samples = Sphere(8);

            var first = CultGeometrySurfaceNets.Extract(samples);
            var second = CultGeometrySurfaceNets.Extract(samples);

            second.Positions.Should().Equal(first.Positions);
            second.Quads.Should().Equal(first.Quads);
            second.QuadEdges.Should().Equal(first.QuadEdges);

            for (var index = 1; index < first.QuadEdges.Length; index++)
            {
                first.QuadEdges[index].CompareTo(first.QuadEdges[index - 1]).Should().BePositive();
            }
        }

        [Test]
        public void Null_field_is_rejected()
        {
            Action act = () => CultGeometrySurfaceNets.Extract(null!);

            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void Invalid_field_shape_is_rejected()
        {
            Action act = () => CultGeometrySurfaceNets.Extract(new float[1, 2, 2]);

            act.Should().Throw<ArgumentException>();
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Invalid_cell_size_is_rejected(float cellSize)
        {
            var samples = new float[2, 2, 2];

            Action act = () => CultGeometrySurfaceNets.Extract(samples, cellSize: cellSize);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        private static float[,,] SingleInsideSample(int sizeX, int sizeY, int sizeZ, int ix, int iy, int iz, float insideValue)
        {
            var samples = new float[sizeX, sizeY, sizeZ];
            for (var x = 0; x < sizeX; x++)
            for (var y = 0; y < sizeY; y++)
            for (var z = 0; z < sizeZ; z++) samples[x, y, z] = 1f;

            samples[ix, iy, iz] = insideValue;
            return samples;
        }

        private static float[,,] Sphere(int size)
        {
            var samples = new float[size, size, size];
            var center = (size - 1) / 2f;
            var radius = center - 1f;
            for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
            for (var z = 0; z < size; z++)
            {
                var dx = x - center;
                var dy = y - center;
                var dz = z - center;
                var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                samples[x, y, z] = distance - radius;
            }

            return samples;
        }

        private static Dictionary<(uint, uint), int> EdgeUsage(CultGeometryQuadMesh mesh)
        {
            var usage = new Dictionary<(uint, uint), int>();
            for (var quad = 0; quad < mesh.QuadCount; quad++)
            {
                var baseIndex = quad * 4;
                var indices = new[]
                {
                    mesh.Quads[baseIndex],
                    mesh.Quads[baseIndex + 1],
                    mesh.Quads[baseIndex + 2],
                    mesh.Quads[baseIndex + 3],
                };

                for (var edge = 0; edge < 4; edge++)
                {
                    var a = indices[edge];
                    var b = indices[(edge + 1) % 4];
                    var key = a < b ? (a, b) : (b, a);
                    usage[key] = usage.GetValueOrDefault(key) + 1;
                }
            }

            return usage;
        }

        private static CultVec3 OtherEndpoint(CultGeometryGridEdge edge) => edge.Axis switch
        {
            0 => new CultVec3(edge.X + 1, edge.Y, edge.Z),
            1 => new CultVec3(edge.X, edge.Y + 1, edge.Z),
            _ => new CultVec3(edge.X, edge.Y, edge.Z + 1),
        };

        private static CultVec3 Subtract(CultVec3 left, CultVec3 right) =>
            new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        private static CultVec3 Cross(CultVec3 left, CultVec3 right) => new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));

        private static float Dot(CultVec3 left, CultVec3 right) =>
            (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

        private static CultVec3 Position(float[] positions, uint index)
        {
            var offset = (int)index * 3;
            return new CultVec3(positions[offset], positions[offset + 1], positions[offset + 2]);
        }
    }
}
