using System;
using CultMath;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameCult.Geometry.Unity
{

public static class PlanetaryPatchMeshAdapter
{
    public static UnityEngine.Mesh CreateFaceMesh(PlanetaryCubeFace face, int cellsPerAxis, float radius = 1)
    {
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var source = PlanetaryPatch.CreateFace(face, cellsPerAxis);
        var vertices = new Vector3[source.Vertices.Length];
        var normals = new Vector3[source.Vertices.Length];
        var uv = new Vector2[source.Vertices.Length];
        for (var i = 0; i < source.Vertices.Length; i++)
        {
            var direction = source.Vertices[i].UnitDirection;
            var normal = new Vector3(direction.x, direction.y, direction.z);
            vertices[i] = normal * radius; normals[i] = normal;
            uv[i] = new Vector2(source.Vertices[i].LocalCoordinate.x, source.Vertices[i].LocalCoordinate.y);
        }
        var mesh = new UnityEngine.Mesh { name = $"GameCult.Geometry Planetary {face} {cellsPerAxis}x{cellsPerAxis}", indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16, vertices = vertices, normals = normals, uv = uv, triangles = source.Indices };
        mesh.RecalculateBounds(); return mesh;
    }
}
}
