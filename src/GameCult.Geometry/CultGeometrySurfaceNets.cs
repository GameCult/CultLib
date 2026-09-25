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
        // Start-corner offsets for each axis's four parallel edges of a unit cell. The edge itself
        // runs from the listed corner to that corner plus one step along Axis.
        private static readonly (int Dx, int Dy, int Dz)[] AxisXEdgeStarts =
        {
            (0, 0, 0), (0, 1, 0), (0, 0, 1), (0, 1, 1),
        };

        private static readonly (int Dx, int Dy, int Dz)[] AxisYEdgeStarts =
        {
            (0, 0, 0), (1, 0, 0), (0, 0, 1), (1, 0, 1),
        };

        private static readonly (int Dx, int Dy, int Dz)[] AxisZEdgeStarts =
        {
            (0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0),
        };

        /// <summary>
        /// Extracts the <paramref name="isoValue"/> surface with surface nets.
        /// Values less than or equal to the isovalue are treated as inside.
        /// </summary>
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

            if (!(cellSize > 0f) || float.IsInfinity(cellSize))
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be finite and positive.");
            }

            var sizeX = samples.GetLength(0);
            var sizeY = samples.GetLength(1);
            var sizeZ = samples.GetLength(2);
            var cellsX = sizeX - 1;
            var cellsY = sizeY - 1;
            var cellsZ = sizeZ - 1;
            var originValue = new float3(origin.X, origin.Y, origin.Z);

            var vertexIndex = new Dictionary<(int, int, int), uint>();
            var positions = new List<float>();

            for (var cx = 0; cx < cellsX; cx++)
            for (var cy = 0; cy < cellsY; cy++)
            for (var cz = 0; cz < cellsZ; cz++)
            {
                var vertex = CellVertex(samples, cx, cy, cz, isoValue, originValue, cellSize);
                if (vertex is not { } position) continue;

                vertexIndex[(cx, cy, cz)] = (uint)(positions.Count / 3);
                Append(position, positions);
            }

            var quads = new List<uint>();
            var quadEdges = new List<CultGeometryGridEdge>();

            for (var x = 0; x < sizeX; x++)
            for (var y = 0; y < sizeY; y++)
            for (var z = 0; z < sizeZ; z++)
            {
                for (byte axis = 0; axis < 3; axis++)
                {
                    if (!TryGetOtherEndpoint(sizeX, sizeY, sizeZ, x, y, z, axis, out var ox, out var oy, out var oz))
                    {
                        continue;
                    }

                    var inside = samples[x, y, z] <= isoValue;
                    var otherInside = samples[ox, oy, oz] <= isoValue;
                    if (inside == otherInside) continue;

                    if (!TryGetQuadCells(cellsX, cellsY, cellsZ, x, y, z, axis, out var c0, out var c1, out var c2, out var c3))
                    {
                        continue;
                    }

                    if (!vertexIndex.TryGetValue(c0, out var v0) ||
                        !vertexIndex.TryGetValue(c1, out var v1) ||
                        !vertexIndex.TryGetValue(c2, out var v2) ||
                        !vertexIndex.TryGetValue(c3, out var v3))
                    {
                        // The four cells around a crossing edge each contain that same crossing edge
                        // among their own twelve, so each of them is mixed and has a vertex. This is an
                        // invariant guard, not a reachable branch.
                        throw new InvalidOperationException("A surface-nets crossing edge bordered a cell with no vertex.");
                    }

                    var p0 = Position(positions, v0);
                    var p1 = Position(positions, v1);
                    var p2 = Position(positions, v2);
                    var p3 = Position(positions, v3);
                    var diagonalCross = math.cross(p2 - p0, p3 - p1);

                    var insideCoord = inside ? new float3(x, y, z) : new float3(ox, oy, oz);
                    var outsideCoord = inside ? new float3(ox, oy, oz) : new float3(x, y, z);
                    var outward = outsideCoord - insideCoord;

                    if (math.dot(diagonalCross, outward) < 0f)
                    {
                        (v1, v3) = (v3, v1);
                    }

                    quads.Add(v0);
                    quads.Add(v1);
                    quads.Add(v2);
                    quads.Add(v3);
                    quadEdges.Add(new CultGeometryGridEdge(x, y, z, axis));
                }
            }

            return new CultGeometryQuadMesh
            {
                Positions = positions.ToArray(),
                Quads = quads.ToArray(),
                QuadEdges = quadEdges.ToArray(),
            };
        }

        private static float3? CellVertex(
            float[,,] samples,
            int cx,
            int cy,
            int cz,
            float isoValue,
            float3 origin,
            float cellSize)
        {
            var sum = float3.zero;
            var count = 0;

            AccumulateAxisEdges(samples, cx, cy, cz, 0, AxisXEdgeStarts, isoValue, origin, cellSize, ref sum, ref count);
            AccumulateAxisEdges(samples, cx, cy, cz, 1, AxisYEdgeStarts, isoValue, origin, cellSize, ref sum, ref count);
            AccumulateAxisEdges(samples, cx, cy, cz, 2, AxisZEdgeStarts, isoValue, origin, cellSize, ref sum, ref count);

            return count == 0 ? null : sum / count;
        }

        private static void AccumulateAxisEdges(
            float[,,] samples,
            int cx,
            int cy,
            int cz,
            int axis,
            (int Dx, int Dy, int Dz)[] starts,
            float isoValue,
            float3 origin,
            float cellSize,
            ref float3 sum,
            ref int count)
        {
            foreach (var start in starts)
            {
                var startX = cx + start.Dx;
                var startY = cy + start.Dy;
                var startZ = cz + start.Dz;
                var endX = startX + (axis == 0 ? 1 : 0);
                var endY = startY + (axis == 1 ? 1 : 0);
                var endZ = startZ + (axis == 2 ? 1 : 0);

                var startValue = samples[startX, startY, startZ];
                var endValue = samples[endX, endY, endZ];
                if ((startValue <= isoValue) == (endValue <= isoValue)) continue;

                var startPosition = origin + new float3(startX, startY, startZ) * cellSize;
                var endPosition = origin + new float3(endX, endY, endZ) * cellSize;
                sum += Interpolate(startPosition, startValue, endPosition, endValue, isoValue);
                count++;
            }
        }

        private static float3 Interpolate(float3 first, float firstValue, float3 second, float secondValue, float isoValue)
        {
            var delta = secondValue - firstValue;
            var amount = math.abs(delta) <= 1e-20f ? 0.5f : (isoValue - firstValue) / delta;
            return math.lerp(first, second, amount);
        }

        private static bool TryGetOtherEndpoint(
            int sizeX,
            int sizeY,
            int sizeZ,
            int x,
            int y,
            int z,
            byte axis,
            out int ox,
            out int oy,
            out int oz)
        {
            ox = x;
            oy = y;
            oz = z;

            switch (axis)
            {
                case 0:
                    if (x + 1 >= sizeX) return false;
                    ox = x + 1;
                    return true;
                case 1:
                    if (y + 1 >= sizeY) return false;
                    oy = y + 1;
                    return true;
                default:
                    if (z + 1 >= sizeZ) return false;
                    oz = z + 1;
                    return true;
            }
        }

        // The four cells that share the grid edge from (x, y, z) to (x, y, z) + e_axis, listed as a
        // perimeter loop around the edge (not crossed diagonally) so the caller can build a quad
        // directly from them.
        private static bool TryGetQuadCells(
            int cellsX,
            int cellsY,
            int cellsZ,
            int x,
            int y,
            int z,
            byte axis,
            out (int, int, int) c0,
            out (int, int, int) c1,
            out (int, int, int) c2,
            out (int, int, int) c3)
        {
            c0 = c1 = c2 = c3 = default;

            switch (axis)
            {
                case 0:
                    if (!InRange(y - 1, cellsY) || !InRange(y, cellsY) || !InRange(z - 1, cellsZ) || !InRange(z, cellsZ))
                    {
                        return false;
                    }

                    c0 = (x, y - 1, z - 1);
                    c1 = (x, y, z - 1);
                    c2 = (x, y, z);
                    c3 = (x, y - 1, z);
                    return InRange(x, cellsX);
                case 1:
                    if (!InRange(x - 1, cellsX) || !InRange(x, cellsX) || !InRange(z - 1, cellsZ) || !InRange(z, cellsZ))
                    {
                        return false;
                    }

                    c0 = (x - 1, y, z - 1);
                    c1 = (x, y, z - 1);
                    c2 = (x, y, z);
                    c3 = (x - 1, y, z);
                    return InRange(y, cellsY);
                default:
                    if (!InRange(x - 1, cellsX) || !InRange(x, cellsX) || !InRange(y - 1, cellsY) || !InRange(y, cellsY))
                    {
                        return false;
                    }

                    c0 = (x - 1, y - 1, z);
                    c1 = (x, y - 1, z);
                    c2 = (x, y, z);
                    c3 = (x - 1, y, z);
                    return InRange(z, cellsZ);
            }
        }

        private static bool InRange(int value, int count) => value >= 0 && value < count;

        private static float3 Position(List<float> positions, uint index)
        {
            var offset = (int)index * 3;
            return new float3(positions[offset], positions[offset + 1], positions[offset + 2]);
        }

        private static void Append(float3 value, List<float> destination)
        {
            destination.Add(value.x);
            destination.Add(value.y);
            destination.Add(value.z);
        }
    }
}
