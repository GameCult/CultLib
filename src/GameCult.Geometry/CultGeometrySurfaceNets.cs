using System;
using System.Collections.Generic;
using CultMath;

namespace GameCult.Geometry
{
    /// <summary>
    /// Extracts engine-neutral welded quad meshes from sampled scalar fields with surface nets.
    /// </summary>
    public static class CultGeometrySurfaceNets
    {
        // For axis A, the two perpendicular axes in (u, v) order. Axis 0 (X) pairs (Y, Z) and axis
        // 2 (Z) pairs (X, Y): both right-handed, u x v == +A_hat. Axis 1 (Y) pairs (X, Z), which is
        // left-handed (X x Z == -Y). OrientationSign below records that mirroring explicitly so
        // quad winding can be derived from it instead of a per-quad geometric test.
        private static readonly (int U, int V)[] PerpAxes = { (1, 2), (0, 2), (0, 1) };

        // +1 when (PerpAxes[axis].U, .V) is right-handed, -1 when it is mirrored (axis 1 only).
        private static readonly int[] OrientationSign = { 1, -1, 1 };

        // The perimeter loop around a crossing edge, in perpendicular (u, v) space: a proper square
        // loop (0,0) -> (1,0) -> (1,1) -> (0,1), not a diagonal cross. Added to a cell's own
        // coordinate it gives the axis's four parallel edges inside that cell (for vertex
        // placement); shifted by -1 it gives the four cells that share a crossing edge (for quad
        // assembly). One table, two uses, instead of three axis-specific copies of each.
        private static readonly (int U, int V)[] PerimeterLoop = { (0, 0), (1, 0), (1, 1), (0, 1) };

        /// <summary>
        /// Extracts the <paramref name="isoValue"/> surface with surface nets.
        /// Values less than or equal to the isovalue are treated as inside.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="samples"/> is too small or has a non-finite sample, or
        /// <paramref name="isoValue"/> or a component of <paramref name="origin"/> is non-finite.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is non-positive or infinite.</exception>
        /// <remarks>
        /// Non-finite results from otherwise-finite inputs (for example an extreme
        /// <paramref name="cellSize"/> or samples near float's magnitude limit producing an
        /// infinite vertex position) are a float-domain overflow, not a validated input error;
        /// this method does not range-check magnitudes for that case.
        /// </remarks>
        public static CultGeometryQuadMesh Extract(
            float[,,] samples,
            float isoValue = 0f,
            CultVec3 origin = default,
            float cellSize = 1f)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (samples.GetLength(0) < 2 || samples.GetLength(1) < 2 || samples.GetLength(2) < 2)
            {
                throw new ArgumentException("A surface nets field requires at least two samples on every axis.", nameof(samples));
            }

            foreach (var sample in samples)
            {
                if (!float.IsFinite(sample))
                {
                    throw new ArgumentException("A surface nets field requires every sample to be finite.", nameof(samples));
                }
            }

            if (!float.IsFinite(isoValue))
            {
                throw new ArgumentException("The isovalue must be finite.", nameof(isoValue));
            }

            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            {
                throw new ArgumentException("The origin must be finite.", nameof(origin));
            }

            if (!(cellSize > 0f) || float.IsInfinity(cellSize))
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be finite and positive.");
            }

            var sizeX = samples.GetLength(0);
            var sizeY = samples.GetLength(1);
            var sizeZ = samples.GetLength(2);
            var cellCounts = new[] { sizeX - 1, sizeY - 1, sizeZ - 1 };
            var originValue = new float3(origin.X, origin.Y, origin.Z);

            var vertexIndex = new Dictionary<(int, int, int), uint>();
            var positions = new List<float>();

            for (var cx = 0; cx < cellCounts[0]; cx++)
            for (var cy = 0; cy < cellCounts[1]; cy++)
            for (var cz = 0; cz < cellCounts[2]; cz++)
            {
                var cellCoord = new[] { cx, cy, cz };
                var vertex = CellVertex(samples, cellCoord, isoValue, originValue, cellSize);
                if (vertex == null) continue;

                vertexIndex[(cx, cy, cz)] = (uint)(positions.Count / 3);
                Append(vertex.Value, positions);
            }

            var quads = new List<uint>();
            var quadEdges = new List<CultGeometryGridEdge>();

