using System;
using System.Collections.Generic;
using CultMath;
using GameCult.Geometry;
using UnityEngine;

namespace GameCult.Geometry.Unity.Samples
{

[ExecuteAlways]
public sealed class GameCultGeometryPlanetaryViewer : MonoBehaviour
{
    [Min(0.001f)] public float Radius = 8.4f;
    [Range(1, 256)] public int CellsPerFace = 64;
    public int Seed = 42;
    [Min(0.0001f)] public float SampleFootprint = 0.01f;
    public Shader? PlanetShader;
    public Vector3 LightDirection = new(-0.4f, 0.7f, -0.6f);
    public Color LowColor = new(0.055f, 0.16f, 0.09f, 1);
    public Color HighColor = new(0.62f, 0.48f, 0.28f, 1);
    public Color RidgeColor = new(0.88f, 0.83f, 0.7f, 1);

    private readonly List<Mesh> meshes = new();
    private Material? material;
    private int builtCells;

    public PlanetaryFieldDefinition Definition => PlanetaryFieldDefinition.Create(
        1,
        Radius,
        Seed,
        ViewerErosion(Radius));

    public PlanetarySurfaceSample Sample(float3 direction)
    {
        var source = new ViewerBaseField(Radius);
        var normalized = math.normalize(direction);
        return PlanetaryField.Sample(Definition, normalized, source.Sample(normalized), PlanetaryQueryScale.AtFootprint(SampleFootprint));
    }

    private void OnEnable() => Rebuild();

    private void OnValidate()
    {
        Radius = Mathf.Max(Radius, 0.001f);
        CellsPerFace = Mathf.Clamp(CellsPerFace, 1, 256);
        SampleFootprint = Mathf.Max(SampleFootprint, 0.0001f);
        if (isActiveAndEnabled) Rebuild();
    }

    private void Update()
    {
        if (builtCells != CellsPerFace || material == null) Rebuild();
        ApplyMaterial();
    }

    private void Rebuild()
    {
        ClearGenerated();
        PlanetShader ??= Shader.Find("GameCult.Geometry/Planetary Field Viewer");
        if (PlanetShader == null) return;
        material = new Material(PlanetShader) { name = "GameCult.Geometry Planetary Viewer Material", hideFlags = HideFlags.HideAndDontSave };
        foreach (var face in (PlanetaryCubeFace[])Enum.GetValues(typeof(PlanetaryCubeFace)))
        {
            var mesh = PlanetaryPatchMeshAdapter.CreateFaceMesh(face, CellsPerFace);
            mesh.hideFlags = HideFlags.HideAndDontSave;
            meshes.Add(mesh);
            var child = new GameObject(face.ToString()) { hideFlags = HideFlags.DontSave };
            child.transform.SetParent(transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            child.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
        builtCells = CellsPerFace;
        ApplyMaterial();
    }

    private void ApplyMaterial()
    {
        if (material == null) return;
        var p = ViewerErosion(Radius);
        material.SetFloat("_Radius", Radius);
        material.SetFloat("_SampleFootprint", SampleFootprint);
        material.SetInt("_Seed", Seed);
        material.SetFloat("_ErosionScale", p.Scale);
        material.SetFloat("_ErosionStrength", p.Strength);
        material.SetFloat("_GullyWeight", p.GullyWeight);
        material.SetFloat("_Detail", p.Detail);
        material.SetVector("_Rounding", new(p.Rounding.x, p.Rounding.y, p.Rounding.z, p.Rounding.w));
        material.SetVector("_Onset", new(p.Onset.x, p.Onset.y, p.Onset.z, p.Onset.w));
        material.SetVector("_AssumedSlope", new(p.AssumedSlope.x, p.AssumedSlope.y, 0, 0));
        material.SetFloat("_CellScale", p.CellScale);
        material.SetFloat("_Normalization", p.Normalization);
        material.SetInt("_Octaves", p.Octaves);
        material.SetFloat("_Lacunarity", p.Lacunarity);
        material.SetFloat("_Gain", p.Gain);
        material.SetVector("_LightDirection", LightDirection.normalized);
        material.SetColor("_LowColor", LowColor);
        material.SetColor("_HighColor", HighColor);
        material.SetColor("_RidgeColor", RidgeColor);
    }

    private void OnDisable() => ClearGenerated();
    private void OnDestroy() => ClearGenerated();

    private void ClearGenerated()
    {
        for (var i = transform.childCount - 1; i >= 0; i--) DestroyGenerated(transform.GetChild(i).gameObject);
        foreach (var mesh in meshes) DestroyGenerated(mesh);
        meshes.Clear();
        if (material != null) DestroyGenerated(material);
        material = null;
        builtCells = 0;
    }

    private static void DestroyGenerated(UnityEngine.Object value)
    {
        if (Application.isPlaying) Destroy(value);
        else DestroyImmediate(value);
    }

    private static AdvancedErosionParameters ViewerErosion(float radius) => new(
        radius * 0.075f, 0.12f, 0.58f, 1.45f,
        new float4(0.1f, 0.015f, 0.1f, 2),
        new float4(1.25f, 1.25f, 2.8f, 1.5f),
        new float2(0.7f, 0.85f), 0.7f, 0.5f, 7, 2, 0.5f);

    private readonly struct ViewerBaseField : IPlanetaryBaseField
    {
        private readonly float radius;
        public ViewerBaseField(float radius) => this.radius = radius;

        public PlanetaryBaseFieldSample Sample(float3 direction)
        {
            var value = direction.x * 0.6f + direction.y * direction.z * 0.3f;
            var angularGradient = new float3(0.6f, direction.z * 0.3f, direction.y * 0.3f);
            angularGradient -= direction * math.dot(angularGradient, direction);
            var fieldGradient = angularGradient / radius;
            var radialDisplacement = value * radius * 0.035f;
            var radialGradient = angularGradient * 0.035f;
            return new(radialDisplacement, radialGradient, value, fieldGradient, math.clamp(value, -1, 1));
        }
    }
}
}
