using System;

namespace GameCult.Geometry
{
    /// <summary>
    /// The grid edge from sample (X, Y, Z) to (X, Y, Z) + e_Axis, where Axis 0/1/2 is X/Y/Z.
    /// </summary>
    public readonly struct CultGeometryGridEdge : IEquatable<CultGeometryGridEdge>, IComparable<CultGeometryGridEdge>
    {
        /// <summary>Creates a grid edge.</summary>
        public CultGeometryGridEdge(int x, int y, int z, byte axis)
        {
            X = x;
            Y = y;
            Z = z;
            Axis = axis;
        }

        /// <summary>Gets the starting sample's x coordinate.</summary>
        public int X { get; }

        /// <summary>Gets the starting sample's y coordinate.</summary>
        public int Y { get; }

        /// <summary>Gets the starting sample's z coordinate.</summary>
        public int Z { get; }

        /// <summary>Gets the axis the edge runs along: 0 for x, 1 for y, 2 for z.</summary>
        public byte Axis { get; }

        /// <summary>Returns whether this edge is equal to another edge.</summary>
        public bool Equals(CultGeometryGridEdge other) =>
            X == other.X && Y == other.Y && Z == other.Z && Axis == other.Axis;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CultGeometryGridEdge other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, Axis);

        /// <summary>Compares edges by (X, Y, Z, Axis) in that order.</summary>
        public int CompareTo(CultGeometryGridEdge other)
        {
            var byX = X.CompareTo(other.X);
            if (byX != 0) return byX;

            var byY = Y.CompareTo(other.Y);
            if (byY != 0) return byY;

            var byZ = Z.CompareTo(other.Z);
            if (byZ != 0) return byZ;

            return Axis.CompareTo(other.Axis);
        }

        /// <inheritdoc />
        public override string ToString() => $"({X}, {Y}, {Z})+e{Axis}";

        // The relational operators (<, >, <=, >=) had no caller and no test anywhere in the repo:
        // ordering goes through CompareTo (see
        // Extraction_is_deterministic_and_quad_edges_are_strictly_ascending), so they were deleted
        // rather than carried as untested surface. != is kept only because C# requires it whenever
        // == is defined (CS0216); it is exercised directly by a test.
        public static bool operator ==(CultGeometryGridEdge left, CultGeometryGridEdge right) => left.Equals(right);
        public static bool operator !=(CultGeometryGridEdge left, CultGeometryGridEdge right) => !left.Equals(right);
    }

    /// <summary>
    /// An engine-neutral quad mesh with welded vertices, produced by surface nets extraction.
    /// Not a CultCache document: this is an in-process algorithm result, not a persisted artifact.
    /// </summary>
    public sealed class CultGeometryQuadMesh
    {
        /// <summary>Gets or sets the welded vertex positions, xyz per vertex.</summary>
        public float[] Positions { get; set; } = Array.Empty<float>();

        /// <summary>Gets or sets the quad vertex indices, 4 per quad.</summary>
        public uint[] Quads { get; set; } = Array.Empty<uint>();

        /// <summary>Gets or sets the grid edge each quad was emitted for, one per quad, strictly ascending.</summary>
        public CultGeometryGridEdge[] QuadEdges { get; set; } = Array.Empty<CultGeometryGridEdge>();

        /// <summary>Gets the number of quads.</summary>
        public int QuadCount => Quads.Length / 4;

        /// <summary>Gets the number of welded vertices.</summary>
        public int VertexCount => Positions.Length / 3;
    }
}
