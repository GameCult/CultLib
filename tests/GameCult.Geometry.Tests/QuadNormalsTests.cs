using System;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Geometry.Tests
{
    [TestFixture]
    public sealed class QuadNormalsTests
    {
        [Test]
        public void A_sliver_sharing_a_vertex_does_not_move_the_large_quads_normal()
        {
            // A 10x10 quad in the XY plane (facing +Z) sharing vertex 0 with a near-degenerate
            // sliver tilted into the XZ plane. The sliver's area is ~1e6 times smaller. Offset away
            // from the origin so a diagonal computed as (p2 + p0) instead of (p2 - p0) is not
            // coincidentally the same value (p0 = 0 would erase that distinction).
            var mesh = new CultGeometryQuadMesh
            {
                Positions = new[]
                {
                    // Large quad: 0,1,2,3
                    5f, 7f, 9f,
                    15f, 7f, 9f,
                    15f, 17f, 9f,
                    5f, 17f, 9f,
                    // Sliver quad: 0 (shared), 4, 5, 6
                    5.001f, 7f, 9f,
                    5.001f, 7f, 9.001f,
                    5f, 7f, 9.001f,
                },
                Quads = new[]
                {
                    0u, 1u, 2u, 3u,
                    0u, 4u, 5u, 6u,
                },
                QuadEdges = new[]
                {
                    new CultGeometryGridEdge(0, 0, 0, 2),
                    new CultGeometryGridEdge(0, 0, 0, 1),
                },
            };

            var normals = CultGeometryQuadNormals.FaceWeighted(mesh);

            normals[0].Should().BeApproximately(0f, 1e-4f);
            normals[1].Should().BeApproximately(0f, 1e-4f);
            normals[2].Should().BeApproximately(1f, 1e-4f);
        }

        [Test]
        public void A_closed_cube_gets_unit_length_normals_pointing_away_from_center()
        {
            var mesh = Cube();

            var normals = CultGeometryQuadNormals.FaceWeighted(mesh);

            // Each vertex's normal must point away from the cube's center (0.5,0.5,0.5) along every
            // axis, i.e. its sign must match the vertex's own offset from the center. Checking the
            // exact direction (not just the length) catches a mutant that flips the sign of one of a
            // vertex's three contributing faces: three orthogonal +-2-length vectors summed have the
            // same length no matter which one is negated, so a length-only check cannot tell them apart.
            var oneOverRoot3 = 1f / MathF.Sqrt(3f);
            for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                var px = mesh.Positions[vertex * 3];
                var py = mesh.Positions[(vertex * 3) + 1];
                var pz = mesh.Positions[(vertex * 3) + 2];
                var expected = new[]
                {
                    px > 0.5f ? oneOverRoot3 : -oneOverRoot3,
                    py > 0.5f ? oneOverRoot3 : -oneOverRoot3,
                    pz > 0.5f ? oneOverRoot3 : -oneOverRoot3,
                };

                normals[vertex * 3].Should().BeApproximately(expected[0], 1e-5f);
                normals[(vertex * 3) + 1].Should().BeApproximately(expected[1], 1e-5f);
                normals[(vertex * 3) + 2].Should().BeApproximately(expected[2], 1e-5f);
            }
        }

        [Test]
        public void A_zero_area_quad_contributes_nothing_and_an_untouched_vertex_is_a_finite_zero()
        {
            var mesh = new CultGeometryQuadMesh
            {
                // Vertex 4 is never referenced by any quad.
                Positions = new[]
                {
                    0f, 0f, 0f,
                    1f, 0f, 0f,
                    2f, 0f, 0f,
                    3f, 0f, 0f,
                    9f, 9f, 9f,
                },
                Quads = new[] { 0u, 1u, 2u, 3u },
                QuadEdges = new[] { new CultGeometryGridEdge(0, 0, 0, 0) },
            };

            var normals = CultGeometryQuadNormals.FaceWeighted(mesh);

            normals.Should().HaveCount(15);
            normals.Should().OnlyContain(value => value == 0f);
            normals.Should().OnlyContain(value => float.IsFinite(value));
        }

        [Test]
        public void Null_mesh_is_rejected()
        {
            Action act = () => CultGeometryQuadNormals.FaceWeighted(null!);

            act.Should().Throw<ArgumentNullException>();
        }

        private static CultGeometryQuadMesh Cube()
        {
            // 0..7: (0,0,0) (1,0,0) (1,1,0) (0,1,0) (0,0,1) (1,0,1) (1,1,1) (0,1,1)
            var positions = new[]
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                1f, 1f, 0f,
                0f, 1f, 0f,
                0f, 0f, 1f,
                1f, 0f, 1f,
                1f, 1f, 1f,
                0f, 1f, 1f,
            };

            // Each face ordered so d1 x d2 points outward.
            var quads = new[]
            {
                0u, 3u, 2u, 1u, // -Z
                4u, 5u, 6u, 7u, // +Z
                0u, 1u, 5u, 4u, // -Y
                3u, 7u, 6u, 2u, // +Y
                0u, 4u, 7u, 3u, // -X
                1u, 2u, 6u, 5u, // +X
            };

            var quadEdges = new[]
            {
                new CultGeometryGridEdge(0, 0, 0, 0),
                new CultGeometryGridEdge(0, 0, 0, 1),
                new CultGeometryGridEdge(0, 0, 0, 2),
                new CultGeometryGridEdge(0, 1, 0, 0),
                new CultGeometryGridEdge(1, 0, 0, 1),
                new CultGeometryGridEdge(0, 0, 1, 2),
            };

            return new CultGeometryQuadMesh
            {
                Positions = positions,
                Quads = quads,
                QuadEdges = quadEdges,
            };
        }
    }
}
