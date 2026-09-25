using System;
using CultMath;

namespace GameCult.Geometry
{
    /// <summary>
    /// Derives per-vertex normals for a welded quad mesh by face-weighted averaging.
    /// </summary>
    public static class CultGeometryQuadNormals
    {
        /// <summary>
        /// Computes a per-vertex normal for <paramref name="mesh"/> as the sum, over every quad
        /// touching that vertex, of the quad's diagonal cross product (which points along the
        /// quad's normal and whose length is twice a non-planar quad's area), then normalized.
        /// A vertex touched only by zero-area quads gets a zero normal.
        /// </summary>
        public static float[] FaceWeighted(CultGeometryQuadMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var sums = new float3[mesh.VertexCount];
            for (var quad = 0; quad < mesh.QuadCount; quad++)
            {
                var baseIndex = quad * 4;
                var i0 = mesh.Quads[baseIndex];
                var i1 = mesh.Quads[baseIndex + 1];
                var i2 = mesh.Quads[baseIndex + 2];
                var i3 = mesh.Quads[baseIndex + 3];

                var p0 = Position(mesh.Positions, i0);
                var p1 = Position(mesh.Positions, i1);
                var p2 = Position(mesh.Positions, i2);
                var p3 = Position(mesh.Positions, i3);

                var faceVector = math.cross(p2 - p0, p3 - p1);

                sums[i0] += faceVector;
                sums[i1] += faceVector;
                sums[i2] += faceVector;
                sums[i3] += faceVector;
            }

            var normals = new float[mesh.VertexCount * 3];
            for (var vertex = 0; vertex < sums.Length; vertex++)
            {
                var sum = sums[vertex];

                // A vertex whose contributions cancel or were all zero-area gets a zero normal;
                // normalize follows HLSL and would return NaN for it (matches Extract's degenerate rule).
                var normal = math.dot(sum, sum) > 0f ? math.normalize(sum) : float3.zero;
                normals[vertex * 3] = normal.x;
                normals[vertex * 3 + 1] = normal.y;
                normals[vertex * 3 + 2] = normal.z;
            }

            return normals;
        }

        private static float3 Position(float[] positions, uint index)
        {
            var offset = (int)index * 3;
            return new float3(positions[offset], positions[offset + 1], positions[offset + 2]);
        }
    }
}
