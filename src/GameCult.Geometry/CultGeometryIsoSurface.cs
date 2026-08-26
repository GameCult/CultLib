using System;
using System.Collections.Generic;
using CultMath;

namespace GameCult.Geometry
{
    /// <summary>
    /// Extracts engine-neutral triangle meshes from sampled scalar fields.
    /// </summary>
    public static class CultGeometryIsoSurface
    {
        private static readonly float3[] CubeCorners =
        {
            new(0f, 0f, 0f),
            new(1f, 0f, 0f),
            new(1f, 1f, 0f),
            new(0f, 1f, 0f),
            new(0f, 0f, 1f),
            new(1f, 0f, 1f),
            new(1f, 1f, 1f),
            new(0f, 1f, 1f),
        };

        // A fixed six-tetrahedra decomposition keeps neighboring cube faces consistent.
        private static readonly int[,] CubeTetrahedra =
        {
            { 0, 5, 1, 6 },
            { 0, 1, 2, 6 },
            { 0, 2, 3, 6 },
            { 0, 3, 7, 6 },
            { 0, 7, 4, 6 },
            { 0, 4, 5, 6 },
        };

        /// <summary>
        /// Extracts the <paramref name="isoValue"/> surface with marching tetrahedra.
        /// Values less than or equal to the isovalue are treated as inside.
        /// </summary>
        public static CultGeometryTriangleMesh Extract(
            float[,,] samples,
            float isoValue = 0f,
            CultVec3 origin = default,
            float cellSize = 1f)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (samples.GetLength(0) < 2 || samples.GetLength(1) < 2 || samples.GetLength(2) < 2)
            {
                throw new ArgumentException("An isosurface field requires at least two samples on every axis.", nameof(samples));
            }

            if (!(cellSize > 0f) || float.IsInfinity(cellSize))
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be finite and positive.");
            }

            var positions = new List<float>();
            var normals = new List<float>();
            var indices = new List<uint>();
            var originValue = new float3(origin.X, origin.Y, origin.Z);
            var cornerPositions = new float3[8];
            var cornerValues = new float[8];
            var tetraPositions = new float3[4];
            var tetraValues = new float[4];

            for (var x = 0; x < samples.GetLength(0) - 1; x++)
            for (var y = 0; y < samples.GetLength(1) - 1; y++)
            for (var z = 0; z < samples.GetLength(2) - 1; z++)
            {
                var cubeOrigin = originValue + new float3(x, y, z) * cellSize;
                for (var corner = 0; corner < CubeCorners.Length; corner++)
                {
                    var offset = CubeCorners[corner];
                    cornerPositions[corner] = cubeOrigin + offset * cellSize;
                    cornerValues[corner] = samples[
                        x + (int)offset.x,
                        y + (int)offset.y,
                        z + (int)offset.z];
                }

                for (var tetrahedron = 0; tetrahedron < CubeTetrahedra.GetLength(0); tetrahedron++)
                {
                    for (var vertex = 0; vertex < 4; vertex++)
                    {
                        var corner = CubeTetrahedra[tetrahedron, vertex];
                        tetraPositions[vertex] = cornerPositions[corner];
                        tetraValues[vertex] = cornerValues[corner];
                    }

                    PolygonizeTetrahedron(tetraPositions, tetraValues, isoValue, positions, normals, indices);
                }
            }

            return new CultGeometryTriangleMesh
            {
                Positions = positions.ToArray(),
                Normals = normals.ToArray(),
                Indices = indices.ToArray(),
                Uvs = Array.Empty<float>(),
                TriangleMaterials = new uint[indices.Count / 3],
            };
        }

        private static void PolygonizeTetrahedron(
            float3[] positions,
            float[] values,
            float isoValue,
            List<float> outputPositions,
            List<float> outputNormals,
            List<uint> outputIndices)
        {
            Span<int> inside = stackalloc int[4];
            Span<int> outside = stackalloc int[4];
            var insideCount = 0;
            var outsideCount = 0;

            for (var vertex = 0; vertex < 4; vertex++)
            {
                if (values[vertex] <= isoValue) inside[insideCount++] = vertex;
                else outside[outsideCount++] = vertex;
            }

            if (insideCount == 0 || insideCount == 4) return;

            var outward = Centroid(positions, outside, outsideCount) - Centroid(positions, inside, insideCount);
            if (insideCount == 1 || outsideCount == 1)
            {
                var loneIsInside = insideCount == 1;
                var lone = loneIsInside ? inside[0] : outside[0];
                var others = loneIsInside ? outside : inside;
                var a = Interpolate(positions[lone], values[lone], positions[others[0]], values[others[0]], isoValue);
                var b = Interpolate(positions[lone], values[lone], positions[others[1]], values[others[1]], isoValue);
                var c = Interpolate(positions[lone], values[lone], positions[others[2]], values[others[2]], isoValue);
                EmitOrientedTriangle(a, b, c, outward, outputPositions, outputNormals, outputIndices);
                return;
            }

            var p00 = Interpolate(positions[inside[0]], values[inside[0]], positions[outside[0]], values[outside[0]], isoValue);
            var p01 = Interpolate(positions[inside[0]], values[inside[0]], positions[outside[1]], values[outside[1]], isoValue);
            var p10 = Interpolate(positions[inside[1]], values[inside[1]], positions[outside[0]], values[outside[0]], isoValue);
            var p11 = Interpolate(positions[inside[1]], values[inside[1]], positions[outside[1]], values[outside[1]], isoValue);
            EmitOrientedTriangle(p00, p01, p11, outward, outputPositions, outputNormals, outputIndices);
            EmitOrientedTriangle(p00, p11, p10, outward, outputPositions, outputNormals, outputIndices);
        }

        private static float3 Interpolate(float3 first, float firstValue, float3 second, float secondValue, float isoValue)
        {
            var delta = secondValue - firstValue;
            var amount = math.abs(delta) <= 1e-20f ? 0.5f : (isoValue - firstValue) / delta;
            return math.lerp(first, second, amount);
        }

        private static float3 Centroid(float3[] positions, Span<int> vertices, int count)
        {
            var sum = float3.zero;
            for (var index = 0; index < count; index++) sum += positions[vertices[index]];
            return sum / count;
        }

        private static void EmitOrientedTriangle(
            float3 a,
            float3 b,
            float3 c,
            float3 outward,
            List<float> positions,
            List<float> normals,
            List<uint> indices)
        {
            var normal = math.cross(b - a, c - a);
            if (math.dot(normal, outward) < 0f)
            {
                (b, c) = (c, b);
                normal = -normal;
            }

            normal = math.normalize(normal);
            var firstIndex = (uint)(positions.Count / 3);
            Append(a, positions);
            Append(b, positions);
            Append(c, positions);
            for (var vertex = 0; vertex < 3; vertex++) Append(normal, normals);
            indices.Add(firstIndex);
            indices.Add(firstIndex + 1);
            indices.Add(firstIndex + 2);
        }

        private static void Append(float3 value, List<float> destination)
        {
            destination.Add(value.x);
            destination.Add(value.y);
            destination.Add(value.z);
        }
    }
}