            for (var x = 0; x < sizeX; x++)
            for (var y = 0; y < sizeY; y++)
            for (var z = 0; z < sizeZ; z++)
            {
                var coord = new[] { x, y, z };

                for (byte axis = 0; axis < 3; axis++)
                {
                    // Only an edge whose far endpoint is still inside the grid can cross into a
                    // quad. Bounding the loop with this one condition, instead of a helper that
                    // returned a bool for the caller to branch on, leaves one path through the
                    // rest of the body instead of two that happened to produce the same result.
                    if (coord[axis] < cellCounts[axis])
                    {
                        var other = (int[])coord.Clone();
                        other[axis] += 1;

                        var inside = IsInside(samples[x, y, z], isoValue);
                        var otherInside = IsInside(samples[other[0], other[1], other[2]], isoValue);
                        if (inside != otherInside && TryGetQuadCells(cellCounts, coord, axis, out var cells))
                        {
                            // The four cells around a crossing edge each contain that same crossing
                            // edge among their own twelve, so each of them is mixed and has a
                            // vertex: this indexer cannot miss without the extraction invariant
                            // already being broken.
                            var v = new uint[4];
                            for (var i = 0; i < 4; i++) v[i] = vertexIndex[cells[i]];

                            // Orientation comes only from which endpoint is inside and the axis's
                            // fixed handedness (OrientationSign) - never from the quad's own
                            // geometry, so degenerate (zero-area) quads still orient consistently
                            // with their neighbours.
                            if (inside == (OrientationSign[axis] < 0))
                            {
                                (v[1], v[3]) = (v[3], v[1]);
                            }

                            quads.Add(v[0]);
                            quads.Add(v[1]);
                            quads.Add(v[2]);
                            quads.Add(v[3]);
                            quadEdges.Add(new CultGeometryGridEdge(x, y, z, axis));
                        }
                    }
                }
            }

            return new CultGeometryQuadMesh
            {
                Positions = positions.ToArray(),
                Quads = quads.ToArray(),
                QuadEdges = quadEdges.ToArray(),
            };
        }

        private static bool IsInside(float sample, float isoValue) => sample <= isoValue;

        private static float3? CellVertex(
            float[,,] samples,
            int[] cellCoord,
            float isoValue,
            float3 origin,
            float cellSize)
        {
            var sum = float3.zero;
            var count = 0;

            for (byte axis = 0; axis < 3; axis++)
            {
                AccumulateAxisEdges(samples, cellCoord, axis, isoValue, origin, cellSize, ref sum, ref count);
            }

            return count == 0 ? null : sum / count;
        }

        private static void AccumulateAxisEdges(
            float[,,] samples,
            int[] cellCoord,
            byte axis,
            float isoValue,
            float3 origin,
            float cellSize,
            ref float3 sum,
            ref int count)
        {
            var (u, v) = PerpAxes[axis];

            for (var i = 0; i < 4; i++)
            {
                var start = (int[])cellCoord.Clone();
                start[u] += PerimeterLoop[i].U;
                start[v] += PerimeterLoop[i].V;

                var end = (int[])start.Clone();
                end[axis] += 1;

                var startValue = samples[start[0], start[1], start[2]];
                var endValue = samples[end[0], end[1], end[2]];
                if (IsInside(startValue, isoValue) == IsInside(endValue, isoValue)) continue;

                var startPosition = origin + new float3(start[0], start[1], start[2]) * cellSize;
                var endPosition = origin + new float3(end[0], end[1], end[2]) * cellSize;
                sum += Interpolate(startPosition, startValue, endPosition, endValue, isoValue);
                count++;
            }
        }

        private static float3 Interpolate(float3 first, float firstValue, float3 second, float secondValue, float isoValue)
        {
            // A straddling edge has exactly one endpoint <= isoValue and the other > isoValue, so
            // delta is always non-zero (.NET preserves subnormals; there is no value of delta for
            // which a fallback produces a better answer than the exact interpolation).
            var amount = (isoValue - firstValue) / (secondValue - firstValue);
            return math.lerp(first, second, amount);
        }

        // The four cells that share the grid edge from (x, y, z) to (x, y, z) + e_axis, listed as a
        // perimeter loop around the edge (not crossed diagonally) so the caller can build a quad
        // directly from them. The edge's own coordinate is unchanged for all four cells: it is
        // guaranteed to already index a valid cell there because the caller's own loop bound
        // already verified coord[axis] < cellCounts[axis].
        private static bool TryGetQuadCells(int[] cellCounts, int[] coord, byte axis, out (int, int, int)[] cells)
        {
            cells = Array.Empty<(int, int, int)>();
            var (u, v) = PerpAxes[axis];

            if (!InRange(coord[u] - 1, cellCounts[u]) || !InRange(coord[u], cellCounts[u]) ||
                !InRange(coord[v] - 1, cellCounts[v]) || !InRange(coord[v], cellCounts[v]))
            {
                return false;
            }

            var result = new (int, int, int)[4];
            for (var i = 0; i < 4; i++)
            {
                var cell = (int[])coord.Clone();
                cell[u] += PerimeterLoop[i].U - 1;
                cell[v] += PerimeterLoop[i].V - 1;
                result[i] = (cell[0], cell[1], cell[2]);
            }

            cells = result;
            return true;
        }

        private static bool InRange(int value, int count) => value >= 0 && value < count;

        private static void Append(float3 value, List<float> destination)
        {
            destination.Add(value.x);
            destination.Add(value.y);
            destination.Add(value.z);
        }
    }
}
