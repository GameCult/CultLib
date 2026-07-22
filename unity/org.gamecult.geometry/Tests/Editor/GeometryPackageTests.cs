using CultMath;
using GameCult.Geometry.Unity;
using NUnit.Framework;
using UnityEngine;

namespace GameCult.Geometry.Tests.Editor
{
    public sealed class GeometryPackageTests
    {
        [Test]
        public void CultMathPrimitivesAreVisible()
        {
            var value = new float3(2f, 3f, 4f);

            Assert.That(value.x, Is.EqualTo(2f));
            Assert.That(value.y, Is.EqualTo(3f));
            Assert.That(value.z, Is.EqualTo(4f));
        }

        [Test]
        public void CoreGeometryTypesUseCultMathPrimitives()
        {
            var sphere = new CultSphere(new float3(1f, 2f, 3f), 4f);

            Assert.That(sphere.Center, Is.EqualTo(new float3(1f, 2f, 3f)));
            Assert.That(sphere.Radius, Is.EqualTo(4f));
            Assert.That(sphere.Contains(new float3(1f, 2f, 7f)), Is.True);
        }

        [Test]
        public void PlanetaryAdapterCreatesUnityMesh()
        {
            var mesh = PlanetaryPatchMeshAdapter.CreateFaceMesh(
                PlanetaryCubeFace.PositiveZ,
                2,
                3f);

            try
            {
                Assert.That(mesh, Is.Not.Null);
                Assert.That(mesh.name, Does.Contain("PositiveZ"));
                Assert.That(mesh.vertexCount, Is.EqualTo(9));
                Assert.That(mesh.normals.Length, Is.EqualTo(9));
                Assert.That(mesh.uv.Length, Is.EqualTo(9));
                Assert.That(mesh.triangles.Length, Is.EqualTo(24));
                Assert.That(mesh.bounds.size.sqrMagnitude, Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
