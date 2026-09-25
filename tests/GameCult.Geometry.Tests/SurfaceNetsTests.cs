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

                // Restricted to non-degenerate (non-zero-area) quads: the spec's geometric
                // winding rule is only meaningful there. Degenerate/collapsed quads are covered
                // separately by AssertEdgeDirectionsBalance, since a zero cross product carries
                // no orientation information to check here.
                if (cross.X == 0f && cross.Y == 0f && cross.Z == 0f) continue;

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

        // A single cell's 12 edges form a cube graph (3-regular, 8 vertices): the number of edges
        // with differently-signed endpoints (a graph cut) is provably never 1 or 2 for any corner
        // sign pattern - the minimum nonzero cut is 3 (one corner opposite the other seven), which
        // the two fixtures above already exercise. 4 and 6 below are the next cuts a single cell
        // can actually produce; both distinguish sum/count from the sum/3 mutant just as well.
        [Test]
        public void A_cells_vertex_with_four_crossings_is_their_mean_not_a_third()
        {
            // One face (x=0) inside, the opposite face (x=1) outside: only the 4 x-axis edges
            // cross, each at the same amount since every inside/outside value is uniform.
            var samples = new float[2, 2, 2];
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                samples[0, y, z] = -1f;
                samples[1, y, z] = 3f;
            }

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            // amount = (0 - -1) / (3 - -1) = 0.25 for all four x-edges, at (y, z) in {0,1}x{0,1}.
            // mean = ((0.25,0,0)+(0.25,1,0)+(0.25,0,1)+(0.25,1,1)) / 4 = (0.25, 0.5, 0.5).
            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(0.25f, 1e-6f);
            mesh.Positions[1].Should().BeApproximately(0.5f, 1e-6f);
            mesh.Positions[2].Should().BeApproximately(0.5f, 1e-6f);
        }

        [Test]
        public void A_cells_vertex_with_six_crossings_is_their_mean_not_a_third()
        {
            // Two face-diagonal (non-adjacent) corners inside, the other six outside: a cut of 6
            // (3 edges from each inside corner; the two inside corners share no edge).
            var samples = new float[2, 2, 2];
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++) samples[x, y, z] = 3f;

            samples[0, 0, 0] = -1f;
            samples[1, 1, 0] = -1f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            // amount = (0 - -1) / (3 - -1) = 0.25 for all six crossings (uniform inside/outside
            // values). From (0,0,0): (0.25,0,0), (0,0.25,0), (0,0,0.25).
            // From (1,1,0): (0.75,1,0), (1,0.75,0), (1,1,0.25).
            // mean = (3.0, 3.0, 0.5) / 6 = (0.5, 0.5, 1/12).
            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(0.5f, 1e-6f);
            mesh.Positions[1].Should().BeApproximately(0.5f, 1e-6f);
            mesh.Positions[2].Should().BeApproximately(1f / 12f, 1e-6f);
        }

        [Test]
        public void Vertex_placement_applies_origin_and_cell_size()
        {
            // The inside corner is (1,1,1), not (0,0,0): every crossing edge's start sample has at
            // least one non-zero coordinate, so a cellSize applied by division instead of
            // multiplication (or dropped altogether) changes the result instead of coincidentally
            // matching it (a zero component erases that distinction either way).
            var samples = new float[2, 2, 2];
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++) samples[x, y, z] = 9f;

            samples[1, 1, 1] = -1f;

            var mesh = CultGeometrySurfaceNets.Extract(samples, origin: new CultVec3(10f, 20f, 30f), cellSize: 2f);

            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(10f + (2.9f / 3f * 2f), 1e-5f);
            mesh.Positions[1].Should().BeApproximately(20f + (2.9f / 3f * 2f), 1e-5f);
            mesh.Positions[2].Should().BeApproximately(30f + (2.9f / 3f * 2f), 1e-5f);
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
            // A checkerboard pattern with a one-sample uniform-outside margin, so every crossing
            // edge this exercises has all four surrounding cells in range: any odd usage would be
            // a real non-manifold artifact of the ambiguity, not an open-boundary side effect.
            var samples = new float[5, 5, 5];
            for (var x = 0; x < 5; x++)
            for (var y = 0; y < 5; y++)
            for (var z = 0; z < 5; z++)
            {
                var interior = x is >= 1 and <= 3 && y is >= 1 and <= 3 && z is >= 1 and <= 3;
                samples[x, y, z] = interior ? (((x + y + z) % 2 == 0) ? -1f : 1f) : 5f;
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
                edge.X == 1 && edge.Axis == 0 &&
                (edge.Y == 1 || edge.Y == 2) &&
                (edge.Z == 1 || edge.Z == 2));
        }

        [Test]
        public void An_open_plane_along_y_emits_quads_only_where_all_four_surrounding_cells_exist()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++)
            {
                samples[x, y, z] = y - 1.5f;
            }

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().Be(4);
            mesh.QuadEdges.Should().OnlyContain(edge =>
                edge.Y == 1 && edge.Axis == 1 &&
                (edge.X == 1 || edge.X == 2) &&
                (edge.Z == 1 || edge.Z == 2));
        }

        [Test]
        public void An_open_plane_along_z_emits_quads_only_where_all_four_surrounding_cells_exist()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++)
            {
                samples[x, y, z] = z - 1.5f;
            }

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().Be(4);
            mesh.QuadEdges.Should().OnlyContain(edge =>
                edge.Z == 1 && edge.Axis == 2 &&
                (edge.X == 1 || edge.X == 2) &&
                (edge.Y == 1 || edge.Y == 2));
        }

        [Test]
        public void A_tiny_but_normal_edge_delta_interpolates_exactly_instead_of_falling_back_to_the_midpoint()
        {
            // Every crossing edge from the inside corner has firstValue -1e-30f and secondValue
            // 3e-21f: a delta of ~3e-21, comfortably inside float's normal range (well above the
            // ~1.18e-38 normal floor) but far under the old 1e-20 guard that used to fall back to
            // the 0.5 midpoint. The exact amount, computed independently in double precision
            // below (not by calling Interpolate), is ~3.33e-10 - nowhere near 0.5.
            var samples = new float[2, 2, 2];
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++) samples[x, y, z] = 3e-21f;

            samples[0, 0, 0] = -1e-30f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            var expected = HandDerivedSingleCornerVertex(-1e-30f, 3e-21f);

            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(expected, 1e-12f);
            mesh.Positions[1].Should().BeApproximately(expected, 1e-12f);
            mesh.Positions[2].Should().BeApproximately(expected, 1e-12f);
        }

        [Test]
        public void A_subnormal_edge_delta_interpolates_exactly_instead_of_falling_back_to_the_midpoint()
        {
            // Both endpoints of every crossing edge from the inside corner are themselves
            // subnormal floats (|value| well under the ~1.1755e-38 normal floor), so the delta
            // between them is subnormal too. .NET keeps subnormal precision, so this must
            // interpolate exactly rather than fall back to 0.5, same as the tiny-but-normal case.
            var samples = new float[2, 2, 2];
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++) samples[x, y, z] = 3e-40f;

            samples[0, 0, 0] = -1e-40f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            var expected = HandDerivedSingleCornerVertex(-1e-40f, 3e-40f);

            mesh.VertexCount.Should().Be(1);
            mesh.Positions[0].Should().BeApproximately(expected, 1e-6f);
            mesh.Positions[1].Should().BeApproximately(expected, 1e-6f);
            mesh.Positions[2].Should().BeApproximately(expected, 1e-6f);
        }

        // The four cells around a crossing edge are documented as a perimeter loop in a specific
        // cyclic order per axis (see TryGetQuadCells). Each of the three tests below pins that
        // exact order end to end (which welded vertex position lands in Quads[0..3]) for one axis,
        // using an open-plane fixture whose four relevant cells have distinct, hand-computable
        // vertex positions, so swapping or mirroring any of the four is directly observable.
        [Test]
        public void An_axis_0_quads_corners_follow_the_documented_perimeter_order()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++) samples[x, y, z] = x - 1.5f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);
            var edge = new CultGeometryGridEdge(1, 1, 1, 0);

            // Cells (x, y-1, z-1), (x, y, z-1), (x, y, z), (x, y-1, z) each have their vertex at
            // the cell's own (y, z) center, since the field only varies with x: (1.5, cy+0.5,
            // cz+0.5). The edge's low end (x=1) is inside and axis 0 is not mirrored, so the
            // documented c0..c3 order is not swapped.
            var expected = new[]
            {
                new CultVec3(1.5f, 0.5f, 0.5f), // c0 = (1, 0, 0)
                new CultVec3(1.5f, 1.5f, 0.5f), // c1 = (1, 1, 0)
                new CultVec3(1.5f, 1.5f, 1.5f), // c2 = (1, 1, 1)
                new CultVec3(1.5f, 0.5f, 1.5f), // c3 = (1, 0, 1)
            };

            AssertQuadCorners(mesh, edge, expected);
        }

        [Test]
        public void An_axis_1_quads_corners_follow_the_documented_perimeter_order()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++) samples[x, y, z] = y - 1.5f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);
            var edge = new CultGeometryGridEdge(1, 1, 1, 1);

            // Cells (x-1, y, z-1), (x, y, z-1), (x, y, z), (x-1, y, z) each have their vertex at
            // (cx+0.5, 1.5, cz+0.5). The edge's low end (y=1) is inside, but axis 1 is the
            // mirrored one (OrientationSign[1] < 0), so the documented order is swapped: v1 and
            // v3 trade places relative to the raw c0..c3 listing.
            var expected = new[]
            {
                new CultVec3(0.5f, 1.5f, 0.5f), // c0 = (0, 1, 0)
                new CultVec3(0.5f, 1.5f, 1.5f), // c3 = (0, 1, 1), swapped into v1
                new CultVec3(1.5f, 1.5f, 1.5f), // c2 = (1, 1, 1)
                new CultVec3(1.5f, 1.5f, 0.5f), // c1 = (1, 1, 0), swapped into v3
            };

            AssertQuadCorners(mesh, edge, expected);
        }

        [Test]
        public void An_axis_2_quads_corners_follow_the_documented_perimeter_order()
        {
            var samples = new float[4, 4, 4];
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 4; z++) samples[x, y, z] = z - 1.5f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);
            var edge = new CultGeometryGridEdge(1, 1, 1, 2);

            // Cells (x-1, y-1, z), (x, y-1, z), (x, y, z), (x-1, y, z) each have their vertex at
            // (cx+0.5, cy+0.5, 1.5). The edge's low end (z=1) is inside and axis 2 is not
            // mirrored, so the documented c0..c3 order is not swapped.
            var expected = new[]
            {
                new CultVec3(0.5f, 0.5f, 1.5f), // c0 = (0, 0, 1)
                new CultVec3(1.5f, 0.5f, 1.5f), // c1 = (1, 0, 1)
                new CultVec3(1.5f, 1.5f, 1.5f), // c2 = (1, 1, 1)
                new CultVec3(0.5f, 1.5f, 1.5f), // c3 = (0, 1, 1)
            };

            AssertQuadCorners(mesh, edge, expected);
        }

        // The spec's orientation rule "(d1 x d2).(outside - inside) > 0" still holds on
        // non-degenerate quads (Every_quad_winds_outward_from_inside_to_outside above pins that
        // directly). These fixtures target the cases the old per-quad geometric fallback got
        // wrong: quads that collapse to zero area, where orientation must still come from
        // topology alone and every shared undirected edge must still be traversed in opposite
        // directions by its two (or four) quads.
        [Test]
        public void A_sample_exactly_at_the_isovalue_collapses_every_quad_but_edges_still_balance()
        {
            // The inside sample equals isoValue exactly, so every crossing edge from it
            // interpolates to amount 0: all six quads around it collapse onto the same point.
            var samples = SingleInsideSample(3, 3, 3, 1, 1, 1, 0f);

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().Be(6);
            AssertEdgeDirectionsBalance(mesh);
        }

        [Test]
        public void A_sphere_at_a_huge_origin_with_a_tiny_cell_size_collapses_quads_but_edges_still_balance()
        {
            // A large origin combined with a tiny cell size used to erase the float32 precision
            // the old geometric fallback needed: vertex positions could round to the same value
            // even for non-degenerate quads, making the per-quad cross product an unreliable
            // orientation source. Orientation no longer reads positions at all, so this must
            // balance regardless.
            var samples = Sphere(64);

            var mesh = CultGeometrySurfaceNets.Extract(samples, origin: new CultVec3(1e4f, 1e4f, 1e4f), cellSize: 0.001f);

            mesh.QuadCount.Should().BeGreaterThan(0);
            AssertEdgeDirectionsBalance(mesh);
        }

        [Test]
        public void An_octahedron_field_sampled_exactly_at_the_isovalue_collapses_quads_but_edges_still_balance()
        {
            // An L1-distance ("octahedron") field with the radius chosen to land exactly on
            // several lattice points: those quads collapse just like the single-sample case. A
            // one-sample uniform-outside margin (same as the checkerboard fixture elsewhere in
            // this file) keeps every crossing edge's four surrounding cells in range, so an
            // open-boundary artifact can't masquerade as an orientation imbalance.
            var samples = new float[5, 5, 5];
            for (var x = 0; x < 5; x++)
            for (var y = 0; y < 5; y++)
            for (var z = 0; z < 5; z++)
            {
                var interior = x is >= 1 and <= 3 && y is >= 1 and <= 3 && z is >= 1 and <= 3;
                samples[x, y, z] = interior ? Math.Abs(x - 2) + Math.Abs(y - 2) + Math.Abs(z - 2) - 1f : 5f;
            }

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().BeGreaterThan(0);
            AssertEdgeDirectionsBalance(mesh);
        }

        [Test]
        public void A_field_within_a_tiny_tolerance_of_the_isovalue_collapses_quads_but_edges_still_balance()
        {
            // A sphere field scaled down by 1e-9 so most non-boundary values sit within 1e-8 of
            // the isovalue without changing which side of it they are on (scaling preserves sign).
            var samples = Sphere(10);
            for (var x = 0; x < 10; x++)
            for (var y = 0; y < 10; y++)
            for (var z = 0; z < 10; z++) samples[x, y, z] *= 1e-9f;

            var mesh = CultGeometrySurfaceNets.Extract(samples);

            mesh.QuadCount.Should().BeGreaterThan(0);
            AssertEdgeDirectionsBalance(mesh);
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
        public void Grid_edge_equality_requires_every_component_to_match()
        {
            var edge = new CultGeometryGridEdge(1, 2, 3, 0);

            (edge == new CultGeometryGridEdge(1, 2, 3, 0)).Should().BeTrue();
            (edge == new CultGeometryGridEdge(9, 2, 3, 0)).Should().BeFalse();
            (edge == new CultGeometryGridEdge(1, 9, 3, 0)).Should().BeFalse();
            (edge == new CultGeometryGridEdge(1, 2, 9, 0)).Should().BeFalse();
            (edge == new CultGeometryGridEdge(1, 2, 3, 1)).Should().BeFalse();
        }

        [Test]
        public void Grid_edge_object_equals_distinguishes_every_component_and_rejects_null_and_other_types()
        {
            var edge = new CultGeometryGridEdge(1, 2, 3, 0);
            object same = new CultGeometryGridEdge(1, 2, 3, 0);
            object diffX = new CultGeometryGridEdge(9, 2, 3, 0);
            object diffY = new CultGeometryGridEdge(1, 9, 3, 0);
            object diffZ = new CultGeometryGridEdge(1, 2, 9, 0);
            object diffAxis = new CultGeometryGridEdge(1, 2, 3, 1);

            edge.Equals(same).Should().BeTrue();
            edge.Equals(diffX).Should().BeFalse();
            edge.Equals(diffY).Should().BeFalse();
            edge.Equals(diffZ).Should().BeFalse();
            edge.Equals(diffAxis).Should().BeFalse();
            edge.Equals(null).Should().BeFalse();
            edge.Equals("not an edge").Should().BeFalse();
            edge.Equals((object)42).Should().BeFalse();

            // object.Equals must agree with the typed Equals(CultGeometryGridEdge) it delegates to,
            // and with GetHashCode wherever the two report equal, so the type is safe as a
            // dictionary/set key (Extract uses it as one via vertexIndex's cell-coordinate tuples'
            // sibling role, and tests key EdgeUsage dictionaries by it directly).
            edge.Equals(same).Should().Be(edge.Equals((CultGeometryGridEdge)same));
            edge.GetHashCode().Should().Be(((CultGeometryGridEdge)same).GetHashCode());
        }

        [Test]
        public void Grid_edge_inequality_is_the_negation_of_equality()
        {
            // != has no caller of its own; it exists only because C# requires it whenever == is
            // defined (CS0216). This is its only direct test.
            var edge = new CultGeometryGridEdge(1, 2, 3, 0);

            (edge != new CultGeometryGridEdge(1, 2, 3, 0)).Should().BeFalse();
            (edge != new CultGeometryGridEdge(9, 2, 3, 0)).Should().BeTrue();
        }

        [Test]
        public void Grid_edge_to_string_names_its_coordinates_and_axis()
        {
            var edge = new CultGeometryGridEdge(1, 2, 3, 2);

            edge.ToString().Should().Be("(1, 2, 3)+e2");
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

        [TestCase(float.NegativeInfinity)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NaN)]
        public void A_non_finite_sample_is_rejected(float nonFiniteValue)
        {
            var samples = new float[2, 2, 2];
            samples[0, 0, 0] = nonFiniteValue;

            Action act = () => CultGeometrySurfaceNets.Extract(samples);

            act.Should().Throw<ArgumentException>();
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

        // Hand-derived (double precision, independent of Interpolate) expected position for a
        // single-inside-corner-at-origin fixture where every one of the three crossing edges has
        // the same firstValue/secondValue pair.
        private static float HandDerivedSingleCornerVertex(float insideValue, float outsideValue)
        {
            double first = insideValue;
            double second = outsideValue;
            var amount = (0d - first) / (second - first);
            return (float)(amount / 3d);
        }

        private static void AssertQuadCorners(CultGeometryQuadMesh mesh, CultGeometryGridEdge edge, CultVec3[] expected)
        {
            var quadIndex = Array.IndexOf(mesh.QuadEdges, edge);
            quadIndex.Should().BeGreaterThanOrEqualTo(0, $"a quad for edge {edge} should exist");

            var baseIndex = quadIndex * 4;
            for (var i = 0; i < 4; i++)
            {
                Position(mesh.Positions, mesh.Quads[baseIndex + i]).Should().Be(expected[i],
                    $"corner {i} of the quad for edge {edge} should be {expected[i]}");
            }
        }

        // Every shared undirected edge of a welded quad mesh must be traversed in opposite
        // directions by the quads that share it (net zero over a<b vs b<a traversals), whether it
        // is shared by two quads (the ordinary manifold case) or four (an ambiguous/ degenerate
        // crossing). Coincident-vertex (zero-length) quad edges from a fully collapsed quad are
        // not real shared edges and are excluded.
        private static void AssertEdgeDirectionsBalance(CultGeometryQuadMesh mesh)
        {
            var net = new Dictionary<(uint, uint), int>();
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
                    if (a == b) continue;

                    var key = a < b ? (a, b) : (b, a);
                    var sign = a < b ? 1 : -1;
                    net[key] = net.GetValueOrDefault(key) + sign;
                }
            }

            net.Values.Should().OnlyContain(value => value == 0,
                "every shared undirected edge should be traversed in opposite directions by its quads");
        }
    }
}
