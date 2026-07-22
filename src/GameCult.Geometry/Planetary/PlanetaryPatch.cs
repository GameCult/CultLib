using System;
using System.Linq;
using CultMath;

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

namespace GameCult.Geometry;

public readonly record struct PlanetaryPatchVertex(float2 LocalCoordinate, float3 UnitDirection);

public readonly record struct PlanetaryPatchMesh(
    PlanetaryCubeFace Face,
    int CellsPerAxis,
    PlanetaryPatchVertex[] Vertices,
    int[] Indices);

public static class PlanetaryPatch
{
    public static PlanetaryPatchMesh CreateFace(PlanetaryCubeFace face, int cellsPerAxis)
    {
        if (!Enum.IsDefined(typeof(PlanetaryCubeFace), face)) throw new ArgumentOutOfRangeException(nameof(face));
        if (cellsPerAxis < 1 || cellsPerAxis > 4096) throw new ArgumentOutOfRangeException(nameof(cellsPerAxis));
        var axisVertices = cellsPerAxis + 1;
        var vertices = new PlanetaryPatchVertex[checked(axisVertices * axisVertices)];
        for (var y = 0; y < axisVertices; y++)
        for (var x = 0; x < axisVertices; x++)
        {
            var local = new float2(x / (float)cellsPerAxis, y / (float)cellsPerAxis);
            vertices[y * axisVertices + x] = new(local, PlanetaryTopology.Direction(new(face, local.x * 2 - 1, local.y * 2 - 1)));
        }
        var indices = new int[checked(cellsPerAxis * cellsPerAxis * 6)];
        var write = 0;
        for (var y = 0; y < cellsPerAxis; y++)
        for (var x = 0; x < cellsPerAxis; x++)
        {
            var lowerLeft = y * axisVertices + x;
            var lowerRight = lowerLeft + 1;
            var upperLeft = lowerLeft + axisVertices;
            var upperRight = upperLeft + 1;
            indices[write++] = lowerLeft; indices[write++] = lowerRight; indices[write++] = upperRight;
            indices[write++] = lowerLeft; indices[write++] = upperRight; indices[write++] = upperLeft;
        }
        return new(face, cellsPerAxis, vertices, indices);
    }

    public static PlanetaryPatchMesh[] CreateCubeSphere(int cellsPerAxis)
        => ((PlanetaryCubeFace[])Enum.GetValues(typeof(PlanetaryCubeFace))).Select(face => CreateFace(face, cellsPerAxis)).ToArray();
}
